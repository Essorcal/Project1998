#!/usr/bin/env python
"""Focused live probe: is the 4.95 client's LOCAL self-walk-with-legs path firing?

Static RE found a self-walk animation path that cycles legs WITHOUT the 0x0C overshoot:
  selfWalkAnim(dir) @0x48f2c0  -> sets walk-active=1, frameCtr=0, starts anim timer (NO destination)
  local controller  @0x4900d0/@0x4903d0 (handler-A/B) parse a command buffer (byte+1 = dir)
  self-move dispatch@0x48eb40  keys on buffer[0]: 0x0b->A, 0x26->B
  move-commit gate  @0x48f160  -> calls passability 0x44c8f0; if it fails, no move
This probe logs when each fires so we can tell whether the client is TRYING to self-animate
(and something suppresses it) or never attempts it at all under our 0x04-only flow.

Run with the client already up:  python re/frida_selfwalk.py --attach
Then walk a few steps on the 4.95 client and watch the console.
"""
import sys, time, frida

EXE = r"C:\Program Files (x86)\Nexon\NextAeon\NexusTK_local.exe"

RVA = {
    "selfWalkAnim": 0x08f2c0,   # 0x48f2c0  start leg cycle in place (dir arg), no overshoot
    "dispatch":     0x08eb40,   # 0x48eb40  self-move command dispatch (buffer[0]=subop, [1]=dir)
    "handlerA":     0x0900d0,   # 0x4900d0  subop 0x0b handler (parses buffer, may selfWalkAnim)
    "handlerB":     0x0903d0,   # 0x4903d0  subop 0x26 handler
    "moveCommit":   0x08f160,   # 0x48f160  commit move to target tile; calls passgate
    "passGate":     0x04c8f0,   # 0x44c8f0  passability/'did the view move' gate -> al
    "animReg":      0x01b5d0,   # 0x41b5d0  REGISTER entity in animation list (starts leg cycle)
    "animUnreg":    0x01b5f0,   # 0x41b5f0  UNREGISTER entity from animation list (stops leg cycle)
}

JS = r"""
const RVA = __RVA_JSON__;
let base = Process.findModuleByName('NexusTK_local.exe');
base = base ? base.base : Process.enumerateModules()[0].base;
send({t:'info', m:'module base = ' + base});
function at(n){ return base.add(ptr(RVA[n])); }

// selfWalkAnim(dir): thiscall, this=ecx, stack arg0 = dir
Interceptor.attach(at('selfWalkAnim'), { onEnter(a){
  send({t:'sw', m:'*** selfWalkAnim FIRED  dir=' + (a[0].toInt32() & 0xff)});
}});

// self-move dispatch(ctx): buffer = [ctx+0xc]; subop=buffer[0]; dir=buffer[1]
Interceptor.attach(at('dispatch'), { onEnter(a){
  try {
    const ctx = a[0];
    const buf = ctx.add(0xc).readPointer();
    const subop = buf.readU8();
    const dir = buf.add(1).readU8();
    send({t:'sw', m:'dispatch  subop=0x' + subop.toString(16) + ' dir=' + dir});
  } catch(e){ send({t:'sw', m:'dispatch (unreadable ctx)'}); }
}});

Interceptor.attach(at('handlerA'), { onEnter(a){
  const buf=a[0]; let dir=-1; try{ dir=buf.add(1).readU8(); }catch(e){}
  send({t:'sw', m:'handlerA(0x0b)  dir=' + dir});
}});
Interceptor.attach(at('handlerB'), { onEnter(a){
  const buf=a[0]; let dir=-1; try{ dir=buf.add(1).readU8(); }catch(e){}
  send({t:'sw', m:'handlerB(0x26)  dir=' + dir});
}});

// move-commit (0x48f160): the function that sets logical=dest, walk-active=0, and UNREGISTERS the anim.
Interceptor.attach(at('moveCommit'), { onEnter(a){
  const e = this.context.ecx;                 // thiscall: entity in ecx
  let fc = -1; try { fc = e.add(0x18e).readU8(); } catch(_){}
  send({t:'sw', m:'  moveCommit (logical:=dest, unregister)  frameCtr@enter=' + fc});
}});

"""

def main():
    attach = "--attach" in sys.argv
    def out(line):
        print(f"[{time.strftime('%H:%M:%S')}] {line}")
    def on_message(msg, data):
        if msg["type"] == "error":
            out("JS-ERROR " + msg.get("description", str(msg))); return
        p = msg["payload"]
        if p.get("t") == "info": out("· " + p["m"])
        elif p.get("t") == "sw": out(p["m"])
        else: out(str(p))

    if attach:
        out("attaching to running client...")
        session = frida.attach("NexusTK_local.exe"); pid = None
    else:
        out(f"spawning {EXE}")
        pid = frida.spawn([EXE]); session = frida.attach(pid)

    script = session.create_script(JS.replace("__RVA_JSON__", __import__("json").dumps(RVA)))
    script.on("message", on_message)
    script.load()
    if pid is not None:
        frida.resume(pid)
    out("hooked — walk a few steps on the 4.95 client, then Ctrl+C")
    sys.stdin.read()

if __name__ == "__main__":
    main()
