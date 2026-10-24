"""Third pass: fix the remaining defects.

A4 : fill interior holes (the blue hair-strand lines inside the silhouette)
D2 : proper paw pad outline (no square notch), toes on a real arc
C4 : small flat nostril slits placed low, smooth pad edge
"""

import math
import os

from PIL import Image, ImageDraw, ImageFilter

OUT = r"C:/dening_project/pets_game/art/icon"
BLUE = (0x4A, 0x90, 0xE2)
YELLOW = (0xFF, 0xD1, 0x66)
WHITE = (0xFF, 0xFF, 0xFF)
INK = (0x14, 0x14, 0x14)
S = 1024
SS = 4


def save(img, name):
    p = os.path.join(OUT, f"{name}.png")
    img.save(p, "PNG", optimize=True)
    for size in (512, 144):
        img.resize((size, size), Image.LANCZOS).save(
            os.path.join(OUT, f"{name}_{size}.png"), "PNG", optimize=True
        )
    print(f"wrote {name}: {os.path.getsize(p)//1024} KB")


def catmull(points, seg=26, closed=True):
    pts = list(points)
    if closed:
        pts = [pts[-1]] + pts + [pts[0], pts[1]]
    else:
        pts = [pts[0]] + pts + [pts[-1]]
    out = []
    for i in range(len(pts) - 3):
        p0, p1, p2, p3 = pts[i], pts[i + 1], pts[i + 2], pts[i + 3]
        for j in range(seg):
            t = j / seg
            t2, t3 = t * t, t * t * t
            out.append(
                (
                    0.5 * ((2 * p1[0]) + (-p0[0] + p2[0]) * t
                           + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2
                           + (-p0[0] + 3 * p1[0] - 3 * p2[0] + p3[0]) * t3),
                    0.5 * ((2 * p1[1]) + (-p0[1] + p2[1]) * t
                           + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2
                           + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t3),
                )
            )
    return out


# ------------------------------------------------------------------ A4
def fix_silhouette():
    src = os.path.join(OUT, "icon_A3_clean_profile.png")
    im = Image.open(src).convert("L")  # white silhouette on blue (blue ~ L132)
    im = im.point(lambda v: 255 if v > 200 else 0)

    # flood fill from the border to mark the true outside
    from collections import deque

    w, h = im.size
    px = im.load()
    outside = bytearray(w * h)
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            if px[x, y] == 0 and not outside[y * w + x]:
                outside[y * w + x] = 1
                q.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if px[x, y] == 0 and not outside[y * w + x]:
                outside[y * w + x] = 1
                q.append((x, y))
    while q:
        x, y = q.popleft()
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < w and 0 <= ny < h and not outside[ny * w + nx] and px[nx, ny] == 0:
                outside[ny * w + nx] = 1
                q.append((nx, ny))

    filled = Image.new("L", (w, h), 255)
    fpx = filled.load()
    for y in range(h):
        row = y * w
        for x in range(w):
            if px[x, y] == 0 and outside[row + x]:
                fpx[x, y] = 0

    # smooth the edge a touch
    filled = filled.filter(ImageFilter.GaussianBlur(1.0)).point(
        lambda v: 255 if v > 128 else 0
    )

    out = Image.new("RGB", (w, h), BLUE)
    out.paste(Image.new("RGB", (w, h), WHITE), (0, 0), filled)
    return out


# ------------------------------------------------------------------ D2
def build_paw():
    img = Image.new("RGB", (S * SS, S * SS), BLUE)
    d = ImageDraw.Draw(img)

    def poly(pts):
        d.polygon([(x * SS, y * SS) for x, y in pts], fill=WHITE)

    # main pad
    pad = [
        (512, 474),
        (592, 482),
        (652, 520),
        (676, 586),
        (656, 660),
        (588, 716),
        (512, 734),
        (436, 716),
        (368, 660),
        (348, 586),
        (372, 520),
        (432, 482),
    ]
    poly(catmull(pad, 30))

    # four toes, outer ones lower and smaller
    toes = [
        (300, 442, 62, 76, -0.20),
        (428, 356, 70, 86, -0.06),
        (596, 356, 70, 86, 0.06),
        (724, 442, 62, 76, 0.20),
    ]
    for cx, cy, rx, ry, ang in toes:
        pts = []
        for i in range(80):
            t = 2 * math.pi * i / 80
            x, y = rx * math.cos(t), ry * math.sin(t)
            pts.append(
                (cx + x * math.cos(ang) - y * math.sin(ang),
                 cy + x * math.sin(ang) + y * math.cos(ang))
            )
        poly(pts)

    return img.resize((S, S), Image.LANCZOS)


# ------------------------------------------------------------------ C4
def build_nose():
    img = Image.new("RGB", (S * SS, S * SS), YELLOW)
    d = ImageDraw.Draw(img)

    def poly(pts, color=INK):
        d.polygon([(x * SS, y * SS) for x, y in pts], fill=color)

    def line(pts, width, color=INK):
        p = [(x * SS, y * SS) for x, y in pts]
        d.line(p, fill=color, width=int(width * SS), joint="curve")
        r = width * SS / 2
        for x, y in (p[0], p[-1]):
            d.ellipse([x - r, y - r, x + r, y + r], fill=color)

    # pad: wider and flatter than before, smooth outline
    pad = [
        (512, 344),
        (620, 352),
        (686, 392),
        (700, 442),
        (676, 492),
        (600, 548),
        (544, 574),
        (512, 580),
        (480, 574),
        (424, 548),
        (348, 492),
        (324, 442),
        (338, 392),
        (404, 352),
    ]
    poly(catmull(pad, 28))

    # nostrils: small flat slits, low on the pad
    for mirror in (-1, 1):
        x0 = 512 + mirror * 108
        x1 = 512 + mirror * 62
        line([(x0, 428), (x1, 442)], 26, YELLOW)

    # short philtrum
    line([(512, 588), (512, 640)], 22)

    # muzzle line: tiny V under the philtrum
    line([(468, 664), (512, 682), (556, 664)], 20)

    return img.resize((S, S), Image.LANCZOS)


if __name__ == "__main__":
    save(fix_silhouette(), "icon_A4_profile")
    save(build_paw(), "icon_D2_paw")
    save(build_nose(), "icon_C4_nose")
