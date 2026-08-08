"""Rank client Effect.epf animations against the nexusatlas spell GIFs, so each spell's animation id
can be identified rather than guessed.

Both sides are reduced to the same descriptor:
  * frames composited on black, luminance-thresholded to a mask
  * the union bounding box over all frames (so a tight atlas crop and the client's larger canvas
    line up), then each frame resized into a fixed grid
  * a max-blend "signature" plus the per-frame sequence

Score combines
  * signature correlation      -- does the whole animation trace the same shape
  * per-frame sequence correlation (length-resampled) -- does it EVOLVE the same way
  * frame-count agreement      -- the atlas gifs were captured frame-per-frame, so this is sharp
  * aspect/size agreement      -- several pairs match to the exact pixel

Nothing here decides anything on its own: it produces a ranked shortlist that is then confirmed by
eye against the contact sheets, because a wrong-but-plausible match is worse than an unfilled cell.

Usage:
  python match_spell_fx.py rank [topN]      # ranked candidates per atlas gif
  python match_spell_fx.py pairs <gif> <id> [<id>...]   # side-by-side sheet for confirmation
"""
import os
import sys

import numpy as np
from PIL import Image, ImageChops, ImageDraw

import render_effects as R
import scrape_atlas_spellfx as A

G = 40  # descriptor grid


def mask_bbox(frames, thr=24):
    arrs = [np.asarray(f.convert("L"), dtype=np.float32) for f in frames]
    acc = np.maximum.reduce(arrs)
    ys, xs = np.where(acc > thr)
    if not len(xs):
        return None, arrs
    return (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1), arrs


def descriptor(frames):
    """-> (signature GxG, sequence list of GxG, (w,h), nframes, chroma) or None.

    `chroma` is the mean colour of the lit pixels, normalised to unit length so brightness drops out
    and only the HUE RATIO survives. Without it #19 and #20 -- the same bar pattern in a green and a
    magenta palette -- are indistinguishable and both score exactly 1.000 against both shadowfigure
    animations.
    """
    box, arrs = mask_bbox(frames)
    if box is None:
        return None
    x0, y0, x1, y1 = box
    seq = []
    for a in arrs:
        im = Image.fromarray(a[y0:y1, x0:x1].astype(np.uint8)).resize((G, G), Image.BILINEAR)
        v = np.asarray(im, dtype=np.float32)
        m = v.max()
        seq.append(v / m if m > 0 else v)
    sig = np.maximum.reduce(seq)

    tot = np.zeros(3, dtype=np.float64)
    for f in frames:
        rgb = np.asarray(f.convert("RGB"), dtype=np.float32)[y0:y1, x0:x1]
        lit = rgb.sum(axis=2) > 40
        if lit.any():
            tot += rgb[lit].mean(axis=0)
    n = np.linalg.norm(tot)
    chroma = tot / n if n else np.array([0.577, 0.577, 0.577])
    return sig, seq, (x1 - x0, y1 - y0), len(frames), chroma


def corr(a, b):
    a, b = a.ravel(), b.ravel()
    a, b = a - a.mean(), b - b.mean()
    d = np.linalg.norm(a) * np.linalg.norm(b)
    return float(a.dot(b) / d) if d else 0.0


def seq_corr(sa, sb):
    n = max(len(sa), len(sb))
    idx = lambda s: [s[min(len(s) - 1, round(i * (len(s) - 1) / max(1, n - 1)))] for i in range(n)]
    return float(np.mean([corr(x, y) for x, y in zip(idx(sa), idx(sb))]))


def score(da, db):
    sig = corr(da[0], db[0])
    seq = seq_corr(da[1], db[1])
    fa, fb = da[3], db[3]
    fc = 1.0 - abs(fa - fb) / max(fa, fb)
    (wa, ha), (wb, hb) = da[2], db[2]
    ar = 1.0 - min(1.0, abs((wa / max(1, ha)) - (wb / max(1, hb))) / 2)
    sz = 1.0 - min(1.0, (abs(wa - wb) + abs(ha - hb)) / max(wa + ha, wb + hb))
    col = max(0.0, float(da[4].dot(db[4])))  # cosine between unit chroma vectors
    total = 0.34 * sig + 0.22 * seq + 0.14 * fc + 0.05 * ar + 0.07 * sz + 0.18 * col
    return total, sig, seq, fc, sz, col


def client_descriptors():
    tbl, fpal, pals = R.load_table(), R.load_frame_palettes(), R.load_palettes()
    epf = R.Epf()
    out = {}
    for eid in sorted(tbl):
        fr = R.effect_frames(tbl[eid])
        if not fr:
            continue
        box = R.crop_box(epf, fr)
        d = descriptor([R.render_frame(epf, pals, fpal, f, box) for f in fr])
        if d:
            out[eid] = d
    return out


def atlas_descriptors():
    out = {}
    for n in sorted(os.listdir(A.GIFS)):
        if not n.endswith(".gif") or n == "none.gif":
            continue
        d = descriptor(A.gif_frames(os.path.join(A.GIFS, n)))
        if d:
            out[n[:-4]] = d
    return out


def cmd_rank(top=6):
    cd, ad = client_descriptors(), atlas_descriptors()
    print(f"{len(ad)} atlas animations vs {len(cd)} client effects\n")
    rows = []
    for name in sorted(ad):
        cands = sorted(((score(ad[name], cd[e]), e) for e in cd), key=lambda t: -t[0][0])[:top]
        rows.append((name, cands))
        best = cands[0]
        margin = best[0][0] - cands[1][0][0] if len(cands) > 1 else 1.0
        flag = "  " if margin > 0.05 else "??"
        print(f"{flag} {name:24s} {ad[name][3]:2d}f {ad[name][2][0]:3d}x{ad[name][2][1]:<3d} -> " +
              "  ".join(f"#{e}:{s[0]:.3f}" for s, e in cands))
    return rows


def cmd_pairs(gif, ids, out=None, scale=3):
    """Side-by-side confirmation sheet: the atlas animation on top, each candidate effect below."""
    tbl, fpal, pals = R.load_table(), R.load_frame_palettes(), R.load_palettes()
    epf = R.Epf()
    rows = [(gif, A.gif_frames(os.path.join(A.GIFS, gif + ".gif")))]
    for eid in ids:
        fr = R.effect_frames(tbl[int(eid)])
        box = R.crop_box(epf, fr)
        rows.append((f"#{eid}", [R.render_frame(epf, pals, fpal, f, box) for f in fr]))
    cw = max(max(f.width for f in fs) for _, fs in rows)
    ch = max(max(f.height for f in fs) for _, fs in rows)
    ncol = max(len(fs) for _, fs in rows)
    lbl, pad = 90, 3
    sheet = Image.new("RGB", (lbl + ncol * (cw + pad) * scale, sum(ch + pad for _ in rows) * scale),
                      (20, 20, 24))
    dr = ImageDraw.Draw(sheet)
    y = 0
    for n, fs in rows:
        for k, f in enumerate(fs):
            f = f.resize((f.width * scale, f.height * scale), Image.NEAREST)
            sheet.paste(f, (lbl + k * (cw + pad) * scale, y))
        dr.text((4, y + 4), n, fill=(255, 210, 100))
        y += (ch + pad) * scale
    out = out or os.path.join(A.FX, "pairs", f"{gif}.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    sheet.save(out)
    print("wrote", out, sheet.size)


def blend_img(frames):
    out = frames[0]
    for f in frames[1:]:
        out = ImageChops.lighter(out, f)
    return out


def cmd_verify(cell=132, cols=6, per=36):
    """Grid of [atlas | best client] pairs with scores, for eyeball confirmation of the ranking."""
    tbl, fpal, pals = R.load_table(), R.load_frame_palettes(), R.load_palettes()
    epf = R.Epf()
    cd, ad = client_descriptors(), atlas_descriptors()
    names = sorted(ad)
    cbl = {}
    for eid in sorted(tbl):
        fr = R.effect_frames(tbl[eid])
        if fr:
            box = R.crop_box(epf, fr)
            cbl[eid] = blend_img([R.render_frame(epf, pals, fpal, f, box) for f in fr])
    outs = []
    for g in range(0, len(names), per):
        chunk = names[g:g + per]
        rows = (len(chunk) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * cell * 2, rows * (cell + 16)), (20, 20, 24))
        dr = ImageDraw.Draw(sheet)
        for k, n in enumerate(chunk):
            cands = sorted(((score(ad[n], cd[e]), e) for e in cd), key=lambda t: -t[0][0])
            (sc, *_), eid = cands[0]
            margin = sc - cands[1][0][0]
            cx, cy = (k % cols) * cell * 2, (k // cols) * (cell + 16)
            a = blend_img(A.gif_frames(os.path.join(A.GIFS, n + ".gif")))
            a.thumbnail((cell - 6, cell - 6), Image.LANCZOS)
            sheet.paste(a, (cx + 3, cy + 16))
            b = cbl[eid].copy()
            b.thumbnail((cell - 6, cell - 6), Image.LANCZOS)
            sheet.paste(b, (cx + cell + 3, cy + 16))
            col = (255, 210, 100) if margin > 0.05 else (255, 120, 120)
            dr.text((cx + 3, cy + 3), f"{n}", fill=col)
            dr.text((cx + cell + 3, cy + 3), f"#{eid} {sc:.2f} d{margin:.2f}", fill=col)
            dr.line([(cx + cell, cy + 16), (cx + cell, cy + cell + 12)], fill=(70, 70, 80))
        p = os.path.join(A.FX, f"verify_{g // per}.png")
        sheet.save(p)
        outs.append(p)
        print("wrote", p, sheet.size)
    return outs


if __name__ == "__main__":
    c = sys.argv[1] if len(sys.argv) > 1 else "rank"
    if c == "rank":
        cmd_rank(int(sys.argv[2]) if len(sys.argv) > 2 else 6)
    elif c == "pairs":
        cmd_pairs(sys.argv[2], sys.argv[3:])
    elif c == "verify":
        cmd_verify()
    else:
        print(__doc__)
