"""Build a profile-picture EPF the 4.95 client will actually accept.

The profile picture is NOT something the server hands out — it is a file the PLAYER puts next to the
client, and the client attaches it to its 0x4F "profile saved" packet. If the file is missing or fails any
of the client's checks, the 0x4F still goes out but with picSize = 0, which looks exactly like a server
bug and isn't one. (That is what the capture showing
`CreateFileW(".../users/Zaleroo.epf") -> INVALID_HANDLE` followed by `4f 00 00 04 "asdf" 00` was.)

Install the output as:

    <client dir>/users/<CharacterName>.epf        (the client also retries <CharacterName>.face)

...then save the profile in-game, or have the server send 0x49 (`@askpic`) to make the client re-read it.

The client's validation, reversed from NexusTK_local.exe 0x44edc0 (each check is a hard bail):
    * file size EXACTLY 0xb1c = 2844 bytes
    * dword at +8 == 0xaf0 = 2800  -- the EPF table offset, relative to the 12-byte header: the canonical
      table sits at 12 + 2800 = 2812, after 2800 bytes of frame data, and holds two 16-byte entries
    * toc[1].top  - toc[0].bottom == 0x38 (56)   -> frame height
    * toc[1].left - toc[0].right  == 0x30 (48)   -> frame width
      (toc taken at +2800, 12 bytes before the canonical table: the i16s at 2816 - 2812 and 2818 - 2814,
      which are canonical frame 0's own bottom - top and right - left)
so the picture is one 48x56 8bpp frame. Every one of those numbers is fixed, which is why this script
takes no size options: any other geometry is rejected by the client, not merely drawn wrong.

EPF layout (same as Item.epf; see render_items.py for the early reading):
    +0  u16 frameCount
    +2  u16 width      +4 u16 height   +6 u16 unknown
    +8  u32 tocOffset  (relative to +12: the table is at 12 + tocOffset)
    +12 pixel data (8bpp palette indices)
    canonical table entries, 16B each: top(i16) left(i16) bottom(i16) right(i16) pixOff(u32) stencilOff(u32)
    (docs/4.x/Protocol.md §11c).
    THIS SCRIPT writes its two entries at +2800, the client check's early base, so here and in build() the
    entries carry that early reading's names -- top(i16) left(i16) pixOff(u32) stencilOff(u32) bottom(i16)
    right(i16), the box split across two entries, "frame 0" an origin marker and "frame 1" the picture.
    Read canonically at 2812 the same bytes are frame 0: box top 0, left 0, bottom 56, right 48 (48x56),
    pixOff 0, stencilOff 2688 (= 48*56); canonical frame 1 is all zero.

Usage:
    python make_profile_epf.py <image> <out.epf> [palette.pal] [paletteIndex]

The palette defaults to Item.pal block 0 purely so there is a working default; which palette the client
actually draws the profile picture with is NOT yet pinned, so if the colours come out wrong, sweep the
block index rather than assuming the geometry is broken -- the client accepted the file either way.
"""
import sys, struct
from PIL import Image

W, H = 48, 56
TOC = 0xaf0            # 2800 -- must match the dword the client reads at +8
TOTAL = 0xb1c          # 2844 -- must match the file size exactly


def load_pal_block(path, index):
    d = open(path, "rb").read()
    offs, i = [], 0
    while True:
        j = d.find(b"DLPalette", i)
        if j < 0:
            break
        offs.append(j)
        i = j + 1
    if not offs:
        raise SystemExit(f"{path}: no DLPalette blocks")
    k = index % len(offs)
    end = offs[k + 1] if k + 1 < len(offs) else len(d)
    blk = d[offs[k]:end]
    return [tuple(blk[38 + c * 4:38 + c * 4 + 3]) if 38 + c * 4 + 3 <= len(blk) else (0, 0, 0)
            for c in range(256)]


def quantize(img, pal):
    """Nearest-colour match into the client's fixed 256-entry palette. Index 0 is the transparent key in
    every other EPF the client draws, so it is left for fully transparent pixels only."""
    img = img.convert("RGBA").resize((W, H), Image.LANCZOS)
    px = img.load()
    out = bytearray(W * H)
    cache = {}
    for y in range(H):
        for x in range(W):
            r, g, b, a = px[x, y]
            if a < 128:
                out[y * W + x] = 0
                continue
            key = (r, g, b)
            idx = cache.get(key)
            if idx is None:
                best, bestd = 1, 1 << 30
                for c in range(1, 256):
                    pr, pg, pb = pal[c]
                    dd = (pr - r) ** 2 + (pg - g) ** 2 + (pb - b) ** 2
                    if dd < bestd:
                        best, bestd = c, dd
                        if dd == 0:
                            break
                idx = cache[key] = best
            out[y * W + x] = idx
    return bytes(out)


def build(pixels):
    buf = bytearray(TOTAL)
    struct.pack_into("<H", buf, 0, 2)        # frameCount: the origin marker + the real frame
    struct.pack_into("<H", buf, 2, W)
    struct.pack_into("<H", buf, 4, H)
    struct.pack_into("<H", buf, 6, 0)
    struct.pack_into("<I", buf, 8, TOC)
    buf[12:12 + len(pixels)] = pixels

    # frame 0 -- origin only. Its bottom/right are the top-left corner of frame 1's box, so they must be
    # zero for the client's two subtractions to come out as the raw 56/48 it demands.
    struct.pack_into("<hhIIhh", buf, TOC + 0, 0, 0, 0, 0, 0, 0)
    # frame 1 -- the picture. top/left carry the far corner (0 + H, 0 + W); pixels start at 0 and the
    # stencil offset marks their end, which is how the decoder recovers W*H.
    struct.pack_into("<hhIIhh", buf, TOC + 16, H, W, 0, W * H, 0, 0)
    return bytes(buf)


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    src, dst = sys.argv[1], sys.argv[2]
    palpath = sys.argv[3] if len(sys.argv) > 3 else "Item.pal"
    palidx = int(sys.argv[4]) if len(sys.argv) > 4 else 0

    pal = load_pal_block(palpath, palidx)
    data = build(quantize(Image.open(src), pal))

    assert len(data) == TOTAL, len(data)
    assert struct.unpack_from("<I", data, 8)[0] == TOC
    t1 = struct.unpack_from("<hhIIhh", data, TOC + 16)
    t0 = struct.unpack_from("<hhIIhh", data, TOC + 0)
    assert t1[0] - t0[4] == 0x38 and t1[1] - t0[5] == 0x30, (t0, t1)

    open(dst, "wb").write(data)
    print(f"wrote {dst}: {len(data)} bytes, {W}x{H}, palette {palpath}#{palidx}")
    print("install as <client dir>/users/<CharacterName>.epf, then save your profile (or @askpic)")


if __name__ == "__main__":
    main()
