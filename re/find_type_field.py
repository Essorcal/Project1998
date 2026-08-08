"""Find the field that distinguishes ITEM entities from MOB entities.
Classify by asking the server the name at each entity's tile (only tiles with exactly ONE
entity, so the per-tile name is unambiguous), then diff the raw struct bytes between groups."""
import sys, time, collections
sys.path.insert(0, ".")
import nexus_bot as NB, nexus_agent as NA

JS = r"""
let THIS = null;
Interceptor.attach(ptr('0x576660'), { onEnter(args){ if(!THIS){ THIS=this.context.ecx;
  send({t:'this'}); Interceptor.detachAll(); } }});
const fn = new NativeFunction(ptr('0x576660'),'int',['pointer','pointer','uint'],'thiscall');
rpc.exports = { ready:function(){ return !!THIS; },
  ask:function(x,y){ if(!THIS) return false;
    const m=Memory.alloc(6); m.writeU8(0x0a);
    m.add(1).writeU8((x>>8)&0xff); m.add(2).writeU8(x&0xff);
    m.add(3).writeU8((y>>8)&0xff); m.add(4).writeU8(y&0xff); m.add(5).writeU8(0);
    try{ fn(THIS,m,6); return true; }catch(e){ return false; } } };
"""
agent = NA.Agent(); world = NB.World(agent); last = []
def pump(msg, data):
    if msg.get("type") != "send": return
    p = msg["payload"]
    if p.get("t") == "this": return
    if p.get("op") == 0x0a and p.get("t") != "send":
        b = bytes(int(x,16) for x in p["hex"].split())
        try:
            if 0 < b[3] < 40: last.append((b[1], b[4:4+b[3]].decode('latin-1')))
        except Exception: pass
    try: world.on_packet(p) if p.get("t") != "send" else world.on_send(p)
    except Exception: pass

s, sc = NB.attach(pump); ex = sc.exports_sync; world.mem_ex = ex
sc2 = s.create_script(JS); sc2.on("message", pump); sc2.load(); ex2 = sc2.exports_sync
for _ in range(60):
    if ex2.ready(): break
    time.sleep(0.5)
time.sleep(2)
me = world.read_self_now(); world.bootstrap_pool()
rows = ex.enument(NB.ENT_VTABLE, *world.pool) if world.pool else []
bytile = collections.defaultdict(list)
for u,x,y in rows: bytile[(x,y)].append(u)
solo = [(abs(x-me[0])+abs(y-me[1]), u, x, y) for (x,y),us in bytile.items()
        if len(us)==1 for u in us]
solo.sort()
print(f"self={me} entities={len(rows)} single-entity tiles nearby={len(solo)}")
def base_of(uid):
    for hs in ex.scanu32(uid, 60):
        a=int(hs,16)
        if ex.ru32(hex(a-0xF8))==NB.ENT_VTABLE: return a-0xF8
    return None
samples=[]
for d,u,x,y in solo[:12]:
    n0=len(last); ex2.ask(x,y); time.sleep(0.8)
    if len(last)<=n0: continue
    kind,nm = last[-1]
    if kind != 2: continue
    b = base_of(u)
    if not b: continue
    h = ex.readbytes(hex(b), 0x220)
    if not h: continue
    samples.append((u,x,y,nm,bytes.fromhex(h)))
    print(f"  eid={u:<9} ({x},{y}) d={d:<3} -> {nm!r}")
print(f"\ncollected {len(samples)} classified structs")
open("auto/type_samples.txt","w").write(repr([(u,x,y,nm,by.hex()) for u,x,y,nm,by in samples]))
print("saved -> auto/type_samples.txt")
s.detach()
