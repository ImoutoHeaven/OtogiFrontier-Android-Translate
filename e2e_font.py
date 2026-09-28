#!/usr/bin/env python3
"""Device regression: the packaged font survives game-cache invalidation."""
import os
import time
from pathlib import Path

import e2e_characters as device


def main():
    os.environ["OTOGI_ADB_ROOT"] = "1"
    device.EVIDENCE = Path(__file__).resolve().parent / "out" / "e2e-font"
    device.adb("connect", device.SERIAL)
    device.shell(f"am force-stop {device.PKG}")
    device.shell(f"rm -f {device.LOG}")
    device.shell(f"monkey -p {device.PKG} -c android.intent.category.LAUNCHER 1")
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        log = device.shell(f"cat {device.LOG}", check=False)
        if "[OtogiTranslate] getter-hit" in log:
            break
        time.sleep(2)
    else:
        raise RuntimeError("Loader did not reach the game's HTTP response hook")
    # Match the updater's deletion of its stale font cache, without clearing login.
    device.shell(f"rm -f {device.FILES}/Assets/font {device.FILES}/Assets/font.md5")
    served_before = device.shell(f"cat {device.LOG}").count("[OtogiTranslate] font-local bytes=")
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        log = device.shell(f"cat {device.LOG}", check=False)
        if "[OtogiTranslate] font-local-error" in log:
            device.screenshot("failure")
            (device.EVIDENCE / "Latest.log").write_text(log, encoding="utf-8")
            raise RuntimeError("packaged font unavailable after game-cache invalidation")
        if log.count("[OtogiTranslate] font-local bytes=") > served_before:
            break
        device.tap(960, 980, 2)
        device.tap(1110, 750, 2)  # Confirm the update download on the 1920x1080 layout.
    else:
        device.screenshot("timeout")
        raise RuntimeError("game never requested the replacement font")
    expected = "929faeecb6a0bd636b92a921d2d590350b72e301832272302a064f3d5a0ab893"
    source = f"{device.FILES}/UserData/OtogiTranslate/font"
    assert device.shell(f"sha256sum {source}").split()[0] == expected
    device.screenshot("font-served")
    (device.EVIDENCE / "Latest.log").write_text(log, encoding="utf-8")
    print("PASS real game font request after Assets/font invalidation; source hash verified")
    print(f"Evidence: {device.EVIDENCE}")


if __name__ == "__main__":
    main()
