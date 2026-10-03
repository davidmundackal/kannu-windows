#!/usr/bin/env python3
"""Builds assets/Kannu.ico from Kannu for macOS's app icon.

The macOS AppIcon files are JPEG data despite their .png names, so each size is decoded and
re-encoded as PNG inside the .ico. Usage (Pillow required):

    python3 scripts/make-icons.py <path to kannu repo>/Kannu/Assets.xcassets/AppIcon.appiconset
"""
import sys
from pathlib import Path

from PIL import Image

SIZES = [16, 24, 32, 48, 64, 128, 256]


def main() -> None:
    source = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("../kannu/Kannu/Assets.xcassets/AppIcon.appiconset")
    master = Image.open(source / "KannuIcon-1024.png").convert("RGBA")
    out = Path(__file__).resolve().parent.parent / "assets" / "Kannu.ico"
    master.save(out, format="ICO", sizes=[(s, s) for s in SIZES])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
