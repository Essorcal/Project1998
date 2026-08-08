"""Send the 0x0a name request ourselves via the client's own send fn (it encrypts for us).
Real captured request bytes: `0a 00 01 00 03 00` -> BIG-ENDIAN u16 fields (1 and 3).
Sweep field A (seen as 1 and 7) x field B to learn what they index."""
import sys, time
sys.path.insert(0, ".")
import nexus_bot as NB, nexus_agent as NA

JS = r"""
let THIS = null;
Interceptor.attach(ptr('0x576660'), { onEnter(args){ if (!THIS){ THIS = this.context.ecx;
  send({t:'this', v:THIS.toString()}); Interceptor.detachAll(); } }});
const fn = new NativeFunction(ptr('0x576660'), 'int', ['pointer','pointer','uint'], 'thiscall');
rpc.exports = {
  ready: function(){ return THIS ? THIS.toString() : null; },
  ask: function(a, b){                       // 0a <be16 a> <be16 b> 00
    if (!THIS) return false;
    const m = Memory.alloc(6);
    m.writeU8(0x0a);
    m.add(1).writeU8((a>>8)&0xff); m.add(2).writeU8(a&0xff);
    m.add(3).writeU8((b>>8)&0xff); m.add(4).writeU8(b&0xff);
    m.add(5).writeU8(0);
    try{ fn(THIS, m, 6); return true; }catch(e){ send({t:'err', e:''+e}); return false; }
  }
};
"""
agent = NA.Agent(); world = NB.World(agent)
replies = []
def pump(msg, data):
    if msg.get("type") != "send": return
    p = msg["payload"]
    if p.get("t") == "this": print("this =", p["v"]); return
    if p.get("t") == "err": print("ERR", p["e"]); return
    if p.get("op") == 0x0a and p.get("t") != "send":
        b = bytes(int(x,16) for x in p["hex"].split())
        try:
            if 0 < b[3] < 40:
                replies.append((b[1], b[4:4+b[3]].decode('latin-1')))
        except Exception: pass
    try: world.on_packet(p) if p.get("t") != "send" else world.on_send(p)
    except Exception: pass

s, sc = NB.attach(pump); ex = sc.exports_sync; world.mem_ex = ex
sc2 = s.create_script(JS); sc2.on("message", pump); sc2.load(); ex2 = sc2.exports_sync
print("waiting for `this` (client must send a packet -- bot is running so it should)...")
for _ in range(60):
    if ex2.ready(): break
    time.sleep(0.5)
if not ex2.ready(): print("no this; aborting"); s.detach(); sys.exit()
print("self:", world.read_self_now())
found = {}
for a in (1, 7):
    for b in range(0, 26):
        n0 = len(replies)
        ex2.ask(a, b); time.sleep(0.08)
        if len(replies) > n0:
            kind, nm = replies[-1]
            found[(a,b)] = (kind, nm)
            print(f"  A={a} B={b:<3} -> kind={kind} name={nm!r}", flush=True)
print(f"\ntotal: {len(found)}")
s.detach()
