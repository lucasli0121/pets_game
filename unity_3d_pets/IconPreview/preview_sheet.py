"""把图标目录拼成一张对比图（肉眼验用）。

白图标在白底上看不见，所以格子和图标必须用不同色阶：
  背景 / 格子底 / 图标 三者互不相同。

用法：preview_sheet.py <图标目录> <输出png> [dark]
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pnglib

CELL = 140
PAD = 14
COLS = 5

THEME = {
    "light": {
        "bg": (216, 222, 231),
        "cell": (150, 162, 178),
        "icon": (252, 253, 255),
    },
    "dark": {
        "bg": (16, 19, 25),
        "cell": (92, 102, 118),
        "icon": (248, 250, 253),
    },
}


def blend(dst, src, a):
    return int(dst * (1.0 - a) + src * a)


def main():
    d = sys.argv[1]
    out = sys.argv[2]
    theme = THEME["dark" if (len(sys.argv) > 3 and sys.argv[3] == "dark") else "light"]
    bg, cell, icon = theme["bg"], theme["cell"], theme["icon"]

    names = sorted(f[:-4] for f in os.listdir(d) if f.endswith(".png"))
    if not names:
        print("no png in", d)
        return
    nrows = (len(names) + COLS - 1) // COLS
    W = COLS * (CELL + PAD) + PAD
    H = nrows * (CELL + PAD) + PAD

    # 画布：底色铺满
    canvas = []
    for y in range(H):
        row = bytearray()
        for x in range(W):
            row += bytes((bg[0], bg[1], bg[2], 255))
        canvas.append(bytes(row))

    for i, n in enumerate(names):
        cw, ch, irows = pnglib.read_png(os.path.join(d, n + ".png"))
        cx = PAD + (i % COLS) * (CELL + PAD)
        cy = PAD + (i // COLS) * (CELL + PAD)

        # 格子底
        for y in range(cy, cy + CELL):
            if 0 <= y < H:
                row = bytearray(canvas[y])
                for x in range(cx, cx + CELL):
                    if 0 <= x < W:
                        row[x * 4:x * 4 + 3] = bytes(cell)
                canvas[y] = bytes(row)

        # 图标：alpha 合成到格子上
        for y in range(ch):
            sy = cy + (y * CELL) // ch
            if sy < 0 or sy >= H:
                continue
            src = irows[y]
            row = bytearray(canvas[sy])
            for x in range(cw):
                a = src[x * 4 + 3] / 255.0
                if a <= 0.0:
                    continue
                px = cx + (x * CELL) // cw
                if px < 0 or px >= W:
                    continue
                for k in range(3):
                    row[px * 4 + k] = blend(row[px * 4 + k], icon[k], a)
            canvas[sy] = bytes(row)

    pnglib.write_png(out, W, H, canvas)
    print("wrote", out, W, "x", H, "icons:", ", ".join(names))


if __name__ == "__main__":
    main()
