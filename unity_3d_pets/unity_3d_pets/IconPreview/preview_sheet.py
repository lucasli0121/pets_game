"""把切好的单图标 PNG 拼成对比图，用来肉眼确认切图有没有错位。
纯标准库。可选 -bg 指定底色（默认白底，浅色图标看不清时用深底）。"""
import sys
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pnglib import read_png, write_png

NAMES = ['pet', 'food', 'shop', 'scene', 'gear',
         'heart', 'star', 'paw', 'bowl', 'close']

d = os.path.dirname(os.path.abspath(__file__))
src_dir = sys.argv[1] if len(sys.argv) > 1 else d
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(d, '_sheet.png')
dark = len(sys.argv) > 3 and sys.argv[3] == 'dark'

SIZE, PAD, COLS = 128, 16, 5
RGB = (47, 127, 240)
BG = (30, 34, 40) if dark else (255, 255, 255)

rows_n = (len(NAMES) + COLS - 1) // COLS
W = COLS * (SIZE + PAD) + PAD
H = rows_n * (SIZE + PAD) + PAD
canvas = [[BG + (255,)] * W for _ in range(H)]

for i, n in enumerate(NAMES):
    w, h, px = read_png(os.path.join(src_dir, n + '.png'))
    ox = PAD + (i % COLS) * (SIZE + PAD)
    oy = PAD + (i // COLS) * (SIZE + PAD)
    for y in range(h):
        for x in range(w):
            a = px[y][x * 4 + 3] / 255
            if a <= 0:
                continue
            cx, cy = ox + x, oy + y
            if 0 <= cx < W and 0 <= cy < H:
                r, g, b, _ = canvas[cy][cx]
                canvas[cy][cx] = (int(r + (RGB[0] - r) * a),
                                  int(g + (RGB[1] - g) * a),
                                  int(b + (RGB[2] - b) * a), 255)

write_png(out, W, H,
          [bytes(bytearray(sum([list(c) for c in row], []))) for row in canvas])
print('ok', out, W, H)