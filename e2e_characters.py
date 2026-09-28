#!/usr/bin/env python3
"""Device E2E: empty cache, GitHub fetch, character-story merge, unowned 400 substitute."""
import os
import re
import shlex
import shutil
import subprocess
import sys
import time
from pathlib import Path

ADB = os.environ.get("OTOGI_ADB") or shutil.which("adb")
if not ADB and sys.argv[1:] != ["--self-test"]:
    raise SystemExit("adb not found; set OTOGI_ADB or put adb on PATH")
SERIAL = os.environ.get("ANDROID_SERIAL", "127.0.0.1:5555")
PKG = "jp.co.dmm.dmmgames.kms.prototype"
FILES = f"/sdcard/Android/data/{PKG}/files"
LOG = f"{FILES}/melonloader/etc/Latest.log"
CACHE = f"{FILES}/UserData/OtogiCgUnlock"
EVIDENCE = Path(__file__).resolve().parent / "out" / "e2e"
EXPECTED_MONSTERS = 975
EXPECTED_SPIRITS = 149


def adb(*args, check=True):
    cmd = [ADB, "-s", SERIAL, *args]
    result = subprocess.run(cmd, capture_output=True)
    out = (result.stdout or b"") + (result.stderr or b"")
    text = out.decode("utf-8", "replace")
    if check and result.returncode != 0:
        raise RuntimeError(f"adb {args} failed ({result.returncode}): {text[-500:]}")
    return text


def shell(command, check=True):
    if os.environ.get("OTOGI_ADB_ROOT") == "1":
        command = "su -c " + shlex.quote(command)
    return adb("shell", command, check=check)


def self_test():
    from unittest.mock import patch

    commands = ["", "cat '/sdcard/app files/Latest.log'", "printf '%s' \"a'b;$HOME\"\ntrue"]
    for root in ("", "0", "1"):
        with patch.dict(os.environ, OTOGI_ADB_ROOT=root), patch(__name__ + ".adb") as run:
            for command in commands:
                shell(command, check=False)
                args, kwargs = run.call_args
                assert args[0] == "shell" and kwargs == {"check": False}
                if root == "1":
                    assert shlex.split(args[1]) == ["su", "-c", command]
                else:
                    assert args[1] == command
    print("PASS shell root opt-in and quoting (no device commands)")


def screenshot(name):
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    path = EVIDENCE / f"{name}.png"
    raw = subprocess.run(
        [ADB, "-s", SERIAL, "exec-out", "screencap", "-p"],
        capture_output=True, check=True,
    ).stdout
    path.write_bytes(raw)
    if len(raw) < 1000:
        raise RuntimeError(f"tiny screenshot {name}: {len(raw)}")
    return path


def loader_log():
    return shell(f"cat {LOG}", check=False)


def wait_log(needle, seconds=90):
    deadline = time.time() + seconds
    text = ""
    while time.time() < deadline:
        text = loader_log()
        if needle in text:
            return text
        time.sleep(2)
    screenshot("timeout")
    raise RuntimeError(f"timeout waiting for {needle!r}\n{text[-1500:]}")


def tap(x, y, wait=1.0):
    shell(f"input tap {x} {y}")
    time.sleep(wait)


def boot_cold_cache():
    subprocess.run([ADB, "connect", SERIAL], capture_output=True)
    shell(f"am force-stop {PKG}")
    shell(f"rm -rf {CACHE}")
    shell(f"rm -f {LOG}")
    shell("logcat -c")
    shell(f"monkey -p {PKG} -c android.intent.category.LAUNCHER 1")
    wait_log("[OtogiCgUnlock] installed", 60)
    screenshot("00-boot")
    wait_log("downloaded relative=characters.json", 90)
    screenshot("01-characters-downloaded")


def open_character_story():
    for _ in range(8):
        log = loader_log()
        if "characters-merged" in log:
            break
        tap(960, 540, 4)
        tap(1110, 750, 4)  # Accept the resource-update confirmation when present.
        screenshot("02-after-tap")
        tap(1690, 350, 6)
        screenshot("03-story-tap")
        log = loader_log()
        if "characters-merged" in log:
            break
    text = wait_log("characters-merged", 30)
    match = re.search(
        r"characters-merged added=(\d+) monsters=(\d+) spirits=(\d+)", text)
    if not match:
        raise RuntimeError("merge log missing counts:\n" + text[-2000:])
    added, monsters, spirits = map(int, match.groups())
    screenshot("04-character-story")
    if monsters < EXPECTED_MONSTERS:
        raise RuntimeError(
            f"monsters={monsters} added={added} expected>={EXPECTED_MONSTERS}")
    if spirits < EXPECTED_SPIRITS:
        raise RuntimeError(f"spirits={spirits} expected>={EXPECTED_SPIRITS}")
    return added, monsters, spirits


def open_unowned_row():
    # Slot 2 (Vermelho) episode list is a live 200; locked adult rows 400.
    tap(430, 310, 6)
    screenshot("05-episode-list")
    wait_log("downloaded relative=scenes/", 60)
    tap(1700, 220, 8)
    screenshot("05-unowned")
    text = loader_log()
    pid = shell(f"pidof {PKG}").strip()
    errors = shell(f"logcat -d --pid={pid} -v brief Unity:E '*:S'")
    (EVIDENCE / "scene-errors.txt").write_text(errors, encoding="utf-8")
    if re.search(r"JsonSerializationException|ArgumentNullException|NullReferenceException", errors):
        raise RuntimeError("game scene deserialization/runtime exception; see scene-errors.txt")
    if "hook-failed" in text or "response-error" in text:
        raise RuntimeError("plugin error in log")
    if re.search(
            r"\[OtogiCgUnlock\] sub url=/api/(MAdults/MonsterMAdults|MScenes)/\d+ originalStatus=40[04]",
            text):
        return text
    raise RuntimeError(
        "locked-row substitute originalStatus=400/404 missing:\n" + text[-2000:])


def main():
    boot_cold_cache()
    added, monsters, spirits = open_character_story()
    open_unowned_row()
    print(
        f"PASS characters-merged added={added} monsters={monsters} spirits={spirits}")
    print("PASS locked-row substitute originalStatus=400/404")
    print(f"PASS screenshots {EVIDENCE}")


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["--self-test"]:
            self_test()
        else:
            main()
    except Exception as exc:
        print("FAIL", exc)
        sys.exit(1)
