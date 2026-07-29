#!/usr/bin/env python
"""
Find the crypt function in the live (packed) 7.5.2.0 client by locating code that
REFERENCES the static key buffer (Urk#nI7ni @ +0x29ba64). The instruction that loads
that address lives inside the decrypt/encrypt routine. Pure memory reads — no exceptions,
no page tricks (packer-safe). For each xref: report RVA, hexdump, disassembly, and a
backward scan for the function prologue (candidate function entry to hook next).

Usage: python re/frida_xref.py --attach
"""
import sys, frida

MOD = "NexusTK.exe"
KEY_ADDRS = [0x69ba64, 0x69ba6d, 0x69ba76, 0x69ba7f]   # the 4 Urk#nI7ni copies

JS = r"""
'use strict';
const MAIN = (Process.findModuleByName && Process.findModuleByName('__MOD__')) || Process.enumerateModules()[0];
const base = MAIN.base, hi = MAIN.base.add(MAIN.size);
send({t:'info', m:'module '+MAIN.name+' base='+base});

const TARGETS = __TARGETS__;
function lepat(v){ return [v&0xff,(v>>8)&0xff,(v>>16)&0xff,(v>>24)&0xff]
  .map(b=>('0'+b.toString(16)).slice(-2)).join(' '); }
function hex(p,n){ try{ return Array.from(new Uint8Array(p.readByteArray(n)))
  .map(b=>('0'+(b&0xff).toString(16)).slice(-2)).join(' '); }catch(e){ return '?'; } }

function findPrologue(addr){          // scan back up to 0x600 bytes for 55 8b ec / 55 8b
  for(let off=1; off<0x600; off++){
    const p=addr.sub(off);
    try{ const b=new Uint8Array(p.readByteArray(3));
      if(b[0]===0x55 && b[1]===0x8b && (b[2]===0xec)) return {p:p, kind:'push ebp;mov ebp,esp'};
    }catch(e){ break; }
  }
  return null;
}
function disasm(addr, count){
  let out=[], p=addr;
  for(let i=0;i<count;i++){ try{ const ins=Instruction.parse(p);
    out.push('  +0x'+p.sub(base).toString(16)+'  '+ins.mnemonic+' '+ins.opStr); p=ins.next; }
    catch(e){ break; } }
  return out.join('\n');
}

(function(){
  const rx = Process.enumerateRanges('r-x').filter(r=> r.base.compare(base)>=0 && r.base.compare(hi)<0);
  let total=0;
  for(const t of TARGETS){
    const pat=lepat(t);
    for(const r of rx){
      let hits; try{ hits=Memory.scanSync(r.base, r.size, pat); }catch(e){ continue; }
      for(const h of hits){
        total++;
        const a=h.address;
        const pro=findPrologue(a);
        send({t:'xref', m:'xref to 0x'+t.toString(16)+' at +0x'+a.sub(base).toString(16)+
          '\n   ctx: '+hex(a.sub(6),16)+
          (pro? ('\n   prologue @ +0x'+pro.p.sub(base).toString(16)+' ('+pro.kind+')\n'+disasm(pro.p,10)) : '\n   (no prologue found within 0x600)')});
        if(total>=20) { send({t:'done', m:'done ('+total+' xrefs)'}); return; }
      }
    }
  }
  send({t:'done', m:'done ('+total+' xrefs)'});
})();
""".replace("__MOD__", MOD).replace("__TARGETS__", "[" + ",".join(hex(a) for a in KEY_ADDRS) + "]")


def main():
    if "--attach" not in sys.argv:
        print("run with --attach"); return
    dev = frida.get_local_device()
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
    if not procs:
        print("no running", MOD); return
    done = {"n": 0}

    def on_message(msg, data):
        if msg["type"] == "error":
            print("[frida-error]", msg.get("description")); return
        p = msg["payload"]
        if p.get("t") == "done":
            done["n"] += 1
        print({"info": "[i]", "xref": ">>", "done": "[done]"}.get(p.get("t"), "?"), p["m"])

    print(f"scanning {[p.pid for p in procs]}")
    for p in procs:
        try:
            s = dev.attach(p.pid); sc = s.create_script(JS)
            sc.on("message", on_message); sc.load()
        except Exception as e:
            print(f"  attach {p.pid} failed: {e}")
    import time
    for _ in range(60):
        time.sleep(0.5)
        if done["n"] >= len(procs):
            break


if __name__ == "__main__":
    main()
