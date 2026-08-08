"""Calibrate the viewport->tile mapping for the 0x0a name request, then name every mob.
Request plaintext (captured from a real right-click): 0a <u16 vx> <u16 vy>  (viewport tile).
We call the client's own send fn 0x576660 (__thiscall, this=conn obj) so IT encrypts for us."""
import sys, time, collections
sys.path.insert(0, ".")
import nexus_bot as NB, nexus_agent as NA
import frida

JS = r"""
let THIS = null;
// capture `this` (ecx) from a REAL call, then DETACH -- an active Interceptor trampoline on
// the same address is not safe to call into.
Interceptor.attach(ptr('0x576660'), { onEnter(args){ if (!THIS) { THIS = this.context.ecx;
  send({t:'this', v: THIS.toString()}); Interceptor.detachAll(); } }});
// __thiscall: with Frida the this-pointer is the FIRST DECLARED ARG, not fn.call()'s thisArg.
const fn = new NativeFunction(ptr('0x576660'), 'int', ['pointer','pointer','uint'], 'thiscall');
rpc.exports = {
  ready: function(){ return THIS ? THIS.toString() : null; },
  ask: function(vx, vy){                       // send 0a <u16 vx> <u16 vy>
    if (!THIS) return false;
    const b = Memory.alloc(6);
    b.writeU8(0x0a); b.add(1).writeU16(vx); b.add(3).writeU16(vy); b.add(5).writeU8(0);
    try{ fn(THIS, b, 6); return true; }catch(e){ send({t:'err', e:''+e}); return false; }
  }
};
"""
agent = NA.Agent(); world = NB.World(agent)
replies = []          # (ts, name)
def pump(msg, data):
    if msg.get("type") != "send": return
    p = msg["payload"]
    if p.get("t") == "this": print("conn this =", p["v"]); return
    if p.get("t") == "err": print("ERR", p["e"]); return
    if p.get("op") == 0x0a and p.get("t") != "send":
        b = bytes(int(x,16) for x in p["hex"].split())
        try:
            if b[1] == 0x02 and 0 < b[3] < 40:
                replies.append((time.time(), b[4:4+b[3]].decode('latin-1')))
        except Exception: pass
    try: world.on_packet(p) if p.get("t") != "send" else world.on_send(p)
    except Exception: pass

s, sc = NB.attach(pump); ex = sc.exports_sync; world.mem_ex = ex
sc2 = s.create_script(JS); sc2.on("message", pump); sc2.load()
ex2 = sc2.exports_sync
print("waiting for the client to send something so we can capture `this`...")
for _ in range(40):
    if ex2.ready(): break
    time.sleep(0.5)
if not ex2.ready():
    print("no `this` captured (client idle). Move the character once and rerun."); s.detach(); sys.exit()

me = world.read_self_now(); world.bootstrap_pool()
rows = ex.enument(NB.ENT_VTABLE, *world.pool) if world.pool else []
mobs = {(x, y): u for u, x, y in rows}
print(f"self={me}  known mobs: {len(mobs)}")
print("sweeping viewport tiles...")
hits = {}
for vy in range(0, 14):
    for vx in range(0, 18):
        n0 = len(replies)
        ex2.ask(vx, vy)
        time.sleep(0.06)
        if len(replies) > n0:
            hits[(vx, vy)] = replies[-1][1]
print(f"\nname hits: {len(hits)}")
for k, v in sorted(hits.items()): print(f"   viewport{k} -> {v!r}")
# solve offset: viewport + offset = map tile, matched against known mob tiles
best = None
cand = collections.Counter()
for (vx, vy) in hits:
    for (mx, my) in mobs:
        cand[(mx - vx, my - vy)] += 1
if cand:
    (ox, oy), n = cand.most_common(1)[0]
    print(f"\nBEST OFFSET: map = viewport + ({ox},{oy})   matches {n}/{len(hits)} hits")
    print(f"  => self {me} should be viewport ({me[0]-ox},{me[1]-oy})")
s.detach()
