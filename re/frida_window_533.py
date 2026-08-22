#!/usr/bin/env python
r"""
Answer "what keeps closing my character sheet?" on the 5.33 client, with a call stack.

The client has FOUR mutually-exclusive panels behind one switcher, `sub_435790(this, index, body)`:

    index 0 = self profile (the character sheet)   2 = ?
    index 1 = click profile (view another player)  3 = ?

Switching to any other index closes whatever is open, so the sheet closing IS a call to this function
with a different index. Knowing that much is not enough, though: the interesting question is who called
it, and the two candidate answers want opposite fixes. If a packet handler is on the stack, the server
sent something that closed it and the server must stop. If the stack is pure UI, 5.33 simply does not
let the sheet coexist with that panel and there is nothing to fix.

So this logs, for every switch, the index being opened, the index that WAS open, whether an opcode was
being dispatched at the time, and a backtrace. Reads nothing, hooks nothing hot -- it is safe to leave
running while playing normally.

The backtrace prints raw addresses (the exe has no symbols). Map any of them to their enclosing
function with:  python re/dispatch_533.py --parsers 1   or   python re/disx.py --533 <addr> 120

Usage (client already running):
    python re/frida_window_533.py --attach
    python re/frida_window_533.py --attach --closes-only   # only switches that CLOSE the sheet

Output also lands in re/window_log_533.txt.
"""
import os
import sys
import time
from pathlib import Path

import frida

from _paths import CLIENT5, require

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "window_log_533.txt")

WINDOW_SWITCH_RVA = 0x35790     # sub_435790
DISPATCH_RVA = 0x63320          # sub_463320, the opcode dispatcher
CURRENT_INDEX_OFF = 0x14        # manager + 0x14 = the index currently shown

NAMES = {0: "self profile (character sheet)", 1: "click profile", 2: "?", 3: "?"}

JS = r"""
'use strict';
var m = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName('NexusTK.exe') : null;
var base = m ? m.base : ptr('0x400000');
var lo = base, hi = base.add(0x200000);       // only backtrace frames inside the exe are useful
var curOp = -1;

// Bracket the dispatcher so a switch can be blamed on a packet (or exonerated).
Interceptor.attach(base.add(__DISPATCH__), {
  onEnter: function () {
    this.prevOp = curOp; curOp = -1;
    try {
      var a0 = this.context.esp.add(4).readPointer();
      curOp = a0.add(0xc).readPointer().readU8();
    } catch (e) {}
  },
  onLeave: function () { curOp = this.prevOp; }
});

Interceptor.attach(base.add(__SWITCH__), {
  onEnter: function () {
    var idx = -1, cur = -1;
    try { idx = this.context.esp.add(4).readS32(); } catch (e) {}
    try { cur = this.context.ecx.add(__CUROFF__).readS32(); } catch (e) {}   // thiscall: ecx = manager
    var bt = [];
    try {
      bt = Thread.backtrace(this.context, Backtracer.ACCURATE)
                 .filter(function (a) { return a.compare(lo) >= 0 && a.compare(hi) < 0; })
                 .slice(0, 12)
                 .map(function (a) { return '0x' + a.sub(base).add(ptr('0x400000')).toString(16); });
    } catch (e) {}
    send({ t:'win', idx: idx, cur: cur, op: curOp, bt: bt });
  }
});

send({ t:'info', m:'window switcher hooked -- open the character sheet and reproduce' });
""".replace("__SWITCH__", hex(WINDOW_SWITCH_RVA)) \
   .replace("__DISPATCH__", hex(DISPATCH_RVA)) \
   .replace("__CUROFF__", hex(CURRENT_INDEX_OFF))


def main():
    argv = sys.argv[1:]
    attach = "--attach" in argv
    closes_only = "--closes-only" in argv
    client = CLIENT5
    for i, a in enumerate(argv):
        if a == "--client" and i + 1 < len(argv):
            client = Path(argv[i + 1])
    require(client, "5.33 client install", "P1998_CLIENT5")

    log = open(LOG, "w", encoding="utf-8", buffering=1)

    def emit(s):
        print(s)
        log.write(s + "\n")

    def on_message(msg, _data):
        if msg.get("type") == "error":
            emit("[ERROR] " + msg.get("description", str(msg)))
            return
        p = msg.get("payload", {})
        if p.get("t") == "info":
            emit("[probe] " + p.get("m", ""))
            return
        idx, cur, op, bt = p["idx"], p["cur"], p["op"], p.get("bt", [])
        # "Closing the sheet" = the character sheet was open and we are switching away from it.
        closing = cur == 0 and idx != 0
        if closes_only and not closing:
            return
        src = f"during opcode 0x{op:02x}" if op >= 0 else "NO packet in flight (client UI)"
        tag = "  <<< CLOSES THE CHARACTER SHEET" if closing else ""
        emit(f"[{time.strftime('%H:%M:%S')}] switch {cur} -> {idx} "
             f"({NAMES.get(idx, '?')})  {src}{tag}")
        if bt:
            emit("            stack: " + " <- ".join(bt))

    session = frida.attach("NexusTK.exe") if attach else None
    pid = None
    if session is None:
        exe = str(require(client / "NexusTK.exe", "5.33 client exe", "P1998_CLIENT5"))
        pid = frida.spawn([exe], cwd=str(client))
        session = frida.attach(pid)
    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid)
    emit(f"[probe] client: {client}; logging to {LOG}. Ctrl-C to stop.")
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
