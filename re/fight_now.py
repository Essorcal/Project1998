#!/usr/bin/env python
"""Emergency: just fight and heal. No gating, no tree, no buff logic."""
import os, sys, time
D = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, D)
import nexus_bot as NB
import nexus_agent as NA
from bot_input_test import find_windows, VK

HARE = 125
STOP = os.path.join(NA.OUT, "STOP")
hwnd, pid = (lambda w: (w[0][0], w[0][2]))(find_windows())
agent = NA.Agent()
world = NB.World(agent)
s, sc = NB.attach(NB.build_pump(world, agent), pid=pid)
ex = sc.exports_sync
H = str(hwnd)
time.sleep(0.8)

pat = " ".join(f"{(NB.ENT_VTABLE >> (8 * i)) & 0xff:02x}" for i in range(4))
regs = []
for h in ex.scanpat(pat, 5000):
    a = int(h, 16)
    if any(lo <= a < hi for lo, hi in regs):
        continue
    r = ex.rangeof(h)
    if r:
        lo = int(r[0], 16)
        regs.append((lo, lo + r[1]))
print(f"{len(regs)} regions", flush=True)

me_ = lambda: (lambda p: tuple(p) if p else None)(
    ex.selfxy(hex(NB.SELF_PTR_ADDR), NB.SELF_OFF["x"], NB.SELF_OFF["y"]))
vit = lambda: tuple(ex.selfstats(hex(NB.SELF_PTR_ADDR), NB.SELF_OFF["curhp"],
                                 NB.SELF_OFF["maxhp"], NB.SELF_OFF["curmana"],
                                 NB.SELF_OFF["maxmana"], NB.SELF_OFF["exp"]))


def hares():
    out = []
    for lo, hi in regs:
        try:
            for r in (ex.enument(NB.ENT_VTABLE, lo, hi) or []):
                if len(r) >= 5 and 1000 < r[0] < 100_000_000 and r[3] == 3 \
                        and (r[4] & 0x7FFF) == HARE:
                    out.append((r[0], r[1], r[2]))
        except Exception:
            pass
    return out


def esc():
    for _ in range(2):
        ex.postchar(H, 0x1B, 0x1B, False)
        time.sleep(0.05)


def heal(n):
    esc()
    for _ in range(n):
        ex.postchar(H, ord("Z"), ord("Z"), True)
        time.sleep(0.12)
        ex.postchar(H, ord("A"), ord("a"), False)
        time.sleep(0.33)


last_step = 0.0
print("FIGHTING", flush=True)
while True:
    if os.path.exists(STOP):
        os.remove(STOP)
        print("stopped")
        break
    v = vit()
    me = me_()
    if not (v and me and v[1]):
        time.sleep(0.1)
        continue
    frac = v[0] / float(v[1])
    if frac < 0.90 and v[2] > 20:
        n = 6 if frac < 0.5 else 4 if frac < 0.7 else 2
        heal(n)
        continue
    hs = hares()
    if not hs:
        time.sleep(0.1)
        continue
    t = min(hs, key=lambda m: abs(m[1] - me[0]) + abs(m[2] - me[1]))
    d = abs(t[1] - me[0]) + abs(t[2] - me[1])
    if d <= 1:
        dir_ = ("right" if t[1] > me[0] else "left") if t[1] != me[0] else \
               ("down" if t[2] > me[1] else "up")
        ex.postkey(H, VK[dir_])
        time.sleep(0.05)
        for _ in range(3):
            ex.postkey(H, VK["space"])
            time.sleep(0.12)
    else:
        if time.time() - last_step > 0.22:
            dx, dy = t[1] - me[0], t[2] - me[1]
            dir_ = ("right" if dx > 0 else "left") if abs(dx) >= abs(dy) else \
                   ("down" if dy > 0 else "up")
            ex.postkey(H, VK[dir_])
            time.sleep(0.03)
            ex.postkey(H, VK[dir_])
            last_step = time.time()
    time.sleep(0.05)
s.detach()
