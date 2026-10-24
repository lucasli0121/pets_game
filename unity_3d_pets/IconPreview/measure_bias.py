import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pnglib

ICON_DIR = r"C:\dening_project\pets_game\unity_3d_pets\unity_3d_pets\Assets\Resources\PetIcons"
NAMES = ["heart", "pet", "gear", "food", "shop", "scene", "star", "paw", "bowl", "close"]

print("%-7s %-9s %-8s %-11s %-16s %s" % ("name", "canvas", "bbox", "bboxCenter", "alphaCentroid", "bias(px @display)"))
for n in NAMES:
    p = os.path.join(ICON_DIR, n + ".png")
    if not os.path.exists(p):
        print(n, "MISSING")
        continue
    w, h, rows = pnglib.read_png(p)
    sx = sy = sw = 0.0
    x0, y0, x1, y1 = w, h, -1, -1
    for y in range(h):
        r = rows[y]
        for x in range(w):
            a = r[x * 4 + 3] / 255.0
            if a > 0.05:
                sx += x * a
                sy += y * a
                sw += a
                if x < x0: x0 = x
                if x > x1: x1 = x
                if y < y0: y0 = y
                if y > y1: y1 = y
    if sw <= 0:
        print(n, "EMPTY alpha")
        continue
    bw = x1 - x0 + 1
    bh = y1 - y0 + 1
    bcx = x0 + (bw - 1) / 2.0
    bcy = y0 + (bh - 1) / 2.0
    gcx = sx / sw
    gcy = sy / sw
    k = 38.0 / w   # 心形在 UI 里是 38px
    print("%-7s %dx%-6d %dx%-5d (%.1f,%.1f) (%.2f,%.2f)  dx=%+.2f dy=%+.2f"
          % (n, w, h, bw, bh, bcx, bcy, gcx, gcy, -(gcx - bcx) * k, -(gcy - bcy) * k))
