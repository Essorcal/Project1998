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
    # ---- stats/HUD write-watch ----
    "dispatch": 0x04b9c0,  # world packet dispatcher: thiscall(ecx=world), arg0=pktObj, [pktObj+0xc]=buf, buf[0]=opcode
}

# The self player object is [world+0x40c]; world global is *(0x4fd3c8). We diff a generous slice of the
# self object around every world packet so the stats opcode reveals itself by WHICH bytes it writes.
SELF_OFF = 0x40c
SELF_SNAP = 0x600

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
    // POKE-sweep control: the client's own chat (op 0x0e = chatType,msgLen,msg). "!poke" starts the
    // self-object sweep, "!poke <hex>" starts at a given offset, "!pokestop" stops it. Client-side only.
    if (op === 0x0e) {
      try {
        const mlen = src.add(2).readU8();
        const msg = src.add(3).readAnsiString(mlen);
        if (msg && msg.toLowerCase().indexOf('!pokestop') === 0) { pokeOn = false; send({t:'POKE', m:'sweep STOPPED'}); }
        else if (msg && msg.toLowerCase().indexOf('!poke') === 0) {
          const parts = msg.split(/\s+/);
          if (parts.length > 1) { const v = parseInt(parts[1], 16); if (!isNaN(v)) pokeCur = v; }
          if (pokeSavedOff >= 0) { try { const s = selfPtr(); if (s) s.add(pokeSavedOff).writeU8(pokeSaved); } catch(e){} pokeSavedOff = -1; }
          pokeOn = true; send({t:'POKE', m:'sweep STARTED at self+0x' + pokeCur.toString(16)});
        }
      } catch (e) {}
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

// ---- STATS/HUD WRITE-WATCH ------------------------------------------------
// Diff the self player object ([world+0x40c]) across every world packet. Whatever packet writes
// stat-shaped bytes (level/HP/MP/might/...) is the stats opcode, and the changed offsets are the
// struct layout. Layout-agnostic: we don't need to know where fields are, we watch what moves.
// Movement/coords packets (0x04/0x0c) mutate position every step -> muted to cut noise; a probe is
// sent while standing still anyway.
const SELF_OFF = __SELF_OFF__, SELF_SNAP = __SELF_SNAP__;
const MUTE_OPS = { 0x04:1, 0x0c:1 };
function readSelf(world) {
  try {
    const self = world.add(SELF_OFF).readPointer();
    if (self.isNull()) return null;
    return { self: self, bytes: new Uint8Array(self.readByteArray(SELF_SNAP)) };
  } catch (e) { return null; }
}
// Location-agnostic sentinel scan: the stats probe (Session.SendStatProbe) plants zero-free values
// exp=0x11223344 and coins=0x55667788. If ANY opcode stores them (self entity OR a separate player
// struct), scanning writable memory after the handler finds the persistent copy and its address —
// revealing where 4.95 keeps HUD stats. We scan on the probed opcode only (skip the transient packet
// buffer we were handed) and dedup addresses across calls so repeats stay quiet.
const SENTINELS = ['44 33 22 11', '11 22 33 44', '88 77 66 55', '55 66 77 88'];  // exp/coins LE & BE
const seenHits = {};
function scanSentinels(exclLo, exclHi) {
  const out = [];
  let ranges;
  try { ranges = Process.enumerateRanges('rw-'); } catch (e) { return out; }
  for (const r of ranges) {
    if (r.size > 0x2000000) continue;                 // skip huge mappings
    for (const pat of SENTINELS) {
      let hits;
      try { hits = Memory.scanSync(r.base, r.size, pat); } catch (e) { continue; }
      for (const h of hits) {
        const a = h.address;
        if (exclLo && a.compare(exclLo) >= 0 && a.compare(exclHi) < 0) continue;  // the packet buffer
        const key = String(a);
        if (seenHits[key]) continue;
        seenHits[key] = 1;
        let ctx = '<err>';
        try { ctx = hex(a.sub(8), 32); } catch (e) {}
        out.push({addr:key, pat:pat, ctx:ctx});
      }
    }
  }
  return out;
}

Interceptor.attach(at('dispatch'), {
  onEnter(args) {
    this.world = this.context.ecx;
    this.op = -1; this.buf = null;
    try { this.buf = args[0].add(0xc).readPointer(); this.op = this.buf.readU8(); } catch (e) {}
    this.snap = readSelf(this.world);
  },
  onLeave(ret) {
    if (this.op < 0 || MUTE_OPS[this.op]) return;
    // 1) self-entity diff
    if (this.snap) {
      const after = readSelf(this.world);
      if (after && String(after.self) === String(this.snap.self)) {
        const a = this.snap.bytes, b = after.bytes, diffs = [];
        for (let i = 0; i < SELF_SNAP; i++) if (a[i] !== b[i]) diffs.push(i);
        if (diffs.length) {
          const parts = diffs.map(i => '+0x' + i.toString(16) + ':' +
            ('0'+a[i].toString(16)).slice(-2) + '->' + ('0'+b[i].toString(16)).slice(-2));
          send({t:'DIFF', op:this.op, n:diffs.length, m:parts.join(' ')});
        }
      }
    }
    // 2) whole-memory sentinel scan (finds stats stored anywhere, e.g. a separate player struct)
    const lo = this.buf, hi = this.buf ? this.buf.add(0x100) : null;
    const hits = scanSentinels(lo, hi);
    for (const h of hits) send({t:'SENT', op:this.op, addr:h.addr, pat:h.pat, ctx:h.ctx});
  }
});

// ---- POKE SWEEP: find where the HUD READS self stats (read-side look-lab) ----------------------
// The write/scan hunt can't find the stats opcode (layout confound). Flip it: poke a sentinel (99)
// into ONE self-object byte at a time and revert it, so 99 flashes on whichever HUD field sources
// that offset. That pins the stat offsets on the self struct with no opcode/layout guessing; then a
// static xref of writers to those offsets gives the real stats packet. Gated by the !poke chat cmd.
const POKE_LO = 0x40, POKE_HI = 0x220, POKE_VAL = 0x63;   // 99
const POKE_SKIP = [[0x132,0x142],[0x1a0,0x1a4]];          // name text, embedded object pointer
let pokeOn = false, pokeCur = POKE_LO, pokeSaved = 0, pokeSavedOff = -1;
function selfPtr() {
  try { const w = ptr(0x4fd3c8).readPointer(); if (w.isNull()) return null;
        const s = w.add(SELF_OFF).readPointer(); return s.isNull() ? null : s; } catch (e) { return null; }
}
function pokeSkip(off) { for (const r of POKE_SKIP) if (off >= r[0] && off < r[1]) return true; return false; }
setInterval(function () {
  if (!pokeOn) return;
  const s = selfPtr(); if (!s) return;
  if (pokeSavedOff >= 0) { try { s.add(pokeSavedOff).writeU8(pokeSaved); } catch (e) {} pokeSavedOff = -1; }
  while (pokeSkip(pokeCur)) pokeCur++;
  if (pokeCur >= POKE_HI) {
    pokeOn = false;
    send({t:'POKE', m:'sweep COMPLETE (0x40..0x220). If NO HUD number ever showed 99, stats are NOT on the self object.'});
    pokeCur = POKE_LO; return;
  }
  try {
    pokeSaved = s.add(pokeCur).readU8(); pokeSavedOff = pokeCur;
    s.add(pokeCur).writeU8(POKE_VAL);
    send({t:'POKE', m:'self+0x' + pokeCur.toString(16) + ' = 99  (was 0x' + ('0'+pokeSaved.toString(16)).slice(-2) + ')  <-- watch the HUD for 99'});
  } catch (e) { send({t:'POKE', m:'self+0x' + pokeCur.toString(16) + ' <unwritable>'}); }
  pokeCur++;
}, 1600);

send({t:'info', m:'hooks installed (recv/send/connect/decrypt/encrypt/worldctor/mapload/createfile/0x33-trace/statwatch/pokesweep)'});
""".replace("__RVA_JSON__", __import__("json").dumps(RVA)) \
   .replace("__SELF_OFF__", hex(SELF_OFF)) \
   .replace("__SELF_SNAP__", hex(SELF_SNAP))


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
        elif t == "DIFF":
            out(f"DIFF  op=0x{p['op']:02x} changed {p['n']} self-bytes: {p['m']}")
        elif t == "SENT":
            out(f"SENT  op=0x{p['op']:02x} STORED sentinel [{p['pat']}] @ {p['addr']}  ctx: {p['ctx']}")
        elif t == "POKE":
            out("POKE  " + p["m"])
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
