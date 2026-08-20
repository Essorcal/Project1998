#!/usr/bin/env python
"""Clean function-boundary confirmation (read-only): for every 0x08 stats packet, compare the raw
byte at payload[47] with [state+0x451] read AFTER the handler returns. If they always match, offset
47 is the fast-move source and the copy is verbatim.

  python re/frida_fastmove5.py [seconds]
"""
import sys, time, frida
DUR = int(sys.argv[1]) if len(sys.argv) > 1 else 20

JS = r"""
const STATEPP = ptr('0x4fd390');
function flag(){ try { return STATEPP.readPointer().add(0x451).readU8(); } catch(e){ return -1; } }
Interceptor.attach(ptr('0x48fc40'), {
  onEnter() {
    try {
      const payload = this.context.esp.add(4).readPointer();
      this._b47 = payload.add(47).readU8();
      this._op  = payload.readU8();
      this._fl  = payload.add(1).readU8();
    } catch(e){ this._b47=-1; }
    this._before = flag();
  },
  onLeave() {
    send({tag:'s', op:this._op, flags:this._fl, b47:this._b47, before:this._before, after:flag()});
  }
});
send({tag:'ready', flag:flag()});
"""

def main():
    dev = frida.get_local_device(); pid=None
    for p in dev.enumerate_processes():
        if p.name=="NexusTK.exe":
            s=dev.attach(p.pid); sc=s.create_script("send({sz:Process.getModuleByName('NexusTK.exe').size});")
            g={}; sc.on('message', lambda m,d: g.update(m.get('payload',{}))); sc.load(); time.sleep(0.2); s.detach()
            if g.get('sz') in (1130544,1155072): pid=p.pid
    if pid is None: print("client not found"); return
    print("attaching", pid); sess=dev.attach(pid); scr=sess.create_script(JS)
    n=[0]; mism=[0]
    def om(msg,data):
        if msg.get('type')=='error': print("JS ERR", msg.get('description')); return
        p=msg.get('payload',{})
        if p.get('tag')=='ready': print(f"[ready] flag={p['flag']}"); return
        if p.get('tag')=='s':
            n[0]+=1; match = (p['b47']==p['after'])
            if not match: mism[0]+=1
            if n[0]<=6 or not match or p['after']!=0:
                print(f"pkt op=0x{p['op']:02x} flags=0x{p['flags']:02x} payload[47]={p['b47']} flag {p['before']}->{p['after']} match={match}")
    scr.on('message',om); scr.load(); time.sleep(DUR); sess.detach()
    print(f"done. {n[0]} stats packets, {mism[0]} mismatches (payload[47] != resulting flag).")

if __name__=="__main__":
    main()
