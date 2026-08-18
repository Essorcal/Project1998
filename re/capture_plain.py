#!/usr/bin/env python
"""Capture OUTGOING packets in PLAINTEXT, before the client encrypts them.

The first capture showed every outgoing frame is encrypted after the opcode (byte 4 is a
per-packet sequence counter), so the wire tells us nothing about which menu option was
chosen. But the client builds each packet in the clear and hands it to its own send
function at 0x576660 -- the same one nexus_bot's sendraw() uses. Hook that and we read the
packet as the client wrote it.

Offsets: the client prepends AA <len16> and inserts its sequence byte, so a byte the SERVER
reads at offset N is at index N-4 of this buffer (index 0 is the opcode). Verified against
clif_parselookat, which reads x at server offset 5 -- asktile writes it at index 1.

From RTK-Server/rtk/src/map/clif.c:
    0x43 click entity   : id BE32   @ server 6  -> idx 2
    0x3A dialog/menu    : subtype   @ server 5  -> idx 1
                          choice    @ server 13 -> idx 9
                          menu      @ server 15 -> idx 11
    0x39 menu select    : subtype   @ server 5  -> idx 1
                          id BE32   @ server 6  -> idx 2
                          selection @ server 10 -> idx 6

Injects nothing -- it only reads the buffer on its way out.
"""
import os, sys, time, json
D = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, D)

import nexus_agent as NA
from bot_input_test import find_windows
import frida

SECS = float(sys.argv[1]) if len(sys.argv) > 1 else 600.0
OUT = os.path.join(NA.OUT, "plain_sends.jsonl")

JS = r"""
'use strict';
const SENDFN = ptr('0x576660');
Interceptor.attach(SENDFN, {
  onEnter(args){
    try{
      const buf = args[0];               // thiscall: ecx = conn, arg0 = plaintext buffer
      let len = args[1].toInt32();
      if (len <= 0 || len > 512) return;
      const b = new Uint8Array(buf.readByteArray(len));
      let hex = '';
      for (let i = 0; i < b.length; i++) hex += ('0' + b[i].toString(16)).slice(-2) + ' ';
      send({ts: Date.now(), len: len, hex: hex.trim()});
    }catch(e){}
  }
});
"""

NAMES = {0x43: "CLICK-ENTITY", 0x3a: "DIALOG/MENU", 0x39: "MENU-SELECT", 0x0f: "SPELL",
         0x06: "step", 0x11: "turn", 0x13: "attack", 0x0a: "look-at", 0x2d: "profile-req"}
NOISY = {0x06, 0x11, 0x13, 0x45}


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
    pid = wins[0][2]
    f = open(OUT, "a", encoding="utf-8", buffering=1)
    n = [0]

    def on_message(msg, data):
        if msg.get("type") != "send":
            return
        p = msg.get("payload") or {}
        hexs = p.get("hex", "")
        if not hexs:
            return
        b = [int(x, 16) for x in hexs.split()]
        op = b[0]
        f.write(json.dumps({"ts": p.get("ts"), "op": op, "hex": hexs}) + "\n")
        n[0] += 1
        if op in NOISY:
            return
        note = ""
        try:
            if op == 0x43 and len(b) >= 6:
                note = f"  entity_id={be(b, 2, 4)}"
            elif op == 0x3a:
                note = f"  subtype={b[1] if len(b) > 1 else '?'}"
                if len(b) >= 10:
                    note += f" choice={b[9]}"
                if len(b) >= 12:
                    note += f" MENU_OPTION={b[11]}"
            elif op == 0x39 and len(b) >= 8:
                note = f"  subtype={b[1]} id={be(b, 2, 4)} SELECTION={be(b, 6, 2)}"
        except Exception:
            pass
        print(f"0x{op:02x} {NAMES.get(op, ''):<13}{note}\n     {hexs[:150]}", flush=True)

    dev = frida.get_local_device()
    s = dev.attach(pid)
    sc = s.create_script(JS)
    sc.on("message", on_message)
    sc.load()
    print(f"hooked pid {pid}; plaintext sends -> {OUT}")
    print("Now: click the tree, pick option 2. Then click, pick 3. Then click, pick 2.\n")

    t0 = time.time()
    try:
        while time.time() - t0 < SECS:
            time.sleep(0.4)
    except KeyboardInterrupt:
        pass
    finally:
        print(f"\n{n[0]} plaintext frames -> {OUT}")
        try:
            s.detach()
        except Exception:
            pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
