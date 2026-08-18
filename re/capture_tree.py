#!/usr/bin/env python
"""Record the EXACT packets for the Mythic run: gate, tree dialog, and the transit.

The server source tells us the shapes already (RTK-Server/rtk/src/map/clif.c):
    0x43  click an entity      -> entity id  BE32 @ server offset 6
    0x3A  dialog continue      -> subtype @5, choice @13
    0x39  menu selection       -> subtype=1 @5, id BE32 @6, selection BE16 @10
and the client frames every packet as  AA <len16> <opcode> ... , so a byte at server
offset N sits at offset N-3 in the hex printed below.

What is NOT in the source is what the CLIENT actually emits -- whether it inserts a
sequence byte, how it numbers the tree's menu options, and which entity id the tree has.
So: drive the sequence by hand once, and this records it. Read-only apart from the
recording -- it injects nothing.

    python re/capture_tree.py            # runs until Ctrl-C
    python re/capture_tree.py 180        # or for N seconds

Do this while it runs:
    1. cast Gateway -> N -> Enter        (expect a map change to Mythic Nexus)
    2. click the HealerOfDoom tree       (it is at 35,8 on Mythic Nexus)
    3. pick option 2  (heal + mana)
    4. click it again, pick option 3     (the ASV buff)
    5. click it again, pick option 2     (heal + mana)
    6. walk onto 49,11                   (expect a warp to Mythic Gateway)
    7. take one step north               (expect a warp to Mythic Waters 1)
"""
import os, sys, time, json
D = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, D)

import nexus_bot as NB
import nexus_agent as NA
from bot_input_test import find_windows

SECS = float(sys.argv[1]) if len(sys.argv) > 1 else None
OUT = os.path.join(NA.OUT, "tree_capture.jsonl")

# Widen the capture: the shipped hooks keep only the handful of opcodes the grinder needs,
# and record hex for exactly one of them. We want everything, both directions.
JS = (NB.BOT_JS
      .replace("if(!RECV.has(op)) return;", "")
      .replace("SEND.has(op)", "true")
      .replace("op === 0x0a ?", "true ?")
      .replace("Math.min(len,48)", "Math.min(len,255)"))

INTERESTING = {0x39: "MENU-SELECT", 0x3A: "DIALOG", 0x43: "CLICK-ENTITY",
               0x0f: "SPELL-CAST", 0x3F: "MAP-CHANGE", 0x06: "step", 0x11: "turn"}


def be(b, i, n):
    v = 0
    for k in range(n):
        v = (v << 8) | b[i + k]
    return v


def main():
    wins = find_windows()
    if not wins:
        print("client not running")
        return 1
    hwnd, pid = wins[0][0], wins[0][2]

    agent = NA.Agent()
    world = NB.World(agent)
    seen = {"n": 0}
    f = open(OUT, "a", encoding="utf-8", buffering=1)

    def on_message(msg, data):
        if msg.get("type") != "send":
            return
        p = msg.get("payload") or {}
        op, hexs = p.get("op"), p.get("hex") or ""
        if op is None:
            return
        out = {"dir": "send" if p.get("t") == "send" else "recv",
               "op": op, "ts": p.get("ts"), "hex": hexs}
        f.write(json.dumps(out) + "\n")
        seen["n"] += 1
        if op not in INTERESTING:
            return
        b = [int(x, 16) for x in hexs.split()] if hexs else []
        note = ""
        # hex starts AT the opcode == server offset 3, so server offset N -> b[N-3]
        try:
            if op == 0x43 and len(b) >= 7:
                note = f"  entity_id={be(b, 3, 4)}"
            elif op == 0x39 and len(b) >= 9:
                note = f"  subtype={b[2]} id={be(b, 3, 4)} SELECTION={be(b, 7, 2)}"
            elif op == 0x3A and len(b) >= 11:
                note = f"  subtype={b[2]} choice={b[10]}"
        except Exception:
            pass
        print(f"{out['dir']:>4} 0x{op:02x} {INTERESTING.get(op, ''):<12}{note}\n"
              f"      {hexs[:120]}", flush=True)

    # attach() always loads the stock (filtered) BOT_JS, so load the widened one ourselves.
    # BOT_JS already has its __MOD__/__RVA__ placeholders substituted at import time.
    import frida
    dev = frida.get_local_device()
    s = dev.attach(pid)
    sc = s.create_script(JS)
    sc.on("message", on_message)
    sc.load()
    print(f"recording -> {OUT}\nrun the sequence now; Ctrl-C when done\n")

    t0 = time.time()
    try:
        while SECS is None or time.time() - t0 < SECS:
            time.sleep(0.4)
    except KeyboardInterrupt:
        pass
    finally:
        print(f"\n{seen['n']} frames recorded to {OUT}")
        try:
            s.detach()
        except Exception:
            pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
