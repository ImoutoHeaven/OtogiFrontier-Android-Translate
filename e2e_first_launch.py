#!/usr/bin/env python3
"""Fresh APK install and first launch: no su, grants, or device-file preparation."""
import argparse
import re
import time
from pathlib import Path

import e2e_characters as device


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("apk", type=Path)
    parser.add_argument("package")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+", args.package):
        parser.error("invalid package name")
    if not args.apk.is_file():
        parser.error("APK not found")
    device.adb("connect", device.SERIAL)
    if "package:" in device.adb("shell", "pm", "path", args.package, check=False):
        raise RuntimeError("first-launch test requires a package not already installed")
    device.adb("install", str(args.apk.resolve()))
    device.adb("logcat", "-c")
    device.adb("shell", "am", "start", "-W", "-n",
               args.package + "/com.unity3d.player.UnityPlayerActivity")
    evidence = Path(__file__).resolve().parent / "out" / "e2e-first-launch"
    evidence.mkdir(parents=True, exist_ok=True)
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        pid = device.adb("shell", "pidof", args.package, check=False).strip()
        if pid.isdigit():
            text = device.adb("logcat", "-d", "--pid=" + pid, "-v", "brief")
            safe = "\n".join(line for line in text.splitlines()
                             if "OtogiBootstrap" in line or "MelonLoader" in line)
            (evidence / "bootstrap.log").write_text(safe, encoding="utf-8")
            if any(marker in safe for marker in ("hook-failed", "runtime-init-failed", "font-redirect-failed")):
                raise RuntimeError(f"plugin initialization failed; evidence={evidence}")
            scan_ready = "[OtogiTranslate] tmp-scan-disabled" in safe or "[OtogiTranslate] ui-scan-installed" in safe
            if scan_ready and all(marker in safe for marker in (
                "IL2CPP directory ready", "[OtogiTranslate] hook-installed",
                "[OtogiCgUnlock] installed", "[OtogiTranslate] runtime-driver-installed",
                "[OtogiTranslate] font-redirect-installed",
                "[OtogiTranslate] dictionary-transport-installed",
            )):
                device.EVIDENCE = evidence
                device.screenshot("first-launch")
                print("PASS fresh install and first launch without su, grants, or device-file preparation")
                return
        time.sleep(2)
    raise RuntimeError(f"first-launch initialization failed; evidence={evidence}")


if __name__ == "__main__":
    main()
