#!/usr/bin/env python
"""Pin the EXACT byte offset in the 0x08 stats packet that the client copies into the fast-move flag
[state+0x451]. Read-only.

In handler 0x48fc40 the trailing flag byte is fetched at 0x48fd2b:
    0x48fd25 movsx ecx, si       ; ecx = esi
    0x48fd28 add   ecx, ebx      ; ecx = payload + esi   <- source pointer
    0x48fd2b call  0x475c90      ; read that byte
We hook 0x48fc40 to capture `payload`, and 0x48fd2b to read `ecx` (source ptr) and the byte value,
then print offset = ecx - payload. That is the packet offset our SendStats must set.

  python re/frida_fastmove4.py [seconds]
"""
import sys, time, frida

DUR = int(sys.argv[1]) if len(sys.argv) > 1 else 12

JS = r"""
const STATEPP = ptr('0x4fd390');
let payload = null;

Interceptor.attach(ptr('0x48fc40'), {
  onEnter() {
    try { payload = this.context.esp.add(4).readPointer(); } catch(e){ payload=null; }
    // full packet dump (up to 64 bytes) so we can see every field
    try { send({tag:'pkt'}, payload.readByteArray(64)); } catch(e){}
  }
});

// the flag-source read
Interceptor.attach(ptr('0x48fd2b'), {
  onEnter() {
    try {
      const src = this.context.ecx;          // payload + esi
      const off = payload ? src.sub(payload).toInt32() : -1;
      const val = src.readU8();
      send({tag:'flagsrc', off, val, src: src.toString()});
    } catch(e){ send({tag:'flagsrc_err', e: e.message}); }
  }
});

// and the actual write, to confirm value lands in the state singleton
Interceptor.attach(ptr('0x48fd3a'), {
  onEnter() {
    const v = this.context.eax.toInt32() & 0xff;
    const base = this.context.edx;
    let st=null; try{ st=STATEPP.readPointer(); }catch(e){}
    send({tag:'write', val:v, isState: st? base.equals(st): false});
  }
});
send({tag:'ready'});
"""

def main():
    dev = frida.get_local_device()
    pid = None
    for p in dev.enumerate_processes():
        if p.name == "NexusTK.exe":
            s = dev.attach(p.pid)
            sc = s.create_script("const m=Process.getModuleByName('NexusTK.exe'); send({sz:m.size});")
            got = {}; sc.on('message', lambda m, d: got.update(m.get('payload', {})))
            sc.load(); time.sleep(0.2); s.detach()
            if got.get('sz') in (1130544, 1155072): pid = p.pid
    if pid is None:
        print("4.95 client not found"); return
    print("attaching pid", pid)
    session = dev.attach(pid); script = session.create_script(JS)
    def on_message(msg, data):
        if msg.get('type') == 'error':
            print("JS ERROR:", msg.get('description')); return
        p = msg.get('payload', {}); t = p.get('tag')
        if t == 'pkt' and data:
            print("0x08 pkt:", data.hex())
        elif t == 'flagsrc':
            print(f"  --> flag byte from OFFSET {p['off']} (value {p['val']}) src={p['src']}")
        elif t == 'flagsrc_err':
            print("  flagsrc err:", p['e'])
        elif t == 'write':
            print(f"  --> WRITE [state+0x451]={p['val']} isStateSingleton={p['isState']}")
    script.on('message', on_message); script.load()
    time.sleep(DUR); session.detach(); print("done.")

if __name__ == "__main__":
    main()
