#!/usr/bin/env python
r"""
Version-SAFE Frida probe for the NexusTK 5.33 client (NextAeon5\NexusTK.exe).

frida_probe.py is pinned to 4.95 absolute addresses (mapload/dispatch/decrypt...),
which are meaningless in the 5.33 binary. This probe hooks ONLY export-name-resolved
functions, so it is address-independent and works on any client build:

  * WSOCK32 connect/recv/send  -> every byte in/out, per socket (login 2000 vs game 2005)
  * kernel32 CreateFileA/W     -> EVERY file the client opens, flagged if map-related,
                                  WITH success/failure (INVALID_HANDLE_VALUE)

Goal: when the client enters the world and shows the black void, see
  (a) does it try to open a local map file  (Maps\TK######.cmp / C####.MAP / ...) and fail?
      -> then the fix is "give it a local map file" (like 4.x / jeedee 6.x), no streaming.
  (b) does it SEND a map-request/CRC packet after entry?  -> pull model, answer it.
  (c) neither, just waits?  -> server must PUSH FieldMap.

Usage (run the C# server first; the NexusTK.dat Connaddr patch points 5.33 at 127.0.0.1):
    python re/frida_probe_533.py            # spawn the 5.33 client under Frida
    python re/frida_probe_533.py --attach   # attach to an already-running client
Then log in through the GUI and walk into the void. Events stream to stdout + probe_log_533.txt.
"""
import sys, os, frida

EXE = r"C:\Program Files (x86)\Nexon\NextAeon5\NexusTK.exe"
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
    if attach:
        session = frida.attach("NexusTK.exe")
    else:
        pid = frida.spawn(EXE)
        session = frida.attach(pid)
    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    if not attach:
        frida.resume(session._impl.pid if hasattr(session, "_impl") else pid)
    emit(f"[probe] loaded; logging to {LOG}. Ctrl-C to stop.")
    sys.stdin.read()

if __name__ == "__main__":
    main()
