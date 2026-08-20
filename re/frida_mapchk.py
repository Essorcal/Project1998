#!/usr/bin/env python
"""
Crack the 4.95 client's VIEW CHECKSUM — the `00 chk(u16BE) 00` trailer on its map-data request (0x05)
and its walk (0x06).

Why it matters: the client sends that checksum on every walk. If the server can compute the same value it
can tell whether the client's cached terrain for the current viewport is already correct and skip streaming
it — a player revisiting a map they have on disk would receive nothing. Today we push strips blindly.

Why a live probe instead of static RE: the checksum is content-derived (it is exactly 0x0000 when the
client's memory-mapped cache is freshly zero-filled, and stable for a repeated identical state), but it
matched none of ~20 common algorithms over the requested rect, so either the covered REGION differs from
the request rect or the algorithm is custom. Both fall out immediately if we capture the client's exact map
array alongside the checksum it produced from it.

What it captures, per outgoing 0x05/0x06:
  * the plaintext packet (rect / walk + the checksum trailer)
  * the map dimensions and the ENTIRE cell array at that instant, straight out of the memory-mapped view

That is complete ground truth: an offline solver can then try every (region, algorithm) pair rather than
guessing. Also grabs one backtrace per opcode so we get the packet BUILDER's address for free.

Test on a SMALL map — the array is dumped per sample, so a 220x220 city is 193 KB a pop. Map 1000
'Koguryo Valley' is 18x25 (1800 B) and is reachable from Kugnae's north edge. Cells are capped at
MAX_CELLS; bigger maps log the rect and checksum but no array (still useful for the region sweep).

Usage (server running, VirtualStore map cache CLEARED so the state is known):
    python re/frida_mapchk.py             # spawn the client under Frida
    python re/frida_mapchk.py --attach    # attach to a running client

Then: log in, walk onto a small map, and walk a dozen steps. Ctrl-C. Feed the JSONL to
re/solve_mapchk.py.
"""
import sys, os, json, base64, frida
from _paths import CLIENT

EXE = str(CLIENT / "NexusTK_local.exe")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mapchk.jsonl")

# RVAs (VA - 0x400000). No ASLR in this binary, but resolve via module base anyway.
RVA_ENCRYPT = 0x078760   # encrypt(src, len, out, this) -- src is PLAINTEXT, so we see the packet as built
RVA_MAPLOAD = 0x04A780   # thiscall: opens Maps\TK%d.map; ECX = the map object we need field offsets off

# Map-object layout, from static RE of the open path (see docs §10.7):
#   +0x3de map id   +0x3e0 width   +0x3e2 height   +0x3e4 file handle
#   +0x3e8 file mapping   +0x3ec MapViewOfFile pointer = the live cell array (4 bytes/cell)
OFF_ID, OFF_W, OFF_H, OFF_CELLS = 0x3de, 0x3e0, 0x3e2, 0x3ec

JS = r"""
// Frida 17 reshuffled the module API; try the variants rather than pinning one.
function modBase(name) {
  try { if (Process.findModuleByName) return Process.findModuleByName(name).base; } catch (e) {}
  try { if (Process.getModuleByName) return Process.getModuleByName(name).base; } catch (e) {}
  try { if (Module.getBaseAddress) return Module.getBaseAddress(name); } catch (e) {}
  const m = Process.enumerateModules().find(m => m.name.toLowerCase() === name.toLowerCase());
  if (!m) throw new Error('module not found: ' + name);
  return m.base;
}
const BASE = modBase('NexusTK_local.exe');
const at = rva => BASE.add(rva);
const RVA_ENCRYPT = %d, RVA_MAPLOAD = %d;
const OFF_ID = %d, OFF_W = %d, OFF_H = %d, OFF_CELLS = %d;
const MAX_CELLS = %d;

let mapObj = null;        // 'this' of the map manager, captured from the map-open call
const seenBt = {};        // one backtrace per opcode is plenty

Interceptor.attach(at(RVA_MAPLOAD), {
  onEnter(args) {
    mapObj = this.context.ecx;      // thiscall
    send({ t: 'mapobj', addr: mapObj.toString() });
  }
});

Interceptor.attach(at(RVA_ENCRYPT), {
  onEnter(args) {
    const src = args[0], len = args[1].toInt32();
    if (len < 2) return;
    const op = src.readU8();
    if (op !== 0x05 && op !== 0x06) return;

    // Plaintext as the client built it. We keep the whole thing rather than parsing here so the
    // offline solver sees exactly what the server saw on the wire.
    const pkt = [];
    for (let i = 0; i < Math.min(len, 24); i++) pkt.push(src.add(i).readU8());

    let bt = null;
    if (!seenBt[op]) {
      seenBt[op] = 1;
      bt = Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 10)
        .map(a => { const off = a.sub(BASE); return (off.compare(0x1000000) < 0) ? ('+0x' + off.toString(16)) : a.toString(); });
    }

    const rec = { t: 'pkt', op: op, len: len, pkt: pkt, bt: bt };

    if (mapObj !== null) {
      try {
        const w = mapObj.add(OFF_W).readU16();
        const h = mapObj.add(OFF_H).readU16();
        const id = mapObj.add(OFF_ID).readU16();
        const cells = mapObj.add(OFF_CELLS).readPointer();
        rec.map = id; rec.w = w; rec.h = h;
        if (!cells.isNull() && w > 0 && h > 0 && (w * h) <= MAX_CELLS) {
          send(rec, cells.readByteArray(w * h * 4));    // array rides as the message payload
          return;
        }
        rec.note = 'array skipped (too big or null)';
      } catch (e) { rec.note = 'map read failed: ' + e; }
    }
    send(rec);
  }
});

send({ t: 'info', m: 'hooks installed: encrypt(0x05/0x06) + mapload' });
""" % (RVA_ENCRYPT, RVA_MAPLOAD, OFF_ID, OFF_W, OFF_H, OFF_CELLS, 8192)


def main():
    attach = "--attach" in sys.argv
    fh = open(OUT, "w", encoding="utf-8")
    n = [0]

    def on_message(msg, data):
        if msg.get("type") == "error":
            print("JS ERROR:", msg.get("description"))
            return
        p = msg.get("payload") or {}
        t = p.get("t")
        if t == "info":
            print("[*]", p["m"])
        elif t == "mapobj":
            print(f"[*] map object @ {p['addr']}")
        elif t == "pkt":
            body = p["pkt"][1:]
            chk = None
            # 6-byte payload then `00 chk(u16BE) 00` -- see docs §10.7
            if len(body) >= 10:
                chk = (body[7] << 8) | body[8]
            rec = dict(p)
            if data:
                rec["cells_b64"] = base64.b64encode(data).decode()
            fh.write(json.dumps(rec) + "\n")
            fh.flush()
            n[0] += 1
            tag = "req" if p["op"] == 5 else "walk"
            dims = f" map={p.get('map')} {p.get('w')}x{p.get('h')}" if "map" in p else ""
            arr = f" cells={len(data)}B" if data else " (no array)"
            print(f"[{n[0]:4d}] {tag} " + " ".join(f"{b:02x}" for b in body[:10]) +
                  f"  chk={chk:#06x}" + dims + arr)
            if p.get("bt"):
                print(f"        builder backtrace (op {p['op']:#04x}): {' <- '.join(p['bt'])}")
        else:
            print(p)

    if attach:
        print("attaching to running client...")
        session = frida.attach("NexusTK_local.exe")
        pid = None
    else:
        print(f"spawning {EXE}")
        pid = frida.spawn([EXE])
        session = frida.attach(pid)

    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid)
    print(f"logging to {OUT}   (log in, walk onto a SMALL map, walk around, then Ctrl-C)")
    try:
        sys.stdin.read()
    except KeyboardInterrupt:
        pass
    fh.close()
    print(f"\nwrote {n[0]} sample(s) to {OUT}")


if __name__ == "__main__":
    main()
