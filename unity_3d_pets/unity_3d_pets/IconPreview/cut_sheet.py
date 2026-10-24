"""把AI 生成的图标表切成 10 张独立 PNG（纯白实心 + alpha 抗锯齿）。

生图工具给的图是不透明的浅灰底 + 深灰图形，没有透明通道，
所以这里用亮度阈值二值化反推 alpha：
  背景 lum≈223、图形 lum≈92，双峰干净，取 160 为界；
  阈值附近的过渡像素按线性插值给部分 alpha，等效于 1px 羽化。

输出统一 128x128、纯白 RGB、alpha 为形状覆盖度 —— 这样 Unity 里
Image.color 是乘法染色，白图标能被染成主蓝/红/灰而不会变脏。
"""
import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pnglib import read_png, write_png

NAMES = ['pet', 'food', 'shop', 'scene',
         'gear', 'heart', 'star', 'paw',
         'bowl', 'close']

SRC = sys.argv[1]
OUT = sys.argv[2]
SIZE = 128          # 输出边长
PAD_RATIO = 0.10    # bbox 外扩比例，让图形不贴边
TH = 160.0# 二值化阈值（亮度）
FEATHER = 60.0      # 阈值附近的过渡带宽度（亮度）

w, h, px = read_png(SRC)
lum = [[0.0] * w for _ in range(h)]
for y in range(h):
    row = px[y]
    lr = lum[y]
    for x in range(w):
        i = x * 4
        lr[x] = 0.299 * row[i] + 0.587 * row[i + 1] + 0.114 * row[i + 2]

# 覆盖率 alpha：0（背景）~ 1（图形）
cov = [[max(0.0, min(1.0, (TH + FEATHER - lum[y][x]) / (2 * FEATHER)))
        for x in range(w)] for y in range(h)]


def segments(vals):
    """把 0/非0 的序列聚成连续段。"""
    segs = []
    i = 0
    n = len(vals)
    while i < n:
        if vals[i] > 0.35:
            i0 = i
            while i < n and vals[i] > 0.35:
                i += 1
            segs.append((i0, i))
        else:
            i += 1
    return segs


# 行带（3 行）
rowmax = [max(cov[y]) for y in range(h)]
row_bands = segments(rowmax)

# 列段（每行带内单独切，避免跨行误并）
cells = []
for (y0, y1) in row_bands:
    colmax = [0.0] * w
    for x in range(w):
        colmax[x] = max(cov[y][x] for y in range(y0, y1))
    for (x0, x1) in segments(colmax):
        cells.append((x0, y0, x1, y1))

print('row_bands', row_bands)
print('cells found', len(cells))
for i, c in enumerate(cells):
    print('  %2d  x=%4d..%4d y=%4d..%4d  %dx%d'
          % (i, c[0], c[2], c[1], c[3], c[2] - c[0], c[3] - c[1]))

if len(cells) != len(NAMES):
    print('!!格子数 %d != 图标数 %d，先修prompt 或阈值再切' % (len(cells), len(NAMES)))
    sys.exit(1)


def save(idx, name):
    x0, y0, x1, y1 = cells[idx]
    # 二次收紧：段是用 0.35 阈值量的，可能带一圈半透明毛边
    bx0, bx1, by0, by1 = x0, x1, y0, y1
    while bx0 < bx1 and max(cov[y][bx0] for y in range(by0, by1)) < 0.5:
        bx0 += 1
    while bx1 > bx0 and max(cov[y][bx1 - 1] for y in range(by0, by1)) < 0.5:
        bx1 -= 1
    while by0 < by1 and max(cov[by0][x] for x in range(bx0, bx1)) < 0.5:
        by0 += 1
    while by1 > by0 and max(cov[by1 - 1][x] for x in range(bx0, bx1)) < 0.5:
        by1 -= 1

    bw, bh = bx1 - bx0, by1 - by0
    side = max(bw, bh)
    pad = int(round(side * PAD_RATIO))
    side2 = side + pad * 2
    # 等比缩放进 SIZE 画布，**bbox 严格居中**。
    # 源坐标 = bbox 原点 - pad + 输出坐标 / scale。
    # 注意是 x / scale，不是 x / SIZE / scale —— 后面那个多除了一个 SIZE，
    # 采样点会整体缩到 bbox 左上角附近、且因为减了 pad 而全落在留白区，
    # 结果是每张图的 alpha 恒为 0（图标看起来是全透明）。
    scale = SIZE / float(side2)
    # 居中偏移：画布比图形大一圈，这一圈要平分到两边。
    # 不居中的话视觉重心会偏（实测狗头偏上 24px、心形偏上 9px），
    # 表现就是「图标没放在背景正中间」。
    offX = (side - bw) / 2.0
    offY = (side - bh) / 2.0

    # **按 alpha 质心再校正一次**。bbox 居中只保证「外接矩形」居中，
    # 但心形这类上宽下尖的形状，重心明显偏上（实测偏 9/128≈7%），
    # 摆在胶囊里看着就是没对齐。这里以质心为准反推平移量。
    sx = sy = sw = 0.0
    for y in range(by0, by1):
        row = cov[y]
        for x in range(bx0, bx1):
            a = row[x]
            if a > 0.05:
                sx += x * a
                sy += y * a
                sw += a
    if sw > 0:
        biasX = sx / sw - (bx0 + bw * 0.5)
        biasY = sy / sw - (by0 + bh * 0.5)
        offX -= biasX
        offY -= biasY

    ox = bx0 - pad + offX
    oy = by0 - pad + offY

    rows = []
    for y in range(SIZE):
        row = bytearray()
        sy = oy + y / scale
        iy = int(sy)
        fy = sy - iy
        for x in range(SIZE):
            sx = ox + x / scale
            ix = int(sx)
            fx = sx - ix
            # 双线性采样：图标边缘是斜的，最近邻会在缩小时丢形状
            a = 0.0
            x0, x1 = ix, ix + 1
            y0, y1 = iy, iy + 1
            for (yy, wy) in ((y0, 1 - fy), (y1, fy)):
                if wy <= 0 or not (0 <= yy < h):
                    continue
                for (xx, wx) in ((x0, 1 - fx), (x1, fx)):
                    if wx <= 0 or not (0 <= xx < w):
                        continue
                    a += cov[yy][xx] * wx * wy
            av = int(round(max(0.0, min(1.0, a)) * 255))
            row += bytes((255, 255, 255, av))
        rows.append(bytes(row))
    write_png(os.path.join(OUT, name + '.png'), SIZE, SIZE, rows)
    nz = sum(1 for r in rows for i in range(3, SIZE * 4, 4) if r[i] > 8)
    print('saved %-6s bbox %dx%d -> %d  不透明像素 %d (%.1f%%)'
          % (name, bw, bh, SIZE, nz, nz * 100.0 / (SIZE * SIZE)))


for i, n in enumerate(NAMES):
    save(i, n)
print('done', len(NAMES))