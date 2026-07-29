#!/usr/bin/env python
"""
FULLY AUTOMATED live-client scraper. Start it once, never touch it again.

It attaches to the running NexusTK client, hooks the internal decrypt routine
(+0x178b20) and continuously maintains, with NO manual intervention:

  1. BASE STATS PER LEVEL (gear-accounted)
     The server pushes a stat-block packet (0x08 sub-58/59/78/79) on every change.
     Those readings are EQUIPPED totals, so we decompose them:
       * level-up  (0x78/0x79, level changes) -> gear is constant across the ding,
         so delta(displayed) == delta(BASE). Exact and gear-independent.
       * equip/unequip (0x58/0x59, level constant) -> delta(displayed) == that
         item's bonus. Added to a running gear_bonus vector we subtract from
         every later reading, so base stays correct across gear swaps.
     -> auto/char_levels.csv   (absolute base per level)
     -> auto/level_diffs.csv   (what each level-up actually grants)
     -> auto/gear_events.csv   (every gear bonus observed)

  2. MELEE SWING DATA + FORMULA FIT
     Every 0x13 mob-HP packet is a swing: true damage = body[10], crit = flags&0x40.
     Each swing is stamped with your full live stats and the mob's identity/look.
     -> auto/swings.csv, and a refreshed fit in auto/swing_model.md

  3. LIVE STATUS (level / exp / TNL / base+equipped stats) -> auto/status.json

Survives client restarts (re-attaches on its own). Run:
    python re/nexus_agent.py
"""
import os, sys, json, csv, time, threading, collections
import frida

MOD = "NexusTK.exe"
DEC_RVA = 0x178b20
D = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(D, "auto")
os.makedirs(OUT, exist_ok=True)

P_LEVELS = os.path.join(OUT, "char_levels.csv")
P_DIFFS = os.path.join(OUT, "level_diffs.csv")
P_GEAR = os.path.join(OUT, "gear_events.csv")
P_SWINGS = os.path.join(OUT, "swings.csv")
P_STATUS = os.path.join(OUT, "status.json")
P_MODEL = os.path.join(OUT, "swing_model.md")
P_STATE = os.path.join(OUT, "agent_state.json")
P_RAW = os.path.join(OUT, "raw_packets.jsonl")
P_LOCK = os.path.join(OUT, "agent.lock")
P_LOG = os.path.join(OUT, "agent.log")

STALE = 30      # seconds without a heartbeat before a lock is considered abandoned


def log(msg):
    line = f"{time.strftime('%H:%M:%S')} {msg}"
    print(line, flush=True)
    try:
        with open(P_LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


def claim_lock():
    """Single instance only: two hooks writing the same files corrupts them."""
    if os.path.exists(P_LOCK):
        try:
            age = time.time() - json.load(open(P_LOCK, encoding="utf-8"))["beat"]
        except Exception:
            age = STALE + 1
        if age < STALE:
            log("another agent is already running (lock is fresh) - exiting")
            return False
        log("found a stale lock from a dead agent - taking over")
    beat_lock()
    return True


def beat_lock():
    json.dump({"pid": os.getpid(), "beat": time.time()},
              open(P_LOCK, "w", encoding="utf-8"))

JS = r"""
'use strict';
const MAIN = (Process.findModuleByName && Process.findModuleByName('__MOD__')) || Process.enumerateModules()[0];
Interceptor.attach(MAIN.base.add(__RVA__), {
  onEnter(args){ this.out = args[2]; },
  onLeave(ret){
    try{
      let n = ret.toInt32(); if(n<=0) return; if(n>2048) n=2048;
      const b = new Uint8Array(this.out.readByteArray(n));
      send({ts:Date.now(), op:b[0], n:n, hex:Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ')});
    }catch(e){}
  }
});
""".replace("__MOD__", MOD).replace("__RVA__", hex(DEC_RVA))

# stat vector we decompose into base + gear
VEC = ["might", "grace", "will", "maxhp", "maxmana"]
Z = {k: 0 for k in VEC}


def be(b, o, n):
    v = 0
    for i in range(n):
        if o + i < len(b):
            v = (v << 8) | b[o + i]
    return v


def parse_statblock(d):
    """0x08 sub-58/59/78/79 -> equipped totals. Offsets from known-plaintext mapping."""
    if len(d) < 20:
        return None
    s = {"level": d[6], "might": d[15], "will": d[16], "grace": d[19],
         "maxhp": be(d, 9, 2), "maxmana": be(d, 13, 2)}
    if len(d) >= 63:
        s["tnl"] = be(d, 61, 2)
    if len(d) >= 65:
        s["ac"], s["dam"] = d[63], d[64]
    return s


class Agent:
    def __init__(self):
        self.lock = threading.Lock()
        # gear_bonus = what currently-worn gear adds on top of base, for each VEC field.
        # Seeded from calibration, then kept in sync by every equip/unequip delta.
        self.gear = dict(Z)
        self.gear_known = True      # False once a reading implies gear we never saw applied
        self.cur = None             # last equipped-total statblock
        self.level = None
        self.exp = None
        self.tnl = None
        self.ac = self.dam = self.hit = None
        self.curhp = None
        self.levels = {}            # level -> base row
        self.diffs = []
        self.entities = {}          # entity id -> look
        self.mobhp = {}             # entity id -> last hp
        self.swings = []
        self.pending_name = None
        self.load()

    # ---------- persistence ----------
    def load(self):
        if os.path.exists(P_STATE):
            try:
                st = json.load(open(P_STATE, encoding="utf-8"))
                self.gear.update({k: st.get("gear", {}).get(k, 0) for k in VEC})
                self.gear_known = st.get("gear_known", True)
            except Exception:
                pass
        if os.path.exists(P_LEVELS):
            try:
                for r in csv.DictReader(open(P_LEVELS, encoding="utf-8")):
                    self.levels[int(r["level"])] = r
            except Exception:
                pass

    def save_state(self):
        json.dump({"gear": self.gear, "gear_known": self.gear_known},
                  open(P_STATE, "w", encoding="utf-8"), indent=1)

    def base_of(self, s):
        """equipped reading -> absolute base, by subtracting the running gear bonus."""
        return {k: s[k] - self.gear.get(k, 0) for k in VEC}

    # ---------- packet handling ----------
    def on_packet(self, p):
        op, d = p["op"], bytes(int(x, 16) for x in p["hex"].split())
        ts = p["ts"]
        with self.lock:
            if op == 0x08 and len(d) > 1:
                self.on_vitals(d, ts)
            elif op == 0x13 and len(d) >= 11:
                self.on_mobhp(d, ts)
            elif op == 0x07 and len(d) >= 14:
                self.entities[be(d, 8, 4)] = be(d, 12, 2) & 0x7fff
            elif op == 0x0a:
                txt = "".join(chr(c) for c in d[1:] if 32 <= c < 127)
                if "experience" in txt:
                    self.pending_name = txt

    def on_vitals(self, d, ts):
        sub = d[1]
        if sub == 0x19 and len(d) >= 29:
            self.exp, self.tnl = be(d, 4, 2), be(d, 24, 2)
            self.ac, self.dam, self.hit = d[26], d[27], d[28]
        elif sub == 0x38 and len(d) >= 14:
            self.curhp, self.exp = be(d, 4, 2), be(d, 12, 2)
        elif sub in (0x58, 0x59, 0x78, 0x79):
            s = parse_statblock(d)
            if not s:
                return
            self.on_statblock(s, sub, ts)

    def on_statblock(self, s, sub, ts):
        """THE core of gear accounting."""
        prev = self.cur
        if "tnl" in s:
            self.tnl = s["tnl"]
        if "ac" in s:
            self.ac, self.dam = s["ac"], s["dam"]

        if prev is None:                       # first sighting: nothing to diff
            self.cur, self.level = s, s["level"]
            self.record_level(s, ts, delta=None)
            self.flush()
            return

        dlv = s["level"] - prev["level"]
        delta = {k: s[k] - prev[k] for k in VEC}
        changed = any(delta[k] for k in VEC)

        if dlv > 0:
            # LEVEL-UP: gear constant across the ding -> delta IS the base gain.
            self.record_level(s, ts, delta=delta)
        elif dlv == 0 and changed:
            # GEAR CHANGE: level constant -> delta is an item's bonus. Track it so
            # every later reading still decomposes to the right base.
            for k in VEC:
                self.gear[k] += delta[k]
            self.log_gear(ts, s["level"], delta)
            self.save_state()
            # base must be unchanged by a gear swap; if the level row disagrees, the
            # calibration was off -> re-derive the row from the new (better) info.
            self.record_level(s, ts, delta=None, resync=True)

        self.cur, self.level = s, s["level"]
        self.flush()

    def record_level(self, s, ts, delta, resync=False):
        lvl = s["level"]
        base = self.base_of(s)
        row = {"level": lvl}
        row.update({f"{k}_base": base[k] for k in VEC})
        row["tnl_next"] = s.get("tnl", "")
        row["ac"] = s.get("ac", "")
        row["dam"] = s.get("dam", "")
        row["gear_known"] = int(self.gear_known)
        row["gear_sub"] = ";".join(f"{k}{self.gear[k]:+d}" for k in VEC if self.gear[k])
        old = self.levels.get(lvl)
        self.levels[lvl] = row
        if delta and any(delta[k] for k in VEC):
            self.diffs.append({"level": lvl, "ts": ts,
                               **{f"d_{k}": delta[k] for k in VEC}})
        if resync and old and old.get("might_base") != row.get("might_base"):
            pass  # newer reading wins; kept silent, the CSV shows the current truth

    def log_gear(self, ts, level, delta):
        new = not os.path.exists(P_GEAR)
        with open(P_GEAR, "a", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            if new:
                w.writerow(["ts", "level", "kind"] + [f"d_{k}" for k in VEC])
            kind = "equip" if sum(delta.values()) > 0 else "unequip"
            w.writerow([ts, level, kind] + [delta[k] for k in VEC])

    def on_mobhp(self, d, ts):
        eid = be(d, 1, 4)
        flags, hp, dmg = d[5], d[6], d[10]
        prev = self.mobhp.get(eid)
        self.mobhp[eid] = hp
        if dmg <= 0 or self.cur is None:
            return
        base = self.base_of(self.cur)
        self.swings.append({
            "ts": ts, "eid": eid, "look": self.entities.get(eid, ""),
            "dmg": dmg, "crit": 1 if flags & 0x40 else 0,
            "mob_hp_after": hp, "mob_hp_before": prev if prev is not None else "",
            "level": self.cur["level"],
            "might": self.cur["might"], "grace": self.cur["grace"], "will": self.cur["will"],
            "might_base": base["might"], "grace_base": base["grace"], "will_base": base["will"],
            "dam": self.dam if self.dam is not None else "",
            "hit": self.hit if self.hit is not None else "",
            "ac": self.ac if self.ac is not None else "",
        })

    # ---------- outputs ----------
    def flush(self):
        cols = ["level"] + [f"{k}_base" for k in VEC] + \
               ["tnl_next", "ac", "dam", "gear_known", "gear_sub"]
        with open(P_LEVELS, "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=cols)
            w.writeheader()
            for lvl in sorted(self.levels):
                r = self.levels[lvl]
                w.writerow({c: r.get(c, "") for c in cols})
        if self.diffs:
            dc = ["level", "ts"] + [f"d_{k}" for k in VEC]
            with open(P_DIFFS, "w", newline="", encoding="utf-8") as f:
                w = csv.DictWriter(f, fieldnames=dc)
                w.writeheader()
                for r in self.diffs:
                    w.writerow({c: r.get(c, "") for c in dc})
        st = {"level": self.level, "exp": self.exp, "tnl": self.tnl,
              "curhp": self.curhp, "ac": self.ac, "dam": self.dam, "hit": self.hit,
              "equipped": self.cur, "gear_bonus": self.gear,
              "base": self.base_of(self.cur) if self.cur else None,
              "levels_captured": sorted(self.levels), "swings": len(self.swings),
              "updated": time.strftime("%Y-%m-%d %H:%M:%S")}
        json.dump(st, open(P_STATUS, "w", encoding="utf-8"), indent=1, default=str)

    def flush_swings(self):
        with self.lock:
            if not self.swings:
                return
            cols = list(self.swings[0].keys())
            new = not os.path.exists(P_SWINGS)
            with open(P_SWINGS, "a", newline="", encoding="utf-8") as f:
                w = csv.DictWriter(f, fieldnames=cols)
                if new:
                    w.writeheader()
                for r in self.swings:
                    w.writerow(r)
            self.swings = []


def fit_model():
    """Refresh the melee-swing analysis from swings.csv (no extra process needed)."""
    if not os.path.exists(P_SWINGS):
        return
    rows = list(csv.DictReader(open(P_SWINGS, encoding="utf-8")))
    if not rows:
        return

    def num(r, k):
        try:
            return float(r[k])
        except Exception:
            return None

    L = []
    for r in rows:
        d, m = num(r, "dmg"), num(r, "might_base")
        if d is None or m is None:
            continue
        L.append((d, m, num(r, "dam") or 0, num(r, "level") or 0,
                  int(r.get("crit") or 0), r.get("look", "")))
    if not L:
        return
    out = ["# Melee swing model (auto-refreshed)", "",
           f"samples: {len(L)}", ""]

    # per-mob damage profile; backstab shows up as a ~2x cluster
    bym = collections.defaultdict(list)
    for d, m, dam, lv, cr, look in L:
        bym[look].append((d, cr))
    out += ["## Damage by mob (look id)", "",
            "| look | n | min | median | max | crit n | low-cluster mean | high-cluster mean | ratio |",
            "|---|---|---|---|---|---|---|---|---|"]
    for look, v in sorted(bym.items(), key=lambda x: -len(x[1])):
        ds = sorted(d for d, _ in v)
        n = len(ds)
        med = ds[n // 2]
        lo = [d for d in ds if d <= med]
        hi = [d for d in ds if d > med]
        lom = sum(lo) / len(lo) if lo else 0
        him = sum(hi) / len(hi) if hi else 0
        out.append(f"| {look} | {n} | {ds[0]:.0f} | {med:.0f} | {ds[-1]:.0f} | "
                   f"{sum(c for _, c in v)} | {lom:.1f} | {him:.1f} | "
                   f"{(him/lom if lom else 0):.2f} |")

    # least-squares fit dmg ~ a*DAM + b*might + c over non-crit swings
    nc = [(d, m, dam) for d, m, dam, lv, cr, look in L if not cr]
    if len(nc) >= 6:
        import itertools
        n = len(nc)
        X = [[dam, m, 1.0] for _, m, dam in nc]
        y = [d for d, _, _ in nc]
        # normal equations (3x3) solved by Gaussian elimination
        A = [[sum(X[i][a] * X[i][b] for i in range(n)) for b in range(3)] +
             [sum(X[i][a] * y[i] for i in range(n))] for a in range(3)]
        for c in range(3):
            piv = max(range(c, 3), key=lambda r: abs(A[r][c]))
            A[c], A[piv] = A[piv], A[c]
            if abs(A[c][c]) < 1e-9:
                break
            for r in range(3):
                if r != c:
                    f = A[r][c] / A[c][c]
                    for k in range(c, 4):
                        A[r][k] -= f * A[c][k]
        else:
            coef = [A[i][3] / A[i][i] for i in range(3)]
            ybar = sum(y) / n
            ss = sum((y[i] - ybar) ** 2 for i in range(n))
            rs = sum((y[i] - sum(coef[j] * X[i][j] for j in range(3))) ** 2 for i in range(n))
            out += ["", "## Least-squares fit (non-crit swings)", "",
                    f"`dmg ~= {coef[0]:.3f}*DAM + {coef[1]:.3f}*might + {coef[2]:.2f}`",
                    f"R^2 = {1 - rs/ss if ss else 0:.3f}  (n={n})",
                    "",
                    "RTK reference: `(s/2*enchant + DAM*2.5 + might/8 + class) * rage * crit`,",
                    "then `x(1 + mobArmor/100)` and `x2` for a positional (back) hit."]
    open(P_MODEL, "w", encoding="utf-8").write("\n".join(out) + "\n")


def run():
    if not claim_lock():
        return
    ag = Agent()
    raw = open(P_RAW, "a", encoding="utf-8", buffering=1)

    def on_message(msg, data):
        if msg.get("type") != "send":
            return
        p = msg["payload"]
        raw.write(json.dumps(p) + "\n")
        try:
            ag.on_packet(p)
        except Exception:
            pass

    log(f"agent started (pid {os.getpid()}) - outputs -> {OUT}")
    attached = {}                     # pid -> session (held so the hook stays alive)
    last_fit = 0
    waiting = False
    while True:
        try:
            dev = frida.get_local_device()
            live = {pr.pid for pr in dev.enumerate_processes() if pr.name.lower() == MOD.lower()}
            for pid in live - set(attached):
                try:
                    s = dev.attach(pid)
                    sc = s.create_script(JS)
                    sc.on("message", on_message)
                    sc.load()
                    attached[pid] = (s, sc)
                    log(f"hooked {MOD} pid {pid} - capturing")
                    waiting = False
                except Exception as e:
                    log(f"attach {pid} failed: {e}")
            for pid in set(attached) - live:
                attached.pop(pid, None)
                log(f"client pid {pid} exited - will re-hook when it returns")
            if not live and not waiting:
                log(f"waiting for {MOD} to start...")
                waiting = True
        except Exception as e:
            log(f"device error: {e}")
        ag.flush_swings()
        with ag.lock:                 # keep status.json live off the 0x19/0x38 tickers too,
            ag.flush()                # not just the rare stat-block pushes
        if time.time() - last_fit > 60:
            try:
                fit_model()
            except Exception as e:
                log(f"fit error: {e}")
            last_fit = time.time()
        beat_lock()
        time.sleep(5)


def backfill(path):
    """Replay an existing decoded_live.jsonl through the same logic.

    Starting from gear={0,...} at the first stat-block means the earliest reading
    defines the reference point; every later equip/unequip delta is then learned
    automatically, so the gear vector self-calibrates with no manual input.
    """
    ag = Agent()
    ag.gear = dict(Z)
    n = 0
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            p = json.loads(line)
        except Exception:
            continue
        try:
            ag.on_packet(p)
            n += 1
        except Exception:
            pass
    ag.flush()
    ag.flush_swings()
    ag.save_state()
    fit_model()
    print(f"[backfill] replayed {n} packets from {os.path.basename(path)}")
    print(f"[backfill] levels: {sorted(ag.levels)}")
    print(f"[backfill] learned gear bonus: "
          f"{ {k: v for k, v in ag.gear.items() if v} or 'none'}")


if __name__ == "__main__":
    if "--backfill" in sys.argv:
        i = sys.argv.index("--backfill")
        src = sys.argv[i + 1] if len(sys.argv) > i + 1 else os.path.join(D, "decoded_live.jsonl")
        backfill(src)
    else:
        run()
