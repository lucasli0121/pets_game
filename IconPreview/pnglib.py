"""纯标准库 PNG 读写（8bit）。

支持 color type：0 灰度 / 2 RGB / 3 调色板 / 4 灰度+A / 6 RGBA，以及 tRNS。
read_png 统一输出 (w, h, rows)，rows[y] 是长度 w*4 的 bytes（RGBA8）。
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


def _unfilter(raw, w, h, bpp, stride):
    out = bytearray(h * stride)
    pos = 0
    for y in range(h):
        ft = raw[pos]
        pos += 1
        line = raw[pos:pos + stride]
        pos += stride
        base = y * stride
        prev = base - stride
        for i in range(stride):
            a = out[base + i - bpp] if i >= bpp else 0
            b = out[prev + i] if y > 0 else 0
            c = out[prev + i - bpp] if (y > 0 and i >= bpp) else 0
            x = line[i]
            if ft == 0:
                v = x
            elif ft == 1:
                v = x + a
            elif ft == 2:
                v = x + b
            elif ft == 3:
                v = x + (a + b) // 2
            elif ft == 4:
                v = x + _paeth(a, b, c)
            else:
                raise ValueError("bad filter type %d" % ft)
            out[base + i] = v & 0xFF
    return out


def read_png(path):
    """返回 (w, h, rows)。rows[y] = bytes of length w*4, RGBA8。"""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a png")

    pos = 8
    ihdr = None
    idat = bytearray()
    plte = None
    trns = None
    while pos < len(data):
        (ln,) = struct.unpack(">I", data[pos:pos + 4])
        typ = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + ln]
        pos += 12 + ln
        if typ == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", chunk)
        elif typ == b"IDAT":
            idat += chunk
        elif typ == b"PLTE":
            plte = chunk
        elif typ == b"tRNS":
            trns = chunk
        elif typ == b"IEND":
            break

    w, h, depth, ctype, comp, filt, inter = ihdr
    if depth != 8 or inter != 0:
        raise ValueError("only 8bit non-interlaced supported (depth=%d inter=%d)" % (depth, inter))

    raw = zlib.decompress(bytes(idat))
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    stride = w * channels
    out = _unfilter(raw, w, h, channels, stride)

    rows = []
    for y in range(h):
        base = y * stride
        row = bytearray(w * 4)
        for x in range(w):
            i = base + x * channels
            if ctype == 6:
                row[x * 4:x * 4 + 4] = out[i:i + 4]
            elif ctype == 2:
                row[x * 4:x * 4 + 3] = out[i:i + 3]
                row[x * 4 + 3] = 255
            elif ctype == 0:
                g = out[i]
                row[x * 4:x * 4 + 3] = bytes((g, g, g))
                row[x * 4 + 3] = 255
            elif ctype == 4:
                g, a = out[i], out[i + 1]
                row[x * 4:x * 4 + 3] = bytes((g, g, g))
                row[x * 4 + 3] = a
            elif ctype == 3:
                idx = out[i] * 3
                row[x * 4:x * 4 + 3] = plte[idx:idx + 3]
                t = trns[out[i]] if (trns is not None and out[i] < len(trns)) else 255
                row[x * 4 + 3] = t
        rows.append(bytes(row))
    return w, h, rows


def _chunk(typ, payload):
    return (struct.pack(">I", len(payload)) + typ + payload
            + struct.pack(">I", zlib.crc32(typ + payload) & 0xFFFFFFFF))


def write_png(path, w, h, rows):
    """rows[y] = bytes/bytearray of length w*4 (RGBA8) 或长度 w*4 的像素序列。"""
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        r = rows[y]
        raw += r if isinstance(r, (bytes, bytearray)) else bytes(r)
    body = zlib.compress(bytes(raw), 9)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(_chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)))
        f.write(_chunk(b"IDAT", body))
        f.write(_chunk(b"IEND", b""))
