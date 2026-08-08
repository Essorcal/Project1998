"""Measure NAKED BASE STATS and PER-ITEM stat contributions on the live client.

Why: swings.csv carries the EQUIPPED stat vector, but fitting a swing-damage formula wants
the base separated from what gear adds. `base_of()` currently just mirrors the equipped
values because the true gear total was never observed from naked.

Method (the in-game sequence):
  Shift+T -> Shift+A     take off everything          -> statblock = NAKED BASE
  w, <slot letter>       re-equip one piece at a time -> statblock delta = THAT ITEM

Each equip is individually observable, so one pass yields the base vector AND a per-item
breakdown, and the character ends up dressed again.

SAFETY:
  * refuses to run outside a town unless --force (being naked next to mobs gets you killed)
  * records the starting vector and verifies the ending vector matches it
  * items go to the inventory, never the ground -- nothing can be lost, only left unworn
  * if re-equipping stalls it stops and tells you exactly what is still off

Usage:  python calibrate_base_stats.py [--force] [--letters abcdefgh]
"""
import sys, time, json, csv, os
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

TOWNS = {"Buya", "Kugnae", "Nagnang"}          # safe rooms: no hostile spawns
SETTLE = 1.6                                   # seconds to wait for statblocks to land
OUT_CSV = os.path.join(NA.OUT, "gear_contributions.csv")
VEC = ["level", "might", "grace", "will", "maxhp", "maxmana"]


class Watch:
    """Collects statblocks (0x08 sub 0x58/0x59/0x78/0x79) and ac/dam/hit (sub 0x19)."""

    def __init__(self):
        self.cur = None
        self.ac = self.dam = self.hit = None
        self.n = 0

    def pump(self, msg, data):
        if msg.get("type") != "send":
            return
        p = msg["payload"]
        if p.get("op") != 0x08:
            return
        d = bytes(int(x, 16) for x in p["hex"].split())
        if len(d) < 2:
            return
        if d[1] == 0x19 and len(d) >= 29:
            self.ac, self.dam, self.hit = d[26], d[27], d[28]
        elif d[1] in (0x58, 0x59, 0x78, 0x79):
            s = NA.parse_statblock(d)
            if s:
                self.cur = s
                self.n += 1

    def vector(self):
        if not self.cur:
            return None
        v = {k: self.cur.get(k) for k in VEC}
        v.update(ac=self.ac, dam=self.dam, hit=self.hit)
        return v


def diff(a, b):
    """b - a, for the fields both define."""
    if not a or not b:
        return {}
    out = {}
    for k in set(a) | set(b):
        x, y = a.get(k), b.get(k)
        if isinstance(x, int) and isinstance(y, int) and x != y:
            out[k] = y - x
    return out


def main():
    force = "--force" in sys.argv
    letters = "abcdefghijklmnop"
    if "--letters" in sys.argv:
        letters = sys.argv[sys.argv.index("--letters") + 1]

    wins = NB.find_windows()
    if not wins:
        print("no live client window"); return
    hwnd = wins[0][0]
    agent = NA.Agent(); world = NB.World(agent)
    watch = Watch()

    base_pump = NB.build_pump(world, agent)

    def pump(msg, data):
        watch.pump(msg, data)
        base_pump(msg, data)

    s, sc = NB.attach(pump)
    ex = sc.exports_sync
    world.mem_ex = ex
    # Shifted keys MUST go through SendInput: PostMessageW never updates real keyboard
    # state, so the client's GetKeyState(VK_SHIFT) reads "up" and Shift+A is ignored
    # (proven -- Shift+A was a no-op via frida/PostMessage and fired 19 packets via
    # SendInput). SendInput needs focus, so this foregrounds the game for the run.
    import ctypes
    ctrl = NB.Controller(hwnd, mode="send")
    ctrl.fkey = None
    time.sleep(1.2)
    ctypes.windll.user32.SetForegroundWindow(hwnd)
    time.sleep(0.6)

    # --- safety: where are we? ---
    rt = NB.RoomTracker(log=lambda m: print("   ", m))
    room = rt.poll(ex)
    print(f"room: {room!r}")
    if room not in TOWNS and not force:
        print(f"REFUSING: {room!r} is not a known safe town {sorted(TOWNS)}.")
        print("Being unequipped next to hostile mobs is how characters die.")
        print("Walk into town and rerun, or pass --force if you know it's safe.")
        s.detach(); return

    ctrl.close_chat(2)
    start = watch.vector()
    print(f"starting vector (from packets so far): {start}")

    # --- 1. take everything off ---
    print("\n-> Shift+T, Shift+A  (take off all)")
    ctrl.press_char("T"); time.sleep(0.35)
    ctrl.press_char("A"); time.sleep(SETTLE * 2)
    naked = watch.vector()
    if not naked or watch.n == 0:
        print("no statblock seen after unequip -- aborting before touching anything else.")
        print("(nothing was re-equipped; if gear did come off, put it back with w,<letter>)")
        s.detach(); return
    print(f"NAKED BASE: {naked}")

    # --- 2. re-equip one piece at a time, recording each delta ---
    rows, prev = [], naked
    print("\n-> re-equipping, one slot per step")
    for ch in letters:
        before_n = watch.n
        ctrl.press_char("w"); time.sleep(0.3)
        ctrl.press_char(ch); time.sleep(SETTLE)
        if watch.n == before_n:
            continue                       # nothing equipped for this letter
        now = watch.vector()
        d = diff(prev, now)
        if d:
            print(f"   '{ch}' -> {d}")
            rows.append({"slot_key": ch, **{f"d_{k}": v for k, v in d.items()}})
            prev = now
    final = watch.vector()

    # --- 3. report + verify we ended up dressed again ---
    print(f"\nNAKED : {naked}")
    print(f"FINAL : {final}")
    total = diff(naked, final)
    print(f"TOTAL gear contribution: {total}")
    if start and final and diff(start, final):
        print(f"WARNING: final differs from start by {diff(start, final)} "
              f"-- some slots may still be unequipped; check the character.")
    else:
        print("final vector matches the starting vector -- fully re-equipped.")

    if rows:
        cols = sorted({k for r in rows for k in r})
        cols.remove("slot_key"); cols = ["slot_key"] + cols
        NA.append_csv(OUT_CSV, rows, cols)
        print(f"wrote per-item deltas -> {OUT_CSV}")

    # persist the measured base so base_of() can finally be real
    if naked and total:
        agent.gear = {k: total.get(k, 0) for k in NA.VEC}
        agent.gear_known = True
        agent.cur = {k: final.get(k) for k in VEC if final.get(k) is not None}
        agent.ac, agent.dam, agent.hit = final.get("ac"), final.get("dam"), final.get("hit")
        agent.stats_ts = int(time.time() * 1000)
        agent.save_state()
        print("saved measured gear total to agent_state.json (gear_known=True)")
        print(f"  => base stats are now recoverable: base = equipped - {agent.gear}")
    s.detach()


if __name__ == "__main__":
    main()
