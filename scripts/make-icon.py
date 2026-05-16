"""Build a multi-resolution Windows .ico file from a single source PNG.

Auto-crops the dark "letterbox" margin around the pixel art before
resizing so the design fills the icon canvas — this keeps small details
(like the arrow badge in the bottom-right corner) from being the first
thing to disappear at 16x16 / 24x24.

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

# How much of the icon canvas the design should occupy after cropping.
# 0.96 = a thin 2% margin on each side so the artwork doesn't butt right
# against the corner pixels (which Windows masks during rounding).
CONTENT_FILL = 0.96


def detect_content_bbox(img: Image.Image) -> tuple[int, int, int, int]:
    """Return (left, top, right, bottom) of the pixel-art content.

    The source uses a dark navy background (~RGB 12-15-23) rather than
    pure black, so a simple `getbbox()` won't work. We classify each pixel
    as content if it's either bright OR clearly saturated.
    """
    w, h = img.size
    px = img.load()
    minx, miny, maxx, maxy = w, h, 0, 0
    found_any = False
    for y in range(h):
        for x in range(w):
            r, g, b, _ = px[x, y]
            brightness = (r + g + b) / 3
            saturation = max(r, g, b) - min(r, g, b)
            if brightness > 80 or saturation > 30:
                found_any = True
                if x < minx: minx = x
                if x > maxx: maxx = x
                if y < miny: miny = y
                if y > maxy: maxy = y
    if not found_any:
        return 0, 0, w - 1, h - 1
    return minx, miny, maxx + 1, maxy + 1


def crop_and_square(img: Image.Image) -> Image.Image:
    """Crop to content bbox, then pad to a square canvas centered on the
    original background color, leaving (1-CONTENT_FILL)/2 margin per side."""
    left, top, right, bottom = detect_content_bbox(img)
    cropped = img.crop((left, top, right, bottom))
    cw, ch = cropped.size
    side = max(cw, ch)
    canvas_size = round(side / CONTENT_FILL)
    bg = img.getpixel((0, 0))
    canvas = Image.new("RGBA", (canvas_size, canvas_size), bg)
    ox = (canvas_size - cw) // 2
    oy = (canvas_size - ch) // 2
    canvas.paste(cropped, (ox, oy))
    return canvas


def main() -> None:
    raw = Image.open(SRC).convert("RGBA")
    print(f"Source: {SRC} ({raw.size[0]}x{raw.size[1]})")

    src_img = crop_and_square(raw)
    print(f"After crop+square: {src_img.size[0]}x{src_img.size[1]} "
          f"(content fills ~{int(CONTENT_FILL*100)}% of canvas)")

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

