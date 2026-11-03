"""Generates Jer's icons (app icon + tray glyphs).

Usage: python tools/make_icons.py
Requires Pillow. Writes into src/Jer/Assets/.
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "Jer" / "Assets"
CANVAS = 1024
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
TRAY_SIZES = [16, 20, 24, 32, 40, 48, 64]


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def gradient(size, start, end):
    """Diagonal gradient from top-left (start) to bottom-right (end)."""
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = lerp(start, end, t) + (255,)
    return img


def rounded_mask(size, box, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)
    return mask


def draw_face(draw, cx, cy, scale, pill_fill, eye_fill):
    """Black pill (the notch) with two eyes (the mascot)."""
    pw, ph = 700 * scale, 300 * scale
    draw.rounded_rectangle(
        (cx - pw / 2, cy - ph / 2, cx + pw / 2, cy + ph / 2),
        radius=ph / 2,
        fill=pill_fill,
    )
    ew, eh = 84 * scale, 140 * scale
    gap = 110 * scale
    for ex in (cx - gap, cx + gap):
        draw.rounded_rectangle(
            (ex - ew / 2, cy - eh / 2, ex + ew / 2, cy + eh / 2),
            radius=ew / 2,
            fill=eye_fill,
        )


def app_icon():
    base = gradient(CANVAS, (99, 102, 241), (168, 85, 247))  # indigo -> violet
    mask = rounded_mask(CANVAS, (40, 40, CANVAS - 40, CANVAS - 40), 220)
    icon = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    icon.paste(base, (0, 0), mask)
    # soft highlight in the upper half
    glow = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((-200, -520, CANVAS + 200, 420), fill=(255, 255, 255, 34))
    icon = Image.alpha_composite(icon, Image.composite(glow, Image.new("RGBA", glow.size), mask))
    draw = ImageDraw.Draw(icon)
    draw_face(draw, CANVAS / 2, CANVAS / 2, 1.0, (12, 12, 16, 255), (255, 255, 255, 255))
    return icon


def tray_glyph(color):
    """Monochrome pill with eyes punched out, for the notification area."""
    glyph = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(glyph)
    draw_face(draw, CANVAS / 2, CANVAS / 2, 1.38, color + (255,), (0, 0, 0, 0))
    return glyph


def save_ico(img, path, sizes):
    frames = [img.resize((s, s), Image.LANCZOS) for s in sizes]
    frames[-1].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    icon = app_icon()
    save_ico(icon, ASSETS / "Jer.ico", ICO_SIZES)
    icon.resize((256, 256), Image.LANCZOS).save(ASSETS / "Jer.png")
    save_ico(tray_glyph((255, 255, 255)), ASSETS / "TrayWhite.ico", TRAY_SIZES)
    save_ico(tray_glyph((20, 20, 20)), ASSETS / "TrayBlack.ico", TRAY_SIZES)
    print("Icons written to", ASSETS)


if __name__ == "__main__":
    main()
