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
P_KILLS = os.path.join(OUT, "kills.csv")
P_INCOMING = os.path.join(OUT, "incoming.csv")
P_MOBS = os.path.join(OUT, "mob_stats.csv")
P_HITRATE = os.path.join(OUT, "hitrate.csv")
P_ATTEMPTS = os.path.join(OUT, "attempts.csv")
P_STATUS = os.path.join(OUT, "status.json")
P_MODEL = os.path.join(OUT, "swing_model.md")
P_STATE = os.path.join(OUT, "agent_state.json")
P_RAW = os.path.join(OUT, "raw_packets.jsonl")
P_LOCK = os.path.join(OUT, "agent.lock")
P_LOG = os.path.join(OUT, "agent.log")

STALE = 30      # seconds without a heartbeat before a lock is considered abandoned

# Every damage row is self-contained: WHO we hit (zone+mob identity) and exactly what our
# character looked like at that instant (equipped stat vector + the loadout that produced
# it), so a row never has to be joined against session state to be usable in a fit.
SWING_COLS = [
    "ts", "eid", "look", "mob", "zone", "gear",
    "dmg", "crit", "mob_hp_after", "mob_hp_before",
    "level", "might", "grace", "will",
    "might_base", "grace_base", "will_base",
    "dam", "hit", "ac", "maxhp", "maxmana", "stats_age_ms", "weapon",
]
# One row per SWING ATTEMPT (hit or miss) -- the table P(hit) is fit from. Carries the same
# stat vector as a damage row plus the geometry, since a Rogue's flank/backstab bonuses make
# relative position a predictor of both landing and damage.
ATTEMPT_COLS = [
    "ts", "eid", "mob", "zone", "hit", "dmg",
    "level", "might", "grace", "will", "dam", "hit_stat", "ac",
    "self_x", "self_y", "mob_x", "mob_y", "facing", "rel_dir", "dist", "weapon",
    # The EXPERIMENTAL CONDITION. `hit_stat`/`ac` come from 0x08 sub 0x19, which fires on
    # its own schedule and NOT when gear changes -- so after a swap they stay stale and the
    # row silently claims the old value. The worn loadout comes from the 0x39 profile the
    # instant the swap is verified, so it is the trustworthy label for which arm a row
    # belongs to.
    "gear",
]
KILL_COLS = [
    "ts", "eid", "look", "mob", "zone", "gear",
    "hp_total", "swings", "last_dmg", "bar_max", "clean", "level", "exp",
]


def append_csv(path, rows, cols):
    """Append rows to a CSV, keeping the on-disk header authoritative.

    If the existing file's header differs from `cols` (we added a column), the old
    file is rotated aside rather than appended to -- appending a wider row set to a
    narrower header silently shifts every value into the wrong column.
    """
    if not rows:
        return
    header = None
    if os.path.exists(path):
        try:
            with open(path, newline="", encoding="utf-8") as f:
                header = next(csv.reader(f), None)
        except OSError:
            header = None
    if header is not None and header != cols:
        bak = f"{path}.{time.strftime('%Y%m%d-%H%M%S')}.bak"
        try:
            os.replace(path, bak)
        except OSError:
            pass
        header = None
    with open(path, "a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=cols, extrasaction="ignore")
        if header is None:
            w.writeheader()
        for r in rows:
            w.writerow({c: r.get(c, "") for c in cols})


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

// --- SEND side: count outgoing ATTACK frames (opcode 0x13, the spacebar swing).
// The frame header is PLAINTEXT (AA | len_be16 | opcode | inc | body), so we read
// the opcode straight off the wire with no decryption. This is the ONLY way to see
// misses: the decrypt hook above only fires on a landed hit (server sends 0x13 back
// with damage). attacks - hits = misses. 7.x client is IOCP-based, so WSASend is the
// real egress path; we hook blocking send() too for safety (only one fires per build).
function scanAttacks(ptr, n){
  try{
    let o = 0;
    while(o + 4 <= n){
      if(ptr.add(o).readU8() === 0xAA){
        const len = (ptr.add(o+1).readU8() << 8) | ptr.add(o+2).readU8();  // bytes after AA
        if(len < 4 || len > 4096){ o++; continue; }                        // not a real frame
        if(ptr.add(o+3).readU8() === 0x13) send({t:'atk', ts:Date.now()}); // attack trigger
        o += 1 + len;
      } else { o++; }
    }
  }catch(e){}
}
function hookSend(mod, name){
  let a = null;
  try{ const m = Process.findModuleByName(mod); if(m) a = m.findExportByName(name); }catch(e){}
  if(!a){ try{ a = Module.findExportByName(mod, name); }catch(e){} }
  if(!a) return;
  if(name === 'WSASend'){
    Interceptor.attach(a, { onEnter(args){
      try{
        const bufs = args[1]; const cnt = args[2].toInt32();
        for(let i=0;i<cnt;i++){                       // WSABUF[]: {u_long len; char* buf} = 8 bytes on x86
          const wb = bufs.add(i*8);
          scanAttacks(wb.add(4).readPointer(), wb.readU32());
        }
      }catch(e){}
    }});
  } else {
    Interceptor.attach(a, { onEnter(args){ scanAttacks(args[1], args[2].toInt32()); } });
  }
}
hookSend('ws2_32.dll', 'WSASend');
hookSend('ws2_32.dll', 'send');
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
        # per-attempt (hit AND miss) logging -- see on_attack/resolve_attempts
        self.swing_ctx = {}         # set by the bot right before it fires: target + geometry
        self.attempts_open = []     # fired, not yet resolved to hit/miss
        self.attempts = []          # resolved, pending flush
        self.hits_by_eid = {}       # eid -> [(ts, dmg)], for attempt pairing
        self.stats_ts = 0           # when the equipped stat vector was last refreshed from
                                    # the wire; rows carry its age so staleness is visible
                                    # rather than silently baked into a fit
        self.curhp = None
        self.levels = {}            # level -> base row
        self.diffs = []
        self.entities = {}          # entity id -> look
        self.mobhp = {}             # entity id -> last hp-bar reading
        self.swings = []
        # --- experiment context, set by the bot; written onto EVERY swing/kill row so a row
        # is self-describing. Never join these on afterwards: level and gear change DURING a
        # session, so a later join would silently mislabel rows with the wrong stat vector. ---
        self.mob_names = {}             # eid -> server-reported name (0x0a tile query)
        self.zone = ""                  # current room/zone name
        self.weapon = ""                # equipped weapon name -> joins to auto/item_stats.csv
                                        # for Damage S/L, the ONE combat input the character
                                        # stat vector can never carry (it isn't a char stat)
        self.gear_sig = ""              # worn loadout signature (which gear was on for this hit)
        self.hits = []                  # (ts, look) of every LANDED hit (0x13 recv, dmg>0),
                                        # recorded even before a statblock is known (unlike swings)
        self.attacks = []               # ts of every outgoing 0x13 attack (hit OR miss)
        self.fight_windows = []         # (start_ts, end_ts, look) per completed fight, so an
                                        # attack outside any active fight (overkill on a dead mob,
                                        # retargeting between kills) is EXCLUDED from the hit rate
        self.pending_name = None
        # --- mob HP / AC derivation ---
        self.spawned = set()        # eids we watched spawn -> we saw their FULL health bar
        self.fights = {}            # eid -> {look, total, swings, barmax}
        self.kills = []
        self.barmax = {}            # look -> largest hp-bar value ever seen
        self.await_exp = None       # (kill record, ts) waiting for the "N experience!" text
        # --- incoming damage: the ONE place we have ground truth, because YOUR AC is
        # known exactly from packets and you can vary it by changing armor. Lets us test
        # dmg_in = attack x (1 + AC/100) against a known AC instead of assuming RTK's law.
        self.prev_hp = None
        self.incoming = []
        self.load()

    # ---------- persistence ----------
    def load(self):
        if os.path.exists(P_STATE):
            try:
                st = json.load(open(P_STATE, encoding="utf-8"))
                self.gear.update({k: st.get("gear", {}).get(k, 0) for k in VEC})
                self.gear_known = st.get("gear_known", True)
                # The equipped stat vector only changes via events that THEMSELVES send a
                # statblock, so a cached vector stays valid until the next one arrives --
                # and a grind run may see none at all (they fire on change, not on a timer).
                # Without this, every row in such a run carries no stats.
                if st.get("cur"):
                    self.cur = st["cur"]
                    self.level = self.cur.get("level")
                for k in ("ac", "dam", "hit"):
                    if st.get(k) is not None:
                        setattr(self, k, st[k])
                self.stats_ts = st.get("stats_ts") or 0
            except Exception:
                pass
        if os.path.exists(P_LEVELS):
            try:
                for r in csv.DictReader(open(P_LEVELS, encoding="utf-8")):
                    self.levels[int(r["level"])] = r
            except Exception:
                pass

    def save_state(self):
        json.dump({"gear": self.gear, "gear_known": self.gear_known,
                   "cur": self.cur, "ac": self.ac, "dam": self.dam, "hit": self.hit,
                   "stats_ts": getattr(self, "stats_ts", 0)},
                  open(P_STATE, "w", encoding="utf-8"), indent=1)

    def base_of(self, s):
        """Equipped reading -> absolute base, by subtracting the running gear bonus.

        Only meaningful if the gear total was actually observed (i.e. we watched every
        piece come off from naked). Otherwise `gear` holds a partial set of diffs and
        subtracting it invents nonsense -- it produced might_base = -4 from a stale
        cross-session dict. When the total isn't trustworthy, report the equipped value
        rather than a fabricated base.
        """
        if not self.gear_known:
            return {k: s[k] for k in VEC}
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
                eid = be(d, 8, 4)
                self.entities[eid] = be(d, 12, 2) & 0x7fff
                self.spawned.add(eid)      # watched from birth -> its kill total == full HP
            elif op == 0x0a:
                txt = "".join(chr(c) for c in d[1:] if 32 <= c < 127)
                if "experience" in txt:
                    self.on_exp_text(txt, ts)

    def on_attack(self, ts):
        """Every outgoing 0x13 (spacebar swing), hit or miss. Paired against landed
        hits (on_mobhp) in hit_rate_rows() to recover the live per-mob hit rate.

        Also emits a PER-ATTEMPT row. swings.csv only exists for dmg>0, so a miss leaves
        no trace there and P(hit) cannot be fit from it -- which is precisely what a +hit
        item is meant to move. Each attempt is stamped with the full stat vector and the
        geometry at swing time (Rogue flank/backstab bonuses make relative position a real
        predictor), then resolved to hit/miss once the pairing window closes.
        """
        with self.lock:
            self.attacks.append(ts)
            if not self.swing_ctx:
                return          # no adjacent target -> not a real attempt; logging it
                                # would feed P(hit) a guaranteed miss (calibration swings)
            ctx = dict(self.swing_ctx)
            self.attempts_open.append({"ts": ts, **ctx})

    def resolve_attempts(self, window=800):
        """Close out attempts older than `window` ms: an attempt is a HIT if a landed hit
        on the same target arrived within the window, else a MISS."""
        now = time.time() * 1000
        done, keep = [], []
        for a in self.attempts_open:
            if now - a["ts"] < window + 200:
                keep.append(a)
                continue
            eid = a.get("eid")
            landed = [h for h in self.hits_by_eid.get(eid, [])
                      if 0 <= h[0] - a["ts"] <= window]
            a["hit"] = 1 if landed else 0
            a["dmg"] = landed[0][1] if landed else 0
            done.append(a)
        self.attempts_open = keep
        self.attempts.extend(done)

    def hit_rate_rows(self, window=800, grace=600):
        """IN-FIGHT hit rate. An attack is only counted if it falls inside an active
        fight window [first_hit-grace, death+grace] for some mob -- this drops the
        overkill swings on an already-dead mob and the retargeting swings between rapid
        kills, which are real misses on the wire but NOT hit-formula misses (they'd
        deflate fast-dying mobs like squirrels). Within a fight, an attack that pairs to
        a landed hit within `window` ms is a hit; otherwise a genuine in-fight miss.
        The mob label comes from the FIGHT WINDOW, not nearest-hit guessing."""
        import bisect
        # active fight windows: completed + any still in progress (start..last hit)
        wins = list(self.fight_windows)
        for f in self.fights.values():
            wins.append((f["start"], f.get("last_ts", f["start"]), str(f["look"])))
        wins = sorted((s - grace, e + grace, lk) for (s, e, lk) in wins)
        wstart = [w[0] for w in wins]

        hits = sorted(self.hits, key=lambda h: h[0])
        hts = [h[0] for h in hits]
        used = [False] * len(hits)
        per = {}
        def bump(look, key):
            per.setdefault(look, {"hits": 0, "misses": 0})[key] += 1

        for a in sorted(self.attacks):
            # which fight window (if any) contains this attack? (padded windows may overlap)
            j = bisect.bisect_right(wstart, a) - 1
            look = None
            for w in wins[max(0, j - 2): j + 3]:
                if w[0] <= a <= w[1]:
                    look = w[2]
                    break
            if look is None:
                continue                       # not in a fight -> excluded (overkill/retarget)
            k = bisect.bisect_left(hts, a)
            landed = None
            while k < len(hits) and hits[k][0] - a <= window:
                if not used[k]:
                    landed = k
                    break
                k += 1
            if landed is not None:
                used[landed] = True
                bump(look, "hits")
            else:
                bump(look, "misses")
        for r in per.values():
            n = r["hits"] + r["misses"]
            r["rate"] = round(100 * r["hits"] / n, 1) if n else 0.0
        return per

    def on_exp_text(self, txt, ts):
        """'N experience!' right after a kill -> that mob's exp value."""
        import re
        m = re.search(r"(\d+)\s*experience", txt)
        if not m or not self.await_exp:
            return
        kill, kts = self.await_exp
        if ts - kts <= 3000:
            kill["exp"] = int(m.group(1))
        self.await_exp = None

    def on_vitals(self, d, ts):
        sub = d[1]
        if sub == 0x19 and len(d) >= 29:
            self.exp, self.tnl = be(d, 4, 2), be(d, 24, 2)
            prev_adh = (self.ac, self.dam, self.hit)
            self.ac, self.dam, self.hit = d[26], d[27], d[28]
            self.stats_ts = ts
            if (self.ac, self.dam, self.hit) != prev_adh:
                # persist immediately: sub 0x19 can go many minutes without firing, so a run
                # that starts before one arrives would log every row with a blank ac/dam/hit
                # (which silently broke the +hit ring experiment -- rows knew nothing of it)
                try:
                    self.save_state()
                except Exception:
                    pass
        elif sub == 0x38 and len(d) >= 14:
            hp = be(d, 4, 2)
            self.on_hp_change(hp, ts)
            self.curhp, self.exp = hp, be(d, 12, 2)
        elif sub in (0x58, 0x59, 0x78, 0x79):
            s = parse_statblock(d)
            if not s:
                return
            self.on_statblock(s, sub, ts)

    def on_hp_change(self, hp, ts):
        """A drop in current HP == a hit taken, stamped with your KNOWN AC."""
        prev = self.prev_hp
        self.prev_hp = hp
        if prev is None or hp >= prev or self.ac is None:
            return
        drop = prev - hp
        maxhp = self.cur["maxhp"] if self.cur else 0
        # a revive/level-up/map-change can move HP for non-combat reasons; a "hit" that
        # exceeds a big fraction of max HP is more likely one of those than a real swing
        if maxhp and drop > maxhp * 0.6:
            return
        nearby = collections.Counter(
            self.entities.get(e, "") for e in self.mobhp
            if self.mobhp.get(e, 0) > 0)
        self.incoming.append({
            "ts": ts, "dmg": drop, "ac": self.ac, "level": self.level,
            "maxhp": maxhp, "hp_after": hp,
            "nearby": ";".join(f"{k}" for k, _ in nearby.most_common(3) if k != ""),
        })

    def on_statblock(self, s, sub, ts):
        """THE core of gear accounting."""
        prev = self.cur
        self.stats_ts = ts
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
            # A same-level stat change is NOT necessarily gear: BUFF SPELLS move the exact
            # same fields (casting `Might` shows up as +3 might, and it lapsing as -3).
            # Booking those as equipment silently corrupts `self.gear`, which base_of()
            # subtracts -- so it would poison every *_base column downstream. We cannot
            # tell the two apart from the statblock alone (the 0x39 item list could, but it
            # only fires when the player opens their profile), so record the event and
            # leave the gear total alone unless it was established by a deliberate
            # calibration (calibrate_base_stats.py, which sets gear_known).
            self.log_gear(ts, s["level"], delta)
            self.record_level(s, ts, delta=None, resync=True)

        self.cur, self.level = s, s["level"]
        # Persist EVERY refreshed vector. Statblocks only arrive on change (a level-up, a
        # gear swap), so if the newest one isn't written to disk, the next run starts from
        # a stale vector and silently labels its rows with the OLD level/stats.
        try:
            self.save_state()
        except Exception:
            pass
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
        look = self.entities.get(eid, "")
        if look != "":
            self.barmax[look] = max(self.barmax.get(look, 0), hp)

        # accumulate the fight: real max HP == total TRUE damage taken to reach 0.
        # (body[6] is a scaled display bar, so it can't be read as HP directly.)
        if dmg > 0:
            self.hits.append((ts, str(look)))   # landed hit, for attack<->hit pairing; str for stable sort/keys
            by = self.hits_by_eid.setdefault(eid, [])
            by.append((ts, dmg))                # per-target, for per-ATTEMPT hit/miss pairing
            if len(by) > 200:
                del by[:100]
            f = self.fights.setdefault(eid, {"look": look, "total": 0, "swings": 0,
                                             "start": ts, "barmax": hp, "last": 0})
            f["total"] += dmg
            f["swings"] += 1
            f["last"] = dmg          # the killing blow overkills, so HP is bounded below
                                     # by (total - last), not equal to total
            f["last_ts"] = ts        # newest hit ts -> upper edge of the active fight window
            f["barmax"] = max(f["barmax"], hp)
            if f["look"] == "" and look != "":
                f["look"] = look
        if hp == 0 and eid in self.fights:
            f = self.fights.pop(eid)
            self.fight_windows.append((f["start"], ts, str(f["look"])))   # for in-fight gating
            if len(self.fight_windows) > 4000:
                self.fight_windows = self.fight_windows[-3000:]
            self.kills.append({
                "ts": ts, "eid": eid, "look": f["look"],
                "hp_total": f["total"], "swings": f["swings"],
                "last_dmg": f["last"], "bar_max": f["barmax"],
                # only a mob we watched spawn is guaranteed to have been at FULL health
                # when we started hitting it; others may be partial and would understate HP.
                "clean": 1 if eid in self.spawned else 0,
                "mob": self.mob_names.get(eid, ""), "zone": self.zone, "gear": self.gear_sig,
                "level": self.cur["level"] if self.cur else "",
                "exp": "",
            })
            self.await_exp = (self.kills[-1], ts)
            self.spawned.discard(eid)

        if dmg <= 0 or self.cur is None:
            return
        base = self.base_of(self.cur)
        self.swings.append({
            "ts": ts, "eid": eid, "look": self.entities.get(eid, ""),
            "mob": self.mob_names.get(eid, ""), "zone": self.zone, "gear": self.gear_sig,
            "dmg": dmg, "crit": 1 if flags & 0x40 else 0,
            "mob_hp_after": hp, "mob_hp_before": prev if prev is not None else "",
            "level": self.cur["level"],
            "might": self.cur["might"], "grace": self.cur["grace"], "will": self.cur["will"],
            "might_base": base["might"], "grace_base": base["grace"], "will_base": base["will"],
            "dam": self.dam if self.dam is not None else "",
            "hit": self.hit if self.hit is not None else "",
            "ac": self.ac if self.ac is not None else "",
            "maxhp": self.cur.get("maxhp", ""), "maxmana": self.cur.get("maxmana", ""),
            # how old the equipped stat vector is at this hit: 0 = refreshed this session,
            # large = carried over from a previous run and not yet re-confirmed
            "stats_age_ms": (ts - self.stats_ts) if self.stats_ts else "",
            "weapon": self.weapon,
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
        try:
            rates = self.hit_rate_rows()
            if rates:
                with open(P_HITRATE, "w", newline="", encoding="utf-8") as f:
                    w = csv.writer(f)
                    w.writerow(["look", "attacks", "hits", "misses", "hit_rate_pct"])
                    for look in sorted(rates, key=lambda k: -(rates[k]["hits"] + rates[k]["misses"])):
                        r = rates[look]
                        w.writerow([look, r["hits"] + r["misses"], r["hits"], r["misses"], r["rate"]])
        except Exception as e:
            log(f"hitrate write skipped: {e}")   # never let hit-rate math kill the daemon
        st = {"level": self.level, "exp": self.exp, "tnl": self.tnl,
              "curhp": self.curhp, "ac": self.ac, "dam": self.dam, "hit": self.hit,
              "attacks": len(self.attacks),
              "equipped": self.cur, "gear_bonus": self.gear,
              "base": self.base_of(self.cur) if self.cur else None,
              "levels_captured": sorted(self.levels), "swings": len(self.swings),
              "updated": time.strftime("%Y-%m-%d %H:%M:%S")}
        json.dump(st, open(P_STATUS, "w", encoding="utf-8"), indent=1, default=str)

    def flush_swings(self):
        with self.lock:
            self.resolve_attempts()   # close out fired swings into hit/miss rows
            if self.swings:
                append_csv(P_SWINGS, self.swings, SWING_COLS)
                self.swings = []
            # hold back the newest kill briefly so its "N experience!" text can land
            ready = [k for k in self.kills if k["exp"] != "" or time.time() * 1000 - k["ts"] > 4000]
            if ready:
                append_csv(P_KILLS, ready, KILL_COLS)
                done = {id(k) for k in ready}
                self.kills = [k for k in self.kills if id(k) not in done]
            if self.attempts:
                append_csv(P_ATTEMPTS, self.attempts, ATTEMPT_COLS)
                self.attempts = []
            if self.incoming:
                append_csv(P_INCOMING, self.incoming,
                           ["ts", "dmg", "ac", "level", "maxhp", "hp_after", "nearby"])
                self.incoming = []


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
    out += mob_section(L)
    out += ac_section(L)
    out += incoming_section()
    open(P_MODEL, "w", encoding="utf-8").write("\n".join(out) + "\n")


def ac_section(L):
    """Absolute mob AC, IF the data supports it.

    dmg = f(your stats) x (1 + AC/100). Rescaling f and AC inversely gives identical
    predictions, so regression on damage ALONE can never pin AC -- only the product is
    observable. The degeneracy breaks only with a known coefficient: RTK's damage term
    is DAM*2.5, so regressing dmg vs DAM *on one mob* gives slope = 2.5*(1+AC/100),
    and AC = 100*(slope/2.5 - 1). That needs DAM to VARY on the same mob (swap weapons).
    """
    out = ["", "## Absolute mob AC (needs DAM to vary per mob AT A FIXED LEVEL)"]
    # DAM must vary with everything else held still. Comparing across levels would let
    # 9 levels of might/level growth masquerade as the DAM slope.
    per = collections.defaultdict(list)
    for d, m, dam, lv, cr, look in L:
        if not cr:
            per[(look, lv, m)].append((dam, d))
    usable = {k: v for k, v in per.items() if len({x[0] for x in v}) >= 2}
    if not usable:
        dams = sorted({x[0] for v in per.values() for x in v})
        return out + ["",
                      f"**Not yet possible.** DAM values seen: {dams}, but never two of them",
                      "on the same mob at the same level/might -- so any slope would be",
                      "confounded by level progression, not a DAM effect.",
                      "",
                      "Damage ratios alone give only a RELATIVE armor ladder (above). The",
                      "attack-scale vs armor degeneracy is mathematical, not a data-volume",
                      "problem -- more grinding will never fix it.",
                      "",
                      "**The controlled experiment that unlocks it:** park at ONE level, farm",
                      "ONE mob type, and swap between 2 weapons of different DAM. That single",
                      "session pins that mob's AC, and the relative ladder carries it to every",
                      "other mob."]
    out += ["", "| look | level | might | DAM values | slope | implied AC = 100*(slope/2.5 - 1) |",
            "|---|---|---|---|---|---|"]
    for (look, lv, m), v in sorted(usable.items(), key=lambda x: -len(x[1])):
        n = len(v)
        sx = sum(x[0] for x in v); sy = sum(x[1] for x in v)
        sxx = sum(x[0] * x[0] for x in v); sxy = sum(x[0] * x[1] for x in v)
        den = n * sxx - sx * sx
        if not den:
            continue
        slope = (n * sxy - sx * sy) / den
        out.append(f"| {look} | {lv} | {m:.0f} | {sorted({x[0] for x in v})} | {slope:.2f} | "
                   f"{100*(slope/2.5 - 1):.0f} |")
    out += ["", "_Assumes RTK's DAM*2.5 term holds on the live server. If it doesn't, these",
            "are wrong by exactly that factor -- cross-check against the incoming-damage",
            "test below, which uses YOUR known AC and assumes nothing._"]
    return out


def incoming_section():
    """Test the armor law where we have ground truth: YOUR OWN AC.

    dmg_taken = mob_attack x (1 + yourAC/100). Your AC is read exactly from packets and
    changes when you swap armor, so this validates the (1 + AC/100) form itself on the
    LIVE server -- no RTK assumption required.
    """
    out = ["", "## Armor law check, using YOUR known AC"]
    if not os.path.exists(P_INCOMING):
        return out + ["", "_no incoming hits recorded yet_"]
    rows = list(csv.DictReader(open(P_INCOMING, encoding="utf-8")))
    # AC must vary against the SAME attacker. Across levels you are fighting different
    # mobs entirely, so a damage change reflects the attacker, not your armor.
    grp = collections.defaultdict(lambda: collections.defaultdict(list))
    for r in rows:
        try:
            grp[(r.get("nearby", ""), int(r["level"]))][int(r["ac"])].append(int(r["dmg"]))
        except Exception:
            pass
    controlled = {k: v for k, v in grp.items() if len(v) >= 2 and k[0]}
    if not controlled:
        seen = sorted({int(r["ac"]) for r in rows if (r.get("ac") or "").lstrip("-").isdigit()})
        return out + ["", f"AC values seen across the whole capture: {seen} -- but never two",
                      "of them against the same attacker at the same level, so nothing here is",
                      "a controlled comparison yet.",
                      "",
                      "**The experiment:** stand in one spot taking hits from one mob type and",
                      "toggle a piece of armor on and off. Your AC is known exactly from the",
                      "packets, so that directly measures the armor law with zero assumptions."]
    out += ["", "| attacker | level | your AC | hits | mean dmg | ratio vs first | predicted |",
            "|---|---|---|---|---|---|---|"]
    for (near, lv), byac in sorted(controlled.items(), key=lambda x: -sum(len(y) for y in x[1].values())):
        acs = sorted(byac)
        ref_ac = acs[0]
        ref = sum(byac[ref_ac]) / len(byac[ref_ac])
        for ac in acs:
            v = byac[ac]
            mean = sum(v) / len(v)
            pred = (1 + ac / 100) / (1 + ref_ac / 100)
            out.append(f"| {near} | {lv} | {ac} | {len(v)} | {mean:.1f} | "
                       f"{(mean/ref if ref else 0):.3f} | {pred:.3f} |")
    out += ["", "_'ratio vs first' should track 'predicted' if damage really scales as",
            "(1 + AC/100). Agreement confirms the armor law live and lets the DAM*2.5",
            "anchor above be trusted; disagreement means the live formula differs from RTK._"]
    return out


def mob_section(L):
    """Derive per-mob max HP (from clean kills) and RELATIVE armor (from damage ratios).

    HP: a mob's real max HP == the total TRUE damage it absorbed over a kill we watched
        from its spawn. The 0x13 hp byte is a scaled bar, so it can't be read directly;
        bar_max/hp_total tells us that scale empirically.
    AC: RTK applies damage x(1 + mobArmor/100), so for the SAME player stats the ratio of
        mean damage between two mobs is (1+AC_a/100)/(1+AC_b/100). That yields armor
        RELATIVE to a reference; absolute AC needs one mob's true AC as an anchor, so we
        report it against the softest observed mob (assumed AC 0) and label it as such.
    """
    out = ["", "## Mob HP + armor"]
    if not os.path.exists(P_KILLS):
        return out + ["", "_no kills recorded yet_"]
    kills = list(csv.DictReader(open(P_KILLS, encoding="utf-8")))
    if not kills:
        return out + ["", "_no kills recorded yet_"]

    bylook = collections.defaultdict(list)
    for k in kills:
        try:
            hp, clean = int(k["hp_total"]), k.get("clean") == "1"
        except Exception:
            continue
        if clean and hp > 0:
            bylook[k.get("look", "")].append(
                (hp, int(k["swings"] or 0), float(k["bar_max"] or 0),
                 int(k["exp"]) if (k.get("exp") or "").isdigit() else None,
                 int(k.get("last_dmg") or 0)))

    # frontal (non-crit, low-cluster) mean damage per mob -> armor proxy
    dmg_by_look = collections.defaultdict(list)
    for d, m, dam, lv, cr, look in L:
        if not cr:
            dmg_by_look[look].append(d)
    frontal = {}
    for look, ds in dmg_by_look.items():
        ds = sorted(ds)
        med = ds[len(ds) // 2]
        lo = [x for x in ds if x <= med]
        if lo:
            frontal[look] = sum(lo) / len(lo)

    if not bylook:
        out += ["", "_no CLEAN kills yet (a clean kill = one we watched from spawn, so the"
                " damage total equals full HP). Keep playing; they accumulate on their own._"]
    else:
        out += ["",
                "HP is BOUNDED, not exact: the killing blow overkills, so true max HP lies in",
                "`(total_damage - killing_blow, total_damage]`. A one-shot kill only gives an",
                "upper bound (lower bound 0), so those rows are marked and are weak evidence.",
                "",
                "| look | clean kills | HP lower | HP upper | best bound | multi-hit kills | swings | exp |",
                "|---|---|---|---|---|---|---|---|"]
        for look, v in sorted(bylook.items(), key=lambda x: -len(x[1])):
            n = len(v)
            multi = [x for x in v if x[1] >= 2]
            src = multi or v
            ups = sorted(x[0] for x in src)
            los = sorted(max(0, x[0] - x[4]) for x in src)
            up, lo = ups[len(ups) // 2], los[len(los) // 2]
            # the tightest single observation: smallest gap between bound pair
            tight = min(src, key=lambda x: x[4])
            tb = f"({max(0, tight[0]-tight[4])}, {tight[0]}]"
            sw = sum(x[1] for x in v) / n
            exps = [x[3] for x in v if x[3] is not None]
            ex = f"{sorted(exps)[len(exps)//2]}" if exps else "-"
            flag = "" if multi else "  _(1-shot only)_"
            out.append(f"| {look} | {n} | {lo} | {up} | {tb}{flag} | {len(multi)} | "
                       f"{sw:.1f} | {ex} |")

    if len(frontal) >= 2:
        ref = max(frontal.values())
        out += ["", "### Relative armor (frontal non-crit damage ratios)", "",
                "| look | frontal mean dmg | dmg vs softest | implied AC if softest = 0 |",
                "|---|---|---|---|"]
        for look, fm in sorted(frontal.items(), key=lambda x: -x[1]):
            out.append(f"| {look} | {fm:.1f} | {fm/ref:.3f} | {100*(fm/ref - 1):.0f} |")
        out += ["", "_Caveat: this assumes the softest observed mob has AC 0 and that your"
                " stats were comparable across these fights. It is a RELATIVE ladder;"
                " anchor it with one known mob AC to make it absolute._"]
    return out


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
            if p.get("t") == "atk":        # outgoing attack frame (send-side hook)
                ag.on_attack(p["ts"])
            else:
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
