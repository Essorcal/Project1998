#!/usr/bin/env python
"""
NexusTK 4.95 monster sprite <-> name matching tool.

Renders our OWN client's Monster.epf sprites (one thumbnail per look id 0..326)
next to the Nexus Atlas monster list (names/exp/type scraped via Wayback, pre-6.5),
and lets you assign one OR MORE named monsters to each look id in a browser
(the same base sprite can be several monsters - color/level variants like
red/blue/green dog). Saves to  data/monster_mapping.json  in the repo, which the
server reads back.

Run:   python re/monster-matcher/monster_matcher.py
Then:  open http://localhost:8777

Mapping format (v2):  { "version":2, "entries": { "<lookId>": [ {name,color,hp,exp,type,atlas}, ... ] } }
"""
import json, os, sys, http.server, webbrowser
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
PALETTES = load("palettes.json")

def load_mapping():
    if os.path.exists(MAPPING_PATH):
        try:
            with open(MAPPING_PATH, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {"version": 2, "entries": {}}

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
.stat{color:var(--mut);font-size:12px}
.stat b{color:var(--accent)}
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
.lid{font-size:20px;font-weight:700}
.lidsub{font-size:10px;color:var(--mut)}
.variants{display:flex;flex-direction:column;gap:5px}
.vrow{display:flex;flex-direction:column;gap:3px;border-top:1px dashed var(--edge);padding-top:5px}
.vrow:first-child{border-top:none;padding-top:0}
.vline{display:flex;gap:4px;align-items:center}
.vline .name{flex:1;min-width:0}
.vline .num{width:52px}
.vline .rm{padding:5px 8px;background:transparent;border-color:transparent;color:var(--mut);font-size:15px;line-height:1}
.vline .rm:hover{color:var(--warn)}
.vline .colbtn{padding:4px 6px;display:flex;align-items:center;gap:3px;font-size:13px}
.vline .colbtn .colidx{font-size:11px;color:var(--accent);min-width:8px}
#colorpick{position:absolute;z-index:120;display:none;background:#12151b;border:1px solid var(--accent);border-radius:8px;box-shadow:0 10px 30px #000b;padding:8px;width:340px}
#colorpick .cphdr{font-size:11.5px;color:var(--mut);margin-bottom:6px}
#colorpick .cpgrid{display:grid;grid-template-columns:repeat(7,1fr);gap:5px;max-height:340px;overflow:auto}
#colorpick .cpsw{display:flex;flex-direction:column;align-items:center;gap:2px;padding:3px;border:1px solid var(--edge);border-radius:5px;cursor:pointer;background:#1b1f27}
#colorpick .cpsw:hover{border-color:var(--accent)}
#colorpick .cpsw.sel{border-color:var(--accent);background:#22302a}
#colorpick .cpsw span{font-size:9px;color:var(--mut)}
#colorpick canvas,#colorpick .cpcanv canvas{image-rendering:pixelated;max-width:38px;max-height:38px}
.meta{font-size:10.5px;color:var(--mut);min-height:13px;padding-left:2px}
.meta b{color:var(--warn)}
.addv{align-self:flex-start;font-size:12px;padding:4px 9px;color:var(--accent)}
.hidden{display:none}
.legend{font-size:12px;color:var(--mut)}
/* shared searchable picker */
#pick{position:absolute;z-index:100;display:none;max-height:280px;overflow:auto;background:#161a21;border:1px solid var(--accent);border-radius:7px;box-shadow:0 8px 24px #000a;min-width:260px}
#pick .pk{padding:6px 10px;cursor:pointer;display:flex;flex-direction:column;gap:1px;border-bottom:1px solid #222}
#pick .pk:hover,#pick .pk.hi{background:#243b30}
#pick .pk b{font-size:13px}
#pick .pk span{font-size:11px;color:var(--mut)}
#pick .pk.empty{color:var(--mut);cursor:default}
#toast{position:fixed;bottom:18px;right:18px;background:var(--accent);color:#0c1a12;padding:10px 16px;border-radius:8px;font-weight:600;opacity:0;transition:.3s;pointer-events:none;z-index:200}
#toast.show{opacity:1}
</style></head><body>
<header>
  <h1>🐾 NexusTK 4.95 Monster Matcher</h1>
  <span class="stat"><b id="cnt">0</b> ids named · <b id="vcnt">0</b> monsters</span>
  <span class="sp"></span>
  <div class="controls">
    <input id="search" placeholder="filter by look id / name…" style="width:200px">
    <label class="stat"><input type="checkbox" id="onlyUnnamed"> only unnamed</label>
    <button id="btnSave" class="primary">💾 Save to repo</button>
    <button id="btnExport">⬇ Download JSON</button>
  </div>
</header>
<div class="legend" style="padding:8px 16px 0">
  Each sprite is one look id (= <code>0x8000 | id</code> in the 0x07 spawn). A sprite can be <b>several
  monsters</b> — click <b>+ variant</b> for color/level variants (red/blue/green dog…). Type in the name box
  to search Nexus Atlas; picking one auto-fills exp/type. The <b>🎨</b> button shows this sprite in all 20
  recolor palettes — click the one that matches the variant's color (that palette index is the spawn color).
  Auto-saves in your browser; <b>Save to repo</b> writes <code>data/monster_mapping.json</code>.
</div>
<div class="grid" id="grid"></div>
<div id="pick"></div>
<div id="colorpick"></div>
<div id="toast"></div>
<script>
const MONSTERS = __MONSTERS__;
const ATLAS = __ATLAS__;
const PALETTES = __PALETTES__;                 // 20 x 256 x [r,g,b] recolor LUTs
const MON = {}; MONSTERS.forEach(x=>MON[x.id]=x);
const LSKEY = "tk495_monster_map_v2";
// normalize a stored value to an array of variant objects
function toList(v){ if(!v) return []; if(Array.isArray(v)) return v; if(v.name) return [v]; return []; }
let MAP = {};
(function initMap(){
  const server = (__MAPPING__.entries)||{};
  let draft={}; try{draft=JSON.parse(localStorage.getItem(LSKEY)||"{}");}catch(e){}
  const src = Object.keys(server).length?server:draft;
  for(const k in src){ const l=toList(src[k]); if(l.length) MAP[k]=l; }
})();

const esc=s=>(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/"/g,'&quot;');
const numOr=v=>{const n=parseInt(v,10); return isNaN(n)?undefined:n;};
const atlasSorted=[...ATLAS].sort((a,b)=>(a.exp||0)-(b.exp||0));
const byName={}; atlasSorted.forEach(a=>byName[a.name.toLowerCase()]=a);

const grid=document.getElementById('grid');
const cntEl=document.getElementById('cnt'), vcntEl=document.getElementById('vcnt');
function updCount(){
  const ids=Object.keys(MAP).filter(k=>MAP[k]&&MAP[k].length);
  cntEl.textContent=ids.length;
  vcntEl.textContent=ids.reduce((s,k)=>s+MAP[k].length,0);
}
function saveLocal(){ localStorage.setItem(LSKEY,JSON.stringify(MAP)); updCount(); }

function variantRow(v){
  v=v||{};
  const row=document.createElement('div'); row.className='vrow';
  row.innerHTML=`<div class="vline">
      <input class="name" placeholder="monster name…" autocomplete="off" value="${esc(v.name)}">
      <button class="colbtn" title="pick recolor (palette)">🎨<span class="colidx">${v.color??''}</span></button>
      <input class="num hp" type="number" placeholder="hp" title="HP (optional; blank = derived from exp)" value="${v.hp??''}">
      <button class="rm" title="remove variant">×</button>
    </div><div class="meta"></div>`;
  row.dataset.color = (v.color??'');
  row.querySelector('.rm').onclick=()=>{
    const card=row.closest('.card');
    row.remove();
    if(!card.querySelector('.vrow')) card.querySelector('.variants').appendChild(variantRow());
    commitCard(card);
  };
  row.querySelector('.colbtn').onclick=(e)=>{ e.preventDefault(); openColorPicker(row); };
  refreshMeta(row);
  return row;
}
// draw a sprite's palette-indices into a canvas using palette p (or its default when p is null/'')
function drawSprite(canvas, mon, p, scale){
  const idx=Uint8Array.from(atob(mon.idx),c=>c.charCodeAt(0));
  const w=mon.fw, h=mon.fh; const pal=PALETTES[(p==null||p==='')?mon.pal:p]||PALETTES[mon.pal];
  scale=scale||1; canvas.width=w*scale; canvas.height=h*scale;
  const ctx=canvas.getContext('2d'); ctx.imageSmoothingEnabled=false;
  const img=ctx.createImageData(w,h);
  for(let i=0;i<w*h;i++){ const k=idx[i]; const o=i*4;
    if(k){ const c=pal[k]; img.data[o]=c[0]; img.data[o+1]=c[1]; img.data[o+2]=c[2]; img.data[o+3]=255; } }
  // put at 1x then scale
  const tmp=document.createElement('canvas'); tmp.width=w; tmp.height=h; tmp.getContext('2d').putImageData(img,0,0);
  ctx.drawImage(tmp,0,0,w*scale,h*scale);
}
// ---- color / recolor picker ----
let colorRow=null;
function openColorPicker(row){
  colorRow=row; const card=row.closest('.card'); const mon=MON[+card.dataset.id];
  const cp=document.getElementById('colorpick');
  if(!mon || !mon.idx){ cp.innerHTML='<div class="cphdr">no sprite indices for this id</div>'; }
  else {
    let html='<div class="cphdr">Pick the recolor (palette) for this variant — click a swatch. '+
             '<b>Default</b> = the sprite\'s own palette.</div><div class="cpgrid">';
    html+=`<div class="cpsw" data-p=""><div class="cpcanv" id="cpd"></div><span>default (${mon.pal})</span></div>`;
    for(let p=0;p<PALETTES.length;p++) html+=`<div class="cpsw" data-p="${p}"><canvas class="cpc" data-p="${p}"></canvas><span>${p}</span></div>`;
    html+='</div>'; cp.innerHTML=html;
    // render swatches
    const dc=document.createElement('canvas'); drawSprite(dc,mon,'',2); document.getElementById('cpd').appendChild(dc);
    cp.querySelectorAll('canvas.cpc').forEach(cv=>drawSprite(cv,mon,+cv.dataset.p,2));
    const cur=row.dataset.color;
    cp.querySelectorAll('.cpsw').forEach(sw=>{ if((sw.dataset.p||'')===(cur||'')) sw.classList.add('sel'); });
  }
  // position near the button
  const r=row.querySelector('.colbtn').getBoundingClientRect();
  cp.style.left=Math.min(r.left+window.scrollX, window.scrollX+innerWidth-360)+'px';
  cp.style.top=(r.bottom+window.scrollY+2)+'px'; cp.style.display='block';
}
function closeColorPicker(){ const cp=document.getElementById('colorpick'); cp.style.display='none'; colorRow=null; }
document.getElementById('colorpick').addEventListener('mousedown',e=>{
  const sw=e.target.closest('.cpsw'); if(!sw||!colorRow) return; e.preventDefault();
  const p=sw.dataset.p;
  colorRow.dataset.color=p;
  colorRow.querySelector('.colidx').textContent = p===''? '' : p;
  commitCard(colorRow.closest('.card')); closeColorPicker();
});
document.addEventListener('mousedown',e=>{ const cp=document.getElementById('colorpick');
  if(cp.style.display==='block' && !e.target.closest('#colorpick') && !e.target.closest('.colbtn')) closeColorPicker(); });
function refreshMeta(row){
  const nm=row.querySelector('.name').value.trim();
  const a=byName[nm.toLowerCase()];
  row.querySelector('.meta').innerHTML = a? `Atlas: exp <b>${a.exp}</b> · ${a.type||'?'} · ${a.page}`
    : (nm? '<i>custom name (not in Atlas)</i>':'');
}
function commitCard(card){
  const id=card.dataset.id;
  const vs=[...card.querySelectorAll('.vrow')].map(r=>{
    const name=r.querySelector('.name').value.trim(); if(!name) return null;
    const a=byName[name.toLowerCase()];
    return {name, color:numOr(r.dataset.color),          // palette/recolor from the 🎨 picker
            hp:numOr(r.querySelector('.hp').value),
            exp:a?a.exp:undefined,                        // exp is auto from Atlas
            type:a?a.type:undefined, atlas:a?a.name:undefined};
  }).filter(Boolean);
  if(vs.length) MAP[id]=vs; else delete MAP[id];
  card.classList.toggle('named',vs.length>0);
  saveLocal();
}
function card(mon){
  const id=String(mon.id), list=MAP[id]||[];
  const el=document.createElement('div'); el.className='card'+(list.length?' named':''); el.dataset.id=id;
  const look='0x'+(0x8000|mon.id).toString(16);
  el.innerHTML=`<div class="top">
      <div class="thumb">${mon.img?`<img src="${mon.img}">`:'<span class=stat>no sprite</span>'}</div>
      <div class="idcol"><div class="lid">${mon.id}</div><div class="lidsub">look ${look} · pal ${mon.pal}</div></div>
    </div><div class="variants"></div>
    <button class="addv">+ variant</button>`;
  const vc=el.querySelector('.variants');
  (list.length?list:[{}]).forEach(v=>vc.appendChild(variantRow(v)));
  el.querySelector('.addv').onclick=()=>{ const r=variantRow(); vc.appendChild(r); r.querySelector('.name').focus(); };
  el.addEventListener('change',e=>{ if(e.target.matches('.name,.color,.hp,.exp')){ commitCard(el); refreshMeta(e.target.closest('.vrow')); }});
  return el;
}
MONSTERS.forEach(mn=>grid.appendChild(card(mn)));
updCount();

// ---- shared searchable picker ----
const PICK=document.getElementById('pick'); let pickInput=null, pickIdx=-1, pickItems=[];
function openPick(input){ pickInput=input; fillPick(input.value); position(); PICK.style.display='block'; }
function position(){ if(!pickInput)return; const r=pickInput.getBoundingClientRect();
  PICK.style.left=(r.left+window.scrollX)+'px'; PICK.style.top=(r.bottom+window.scrollY+2)+'px'; PICK.style.width=Math.max(r.width,260)+'px'; }
function fillPick(q){
  q=(q||'').toLowerCase();
  pickItems=atlasSorted.filter(a=>!q||a.name.toLowerCase().includes(q)).slice(0,80);
  pickIdx=-1;
  PICK.innerHTML=pickItems.length? pickItems.map((a,i)=>
    `<div class="pk" data-i="${i}"><b>${esc(a.name)}</b><span>exp ${a.exp??'?'} · ${a.type||'?'} · ${a.page}</span></div>`).join('')
    : '<div class="pk empty">no Atlas match — your text is kept as a custom name</div>';
}
function choose(i){
  const a=pickItems[i]; if(!a||!pickInput) return;
  pickInput.value=a.name;
  const row=pickInput.closest('.vrow');
  if(!row.querySelector('.exp').value && a.exp!=null) row.querySelector('.exp').value=a.exp;
  commitCard(pickInput.closest('.card')); refreshMeta(row);
  hidePick();
}
function hidePick(){ PICK.style.display='none'; pickInput=null; }
PICK.addEventListener('mousedown',e=>{ const pk=e.target.closest('.pk'); if(pk&&pk.dataset.i!=null){ e.preventDefault(); choose(+pk.dataset.i);} });
document.addEventListener('focusin',e=>{ if(e.target.classList.contains('name')) openPick(e.target); });
document.addEventListener('input',e=>{ if(e.target===pickInput){ fillPick(e.target.value); position(); }});
document.addEventListener('focusout',e=>{ if(e.target.classList.contains('name')) setTimeout(()=>{ if(pickInput===e.target) hidePick(); },120); });
document.addEventListener('keydown',e=>{
  if(!pickInput||PICK.style.display==='none') return;
  if(e.key==='ArrowDown'||e.key==='ArrowUp'){ e.preventDefault();
    pickIdx=Math.max(0,Math.min(pickItems.length-1,pickIdx+(e.key==='ArrowDown'?1:-1)));
    [...PICK.children].forEach((c,i)=>c.classList.toggle('hi',i===pickIdx));
    const hi=PICK.children[pickIdx]; if(hi)hi.scrollIntoView({block:'nearest'});
  } else if(e.key==='Enter'){ if(pickIdx>=0){ e.preventDefault(); choose(pickIdx);} else hidePick(); }
  else if(e.key==='Escape'){ hidePick(); }
});
window.addEventListener('scroll',()=>{ if(pickInput) position(); },true);

// ---- filter ----
const search=document.getElementById('search'), onlyU=document.getElementById('onlyUnnamed');
function applyFilter(){ const q=search.value.trim().toLowerCase();
  [...grid.children].forEach(c=>{ const id=c.dataset.id;
    const names=(MAP[id]||[]).map(v=>v.name.toLowerCase()).join(' ');
    let ok=true; if(q) ok=id===q||id.includes(q)||names.includes(q);
    if(ok&&onlyU.checked) ok=!(MAP[id]&&MAP[id].length);
    c.classList.toggle('hidden',!ok);
  });
}
search.addEventListener('input',applyFilter); onlyU.addEventListener('change',applyFilter);

function toast(msg,warn){const t=document.getElementById('toast');t.textContent=msg;t.style.background=warn?'#e0a44a':'';t.classList.add('show');setTimeout(()=>t.classList.remove('show'),1900);}
document.getElementById('btnSave').onclick=async()=>{
  try{ const r=await fetch('/save',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({version:2,entries:MAP})});
    const j=await r.json(); toast(j.ok?`Saved ${j.monsters} monsters across ${j.ids} ids`:'Save failed',!j.ok);
  }catch(e){toast('Save failed: '+e,true);}
};
document.getElementById('btnExport').onclick=()=>{
  const blob=new Blob([JSON.stringify({version:2,entries:MAP},null,1)],{type:'application/json'});
  const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='monster_mapping.json';a.click();
};
</script></body></html>"""

class H(http.server.BaseHTTPRequestHandler):
    def _send(self, code, body, ctype="text/html; charset=utf-8"):
        b = body.encode("utf-8") if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def log_message(self, *a):
        pass

    def do_GET(self):
        if urlparse(self.path).path in ("/", "/index.html"):
            page = (PAGE
                    .replace("__MONSTERS__", json.dumps(MONSTERS))
                    .replace("__ATLAS__", json.dumps(ATLAS))
                    .replace("__PALETTES__", json.dumps(PALETTES))
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
                json.dump({"version": 2, "entries": entries}, f, indent=1)
            monsters = sum(len(v) for v in entries.values())
            self._send(200, json.dumps({"ok": True, "ids": len(entries), "monsters": monsters}), "application/json")
        except Exception as e:
            self._send(200, json.dumps({"ok": False, "error": str(e)}), "application/json")

def main():
    http.server.ThreadingHTTPServer.allow_reuse_address = True
    with http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H) as httpd:
        url = f"http://localhost:{PORT}"
        print(f"Monster Matcher: {len(MONSTERS)} sprites, {len(ATLAS)} Atlas monsters")
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
