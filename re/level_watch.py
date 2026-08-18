#!/usr/bin/env python
"""Watch for level-ups and record base stats. READ-ONLY -- never touches the character.

No movement, no keys, no bot. It attaches, listens to the packets the client is already
receiving, and writes a row every time the level changes.

WHY NOT JUST USE char_levels.csv
    The agent already writes it, but its `*_base` columns are DERIVED: equipped totals minus
    a running gear-bonus vector that is updated from every equip/unequip delta. Once that
    vector drifts, every later row is silently wrong and nothing says so. In the current file
    `gear_known` is 0 from level 17 on, `ac` is blank from 15 on, and level 16 records
    might_base = -4, which is not a number a character can have.

    So this records BOTH, side by side, and never conflates them:
      * the EQUIPPED reading, which is measured and always true
      * the loadout that produced it, from the 0x39 profile (authoritative, server-sent)
      * the derived base, only when the agent still believes its gear vector is good
    Anything derived is labelled as derived. If the gear vector is untrustworthy the row
    still holds a real measurement plus the gear list, so base can be recovered later.

    To capture a clean, gear-free baseline: take everything off and run with --naked, which
    records the reading as an absolute base (nothing to subtract).

    python re/level_watch.py                  # watch until Ctrl-C
    python re/level_watch.py --naked          # this reading IS the base (gear is off)
    python re/level_watch.py --once           # print current stats and exit
"""
import os, sys, csv, time

D = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, D)

import nexus_bot as NB
import nexus_agent as NA
from bot_input_test import find_windows

P_OUT = os.path.join(NA.OUT, "level_base.csv")
# `character` FIRST-CLASS, not an afterthought. This file held 23 rows from one rogue and
# nothing in it said so; pointing the recorder at a second character would have interleaved
# two stat curves in one table with no way to separate them afterwards -- and every formula
# fitted to it would have been quietly wrong. The label is the 0x39 self-profile's first
# string, which is server-sent, so it cannot drift the way a hand-set flag would.
COLS = ["ts", "when", "character", "level", "naked", "ac_base",
        # measured, exactly as the server reported it
        "ac", "might", "grace", "will", "maxhp", "maxmana",
        # derived by subtracting the agent's gear vector -- trust only if gear_known
        "might_base", "grace_base", "will_base", "gear_known",
        "weapon", "gear", "exp", "tnl"]


def stats(agent, since=0):
    """Current stats, but ONLY if the server has sent a statblock since `since`.

    agent.cur is restored from disk at startup, so a fresh Agent happily reports the level
    the character was at when it last ran -- it read 28 while the character was 46. A stale
    number that looks live is worse than no number, so anything older than our attach is
    refused and the caller waits."""
    if getattr(agent, "stats_ts", 0) <= since:
        return None
    c = agent.cur
    if not c:
        return None
    return {
        "level": c.get("level"),
        # BASE armour -- from the statblock, the only AC that changes on level-up.
        "ac_base": getattr(agent, "ac_base", None),
        # the displayed total, which swings with buffs and debuffs mid-combat
        "ac": agent.ac,
        "might": c.get("might"), "grace": c.get("grace"), "will": c.get("will"),
        "maxhp": c.get("maxhp"), "maxmana": c.get("maxmana"),
    }


def base_of(agent, s):
    """What the agent thinks the gear-free values are. Derived, not measured."""
    try:
        g = agent.gear
        return {"might_base": s["might"] - g.get("might", 0),
                "grace_base": s["grace"] - g.get("grace", 0),
                "will_base": s["will"] - g.get("will", 0),
                "gear_known": 1 if agent.gear_known else 0}
    except Exception:
        return {"might_base": "", "grace_base": "", "will_base": "", "gear_known": 0}


def profile(ex, world, tries=4):
    """(label, worn items), straight from the server. A 0x2d request moves nothing.

    The label is the profile's first string -- who this actually is. Returned alongside the
    gear so a row can never be written without knowing which character produced it."""
    with world.lock:
        world.equipment = None
    for _ in range(tries):
        try:
            ex.sendraw([0x2d, 0x00, 0x00])
        except Exception:
            pass
        time.sleep(0.5)
        with world.lock:
            eq = world.equipment
        if eq:
            return (eq[1] or ""), list(eq[2])
    return "", []


def migrate(path):
    """Make an existing file's header match COLS before appending to it.

    A header is written ONCE, when the file is created. Add a column to COLS later
    and every subsequent append carries one more field than the header names --
    silently, because appending never re-reads the header. csv.DictReader then
    assigns by position and every column after the new one shifts by one. This
    already happened: ac_base was inserted at index 4, and the row written after
    that read back as might=8 for a character whose might is 35.

    That is the same failure that lost the AC history -- a number recorded in a
    form that cannot be read back, with nothing saying so. So the header is
    reconciled on every run: missing columns are back-filled empty (old rows
    genuinely have no value for them), and the original is kept as .bak."""
    try:
        with open(path, newline="", encoding="utf-8") as f:
            rows = list(csv.reader(f))
    except OSError:
        return
    if not rows or rows[0] == COLS:
        return
    old, data = rows[0], rows[1:]
    if not set(old) <= set(COLS):
        # columns were renamed or removed, not just added -- do not guess
        print(f"  ! {os.path.basename(path)} header does not match and cannot be "
              f"migrated automatically: {old}")
        return
    idx = {c: i for i, c in enumerate(old)}
    out = []
    for r in data:
        if len(r) == len(COLS):
            out.append(r)                       # already written in the new shape
        elif len(r) == len(old):
            out.append([r[idx[c]] if c in idx else "" for c in COLS])
        else:
            print(f"  ! skipping a row of {len(r)} fields (header has "
                  f"{len(old)}): {r[:4]}")
    import shutil
    shutil.copy(path, path + ".bak")
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(COLS)
        w.writerows(out)
    print(f"  migrated {os.path.basename(path)}: {len(old)} -> {len(COLS)} columns, "
          f"{len(out)} rows kept (backup at .bak)")


def write_row(row):
    new = not os.path.exists(P_OUT)
    os.makedirs(os.path.dirname(P_OUT), exist_ok=True)
    with open(P_OUT, "a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=COLS, extrasaction="ignore")
        if new:
            w.writeheader()
        w.writerow(row)


def show_delta(prev, cur, gear_same):
    """The level-up delta -- the one number here that needs no assumptions.

    Gear does not change during a ding, so its contribution cancels: delta(displayed) IS
    delta(base), exactly, whether or not we know what the gear is worth. Everything else in
    this file is either a raw reading or an estimate; this is a measurement."""
    # A BIG AC MOVE WITH GEAR UNCHANGED IS A MAGICAL EFFECT, NOT A LEVEL GAIN.
    # Levels nudge AC a few points at most (base 56 -> 50 across three levels). Equipped AC
    # went 1 -> 51 in a single step at level 54, which no level does.
    #
    # The DIRECTION does not identify the cause, and I got this wrong first time by calling
    # it a buff expiring: worse AC is equally "a buff wore off" or "a debuff landed" -- in
    # that instance it was the scourge debuff. AC alone cannot separate them, so the message
    # reports the fact and names both possibilities rather than picking one.
    try:
        dac = int(cur["ac"]) - int(prev["ac"])
    except (TypeError, ValueError, KeyError):
        dac = 0
    buffed = bool(gear_same) and abs(dac) >= 15
    if buffed:
        # Reported by the player: AC going UP has meant being debuffed (scourge), and AC
        # coming DOWN has meant a buff landing. Note that game-data/spell_effects.csv is no
        # help here -- scourge_poet is extracted as archetype=Debuff, debuff=slow,
        # durationMs=0, with no AC amount at all, so the table cannot tell us the size of
        # what we just measured.
        cause = "DEBUFFED (or a buff expired)" if dac > 0 else "BUFFED (or a debuff wore off)"
        print(f"  ** EFFECT CHANGED: ac {dac:+d} with the same gear worn -- {cause}, "
              f"not a level gain")

    # The AC that belongs in a level gain is ac_base, from the statblock. The displayed
    # value is not a level quantity at all -- it moves whenever anything is cast.
    keys = [("ac_base", "base ac"), ("might", "might"), ("grace", "grace"),
            ("will", "will"), ("maxhp", "hp"), ("maxmana", "mana")]
    parts = []
    for k, label in keys:
        try:
            d = int(cur[k]) - int(prev[k])
        except (TypeError, ValueError):
            continue
        if d:
            parts.append(f"{label} {d:+d}")
    if gear_same is None:
        # One of the two readings has no loadout (the profile request needs a connection
        # handle the client only supplies after it sends something). Not knowing whether
        # gear changed is not the same as knowing it did -- saying "gear CHANGED" there was
        # a false alarm on the very first level-up.
        print("  (loadout unknown on one reading -- delta is probably clean, unverified)")
        if parts:
            print(f"  BASE GAIN: {'  '.join(parts)}")
    elif not gear_same:
        print("  delta    : gear CHANGED across the level -- delta is not a clean base delta")
    elif parts:
        print(f"  BASE GAIN: {'  '.join(parts)}   (exact -- gear cancels across a ding)")
    else:
        print("  BASE GAIN: nothing moved")


def show(tag, row, ac_off=None):
    print(f"\n=== {tag}  level {row['level']} ===")
    print(f"  BASE AC  : {row['ac_base']}        (statblock -- moves only on level-up)")
    print(f"  shown ac : {row['ac']}        (displayed total -- swings with buffs/debuffs)")
    print(f"  stats    : might {row['might']}  grace {row['grace']}  will {row['will']}  "
          f"hp {row['maxhp']}  mana {row['maxmana']}")
    # NO DERIVED BASE AC. The reading below is the DISPLAYED value and nothing more.
    #
    # Deriving base as equipped + a fixed gear offset requires the sample to be free of
    # buffs and debuffs, and there is no way to tell from the sample whether it is. AC moves
    # by ~50 during ordinary combat: measured at level 65 within minutes -- 1, then -14,
    # then 35 -- all at the same level, with base constant (base only changes on level-up).
    # Every "BASE AC" figure this printed was equipped-plus-a-constant reported as fact, and
    # across fifteen levels it was wrong: it claimed 50 while the real base was 35.
    #
    # Base AC is on the character panel. Read it there. This records what the packets say.
    print(f"  wearing  : {row['gear'] or '(unknown)'}", flush=True)


def main():
    args = sys.argv[1:]
    naked = "--naked" in args
    # --base-ac N: the true base AC, read off the character panel. With the equipped value
    # we then know exactly what the worn gear is worth, and every later reading converts.
    base_ac = None
    if "--base-ac" in args:
        try:
            base_ac = int(args[args.index("--base-ac") + 1])
        except (IndexError, ValueError):
            print("--base-ac needs a number")
            return 1
    migrate(P_OUT)
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
    print(f"attached to pid {pid} -- READ ONLY, no input will be sent to the client")
    if naked:
        print("--naked: recording readings as absolute base (no gear to subtract)")

    try:
        # Wait for a statblock sent AFTER we attached -- see stats(). The server sends one
        # on level, equip, damage and similar; walking a step or taking a hit is enough.
        t_attach = time.time() * 1000
        print("waiting for a live statblock -- walk a step or take a hit in game", flush=True)
        # Wait INDEFINITELY. This runs for a whole play session, so a character that happens
        # to be standing still at attach time is not a reason to quit -- the old two-minute
        # limit exited during a lull and stopped recording for the rest of the session.
        s0 = None
        i = 0
        while s0 is None:
            s0 = stats(agent, t_attach)
            if s0:
                break
            if i and i % 120 == 0:
                print(f"  ...still waiting ({i // 2}s). Any stat change sends one.",
                      flush=True)
            i += 1
            time.sleep(0.5)
        who, worn = profile(ex, world)
        row = dict(ts=int(time.time() * 1000), when=time.strftime("%H:%M:%S"),
                   character=who, naked=1 if naked else 0,
                   weapon=worn[0] if worn else "",
                   gear="|".join(worn), exp=agent.exp, tnl="", **s0, **base_of(agent, s0))
        ac_off = None
        if base_ac is not None and s0["ac"] is not None:
            ac_off = base_ac - int(s0["ac"])
            print(f"\nanchored: base AC {base_ac} vs equipped {s0['ac']} "
                  f"-> this loadout is worth {ac_off:+d} AC")
        show("starting", row, ac_off)
        write_row(row)
        if "--once" in args:
            return 0

        prev_row = row
        last = s0["level"]
        print(f"\nwatching for level-ups from {last}... (Ctrl-C to stop)", flush=True)
        while True:
            time.sleep(1.0)
            s1 = stats(agent, t_attach)
            if not s1 or s1["level"] == last:
                continue
            time.sleep(1.5)                    # let the whole statblock settle
            s1 = stats(agent, t_attach) or s1
            who, worn = profile(ex, world)
            row = dict(ts=int(time.time() * 1000), when=time.strftime("%H:%M:%S"),
                       character=who, naked=1 if naked else 0,
                       weapon=worn[0] if worn else "",
                       gear="|".join(worn), exp=agent.exp, tnl="",
                       **s1, **base_of(agent, s1))
            show(f"LEVEL UP {last} -> {s1['level']}", row, ac_off)
            pg, cg = prev_row.get("gear"), row.get("gear")
            same = None if not (pg and cg) else (pg == cg)
            show_delta(prev_row, row, same)
            write_row(row)
            prev_row = row
            last = s1["level"]
    except KeyboardInterrupt:
        print("\nstopped.")
    finally:
        try:
            s.detach()
        except Exception:
            pass
    print(f"rows written to {P_OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
