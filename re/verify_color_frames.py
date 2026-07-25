"""Verify the color=frame-offset theory: decode Monster.epf frames at Starting+color*Walk
directly (no palette swapping) for a look id, render with its OWN Monster.tbl Palette block,
and save a strip to compare against a live !crecol screenshot.
Usage: python verify_color_frames.py <lookId> [loColor] [hiColor]
"""
import sys, struct
from PIL import Image, ImageDraw

def load_pal_blocks(path="monster.pal"):
    d = open(path, "rb").read()
    tag = b"DLPalette"
    offs = []
    i = 0
    while True:
        j = d.find(tag, i)
        if j < 0:
            break
        offs.append(j)
        i = j + 1
    blocks = []
    for k, off in enumerate(offs):
        end = offs[k + 1] if k + 1 < len(offs) else len(d)
        blk = d[off:end]
        colors = []
        for ci in range(256):
            o = 38 + ci * 4
            colors.append(tuple(blk[o:o + 3]) if o + 3 <= len(blk) else (0, 0, 0))
        blocks.append(colors)
    return blocks

def epf_frame(epf, frame_idx):
    frameCount, w, h, unk = struct.unpack_from("<HHHH", epf, 0)
    (tocOffset,) = struct.unpack_from("<I", epf, 8)
    if frame_idx < 0 or frame_idx >= frameCount:
        return None
    toc_o = tocOffset + frame_idx * 16
    top, left, pixOff, stencilOff, bottom, right = struct.unpack_from("<hhIIhh", epf, toc_o)
    pixbytes = stencilOff - pixOff
    if pixbytes <= 4:
        return None
    bw = abs(right - left) or 1
    bh = abs(bottom - top) or 1
    # derive true dims: which box dim divides pixbytes evenly
    fw, fh = bw, bh
    if pixbytes % bw == 0:
        fw, fh = bw, pixbytes // bw
    elif pixbytes % bh == 0:
        fw, fh = pixbytes // bh, bh
    raw = epf[12 + pixOff: 12 + pixOff + fw * fh]
    return fw, fh, raw

def main():
    look = int(sys.argv[1])
    lo = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    hi = int(sys.argv[3]) if len(sys.argv) > 3 else 11
    tbl = {}
    for line in open("monster.tbl", encoding="latin1"):
        if line.startswith("ID "):
            parts = line.strip().rstrip(",").split(", ")
            fields = {}
            for p in parts:
                k, v = p.split(" ", 1) if " " in p else (p, "")
                fields[k] = v
            # first token is "ID n"
            idnum = int(parts[0].split(" ")[1])
            row = {}
            for p in parts[1:]:
                k, v = p.rsplit(" ", 1)
                row[k.strip()] = int(v)
            tbl[idnum] = row
    row = tbl[look]
    starting, walk, pal_idx = row["Starting"], row["Walk"], row["Palette"]
    print(f"look {look}: Starting={starting} Walk={walk} Palette={pal_idx}")

    epf = open("monster.epf", "rb").read()
    blocks = load_pal_blocks("monster.pal")
    pal = blocks[pal_idx]

    scale = 4
    cells = []
    for c in range(lo, hi + 1):
        frame_idx = starting + c * walk
        res = epf_frame(epf, frame_idx)
        if res is None:
            cells.append((c, frame_idx, None))
            continue
        fw, fh, raw = res
        im = Image.new("RGBA", (fw, fh), (0, 0, 0, 0))
        px = im.load()
        for i in range(min(len(raw), fw * fh)):
            k = raw[i]
            if k:
                r, g, b = pal[k]
                px[i % fw, i // fw] = (r, g, b, 255)
        cells.append((c, frame_idx, im))

    cw = max((im.width for _, _, im in cells if im), default=20) * scale + 8
    ch = max((im.height for _, _, im in cells if im), default=20) * scale + 22
    cols = 6
    rows = (len(cells) + cols - 1) // cols
    sheet = Image.new("RGB", (cw * cols, ch * rows), (30, 30, 34))
    draw = ImageDraw.Draw(sheet)
    for i, (c, frame_idx, im) in enumerate(cells):
        col, row_i = i % cols, i // cols
        x0, y0 = col * cw + 4, row_i * ch + 4
        if im:
            big = im.resize((im.width * scale, im.height * scale), Image.NEAREST)
            bg = Image.new("RGB", big.size, (20, 20, 24))
            bg.paste(big, (0, 0), big)
            sheet.paste(bg, (x0, y0))
            draw.text((x0, y0 + big.height + 1), f"color={c} f={frame_idx}", fill=(255, 255, 255))
        else:
            draw.text((x0, y0 + 20), f"color={c} f={frame_idx} EMPTY", fill=(255, 80, 80))
    out = f"verify_{look}.png"
    sheet.save(out)
    print("wrote", out)

if __name__ == "__main__":
    main()
