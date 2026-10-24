"""Render frames from the client's Item.epf into a labeled contact sheet, so item icons can be
identified by eye and mapped RTK-item -> client-frame. Item.epf has exactly 1310 frames == Item.tbl's
1310 items, so FRAME INDEX == client item id; the 0x0F/0x37 icon field is that id (NOT the RTK ItmIcon).
That is the CANONICAL frame index (table at 12 + u32@8, docs/4.x/Protocol.md §11c). THIS SCRIPT numbers
frames one higher: it reads the table 12 bytes early (see epf_frame), so its frame N+1 is item id N.

EPF frame TOC entry (16B), canonically: top(i16) left(i16) bottom(i16) right(i16) pixOff(u32)
stencilOff(u32); epf_frame unpacks the early-read bytes under other names (see there). Raw 8bpp indices at
12+pixOff. Both dimensions come from the box, and epf_frame refuses a frame unless w*h == stencilOff-pixOff.
Palette per item from Item.tbl -> Item.pal (15 "DLPalette" blocks).

Usage: python render_items.py <lo> <hi> [outPng] [scale] [cols]
"""
import sys, struct
from PIL import Image, ImageDraw

def load_pal_blocks(path="Item.pal"):
    d = open(path, "rb").read()
    offs, i = [], 0
    while True:
        j = d.find(b"DLPalette", i)
        if j < 0: break
        offs.append(j); i = j + 1
    blocks = []
    for k, off in enumerate(offs):
        end = offs[k + 1] if k + 1 < len(offs) else len(d)
        blk = d[off:end]
        blocks.append([tuple(blk[38 + c * 4:38 + c * 4 + 3]) if 38 + c * 4 + 3 <= len(blk) else (0, 0, 0)
                       for c in range(256)])
    return blocks

def load_tbl_palettes(path="Item.tbl"):
    pal = {}
    for line in open(path, encoding="latin1"):
        if line.startswith("ID "):
            parts = line.strip().rstrip(",").split(", ")
            idn = int(parts[0].split(" ")[1])
            for p in parts[1:]:
                k, v = p.rsplit(" ", 1)
                if k.strip() == "Palette": pal[idn] = int(v)
    return pal

# CORRECTED 2026-08-07; RE-READ 2026-10-24 against the 4.95 install. This script reads the table at u32@8,
# 12 bytes before the canonical table at 12 + u32@8 (docs/4.x/Protocol.md §11c), whose entries are
#   top(i16) left(i16) bottom(i16) right(i16) pixOff(u32) stencilOff(u32).
# Read that early, the 16 bytes at toc+i*16 are canonical entry i-1's bottom, right, pixOff, stencilOff and
# then entry i's top, left -- unpacked below as top, left, pix, sten, bottom, right. So the box taken across
# two entries, (left[i] - right[i-1]) x (top[i] - bottom[i-1]), is canonical frame i-1's own box, with its
# own pixOff and stencilOff: it reproduces stencilOff-pixOff for 1309/1309 Item.epf frames, where the old
# same-entry box matched almost none of them and the n//w fallback sheared every sprite whose box was wrong.
# This script's frame i IS canonical frame i-1 (canonical frame 0 is a real 24x24 frame it cannot reach),
# hence its frame N+1 == client item id N (id 265, the green helm, is canonical frame 265 and frame 266 here).
def epf_frame(epf, fi):
    fc, = struct.unpack_from("<H", epf, 0)
    toc, = struct.unpack_from("<I", epf, 8)
    if fi < 1 or fi >= fc: return None
    top, left, pix, sten, _, _ = struct.unpack_from("<hhIIhh", epf, toc + fi * 16)
    _, _, _, _, pbot, pright = struct.unpack_from("<hhIIhh", epf, toc + (fi - 1) * 16)
    w = left - pright
    h = top - pbot
    if w <= 0 or h <= 0 or w * h != sten - pix: return None
    return w, h, epf[12 + pix: 12 + pix + w * h]

def main():
    lo, hi = int(sys.argv[1]), int(sys.argv[2])
    out = sys.argv[3] if len(sys.argv) > 3 else f"items_{lo}_{hi}.png"
    scale = int(sys.argv[4]) if len(sys.argv) > 4 else 2
    cols = int(sys.argv[5]) if len(sys.argv) > 5 else 16
    epf = open("Item.epf", "rb").read()
    blocks = load_pal_blocks(); tbl = load_tbl_palettes()

    cell = 40 * scale  # fixed cell so the grid is regular; sprites centered
    lab = 12
    rows = (hi - lo + cols - 1) // cols
    sheet = Image.new("RGB", (cell * cols, (cell + lab) * rows), (28, 28, 32))
    draw = ImageDraw.Draw(sheet)
    for n, fi in enumerate(range(lo, hi)):
        col, row = n % cols, n // cols
        x0, y0 = col * cell, row * (cell + lab)
        res = epf_frame(epf, fi)
        if res:
            w, h, raw = res
            pal = blocks[tbl.get(fi, 0) % len(blocks)]
            im = Image.new("RGBA", (w, h), (0, 0, 0, 0)); px = im.load()
            for i in range(min(len(raw), w * h)):
                k = raw[i]
                if k: px[i % w, i // w] = (*pal[k], 255)
            im = im.resize((min(w * scale, cell), min(h * scale, cell)), Image.NEAREST)
            bg = Image.new("RGB", im.size, (18, 18, 22)); bg.paste(im, (0, 0), im)
            sheet.paste(bg, (x0 + (cell - im.width) // 2, y0 + (cell - im.height) // 2))
        draw.text((x0 + 1, y0 + cell), str(fi), fill=(170, 170, 180))
    sheet.save(out); print("wrote", out, sheet.size)

if __name__ == "__main__":
    main()
