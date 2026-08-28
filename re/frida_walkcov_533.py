#!/usr/bin/env python
r"""
Find the 5.33 client's WALK GATE by execution-coverage diff.

The gate runs in the input/update loop, not the packet path (a blocked step sends nothing), so socket
hooks can't reach it. This Stalker-follows the client's main thread and records the set of basic-block
starts executed during a fixed window, writing them to JSON. Run it twice --

    A = walking in OPEN ground (gate PASSES -> build + enqueue the walk)
    B = pushing into a WALL    (gate BLOCKS -> no walk built)

-- then diff. Idle/background blocks appear in BOTH windows and cancel; what is left is walk-specific.
A-only = the walk builder + the gate's pass-branch. B-only = the gate's block-branch. Both hand us a
short RVA list to disassemble (python re/disx.py --533 0xVA) instead of guessing.

Main thread id comes from a per-frame tick (0x4780e0, fires every frame on the UI thread regardless of
input), so no movement is needed to identify it.

    # capture ~12s of OPEN-GROUND walking:
    python re/frida_walkcov_533.py --attach --tag A --seconds 12
    # capture ~12s of pushing a WALL:
    python re/frida_walkcov_533.py --attach --tag B --seconds 12
    # diff:
    python re/frida_walkcov_533.py --diff re/walkcov_A.json re/walkcov_B.json

Stalker slows the client hard WHILE a window is armed -- expect the game to crawl for the N seconds.
"""
import sys, os, json, time, frida
from pathlib import Path
from _paths import CLIENT5, require

HERE = os.path.dirname(os.path.abspath(__file__))
FRAME_RVA = 0x780e0

JS = r"""
'use strict';
var m = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName('NexusTK.exe') : null;
if (!m && typeof Process.getModuleByName === 'function') { try { m = Process.getModuleByName('NexusTK.exe'); } catch (e) {} }
var base = m ? m.base : ptr('0x400000');
var size = m ? m.size : 0x400000;
var lo = base, hi = base.add(size);
var FRAME_RVA = %d;

var mainTid = null, armed = false, cov = {};

Interceptor.attach(base.add(FRAME_RVA), {
  onEnter: function () {
    if (mainTid === null) { mainTid = Process.getCurrentThreadId(); send({t:'tid', tid:mainTid}); }
  }
});

function record(p) { if (p.compare(lo) >= 0 && p.compare(hi) < 0) cov['0x' + p.sub(base).add(0x400000).toString(16)] = 1; }

recv('ctl', function onCtl(msg) {
  if (msg.cmd === 'start' && mainTid !== null && !armed) {
    cov = {}; armed = true;
    Stalker.follow(mainTid, { events: { block: true }, onReceive: function (events) {
      var parsed = Stalker.parse(events, { annotate: false, stringify: false });
      for (var i = 0; i < parsed.length; i++) {
        var row = parsed[i];
        for (var j = 0; j < row.length; j++) { try { record(row[j]); } catch (e) {} }
      }
    }});
    send({t:'info', m:'ARMED'});
  } else if (msg.cmd === 'stop') {
    if (armed) { try { Stalker.unfollow(mainTid); } catch (e) {} Stalker.flush(); armed = false; }
    send({t:'cov', rvas:Object.keys(cov)});
  }
  recv('ctl', onCtl);
});
send({t:'info', m:'ready base=' + base + ' size=0x' + size.toString(16)});
""" % (FRAME_RVA,)


def do_diff(fa, fb):
    A = set(json.load(open(fa)))
    B = set(json.load(open(fb)))
    a_only = sorted(A - B, key=lambda x: int(x, 16))
    b_only = sorted(B - A, key=lambda x: int(x, 16))
    print(f"A={len(A)} B={len(B)}  A-only={len(a_only)}  B-only={len(b_only)}")
    print("\n--- A-only (walk ALLOWED: builder + gate pass-branch) ---")
    print("  " + " ".join(a_only))
    print("\n--- B-only (walk BLOCKED: gate block-branch) ---")
    print("  " + " ".join(b_only))


def main():
    argv = sys.argv[1:]
    if "--diff" in argv:
        i = argv.index("--diff")
        do_diff(argv[i + 1], argv[i + 2]); return

    tag = argv[argv.index("--tag") + 1] if "--tag" in argv else "X"
    seconds = int(argv[argv.index("--seconds") + 1]) if "--seconds" in argv else 12
    out = os.path.join(HERE, f"walkcov_{tag}.json")

    session = frida.attach("NexusTK.exe")
    script = session.create_script(JS)
    got = {"tid": None, "cov": None}

    def on_message(msg, _data):
        if msg.get("type") == "error":
            print("[ERROR]", msg.get("description", str(msg))); return
        p = msg.get("payload", {})
        if p.get("t") == "tid":
            got["tid"] = p["tid"]; print(f"[probe] main thread id = {p['tid']}")
        elif p.get("t") == "info":
            print("[probe]", p.get("m", ""))
        elif p.get("t") == "cov":
            got["cov"] = p["rvas"]

    script.on("message", on_message)
    script.load()

    print("[probe] waiting for main thread id (needs one frame)...")
    for _ in range(50):
        if got["tid"] is not None:
            break
        time.sleep(0.1)
    if got["tid"] is None:
        print("[probe] never saw the per-frame tick -- is the client focused/in-world?"); return

    print(f"\n>>> ARMING window '{tag}' for {seconds}s -- START MOVING NOW <<<")
    script.post({"type": "ctl", "cmd": "start"})
    time.sleep(seconds)
    script.post({"type": "ctl", "cmd": "stop"})
    for _ in range(30):
        if got["cov"] is not None:
            break
        time.sleep(0.1)
    if got["cov"] is None:
        print("[probe] no coverage returned."); return
    json.dump(got["cov"], open(out, "w"))
    print(f"[probe] window '{tag}': {len(got['cov'])} unique blocks -> {out}")


if __name__ == "__main__":
    main()
