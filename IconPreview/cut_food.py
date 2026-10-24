"""把生图出来的 3x2 图标表切成 6 张正方形 PNG。

源图背景是**白色不透明**（生图的 transparent 参数没生效），所以判据不能用 alpha，
改用「距白距离」反推覆盖度：

    d = 255 - min(r,g,b)          # 白=0，饱和色=255
    a = clamp(d / D, 0, 1)        # D = 图内最深的 min 通道（估计墨色）
    color = (obs - (1-a)*255) / a # 按已知白底反预乘，恢复原色

为什么要反预乘：直接拿观测 RGB 当图标色，边缘会有一圈白边，
图标挪到非白底上（比如彩色的卡片）时那圈白边会显出来。

仍然按 **alpha 质心** 定窗口中心 —— bbox 中心 != 视觉中心
（蛋糕/重心偏下的图形尤其明显）。

用法：cut_food.py <源图> <输出目录>
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pnglib import read_png, write_png

SIZE = 256          # 输出边长
MARGIN = 14         # 图形外扩余量（抗锯齿边）
INK_MIN = 0.5       # 质心 / bbox 判定用的覆盖度阈值

# 表内顺序 -> 资源名（与 FoodItemConfig.iconKey 去掉 icon_ 前缀后对应）
NAMES = [
    "food_beef",     # (0,0) 一碗棕色颗粒狗粮
    "food_biscuit",  # (1,0) 两块金黄饼干
    "food_chicken",  # (2,0) 两条鸡肉干
    "food_cake",     # (0,1) 粉色生日蛋糕
    "food_normal",   # (1,1) 牛皮纸狗粮袋
    "coin",          # (2,1) 金币
]


def build_alpha(rows, w, h):
    """返回 (cov, rgb)：cov[y][x] = 0~1 覆盖度，rgb[y][x] = 反预乘后的 (r,g,b)。"""
    # 1. 先算原始距白距离，取全局最深值当墨色基准
    raw = []
    dmax = 0
    for y in range(h):
        line = []
        r = rows[y]
        for x in range(w):
            i = x * 4
            d = 255 - min(r[i], r[i + 1], r[i + 2])
            if d > dmax:
                dmax = d
            line.append(d)
        raw.append(line)
    D = max(200.0, min(255.0, float(dmax)))

    cov = []
    rgb = []
    for y in range(h):
        cline = []
        rline = []
        r = rows[y]
        for x in range(w):
            i = x * 4
            a = raw[y][x] / D
            if a > 1.0:
                a = 1.0
            if a < 0.003:
                a = 0.0
            cline.append(a)
            if a <= 0.0:
                rline.append((255, 255, 255))
            else:
                inv = 1.0 / a
                px = []
                for c in range(3):
                    v = (r[i + c] - (1.0 - a) * 255.0) * inv
                    px.append(0 if v < 0 else (255 if v > 255 else int(v + 0.5)))
                rline.append(tuple(px))
        cov.append(cline)
        rgb.append(rline)
    return cov, rgb


def cell_box(cov, w, h, x0, y0, x1, y1):
    """格子里覆盖度的 bbox 与质心。"""
    minx, miny, maxx, maxy = x1, y1, -1, -1
    sx = sy = sa = 0.0
    for y in range(y0, y1):
        line = cov[y]
        for x in range(x0, x1):
            a = line[x]
            if a < INK_MIN:
                continue
            if x < minx:
                minx = x
            if x > maxx:
                maxx = x
            if y < miny:
                miny = y
            if y > maxy:
                maxy = y
            sx += x * a
            sy += y * a
            sa += a
    if maxx < 0:
        return None
    return minx, miny, maxx, maxy, sx / sa, sy / sa


def cut(cov, rgb, w, h, name):
    cols, rows_n = 3, 2
    cw, ch = w // cols, h // rows_n
    idx = NAMES.index(name)
    cx, cy = idx % cols, idx // cols
    box = cell_box(cov, w, h, cx * cw, cy * ch, (cx + 1) * cw, (cy + 1) * ch)
    if box is None:
        return None
    minx, miny, maxx, maxy, gx, gy = box
    bw, bh = maxx - minx + 1, maxy - miny + 1

    # 窗口中心取质心：重心偏下的图形（蛋糕）才能落在画布正中
    dcx, dcy = gx - (minx + maxx) * 0.5, gy - (miny + maxy) * 0.5
    side = max(bw + 2 * abs(dcx), bh + 2 * abs(dcy)) + 2 * MARGIN
    ox, oy = gx - side * 0.5, gy - side * 0.5
    scale = SIZE / float(side)

    def sample(sx_, sy_):
        x0, y0 = int(sx_), int(sy_)
        fx, fy = sx_ - x0, sy_ - y0
        if x0 < 0 or y0 < 0 or x0 + 1 >= w or y0 + 1 >= h:
            return None
        a = (cov[y0][x0] * (1 - fx) * (1 - fy) + cov[y0][x0 + 1] * fx * (1 - fy)
             + cov[y0 + 1][x0] * (1 - fx) * fy + cov[y0 + 1][x0 + 1] * fx * fy)
        c = []
        for k in range(3):
            v = (rgb[y0][x0][k] * (1 - fx) * (1 - fy) + rgb[y0][x0 + 1][k] * fx * (1 - fy)
                 + rgb[y0 + 1][x0][k] * (1 - fx) * fy + rgb[y0 + 1][x0 + 1][k] * fx * fy)
            c.append(int(v + 0.5))
        return a, c

    out = []
    for y in range(SIZE):
        line = bytearray(SIZE * 4)
        for x in range(SIZE):
            s = sample(ox + (x + 0.5) / scale - 0.5, oy + (y + 0.5) / scale - 0.5)
            if s is None:
                continue
            a, c = s
            line[x * 4:x * 4 + 3] = bytes(c)
            line[x * 4 + 3] = int(a * 255 + 0.5)
        out.append(bytes(line))
    return out


def stats(rows, w, h):
    ink = 0
    sx = sy = sa = 0.0
    minx, miny, maxx, maxy = w, h, -1, -1
    for y in range(h):
        r = rows[y]
        for x in range(w):
            a = r[x * 4 + 3] / 255.0
            if a < INK_MIN:
                continue
            ink += 1
            if x < minx:
                minx = x
            if x > maxx:
                maxx = x
            if y < miny:
                miny = y
            if y > maxy:
                maxy = y
            sx += x * a
            sy += y * a
            sa += a
    if sa == 0:
        return None
    return ink / float(w * h), (sx / sa - w * 0.5, sy / sa - h * 0.5), (minx, miny, maxx, maxy)


def main():
    src = sys.argv[1]
    outdir = sys.argv[2] if len(sys.argv) > 2 else "."
    w, h, rows = read_png(src)
    print("src %dx%d" % (w, h))
    cov, rgb = build_alpha(rows, w, h)
    os.makedirs(outdir, exist_ok=True)

    ok = True
    for name in NAMES:
        out = cut(cov, rgb, w, h, name)
        if out is None:
            print("%-14s **EMPTY CELL**" % name)
            ok = False
            continue
        path = os.path.join(outdir, name + ".png")
        write_png(path, SIZE, SIZE, out)
        st = stats(out, SIZE, SIZE)
        ink, (dcx, dcy), bb = st
        clipped = (bb[0] <= 0 or bb[1] <= 0 or bb[2] >= SIZE - 1 or bb[3] >= SIZE - 1)
        flag = "OK"
        if abs(dcx) > 1.0 or abs(dcy) > 1.0:
            flag = "**BIAS**"
        if clipped:
            flag += " **CLIPPED**"
        if flag != "OK":
            ok = False
        print("%-14s ink=%.1f%% bbox=(%d,%d)-(%d,%d) dc=(%+.2f,%+.2f) %s"
              % (name, ink * 100, bb[0], bb[1], bb[2], bb[3], dcx, dcy, flag))
    print("ALL OK" if ok else "HAS PROBLEMS")


if __name__ == "__main__":
    main()
