#!/usr/bin/env python3
"""Renders src/Berichtsheft.Desktop/app.ico (pure Python, no dependencies)."""
import struct, zlib, os

def render(size, ss=4):
    n = size * ss
    def inside_round(x, y, x0, y0, x1, y1, r):
        if not (x0 <= x <= x1 and y0 <= y <= y1): return False
        cx = min(max(x, x0 + r), x1 - r); cy = min(max(y, y0 + r), y1 - r)
        return (x - cx) ** 2 + (y - cy) ** 2 <= r * r
    px = []
    for py in range(size):
        row = []
        for pxx in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    x = (pxx * ss + sx + .5) / n; y = (py * ss + sy + .5) / n
                    c = (0, 0, 0, 0)
                    if inside_round(x, y, 0.02, 0.02, 0.98, 0.98, 0.20):
                        t = y  # blue gradient
                        c = (int(37 + 20 * t), int(99 + 20 * t), int(235 - 30 * t), 255)
                        # white page with folded corner
                        if inside_round(x, y, 0.24, 0.16, 0.76, 0.86, 0.04) and not (x + (0.16 - y) * 0 > 0.62 and y < 0.30 and (x - 0.62) > (y - 0.16) * -1 + 0.0 and (x - 0.62) + (0.30 - y) > 0.14):
                            c = (255, 255, 255, 255)
                            if 0.62 < x <= 0.76 and 0.16 <= y < 0.30 and (x - 0.62) + (0.30 - y) <= 0.14:
                                c = (200, 215, 245, 255)
                            for ly in (0.42, 0.54, 0.66):  # text lines
                                if ly <= y <= ly + 0.05 and 0.32 <= x <= (0.68 if ly < 0.66 else 0.54):
                                    c = (37, 99, 235, 255)
                    for i in range(4): acc[i] += c[i] * (c[3] if i < 3 else 1)
            a = acc[3] / (ss * ss)
            if acc[3] == 0: row.append((0, 0, 0, 0))
            else: row.append(tuple(int(acc[i] / acc[3]) for i in range(3)) + (int(a),))
        px.append(row)
    return px

def png(size):
    px = render(size)
    raw = b''.join(b'\x00' + b''.join(bytes(p) for p in row) for row in px)
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')

sizes = [16, 24, 32, 48, 64, 128, 256]
imgs = [png(s) for s in sizes]
out = struct.pack('<HHH', 0, 1, len(sizes))
off = 6 + 16 * len(sizes)
for s, d in zip(sizes, imgs):
    out += struct.pack('<BBBBHHII', s % 256, s % 256, 0, 0, 1, 32, len(d), off); off += len(d)
out += b''.join(imgs)
path = os.path.join(os.path.dirname(__file__), '..', 'src', 'Berichtsheft.Desktop', 'app.ico')
open(path, 'wb').write(out)
print('wrote', path, len(out), 'bytes')
