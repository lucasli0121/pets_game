"""探测 AI 图标表的 alpha 投影结构，用来定位每个图格外框。"""
import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pnglib import read_png

src = sys.argv[1]
w, h, px = read_png(src)
print('size', w, h)

TH = 24


def colprofile(y0, y1):
    cols = []
    for x in range(w):
        n = 0
        for y in range(y0, y1):
            if px[y][x * 4 + 3] > TH:
                n += 1
        cols.append(n)
    segs = []
    x = 0
    while x < w:
        if cols[x] > 0:
            x0 = x
            while x < w and cols[x] > 0:
                x += 1
            segs.append((x0, x))
        else:
            x += 1
    return segs


rows = []
for y in range(h):
    n = 0
    for x in range(w):
        if px[y][x * 4 + 3] > TH:
            n += 1
    rows.append(n)

bands = []
y = 0
while y < h:
    if rows[y] > 0:
        y0 = y
        while y < h and rows[y] > 0:
            y += 1
        bands.append((y0, y))
    else:
        y += 1

print('row bands:', bands)
for (y0, y1) in bands:
    print('band', (y0, y1), 'h=', y1 - y0, 'col segs:', colprofile(y0, y1))

# 顺便看一下实际颜色（取一个非透明像素）
for y in range(h):
    for x in range(w):
        if px[y][x * 4 + 3] > 200:
            print('sample color', px[y][x * 4:x * 4 + 4])
            raise SystemExit(0)