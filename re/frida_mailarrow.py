#!/usr/bin/env python
"""
Mail/parcel HUD-arrow probe for NexusTK 4.95 (NexusTK_local.exe).

WHAT WE PROVED STATICALLY (2026-07-28):
  * The client SHIPS the assets: MAIL.EPF (arrow), PARCEL.EPF (bag), CONNSTAT.EPF (ping creatures)
    are named entries in Inter.dat and referenced by name in the exe.
  * There is a dedicated mail/parcel HUD WIDGET (C++ object, vtable 0x4ce440, ctor 0x47a230). Its
    render method 0x469480 loads MAIL.EPF/PARCEL.EPF and draws:
        [this+0x105] = parcel count  (draws that many PARCEL.EPF bag frames)
        [this+0x106] = hasMail flag   (nonzero -> draws the MAIL.EPF arrow)
        [this+0x104], [this+0x108] = related state
    Setter SetHasMail(byte) @0x47a2c0 writes [this+0x106].
  * BUT every SetHasMail caller we found is UI construction / the mailbox-dialog builder -- we did NOT
    find code that sets hasMail from a received 0x08 stats-packet byte. So the open question is whether
    the widget is network-wired in 4.95 at all.

WHAT THIS PROBE DOES:
  Hooks the widget render 0x469480 and dumps [this+0x104..0x108] whenever the arrow/bag WOULD show
  (i.e. any of those bytes nonzero). Run it, then from the server sweep 0x08 packets with the flag byte
  set at candidate offsets (!mailflag <off>). If a byte flips to nonzero, the widget IS network-driven
  and you've found the wiring + the offset. If it NEVER flips no matter what the server sends, the arrow
  is present-but-unwired in 4.95 (assets + widget compiled in, no packet path) -- same story as the 'm'
  key. Either way this is the decisive test.

  Also hooks SetHasMail 0x47a2c0 to log every call + its argument + caller (so we see who sets it live).

Usage (client running, in-world):
    python re/frida_mailarrow.py --attach
"""
import sys, frida

EXE = "NexusTK_local.exe"
RVA_RENDER     = 0x069480   # 0x469480 mail/parcel widget render (this=ecx/esi)
RVA_SETHASMAIL = 0x07a2c0   # 0x47a2c0 SetHasMail(byte) -> [this+0x106]

JS = """
function baseOf(n){
  try { if (typeof Module.findBaseAddress==='function') return Module.findBaseAddress(n); } catch(e){}
  try { return Process.getModuleByName(n).base; } catch(e){}
  try { return Module.getBaseAddress(n); } catch(e){}
  return null;
}
const base = baseOf('%s');
send({t:'info', m:'base='+base});

// widget render: this = ecx (thiscall). Dump state bytes when the arrow/bag would draw.
Interceptor.attach(base.add(%d), {
  onEnter() {
    const self = this.context.ecx;
    try {
      const b104 = self.add(0x104).readU8();
      const b105 = self.add(0x105).readU8();   // parcel count
      const b106 = self.add(0x106).readU8();   // hasMail
      const b108 = self.add(0x108).readU8();
      if (b104 || b105 || b106 || b108) {
        send({t:'draw', self:self.toString(), b104:b104, b105:b105, b106:b106, b108:b108});
      }
    } catch(e){}
  }
});

// SetHasMail(byte) -> [this+0x106]. Log arg + return address (caller).
Interceptor.attach(base.add(%d), {
  onEnter(args) {
    const arg = this.context.esp.add(4).readU8();     // stack arg (ret 4)
    const ret = this.context.esp.readU32();           // return address = caller
    send({t:'set', arg:arg, caller:'0x'+ret.toString(16)});
  }
});
send({t:'info', m:'hooks installed on render 0x469480 + SetHasMail 0x47a2c0'});
""" % (EXE, RVA_RENDER, RVA_SETHASMAIL)

def on_message(msg, data):
    if msg.get("type")=="error":
        print("[frida-error]", msg.get("description")); return
    p=msg.get("payload",{}); t=p.get("t")
    if t=="info": print("[i]", p["m"])
    elif t=="draw":
        print(f"  RENDER {p['self']}: +104={p['b104']} parcelCount(+105)={p['b105']} "
              f"hasMail(+106)={p['b106']} +108={p['b108']}"
              + ("   <<< ARROW/BAG WOULD SHOW" if (p['b105'] or p['b106']) else ""))
    elif t=="set":
        print(f"  SetHasMail(arg={p['arg']})  <- caller {p['caller']}")

def main():
    attach = "--attach" in sys.argv
    dev = frida.get_local_device()
    if attach:
        pid = dev.get_process(EXE).pid; session = dev.attach(pid)
    else:
        pid = dev.spawn([r"C:\Program Files (x86)\Nexon\NextAeon\NexusTK_local.exe"]); session = dev.attach(pid)
    script = session.create_script(JS); script.on("message", on_message); script.load()
    if not attach: dev.resume(pid)
    print("Attached. In-world, watch the SetHasMail calls (who sets the flag) and RENDER dumps.")
    print("Then from the server sweep: !mailflag 40 .. !mailflag 57 and see if hasMail(+106) ever flips.\n")
    sys.stdin.read()

if __name__ == "__main__":
    main()
