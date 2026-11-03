#!/usr/bin/env python3
"""Rebuild committed native icon resources from the original SVG on macOS.

Requires Google Chrome, sips and iconutil. No renderer is required at app runtime
or during normal builds; the generated ICNS/ICO are committed resource assets.
"""
from pathlib import Path
import os
import shutil
import struct
import subprocess
import tempfile


def run(*args):
    subprocess.run([str(arg) for arg in args], check=True, stdout=subprocess.DEVNULL)


def main():
    repo = Path(__file__).resolve().parent.parent
    chrome = Path("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome")
    if not chrome.is_file() or not shutil.which("iconutil") or not shutil.which("sips"):
        raise SystemExit("Requires macOS with Google Chrome, sips and iconutil installed.")
    with tempfile.TemporaryDirectory(prefix="motion-clicker-icon-") as scratch:
        temp = Path(scratch)
        master = temp / "master.png"
        run(chrome, "--headless=new", "--disable-gpu", "--no-first-run",
            "--no-default-browser-check", f"--user-data-dir={temp / 'profile'}",
            "--default-background-color=00000000", "--hide-scrollbars",
            "--window-size=1024,1024", f"--screenshot={master}",
            (repo / "assets/app-icon.svg").as_uri())
        iconset = temp / "MotionClicker.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                suffix = "@2x" if scale == 2 else ""
                run("sips", "-z", size * scale, size * scale, master,
                    "--out", iconset / f"icon_{size}x{size}{suffix}.png")
        icns = temp / "MotionClicker.icns"
        run("iconutil", "-c", "icns", iconset, "-o", icns)
        sizes = (16, 24, 32, 48, 64, 128, 256)
        payloads = []
        for size in sizes:
            png = temp / f"windows-{size}.png"
            run("sips", "-z", size, size, master, "--out", png)
            payloads.append(png.read_bytes())
        directory = bytearray(struct.pack("<HHH", 0, 1, len(sizes)))
        offset = 6 + 16 * len(sizes)
        for size, payload in zip(sizes, payloads):
            directory.extend(struct.pack("<BBBBHHII", size % 256, size % 256,
                                         0, 0, 1, 32, len(payload), offset))
            offset += len(payload)
        ico = temp / "MotionClicker.ico"
        ico.write_bytes(directory + b"".join(payloads))
        for src, dest in (
            (icns, repo / "macos/Resources/MotionClicker.icns"),
            (ico, repo / "windows/MotionClicker.Windows/Assets/MotionClicker.ico"),
        ):
            dest.parent.mkdir(parents=True, exist_ok=True)
            staging = dest.with_suffix(dest.suffix + ".tmp")
            shutil.copyfile(src, staging)
            os.replace(staging, dest)
        preview = repo / "dist/app-icon.png"
        preview.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(master, preview)
    print("Updated ICNS, ICO and dist/app-icon.png from assets/app-icon.svg")


if __name__ == "__main__":
    main()
