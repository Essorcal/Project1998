#!/usr/bin/env python
"""Read-only live probe: is the self-controller command dispatcher (0x48eb40) packet-driven,
and does incoming opcode 0x08 write the fast-move flag [state+0x451]?

Attaches to the P1998 4.95 client (NexusTK.exe, byte-identical to NexusTK_local.exe, no ASLR).
Hooks (log + read a byte only, never writes):
  * 0x48eb40  self-controller command dispatcher  -> logs payload[0] (the command byte)
  * 0x48fc40  cmd-0x08 handler                    -> dumps message bytes, flag before/after, BACKTRACE
  * 0x464c00  options-window apply                -> flag before/after
  * 0x48fc00  hotkey toggle                       -> logs
State singleton ptr @ 0x4fd390 ; runtime fast-move flag = [state+0x451].

  python re/frida_fastmove2.py [seconds]
"""
import sys, time, frida

DUR = int(sys.argv[1]) if len(sys.argv) > 1 else 10

JS = r"""
const IB = ptr('0x400000');
const STATEPP = ptr('0x4fd390');          // pointer-to-state global
function stateFlag() {
  try {
    const st = STATEPP.readPointer();
    if (st.isNull()) return -1;
    return st.add(0x451).readU8();
  } catch (e) { return -2; }
}
function hx(p, n) {
  try { return p.readByteArray(n); } catch (e) { return null; }
}

// 0x48eb40 dispatcher: arg1 at [esp+4] on entry; payload = [arg1+0xc]; cmd = payload[0]
const cmdCounts = {};
Interceptor.attach(ptr('0x48eb40'), {
  onEnter(args) {
    try {
      const arg1 = this.context.esp.add(4).readPointer();
      const payload = arg1.add(0xc).readPointer();
      const cmd = payload.readU8();
      cmdCounts[cmd] = (cmdCounts[cmd]||0)+1;
    } catch (e) {}
  }
});

// 0x48fc40 cmd-0x08 handler: arg1 (payload) at [esp+4]; this=ecx=self
Interceptor.attach(ptr('0x48fc40'), {
  onEnter(args) {
    const before = stateFlag();
    let bytes = null;
    try {
      const payload = this.context.esp.add(4).readPointer();
      bytes = hx(payload, 16);
      this._payload = payload;
    } catch (e) {}
    this._before = before;
    // backtrace to see if this is the network receive path
    const bt = Thread.backtrace(this.context, Backtracer.ACCURATE)
                 .slice(0,8).map(a => a.sub(IB).toString(16)).join(' ');
    send({tag:'cmd08_enter', before, bt});
    if (bytes) send({tag:'cmd08_bytes'}, bytes);
  },
  onLeave(retval) {
    send({tag:'cmd08_leave', after: stateFlag(), before: this._before});
  }
});

// 0x464c00 options apply
Interceptor.attach(ptr('0x464c00'), {
  onEnter(args) { this._b = stateFlag(); },
  onLeave(retval) { send({tag:'apply', before:this._b, after:stateFlag()}); }
});

// 0x48fc00 hotkey toggle
Interceptor.attach(ptr('0x48fc00'), {
  onEnter(args) { send({tag:'toggle_enter', before: stateFlag()}); },
  onLeave(retval) { send({tag:'toggle_leave', after: stateFlag()}); }
});

// periodic dispatcher-command histogram + current flag
function dump() {
  send({tag:'hist', flag: stateFlag(), cmds: cmdCounts});
}
setInterval(dump, 2000);
send({tag:'ready', flag: stateFlag()});
"""

def main():
    dev = frida.get_local_device()
    target = None
    for p in dev.enumerate_processes():
        if p.name.lower().startswith("nexustk"):
            # pick the 4.95 client by module size if possible; else by name
            target = p.pid
    # prefer the P1998 client explicitly
    for p in dev.enumerate_processes():
        if p.name == "NexusTK.exe":
            try:
                sess0 = dev.attach(p.pid)
                sess0.detach()
            except Exception:
                pass
    # attach to the smaller (4.95) module: probe each
    cands = [p.pid for p in dev.enumerate_processes() if p.name == "NexusTK.exe"]
    print("candidate pids:", cands)
    pid = None
    for c in cands:
        s = dev.attach(c)
        sc = s.create_script("const m=Process.getModuleByName('NexusTK.exe'); send({sz:m.size, base:m.base.toString()});")
        got = {}
        sc.on('message', lambda msg, data: got.update(msg.get('payload', {})))
        sc.load(); time.sleep(0.3); s.detach()
        print(f"  pid {c}: module size {got.get('sz')}")
        if got.get('sz') in (1130544, 1155072):   # file size / in-memory virtual size
            pid = c
    if pid is None:
        print("4.95 client (1.13MB) not found among", cands); return
    print("attaching to 4.95 client pid", pid)
    session = dev.attach(pid)
    script = session.create_script(JS)

    def on_message(msg, data):
        if msg.get('type') == 'error':
            print("JS ERROR:", msg.get('description')); return
        p = msg.get('payload', {})
        tag = p.get('tag')
        if tag == 'ready':
            print(f"[ready] flag={p['flag']}")
        elif tag == 'cmd08_enter':
            print(f"[cmd08] ENTER flag_before={p['before']}  backtrace(rva): {p['bt']}")
        elif tag == 'cmd08_bytes' and data:
            print("        msg bytes:", data.hex())
        elif tag == 'cmd08_leave':
            print(f"[cmd08] LEAVE flag {p['before']} -> {p['after']}")
        elif tag == 'apply':
            print(f"[apply] flag {p['before']} -> {p['after']}")
        elif tag == 'toggle_enter':
            print(f"[toggle] ENTER flag_before={p['before']}")
        elif tag == 'toggle_leave':
            print(f"[toggle] LEAVE flag_after={p['after']}")
        elif tag == 'hist':
            cs = {hex(int(k)): v for k, v in p['cmds'].items()}
            print(f"[hist] flag={p['flag']}  dispatcher cmds: {cs}")

    script.on('message', on_message)
    script.load()
    time.sleep(DUR)
    session.detach()
    print("done.")

if __name__ == "__main__":
    main()
