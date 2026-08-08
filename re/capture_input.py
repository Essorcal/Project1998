"""Watch what the HUMAN does, and what the client SENDS because of it.

Purpose: some actions (viewing an item to read its damage range, e.g. `1m20` / `15m50`)
are only reachable through the UI, and the request format can't be guessed. Demonstrate
the action once with this running and the bot can reproduce it forever -- it already owns
the client's send function, so it only needs to learn the bytes.

Three streams, timestamped so they line up:
  INPUT  keyboard + mouse, read from the client's own message pump (PeekMessageW/GetMessageW)
  SEND   PLAINTEXT outgoing packets, hooked at the client's send fn 0x576660 -- the wire
         copy is encrypted, so hooking WSASend shows only the opcode byte and a garbled body
  RECV   decoded replies, with item-info (0x0f) parsed to show the stat text

Usage:  python capture_input.py [seconds]
"""
import sys, time
sys.path.insert(0, ".")
import nexus_bot as NB
import pull_all as PA
import frida

SECS = int(sys.argv[1]) if len(sys.argv) > 1 else 240
# opt-in only: hooking the message pump crashed the live client (see JS comment)
HOTHOOKS = "--input" in sys.argv

VK = {0x08: "BACK", 0x09: "TAB", 0x0D: "ENTER", 0x10: "SHIFT", 0x11: "CTRL", 0x12: "ALT",
      0x1B: "ESC", 0x20: "SPACE", 0x25: "LEFT", 0x26: "UP", 0x27: "RIGHT", 0x28: "DOWN"}
MSGS = {0x0100: "KEYDOWN", 0x0101: "KEYUP", 0x0102: "CHAR",
        0x0201: "LBUTTONDOWN", 0x0202: "LBUTTONUP", 0x0203: "LDBLCLK",
        0x0204: "RBUTTONDOWN", 0x0205: "RBUTTONUP"}

JS = r"""
'use strict';
const WANT = new Set([0x0100,0x0101,0x0102,0x0201,0x0202,0x0203,0x0204,0x0205]);

// Frida 17 removed Module.findExportByName -- calling it throws at load time, which was
// silently swallowed and left this capture recording nothing. Resolve version-safely.
function resolve(mod, name){
  try{ return Process.getModuleByName(mod).findExportByName(name); }catch(e){}
  try{ return Module.getExportByName(mod, name); }catch(e){}
  try{ return Module.findExportByName(mod, name); }catch(e){}
  return null;
}

function hookMsg(name){
  const a = resolve('user32.dll', name);
  if (!a) return;
  Interceptor.attach(a, {
    onEnter(args){ this.msg = args[0]; },
    onLeave(ret){
      try{
        if (ret.toInt32() === 0) return;                 // no message retrieved
        const m = this.msg;
        const message = m.add(4).readU32();
        if (!WANT.has(message)) return;                  // filter INSIDE the client: cheap
        send({k:'input', msg:message, w:m.add(8).readU32(), l:m.add(12).readU32(),
              x:m.add(20).readS32(), y:m.add(24).readS32(), ts:Date.now()});
      }catch(e){}
    }
  });
}
// The message pump is a VERY hot path (thousands of calls/sec in a game loop) and an
// Interceptor on it costs on every call, filter or not -- running these crashed the live
// client. They are OFF by default; the SEND stream below is what actually matters, and it
// fires only on real sends (~12 per 4s of movement vs ~148 message-pump hits).
if (__HOTHOOKS__){
  hookMsg('PeekMessageW'); hookMsg('GetMessageW');
  hookMsg('PeekMessageA'); hookMsg('GetMessageA');
}

// PLAINTEXT egress: the client frames+encrypts inside this call, so onEnter is the only
// place the real payload is visible.
try{
  Interceptor.attach(ptr('0x576660'), {
    onEnter(args){
      try{
        let n = args[1].toInt32(); if (n <= 0 || n > 512) return;
        const b = new Uint8Array(args[0].readByteArray(n));
        send({k:'send', ts:Date.now(),
              hex:Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ')});
      }catch(e){}
    }
  });
}catch(e){}
"""


def main():
    dev = frida.get_local_device()
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == "nexustk.exe"]
    if not procs:
        print("no live client"); return
    pid = procs[0].pid

    t0 = time.time()

    def rel():
        return f"{time.time() - t0:7.2f}s"

    # --- stream 1+2: input and plaintext sends ---
    def on_input(msg, data):
        if msg.get("type") == "error":
            print(f"!! SCRIPT ERROR: {msg.get('description')}")
            return
        if msg.get("type") != "send":
            return
        p = msg["payload"]
        k = p.get("k")
        if k == "input":
            name = MSGS.get(p["msg"], hex(p["msg"]))
            if p["msg"] in (0x0100, 0x0101, 0x0102):
                w = p["w"]
                key = VK.get(w) or (chr(w) if 32 <= w < 127 else hex(w))
                print(f"{rel()} INPUT  {name:<12} key={key!r}")
            else:
                print(f"{rel()} INPUT  {name:<12} at ({p['x']},{p['y']})")
        elif k == "send":
            hx = p["hex"]
            op = hx.split()[0] if hx else "??"
            print(f"{rel()} SEND   op=0x{op}  {hx[:90]}")

    sess = dev.attach(pid)
    sc = sess.create_script(JS.replace("__HOTHOOKS__", "true" if HOTHOOKS else "false"))
    sc.on("message", on_input)
    sc.load()

    # --- stream 3: replies (item info especially) ---
    def on_recv(msg, data):
        if msg.get("type") == "error":
            print(f"!! RECV SCRIPT ERROR: {msg.get('description')}")
            return
        if msg.get("type") != "send":
            return
        p = msg["payload"]
        op = p.get("op")
        if op == 0x0f:
            d = bytes(int(x, 16) for x in p["hex"].split())
            it = PA.parse_item(d)
            if it:
                print(f"{rel()} RECV   ITEM {it['name']!r}  stat_text={it['stat_text']!r}")
        elif op == 0x39:
            print(f"{rel()} RECV   PROFILE 0x39 len={p.get('n')}")
        elif op == 0x08 and p.get("hex"):
            d = bytes(int(x, 16) for x in p["hex"].split())
            if len(d) > 1 and d[1] in (0x58, 0x59, 0x78, 0x79):
                print(f"{rel()} RECV   STATBLOCK sub=0x{d[1]:02x}")

    s2, sc2 = NB.attach(on_recv)

    print(f"capturing {SECS}s -- go ahead and demonstrate (view an item, open profile, etc).")
    print(f"keyboard/mouse stream: {'ON (--input, RISKY)' if HOTHOOKS else 'off (safe); packets still captured'}")
    print("Legend: INPUT = what you pressed/clicked, SEND = plaintext packet it produced,")
    print("        RECV = what the server sent back.\n")
    try:
        time.sleep(SECS)
    except KeyboardInterrupt:
        pass
    sess.detach(); s2.detach()
    print("\ndone.")


if __name__ == "__main__":
    main()
