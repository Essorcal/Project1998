#!/usr/bin/env python
"""
nexus_bot.py -- self-driving grinder for the live client. STAGE 1: PERCEPTION.

This is the foundation the auto-grinder is built on. It attaches to NexusTK.exe once
(frida, the same decrypt hook nexus_agent uses) and maintains a live WORLD MODEL:

  * my own entity id + (x,y)   -- learned by an active probe: nudge a movement key and
                                  see which entity's 0x0c walk echoes in the pressed
                                  direction. That eid is me; 0x0c then tracks me exactly.
  * every nearby entity        -- {eid: look, x, y, alive} from 0x07 spawn / 0x0c walk /
                                  0x0e despawn.
  * my vitals                  -- hp/maxhp/level/exp, read straight from the Agent (which
                                  already decodes the 0x08 stat/hp packets).

It ALSO runs an embedded Agent so the full research dataset (kills/swings/hitrate/base
stats) keeps accumulating -- one frida client feeds both the data logger and the control
brain. (Don't run nexus_agent.py at the same time; this subsumes it.)

STAGE 1 does NOT grind yet. Run it with --watch to prove the eyes track reality:
    python re/nexus_bot.py --watch
It will (1) find the live window, (2) calibrate self-identity with a few small nudges,
(3) print a live readout of where it thinks it is and what mobs it sees. Verify that
against your screen. Once perception is trusted, STAGE 2 (the behavior loop) plugs in.

    python re/nexus_bot.py --watch          # perceive only (default)
    python re/nexus_bot.py --calibrate-only # just prove self-identity, then exit
"""
import os, sys, time, json, csv, re, threading, collections
import frida

import nexus_agent as NA                     # reuse JS hook + Agent (data + vitals decode)
import pull_all as PA
import inventory as INV                         # profile/item decoders (stats+gear+items)
from bot_input_test import find_windows, post_key, send_key, VK

BE = NA.be                                   # big-endian reader

# --- SELF struct via STATIC pointer chain (from TkMemory, verified live on 7.5.2.0) ---
# The client keeps a fixed global at BASE+0x29b4e4 that points to the player's own struct.
# No ASLR (BASE=0x400000 always), so this is a permanent, instant anchor -- it replaces the
# flaky movement-wiggle self-locate that kept picking the camera/scroll field (wrong frame).
# Layout from the struct root R (all u32 unless noted). X,Y are in the DISPLAY/WIRE frame,
# i.e. exactly the coords shown on the HUD and shared with mob 0x07/0x0c positions.
NX_BASE       = 0x400000
SELF_PTR_ADDR = NX_BASE + 0x29b4e4           # [this] -> R (self struct root)
SELF_OFF = {"x": 0xFC, "y": 0x100, "curhp": 0x104, "maxhp": 0x108,
            "curmana": 0x10C, "maxmana": 0x110, "exp": 0x114, "gold": 0x11C}
SELF_LVL_OFF = 0x118                          # level is u16 here
ENT_VTABLE   = 0x622f58                       # shared vtable of every MOB entity object (self
                                              # is a subclass, vtable 0x630cb4); same layout:
                                              # uid@+0xF8, x@+0xFC, y@+0x100. Objects live in a
                                              # fixed-stride 0x20c pool -- scanning that region
                                              # for this vtable enumerates the client's REAL
                                              # entity table (re/find_entlist.py, test_enum.py)


MAPCSV = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                      "..", "data", "game-data", "map_index.csv")
P_WARPS = os.path.join(NA.OUT, "warps.csv")
P_STOP = os.path.join(NA.OUT, "STOP")   # touch this to stop the bot CLEANLY
                                        # (force-killing it can crash the client)


def load_map_names():
    """name -> (id, w, h) for every known map, used both to IDENTIFY the room-name buffer
    in memory and to validate that the cached address still holds a real room name."""
    out = {}
    try:
        with open(MAPCSV, encoding="utf-8") as f:
            for r in csv.DictReader(f):
                out.setdefault(r["name"], (r["id"], r["xs"], r["ys"]))
    except OSError:
        pass
    return out


class RoomTracker:
    """Current room name, read from client memory, plus WARP detection.

    Why this exists: a warp can drop you one tile from where you stood, so a position
    delta can never distinguish "walked" from "warped" (the old `jumped = dist > 8` test
    silently missed exactly that case, leaving the previous room's learned wall grid in
    place and mislabelling every logged kill).

    The name lives as an inline UTF-16LE string in a heap block with NOTHING pointing at
    it, so there is no static pointer chain to follow. Instead: locate it once by
    harvesting heap strings and intersecting with the known map names, then poll that
    address every tick (one cheap read). If the address stops reading a valid map name --
    reallocation, map change, restart -- re-locate automatically, cheaply first (rescan
    only the block it used to live in) and then via a full harvest.
    """

    def __init__(self, log=lambda s: None):
        self.names = load_map_names()
        self.addr = None
        self.block = None            # (lo, hi) of the containing region, for cheap rescans
        self.room = None
        self.log = log
        self.last_locate = 0.0

    # ---- locating ----
    def _harvest(self, ex, lo=0, hi=0):
        try:
            got = ex.utf16strings(4, 400000, lo, hi)
        except Exception:
            return None
        for addr, s in got:
            if s in self.names:
                return int(addr, 16), s
        return None

    def locate(self, ex, force_full=False):
        """Find the room-name buffer. Cheap block rescan first, then a full harvest."""
        now = time.time()
        if now - self.last_locate < 3.0:      # don't thrash on a transient bad read
            return False
        self.last_locate = now
        hit = None
        if self.block and not force_full:
            hit = self._harvest(ex, self.block[0], self.block[1])
        if hit is None:
            hit = self._harvest(ex)
        if hit is None:
            return False
        self.addr, self.room = hit
        try:
            r = ex.rangeof(hex(self.addr))
            if r:
                base = int(r[0], 16)
                self.block = (base, base + r[1])
        except Exception:
            pass
        self.log(f"[room] located name buffer @{hex(self.addr)} = {self.room!r}")
        return True

    # ---- polling ----
    def poll(self, ex):
        """Current room name, or None if it can't be read. Self-heals a stale address."""
        if self.addr is None:
            if not self.locate(ex):
                return None
        try:
            s = ex.rutf16(hex(self.addr), 40)
        except Exception:
            s = None
        if s and s in self.names:
            self.room = s
            return s
        # the buffer moved or holds something else -> re-locate
        if self.locate(ex):
            return self.room
        return None

    def meta(self, room=None):
        return self.names.get(room or self.room or "", ("", "", ""))


def read_self_root(ex):
    """Resolve the self-struct root R from the static pointer (re-resolved every call so it
    survives map changes / heap moves automatically). Returns R or None."""
    try:
        r = ex.ru32(hex(SELF_PTR_ADDR))
    except Exception:
        return None
    return r if (r and r >= 0x100000) else None

# LEAN, FILTERED hooks. CRITICAL: this Interceptor runs INSIDE the client's network
# thread, so it must do as little as possible -- a dense zone (30+ entities) floods the
# decrypt path with turn/walk/swing-anim packets, and forwarding all of them once locked
# up the whole machine. So we filter to ONLY the opcodes the bot decodes, BEFORE building
# any hex string or crossing the frida<->python boundary. That drops ~40%+ of traffic
# (0x11 turn, 0x1a swing-anim, chat, etc.) at near-zero cost inside the client.
#   recv we use: 07 spawn, 08 stats, 0a exp-text, 0b self-loc, 0c walk, 0e despawn,
#                0f item-info, 13 mob-hp, 39 profile
#   send we use: 06 step, 11 turn, 13 attack
BOT_JS = r"""
'use strict';
const MAIN = (Process.findModuleByName && Process.findModuleByName('__MOD__')) || Process.enumerateModules()[0];
const RECV = new Set([0x04,0x07,0x08,0x0a,0x0b,0x0c,0x0e,0x0f,0x13,0x39]);
const SEND = new Set([0x06,0x11,0x13,0x0f,0x0a]); // +0x0f = spell cast (verify our casts)
                                                  // +0x0a = right-click name request: we log
                                                  // its payload so the format is learned the
                                                  // first time a human right-clicks a mob
Interceptor.attach(MAIN.base.add(__RVA__), {
  onEnter(args){ this.out = args[2]; },
  onLeave(ret){
    try{
      let n = ret.toInt32(); if(n<=0) return; if(n>2048) n=2048;
      const op = this.out.readU8();
      if(!RECV.has(op)) return;                    // drop unused opcodes cheaply, in-client
      const b = new Uint8Array(this.out.readByteArray(n));
      send({ts:Date.now(), op:op, n:n, hex:Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ')});
    }catch(e){}
  }
});
function scanFrames(ptr, n){
  try{
    let o = 0;
    while(o + 4 <= n){
      if(ptr.add(o).readU8() === 0xAA){
        const len = (ptr.add(o+1).readU8() << 8) | ptr.add(o+2).readU8();
        if(len < 4 || len > 4096){ o++; continue; }
        const op = ptr.add(o+3).readU8();
        // include the first payload byte for move(0x06)/turn(0x11): it is the DIRECTION the
        // client actually applied -> authoritative facing (our own belief drifts whenever a
        // keypress is swallowed, which made us swing at right angles to the mob).
        if(SEND.has(op)) send({t:'send', op:op, ts:Date.now(),
                               hex:(op === 0x0a ? Array.from(new Uint8Array(
                                     ptr.add(o+3).readByteArray(Math.min(len,48))))
                                     .map(x=>('0'+x.toString(16)).slice(-2)).join(' ') : '')});
        o += 1 + len;
      } else { o++; }
    }
  }catch(e){}
}
function hookSend(mod, name){
  let a = null;
  try{ const m = Process.findModuleByName(mod); if(m) a = m.findExportByName(name); }catch(e){}
  if(!a){ try{ a = Module.findExportByName(mod, name); }catch(e){} }
  if(!a) return;
  if(name === 'WSASend'){
    Interceptor.attach(a, { onEnter(args){
      try{ const bufs = args[1]; const cnt = args[2].toInt32();
        for(let i=0;i<cnt;i++){ const wb = bufs.add(i*8); scanFrames(wb.add(4).readPointer(), wb.readU32()); }
      }catch(e){}
    }});
  } else {
    Interceptor.attach(a, { onEnter(args){ scanFrames(args[1], args[2].toInt32()); } });
  }
}
hookSend('ws2_32.dll', 'WSASend');
hookSend('ws2_32.dll', 'send');

// --- memory read/scan: used to locate + read our EXACT self position (x@A,y@A+4 u32)
// straight from the client, replacing lossy dead reckoning. snap/diffval run only during
// the one-off startup calibration; ru32 runs each tick (cheap). ---
var SNAP = [];
// Inject keystrokes from INSIDE the client process (same-process PostMessage bypasses UIPI /
// window-focus entirely, so it works reliably in the background). This is the ONLY input path
// that proved to move the character; external PostMessage/SendInput are focus-dependent.
// capture the connection object from a real send, then detach (an active trampoline on the
// address is not safe to call into), and keep a callable handle to the same function.
var __CONN = null;
const __SENDFN = new NativeFunction(ptr('0x576660'), 'int', ['pointer','pointer','uint'], 'thiscall');
try{
  const __l = Interceptor.attach(ptr('0x576660'), { onEnter(a){
    if (!__CONN){ __CONN = this.context.ecx; __l.detach(); } }});
}catch(e){}
const __user32 = Process.getModuleByName('user32.dll');
const __PostMessageW = new NativeFunction(__user32.getExportByName('PostMessageW'),
                                          'int', ['pointer','uint','uint','pointer']);
rpc.exports = {
  ru32: function(a){ try{ return ptr(a).readU32(); }catch(e){ return null; } },
  ru16: function(a){ try{ return ptr(a).readU16(); }catch(e){ return null; } },
  // resolve the self-struct static pointer chain and read (x,y) in ONE round trip -- frida RPC
  // latency is the per-tick bottleneck, so folding [ptr]->+x/+y into a single call (instead of
  // 3 ru32 round trips) is what lets the control loop run fast. ptrAddr/ox/oy passed from Python.
  selfxy: function(ptrAddr, ox, oy){
    try{ const r = ptr(ptrAddr).readU32(); if(!r || r < 0x100000) return null;
      const rp = ptr(r); const x = rp.add(ox).readU32(); const y = rp.add(oy).readU32();
      if(x==null||y==null||x>=4096||y>=4096) return null;
      return [x, y];
    }catch(e){ return null; }
  },
  // post a key (WM_KEYDOWN then WM_KEYUP) to the client's own window; arrows get the
  // extended-key lparam bit. hwnd passed as a decimal string.
  postkey: function(hwnd, vk){
    try{
      const h = ptr(hwnd);
      let lp = 1; if (vk >= 0x25 && vk <= 0x28) lp |= (1 << 24);
      __PostMessageW(h, 0x0100, vk, ptr(lp >>> 0));
      __PostMessageW(h, 0x0101, vk, ptr((lp | (1<<30) | (1<<31)) >>> 0));
      return true;
    }catch(e){ return false; }
  },
  // post a character (optionally Shift-held) as WM_KEYDOWN + WM_CHAR + WM_KEYUP -- the path
  // the cast prompt / chat / menus read. Used for Shift+Z, spell letters, Enter, Esc.
  postchar: function(hwnd, vk, ch, shift){
    try{
      const h = ptr(hwnd);
      const up = (1 | (1<<30) | (1<<31)) >>> 0;
      if (shift) __PostMessageW(h, 0x0100, 0x10, ptr(1));            // VK_SHIFT down
      __PostMessageW(h, 0x0100, vk, ptr(1));
      if (ch >= 0) __PostMessageW(h, 0x0102, ch, ptr(1));            // WM_CHAR
      __PostMessageW(h, 0x0101, vk, ptr(up));
      if (shift) __PostMessageW(h, 0x0101, 0x10, ptr(up));           // VK_SHIFT up
      return true;
    }catch(e){ return false; }
  },
  // read the live VITALS (curhp,maxhp,curmana,maxmana,exp) from the self-struct in ONE round
  // trip -- so survival can watch HP every tick cheaply (a broken/slow HP feed is what let the
  // character die: it never healed). Offsets passed from Python (SELF_OFF).
  selfstats: function(ptrAddr, oc, om, ocm, omm, oe){
    try{ const r = ptr(ptrAddr).readU32(); if(!r || r < 0x100000) return null;
      const rp = ptr(r);
      return [rp.add(oc).readU32(), rp.add(om).readU32(), rp.add(ocm).readU32(),
              rp.add(omm).readU32(), rp.add(oe).readU32()];
    }catch(e){ return null; }
  },
  // ---- room name (UTF-16LE, inline in a heap block; nothing points at it) ----
  rutf16: function(a, n){ try{ return ptr(a).readUtf16String(n); }catch(e){ return null; } },
  // byte-pattern scan (used to anchor the inventory array on a known item name)
  scanpat: function(pat, cap){
    const out = []; let rs;
    try{ rs = Process.enumerateRanges('rw-'); }catch(e){ return out; }
    for (const r of rs){
      if (r.size > 0x4000000) continue;
      let ms; try{ ms = Memory.scanSync(r.base, r.size, pat); }catch(e){ continue; }
      for (const m of ms){ out.push(m.address.toString()); if (out.length >= cap) return out; }
    }
    return out;
  },
  // Harvest UTF-16LE ASCII strings from writable memory. Used to LOCATE the room-name
  // buffer (intersect the harvest with the known map-name list); ~10s, so it runs only on
  // a cold start or when the cached address stops reading a valid map name.
  utf16strings: function(minLen, cap, lo, hi){
    const out = [];
    let rs; try{ rs = Process.enumerateRanges('rw-'); }catch(e){ return out; }
    for (const r of rs){
      const b = parseInt(r.base.toString(), 16);
      if (lo && (b + r.size < lo || b > hi)) continue;   // bounded rescan (fast path)
      if (r.size > 0x4000000) continue;                  // skip huge asset mappings
      let buf; try{ buf = new Uint8Array(r.base.readByteArray(r.size)); }catch(e){ continue; }
      let i = 0;
      while (i + 1 < buf.length){
        if (buf[i] >= 0x20 && buf[i] <= 0x7e && buf[i+1] === 0x00){
          let j = i, s = '';
          while (j + 1 < buf.length && buf[j] >= 0x20 && buf[j] <= 0x7e && buf[j+1] === 0x00){
            s += String.fromCharCode(buf[j]); j += 2;
          }
          if (s.length >= minLen){
            out.push([r.base.add(i).toString(), s]);
            if (out.length >= cap) return out;
          }
          i = j + 2;
        } else i += 2;
      }
    }
    return out;
  },
  // Send an ARBITRARY plaintext packet through the client's own send fn (it frames and
  // encrypts for us). `2d 00 00` = request self-profile -> 0x39 with the worn item list,
  // which is how the bot labels each run's loadout without anyone opening a UI.
  sendraw: function(bytes){
    if (!__CONN) return false;
    const m = Memory.alloc(bytes.length);
    for (let i = 0; i < bytes.length; i++) m.add(i).writeU8(bytes[i]);
    try{ __SENDFN(__CONN, m, bytes.length); return true; }catch(e){ return false; }
  },
  // which mapped range contains addr -> [base, size, prot] (to bound the entity-pool scan)
  rangeof: function(a){
    try{ const r = Process.findRangeByAddress(ptr(a));
      return r ? [r.base.toString(), r.size, r.protection] : null; }catch(e){ return null; }
  },
  // ENUMERATE THE CLIENT'S OWN ENTITY TABLE in one round trip: scan [lo,hi) for the entity
  // class vtable (every live entity object starts with it; the pool is fixed-stride 0x20c so
  // freed slots lose the vtable to allocator bookkeeping), then read uid@+0xF8, x@+0xFC,
  // y@+0x100 per hit. This is the client's ground truth of what EXISTS -- ghost-proof.
  enument: function(vt, lo, hi){
    const out = [];
    try{
      const pat = [vt&0xff,(vt>>>8)&0xff,(vt>>>16)&0xff,(vt>>>24)&0xff]
        .map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
      const ms = Memory.scanSync(ptr(lo), hi - lo, pat);
      for (const m of ms){
        const a = m.address;
        if (a.and(3).toInt32() !== 0) continue;          // vtable ptr is 4-aligned
        try{
          const uid = a.add(0xF8).readU32();
          const x = a.add(0xFC).readU32(), y = a.add(0x100).readU32();
          const ty = a.add(0xF4).readU32();   // 3 = creature, 0 = ground item (see below)
          // LOOK @+0x178, u16, with flag bits in the high half -- mask exactly like the 0x07
          // wire parser does (&0x7fff). Located by differential (re/find_entity_look.py):
          // one entity read 32926 -> 158 (a leviathan, cross-checked against its own 0x07) and
          // another 32793 -> 25 (squirrel family). WITHOUT this the pool supplies look=None,
          // which is what made a look-based kill whitelist refuse nearly every mob -- the bot
          // stood still being beaten by a target it would not engage.
          const lk = a.add(0x178).readU16() & 0x7FFF;
          // VALIDATE HARD: a freed/uninitialised pool slot can still carry the vtable and
          // reads as e.g. uid=4 at (0,0). That phantom dragged the bot 20+ tiles to the map
          // corner chasing nothing. Real entity ids are large (>50k observed) and no real
          // entity stands on tile 0 (maps are 1-based in this frame).
          // TYPE FIELD @+0xF4: 3 on all 24 sampled mobs, 0 on a ground item ("Rat meat").
          // Mobs render ON TOP of drops, so the client must layer them separately -- this is
          // that discriminator. Filtering here keeps ground loot out of targeting entirely
          // (loot was the "mob" that never bled and burned swings before being blacklisted).
          if (uid > 1000 && x > 0 && y > 0 && x < 1000 && y < 1000) out.push([uid, x, y, ty, lk]);
        }catch(e){}
      }
    }catch(e){}
    return out;
  },
  // RIGHT-CLICK at a client-area pixel: this makes the CLIENT itself emit the name request
  // (opcode 0x0a), so we never need to know that packet's format -- the reply names the mob.
  // Same same-process PostMessage path as the keyboard, so it works unfocused/in background.
  postmouse: function(hwnd, x, y, right){
    try{
      const h = ptr(hwnd);
      const lp = ptr(((y & 0xFFFF) << 16 | (x & 0xFFFF)) >>> 0);
      const DOWN = right ? 0x0204 : 0x0201, UP = right ? 0x0205 : 0x0202;
      const BTN  = right ? 0x0002 : 0x0001;
      __PostMessageW(h, 0x0200, 0, lp);          // WM_MOUSEMOVE first (hover/target)
      __PostMessageW(h, DOWN, BTN, lp);
      __PostMessageW(h, UP, 0, lp);
      return true;
    }catch(e){ return false; }
  },
  // Ask the server for the NAME at a map tile: plaintext `0a <be16 x> <be16 y> 00` handed to
  // the client's own send fn (0x576660, __thiscall) so IT frames+encrypts -- no cipher work.
  // `this` (the connection object) is captured from a real call, never hardcoded.
  asktile: function(x, y){
    if (!__CONN) return false;
    const m = Memory.alloc(6);
    m.writeU8(0x0a);
    m.add(1).writeU8((x>>8)&0xff); m.add(2).writeU8(x&0xff);
    m.add(3).writeU8((y>>8)&0xff); m.add(4).writeU8(y&0xff);
    m.add(5).writeU8(0);
    try{ __SENDFN(__CONN, m, 6); return true; }catch(e){ return false; }
  },
  scanu32: function(v, cap){
    v = v>>>0; const p = [v&0xff,(v>>>8)&0xff,(v>>>16)&0xff,(v>>>24)&0xff]
      .map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    const out = []; let rs; try{ rs = Process.enumerateRanges('rw-'); }catch(e){ return out; }
    for (const r of rs){ let ms; try{ ms = Memory.scanSync(r.base, r.size, p); }catch(e){ continue; }
      for (const m of ms){ out.push(m.address.toString()); if(out.length>=cap) return out; } }
    return out;
  },
  // bulk-read a memory window as hex -- ONE call replaces N per-entity scans (the entity heap
  // region is small + contiguous, so reading it once per tick and matching eids locally is fast).
  readbytes: function(addr, n){
    try{ if (n > 0x80000) n = 0x80000;
      const b = new Uint8Array(ptr(addr).readByteArray(n));
      let s = ''; for (let i=0;i<b.length;i++) s += ('0'+b[i].toString(16)).slice(-2);
      return s;
    }catch(e){ return ''; }
  },
  snap: function(loB, hiB, capMB){
    SNAP = []; let total = 0; const cap = capMB*1024*1024; let rs;
    try{ rs = Process.enumerateRanges('rw-'); }catch(e){ return 0; }
    for (const r of rs){ const b = parseInt(r.base.toString(),16);
      if (b < loB || b > hiB) continue; if (r.size > 16*1024*1024) continue;
      if (total + r.size > cap) break;
      try{ SNAP.push({base:r.base, size:r.size, bytes:r.base.readByteArray(r.size)}); total += r.size; }catch(e){} }
    return total;
  },
  diffval: function(delta){
    const out = [];
    for (const s of SNAP){ let cur; try{ cur = new Uint8Array(s.base.readByteArray(s.size)); }catch(e){ continue; }
      const old = new Uint8Array(s.bytes); const n = Math.min(cur.length, old.length) - 2;
      for (let o = 0; o + 2 <= n; o++){ const ov = old[o]|(old[o+1]<<8); const cv = cur[o]|(cur[o+1]<<8);
        if ((cv - ov) === delta && ov <= 4096 && cv <= 4096 && cv >= 0){ out.push(s.base.add(o).toString());
          if (out.length > 4000) return out; } } }
    return out;
  },
  // find the player VITALS struct by STRUCTURAL signature (no known value / no packet needed):
  //   +4 curhp(1..3000), +8 maxmana==+12 (duplicated), +16 exp(>1000), +20 level(1..99, u16),
  //   +24 curmana(<=maxmana). Returns all matching base addresses (capped).
  findvitals: function(){
    const out = []; let ranges; try{ ranges = Process.enumerateRanges('rw-'); }catch(e){ return out; }
    for (const r of ranges){
      const b = parseInt(r.base.toString(), 16);
      if (b < 0x100000 || b > 0x7ff00000 || r.size > 16*1024*1024) continue;
      let a; try{ a = new Uint8Array(r.base.readByteArray(r.size)); }catch(e){ continue; }
      const u32 = (o)=>((a[o]|(a[o+1]<<8)|(a[o+2]<<16)|(a[o+3]<<24))>>>0);
      const u16 = (o)=>(a[o]|(a[o+1]<<8));
      for (let o = 0; o + 28 <= a.length; o += 4){
        const mm = u32(o+8);
        if (mm < 50 || mm > 3000) continue;
        if (u32(o+12) !== mm) continue;
        const exp = u32(o+16); if (exp < 1000 || exp > 2000000000) continue;
        const lv = u16(o+20); if (lv < 1 || lv > 99) continue;
        const ch = u32(o+4); if (ch < 1 || ch > 3000) continue;
        if (u32(o+24) > mm) continue;
        out.push(r.base.add(o).toString());
        if (out.length >= 32) return out;
      }
    }
    return out;
  }
};
""".replace("__MOD__", NA.MOD).replace("__RVA__", hex(NA.DEC_RVA))


def dir_of(name):
    return {"up": 0, "right": 1, "down": 2, "left": 3}[name]


def _decide_log(msg):
    try:
        with open(os.path.join(NA.OUT, "bot_decisions.log"), "a", encoding="utf-8") as f:
            f.write(f"{time.strftime('%H:%M:%S')} {msg}\n")
    except OSError:
        pass


DELTA = {0: (0, -1), 1: (1, 0), 2: (0, 1), 3: (-1, 0)}   # dir -> (dx,dy); y grows south
NAME_OF = {(0, -1): "up", (1, 0): "right", (0, 1): "down", (-1, 0): "left"}


UNKNOWN_COST = 4        # cost of stepping onto an unmapped tile vs 1 for proven-walkable
PROBABLE_COST = 25      # inferred (not yet bumped) wall tile -- avoid, but not impassable


def astar(grid, start, goal, max_expand=4000):
    """4-connected A* over the observational occupancy grid. grid[(x,y)]==0 is a proven wall.
    Unknown tiles are still PASSABLE (we must be able to explore) but cost UNKNOWN_COST, so a
    route over ground we have actually walked wins over a straight line through unmapped space.
    Pure optimism made every replan drive into the same wall instead of following the corridor
    we already knew. Returns the list of tiles from start (exclusive) to goal, or None."""
    import heapq
    if start == goal:
        return []
    def blocked(t):
        return grid.get(t) == 0
    openq = [(abs(start[0] - goal[0]) + abs(start[1] - goal[1]), 0, start)]
    came, gsc, seen = {}, {start: 0}, 0
    while openq and seen < max_expand:
        seen += 1
        _, gc, cur = heapq.heappop(openq)
        if cur == goal:
            path = []
            while cur in came:
                path.append(cur)
                cur = came[cur]
            return path[::-1]
        for dx, dy in ((0, -1), (1, 0), (0, 1), (-1, 0)):
            nb = (cur[0] + dx, cur[1] + dy)
            if blocked(nb):
                continue
            gv = grid.get(nb)
            # 1 = proven walkable, 2 = PROBABLE wall (inferred line: expensive but passable so
            # a wrong guess can still be disproved), absent = unknown.
            ng = gc + (1 if gv == 1 else PROBABLE_COST if gv == 2 else UNKNOWN_COST)
            if ng < gsc.get(nb, 1 << 30):
                gsc[nb] = ng
                came[nb] = cur
                heapq.heappush(openq, (ng + abs(nb[0] - goal[0]) + abs(nb[1] - goal[1]), ng, nb))
    return None


class World:
    """Position/entity state, fed from the decrypted packet stream. Vitals live on the
    embedded Agent; this class is purely spatial + identity."""
    def __init__(self, agent):
        self.ag = agent
        self.lock = threading.Lock()
        self.ent = {}                # eid -> {"look":int, "x":int, "y":int, "ts":float}
        # GROUND ITEMS (entity type != 3). These used to be dropped on the floor of
        # refresh_from_pool; keeping them makes loot VISIBLE, so picking up after a kill can be
        # verified (did the drop actually disappear?) rather than fired blind.
        self.loot = {}               # uid -> {"x":int, "y":int, "ts":float}
        self.me = None               # my eid once calibrated (unused: self-walk not echoed)
        self.me_xy = None            # (x,y) best-known self position (= abs dead-reckoning)
        # --- dead reckoning (self position is never on the recv wire; we track it from the
        #     OUTGOING 0x06 step / 0x11 turn stream we generate, anchored to absolute the
        #     first time we land a melee hit) ---
        self.facing = 0              # 0=N 1=E 2=S 3=W -- CONFIRMED from our outgoing 0x06/0x11
        self.facing_ts = 0           # when the client last confirmed a facing (ms)
        self.rel = (0, 0)            # relative position, integrated from confirmed steps
        self.offset = None           # abs = rel + offset, set on first landed hit
        self.last_atk_ts = 0         # last outgoing 0x13, so we only anchor on OUR hits
        self.equipment = None        # (ts, label, [worn item names]) from 0x39 self-profile
        # --- room identity / warps (see RoomTracker: position deltas CANNOT detect a warp) ---
        self.room = None             # current room name, authoritative from client memory
        self.grids = {}              # room name -> its own learned wall grid (kept on return)
        self.warps = []              # (ts, from_room, from_xy, to_room, to_xy) observed warps
        self.last_name = None        # (name, ts) from the 0x0a right-click name reply
        self.last_exp_award = None   # (exp, ts) from the 0x0a "<N> experience!" kill text
        self.items = {}              # item name -> record from 0x0f item-info (loot/browse)
        self.recent_moves = []       # (ts, eid, x, y, dir) ring, for self-calibration
        self.MOVE_KEEP = 400         # keep last N move events
        self.self04 = []             # (ts, x, y) from 0x04 self-pace, the 4.95-style
                                     # self-move channel -- a fallback if the server does
                                     # NOT echo our walk as 0x0c (client-local movement)
        self.sent = []               # (seq, ts, op) of every OUTGOING frame -- the only wire
                                     # evidence of our own movement (server doesn't echo it).
                                     # seq is monotonic so we correlate a press to ITS packet
                                     # without relying on clock windows (frida delivery lags).
        self._seq = 0
        # --- EXACT self position read straight from client memory (see nexus_mem.py):
        # x = u32@mem_addr, y = u32@mem_addr+4. Located once by wiggle-calibration; kept
        # here so abs_pos() returns ground truth and dead reckoning becomes a fallback. ---
        self.mem_ex = None           # frida script exports (ru32/snap/diffval)
        self.self_root = None        # self-struct root (resolved from the static pointer)
        self.mem_addr = None         # address of the self X field (Y at +4)
        self.mem_xy = None           # last good memory read
        self.mem_bad = 0             # consecutive invalid reads -> drop the addr, re-locate
        self.mem_relocate = False    # one-shot: a map change happened, re-run calibration
        self.pool = None             # (lo, hi) of the client's entity-object pool region --
                                     # enument over it = the client's REAL entity table
        # --- OBSERVATIONAL occupancy grid for A* pathing (collision isn't a simple memory
        # byte -- see nexus_mem.py --mapfit). walkable = every tile a mob walks on (they
        # pathfind only over open ground) or that WE stand on; blocked = a tile a step into
        # failed on. Unknown tiles are optimistic-walkable and corrected on the first bump. ---
        self.grid = {}               # (x,y) -> 1 walkable / 0 blocked (per current map)
        self.wall_ts = {}            # (x,y) -> time.time() a wall was learned (for decay)
        # Walls are geometry: they do NOT move. The old 90s TTL erased the map faster than we
        # could learn it (18 walls learned -> 1 retained; the SAME tile relearned 4x), so A*
        # kept re-planning through walls it had already discovered -- which is exactly why the
        # bot could never route around anything or get out of a pit. Keep them for a long time;
        # escape() clears them wholesale if we ever do cage ourselves with a bad one.
        self.WALL_TTL = 900.0        # 15 min (was 90s -- it was deleting the map)

    # ---------- packet intake (called from the frida thread) ----------
    def on_packet(self, p):
        op = p.get("op")
        if op is None:
            return
        d = bytes(int(x, 16) for x in p["hex"].split())
        ts = p["ts"]
        if op == 0x0c and len(d) >= 10:
            eid, ox, oy, dr = BE(d, 1, 4), BE(d, 5, 2), BE(d, 7, 2), d[9]
            # VERIFIED (re/verify_0c.py, 11/15 vs memory): the packet carries the ORIGIN tile +
            # direction; the mob's true position is origin+delta. Storing the origin raw put
            # every moving mob 1 tile behind reality (in its travel direction) -- the root of
            # 'close but never right' ghost-chasing. The dir also gives exact velocity.
            dx, dy = DELTA.get(dr, (0, 0))
            x, y = ox + dx, oy + dy
            with self.lock:
                e = self.ent.get(eid)
                if e is None:
                    e = {"look": None, "x": x, "y": y, "ts": ts,
                         "px": ox, "py": oy, "pts": ts}
                    self.ent[eid] = e
                else:
                    e["px"], e["py"], e["pts"] = ox, oy, ts    # origin tile = exact prior pos
                    e["x"], e["y"], e["ts"] = x, y, ts
                self.grid[(ox, oy)] = 1             # it stood there -> walkable
                self.grid[(x, y)] = 1               # it walks there -> walkable
                self.recent_moves.append((ts, eid, x, y, dr))
                if len(self.recent_moves) > self.MOVE_KEEP:
                    self.recent_moves = self.recent_moves[-self.MOVE_KEEP:]
                if eid == self.me:
                    self.me_xy = (x, y)
        elif op == 0x07 and len(d) >= 3:
            count = BE(d, 1, 2)
            with self.lock:
                for i in range(count):
                    o = 3 + 15 * i
                    if o + 11 > len(d):
                        break
                    x, y = BE(d, o, 2), BE(d, o + 2, 2)
                    eid = BE(d, o + 5, 4)
                    look = BE(d, o + 9, 2) & 0x7fff
                    e = self.ent.get(eid)
                    if e is None:
                        self.ent[eid] = {"look": look, "x": x, "y": y, "ts": ts,
                                         "px": None, "py": None, "pts": None}
                    else:
                        if (e["x"], e["y"]) != (x, y):   # keep velocity history across refreshes
                            e["px"], e["py"], e["pts"] = e["x"], e["y"], e["ts"]
                        e["look"], e["x"], e["y"], e["ts"] = look, x, y, ts
                    self.grid[(x, y)] = 1          # entity occupies -> tile walkable
        elif op == 0x0a and len(d) >= 5:
            # 0x0a is the server TEXT channel: `0a | kind 00 | len | text | ...`
            #   kind 0x02 = a mob NAME (the reply to a right-click, e.g. "Deer"/"Squirrel")
            #   kind 0x03 = event text, notably "<N> experience!" on every kill
            # The name reply carries no eid -- it answers whatever the client last asked about.
            try:
                kind, nlen = d[1], d[3]
                if 0 < nlen <= 60 and len(d) >= 4 + nlen:
                    txt = d[4:4 + nlen].decode("latin-1").strip()
                    if not txt:
                        return
                    if kind == 0x02:
                        with self.lock:
                            self.last_name = (txt, ts)
                        _decide_log(f"NAME: {txt!r}")
                    elif kind == 0x03:
                        m = re.match(r"^(\d+)\s+experience", txt)
                        if m:                       # exact per-kill exp, straight off the wire
                            with self.lock:
                                self.last_exp_award = (int(m.group(1)), ts)
                            _decide_log(f"EXP AWARD: {m.group(1)}")
                        else:
                            _decide_log(f"EVENT: {txt!r}")
            except Exception:
                pass
        elif op == 0x0e and len(d) >= 5:
            eid = BE(d, 1, 4)
            with self.lock:
                self.ent.pop(eid, None)
        elif op == 0x13 and len(d) >= 11:
            # a landed melee hit: the damaged mob sits one tile ahead of us, so it fixes
            # our ABSOLUTE position (dead reckoning only gives relative). Only trust it if
            # WE just attacked (recent outgoing 0x13), not some ambient combat echo.
            eid, dmg = BE(d, 1, 4), d[10]
            if dmg > 0 and (ts - self.last_atk_ts) < 1500:
                with self.lock:
                    e = self.ent.get(eid)
                    if e:
                        fdx, fdy = DELTA[self.facing]
                        me_abs = (e["x"] - fdx, e["y"] - fdy)
                        self.offset = (me_abs[0] - self.rel[0], me_abs[1] - self.rel[1])
                        self.me_xy = me_abs
                        _decide_log(f"ANCHOR hit eid={eid} dmg={dmg} mob=({e['x']},{e['y']}) "
                                    f"face={self.facing} -> me_abs={me_abs}")
            # killing blow (hp bar at 0) -> the mob is gone; stop tracking/targeting its
            # now-stale tile so we don't stand there swinging at a corpse.
            if len(d) > 6 and d[6] == 0:
                with self.lock:
                    self.ent.pop(eid, None)
        elif op == 0x39 and len(d) > 14:        # self-profile -> worn gear + class
            lbl, its = PA.parse_profile(list(d))
            if its:
                with self.lock:
                    self.equipment = (ts, lbl, its)
        elif op == 0x0f and len(d) > 6:         # item-info -> item DB (loot/inventory)
            it = PA.parse_item(list(d))
            if it and it.get("name"):
                with self.lock:
                    self.items[it["name"]] = it
        elif op == 0x04 and len(d) >= 5:
            # 0x04 = the SERVER's view of our self position, in the SAME coordinate frame as
            # mobs (0x07/0x0c) -- unlike the client-memory position, which sits in a different
            # (offset) frame. Anchor wire-frame dead reckoning to it so self and mobs share
            # one frame; 0x06 steps then track precisely between anchors. This is the frame
            # the server resolves attacks in, so facing/adjacency computed here actually lands.
            x04, y04 = BE(d, 1, 2), BE(d, 3, 2)
            with self.lock:
                self.self04.append((ts, x04, y04))
                if len(self.self04) > 200:
                    self.self04 = self.self04[-200:]
                if 0 <= x04 < 4096 and 0 <= y04 < 4096:
                    self.offset = (x04 - self.rel[0], y04 - self.rel[1])
                    self.me_xy = (x04, y04)
        elif op == 0x0b and len(d) >= 6 and d[1] == 0x04:
            # map-entry self-location: our ABSOLUTE (x,y). Rebase dead reckoning onto it --
            # this is the reliable anchor whenever we cross a map edge / door, independent
            # of landing a hit. X,Y are the first two be16 fields after the subtype byte.
            x, y = BE(d, 2, 2), BE(d, 4, 2)
            with self.lock:
                # the server sends 0x0b routinely, not only on real map changes. Only a big
                # position JUMP means we actually changed maps -> reset the per-map state;
                # a routine self-loc near our current tile must NOT wipe the learned grid.
                jumped = (self.me_xy is None or
                          abs(x - self.me_xy[0]) + abs(y - self.me_xy[1]) > 8)
                self.rel = (0, 0)
                self.offset = (x, y)
                self.me_xy = (x, y)
                if jumped:
                    self.ent.clear()
                    self.grid.clear()     # occupancy grid is per-map
                # NB: do NOT invalidate mem_addr here -- the server sends 0x0b self-loc
                # routinely (not only on real map changes), and a needless re-calibration
                # wiggles the character for ~20s. A genuine map change frees/moves the self
                # struct, so its reads go out-of-range and sync_mem drops it via mem_bad.

    def on_send(self, p):
        with self.lock:
            op = p.get("op")
            self._seq += 1
            self.sent.append((self._seq, p["ts"], op))
            # NB: payload byte 0 of an outgoing frame is the SEQUENCE number, not the
            # direction (frames are AA len len op seq ...), so it can't source facing.
            # Facing is handled by always tapping toward an adjacent mob instead --
            # see Brain.face_toward.
            if op == 0x0a and p.get("hex"):
                # a human right-clicked a mob: record the REQUEST payload so we learn how to
                # ask for a name ourselves (the reply, also 0x0a, carries no eid -- the client
                # correlates it with what it asked, so we need this format to do the same).
                _decide_log(f"OUT 0x0a name-request payload: {p['hex']}")
            if op == 0x13:
                self.last_atk_ts = p["ts"]
            if len(self.sent) > 400:
                self.sent = self.sent[-400:]

    def sent_seq(self):
        with self.lock:
            return self._seq

    def sent_after(self, seq0, ops):
        """Outgoing frames newer than seq0 whose opcode is in `ops`, oldest first."""
        with self.lock:
            return [(s, ts, op) for s, ts, op in self.sent if s > seq0 and op in ops]

    def read_self_now(self):
        """FAST, direct (x,y) from the static pointer -- no locks, no dead-reckoning, ~1ms.
        The source of truth for real-time movement. Returns (x,y) or None."""
        ex = self.mem_ex
        if ex is None:
            return None
        try:                                          # ONE round trip (ptr chain + x + y in JS)
            xy = ex.selfxy(hex(SELF_PTR_ADDR), SELF_OFF["x"], SELF_OFF["y"])
        except Exception:
            return None
        if not xy:
            return None
        x, y = xy
        if not (0 <= x < 4096 and 0 <= y < 4096):
            return None
        return (x, y)

    def wait_move(self, pos0, timeout=0.24, poll=0.015):
        """Poll the memory position until it differs from pos0 or timeout. Returns the NEW
        (x,y) once it changes, else None. This is what makes stepping reactive: we act the
        instant the client actually moves us, instead of waiting on a wire round-trip."""
        t0 = time.time()
        while time.time() - t0 < timeout:
            p = self.read_self_now()
            if p and p != pos0:
                return p
            time.sleep(poll)
        return None

    def abs_pos(self):
        p = self.read_self_now()                 # live memory truth first (always correct)
        if p is not None:
            with self.lock:                      # keep dead-reckoning rebased as a fallback
                self.offset = (p[0] - self.rel[0], p[1] - self.rel[1])
            return p
        with self.lock:
            if self.offset is None:
                return None
            return (self.rel[0] + self.offset[0], self.rel[1] + self.offset[1])

    def bootstrap_pool(self):
        """Locate the entity POOL region: find one live mob object by eid scan, validate it by
        the shared class vtable, take its whole mapped range. Needs at least one wire-known mob
        (retried from tick() until it succeeds)."""
        if self.mem_ex is None:
            return None
        for eid, e in self.fresh_entities(within_ms=8000):
            try:
                hits = self.mem_ex.scanu32(eid, 40)
            except Exception:
                return None
            for hs in hits:
                S = int(hs, 16)
                try:
                    vt = self.mem_ex.ru32(hex(S - 0xF8))
                    x, y = self.mem_ex.ru32(hex(S + 4)), self.mem_ex.ru32(hex(S + 8))
                except Exception:
                    continue
                if vt != ENT_VTABLE or x is None or y is None:
                    continue
                if abs(x - e["x"]) + abs(y - e["y"]) > 4:
                    continue
                r = self.mem_ex.rangeof(hex(S - 0xF8))
                if r:
                    lo = int(r[0], 16)
                    self.pool = (lo, lo + r[1])
                    _decide_log(f"entity pool located: {hex(lo)}..{hex(lo + r[1])}")
                    return self.pool
        return None

    def refresh_from_pool(self):
        """Sync self.ent with the client's OWN entity table (enument, ~3ms, ONE round trip).
        Positions are authoritative and PRESENCE is authoritative: anything absent from the
        client's table does not exist -> purged. Ghost-chasing becomes impossible, and
        stationary mobs (which never emit walk packets -> fell out of the wire freshness
        window) stay permanently visible/targetable. The wire keeps supplying look ids,
        velocity (0x0c dir) and kill events on top."""
        if self.mem_ex is None or self.pool is None:
            return None
        try:
            rows = self.mem_ex.enument(ENT_VTABLE, self.pool[0], self.pool[1])
        except Exception:
            return None
        if not rows:
            # empty area OR the pool moved (map-change realloc) -> re-validate the region
            try:
                if not self.mem_ex.rangeof(hex(self.pool[0])):
                    self.pool = None
            except Exception:
                pass
            rows = []
        now = time.time() * 1000
        with self.lock:
            present = set()
            seen_loot = set()
            for row in rows:
                uid, x, y = row[0], row[1], row[2]
                ent_type = row[3] if len(row) > 3 else 3
                plook = row[4] if len(row) > 4 and row[4] else None
                self.grid[(x, y)] = 1          # loot sits on walkable ground -> still useful
                if ent_type != 3:              # ground item / non-creature -> never a target
                    self.ent.pop(uid, None)
                    seen_loot.add(uid)
                    self.loot[uid] = {"x": x, "y": y, "ts": now}
                    continue
                present.add(uid)
                e = self.ent.get(uid)
                if e is None:
                    self.ent[uid] = {"look": plook, "x": x, "y": y, "ts": now,
                                     "px": None, "py": None, "pts": None}
                else:
                    if plook is not None:
                        e["look"] = plook          # pool look is per-entity and always present
                    if (e["x"], e["y"]) != (x, y):     # observed move -> velocity history
                        e["px"], e["py"], e["pts"] = e["x"], e["y"], e["ts"]
                    e["x"], e["y"], e["ts"] = x, y, now
            # PURGE: not in the client's table => not in the world. Grace-keep wire-fresh
            # (<2s) entries -- covers classes outside the mob vtable (e.g. other players)
            # that the wire is actively reporting.
            for eid in [k for k, e in self.ent.items()
                        if k not in present and now - e["ts"] > 2000]:
                del self.ent[eid]
            # Loot purges IMMEDIATELY when the client stops reporting it -- that disappearance
            # is exactly how we confirm a pickup landed, so a grace window would mask it.
            for uid in [k for k in self.loot if k not in seen_loot]:
                del self.loot[uid]
        return len(rows)

    def combat_self(self, max_age_ms=2500):
        """Self position in the SERVER's frame: the latest 0x04 self-loc the server sent us.
        This is the frame the server resolves melee + mob positions in. The client-render
        MEMORY position (read_self_now) runs 1-5 tiles AHEAD of it, so comparing memory-self
        to wire-mob made us 'adjacent' in mixed frames and swing at air ('1 tile off'). Combat
        MUST use this. Falls back to dead reckoning only until the first 0x04 arrives. A stale
        0x04 while we stand still is still correct (we haven't moved either)."""
        with self.lock:
            if self.self04:
                ts, x, y = self.self04[-1]
                if (time.time() * 1000 - ts) <= max_age_ms:
                    return (x, y)
            if self.offset is not None:
                return (self.rel[0] + self.offset[0], self.rel[1] + self.offset[1])
        return None

    def read_vitals(self):
        """(curhp, maxhp, curmana, maxmana, exp) straight from the static self-struct in ONE
        round trip, or None. This is the RELIABLE HP source for survival -- the fragile
        findvitals scan reading None is what let the character die (survival never fired)."""
        ex = self.mem_ex
        if ex is None:
            return None
        try:
            v = ex.selfstats(hex(SELF_PTR_ADDR), SELF_OFF["curhp"], SELF_OFF["maxhp"],
                             SELF_OFF["curmana"], SELF_OFF["maxmana"], SELF_OFF["exp"])
        except Exception:
            return None
        if not v or not v[1] or not (0 <= v[0] <= v[1] and 15 <= v[1] < 30000):
            return None
        return tuple(v)

    def mob_state(self, eid):
        """(x, y, vx, vy, age_ms) for a mob in the WIRE frame, or None if gone. vx/vy are the
        mob's last step direction (-1/0/1) from its previous tile, so the planner can LEAD it:
        aim where it's walking TO, not the tile it just vacated (the reason we chase a fleeing
        mob's tail and never connect)."""
        with self.lock:
            e = self.ent.get(eid)
            if e is None:
                return None
            x, y, ts = e["x"], e["y"], e["ts"]
            px, py, pts = e.get("px"), e.get("py"), e.get("pts")
        now = time.time() * 1000
        vx = vy = 0
        # velocity = the step vector of the mob's LAST OBSERVED MOVE (0x0c or an enum diff),
        # valid only while that MOVE is fresh (pts) -- presence ts refreshes every tick under
        # enumeration, so it can't gauge momentum. Idle >700ms = no momentum worth leading.
        if px is not None and pts is not None and now - pts <= 700:
            vx = (x > px) - (x < px)
            vy = (y > py) - (y < py)
        return (x, y, vx, vy, now - pts if pts is not None else now - ts)

    def sync_mem(self):
        """Read exact self (x,y) from client memory via the STATIC pointer chain and rebase
        dead reckoning onto it, so abs_pos() returns ground truth in the DISPLAY/WIRE frame
        (same frame as mobs). No wiggle/calibration needed and no per-session address to go
        stale -- the root is re-resolved from the fixed global every call, so a map change
        (heap moves the struct) is handled transparently. Returns (x,y) or None."""
        if self.mem_ex is None:
            return None
        try:                                          # ONE round trip (ptr chain + x + y in JS)
            xy = self.mem_ex.selfxy(hex(SELF_PTR_ADDR), SELF_OFF["x"], SELF_OFF["y"])
        except Exception:
            return None
        if not xy or not (0 <= xy[0] < 4096 and 0 <= xy[1] < 4096):
            self.mem_bad += 1
            return None
        x, y = xy
        self.mem_bad = 0
        with self.lock:
            self.mem_addr = SELF_PTR_ADDR                    # keep "located" flag truthy
            self.mem_xy = (x, y)
            self.me_xy = (x, y)
            self.offset = (x - self.rel[0], y - self.rel[1])   # abs_pos() == (x,y) now
            self.grid[(x, y)] = 1                              # we stand here -> walkable
        return (x, y)

    def grid_copy(self):
        with self.lock:
            return dict(self.grid)

    def remove_entity(self, eid):
        with self.lock:
            self.ent.pop(eid, None)

    WALL_INFER_RUN = 3       # collinear confirmed walls needed before extrapolating the line
    WALL_INFER_LEN = 10      # how far to project the wall beyond the confirmed run

    def enter_room(self, room, pos, log=lambda s: None):
        """Called when the room-name buffer reports a DIFFERENT room than we last saw.

        This is the only reliable warp signal: some warps move you a single tile, so
        `dist > 8` can't see them. On a change we (a) record the warp with the tile we
        left from and the tile we arrived on -- that pair IS the warp link, and the
        arrival tile is where the return warp lives -- and (b) swap the learned wall grid
        for this room's own, so pathing never inherits the previous room's walls.
        """
        with self.lock:
            prev, prev_pos = self.room, self.me_xy
            if room == prev:
                return False
            if prev is not None:
                self.warps.append((time.time() * 1000, prev, prev_pos, room, pos))
                self._log_warp(prev, prev_pos, room, pos)
                log(f"[room] WARP {prev}{prev_pos} -> {room}{pos}")
            else:
                log(f"[room] entered {room!r} at {pos}")
            if prev is None:
                # first identification of the room we were already standing in -- the grid
                # learned so far belongs to THIS room, so keep it rather than dropping it
                self.grids[room] = self.grid
            else:
                self.grids[prev] = self.grid          # stash the room we just left
                self.grid = self.grids.setdefault(room, {})
                self.ent.clear()         # entity ids are per-map; the pool also reallocs
            self.room = room
            self.ag.zone = room          # every swing/kill row now carries the room
        return True

    def load_warps(self):
        """Reload every warp we have ever observed, so the map of links between rooms
        accumulates across runs instead of being relearned each session."""
        if not os.path.exists(P_WARPS):
            return 0
        n = 0
        try:
            with open(P_WARPS, encoding="utf-8") as f:
                for r in csv.DictReader(f):
                    try:
                        fx, fy = int(r["from_x"]), int(r["from_y"])
                        tx, ty = int(r["to_x"]), int(r["to_y"])
                    except (ValueError, KeyError, TypeError):
                        continue
                    self.warps.append((float(r.get("ts") or 0), r["from_room"], (fx, fy),
                                       r["to_room"], (tx, ty)))
                    n += 1
        except OSError:
            return 0
        return n

    def exit_tile(self, from_room, to_room):
        """Which tile in `from_room` sends us to `to_room`?

        Prefer a DIRECTLY OBSERVED crossing in that direction -- its from_xy is the warp
        tile itself, which is fact, not inference. Only if we have never made that
        crossing do we fall back to the tile we ARRIVED on coming the other way: warps are
        usually bidirectional and co-located, but that is a guess, so it is reported as
        one. Returns (xy, certain) or (None, False).
        """
        with self.lock:
            for ts, fr, fxy, to, txy in reversed(self.warps):
                if fr == from_room and to == to_room and fxy:
                    return fxy, True
            for ts, fr, fxy, to, txy in reversed(self.warps):
                if fr == to_room and to == from_room and txy:
                    return txy, False       # arrival tile coming the other way -- a guess
        return None, False

    def _log_warp(self, from_room, from_xy, to_room, to_xy):
        """Append the warp link so we can navigate back to a room we left."""
        row = {
            "ts": int(time.time() * 1000),
            "from_room": from_room, "from_x": from_xy[0] if from_xy else "",
            "from_y": from_xy[1] if from_xy else "",
            "to_room": to_room, "to_x": to_xy[0] if to_xy else "",
            "to_y": to_xy[1] if to_xy else "",
        }
        try:
            NA.append_csv(P_WARPS, [row],
                          ["ts", "from_room", "from_x", "from_y",
                           "to_room", "to_x", "to_y"])
        except Exception:
            pass

    def mark_blocked(self, x, y, force=False):
        with self.lock:
            # force=True lets repeated real failures override a "walkable" belief (that belief
            # comes from mobs/us being NEAR the tile, and can be wrong for object-walls).
            if force or self.grid.get((x, y)) != 1:
                self.grid[(x, y)] = 0
                self.wall_ts[(x, y)] = time.time()
                self._infer_wall_line(x, y)

    def _infer_wall_line(self, x, y):
        """Walls in a tile game are straight runs. Once WALL_INFER_RUN confirmed tiles line up,
        project the wall further along that axis as PROBABLE (grid value 2). A* treats probable
        walls as very expensive but still passable, so it immediately routes around the END of a
        long wall instead of bump-learning all 30 tiles first (which is what made the bot look
        like it was staring at a wall doing nothing). A real step onto one clears it to walkable,
        so a wrong guess self-corrects on contact. Caller holds self.lock."""
        for dx, dy in ((0, 1), (1, 0)):                       # vertical run, horizontal run
            run = 1
            for s in (1, -1):                                 # count confirmed walls both ways
                k = 1
                while self.grid.get((x + dx * s * k, y + dy * s * k)) == 0:
                    run += 1
                    k += 1
            if run < self.WALL_INFER_RUN:
                continue
            for s in (1, -1):                                 # project beyond the confirmed run
                k = 1
                while self.grid.get((x + dx * s * k, y + dy * s * k)) == 0:
                    k += 1
                for j in range(k, k + self.WALL_INFER_LEN):
                    t = (x + dx * s * j, y + dy * s * j)
                    if t in self.grid:                        # known (walkable or confirmed) -> stop
                        break
                    self.grid[t] = 2                          # probable wall
                    self.wall_ts[t] = time.time()

    def mark_walkable(self, x, y):
        with self.lock:
            self.grid[(x, y)] = 1               # proven walkable (we stood there / mob there)
            self.wall_ts.pop((x, y), None)

    def decay_walls(self):
        """Forget learned walls after WALL_TTL so a transient block (a mob that has since
        moved off, or a one-off swallowed step misread as a wall) can never permanently cage
        us. Walkable tiles are never decayed -- only proven-blocked ones revert to unknown."""
        now = time.time()
        with self.lock:
            stale = [t for t, ts in self.wall_ts.items() if now - ts > self.WALL_TTL]
            for t in stale:
                if self.grid.get(t) in (0, 2):   # confirmed AND inferred walls both expire
                    del self.grid[t]
                del self.wall_ts[t]

    def fresh_entities(self, within_ms=6000):
        """Entities updated recently. The server doesn't reliably despawn out-of-range
        mobs, so the raw dict accumulates stale ghosts (e.g. a whole town's worth) that we
        must NOT count as 'present'. Freshness = seen via spawn/walk within `within_ms`."""
        now = time.time() * 1000
        with self.lock:
            return [(eid, dict(e)) for eid, e in self.ent.items()
                    if now - e["ts"] <= within_ms]

    def loot_near(self, tile, radius=2):
        """Ground items within `radius` tiles of `tile`, nearest first.

        Sourced from the client's own entity table, so it is presence-authoritative the same
        way mob targeting is: if the client doesn't list it, it isn't there. That makes the
        post-kill trip skippable when nothing dropped, and -- more importantly -- lets us tell
        a successful pickup (item gone) from a swallowed keypress (item still sitting there)."""
        with self.lock:
            items = [(uid, e["x"], e["y"]) for uid, e in self.loot.items()]
        out = [(abs(x - tile[0]) + abs(y - tile[1]), uid, (x, y)) for uid, x, y in items]
        return sorted([o for o in out if o[0] <= radius])

    def prune(self, older_ms=15000):
        now = time.time() * 1000
        with self.lock:
            for eid in [k for k, e in self.ent.items() if now - e["ts"] > older_ms]:
                del self.ent[eid]

    def write_gear(self):
        """Persist the live worn loadout + item catalog captured this session."""
        with self.lock:
            eq = self.equipment
            items = list(self.items.values())
        if eq:
            ts, lbl, its = eq
            with open(PA.P_EQUIP, "w", newline="", encoding="utf-8") as f:
                w = csv.writer(f)
                w.writerow(["ts", "label", "slot", "item"])
                for i, it in enumerate(its):
                    w.writerow([ts, lbl, i, it])
        if items:
            with open(PA.P_ITEMS, "w", newline="", encoding="utf-8") as f:
                w = csv.writer(f)
                w.writerow(["item", "type", "icon", "stat_text", "raw_hex"])
                for it in sorted(items, key=lambda x: x["name"]):
                    w.writerow([it["name"], it["type"], it["icon"],
                                it.get("stat_text", ""), it["raw_hex"]])

    def sent_since(self, ts0, exclude=(0x13,)):
        with self.lock:
            return [(ts, op) for _, ts, op in self.sent if ts >= ts0 and op not in exclude]

    # ---------- self-identity via active probe ----------
    def snapshot_moves(self):
        with self.lock:
            return list(self.recent_moves)

    def moved_since(self, ts0):
        """eid -> (dx sign, dy sign) net motion since ts0, from 0x0c echoes."""
        net = {}
        with self.lock:
            for ts, eid, x, y, dr in self.recent_moves:
                if ts < ts0:
                    continue
                prev = net.get(eid)
                # accumulate direction of the reported step
                dx, dy = DELTA[dr]
                if prev is None:
                    net[eid] = [dx, dy, 1]
                else:
                    prev[0] += dx; prev[1] += dy; prev[2] += 1
        return net

    # ---------- readout ----------
    def nearby(self, radius=12):
        if not self.me_xy:
            with self.lock:
                return sorted(self.ent.items(), key=lambda kv: -kv[1]["ts"])[:12]
        mx, my = self.me_xy
        with self.lock:
            items = [(eid, e) for eid, e in self.ent.items()
                     if eid != self.me and abs(e["x"] - mx) + abs(e["y"] - my) <= radius]
        return sorted(items, key=lambda kv: abs(kv[1]["x"] - mx) + abs(kv[1]["y"] - my))


_WM_KEYDOWN, _WM_KEYUP, _WM_CHAR, _VK_SHIFT = 0x0100, 0x0101, 0x0102, 0x10


def _vk_of(ch):
    if ch.isalpha():
        return ord(ch.upper()), ch.isupper()
    # ',' and '<' are the game's two ITEM PICKUP keys (the Invisible spell's own description
    # names them: "pressing item pickup keys (, or <)"). '<' is Shift+',' -- same VK, shifted.
    sp = {"?": (0xBF, True), " ": (0x20, False), "\r": (0x0D, False), "\x1b": (0x1B, False),
          ",": (0xBC, False), "<": (0xBC, True)}
    return sp.get(ch, (None, False))


class Controller:
    """Injected-input primitives against the live window. PRIMARY path = same-process
    PostMessage via frida (`fkey`, set after attach): it bypasses window-focus and UIPI, so
    it moves the character reliably in the background -- the ONLY method proven to work when
    the client is elevated/unfocused. External PostMessage/SendInput kept as a fallback."""
    def __init__(self, hwnd, mode="post"):
        self.hwnd = hwnd
        self.mode = mode
        self.fkey = None            # frida exports (postkey/postchar) -- set after attach

    def tap(self, name, hold=0.09):
        vk = VK[name]
        if self.fkey is not None:
            try:
                self.fkey.postkey(str(self.hwnd), vk)
                time.sleep(hold)
                return
            except Exception:
                pass
        if self.mode == "send":
            send_key(vk, hold)
        else:
            post_key(self.hwnd, vk, hold)

    def close_chat(self, n=2):
        """Press Esc a few times to dismiss an open chat box / prompt. An open chat swallows
        movement keys (they type into it instead of walking), which looks exactly like being
        walled in -- so we defensively close it before movement bursts."""
        for _ in range(n):
            self.press_char("\x1b", 0.05)
            time.sleep(0.05)

    def press_char(self, ch, hold=0.08):
        """Post a character key (with shift for uppercase/'?') as WM_KEYDOWN + WM_CHAR + KEYUP
        -- the path the client's cast prompt / chat / menus read. Used for spell casting
        (Shift+Z, letter, Enter) and Esc recovery. Routes through frida (same-process) when
        available; falls back to external PostMessage."""
        vk, shift = _vk_of(ch)
        if vk is None:
            return
        if self.fkey is not None:
            try:
                self.fkey.postchar(str(self.hwnd), vk, ord(ch), bool(shift))
                time.sleep(hold)
                return
            except Exception:
                pass
        import ctypes
        u = ctypes.windll.user32
        scan = u.MapVirtualKeyW(vk, 0) & 0xFF
        lp = 1 | (scan << 16)
        lpu = lp | (1 << 30) | (1 << 31)
        if shift:
            u.PostMessageW(self.hwnd, _WM_KEYDOWN, _VK_SHIFT, 1)
        u.PostMessageW(self.hwnd, _WM_KEYDOWN, vk, lp)
        u.PostMessageW(self.hwnd, _WM_CHAR, ord(ch), lp)
        time.sleep(hold)
        u.PostMessageW(self.hwnd, _WM_KEYUP, vk, lpu)
        if shift:
            u.PostMessageW(self.hwnd, _WM_KEYUP, _VK_SHIFT, 1 | (1 << 30) | (1 << 31))


class Mover:
    """Dead-reckoned movement. Each key press is confirmed against the OUTGOING wire:
    a 0x06 means we stepped (advance position), a 0x11 means we only turned, nothing
    means a wall blocked us. NexusTK faces-then-steps, so step() presses up to twice."""
    MOVE, TURN = 0x06, 0x11

    def __init__(self, world, ctrl, timeout=0.30, pace=0.14, retries=3):
        self.w = world
        self.ctrl = ctrl
        self.timeout = timeout          # per-press wait for its packet (latency ~90ms)
        self.pace = pace                # gap between presses, ~ the walk cooldown
        self.retries = retries          # blocked retries before we call it a real wall
        self.last_latency = None        # ms from press to packet, for diagnostics/tuning

    def _press(self, name):
        """Press once, poll the outgoing stream until THIS press's 0x06 (step) or 0x11
        (turn) arrives, else time out. Robust to frida's async delivery (keys off a
        monotonic seq, not a clock window). A timeout means no packet -- either a wall or
        the client swallowed the press during its walk cooldown; the caller retries."""
        D = dir_of(name)
        seq0 = self.w.sent_seq()
        t0 = time.time()
        self.ctrl.tap(name)
        while time.time() - t0 < self.timeout:
            got = self.w.sent_after(seq0, (self.MOVE, self.TURN))
            if got:
                op = got[0][2]                        # first packet after the press decides
                self.last_latency = int((time.time() - t0) * 1000)
                with self.w.lock:
                    self.w.facing = D
                    if op == self.MOVE:
                        dx, dy = DELTA[D]
                        self.w.rel = (self.w.rel[0] + dx, self.w.rel[1] + dy)
                        if self.w.offset is not None:
                            self.w.me_xy = (self.w.rel[0] + self.w.offset[0],
                                            self.w.rel[1] + self.w.offset[1])
                        return "step"
                    return "turn"
            time.sleep(0.03)
        return "blocked"

    def face(self, name):
        """Turn to face `name` if not already; paced so the turn isn't swallowed."""
        if dir_of(name) == self.w.facing:
            return
        for _ in range(self.retries):
            r = self._press(name)
            if r in ("turn", "step") or dir_of(name) == self.w.facing:
                time.sleep(self.pace)
                return
            time.sleep(self.pace)

    def step(self, name):
        """Move one tile in `name`: face first (paced), then press to step, RETRYING a
        blocked step (a swallowed-during-cooldown press clears on retry; a real wall never
        does). Returns 'step' or 'blocked'."""
        self.face(name)
        for _ in range(self.retries):
            r = self._press(name)
            if r == "step":
                time.sleep(self.pace)       # respect the walk cooldown before the next tile
                return "step"
            # 'turn' (facing wasn't set yet) or 'blocked' (cooldown/wall) -> pace and retry
            time.sleep(self.pace)
        return "blocked"

    def try_step(self, name):
        """FAST single-attempt step for EXPLORATION: face, one step press, and give up
        immediately if blocked (a wall) instead of burning retries. Keeps sweeping quick in
        tight/walled areas where most directions are blocked."""
        r = self._press(name)
        if r == "turn":                     # needed to turn first; one more press to move
            r = self._press(name)
        if r == "step":
            time.sleep(self.pace)
        return r


class MemSelf:
    """Locate the client's self-position struct (x@A, y@A+4, both u32) with no known eid:
    snapshot heap, inject a known number of steps (counted from the outgoing 0x06 stream),
    and diff for the u32 that changed by exactly that many tiles. Opposite-direction moves
    are intersected so only the true coordinate survives (counters/timers get filtered).
    Proven method -- see re/nexus_mem.py. Runs once at startup (~6s) and on map change."""
    LO, HI, CAP = 0x00100000, 0x40000000, 96
    SIGN = {"down": +1, "up": -1, "right": +1, "left": -1}

    def __init__(self, ex, world, ctrl):
        self.ex = ex
        self.w = world
        self.ctrl = ctrl

    def _push(self, direction, presses):
        seq0 = self.w.sent_seq()
        for _ in range(presses):
            self.ctrl.tap(direction)
            time.sleep(0.16)
        time.sleep(0.30)
        return len(self.w.sent_after(seq0, (0x06,)))   # real steps that left the wire

    def _track(self, pos_dir, neg_dir):
        """Correlate along ONE axis: move a few tiles each way, intersect the address sets
        that changed by exactly the step count. Skips blocked directions. Returns the set
        of addresses whose value tracked movement on this axis (u16-granular)."""
        inter = None
        moves = 0
        for d, presses in ((pos_dir, 8), (neg_dir, 5), (pos_dir, 4)):
            self.ex.snap(self.LO, self.HI, self.CAP)
            s = self._push(d, presses)
            if s == 0:
                continue
            hits = set(self.ex.diffval(s * self.SIGN[d]))
            inter = hits if inter is None else (inter & hits)
            moves += 1
        return (inter or set()) if moves >= 2 else set()   # need 2 moves to disambiguate

    def _plausible(self, x_addr):
        x = self.ex.ru32(hex(x_addr))
        y = self.ex.ru32(hex(x_addr + 4))
        return (x is not None and y is not None
                and 0 <= x < 4096 and 0 <= y < 4096)

    def _mob_centroid(self):
        """Center of the currently-visible mob cluster (wire frame). The real player tile
        sits INSIDE this cluster; the camera/viewport origin is offset ~one view to the side,
        which is how we tell the true self-coordinate from the scroll value."""
        ents = self.w.fresh_entities()
        pts = [(e["x"], e["y"]) for _, e in ents
               if (e.get("look") is None or 0 < e.get("look", 0) <= 500)]
        if not pts:
            return None
        return (sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts))

    def calibrate(self, log=print):
        """Resolve the self struct via the STATIC pointer chain (no wiggle). The old
        movement-correlation approach kept locking onto the camera/scroll field (wrong frame),
        which parked the bot ~a view-width off the mobs. The static global BASE+0x29b4e4 points
        straight at the player struct, in the display/wire frame -- instant and correct."""
        root = read_self_root(self.ex)
        if root is None:
            log("self-locate: static pointer unresolved; falling back to dead reckoning")
            return None
        x = self.ex.ru32(hex(root + SELF_OFF["x"]))
        y = self.ex.ru32(hex(root + SELF_OFF["y"]))
        if x is None or not (0 <= x < 4096) or y is None or not (0 <= y < 4096):
            log(f"self-locate: implausible read ({x},{y}) at root {hex(root)}")
            return None
        with self.w.lock:
            self.w.self_root = root
            self.w.mem_addr = root + SELF_OFF["x"]
            self.w.mem_bad = 0
        log(f"self-position located @ {hex(root)} (static ptr) -> ({x},{y})")
        return root


class StatsMem:
    """Read the player's live VITALS straight from the client's stats struct (found in
    memory, see nexus_mem.py --stats): curhp/maxhp/curmp/maxmp/level/exp. Combined with the
    static attributes (might/grace/will/ac/dam -- constant at a fixed level+gear, and equal
    to the known base+gear values), this gives the agent a COMPLETE, always-live stat vector
    so every swing/kill row is fully labelled instead of waiting on the sparse 0x08 packet."""
    # All vitals live in the SAME struct as self-position, read via the static pointer chain
    # (see SELF_OFF). curhp AND maxhp are both real fields now (no hp-peak guessing), so
    # survival math uses the true max. Re-resolved from the fixed global every read, so a
    # level-up or map change can never break it.
    ATTR = {"might": 11, "grace": 17, "will": 9, "ac": 69, "dam": 1}   # refined via statblock

    def __init__(self, ex, agent):
        self.ex = ex
        self.ag = agent
        self.addr = None           # last resolved root (for the status flag)

    def find(self, log=print):
        """Resolve the self struct via the static pointer and validate it's a real player.
        No scan/seed needed -- works immediately at startup, any level."""
        v = self.read()
        if not self._plausible_player(v):
            self.addr = None
            return None
        log(f"self struct @ {hex(self.addr)} (static ptr) -> {v}")
        return self.addr

    @staticmethod
    def _plausible_player(v):
        if not v:
            return False
        ch, mh, mm, exp = v.get("curhp"), v.get("maxhp"), v.get("maxmana"), v.get("exp")
        return (mh and 15 <= mh < 30000 and ch is not None and 0 <= ch <= mh
                and mm and mm <= 6000 and exp and exp >= 1000)

    def read(self):
        # The self/position struct holds the fast-changing VITALS reliably (curhp/maxhp/mana/
        # exp). It does NOT hold a valid level -- +0x118 is a different counter (it read 18
        # while the real level was 16). Level + attributes come from the wire 0x08 statblock,
        # which is authoritative; we never invent them here.
        root = read_self_root(self.ex)
        if root is None:
            self.addr = None
            return None
        self.addr = root
        try:
            r32 = lambda o: self.ex.ru32(hex(root + o))
            return {"curhp": r32(SELF_OFF["curhp"]), "maxhp": r32(SELF_OFF["maxhp"]),
                    "maxmana": r32(SELF_OFF["maxmana"]), "curmana": r32(SELF_OFF["curmana"]),
                    "exp": r32(SELF_OFF["exp"])}
        except Exception:
            return None

    def sync(self):
        """Push the live VITALS (curhp/maxhp/mana/exp) from memory into the agent. Level and
        attributes (might/grace/will/ac/dam) are OWNED by the wire 0x08 statblock -- we never
        overwrite them here (that's what mislabelled the level as 18). Returns the dict or
        None if the read looks invalid (agent keeps its last good values)."""
        v = self.read()
        if not v or not v.get("maxhp"):
            return None
        if not (0 <= (v.get("curhp") or -1) <= v["maxhp"]):
            return None
        ag = self.ag
        with ag.lock:
            ag.curhp = v["curhp"]
            ag.curmana = v["curmana"]
            ag.exp = v["exp"]
            # Rebuild a COMPLETE cur so downstream logging never KeyErrors. maxhp/maxmana come
            # from memory (reliable, live); level + attributes come from the WIRE statblock
            # (ag.level / prior cur), NEVER from memory (+0x118 was a bad level guess). ATTR
            # seeds only fill in until the first wire statblock arrives.
            prev = ag.cur or {}
            ag.cur = {"level": ag.level if ag.level else prev.get("level"),
                      "might": prev.get("might", self.ATTR["might"]),
                      "grace": prev.get("grace", self.ATTR["grace"]),
                      "will": prev.get("will", self.ATTR["will"]),
                      "maxhp": v["maxhp"], "maxmana": v["maxmana"]}
        return v


class MemEntities:
    """LIVE enemy perception from client memory -- the robust fix for stale wire tracking.
    Every entity is a heap object: uid@BASE == the wire eid, X@BASE+4, Y@BASE+8 (u32 tiles),
    found by scanning for the eid (see re/verify_ent_layout.py, 19/19). The wire (0x07/0x0c/
    0x0e) supplies the ROSTER (which eids exist + their look/type); memory supplies every
    entity's TRUE position each tick (wire pos lags 0-6 tiles). Also PRUNES ghosts: an entity
    whose struct is freed (uid no longer at BASE, re-scan fails) is removed from the world."""
    MARGIN = 0x2000            # heap window padding around the known entity bases
    CAP = 0x20000             # max window bytes to bulk-read per tick (128KB)

    def __init__(self, ex, world):
        self.ex = ex
        self.w = world
        self.slots = {}            # eid -> BASE addr (cached)
        self.mem_pos = {}          # eid -> (x,y) client-render position (for aggro/diagnostics)
        self.miss = collections.Counter()   # eid -> consecutive resolve failures
        # NOTE: we deliberately do NOT overwrite world.ent's (x,y) with these memory positions.
        # Memory = the CLIENT-RENDER frame, which runs 1-5 tiles AHEAD of the SERVER (our fast
        # client-local walk outruns the server). Melee resolves on the SERVER position, so
        # combat must use the WIRE (0x07/0x0c = server) positions or every swing hits air.
        # MemEntities' job here is GHOST-PRUNING (remove entities whose struct is freed) +
        # supplying mem_pos for aggro trend analysis; the wire owns the combat positions.

    def _scan(self, eid):
        """Full-memory scan for a single eid (SLOW; only for bootstrap / entities outside the
        known heap window). Returns BASE or None."""
        try:
            hits = self.ex.scanu32(eid, 40)
        except Exception:
            return None
        for a in hits:
            ap = int(a, 16)
            x = self.ex.ru32(hex(ap + 4)); y = self.ex.ru32(hex(ap + 8))
            if x is not None and 1 <= x <= 600 and y is not None and 1 <= y <= 600:
                return ap
        return None

    def refresh(self):
        """One bulk read of the entity heap window per tick, then locate every rostered eid in
        it locally and overwrite its (x,y) with the LIVE memory position. Ghosts (structs gone)
        are pruned. This is O(1 read) per tick instead of O(N scans) -- keeps the loop reactive."""
        if self.ex is None:
            return
        now = time.time() * 1000
        with self.w.lock:
            roster = list(self.w.ent.keys())
        if not roster:
            return
        # bootstrap the heap window from any cached base; else scan one eid to seed it
        bases = list(self.slots.values())
        if not bases:
            for eid in roster:
                b = self._scan(eid)
                if b:
                    self.slots[eid] = b; bases = [b]; break
            if not bases:
                return
        lo = min(bases) - self.MARGIN
        size = min(self.CAP, (max(bases) + self.MARGIN) - lo)
        hexbuf = self.ex.readbytes(hex(lo), size)
        buf = bytes.fromhex(hexbuf) if hexbuf else b""
        n = len(buf)
        u32 = lambda o: int.from_bytes(buf[o:o+4], "little") if 0 <= o and o + 4 <= n else None

        def find_in_buf(eid):
            pat = eid.to_bytes(4, "little")
            i = buf.find(pat)
            while i >= 0:
                x, y = u32(i + 4), u32(i + 8)
                if x is not None and 1 <= x <= 600 and y is not None and 1 <= y <= 600:
                    return i
                i = buf.find(pat, i + 4)
            return None

        scans_left = 2          # RATE-LIMIT the slow full-memory scans per refresh -- an
                                # unbounded scan storm (window missing many eids) was the hang.
        for eid in roster:
            base = self.slots.get(eid)
            off = (base - lo) if (base is not None and lo <= base < lo + n) else None
            if off is None or u32(off) != eid:            # not cached-in-window / moved
                off = find_in_buf(eid)
                if off is not None:
                    self.slots[eid] = lo + off
                elif scans_left > 0:                      # outside window -> at most 2 scans/tick
                    scans_left -= 1
                    b = self._scan(eid)
                    if b is None:
                        self.miss[eid] += 1
                        if self.miss[eid] >= 3:           # struct truly gone -> prune ghost
                            self.w.remove_entity(eid)
                            self.slots.pop(eid, None); self.miss.pop(eid, None)
                        continue
                    self.slots[eid] = b
                    x = self.ex.ru32(hex(b + 4)); y = self.ex.ru32(hex(b + 8))
                    self._apply(eid, x, y, now)
                    self.miss.pop(eid, None)
                    continue
                else:
                    continue                              # defer to a later tick (stay reactive)
            self.miss.pop(eid, None)
            self._apply(eid, u32(off + 4), u32(off + 8), now)

    def _apply(self, eid, x, y, now):
        # Store the client-render position separately (NOT into world.ent -- combat uses the
        # server/wire frame). Also refresh the wire entity's ts so a live-in-memory mob is never
        # aged out as a ghost, and mark its tile walkable for pathing.
        if x is not None and 1 <= x <= 600 and y is not None and 1 <= y <= 600:
            self.mem_pos[eid] = (x, y)
            with self.w.lock:
                e = self.w.ent.get(eid)
                if e is not None:
                    e["ts"] = now                         # confirmed present in memory -> fresh
                self.w.grid[(x, y)] = 1


def calibrate_moveop(world, ctrl, rounds=4):
    """Find the client's OUTGOING move + turn opcodes by pressing each direction and
    seeing what leaves the wire. Self-walk isn't echoed in recv, so our own send stream
    is the only signal a step happened. NexusTK faces-then-steps: pressing a NEW direction
    turns first (turn opcode), pressing the SAME direction again steps (move opcode) -- so
    the opcode that appears on the *second* identical press is the move opcode.

    Returns dict {'move': op|None, 'turn': op|None, 'by_op': Counter}."""
    print(f"Probing outgoing move/turn opcodes ({ctrl.mode} input)...")
    first_press = collections.Counter()   # ops seen right after a direction CHANGE
    repeat_press = collections.Counter()   # ops seen on an immediate repeat of same dir
    total = collections.Counter()
    seq = ["left", "up", "right", "down"]   # each is a direction change from the previous
    for r in range(rounds):
        for name in seq:
            # press 1: direction change -> expect a TURN
            t0 = time.time() * 1000
            ctrl.tap(name)
            time.sleep(0.28)
            for _, op in world.sent_since(t0):
                first_press[op] += 1; total[op] += 1
            # press 2: same direction -> expect a STEP
            t1 = time.time() * 1000
            ctrl.tap(name)
            time.sleep(0.28)
            for _, op in world.sent_since(t1):
                repeat_press[op] += 1; total[op] += 1

    if not total:
        print("  NOTHING went out on the wire during nudges. The character moved but no")
        print("  packet was captured -- the send hook may not be on the real egress path.")
        return {"move": None, "turn": None, "by_op": total}

    # move opcode: dominant on repeat presses (a same-dir step). turn opcode: appears on
    # first (changed-dir) presses but drops on repeats.
    move_op = repeat_press.most_common(1)[0][0] if repeat_press else None
    turn_candidates = [(op, first_press[op] - repeat_press.get(op, 0))
                       for op in first_press]
    turn_candidates.sort(key=lambda x: -x[1])
    turn_op = turn_candidates[0][0] if turn_candidates and turn_candidates[0][1] > 0 else None
    if turn_op == move_op:
        turn_op = None

    print(f"  outgoing opcodes seen: "
          + ", ".join(f"0x{op:02x}:{n}" for op, n in total.most_common()))
    print(f"  -> MOVE opcode: {'0x%02x' % move_op if move_op is not None else '?'} "
          f"(dominant on same-dir repeat)")
    print(f"  -> TURN opcode: {'0x%02x' % turn_op if turn_op is not None else '?'} "
          f"(only on direction change)")
    return {"move": move_op, "turn": turn_op, "by_op": total}


_pkt_count = [0]        # total frida messages processed (for a live rate readout)


def build_pump(world, agent, raw_log=False):
    """frida message handler feeding BOTH the data Agent and the spatial World. Per-packet
    disk logging is OFF by default -- writing every frame to raw_packets.jsonl in a hot loop
    added disk load during the lockup. Enable with --raw only for short offline captures."""
    raw = open(NA.P_RAW, "a", encoding="utf-8", buffering=1) if raw_log else None

    def on_message(msg, data):
        if msg.get("type") != "send":
            return
        p = msg["payload"]
        _pkt_count[0] += 1
        if raw is not None:
            raw.write(json.dumps(p) + "\n")
        try:
            if p.get("t") == "send":          # outgoing frame (filtered to 06/11/13)
                world.on_send(p)
                if p.get("op") == 0x13:       # attack trigger -> feed the hit-rate math
                    agent.on_attack(p["ts"])
            else:
                agent.on_packet(p)
                world.on_packet(p)
        except Exception:
            pass
    return on_message


CLIENT_GONE = [False]   # set when the client process dies -> loops exit instead of spinning


def attach(on_message, pid=None):
    """Hook the client. `pid` MUST be given when more than one client is running: the
    window we drive (find_windows()[0]) and frida's process list are independently
    ordered, so defaulting to pids[0] can hook one client while injecting input into
    ANOTHER -- reading one game's memory while playing a different one."""
    dev = frida.get_local_device()
    pids = [pr.pid for pr in dev.enumerate_processes()
            if pr.name.lower() == NA.MOD.lower()]
    if not pids:
        raise RuntimeError(f"{NA.MOD} not running")
    if pid is not None and pid not in pids:
        raise RuntimeError(f"asked to hook pid {pid} but it is not a live {NA.MOD}")
    if pid is None:
        if len(pids) > 1:
            print(f"WARNING: {len(pids)} clients running {pids}; hooking {pids[0]}. "
                  f"Pass the pid explicitly to be sure input and memory match.")
        pid = pids[0]
    s = dev.attach(pid)
    # WATCHDOG: when the client exits, frida detaches. Without this the bot kept "playing" a
    # dead client for 8 HOURS -- stale memory reads, input into the void, an endless
    # "no open direction" sweep. Now it stops immediately and says why.
    def _detached(reason, *a):
        if str(reason) == "application-requested":
            return                    # our own s.detach() at the end of a probe -- not a death
        CLIENT_GONE[0] = True
        print(f"\n*** CLIENT GONE ({reason}) -- stopping ***", flush=True)
    s.on("detached", _detached)
    sc = s.create_script(BOT_JS)
    sc.on("message", on_message)
    sc.load()
    print(f"hooked {NA.MOD} pid {pid}")
    return s, sc


def watch_loop(world, agent, deadline=None):
    """Live perception readout -- no acting. Ctrl-C to stop."""
    print("\n--- LIVE PERCEPTION (Ctrl-C to stop) ---")
    last_flush = 0
    while True:
        if deadline and time.time() > deadline:
            print("time limit reached.")
            return
        me = world.me_xy
        lvl, hp = agent.level, agent.curhp
        maxhp = agent.cur["maxhp"] if agent.cur else "?"
        near = world.nearby()
        line = [f"me eid={world.me} @ {me}  lvl={lvl} hp={hp}/{maxhp}  mobs={len(near)}"]
        for eid, e in near[:6]:
            dx = e["x"] - me[0] if me else "?"
            dy = e["y"] - me[1] if me else "?"
            line.append(f"    look{e['look']} eid={eid} @({e['x']},{e['y']}) d=({dx},{dy})")
        print("\n".join(line))
        # keep the dataset flowing while we watch
        if time.time() - last_flush > 5:
            with agent.lock:
                agent.flush()
            agent.flush_swings()
            last_flush = time.time()
        time.sleep(1.5)


P_BOTSTATUS = os.path.join(NA.OUT, "bot_status.json")
P_DECIDE = os.path.join(NA.OUT, "bot_decisions.log")


class Brain:
    """The grind loop. Strategy is deliberately look-agnostic: attack the nearest entity,
    keep hitting whatever takes damage, blacklist whatever doesn't (NPCs / players / gone).
    That needs no trustworthy look table AND records the real live look->mob mapping as a
    side effect. Position comes from dead reckoning; it auto-anchors to absolute the first
    time a hit lands (World.on_packet 0x13), upgrading blind bootstrap into targeted nav."""
    HP_FLOOR = 55          # absolute HP to break off and recover (until maxhp is known)
    BLACKLIST_SEC = 20     # how long a persistently non-damaging entity stays ignored
    AIR_ANCHOR_DROP = 6    # air-swings across DISTINCT targets before we distrust the anchor
    MOB_LOOK_MAX = 500     # real mob sprites are small ids; players/appearances are huge
                           # (16k+), so anything above this is NOT a huntable mob -> skip

    def __init__(self, world, agent, mover, ctrl, hunt_looks=None):
        self.w = world
        self.ag = agent
        self.mv = mover
        self.ctrl = ctrl
        self.hunt = set(hunt_looks) if hunt_looks else None   # None -> any (damage-gated)
        # NAME WHITELIST (--names). When set this is an ABSOLUTE gate: we attack only mobs whose
        # resolved name is on it, and an UNNAMED mob is never attacked. Look-id whitelisting is
        # not enough on a map that mixes safe and lethal mobs -- we don't know the look ids up
        # front, and guessing costs a death. Names are slow to resolve (one right-click round
        # trip each), so the look -> verdict caches below make it a one-time cost per species.
        self.hunt_names = None     # set of lowercase allowed names
        self.look_ok = set()       # looks confirmed to BE an allowed name
        self.look_bad = set()      # looks confirmed NOT to be -- never attack, flee from
        self.flee_ts = 0
        self.ments = None          # MemEntities: live enemy positions from memory (set by grind_loop)
        self.blacklist = {}        # eid -> expiry ts
        self._sendctrl = None      # SendInput controller (shifted keys only)
        self.no_melee = {}         # eid -> expiry: standing ON our tile, so it
                                   # CANNOT be meleed. Kept separate from
                                   # blacklist because the attacker-priority path
                                   # clears blacklist entries, which re-targeted
                                   # these forever (a livelock: switch -> reject ->
                                   # switch, thousands of times a second).
        self.state = "init"
        self.session_kills = 0
        self.session_loot = 0      # items picked up this session
        self.loot_enabled = True   # --no-loot turns off the post-kill pickup trip
        self._last_tile = {}       # eid -> last seen tile, so a kill knows where the drop is
        self.k0 = None             # kills.csv baseline
        self.damaged_looks = collections.Counter()   # look -> hits landed (live truth)
        self.explore_dir = 0
        self.target_eid = None     # locked target: hammer ONE mob until it dies
        self.hp_max_seen = 0       # proxy for maxhp (until a statblock gives the real one)
        self.air_streak = 0        # consecutive air-swings; a long run means a bad anchor
        self.best_dist = {}        # eid -> closest Manhattan distance we've achieved to it
        self.approach_fail = {}    # eid -> approach cycles with NO progress (walled off)
        self.UNREACHABLE = 2       # no-progress approach cycles before giving up on a mob
        self.WEDGE_GIVEUP = 4      # our OWN pos unchanged this many cycles = truly wedged (only
                                   # then abandon a mob; missing alone never abandons it)
        self.STRIKE_SWINGS = 40    # max rapid swings per strike() commitment (relentless)
        # --- exploration reach. A grind area is far bigger than the few tiles around where we
        #     happen to stand, and mobs respawn across the whole map, so nibbling the nearest
        #     frontier starves us of targets. EXPLORE_REACH caps how far one frontier trek may
        #     score for (distance is a POSITIVE term now, so distant frontiers win);
        #     EXPLORE_SWEEP is the blind fallback when nothing is pathable and needs to actually
        #     cover ground rather than shuffle in place.
        self.EXPLORE_REACH = 30
        self.EXPLORE_SWEEP = 10
        self.SWING_GAP = 0.11      # gap between swings (~client swing cooldown; keep it TIGHT)
        self._last_axis = "x"      # hysteresis for straight-run movement (avoid axis flip-flop)
        self._wall_fail = {}       # (x,y) -> consecutive failed steps INTO it (wall only after >=2)
        self._ments_ts = 0.0       # last heavy memory-entity refresh (throttled off the hot loop)
        self.attackers = {}        # eid -> ts we last took damage while it was adjacent
        self.ATTACKER_TTL = 12     # seconds an attacker keeps priority after it last hit us
        self._last_hp = None       # previous tick's HP, to detect incoming damage
        self._name_wait = None     # (eid, ts) name request in flight (reply is per-TILE and
                                   # carries no eid, so we attribute it to what we asked about)
        self._name_ts = 0.0        # last name request (server ignores rapid-fire; pace ~0.8s)
        self.hits_on = {}          # eid -> landed hits this engagement (tank detection)
                                   # (no tank/hit-count give-up exists by design: if we've
                                   # engaged a mob we finish it. The only unlock is dealing
                                   # ZERO damage over many bursts, i.e. not a valid target.)
        self._gain_ts = {}         # eid -> last time we got CLOSER to it (time-based give-up)
        self.me_now = None         # self-pos read ONCE at the top of each tick, reused everywhere
        self.room_tracker = RoomTracker(log=self._dlog)  # room identity + warp detection
        self._room_ts = 0.0        # last room poll (throttled; a re-locate costs a harvest)
        self.home_room = None      # room we hunt in; a warp out of it triggers return_home()
        self._pending_step = None  # (tgt, pos0, dir, need_turn, ts) fired-but-unconfirmed step
        self._last_step_ts = 0.0   # when we last fired a step (pace to the walk cooldown)
        self.STEP_GAP = 0.18       # min seconds between fired steps (~ client walk cooldown)
        self.no_hit = {}           # eid -> approach cycles with no landed hit (ghost/uncatchable)
        self.pos_hist = collections.deque()   # (ts, pos) to detect being globally stuck
        self.escape_dir = 0        # rotating heading for the blind-sweep fallback
        self.travel_path = []      # current multi-tile plan, advanced one step per tick
        self._bad_frontier = {}    # frontier tiles we failed to reach -> (tile, ts), retried later
        self.STUCK_SEC = 10        # penned-in this long (position barely moves) -> escape
        # --- SURVIVAL: watch HP, predict time-to-death, and cast to survive. Spells map to
        # letters by spellbook order: Soothe=a (heal), Gateway=b (escape), Might=c (buff). ---
        self.hp_hist = collections.deque()    # (ts, hp) over a short window -> loss rate
        self.last_soothe = 0
        self.last_might = 0
        self.SOOTHE_FRAC = 0.75    # heal when HP drops below this -- never coast down toward
                                   # half HP; healing is cheap and fast, dying is not
        self.TOPOFF_FRAC = 0.95    # after a kill, heal back up to ~full before the next fight
        self.MANA_FLOOR = 0.15     # keep this much mana in reserve (don't dry out on top-offs)
        self._need_topoff = False  # set when a mob we were damaging died
        self.GATEWAY_FRAC = 0.25   # emergency-escape below this fraction
        self.GATEWAY_TTD = 2.5     # ...or if predicted seconds-to-death is under this
        self.SOOTHE_CD = 0.35      # min seconds between Soothe casts -- casting is FAST
                                   # (Shift+Z -> letter, no Enter); the old 1.8s made healing
                                   # lose the race against incoming damage
        self.SPELL_CAP = 3         # hard limit: at most 3 spells per rolling second
        self.SPELL_GAP = 0.30      # pause between chained casts (3 in ~0.9s = at the cap)
        self._spell_ts = collections.deque()   # cast timestamps, for the rolling-second cap
        self.MIGHT_CD = 150        # refresh the Might buff this often

    # ---------- helpers ----------
    def vitals(self):
        hp = self.ag.curhp
        maxhp = self.ag.cur["maxhp"] if self.ag.cur else None
        return hp, maxhp

    def _now(self):
        return time.time() * 1000

    def _dlog(self, msg):
        try:
            with open(P_DECIDE, "a", encoding="utf-8") as f:
                f.write(f"{time.strftime('%H:%M:%S')} [{self.state}] {msg}\n")
        except OSError:
            pass

    def fetch_profile(self, tries=6):
        """Worn items straight from the server (`2d 00 00` -> 0x39). Authoritative."""
        with self.w.lock:
            self.w.equipment = None
        for i in range(tries):
            try:
                self.w.mem_ex.sendraw([0x2d, 0x00, 0x00])
            except Exception:
                pass
            if i == 1:
                # sendraw needs the connection object, captured from the client's FIRST
                # send. If none has happened yet the request silently no-ops, so nudge the
                # client into sending with a turn (cosmetic -- changes facing, not position).
                self.ctrl.tap("left"); time.sleep(0.2); self.ctrl.tap("right")
            time.sleep(0.6)
            with self.w.lock:
                eq = self.w.equipment
            if eq:
                return list(eq[2])
        return []

    # equipment slot letters for the Shift+T take-off prompt
    SLOTS = {"weapon": "w", "armor": "a", "helm": "h", "left": "l", "right": "r"}

    def unequip(self, slot):
        """Take ONE item off, by equipment slot letter.

        Slots: w=weapon, a=armor, h=helm, l=left hand, r=right hand.
        Uppercase A after Shift+T means ALL -- lowercase is a single slot.

        The sequence is Shift+T then the LOWERCASE slot letter (Shift+L does nothing --
        verified live). Shifted keys must go through SendInput: PostMessage never sets real
        keyboard state, so the client's GetKeyState(VK_SHIFT) reads "up" and drops the key.
        SendInput needs focus, so this briefly foregrounds the client -- acceptable because
        it only happens at swap points, never mid-fight.

        This is what makes `hit` a clean single-stat variable: wearing a second ring just
        fills the other hand (it does NOT displace), so taking one OFF is the only way to
        move `hit` without touching anything else.
        """
        import ctypes
        before = self.fetch_profile()
        if not before:
            self._dlog("unequip: profile unreadable -- refusing to touch gear")
            return None
        if self._sendctrl is None:
            self._sendctrl = Controller(self.ctrl.hwnd, mode="send")
            self._sendctrl.fkey = None
        try:
            ctypes.windll.user32.SetForegroundWindow(self.ctrl.hwnd)
        except Exception:
            pass
        time.sleep(0.4)
        self._sendctrl.press_char("T"); time.sleep(0.45)
        self._sendctrl.press_char(slot); time.sleep(1.2)
        self._sendctrl.close_chat(1)
        after = self.fetch_profile()
        gone = [i for i in before if i not in after] if after else []
        self._dlog(f"unequip slot {slot!r} -> removed {gone} | worn {after}")
        return gone

    def swap_to(self, item, must_remove=None):
        """Wear `item`, so a stat varies WITHOUT anyone managing gear by hand.

        Slot letters are read LIVE from the client's inventory array: they shift as items
        move (one slot held four different items in a single session), so a hard-coded key
        would eventually wear the wrong thing. The result is then verified against the 0x39
        profile -- if `item` isn't actually worn afterwards, we report and stop rather than
        keep swinging while the data silently claims the wrong loadout.
        """
        before = self.fetch_profile()
        if not before:
            self._dlog("swap: profile unreadable -- skipping (won't touch gear blind)")
            return False
        if item in before and not must_remove:
            return True                       # already wearing it, nothing to do
        if must_remove and must_remove not in before:
            return True                       # the thing we wanted gone is already off
        if item in before and must_remove in before:
            # BOTH are already worn -- e.g. two ring slots holding Black ring AND Sea ring.
            # Wearing `item` would be a no-op and we'd report a successful swap having
            # changed nothing (which silently produced an entire "experiment" with zero
            # variance). Refuse instead of lying about it.
            self._dlog(f"swap: both {item!r} and {must_remove!r} already worn -- "
                       f"cannot vary this slot; pick items that share ONE slot")
            return False
        try:
            letter = INV.letter_of(self.w.mem_ex, item)
        except Exception as e:
            self._dlog(f"swap: inventory read failed ({e})")
            return False
        if not letter:
            self._dlog(f"swap: {item!r} not in inventory")
            return False
        self.ctrl.close_chat(1)
        self.ctrl.press_char("w"); time.sleep(0.35)
        self.ctrl.press_char(letter); time.sleep(1.3)
        self.ctrl.close_chat(1)
        after = self.fetch_profile()
        # Success means the intended item is ON *and*, when we're varying a stat, the item
        # it was supposed to displace is OFF. Checking only "is it worn" is what let a
        # no-op pass as a swap.
        ok = item in after and (must_remove is None or must_remove not in after)
        self._dlog(f"swap -> {item!r} via '{letter}': {'OK' if ok else 'FAILED'} | {after}")
        if ok:
            gone = [i for i in before if i not in after]
            self._dlog(f"       displaced {gone}")
        return ok

    FACE_DELTA = {0: (0, -1), 1: (1, 0), 2: (0, 1), 3: (-1, 0)}   # N E S W

    def _stamp_swing(self):
        """Record WHO we're swinging at and the geometry, so the resulting attempt row is
        self-contained. `rel_dir` is where the mob sits relative to our facing
        (0 = straight ahead, 2 = directly behind us); for a Rogue, flank/backstab bonuses
        make that a real predictor of both landing and damage, and it's free to capture."""
        ag, w = self.ag, self.w
        me = self.me_now or w.abs_pos()
        eid = self.target_eid
        mob = None
        with w.lock:
            if eid is not None:
                e = w.ent.get(eid)
                if e:
                    mob = (e["x"], e["y"])
            if mob is None and me:
                # No lock (calibration/sweep swings, or the lock just died): a melee swing
                # only ever resolves against an ADJACENT mob, so attribute it to one if
                # present. If nothing is adjacent this isn't a real attempt at all -- log
                # nothing, rather than feed the hit rate a guaranteed miss.
                adj = [(u, e2) for u, e2 in w.ent.items()
                       if abs(e2["x"] - me[0]) + abs(e2["y"] - me[1]) <= 1]
                if adj:
                    eid, e2 = min(adj, key=lambda t: abs(t[1]["x"] - me[0])
                                  + abs(t[1]["y"] - me[1]))
                    mob = (e2["x"], e2["y"])
        if eid is None or mob is None:
            with ag.lock:
                ag.swing_ctx = None          # tells on_attack to skip this one
            return
        ctx = {"eid": eid, "mob": ag.mob_names.get(eid, ""), "zone": ag.zone,
               "self_x": me[0] if me else "", "self_y": me[1] if me else "",
               "mob_x": mob[0] if mob else "", "mob_y": mob[1] if mob else "",
               "facing": w.facing}
        if me and mob:
            dx, dy = mob[0] - me[0], mob[1] - me[1]
            ctx["dist"] = abs(dx) + abs(dy)
            fx, fy = self.FACE_DELTA.get(w.facing, (0, 0))
            # quadrant of the target relative to where we're looking
            ctx["rel_dir"] = 0 if (dx, dy) == (fx, fy) else (
                2 if (dx, dy) == (-fx, -fy) else 1)
        c = ag.cur or {}
        ctx.update(level=c.get("level", ""), might=c.get("might", ""),
                   grace=c.get("grace", ""), will=c.get("will", ""),
                   dam=ag.dam if ag.dam is not None else "",
                   hit_stat=ag.hit if ag.hit is not None else "",
                   ac=ag.ac if ag.ac is not None else "",
                   weapon=ag.weapon, gear=ag.gear_sig)
        with ag.lock:
            ag.swing_ctx = ctx

    def attack_burst(self, n=3):
        """Swing n times in the current facing; return how many hits landed. Hits are
        confirmed from recv (agent.hits grows), so we poll briefly for the async echo."""
        with self.ag.lock:
            before = len(self.ag.hits)
        for _ in range(n):
            self._stamp_swing()          # label this attempt BEFORE it goes out
            self.ctrl.tap("space")
            time.sleep(self.mv.pace)
        deadline = time.time() + 0.5
        while time.time() < deadline:
            with self.ag.lock:
                landed = len(self.ag.hits) - before
            if landed > 0:
                break
            time.sleep(0.04)
        return landed

    def dir_to(self, me, mob):
        dx, dy = mob[0] - me[0], mob[1] - me[1]
        if abs(dx) >= abs(dy):
            return "right" if dx > 0 else "left"
        return "down" if dy > 0 else "up"

    def _huntable(self, eid, e):
        """Is this thing on the kill list? True = yes, False = banned, None = NOT YET KNOWN.

        Callers must treat None as "do not attack". That asymmetry is the whole point: on a map
        where one species kills us instantly, the cost of attacking an unknown is a death, while
        the cost of waiting is a second. So unknown is refused, not assumed safe.
        """
        look = e.get("look") if e else None
        # LOOK GATE FIRST — it is the RELIABLE field. Look comes from the binary 0x07 spawn
        # packet; the NAME comes from a per-tile right-click reply that carries no entity id and
        # arrives asynchronously, so it lands on the wrong mob when two are close (proven: 19
        # kills logged "Rat" at look 90, which is Mouse's sprite — Rat is look 91). Never gate
        # safety on the unreliable field: an unknown look is refused outright, so a mob we have
        # not identified can never be attacked no matter what name got attached to it.
        if self.hunt is not None:
            if look is None:
                return None                   # not yet identified -> do not attack
            return True if look in self.hunt else False
        if not self.hunt_names:
            return True                       # no whitelist -> legacy behaviour
        nm = self.ag.mob_names.get(eid)
        if nm:
            ok = nm.strip().lower() in self.hunt_names
            if look is not None:              # remember the species, not just this individual
                (self.look_ok if ok else self.look_bad).add(look)
            return ok
        if look is not None:
            if look in self.look_bad:         # banned wins ties -- safety over throughput
                return False
            if look in self.look_ok:
                return True
        return None

    def flee_banned(self, me):
        """Step directly away from any BANNED mob that has got close. Returns True if it took
        the tick. Not-attacking is necessary but not sufficient: a lethal mob that walks onto us
        still kills us while we stand there politely ignoring it."""
        if not (self.hunt_names or self.hunt is not None) or me is None:
            return False
        near = []
        for eid, e in self.w.fresh_entities(within_ms=4000):
            if self._huntable(eid, e) is False:
                d = abs(e["x"] - me[0]) + abs(e["y"] - me[1])
                if d <= 2:
                    near.append((d, e))
        if not near:
            return False
        near.sort(key=lambda t: t[0])
        e = near[0][1]
        dx, dy = me[0] - e["x"], me[1] - e["y"]
        order = []
        if abs(dx) >= abs(dy):
            order = [("right" if dx > 0 else "left"), ("down" if dy > 0 else "up")]
        else:
            order = [("down" if dy > 0 else "up"), ("right" if dx > 0 else "left")]
        for d in order + ["up", "down", "left", "right"]:
            if self.nav_step(d):
                self.state = "flee"
                if time.time() - self.flee_ts > 3:
                    self._dlog(f"FLEE banned mob at ({e['x']},{e['y']}) -> stepping {d}")
                    self.flee_ts = time.time()
                return True
        return False

    def choose_target(self, me):
        """Prefer the currently locked target if it's still fresh and not blacklisted --
        so we commit to killing ONE mob instead of scattering hits. Otherwise pick the
        nearest valid mob and lock it."""
        now = self._now()
        fresh_all = dict(self.w.fresh_entities(within_ms=10000))
        # TOP PRIORITY: something ADJACENT is hitting us. It's in range right now and it will
        # keep hitting us while we walk away, so kill it first -- even if we have a wounded
        # target elsewhere (a bat chasing us used to be ignored in favour of the old target).
        if me:
            for a, ats in sorted(self.attackers.items(), key=lambda kv: -kv[1]):
                if time.time() - ats > self.ATTACKER_TTL:
                    self.attackers.pop(a, None)
                    continue
                ea = fresh_all.get(a)
                if ea and abs(ea["x"] - me[0]) + abs(ea["y"] - me[1]) <= 1:
                    # RETALIATION IS NOT EXEMPT FROM THE WHITELIST. Fighting back against
                    # whatever hit us is right on a map of comparable mobs and fatal on one
                    # that mixes in something far out of our league -- it hits us once and we
                    # obligingly turn and engage it. Banned/unknown attackers are fled, not
                    # fought (see flee_banned).
                    if self._huntable(a, ea) is not True:
                        continue
                    if self.target_eid != a:
                        self._dlog(f"attacker {a} adjacent -> switch target (was {self.target_eid})")
                    self.target_eid = a
                    return (1, a, ea)
        if self.target_eid is not None:
            # keep the locked target committed even if it briefly stops moving (a stationary
            # mob emits no walk packet); only drop it when gone/blacklisted -> less thrashing
            fresh = fresh_all
            e = fresh.get(self.target_eid)
            # Re-check the lock against the whitelist every tick: a target locked while its
            # name was still unknown can turn out to be banned once the reply lands, and
            # "already committed" must not be a way around the gate.
            if e is not None and self._huntable(self.target_eid, e) is not True:
                self._dlog(f"locked target {self.target_eid} not on the kill list -> dropping")
                self.hits_on.pop(self.target_eid, None)
                self.target_eid = None
                e = None            # fall through to a fresh pick_target below
            # A WOUNDED mob is never traded for a closer one -- finish what we started.
            if e and self.hits_on.get(self.target_eid, 0) > 0:
                d = (abs(e["x"] - me[0]) + abs(e["y"] - me[1])) if me else 0
                return (d, self.target_eid, e)
            # Not yet wounded, but something is HITTING US -> switch to the attacker.
            live_att = [a for a, ts in self.attackers.items()
                        if time.time() - ts <= self.ATTACKER_TTL]
            if live_att and self.target_eid not in live_att:
                self.target_eid = None
            if e and self.blacklist.get(self.target_eid, 0) <= now:
                look = e.get("look")
                if look is None or (0 < look <= self.MOB_LOOK_MAX):
                    d = (abs(e["x"] - me[0]) + abs(e["y"] - me[1])) if me else 0
                    return (d, self.target_eid, e)
            self.target_eid = None            # dead / gone / blacklisted -> drop the lock
        t = self.pick_target(me)
        self.target_eid = t[1] if t else None
        return t

    def pick_target(self, me):
        now = self._now()
        best = None                           # (score, dist, eid, e)
        ents = self.w.fresh_entities(within_ms=5000)   # present mobs only
        for eid, e in ents:
            if self.blacklist.get(eid, 0) > now:
                continue
            # sanity: never chase a bogus entity (id/coords out of the plausible range)
            if eid <= 1000 or not (0 < e.get("x", 0) < 1000 and 0 < e.get("y", 0) < 1000):
                continue
            look = e.get("look")
            # Only EXCLUDE entities we KNOW are players/appearances (look > 500). Unknown
            # looks (None) are kept -- "keep what bleeds" validates them.
            if look is not None and (look <= 0 or look > self.MOB_LOOK_MAX):
                continue
            if self.hunt is not None and look not in self.hunt:
                continue
            if self._huntable(eid, e) is not True:   # banned, or name not resolved YET
                continue
            d = (abs(e["x"] - me[0]) + abs(e["y"] - me[1])) if me else 20
            # prefer NEAR *and* FRESH: a mob that emitted a walk packet a moment ago is
            # definitely there; a stale one is likely a ghost that already wandered off. Add
            # its seconds-since-seen to the distance so fresh mobs win close calls.
            recency = (now - e["ts"]) / 1000.0
            score = d + recency
            # ATTACKERS FIRST: something actively hitting us outranks any nearer/fresher mob.
            att = self.attackers.get(eid)
            if att is not None:
                if time.time() - att <= self.ATTACKER_TTL:
                    score -= 1000
                else:
                    self.attackers.pop(eid, None)
            if best is None or score < best[0]:
                best = (score, d, eid, e)
        return (best[1], best[2], best[3]) if best else None

    # ---------- behaviors ----------
    def _sweep(self, legs):
        """Cover ground: take up to `legs` steps, and for each step TRY ALL 4 directions
        until one is open (so a wall/corner can't trap us spinning). Swing after each step
        so we catch any mob we pass -- pre-anchor this is how we land the first hit."""
        dirs = ["up", "right", "down", "left"]
        moved = 0
        for _ in range(legs):
            stepped = False
            for k in range(4):
                name = dirs[(self.explore_dir + k) % 4]
                if self.nav_step(name):                    # fast, memory-confirmed, learns walls
                    self.explore_dir = (self.explore_dir + k) % 4
                    moved += 1
                    stepped = True
                    # Blind swings are how we USED to catch mobs we walked past -- but a blind
                    # swing can land on anything adjacent, including the thing we must never
                    # touch. With a name whitelist active, only ever swing at a vetted target.
                    if not self.hunt_names:
                        self.attack_burst(1)
                    break
            if not stepped:
                self._dlog("sweep: no open direction here")
                break
        self.explore_dir = (self.explore_dir + 1) % 4      # change heading to cover 2D
        self._dlog(f"sweep moved={moved} facing={self.w.facing}")
        return moved

    def bootstrap(self):
        """No absolute anchor yet: swing each direction in place to catch an adjacent mob
        (lands a hit -> anchor). If nothing adjacent, sweep to a new spot and retry."""
        self.state = "bootstrap"
        for name in ("up", "right", "down", "left"):
            self.mv.face(name)
            landed = self.attack_burst(1)
            self._dlog(f"bootstrap face={name} landed={landed}")
            if landed:
                return                           # hit -> 0x13 handler set the anchor
            with self.w.lock:
                if self.w.offset is not None:
                    return
        self._sweep(legs=5)                      # nothing adjacent -> reposition and retry

    def frontiers(self, grid, me, limit=4000):
        """Tiles we KNOW are walkable that touch an UNKNOWN tile -- the boundary of explored
        space. Pathing to these is how you systematically leave a pit: stairs/exits are, by
        definition, in the part of the map you haven't seen yet. Random 4-direction probing
        (the old escape) just rattles around inside the area you already know.

        Returns (tile, gain) with gain = how many of its 4 neighbours are unknown, so the caller
        can prefer frontiers that open up REAL new ground over ones that reveal a single tile.
        The old version truncated at 400 mid-iteration, which (dict order being insertion order)
        silently dropped the far frontiers -- the ones actually worth walking to."""
        out = []
        for (x, y), v in grid.items():
            if v != 1:
                continue
            gain = 0
            for dx, dy in ((0, -1), (1, 0), (0, 1), (-1, 0)):
                if (x + dx, y + dy) not in grid:
                    gain += 1
            if gain:
                out.append(((x, y), gain))
            if len(out) >= limit:
                break
        return out

    def travel_step(self, me):
        """Advance ONE step along the current travel path (re-planning when it breaks). Keeps
        long-distance movement inside the reactive one-action-per-tick model."""
        if not self.travel_path:
            return False
        nxt = self.travel_path[0]
        d = self._dir_between(me, nxt)
        if d is None:                                   # we drifted off-path -> replan next tick
            self.travel_path = []
            return False
        if self.nav_step(d):
            if self.travel_path and self.travel_path[0] == nxt:
                self.travel_path.pop(0)
            return True
        self.travel_path = []                           # blocked (wall learned) -> replan
        return False

    def explore(self):
        """FRONTIER EXPLORATION -- replaces the random sweep/escape. Pick the nearest frontier
        tile we can actually reach, path to it, and walk it one step per tick. As we walk, the
        grid grows, new frontiers appear beyond, and we naturally push out of a pit and find
        the stairs. Falls back to a blind sweep only if the map has no frontier at all."""
        self.state = "explore"
        me = self.me_now or self.w.abs_pos()
        if me is None:
            self.bootstrap()
            return
        if self.travel_path and self.travel_step(me):
            return                                      # continue the current plan
        if self.return_home(me):                        # warped out of the hunt room -> go back
            return
        grid = self.w.grid_copy()
        cands = self.frontiers(grid, me)
        # skip frontiers we've already tried and failed to reach recently (anti-thrash)
        now = time.time()
        self._bad_frontier = {t: ts for t, ts in self._bad_frontier.items() if now - ts < 45}
        # RANK: go FAR and go where there is most to see. Sorting by nearest-first (the old
        # behaviour) makes the bot nibble the boundary one tile from its feet -- the decision log
        # showed it picking 2-step frontiers over and over with only 16 tiles known, so it never
        # left the corner it spawned in and never found new mobs. Distance is a POSITIVE term
        # here, capped so we don't commit to an absurd trek across the whole map.
        scored = []
        for t, gain in cands:
            if t in self._bad_frontier:
                continue
            d = abs(t[0] - me[0]) + abs(t[1] - me[1])
            if d <= 1:
                self._bad_frontier[t] = now             # standing on it and still unknown beyond
                continue
            scored.append((gain * 4 + min(d, self.EXPLORE_REACH), d, t))
        scored.sort(reverse=True)
        # A* is not free -- try only the best handful per tick, the rest get another chance next
        # tick once the grid has grown.
        for _, d, t in scored[:12]:
            path = astar(grid, me, t)
            if path:
                self.travel_path = path
                self._dlog(f"explore -> frontier {t} ({len(path)} steps, dist {d}, "
                           f"{len(cands)} frontiers)")
                self.travel_step(me)
                return
            self._bad_frontier[t] = now
        self._dlog(f"no reachable frontier ({len(cands)} candidates) -> blind sweep")
        self._sweep(legs=self.EXPLORE_SWEEP)

    def return_home(self, me):
        """If a warp dropped us out of the room we were hunting in, walk back.

        Uses the observed warp graph: the tile that took us OUT of this room toward home
        is fact once we've made the crossing; before that we fall back to the tile we
        arrived on (warps are usually co-located, but it's a guess -- logged as one).
        Returns True if we're actively travelling home.
        """
        home = self.home_room
        room = self.w.room
        if not home or not room or room == home:
            return False
        tile, certain = self.w.exit_tile(room, home)
        if tile is None:
            return False                     # unknown link -- normal exploration will find it
        if me == tile:
            # standing on it without warping: either the guess was wrong or the trigger
            # needs a fresh entry. Step off and let the next pass walk back onto it.
            self._dlog(f"on warp tile {tile} but still in {room!r} -- stepping off to re-enter")
            self._sweep(legs=1)
            return True
        path = astar(self.w.grid_copy(), me, tile)
        if not path:
            return False                     # can't reach it yet -> keep exploring
        self.travel_path = path
        self._dlog(f"returning to {home!r} via {'observed' if certain else 'assumed'} warp "
                   f"tile {tile} ({len(path)} steps, currently in {room!r})")
        return self.travel_step(me)

    def wander(self):
        """Anchored but nothing huntable nearby: explore toward unknown space to find mobs."""
        self.explore()

    def _stuck(self, me):
        """True if our EXACT position has barely moved for STUCK_SEC despite trying -- i.e.
        we're penned or wall-bumping. Uses memory ground truth, so it can't be fooled by
        dead-reckoning drift the way a rel-based check could."""
        if me is None:
            return False
        now = self._now()
        self.pos_hist.append((now, me))
        while self.pos_hist and now - self.pos_hist[0][0] > self.STUCK_SEC * 1000:
            self.pos_hist.popleft()
        # ticks are slow (each can spend seconds inside approach), so a handful of samples
        # spanning STUCK_SEC is enough -- don't demand a high sample count
        if len(self.pos_hist) < 3 or now - self.pos_hist[0][0] < self.STUCK_SEC * 1000:
            return False
        xs = [p[0] for _, p in self.pos_hist]
        ys = [p[1] for _, p in self.pos_hist]
        return (max(xs) - min(xs)) <= 1 and (max(ys) - min(ys)) <= 1

    def escape(self):
        """Stuck/penned: get OUT properly instead of rattling around. Old behaviour probed 4
        headings at random, which can never find stairs in a pit -- it only re-walks space it
        already knows. Now we (1) forget learned walls, since a false wall is the usual reason
        we think we're penned, and (2) hand over to frontier exploration, which pushes into
        UNKNOWN space -- where the exit necessarily is."""
        self.state = "escape"
        start = self.me_now or self.w.abs_pos()
        with self.w.lock:                          # drop phantom walls; real ones re-learn fast
            walls = [t for t, v in self.w.grid.items() if v == 0]
            for t in walls:
                del self.w.grid[t]
            self.w.wall_ts.clear()
        self.blacklist.clear()                     # give nearby mobs a fresh chance too
        self.travel_path = []
        self._bad_frontier.clear()
        self.pos_hist.clear()
        self._dlog(f"escape from {start}: cleared {len(walls)} learned walls -> frontier explore")
        self.explore()

    def handle_death(self):
        """curhp==0: the character is a ghost -- swinging/moving accomplishes nothing until it
        revives. Stop flailing, surface it loudly (rate-limited), and idle. Revival on the live
        client isn't a known keypress here, so we don't guess -- we hold and wait for HP>0
        (manual or auto revive), then resume grinding cleanly."""
        self.state = "DEAD"
        now = time.time()
        if now - getattr(self, "_last_death_log", 0) > 5:
            self._last_death_log = now
            self._dlog("*** CHARACTER IS DEAD (curhp=0) -- idling until revived (HP>0) ***")
            log_bot("*** CHARACTER IS DEAD (curhp=0) -- bot idling until revived ***")
        # drop all combat state so we start fresh on revive
        self.target_eid = None
        self.blacklist.clear()
        self._pending_step = None
        time.sleep(0.5)

    def recover(self):
        """Low HP: back away from the nearest mob and idle to regen."""
        self.state = "recover"
        me = self.me_now or self.w.abs_pos()
        hp, maxhp = self.vitals()
        self._dlog(f"recover hp={hp}/{maxhp} at {me}")
        # Heal FIRST -- that's what actually fixes low HP; retreating alone just loses tempo.
        self.heal_burst(3)
        tgt = self.pick_target(me)
        if me and tgt:
            _, _, e = tgt
            away = {"right": "left", "left": "right", "up": "down", "down": "up"}[
                self.dir_to(me, (e["x"], e["y"]))]
            self.nav_step(away)        # ONE fast step per tick, not a blocking 3-step burst

    def strike(self, eid):
        """Adjacent to a mob: re-read its live tile, re-face, swing -- but STOP the moment we
        detect a GHOST. Mobs emit a walk packet only when they MOVE, so a mob that wandered
        off (or died) lingers at its last tile in our freshness window; if we're standing ON
        that tile (dd==0) it is provably not there -> drop it. A couple of no-bleed swings on
        an 'adjacent' tile means the same, so we bail fast instead of flailing at a corpse."""
        self.state = "attack"
        total = 0
        last_face = None
        dry = 0            # consecutive adjacent swings that landed nothing -> re-face
        misses = 0
        chase = 0                              # consecutive follow-steps without getting adjacent
        deadline = time.time() + 3.0           # hard cap: never spin here longer than 3s
        # SERVER SETTLE: our client-local walk outruns the server (which resolves the hit), so
        # give the server a moment to register that we've arrived adjacent before we swing.
        time.sleep(0.30)
        logged = False
        for _ in range(self.STRIKE_SWINGS):   # RELENTLESS: keep swinging, don't quit on misses
            if time.time() > deadline:
                break                          # time-box the whole engagement (anti-hang)
            me = self.w.read_self_now() or self.w.abs_pos()
            mob = self.mob_pos(eid)
            if me is None or mob is None:
                break                          # mob gone (killed / out of range) -> done
            if not logged:                     # diagnostic: self(mem) vs mob(wire) vs mob(mem)
                memp = self.ments.mem_pos.get(eid) if self.ments else None
                self._dlog(f"STRIKE eid={eid} me-mem={me} mob-wire={mob} mob-mem={memp} dd={abs(mob[0]-me[0])+abs(mob[1]-me[1])}")
                logged = True
            dd = abs(mob[0] - me[0]) + abs(mob[1] - me[1])
            if dd == 0:                        # standing on its tile -> ghost/corpse, drop it
                self.w.remove_entity(eid)
                self._dlog(f"ghost {eid} @ {mob} -> removed")
                break
            if dd >= 2:                        # drifted away -> chase a couple tiles, then bail
                chase += 1
                if dd > 3 or chase > 2:        # broke away / can't close -> hand back to approach A*
                    break
                self._greedy_step(me, mob)     # follow one tile toward it
                continue
            chase = 0                          # adjacent again
            # adjacent: face only if the direction changed, then SWING AS FAST AS POSSIBLE
            d = self.dir_to(me, mob)
            # Re-face when the direction changes OR when swings stop landing. Facing only
            # on a CHANGE was the bug behind "turned around and never adjusts": if a turn
            # tap is swallowed (or something else spins us) while the mob stays in the same
            # direction, `d` never changes, so we never re-face and swing at air forever.
            # A run of misses while adjacent is the observable symptom, so use it as the
            # trigger -- face_toward is self-correcting, so re-facing is always safe.
            if d != last_face or dry >= 2:
                if dry >= 2:
                    self._dlog(f"{dry} misses while adjacent -> re-facing {d}")
                self._fast_face(d)
                last_face = d
                dry = 0
            with self.ag.lock:
                h0 = len(self.ag.hits)
            self._stamp_swing()                # label the attempt BEFORE it goes out
            self.ctrl.tap("space", hold=0.03)  # short hold -> rapid repeated swings
            time.sleep(self.SWING_GAP)         # ~ the client swing cooldown, no more
            with self.ag.lock:
                landed = len(self.ag.hits) - h0
            dry = 0 if landed else dry + 1     # feeds the re-face trigger above
            total += landed
            if landed:
                ee = dict(self.w.fresh_entities()).get(eid)
                lk = ee.get("look") if ee else None
                if lk is not None:
                    self.damaged_looks[lk] += landed
                self.air_streak = 0
                misses = 0
            else:
                misses += 1
        if total:
            self._dlog(f"struck {eid}: {total} hits landed")
        return total

    # ---------- spell casting + survival ----------
    def cast_spell(self, letter, tries=4, arg=None):
        """Cast a spell. CRITICAL: simple spells (Soothe 'a', Might 'c', Feral 'd') are cast by
        Shift+Z -> letter with NO Enter -- the letter fires the spell immediately, and a stray
        Enter afterwards just OPENS THE CHAT BOX (which then eats all our movement keys and
        looks like being walled-in). Only INPUT spells (Gateway 'b') take a follow-up value:
        Shift+Z -> b -> <dir> -> Enter. Verify via the outgoing 0x0f; retry with Esc recovery."""
        for attempt in range(tries):
            self._spell_throttle()                   # respect the 3-spells-per-second cap
            self._spell_ts.append(time.time())
            self.ctrl.close_chat(1)                  # clean state (close any stray chat/prompt)
            seq0 = self.w.sent_seq()
            self.ctrl.press_char("Z", 0.10)          # Shift+Z opens the cast prompt
            time.sleep(0.24)
            self.ctrl.press_char(letter, 0.10)       # the letter itself casts a simple spell
            time.sleep(0.24)
            if arg is not None:                       # input spell (Gateway): type dir + Enter
                self.ctrl.press_char(arg, 0.10)
                time.sleep(0.18)
                self.ctrl.press_char("\r", 0.10)
                time.sleep(0.35)
            if self.w.sent_after(seq0, (0x0f,)):
                self._dlog(f"cast '{letter}'{('/'+str(arg)) if arg else ''} OK (attempt {attempt+1})")
                return True
            self.ctrl.close_chat(1)                   # recover: close whatever opened
            time.sleep(0.14)
        self._dlog(f"cast '{letter}' FAILED after {tries} tries")
        return False

    def note_attackers(self, dmg):
        """We just took damage -> whatever is ADJACENT is hitting us. Mark those mobs as
        attackers so targeting prioritises them: fighting back beats being farmed for free
        while we chase something else across the map."""
        me = self.me_now or self.w.read_self_now()
        if me is None:
            return
        now = time.time()
        hit_us = []
        for eid, e in self.w.fresh_entities(within_ms=3000):
            if abs(e["x"] - me[0]) + abs(e["y"] - me[1]) <= 1:
                if self.no_melee.get(eid, 0) > self._now():
                    continue                      # on our tile: cannot be meleed, and
                                                  # re-targeting it is the livelock
                self.attackers[eid] = now
                self.blacklist.pop(eid, None)     # never ignore something that is hitting us
                hit_us.append(eid)
        if hit_us:
            self._dlog(f"TOOK {dmg} dmg -> attackers {hit_us} (priority targets)")

    def _room_upkeep(self):
        """One cheap read of the room-name buffer per tick; a CHANGE means we warped.

        Poll rate is throttled because a re-locate (when the address goes stale) costs a
        heap harvest -- but the common case is a single UTF-16 read, so this is ~free.
        """
        ex = self.w.mem_ex
        if ex is None:
            return
        now = time.time()
        if now - getattr(self, "_room_ts", 0) < 0.5:
            return
        self._room_ts = now
        room = self.room_tracker.poll(ex)
        if room and room != self.w.room:
            pos = self.me_now or self.w.read_self_now()
            self.w.enter_room(room, pos, log=self._dlog)
            if self.home_room is None:
                # the room we were grinding in when we first identified ourselves; if a warp
                # tile later drags us out, return_home() walks us back to it
                self.home_room = room
                mid, mw, mh = self.room_tracker.meta(room)
                self._dlog(f"home room set to {room!r} (map {mid}, {mw}x{mh})")

    def _name_upkeep(self):
        """Keep every logged swing self-describing: resolve the in-flight name reply, ask for
        the current target's name once, and keep the worn-gear signature current.

        The 0x0a reply is per-TILE and carries no eid, so we attribute it to the eid we asked
        about -- and only when that mob is ALONE on its tile, otherwise loot or a second mob
        sharing the tile could supply the name."""
        w, ag = self.w, self.ag
        # gear signature: which loadout was worn for these hits (stat swaps are the whole
        # point of the experiment, so this must be on the row, not joined on later)
        with w.lock:
            eq = w.equipment
        if eq is None:
            # The startup fetch is time-boxed and sometimes lands before the client's first
            # send (which is what supplies the send fn's connection object), so it silently
            # no-ops. Keep asking until the profile actually arrives -- once it does, every
            # subsequent row carries the real loadout and weapon.
            now2 = time.time()
            if now2 - getattr(self, "_profile_ts", 0) > 10.0:
                self._profile_ts = now2
                try:
                    w.mem_ex.sendraw([0x2d, 0x00, 0x00])
                except Exception:
                    pass
        if eq:
            ag.gear_sig = "|".join(eq[2])        # real item names, when the profile arrived
            # slot 0 is the weapon; it joins to auto/item_stats.csv for Damage S/L, which
            # is the only combat input the character stat vector cannot express
            ag.weapon = eq[2][0] if eq[2] else ""
        # NB: do NOT synthesise a "gear signature" from the equipped stat vector. It looks
        # like it would work, but BUFF SPELLS move the same fields as gear (`Might` = +3
        # might), so the same loadout fingerprints differently with a buff up and identical
        # loadouts split into bogus groups -- destroying the grouping it was meant to give.
        # Leave `gear` empty unless the 0x39 profile actually named the worn items.
        # resolve a pending reply
        if self._name_wait is not None:
            eid, ts = self._name_wait
            with w.lock:
                ln = w.last_name
            if ln and ln[1] >= ts:
                ag.mob_names[eid] = ln[0]
                self._dlog(f"NAMED {eid} = {ln[0]!r}")
                self._name_wait = None
            elif time.time() - ts > 2.0:
                self._name_wait = None            # no reply (out of view / throttled)
        # ask for the locked target's name once
        now = time.time()
        cand = self.target_eid
        if self.hunt_names:
            # With a name whitelist we never LOCK an unvetted mob, so waiting on target_eid
            # would resolve nothing and the bot would sit idle forever. Probe the NEAREST
            # unvetted mob instead -- naming is what promotes an unknown into a target (or
            # into a thing to stay away from), so it has to run BEFORE target selection, not
            # after it. Costs one right-click per species: the look -> verdict cache in
            # _huntable means every later individual is judged instantly.
            mepos = self.me_now or w.read_self_now()
            unvetted = []
            if mepos:
                for eid, ee in w.fresh_entities(within_ms=4000):
                    if self._huntable(eid, ee) is None:
                        unvetted.append((abs(ee["x"] - mepos[0]) + abs(ee["y"] - mepos[1]), eid))
            cand = min(unvetted)[1] if unvetted else None
        if (self._name_wait is None and cand is not None
                and cand not in ag.mob_names and now - self._name_ts > 0.8):
            ms = w.mob_state(cand)
            if ms:
                tile = (ms[0], ms[1])
                others = [e for e, ee in w.fresh_entities(within_ms=4000)
                          if e != cand and (ee["x"], ee["y"]) == tile]
                if not others:                    # unambiguous tile only
                    try:
                        if w.mem_ex.asktile(tile[0], tile[1]):
                            self._name_wait = (cand, time.time())
                            self._name_ts = now
                    except Exception:
                        pass

    def _spell_throttle(self):
        """Block until casting again would stay within SPELL_CAP casts per rolling second."""
        now = time.time()
        while self._spell_ts and now - self._spell_ts[0] > 1.0:
            self._spell_ts.popleft()
        if len(self._spell_ts) >= self.SPELL_CAP:
            wait = 1.0 - (time.time() - self._spell_ts[0])
            if wait > 0:
                time.sleep(wait)
            while self._spell_ts and time.time() - self._spell_ts[0] > 1.0:
                self._spell_ts.popleft()

    def cast_fast(self, letter):
        """FAST simple-spell cast: Shift+Z then the letter, NO Enter and no verification
        round-trip. A simple spell fires the instant the letter lands, so the old ~0.7s of
        internal sleeps (plus a 1.8s cooldown) just meant healing lost the race with incoming
        damage. Rate-limited to the 3-spells-per-second cap."""
        self._spell_throttle()
        self.ctrl.press_char("Z", 0.02)
        time.sleep(0.05)
        self.ctrl.press_char(letter, 0.02)
        self._spell_ts.append(time.time())

    def heal_burst(self, n=3):
        """Chain up to n Soothes at the cap: cast, ~300ms pause, cast, pause, cast. Stops
        early once HP is comfortable so we don't waste mana or time."""
        cast = 0
        for i in range(n):
            self.cast_fast("a")
            cast += 1
            time.sleep(self.SPELL_GAP)
            v = self.w.read_vitals()
            if v and v[1] and v[0] / v[1] > 0.80:      # healthy again -> stop early
                break
        self.last_soothe = self._now()
        return cast

    def top_off(self, max_casts=8):
        """Post-kill: chain Soothes back to ~full in the lull between fights. Cheap insurance --
        entering the next fight at full HP beats healing under pressure. Stops at TOPOFF_FRAC,
        when mana hits the reserve floor, or when a cast stops helping."""
        cast = 0
        for _ in range(max_casts):
            v = self.w.read_vitals()
            if not v or not v[1]:
                break
            hp, maxhp, mana, maxmana = v[0], v[1], v[2], v[3]
            if hp / maxhp >= self.TOPOFF_FRAC:
                break
            if maxmana and mana / maxmana <= self.MANA_FLOOR:
                self._dlog(f"top-off stopped: mana {mana}/{maxmana} at reserve floor")
                break
            self.cast_fast("a")
            cast += 1
            time.sleep(self.SPELL_GAP)
        if cast:
            v = self.w.read_vitals()
            self._dlog(f"top-off x{cast} -> hp={v[0] if v else '?'}/{v[1] if v else '?'}")
        self.last_soothe = self._now()
        return cast

    def survival(self):
        """Called first each tick. Watch HP, predict time-to-death from the recent loss
        rate, and act: emergency Gateway escape if death is imminent, Soothe to heal when
        moderately low, and keep the Might buff up. Returns True if it took over this tick."""
        now = self._now()
        # Might buff refresh runs FIRST, without needing stats -- besides buffing, the cast
        # forces a 0x08 statblock, which gives the agent the CURRENT maxmana that StatsMem
        # anchors its find on. This is what bootstraps live stats at startup.
        if now - self.last_might > self.MIGHT_CD * 1000:
            self.last_might = now
            self._dlog("refresh Might buff (also bootstraps statblock)")
            self.cast_spell("c")
        hp, maxhp = self.vitals()
        if hp is None or not maxhp:
            return False
        self.hp_hist.append((now, hp))
        while self.hp_hist and now - self.hp_hist[0][0] > 4000:
            self.hp_hist.popleft()
        # predicted seconds-to-death from the HP-loss rate over the window
        ttd = None
        if len(self.hp_hist) >= 2:
            dt = (self.hp_hist[-1][0] - self.hp_hist[0][0]) / 1000.0
            dhp = self.hp_hist[0][1] - hp                 # >0 means losing HP
            if dt > 0.3 and dhp > 0:
                ttd = hp / (dhp / dt)
        frac = hp / maxhp

        # EMERGENCY: about to die -> Gateway out, then make sure the attacker is gone
        if frac <= self.GATEWAY_FRAC or (ttd is not None and ttd <= self.GATEWAY_TTD):
            self.emergency_escape(hp, ttd)
            return True
        # POST-KILL TOP-OFF: just killed something and we're not full -> heal up NOW, while
        # nothing is hitting us, rather than starting the next fight already damaged.
        if self._need_topoff:
            self._need_topoff = False
            if frac < self.TOPOFF_FRAC:
                self.state = "topoff"
                self.top_off()
                return True
        # Out of mana -> casting is a no-op; don't burn ticks pretending to heal.
        vv = self.w.read_vitals()
        if vv and vv[3] and vv[2] / vv[3] < 0.05:
            return False
        # HEAL: moderately hurt -> chain Soothes fast (cast/300ms/cast/300ms/cast). The deeper
        # the hole, the more casts we chain; a single slow cast used to lose to incoming damage.
        if frac <= self.SOOTHE_FRAC and now - self.last_soothe > self.SOOTHE_CD * 1000:
            self.state = "heal"
            n = 3 if frac <= 0.40 else 2 if frac <= 0.50 else 1
            cast = self.heal_burst(n)
            v = self.w.read_vitals()
            self._dlog(f"Soothe x{cast} hp={hp}/{maxhp} ({frac:.0%}) ttd={ttd} -> "
                       f"hp={v[0] if v else '?'}")
            return True
        return False

    def _gateway_dir(self):
        """Pick the Gateway direction letter (n/s/e/w) that heads AWAY from the mob cluster."""
        me = self.w.read_self_now() or self.w.abs_pos()
        ents = self.w.fresh_entities()
        if me and ents:
            cx = sum(e["x"] for _, e in ents) / len(ents)
            cy = sum(e["y"] for _, e in ents) / len(ents)
            dx, dy = me[0] - cx, me[1] - cy
            if abs(dx) >= abs(dy):
                return "e" if dx >= 0 else "w"
            return "s" if dy >= 0 else "n"
        return "n"

    def emergency_escape(self, hp, ttd):
        """Imminent death: Gateway away, then confirm the attacker is no longer adjacent.
        Gateway teleports us out (map reload clears entities), so success = we're no longer
        being hit. Soothe once after landing to stabilise."""
        self.state = "ESCAPE"
        attackers = [eid for eid, _ in self.w.fresh_entities()]
        # Gateway is an INPUT spell: Shift+Z -> b -> <direction letter> -> Enter. Pick the
        # direction AWAY from the mob cluster so we teleport clear of the attackers.
        gdir = self._gateway_dir()
        self._dlog(f"EMERGENCY hp={hp} ttd={ttd} -> GATEWAY {gdir} (attackers={len(attackers)})")
        if not self.cast_spell("b", arg=gdir):   # Gateway <dir>
            self.cast_spell("a")                 # couldn't gateway -> at least Soothe
            return
        time.sleep(1.2)                       # let the teleport + map reload land
        self.w.sync_mem()
        # after a Gateway the server sends a fresh self-loc + new entity set; if HP has
        # stopped dropping and the old attackers aren't around, we're safe
        hp2, _ = self.vitals()
        still = [e for e, _ in self.w.fresh_entities() if e in attackers]
        self._dlog(f"post-gateway hp={hp2} old-attackers-still-here={len(still)}")
        self.target_eid = None
        self.hp_hist.clear()
        if hp2 is not None and hp2 < (self.ag.cur["maxhp"] * 0.6 if self.ag.cur else 500):
            self.cast_spell("a")              # top up after escaping

    def mob_pos(self, eid):
        """Current live tile of a mob (from the wire's 0x07/0x0c), or None if gone."""
        fresh = dict(self.w.fresh_entities())
        e = fresh.get(eid)
        return (e["x"], e["y"]) if e else None

    def _entity_on(self, tile):
        """The eid of a fresh mob/player standing on `tile`, or None. A blocked step onto an
        occupied tile is a SOFT block (it clears when they move) -- not a wall."""
        for eid, e in self.w.fresh_entities(within_ms=4000):
            if (e.get("x"), e.get("y")) == tile:
                return eid
        return None

    def pickup(self):
        """Press the item-pickup key on the tile we're standing on.

        The game exposes TWO pickup keys -- ',' and '<' (Shift+',') -- named by the Invisible
        spell's own description. We fire ',' FIRST because it is unshifted: shifted keys are
        unreliable through the same-process PostMessage path (the client's GetKeyState(VK_SHIFT)
        reads "up", which is why `unequip` had to fall back to focus-stealing SendInput), while
        an unshifted tap goes straight through in the background. '<' follows as a cheap second
        chance in case the two keys are bound to different behaviours (e.g. item vs coins).
        """
        # An open chat box would eat the keypress as literal text (the same failure that makes
        # movement look "walled in"), so clear it first -- cheap here, since looting only ever
        # runs in the lull between fights.
        self.ctrl.close_chat(1)
        self.ctrl.press_char(",", 0.06)
        time.sleep(0.12)
        self.ctrl.press_char("<", 0.06)
        time.sleep(0.12)

    def loot_kill(self, tile, budget=3.5):
        """Walk onto a dead mob's tile and pick up whatever it dropped.

        Runs in the lull AFTER a kill, so it never competes with combat for the tick. Strictly
        time-boxed: looting is worth much less than killing, so a drop we can't reach in a
        couple of seconds is abandoned rather than allowed to stall the grind (the same reason
        chases are time-boxed, not tick-counted).

        Uses the client's ground-item table to decide what to visit, which handles the case the
        naive "walk to the corpse tile" misses: drops do not always land exactly where the mob
        died, and a mob killed while it was mid-step leaves its loot a tile off.
        """
        if not self.loot_enabled:
            return 0
        # Never walk into danger for loot. The corpse tile is wherever the fight ended, which
        # can easily be next to something on the banned list -- a drop is not worth a death.
        if self.hunt_names:
            for eid, e in self.w.fresh_entities(within_ms=4000):
                if (self._huntable(eid, e) is False
                        and abs(e["x"] - tile[0]) + abs(e["y"] - tile[1]) <= 3):
                    self._dlog(f"loot: skipping {tile} -- banned mob nearby")
                    return 0
        deadline = time.time() + budget
        got = 0
        # The corpse tile first (what actually dropped is usually right there), then any other
        # ground item the client reports nearby -- cheap, since we're already standing next to it.
        targets = [tile] + [t for _, _, t in self.w.loot_near(tile, radius=2) if t != tile]
        for t in targets[:3]:
            while time.time() < deadline:
                me = self.w.read_self_now() or self.me_now
                if me is None:
                    return got
                if (me[0], me[1]) == tuple(t):
                    break
                grid = self.w.grid_copy()
                path = astar(grid, me, tuple(t))
                d = self._dir_between(me, path[0]) if path else None
                if d is None:
                    self._greedy_step(me, tuple(t))
                else:
                    self.nav_step(d)
                time.sleep(0.06)
            else:
                self._dlog(f"loot: gave up routing to {t} (budget)")
                continue
            before = {u for _, u, _ in self.w.loot_near(t, radius=0)}
            self.pickup()
            time.sleep(0.25)
            after = {u for _, u, _ in self.w.loot_near(t, radius=0)}
            taken = len(before - after)
            got += taken
            if before and not taken:
                # The key went nowhere (swallowed, or a full pack). One retry, then move on --
                # hammering it would burn the whole budget on an item we may not be able to hold.
                self.pickup()
                time.sleep(0.25)
                got += len(before - {u for _, u, _ in self.w.loot_near(t, radius=0)})
        if got:
            self.session_loot += got
            self._dlog(f"loot: picked up {got} item(s) at {tile}")
        return got

    def nav_step(self, d):
        """FAST, memory-driven step in direction `d`. NexusTK faces-then-steps, so a tap may
        only TURN the first time; we tap, poll the memory position for a real tile change, and
        tap once more if it only turned. Returns True iff the tile actually changed. On a
        genuine no-move we LEARN the map: mark the target tile a WALL -- unless a fresh entity
        sits on it (a soft, self-clearing block). No wire round-trip => reactive, not laggy."""
        now = time.time()
        pos0 = self.me_now if self.me_now is not None else self.w.read_self_now()
        if pos0 is None:
            self.mv.step(d)                                  # no memory -> fall back to slow path
            return False
        dd = dir_of(d)
        dx, dy = DELTA[dd]
        tgt = (pos0[0] + dx, pos0[1] + dy)
        # skip a tile we ALREADY know is a wall -- don't burn a step tapping into it.
        # (walls decay after WALL_TTL, so this self-corrects if the map really changed.)
        if self.w.grid.get(tgt) == 0:
            return False
        # Don't fire while a prior step is still unconfirmed (would overwrite its record and the
        # wall would never be learned), and PACE to the walk cooldown (a press fired mid-cooldown
        # is swallowed and then misreads as a wall). One confirmed step per cooldown = max real
        # move speed with zero wasted taps.
        if self._pending_step is not None or now - self._last_step_ts < self.STEP_GAP:
            return True
        # FIRE-AND-CONFIRM-NEXT-TICK: NexusTK faces-then-steps, so a direction CHANGE needs a
        # turn tap then a step tap; going straight needs just one. We FIRE and return immediately
        # (no blocking wait, no extra memory read) -- the NEXT tick's single self-pos read
        # confirms whether we moved (see _confirm_step). This is what makes movement fast.
        # Track facing while travelling so `need_turn` is meaningful (we tap twice on a turn).
        # This is a best-effort hint ONLY -- combat never trusts it: face_toward() re-taps
        # toward an adjacent mob unconditionally, which is self-correcting regardless of drift.
        need_turn = (self.w.facing != dd)
        with self.w.lock:
            self.w.facing = dd
        self.ctrl.tap(d, hold=0.02)
        if need_turn:
            time.sleep(0.02)
            self.ctrl.tap(d, hold=0.02)                      # turn, then step
        self._last_axis = "x" if d in ("left", "right") else "y"
        self._pending_step = (tgt, pos0, d, need_turn, now)
        self._last_step_ts = now
        return True                                          # issued (optimistic; confirmed next tick)

    def _confirm_step(self, cur):
        """Resolve the previously-fired step against THIS tick's self-position (read once in
        tick()). Learn walls across ticks -- never on a single miss, and only after the walk
        cooldown has fully elapsed, so a cooldown-swallowed press can't be mistaken for a wall."""
        ps = self._pending_step
        if ps is None or cur is None:
            return
        tgt, pos0, d, need_turn, t0 = ps
        if cur != pos0:                                      # we moved (to tgt or drifted) -> open
            self.w.mark_walkable(*cur)
            with self.w.lock:
                self.w.rel = (0, 0)
                self.w.offset = cur
            self._wall_fail.pop(tgt, None)
            self._nomove_streak = 0
            self._pending_step = None
            return
        # Give the client a REAL window to apply the step before calling it a failure. 0.19s
        # was too tight: a live input test moved reliably at 0.25s spacing, so short windows
        # manufactured "walls" out of perfectly good tiles (the bot walled itself into a
        # 1-tile prison at (4,5) -- all four neighbours "blocked", which is impossible).
        if time.time() - t0 < 0.34:
            return
        self._pending_step = None
        # NB: do NOT skip learning when need_turn was set. nav_step already taps TWICE in that
        # case (turn, then step), so a no-move here is real evidence the tile is blocked. The
        # old `if need_turn: return` meant walls were almost never learned while navigating
        # (w.facing isn't tracked during travel, so need_turn was nearly always true) -- the bot
        # would tap into a wall forever, learning nothing, and A* never routed around it.
        self._nomove_streak = getattr(self, "_nomove_streak", 0) + 1
        if self._nomove_streak >= 6:                         # long total stall -> chat box likely open
            self.ctrl.close_chat(1)
            self._nomove_streak = 0
        occ = self._entity_on(tgt)
        self._wall_fail[tgt] = self._wall_fail.get(tgt, 0) + 1
        n = self._wall_fail[tgt]
        gv = self.w.grid.get(tgt)
        if n <= 6 or n % 10 == 0:                            # diagnose why a step keeps failing
            self._dlog(f"NOMOVE {d} {pos0}->{tgt} fail#{n} grid={gv} occ={occ} turn={need_turn}")
        if occ is not None and n < 4:
            return                                           # mob standing there -> soft block
        # A tile we cannot enter is blocked, even if we previously believed it walkable: mobs
        # marked it walkable by standing NEXT to it, or it carries an object-wall. After enough
        # failures, our own experience must win over that belief or we tap into it forever.
        # force from the 2nd failure: "walkable" is often a STALE belief (any entity standing
        # on a tile marks it walkable, and object-walls block movement into a tile that mobs
        # still occupy). Two real failures with nothing standing there beats that belief --
        # otherwise mark_blocked silently no-ops and we tap into the wall forever (observed:
        # `grid=1` on every NOMOVE, grid_walls stuck at 0).
        if n >= 3:                  # 3 real failures (not 2) before carving a wall
            self.w.mark_blocked(*tgt, force=True)
            self._dlog(f"WALL {tgt} (fail#{n} tried {d} from {pos0})")

    def _dir_between(self, a, b):
        return NAME_OF.get((b[0] - a[0], b[1] - a[1]))

    def _fast_face(self, d):
        """Turn to face `d` with a single tap (no retry loop). We're already adjacent when
        this is called, so one turn tap is enough and keeps the swing cadence tight."""
        if d is None:
            return
        self.face_toward(d)           # self-correcting tap (see face_toward)

    def _adj_goal(self, grid, me, mob):
        """The walkable tile adjacent to the mob that is closest to us (and not a known wall).
        We path to THAT, then face+strike -- standing on the mob's own tile is never the goal."""
        cands = [(mob[0] + dx, mob[1] + dy) for dx, dy in DELTA.values()]
        cands = [c for c in cands if grid.get(c) != 0]
        if not cands:
            return mob
        return min(cands, key=lambda c: abs(c[0] - me[0]) + abs(c[1] - me[1]))

    def _greedy_step(self, me, mob):
        """Fallback when A* has no route yet: try to step toward the mob on the dominant axis,
        then the other, then any free direction. Each attempt is a nav_step, so every failure
        LEARNS a wall -- the next A* replan then has real map data to route around."""
        dx, dy = mob[0] - me[0], mob[1] - me[1]
        order = []
        if abs(dx) >= abs(dy):
            if dx: order.append("right" if dx > 0 else "left")
            if dy: order.append("down" if dy > 0 else "up")
        else:
            if dy: order.append("down" if dy > 0 else "up")
            if dx: order.append("right" if dx > 0 else "left")
        for d in ("up", "down", "left", "right"):
            if d not in order:
                order.append(d)
        for d in order:
            if self.nav_step(d):
                return True
        return False

    def approach_and_hit(self, eid, max_steps=16):
        """Close on a MOVING mob by A*-pathing over the LEARNED map (walls from failed steps,
        walkable from our track + every mob's tile), re-planning each step because the mob
        moves and we keep discovering walls. Swing the instant we're adjacent. Returns hits
        landed, or None if the mob vanished."""
        self.state = "approach"
        for _ in range(max_steps):
            me = self.w.sync_mem() or self.w.abs_pos()
            mob = self.mob_pos(eid)
            if mob is None:
                return None                              # despawned / out of range
            if me is None:
                return 0
            if abs(mob[0] - me[0]) + abs(mob[1] - me[1]) <= 1:   # adjacent -> strike now
                return self.strike(eid)
            grid = self.w.grid_copy()
            goal = self._adj_goal(grid, me, mob)
            path = astar(grid, me, goal)
            self._dlog(f"go me={me} mob={mob} goal={goal} path={len(path) if path else None}")
            if path:
                nxt = path[0]
                d = self._dir_between(me, nxt)
                if d is None or not self.nav_step(d):
                    continue                             # blocked -> wall learned, replan
            else:
                # goal fully walled off by KNOWN walls -> probe greedily to learn a way through
                if not self._greedy_step(me, mob):
                    return 0                             # genuinely can't advance toward it
        return 0

    def _step_toward(self, me, mob):
        """Thin wrapper (used by strike's follow-a-drifting-mob path): one learning step."""
        return self._greedy_step(me, mob)

    def face_toward(self, d, taps=2):
        """Make sure we face `d` when a mob is ADJACENT in that direction, WITHOUT needing to
        know our current facing (belief drifts on swallowed keypresses; the outgoing payload
        can't tell us either -- byte 0 is a sequence number).

        The trick: tapping toward an adjacent mob is self-correcting. If we're not facing it,
        NexusTK turns us. If we ARE facing it, the step is blocked by the mob standing there,
        so we keep the tile AND the facing. Either way we end up facing the mob, which is why
        this beats any facing-tracking scheme. Two paced taps absorb a swallowed press."""
        for i in range(taps):
            self.ctrl.tap(d, hold=0.02)
            time.sleep(0.07)                          # let the client apply turn/blocked-step
        with self.w.lock:                             # best-effort belief (nav_step hinting only)
            self.w.facing = dir_of(d)
        return True

    def _face_and_swing(self, d):
        """Turn to face `d` only if not already facing it (a dir tap when already facing STEPS
        instead of turning), then swing once and briefly poll for the hit echo. Returns hits
        landed this swing."""
        if d is not None:
            self.face_toward(d)       # self-correcting tap (see face_toward)
        with self.ag.lock:
            h0 = len(self.ag.hits)
        self.ctrl.tap("space", hold=0.03)
        deadline = time.time() + 0.18
        while time.time() < deadline:
            with self.ag.lock:
                landed = len(self.ag.hits) - h0
            if landed:
                return landed
            time.sleep(0.03)
        return 0

    def combat_reactive(self, tgt):
        """Reactive combat: ONE decision per tick from the freshest state, so we never commit
        to a stale plan. ALL geometry is now the single CLIENT frame: self from memory (static
        ptr) and mobs from the client's own entity table (refresh_from_pool) -- the same frame
        by construction, so 'adjacent' means actually adjacent on screen. Step pacing keeps the
        server within ~1 tile of our client pos, and the settle before the first swing lets it
        catch up -- the server then resolves the hit where we see it."""
        _, eid, e = tgt
        me = self.me_now or self.w.read_self_now()
        ms = self.w.mob_state(eid)
        if ms is None:                                    # mob gone -> release the lock
            # It vanished while WE were damaging it => we killed it. Top HP back up now, in the
            # lull between fights, instead of waiting to be in danger mid-fight.
            killed = self.hits_on.pop(eid, 0) > 0
            if killed:
                self._need_topoff = True
            self.target_eid = None
            self.best_dist.pop(eid, None)
            self.approach_fail.pop(eid, None)
            grave = self._last_tile.pop(eid, None)
            if killed and grave is not None:
                self.loot_kill(grave)                     # collect the drop in the post-kill lull
            return
        if me is None:                                    # no self-pos this tick -> wait, don't guess
            return
        mx, my, vx, vy, age = ms
        # Remember where it stood: once it dies the entity is gone from the client's table, so
        # this is the only record of where its loot will be.
        self._last_tile[eid] = (mx, my)
        dd = abs(mx - me[0]) + abs(my - me[1])
        if dd == 0:
            # An entity on our EXACT tile can't be meleed. Removing it was an infinite silent
            # loop: refresh_from_pool re-adds it from the client's table next tick, it scores
            # as nearest (distance 0), and we bounce here forever doing nothing -- the bot just
            # stands there. Blacklist it briefly so we move on to a real target.
            self.blacklist[eid] = self._now() + 8000
            self.no_melee[eid] = self._now() + 8000
            self.target_eid = None
            # Skipping alone leaves it hitting us while we do nothing. STEP OFF so it
            # becomes adjacent -- then it is a normal, meleeable target.
            self._dlog(f"entity {eid} shares our tile {me} -> stepping off to melee it")
            self._sweep(legs=1)
            return
        # ---- ADJACENT: RAPID swing burst -- fire fast (no per-swing blocking poll), only
        #      re-checking adjacency cheaply between swings so we bail the instant it moves.
        #      Re-face on the fly if the mob slips to a different adjacent side. ----
        if dd == 1:
            self.state = "attack"
            self.best_dist[eid] = 1
            self.approach_fail.pop(eid, None)
            self._gain_ts[eid] = time.time()
            # SERVER SETTLE: if we JUST stepped, the server may still be applying it -- swinging
            # instantly resolves the hit from our OLD server tile (a miss). Wait out the rest of
            # a short settle window since the last fired step, then swing.
            since = time.time() - self._last_step_ts
            if since < 0.22:
                time.sleep(0.22 - since)
            with self.ag.lock:
                h0 = len(self.ag.hits)
            # CONFIRM facing before swinging -- an unverified turn is why swings went sideways.
            d = self.dir_to(me, (mx, my))
            self.face_toward(d)                            # always ends up facing the mob
            for _ in range(5):
                self._stamp_swing()            # label the attempt BEFORE it goes out
                self.ctrl.tap("space", hold=0.02)
                time.sleep(self.SWING_GAP)                     # ~ client swing cooldown, no more
                ms2 = self.w.mob_state(eid)
                if ms2 is None:
                    break
                me2 = self.w.read_self_now() or me             # same client frame as the mob
                if abs(ms2[0] - me2[0]) + abs(ms2[1] - me2[1]) != 1:   # it moved -> re-decide next tick
                    break
                nd = self.dir_to(me2, (ms2[0], ms2[1]))         # shifted to another side? re-face
                if nd != d:                                 # slipped to another side -> re-face
                    self.face_toward(nd, taps=1)
                    d = nd
            with self.ag.lock:
                landed = len(self.ag.hits) - h0
            if landed:
                self.air_streak = 0
                self.no_hit.pop(eid, None)
                lk = e.get("look")
                if lk is not None:
                    self.damaged_looks[lk] += landed
                # NO hit-count give-up. If we're fighting it, we KILL it -- however many swings
                # that takes. Walking away from a mob that's attacking us just means eating the
                # damage for no reward while it follows us anyway.
                self.hits_on[eid] = self.hits_on.get(eid, 0) + landed
            else:
                # NO ghost-guess here. Presence is authoritative from the client's entity table
                # (refresh_from_pool purges anything the client doesn't have), so a live target
                # that simply MISSED is not a ghost -- the old no-bleed counter dropped real,
                # actively-moving mobs mid-fight. Only a long dead spell (never any damage while
                # we stay adjacent) unlocks the target, letting us pick a better one.
                self.no_hit[eid] = self.no_hit.get(eid, 0) + 1
                if self.no_hit[eid] >= 25:
                    self.blacklist[eid] = self._now() + 10000
                    self.target_eid = None
                    self.no_hit.pop(eid, None)
                    self._dlog(f"no damage after 25 bursts on {eid} -> unlock, pick another")
            self._dlog(f"SWING eid={eid} me={me} mob=({mx},{my}) v=({vx},{vy}) hits={landed}")
            return
        # ---- APPROACH (single client frame now -- no translation needed). ----
        # PURSUIT SHORTCUT: a mob that just stepped away from us vacated a tile that is
        # adjacent to US and adjacent to ITS new tile -- stepping INTO it is guaranteed
        # walkable and keeps us glued to a wanderer (classic follow trick).
        if dd == 2 and (vx or vy) and age < 600:
            vac = (mx - vx, my - vy)
            if abs(vac[0] - me[0]) + abs(vac[1] - me[1]) == 1:
                d = self._dir_between(me, vac)
                if d:
                    self.nav_step(d)
                    self._dlog(f"FOLLOW eid={eid} me={me} mob=({mx},{my}) v=({vx},{vy}) -> vacated {vac}")
                    return
        # A*-route ONE step toward the mob (velocity-led), re-planning next tick. A* is what
        # routes AROUND walls -- dir_to alone hammers the dominant axis into them (WALL x24).
        self.state = "approach"
        lead = (mx + vx, my + vy) if (vx or vy) else (mx, my)
        grid = self.w.grid_copy()
        goal = self._adj_goal(grid, me, lead)
        path = astar(grid, me, goal)
        d = self._dir_between(me, path[0]) if path else None
        # If a MOB is standing on the tile we need, don't queue behind it -- kill it. It's
        # adjacent, it's in our way, and (per the rule) anything fighting us gets killed.
        if path:
            blocker = self._entity_on(path[0])
            if blocker is not None and blocker != eid and self.blacklist.get(blocker, 0) <= self._now():
                be = dict(self.w.fresh_entities()).get(blocker)
                if self._huntable(blocker, be) is not True:
                    # Something not on the kill list is in the way. Do NOT punch it, but do NOT
                    # mark its tile a wall either: an earlier version wrote `grid[tile] = 0`
                    # here, and because decay_walls only expires tiles that also have a wall_ts
                    # entry, every tile a non-target ever stood on became a PERMANENT wall. A*
                    # then routed around empty ground forever -- the bot circling mobs it could
                    # not reach. Mobs move; a body is a transient obstacle, so just step aside
                    # and re-plan next tick.
                    self._dlog(f"non-target {blocker} blocks {path[0]} -> stepping aside")
                    self._sweep(legs=1)
                    return
                self.target_eid = blocker
                self._dlog(f"blocked by {blocker} at {path[0]} -> retarget it")
                return
        if d is None:                                    # no known route -> greedy probe (learns walls)
            self._greedy_step(me, (mx, my))
            d = "greedy"
        else:
            self.nav_step(d)
        self._dlog(f"APPR eid={eid} me={me} mob=({mx},{my}) v=({vx},{vy}) dd={dd} d={d} path={len(path) if path else 0}")
        # Relentless, but don't chase an uncatchable fleer forever. TIME-based (ticks are now
        # ~30x/sec, so a tick counter would give up in a fraction of a second): give up only if
        # we've made NO progress for several seconds AND it's still well out of reach. A mob we're
        # nearly on (dd<=2) is never abandoned -- one more step usually lands us adjacent.
        best = self.best_dist.get(eid)
        wounded = self.hits_on.get(eid, 0) > 0
        if best is None or dd < best:
            self.best_dist[eid] = dd
            self._gain_ts[eid] = time.time()
        elif wounded:
            # WE ALREADY HURT IT -> never give up chasing; finishing the kill is worth far more
            # than switching to a fresh full-HP mob. Only its death ends this engagement.
            pass
        # NB: this must NOT be restricted to dd > 2. At dd == 2 another mob often stands on the
        # single tile between us, so the step can never succeed -- with a dd>2 guard the bot
        # spun there forever (the "stops fighting and just stands" bug).
        elif (eid in self.attackers
              and time.time() - self.attackers[eid] <= self.ATTACKER_TTL):
            pass                    # it's hitting us -> never abandon it, fight back
        elif time.time() - self._gain_ts.get(eid, time.time()) > 5.0:
            self.blacklist[eid] = self._now() + 8000
            self.target_eid = None
            self.best_dist.pop(eid, None)
            self._gain_ts.pop(eid, None)
            self._dlog(f"uncatchable {eid} (dd={dd} best={best}, 4s no gain) -> blacklist, pick another")

    def engage(self, me, tgt):
        self.combat_reactive(tgt)

    DENSE_LIMIT = 60       # pure runaway guard, NOT a "town" filter -- caves legitimately
                           # hold tons of monsters (the target!). The lockup was fixed by
                           # the opcode filter, not by avoiding mobs. Only an absurd count
                           # (client glitch / everything-on-screen) trips this now.

    def tick(self):
        self.me_now = self.w.sync_mem()           # the ONE self-pos read per tick (reused everywhere)
        self._confirm_step(self.me_now)           # resolve the step we fired last tick (learn walls)
        # RELIABLE per-tick vitals straight from the static self-struct -> survival always has
        # real HP (the broken findvitals feed reading None is what let the character die). Also
        # the single source of truth for DEATH detection.
        v = self.w.read_vitals()
        if v is not None:
            with self.ag.lock:
                self.ag.curhp = v[0]
                self.ag.curmana = v[2]
                self.ag.exp = v[4]
                prev = self.ag.cur or {}
                self.ag.cur = {"level": self.ag.level or prev.get("level"),
                               "might": prev.get("might", StatsMem.ATTR["might"]),
                               "grace": prev.get("grace", StatsMem.ATTR["grace"]),
                               "will": prev.get("will", StatsMem.ATTR["will"]),
                               "maxhp": v[1], "maxmana": v[3]}
            # FIGHT BACK: an HP drop means something is meleeing us -> flag adjacent mobs as
            # attackers so they become the next target.
            if self._last_hp is not None and v[0] < self._last_hp:
                self.note_attackers(self._last_hp - v[0])
            self._last_hp = v[0]
            if v[0] == 0:                         # DEAD -> stop flailing at ghosts, handle revival
                self.handle_death()
                return
        # PERCEPTION = the client's own entity table (enument, ~3ms): positions + presence
        # authoritative every tick, ghosts impossible, stationary mobs stay targetable.
        # (Replaces the old MemEntities bulk-scan entirely.)
        if self.w.pool is None:
            self.w.bootstrap_pool()               # needs one wire-known mob; retried until found
        self.w.refresh_from_pool()
        self._name_upkeep()                       # label mobs (and gear) for the stats DB
        self._room_upkeep()                       # room identity + WARP detection (before pathing)
        self.w.decay_walls()                      # forget stale learned walls (anti-cage)
        if self.survival():                       # HP watch: heal / emergency-escape first
            return
        # Get clear of anything on the banned list BEFORE deciding what to do this tick. Against
        # a mob that can one-shot us, distance is the only real defence -- healing loses that
        # race, and not-attacking on its own just means dying politely.
        if self.flee_banned(self.me_now):
            return
        # runaway guard counts NEARBY entities only -- enumeration now tracks the whole map
        # section (~100 real mobs), which is normal, not a glitch; only an absurd LOCAL pile
        # (client glitch) should trip this.
        fresh = self.w.fresh_entities()
        mpos = self.me_now
        crowd = (sum(1 for _, e in fresh
                     if abs(e["x"] - mpos[0]) + abs(e["y"] - mpos[1]) <= 12)
                 if mpos else len(fresh))
        if crowd > self.DENSE_LIMIT:
            self.state = "crowded"
            time.sleep(0.5)
            return
        hp, maxhp = self.vitals()
        if hp:
            self.hp_max_seen = max(self.hp_max_seen, hp)
        # recover well before death: 30% of maxhp, or of the largest HP we've observed
        # (a good maxhp proxy once we've been near full), else a conservative absolute.
        ref = maxhp or (self.hp_max_seen if self.hp_max_seen > 200 else None)
        floor = (0.30 * ref) if ref else self.HP_FLOOR
        if hp is not None and hp < floor:
            self.recover()
            return
        me = self.w.abs_pos()
        # HEARTBEAT: log once every 5s of decisions that produce no other output, so a silent
        # loop (the bot "just standing") is visible in the log instead of looking like idle.
        if time.time() - getattr(self, "_hb_ts", 0) > 5:
            self._hb_ts = time.time()
            self._dlog(f"hb pos={me} tgt={self.target_eid} state={self.state} "
                       f"ents={len(self.w.fresh_entities())} bl={len(self.blacklist)}")
        if self._stuck(me):                       # penned / bumping a wall -> find an opening
            self.escape()
            return
        tgt = self.choose_target(me)
        if tgt is None:
            (self.wander() if me else self.bootstrap())
            return
        if me is None:
            self.bootstrap()
            return
        self.engage(me, tgt)

    def write_status(self):
        with self.w.lock:
            me = None if self.w.offset is None else (self.w.rel[0] + self.w.offset[0],
                                                     self.w.rel[1] + self.w.offset[1])
            nmob = len(self.w.ent)
            facing = self.w.facing
            gwalk = sum(1 for v in self.w.grid.values() if v == 1)
            gblock = sum(1 for v in self.w.grid.values() if v == 0)
            ginfer = sum(1 for v in self.w.grid.values() if v == 2)
        st = {"state": self.state, "abs_pos": me, "facing": facing,
              "hp": self.ag.curhp, "maxhp": self.ag.cur["maxhp"] if self.ag.cur else None,
              "level": self.ag.level, "exp": self.ag.exp,
              "entities_tracked": nmob, "session_kills": self.session_kills,
              "damaged_looks": dict(self.damaged_looks.most_common()),
              "blacklisted": len(self.blacklist),
              "grid_walkable": gwalk, "grid_walls": gblock, "grid_inferred": ginfer,
              "mem_pos": self.w.mem_addr is not None,
              "updated": time.strftime("%Y-%m-%d %H:%M:%S")}
        json.dump(st, open(P_BOTSTATUS, "w", encoding="utf-8"), indent=1, default=str)


def count_kills_csv():
    """Total kills logged so far (survives agent.kills being drained by flush)."""
    try:
        with open(NA.P_KILLS, "r", encoding="utf-8") as f:
            return max(0, sum(1 for _ in f) - 1)   # minus header
    except OSError:
        return 0


def grind_loop(world, agent, mover, ctrl, deadline=None, hunt_looks=None,
               memself=None, statsmem=None, mementities=None,
               swap_items=None, swap_every=15, swap_slot="l", loot=True,
               hunt_names=None):
    brain = Brain(world, agent, mover, ctrl, hunt_looks=hunt_looks)
    brain.ments = mementities
    brain.loot_enabled = loot
    brain.hunt_names = hunt_names
    if hunt_names:
        log_bot(f"KILL LIST (names): {sorted(hunt_names)} -- everything else is avoided")
    brain.k0 = count_kills_csv()
    # Resolve the room BEFORE the first swing: the cold locate is a ~7s heap harvest, so
    # doing it lazily on tick 1 both stalls the loop mid-combat and leaves the first few
    # damage rows unattributed to a room.
    # Ask the server for our own profile so every row is labelled with the ACTUAL worn
    # loadout (and the weapon, which joins to item_stats.csv for Damage S/L). `2d 00 00` is
    # the request the client itself uses -- learned from a live capture. It must run AFTER
    # calibration: the send fn's connection object is captured from the client's first
    # send, so asking at startup silently no-ops.
    # use the primed helper: a bare sendraw silently no-ops until the client's first send
    # has supplied the connection object, which is why this kept reporting "not obtained"
    eq0 = brain.fetch_profile()
    if eq0:
        print(f"loadout: {', '.join(eq0)}")
    else:
        print("loadout: not obtained yet (retried every 10s; weapon label fills in later)")
    if world.mem_ex is not None:
        r0 = brain.room_tracker.poll(world.mem_ex)
        if r0:
            mid, mw, mh = brain.room_tracker.meta(r0)
            brain.home_room = r0
            world.enter_room(r0, world.read_self_now(), log=brain._dlog)
            print(f"room: {r0} (map {mid}, {mw}x{mh})")
        else:
            print("room: UNKNOWN (name buffer not located; kills won't be room-attributed)")
    swap_i = 0
    if swap_items:
        # Start the experiment on a known footing so each configuration gets equal kills.
        first = swap_items[0]
        other = swap_items[1] if len(swap_items) > 1 else None
        if first.lower() in ("none", "-", "off"):
            started = bool(brain.unequip(swap_slot))
        else:
            started = brain.swap_to(first, must_remove=None if (other or "").lower() in
                                    ("none", "-", "off") else other)
        if started:
            print(f"experiment: alternating {swap_items} every {swap_every} kills "
                  f"(now wearing {swap_items[0]!r})")
        else:
            # A startup hiccup (usually the profile not being fetchable yet) must NOT kill
            # the experiment -- the periodic swap retries once the connection is warm.
            print(f"experiment: initial wear of {swap_items[0]!r} deferred; "
                  f"will retry at the first swap point")
    swap_mark = count_kills_csv()
    print("\n--- GRIND (Ctrl-C to stop) ---")
    last_flush = last_print = last_stats = 0
    stall_since = time.time()
    last_prog = (None, 0)          # (position, kills) used to detect total lack of progress
    while True:
        if deadline and time.time() > deadline:
            print("time limit reached.")
            break
        if os.path.exists(P_STOP):
            # GRACEFUL STOP. Force-killing this process leaves frida's interceptors
            # installed in the client with no chance to detach, which has crashed the
            # game. Touch auto/STOP instead and we unwind normally (detach included).
            print("stop file seen -- ending session cleanly.")
            try:
                os.remove(P_STOP)
            except OSError:
                pass
            break
        if CLIENT_GONE[0]:                        # client exited -> stop, don't play a corpse
            print("client is gone -- ending session.")
            break
        # STALL WATCHDOG: if neither our position nor the kill count changes for 5 minutes,
        # something is wrong outside our control (client hung/disconnected/logged out, or we're
        # truly penned). Stop loudly instead of burning hours in a no-op sweep.
        prog = (world.read_self_now(), count_kills_csv())
        # --- unattended stat variance: alternate the swap items every N kills ---
        # Only between fights (no live target), so a menu never opens mid-combat.
        if swap_items and prog[1] - swap_mark >= swap_every and brain.target_eid is None:
            swap_i = (swap_i + 1) % len(swap_items)
            nxt = swap_items[swap_i]
            prev_item = swap_items[(swap_i - 1) % len(swap_items)]
            if nxt.lower() in ("none", "-", "off"):
                # Take the slot OFF rather than wear something. Needed for `hit`: wearing a
                # second ring just fills the other hand (verified -- it does NOT displace),
                # so removal is the only way to move that stat alone.
                removed = brain.unequip(swap_slot)
                ok = bool(removed)
            else:
                # must_remove: the pair must share ONE slot, else "wearing" the next item
                # changes nothing and the experiment yields no variance at all.
                ok = brain.swap_to(nxt, must_remove=None if prev_item.lower() in
                                   ("none", "-", "off") else prev_item)
            if ok:
                print(f"[experiment] {prog[1] - swap_mark} kills done -> now wearing {nxt!r}")
                swap_mark = prog[1]
            else:
                print(f"[experiment] swap to {nxt!r} FAILED -- stopping the experiment "
                      f"(data would be mislabelled)")
                swap_items = None
        if prog[0] is not None and prog != last_prog:
            last_prog = prog
            stall_since = time.time()
        elif time.time() - stall_since > 300:
            print(f"*** NO PROGRESS FOR 5 MIN (pos={prog[0]} kills={prog[1]}) -- stopping ***")
            log_bot("stalled 5min with no movement and no kills -> stopping")
            break
        # Stats read is wrapped: a stale struct address (heap moved/freed) makes
        # sync/find throw, and an UNHANDLED throw here would kill the whole loop and
        # leave the character with NO survival -> AFK death. On any error, drop the
        # address so the next iteration re-finds it, and keep the loop alive.
        if statsmem is not None and time.time() - last_stats > 1:
            try:
                if statsmem.addr is None:
                    statsmem.find(log=log_bot)    # keep trying until exp is known + struct found
                else:
                    statsmem.sync()               # live hp/mp/level/exp -> agent (survival, logging)
            except Exception as e:
                log_bot(f"stats error: {e} -> re-find next tick")
                statsmem.addr = None
            last_stats = time.time()
        # re-locate self in memory after a map change invalidated the address (0x0b).
        # one-shot flag so a failed calibration doesn't wiggle every iteration.
        if memself is not None and world.mem_relocate:
            world.mem_relocate = False
            log_bot("map changed -> re-locating self-position...")
            try:
                memself.calibrate(log=log_bot)
            except Exception as e:
                log_bot(f"relocate error: {e}")
        try:
            brain.tick()
        except Exception as e:
            log_bot(f"tick error: {e}")
        now = time.time()
        if now - last_flush > 4:
            with agent.lock:
                agent.flush()
            agent.flush_swings()
            world.write_gear()
            brain.session_kills = count_kills_csv() - brain.k0
            brain.write_status()
            last_flush = now
        if now - last_print > 5:
            me = world.abs_pos()
            g = world.grid_copy()
            gw = sum(1 for v in g.values() if v == 1)
            mhp = agent.cur["maxhp"] if agent.cur else "?"
            mmp = agent.cur["maxmana"] if agent.cur else "?"
            mp = getattr(agent, "curmana", None)
            print(f"[{brain.state:9}] pos={me} lvl={agent.level} hp={agent.curhp}/{mhp} "
                  f"mp={mp}/{mmp} exp={agent.exp} kills={brain.session_kills} loot={brain.session_loot} "
                  f"grid={gw}w looks={dict(brain.damaged_looks)}")
            last_print = now
        time.sleep(0.03)                          # fast control loop -> re-decide ~30x/sec
    with agent.lock:
        agent.flush()
    agent.flush_swings()
    brain.session_kills = count_kills_csv() - brain.k0
    brain.write_status()
    print(f"session kills: {brain.session_kills}  items looted: {brain.session_loot}  "
          f"damaged looks: {dict(brain.damaged_looks)}")


def log_bot(msg):
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


def measure_loop(world, agent, deadline):
    """LISTEN ONLY -- no input injection, no attacking. Measures the filtered packet rate
    and what mobs are actually present, so we can confirm the hook is light in a mob-dense
    cave before ever enabling the grind. Safe to run anywhere."""
    print("\n--- MEASURE (listen only, no input) ---")
    last = time.time()
    last_n = _pkt_count[0]
    while time.time() < deadline:
        time.sleep(1.0)
        now = time.time()
        n = _pkt_count[0]
        rate = (n - last_n) / (now - last)
        last, last_n = now, n
        fresh = world.fresh_entities()
        looks = collections.Counter(e["look"] for _, e in fresh
                                    if e["look"] and e["look"] <= 500)
        print(f"  pkts/s={rate:6.0f}  fresh_entities={len(fresh):3d}  "
              f"mob_looks={dict(looks.most_common(8))}")
    print("measure done.")


def drtest_loop(world, mover):
    """Validate dead reckoning: walk a closed square and check we return to the origin.
    If rel drifts far from (0,0) the step model is wrong (async timing, wall, or the
    faces-then-steps assumption) -- fix that before trusting navigation."""
    print("\n--- DEAD-RECKONING TEST: walking a 3x3 square ---")
    print("(watch the character trace a small box and return to start)")
    with world.lock:
        world.rel = (0, 0)
    plan = ["right", "right", "down", "down", "left", "left", "up", "up"]
    results = []
    lats = []
    for name in plan:
        mover.last_latency = None
        r = mover.step(name)
        results.append((name, r, world.rel))
        if mover.last_latency is not None:
            lats.append(mover.last_latency)
        lat = f" lat={mover.last_latency}ms" if mover.last_latency is not None else ""
        print(f"  {name:5} -> {r:7}  rel={world.rel}  facing={world.facing}{lat}")
        time.sleep(0.15)
    if lats:
        print(f"\npacket latency: min={min(lats)} avg={sum(lats)//len(lats)} max={max(lats)} ms "
              f"(sets a floor on step speed)")
    fx, fy = world.rel
    drift = abs(fx) + abs(fy)
    steps = sum(1 for _, r, _ in results if r == "step")
    print(f"\nsteps taken: {steps}/8   final rel={world.rel}   drift from origin={drift}")
    if drift == 0 and steps == 8:
        print("PERFECT: dead reckoning is exact. Navigation is safe to build on.")
    elif drift <= 2:
        print("GOOD: minor drift (a blocked step or timing). Usable; I'll add re-anchoring.")
    else:
        print("OFF: large drift. The step model needs tuning (settle time / wall handling)")
        print("before autonomous navigation. Tell me the outcomes above.")


def main():
    args = sys.argv[1:]
    mode = "send" if "--sendinput" in args else "post"

    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    secs = opt("--seconds")
    deadline = (time.time() + float(secs)) if secs else None
    looks_arg = opt("--looks")            # e.g. "21,25" to restrict hunt; else damage-gated
    # unattended stat-variance experiment: alternate two items in one slot every N kills,
    # e.g. --swap "Black ring,Sea ring" --swap-every 15  (hit 3 <-> 0, nothing else moves)
    swap_arg = opt("--swap")
    swap_items = [x.strip() for x in swap_arg.split(",")] if swap_arg else None
    swap_every = int(opt("--swap-every", "15"))
    swap_slot = opt("--swap-slot", "l")   # equipment slot for "none" (take off)
    hunt_looks = [int(x) for x in looks_arg.split(",")] if looks_arg else None
    # --names "Green squirrel"  -> ONLY these are ever attacked; anything else is fled from.
    names_arg = opt("--names")
    hunt_names = ({n.strip().lower() for n in names_arg.split(",") if n.strip()}
                  if names_arg else None)

    wins = find_windows()
    if not wins:
        print("No live NexusTK.exe window found (client must be running + logged in).")
        return
    hwnd = wins[0][0]
    print(f"live client hwnd={hwnd}  (input mode: {mode})")

    agent = NA.Agent()
    world = World(agent)
    ctrl = Controller(hwnd, mode=mode)
    mover = Mover(world, ctrl)
    if mode == "send":
        import ctypes
        ctypes.windll.user32.SetForegroundWindow(hwnd); time.sleep(0.3)
    # hook the SAME process whose window we drive -- see attach()
    s, sc = attach(build_pump(world, agent, raw_log=("--raw" in args)),
                   pid=wins[0][2])
    world.mem_ex = sc.exports_sync                # memory read/scan RPC for exact self-pos
    ctrl.fkey = sc.exports_sync                   # SAME-PROCESS input (postkey/postchar)
    ctrl.close_chat(3)                            # a leftover open chat box swallows movement
    nw = world.load_warps()                       # warp graph accumulates across sessions
    if nw:
        print(f"loaded {nw} known warp link(s) from {os.path.basename(P_WARPS)}")
    time.sleep(1.5)                               # let some state arrive

    if "--measure" in args:
        measure_loop(world, agent, deadline or (time.time() + 15))
        return
    if "--drtest" in args:
        drtest_loop(world, mover)
        return
    if "--grind" in args:
        memself = MemSelf(sc.exports_sync, world, ctrl)
        statsmem = StatsMem(sc.exports_sync, agent)
        mementities = MemEntities(sc.exports_sync, world)
        if "--nomem" not in args:
            log_bot("calibrating exact self-position from memory (a few nudges)...")
            memself.calibrate(log=log_bot)
            log_bot("locating live stats (hp/mp/level) in memory...")
            statsmem.find(log=log_bot)
        try:
            grind_loop(world, agent, mover, ctrl, deadline=deadline,
                       hunt_looks=hunt_looks, memself=memself, statsmem=statsmem,
                       mementities=mementities, swap_items=swap_items,
                       swap_every=swap_every, swap_slot=swap_slot,
                       loot=("--no-loot" not in args), hunt_names=hunt_names)
        except KeyboardInterrupt:
            print("\nstopped.")
        return

    # default: confirm the move opcode is flowing, then perception readout
    caps = calibrate_moveop(world, ctrl)
    if caps["move"] is None:
        print("\nCould not see the move opcode on the wire. If the character DID move,")
        print("the send hook missed the egress path; tell me and I'll widen it.")
    print("\nPerception readout (Ctrl-C to stop)...")
    try:
        watch_loop(world, agent, deadline=deadline)
    except KeyboardInterrupt:
        print("\nstopped.")


if __name__ == "__main__":
    main()
