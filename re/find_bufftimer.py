#!/usr/bin/env python
"""READ-ONLY locator for buff countdown timers in client memory.

Sends NO input, moves nothing, casts nothing. It attaches, scans memory, and reports which
addresses hold a value that is counting DOWN like a buff timer -- so we can then read those
addresses live to know, at every tick, which buffs are up and how long each has left.

WHY MEMORY, NOT PACKETS: the client prints the seconds-remaining on screen, so the number is
a value in memory. Reading it there is direct ground truth; inferring buff state from the
stat block was abandoned because gear and buffs both move armour and confound each other.

TWO WAYS TO FIND IT -- use whichever fits:

  1. --value N   (BEST -- uses what you can see)
       You read your on-screen timer to me: "it says 843". I scan for a u16/u32 equal to N,
       then watch those addresses for a second: the real timer is the one now reading a bit
       less. One or two readings and it's pinned. Give the CURRENT number, not the duration.

  2. (no args)   general sweep
       Snapshot memory, then over several seconds keep only the u16 values that decrease by
       ~1 per second -- the signature of a countdown. Reports survivors and their values, so
       you can match one to what's on your screen. Have a buff actively ticking when you run.

  --dump 0xADDR  once a timer is found, dump the bytes around it so the buff-table layout
                 (id/duration/remaining for EACH of several buffs) can be mapped.

    python re/find_bufftimer.py --value 843
    python re/find_bufftimer.py                # general decreasing-counter sweep
    python re/find_bufftimer.py --dump 0x1a2b3c4
"""
import os, sys, time, struct

D = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, D)

import nexus_bot as NB
import nexus_agent as NA
from bot_input_test import find_windows

LO, HI = 0x100000, 0x7ff00000
CAP_MB = 512


def attach():
    wins = find_windows()
    if not wins:
        print("No live NexusTK.exe window found.")
        return None, None, None
    pid = wins[0][2]
    agent = NA.Agent()
    world = NB.World(agent)
    s, sc = NB.attach(NB.build_pump(world, agent), pid=pid)
    print(f"attached to pid {pid} -- READ ONLY, sends no input\n")
    return s, sc.exports_sync, pid


def le(v, width):
    return " ".join(f"{b:02x}" for b in struct.pack("<I" if width == 4 else "<H", v))


def rd(ex, addr, width):
    try:
        return ex.ru32(addr) if width == 4 else ex.ru16(addr)
    except Exception:
        return None


def by_value(ex, target):
    """Find addresses holding `target` (±2 for timing skew) as u16 or u32, then keep only
    the ones that are TICKING DOWN. A buff timer is the value that both matches what's on
    screen AND keeps dropping; a coincidental match sits still."""
    print(f"scanning for a countdown at or just below {target} ...")
    cands = []                                 # (addr, width)
    seen = set()
    # A countdown only goes DOWN, and there's a few seconds of lag between you reading me the
    # number and this scan running -- so search a window BELOW the reported value (and a hair
    # above for safety). 25s of slack absorbs chat latency without pulling in the whole map.
    window = list(range(target - 25, target + 3))
    # u32 by DEFAULT. As a u16, a small number like 170 is "aa 00" and appears ~100k times --
    # far too many to poll (that stalled the tool). As a u32 it's "aa 00 00 00": the two extra
    # zero bytes make it rare, so the candidate set is small enough to read safely. --u16 forces
    # the 2-byte scan if the timer turns out to be stored narrow.
    width = 2 if "--u16" in sys.argv else 4
    for v in window:
        if v < 0:
            continue
        try:
            hits = ex.scanpat(le(v, width), 8000)
        except Exception:
            hits = []
        for h in hits:
            if h not in seen:
                seen.add(h)
                cands.append((int(h, 16), width))
    print(f"  {len(cands)} raw address(es) (u{width*8}) hold a value near {target}.")
    # HARD GUARD: never poll a huge set -- that is what stalled the client last time.
    if len(cands) > 4000:
        print(f"  too many ({len(cands)}) to poll safely as u{width*8}. "
              f"{'Try without --u16.' if width == 2 else 'The timer may be u16 -- rerun with --u16.'}")
        return
    print(f"  watching which of the {len(cands)} count down...")
    # sample the candidates over a few seconds; a timer strictly decreases ~1/sec
    series = {c: [] for c in cands}
    t0 = time.time()
    for _ in range(6):
        for c in list(series):
            series[c].append((time.time() - t0, rd(ex, hex(c[0]), c[1])))
        time.sleep(1.0)
    timers = []
    for c, pts in series.items():
        vals = [v for _, v in pts if v is not None]
        if len(vals) < 4:
            continue
        drop = vals[0] - vals[-1]
        elapsed = pts[-1][0] - pts[0][0]
        monotone = all(b <= a for a, b in zip(vals, vals[1:]))   # never increases
        # ~1 per second, and it actually moved (a frozen value is not a timer)
        if monotone and drop >= 2 and abs(drop / elapsed - 1.0) < 0.6:
            timers.append((c, vals, drop / elapsed))
    print()
    if not timers:
        print("  no ticking countdown matched. Either the value moved between scans, or the "
              "timer is not stored in seconds -- tell me the on-screen number again RIGHT "
              "before I scan, or try the no-arg sweep.")
        return
    print(f"FOUND {len(timers)} ticking timer(s):")
    for (addr, width), vals, rate in sorted(timers, key=lambda t: -t[0][1]):
        print(f"  0x{addr:x}  u{width*8}  values={vals}  ~{rate:.2f}/s  "
              f"-> this is a buff countdown")
    print("\nnext: run with --dump 0x<addr> on one of these to map the buff table "
          "(id + remaining for each buff).")


def catch(ex, window=20):
    """Snapshot, let you CAST during a window, then find the timer by its up-jump.

    You never read me a number. A cast sets the buff's timer from ~0 to full duration -- a
    value leaping UP -- while every other counter in memory keeps ticking down. So: baseline,
    you cast, then keep only u16s that jumped up into a plausible duration range and are now
    counting back down. Works for several buffs at once (cast them all in the window)."""
    print("snapshotting baseline...")
    total = ex.snap(LO, HI, CAP_MB)
    print(f"  {total/1048576:.0f} MB captured.\n")
    print(f">>> CAST YOUR BUFF(S) NOW -- you have {window} seconds. Cast Bless and Flank. <<<")
    for r in range(window, 0, -1):
        print(f"    ...{r}s", end="\r", flush=True)
        time.sleep(1.0)
    print("\nscanning for timers that jumped up...          ")
    try:
        risers = ex.risers(30)                 # jumped up by >=30 -> a freshly-set timer
    except Exception as e:
        print(f"  risers failed: {e}")
        return
    # keep plausible durations, then confirm each is now DECREASING (a real countdown)
    cands = [(int(a, 16), ov, cv) for a, ov, cv in risers if 20 <= cv <= 4096]
    print(f"  {len(cands)} value(s) jumped up into a timer-like range; "
          f"confirming which tick down...")
    series = {c[0]: [] for c in cands}
    t0 = time.time()
    for _ in range(5):
        for a in list(series):
            series[a].append(rd(ex, hex(a), 2))
        time.sleep(1.0)
    timers = []
    for (addr, ov, cv) in cands:
        vals = [v for v in series[addr] if v is not None]
        if len(vals) >= 4 and all(b <= a for a, b in zip(vals, vals[1:])) \
                and vals[0] - vals[-1] >= 2:
            timers.append((addr, ov, cv, vals))
    print()
    if not timers:
        print("  caught up-jumps but none are ticking down -- recast during the window and "
              "retry; or the timer is not a plain u16 of seconds.")
        return
    print(f"FOUND {len(timers)} buff timer(s):")
    for addr, ov, cv, vals in sorted(timers, key=lambda t: -t[3][0]):
        print(f"  0x{addr:x}  cast set it {ov}->{cv}, now {vals}  "
              f"(full duration ~= {cv}s)")
    print("\nnext: --dump 0x<addr> on each to read the buff id beside the countdown, so all "
          "of them can be read together every tick.")


def sweep(ex, gap=5.0):
    """Find buff countdowns with no cast and no on-screen number needed.

    A seconds-timer drops by EXACTLY the elapsed seconds: ~5 over 5s. That single fact is
    nearly unique to it -- a frame counter drops by ~150 in 5s, a millisecond timer by ~5000,
    a frozen value by 0. So snapshot, wait exactly `gap` seconds, and keep only u16s that fell
    by ~gap. Then confirm each keeps falling at ~1/sec (rejects the frozen and the jumped-to-
    zero that fooled the first attempt) and report their live values to match to the screen."""
    print("snapshotting memory (both buffs should be ticking)...")
    total = ex.snap(LO, HI, CAP_MB)
    print(f"  {total/1048576:.0f} MB captured. Waiting {gap:.0f}s for timers to tick...")
    time.sleep(gap)
    cand = set()
    for d in (-int(gap) - 1, -int(gap), -int(gap) + 1):     # phase tolerance ±1 second
        try:
            cand |= set(ex.diffval(d))
        except Exception:
            pass
    cand = [int(a, 16) for a in cand]
    print(f"  {len(cand)} u16(s) fell by ~{int(gap)} -- i.e. ~1/second. Confirming...\n")
    # confirm a clean ~1/sec decline over another few seconds
    series = {a: [] for a in cand}
    t0 = time.time()
    stamps = []
    for _ in range(5):
        stamps.append(time.time() - t0)
        for a in list(series):
            series[a].append(rd(ex, hex(a), 2))
        time.sleep(1.0)
    timers = []
    for a, vals in series.items():
        vv = [v for v in vals if v is not None]
        if len(vv) < 4:
            continue
        elapsed = stamps[-1] - stamps[0]
        drop = vv[0] - vv[-1]
        monotone = all(b <= a2 for a2, b in zip(vv, vv[1:]))
        rate = drop / elapsed if elapsed else 0
        # ~1/sec, actually moving, and NOT a big jump / zeroing (drop close to elapsed)
        if monotone and 0.6 <= rate <= 1.6 and drop <= elapsed + 2:
            timers.append((a, vv))
    if not timers:
        print("  no clean 1/sec countdown confirmed. Recast a buff so it's freshly ticking "
              "and re-run, or widen the gap.")
        return
    print(f"FOUND {len(timers)} live countdown(s) -- match these to Bless/Flank on screen:")
    for a, vv in sorted(timers, key=lambda t: -t[1][0]):
        print(f"  0x{a:x}  u16 now {vv[0]} -> {vv[-1]}  (ticking ~1/s)")
    print("\ntell me which value matches which buff; then --dump 0x<addr> to map the id "
          "field beside each countdown.")


def dump(ex, addr):
    """Show the bytes around a found timer, decoded as u16/u32, to map the buff structure."""
    base = int(addr, 16) - 32
    try:
        hexs = ex.readbytes(hex(base), 96)
    except Exception:
        hexs = ""
    if not hexs:
        print("could not read there.")
        return
    b = bytes.fromhex(hexs)
    print(f"bytes around {addr} (base 0x{base:x}, the target is at offset +32):\n")
    for o in range(0, len(b) - 4, 4):
        u32 = int.from_bytes(b[o:o + 4], "little")
        u16 = int.from_bytes(b[o:o + 2], "little")
        u16b = int.from_bytes(b[o + 2:o + 4], "little")
        mark = "  <== target" if o == 32 else ""
        print(f"  +{o - 32:+4d}  0x{base + o:x}  u32={u32:<12} u16=({u16},{u16b}){mark}")
    print("\nread this twice a second apart (re-run --dump) to see which fields tick and "
          "which stay fixed -- the fixed neighbour is likely the buff id/type.")


def main():
    args = sys.argv[1:]
    s, ex, pid = attach()
    if ex is None:
        return 1
    try:
        if "--value" in args:
            by_value(ex, int(args[args.index("--value") + 1]))
        elif "--dump" in args:
            dump(ex, args[args.index("--dump") + 1])
        elif "--catch" in args:
            catch(ex)
        else:
            sweep(ex)
    finally:
        try:
            s.detach()
        except Exception:
            pass
        print("\ndetached.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
