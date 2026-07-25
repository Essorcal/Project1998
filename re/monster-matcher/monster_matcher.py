#!/usr/bin/env python
"""
NexusTK 4.95 monster sprite <-> name/color matching tool.

Left  = truth: our client's Monster.epf sprite for each look id (0..326).
Right = the real, correctly-COLORED Nexus Atlas monster art (GIFs scraped from
Wayback, pre-6.5). Each look id can hold several variants (red/blue/green dog):
you pick them from a popover that shows the actual Atlas art, pre-ranked by shape
similarity to the sprite. Picking an Atlas monster records its name, colour, exp,
type and its real RGB palette — no more guessing palette indices.

Run:   python re/monster-matcher/monster_matcher.py   ->  http://localhost:8777
Save writes  data/monster_mapping.json  (read back by the server).

Mapping v3:  { "version":3, "entries": {
    "<lookId>": [ {name,color,file,exp,type,hp,pal:[[r,g,b]...]}, ... ] } }
"""
import json, os, http.server, webbrowser
from urllib.parse import urlparse

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
MAPPING_PATH = os.path.join(REPO, "data", "monster_mapping.json")
PORT = 8777

def load(name):
    with open(os.path.join(HERE, name), "r", encoding="utf-8") as f:
        return json.load(f)

MONSTERS = load("monsters.json")
ATLAS = load("atlas.json")
ATLAS_REF = load("atlas_ref.json")   # {refs:[...], cand:{lookId:[{i,s}]}}

def load_mapping():
    if os.path.exists(MAPPING_PATH):
        try:
            with open(MAPPING_PATH, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {"version": 3, "entries": {}}

PAGE = r"""<!doctype html><html><head><meta charset="utf-8">
<title>NexusTK 4.95 — Monster Matcher</title>
<style>
:root{--bg:#1b1d24;--card:#262a33;--card2:#2e333d;--edge:#3a4150;--txt:#e6e9ef;--mut:#9aa3b2;--accent:#66d9a0;--warn:#e0a44a}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.4 system-ui,Segoe UI,sans-serif}
header{position:sticky;top:0;z-index:20;background:#14161c;border-bottom:1px solid var(--edge);padding:10px 16px;display:flex;gap:14px;align-items:center;flex-wrap:wrap}
header h1{font-size:15px;margin:0;font-weight:600}
header .sp{flex:1}
button{background:var(--card2);color:var(--txt);border:1px solid var(--edge);border-radius:6px;padding:7px 12px;cursor:pointer;font-size:13px}
button.primary{background:var(--accent);color:#0c1a12;border-color:var(--accent);font-weight:600}
button:hover{filter:brightness(1.12)}
input{background:#1f232b;color:var(--txt);border:1px solid var(--edge);border-radius:5px;padding:6px 8px;font-size:13px}
.stat{color:var(--mut);font-size:12px}.stat b{color:var(--accent)}
.controls{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.grid{display:grid;grid-template-columns:repeat(6,1fr);gap:10px;padding:14px}
@media(max-width:1100px){.grid{grid-template-columns:repeat(4,1fr)}}
@media(max-width:760px){.grid{grid-template-columns:repeat(2,1fr)}}
.card{background:var(--card);border:1px solid var(--edge);border-radius:8px;padding:8px;display:flex;flex-direction:column;gap:6px}
.card.named{border-color:var(--accent);background:#22302a}
.card .top{display:flex;gap:8px;align-items:center}
.thumb{width:76px;height:76px;background:#12141a;border:1px solid var(--edge);border-radius:6px;display:flex;align-items:center;justify-content:center;flex:0 0 auto}
.thumb img{image-rendering:pixelated;max-width:72px;max-height:72px}
.idcol{display:flex;flex-direction:column;gap:2px}
.lid{font-size:20px;font-weight:700}.lidsub{font-size:10px;color:var(--mut)}
.variants{display:flex;flex-direction:column;gap:4px}
.vrow{display:flex;gap:6px;align-items:center;border-top:1px dashed var(--edge);padding-top:4px}
.vrow:first-child{border-top:none;padding-top:0}
.vrow .gif{width:34px;height:34px;background:#12141a;border:1px solid var(--edge);border-radius:5px;display:flex;align-items:center;justify-content:center;flex:0 0 auto}
.vrow .gif img{image-rendering:pixelated;max-width:32px;max-height:32px}
.vrow .vmeta{flex:1;min-width:0}
.vrow .vname{font-size:12px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.vrow .vsub{font-size:10px;color:var(--mut)}
.vrow .swatch{display:inline-block;width:9px;height:9px;border-radius:2px;border:1px solid #0006;vertical-align:middle;margin-right:3px}
.vrow .num{width:46px}
.vrow .rm{padding:4px 7px;background:transparent;border-color:transparent;color:var(--mut);font-size:15px;line-height:1}
.vrow .rm:hover{color:var(--warn)}
.addv{align-self:flex-start;font-size:12px;padding:4px 9px;color:var(--accent)}
.hidden{display:none}
.legend{font-size:12px;color:var(--mut)}
.legend code{color:var(--txt)}
/* variant picker popover */
#vp{position:absolute;z-index:120;display:none;background:#12151b;border:1px solid var(--accent);border-radius:9px;box-shadow:0 12px 34px #000c;width:360px;max-height:70vh;display:none;flex-direction:column}
#vp .vphd{padding:8px 10px;border-bottom:1px solid var(--edge);font-size:11.5px;color:var(--mut)}
#vp .vphd input{width:100%;margin-top:6px}
#vp .vplist{overflow:auto;padding:6px}
#vp .sec{font-size:10px;color:var(--mut);text-transform:uppercase;letter-spacing:.05em;margin:6px 4px 3px}
#vp .opt{display:flex;gap:8px;align-items:center;padding:5px 6px;border-radius:6px;cursor:pointer}
#vp .opt:hover,#vp .opt.hi{background:#243b30}
#vp .opt .gif{width:38px;height:38px;background:#0d0f14;border:1px solid var(--edge);border-radius:5px;display:flex;align-items:center;justify-content:center;flex:0 0 auto}
#vp .opt .gif img{image-rendering:pixelated;max-width:36px;max-height:36px}
#vp .opt .txt{flex:1;min-width:0}
#vp .opt .on{font-size:12.5px;font-weight:600}
#vp .opt .os{font-size:10.5px;color:var(--mut)}
#vp .opt .score{font-size:10px;color:var(--accent);flex:0 0 auto}
#vp .opt.picked{outline:1px solid var(--accent);background:#1c2a22}
#vp .none{color:var(--mut);font-size:12px;padding:10px}
#toast{position:fixed;bottom:18px;right:18px;background:var(--accent);color:#0c1a12;padding:10px 16px;border-radius:8px;font-weight:600;opacity:0;transition:.3s;pointer-events:none;z-index:200}
#toast.show{opacity:1}
</style></head><body>
<header>
  <h1>🐾 NexusTK 4.95 Monster Matcher</h1>
  <span class="stat"><b id="cnt">0</b> ids named · <b id="vcnt">0</b> variants</span>
  <span class="sp"></span>
  <div class="controls">
    <input id="search" placeholder="filter look id / name…" style="width:190px">
    <label class="stat"><input type="checkbox" id="onlyUnnamed"> only unnamed</label>
    <label class="stat"><input type="checkbox" id="onlyCand" checked> only w/ suggestions</label>
    <button id="btnSave" class="primary">💾 Save to repo</button>
    <button id="btnExport">⬇ Download JSON</button>
  </div>
</header>
<div class="legend" style="padding:8px 16px 0">
  Left thumbnail = the client's <b>Monster.epf</b> sprite for one look id (<code>0x8000 | id</code> in the 0x07 spawn).
  Click <b>+ add variant</b> to open the picker: it lists the <b>real, correctly-coloured Nexus Atlas art</b>,
  pre-ranked by shape match to this sprite. Pick every colour/level variant (red/blue/green…). Each records the
  monster's name, colour, exp/type and its real palette. Auto-saves locally; <b>Save to repo</b> writes
  <code>data/monster_mapping.json</code>.
</div>
<div class="grid" id="grid"></div>
<div id="vp"></div>
<div id="toast"></div>
<script>
const MONSTERS = __MONSTERS__;
const REFS = __REFS__;            // atlas reference art [{file,name,color,w,h,pal,gif,exp,mtype}]
const CAND = __CAND__;            // {lookId:[{i,s}]}  shape-ranked candidate indices into REFS
const MON = {}; MONSTERS.forEach(x=>MON[x.id]=x);
const REFBYFILE = {}; REFS.forEach((r,i)=>{r._i=i; REFBYFILE[r.file]=r;});
const LSKEY = "tk495_monster_map_v3";
function toList(v){ if(!v) return []; if(Array.isArray(v)) return v; if(v.name) return [v]; return []; }
let MAP = {};
(function initMap(){
  const server=(__MAPPING__.entries)||{};
  let draft={}; try{draft=JSON.parse(localStorage.getItem(LSKEY)||"{}");}catch(e){}
  const src=Object.keys(server).length?server:draft;
  for(const k in src){ const l=toList(src[k]); if(l.length) MAP[k]=l; }
})();

const esc=s=>(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/"/g,'&quot;');
const numOr=v=>{const n=parseInt(v,10);return isNaN(n)?undefined:n;};
const rgb=c=>c?`rgb(${c[0]},${c[1]},${c[2]})`:'transparent';
// dominant "colour" swatch = 2nd palette entry (1st is usually the dark outline)
function keyColor(pal){ if(!pal||!pal.length) return null; return pal.length>1?pal[1]:pal[0]; }

const grid=document.getElementById('grid');
const cntEl=document.getElementById('cnt'), vcntEl=document.getElementById('vcnt');
function updCount(){
  const ids=Object.keys(MAP).filter(k=>MAP[k]&&MAP[k].length);
  cntEl.textContent=ids.length;
  vcntEl.textContent=ids.reduce((s,k)=>s+MAP[k].length,0);
}
function saveLocal(){ localStorage.setItem(LSKEY,JSON.stringify(MAP)); updCount(); }

// ---- variant row (shows the real Atlas art) ----
function variantRow(card,v){
  const ref=REFBYFILE[v.file];
  const row=document.createElement('div'); row.className='vrow'; row.dataset.file=v.file||'';
  const kc=keyColor(v.pal||(ref&&ref.pal));
  row.innerHTML=`<div class="gif">${ref?`<img src="${ref.gif}">`:''}</div>
    <div class="vmeta">
      <div class="vname">${esc(v.name)}</div>
      <div class="vsub"><span class="swatch" style="background:${rgb(kc)}"></span>${v.color?esc(v.color)+' · ':''}${v.exp!=null?'exp '+v.exp:'exp ?'}${v.type?' · '+esc(v.type):''}</div>
    </div>
    <input class="num hp" type="number" placeholder="hp" title="HP override (optional; blank = derived from exp)" value="${v.hp??''}">
    <button class="rm" title="remove">×</button>`;
  row.querySelector('.hp').onchange=()=>commitCard(card);
  row.querySelector('.rm').onclick=()=>{ row.remove(); commitCard(card); };
  return row;
}
function commitCard(card){
  const id=card.dataset.id;
  const vs=[...card.querySelectorAll('.vrow')].map(r=>{
    const ref=REFBYFILE[r.dataset.file]; if(!ref) return null;
    return {name:ref.name, color:ref.color||undefined, file:ref.file,
            exp:ref.exp!=null?ref.exp:undefined, type:ref.mtype||undefined,
            hp:numOr(r.querySelector('.hp').value), pal:ref.pal};
  }).filter(Boolean);
  if(vs.length) MAP[id]=vs; else delete MAP[id];
  card.classList.toggle('named',vs.length>0);
  saveLocal();
}
function renderVariants(card){
  const id=card.dataset.id, vc=card.querySelector('.variants'); vc.innerHTML='';
  (MAP[id]||[]).forEach(v=>vc.appendChild(variantRow(card,v)));
}
function card(mon){
  const id=String(mon.id), list=MAP[id]||[];
  const el=document.createElement('div'); el.className='card'+(list.length?' named':''); el.dataset.id=id;
  el.dataset.hascand=(CAND[id]&&CAND[id].length)?'1':'0';
  const look='0x'+(0x8000|mon.id).toString(16);
  el.innerHTML=`<div class="top">
      <div class="thumb">${mon.img?`<img src="${mon.img}">`:'<span class=stat>no sprite</span>'}</div>
      <div class="idcol"><div class="lid">${mon.id}</div><div class="lidsub">look ${look} · pal ${mon.pal}</div></div>
    </div><div class="variants"></div>
    <button class="addv">+ add variant</button>`;
  renderVariants(el);
  el.querySelector('.addv').onclick=(e)=>openVP(el,e.target);
  return el;
}
MONSTERS.forEach(mn=>grid.appendChild(card(mn)));
updCount();

// ---- variant picker popover ----
const VP=document.getElementById('vp'); let vpCard=null, vpItems=[], vpIdx=-1;
function openVP(cardEl,anchor){
  vpCard=cardEl;
  VP.innerHTML=`<div class="vphd">Add a variant to <b>look ${cardEl.dataset.id}</b> — pick the matching Atlas monster (real colours).
    <input id="vpq" placeholder="search all ${REFS.length} Atlas monsters by name…" autocomplete="off"></div>
    <div class="vplist" id="vplist"></div>`;
  fillVP('');
  const r=anchor.getBoundingClientRect();
  VP.style.left=Math.min(r.left+window.scrollX, window.scrollX+innerWidth-372)+'px';
  VP.style.top=(r.bottom+window.scrollY+3)+'px'; VP.style.display='flex';
  const q=document.getElementById('vpq'); q.oninput=()=>{fillVP(q.value);}; q.focus();
}
function closeVP(){ VP.style.display='none'; vpCard=null; vpItems=[]; vpIdx=-1; }
function pickedFiles(){ return new Set((MAP[vpCard.dataset.id]||[]).map(v=>v.file)); }
function optHTML(r,score,picked){
  return `<div class="opt${picked?' picked':''}" data-file="${esc(r.file)}">
    <div class="gif"><img src="${r.gif}"></div>
    <div class="txt"><div class="on">${esc(r.name)}</div>
      <div class="os">${r.color?esc(r.color)+' · ':''}${r.exp!=null?'exp '+r.exp:'exp ?'}${r.mtype?' · '+esc(r.mtype):''} · ${r.w}×${r.h}</div></div>
    ${score!=null?`<div class="score">${(score*100).toFixed(0)}%</div>`:''}</div>`;
}
function fillVP(q){
  if(!vpCard) return;
  q=(q||'').trim().toLowerCase();
  const pf=pickedFiles(); const list=document.getElementById('vplist');
  const matches=r=>!q||r.name.toLowerCase().includes(q)||r.file.toLowerCase().includes(q);
  const cs=(CAND[vpCard.dataset.id]||[]);
  const suggested=cs.map(c=>{const r=REFS[c.i]; r._score=c.s; return r;}).filter(matches);
  const sugFiles=new Set(suggested.map(r=>r.file));
  const rest=REFS.filter(r=>!sugFiles.has(r.file)&&matches(r))
    .slice().sort((a,b)=>a.name.localeCompare(b.name));
  vpItems=suggested.concat(rest);
  let html='';
  if(suggested.length) html+='<div class="sec">suggested for this shape</div>'+
    suggested.map(r=>optHTML(r,r._score,pf.has(r.file))).join('');
  html+=`<div class="sec">${q?'other matches':'all atlas monsters'} (${rest.length})</div>`+
    (rest.length? rest.map(r=>optHTML(r,null,pf.has(r.file))).join('')
      : (suggested.length?'':'<div class="none">no Atlas monster matches</div>'));
  list.innerHTML=html;
  vpIdx=-1;
}
function toggleVariant(file){
  const id=vpCard.dataset.id; const ref=REFBYFILE[file]; if(!ref) return;
  const cur=MAP[id]||[]; const at=cur.findIndex(v=>v.file===file);
  if(at>=0){ cur.splice(at,1); if(!cur.length) delete MAP[id]; else MAP[id]=cur; }
  else { cur.push({name:ref.name,color:ref.color||undefined,file:ref.file,
                   exp:ref.exp!=null?ref.exp:undefined,type:ref.mtype||undefined,pal:ref.pal});
         MAP[id]=cur; }
  vpCard.classList.toggle('named',(MAP[id]||[]).length>0);
  renderVariants(vpCard); saveLocal();
  // refresh popover picked-state
  const q=document.getElementById('vpq'); fillVP(q?q.value:'');
}
VP.addEventListener('mousedown',e=>{ const o=e.target.closest('.opt'); if(o){ e.preventDefault(); toggleVariant(o.dataset.file);} });
document.addEventListener('mousedown',e=>{ if(VP.style.display!=='none' && !e.target.closest('#vp') && !e.target.classList.contains('addv')) closeVP(); });
document.addEventListener('keydown',e=>{
  if(VP.style.display==='none') return;
  if(e.key==='Escape'){ closeVP(); return; }
  if(e.key==='ArrowDown'||e.key==='ArrowUp'){ e.preventDefault();
    vpIdx=Math.max(0,Math.min(vpItems.length-1,vpIdx+(e.key==='ArrowDown'?1:-1)));
    [...VP.querySelectorAll('.opt')].forEach((c,i)=>c.classList.toggle('hi',i===vpIdx));
    const hi=VP.querySelectorAll('.opt')[vpIdx]; if(hi)hi.scrollIntoView({block:'nearest'});
  } else if(e.key==='Enter'){ if(vpIdx>=0 && vpItems[vpIdx]){ e.preventDefault(); toggleVariant(vpItems[vpIdx].file);} }
});
// page scroll (not the popover's own internal list, which is a normal thing to scroll
// while browsing the full Atlas roster) invalidates the popover's anchored position.
window.addEventListener('scroll',()=>{ if(vpCard) closeVP(); });

// ---- filter ----
const search=document.getElementById('search'), onlyU=document.getElementById('onlyUnnamed'), onlyC=document.getElementById('onlyCand');
function applyFilter(){ const q=search.value.trim().toLowerCase();
  [...grid.children].forEach(c=>{ const id=c.dataset.id;
    const names=(MAP[id]||[]).map(v=>(v.name||'').toLowerCase()).join(' ');
    let ok=true; if(q) ok=id===q||id.includes(q)||names.includes(q);
    if(ok&&onlyU.checked) ok=!(MAP[id]&&MAP[id].length);
    if(ok&&onlyC.checked) ok=c.dataset.hascand==='1'||(MAP[id]&&MAP[id].length);
    c.classList.toggle('hidden',!ok);
  });
}
search.addEventListener('input',applyFilter); onlyU.addEventListener('change',applyFilter); onlyC.addEventListener('change',applyFilter);
applyFilter();

function toast(msg,warn){const t=document.getElementById('toast');t.textContent=msg;t.style.background=warn?'#e0a44a':'';t.classList.add('show');setTimeout(()=>t.classList.remove('show'),1900);}
document.getElementById('btnSave').onclick=async()=>{
  try{ const r=await fetch('/save',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({version:3,entries:MAP})});
    const j=await r.json(); toast(j.ok?`Saved ${j.monsters} variants across ${j.ids} ids`:'Save failed',!j.ok);
  }catch(e){toast('Save failed: '+e,true);}
};
document.getElementById('btnExport').onclick=()=>{
  const blob=new Blob([JSON.stringify({version:3,entries:MAP},null,1)],{type:'application/json'});
  const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='monster_mapping.json';a.click();
};
</script></body></html>"""

class H(http.server.BaseHTTPRequestHandler):
    def _send(self, code, body, ctype="text/html; charset=utf-8"):
        b = body.encode("utf-8") if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(b)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(b)

    def log_message(self, *a):
        pass

    def do_GET(self):
        if urlparse(self.path).path in ("/", "/index.html"):
            page = (PAGE
                    .replace("__MONSTERS__", json.dumps(MONSTERS))
                    .replace("__REFS__", json.dumps(ATLAS_REF["refs"]))
                    .replace("__CAND__", json.dumps(ATLAS_REF["cand"]))
                    .replace("__MAPPING__", json.dumps(load_mapping())))
            self._send(200, page)
        else:
            self._send(404, "not found")

    def do_POST(self):
        if urlparse(self.path).path != "/save":
            return self._send(404, "not found")
        n = int(self.headers.get("Content-Length", 0))
        try:
            data = json.loads(self.rfile.read(n).decode("utf-8"))
            entries = {}
            for k, v in (data.get("entries") or {}).items():
                lst = v if isinstance(v, list) else ([v] if v and v.get("name") else [])
                lst = [x for x in lst if x and x.get("name")]
                if lst:
                    entries[k] = lst
            os.makedirs(os.path.dirname(MAPPING_PATH), exist_ok=True)
            with open(MAPPING_PATH, "w", encoding="utf-8") as f:
                json.dump({"version": 3, "entries": entries}, f, indent=1)
            monsters = sum(len(v) for v in entries.values())
            self._send(200, json.dumps({"ok": True, "ids": len(entries), "monsters": monsters}), "application/json")
        except Exception as e:
            self._send(200, json.dumps({"ok": False, "error": str(e)}), "application/json")

def main():
    http.server.ThreadingHTTPServer.allow_reuse_address = True
    with http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H) as httpd:
        url = f"http://localhost:{PORT}"
        print(f"Monster Matcher: {len(MONSTERS)} sprites, {len(ATLAS_REF['refs'])} Atlas ref images")
        print(f"  -> open {url}")
        print(f"  -> Save writes {MAPPING_PATH}")
        print("  Ctrl-C to stop.")
        try:
            webbrowser.open(url)
        except Exception:
            pass
        httpd.serve_forever()

if __name__ == "__main__":
    main()
