"""Regenerates Sling's icon: the .ico, the three SVGs and the two PNGs.

    python assets/build-icon.py

Needs Pillow and nothing else. That is deliberate and it is the one real divergence from
Etch, whose generator needs cairosvg and therefore a native libcairo-2.dll that is not on a
stock Windows machine and not on Hendrik's - so Etch's icon can only be regenerated somewhere
other than where Etch is built, which is how an icon quietly stops being regenerable at all.
Pillow installs from a wheel anywhere.

THE GEOMETRY BELOW IS THE SOURCE. The SVGs are emitted from the same numbers the raster is
drawn from, so there is no second artefact to keep in step and the drift cannot happen by
construction rather than by discipline.

Every frame is drawn at 8x on its own and reduced with LANCZOS. Nothing is ever downsampled
from one big raster, which is the thing that makes 16 px muddy.
"""

from __future__ import annotations

import os
import struct

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))

S = 256          # design canvas, in which every number below is expressed
OVER = 8         # supersample factor

# Etch's graphite plate, so two of Hendrik's tools in one taskbar read as one family.
PLATE_TOP = (0x2B, 0x30, 0x39)
PLATE_BOT = (0x15, 0x18, 0x1D)
PLATE_EDGE = (0x3A, 0x40, 0x4A)
PLATE_FLAT = (0x22, 0x26, 0x2D)

# Teal to azure. Etch took amber away from the cyan this category defaults to; Sling takes
# the other side of that same decision, and the point is the pair.
TEAL = (0x7F, 0xE9, 0xD4)
AZURE = (0x1B, 0x93, 0xD8)


class Drawing:
    """One set of numbers, for one band of sizes.

    Three of them, because the content has to change with the size and the mark must not
    change weight as a window is dragged between monitors. 32 - 48 is one drawing because
    that is the taskbar across 100 - 150% DPI.
    """

    def __init__(self, name, scale, gradient, hairline, flat_plate, out_y, back_y,
                 shaft, back_shaft, head, back_head, inset, radius):
        self.name = name
        self.scale = scale
        self.gradient = gradient
        self.hairline = hairline
        self.flat_plate = flat_plate
        self.out_y = out_y            # centre line of the request arrow
        self.back_y = back_y          # centre line of the response arrow
        self.shaft = shaft            # half-height of the request shaft
        self.back_shaft = back_shaft  # half-height of the response shaft
        self.head = head              # half-height of the request head
        self.back_head = back_head    # half-height of the response head
        self.inset = inset
        self.radius = radius


# The mark: request out on top, response back underneath.
#
# The two arrows carry DIFFERENT weights on purpose. Two equal ones are the operating
# system's sync glyph, which says "these two things match"; this has to say "I asked, and it
# answered", and the request is the thing the user pressed.
#
# What the small drawings buy is the GAP between the two heads. At 16 px the shafts are what
# survives and the heads are what merges, so tiny widens the separation and thickens the
# shafts rather than simply scaling the master down.
MASTER = Drawing(
    "master", scale=1.00, gradient=True, hairline=True, flat_plate=False,
    out_y=97, back_y=163, shaft=16, back_shaft=13, head=30, back_head=26,
    inset=14, radius=54,
)

SMALL = Drawing(
    "small", scale=1.10, gradient=True, hairline=False, flat_plate=False,
    out_y=95, back_y=165, shaft=17, back_shaft=14, head=30, back_head=26,
    inset=13, radius=50,
)

TINY = Drawing(
    "tiny", scale=1.18, gradient=False, hairline=False, flat_plate=True,
    out_y=92, back_y=168, shaft=19, back_shaft=17, head=30, back_head=28,
    inset=10, radius=40,
)

# Ten frames. The bands are the drawing each size is rendered from.
FRAMES = [
    (16, TINY), (20, TINY), (24, TINY),
    (32, SMALL), (40, SMALL), (48, SMALL),
    (64, MASTER), (96, MASTER), (128, MASTER), (256, MASTER),
]


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def arrows(dr: Drawing):
    """The mark as polygons, in canvas units. The single source both renderers read."""
    cx = S / 2
    cy = S / 2
    k = dr.scale

    def sx(x):
        return cx + (x - cx) * k

    def sy(y):
        return cy + (y - cy) * k

    # Request: left to right, the heavier of the two.
    out_tip, out_base, out_tail = sx(214), sx(148), sx(44)
    top = [
        (out_tail, sy(dr.out_y - dr.shaft)),
        (out_base, sy(dr.out_y - dr.shaft)),
        (out_base, sy(dr.out_y - dr.head)),
        (out_tip, sy(dr.out_y)),
        (out_base, sy(dr.out_y + dr.head)),
        (out_base, sy(dr.out_y + dr.shaft)),
        (out_tail, sy(dr.out_y + dr.shaft)),
    ]

    # Response: right to left, lighter, and it starts short of the request's tail so the
    # two are read as a circuit rather than as a bracket.
    back_tip, back_base, back_tail = sx(42), sx(108), sx(212)
    bottom = [
        (back_tail, sy(dr.back_y - dr.back_shaft)),
        (back_base, sy(dr.back_y - dr.back_shaft)),
        (back_base, sy(dr.back_y - dr.back_head)),
        (back_tip, sy(dr.back_y)),
        (back_base, sy(dr.back_y + dr.back_head)),
        (back_base, sy(dr.back_y + dr.back_shaft)),
        (back_tail, sy(dr.back_y + dr.back_shaft)),
    ]

    return top, bottom


def render(dr: Drawing, size: int) -> Image.Image:
    n = OVER
    big = S * n
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))

    box = (dr.inset * n, dr.inset * n, big - dr.inset * n, big - dr.inset * n)
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=dr.radius * n, fill=255)

    if dr.flat_plate:
        plate = Image.new("RGB", (big, big), PLATE_FLAT)
    else:
        column = Image.new("RGB", (1, big))
        cd = ImageDraw.Draw(column)
        for y in range(big):
            cd.point((0, y), fill=lerp(PLATE_TOP, PLATE_BOT, y / (big - 1)))
        plate = column.resize((big, big))

    img.paste(plate, (0, 0), mask)

    if dr.hairline:
        ImageDraw.Draw(img).rounded_rectangle(
            box, radius=dr.radius * n, outline=PLATE_EDGE + (255,), width=n
        )

    ink = Image.new("RGBA", (big, big))
    if dr.gradient:
        gd = ImageDraw.Draw(ink)
        for y in range(big):
            gd.line([(0, y), (big, y)], fill=lerp(TEAL, AZURE, y / (big - 1)) + (255,))
    else:
        # Flat at 16 - 24: a gradient across twenty pixels is a gradient nobody sees, and
        # it costs contrast at the ends where the mark is thinnest.
        ink.paste(lerp(TEAL, AZURE, 0.45) + (255,), (0, 0, big, big))

    shape = Image.new("L", (big, big), 0)
    sd = ImageDraw.Draw(shape)
    for polygon in arrows(dr):
        sd.polygon([(x * n, y * n) for x, y in polygon], fill=255)
    ink.putalpha(shape)

    img.alpha_composite(ink)
    return img.resize((size, size), Image.LANCZOS)


# ------------------------------------------------------------------------------ svg


def svg(dr: Drawing) -> str:
    top, bottom = arrows(dr)
    plate_fill = (
        f'rgb{PLATE_FLAT}' if dr.flat_plate else "url(#plate)"
    )
    ink_fill = (
        f'rgb{lerp(TEAL, AZURE, 0.45)}' if not dr.gradient else "url(#ink)"
    )

    def path(points):
        head = f"M {points[0][0]:.2f} {points[0][1]:.2f}"
        rest = " ".join(f"L {x:.2f} {y:.2f}" for x, y in points[1:])
        return f"{head} {rest} Z"

    hairline = (
        f'  <rect x="{dr.inset + 0.5}" y="{dr.inset + 0.5}" '
        f'width="{S - dr.inset * 2 - 1}" height="{S - dr.inset * 2 - 1}" '
        f'rx="{dr.radius}" fill="none" stroke="rgb{PLATE_EDGE}" stroke-width="1"/>\n'
        if dr.hairline else ""
    )

    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {S} {S}">\n'
        f"  <defs>\n"
        f'    <linearGradient id="plate" x1="0" y1="0" x2="0" y2="1">\n'
        f'      <stop offset="0" stop-color="rgb{PLATE_TOP}"/>\n'
        f'      <stop offset="1" stop-color="rgb{PLATE_BOT}"/>\n'
        f"    </linearGradient>\n"
        f'    <linearGradient id="ink" x1="0" y1="0" x2="0" y2="1">\n'
        f'      <stop offset="0" stop-color="rgb{TEAL}"/>\n'
        f'      <stop offset="1" stop-color="rgb{AZURE}"/>\n'
        f"    </linearGradient>\n"
        f"  </defs>\n"
        f'  <rect x="{dr.inset}" y="{dr.inset}" width="{S - dr.inset * 2}" '
        f'height="{S - dr.inset * 2}" rx="{dr.radius}" fill="{plate_fill}"/>\n'
        f"{hairline}"
        f'  <path d="{path(top)}" fill="{ink_fill}"/>\n'
        f'  <path d="{path(bottom)}" fill="{ink_fill}"/>\n'
        f"</svg>\n"
    )


# ------------------------------------------------------------------------------ ico


def bmp_frame(img: Image.Image) -> bytes:
    """A BITMAPINFOHEADER frame, with the three traps this format sets.

    biHeight is DOUBLE the real height (colour rows plus AND-mask rows). Rows are
    bottom-up and BGRA. The 1 bpp AND mask must be present and 4-byte padded even when
    every bit of it is zero.
    """
    w, h = img.size
    px = img.load()

    header = struct.pack(
        "<IiiHHIIiiII",
        40, w, h * 2, 1, 32, 0, w * h * 4, 0, 0, 0, 0,
    )

    colour = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            colour += bytes((b, g, r, a))

    stride = ((w + 31) // 32) * 4
    mask = bytearray(stride * h)

    return header + bytes(colour) + bytes(mask)


def png_frame(img: Image.Image) -> bytes:
    from io import BytesIO

    buf = BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def pack_ico(frames: list[tuple[int, Image.Image]]) -> bytes:
    """Written by hand because Pillow's ICO writer cannot take a different image per frame,
    and the whole point here is that 16 px is a different drawing from 128 px."""
    blobs = [
        # BMP below 64, PNG at and above, which is what every consumer expects.
        (size, bmp_frame(img) if size < 64 else png_frame(img))
        for size, img in frames
    ]

    offset = 6 + 16 * len(blobs)
    out = struct.pack("<HHH", 0, 1, len(blobs))

    for size, blob in blobs:
        byte = 0 if size == 256 else size
        out += struct.pack("<BBBBHHII", byte, byte, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)

    return out + b"".join(blob for _, blob in blobs)


# ------------------------------------------------------------------------------ verify


def verify(path: str, frames: list[tuple[int, Image.Image]]):
    """Reads the packed file back. The write path proving itself is not evidence."""
    data = open(path, "rb").read()
    reserved, kind, count = struct.unpack_from("<HHH", data, 0)
    assert (reserved, kind) == (0, 1), "not an icon"
    assert count == len(frames), f"{count} entries for {len(frames)} frames"

    for i, (size, _) in enumerate(frames):
        w, h, colours, pad, planes, bpp, length, offset = struct.unpack_from(
            "<BBBBHHII", data, 6 + 16 * i
        )
        expected = 0 if size == 256 else size
        assert (w, h) == (expected, expected), f"{size}: entry says {w}x{h}"
        assert planes == 1 and bpp == 32, f"{size}: {planes} planes, {bpp} bpp"
        assert offset + length <= len(data), f"{size}: runs past the end"

        if size < 64:
            _, bw, bh = struct.unpack_from("<Iii", data, offset)
            assert bh == bw * 2, f"{size}: biHeight {bh} is not double {bw}"

    for size, img in frames:
        # A plate is a rounded square, so its corner must be transparent. A THRESHOLD, not
        # an equality: a LANCZOS reduction rings, and a correct rounded corner lands at 1.
        assert img.getpixel((0, 0))[3] <= 8, f"{size}: corner is not transparent"
        assert img.getpixel((size - 1, size - 1))[3] <= 8, f"{size}: corner is not transparent"

    print(f"verified {path}: {count} frames, all sizes, planes, bpp and corners")


# ------------------------------------------------------------------------------ main


def main():
    frames = [(size, render(dr, size)) for size, dr in FRAMES]

    ico = os.path.join(HERE, "sling.ico")
    with open(ico, "wb") as f:
        f.write(pack_ico(frames))
    verify(ico, frames)

    for name, dr in (("sling.svg", MASTER), ("sling-small.svg", SMALL), ("sling-tiny.svg", TINY)):
        with open(os.path.join(HERE, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(svg(dr))
        print("wrote", name)

    for size, name in ((256, "sling-256.png"), (512, "sling-512.png")):
        render(MASTER, size).save(os.path.join(HERE, name))
        print("wrote", name)


if __name__ == "__main__":
    main()
