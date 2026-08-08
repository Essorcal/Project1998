"""Name every nearby mob: entity table gives (eid, x, y); we ask the server for the name at
that tile using the client's own send fn (0x576660, __thiscall) so it encrypts for us.
Request: 0a <be16 x> <be16 y> 00   Reply: 0a <kind=02> 00 <len> <name>"""
import sys, time
sys.path.insert(0, ".")
import nexus_bot as NB, nexus_agent as NA

JS = r"""
let THIS = null;
Interceptor.attach(ptr('0x576660'), { onEnter(args){ if (!THIS){ THIS = this.context.ecx;
  send({t:'this'}); Interceptor.detachAll(); } }});
const fn = new NativeFunction(ptr('0x576660'), 'int', ['pointer','pointer','uint'], 'thiscall');
rpc.exports = { ready: function(){ return !!THIS; },
  ask: function(x, y){ if(!THIS) return false;
    const m = Memory.alloc(6); m.writeU8(0x0a);
    m.add(1).writeU8((x>>8)&0xff); m.add(2).writeU8(x&0xff);
    m.add(3).writeU8((y>>8)&0xff); m.add(4).writeU8(y&0xff); m.add(5).writeU8(0);
    try{ fn(THIS, m, 6); return true; }catch(e){ return false; } } };
"""
agent = NA.Agent(); world = NB.World(agent)
last = []
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
me = world.read_self_now()
world.bootstrap_pool()
rows = ex.enument(NB.ENT_VTABLE, *world.pool) if world.pool else []
print(f"self={me}  entities={len(rows)}")
near = sorted(((abs(x-me[0])+abs(y-me[1]), u, x, y) for u, x, y in rows))[:14]
print("\n  eid        tile      dist  NAME")
named = {}
for d, u, x, y in near:
    n0 = len(last)
    ex2.ask(x, y); time.sleep(0.75)
    nm = last[-1][1] if len(last) > n0 else "(no reply)"
    kind = last[-1][0] if len(last) > n0 else 0
    if kind == 2: named[u] = nm
    print(f"  {u:<10} ({x:>3},{y:>3})  {d:>3}   {nm}")
print(f"\nnamed {len(named)}/{len(near)} mobs")
s.detach()
