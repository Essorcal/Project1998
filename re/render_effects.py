"""Render the 4.95 client's spell/effect animations (Effect.epf) so they can be identified by eye
and matched against the nexusatlas spell-animation GIFs.

Asset layout (all extracted from NexusTK.dat into re/fx/):
  Effect.tbl  text: "NumEffects 121" then per effect an "ID n, NumFrontFrames a, ... NumBackFrames b"
              line followed by a+b "Frame# N, Delay ms, Alpha f, Light l" lines. Frame# is the
              ABSOLUTE Effect.epf frame index -- no cumulative-sum needed. The first a are the
              "front" layer (drawn over the sprite), the last b the "back" layer (drawn under it).
  Effect.frm  u32 count (1432 == epf frame count) then one u32 PER FRAME = its palette index (0-10).
  Effect.pal  u32 count then N 1056B "DLPalette" blocks: 32B header + 256 RGBA entries at +32.
              (NOT +38 -- that offset in render_items.py is 6 bytes into the color data and shears
              every channel, which is why a first pass came out magenta/blue.)
  Effect.epf  u16 frameCount, u16 w, u16 h, u16 pad, u32 tocOff; frames are raw 8bpp palette
              indices at 12+pixOff. TOC is at 12+tocOff (RELATIVE to the header, not absolute) and
              each 16B entry is top(i16) left(i16) bottom(i16) right(i16) pixOff(u32) stencilOff(u32)
              -- 4 shorts then 2 ints. Verified: all 1432 frames satisfy stencilOff-pixOff == w*h.
              Box coords are signed and usually negative: they are offsets from the cast tile, so the
              effect is centred on its target rather than drawn at the canvas origin.

Palette index 0 is transparent (these are glows composited over the map).

Usage:
  python render_effects.py sheet <lo> <hi> [out.png] [scale]   # contact sheet, frames left->right
  python render_effects.py gif <id> [out.gif] [scale]          # one animated gif
  python render_effects.py gifall <outdir> [scale]             # every effect as a gif
  python render_effects.py info                                # per-effect frame/palette summary
"""
import os
import struct
import sys

from PIL import Image

FX = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fx")


def load_palettes(path=None):
    d = open(path or os.path.join(FX, "Effect.pal"), "rb").read()
    offs, i = [], 0
    while True:
        j = d.find(b"DLPalette", i)
        if j < 0:
            break
        offs.append(j)
        i = j + 1
    pals = []
    for off in offs:
        base = off + 32
        pals.append([tuple(d[base + c * 4:base + c * 4 + 3]) if base + c * 4 + 3 <= len(d) else (0, 0, 0)
                     for c in range(256)])
    return pals


def load_frame_palettes(path=None):
    d = open(path or os.path.join(FX, "Effect.frm"), "rb").read()
    n, = struct.unpack_from("<I", d, 0)
    return list(struct.unpack_from("<%dI" % n, d, 4))


def load_table(path=None):
    """-> {id: {'front': [frame#], 'back': [frame#], 'delay': [ms]}}"""
    effects, cur, want = {}, None, 0
    for line in open(path or os.path.join(FX, "Effect.tbl"), encoding="latin1"):
        line = line.strip().rstrip(",")
        if not line:
            continue
        parts = dict()
        for p in line.split(", "):
            k, _, v = p.rpartition(" ")
            parts[k.strip() or p] = v
        if line.startswith("ID "):
            eid = int(parts["ID"])
            nf, nb = int(parts["NumFrontFrames"]), int(parts["NumBackFrames"])
            cur = effects[eid] = {"front": [], "back": [], "delay": [], "n": nf + nb, "nf": nf}
            want = nf + nb
        elif line.startswith("Frame#") and cur is not None and want:
            fi, delay = int(parts["Frame#"]), int(parts["Delay"])
            (cur["front"] if len(cur["front"]) < cur["nf"] else cur["back"]).append(fi)
            cur["delay"].append(delay)
            want -= 1
    return effects


class Epf:
    def __init__(self, path=None):
        self.d = open(path or os.path.join(FX, "Effect.epf"), "rb").read()
        self.count, self.w, self.h = struct.unpack_from("<HHH", self.d, 0)
        self.toc = 12 + struct.unpack_from("<I", self.d, 8)[0]

    def frame(self, fi):
        """-> (left, top, w, h, raw8bpp) or None for an empty frame."""
        if fi < 0 or fi >= self.count:
            return None
        top, left, bot, right, pix, sten = struct.unpack_from("<hhhhII", self.d, self.toc + fi * 16)
        w, h = right - left, bot - top
        if w <= 0 or h <= 0 or sten - pix != w * h:
            return None
        return left, top, w, h, self.d[12 + pix: 12 + pix + w * h]


def crop_box(epf, frames):
    """Union bounding box over a frame list, in the signed tile-relative space the boxes use.
    Every frame of one effect is drawn into this shared box so the animation doesn't jitter."""
    box = None
    for fi in frames:
        f = epf.frame(fi)
        if not f:
            continue
        left, top, w, h, _ = f
        b = (left, top, left + w, top + h)
        box = b if box is None else (min(box[0], b[0]), min(box[1], b[1]),
                                     max(box[2], b[2]), max(box[3], b[3]))
    return box or (0, 0, 1, 1)


def render_frame(epf, pals, fpal, fi, box, bg=(0, 0, 0)):
    """One frame composited into the effect's shared bounding box."""
    x0, y0, x1, y1 = box
    img = Image.new("RGB", (max(1, x1 - x0), max(1, y1 - y0)), bg)
    f = epf.frame(fi)
    if not f:
        return img
    left, top, w, h, raw = f
    pal = pals[fpal[fi]] if fi < len(fpal) and fpal[fi] < len(pals) else pals[0]
    px = img.load()
    for y in range(h):
        row = raw[y * w:(y + 1) * w]
        ty = top + y - y0
        if not (0 <= ty < img.height):
            continue
        for x, c in enumerate(row):
            if c:  # index 0 == transparent
                tx = left + x - x0
                if 0 <= tx < img.width:
                    px[tx, ty] = pal[c]
    return img


def effect_frames(eff, layer="both"):
    if layer == "front":
        return eff["front"]
    if layer == "back":
        return eff["back"]
    return eff["front"] + eff["back"]


def cmd_info():
    tbl, fpal = load_table(), load_frame_palettes()
    epf = Epf()
    print(f"{len(tbl)} effects, epf {epf.count} frames {epf.w}x{epf.h}")
    print("id  front back  frames                     pal  delay")
    for eid in sorted(tbl):
        e = tbl[eid]
        fr = effect_frames(e)
        pals = sorted({fpal[f] for f in fr if f < len(fpal)})
        span = f"{min(fr)}-{max(fr)}" if fr else "-"
        print(f"{eid:3d} {len(e['front']):5d} {len(e['back']):4d}  {span:24s} {str(pals):6s} "
              f"{e['delay'][0] if e['delay'] else '-'}")


def cmd_sheet(lo, hi, out, scale):
    tbl, fpal, pals = load_table(), load_frame_palettes(), load_palettes()
    epf = Epf()
    ids = [i for i in sorted(tbl) if lo <= i <= hi]
    rows = []
    for eid in ids:
        fr = effect_frames(tbl[eid])
        box = crop_box(epf, fr)
        cells = [render_frame(epf, pals, fpal, f, box) for f in fr]
        rows.append((eid, cells))
    cw = max((c.width for _, cs in rows for c in cs), default=1)
    ch = max((c.height for _, cs in rows for c in cs), default=1)
    ncol = max((len(cs) for _, cs in rows), default=1)
    pad, lblw = 2, 44
    W = lblw + ncol * (cw + pad) * scale
    H = sum((ch + pad) for _ in rows) * scale
    sheet = Image.new("RGB", (W, H), (24, 24, 28))
    from PIL import ImageDraw
    dr = ImageDraw.Draw(sheet)
    y = 0
    for eid, cells in rows:
        for k, c in enumerate(cells):
            c = c.resize((c.width * scale, c.height * scale), Image.NEAREST)
            sheet.paste(c, (lblw + k * (cw + pad) * scale, y))
        dr.text((4, y + 4), f"#{eid}", fill=(255, 220, 120))
        y += (ch + pad) * scale
    sheet.save(out)
    print(f"wrote {out}  ({len(ids)} effects, {sheet.width}x{sheet.height})")


def cmd_gif(eid, out, scale, layer="both"):
    tbl, fpal, pals = load_table(), load_frame_palettes(), load_palettes()
    epf = Epf()
    e = tbl[eid]
    fr = effect_frames(e, layer)
    if not fr:
        print(f"effect {eid}: no frames")
        return
    box = crop_box(epf, fr)
    imgs = []
    for f in fr:
        im = render_frame(epf, pals, fpal, f, box)
        if scale != 1:
            im = im.resize((im.width * scale, im.height * scale), Image.NEAREST)
        imgs.append(im.convert("P", palette=Image.ADAPTIVE))
    imgs[0].save(out, save_all=True, append_images=imgs[1:],
                 duration=e["delay"][:len(fr)] or 100, loop=0)
    print(f"wrote {out}  ({len(fr)} frames, {box[2] - box[0]}x{box[3] - box[1]})")


def cmd_gifall(outdir, scale):
    os.makedirs(outdir, exist_ok=True)
    for eid in sorted(load_table()):
        cmd_gif(eid, os.path.join(outdir, f"fx_{eid:03d}.gif"), scale)


def blend(frames):
    """Max-blend a frame list into one image -- a compact signature of the whole animation that
    survives being shrunk to a thumbnail, unlike any single frame."""
    from PIL import ImageChops
    out = frames[0]
    for f in frames[1:]:
        out = ImageChops.lighter(out, f)
    return out


def cmd_grid(out="fx_grid.png", cell=120, cols=11):
    """One labelled thumbnail per effect: the whole 121-effect space on a single sheet."""
    from PIL import ImageDraw
    tbl, fpal, pals = load_table(), load_frame_palettes(), load_palettes()
    epf = Epf()
    ids = sorted(tbl)
    sheet = Image.new("RGB", (cols * cell, ((len(ids) + cols - 1) // cols) * (cell + 14)), (20, 20, 24))
    dr = ImageDraw.Draw(sheet)
    for k, eid in enumerate(ids):
        fr = effect_frames(tbl[eid])
        box = crop_box(epf, fr)
        im = blend([render_frame(epf, pals, fpal, f, box) for f in fr]) if fr else \
            Image.new("RGB", (cell, cell), (20, 20, 24))
        im.thumbnail((cell - 4, cell - 4), Image.LANCZOS)
        cx, cy = (k % cols) * cell, (k // cols) * (cell + 14)
        sheet.paste(im, (cx + (cell - im.width) // 2, cy + 14 + (cell - 14 - im.height) // 2))
        dr.text((cx + 3, cy + 2), f"#{eid}  {len(fr)}f {box[2] - box[0]}x{box[3] - box[1]}",
                fill=(255, 210, 100))
    sheet.save(out)
    print(f"wrote {out} {sheet.size}")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    c = sys.argv[1]
    if c == "info":
        cmd_info()
    elif c == "grid":
        cmd_grid(sys.argv[2] if len(sys.argv) > 2 else "fx_grid.png",
                 int(sys.argv[3]) if len(sys.argv) > 3 else 120)
    elif c == "sheet":
        cmd_sheet(int(sys.argv[2]), int(sys.argv[3]),
                  sys.argv[4] if len(sys.argv) > 4 else "fx_sheet.png",
                  int(sys.argv[5]) if len(sys.argv) > 5 else 1)
    elif c == "gif":
        cmd_gif(int(sys.argv[2]), sys.argv[3] if len(sys.argv) > 3 else "fx.gif",
                int(sys.argv[4]) if len(sys.argv) > 4 else 1)
    elif c == "gifall":
        cmd_gifall(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 1)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
