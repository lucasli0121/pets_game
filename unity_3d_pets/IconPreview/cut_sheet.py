"""从图标表大图切出单张 128x128 图标（纯白 RGB + alpha 覆盖度）。

大图是不透明浅灰底（lum≈223）+ 深灰图形（lum≈92），所以：
  1. 用亮度阈值 160±60 羽化反推每个像素的 alpha；
  2. alpha 投影分出行带、列段，每段一个图标；
  3. 段内二次收紧 bbox；
  4. **按 alpha 质心反推平移**，让图形视觉重心落在画布正中；
  5. 双线性缩放到 128x128 输出。

质心平移的符号很容易写反，所以脚本最后会自检并打印每个图标的
「质心 - bbox中心」，全为 0（±0.5 内）才算通过。
"""

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pnglib

SIZE = 128
THRESH = 160.0
FEATHER = 60.0
GAP = 4          # 行带/列段之间至少空这么多像素才算切开
ALPHA_MIN = 0.02

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "ai",
                   "A_sprite_sheet_of_12_flat_vect_2026-10-19T14-24-07.png")
OUT = r"C:\dening_project\pets_game\unity_3d_pets\unity_3d_pets\Assets\Resources\PetIcons"

# 大图里的图标顺序（12 格，前 10 个用）
NAMES = ["pet", "food", "shop", "scene", "gear", "heart",
         "star", "paw", "bowl", "close", "ignore10", "ignore11"]


def coverage(rows, w, h):
    """从大图反推 alpha 覆盖度。

    大图是不透明浅灰底（双峰 lum≈223 / 250）+ 深灰图形（峰 lum≈92）。
    直方图显示 lum 100~205 之间还散着大量孤立噪点（每 bin 20~50 个），
    直接按亮度反推会把整张图判成「有内容」，切出来是铺满的脏底。

    做法：
      1. 核心 = lum < 140（图形实心部分），噪点不会连成大块；
      2. 4 邻域连通域，丢掉面积小的域（噪点）；
      3. 图形 bbox 内，alpha = (205 - lum) / (205 - 90)，得到羽化边缘；
         bbox 外一律 0（这样背景噪点全被排除）；
      4. 3x3 纯卷积（固定 1/9 权重）磨掉锯齿。

    **不能**用「3x3 邻域计数 + s/n」那种加权平均：n 在 1~9 之间跳，
    alpha 会量化成方块，出来是一张网格纹理图。
    """
    lum = [[0.0] * w for _ in range(h)]
    for y in range(h):
        r = rows[y]
        line = lum[y]
        for x in range(w):
            line[x] = (r[x * 4] * 299 + r[x * 4 + 1] * 587 + r[x * 4 + 2] * 114) / 1000.0

    # 1. 核心
    core = [[1 if lum[y][x] < 140.0 else 0 for x in range(w)] for y in range(h)]

    # 2. 连通域（4 邻域，迭代式泛洪）
    lab = [[0] * w for _ in range(h)]
    cur = 0
    sizes = {}
    for y0 in range(h):
        for x0 in range(w):
            if core[y0][x0] == 0 or lab[y0][x0] != 0:
                continue
            cur += 1
            stack = [(y0, x0)]
            lab[y0][x0] = cur
            n = 0
            while stack:
                y, x = stack.pop()
                n += 1
                for (dy, dx) in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    yy = y + dy
                    xx = x + dx
                    if 0 <= yy < h and 0 <= xx < w and core[yy][xx] and lab[yy][xx] == 0:
                        lab[yy][xx] = cur
                        stack.append((yy, xx))
            sizes[cur] = n

    # 3. 保留大连通域（图形），其余（噪点）丢掉
    keep_ids = set(i for i, n in sizes.items() if n >= 400)
    core_mask = [[1 if (lab[y][x] in keep_ids and keep_ids) else 0
                   for x in range(w)] for y in range(h)]

    # 4. 膨胀 3 圈，把抗锯齿的羽化边缘圈进来
    grown = [row[:] for row in core_mask]
    for _ in range(3):
        nxt = [row[:] for row in grown]
        for y in range(h):
            for x in range(w):
                if grown[y][x]:
                    continue
                if ((y > 0 and grown[y - 1][x]) or (y < h - 1 and grown[y + 1][x])
                        or (x > 0 and grown[y][x - 1]) or (x < w - 1 and grown[y][x + 1])):
                    nxt[y][x] = 1
        grown = nxt

    # 5. alpha（膨胀区内按亮度羽化，膨胀区外为 0）
    raw = []
    for y in range(h):
        line = []
        for x in range(w):
            if grown[y][x]:
                a = (205.0 - lum[y][x]) / (205.0 - 90.0)
                line.append(0.0 if a <= 0.0 else (1.0 if a >= 1.0 else a))
            else:
                line.append(0.0)
        raw.append(line)

    # 5. 3x3 纯卷积
    cov = []
    for y in range(h):
        line = []
        for x in range(w):
            s = 0.0
            for dy in (-1, 0, 1):
                yy = y + dy
                if yy < 0 or yy >= h:
                    continue
                for dx in (-1, 0, 1):
                    xx = x + dx
                    if xx < 0 or xx >= w:
                        continue
                    s += raw[yy][xx]
            line.append(s / 9.0)
        cov.append(line)
    return cov


def bands(vals, thresh, merge_gap=0, min_h=4):
    """把投影里连续的非零段切出来。

    阈值要用「相对最大值」而不是绝对值 —— 图标内部有镂空，
    投影值会掉到几十，绝对阈值会把一个图标切成好几条。
    merge_gap 用来把同一行/列里被镂空打断的段接回去。
    """
    if not vals:
        return []
    peak = max(vals)
    if peak <= 0:
        return []
    out = []
    run = None
    gap = 0
    for i, v in enumerate(vals):
        if v > thresh * peak:
            if run is None:
                run = [i, i]
            else:
                run[1] = i
            gap = 0
        else:
            if run is not None:
                gap += 1
                if gap > merge_gap:
                    if run[1] - run[0] + 1 >= min_h:
                        out.append(tuple(run))
                    run = None
                    gap = 0
    if run is not None and run[1] - run[0] + 1 >= min_h:
        out.append(tuple(run))
    return out


def out_centroid(rows):
    """输出图（RGBA8 行）的 alpha 质心。"""
    sx = sy = sw = 0.0
    for y in range(len(rows)):
        r = rows[y]
        for x in range(len(r) // 4):
            a = r[x * 4 + 3] / 255.0
            if a > ALPHA_MIN:
                sx += x * a
                sy += y * a
                sw += a
    if sw <= 0:
        return 0.0, 0.0, 0.0
    return sx / sw, sy / sw, sw


def shift(rows, dx, dy):
    """整体平移 alpha 层（RGB 恒为白，不动）。"""
    h = len(rows)
    w = len(rows[0]) // 4
    out = []
    for y in range(h):
        sy = y - dy
        if sy < 0 or sy >= h:
            out.append(b"\xff" * (w * 4))
            continue
        src = rows[sy]
        row = bytearray(w * 4)
        for i in range(w):
            row[i * 4] = 255
            row[i * 4 + 1] = 255
            row[i * 4 + 2] = 255
        for x in range(w):
            sx = x - dx
            if 0 <= sx < w:
                row[x * 4 + 3] = src[sx * 4 + 3]
        out.append(bytes(row))
    return out


def centroid(cov, x0, y0, x1, y1):
    sx = sy = sw = 0.0
    for y in range(y0, y1 + 1):
        row = cov[y]
        for x in range(x0, x1 + 1):
            a = row[x]
            if a > ALPHA_MIN:
                sx += x * a
                sy += y * a
                sw += a
    if sw <= 0:
        return 0.0, 0.0, 0.0
    return sx / sw, sy / sw, sw


def cut(cov, w, h, bx0, by0, bx1, by1, name):
    """切一个图标，输出 (rows, 诊断)。

    **以 alpha 质心为画布中心**：窗口中心取图形质心，所以输出后质心正好落在
    画布正中，图形不需要在 UI 层再补偏移。
    窗口边长 S = max(bw + 2|dcx|, bh + 2|dcy|)，保证 bbox 完整落在窗口内
    （不裁剪），同时质心居中。图形在画布里的占比 = bh*128/S。
    """
    # 段内二次收紧 bbox。阈值放宽到 0.005，把图形自带的光晕/柔边也算进去 ——
    # 用 0.02 会把外圈淡光晕切掉，双线性缩放后边缘就贴到画布边上被削平。
    minx, maxx, miny, maxy = bx1, bx0, by1, by0
    for y in range(by0, by1 + 1):
        row = cov[y]
        for x in range(bx0, bx1 + 1):
            if row[x] > 0.005:
                if x < minx: minx = x
                if x > maxx: maxx = x
                if y < miny: miny = y
                if y > maxy: maxy = y
    bw = maxx - minx + 1
    bh = maxy - miny + 1
    if bw <= 0 or bh <= 0:
        return None, 0

    gx, gy, sw = centroid(cov, minx, miny, maxx, maxy)
    if sw <= 0:
        return None, 0
    dcx = gx - (minx + maxx) * 0.5
    dcy = gy - (miny + maxy) * 0.5

    # 窗口边长：既要装下 bbox（不裁剪），又以质心为窗口中心。
    # +12 是给羽化边缘 + 双线性缩放的溢出留的余量。
    side = max(bw + 2 * abs(dcx), bh + 2 * abs(dcy)) + 12
    scale = SIZE / float(side)
    # 源图窗口中心 = 质心
    base_ox = gx - side * 0.5
    base_oy = gy - side * 0.5

    out_rows = []
    for y in range(SIZE):
        row = bytearray(SIZE * 4)
        # 双线性：像素中心在 i+0.5，所以源坐标要减半个像素再取整
        sy_ = base_oy + (y + 0.5) / scale - 0.5
        iy = int(math.floor(sy_))
        fy = sy_ - iy
        for x in range(SIZE):
            sx_ = base_ox + (x + 0.5) / scale - 0.5
            ix = int(math.floor(sx_))
            fx = sx_ - ix
            a = 0.0
            for (yy, wy) in ((iy, 1.0 - fy), (iy + 1, fy)):
                if yy < 0 or yy >= h or wy <= 0.0:
                    continue
                crow = cov[yy]
                for (xx, wx) in ((ix, 1.0 - fx), (ix + 1, fx)):
                    if xx < 0 or xx >= w or wx <= 0.0:
                        continue
                    a += crow[xx] * wx * wy
            v = int(round(255 * (1.0 if a >= 1.0 else (0.0 if a <= 0.0 else a))))
            row[x * 4] = 255
            row[x * 4 + 1] = 255
            row[x * 4 + 2] = 255
            row[x * 4 + 3] = v
        out_rows.append(bytes(row))

    # 自检：质心偏差 + 图形是否被裁 + 画布占比
    gx2, gy2, sw2 = out_centroid(out_rows)
    ob = 0
    ox0, oy0, ox1, oy1 = SIZE, SIZE, -1, -1
    for y in range(SIZE):
        for x in range(SIZE):
            if out_rows[y][x * 4 + 3] > 255 * ALPHA_MIN:
                ob += 1
                if x < ox0: ox0 = x
                if x > ox1: ox1 = x
                if y < oy0: oy0 = y
                if y > oy1: oy1 = y
    # 触边判定：只有边界上有较高 alpha 才算真裁剪（羽化残留在 0.02 附近不算）
    edge_solid = False
    for x in range(SIZE):
        if out_rows[0][x * 4 + 3] > 80 or out_rows[SIZE - 1][x * 4 + 3] > 80:
            edge_solid = True
    for y in range(SIZE):
        if out_rows[y][0 * 4 + 3] > 80 or out_rows[y][(SIZE - 1) * 4 + 3] > 80:
            edge_solid = True
    target = (SIZE - 1) * 0.5
    diag = {
        "bias": (gx2 - target, gy2 - target),
        "bbox": (ox1 - ox0 + 1, oy1 - oy0 + 1),
        "clipped": edge_solid,
        "fill": (ox1 - ox0 + 1) / float(SIZE),
        "ink": ob,
    }
    return out_rows, diag


def main():
    w, h, rows = pnglib.read_png(SRC)
    print("src", w, h)
    cov = coverage(rows, w, h)

    rp = [sum(cov[y]) for y in range(h)]
    cp = [0] * w
    for y in range(h):
        row = cov[y]
        for x in range(w):
            cp[x] += row[x]

    # 阈值取峰值的 3%：滤掉噪点但不切碎图形；间隙 40px 把镂空接回去
    RB = bands(rp, 0.03, merge_gap=40, min_h=20)
    CB = bands(cp, 0.03, merge_gap=40, min_h=20)
    print("row bands:", len(RB), RB)
    print("col bands:", len(CB), CB)

    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    idx = 0
    for (ry0, ry1) in RB:
        # 只在这个行带范围内重算列投影
        sub = [0] * w
        for y in range(ry0, ry1 + 1):
            row = cov[y]
            for x in range(w):
                sub[x] += row[x]
        cbi = bands(sub, 0.03, merge_gap=40, min_h=20)
        for k in range(len(cbi)):
            if idx >= len(NAMES):
                break
            cx0, cx1 = cbi[k][0], cbi[k][1]
            name = NAMES[idx]
            idx += 1
            out, diag = cut(cov, w, h, cx0, ry0, cx1, ry1, name)
            if out is None:
                print("%-8s EMPTY" % name)
                continue
            pnglib.write_png(os.path.join(OUT, name + ".png"), SIZE, SIZE, out)
            print("%-8s srcBbox=%dx%d  outBbox=%dx%d fill=%.0f%%  centroidBias=(%+.2f, %+.2f)%s"
                  % (name, cx1 - cx0 + 1, ry1 - ry0 + 1,
                     diag["bbox"][0], diag["bbox"][1], diag["fill"] * 100,
                     diag["bias"][0], diag["bias"][1],
                     "  **CLIPPED**" if diag["clipped"] else ""))
    print("done, cells =", idx)


if __name__ == "__main__":
    main()
