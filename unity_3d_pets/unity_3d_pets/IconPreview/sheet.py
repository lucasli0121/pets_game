"""把 PetIcons 导出的单图标 PNG 拼成一张对比图。纯标准库，不依赖 PIL。"""
import struct, zlib, sys, os


def read_png(p):
    d = open(p, 'rb').read()
    pos, w, h, idat = 8, None, None, b''
    while pos < len(d):
        ln = struct.unpack('>I', d[pos:pos + 4])[0]
        typ = d[pos + 4:pos + 8]
        data = d[pos + 8:pos + 8 + ln]
        pos += 12 + ln
        if typ == b'IHDR':
            w, h, bd, ct = struct.unpack('>IIBB', data[:10])
        elif typ == b'IDAT':
            idat += data
    raw = zlib.decompress(idat)
    px, stride, prev = [], w * 4, bytearray(w * 4)
    for y in range(h):
        f = raw[y * (stride + 1)]
        line = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        if f == 1:
            for i in range(4, stride):
                line[i] = (line[i] + line[i - 4]) & 255
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif f == 3:
            for i in range(stride):
                a = line[i - 4] if i >= 4 else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i - 4] if i >= 4 else 0
                b = prev[i]
                c = prev[i - 4] if i >= 4 else 0
                pp = a + b - c
                pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        px.append(bytes(line))
        prev = line
    return w, h, px


def write_png(path, w, h, rows):
    raw = b''.join(b'\x00' + r for r in rows)

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)

    out = (b'\x89PNG\r\n\x1a\n'
           + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress(raw))
           + chunk(b'IEND', b''))
    open(path, 'wb').write(out)


NAMES = ['pet', 'food', 'shop', 'scene', 'gear', 'heart', 'star', 'paw', 'bowl', 'close']
SIZE, PAD, COLS = 128, 16, 5
RGB = (47, 127, 240)

d = os.path.dirname(os.path.abspath(__file__))
rows_n = (len(NAMES) + COLS - 1) // COLS
W = COLS * (SIZE + PAD) + PAD
H = rows_n * (SIZE + PAD) + PAD
canvas = [[(255, 255, 255, 255)] * W for _ in range(H)]

for i, n in enumerate(NAMES):
    w, h, px = read_png(os.path.join(d, n + '.png'))
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

write_png(os.path.join(d, '_sheet.png'), W, H,
          [bytes(bytearray(sum([list(c) for c in row], []))) for row in canvas])
print('ok', W, H)
