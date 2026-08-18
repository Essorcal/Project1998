#!/usr/bin/env python
"""
spell_rate_probe.py -- measure how fast the LIVE 7.x client is actually allowed to cast, and
WHERE the limit lives (client-side self-pacing vs server-side dropping).

The question this answers: zaps appear to fire ~1/second in live NexusTK, but RTK has NO rate
limit on the zap path at all (global_zap.lua has no setAether, Player.canCast is state-only,
clif_parsemagic gates only on aethers/silence/deflect). So the 1/sec comes from somewhere the
RTK source doesn't model. Two candidate mechanisms, and they look completely different on the
wire:

  (a) CLIENT self-paces  -> outbound 0x0f frames arrive ~1000ms apart. The client refuses to
      send while its own cast pose is playing, so the pose length IS the cast rate.
  (b) SERVER drops       -> outbound 0x0f frames arrive ~31ms apart (the OS key-repeat rate we
      already measured on 4.95) but only ~1/sec produces an effect; the rest are answered by
      nothing at all.

Hooks (both proven in this directory already):
  IN  : the client's internal decrypt +0x178b20 -> plaintext of every inbound packet
        (borrowed from frida_decode_live.py)
  OUT : ws2_32 send/WSASend, walking the AA|len|op|inc|body framing -> opcode + timestamp of
        every outbound frame. The opcode byte is plaintext in the header, so no encrypt-side
        hook is needed to count casts (the body/slot stays encrypted -- hence one spell per
        labelled run rather than trying to demux slots).

Inbound packets that matter here:
  0x1a action  -> the CAST POSE. body = id(u32BE) type(u8) time(u16BE) param(u8); type 6 is the
                  cast pose and `time` is its length in FRAMES. If (a) is true, this number
                  times the client's frame duration should land on the observed cast interval,
                  which is the whole hypothesis in one field.
  0x13 mob-HP  -> damage actually landed
  0x29 effect / 0x19 sound -> the cast visibly/audibly resolved
  0x0a sys-text

Usage -- one spell per run, hold the key down for the whole window:
    python re/spell_rate_probe.py --attach --label spark   --seconds 60
    python re/spell_rate_probe.py --attach --label soothe  --seconds 60     # non-zap control
    python re/spell_rate_probe.py --compare

SHARED-TIMER RUNS (does casting compete with swinging for one slot?):
    python re/spell_rate_probe.py --attach --label melee      --seconds 60  # hold ATTACK only
    python re/spell_rate_probe.py --attach --label spark_melee --seconds 60 # hold ATTACK *and* cast spark
The mixed run is the decisive one. If the timer is shared, no swing ever lands inside a cast's second
and casts+swings together come to ~1/sec. If they are independent, swings keep their own ~333ms cadence
straight through the casts. The melee-only run is the baseline: it also gives the live swing rate, which
we have never actually measured -- our SwingIntervalMs=333 is inherited from RTK, not observed.

The control run is the point: `sendAction(6, 35)` is the default across ~117 RTK spell scripts,
not something zaps do specially. If a heal paces identically to a zap then this is a property
of CASTING and belongs in the cast path; if only zaps pace, it is zap-specific and belongs in
the zap path.

CAUTION: cast BY HAND for these runs. Driving it with nexus_bot.py measures the bot's own
one-action-per-tick loop and will read ~1/sec no matter what the game does.
"""
import sys, os, json, time, bisect, statistics
from collections import Counter, defaultdict

import frida

MOD = "NexusTK.exe"
DEC_RVA = 0x178B20                      # internal decrypt, from the key xref (see frida_decode_live.py)
OUTDIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "auto", "spell_rate")

CAST_OP = 0x0F                          # outbound cast
ATTACK_OP = 0x13                        # outbound melee swing (inbound 0x13 is mob-HP -- dir separates them)
ACT_SWING, ACT_CAST = 1, 6              # 0x1a action types: RTK clif.c sends 1 for a swing, 6 for a cast
OP_ACTION, OP_MOBHP, OP_EFFECT, OP_SOUND, OP_TEXT = 0x1A, 0x13, 0x29, 0x19, 0x0A
# Anything here counts as "the server acknowledged that cast".
RESPONSE_OPS = {OP_ACTION, OP_MOBHP, OP_EFFECT, OP_SOUND}
# A gap this long means the key came up: intervals are only meaningful WITHIN a held run.
# Deliberately well ABOVE the ~1000ms we are trying to measure -- a threshold of a few hundred
# ms would split a genuine 1/sec held cadence into one "run" per cast and erase the very
# distribution we came for. Hand-tapping is separated from a held key by REGULARITY instead
# (see `spread` below), not by gap size.
RUN_GAP_MS = 2500
# How long after an outbound cast we still credit an inbound packet as its answer.
RESPONSE_WINDOW_MS = 600

JS = r"""
'use strict';
const MAIN = (Process.findModuleByName && Process.findModuleByName('__MOD__')) || Process.enumerateModules()[0];

// ---- inbound: hook the internal decrypt, out[0] = opcode, rest = plaintext body ----------
Interceptor.attach(MAIN.base.add(__RVA__), {
  onEnter(args){ this.out = args[2]; },
  onLeave(ret){
    try{
      let n = ret.toInt32(); if(n <= 0) return; if(n > 24) n = 24;   // header is all we need
      const b = new Uint8Array(this.out.readByteArray(n));
      send({t:'in', ts:Date.now(), op:b[0],
            hex:Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ')});
    }catch(e){}
  }
});

// ---- outbound: walk AA|len(u16BE)|op|inc|body in the socket buffer -----------------------
// The opcode byte survives in the clear in the frame header, so casts are countable without
// touching the encrypt side. Buffers are packet-sized, so this walk is cheap (a JS byte-walk
// over a whole memory RANGE is what freezes this client -- this is not that).
function scanOut(ptr, n){
  try{
    let o = 0;
    while(o + 4 <= n){
      if(ptr.add(o).readU8() === 0xAA){
        const len = (ptr.add(o+1).readU8() << 8) | ptr.add(o+2).readU8();
        if(len < 4 || len > 4096){ o++; continue; }
        send({t:'out', ts:Date.now(), op:ptr.add(o+3).readU8(), inc:ptr.add(o+4).readU8()});
        o += 1 + len;
      } else o++;
    }
  }catch(e){}
}
function hook(mod, name){
  let a = null;
  try{ const m = Process.findModuleByName(mod); if(m) a = m.findExportByName(name); }catch(e){}
  if(!a){ try{ a = Module.findExportByName(mod, name); }catch(e){} }
  if(!a) return;
  if(name === 'WSASend'){
    Interceptor.attach(a, {onEnter(args){ try{
      const bufs = args[1], cnt = args[2].toInt32();
      for(let i = 0; i < cnt; i++){ const wb = bufs.add(i*8); scanOut(wb.add(4).readPointer(), wb.readU32()); }
    }catch(e){} }});
  } else {
    Interceptor.attach(a, {onEnter(args){ scanOut(args[1], args[2].toInt32()); }});
  }
}
hook('ws2_32.dll', 'WSASend');
hook('ws2_32.dll', 'send');

send({t:'info', m:'hooks installed: decrypt +0x__RVAHEX__, ws2_32 send/WSASend'});
""".replace("__MOD__", MOD).replace("__RVA__", hex(DEC_RVA)).replace("__RVAHEX__", format(DEC_RVA, "x"))


# ---------------------------------------------------------------------------- helpers -----
def pct(vals, p):
    """Percentile without numpy. vals must be non-empty."""
    s = sorted(vals)
    if len(s) == 1:
        return s[0]
    i = (len(s) - 1) * p / 100.0
    lo, hi = int(i), min(int(i) + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (i - lo)


def parse_action(hexstr):
    """0x1a body = id(u32BE) type(u8) time(u16BE) param(u8) -> (eid, type, time_frames) or None.

    Layout confirmed against live 7.x capture, and identical to our 4.95 SendAction
    (Server/Session.Combat.cs): `1a 00 0a 9e c5 06 00 1e 00` = eid 696005, type 6, time 30.
    """
    b = bytes(int(x, 16) for x in hexstr.split())
    if len(b) < 9 or b[0] != OP_ACTION:
        return None
    return int.from_bytes(b[1:5], "big"), b[5], (b[6] << 8) | b[7]


def entity_of(hexstr, op):
    """Entity id carried by an inbound packet, for the ops that key on one."""
    b = bytes(int(x, 16) for x in hexstr.split())
    if op == OP_ACTION and len(b) >= 5:
        return int.from_bytes(b[1:5], "big")
    if op == OP_EFFECT and len(b) >= 5:
        return int.from_bytes(b[1:5], "big")
    return None


def infer_self_id(rows):
    """Which entity are WE?

    0x1a is broadcast for every nearby entity's action, so raw pose counts mix in other players
    and mobs. Our own id is the one whose actions cluster right behind our own outbound casts.
    Self-calibrating, so it survives attaching mid-session (which misses the 0x33 self-entity
    push entirely).
    """
    casts = sorted(r["ts"] for r in rows if r["dir"] == "out" and r["op"] == CAST_OP)
    if not casts:
        return None
    score = Counter()
    for r in rows:
        if r["dir"] != "in" or r["op"] != OP_ACTION:
            continue
        eid = entity_of(r["hex"], OP_ACTION)
        if eid is None:
            continue
        i = bisect.bisect_right(casts, r["ts"])
        if i and r["ts"] - casts[i - 1] <= RESPONSE_WINDOW_MS:
            score[eid] += 1
    return score.most_common(1)[0][0] if score else None


def split_runs(ts):
    """Split cast timestamps into held-key runs; intervals only mean something within a run."""
    runs, cur = [], []
    for t in ts:
        if cur and t - cur[-1] > RUN_GAP_MS:
            runs.append(cur); cur = []
        cur.append(t)
    if cur:
        runs.append(cur)
    return runs


# --------------------------------------------------------------------------- analysis -----
def analyse(rows, label):
    """Reduce one capture to the numbers that separate client-pacing from server-dropping."""
    outs = [r for r in rows if r["dir"] == "out"]
    ins = [r for r in rows if r["dir"] == "in"]
    casts = [r["ts"] for r in outs if r["op"] == CAST_OP]

    res = {"label": label, "casts": len(casts), "runs": 0, "gaps": [], "poses": Counter(),
           "responses": 0, "unanswered": 0, "latencies": [], "effect_gaps": [],
           "in_ops": Counter(r["op"] for r in ins), "span_s": 0.0,
           "self_id": None, "foreign_actions": 0,
           "confirms": [], "confirm_gaps": [], "per_window": Counter(),
           "swings_sent": [], "swing_poses": [], "swing_gaps": [],
           "combined_gaps": [], "cross_gaps": []}
    if not casts:
        return res

    all_ts = [r["ts"] for r in rows]
    res["span_s"] = (max(all_ts) - min(all_ts)) / 1000.0

    runs = split_runs(casts)
    res["runs"] = len(runs)
    for run in runs:
        res["gaps"] += [b - a for a, b in zip(run, run[1:])]

    # 0x1a/0x29 are broadcast for EVERY nearby entity. Without this filter, other players'
    # actions and mob swings inflate the pose rate and mask dropped casts.
    self_id = infer_self_id(rows)
    res["self_id"] = self_id

    # Cast poses: type + length in frames, straight off the wire, OURS only.
    for r in ins:
        if r["op"] != OP_ACTION:
            continue
        p = parse_action(r["hex"])
        if not p:
            continue
        eid, typ, frames = p
        if self_id is not None and eid != self_id:
            res["foreign_actions"] += 1
            continue
        res["poses"][(typ, frames)] += 1

    # Spacing of our own pose. NOTE: near-zero gaps are REAL casts, not duplicate broadcasts --
    # a burst of casts inside one second sends a pose each, they just arrive together and the
    # client renders one animation because each re-asserts a pose that has not expired. An
    # earlier version of this script filtered sub-20ms gaps out as noise and thereby deleted the
    # exact evidence that three casts had landed. Do not re-add that filter.
    pose_ts = [r["ts"] for r in ins if r["op"] == OP_ACTION
               and (self_id is None or entity_of(r["hex"], OP_ACTION) == self_id)]
    res["effect_gaps"] = [b - a for a, b in zip(pose_ts, pose_ts[1:]) if b - a < 5000]

    # THE GROUND TRUTH: "You cast <name>." in the 0x0a minitext channel. One line per cast that
    # the server actually resolved, so it counts a burst of 3 as 3 -- which the animation does
    # not. This, not the pose, is the served rate.
    res["confirms"] = [r["ts"] for r in ins if r["op"] == OP_TEXT and b"You cast" in
                       bytes(int(x, 16) for x in r["hex"].split())]
    res["per_window"] = Counter(Counter(t // 1000 for t in res["confirms"]).values())
    res["confirm_gaps"] = [b - a for a, b in zip(res["confirms"], res["confirms"][1:]) if b - a < 5000]

    # ---- SHARED-TIMER TEST -------------------------------------------------------------------------
    # Hypothesis: casting and swinging run off ONE timer, so you cannot swing and zap at the same time.
    # If SHARED, no swing ever lands inside a cast's second and casts+swings together come to ~1/sec.
    # If INDEPENDENT, swings keep their own ~333ms cadence straight through the casts and the cross gaps
    # fill in near zero. Swing poses are the ground truth for a landed swing, the same way the "You cast"
    # line is for a cast -- an outbound 0x13 only means the client asked.
    res["swings_sent"] = [r["ts"] for r in outs if r["op"] == ATTACK_OP]
    res["swing_poses"] = [r["ts"] for r in ins if r["op"] == OP_ACTION
                          and (parse_action(r["hex"]) or (None, None, None))[1] == ACT_SWING
                          and (self_id is None or entity_of(r["hex"], OP_ACTION) == self_id)]
    res["swing_gaps"] = [b - a for a, b in zip(res["swing_poses"], res["swing_poses"][1:]) if b - a < 5000]

    # Every landed action on one timeline, and how close a swing ever gets to a cast.
    combined = sorted(res["confirms"] + res["swing_poses"])
    res["combined_gaps"] = [b - a for a, b in zip(combined, combined[1:]) if b - a < 5000]
    res["cross_gaps"] = []
    if res["confirms"] and res["swing_poses"]:
        sw = sorted(res["swing_poses"])
        for c in res["confirms"]:
            i = bisect.bisect_left(sw, c)
            near = [abs(sw[j] - c) for j in (i - 1, i) if 0 <= j < len(sw)]
            if near:
                res["cross_gaps"].append(min(near))

    # Pair each outbound cast with the first inbound response inside the window. An unanswered
    # cast is the signature of a server-side drop. 0x13 keys on the TARGET's id, so it is not
    # self-filtered; 0x1a/0x29 are.
    resp_ts = sorted(r["ts"] for r in ins if r["op"] in RESPONSE_OPS
                     and not (r["op"] in (OP_ACTION, OP_EFFECT) and self_id is not None
                              and entity_of(r["hex"], r["op"]) != self_id))
    used, j = set(), 0
    for c in casts:
        while j < len(resp_ts) and resp_ts[j] < c:
            j += 1
        k, hit = j, None
        while k < len(resp_ts) and resp_ts[k] - c <= RESPONSE_WINDOW_MS:
            if k not in used:
                hit = k; break
            k += 1
        if hit is None:
            res["unanswered"] += 1
        else:
            used.add(hit)
            res["responses"] += 1
            res["latencies"].append(resp_ts[hit] - c)
    return res


def burst_size(res):
    """Casts the server resolved per one-second window -- the number we are actually after.

    Read off the modal window rather than the mean: partial windows at the edges of a held run
    drag an average down and would read a clean 3/sec as ~1.4/sec.
    """
    real = {k: v for k, v in res["per_window"].items() if k > 0}
    return max(real.items(), key=lambda kv: kv[1])[0] if real else 0


def verdict(res):
    if not res["gaps"]:
        return "no held run captured -- hold the cast key down for a few seconds"
    med = statistics.median(res["gaps"])
    drop = res["unanswered"] / max(1, res["casts"])
    eff = statistics.median(res["effect_gaps"]) if res["effect_gaps"] else None

    # Mixed run: does a swing share the cast's timer or run alongside it? Answer before the rate,
    # because it decides WHERE the limiter lives, not just how fast it ticks.
    if res["confirms"] and res["swing_poses"]:
        xg, cb = res["cross_gaps"], res["combined_gaps"]
        near = sum(1 for g in xg if g < 400) / max(1, len(xg))
        comb = statistics.median(cb) if cb else 0
        if near < 0.1 and comb >= 700:
            return (f"SHARED TIMER: swings and casts never coincide (only {near:.0%} of casts had a swing "
                    f"within 400ms) and together they come to one action every {comb:.0f}ms. Swinging and "
                    f"zapping compete for the SAME slot.")
        return (f"INDEPENDENT TIMERS: {near:.0%} of casts had a swing within 400ms, combined cadence "
                f"{comb:.0f}ms vs cast-only {statistics.median(res['confirm_gaps'] or [0]):.0f}ms. "
                f"Swings run alongside casts rather than competing.")

    # Confirmations are authoritative when present; poses are not, because a burst of casts
    # inside one second collapses into a single visible animation.
    n = burst_size(res)
    if n:
        anim = len(res["confirms"]) / max(1, sum(res["poses"].values()))
        note = f" ({anim:.1f} casts per animation)" if anim > 1.2 else ""
        if n == 1:
            return (f"1 CAST/SEC: {len(res['confirms'])} confirmations, modal window holds exactly 1"
                    f" while the client asked every ~{med:.0f}ms. Rate-limited well below the "
                    f"3/sec action budget.")
        return (f"{n} CASTS/SEC: modal window holds exactly {n} confirmations{note}, client asked "
                f"every ~{med:.0f}ms. Consistent with RTK's shared action budget (3/sec).")
    # A machine cadence (client self-pacing, or a bot tick) is TIGHT; a human tapping a key is
    # not. This is what separates them once the gap size alone stops being diagnostic.
    spread = pct(res["gaps"], 90) - pct(res["gaps"], 10)
    tight = spread < max(60.0, 0.25 * med)

    if med >= 700:
        if tight:
            return (f"CLIENT-SIDE: the client itself only sends every ~{med:.0f}ms (tight, spread "
                    f"{spread:.0f}ms), and {1-drop:.0%} of those are answered. Nothing is being "
                    f"dropped -- it refuses to ask. Cast BY HAND? A bot tick reads identically.")
        return (f"INCONCLUSIVE: ~{med:.0f}ms apart but sloppy (spread {spread:.0f}ms) -- that is a "
                f"human tapping, not a held key. Hold the key down and re-run.")
    if med < 200 and eff and eff >= 700:
        return (f"SERVER-SIDE: client sends every ~{med:.0f}ms but effects land ~{eff:.0f}ms apart "
                f"and {drop:.0%} of casts got no answer at all. The limit is on the server.")
    if med < 200 and drop > 0.4:
        return (f"SERVER-SIDE (drops): client sends every ~{med:.0f}ms, {drop:.0%} unanswered.")
    return (f"UNCLEAR: send gap ~{med:.0f}ms, {drop:.0%} unanswered"
            + (f", effects ~{eff:.0f}ms apart" if eff else "") + " -- widen the capture.")


def report(res):
    print(f"\n=== {res['label']} " + "=" * (60 - len(res['label'])))
    print(f"  window            {res['span_s']:.1f}s")
    print(f"  self entity       {res['self_id']}"
          + (f"   ({res['foreign_actions']} foreign 0x1a filtered out)" if res["foreign_actions"] else ""))
    print(f"  outbound 0x0f     {res['casts']} casts in {res['runs']} held run(s)")
    if res["gaps"]:
        g = res["gaps"]
        print(f"  send gap (ms)     median {statistics.median(g):7.1f}   "
              f"p10 {pct(g,10):6.1f}  p90 {pct(g,90):7.1f}  min {min(g):5.0f}  max {max(g):6.0f}")
        print(f"  effective rate    {1000.0/statistics.median(g):.2f} casts/sec asked for")
    # Spacing BETWEEN bursts, ignoring casts that shared a window. This is the cadence the
    # limiter enforces; the raw pose/confirm median is dominated by intra-burst ~0ms gaps and
    # would report a 3-cast burst as a 2000/sec cast rate.
    between = [g for g in res["confirm_gaps"] or res["effect_gaps"] if g >= 20]
    if between:
        print(f"  between bursts    median {statistics.median(between):7.1f}ms   "
              f"p10 {pct(between,10):6.1f}  p90 {pct(between,90):7.1f}   "
              f"-> {burst_size(res)} cast(s) every {statistics.median(between)/1000:.2f}s")
    if res["confirms"]:
        print(f"  CONFIRMED casts   {len(res['confirms'])}  "
              f"(\"You cast ...\" minitext -- counts a burst of 3 as 3, the animation does not)")
        print("  per 1s window     " + ", ".join(
            f"{k} cast(s) x{v}" for k, v in sorted(res["per_window"].items()) if k > 0)
              + f"   -> modal {burst_size(res)}/sec")
        if res["confirm_gaps"]:
            cg = res["confirm_gaps"]
            near = sum(1 for g in cg if g < 20)
            print(f"  confirm gap (ms)  median {statistics.median(cg):7.1f}   "
                  f"{near} of {len(cg)} are <20ms = same-second bursts")
    if res["swings_sent"] or res["swing_poses"]:
        print(f"  swings            {len(res['swings_sent'])} sent, {len(res['swing_poses'])} landed"
              + (f", median gap {statistics.median(res['swing_gaps']):.0f}ms" if res["swing_gaps"] else ""))
        if res["combined_gaps"]:
            cb = res["combined_gaps"]
            print(f"  casts+swings      median gap {statistics.median(cb):7.1f}ms   "
                  f"-> {1000.0/max(1e-9, statistics.median(cb)):.2f} actions/sec combined")
        if res["cross_gaps"]:
            xg = res["cross_gaps"]
            print(f"  swing<->cast      nearest swing to a cast: median {statistics.median(xg):.0f}ms, "
                  f"min {min(xg):.0f}ms  ({sum(1 for g in xg if g < 400)} of {len(xg)} casts had a swing "
                  f"within 400ms)")
    if res["poses"]:
        print("  cast pose         " + ", ".join(
            f"type={t} time={f} frames (x{n})" for (t, f), n in res["poses"].most_common(4)))
        for (t, f), _ in res["poses"].most_common(1):
            # Only meaningful at 1 cast/sec, where one burst == one pose and the between-burst
            # gap really is the pose length. At 3/sec the poses overlap and this says nothing.
            if between and f and burst_size(res) == 1:
                ms = statistics.median(between)
                print(f"                    -> {ms:.0f}ms / {f} frames = {ms/f:.2f} ms per frame")
    if res["casts"]:
        print(f"  answered          {res['responses']}/{res['casts']} "
              f"({res['unanswered']} unanswered = {res['unanswered']/res['casts']:.0%})")
    if res["latencies"]:
        print(f"  response latency  median {statistics.median(res['latencies']):.0f}ms")
    print("  inbound ops       " + ", ".join(f"0x{o:02x}:{n}" for o, n in res["in_ops"].most_common(8)))
    print(f"\n  VERDICT: {verdict(res)}")


# ---------------------------------------------------------------------------- capture -----
def capture(label, seconds):
    os.makedirs(OUTDIR, exist_ok=True)
    path = os.path.join(OUTDIR, f"{label}.jsonl")

    dev = frida.get_local_device()
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
    if not procs:
        print(f"no running {MOD} -- start the client and log in first")
        return
    if len(procs) > 1:
        print(f"[!] {len(procs)} {MOD} processes: {[p.pid for p in procs]} -- the launcher spawns "
              f"the game as a CHILD, so hooking all of them is expected (only one will produce traffic)")

    rows = []
    outf = open(path, "w", encoding="utf-8", buffering=1)

    def on_message(msg, data):
        if msg["type"] == "error":
            print("[frida-error]", msg.get("description")); return
        p = msg["payload"]
        if p.get("t") == "info":
            print("[i]", p["m"]); return
        row = {"dir": p["t"], "ts": p["ts"], "op": p["op"], "hex": p.get("hex", "")}
        rows.append(row)
        outf.write(json.dumps(row) + "\n")

    sessions = []       # MUST hold these -- Python GCs the session and silently detaches
    for p in procs:
        try:
            s = dev.attach(p.pid)
            sc = s.create_script(JS)
            sc.on("message", on_message)
            sc.load()
            sessions.append((s, sc))
        except Exception as e:
            print(f"  attach {p.pid} failed: {e}")
    if not sessions:
        print("nothing attached"); return

    print(f"\ncapturing '{label}' for {seconds}s -> {path}")
    print("HOLD THE CAST KEY DOWN on a live target. Cast BY HAND, not with the bot.\n")
    t_end = time.time() + seconds
    while time.time() < t_end:
        time.sleep(1.0)
        now = int(time.time() * 1000)
        c = sum(1 for r in rows if r["dir"] == "out" and r["op"] == CAST_OP and now - r["ts"] < 1000)
        a = sum(1 for r in rows if r["dir"] == "in" and r["op"] == OP_ACTION and now - r["ts"] < 1000)
        print(f"  [{int(t_end-time.time()):3d}s left] casts sent {c}/s   poses back {a}/s   "
              f"({len(rows)} packets)")
    outf.close()

    for s, _ in sessions:
        try: s.detach()
        except Exception: pass

    report(analyse(rows, label))
    print(f"\nsaved -> {path}")


def compare():
    if not os.path.isdir(OUTDIR):
        print(f"no captures in {OUTDIR}"); return
    results = []
    for fn in sorted(os.listdir(OUTDIR)):
        if not fn.endswith(".jsonl"):
            continue
        rows = []
        with open(os.path.join(OUTDIR, fn), encoding="utf-8") as f:
            for line in f:
                try: rows.append(json.loads(line))
                except Exception: pass        # tolerate a truncated tail
        if rows:
            results.append(analyse(rows, fn[:-6]))
    if not results:
        print("no usable captures"); return

    print(f"\n{'spell':<16}{'sent':>6}{'send gap':>10}{'confirmed':>11}{'per sec':>9}"
          f"{'pose':>9}{'casts/anim':>12}")
    print("-" * 73)
    for r in results:
        g = f"{statistics.median(r['gaps']):.0f}ms" if r["gaps"] else "-"
        p = (f"{r['poses'].most_common(1)[0][0][0]}:{r['poses'].most_common(1)[0][0][1]}"
             if r["poses"] else "-")
        n = burst_size(r)
        npose = sum(r["poses"].values())
        a = f"{len(r['confirms'])/npose:.2f}" if npose else "-"
        print(f"{r['label']:<16}{r['casts']:>6}{g:>10}{len(r['confirms']):>11}{n:>9}{p:>9}{a:>12}")
    for r in results:
        report(r)


def main():
    if "--compare" in sys.argv:
        compare(); return
    if "--attach" not in sys.argv:
        print(__doc__); return
    label = "run"
    if "--label" in sys.argv:
        label = sys.argv[sys.argv.index("--label") + 1]
    seconds = 60
    if "--seconds" in sys.argv:
        seconds = int(sys.argv[sys.argv.index("--seconds") + 1])
    capture(label, seconds)


if __name__ == "__main__":
    main()
