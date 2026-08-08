"""Render one comparison image per DISTINCT animation conflict, so a human can settle it by eye.

The ~70 individual REVIEW rows collapse into far fewer real questions: many spells share the same
(csv id, atlas id, gif) triple. Each sheet shows three things side by side:

    [ what nexusatlas shows ]  [ what the CSV currently points at ]  [ what the atlas maps to ]

so the question is always "which of the two client effects is the atlas capture?".
"""
import os

from PIL import Image, ImageDraw

import apply_spell_anim as AP
import match_spell_fx as M
import render_effects as R
import scrape_atlas_spellfx as A

OUT = os.path.join(AP.FX, "review")


def strip_frames(frames, cell):
    out = []
    for f in frames:
        f = f.copy()
        f.thumbnail((cell, cell), Image.LANCZOS)
        out.append(f)
    return out


def main(rows, cell=104, maxframes=10):
    os.makedirs(OUT, exist_ok=True)
    tbl, fpal, pals = R.load_table(), R.load_frame_palettes(), R.load_palettes()
    epf = R.Epf()

    def eff_frames(wire):
        """Client frames for a WIRE id (= table index + 1)."""
        eid = wire - 1
        if eid not in tbl:
            return []
        fr = R.effect_frames(tbl[eid])
        if not fr:
            return []
        box = R.crop_box(epf, fr)
        return [R.render_frame(epf, pals, fpal, f, box) for f in fr]

    groups = {}
    for k, nm, cls, gif, cur, wire, verdict, why in rows:
        if verdict != "REVIEW":
            continue
        groups.setdefault((gif, cur, str(wire)), []).append(k)

    made = []
    for (gif, cur, wire), keys in sorted(groups.items()):
        lanes = []
        if gif and gif != AP.NO_ANIM and os.path.exists(os.path.join(A.GIFS, gif + ".gif")):
            lanes.append((f"nexusatlas: {gif}", A.gif_frames(os.path.join(A.GIFS, gif + ".gif"))))
        if cur.lstrip("-").isdigit():
            lanes.append((f"CSV now: {cur}", eff_frames(int(cur))))
        if wire.isdigit():
            lanes.append((f"atlas says: {wire}", eff_frames(int(wire))))
        lanes = [(t, strip_frames(fs, cell)) for t, fs in lanes if fs]
        if not lanes:
            continue
        ncol = min(maxframes, max(len(fs) for _, fs in lanes))
        lbl, pad, hdr = 168, 4, 26
        W = lbl + ncol * (cell + pad)
        H = hdr + len(lanes) * (cell + pad) + 22
        sheet = Image.new("RGB", (W, H), (18, 18, 22))
        dr = ImageDraw.Draw(sheet)
        title = ", ".join(sorted(keys)[:3]) + ("" if len(keys) <= 3 else f"  +{len(keys) - 3} more")
        dr.text((6, 6), title, fill=(150, 230, 255))
        y = hdr
        for t, fs in lanes:
            step = max(1, (len(fs) + ncol - 1) // ncol)
            for i, f in enumerate(fs[::step][:ncol]):
                sheet.paste(f, (lbl + i * (cell + pad), y + (cell - f.height) // 2))
            dr.text((6, y + cell // 2 - 6), t, fill=(255, 210, 100))
            y += cell + pad
        dr.text((6, y + 4), f"{len(keys)} spell(s)", fill=(140, 140, 150))
        name = f"{gif}_{cur or 'blank'}_vs_{wire or 'none'}.png".replace("/", "_")
        p = os.path.join(OUT, name)
        sheet.save(p)
        made.append((p, keys, gif, cur, wire))
    print(f"{len(made)} distinct conflicts -> {OUT}")
    for p, keys, gif, cur, wire in made:
        print(f"  {os.path.basename(p):44s} csv={cur:>5s} atlas={wire:>5s}  {len(keys)} spells")
    return made
