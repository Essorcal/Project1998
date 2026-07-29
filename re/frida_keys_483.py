#!/usr/bin/env python
"""
Key-dispatch probe for NexusTK 4.83 (KRU\\NexusTK483\\NexusTK.exe) -- the 2000-10-12 build.

Sibling of re/frida_keys.py (which does the same for 4.95). Answers: "when I press a
single-letter hotkey (m/i/b/...) in the 4.83 client, does anything actually fire?"

STATIC RE (done 2026-07-28, offline, no client boot needed) already established the 4.83
input path and it is byte-for-byte the same GENERATION as 4.95, only shifted in address:

  per-mode keydown wrapper  0x41f220   (arg0 = VK, arg1 = lParam; masks VK & 0xff,
                                        pushes a type-0x0d "keydown" event)
     -> event router         0x480b20  (this=ecx; args: evtType, key&0xff, lParam)
                                        packs {type,a,b} into a 24-byte event, enqueues
                                        via 0x46aac0, signals the consumer.
  (4.95 analogues: wrapper 0x41e9f0, router 0x485940 -- identical shape.)

KEY STATIC FINDING for mail:
  * VK 0x4D ('m') is NEVER used as a key comparison anywhere in the 4.83 binary. Its only
    immediate uses are `push 0x4d` / `mov [struct],0x4d` (the NUMBER 77, unrelated to input).
  * The only per-mode letter dispatch table (switch @0x490d13, byte-index tbl 0x490d8c,
    jump tbl 0x490d80) routes ONLY A / D / numpad to real handlers (movement); every other
    letter -- i, b, m included -- falls through to the shared default case. This switch is
    byte-identical to 4.95's (@0x4959d3).
  => Statically, 4.83's 'm' looks just as unwired as 4.95's. The '?' help line "m = mailbox"
     comes from a string in NexusTK.dat, not from any code that binds the key.

THIS PROBE confirms it live: it logs every key that reaches the wrapper + the enqueue, so you
can directly compare 'm' against other help-advertised keys. If 'm' enqueues exactly like the
others and nothing downstream happens, it's a dead key (help string only).

Usage (client must be running and, ideally, in-world so no dialog eats the key):
    python re/frida_keys_483.py --attach
Or spawn it (needs the dat redirect applied first so it can reach the local server):
    python re/frida_keys_483.py
Then press:  i  b  m  p  o  s   and watch which ones do more than just enqueue.
"""
import sys, frida

EXE = r"C:\Program Files (x86)\KRU\NexusTK483\NexusTK.exe"

RVA_KEYDOWN = 0x11f220   # 0x41f220  per-mode keydown wrapper (arg0=VK, arg1=lParam)
RVA_ROUTER  = 0x080b20   # 0x480b20  event router: (this=ecx, evtType, key&0xff, lParam)

# NOTE: key->action dispatch is ASYNC (the queued type-0x0d event is consumed later by the
# active screen's handler, a vtable call), so hooking a single "dispatcher" is unreliable.
# Instead we use a build-independent POSITIVE CONTROL: real server actions (attack, pick up,
# cast, etc.) call WSOCK32!send/sendto. A dead key sends nothing AND opens nothing.
SEND_DLL = "WSOCK32.dll"     # 4.83 links the old wsock32, not ws2_32

VK_NAMES = {0x08:"BACK",0x09:"TAB",0x0D:"ENTER",0x10:"SHIFT",0x11:"CTRL",0x12:"ALT",
            0x1B:"ESC",0x20:"SPACE",0x25:"LEFT",0x26:"UP",0x27:"RIGHT",0x28:"DOWN",
            0x70:"F1",0x71:"F2",0x72:"F3",0xBF:"OEM_2(/?)"}

def vk_label(vk):
    if 0x41 <= vk <= 0x5A: return f"'{chr(vk).lower()}'  (VK 0x{vk:02x})"
    if 0x30 <= vk <= 0x39: return f"'{chr(vk)}'  (VK 0x{vk:02x})"
    return f"{VK_NAMES.get(vk, '?')}  (VK 0x{vk:02x})"

JS = """
function baseOf(name){
  // Frida 17 removed Module.findBaseAddress; support old + new APIs.
  try { if (typeof Module.findBaseAddress === 'function') return Module.findBaseAddress(name); } catch(e){}
  try { return Process.getModuleByName(name).base; } catch(e){}
  try { return Module.getBaseAddress(name); } catch(e){}
  return null;
}
function expOf(mod, fn){
  try { return Module.getExportByName(mod, fn); } catch(e){}
  try { return Process.getModuleByName(mod).getExportByName(fn); } catch(e){}
  return null;
}
const base = baseOf('NexusTK.exe');
send({t:'info', m:'base=' + base});

// event router: [esp+4]=evtType, [esp+8]=key&0xff  (only report keydown events) -- the MARKER
Interceptor.attach(base.add(%d), {
  onEnter() {
    const sp = this.context.esp;
    if (sp.add(4).readU32() === 0x0d) send({t:'key', vk: sp.add(8).readU32()});
  }
});

// WSOCK32 send/sendto -- the POSITIVE CONTROL: any outbound game packet.
// stdcall: send(SOCKET s, char* buf, int len, int flags) -> [esp+8]=buf, [esp+0xc]=len
['send','sendto'].forEach(function(fn){
  const p = expOf('%s', fn);
  if (!p) { send({t:'info', m:'WARN: no export '+fn}); return; }
  Interceptor.attach(p, {
    onEnter(args) {
      const sp = this.context.esp;
      const len = sp.add(0x0c).readU32() | 0;
      const buf = sp.add(0x08).readPointer();
      let head = '';
      try { const n = Math.min(len,8); for (let i=0;i<n;i++){ head += ('0'+buf.add(i).readU8().toString(16)).slice(-2)+' '; } } catch(e){}
      send({t:'send', fn:fn, len:len, head:head.trim()});
    }
  });
});
send({t:'info', m:'hooks installed'});
""" % (RVA_ROUTER, SEND_DLL)

def on_message(msg, data):
    if msg.get("type") == "error":
        print("[frida-error]", msg.get("description")); return
    p = msg.get("payload", {}); t = p.get("t")
    if t == "info":  print("[i]", p["m"])
    elif t == "key": print(f"\n>>> KEY {vk_label(p['vk'])} enqueued")
    elif t == "send": print(f"    [SEND {p['fn']}] len={p['len']:<4} bytes: {p['head']}   <-- a server action fired")

def main():
    attach = "--attach" in sys.argv
    dev = frida.get_local_device()
    if attach:
        pid = dev.get_process("NexusTK.exe").pid
        session = dev.attach(pid)
    else:
        pid = dev.spawn([EXE]); session = dev.attach(pid)
    script = session.create_script(JS)
    script.on("message", on_message); script.load()
    if not attach: dev.resume(pid)
    print("Attached to 4.83.  Test protocol (in-world):")
    print("  1) POSITIVE CONTROL: do a server action -- attack a mob, or pick up an item.")
    print("     You should see  >>> KEY ...  immediately followed by  [SEND ...] .")
    print("  2) Then press  m  (the Mail key).  If it only shows  >>> KEY 'm'  with NO [SEND]")
    print("     and no window opens on screen, it is a dead key (help-string only).")
    print("  (Movement/heartbeat may emit periodic [SEND]s on their own -- watch for a send that")
    print("   lands right ON a keypress, not the idle background traffic.)\n")
    sys.stdin.read()

if __name__ == "__main__":
    main()
