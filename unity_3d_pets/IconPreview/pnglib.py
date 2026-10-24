"""极简 PNG 读写（纯标准库）。

只支持 8bit：灰度 / 灰度+A / RGB / RGBA / 调色板(+tRNS)。
读出统一为 RGBA8（每行 bytes，长度 = w*4）。
"""

import struct
import zlib


def _paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def _unfilter(raw, w, h, bpp):
    stride = w * bpp
    out = bytearray()
    prev = bytearray(stride)
    pos = 0
    for _ in range(h):
        ft = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        pos += stride
        if ft == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 0xFF
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ft == 4:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                c = prev[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + _paeth(a, prev[i], c)) & 0xFF
        out += line
        prev = line
    return out


def read_png(path):
    """返回 (w, h, rows)。rows 每行是 RGBA8 bytes。"""
    with open(path, "rb") as f:
        data = f.read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "not a png: " + path
    pos = 8
    idat = bytearray()
    plte = None
    trns = None
    w = h = bitdepth = colortype = None
    while pos < len(data):
        (ln,) = struct.unpack(">I", data[pos:pos + 4])
        typ = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + ln]
        pos += 12 + ln
        if typ == b"IHDR":
            w, h, bitdepth, colortype = struct.unpack(">IIBB", body[:10])
        elif typ == b"PLTE":
            plte = body
        elif typ == b"tRNS":
            trns = body
        elif typ == b"IDAT":
            idat += body
        elif typ == b"IEND":
            break
    assert bitdepth == 8, "only 8bit supported, got " + str(bitdepth)
    nch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[colortype]
    raw = _unfilter(zlib.decompress(bytes(idat)), w, h, nch)
    stride = w * nch
    rows = []
    for y in range(h):
        line = raw[y * stride:(y + 1) * stride]
        rgba = bytearray(w * 4)
        if colortype == 6:
            rgba[:] = line
        elif colortype == 2:
            for x in range(w):
                rgba[x * 4:x * 4 + 3] = line[x * 3:x * 3 + 3]
                rgba[x * 4 + 3] = 255
        elif colortype == 0:
            for x in range(w):
                v = line[x]
                rgba[x * 4:x * 4 + 3] = bytes((v, v, v))
                rgba[x * 4 + 3] = 255
        elif colortype == 4:
            for x in range(w):
                v = line[x * 2]
                rgba[x * 4:x * 4 + 3] = bytes((v, v, v))
                rgba[x * 4 + 3] = line[x * 2 + 1]
        else:  # 3 调色板
            for x in range(w):
                idx = line[x]
                rgba[x * 4:x * 4 + 3] = plte[idx * 3:idx * 3 + 3]
                rgba[x * 4 + 3] = trns[idx] if (trns and idx < len(trns)) else 255
        rows.append(bytes(rgba))
    return w, h, rows


def write_png(path, w, h, rows):
    raw = bytearray()
    for row in rows:
        raw.append(0)
        raw += row if isinstance(row, (bytes, bytearray)) else bytes(row)
    comp = zlib.compress(bytes(raw), 9)

    def chunk(typ, body):
        return (struct.pack(">I", len(body)) + typ + body
                + struct.pack(">I", zlib.crc32(typ + body) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
                + chunk(b"IDAT", comp) + chunk(b"IEND", b""))
