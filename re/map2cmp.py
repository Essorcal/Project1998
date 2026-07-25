#!/usr/bin/env python
"""Convert a headerless 4.x NextAeon TK<id>.map (LE, 4 bytes/cell [ground16][object16],
top-2-bits of ground = passable flag) into a 5.33 TK######.cmp:
  "CMAP" + u32LE (height<<16|width) + zlib( W*H*6 bytes: per cell 3 u16LE [ground+1][passable][object+1] ).
Ground/object indices land in the verbatim-shared tile range, so 5.33 renders them with its own tiles.
Usage: python map2cmp.py <in.map> <W> <H> <out.cmp>"""
import sys, struct, zlib

src, W, H, dst = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]
d = open(src, "rb").read()
assert len(d) == W*H*4, f"size {len(d)} != {W}*{H}*4={W*H*4}"

payload = bytearray()
for i in range(W*H):
    g16, o16 = struct.unpack_from("<HH", d, i*4)
    ground = g16 & 0x3FFF          # strip passable flag -> 14-bit tile index (already the +1 stored value)
    passable = (g16 >> 14) & 0x3   # 0 or 3 in 4.x; carried through (walkability, not visual)
    obj = o16 & 0x3FFF             # object tile (0 = none)
    payload += struct.pack("<HHH", ground, passable, obj)

comp = zlib.compress(bytes(payload), 9)
out = b"CMAP" + struct.pack("<I", (H << 16) | W) + comp
open(dst, "wb").write(out)
print(f"wrote {dst}: {len(out)} bytes  (payload {len(payload)} -> zlib {len(comp)}; dims {W}x{H})")
# sanity: first few cells
for i in range(3):
    g16, o16 = struct.unpack_from("<HH", d, i*4)
    print(f"  cell{i}: map g16={g16}(0x{g16:04x}) o16={o16} -> ground={g16&0x3FFF} pass={(g16>>14)&3} obj={o16&0x3FFF}")
