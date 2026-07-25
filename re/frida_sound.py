#!/usr/bin/env python
"""Live trace of the NexusTK 4.95 client's SOUND path, to find why 0x19-triggered spell SFX stay silent
(or crash the client).

Differential approach: background MUSIC already works (0x19 type 2 -> MIDI) and flows through the SAME
low-level play fn 0x4798c0 as sound effects. Hooks:

  * 0x450ad0  the 0x19 handler     -> log the exact packet bytes the client receives (type + fields)
  * 0x4798c0  the low-level play fn -> log (soundId, type) args + gate flags [mgr+8]/[mgr+0x274] + return

Plus a CRASH CATCHER: if the client faults (e.g. while parsing our sound packet), log the faulting
instruction (pc/rva) + backtrace instead of dying silently — that pinpoints a malformed-packet bug.

Read it live: entering the world should fire PLAY with type=2 (music) + HEALTHY flags; then cast a spell and
watch whether PLAY fires, with what type, whether the gates are set, or whether a CRASH is logged.

Usage (server running; client will be spawned fresh so we catch the music baseline):
    python re/frida_sound.py
  or attach to an already-running client:
    python re/frida_sound.py --attach
"""
import sys, os, frida

EXE = r"C:\Program Files (x86)\Nexon\NextAeon\NexusTK_local.exe"
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sound_log.txt")

# RVAs (VA - 0x400000) from static RE of NexusTK_local.exe.
H19     = 0x050ad0   # 0x450ad0  the 0x19 (music/sound) handler; arg0 = decrypted packet body ptr (starts at opcode)
PLAY    = 0x0798c0   # 0x4798c0  low-level audio play fn: thiscall(ecx=mgr), arg0=soundId, arg1=type
DECRYPT = 0x078680   # 0x478680  decrypt(src, len, out, this); src[0]=opcode, out=decrypted body

JS = r"""
const MOD = 'NexusTK_local.exe';
let base = null;
{ const m = Process.findModuleByName(MOD); base = m ? m.base : Process.enumerateModules()[0].base; }
send({t:'info', m:'module base = ' + base});
function at(rva) { return base.add(rva); }
function hex(p, n) {
  try { return Array.from(new Uint8Array(p.readByteArray(n)))
      .map(b => ('0'+(b&0xff).toString(16)).slice(-2)).join(' '); } catch(e) { return '<unreadable>'; }
}
function tryHook(rva, name, cbs) {
  try { Interceptor.attach(at(rva), cbs); send({t:'info', m:'hooked '+name+' @ rva 0x'+rva.toString(16)}); }
  catch (e) { send({t:'info', m:'HOOK FAILED '+name+': '+e}); }
}

// ---- decrypt: prove whether a 0x19 packet actually ARRIVES (independent of the handler) ----
tryHook(RVA_DECRYPT, 'decrypt', {
  onEnter(args) { this.src = args[0]; this.len = args[1].toInt32(); this.out = args[2]; },
  onLeave(ret) {
    let op = -1; try { op = this.src.readU8(); } catch(e){}
    if (op === 0x19) send({t:'RECV19', m:'0x19 ARRIVED ('+this.len+'B): '+hex(this.out, Math.min(this.len, 40))});
  }
});

// ---- 0x19 handler: what packet did the client actually receive? ----
tryHook(RVA_H19, 'h19', {
  onEnter(args) {
    const body = args[0];                 // edi = [ebp+8] = packet body (starts at opcode 0x19)
    let type = -1; try { type = body.add(1).readU8(); } catch(e){}
    send({t:'H19', m:'0x19 handler: type='+type+'  body[0..23]= '+hex(body, 24)});
  }
});

// ---- low-level play fn: does it fire, with what, and do the gate flags block it? ----
tryHook(RVA_PLAY, 'play', {
  onEnter(args) {
    const mgr = this.context.ecx;         // thiscall: ecx = sound manager
    let sid=-1, typ=-1, f8=-1, f274=-1;
    try { sid = args[0].toInt32(); } catch(e){}
    try { typ = args[1].toInt32(); } catch(e){}
    try { f8   = mgr.add(0x8).readU8(); }   catch(e){}
    try { f274 = mgr.add(0x274).readU8(); } catch(e){}
    this.sid=sid; this.typ=typ;
    send({t:'PLAY', m:'play fn: soundId='+sid+' type='+typ+'  gate[mgr+8]='+f8+' gate[mgr+0x274]='+f274});
  },
  onLeave(ret) { send({t:'PLAY', m:'   -> returned '+ret+' (soundId='+this.sid+' type='+this.typ+')'}); }
});

// ---- crash catcher: log any client fault (pc/rva + backtrace) instead of dying silently ----
Process.setExceptionHandler(function (details) {
  try {
    const pc = details.context.pc, rva = pc.sub(base);
    let bt = '';
    try {
      bt = Thread.backtrace(details.context, Backtracer.ACCURATE)
        .map(a => { const r = a.sub(base); return '0x'+a.toString(16) +
          (r.compare(0)>=0 && r.compare(ptr(0x400000))<0 ? ' (rva 0x'+r.toString(16)+')' : ''); })
        .slice(0, 14).join('\n        ');
    } catch(e) { bt = '<no backtrace>'; }
    send({t:'CRASH', m:'*** '+details.type+' @ pc=0x'+pc.toString(16)+'  rva=0x'+rva.toString(16)+
      '  access='+(details.memory ? (details.memory.operation+' '+details.memory.address) : '?')+
      '\n     backtrace:\n        '+bt});
  } catch(e) { send({t:'CRASH', m:'exception in handler: '+e}); }
  return false;   // let it proceed; we just logged the location
});
""".replace("RVA_DECRYPT", "0x%x" % DECRYPT).replace("RVA_H19", "0x%x" % H19).replace("RVA_PLAY", "0x%x" % PLAY)


def main():
    attach = "--attach" in sys.argv
    logf = open(LOG, "w", encoding="utf-8")
    def out(s):
        print(s); logf.write(s + "\n"); logf.flush()

    def on_message(msg, data):
        if msg.get("type") == "error":
            out("JS-ERROR: " + msg.get("description", str(msg))); return
        p = msg.get("payload", {})
        t = p.get("t")
        if   t == "info":   out("· " + p["m"])
        elif t == "RECV19": out("RECV19 " + p["m"])
        elif t == "H19":   out("H19   " + p["m"])
        elif t == "PLAY":  out("PLAY  " + p["m"])
        elif t == "CRASH": out("!!!!! " + p["m"])
        else:              out(str(p))

    try:
        if attach:
            out("attaching to running client...")
            session = frida.attach("NexusTK_local.exe"); pid = None
        else:
            out("spawning " + EXE)
            pid = frida.spawn([EXE]); session = frida.attach(pid)
    except Exception as e:
        out("FRIDA ATTACH/SPAWN FAILED: " + repr(e)); return

    script = session.create_script(JS)
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid); out("client resumed — log in, enter the world (music = PLAY type=2), then cast")
    out("logging to " + LOG + "  (Ctrl-C to stop)")
    try: sys.stdin.read()
    except KeyboardInterrupt: pass
    out("detaching")


if __name__ == "__main__":
    main()
