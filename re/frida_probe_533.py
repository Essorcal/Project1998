#!/usr/bin/env python
r"""
Version-SAFE Frida probe for the NexusTK 5.33 client (NextAeon533\NexusTK.exe).

frida_probe.py is pinned to 4.95 absolute addresses (mapload/dispatch/decrypt...),
which are meaningless in the 5.33 binary. This probe hooks ONLY export-name-resolved
functions, so it is address-independent and works on any client build:

  * WSOCK32 connect/recv/send  -> every byte in/out, per socket (5.33 lane: login 2001, game 2006)
  * kernel32 CreateFileA/W     -> EVERY file the client opens, flagged if map-related,
                                  WITH success/failure (INVALID_HANDLE_VALUE)

Goal: when the client enters the world and shows the black void, see
  (a) does it try to open a local map file  (Maps\TK######.cmp / C####.MAP / ...) and fail?
      -> then the fix is "give it a local map file" (like 4.x / jeedee 6.x), no streaming.
  (b) does it SEND a map-request/CRC packet after entry?  -> pull model, answer it.
  (c) neither, just waits?  -> server must PUSH FieldMap.

Usage (run the C# server first; the NexusTK.dat Connaddr patch points 5.33 at 127.0.0.1):
    python re/frida_probe_533.py                       # spawn the 5.33 client under Frida
    python re/frida_probe_533.py --attach              # attach to an already-running client
    python re/frida_probe_533.py --client "D:\NextAeon533"   # a different install
Then log in through the GUI and walk. Events stream to stdout + probe_log_533.txt.

Which install: _paths.CLIENT5, i.e. P1998_CLIENT5 or the first NextAeon533/NextAeon5 tree that
exists. Point this at the tree the two patchers have been run against -- an unpatched client
connects to Nexon's dead IPs instead of the local server, and the probe then prints nothing at all
rather than an error.
"""
import sys, os, frida
from _paths import CLIENT5, require

CLIENT = CLIENT5
for i, a in enumerate(sys.argv):
    if a == "--client" and i + 1 < len(sys.argv):
        from pathlib import Path
        CLIENT = Path(sys.argv[i + 1])
require(CLIENT, "5.33 client install", "P1998_CLIENT5")
EXE = str(require(CLIENT / "NexusTK.exe", "5.33 client exe", "P1998_CLIENT5"))
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "probe_log_533.txt")

JS = r"""
'use strict';
function findExport(mod, name) {
  let m = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName(mod) : null;
  if (!m) { try { m = Module.load(mod); } catch (e) {} }
  if (m && typeof m.findExportByName === 'function') { const a = m.findExportByName(name); if (a) return a; }
  if (typeof Module.findGlobalExportByName === 'function') return Module.findGlobalExportByName(name);
  if (typeof Module.findExportByName === 'function') return Module.findExportByName(mod, name);
  return null;
}
function hex(p, n) {
  if (n <= 0) return ''; if (n > 512) n = 512;
  try { return Array.from(new Uint8Array(p.readByteArray(n))).map(b => ('0'+(b&0xff).toString(16)).slice(-2)).join(' '); }
  catch (e) { return '<unreadable>'; }
}
const fdInfo = {};
// Optional outgoing-opcode backtrace target, injected from Python (-1 = off). Declaring it here rather
// than assuming it exists: without this the send hook threw ReferenceError on EVERY packet the client
// sent, which is noisy enough to bury the log it is supposed to be producing.
const TRACE_SEND = __TRACE_SEND__;
function hookWsock(name, onEnter, onLeave) {
  const a = findExport('WSOCK32.dll', name) || findExport('ws2_32.dll', name);
  if (!a) { send({t:'info', m:'no export ' + name}); return; }
  Interceptor.attach(a, { onEnter, onLeave });
  send({t:'info', m:'hooked ' + name});
}
// connect -> label socket by dest ip:port
hookWsock('connect', function (args) {
  const sa = args[1]; const fd = args[0].toInt32();
  const port = (sa.add(2).readU8() << 8) | sa.add(3).readU8();
  const ip = [sa.add(4).readU8(), sa.add(5).readU8(), sa.add(6).readU8(), sa.add(7).readU8()].join('.');
  fdInfo[fd] = ip + ':' + port;
  send({t:'net', m:'connect fd=' + fd + ' -> ' + fdInfo[fd]});
}, null);
// recv -> bytes FROM server (our packets, incl. the 0x15 the client reacts to)
hookWsock('recv', function (args) { this.fd = args[0].toInt32(); this.buf = args[1]; },
  function (ret) { const n = ret.toInt32(); if (n > 0) send({t:'recv', ch:fdInfo[this.fd]||'?', n:n, m:hex(this.buf, n)}); });
// send -> bytes FROM client (a map request / CRC would show here after entry)
hookWsock('send', function (args) {
  const fd = args[0].toInt32(), buf = args[1], n = args[2].toInt32();
  send({t:'send', ch:fdInfo[fd]||'?', n:n, m:hex(buf, n)});
  // Optional: backtrace one OUTGOING opcode. The framing is AA | len u16 | op | inc | body and only
  // the BODY is enciphered, so buf[3] is the plaintext opcode and can be matched here. This answers
  // "what makes the client send this?" -- which is the question that decides whether an opcode we do
  // not understand should be answered, ignored, or answered with something else entirely.
  if (TRACE_SEND >= 0) {
    try {
      if (n >= 4 && buf.add(3).readU8() === TRACE_SEND) {
        const mod = (typeof Process.findModuleByName === 'function')
                    ? Process.findModuleByName('NexusTK.exe') : null;
        const lo = mod ? mod.base : ptr('0x400000');
        const hi = lo.add(0x200000);
        const bt = Thread.backtrace(this.context, Backtracer.ACCURATE)
                         .filter(function (a) { return a.compare(lo) >= 0 && a.compare(hi) < 0; })
                         .slice(0, 14)
                         .map(function (a) { return '0x' + a.toString(16); });
        send({t:'bt', op:TRACE_SEND, m:bt.join(' <- ')});
      }
    } catch (e) {}
  }
}, null);

// CreateFile: log EVERY open; flag map-ish; report success/failure in onLeave.
function hookCreateFile(name, wide) {
  const a = findExport('kernel32.dll', name);
  if (!a) { send({t:'info', m:'no export ' + name}); return; }
  Interceptor.attach(a, {
    onEnter(args) {
      let s = null;
      try { s = wide ? args[0].readUtf16String() : args[0].readAnsiString(); } catch (e) {}
      this.path = s;
      this.mapish = s && /map|\.cmp|\.dat|tile|field/i.test(s);
    },
    onLeave(ret) {
      if (this.path == null) return;
      const ok = !ret.equals(ptr(-1));           // INVALID_HANDLE_VALUE == -1
      if (this.mapish || !ok)
        send({t:'file', m:name + '("' + this.path + '") -> ' + (ok ? 'OK' : 'FAIL/not-found')});
    }
  });
  send({t:'info', m:'hooked ' + name});
}
hookCreateFile('CreateFileW', true);
hookCreateFile('CreateFileA', false);
send({t:'info', m:'=== 5.33 probe armed: log in and walk into the void ==='});
"""

def main():
    attach = "--attach" in sys.argv
    trace_send = -1
    for i, a in enumerate(sys.argv):
        if a == "--trace-send" and i + 1 < len(sys.argv):
            trace_send = int(sys.argv[i + 1], 16)
    js = JS.replace("__TRACE_SEND__", str(trace_send))
    log = open(LOG, "w", encoding="utf-8", buffering=1)
    def emit(line):
        print(line); log.write(line + "\n")
    def on_message(msg, data):
        if msg.get("type") == "error":
            emit("[ERROR] " + msg.get("description", str(msg))); return
        p = msg.get("payload", {})
        t = p.get("t")
        import time
        ts = time.strftime("%H:%M:%S")
        if t == "file":
            emit(f"[{ts}] FILE  {p['m']}")
        elif t in ("recv", "send", "net"):
            tag = {"recv": "<~ RECV", "send": "~> SEND", "net": "NET  "}[t]
            extra = f" ({p['ch']}, {p['n']}B)" if t in ("recv", "send") else ""
            emit(f"[{ts}] {tag}{extra} {p['m']}")
        else:
            emit(f"[{ts}] · {p.get('m','')}")
    emit(f"[probe] client: {CLIENT}")
    if attach:
        session = frida.attach("NexusTK.exe")
        pid = None
    else:
        # cwd, or the client resolves its .DAT archives against re/ and dies before the first hook.
        pid = frida.spawn([EXE], cwd=str(CLIENT))
        session = frida.attach(pid)
    script = session.create_script(js)
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid)
    emit(f"[probe] loaded; logging to {LOG}. Ctrl-C to stop.")
    park()


def park():
    """Block until Ctrl-C.

    Deliberately NOT sys.stdin.read(). Under any launcher that hands the probe a console it never
    types into -- a background job, a task runner, CI -- stdin is a tty at EOF, so read() returns
    instantly, main() falls off the end, and the exiting Python takes the frida-spawned client down
    with it. Symptom: the client window blinks away a second after it appears and the log holds
    nothing but the "armed" banner. Ctrl-C interrupts a sleep just as well as a read.
    """
    import time
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        pass

if __name__ == "__main__":
    main()
