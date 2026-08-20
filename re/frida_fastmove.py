#!/usr/bin/env python
"""Live probe: what does fast-move actually change in the 4.95 client, and why does the
SILENT (client-authoritative) path freeze?

Static RE found:
  * game-state singleton pointer @ 0x4fd390 ; byte [state+0x451] is a flag read in the walk-input
    path (0x48d68d) right before the client queues a local self-move command with dir|0x80.
  * selfWalkAnim @0x48f2c0  -> starts the LOCAL leg animation (client self-walks; no server needed)
  * moveCommit   @0x48f160  -> finalizes the step locally (logical:=dest, walk-active:=0)
  * the checkbox/toggle writes [obj+0x451] from a sete/setne (0x464d1d, 0x48fc1e)

This probe answers, live:
  Q1. What is [state+0x451] with fast-move OFF vs ON? (is it THE flag?)
  Q2. With fast-move ON, does selfWalkAnim/moveCommit fire LOCALLY on every keypress
      (client self-pacing) or only once and then stall (the freeze)?
  Q3. Does the freeze coincide with the flag flipping, a map/dialog event, or a stuck anim?

Run with the client already up and logged in:
    python re/frida_fastmove.py --attach
Then, watching the console:
    1. Walk 3-4 steps with fast-move OFF.
    2. Toggle fast-move ON (F10 checkbox or its key).  <-- watch for [state+0x451] change
    3. Walk 3-4 steps with fast-move ON.
    4. If it freezes, note whether selfWalkAnim stopped firing while you were still pressing.
Ctrl+C to stop. Paste the console output back.
"""
import sys, time, json, frida
from _paths import CLIENT

EXE = str(CLIENT / "NexusTK_local.exe")

RVA = {
    "selfWalkAnim": 0x08f2c0,   # 0x48f2c0
    "moveCommit":   0x08f160,   # 0x48f160
    "passGate":     0x04c8f0,   # 0x44c8f0  moveCommit's passability gate; al=1 pass, al=0 blocked
}
STATE_PTR_VA = 0x4fd390         # dword: pointer to the game-state singleton
FLAG_OFF     = 0x451            # byte offset of the candidate fast-move flag on that object
E_WALKACTIVE = 0x18c
E_FRAMECTR   = 0x18e
E_DONTWAIT   = 0x65f3
E_FORM       = 0x17d            # 3 == mounted

JS = r"""
const RVA = __RVA_JSON__;
const STATE_PTR_VA = __STATE__, FLAG_OFF = __FLAG__;
const E = __ENT__;
let base = Process.findModuleByName('NexusTK_local.exe');
base = base ? base.base : Process.enumerateModules()[0].base;
send({t:'info', m:'module base = ' + base});
function at(n){ return base.add(ptr(RVA[n])); }

function stateObj(){
  try { const p = base.add(ptr(STATE_PTR_VA - 0x400000)).readPointer(); return p.isNull()? null : p; }
  catch(e){ return null; }
}
function flag(){
  const s = stateObj(); if (!s) return -1;
  try { return s.add(FLAG_OFF).readU8(); } catch(e){ return -2; }
}
function entFields(ecx){
  const o = {};
  try { o.wa = ecx.add(E.wa).readU8(); } catch(e){ o.wa = '?'; }
  try { o.fc = ecx.add(E.fc).readU8(); } catch(e){ o.fc = '?'; }
  try { o.dw = ecx.add(E.dw).readU8(); } catch(e){ o.dw = '?'; }
  try { o.fm = ecx.add(E.fm).readU8(); } catch(e){ o.fm = '?'; }
  return o;
}

// Poll the flag; report only on change (captures the toggle without spamming).
let last = -99;
setInterval(function(){
  const f = flag();
  if (f !== last){ send({t:'flag', m:'[state+0x451] = ' + f + '   (was ' + last + ')'}); last = f; }
}, 150);

Interceptor.attach(at('selfWalkAnim'), { onEnter(a){
  const dir = a[0].toInt32() & 0xff;
  const e = this.context.ecx;
  const ff = entFields(e);
  send({t:'sw', m:'selfWalkAnim dir=' + dir + '  flag=' + flag() +
      '  walkActive=' + ff.wa + ' frameCtr=' + ff.fc + ' dontWait=' + ff.dw + ' form=' + ff.fm});
}});

Interceptor.attach(at('moveCommit'), { onEnter(a){
  const e = this.context.ecx;
  const ff = entFields(e);
  send({t:'mc', m:'  moveCommit          flag=' + flag() + ' frameCtr=' + ff.fc + ' form=' + ff.fm});
}});

// passGate: moveCommit's passability check. Return al=1 -> step allowed (commits), al=0 -> BLOCKED
// (moveCommit bails, step does not complete). If this returns 0 during a fast-move-ON freeze, the client
// is refusing the step because the destination terrain isn't loaded in its local map = a TERRAIN problem.
// If it returns 1 yet the step still stalls, the blocker is the ack/prediction cap instead.
Interceptor.attach(at('passGate'), {
  onLeave(ret){ send({t:'pg', m:'    passGate -> ' + (ret.toInt32() & 0xff) + '  (1=pass, 0=blocked)'}); }
});
"""

def main():
    attach = "--attach" in sys.argv
    def out(line): print(f"[{time.strftime('%H:%M:%S')}] {line}")
    def on_message(msg, data):
        if msg["type"] == "error": out("JS-ERROR " + msg.get("description", str(msg))); return
        p = msg["payload"]; out(("· " if p.get("t")=="info" else "") + p["m"])

    if attach:
        out("attaching to running client..."); session = frida.attach("NexusTK_local.exe"); pid = None
    else:
        out(f"spawning {EXE}"); pid = frida.spawn([EXE]); session = frida.attach(pid)

    js = (JS.replace("__RVA_JSON__", json.dumps(RVA))
            .replace("__STATE__", hex(STATE_PTR_VA))
            .replace("__FLAG__", hex(FLAG_OFF))
            .replace("__ENT__", json.dumps({"wa":E_WALKACTIVE,"fc":E_FRAMECTR,"dw":E_DONTWAIT,"fm":E_FORM})))
    script = session.create_script(js); script.on("message", on_message); script.load()
    if pid is not None: frida.resume(pid)
    out("hooked. 1) walk fast-move OFF  2) toggle ON (watch flag)  3) walk ON  4) note any freeze. Ctrl+C to stop.")
    sys.stdin.read()

if __name__ == "__main__":
    main()
