"""Generate app icons for the pet game by drawing precise vector-like shapes.

Two directions:
  A: deep blue background + white dog head silhouette
  C: warm yellow background + black dog nose close-up

All output is pure flat color, no gradient, no watermark, 1024x1024 master
then downscaled to 512 and 144.
"""

import math
import os

from PIL import Image, ImageDraw

OUT = r"C:/dening_project/pets_game/art/icon"
BLUE = (0x4A, 0x90, 0xE2, 255)
YELLOW = (0xFF, 0xD1, 0x66, 255)
WHITE = (0xFF, 0xFF, 0xFF, 255)
INK = (0x14, 0x14, 0x14, 255)

S = 1024
SS = 4  # supersample factor


def catmull_rom(points, samples_per_seg=24, closed=True):
    """Return a dense polyline through the given control points."""
    pts = list(points)
    if closed:
        pts = [pts[-1]] + pts + [pts[0], pts[1]]
    else:
        pts = [pts[0]] + pts + [pts[-1]]
    out = []
    for i in range(len(pts) - 3):
        p0, p1, p2, p3 = pts[i], pts[i + 1], pts[i + 2], pts[i + 3]
        for j in range(samples_per_seg):
            t = j / samples_per_seg
            t2, t3 = t * t, t * t * t
            x = 0.5 * (
                (2 * p1[0])
                + (-p0[0] + p2[0]) * t
                + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2
                + (-p0[0] + 3 * p1[0] - 3 * p2[0] + p3[0]) * t3
            )
            y = 0.5 * (
                (2 * p1[1])
                + (-p0[1] + p2[1]) * t
                + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2
                + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t3
            )
            out.append((x, y))
    return out


def scaled(points, k):
    return [(x * k, y * k) for x, y in points]


def ellipse_pts(cx, cy, rx, ry, n=64):
    return [
        (cx + rx * math.cos(2 * math.pi * i / n), cy + ry * math.sin(2 * math.pi * i / n))
        for i in range(n)
    ]


def rounded_polyline(draw, pts, width, color):
    """Draw a polyline with round caps and joins."""
    w = width
    draw.line(pts, fill=color, width=int(w), joint="curve")
    for x, y in (pts[0], pts[-1]):
        r = w / 2
        draw.ellipse([x - r, y - r, x + r, y + r], fill=color)


# ---------------------------------------------------------------- direction C
def build_nose(with_whiskers):
    img = Image.new("RGBA", (S * SS, S * SS), YELLOW)
    d = ImageDraw.Draw(img)

    # main nose pad: wide at the top, tapering to a rounded point at the bottom
    pad = [
        (512, 318),
        (612, 330),
        (676, 372),
        (690, 424),
        (652, 486),
        (566, 552),
        (520, 580),
        (504, 580),
        (458, 552),
        (372, 486),
        (334, 424),
        (348, 372),
        (412, 330),
    ]
    d.polygon(scaled(catmull_rom(pad, 30), SS), fill=INK)

    # nostrils: two rotated ellipses punched out in the background colour
    for cx, cy in ((452, 404), (572, 404)):
        nx, ny = 34, 24
        ang = math.radians(-14)
        pts = []
        for i in range(72):
            t = 2 * math.pi * i / 72
            x, y = nx * math.cos(t), ny * math.sin(t)
            pts.append(
                (
                    cx + x * math.cos(ang) - y * math.sin(ang),
                    cy + x * math.sin(ang) + y * math.cos(ang),
                )
            )
        d.polygon(scaled(pts, SS), fill=YELLOW)

    # philtrum line
    rounded_polyline(d, scaled([(512, 592), (512, 672)], SS), 30 * SS, INK)

    # mouth curve
    mouth = catmull_rom(
        [(398, 700), (456, 748), (512, 758), (568, 748), (626, 700)], 24, closed=False
    )
    rounded_polyline(d, scaled(mouth, SS), 30 * SS, INK)

    if with_whiskers:
        for base_x, dirn in ((330, -1), (694, 1)):
            for i, (dx, dy) in enumerate(((0, -1), (1, 0), (0, 1))):
                x0 = base_x
                y0 = 640 + i * 58
                x1 = base_x + dirn * 96
                y1 = y0 + dy * 66
                rounded_polyline(d, scaled([(x0, y0), (x1, y1)], SS), 24 * SS, INK)

    return img.resize((S, S), Image.LANCZOS)


# ---------------------------------------------------------------- direction A
def build_silhouette(front):
    img = Image.new("RGBA", (S * SS, S * SS), BLUE)
    d = ImageDraw.Draw(img)

    if not front:
        # side profile facing right
        outline = [
            (742, 452),   # nose tip
            (694, 444),   # upper lip
            (648, 410),   # bridge
            (606, 372),   # stop
            (566, 306),   # forehead
            (542, 236),   # front ear base
            (524, 168),   # front ear tip
            (572, 224),   # front ear back edge
            (600, 264),   # dip between ears
            (620, 196),   # rear ear base
            (652, 152),   # rear ear tip
            (700, 232),   # rear ear back edge
            (740, 300),   # back of skull
            (806, 364),   # neck ruff upper
            (826, 468),   # ruff outer
            (768, 548),   # neck side
            (664, 604),   # chest
            (548, 634),   # chest bottom
            (470, 596),   # lower chest
            (486, 536),   # throat
            (556, 512),   # jaw underside
            (676, 484),   # lower lip
        ]
        d.polygon(scaled(catmull_rom(outline, 26), SS), fill=WHITE)
    else:
        # front view, symmetrical head with two upright ears
        head = [
            (512, 232),
            (566, 250),
            (600, 300),
            (612, 372),
            (600, 448),
            (556, 512),
            (512, 534),
            (468, 512),
            (424, 448),
            (412, 372),
            (424, 300),
            (458, 250),
        ]
        d.polygon(scaled(catmull_rom(head, 26), SS), fill=WHITE)
        # muzzle
        muzzle = ellipse_pts(512, 496, 96, 78)
        d.polygon(scaled(muzzle, SS), fill=WHITE)
        # ears
        for mirror in (-1, 1):
            ear = [
                (512 + mirror * 46, 306),
                (512 + mirror * 74, 186),
                (512 + mirror * 132, 214),
                (512 + mirror * 128, 300),
                (512 + mirror * 96, 356),
            ]
            if mirror < 0:
                ear = ear[::-1]
            d.polygon(scaled(catmull_rom(ear, 22), SS), fill=WHITE)
        # neck
        neck = [(392, 470), (632, 470), (700, 690), (324, 690)]
        d.polygon(scaled(neck, SS), fill=WHITE)

    return img.resize((S, S), Image.LANCZOS)


def save(img, name):
    os.makedirs(OUT, exist_ok=True)
    big = os.path.join(OUT, f"{name}.png")
    img.save(big, "PNG", optimize=True)
    for size in (512, 144):
        img.resize((size, size), Image.LANCZOS).save(
            os.path.join(OUT, f"{name}_{size}.png"), "PNG", optimize=True
        )
    print("wrote", big, os.path.getsize(big) // 1024, "KB")


if __name__ == "__main__":
    save(build_nose(False), "icon_C1_nose")
    save(build_nose(True), "icon_C2_nose_whiskers")
    save(build_silhouette(False), "icon_A1_profile")
    save(build_silhouette(True), "icon_A2_front")
