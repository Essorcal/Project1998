#!/usr/bin/env python
"""
Hook the candidate crypt functions in the live client and dump their inputs/outputs, to
identify THE decrypt routine (ciphertext in -> plaintext/AA-framed out) and whether it's
universal (fires for every packet incl. table-key combat) or static-key-only.

thiscall shape (this=ecx, stack args arg0/arg1/arg2). We snapshot arg0 & arg2 buffers on
enter and leave (handles in-place decrypt where out==src).

Usage: python re/frida_hook_crypt.py --attach   (run while receiving packets)
"""
import sys, frida

MOD = "NexusTK.exe"
FUNCS = [0x177030, 0x178b20, 0x178c40]     # RVAs from the xref scan
LIMIT = 6                                   # calls logged per function

JS = r"""
'use strict';
const MAIN = (Process.findModuleByName && Process.findModuleByName('__MOD__')) || Process.enumerateModules()[0];
const base = MAIN.base;
const FUNCS = __FUNCS__, LIMIT = __LIMIT__;
send({t:'info', m:'base='+base});
function hx(p,n){ try{ const b=new Uint8Array(p.readByteArray(n));
  let h=Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
  let a=Array.from(b).map(x=> (x>=32&&x<127)?String.fromCharCode(x):'.').join('');
  return h+'  |'+a+'|'; }catch(e){ return '<unreadable>'; } }

FUNCS.forEach(function(rva){
  const addr = base.add(rva);
  let n=0;
  Interceptor.attach(addr, {
    onEnter(args){
      this.n = n++;
      if(this.n>=LIMIT) return;
      this.this_ = this.context.ecx;
      this.a0 = args[0]; this.a1 = args[1]; this.a2 = args[2];
      this.srcIn = hx(args[0], 40);
      send({t:'call', m:'>> +0x'+rva.toString(16)+' #'+this.n+
        '  ecx='+this.context.ecx+'  a0='+args[0]+' a1='+args[1]+'('+args[1].toInt32()+') a2='+args[2]+
        '\n    a0.in : '+this.srcIn});
    },
    onLeave(ret){
      if(this.n>=LIMIT) return;
      send({t:'call', m:'   +0x'+rva.toString(16)+' #'+this.n+' ret='+ret+
        '\n    a0.out: '+hx(this.a0,40)+
        '\n    a2.out: '+hx(this.a2,40)});
    }
  });
  send({t:'info', m:'hooked +0x'+rva.toString(16)});
});
""".replace("__MOD__", MOD).replace("__FUNCS__", "[" + ",".join(hex(f) for f in FUNCS) + "]").replace("__LIMIT__", str(LIMIT))


def main():
    if "--attach" not in sys.argv:
        print("run with --attach"); return
    dev = frida.get_local_device()
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
    if not procs:
        print("no running", MOD); return

    def on_message(msg, data):
        if msg["type"] == "error":
            print("[frida-error]", msg.get("description")); return
        p = msg["payload"]
        print({"info": "[i]", "call": "  "}.get(p.get("t"), "?"), p["m"])

    print(f"hooking crypt in {[p.pid for p in procs]}")
    for p in procs:
        try:
            s = dev.attach(p.pid); sc = s.create_script(JS)
            sc.on("message", on_message); sc.load()
        except Exception as e:
            print(f"  attach {p.pid} failed: {e}")
    print("watching ~20s of traffic...\n")
    import time
    time.sleep(20)
    print("\n(done)")


if __name__ == "__main__":
    main()
