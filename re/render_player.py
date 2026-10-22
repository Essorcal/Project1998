"""Compose a 4.95 player sprite from the client's own tables, the way the player draw 0x432320 does,
so the rules in docs/4.x/Protocol.md section 8.2 can be checked by eye.

What it implements (each rule is the doc's, with the address that decides it there):
  * the per-pose layer order from DrwOrder.tbl (Inter.dat; 100 five-byte records, 0x431a44 loads it,
    0x432f90 looks it up, out of range -> "bh00");
  * the pose index p and each layer's frame from the resolver 0x433040: body = Body.Starting + p,
    head = Head.Starting + p, weapon/shield = <tbl>.Starting + their own offsets;
  * the archive swaps in 0x432320: GHOST.EPF for form 1, EMOTION.EPF for the head during actions 11..24;
  * the body layer's ramp shift (appearance byte [4]: index += colour*8 when index >= 0x30, 0x428e1a).

It reads NexusTK.dat and Inter.dat from the client install (re/_paths.py; P1998_CLIENT overrides) and
writes nothing but the PNGs you ask for.

Usage:
  python re/render_player.py sheets <outdir> [scale]     # the evidence set: facings, walks, swings, mount...
  python re/render_player.py one <out.png> sex form face armor colour weapon shield dir phase [action frame]
"""
import os
import struct
import sys

from PIL import Image, ImageDraw

from _paths import CLIENT
from render_weapons import load_palettes, load_tbl

DAT = CLIENT / "NexusTK.dat"
INTER = CLIENT / "Inter.dat"

# 0x4cb508: walk phase ([entity+0x18e], 0..3) -> sub-frame. 0x4cb510 / 0x4cb524: weapon class -> pose base
# for standing/walking and for the attack swing. Classes from 0x432fe0: 0 none(0xFF), 1 sword, 2 spear,
# 3 bow, 4 fan.
WALK_SUB = (0, 1, 0, 2)
STAND_BASE = (0, 12, 32, 52, 12)
SWING_BASE = (24, 24, 44, 64, 24)


def weapon_class(b):
    if b <= 0x7F:
        return 1
    if b <= 0xBF:
        return 2
    if b <= 0xDF:
        return 3
    if b <= 0xFE:
        return 4
    return 0


def pak(path):
    d = open(path, "rb").read()
    n, = struct.unpack_from("<I", d, 0)
    ents = [(struct.unpack_from("<I", d, 4 + 17 * i)[0],
             d[8 + 17 * i:21 + 17 * i].split(b"\0")[0].decode("latin1")) for i in range(n)]
    return {nm.lower(): d[s:ents[i + 1][0]] for i, (s, nm) in enumerate(ents[:-1])}


class Epf:
    """8bpp frames; each frame's box (top, left, bottom, right) is signed, relative to one shared origin."""

    def __init__(self, blob):
        self.d = blob
        self.count = struct.unpack_from("<H", blob, 0)[0]
        self.toc = 12 + struct.unpack_from("<I", blob, 8)[0]

    def frame(self, i):
        if i < 0 or i >= self.count:
            return None
        top, left, bot, right, pix, _ = struct.unpack_from("<hhhhII", self.d, self.toc + 16 * i)
        w, h = right - left, bot - top
        if w <= 0 or h <= 0:
            return None
        return left, top, w, h, self.d[12 + pix:12 + pix + w * h]


class Assets:
    def __init__(self):
        nx, it = pak(DAT), pak(INTER)
        drw = it["drworder.tbl"]
        # 0x431a62..0x431ad0: count = size / 5; each record is 4 layer letters then one separator byte.
        self.order = [drw[5 * k:5 * k + 4].decode("latin1") for k in range(len(drw) // 5)]
        self.tbl, self.pal, self.epf = {}, {}, {}
        for fam in ("body", "head", "sword", "spear", "fan", "shield"):
            self.tbl[fam] = load_tbl(nx[f"{fam}.tbl"])
            self.pal[fam] = load_palettes(nx[f"{fam}.pal"])
            self.epf[fam] = Epf(nx[f"{fam}.epf"])
        self.epf["ghost"] = Epf(nx["ghost.epf"])
        self.pal["ghost"] = load_palettes(nx["ghost.pal"])
        self.epf["emotion"] = Epf(nx["emotion.epf"])

    def rec(self, fam, art):
        """(palette, Starting); a missing art falls back to the table's default record (0x431d80/0x431f60/
        0x432140 initialise it): Head's Starting is 0xFFFFFF01 (so no frame exists), the others' is 0."""
        t = self.tbl[fam]
        if 0 <= art < len(t):
            return t[art][1], t[art][2]
        return -1, (-255 if fam == "head" else 0)

    def drworder(self, p):
        return self.order[p] if 0 <= p < len(self.order) else "bh00"


def resolve(a, look, d, phase=0, action=0, f=0):
    """-> (order string, {letter: (archive, frame, family-for-palette)}). Mirrors 0x433040 + 0x432320."""
    sex, form, face, armor, colour, weapon, shield = look
    if form == 2 or form >= 6:
        return "", {}
    if form == 1:                                             # ghost: one layer, GHOST.EPF frame = face
        return "h000", {"h": ("ghost", face, "ghost")}
    hp, hs = a.rec("head", face)
    bp, bs = a.rec("body", armor)
    layers = {}

    def hb(p, head_archive="head", head_frame=None):
        layers["h"] = (head_archive, (hs + p) & 0xFFFF if head_frame is None else head_frame, "head")
        layers["b"] = ("body", (bs + p) & 0xFFFF, "body")

    if action == 0 and form == 3:                             # mounted: 80 + 3*dir + sub, no weapon/shield
        p = 80 + 3 * d + WALK_SUB[phase]
        hb(p)
        return a.drworder(p), layers
    if action == 0:                                           # standing / walking
        cls = weapon_class(weapon)
        p = STAND_BASE[cls] + 3 * d + WALK_SUB[phase]
        hb(p)
        off = 3 * d + WALK_SUB[phase]
    elif action == 1:                                         # attack: two frames per facing
        cls = weapon_class(weapon)
        p = SWING_BASE[cls] + 2 * d + f
        hb(p)
        off = 12 + 2 * d + f
    else:
        emo = 20 * face
        table = {2: (24 + 2 * d + f, None), 3: (64 + 2 * d + f, None), 4: (76 + d, None), 5: (76 + d, None),
                 6: (72 + d, None), 7: (92, None), 8: (92, None), 9: (96 + d, None), 10: (93, None),
                 20: (95, emo + 9), 22: (100 + f, emo + 11 + f), 24: (96 + d, emo + 16 + d)}
        if action in table:
            p, ef = table[action]
            if ef is None:
                hb(p)
            else:
                hb(p, "emotion", ef)
            return a.drworder(p), layers
        if 11 <= action <= 23:                                # 11..19, 21, 23: standing body, emotion head
            ef = emo + (15 if action == 23 else action - 11)
            hb(3 * d, "emotion", ef)
            return "bhws", layers
        return "", {}
    if shield != 0xFF:
        sp, ss = a.rec("shield", shield)
        layers["s"] = ("shield", ss + off, "shield")
    cls = weapon_class(weapon)
    fam = {1: "sword", 2: "spear", 4: "fan"}.get(cls)
    if fam:
        art = weapon - {1: 0, 2: 0x80, 4: 0xE0}[cls]
        wp, ws = a.rec(fam, art)
        layers["w"] = (fam, ws + off, fam)
    return a.drworder(p), layers


def palette_for(a, fam, look):
    sex, form, face, armor, colour, weapon, shield = look
    if fam == "ghost":
        return a.pal["ghost"][0]
    art = {"head": face, "body": armor, "shield": shield}.get(fam)
    if art is None:
        art = weapon - {"sword": 0, "spear": 0x80, "fan": 0xE0}[fam]
    p, _ = a.rec(fam, art)
    pals = a.pal[fam]
    return pals[p] if 0 <= p < len(pals) else pals[0]


def compose(a, look, d, phase=0, action=0, f=0, size=(128, 112), origin=(64, 88), order_override=None):
    order, layers = resolve(a, look, d, phase, action, f)
    if order_override is not None:
        order = order_override
    img = Image.new("RGBA", size, (0, 0, 0, 0))
    px = img.load()
    colour = look[4]
    for ch in order:
        if ch not in layers:
            continue
        arch, fr, fam = layers[ch]
        got = a.epf[arch].frame(fr)
        if got is None:
            continue
        left, top, w, h, raw = got
        pal = palette_for(a, fam, look)
        tint = ch == "b" and colour != 0
        for y in range(h):
            ty = origin[1] + top + y
            if not 0 <= ty < size[1]:
                continue
            row = raw[y * w:(y + 1) * w]
            for x, v in enumerate(row):
                if not v:
                    continue
                tx = origin[0] + left + x
                if 0 <= tx < size[0]:
                    if tint and v >= 0x30:
                        v = (v + colour * 8) & 0xFF
                    px[tx, ty] = (*pal[v], 255)
    return img, order


def grid(cells, cols, scale, title, out):
    """cells: [(label, image)]; writes a labelled sheet."""
    cw, ch = cells[0][1].size
    lab = 22
    rows = (len(cells) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * cw * scale, 18 + rows * (ch * scale + lab)), (40, 44, 52))
    dr = ImageDraw.Draw(sheet)
    dr.text((4, 3), title, fill=(255, 255, 255))
    for i, (label, im) in enumerate(cells):
        x, y = (i % cols) * cw * scale, 18 + (i // cols) * (ch * scale + lab)
        sheet.paste(im.resize((cw * scale, ch * scale), Image.NEAREST), (x, y), im.resize((cw * scale, ch * scale), Image.NEAREST))
        dr.text((x + 3, y + ch * scale + 2), label, fill=(255, 230, 120))
    sheet.save(out)
    print("wrote", out)


DIRS = "NESW"


def sheets(outdir, scale=2):
    a = Assets()
    os.makedirs(outdir, exist_ok=True)
    base = [0, 0, 7, 4, 0, 0xFF, 0xFF]                   # male, normal form, face 7, body 4, no dye

    def look(**kw):
        lk = list(base)
        for k, v in kw.items():
            lk[{"sex": 0, "form": 1, "face": 2, "armor": 3, "colour": 4, "weapon": 5, "shield": 6}[k]] = v
        return lk

    kits = [("bare", look()), ("bare+shield2", look(shield=2)), ("sword8", look(weapon=8)),
            ("sword8+shield2", look(weapon=8, shield=2)), ("spear3", look(weapon=0x83)),
            ("spear3+shield2", look(weapon=0x83, shield=2)), ("fan1", look(weapon=0xE1)),
            ("fan1+shield2", look(weapon=0xE1, shield=2))]

    # 1. every facing, standing (phase 0), every kit
    cells = []
    for name, lk in kits:
        for d in range(4):
            im, order = compose(a, lk, d)
            cells.append((f"{name} {DIRS[d]} {order}", im))
    grid(cells, 4, scale, "standing: one row per kit, columns N E S W (order = DrwOrder.tbl[p])",
         os.path.join(outdir, "01_facings_standing.png"))

    # 2. the walk cycle, sword + shield and spear + shield, phase 0..3 per facing
    for name, lk in (("sword8+shield2", kits[3][1]), ("spear3+shield2", kits[5][1]), ("bare+shield2", kits[1][1])):
        cells = []
        for d in range(4):
            for ph in range(4):
                im, order = compose(a, lk, d, phase=ph)
                cells.append((f"{DIRS[d]} ph{ph} {order}", im))
        grid(cells, 4, scale, f"walk cycle {name}: rows N E S W, columns phase 0..3 (sub-frame 0,1,0,2)",
             os.path.join(outdir, f"02_walk_{name.replace('+', '_')}.png"))

    # 3. the attack swing (action 1), both frames, per facing and kit
    cells = []
    for name, lk in kits:
        for d in range(4):
            for f in range(2):
                im, order = compose(a, lk, d, action=1, f=f)
                cells.append((f"{name} {DIRS[d]} f{f} {order}", im))
    grid(cells, 8, scale, "attack (0x1A type 1): one row per kit, N f0 f1, E f0 f1, S f0 f1, W f0 f1",
         os.path.join(outdir, "03_attack.png"))

    # 4. mounted (form 3): facing x phase, with a weapon and shield in the look (both must vanish)
    lk = look(form=3, weapon=8, shield=2)
    cells = []
    for d in range(4):
        for ph in range(4):
            im, order = compose(a, lk, d, phase=ph)
            cells.append((f"mounted {DIRS[d]} ph{ph} {order}", im))
    grid(cells, 4, scale, "form 3 (mounted), sword 8 + shield 2 in the look: rows N E S W, columns phase 0..3",
         os.path.join(outdir, "04_mounted.png"))

    # 5. other actions (sword + shield in the look; both are hidden by 0x433040 for these)
    lk = look(weapon=8, shield=2)
    cells = []
    for act, nm, frames in ((2, "throw", 2), (3, "shot", 1), (4, "pickup", 1), (5, "drop", 1), (6, "magic", 1),
                            (8, "eat", 1), (9, "respect", 1), (10, "triumph", 1)):
        for d in range(4):
            for f in range(frames):
                im, order = compose(a, lk, d, action=act, f=f)
                cells.append((f"{act} {nm} {DIRS[d]} f{f} {order}", im))
    grid(cells, 8, scale, "actions 2..10 (sword 8 + shield 2 in the look)", os.path.join(outdir, "05_actions.png"))

    # 6. emotes 11..24 (EMOTION.EPF head) and the ghost (form 1)
    cells = []
    for act in range(11, 25):
        frames = 4 if act == 22 else 1
        for f in range(frames):
            im, order = compose(a, lk, 2, action=act, f=f)
            cells.append((f"emote {act} f{f} {order}", im))
    for d in range(4):
        im, order = compose(a, look(form=1, weapon=8, shield=2), d)
        cells.append((f"ghost {DIRS[d]} {order}", im))
    for fc in (0, 1, 2, 89):
        im, order = compose(a, look(form=1, face=fc), 2)
        cells.append((f"ghost face {fc}", im))
    grid(cells, 8, scale, "emotes 11..24 facing S (EMOTION.EPF head) and the ghost (GHOST.EPF frame = face)",
         os.path.join(outdir, "06_emotes_ghost.png"))

    # 7. control: the table's order against one fixed order, only where they differ in what is drawn
    cells = []
    for name, lk in (("sword8+shield2", kits[3][1]), ("bare+shield2", kits[1][1]), ("fan1+shield2", kits[7][1])):
        for d in range(4):
            for act, frs in ((0, range(4)), (1, range(2))):
                for x in frs:
                    kw = {"phase": x} if act == 0 else {"action": 1, "f": x}
                    im, order = compose(a, lk, d, **kw)
                    alt, _ = compose(a, lk, d, order_override="bhws", **kw)
                    if im.tobytes() != alt.tobytes():
                        tag = f"ph{x}" if act == 0 else f"atk f{x}"
                        cells.append((f"{name} {DIRS[d]} {tag} {order}", im))
                        cells.append(("same, fixed bhws", alt))
    for d in (1, 3):
        im, order = compose(a, lk, d, action=6)
        alt, _ = compose(a, lk, d, action=6, order_override="bhws")
        cells.append((f"magic {DIRS[d]} {order}", im))
        cells.append(("same, fixed bhws", alt))
    grid(cells, 6, scale, "control: DrwOrder.tbl order (left of each pair) vs a fixed b,h,w,s order (right)",
         os.path.join(outdir, "07_order_control.png"))


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "sheets":
        sheets(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 2)
    elif len(sys.argv) >= 12 and sys.argv[1] == "one":
        vals = [int(v, 0) for v in sys.argv[3:]]
        im, order = compose(Assets(), vals[:7], *vals[7:])
        im.save(sys.argv[2])
        print(sys.argv[2], order)
    else:
        print(__doc__)
