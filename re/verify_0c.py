"""Verify the 0x0c mob-walk packet semantics EMPIRICALLY (read-only, no input):
  Hypothesis A: packet (x,y) = the mob's NEW position (what our parser assumes today)
  Hypothesis B: packet (x,y) = the ORIGIN tile; true position = (x,y) + DELTA[dir]
For each 0x0c event, wait a beat, then read that mob's REAL position from client memory
(entity struct: uid@base, x@+4, y@+8, vtable-validated) and score A vs B.
If B wins, our wire tracker has been placing every moving mob 1 tile behind reality --
the exact 'close but never right / swinging at nothing' symptom."""
import sys, time, collections
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

agent = NA.Agent(); world = NB.World(agent)
s, sc = NB.attach(NB.build_pump(world, agent)); ex = sc.exports_sync
world.mem_ex = ex
time.sleep(2)

MODLO, MODHI = 0x400000, 0x6b3000
addr_cache = {}     # eid -> memory addr of its entity struct

def mem_pos(eid, near):
    """Real client position of `eid` from its entity struct; cached addr, re-scan on miss."""
    a = addr_cache.get(eid)
    if a is not None:
        try:
            if ex.ru32(hex(a)) == eid:
                x, y = ex.ru32(hex(a + 4)), ex.ru32(hex(a + 8))
                if x is not None and 0 <= x < 4096 and 0 <= (y or 0) < 4096:
                    return (x, y)
        except Exception:
            pass
        addr_cache.pop(eid, None)
    best = None
    for hs in ex.scanu32(eid, 40):
        ap = int(hs, 16)
        try:
            x, y = ex.ru32(hex(ap + 4)), ex.ru32(hex(ap + 8))
            v1, v2 = ex.ru32(hex(ap - 0x38)), ex.ru32(hex(ap - 0x20))
        except Exception:
            continue
        MOD = lambda v: v is not None and MODLO <= v < MODHI
        if not (MOD(v1) or MOD(v2)):
            continue
        if x is None or y is None or not (1 <= x < 4096 and 1 <= y < 4096):
            continue
        d = abs(x - near[0]) + abs(y - near[1])
        if d <= 3 and (best is None or d < best[0]):
            best = (d, ap, x, y)
    if best is None:
        return None
    addr_cache[eid] = best[1]
    return (best[2], best[3])

print("watching 0x0c mob walks vs memory for 30s (read-only)...")
seen = 0
score = collections.Counter()
deadline = time.time() + 30
last_len = 0
while time.time() < deadline:
    moves = world.snapshot_moves()
    new = moves[last_len:]
    last_len = len(moves)
    for ts, eid, x, y, dr in new:
        if dr not in NB.DELTA:
            continue
        time.sleep(0.12)                      # let the client apply/render the step
        # skip if the mob moved AGAIN already (ambiguous sample)
        latest = [m for m in world.snapshot_moves() if m[1] == eid]
        if latest and latest[-1][0] != ts:
            continue
        dx, dy = NB.DELTA[dr]
        mp = mem_pos(eid, (x, y))
        if mp is None:
            score["unresolved"] += 1
            continue
        seen += 1
        if mp == (x + dx, y + dy):
            score["B: origin+delta (parser is 1 tile behind!)"] += 1
        elif mp == (x, y):
            score["A: packet==position (parser correct)"] += 1
        else:
            score[f"other"] += 1
        if seen >= 40:
            break
    if seen >= 40:
        break
    time.sleep(0.05)

print(f"\nsamples resolved: {seen}")
for k, v in score.most_common():
    print(f"  {k}: {v}")
s.detach()
