"""Search a client's raw Effect.epf FRAME range for the art in an atlas GIF, with no Effect.tbl needed.

The 5.33 Effect.tbl is obfuscated, so effect ids are unavailable there -- but the .epf, .frm and .pal
all parse with the 4.95 loaders, and the pixel blocks are strictly append-only
(4.83 1432 frames  <  4.95 1611  <  5.33 2247). So "is this animation in that client at all?" is
answerable at the FRAME level: template-match every atlas frame against every client frame in a range.

Usage: python find_frame_in_gif.py <gifname> [--assets DIR] [--lo N] [--hi N] [--top N]
"""
import os
import sys

import numpy as np

import render_effects as R
import scrape_atlas_spellfx as A
import template_match_fx as T

if __name__ == "__main__":
    argv = sys.argv[1:]
    assets, lo, hi, top = "fx533", 1611, 2247, 10

    def opt(flag, cast, cur):
        global argv
        if flag in argv:
            i = argv.index(flag); v = cast(argv[i + 1]); del argv[i:i + 2]; return v
        return cur
    assets = opt("--assets", str, assets)
    lo = opt("--lo", int, lo); hi = opt("--hi", int, hi); top = opt("--top", int, top)

    gif = argv[0]
    R.FX = os.path.join(os.path.dirname(os.path.abspath(__file__)), assets)
    fpal, pals, epf = R.load_frame_palettes(), R.load_palettes(), R.Epf()

    scene = [T.lum(f) for f in A.gif_frames(os.path.join(A.GIFS, gif + ".gif"))]
    scene = [s for s in scene if s.max() > 30]
    print(f"{gif}: {len(scene)} atlas frames vs client frames {lo}-{hi - 1} of {assets}", flush=True)

    out = []
    for fi in range(lo, hi):
        f = epf.frame(fi)
        if not f:
            continue
        t = T.lum(R.render_frame(epf, pals, fpal, fi, R.crop_box(epf, [fi])))
        ys, xs = np.where(t > 20)
        if not len(xs):
            continue
        tc = t[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
        if tc.size < 40:
            continue
        best = max((T.best_offset_corr(s, tc) for s in scene), default=-1.0)
        out.append((best, fi))
    out.sort(reverse=True)
    for s, fi in out[:top]:
        print(f"  frame {fi:5d}  corr {s:.3f}", flush=True)
