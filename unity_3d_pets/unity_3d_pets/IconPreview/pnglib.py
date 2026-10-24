"""通用 PNG 读写（纯标准库）。支持 8bit 的灰度/RGB/调色板/灰度+A/RGBA，
以及 tRNS 透明块。够用来处理生图工具输出的各种 PNG。"""
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


def read_png(path):
    """返回 (w, h, rows)，rows 是每行一个 bytes，格式统一为 RGBA8。"""
    d = open(path, 'rb').read()
    if d[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError('not a png')

    pos = 8
    w = h = bd = ct = None
    idat = b''
    plte = None
    trns = None
    while pos < len(d):
        ln = struct.unpack('>I', d[pos:pos + 4])[0]
        typ = d[pos + 4:pos + 8]
        data = d[pos + 8:pos + 8 + ln]
        pos += 12 + ln
        if typ == b'IHDR':
            w, h, bd, ct, comp, filt, interlace = struct.unpack('>IIBBBBB', data)
            if bd != 8:
                raise ValueError('only bit depth 8 supported, got %d' % bd)
            if interlace:
                raise ValueError('interlaced png not supported')
        elif typ == b'PLTE':
            plte = data
        elif typ == b'tRNS':
            trns = data
        elif typ == b'IDAT':
            idat += data
        elif typ == b'IEND':
            break

    nch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ct]
    raw = zlib.decompress(idat)
    stride = w * nch
    prev = bytearray(stride)
    out = []
    off = 0
    for _ in range(h):
        f = raw[off]
        line = bytearray(raw[off + 1:off + 1 + stride])
        off += 1 + stride
        if f == 1:
            for i in range(nch, stride):
                line[i] = (line[i] + line[i - nch]) & 255
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif f == 3:
            for i in range(stride):
                a = line[i - nch] if i >= nch else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i - nch] if i >= nch else 0
                b = prev[i]
                c = prev[i - nch] if i >= nch else 0
                line[i] = (line[i] + _paeth(a, b, c)) & 255
        elif f != 0:
            raise ValueError('bad filter %d' % f)
        out.append(_to_rgba(line, ct, nch, plte, trns))
        prev = line
    return w, h, out


def _to_rgba(line, ct, nch, plte, trns):
    n = len(line) // nch
    o = bytearray(n * 4)
    if ct == 6:
        return bytes(line)
    if ct == 2:
        for i in range(n):
            o[i * 4:i * 4 + 3] = line[i * 3:i * 3 + 3]
            o[i * 4 + 3] = 255
        return bytes(o)
    if ct == 0:
        for i in range(n):
            v = line[i]
            o[i * 4:i * 4 + 3] = bytes((v, v, v))
            o[i * 4 + 3] = 255
        return bytes(o)
    if ct == 4:
        for i in range(n):
            v = line[i * 2]
            o[i * 4:i * 4 + 3] = bytes((v, v, v))
            o[i * 4 + 3] = line[i * 2 + 1]
        return bytes(o)
    if ct == 3:
        for i in range(n):
            idx = line[i]
            p = plte[idx * 3:idx * 3 + 3]
            o[i * 4:i * 4 + 3] = p
            a = trns[idx] if (trns and idx < len(trns)) else 255
            o[i * 4 + 3] = a
        return bytes(o)
    raise ValueError('ct %d' % ct)


def write_png(path, w, h, rows):
    """rows：每行 RGBA8 的 bytes，或每行 4 元素像素列表。"""
    norm = []
    for r in rows:
        if isinstance(r, (bytes, bytearray)):
            norm.append(bytes(r))
        else:
            norm.append(bytes(bytearray(sum([list(p) for p in r], []))))
    raw = b''.join(b'\x00' + r for r in norm)

    def chunk(t, d):
        return (struct.pack('>I', len(d)) + t + d
                + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff))

    out = (b'\x89PNG\r\n\x1a\n'
           + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress(raw, 9))
           + chunk(b'IEND', b''))
    open(path, 'wb').write(out)