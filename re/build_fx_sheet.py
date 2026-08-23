"""Build a browsable HTML sheet of one client's whole Effect.tbl: every effect animated, at its real
frame delays, labelled with the spells that currently use it.

Picking an animation id used to mean squinting at a static contact sheet. The ids are the literal
wire byte (Session.SendEffect's EfxWireOffset is 0), so what you see here is what a cast draws.

Inputs come from render_fx_gifs / the usage scan:
  re/fx/fx495_gifs.json   {eid: {b64, n, w, h, ow, oh}}   animated GIFs, base64
  re/fx/fx495_usage.json  {use: {id: [spell display names]}, lua: {id: [verb names]}}

Usage: python build_fx_sheet.py [out.html]
"""
import html
import json
import os
import sys

FX = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fx")

# TWO NUMBER SPACES, and mixing them is the classic bug here (it cost a round of wrong edits):
#   Effect.tbl INDEX  -- what the client actually draws, what render_effects.py renders, 0-based
#   WIRE value        -- what goes in spell_effects.csv `animation` and ctx:fx(), = index + 1
# The client loads Effect.tbl 1-based, so the 0x29 byte N draws entry N-1. Proof: blind_mage carries
# animation 1 and renders `vex`, which re/fx/animation_map.csv records as client_effect 0 / wire 1.
# Cards are keyed by INDEX; usage is keyed by WIRE, so a card shows the spells at index + 1.
GUARDIANS = {34: "Baekho's cunning", 35: "Chung ryong's rage",
             36: "Ju jak evocation", 37: "Hyun moo revival"}

CSS = """
:root{
  --ground:#0b0d10; --panel:#14181d; --panel2:#191e25; --line:#232a32;
  --ink:#d7dde4; --mute:#7c8896; --dim:#4e5964;
  --ember:#e8873c; --cool:#6fb3c4; --gold:#f0c860;
  --mono:"JetBrains Mono",ui-monospace,Menlo,Consolas,monospace;
  --sans:"IBM Plex Sans",system-ui,-apple-system,Segoe UI,sans-serif;
}
*{box-sizing:border-box}
body{margin:0;background:var(--ground);color:var(--ink);font-family:var(--sans);
     -webkit-font-smoothing:antialiased}
.wrap{max-width:1500px;margin:0 auto;padding:28px 20px 80px}
h1{font-size:19px;font-weight:600;letter-spacing:.01em;margin:0;text-wrap:balance}
.sub{color:var(--mute);font-size:13px;line-height:1.65;max-width:66ch;margin:9px 0 0}
.sub code{font-family:var(--mono);color:var(--ink);font-size:12px}
.sub b{color:var(--ink);font-weight:600}
.bar{position:sticky;top:0;z-index:5;display:flex;flex-wrap:wrap;gap:10px;align-items:center;
     padding:14px 0 13px;margin:20px 0 18px;background:linear-gradient(var(--ground) 78%,transparent);
     border-bottom:1px solid var(--line)}
input[type=search]{flex:1 1 260px;min-width:0;background:var(--panel);border:1px solid var(--line);
  color:var(--ink);font-family:var(--mono);font-size:13px;padding:8px 11px;border-radius:3px}
input[type=search]:focus{outline:2px solid var(--ember);outline-offset:1px}
.chips{display:flex;gap:6px;flex-wrap:wrap}
button.chip{background:var(--panel);border:1px solid var(--line);color:var(--mute);
  font-family:var(--sans);font-size:12px;font-weight:500;letter-spacing:.03em;
  padding:7px 12px;border-radius:3px;cursor:pointer}
button.chip:hover{color:var(--ink);border-color:var(--dim)}
button.chip[aria-pressed=true]{background:var(--ember);border-color:var(--ember);color:#12161a}
button.chip:focus-visible{outline:2px solid var(--gold);outline-offset:2px}
.count{font-family:var(--mono);font-size:12px;color:var(--dim);margin-left:auto;white-space:nowrap;
  font-variant-numeric:tabular-nums}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(178px,1fr));gap:12px}
.cell{background:var(--panel);border:1px solid var(--line);border-radius:4px;padding:9px 9px 4px;
  display:flex;flex-direction:column;gap:7px;min-width:0}
.cell[data-state=guard]{border-color:var(--gold);box-shadow:0 0 0 1px rgba(240,200,96,.22)}
.cell[data-state=free] header b{color:var(--cool)}
.cell header{display:flex;align-items:baseline;gap:8px}
.cell header b{font-family:var(--mono);font-size:15px;font-weight:700;color:var(--ember)}
.wire{font-family:var(--mono);font-size:10.5px;color:var(--cool);letter-spacing:.02em;
  font-variant-numeric:tabular-nums}
.meta{font-family:var(--mono);font-size:10.5px;color:var(--dim);margin-left:auto;
  font-variant-numeric:tabular-nums}
.stage{background:#000;border-radius:2px;height:132px;display:grid;place-items:center;overflow:hidden}
.stage img{max-width:100%;max-height:126px;image-rendering:pixelated}
.none{font-family:var(--mono);font-size:11px;color:var(--dim)}
.badge{font-size:10.5px;font-weight:600;letter-spacing:.04em;color:#12161a;background:var(--gold);
  padding:3px 7px;border-radius:2px;align-self:flex-start}
.uses{list-style:none;margin:0;padding:0 0 5px;display:flex;flex-wrap:wrap;gap:3px}
.uses li{font-size:10.5px;color:var(--mute);background:var(--panel2);border:1px solid var(--line);
  padding:2px 5px;border-radius:2px;line-height:1.4}
.uses li.more{color:var(--dim);border-style:dashed}
.empty{color:var(--dim);font-family:var(--mono);font-size:13px;padding:40px 0}
@media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
"""

JS = """
const cells=[...document.querySelectorAll('.cell')], q=document.getElementById('q'),
      count=document.getElementById('count'), empty=document.getElementById('empty');
let filt='all';
function apply(){
  const t=q.value.trim().toLowerCase(); let n=0;
  for(const c of cells){
    const okF = filt==='all' || c.dataset.state===filt ||
                (filt==='used' && c.dataset.state==='guard');
    const okQ = !t || c.dataset.id===t || c.dataset.wire===t ||
                c.dataset.q.includes(t) || c.dataset.id.startsWith(t);
    const show = okF && okQ;
    c.hidden = !show; if(show) n++;
  }
  count.textContent = n + ' / ' + cells.length + ' shown';
  empty.hidden = n>0;
}
q.addEventListener('input',apply);
for(const b of document.querySelectorAll('.chip')) b.addEventListener('click',()=>{
  filt=b.dataset.f;
  for(const o of document.querySelectorAll('.chip')) o.setAttribute('aria-pressed', String(o===b));
  apply();
});
apply();
"""


def build(out_path):
    gifs = json.load(open(os.path.join(FX, "fx495_gifs.json")))
    usage = json.load(open(os.path.join(FX, "fx495_usage.json")))
    use, lua = usage["use"], usage["lua"]

    cards, n_used = [], 0
    for eid in sorted(int(k) for k in gifs):
        g = gifs[str(eid)]
        wire = eid + 1                                     # what spell_effects.csv / ctx:fx() carry
        spells = use.get(str(wire), []) + [f"{v} (lua)" for v in lua.get(str(wire), [])]
        if spells:
            n_used += 1
        state = "guard" if eid in GUARDIANS else ("used" if spells else "free")
        img = ('<span class="none">no frames</span>' if not g else
               f'<img src="data:image/gif;base64,{g["b64"]}" width="{g["w"]}" height="{g["h"]}"'
               f' alt="effect index {eid}" loading="lazy">')
        meta = f'{g["n"]}f &middot; {g["ow"]}&times;{g["oh"]}' if g else "&mdash;"
        tags = "".join(f"<li>{html.escape(s)}</li>" for s in spells[:14])
        more = f'<li class="more">+{len(spells) - 14} more</li>' if len(spells) > 14 else ""
        badge = f'<span class="badge">{html.escape(GUARDIANS[eid])}</span>' if eid in GUARDIANS else ""
        cards.append(
            f'<article class="cell" data-id="{eid}" data-wire="{wire}" data-state="{state}"'
            f' data-q="{html.escape(" ".join(spells).lower())}">'
            f'<header><b>{eid}</b><span class="wire">csv {wire}</span>'
            f'<span class="meta">{meta}</span></header>'
            f'<div class="stage">{img}</div>{badge}'
            f'<ul class="uses">{tags}{more}</ul></article>')

    n_free = len(gifs) - n_used
    doc = f"""<title>4.95 Effect Table</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600&amp;family=JetBrains+Mono:wght@400;700&amp;display=swap">
<style>{CSS}</style>
<div class="wrap">
<h1>4.95 Effect Table</h1>
<p class="sub">All {len(gifs)} entries of <code>Effect.tbl</code> from the 4.95 client
(<code>Nexon/NextAeon/NexusTK.dat</code>), animated at their real frame delays.</p>
<p class="sub"><b>Two numbers per card, and they are not the same number.</b> The large orange one is
the <b>Effect.tbl index</b> &mdash; the art itself, what the client draws. The blue <code>csv</code>
one is <b>index&nbsp;+&nbsp;1</b>: the value you write into the <code>animation</code> column of
<code>spell_effects.csv</code> or pass to <code>ctx:fx()</code>, because the client loads the table
1-based and the <code>0x29</code> byte N draws entry N&minus;1. Blind carries <code>animation 1</code>
and renders index&nbsp;0, <code>vex</code>. Spell chips are listed on the card they actually draw.</p>
<p class="sub">Ids <b>117&ndash;127</b> are frameless or absent in the 4.83 assets still sitting in
<code>re/fx/</code>, so no earlier matching run could see them.</p>
<div class="bar">
  <input type="search" id="q" placeholder="filter by id or spell name&hellip;" aria-label="Filter effects">
  <div class="chips" role="group" aria-label="Filter by use">
    <button class="chip" data-f="all" aria-pressed="true">All</button>
    <button class="chip" data-f="used" aria-pressed="false">In use ({n_used})</button>
    <button class="chip" data-f="free" aria-pressed="false">Unclaimed ({n_free})</button>
    <button class="chip" data-f="guard" aria-pressed="false">Guardians (4)</button>
  </div>
  <span class="count" id="count"></span>
</div>
<div class="grid" id="grid">{"".join(cards)}</div>
<p class="empty" id="empty" hidden>nothing matches that filter.</p>
</div>
<script>{JS}</script>"""
    open(out_path, "w", encoding="utf-8").write(doc)
    return len(doc), n_used, n_free


if __name__ == "__main__":
    dest = sys.argv[1] if len(sys.argv) > 1 else os.path.join(FX, "fx495_sheet.html")
    size, used, free = build(dest)
    print(f"wrote {dest}  {size / 1e6:.2f} MB  in-use {used}  unclaimed {free}")
