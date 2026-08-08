"""Definitive matcher: locate a client Effect.epf frame INSIDE an atlas GIF frame at native scale.

The bbox-normalising scorer in match_spell_fx.py resolves most animations but goes flat on small or
oddly-cropped ones, because it stretches each animation to its own bounding box. That normalisation
is unnecessary: spiritfury/#17 and whitetigerclaws/#31 turned out to be PIXEL-IDENTICAL, which means
the atlas captured the client's sprites 1:1 -- no scaling. So the honest test is template matching:
slide the client frame over the atlas frame and take the best normalised correlation.

This also tolerates the two things that broke the other scorer:
  * subsampled captures (an atlas gif may skip frames) -- every atlas frame is matched against EVERY
    client frame independently, so dropped frames cost nothing
  * different canvas/crop -- the offset search absorbs it

Usage: python template_match_fx.py <gif> [<gif> ...]      (omit for the unresolved set)
"""
import os
import sys

import numpy as np

import match_spell_fx as M
import render_effects as R
import scrape_atlas_spellfx as A


def lum(img):
    return np.asarray(img.convert("L"), dtype=np.float32)


def windows(a, th, tw):
    """All (th,tw) windows of `a` as a view, via stride tricks."""
    H, W = a.shape
    if H < th or W < tw:
        return None
    s = a.strides
    return np.lib.stride_tricks.as_strided(
        a, shape=(H - th + 1, W - tw + 1, th, tw), strides=(s[0], s[1], s[0], s[1]), writeable=False)


def best_offset_corr(scene, tmpl):
    """Max normalised cross-correlation of `tmpl` over all positions in `scene`."""
    th, tw = tmpl.shape
    w = windows(scene, th, tw)
    if w is None:
        return -1.0
    t = tmpl - tmpl.mean()
    tn = np.linalg.norm(t)
    if tn < 1e-6:
        return -1.0
    flat = w.reshape(w.shape[0] * w.shape[1], th * tw).astype(np.float32)
    flat = flat - flat.mean(axis=1, keepdims=True)
    n = np.linalg.norm(flat, axis=1)
    ok = n > 1e-6
    if not ok.any():
        return -1.0
    return float((flat[ok] @ t.ravel() / (n[ok] * tn)).max())


def client_frames():
    tbl, fpal, pals = R.load_table(), R.load_frame_palettes(), R.load_palettes()
    epf = R.Epf()
    out = {}
    for eid in sorted(tbl):
        fr = R.effect_frames(tbl[eid])
        if not fr:
            continue
        box = R.crop_box(epf, fr)
        ims = [R.render_frame(epf, pals, fpal, f, box) for f in fr]
        # keep the frames with real content; blank ones match everything
        keep = [lum(i) for i in ims]
        keep = [k for k in keep if k.max() > 30]
        if keep:
            out[eid] = keep
    return out


def rank(gif, cf, top=5):
    scene = [lum(f) for f in A.gif_frames(os.path.join(A.GIFS, gif + ".gif"))]
    scene = [s for s in scene if s.max() > 30]
    scores = []
    for eid, tmpls in cf.items():
        best = -1.0
        for t in tmpls:
            # crop the template to its own content so padding doesn't dominate
            ys, xs = np.where(t > 20)
            if not len(xs):
                continue
            tc = t[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
            if tc.size < 40:
                continue
            for s in scene:
                c = best_offset_corr(s, tc)
                if c > best:
                    best = c
        scores.append((best, eid))
    return sorted(scores, reverse=True)[:top]


if __name__ == "__main__":
    gifs = sys.argv[1:] or ["kwisinfury", "kwisinret", "mingkenhardenbody", "sanc", "confuse",
                            "sealfate", "chill", "ohaenginspire", "newmight", "dart", "desperate",
                            "endear", "slash", "spark", "ohaengww", "mingkenvenom", "ls",
                            "kwisinpurge", "totemeffect", "remmy"]
    cf = client_frames()
    print(f"template-matching {len(gifs)} animations against {len(cf)} effects\n")
    for g in gifs:
        r = rank(g, cf)
        mark = "OK" if r and r[0][0] > 0.90 else ("~ " if r and r[0][0] > 0.75 else "??")
        print(f"{mark} {g:20s} -> " + "  ".join(f"#{e}:{s:.3f}" for s, e in r))
