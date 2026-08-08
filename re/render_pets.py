"""Render the Call of the Wild / alignment pet-summon creatures side by side, so the four alignment
ladders can be compared visually.

Monster.tbl:  "NumMonsters N" then "ID n, Palette p, Starting s, Walk w, Attack a, Delay d, Shadow z"
              -- `Starting` is the first Monster.epf frame index for that look.
Monster.epf:  same container as Effect.epf -- u16 count, u16 w, u16 h, u16 pad, u32 tocOff, TOC at
              12+tocOff, 16B entries of top,left,bottom,right (i16 x4) then pixOff,stencilOff (u32 x2).
Monster.pal:  colours at block+38 (NOT the +32 that Effect.pal uses -- these two files have different
              header sizes, verified separately against ground truth in monster-matcher/).

The per-mob palette is mobs.csv's MobLookColor, which is a palette-block index.

Usage: python render_pets.py [out.png]
"""
import csv
import os
import struct
import sys

from PIL import Image, ImageDraw

RE = os.path.dirname(os.path.abspath(__file__))
GD = os.path.join(os.path.dirname(RE), "data", "game-data")


def load_pal(path=os.path.join(RE, "monster.pal")):
    d = open(path, "rb").read()
    offs, i = [], 0
    while True:
        j = d.find(b"DLPalette", i)
        if j < 0:
            break
        offs.append(j)
        i = j + 1
    out = []
    for k, off in enumerate(offs):
        end = offs[k + 1] if k + 1 < len(offs) else len(d)
        blk = d[off:end]
        out.append([tuple(blk[38 + c * 4:38 + c * 4 + 3]) if 38 + c * 4 + 3 <= len(blk) else (0, 0, 0)
                    for c in range(256)])
    return out


def load_tbl(path=os.path.join(RE, "monster.tbl")):
    looks = {}
    for line in open(path, encoding="latin1"):
        if not line.startswith("ID "):
            continue
        parts = dict()
        for p in line.strip().rstrip(",").split(", "):
            k, _, v = p.rpartition(" ")
            parts[(k or p).strip()] = v
        looks[int(parts["ID"])] = {"pal": int(parts["Palette"]), "start": int(parts["Starting"])}
    return looks


class Epf:
    def __init__(self, path=os.path.join(RE, "monster.epf")):
        self.d = open(path, "rb").read()
        self.count, self.w, self.h = struct.unpack_from("<HHH", self.d, 0)
        self.toc = 12 + struct.unpack_from("<I", self.d, 8)[0]

    def frame(self, fi):
        if fi < 0 or fi >= self.count:
            return None
        top, left, bot, right, pix, sten = struct.unpack_from("<hhhhII", self.d, self.toc + fi * 16)
        w, h = right - left, bot - top
        if w <= 0 or h <= 0 or sten - pix != w * h:
            return None
        return w, h, self.d[12 + pix: 12 + pix + w * h]


def sprite(epf, looks, pals, look, color, scale=2, use_color=False):
    """Render a look's first frame.

    NOTE mobs.csv's MobLookColor is NOT an index into monster.pal: it ranges 0-53 while the file has
    only 20 DLPalette blocks, and 250 of 716 mobs exceed the range. It is the client's recolour id,
    resolved against some other palette source we have not identified. So the DEFAULT here is the
    sprite's own palette from monster.tbl, which is authoritative for "what this creature looks
    like"; pass use_color=True to try the recolour index anyway.
    """
    info = looks.get(look)
    if not info:
        return None
    f = epf.frame(info["start"])
    if not f:
        return None
    w, h, raw = f
    pal = pals[color] if (use_color and 0 <= color < len(pals)) else pals[info["pal"] % len(pals)]
    im = Image.new("RGB", (w, h), (0, 0, 0))
    px = im.load()
    for y in range(h):
        row = raw[y * w:(y + 1) * w]
        for x, c in enumerate(row):
            if c:
                px[x, y] = pal[c]
    return im.resize((w * scale, h * scale), Image.NEAREST)


TIERS = [("Companion  lvl 68", 562, 578, 579, 580),
         ("Assistant  lvl 72", 563, 581, 582, 583),
         ("Protector  lvl 81", 564, 584, 585, 586),
         ("Fighter    lvl 90", 565, 587, 588, 589),
         ("Warrior    lvl 99", 566, 590, 591, 592),
         ("Champion   Mark 1", 567, 593, 594, 595),
         ("Avatar     Mark 2", 568, 596, 597, 598)]
COLS = ["Unaligned (Call of the Wild)", "Kwi-Sin", "Ming-Ken", "Ohaeng"]


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(RE, "fx", "pets.png")
    mobs = {int(r["MobId"]): r for r in csv.DictReader(open(os.path.join(GD, "mobs.csv"),
                                                           encoding="utf-8-sig"))
            if r["MobId"].isdigit()}
    epf, looks, pals = Epf(), load_tbl(), load_pal()
    cell, lbl, hdr = 150, 150, 26
    sheet = Image.new("RGB", (lbl + 4 * cell, hdr + len(TIERS) * (cell + 18)), (20, 20, 24))
    dr = ImageDraw.Draw(sheet)
    for i, c in enumerate(COLS):
        dr.text((lbl + i * cell + 4, 8), c, fill=(150, 230, 255))
    y = hdr
    for tier, *ids in TIERS:
        dr.text((6, y + cell // 2), tier, fill=(255, 210, 100))
        for i, mid in enumerate(ids):
            r = mobs.get(mid)
            if not r:
                dr.text((lbl + i * cell + 4, y + 20), f"{mid} MISSING", fill=(255, 120, 120))
                continue
            im = sprite(epf, looks, pals, int(r["MobLook"]), int(r["MobLookColor"]))
            name = r["Description"] or r["Identifier"]
            dr.text((lbl + i * cell + 4, y + 2), f"{name[:22]}", fill=(210, 210, 215))
            dr.text((lbl + i * cell + 4, y + 13), f"look {r['MobLook']}", fill=(130, 130, 140))
            if im:
                im.thumbnail((cell - 8, cell - 26), Image.LANCZOS)
                sheet.paste(im, (lbl + i * cell + 4, y + 24))
        y += cell + 18
    os.makedirs(os.path.dirname(out), exist_ok=True)
    sheet.save(out)
    print("wrote", out, sheet.size)


if __name__ == "__main__":
    main()
