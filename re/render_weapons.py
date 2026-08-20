"""Render the 4.95 client's equipped-weapon art (Sword/Spear/Fan/Shield .epf) as labeled
contact sheets, one representative frame per weapon id, so a weapon look byte can be
identified by eye.

Why this exists: the 0x33 appearance weapon byte is FAMILY-RANGED (RE'd 2026-08-19, classifier
0x432fe0 in NexusTK_local.exe, dispatch in the player-draw fn 0x432320):
    0x00..0x7F -> SWORD.EPF  art = byte          (Sword.tbl: 95 arts, 0..94)
    0x80..0xBF -> SPEAR.EPF  art = byte - 0x80   (Spear.tbl: 31 arts, 0..30)
    0xC0..0xDF -> BOW.EPF    art = byte - 0xC0   (Bow.tbl:   0 arts — 4.95 draws NOTHING)
    0xE0..0xFE -> FAN.EPF    art = byte - 0xE0   (Fan.tbl:    4 arts, 0..3)
    0xFF       -> bare hands
Items.csv ItmLook uses RTK's flat space (family = look/10000: 0 sword, 1 spear/2H, 2 bow,
3 fan) sized for a LATER client, so anything whose per-family art index exceeds the counts
above has no 4.95 art and needs re-pointing — these sheets are what you re-point against.

Assets come straight out of NexusTK.dat (same container render_effects.py's re/fx files were
extracted from). Per-family files:
  <Fam>.tbl  text: "NumWeapons N" then per art "ID n, Palette p, Starting f" — 20 epf frames
             per art (walk/swing poses x directions), Starting = first absolute frame index.
  <Fam>.pal  DLPalette blocks, exactly like Effect.pal (color data at +32).
  <Fam>.epf  same header/TOC layout as Effect.epf (see render_effects.py docstring).

Usage:
  python re/render_weapons.py sheet <sword|spear|fan|shield> [out.png] [scale]
  python re/render_weapons.py all [outdir] [scale]        # one sheet per family
"""
import os
import struct
import sys

from PIL import Image, ImageDraw

DAT = r"C:\Program Files (x86)\Nexon\NextAeon\NexusTK.dat"
FAMILIES = ("sword", "spear", "fan", "shield", "bow")


def dat_entries():
    d = open(DAT, "rb").read()
    n, = struct.unpack_from("<I", d, 0)
    ents, off = [], 4
    for _ in range(n):
        start, = struct.unpack_from("<I", d, off)
        name = d[off + 4:off + 17].split(b"\0")[0].decode("ascii", "replace")
        ents.append((start, name))
        off += 17
    return d, ents


def dat_read(name):
    d, ents = dat_entries()
    for i, (start, nm) in enumerate(ents[:-1]):
        if nm.lower() == name.lower():
            return d[start:ents[i + 1][0]]
    raise KeyError(name)


def load_palettes(blob):
    """DLPalette blocks with a VARIABLE header: color data starts at +32 + 2*count, where the
    u32 at +24 is a count of u16 'animated color' slots (0 for every Effect.pal block, which is
    why render_effects.py's flat +32 worked there — and why a flat offset shears these)."""
    offs, i = [], 0
    while (j := blob.find(b"DLPalette", i)) >= 0:
        offs.append(j)
        i = j + 1
    pals = []
    for o in offs:
        cnt, = struct.unpack_from("<I", blob, o + 24)
        base = o + 32 + 2 * cnt
        pals.append([tuple(blob[base + c * 4:base + c * 4 + 3]) for c in range(256)])
    return pals


def load_tbl(blob):
    """-> [(art_id, palette, starting_frame)]"""
    arts = []
    for line in blob.decode("latin1").splitlines():
        if line.startswith("ID "):
            parts = {}
            for p in line.strip().rstrip(",").split(", "):
                k, _, v = p.rpartition(" ")
                parts[k.strip()] = v
            arts.append((int(parts["ID"]), int(parts["Palette"]), int(parts["Starting"])))
    return arts


class Epf:
    def __init__(self, blob):
        self.d = blob
        self.count, self.w, self.h, _, toc = struct.unpack_from("<HHHHI", blob, 0)
        self.toc = 12 + toc

    def frame(self, i, pal):
        top, left, bot, right, pix, _ = struct.unpack_from("<hhhhII", self.d, self.toc + 16 * i)
        w, h = right - left, bot - top
        if w <= 0 or h <= 0:
            return None
        img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        px = img.load()
        base = 12 + pix
        for y in range(h):
            for x in range(w):
                v = self.d[base + y * w + x]
                if v:
                    px[x, y] = (*pal[v], 255)
        return img


# Frame layout within an art's 20-frame block (verified 2026-08-20 by rendering all 20 of sw8/sw24/sw28):
# 0..12 are the STATIC HOLD / walk poses -- the weapon as it hangs in the character's hand, which is what
# you actually see in game and what the Atlas hand/ reference gifs show. 13/15/17/19 are SWING ARCS: a
# smeared motion trail that looks nothing like the weapon. `best_frame` below picks the meatiest frame,
# which is ALWAYS one of the arcs -- that made the old sheets unusable for identifying a weapon, and is how
# several ItmLook values got mis-picked. Default to the hold pose; keep the arc picker for motion checks.
HOLD_POSE = 0

def hold_frame(epf, start, pal, pose=HOLD_POSE):
    """The static held pose -- what the player sees standing still. Use this to identify a weapon."""
    return epf.frame(start + pose, pal)


def best_frame(epf, start, pal):
    """Meatiest of the art's 20 frames. NOTE: this lands on a swing ARC, not the weapon -- see HOLD_POSE."""
    best, best_n = None, -1
    for i in range(start, min(start + 20, epf.count)):
        img = epf.frame(i, pal)
        if img is None:
            continue
        n = sum(1 for p in img.getdata() if p[3])
        if n > best_n:
            best, best_n = img, n
    return best


def sheet(fam, out=None, scale=2):
    tbl = load_tbl(dat_read(f"{fam.capitalize()}.tbl"))
    if not tbl:
        print(f"{fam}: 0 arts (nothing to render)")
        return
    pals = load_palettes(dat_read(f"{fam.capitalize()}.pal"))
    epf = Epf(dat_read(f"{fam.capitalize()}.epf"))
    cell_w, cell_h, label_h = 64, 80, 12
    cols = 10
    rows = (len(tbl) + cols - 1) // cols
    canvas = Image.new("RGB", (cols * cell_w * scale, rows * (cell_h + label_h) * scale), (24, 24, 32))
    draw = ImageDraw.Draw(canvas)
    for aid, palix, start in tbl:
        pal = pals[palix] if palix < len(pals) else pals[0]
        img = hold_frame(epf, start, pal)   # the weapon as held; see HOLD_POSE
        cx = (aid % cols) * cell_w * scale
        cy = (aid // cols) * (cell_h + label_h) * scale
        if img is not None:
            img = img.resize((img.width * scale, img.height * scale), Image.NEAREST)
            canvas.paste(img, (cx + max(0, (cell_w * scale - img.width) // 2),
                               cy + max(0, (cell_h * scale - img.height) // 2)), img)
        draw.text((cx + 2, cy + cell_h * scale), f"{fam[:2]}{aid}", fill=(255, 255, 120))
    out = out or f"re/weapons_{fam}.png"
    canvas.save(out)
    print(f"{fam}: {len(tbl)} arts -> {out}")


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "sheet":
        sheet(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None,
              int(sys.argv[4]) if len(sys.argv) > 4 else 2)
    elif len(sys.argv) >= 2 and sys.argv[1] == "all":
        outdir = sys.argv[2] if len(sys.argv) > 2 else "re"
        scale = int(sys.argv[3]) if len(sys.argv) > 3 else 2
        for fam in FAMILIES:
            sheet(fam, os.path.join(outdir, f"weapons_{fam}.png"), scale)
    else:
        print(__doc__)
