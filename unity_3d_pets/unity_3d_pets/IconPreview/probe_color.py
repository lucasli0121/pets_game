"""探测图标表的颜色分布，判断能不能用亮度阈值把图标从背景里分出来。"""
import sys
import os
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pnglib import read_png

w, h, px = read_png(sys.argv[1])
print('size', w, h, 'ct-always-rgba')

c = Counter()
for y in range(0, h, 3):
    for x in range(0, w, 3):
        i = y * 4
        c[(px[y][i], px[y][i + 1], px[y][i + 2])] += 1
for col, n in c.most_common(12):
    lum = 0.299 * col[0] + 0.587 * col[1] + 0.114 * col[2]
    print('#%02x%02x%02x' % col, 'n=%d' % n, 'lum=%.1f' % lum)

# 直方图：按亮度分桶看是否有明显双峰
buckets = [0] * 16
for y in range(h):
    for x in range(w):
        i = y * 4
        lum = 0.299 * px[y][i] + 0.587 * px[y][i + 1] + 0.114 * px[y][i + 2]
        buckets[min(15, int(lum / 16))] += 1
print('lum histogram (bucket=16):', buckets)