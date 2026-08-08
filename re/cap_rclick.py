"""Capture the PLAINTEXT of the right-click name request (opcode 0x0a) at the client's own
send function 0x576660 (which takes [op][payload] and does framing+encryption itself).
Also captures the 0x0a REPLY so we can pair request->name, and dumps nearby entity eids so
we can see which eid the request encodes."""
import sys, time
sys.path.insert(0, ".")
import nexus_bot as NB, nexus_agent as NA
import frida

JS = r"""
Interceptor.attach(ptr('0x576660'), { onEnter(args){
  try{
    const len = args[1].toInt32();
    if (len < 1 || len > 64) return;
    const b = new Uint8Array(args[0].readByteArray(len));
    if (b[0] === 0x0a)
      send({t:'req', len:len, hex:Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ')});
  }catch(e){}
}});
"""
agent = NA.Agent(); world = NB.World(agent)
reqs, names = [], []

def pump(msg, data):
    if msg.get("type") != "send":
        return
    p = msg["payload"]
    if p.get("t") == "req":
        reqs.append((time.time(), p)); print(f"  REQUEST  plaintext: {p['hex']}", flush=True); return
    if p.get("op") == 0x0a and p.get("t") != "send":
        b = bytes(int(x, 16) for x in p["hex"].split())
        try:
            if b[1] == 0x02 and 0 < b[3] < 40:
                n = b[4:4+b[3]].decode('latin-1')
                names.append((time.time(), n)); print(f"  REPLY    name: {n!r}", flush=True)
        except Exception: pass
    try:
        world.on_packet(p) if p.get("t") != "send" else world.on_send(p)
    except Exception: pass

s, sc = NB.attach(pump); ex = sc.exports_sync; world.mem_ex = ex
sc2 = s.create_script(JS); sc2.on("message", pump); sc2.load()
time.sleep(3)
me = world.read_self_now(); world.bootstrap_pool()
rows = ex.enument(NB.ENT_VTABLE, *world.pool) if world.pool else []
near = sorted(((abs(x-me[0])+abs(y-me[1]), u, x, y) for u, x, y in rows))[:10] if me else []
print(f"self={me}  nearby entities (eid, tile, dist):")
for d, u, x, y in near:
    print(f"    eid={u} ({x},{y}) d={d}  hex_be={u.to_bytes(4,'big').hex(' ')}  le={u.to_bytes(4,'little').hex(' ')}")
print("\n>>> RIGHT-CLICK 2-3 MOBS NOW  (35 second window) <<<\n", flush=True)
time.sleep(35)
s.detach()
print(f"\n=== captured {len(reqs)} requests, {len(names)} names ===")
for ts, p in reqs:
    print("  REQ:", p["hex"])
for ts, n in names:
    print("  NAME:", n)
