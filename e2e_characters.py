#!/usr/bin/env python3
"""Device E2E: empty cache, GitHub fetch, character-story merge, unowned 400 substitute."""
import re
import subprocess
import sys
import time
from pathlib import Path

ADB = r"C:\Users\Eden\otogifrontier\platform_tools\adb.exe"
SERIAL = "127.0.0.1:5555"
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
    return adb("shell", command, check=check)


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
    for y in (220, 380, 540, 700):
        tap(1700, y, 6)
        text = loader_log()
        screenshot("05-unowned")
        if "hook-failed" in text or "response-error" in text:
            raise RuntimeError("plugin error in log")
        if "[OtogiCgUnlock] sub " in text and (
                "originalStatus=400" in text or "originalStatus=404" in text):
            return text
        tap(150, 1020, 2)
    raise RuntimeError(
        "locked-row substitute originalStatus=400/404 missing:\n" + loader_log()[-2000:])


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
        main()
    except Exception as exc:
        print("FAIL", exc)
        sys.exit(1)
