#!/usr/bin/env python
r"""
Recover the 5.33 client's ACTUAL packet grammar, field by field, by watching it read.

The client pulls every field out of a packet body through five tiny stream primitives. Hook those,
bracket them with the opcode dispatcher so each read can be attributed to a packet and turned into an
offset, and the read order that comes out IS the grammar -- not a guess reconstructed from a hexdump,
and not an analogy to 4.95 or to a 6.x/7.x reference server.

  0x4a1200(p)          -> u8      at p                  (non-advancing)
  0x4a1210(p)          -> u16 BE  at p                  (non-advancing)
  0x4a1250(p)          -> u32 BE  at p                  (non-advancing)
  0x4a3e30(base,&cur)  -> u8      at base+*cur, cur += 1
  0x4a3e50(base,&cur)  -> u16 BE  at base+*cur, cur += 2
  0x4a3ec0(base,&cur)  -> u32 BE  at base+*cur, cur += 4

Reading it: a run of offsets 0,1,2,3.. is a fixed header. A u8 read at offset N followed by a JUMP to
N+1+k is a length-prefixed string of k bytes (the client memcpy's the body, which is not a hooked
read, so the string shows up as a gap -- the gap is the evidence, not missing data). A repeated block
of offsets is an array, and the field just before it is the count.

An offset the client NEVER reads is the useful negative result: we are sending a field into a hole.

Usage (client must already be running; attach so world entry is not required):
    python re/grammar_533.py --attach --op 39          # the self-profile grammar
    python re/grammar_533.py --attach --op 33,0e       # several
    python re/grammar_533.py --attach                  # everything except terrain (0x06 is ~1k reads)
    python re/grammar_533.py --attach --op 06           # ...include terrain explicitly

Each packet prints as a field table plus the raw body, and is appended to re/grammar_533.jsonl.
"""
import argparse
import json
import os
import time
from pathlib import Path

import frida

from _paths import CLIENT5, require

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "grammar_533.jsonl")

# All known packet dispatchers. The client offers each packet to every one of them in turn until one
# accepts, so bracketing only the world dispatcher (0x463320) missed the reads of every opcode another
# dispatcher owns -- 0x08 stats and 0x0f/0x10/0x17 items+spells among them.
DISPATCH_RVAS = [0x63320, 0xD5F80, 0xE13D0, 0xE6B70]
DISPATCH_RVA = 0x63320
READERS = [   # (rva, width, advancing)
    (0xA1200, 1, False),
    (0xA1210, 2, False),
    (0xA1250, 4, False),
    (0xA3E30, 1, True),
    (0xA3E50, 2, True),
    (0xA3EC0, 4, True),   # u32 BE, advancing -- missing until 0x0F was traced, so every u32 field read
                          # through it was INVISIBLE and looked like an unexplained gap in the offsets.
]

JS = r"""
'use strict';
var m = (typeof Process.findModuleByName === 'function') ? Process.findModuleByName('NexusTK.exe') : null;
var base = m ? m.base : ptr('0x400000');
var WANT = __WANT__;           // null = all but 0x06; else an array of opcodes
var cur = null;                // the packet whose READS we are recording (subject to WANT)
var curOp = -1;                // the opcode being dispatched right now, REGARDLESS of WANT.
// Kept separate on purpose. Attributing a window switch needs to know which packet is on the stack even
// when that packet's reads are filtered out -- with one variable doing both jobs, every switch driven by
// a non-selected opcode reported as "local UI", which is exactly the wrong answer to "what closed my
// character sheet".

function wanted(op) {
  if (WANT === null) return op !== 0x06;
  return WANT.indexOf(op) >= 0;
}

__DISPATCH_LIST__.forEach(function (rva) {
Interceptor.attach(base.add(rva), {
  onEnter: function () {
    this.prev = cur; cur = null;
    this.prevOp = curOp; curOp = -1;
    try {
      var a0 = this.context.esp.add(4).readPointer();
      var body = a0.add(0xc).readPointer();
      var op = body.readU8();
      curOp = op;
      if (!wanted(op)) return;
      var b = new Uint8Array(body.readByteArray(320));
      cur = { op: op, base: body, reads: [], callers: {},
              hex: Array.from(b).map(function (x) { return ('0'+x.toString(16)).slice(-2); }).join(' ') };
      this.mine = cur;
    } catch (e) {}
  },
  onLeave: function (ret) {
    if (this.mine) {
      send({ t:'pkt', op:this.mine.op, handled:(ret.toInt32() & 0xff) !== 0,
             reads:this.mine.reads, callers:this.mine.callers, hex:this.mine.hex });
    }
    cur = this.prev; curOp = this.prevOp;
  }
});
});

function rec(off, width, val, ra) {
  if (!cur) return;
  if (off < 0 || off > 4096) return;      // a read of something that is not this body
  if (cur.reads.length > 600) return;
  cur.reads.push([off, width, val]);
  // Which function did the reading. Once a packet is being mis-parsed, the offsets after the first
  // divergence are worthless -- but the CALLER is not: it names the routine to disassemble for the
  // real grammar, which beats inferring one from reads driven by a body of the wrong shape.
  if (ra) { var k = '0x' + ra.toString(16); cur.callers[k] = (cur.callers[k] || 0) + 1; }
}

// Window manager sub_435790(this, index, body): index 0 = self profile, 1 = click profile. Every
// switch closes whatever window is open, so this is what to watch when a screen closes on its own --
// it reports which opcode drove the switch.
Interceptor.attach(base.add(0x35790), {
  onEnter: function () {
    try {
      send({ t:'win', idx: this.context.esp.add(4).readS32(),
             op: curOp, ra: '0x' + this.returnAddress.toString(16) });
    } catch (e) {}
  }
});

function bt(ctx) {
  return Thread.backtrace(ctx, Backtracer.ACCURATE)
    .filter(function (a) { return a.compare(base) >= 0 && a.compare(base.add(0x200000)) < 0; })
    .slice(0, 10).map(function (a) { return '0x' + a.toString(16); }).join(' <- ');
}

// sub_4857c0: the deferred move-commit / scene rebuild the 0x08 trailing byte can fire. Watch it
// directly -- the body[46]=0 fix assumed this was the pane-wiper, and the wipe survived, so either
// something else still calls it or the wipe lives elsewhere. Either way this settles it.
Interceptor.attach(base.add(0x857c0), {
  onEnter: function () { send({ t:'mv', op: curOp, m: bt(this.context) }); }
});

// sub_4e1e90(index, &buf): the per-field HUD widget notify inside the 0x08 stats parser sub_4e2450.
// It is called ONLY when a stat field differs from the value the client last stored, with a small
// index (1=nation 2=totem 4=level 5=might 6=grace 7=will 8=hp/maxhp 9=mp/maxmp 0xa=exp 0xb=coins).
// This is the whole question for the pane-wipe: fire a LONE 0x08 (@mailflag 45 0) whose values match
// what is already stored -- if these still fire, our packet is NOT byte-stable (something differs
// every send) and that is the fixable bug; if NONE fire yet the pane still wipes, the wipe is an
// unconditional repaint, not the change-notify path.
Interceptor.attach(base.add(0xe1e90), {
  onEnter: function () {
    try { send({ t:'notify', op: curOp, idx: this.context.esp.add(4).readS32() }); } catch (e) {}
  }
});

// sub_4d9f00: the UNCONDITIONAL tail of the 0x08 parser (writes a level-band icon id to 0x5528dc..e4).
// Runs on every 0x08 regardless of whether anything changed. If the pane wipes with zero notify events,
// this (or the pre-dispatch HUD path) is the suspect.
Interceptor.attach(base.add(0xd9f00), {
  onEnter: function () { send({ t:'stattail', op: curOp }); }
});

// Outgoing 0x05 map re-request = the client rebuilding its view on its own (what Ctrl+R does). The
// backtrace names the requester; curOp says whether a packet handler drove it (usually -1: deferred
// to the main loop, so correlate by adjacency in the timeline instead).
(function () {
  var m2 = Process.findModuleByName('WSOCK32.dll') || Process.findModuleByName('ws2_32.dll');
  var a = m2 ? m2.findExportByName('send') : null;
  if (!a) { send({ t:'info', m:'no wsock send export -- 0x05 watch off' }); return; }
  Interceptor.attach(a, {
    onEnter: function (args) {
      try {
        var buf = args[1], n = args[2].toInt32();
        if (n >= 4 && buf.readU8() === 0xaa && buf.add(3).readU8() === 0x05)
          send({ t:'req05', op: curOp, m: bt(this.context) });
      } catch (e) {}
    }
  });
})();

__READERS__

send({ t:'info', m:'grammar tracer armed on ' + base + ' -- exercise the client' });
"""


def build_js(want):
    hooks = []
    for rva, width, advancing in READERS:
        if advancing:
            # f(basePtr, &cursor): value at basePtr + *cursor, then *cursor += width.
            # Read the cursor on ENTER (it is the offset of THIS field; on leave it already moved).
            hooks.append(f"""
Interceptor.attach(base.add({hex(rva)}), {{
  onEnter: function () {{
    this.off = -1;
    if (!cur) return;
    try {{
      var b = this.context.esp.add(4).readPointer();
      var cp = this.context.esp.add(8).readPointer();
      if (!b.equals(cur.base)) return;
      this.off = cp.readU32(); this.ra = this.returnAddress;
    }} catch (e) {{}}
  }},
  onLeave: function (ret) {{ if (this.off >= 0) rec(this.off, {width}, (ret.toInt32() >>> 0) & {(1 << (8 * width)) - 1}, this.ra); }}
}});""")
        else:
            hooks.append(f"""
Interceptor.attach(base.add({hex(rva)}), {{
  onEnter: function () {{
    this.off = -1;
    if (!cur) return;
    try {{ this.off = this.context.esp.add(4).readPointer().sub(cur.base).toInt32(); this.ra = this.returnAddress; }} catch (e) {{}}
  }},
  // Mask to the read width: these helpers return in AL/AX, so the upper bytes of EAX are stale
  // garbage from the caller and a raw toInt32() prints a nonsense value next to a correct hex byte.
  onLeave: function (ret) {{ if (this.off >= 0) rec(this.off, {width}, (ret.toInt32() >>> 0) & {(1 << (8 * width)) - 1}, this.ra); }}
}});""")
    return (JS.replace("__DISPATCH_LIST__", json.dumps(DISPATCH_RVAS))
              .replace("__WANT__", "null" if want is None else json.dumps(sorted(want)))
              .replace("__READERS__", "\n".join(hooks)))


def report(p, out):
    op, handled, reads, hexs = p["op"], p["handled"], p["reads"], p["hex"]
    body = bytes.fromhex(hexs.replace(" ", ""))
    print(f"\n=== 0x{op:02x} {'handled' if handled else 'DROPPED'} — {len(reads)} field read(s) ===")
    if not reads:
        print("   (the client read NOTHING out of this body)")
    prev_end = None
    for off, width, val in reads:
        gap = ""
        if prev_end is not None and off > prev_end:
            gap = f"   <-- {off - prev_end} byte(s) skipped (string payload / unread field)"
        kind = {1: "u8 ", 2: "u16", 4: "u32"}[width]
        raw = body[off:off + width].hex() if off + width <= len(body) else "??"
        print(f"   @{off:<4} {kind} = {val:<10} (0x{val:x}){' ' * 2}[{raw}]{gap}")
        prev_end = max(prev_end or 0, off + width)
    if p.get("callers"):
        pretty = ", ".join(f"{k}x{v}" for k, v in sorted(p["callers"].items(), key=lambda kv: -kv[1]))
        print(f"   read by: {pretty}    (disassemble these for the real grammar)")
    print(f"   body: {hexs[:200]}")
    out.write(json.dumps(p) + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--attach", action="store_true")
    ap.add_argument("--client")
    ap.add_argument("--op", help="comma-separated hex opcodes, e.g. 39,33")
    a = ap.parse_args()

    want = None
    if a.op:
        want = {int(x, 16) for x in a.op.replace("0x", "").split(",")}
    client = Path(a.client) if a.client else CLIENT5
    require(client, "5.33 client install", "P1998_CLIENT5")

    out = open(OUT, "a", encoding="utf-8", buffering=1)

    def on_message(msg, _data):
        if msg.get("type") == "error":
            print("[ERROR]", msg.get("description", msg))
            return
        p = msg.get("payload", {})
        if p.get("t") == "info":
            print("[probe]", p.get("m", ""))
        elif p.get("t") == "win":
            # A window switch closes whatever screen is open. `op` is the packet being dispatched at
            # the time (-1 = none, i.e. the local UI did it), which is the answer to "what closed my
            # status screen".
            src = f"opcode 0x{p['op']:02x}" if p.get("op", -1) >= 0 else "local UI (no packet)"
            print(f"[window] switch to index {p['idx']}  <- {src}   ra={p.get('ra')}")
            out.write(json.dumps(p) + "\n")
        elif p.get("t") == "mv":
            src = f"opcode 0x{p['op']:02x}" if p.get("op", -1) >= 0 else "no packet in flight"
            print(f"[move-commit] sub_4857c0 fired  <- {src}   bt: {p.get('m')}")
            out.write(json.dumps(p) + "\n")
        elif p.get("t") == "notify":
            names = {1:"nation",2:"totem",4:"level",5:"might",6:"grace",7:"will",
                     8:"hp/maxhp",9:"mp/maxmp",0xa:"exp",0xb:"coins"}
            i = p.get("idx")
            print(f"[stat-notify] widget {i} ({names.get(i,'?')}) CHANGED  <- op 0x{p.get('op',-1)&0xff:02x}")
            out.write(json.dumps(p) + "\n")
        elif p.get("t") == "stattail":
            print(f"[stat-tail] sub_4d9f00 (unconditional)  <- op 0x{p.get('op',-1)&0xff:02x}")
            out.write(json.dumps(p) + "\n")
        elif p.get("t") == "req05":
            src = f"opcode 0x{p['op']:02x}" if p.get("op", -1) >= 0 else "no packet in flight"
            print(f"[map-rerequest] client sent 0x05  <- {src}   bt: {p.get('m')}")
            out.write(json.dumps(p) + "\n")
        elif p.get("t") == "pkt":
            report(p, out)

    if a.attach:
        session = frida.attach("NexusTK.exe")
        pid = None
    else:
        exe = str(require(client / "NexusTK.exe", "5.33 client exe", "P1998_CLIENT5"))
        pid = frida.spawn([exe], cwd=str(client))
        session = frida.attach(pid)
    script = session.create_script(build_js(want))
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid)
    print(f"[probe] client: {client}; appending to {OUT}. Ctrl-C to stop.")
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
