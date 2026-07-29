#!/usr/bin/env python
"""
One-shot memory scan of the LIVE (packed) NexusTK 7.5.2.0 client. The exe is packed, so
key strings/plaintext exist only in memory at runtime. Locate:
  - cipher keys (Urk#nI7ni static / NexonInc login) -> fixed buffers read ONLY by the
    crypt function -> ideal watchpoint targets to find the decrypt routine
  - known plaintext (player name / Peasant / message text) -> the decrypted packet buffer
Non-invasive: reads memory, sets nothing.

Usage: python re/frida_memscan.py --attach [--name Zalerooo]
"""
import sys, frida

MOD = "NexusTK.exe"

JS = r"""
'use strict';
const MAIN = (typeof Process.findModuleByName==='function' && Process.findModuleByName('__MOD__'))
             || Process.enumerateModules()[0];
const base = MAIN.base, hi = MAIN.base.add(MAIN.size);
send({t:'info', m:'module '+MAIN.name+' base='+base+' size=0x'+MAIN.size.toString(16)});

function scanAll(name, bytesPattern){
  let count=0;
  const ranges = Process.enumerateRanges('r--').concat(Process.enumerateRanges('rw-'));
  for(const r of ranges){
    if(r.size > 0x4000000) continue;
    let hits;
    try{ hits = Memory.scanSync(r.base, r.size, bytesPattern); }catch(e){ continue; }
    for(const h of hits){
      const a=h.address, inMod = a.compare(base)>=0 && a.compare(hi)<0;
      send({t:'hit', m:name+'  @ '+a+(inMod?(' (+0x'+a.sub(base).toString(16)+' in module)'):(' ['+r.protection+']'))});
      if(++count>=12) return;
    }
  }
  if(count===0) send({t:'hit', m:name+'  (not found)'});
}

function strPat(s){ return s.split('').map(c=>('0'+c.charCodeAt(0).toString(16)).slice(-2)).join(' '); }

send({t:'info', m:'scanning...'});
scanAll('Urk#nI7ni', strPat('Urk#nI7ni'));
scanAll('NexonInc',  strPat('NexonInc'));
scanAll('Peasant',   strPat('Peasant'));
scanAll('experience',strPat('experience'));
if('__NAME__'.length) scanAll('name:__NAME__', strPat('__NAME__'));
send({t:'done', m:'scan complete'});
""".replace("__MOD__", MOD)


def main():
    if "--attach" not in sys.argv:
        print("run with --attach (game must be running)"); return
    name = ""
    if "--name" in sys.argv:
        name = sys.argv[sys.argv.index("--name") + 1]
    js = JS.replace("__NAME__", name)

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
        print({"info": "[i]", "hit": ">>", "done": "[done]"}.get(p.get("t"), "?"), p["m"])

    print(f"scanning {len(procs)} {MOD} process(es): {[p.pid for p in procs]}")
    for p in procs:
        try:
            s = dev.attach(p.pid)
            sc = s.create_script(js)
            sc.on("message", on_message)
            sc.load()
        except Exception as e:
            print(f"  attach {p.pid} failed: {e}")
    # one-shot: give scans time, then exit
    import time
    for _ in range(60):
        time.sleep(0.5)
        if done["n"] >= len(procs):
            break
    print("\n(scan done)")


if __name__ == "__main__":
    main()
