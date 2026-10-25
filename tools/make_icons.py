"""draws the clipnest icons from scratch. run: python tools/make_icons.py"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUT = Path(__file__).resolve().parent.parent / "src" / "ClipNest.App" / "Assets"


def gradient(size, top, bottom):
    # a simple diagonal blend between two colors
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    return img


def draw_icon(paused):
    big = 1024
    top, bottom = ((107, 114, 128), (75, 85, 99)) if paused else ((92, 124, 250), (139, 92, 246))
    base = gradient(big, top, bottom)
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=int(big * 0.23), fill=255)
    icon = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    icon.paste(base, (0, 0), mask)

    d = ImageDraw.Draw(icon)
    # clipboard board
    d.rounded_rectangle([250, 220, 774, 840], radius=70, fill=(255, 255, 255, 255))
    # clip at the top
    clip = (49, 46, 129, 255) if not paused else (55, 65, 81, 255)
    d.rounded_rectangle([380, 160, 644, 300], radius=50, fill=clip)
    # lines of text
    line = (124, 140, 255, 255) if not paused else (156, 163, 175, 255)
    for i, width in enumerate([360, 300, 220]):
        y = 420 + i * 120
        d.rounded_rectangle([330, y, 330 + width, y + 56], radius=28, fill=line)

    if paused:
        # an amber badge with pause bars
        d.ellipse([600, 600, 1000, 1000], fill=(245, 158, 11, 255))
        d.rounded_rectangle([715, 690, 775, 910], radius=18, fill=(255, 255, 255, 255))
        d.rounded_rectangle([825, 690, 885, 910], radius=18, fill=(255, 255, 255, 255))
    return icon


def save(icon, name):
    frames = [icon.resize((s, s), Image.LANCZOS) for s in SIZES]
    frames[-1].save(OUT / name, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    active = draw_icon(paused=False)
    save(active, "ClipNest.ico")
    save(active, "TrayActive.ico")
    save(draw_icon(paused=True), "TrayPaused.ico")
    active.resize((256, 256), Image.LANCZOS).save(OUT.parent.parent.parent / "docs" / "icon.png") if (OUT.parent.parent.parent / "docs").exists() else None
    print("icons written to", OUT)
