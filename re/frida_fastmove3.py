#!/usr/bin/env python
"""Read-only: instrument every WRITE and a READ of [state+0x451] to learn exactly what sets the
fast-move flag and confirm the byte the stats handler writes is the same byte the walk path reads.

Writers hooked (mid-instruction, log the register value written; NO modification):
  0x48fc1c  hotkey toggle        mov [eax+0x451], cl
  0x48fd3a  cmd-0x08 handler     mov [edx+0x451], al
  0x464d1b  options apply        mov [edi+0x451], cl
Reader hooked:
  0x4901f9  walk path            cmp [ecx+0x451], 1     (log base+value)
Also dumps each 0x08 packet's bytes so we can see the trailing byte the handler consumes.

  python re/frida_fastmove3.py [seconds]
"""
import sys, time, frida

DUR = int(sys.argv[1]) if len(sys.argv) > 1 else 12

JS = r"""
const IB = ptr('0x400000');
const STATEPP = ptr('0x4fd390');
function statePtr(){ try { return STATEPP.readPointer(); } catch(e){ return ptr(0);} }

// writers: attach at the instruction; value is in a byte register, base in a full register
function hookWriter(rva, valReg, baseReg, tag) {
  Interceptor.attach(ptr('0x'+rva.toString(16)), function () {
    const v = this.context[valReg].toInt32() & 0xff;
    const base = this.context[baseReg];
    send({tag:'WRITE', who:tag, val:v, base:base.toString(), isState: base.equals(statePtr())});
  });
}
hookWriter(0x48fc1c, 'ecx', 'eax', 'hotkey_toggle');
hookWriter(0x48fd3a, 'eax', 'edx', 'cmd08_stats');
hookWriter(0x464d1b, 'ecx', 'edi', 'options_apply');

// reader in walk path
let lastRead = null;
Interceptor.attach(ptr('0x4901f9'), function () {
  const base = this.context.ecx;
  let v = -1; try { v = base.add(0x451).readU8(); } catch(e){}
  const key = base.toString()+':'+v;
  if (key !== lastRead) { lastRead = key;
    send({tag:'READ', base:base.toString(), val:v, isState: base.equals(statePtr())});
  }
});

// dump 0x08 packets (cmd handler 0x48fc40, payload at [esp+4])
Interceptor.attach(ptr('0x48fc40'), {
  onEnter(){ try {
    const payload = this.context.esp.add(4).readPointer();
    send({tag:'PKT08'}, payload.readByteArray(24));
  } catch(e){} }
});

send({tag:'ready', flag: (function(){try{return statePtr().add(0x451).readU8();}catch(e){return -1;}})(),
      state: statePtr().toString()});
"""

def main():
    dev = frida.get_local_device()
    pid = None
    for p in dev.enumerate_processes():
        if p.name == "NexusTK.exe":
            s = dev.attach(p.pid)
            sc = s.create_script("const m=Process.getModuleByName('NexusTK.exe'); send({sz:m.size});")
            got = {}
            sc.on('message', lambda m, d: got.update(m.get('payload', {})))
            sc.load(); time.sleep(0.2); s.detach()
            if got.get('sz') in (1130544, 1155072):
                pid = p.pid
    if pid is None:
        print("4.95 client not found"); return
    print("attaching pid", pid)
    session = dev.attach(pid)
    script = session.create_script(JS)

    def on_message(msg, data):
        if msg.get('type') == 'error':
            print("JS ERROR:", msg.get('description'), msg.get('stack','')); return
        p = msg.get('payload', {})
        t = p.get('tag')
        if t == 'ready':
            print(f"[ready] state@{p['state']} flag={p['flag']}")
        elif t == 'WRITE':
            print(f"[WRITE] {p['who']:14} val={p['val']} base={p['base']} isStateSingleton={p['isState']}")
        elif t == 'READ':
            print(f"[READ ] walk-path base={p['base']} val={p['val']} isStateSingleton={p['isState']}")
        elif t == 'PKT08' and data:
            print(f"        0x08 pkt: {data.hex()}")

    script.on('message', on_message)
    script.load()
    time.sleep(DUR)
    session.detach()
    print("done.")

if __name__ == "__main__":
    main()
