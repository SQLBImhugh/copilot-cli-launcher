"""Build a multi-resolution Windows .ico file from a single source PNG.

Embeds each entry as PNG (preserves alpha; supported by Vista+). Output sizes
match what Windows uses for app icons (taskbar, alt-tab, file explorer, etc.).
"""
from __future__ import annotations
import struct
from io import BytesIO
from pathlib import Path
from PIL import Image

SRC = Path(r"src\CopilotLauncher\Assets\AppIcon.png")
DST = Path(r"src\CopilotLauncher\Assets\AppIcon.ico")
SIZES = [16, 24, 32, 48, 64, 128, 256]


def main() -> None:
    src_img = Image.open(SRC).convert("RGBA")
    print(f"Source: {SRC} ({src_img.size[0]}x{src_img.size[1]})")

    pngs: list[tuple[int, bytes]] = []
    for size in SIZES:
        # NEAREST resampling preserves the pixel-art aesthetic; LANCZOS would
        # blur the chunky 16x16-style edges.
        resized = src_img.resize((size, size), Image.Resampling.NEAREST)
        buf = BytesIO()
        resized.save(buf, format="PNG", optimize=True)
        pngs.append((size, buf.getvalue()))
        print(f"  {size:3d}x{size:<3d}  {len(buf.getvalue()):>6} bytes")

    # ICO file structure:
    #   ICONDIR (6 bytes): reserved=0, type=1 (icon), count=N
    #   ICONDIRENTRY (16 bytes) * N: width, height, colors, reserved,
    #       planes, bitcount, bytes_in_res, image_offset
    #   image data * N (PNG bytes for each entry)
    out = BytesIO()
    n = len(pngs)
    out.write(struct.pack("<HHH", 0, 1, n))

    header_size = 6 + 16 * n
    offset = header_size
    for size, data in pngs:
        # In ICONDIRENTRY, 256 is encoded as 0.
        w = 0 if size == 256 else size
        h = 0 if size == 256 else size
        out.write(struct.pack(
            "<BBBBHHII",
            w, h,
            0,         # color count (0 = >256 colors)
            0,         # reserved
            1,         # color planes
            32,        # bits per pixel (RGBA)
            len(data), # bytes in resource
            offset,    # image offset
        ))
        offset += len(data)

    for _, data in pngs:
        out.write(data)

    DST.write_bytes(out.getvalue())
    print(f"Wrote {DST} ({len(out.getvalue())} bytes, {n} entries)")


if __name__ == "__main__":
    main()
