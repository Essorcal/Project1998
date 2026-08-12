"""Identify an NPC's `look` id by matching a period screenshot against the client's own sprites.

The tswolf newbie-quest pages captured each dialog page as a GIF, which means the NPC portrait in them
is the real client render at native scale. Monster.epf holds those sprites (NPC looks index the creature
space -- NPCs are stationary mobs), so the id can be recovered by comparison rather than guessed.

Matching is on the SPRITE OUTLINE, not colour: the portrait is tinted by the NPC's own palette/recolour
which we can't reproduce exactly, but the near-black outline is stable across palettes. Compared as an
IoU of outline masks aligned on their bounding boxes.

The script self-validates: three of the four portraits have look ids we already know from NPCs.csv
(smith 6, coordinate tutor 87, Mignok 14). If those don't come back rank 1, the method is wrong and the
fourth answer means nothing.
"""
import os, struct, sys
from PIL import Image

RE = r"C:\Users\brian\Desktop\Project1998\re"
sys.path.insert(0, RE)
from render_pets import Epf, load_tbl, load_pal          # noqa: E402  (reuse the proven decoders)

DARK = 170          # sum(rgb) below this counts as outline


def mask_of(pixels, w, h, keep):
    """Bounding-boxed set of outline pixels, origin-normalised so two sprites can be compared."""
    pts = [(x, y) for y in range(h) for x in range(w) if keep(x, y)]
    if not pts:
        return None, None
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
    x0, y0 = min(xs), min(ys)
    return {(x - x0, y - y0) for x, y in pts}, (max(xs) - x0 + 1, max(ys) - y0 + 1)


def portrait_mask(path, right=46):
    im = Image.open(path).convert("RGB"); w, h = im.size
    px = im.load()
    w = min(right, w)
    return mask_of(px, w, h, lambda x, y: sum(px[x, y]) < DARK)


def sprite_mask(epf, looks, pals, look):
    info = looks.get(look)
    if not info:
        return None, None
    f = epf.frame(info["start"])
    if not f:
        return None, None
    w, h, raw = f
    pal = pals[info["pal"] % len(pals)]
    def keep(x, y):
        c = raw[y * w + x]
        return c != 0 and sum(pal[c]) < DARK      # index 0 is transparent, not black
    return mask_of(raw, w, h, keep)


def best(epf, looks, pals, target, topn=6):
    tmask, tsize = target
    scored = []
    for look in sorted(looks):
        smask, ssize = sprite_mask(epf, looks, pals, look)
        if not smask:
            continue
        # a sprite of wildly different footprint is not the same character
        if abs(ssize[0] - tsize[0]) > 4 or abs(ssize[1] - tsize[1]) > 6:
            continue
        iou = len(smask & tmask) / len(smask | tmask)
        scored.append((iou, look, ssize))
    scored.sort(reverse=True)
    return scored[:topn]


def main():
    epf, looks, pals = Epf(), load_tbl(), load_pal()
    cases = [("merchant13.gif", 6,  "Q1 smith"),
             ("quest301.gif",   87, "Q3 coordinate tutor"),
             ("quest405.gif",   14, "Mignok"),
             ("quest201.gif",   None, "Q2 ARMORER  <-- unknown")]
    for f, expect, label in cases:
        t = portrait_mask(f)
        if not t[0]:
            print(f"{f}: no outline found"); continue
        top = best(epf, looks, pals, t)
        got = top[0][1] if top else None
        verdict = ""
        if expect is not None:
            verdict = "  OK" if got == expect else f"  MISMATCH (expected {expect})"
        print(f"\n{label}  [{f}]  target footprint {t[1]}{verdict}")
        for iou, look, size in top:
            star = " <=" if look == expect else ""
            print(f"    look {look:4d}  IoU {iou:.3f}  {size}{star}")


if __name__ == "__main__":
    main()
