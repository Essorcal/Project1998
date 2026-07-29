#!/usr/bin/env python
"""
LIVE decoded-packet view for the 7.5.2.0 client, by hooking its internal decrypt routine
(+0x178b20, found via key xref). Every incoming packet is dumped as PLAINTEXT
(opcode + body) with an opcode label — no cipher reconstruction needed. Also appended to
re/decoded_live.jsonl for offline combat mining.

decrypt(this=ecx, src=arg0, len=arg1, out=arg2) -> returns plaintext length; out[0]=opcode.

Usage: python re/frida_decode_live.py --attach     (then play / fight)
Filter combat live, e.g.:  python re/frida_decode_live.py --attach --only 13,08,02,07
"""
import sys, os, json, time, frida

MOD = "NexusTK.exe"
DEC_RVA = 0x178b20
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "decoded_live.jsonl")

JS = r"""
'use strict';
const MAIN = (Process.findModuleByName && Process.findModuleByName('__MOD__')) || Process.enumerateModules()[0];
const dec = MAIN.base.add(__RVA__);
Interceptor.attach(dec, {
  onEnter(args){ this.out = args[2]; this.len = args[1].toInt32(); },
  onLeave(ret){
    try{
      let n = ret.toInt32(); if(n<=0) return; if(n>2048) n=2048;
      const b = new Uint8Array(this.out.readByteArray(n));
      const hex = Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
      send({t:'pkt', ts:Date.now(), op:b[0], n:n, hex:hex});
    }catch(e){}
  }
});
send({t:'info', m:'decrypt hook installed @ +0x__RVAHEX__'});
""".replace("__MOD__", MOD).replace("__RVA__", hex(DEC_RVA)).replace("__RVAHEX__", format(DEC_RVA, "x"))

LABEL = {0x02: "kill/exp text", 0x04: "coords", 0x07: "spawn", 0x08: "vitals", 0x0a: "sys-text",
         0x0c: "move", 0x0e: "remove", 0x0f: "cast/item", 0x11: "turn", 0x13: "mob-HP/stat",
         0x15: "enter-map", 0x17: "spells?", 0x19: "?", 0x1a: "heartbeat?", 0x1e: "?",
         0x20: "?", 0x33: "self-entity", 0x39: "self-profile"}


def main():
    if "--attach" not in sys.argv:
        print("run with --attach"); return
    only = None
    if "--only" in sys.argv:
        only = {int(x, 16) for x in sys.argv[sys.argv.index("--only") + 1].split(",")}
    seconds = None
    if "--seconds" in sys.argv:
        seconds = int(sys.argv[sys.argv.index("--seconds") + 1])
    quiet = "--quiet" in sys.argv
    append = "--append" in sys.argv     # keep prior captures (build a per-level table over sessions)

    dev = frida.get_local_device()
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
    if not procs:
        print("no running", MOD); return
    outf = open(OUT, "a" if append else "w", encoding="utf-8", buffering=1)
    t0 = [None]

    def on_message(msg, data):
        if msg["type"] == "error":
            print("[frida-error]", msg.get("description")); return
        p = msg["payload"]
        if p.get("t") == "info":
            print("[i]", p["m"]); return
        op = p["op"]
        outf.write(json.dumps({"ts": p["ts"], "op": op, "n": p["n"], "hex": p["hex"]}) + "\n")
        if only and op not in only:
            return
        if t0[0] is None:
            t0[0] = p["ts"]
        if quiet:
            return
        b = bytes(int(x, 16) for x in p["hex"].split())
        asc = "".join(chr(c) if 32 <= c < 127 else "." for c in b)
        rel = (p["ts"] - t0[0]) / 1000
        print(f"+{rel:6.2f}s op=0x{op:02x} {LABEL.get(op,''):14s} n={p['n']:3d}  {p['hex'][:80]}  |{asc[:26]}|")

    print(f"hooking decrypt in {[p.pid for p in procs]} -> logging to {OUT}")
    for p in procs:
        try:
            s = dev.attach(p.pid); sc = s.create_script(JS)
            sc.on("message", on_message); sc.load()
        except Exception as e:
            print(f"  attach {p.pid} failed: {e}")
    if seconds is not None:
        print(f"capturing for {seconds}s — PLAY / FIGHT. Logging to {OUT}")
        import time
        end = seconds
        while end > 0:
            time.sleep(min(30, end)); end -= 30
            print(f"  [capture] {os.path.getsize(OUT)} bytes logged so far")
    else:
        print("PLAY / FIGHT now. Ctrl-C when done.\n")
        try:
            sys.stdin.read()
        except KeyboardInterrupt:
            pass
    outf.close()
    print(f"\nsaved -> {OUT}")


if __name__ == "__main__":
    main()
