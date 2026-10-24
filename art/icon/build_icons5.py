"""Final pass.

A6 : silhouette, holes filled, no notch patching, optically centred at 78%
D4 : paw print with a rounder pad outline
"""

import math
import os
from collections import deque

from PIL import Image, ImageDraw, ImageFilter

OUT = r"C:/dening_project/pets_game/art/icon"
BLUE = (0x4A, 0x90, 0xE2)
WHITE = (0xFF, 0xFF, 0xFF)
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


def catmull(points, seg=32, closed=True):
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


def fill_holes(mask):
    w, h = mask.size
    px = mask.load()
    outside = bytearray(w * h)
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            i = y * w + x
            if px[x, y] == 0 and not outside[i]:
                outside[i] = 1
                q.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            i = y * w + x
            if px[x, y] == 0 and not outside[i]:
                outside[i] = 1
                q.append((x, y))
    while q:
        x, y = q.popleft()
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < w and 0 <= ny < h:
                i = ny * w + nx
                if px[nx, ny] == 0 and not outside[i]:
                    outside[i] = 1
                    q.append((nx, ny))
    out = Image.new("L", (w, h), 255)
    op = out.load()
    for y in range(h):
        row = y * w
        for x in range(w):
            if px[x, y] == 0 and outside[row + x]:
                op[x, y] = 0
    return out


def build_silhouette():
    src = os.path.join(OUT, "icon_A3_clean_profile.png")
    im = Image.open(src).convert("L")
    mask = im.point(lambda v: 255 if v > 200 else 0)
    mask = fill_holes(mask)
    mask = mask.filter(ImageFilter.GaussianBlur(1.2)).point(lambda v: 255 if v > 128 else 0)

    bbox = mask.getbbox()
    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    subject = mask.crop(bbox)
    target = int(S * 0.80)
    k = target / max(w, h)
    subject = subject.resize((max(1, int(w * k)), max(1, int(h * k))), Image.LANCZOS)

    canvas = Image.new("L", (S, S), 0)
    canvas.paste(subject, ((S - subject.width) // 2, (S - subject.height) // 2))

    out = Image.new("RGB", (S, S), BLUE)
    out.paste(Image.new("RGB", (S, S), WHITE), (0, 0), canvas)
    return out


def build_paw():
    img = Image.new("RGB", (S * SS, S * SS), BLUE)
    d = ImageDraw.Draw(img)

    def poly(pts):
        d.polygon([(x * SS, y * SS) for x, y in pts], fill=WHITE)

    pad = [
        (512, 520),
        (560, 524),
        (612, 542),
        (652, 578),
        (668, 628),
        (666, 678),
        (644, 720),
        (600, 752),
        (556, 768),
        (512, 772),
        (468, 768),
        (424, 752),
        (380, 720),
        (358, 678),
        (356, 628),
        (372, 578),
        (412, 542),
        (464, 524),
    ]
    poly(catmull(pad, 32))

    toes = [
        (320, 484, 56, 68, -0.22),
        (440, 406, 66, 80, -0.06),
        (584, 406, 66, 80, 0.06),
        (704, 484, 56, 68, 0.22),
    ]
    for cx, cy, rx, ry, ang in toes:
        pts = []
        for i in range(96):
            t = 2 * math.pi * i / 96
            x, y = rx * math.cos(t), ry * math.sin(t)
            pts.append(
                (cx + x * math.cos(ang) - y * math.sin(ang),
                 cy + x * math.sin(ang) + y * math.cos(ang))
            )
        poly(pts)

    return img.resize((S, S), Image.LANCZOS)


if __name__ == "__main__":
    save(build_silhouette(), "icon_A6_profile")
    save(build_paw(), "icon_D4_paw")
