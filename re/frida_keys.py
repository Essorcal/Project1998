#!/usr/bin/env python
"""
Key-dispatch probe for NexusTK 4.95 (NexusTK_local.exe).

Answers: "when I press a single-letter hotkey (m/i/b/...), what does the client do?"

Static RE established the input path:
  WndProc(0x403502) -> WM_KEYDOWN branch(0x403789)
     -> only if no focused text-field/dialog eats the key (gate call 0x43a6f0)
     -> per-mode keydown entry  0x41e9f0 (cdecl: arg0 = VK code, arg1 = lParam)
     -> packages a type-0x0d "keydown" event and ENQUEUES it (0x485940 -> 0x46f1f0)
        for the active game screen to consume in the main loop.

So EVERY letter hotkey that reaches the world (no dialog focused) passes through
0x41e9f0. This probe logs each one, and also logs the event-enqueue, so you can compare
a KNOWN-WORKING key (i = Inventory, b = Bulletin) against 'm' (Mail) and 'p' (Post).

If 'm' shows up at 0x41e9f0 + the enqueue exactly like 'i'/'b' do, the keystroke IS
delivered to the game — meaning the active screen simply has no action bound to it
(a dead key), not that the key is being swallowed earlier.

Usage (client already running):
    python re/frida_keys.py --attach
Or spawn it:
    python re/frida_keys.py
Then, with your character standing in the world (no dialog open), press: i  b  m  p  o
"""
import sys, os, frida
from _paths import CLIENT

EXE = str(CLIENT / "NexusTK_local.exe")

RVA_KEYDOWN = 0x01e9f0   # 0x41e9f0  per-mode keydown entry (arg0=VK, arg1=lParam)
RVA_ENQUEUE = 0x085940   # 0x485940  event router: (this=ecx, evtType, key&0xff, lParam)

VK_NAMES = {0x08:"BACK",0x09:"TAB",0x0D:"ENTER",0x10:"SHIFT",0x11:"CTRL",0x12:"ALT",
            0x1B:"ESC",0x20:"SPACE",0x25:"LEFT",0x26:"UP",0x27:"RIGHT",0x28:"DOWN",
            0x70:"F1",0x71:"F2",0x72:"F3",0xBF:"OEM_2(/?)"}

def vk_label(vk):
    if 0x41 <= vk <= 0x5A:   # A-Z virtual-key codes
        return f"'{chr(vk).lower()}'  (VK 0x{vk:02x})"
    if 0x30 <= vk <= 0x39:
        return f"'{chr(vk)}'  (VK 0x{vk:02x})"
    return f"{VK_NAMES.get(vk, '?')}  (VK 0x{vk:02x})"

JS = """
function baseOf(name){
  // Frida 17 removed Module.findBaseAddress; support old + new APIs.
  try { if (typeof Module.findBaseAddress === 'function') return Module.findBaseAddress(name); } catch(e){}
  try { return Process.getModuleByName(name).base; } catch(e){}
  try { return Module.getBaseAddress(name); } catch(e){}
  return null;
}
const base = baseOf('NexusTK_local.exe');
send({t:'info', m:'base=' + base});

// --- per-mode keydown entry: arg0 = VK code, arg1 = lParam (cdecl, ret 8) ---
Interceptor.attach(base.add(%d), {
  onEnter(args) {
    const sp = this.context.esp;
    const vk = sp.add(4).readU32();
    const lp = sp.add(8).readU32();
    send({t:'keydown', vk:vk, lp:lp});
  }
});

// --- event router (fires for the keydown event that gets queued to the screen) ---
Interceptor.attach(base.add(%d), {
  onEnter(args) {
    const sp = this.context.esp;
    const evt = sp.add(4).readU32();   // event type (0x0d = keydown)
    const key = sp.add(8).readU32();   // key & 0xff
    if (evt === 0x0d) send({t:'enqueue', key:key});
  }
});
""" % (RVA_KEYDOWN, RVA_ENQUEUE)

def on_message(msg, data):
    if msg.get("type") == "error":
        print("[frida-error]", msg.get("description")); return
    p = msg.get("payload", {})
    t = p.get("t")
    if t == "info":
        print("[i]", p["m"])
    elif t == "keydown":
        print(f"  KEYDOWN  reached game dispatcher (0x41e9f0):  {vk_label(p['vk'])}")
    elif t == "enqueue":
        k = p["key"]
        print(f"     -> queued as keydown event to active screen (key=0x{k:02x})")

def main():
    attach = "--attach" in sys.argv
    dev = frida.get_local_device()
    if attach:
        pid = dev.get_process("NexusTK_local.exe").pid
        session = dev.attach(pid)
    else:
        pid = dev.spawn([EXE])
        session = dev.attach(pid)
    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    if not attach:
        dev.resume(pid)
    print("Attached. Stand in the world (no dialog open) and press:  i  b  m  p  o")
    print("Compare: 'i'/'b' are known-working keys; 'm'/'p' are the Mail/Post keys in question.\n")
    sys.stdin.read()

if __name__ == "__main__":
    main()
