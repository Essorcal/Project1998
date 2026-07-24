#!/usr/bin/env python
"""
Live instrumentation of the NexusTK 4.95 client (NexusTK_local.exe) via Frida.

Purpose: crack the game-channel world-entry wall by DIRECT OBSERVATION. Hooks the
client's own decrypt/encrypt routines (found by reversing the binary), the raw
socket calls, and the map-load routine — so we see exactly:
  * what the client sends/receives on the login (2000) vs game (2005) socket,
  * the client's own decryption of OUR game packets (proves crypto correctness live),
  * whether/when the client attempts to load a map (= it reached world-entry).

The exe has no ASLR/relocations (ImageBase 0x400000 always), so absolute VAs are stable.
We still resolve via module base + RVA for robustness.

NOTE — with the RUNASADMIN / WinXP-compat flags removed, the client runs non-elevated,
so this probe works from an ordinary terminal (no Administrator needed). If attach ever
fails with 0x2e4 (ERROR_ELEVATION_REQUIRED), a compat flag came back — clear it or run
this elevated.

Usage (run the C# server first, in any terminal):
    python re/frida_probe.py                 # spawn the client under Frida
    python re/frida_probe.py --attach        # attach to an already-running client

Then log in through the client GUI and walk to the game-server handoff. All events
stream to stdout AND to re/probe_log.txt.
"""
import sys, os, time, frida

EXE = r"C:\Program Files (x86)\Nexon\NextAeon\NexusTK_local.exe"
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "probe_log.txt")

# RVAs (VA - 0x400000), from static RE of NexusTK_local.exe
RVA = {
    "decrypt":  0x078680,  # decrypt(src, len, out, this)  -- receive path
    "encrypt":  0x078760,  # encrypt(src, len, out, this)  -- send path (plaintext in)
    "mapload":  0x04a780,  # loads Maps\TK%d.map  -- fires when client renders a map
    "worldctor":0x04a090,  # game-world object ctor -- built only on 0x02+00 (enter-world trigger)
    # ---- 0x33 self-entity handler internals (to find where it bails) ----
    "h33":      0x04fef0,  # the 0x33 handler
    "place":    0x024310,  # 0x424310  place/validate at (Y,X) -> al; al==0 => BAIL
    "entcreate":0x04d7d0,  # 0x44d7d0  create/find entity -> eax; null => BAIL
    "opnew":    0x03fd80,  # 0x43fd80  operator new; size 0x17c == sprite alloc (reached flag branch)
    "h04":      0x04faf0,  # 0x04 coords handler; reads [world+0x40c] (self entity ptr) for the camera
    "entmove":  0x062320,  # 0x462320 start-walk-animation(entity, X, Y, dir, speed); sets walking flags
}

JS = r"""
'use strict';
const MOD = 'NexusTK_local.exe';
const RVA = __RVA_JSON__;

// --- Frida 17 vs older API shims -------------------------------------------
// v17 removed Module.findBaseAddress and the 2-arg Module.findExportByName.
function moduleByName(name) {
  if (typeof Process.findModuleByName === 'function') return Process.findModuleByName(name);
  return null;
}
function ensureModule(name) {              // force-load late-bound DLLs (e.g. WSOCK32) so exports resolve
  let m = moduleByName(name);
  if (!m) { try { m = Module.load(name); } catch (e) {} }
  return m;
}
function findExport(moduleName, exportName) {
  const m = ensureModule(moduleName);
  if (m && typeof m.findExportByName === 'function') {
    const a = m.findExportByName(exportName);
    if (a) return a;
  }
  if (typeof Module.findGlobalExportByName === 'function') return Module.findGlobalExportByName(exportName);
  if (typeof Module.findExportByName === 'function') return Module.findExportByName(moduleName, exportName); // old API
  return null;
}

let base = null;
{
  const m = moduleByName(MOD);
  base = m ? m.base : Process.enumerateModules()[0].base;
}
send({t:'info', m:'module base = ' + base});

function hex(ptr, n) {
  if (n <= 0) return '';
  if (n > 512) n = 512;
  try { return Array.from(new Uint8Array(ptr.readByteArray(n)))
      .map(b => ('0'+(b&0xff).toString(16)).slice(-2)).join(' '); }
  catch (e) { return '<unreadable>'; }
}
function at(name) { return base.add(ptr(RVA[name])); }

// ---- socket fd -> channel map (from connect) ----
const fdInfo = {};

function hookWsock(name, onEnter, onLeave) {
  const a = findExport('WSOCK32.dll', name) || findExport('ws2_32.dll', name);
  if (!a) { send({t:'info', m:'no export ' + name}); return; }
  Interceptor.attach(a, { onEnter, onLeave });
}

// connect(fd, sockaddr, len) -> record destination port so we know which socket is game(2005)
hookWsock('connect', function (args) {
  const fd = args[0].toInt32();
  const sa = args[1];
  const port = (sa.add(2).readU8() << 8) | sa.add(3).readU8();     // sin_port, big-endian
  const ip = [sa.add(4).readU8(), sa.add(5).readU8(), sa.add(6).readU8(), sa.add(7).readU8()].join('.');
  fdInfo[fd] = ip + ':' + port;
  send({t:'net', m:'connect fd=' + fd + ' -> ' + fdInfo[fd]});
}, null);

// recv(fd, buf, len, flags) -> log bytes actually received
hookWsock('recv', function (args) {
  this.fd = args[0].toInt32(); this.buf = args[1];
}, function (ret) {
  const n = ret.toInt32();
  if (n > 0) send({t:'recv', fd:this.fd, ch:fdInfo[this.fd]||'?', m:hex(this.buf, n), n:n});
});

// send(fd, buf, len, flags) -> log raw wire bytes leaving the client
hookWsock('send', function (args) {
  const fd = args[0].toInt32(); const buf = args[1]; const n = args[2].toInt32();
  send({t:'send', fd:fd, ch:fdInfo[fd]||'?', m:hex(buf, n), n:n});
}, null);

// decrypt(src, len, out, this): the client decrypting a received packet.
// src[0]=opcode, src[1]=increment (still encrypted body follows); out = decrypted result.
Interceptor.attach(at('decrypt'), {
  onEnter(args) { this.src = args[0]; this.len = args[1].toInt32(); this.out = args[2]; },
  onLeave(ret) {
    const op = this.src.readU8();
    const inc = this.src.add(1).readU8();
    send({t:'decrypt', op:op, inc:inc, len:this.len, m:hex(this.out, this.len)});
  }
});

// encrypt(src, len, out, this): the client encrypting a packet to send. src = PLAINTEXT.
Interceptor.attach(at('encrypt'), {
  onEnter(args) {
    const src = args[0]; const len = args[1].toInt32();
    const op = src.readU8();
    let bt = null;
    // For the creation packet (0x04) and namecheck (0x02) on the login channel, capture the call
    // stack so we can find the client's packet BUILDER and statically decode which creation-UI field
    // writes which byte (gender/nation/totem/face/hair) — uncontrolled samples are ambiguous.
    if (op === 0x04 || op === 0x02) {
      const base = Process.findModuleByName('NexusTK_local.exe').base;
      bt = Thread.backtrace(this.context, Backtracer.ACCURATE)
        .slice(0, 8)
        .map(a => { const off = a.sub(base); return (off.compare(0x1000000) < 0) ? ('+0x' + off.toString(16)) : a.toString(); });
    }
    send({t:'encrypt', op:op, len:len, m:hex(src, len), bt:bt});
  }
});

// world-object ctor: built ONLY when the client receives 0x02 + first-payload-byte 0x00.
// If this fires, the enter-world trigger worked and world packets (0x15/0x04/0x33) are live.
Interceptor.attach(at('worldctor'), {
  onEnter(args) { send({t:'WORLDCTOR', m:'*** game-world object CONSTRUCTED (0x02+00 worked) *** this=' + args[0]}); }
});

// map-load: sprintf("Maps\\TK%d.map", id). If this fires, the world processed our 0x15 enter-map.
Interceptor.attach(at('mapload'), {
  onEnter(args) {
    send({t:'MAPLOAD', m:'*** client is loading a map! *** a0=' + args[0] + ' a1=' + args[1]});
  }
});

// CreateFile: capture the actual map filename being opened AND whether the open succeeds.
// Old client is ANSI (CreateFileA) but hook W too in case a shim/CRT routes there.
function hookCreateFile(name, wide) {
  const a = findExport('kernel32.dll', name);
  if (!a) { send({t:'info', m:'no export ' + name}); return; }
  Interceptor.attach(a, {
    onEnter(args) {
      let s = '<null>';
      try { s = wide ? args[0].readUtf16String() : args[0].readAnsiString(); } catch (e) {}
      this.name = s;
      this.want = s && /\.map/i.test(s);
    },
    onLeave(ret) {
      if (!this.want) return;
      const h = ret.toInt32();
      send({t:'FILE', m:name + '("' + this.name + '") -> handle=0x' + (h>>>0).toString(16) +
                        (h === -1 ? '  <<< OPEN FAILED >>>' : '  (ok)')});
    }
  });
}
hookCreateFile('CreateFileA', false);
hookCreateFile('CreateFileW', true);

// ---- 0x33 self-entity handler trace: find exactly where it bails ----
let in33 = 0;
Interceptor.attach(at('h33'), {
  onEnter(args) { in33++; send({t:'T33', m:'0x33 handler ENTER (body ' + hex(args[0], 24) + ')'}); },
  onLeave(ret) { in33--; }
});
Interceptor.attach(at('place'), {
  onLeave(ret) { if (in33) send({t:'T33', m:'  place/validate 0x424310 -> al=' + (ret.toInt32() & 0xff) + (((ret.toInt32()&0xff)===0)?'  <<< BAIL (placement failed) >>>':'')}); }
});
Interceptor.attach(at('entcreate'), {
  onLeave(ret) { if (in33) send({t:'T33', m:'  create-entity 0x44d7d0 -> 0x' + (ret.toInt32()>>>0).toString(16) + ((ret.toInt32()===0)?'  <<< BAIL (null entity) >>>':'')}); }
});
Interceptor.attach(at('opnew'), {
  onEnter(args) { if (in33 && args[0].toInt32() === 0x17c) send({t:'T33', m:'  *** SPRITE ALLOC 0x17c -> reached flag branch, entity is being RENDERED ***'}); }
});
// 0x04 coords handler: is the self-entity pointer [world+0x40c] set (camera can follow) or null (black)?
let gSelf = null, modBase = base;
Interceptor.attach(at('h04'), {
  onEnter(args) {
    const world = this.context.ecx;   // thiscall: ecx = world object
    let selfptr = '<err>';
    try { selfptr = world.add(0x40c).readPointer(); gSelf = selfptr; } catch (e) {}
    send({t:'T33', m:'0x04 handler: world=' + world + '  self[+0x40c]=' + selfptr +
                     (String(selfptr) === '0x0' ? '  <<< NO self entity -> camera has nothing to follow >>>' : '')});
  }
});

// start-walk-animation: WHO initiates walks? If pressing a key fires this on the self from client
// code (retaddr NOT in our 0x0c handler 0x4502c0..0x450340), the client is client-authoritative.
Interceptor.attach(at('entmove'), {
  onEnter(args) {
    const ent = this.context.ecx;
    const x = args[0].toInt32(), y = args[1].toInt32(), dir = args[2].toInt32() & 0xff;
    const ra = this.returnAddress;
    const rva = ra.sub(modBase);
    const fromOurMove = (rva.compare(ptr(0x502c0)) >= 0 && rva.compare(ptr(0x50340)) < 0);
    let curX = '?', curY = '?';
    try { curX = ent.add(0x10c).readS32(); curY = ent.add(0x110).readS32(); } catch (e) {}
    send({t:'WALK', m:'start-walk ent=' + ent + (String(ent)===String(gSelf)?'(SELF)':'') +
                     ' cur=(' + curX + ',' + curY + ') -> target=(' + x + ',' + y + ') dir=' + dir +
                     ' caller=' + (fromOurMove ? 'OUR-0x0c' : ('client@0x'+rva.toString(16)))});
  }
});

send({t:'info', m:'hooks installed (recv/send/connect/decrypt/encrypt/worldctor/mapload/createfile/0x33-trace)'});
""".replace("__RVA_JSON__", __import__("json").dumps(RVA))


def main():
    attach = "--attach" in sys.argv
    logf = open(LOG, "w", encoding="utf-8", buffering=1)

    def out(line):
        stamp = time.strftime("%H:%M:%S")
        s = f"[{stamp}] {line}"
        print(s)
        logf.write(s + "\n")

    def on_message(msg, data):
        if msg["type"] == "error":
            out("JS-ERROR " + msg.get("description", str(msg)))
            return
        p = msg["payload"]
        t = p.get("t")
        if t == "info":
            out("· " + p["m"])
        elif t == "net":
            out("NET   " + p["m"])
        elif t == "recv":
            out(f"RECV  fd={p['fd']} [{p['ch']}] {p['n']}B: {p['m']}")
        elif t == "send":
            out(f"SEND  fd={p['fd']} [{p['ch']}] {p['n']}B: {p['m']}")
        elif t == "decrypt":
            out(f"DECR  op=0x{p['op']:02x} inc=0x{p['inc']:02x} len={p['len']}: {p['m']}")
        elif t == "encrypt":
            out(f"ENCR  op=0x{p['op']:02x} len={p['len']}: {p['m']}")
            if p.get("bt"):
                out(f"      builder backtrace: {' <- '.join(p['bt'])}")
        elif t == "WORLDCTOR":
            out("========== " + p["m"])
        elif t == "MAPLOAD":
            out("############ " + p["m"])
        elif t == "FILE":
            out("FILE  " + p["m"])
        elif t == "T33":
            out(">>33  " + p["m"])
        elif t == "WALK":
            out("WALK  " + p["m"])
        else:
            out(str(p))

    if attach:
        out(f"attaching to running client...")
        session = frida.attach("NexusTK_local.exe")
        pid = None
    else:
        out(f"spawning {EXE}")
        pid = frida.spawn([EXE])
        session = frida.attach(pid)

    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid)
        out("client resumed — log in through the GUI and walk to the game handoff")
    out(f"logging to {LOG}  (Ctrl-C to stop)")
    try:
        sys.stdin.read()
    except KeyboardInterrupt:
        pass
    out("detaching")


if __name__ == "__main__":
    main()
