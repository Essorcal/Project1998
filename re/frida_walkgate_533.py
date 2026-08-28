#!/usr/bin/env python
r"""
Find the 5.33 client's WALK GATE -- the local collision check that decides whether a keypress becomes
an outgoing walk (0x06) at all.

WHY. The server can waive collision all it likes; on 5.33 the client refuses to SEND a walk it believes
is blocked (ground pass + object SObj flags + entities), so the server never sees the attempt. Question
on the table: did retail GM no-clip work SERVER-SIDE? That is only possible if this gate consults a
self-flag the server can set (a GM/ghost/admin bit). To know, we have to find the gate and read it.

HOW. Hook WSOCK32.send (the 5.33 net lane -- see frida_probe_533.py) and, for every outgoing packet,
capture the client's own call stack (frames inside NexusTK.exe, printed as +0xRVA so they feed straight
into `python re/disx.py --533 0x4XXXXX`). A normal walk in open ground produces a send whose backtrace
is  send <- encrypt <- build-walk <- MOVEMENT HANDLER <- ...  -- and the movement handler is where the
gate lives. Walking INTO a wall produces NO send at all, which is itself the proof the block is local.

Correlate by hand: run this, then take ONE step in open ground and watch which send carries a movement
backtrace; then push into a wall and confirm nothing sends.

Usage (client already running + logged in):
    python re/frida_walkgate_533.py --attach
    python re/frida_walkgate_533.py               # or spawn+login yourself
"""
import sys, os, time, frida
from pathlib import Path
from _paths import CLIENT5, require

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "walkgate_533.txt")

JS = r"""
'use strict';
var m = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName('NexusTK.exe') : null;
if (!m && typeof Process.getModuleByName === 'function') { try { m = Process.getModuleByName('NexusTK.exe'); } catch (e) {} }
var base = m ? m.base : ptr('0x400000');
var size = m ? m.size : 0x400000;
var lo = base, hi = base.add(size);
send({t:'info', m:'NexusTK.exe base=' + base + ' size=0x' + size.toString(16)});

function findExport(mod, name) {
  var mm = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName(mod) : null;
  if (!mm) { try { mm = Module.load(mod); } catch (e) {} }
  if (mm && typeof mm.findExportByName === 'function') { var a = mm.findExportByName(name); if (a) return a; }
  if (typeof Module.findGlobalExportByName === 'function') { var g = Module.findGlobalExportByName(name); if (g) return g; }
  if (typeof Module.findExportByName === 'function') { try { return Module.findExportByName(mod, name); } catch (e) {} }
  return null;
}

// In-module frames only, as +0xRVA -- these are the client's own code, ready for disx --533.
// FUZZY, not ACCURATE: the walk is built + enqueued EARLIER in the same main-loop frame than the
// flush that actually sends, so the movement handler has already returned and is NOT on the live
// call chain. A fuzzy scan of the stack region still turns up its stale return address (a leftover
// pointer into client code), which ACCURATE would never report -- that stale frame is the gate.
function clientStack(ctx) {
  var out = [], seen = {};
  try {
    var bt = Thread.backtrace(ctx, Backtracer.FUZZY);
    for (var i = 0; i < bt.length; i++) {
      var a = bt[i];
      if (a.compare(lo) >= 0 && a.compare(hi) < 0) {
        var rva = '+0x' + a.sub(base).toString(16);
        if (!seen[rva]) { seen[rva] = 1; out.push(rva); }
      }
      if (out.length >= 40) break;
    }
  } catch (e) {}
  return out;
}

var sendA = findExport('WSOCK32.dll', 'send') || findExport('ws2_32.dll', 'send');
if (!sendA) { send({t:'info', m:'!! no send export found'}); }
else {
  Interceptor.attach(sendA, {
    onEnter: function (args) {
      try {
        var buf = args[1], len = args[2].toInt32();
        if (len <= 0 || len > 512) return;
        var b = new Uint8Array(buf.readByteArray(Math.min(len, 16)));
        var op = b.length > 3 ? b[3] : -1;   // aa 00 len OP ...
        if (op !== 0x06 && op !== 0x32) return;   // walks only -- that is the gate we are hunting
        var hex = Array.from(b).map(function (x) { return ('0' + x.toString(16)).slice(-2); }).join(' ');
        send({t:'send', len:len, hex:hex, bt:clientStack(this.context)});
      } catch (e) {}
    }
  });
  send({t:'info', m:'hooked send @ ' + sendA + ' -- take ONE step in the open, then push into a wall'});
}
"""


def main():
    argv = sys.argv[1:]
    attach = "--attach" in argv
    client = CLIENT5
    for i, a in enumerate(argv):
        if a == "--client" and i + 1 < len(argv):
            client = Path(argv[i + 1])
    require(client, "5.33 client install", "P1998_CLIENT5")

    log = open(LOG, "w", encoding="utf-8", buffering=1)

    def emit(s):
        print(s); log.write(s + "\n")

    def on_message(msg, _data):
        if msg.get("type") == "error":
            emit("[ERROR] " + msg.get("description", str(msg))); return
        p = msg.get("payload", {})
        if p.get("t") == "info":
            emit("[probe] " + p.get("m", "")); return
        if p.get("t") == "send":
            ts = time.strftime("%H:%M:%S")
            emit(f"[{ts}] send len={p['len']:<4} [{p['hex']}]")
            emit(f"          stack: {' <- '.join(p['bt']) if p['bt'] else '(no in-module frames -- net thread)'}")

    if attach:
        session = frida.attach("NexusTK.exe")
        pid = None
    else:
        exe = str(require(client / "NexusTK.exe", "5.33 client exe", "P1998_CLIENT5"))
        pid = frida.spawn([exe], cwd=str(client)); session = frida.attach(pid)
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
