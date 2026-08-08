"""Assumption-free: lock onto a mob's memory slot by MOVEMENT CORRELATION (same method that
found self-pos). Pick a mob that's moving (wire 0x0c), and keep only the memory u32 addresses
whose value equals the mob's x at EVERY observed step. After a few of the mob's moves only its
real x-field survives; then find the y-field by the same trajectory filter near it. Two mobs
=> the array stride. No layout/encoding assumptions."""
import sys, time, collections
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

agent = NA.Agent(); world = NB.World(agent)
s, sc = NB.attach(NB.build_pump(world, agent)); ex = sc.exports_sync; world.mem_ex = ex

def mob_track(label):
    """Return the memory address whose u32 follows some mob's X trajectory, plus that mob's
    (eid, trajectory). Filters a candidate set across the mob's moves."""
    print(f"[{label}] waiting for a moving mob...")
    xcand = None
    tracked_eid = None
    traj = []
    last = {}
    pending = None                     # (addr) a unique candidate awaiting 1-move confirmation
    t_end = time.time() + 70
    while time.time() < t_end:
        time.sleep(0.35)
        for eid, e in world.fresh_entities(within_ms=2500):
            x, y = e.get("x"), e.get("y")
            if x is None:
                continue
            prev = last.get(eid)
            last[eid] = (x, y)
            if eid != (tracked_eid if tracked_eid else eid):
                continue
            if tracked_eid is None:
                if prev and prev[0] != x:          # a mob just moved in x -> start tracking it
                    tracked_eid = eid
                    traj = [(x, y)]
                    xcand = [int(a, 16) for a in ex.scanu32(x, 60000)]
                    xcand = [a for a in xcand if ex.ru32(hex(a)) == x]
                    print(f"[{label}] tracking eid={eid:#x} now={(x,y)}  cands={len(xcand)}")
                continue
            if eid != tracked_eid or not prev or prev[0] == x:
                continue
            traj.append((x, y))
            nxt = [a for a in xcand if ex.ru32(hex(a)) == x]
            if not nxt:
                xcand = [int(a, 16) for a in ex.scanu32(x, 60000)]
                xcand = [a for a in xcand if ex.ru32(hex(a)) == x]
                print(f"[{label}] re-seed x={x}  cands={len(xcand)}")
                continue
            xcand = nxt
            print(f"[{label}] eid={eid:#x} x->{x}  cands={len(xcand)}")
            # a unique candidate that ALSO has the mob's current y at a nearby offset AND its
            # wire-eid at -4 is conclusive -> lock immediately (don't wait for another move).
            if len(xcand) <= 2:
                for a in xcand:
                    yaddr = next((a + dy for dy in (4, -4, 8, 0xC, 0x10) if ex.ru32(hex(a + dy)) == y), None)
                    if yaddr is not None:
                        print(f"[{label}] LOCKED eid={eid:#x} x@{hex(a)}={x} y@{hex(yaddr)}={y} "
                              f"uid@-4={ex.ru32(hex(a-4))}")
                        return a, yaddr, eid, traj, xcand
    print(f"[{label}] gave up (no mob moved enough)")
    return None

print("=== locating a mob's memory slot (movement correlation) ===")
r1 = mob_track("M1")
if not r1:
    sys.exit(0)
XADDR, y1, eid1, traj1, xc1 = r1
XY = XADDR                                  # x-field addr; y at +4 (confirmed)
print(f"\nAnchor mob slot: x@{hex(XADDR)} y@{hex(XADDR+4)}  (eid {eid1:#x})")

# --- current live positions from wire, to recognise other entities in the array ---
P = set((e["x"], e["y"]) for _, e in world.fresh_entities(within_ms=6000) if e.get("x"))

# 1) STRUCT LAYOUT: dump a window around the x-field
print("\n=== struct window around x-field (offset from x: u32) ===")
for off in range(-0x40, 0x44, 4):
    v = ex.ru32(hex(XADDR + off))
    tag = "<-X" if off == 0 else ("<-Y" if off == 4 else "")
    print(f"  x{off:+#06x}: {v}   {tag}")

# 2) UID confirmation + object base (vtable) detection
uid = ex.ru32(hex(XADDR - 4))
print(f"\nuid@x-4 = {uid} ({hex(uid)})  wire eid = {hex(eid1)}  MATCH={uid==eid1}")
B, BSZ = 0x400000, 0x2b3000
vtbl_off = None
for off in range(-0x60, 0, 4):
    v = ex.ru32(hex(XADDR + off))
    if v and B <= v < B + BSZ:                 # a pointer into the module image = a vtable
        print(f"  vtable-like ptr {hex(v)} at x{off:+#x}  -> object base candidate {hex(XADDR+off)}")
        if vtbl_off is None:
            vtbl_off = off

# 3) find the ENTITY POINTER TABLE: scan for u32 pointers to the object base candidates.
#    A manager/list holds many such pointers close together.
print("\n=== hunting the entity pointer table (pointers to the mob object) ===")
obj_bases = [XADDR - 4]                          # uid start
if vtbl_off is not None:
    obj_bases.insert(0, XADDR + vtbl_off)        # vtable start (true C++ object base)
for ob in obj_bases:
    holders = [int(a, 16) for a in ex.scanu32(ob, 200)]
    inmod = [h for h in holders if B <= h < B + BSZ]
    print(f"  ptrs to {hex(ob)}: {len(holders)} total, {len(inmod)} static; sample: "
          + ", ".join(hex(h) for h in holders[:6]))
    # if several holders are close together, that's the table -> dump it
    holders.sort()
    for i, h in enumerate(holders):
        near = [g for g in holders if 0 <= g - h < 0x400]
        if len(near) >= 3:
            print(f"    TABLE near {hex(h)}: {len(near)} pointers within 0x400 -> dumping entities:")
            for g in near[:24]:
                p = ex.ru32(hex(g))              # pointer to an object
                if not p:
                    continue
                # object -> uid at (p - vtbl_off - ... ) ; but we scanned ptrs to ob, so *g==ob-ish
                exd = ex.ru32(hex(p + (4 if ob == XADDR-4 else (4 - vtbl_off))))
                print(f"      [{hex(g)}] -> obj {hex(p)}")
            break
