"""Builds Ui/Assets/compressor.ico from icon.svg and icon-small.svg, and the 128 px PNG the About window shows.

Run after changing either SVG:  python -I tools/icon/make-icon.py
Needs Inkscape 1.x (on the PATH, or in its default install folder). Every size is stored as PNG inside the .ico
(Windows Vista and later read that).
"""
import os
import shutil
import struct
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "..", "..", "Ui", "Assets")
OUT = os.path.join(ASSETS, "compressor.ico")
PNG = os.path.join(ASSETS, "compressor-128.png")

# The sizes Windows asks for (title bar, taskbar, Explorer views, Start menu, high DPI). Up to 32 px the simplified
# drawing, which keeps its pieces large enough to read.
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SMALL_UP_TO = 32


def inkscape() -> str:
    found = shutil.which("inkscape")
    if found:
        return found
    default = os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "Inkscape", "bin", "inkscape.exe")
    if os.path.exists(default):
        return default
    sys.exit("Inkscape is not installed (not on the PATH, nor in Program Files).")


def render(exe: str, svg: str, size: int, folder: str) -> bytes:
    png = os.path.join(folder, f"{size}.png")
    subprocess.run([exe, svg, "--export-type=png", f"--export-filename={png}", "-w", str(size), "-h", str(size)],
                   check=True, capture_output=True)
    with open(png, "rb") as f:
        return f.read()


def main() -> None:
    exe = inkscape()
    folder = tempfile.mkdtemp()
    try:
        images = [(size, render(exe, os.path.join(HERE, "icon-small.svg" if size <= SMALL_UP_TO else "icon.svg"), size, folder))
                  for size in SIZES]
        about = render(exe, os.path.join(HERE, "icon.svg"), 128, folder)
    finally:
        shutil.rmtree(folder, ignore_errors=True)

    # ICONDIR, one ICONDIRENTRY per image (a width or height of 0 means 256), then the PNGs.
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries, data = b"", b""
    for size, png in images:
        side = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
        data += png

    os.makedirs(ASSETS, exist_ok=True)
    with open(OUT, "wb") as f:
        f.write(header + entries + data)
    with open(PNG, "wb") as f:
        f.write(about)
    print(f"{os.path.normpath(OUT)}: {', '.join(str(s) for s in SIZES)} px")
    print(f"{os.path.normpath(PNG)}")


if __name__ == "__main__":
    main()
