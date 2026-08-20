#!/usr/bin/env python
"""Definitive: prove the 0x08 stats handler copies payload[47] VERBATIM into [state+0x451].
For the next few stats packets, overwrite payload[47] with a sentinel (0x2A) in onEnter, read
[state+0x451] in onLeave (expect 0x2A), then RESTORE the flag to 0 immediately so no walk ever
observes the sentinel. Self-correcting / non-disruptive.

  python re/frida_fastmove6.py [seconds] [sentinel]
"""
import sys, time, frida
DUR = int(sys.argv[1]) if len(sys.argv) > 1 else 25
SENT = int(sys.argv[2]) if len(sys.argv) > 2 else 0x2A

JS = """
const STATEPP = ptr('0x4fd390');
const SENT = %d;
let done = 0;
function flag(){ try { return STATEPP.readPointer().add(0x451).readU8(); } catch(e){ return -1; } }
function setFlag(v){ try { STATEPP.readPointer().add(0x451).writeU8(v); } catch(e){} }
Interceptor.attach(ptr('0x48fc40'), {
  onEnter() {
    this._before = flag();
    try {
      const payload = this.context.esp.add(4).readPointer();
      this._orig = payload.add(47).readU8();
      if (done < 3) { payload.add(47).writeU8(SENT); this._inj = true; }
    } catch(e){ this._inj=false; }
  },
  onLeave() {
    const after = flag();
    if (this._inj) {
      done++;
      send({tag:'test', orig:this._orig, injected:SENT, before:this._before, after:after});
      setFlag(0);   // restore server-authoritative immediately, before any walk
    }
  }
});
send({tag:'ready', flag:flag(), sentinel:SENT});
""" % SENT

def main():
    dev = frida.get_local_device(); pid=None
    for p in dev.enumerate_processes():
        if p.name=="NexusTK.exe":
            s=dev.attach(p.pid); sc=s.create_script("send({sz:Process.getModuleByName('NexusTK.exe').size});")
            g={}; sc.on('message', lambda m,d: g.update(m.get('payload',{}))); sc.load(); time.sleep(0.2); s.detach()
            if g.get('sz') in (1130544,1155072): pid=p.pid
    if pid is None: print("client not found"); return
    print("attaching", pid); sess=dev.attach(pid); scr=sess.create_script(JS)
    def om(msg,data):
        if msg.get('type')=='error': print("JS ERR", msg.get('description')); return
        p=msg.get('payload',{})
        if p.get('tag')=='ready': print(f"[ready] flag={p['flag']} sentinel=0x{p['sentinel']:02x}")
        elif p.get('tag')=='test':
            ok = (p['after']==p['injected'])
            print(f"[test] injected payload[47]=0x{p['injected']:02x} (orig 0x{p['orig']:02x}) -> [state+0x451]=0x{p['after']:02x}  VERBATIM={ok}  (restored to 0)")
    scr.on('message',om); scr.load(); time.sleep(DUR); sess.detach(); print("done.")

if __name__=="__main__":
    main()
