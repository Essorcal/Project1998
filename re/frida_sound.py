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
from _paths import CLIENT

EXE = str(CLIENT / "NexusTK_local.exe")
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sound_log.txt")

# RVAs (VA - 0x400000) from static RE of NexusTK_local.exe.
H19     = 0x050ad0   # 0x450ad0  the 0x19 (music/sound) handler; arg0 = decrypted packet body ptr (starts at opcode)
PLAY    = 0x0798c0   # 0x4798c0  low-level audio play fn: thiscall(ecx=mgr), arg0=soundId, arg1=type
DECRYPT = 0x078680   # 0x478680  decrypt(src, len, out, this); src[0]=opcode, out=decrypted body
H1A     = 0x0503a0   # 0x4503a0  the 0x1A ACTION handler; body: id(u32)@+1 type(u8)@+5 time(u16)@+6 sound(u8)@+8
GFLAG   = 0x0fd390   # 0x4fd390  global -> ptr; [ptr+0x456]==1 gates whether the action plays its sound byte
SNDOBJ  = 0x063ab0   # 0x463ab0  positional-sound "play" wrapper: thiscall(ecx=obj); switch [obj+0x148] (mode 0..4)
JT      = 0x063c88   #           the 5-dword mode->handler jump table 0x463ab0 dispatches through
PLAY2   = 0x079d20   # 0x479d20  the OTHER low-level play fn (looping/streamed?), reached by one of the modes
CTOR    = 0x063950   # 0x463950  sound-object ctor; stores mode at [obj+0x148], soundId at [obj+0x138]

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

// ---- 0x1A action handler: does our cast (type 6) arrive with a sound byte, and does the gate pass? ----
tryHook(RVA_H1A, 'h1a', {
  onEnter(args) {
    const body = args[0];
    let type=-1, snd=-1, flag=-1;
    try { type = body.add(5).readU8(); } catch(e){}
    try { snd  = body.add(8).readU8(); } catch(e){}
    try { flag = base.add(RVA_GFLAG).readPointer().add(0x456).readU8(); } catch(e){}
    send({t:'H1A', m:'0x1A action: type='+type+' sound(byte8)='+snd+'  gate[+0x456]='+flag+'  body= '+hex(body, 12)});
  }
});

// ---- dump the mode->handler jump table so we know which [obj+0x148] value actually PLAYS ----
try {
  const jt = at(RVA_JT);
  const rows = [];
  for (let i = 0; i < 5; i++) {
    const tgt = jt.add(i*4).readPointer();
    rows.push('mode ' + i + ' -> 0x' + tgt.toString(16) + ' (rva 0x' + tgt.sub(base).toString(16) + ')');
  }
  send({t:'info', m:'MODE jump table [0x463c88]:\n     ' + rows.join('\n     ')});
} catch (e) { send({t:'info', m:'JT dump failed: ' + e}); }

// ---- positional-sound object: what MODE + soundId did our 0x19 packet actually build? ----
tryHook(RVA_SNDOBJ, 'sndobj', {
  onEnter(args) {
    const obj = this.context.ecx;
    let mode=-1, sid=-1, x=-1, y=-1;
    try { mode = obj.add(0x148).readS32(); } catch(e){}
    try { sid  = obj.add(0x138).readS32(); } catch(e){}
    try { x    = obj.add(0x130).readS32(); } catch(e){}
    try { y    = obj.add(0x134).readS32(); } catch(e){}
    send({t:'SNDOBJ', m:'play-wrapper: mode[+0x148]='+mode+'  soundId[+0x138]='+sid+'  x='+x+' y='+y});
  }
});

// ---- the OTHER low-level play fn (one mode routes here instead of 0x4798c0) ----
tryHook(RVA_PLAY2, 'play2', {
  onEnter(args) {
    let a0=-1,a1=-1; try{a0=args[0].toInt32();}catch(e){} try{a1=args[1].toInt32();}catch(e){}
    send({t:'PLAY2', m:'play2 (0x479d20): arg0='+a0+' arg1='+a1});
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
""".replace("RVA_DECRYPT", "0x%x" % DECRYPT).replace("RVA_H1A", "0x%x" % H1A).replace("RVA_GFLAG", "0x%x" % GFLAG).replace("RVA_H19", "0x%x" % H19).replace("RVA_SNDOBJ", "0x%x" % SNDOBJ).replace("RVA_JT", "0x%x" % JT).replace("RVA_PLAY2", "0x%x" % PLAY2).replace("RVA_PLAY", "0x%x" % PLAY)


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
        elif t == "H1A":   out("H1A   " + p["m"])
        elif t == "SNDOBJ": out("SNDOBJ " + p["m"])
        elif t == "PLAY2": out("PLAY2 " + p["m"])
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
