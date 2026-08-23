"""Build the 5.33 companion to build_fx_sheet.py.

5.33 splits into two halves, and they are not equally knowable:

  ids 0-127     IDENTICAL to 4.95, and that is verified, not assumed -- the Effect.epf pixel block is a
                byte-exact prefix, the .frm frame->palette map is a byte-exact prefix, and 5.33's first
                13 palettes equal 4.95's 13. So these render from the same GIFs the 4.95 sheet uses.

  frames 1611+  636 frames of art 5.33 adds, drawn from the 16 palettes (13-28) it also adds. These are
                shown as INDIVIDUAL FRAMES, not animations, because grouping frames into effects needs
                Effect.tbl and 5.33's is encrypted (4.95's is plain text; 5.33's is not). Two segmentation
                heuristics were tested against 4.95's known 120 boundaries and both were rejected:
                palette runs found 13 of 120, and bbox jumps scored recall 0.76 / precision 0.38.
                A grouping that wrong is worse than none, so the frames stay ungrouped.

Usage: python build_fx533_sheet.py [out.html]
"""
import html
import json
import os
import sys

import build_fx_sheet as B

FX = B.FX


def build(out_path):
    gifs = json.load(open(os.path.join(FX, "fx495_gifs.json")))
    usage = json.load(open(os.path.join(FX, "fx495_usage.json")))
    frames = json.load(open(os.path.join(FX, "fx533_newframes.json")))
    use, lua = usage["use"], usage["lua"]

    cards = []
    for eid in sorted(int(k) for k in gifs):
        g = gifs[str(eid)]
        wire = eid + 1
        spells = use.get(str(wire), []) + [f"{v} (lua)" for v in lua.get(str(wire), [])]
        state = "guard" if eid in B.GUARDIANS else ("used" if spells else "free")
        img = ('<span class="none">no frames</span>' if not g else
               f'<img src="data:image/gif;base64,{g["b64"]}" width="{g["w"]}" height="{g["h"]}"'
               f' alt="effect index {eid}" loading="lazy">')
        meta = f'{g["n"]}f &middot; {g["ow"]}&times;{g["oh"]}' if g else "&mdash;"
        tags = "".join(f"<li>{html.escape(s)}</li>" for s in spells[:14])
        more = f'<li class="more">+{len(spells) - 14} more</li>' if len(spells) > 14 else ""
        badge = f'<span class="badge">{html.escape(B.GUARDIANS[eid])}</span>' if eid in B.GUARDIANS else ""
        cards.append(
            f'<article class="cell" data-id="{eid}" data-wire="{wire}" data-state="{state}"'
            f' data-q="{html.escape(" ".join(spells).lower())}">'
            f'<header><b>{eid}</b><span class="wire">csv {wire}</span>'
            f'<span class="meta">{meta}</span></header>'
            f'<div class="stage">{img}</div>{badge}'
            f'<ul class="uses">{tags}{more}</ul></article>')

    fcards = []
    for fi in sorted(int(k) for k in frames):
        f = frames[str(fi)]
        fcards.append(
            f'<article class="fcell" data-f="{fi}" data-pal="{f["pal"]}">'
            f'<div class="fstage"><img src="data:image/png;base64,{f["b64"]}"'
            f' width="{f["w"]}" height="{f["h"]}" alt="frame {fi}" loading="lazy"></div>'
            f'<footer><b>{fi}</b><span class="pal">p{f["pal"]}</span>'
            f'<span class="meta">{f["ow"]}&times;{f["oh"]}</span></footer></article>')

    extra = """
.sec{margin:38px 0 0}
.sec h2{font-size:15px;font-weight:600;margin:0 0 4px;letter-spacing:.01em}
.rule{height:1px;background:var(--line);margin:14px 0 18px}
.fgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(118px,1fr));gap:8px}
.fcell{background:var(--panel);border:1px solid var(--line);border-radius:3px;padding:6px 6px 3px;
  display:flex;flex-direction:column;gap:5px}
.fstage{background:#000;border-radius:2px;height:104px;display:grid;place-items:center;overflow:hidden}
.fstage img{max-width:100%;max-height:100px;image-rendering:pixelated}
.fcell footer{display:flex;align-items:baseline;gap:6px}
.fcell footer b{font-family:var(--mono);font-size:12px;color:var(--cool);font-variant-numeric:tabular-nums}
.pal{font-family:var(--mono);font-size:9.5px;color:var(--dim)}
.note{border-left:2px solid var(--gold);padding:2px 0 2px 13px;margin:16px 0 0}
"""

    doc = f"""<title>5.33 Effect Table</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600&amp;family=JetBrains+Mono:wght@400;700&amp;display=swap">
<style>{B.CSS}{extra}</style>
<div class="wrap">
<h1>5.33 Effect Table</h1>
<p class="sub">What the 5.33 client (<code>NextAeon533/Efx.dat</code>) can draw. It arrives in two
halves that are not equally knowable, so they are shown differently and labelled as such.</p>

<section class="sec">
<h2>Shared with 4.95 &mdash; ids 0&ndash;127</h2>
<p class="sub">Identical to the 4.95 client, and that is verified rather than assumed: the
<code>Effect.epf</code> pixel block is a byte-exact prefix, the <code>.frm</code> frame&rarr;palette map
is a byte-exact prefix, and 5.33's first 13 palettes equal 4.95's 13. So an <code>animation</code> value
draws the same art on both clients. Large orange number is the <b>table index</b>; blue is the
<b>csv/wire value</b>, which is index&nbsp;+&nbsp;1.</p>
<div class="rule"></div>
<div class="grid">{"".join(cards)}</div>
</section>

<section class="sec">
<h2>Added by 5.33 &mdash; {len(frames)} frames, ungrouped</h2>
<p class="sub">5.33 appends 636 frames past 4.95's 1611, drawn from the 16 palettes (13&ndash;28) it
also adds. These are <b>individual frames, not animations</b>, and they carry <b>frame</b> numbers, not
effect ids &mdash; the two are different things.</p>
<p class="sub note">Grouping frames into effects needs <code>Effect.tbl</code>, and 5.33's is
encrypted where 4.95's is plain text. Two ways of inferring the boundaries were tested against 4.95's
120 known ones and both were rejected: palette runs found 13 of 120, and bounding-box jumps scored
recall 0.76 at precision 0.38. At that accuracy most groupings shown would be wrong, which is worse
than showing none &mdash; so these stay as frames until the table is cracked.</p>
<div class="rule"></div>
<div class="fgrid">{"".join(fcards)}</div>
</section>
</div>"""
    open(out_path, "w", encoding="utf-8").write(doc)
    return len(doc), len(cards), len(fcards)


if __name__ == "__main__":
    dest = sys.argv[1] if len(sys.argv) > 1 else os.path.join(FX, "fx533_sheet.html")
    size, n_eff, n_fr = build(dest)
    print(f"wrote {dest}  {size / 1e6:.2f} MB  effects {n_eff}  new frames {n_fr}")
