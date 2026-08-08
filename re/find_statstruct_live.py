"""Find the LIVE 7.x equipped-stat struct WITHOUT relying on transcribed values.

The stat page's "next level" (tnl) updates in real time as you kill: every point of exp
gained drops tnl by exactly one. That makes it a differential anchor -- far stronger than
scanning for a static number, because a coincidental match would have to move by the exact
same delta at the exact same time, twice.

Method:
  1. read exp from the self struct (known offset)
  2. snapshot writable memory
  3. wait for exp to change by D  (kill something / let the bot grind)
  4. diffval(-D) -> u16s that fell by exactly D  => tnl candidates
  5. repeat with a second, different delta and INTERSECT -- kills coincidences
  6. dump the struct around the surviving address and label it with the stat page values

Usage:  python find_statstruct_live.py [--gt might=11,grace=18,...]
        Kill mobs in the client (or run the bot) while it waits.
"""
import sys, time, struct
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

# values read off the in-game self-stats page, used only to LABEL what we find
GT = {"level": 17, "might": 11, "grace": 18, "will": 9, "ac": 68, "dam": 1, "hit": 0,
      "maxhp": 971, "maxmana": 473}
WAIT = 240          # seconds to wait for each exp change
CAP_MB = 512


def read_exp(world):
    v = world.read_vitals()
    return v[4] if v else None


def wait_for_exp_change(world, e0, label):
    print(f"  [{label}] exp={e0} -- waiting for it to change (kill something)...")
    t0 = time.time()
    while time.time() - t0 < WAIT:
        e = read_exp(world)
        if e is not None and e != e0:
            print(f"  [{label}] exp {e0} -> {e}  (delta {e - e0:+d})")
            return e
        time.sleep(0.4)
    return None


def round_once(ex, world, label):
    """One snapshot/gain/diff cycle -> set of addresses whose u16 fell by the exp gain."""
    e0 = read_exp(world)
    if e0 is None:
        print("  cannot read exp"); return None
    total = ex.snap(0, 0xFFFFFFFF, CAP_MB)
    print(f"  [{label}] snapshot {total/1e6:.0f} MB")
    e1 = wait_for_exp_change(world, e0, label)
    if e1 is None:
        print("  timed out waiting for exp to change"); return None
    d = e1 - e0
    if d <= 0:
        print(f"  exp went down ({d}) -- skipping this round"); return None
    hits = set(ex.diffval(-d))          # tnl falls by exactly the exp gained
    print(f"  [{label}] u16 values that fell by {d}: {len(hits)}")
    return hits


def main():
    agent = NA.Agent(); world = NB.World(agent)
    s, sc = NB.attach(NB.build_pump(world, agent))
    ex = sc.exports_sync
    world.mem_ex = ex
    time.sleep(2)
    R = NB.read_self_root(ex)
    print(f"self root {hex(R) if R else None}; ground truth = {GT}\n")

    a = round_once(ex, world, "round 1")
    if not a:
        s.detach(); return
    b = round_once(ex, world, "round 2")
    cands = sorted(a & b) if b else sorted(a)
    print(f"\nsurviving tnl candidates after intersect: {len(cands)}")
    for c in cands[:10]:
        print("   ", c)
    if not cands:
        s.detach(); return

    inv = {}
    for k, v in GT.items():
        inv.setdefault(v, []).append(k)
    for c in cands[:6]:
        A = int(c, 16)
        try:
            raw = bytes.fromhex(ex.readbytes(hex(A - 0x80), 0x140))
        except Exception:
            continue
        if len(raw) < 0x140:
            continue
        tnl = struct.unpack_from("<H", raw, 0x80)[0]
        u16 = [struct.unpack_from("<H", raw, o)[0] for o in range(0, 0x140, 2)]
        score = len({v for v in GT.values() if v > 1} & set(u16))
        print(f"\n=== tnl @{hex(A)} = {tnl} | GT values nearby: {score} "
              f"(selfroot{A-R:+#x})" if R else f"\n=== tnl @{hex(A)} = {tnl}")
        for i, v in enumerate(u16):
            off = -0x80 + i * 2
            if v in inv:
                print(f"    {off:+#06x}  {v:<6} <- {'/'.join(inv[v])}")
            elif off == 0:
                print(f"    {off:+#06x}  {v:<6} <- TNL (anchor)")
    s.detach()


if __name__ == "__main__":
    main()
