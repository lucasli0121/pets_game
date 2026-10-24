"""Post-process the AI silhouette and draw a paw-print icon.

A_clean : take the AI side profile, strip the watermark, flatten the background
          to a single flat blue, centre the subject, keep pure white silhouette.
D_paw   : geometric paw print (one pad + four toes), the safest shape at 40px.
C3_nose : revised nose pad - wider, flat top, slit nostrils, no smile line.
"""

import os

from PIL import Image, ImageDraw, ImageFilter

OUT = r"C:/dening_project/pets_game/art/icon"
SRC = os.path.join(OUT, "App_icon__square_1_1__flat_vec_2026-10-18T12-06-35.png")
BLUE = (0x4A, 0x90, 0xE2)
YELLOW = (0xFF, 0xD1, 0x66)
WHITE = (0xFF, 0xFF, 0xFF)
INK = (0x14, 0x14, 0x14)
S = 1024


def save(img, name):
    os.makedirs(OUT, exist_ok=True)
    p = os.path.join(OUT, f"{name}.png")
    img.save(p, "PNG", optimize=True)
    for size in (512, 144):
        img.resize((size, size), Image.LANCZOS).save(
            os.path.join(OUT, f"{name}_{size}.png"), "PNG", optimize=True
        )
    print(f"wrote {name}: {os.path.getsize(p)//1024} KB")


# --------------------------------------------------------------- A_clean
def clean_ai():
    im = Image.open(SRC).convert("RGB")
    im = im.resize((S, S), Image.LANCZOS)

    # luminance mask: white silhouette vs blue background
    gray = im.convert("L")
    mask = gray.point(lambda v: 255 if v > 165 else 0, mode="L")

    # kill the watermark corner before measuring the subject
    d = ImageDraw.Draw(mask)
    d.rectangle([S * 0.74, S * 0.86, S, S], fill=0)

    bbox = mask.getbbox()
    if not bbox:
        raise RuntimeError("mask empty")
    pad = int(min(bbox[2] - bbox[0], bbox[3] - bbox[1]) * 0.06)
    bbox = (
        max(0, bbox[0] - pad),
        max(0, bbox[1] - pad),
        min(S, bbox[2] + pad),
        min(S, bbox[3] + pad),
    )

    subject = mask.crop(bbox)
    w, h = subject.size
    target = int(S * 0.74)
    k = target / max(w, h)
    subject = subject.resize((max(1, int(w * k)), max(1, int(h * k))), Image.LANCZOS)

    canvas = Image.new("L", (S, S), 0)
    canvas.paste(subject, ((S - subject.width) // 2, (S - subject.height) // 2))

    # soften then re-hard-edge so the outline is smooth but crisp
    canvas = canvas.filter(ImageFilter.GaussianBlur(1.2))
    canvas = canvas.point(lambda v: 255 if v > 128 else 0)

    out = Image.new("RGB", (S, S), BLUE)
    out.paste(Image.new("RGB", (S, S), WHITE), (0, 0), canvas)
    return out


# --------------------------------------------------------------- D_paw
def build_paw():
    ss = 4
    img = Image.new("RGB", (S * ss, S * ss), BLUE)
    d = ImageDraw.Draw(img)

    def E(cx, cy, rx, ry, color=WHITE):
        d.ellipse(
            [(cx - rx) * ss, (cy - ry) * ss, (cx + rx) * ss, (cy + ry) * ss], fill=color
        )

    # main pad: wide bottom, slightly narrower top
    E(512, 636, 176, 138)
    d.rectangle([(512 - 176) * ss, (636 - 138) * ss, (512 + 176) * ss, 636 * ss], fill=WHITE)
    E(512, 520, 150, 96)

    # four toes on an arc
    toes = [(-228, 372, 74, 88), (-86, 300, 78, 94), (86, 300, 78, 94), (228, 372, 74, 88)]
    for dx, cy, rx, ry in toes:
        E(512 + dx, cy, rx, ry)

    img = img.resize((S, S), Image.LANCZOS)
    return img


# --------------------------------------------------------------- C3 nose
def build_nose_v3():
    ss = 4
    img = Image.new("RGB", (S * ss, S * ss), YELLOW)
    d = ImageDraw.Draw(img)

    def P(pts, color=INK):
        d.polygon([(x * ss, y * ss) for x, y in pts], fill=color)

    def R(pts, width, color=INK):
        p = [(x * ss, y * ss) for x, y in pts]
        d.line(p, fill=color, width=int(width * ss), joint="curve")
        r = width * ss / 2
        for x, y in (p[0], p[-1]):
            d.ellipse([x - r, y - r, x + r, y + r], fill=color)

    # nose pad: flat top, tapering to a rounded bottom point
    pad = [
        (512, 336),
        (614, 342),
        (676, 372),
        (694, 420),
        (676, 470),
        (598, 528),
        (540, 560),
        (512, 566),
        (484, 560),
        (426, 528),
        (348, 470),
        (330, 420),
        (348, 372),
        (410, 342),
    ]
    P(pad)

    # nostrils as slanted slits
    for mirror in (-1, 1):
        x0, y0 = 512 + mirror * 96, 386
        x1, y1 = 512 + mirror * 62, 424
        R([(x0, y0), (x1, y1)], 34, YELLOW)

    # short philtrum
    R([(512, 570), (512, 626)], 26)

    # subtle mouth: two short strokes angled up from under the pad
    R([(452, 646), (512, 668)], 24)
    R([(512, 668), (572, 646)], 24)

    img = img.resize((S, S), Image.LANCZOS)
    return img


if __name__ == "__main__":
    save(clean_ai(), "icon_A3_clean_profile")
    save(build_paw(), "icon_D1_paw")
    save(build_nose_v3(), "icon_C3_nose")
