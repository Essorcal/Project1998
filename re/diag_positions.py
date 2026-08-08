"""Diagnose the 'swinging at air / offset' symptom: for each live mob compare its MEMORY
position (how MemEntities reads it) against its WIRE position, and verify the match is the REAL
entity object via the vtable (module-range ptr near the struct). Also show self mem-pos.
If mem and wire AGREE -> matching is fine (offset is server-lag). If they DISAGREE -> the eid
match is grabbing a wrong/stale copy."""
import sys, time
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

agent = NA.Agent(); world = NB.World(agent)
s, sc = NB.attach(NB.build_pump(world, agent)); ex = sc.exports_sync; world.mem_ex = ex
time.sleep(5)

def selfpos():
    r = NB.read_self_root(ex)
    return (ex.ru32(hex(r + NB.SELF_OFF["x"])), ex.ru32(hex(r + NB.SELF_OFF["y"]))) if r else None

def all_eid_hits(eid):
    """Every memory location holding this eid, with the coords at +4/+8 and whether a vtable
    (module-range ptr) sits at -0x38 or -0x20 (marks the REAL C++ entity object)."""
    out = []
    for a in ex.scanu32(eid, 40):
        ap = int(a, 16)
        x, y = ex.ru32(hex(ap + 4)), ex.ru32(hex(ap + 8))
        v1, v2 = ex.ru32(hex(ap - 0x38)), ex.ru32(hex(ap - 0x20))
        MOD = lambda v: v is not None and 0x400000 <= v < 0x6b3000
        vt = MOD(v1) or MOD(v2)
        out.append((ap, x, y, vt))
    return out

print("self mem-pos:", selfpos())
ents = sorted(world.fresh_entities(within_ms=6000), key=lambda kv: kv[0])
print(f"\n{len(ents)} wire entities. For each: wire(x,y) vs every memory eid-hit [addr coords vtable]:")
for eid, e in ents[:14]:
    hits = all_eid_hits(eid)
    valid = [(hex(a), (x, y)) for a, x, y, vt in hits if vt and x and 1 <= x <= 600 and 1 <= (y or 0) <= 600]
    print(f"  eid={eid:#010x} wire=({e['x']},{e['y']}) look={e.get('look')}")
    for a, x, y, vt in hits:
        star = " *VTABLE(real obj)" if vt else ""
        print(f"      {hex(a)}: ({x},{y}){star}")
