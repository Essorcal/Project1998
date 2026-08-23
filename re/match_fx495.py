"""Rank atlas GIFs against a chosen client's Effect assets (defaults to the 4.95 set in fx495/).

re/fx/Effect.* is byte-identical to the 4.83 client, NOT 4.95 -- so every earlier match ran against
a table where effects 117-120 are EMPTY and 121-127 do not exist. 4.95's Effect.epf is strictly
append-only over 4.83's (first 1432 frames identical in pixels and TOC), so old matches for 0-116
still stand; only the 11 late effects were invisible. This points the same scorer at the real set.

Usage: python match_fx495.py <gif> [<gif> ...]   [--assets DIR] [--top N]
"""
import os
import sys

import render_effects as R
import template_match_fx as T

if __name__ == "__main__":
    argv = sys.argv[1:]
    assets, top = "fx495", 8
    if "--assets" in argv:
        i = argv.index("--assets"); assets = argv[i + 1]; del argv[i:i + 2]
    if "--top" in argv:
        i = argv.index("--top"); top = int(argv[i + 1]); del argv[i:i + 2]
    R.FX = os.path.join(os.path.dirname(os.path.abspath(__file__)), assets)
    cf = T.client_frames()
    print(f"assets={assets}  effects_with_art={len(cf)}  ids={min(cf)}-{max(cf)}", flush=True)
    for g in argv:
        r = T.rank(g, cf, top=top)
        mark = "OK" if r and r[0][0] > 0.90 else ("~ " if r and r[0][0] > 0.75 else "??")
        print(f"{mark} {g:24s} -> " + "  ".join(f"#{e}:{s:.3f}" for s, e in r), flush=True)
