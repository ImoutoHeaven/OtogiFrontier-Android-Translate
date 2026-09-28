#!/usr/bin/env python3
"""Device E2E: scene-level LLM translation against a local mock endpoint.

Plays one ordinary scene four times: first play sends one scene request, replay rewrites the
response from the scene cache with no request, and an invalid response or an HTTP error hands
the lines to the per-line path. Uses su (OTOGI_ADB_ROOT=1) to stage and restore device files.
"""
import json
import os
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

os.environ["OTOGI_ADB_ROOT"] = "1"
import e2e_characters as device  # noqa: E402

PORT = 18765
SCENE = os.environ.get("OTOGI_SCENE_ID", "10001")
SCENE_KEY = "MScenes/" + SCENE
TRANSLATE = f"{device.FILES}/UserData/OtogiTranslate"
STAGED = [
    f"{device.FILES}/OtogiTranslate.cfg",
    f"{device.FILES}/OtogiTranslate.cache.jsonl",
    f"{TRANSLATE}/llm-scenes",
    f"{TRANSLATE}/MScenes/{SCENE}_gb.json",
]
BACKUP = f"{device.FILES}/.otogi-scene-e2e-backup"
CONFIG = (
    "[LLM]\nEnable = true\n"
    f"Endpoint = http://127.0.0.1:{PORT}/v1/chat/completions\n"
    "Model = mock\nTimeoutSeconds = 10\nRetryCount = 1\nRequestsPerSecond = 10\n"
    "MaxQueue = 256\nSceneTranslation = true\n\n[UI]\nScanIntervalSeconds = 0.5\n"
)


class Mock:
    mode = "ok"
    requests = []
    lock = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        system, user = (m["content"] for m in body["messages"])
        scene = "story dialogue" in system
        record = {"scene": scene, "mode": Mock.mode}
        if scene:
            payload = json.loads(user)
            record["payload"] = payload
            if Mock.mode == "http500":
                with Mock.lock:
                    Mock.requests.append(record)
                self.send_response(500)
                self.end_headers()
                return
            targets = [line for line in payload["lines"] if "id" in line]
            if Mock.mode == "bad-structure":
                targets = targets[:-1]
            content = json.dumps({
                "version": payload["version"], "scene": payload["scene"],
                "translations": [{"id": line["id"], "text": "【场景译】" + line["text"]}
                                 for line in targets]}, ensure_ascii=False)
        else:
            record["text"] = user
            content = "【逐句译】" + user
        with Mock.lock:
            Mock.requests.append(record)
        data = json.dumps({"choices": [{"message": {"content": content}}]},
                          ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def scene_requests():
    with Mock.lock:
        return [r for r in Mock.requests if r["scene"] and r["payload"]["scene"] == SCENE_KEY]


def line_requests():
    with Mock.lock:
        return [r["text"] for r in Mock.requests if not r["scene"]]


def wait_for(predicate, seconds, label):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(1)
    device.screenshot(label + "-timeout")
    raise RuntimeError(f"timeout: {label}\n{device.loader_log()[-2500:]}")


def play_scene(label):
    """Cold start, open the first character's first normal episode, return the loader log."""
    device.shell(f"am force-stop {device.PKG}")
    device.shell(f"rm -f {device.LOG}")
    device.shell(f"monkey -p {device.PKG} -c android.intent.category.LAUNCHER 1")
    device.wait_log("[OtogiTranslate] runtime-driver-installed", 90)
    marker = f"scene-llm type=MScenes id={SCENE} "
    device.open_character_story()
    # First character (Cinderella) -> prologue -> play from the start, 1920x1080 layout.
    for x, y, wait in ((175, 310, 8), (1700, 245, 4), (675, 640, 8)):
        device.tap(x, y, wait)
    return wait_for(lambda: device.loader_log() if marker in device.loader_log() else None,
                    30, label + "-scene")


def scene_line(log):
    match = re.findall(rf"scene-llm type=MScenes id={SCENE} applied=(\d+) requested=(\d+)", log)
    return tuple(map(int, match[-1]))


def check_first_play():
    Mock.mode = "ok"
    log = play_scene("01-first")
    applied, requested = scene_line(log)
    assert applied == 0 and requested > 0, (applied, requested)
    wait_for(lambda: f"scene-llm-translated scene={SCENE_KEY} accepted={requested} rejected=0"
             in device.loader_log(), 30, "01-translated")
    time.sleep(4)
    device.screenshot("01-first-play")
    sent = scene_requests()
    assert len(sent) == 1, len(sent)
    lines = sent[0]["payload"]["lines"]
    targets = [line["text"] for line in lines if "id" in line]
    assert len(targets) == requested
    assert any(line["speaker"] for line in lines), "speaker context missing"
    assert not set(line_requests()) & set(targets), "held scene lines were requested per-line"
    return requested, targets


def check_replay(requested):
    before = len(scene_requests())
    lines_before = len(line_requests())
    log = play_scene("02-replay")
    applied, again = scene_line(log)
    assert applied == requested and again == 0, (applied, again)
    time.sleep(4)
    device.screenshot("02-replay")
    assert len(scene_requests()) == before, "replay sent a scene request"
    # Mock translations keep kana, so a resend would show up here.
    resent = [t for t in line_requests()[lines_before:] if "【场景译】" in t]
    assert not resent, f"applied scene translations were resent per-line: {resent[:3]}"


def check_fallback(mode, label, reason, targets):
    Mock.mode = mode
    device.shell(f"rm -rf {TRANSLATE}/llm-scenes {device.FILES}/OtogiTranslate.cache.jsonl")
    before = len(line_requests())
    play_scene(label)
    wait_for(lambda: f"scene-llm-fallback scene={SCENE_KEY}" in device.loader_log(),
             60, label + "-fallback")
    assert f"reason={reason}" in device.loader_log(), reason
    fallback = wait_for(
        lambda: [t for t in line_requests()[before:] if t in targets or
                 t.replace("\n", "\\n") in targets], 60, label + "-per-line")
    time.sleep(4)
    device.screenshot(label)
    return len(fallback)


def main():
    device.EVIDENCE = Path(__file__).resolve().parent / "out" / "e2e-scene"
    device.EVIDENCE.mkdir(parents=True, exist_ok=True)
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    device.adb("connect", device.SERIAL)
    device.adb("reverse", f"tcp:{PORT}", f"tcp:{PORT}")
    if "yes" in device.shell(f"[ -e {BACKUP} ] && echo yes || true"):
        raise RuntimeError(f"stale backup on device: {BACKUP}")
    device.shell(f"am force-stop {device.PKG}")
    device.shell(f"mkdir -p {BACKUP} {TRANSLATE}/MScenes")
    staged = []
    try:
        for index, path in enumerate(STAGED):
            device.shell(f"[ ! -e {path} ] || mv {path} {BACKUP}/{index}")
            staged.append(index)
        device.shell(f"printf '%s' '{CONFIG}' > {device.FILES}/OtogiTranslate.cfg")
        # An empty dictionary leaves every Japanese line to the scene path.
        device.shell(f"printf '{{}}' > {TRANSLATE}/MScenes/{SCENE}_gb.json")
        requested, targets = check_first_play()
        check_replay(requested)
        invalid = check_fallback("bad-structure", "03-invalid", "invalid-structure", targets)
        failed = check_fallback("http500", "04-http500", "http-500", targets)
    finally:
        device.shell(f"am force-stop {device.PKG}")
        # Only paths whose original was moved aside are reset, so an early failure keeps the rest.
        for index in staged:
            path = STAGED[index]
            device.shell(f"rm -rf {path}; [ ! -e {BACKUP}/{index} ] || mv {BACKUP}/{index} {path}")
        device.shell(f"rmdir {BACKUP}")
        device.adb("reverse", "--remove", f"tcp:{PORT}", check=False)
        server.shutdown()
        (device.EVIDENCE / "mock-requests.json").write_text(
            json.dumps(Mock.requests, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"PASS first play: one scene request, {requested} lines with speaker context, no per-line duplicates")
    print("PASS replay: response rewritten from the scene cache with no request")
    print(f"PASS invalid scene response falls back per-line ({invalid} lines)")
    print(f"PASS scene HTTP 500 falls back per-line ({failed} lines)")
    print(f"Evidence: {device.EVIDENCE}")


if __name__ == "__main__":
    main()
