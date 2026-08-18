#!/usr/bin/env python
"""READ-ONLY reconnaissance of whatever room the character is standing in.

Sends NO input and moves nothing. It attaches, reads the room name, scans the client's
entity pool, and reports every creature with BOTH looks side by side:
  * memlook -- the sprite id read from client memory (+0x178). Fast, but it has drifted
               unreliable this session, so it is not trusted for identity.
  * wirelook -- the look from the entity's 0x07 SPAWN packet on the wire, which is what
                the look-calibration tool trusts. Authoritative.
Plus any server-sent name (from a 0x0a tile query the client already made) and a match
against game-data/mobs.csv so a known look resolves to a mob name.

Purpose: identify a mob we have no static data for (e.g. "Moonscale Serpent") before
pointing a bot at it, and see what ELSE shares the room so the bot never swings at the
wrong thing.

    python re/recon_room.py            # ~20s watch, then a summary
    python re/recon_room.py 40         # watch for 40s
"""
import os, sys, time, csv, collections

D = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, D)

import nexus_bot as NB
import nexus_agent as NA
from bot_input_test import find_windows

TYPE_CREATURE = 3
SCAN_CAP = 5000


def load_look_names():
    """look value -> [mob names] from the server's own mob table."""
    out = collections.defaultdict(list)
    p = os.path.join(D, "..", "game-data", "mobs.csv")
    try:
        for r in csv.DictReader(open(p, encoding="utf-8")):
            try:
                out[int(r["MobLook"])].append(r["Description"])
            except (ValueError, KeyError):
                pass
    except OSError:
        pass
    return out


def regions(ex):
    v = NB.ENT_VTABLE
    pat = " ".join(f"{(v >> (8 * i)) & 0xff:02x}" for i in range(4))
    hits = ex.scanpat(pat, SCAN_CAP)
    regs = []
    for h in hits:
        a = int(h, 16)
        if any(lo <= a < hi for lo, hi in regs):
            continue
        try:
            r = ex.rangeof(h)
        except Exception:
            continue
        if r:
            lo = int(r[0], 16)
            regs.append((lo, lo + r[1]))
    return regs


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 20.0
    names_by_look = load_look_names()

    wins = find_windows()
    if not wins:
        print("No live NexusTK.exe window found.")
        return 1
    pid = wins[0][2]
    agent = NA.Agent()
    world = NB.World(agent)
    s, sc = NB.attach(NB.build_pump(world, agent), pid=pid)
    ex = sc.exports_sync
    world.mem_ex = ex
    print(f"attached to pid {pid} -- READ ONLY, sends no input\n")

    rt = NB.RoomTracker(print)
    try:
        room = None
        for _ in range(6):
            try:
                room = rt.poll(ex) or room
            except Exception:
                pass
            if room:
                break
            time.sleep(0.5)
        wh = rt.names.get(room) if room else None
        dims = f" ({wh[1]}x{wh[2]})" if wh else " (size unknown -- not in map_index)"
        print(f"ROOM: {room or '?'}{dims}\n")

        regs = regions(ex)
        print(f"entity regions: {len(regs)} "
              f"({sum(hi - lo for lo, hi in regs) / 1048576:.0f} MB)\n")

        # watch, accumulating what we see; wire looks arrive as the client gets 0x07s
        seen = {}          # uid -> dict(x,y,memlook,count)
        t_end = time.time() + secs
        me = None
        while time.time() < t_end:
            try:
                me = ex.selfxy(hex(NB.SELF_PTR_ADDR), NB.SELF_OFF["x"],
                               NB.SELF_OFF["y"]) or me
            except Exception:
                pass
            rows = []
            for lo, hi in regs:
                try:
                    rows.extend(ex.enument(NB.ENT_VTABLE, lo, hi) or [])
                except Exception:
                    pass
            for r in rows:
                if len(r) < 5:
                    continue
                uid, x, y, ty, look = r[0], r[1], r[2], r[3], r[4]
                chp = r[5] if len(r) > 5 else None
                mhp = r[6] if len(r) > 6 else None
                if not (1000 < uid < 100_000_000):
                    continue
                d = seen.setdefault(uid, {"x": x, "y": y, "memlook": look,
                                          "ty3": 0, "count": 0, "chp": chp, "mhp": mhp,
                                          "chp_pos": 0})
                d["x"], d["y"], d["memlook"] = x, y, look
                d["chp"], d["mhp"] = chp, mhp
                d["count"] += 1
                if ty == TYPE_CREATURE:
                    d["ty3"] += 1
                if chp and 0 < chp < 100_000_000:
                    d["chp_pos"] += 1                 # times its memory HP read alive
            time.sleep(0.5)

        # wire looks + names from the pump
        with world.lock:
            wire = {eid: e.get("look") for eid, e in world.ent.items()}
        wnames = dict(getattr(agent, "mob_names", {}) or {})

        print(f"self @ {tuple(me) if me else '?'}\n")
        print(f"{len(seen)} distinct entities seen over {secs:.0f}s:\n")
        # group by (wirelook or memlook) to see the populations
        pop = collections.Counter()
        for uid, d in seen.items():
            wl = wire.get(uid)
            key = ("wire", wl) if wl is not None else ("mem", d["memlook"] & 0x7FFF)
            pop[key] += 1
        print("POPULATIONS (by look):")
        for (src, lk), n in pop.most_common():
            nm = names_by_look.get(lk, [])
            label = "/".join(sorted(set(nm))) if nm else "UNKNOWN look"
            print(f"  {n:3d}x  {src}look={lk:<5}  {label}")
        print()
        print("NEAREST 15 entities to you:")
        rows = []
        for uid, d in seen.items():
            dist = (abs(d["x"] - me[0]) + abs(d["y"] - me[1])) if me else 0
            rows.append((dist, uid, d))
        rows.sort(key=lambda t: t[0])
        for dist, uid, d in rows[:15]:
            wl = wire.get(uid)
            nm = wnames.get(uid, "")
            byname = names_by_look.get(wl if wl is not None else d["memlook"] & 0x7FFF, [])
            print(f"  d={dist:<3} uid{uid}@({d['x']},{d['y']})  "
                  f"memlook={d['memlook'] & 0x7FFF} wirelook={wl}  "
                  f"HP={d['chp']}/{d['mhp']} alive={d['chp_pos']}/{d['count']}  "
                  f"ty3={d['ty3']}/{d['count']}  "
                  f"name={nm or ('/'.join(sorted(set(byname))) if byname else '?')}")
    finally:
        try:
            s.detach()
        except Exception:
            pass
        print("\ndetached.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
