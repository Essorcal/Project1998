#!/usr/bin/env python
"""
NexusTK 4.95 monster sprite <-> name matching tool.

Renders our OWN client's Monster.epf sprites (one thumbnail per look id 0..326)
next to the Nexus Atlas monster list (names/exp/type scraped via Wayback, pre-6.5),
and lets you assign a name + stats to each look id in a browser. Saves the result
to  data/monster_mapping.json  in the repo, which the server reads back.

Run:   python tools/monster-matcher/monster_matcher.py
Then:  open http://localhost:8777  and start matching. Click "Save" when done
       (it also auto-saves to your browser as you go).

Data files next to this script:
  monsters.json  = [{id, img(dataURI), pal, start, w, h}, ...]  (our sprites)
  atlas.json     = [{name, exp, type, page, snap}, ...]         (Nexus Atlas)
Output:
  <repo>/data/monster_mapping.json
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

def load_mapping():
    if os.path.exists(MAPPING_PATH):
        try:
            with open(MAPPING_PATH, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {"version": 1, "entries": {}}

PAGE = r"""<!doctype html><html><head><meta charset="utf-8">
<title>NexusTK 4.95 — Monster Matcher</title>
<style>
:root{--bg:#1b1d24;--card:#262a33;--card2:#2e333d;--edge:#3a4150;--txt:#e6e9ef;--mut:#9aa3b2;--accent:#66d9a0;--warn:#e0a44a}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.4 system-ui,Segoe UI,sans-serif}
header{position:sticky;top:0;z-index:10;background:#14161c;border-bottom:1px solid var(--edge);padding:10px 16px;display:flex;gap:14px;align-items:center;flex-wrap:wrap}
header h1{font-size:15px;margin:0;font-weight:600}
header .sp{flex:1}
button{background:var(--card2);color:var(--txt);border:1px solid var(--edge);border-radius:6px;padding:7px 12px;cursor:pointer;font-size:13px}
button.primary{background:var(--accent);color:#0c1a12;border-color:var(--accent);font-weight:600}
button:hover{filter:brightness(1.12)}
input,select{background:#1f232b;color:var(--txt);border:1px solid var(--edge);border-radius:5px;padding:6px 8px;font-size:13px}
.stat{color:var(--mut);font-size:12px}
.stat b{color:var(--accent)}
.controls{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:10px;padding:14px}
.card{background:var(--card);border:1px solid var(--edge);border-radius:8px;padding:8px;display:flex;flex-direction:column;gap:6px}
.card.named{border-color:var(--accent);background:#22302a}
.card .top{display:flex;gap:8px;align-items:center}
.thumb{width:72px;height:72px;background:#12141a;border:1px solid var(--edge);border-radius:6px;display:flex;align-items:center;justify-content:center;flex:0 0 auto}
.thumb img{image-rendering:pixelated;max-width:68px;max-height:68px}
.idcol{display:flex;flex-direction:column;gap:2px}
.lid{font-size:20px;font-weight:700}
.lidsub{font-size:10px;color:var(--mut)}
.card input.name{width:100%}
.row2{display:flex;gap:6px}
.row2 input{width:100%}
.meta{font-size:11px;color:var(--mut);min-height:14px}
.meta b{color:var(--warn)}
.hidden{display:none}
.legend{font-size:12px;color:var(--mut)}
kbd{background:#333;border-radius:3px;padding:1px 5px;border:1px solid #555;font-size:11px}
#toast{position:fixed;bottom:18px;right:18px;background:var(--accent);color:#0c1a12;padding:10px 16px;border-radius:8px;font-weight:600;opacity:0;transition:.3s;pointer-events:none}
#toast.show{opacity:1}
</style></head><body>
<header>
  <h1>🐾 NexusTK 4.95 Monster Matcher</h1>
  <span class="stat"><b id="cnt">0</b> / __N__ named</span>
  <span class="sp"></span>
  <div class="controls">
    <input id="search" placeholder="filter by look id / name…" style="width:200px">
    <label class="stat"><input type="checkbox" id="onlyUnnamed"> only unnamed</label>
    <button id="btnSave" class="primary">💾 Save to repo</button>
    <button id="btnExport">⬇ Download JSON</button>
  </div>
</header>
<div class="legend" style="padding:8px 16px 0">
  Type a name (autocomplete = Nexus Atlas, sorted by exp). Picking an Atlas name auto-fills exp/type.
  Everything auto-saves in your browser; <b>Save to repo</b> writes <code>data/monster_mapping.json</code> for the server.
  Sprites are our real <code>Monster.epf</code> at look id = Monster.tbl index.
</div>
<div class="grid" id="grid"></div>
<datalist id="atlasList"></datalist>
<div id="toast"></div>
<script>
const MONSTERS = __MONSTERS__;
const ATLAS = __ATLAS__;
let MAP = __MAPPING__.entries || {};
const LSKEY = "tk495_monster_map";
// merge any localStorage draft
try{const d=JSON.parse(localStorage.getItem(LSKEY)||"{}"); MAP={...d,...MAP};}catch(e){}

// atlas datalist (sorted by exp asc)
const atlasSorted=[...ATLAS].sort((a,b)=>(a.exp||0)-(b.exp||0));
const dl=document.getElementById('atlasList');
const byName={};
atlasSorted.forEach(a=>{byName[a.name.toLowerCase()]=a;
  const o=document.createElement('option');
  o.value=a.name; o.label=`exp ${a.exp??'?'} · ${a.type||'?'} · ${a.page}`;
  dl.appendChild(o);});

const grid=document.getElementById('grid');
const cnt=document.getElementById('cnt');
function updCount(){cnt.textContent=Object.keys(MAP).filter(k=>MAP[k]&&MAP[k].name).length;}
function saveLocal(){localStorage.setItem(LSKEY,JSON.stringify(MAP));updCount();}

function card(mon){
  const id=mon.id, e=MAP[id]||{};
  const el=document.createElement('div');
  el.className='card'+(e.name?' named':''); el.dataset.id=id;
  el.innerHTML=`
    <div class="top">
      <div class="thumb">${mon.img?`<img src="${mon.img}">`:'<span class=stat>no sprite</span>'}</div>
      <div class="idcol"><div class="lid">${id}</div><div class="lidsub">pal ${mon.pal} · f${mon.start}</div></div>
    </div>
    <input class="name" list="atlasList" placeholder="monster name…" value="${e.name?e.name.replace(/"/g,'&quot;'):''}">
    <div class="row2">
      <input class="hp" type="number" placeholder="hp" value="${e.hp??''}" title="HP">
      <input class="exp" type="number" placeholder="exp" value="${e.exp??''}" title="exp reward">
    </div>
    <div class="meta"></div>`;
  const name=el.querySelector('.name'), hp=el.querySelector('.hp'), exp=el.querySelector('.exp'), meta=el.querySelector('.meta');
  function refreshMeta(){
    const a=byName[(name.value||'').toLowerCase()];
    meta.innerHTML = a ? `Atlas: exp <b>${a.exp}</b> · ${a.type||'?'} · ${a.page}` : (name.value?'<i>custom name (not in Atlas)</i>':'');
  }
  function commit(){
    const nm=name.value.trim();
    if(!nm){delete MAP[id]; el.classList.remove('named');}
    else{
      const a=byName[nm.toLowerCase()];
      MAP[id]={name:nm, hp:hp.value?+hp.value:undefined, exp:exp.value?+exp.value:(a?a.exp:undefined),
               type:a?a.type:undefined, atlas:a?a.name:undefined};
      el.classList.add('named');
    }
    saveLocal(); refreshMeta();
  }
  name.addEventListener('input',()=>{const a=byName[name.value.toLowerCase()]; if(a&&!exp.value)exp.value=a.exp??''; refreshMeta();});
  name.addEventListener('change',commit);
  name.addEventListener('blur',commit);
  hp.addEventListener('change',commit); exp.addEventListener('change',commit);
  refreshMeta();
  return el;
}
MONSTERS.forEach(m=>grid.appendChild(card(m)));
updCount();

// filter
const search=document.getElementById('search'), onlyU=document.getElementById('onlyUnnamed');
function applyFilter(){
  const q=search.value.trim().toLowerCase();
  [...grid.children].forEach(c=>{
    const id=c.dataset.id, nm=(MAP[id]&&MAP[id].name||'').toLowerCase();
    let ok=true;
    if(q) ok = id===q || id.includes(q) || nm.includes(q);
    if(ok && onlyU.checked) ok = !(MAP[id]&&MAP[id].name);
    c.classList.toggle('hidden',!ok);
  });
}
search.addEventListener('input',applyFilter); onlyU.addEventListener('change',applyFilter);

function toast(msg,warn){const t=document.getElementById('toast');t.textContent=msg;t.style.background=warn?'#e0a44a':'';t.classList.add('show');setTimeout(()=>t.classList.remove('show'),1800);}
document.getElementById('btnSave').onclick=async()=>{
  try{
    const r=await fetch('/save',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({version:1,entries:MAP})});
    const j=await r.json();
    toast(j.ok?`Saved ${j.count} → data/monster_mapping.json`:'Save failed',!j.ok);
  }catch(e){toast('Save failed: '+e,true);}
};
document.getElementById('btnExport').onclick=()=>{
  const blob=new Blob([JSON.stringify({version:1,entries:MAP},null,1)],{type:'application/json'});
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

    def log_message(self, *a):  # quiet
        pass

    def do_GET(self):
        if urlparse(self.path).path in ("/", "/index.html"):
            page = (PAGE
                    .replace("__N__", str(len(MONSTERS)))
                    .replace("__MONSTERS__", json.dumps(MONSTERS))
                    .replace("__ATLAS__", json.dumps(ATLAS))
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
            entries = {k: v for k, v in (data.get("entries") or {}).items() if v and v.get("name")}
            os.makedirs(os.path.dirname(MAPPING_PATH), exist_ok=True)
            with open(MAPPING_PATH, "w", encoding="utf-8") as f:
                json.dump({"version": 1, "entries": entries}, f, indent=1)
            self._send(200, json.dumps({"ok": True, "count": len(entries)}), "application/json")
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
