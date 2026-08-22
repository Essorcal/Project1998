#!/usr/bin/env python
r"""
Hook the 5.33 client's opcode DISPATCHER and report, for every server->client packet, whether the
client actually handled it -- plus the decrypted body.

Why this and not frida_probe_533.py: that probe hooks WSOCK32 recv, so it proves what we SENT. It
cannot distinguish "the client parsed this and disagreed" from "the client dropped this on the
floor", and those want opposite fixes. The dispatcher knows. `sub_463320` is a jump table over
`byteMap[op-3]`; every opcode whose entry is the default index 0x2d falls into `0x463c77`, which is
literally `xor al, al; ret 4` -- no log, no error, no reply. It returns AL = 1 handled / 0 ignored,
so hooking onLeave gives a per-packet verdict.

It also lands AFTER the client's decrypt, so the body here is plaintext -- no cipher, no reassembly.

  sub_463320(this, arg0)   arg0 @ [esp+4] on entry; body ptr = [arg0 + 0xc]; opcode = body[0]

Usage (client already running -- start it under frida_probe_533.py or by hand):
    python re/frida_dispatch_533.py --attach
    python re/frida_dispatch_533.py                    # or spawn it here
    python re/frida_dispatch_533.py --attach --only 39,33,0e
    python re/frida_dispatch_533.py --attach --ignored  # only the DROPPED opcodes

Output also lands in re/dispatch_log_533.txt, and a per-opcode summary prints on Ctrl-C.
"""
import json
import os
import sys
import time
from collections import Counter
from pathlib import Path

import frida

from _paths import CLIENT5, require

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "dispatch_log_533.txt")

# The client has MORE THAN ONE packet dispatcher, and this probe was wrong until it hooked them all.
#
# Each is the same shape -- `esi = [arg0+0xc]; al = body[0]; eax = al - BIAS; cmp eax, RANGE; ja default;
# dl = byteMap[eax]; jmp ptrTable[dl]` -- and each is a VIRTUAL METHOD on a different class, so which one
# sees a packet depends on what screen is up. Hooking only the world one (0x463320) made every opcode the
# other dispatchers own look "DROPPED": 0x08 stats, 0x0f/0x10 item add/remove and 0x17 add-spell all report
# unhandled there and are handled at 0x4d5f80. An opcode is only really ignored if EVERY dispatcher that
# saw it returned 0, which is what the summary below now says.
#
# Found by scanning for that instruction shape and keeping the ones mounted in a vtable
# (re: dispatch_533.py --parsers, plus the vtable-slot scan). The last three share slot +0x18.
DISPATCHERS = {
    0x63320: "world   (0x463320)",
    0xD5F80: "ui/item (0x4d5f80)",
    0xE13D0: "ui-b    (0x4e13d0)",
    0xE6B70: "ui-c    (0x4e6b70)",
}
DISPATCH_RVA = 0x63320          # kept for the JS template below

LABEL = {
    0x02: "enter-world", 0x04: "self coords", 0x05: "your entity id", 0x06: "map data",
    0x07: "entity spawn", 0x08: "stats/HUD", 0x0A: "system text", 0x0B: "exit-to-select",
    0x0C: "entity move", 0x0D: "speech", 0x0E: "despawn list", 0x0F: "add spell/item",
    0x10: "remove item/spell", 0x11: "entity turn", 0x13: "mob hp/stat", 0x15: "map info",
    0x19: "media", 0x1E: "ack", 0x20: "time-of-day", 0x2F: "menu window", 0x30: "npc dialog",
    0x33: "self appearance", 0x34: "click profile", 0x36: "user list", 0x37: "bag array",
    0x38: "hard refresh", 0x39: "self profile", 0x3A: "dialog", 0x3B: "mail",
    0x66: "examine item",
}

JS = r"""
'use strict';
var m = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName('NexusTK.exe') : null;
var base = m ? m.base : ptr('0x400000');   // non-ASLR, so the fallback is safe
send({t:'info', m:'NexusTK.exe base = ' + base});

__DISPATCHERS__.forEach(function (d) {
Interceptor.attach(base.add(d.rva), {
  onEnter: function () {
    this.op = -1; this.hex = ''; this.who = d.name;
    try {
      var a0 = this.context.esp.add(4).readPointer();
      var body = a0.add(0xc).readPointer();
      this.op = body.readU8();
      // No length is reachable here, so take a fixed window. 96 bytes covers every fixed-size
      // header we care about; the long ones (profile, map data) get truncated on purpose.
      var b = new Uint8Array(body.readByteArray(96));
      this.hex = Array.from(b).map(function (x) { return ('0' + x.toString(16)).slice(-2); }).join(' ');
    } catch (e) {}
  },
  onLeave: function (ret) {
    if (this.op < 0) return;
    send({t:'op', op:this.op, who:this.who, handled:(ret.toInt32() & 0xff) !== 0, hex:this.hex});
  }
});
});
send({t:'info', m:'hooked __NDISP__ dispatchers -- an opcode is only ignored if ALL of them refuse it'});
"""
JS = (JS.replace("__DISPATCHERS__",
                 json.dumps([{"rva": r, "name": n} for r, n in DISPATCHERS.items()]))
        .replace("__NDISP__", str(len(DISPATCHERS))))


def main():
    argv = sys.argv[1:]
    attach = "--attach" in argv
    ignored_only = "--ignored" in argv
    client = CLIENT5
    only = None
    for i, a in enumerate(argv):
        if a == "--client" and i + 1 < len(argv):
            client = Path(argv[i + 1])
        if a == "--only" and i + 1 < len(argv):
            only = {int(x, 16) for x in argv[i + 1].replace("0x", "").split(",")}
    require(client, "5.33 client install", "P1998_CLIENT5")

    log = open(LOG, "w", encoding="utf-8", buffering=1)
    seen = Counter()
    dropped = Counter()
    handled_by = {}          # opcode -> set of dispatchers that RETURNED handled

    def emit(line):
        print(line)
        log.write(line + "\n")

    def on_message(msg, _data):
        if msg.get("type") == "error":
            emit("[ERROR] " + msg.get("description", str(msg)))
            return
        p = msg.get("payload", {})
        if p.get("t") == "info":
            emit("[probe] " + p.get("m", ""))
            return
        op, handled, hexs = p["op"], p["handled"], p.get("hex", "")
        who = p.get("who", "?")
        seen[op] += 1
        if handled:
            handled_by.setdefault(op, set()).add(who)
        else:
            dropped[op] += 1
        if only is not None and op not in only:
            return
        if ignored_only and handled:
            return
        ts = time.strftime("%H:%M:%S")
        mark = "ok " if handled else "no "
        emit(f"[{ts}] 0x{op:02x} {mark:4s}{who:<20}{LABEL.get(op, ''):<18} {hexs[:90]}")

    if attach:
        session = frida.attach("NexusTK.exe")
        pid = None
    else:
        exe = str(require(client / "NexusTK.exe", "5.33 client exe", "P1998_CLIENT5"))
        pid = frida.spawn([exe], cwd=str(client))
        session = frida.attach(pid)
    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid)
    emit(f"[probe] client: {client}; logging to {LOG}. Ctrl-C for the summary.")
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        pass

    emit("\n=== per-opcode summary (server -> client) ===")
    emit("  op   seen  dropped  label")
    for op in sorted(seen):
        flag = "  <-- ALL DROPPED" if dropped[op] == seen[op] else ""
        emit(f"  0x{op:02x}  {seen[op]:5d}  {dropped[op]:7d}  {LABEL.get(op, '')}{flag}")


if __name__ == "__main__":
    main()
