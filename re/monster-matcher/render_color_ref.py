"""Render a look-id's sprite through all 20 real Monster.pal blocks (decoded at offset 38,
stride 4, RGB=first 3 bytes — verified against mouse.gif ground truth) as a labeled strip,
so it can be pixel-compared against a live !crecol screenshot from the real client.
Usage: python render_color_ref.py <lookId> [outPng]
"""
import sys, json, base64, struct
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
            if o + 3 <= len(blk):
                colors.append(tuple(blk[o:o + 3]))
            else:
                colors.append((0, 0, 0))
        blocks.append(colors)
    return blocks

def main():
    look = int(sys.argv[1])
    out = sys.argv[2] if len(sys.argv) > 2 else f"colorref_{look}.png"
    mons = json.load(open("monsters.json"))
    m = [x for x in mons if x["id"] == look][0]
    w, h = m["fw"], m["fh"]
    idx = base64.b64decode(m["idx"])
    blocks = load_pal_blocks("../monster.pal")
    scale = 4
    pad = 6
    label_h = 14
    cell_w, cell_h = w * scale + pad, h * scale + pad + label_h
    cols = 5
    rows = 4
    sheet = Image.new("RGB", (cell_w * cols, cell_h * rows), (30, 30, 34))
    draw = ImageDraw.Draw(sheet)
    for c in range(20):
        pal = blocks[c]
        im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        px = im.load()
        for i in range(w * h):
            k = idx[i]
            if k:
                r, g, b = pal[k]
                px[i % w, i // w] = (r, g, b, 255)
        im = im.resize((w * scale, h * scale), Image.NEAREST)
        col, row = c % cols, c // cols
        x0 = col * cell_w + pad // 2
        y0 = row * cell_h + pad // 2
        bg = Image.new("RGB", im.size, (20, 20, 24))
        bg.paste(im, (0, 0), im)
        sheet.paste(bg, (x0, y0))
        draw.text((x0, y0 + h * scale + 1), f"color={c}", fill=(255, 255, 255))
    sheet.save(out)
    print("wrote", out, sheet.size)

if __name__ == "__main__":
    main()
