#!/usr/bin/env python
"""
Locate the live 7.5.2.0 client's packet DECRYPT / parse routine at runtime, so we can
hook it and read plaintext for every opcode (bypassing the table cipher entirely).

Idea: we already know WHERE received ciphertext lands (the WSARecv buffer, captured at
GetQueuedCompletionStatusEx completion). The very next thing the client does with those
bytes is read them to decrypt/frame them. So: right after a receive completes, arm a
software watchpoint (page PROT_NONE) on the buffer's first page; the first code to READ
it faults, and its backtrace is the decrypt/parse path. Same technique as frida_probe.py's
entity watchpoint, aimed at the recv buffer.

Run this while RECEIVING traffic (stand in the world / fight). It prints candidate READER
functions (RVA + backtrace), deduped. The RVA that consistently reads fresh packet bytes,
just before opcode dispatch, is the decrypt routine — we hook that next.

Usage:  python re/frida_find_decrypt.py --attach     (client already running)
"""
import sys, frida

MOD = "NexusTK.exe"

JS = r"""
'use strict';
function moduleByName(n){ return (typeof Process.findModuleByName==='function')?Process.findModuleByName(n):null; }
function ensureModule(n){ let m=moduleByName(n); if(!m){try{m=Module.load(n);}catch(e){}} return m; }
function findExport(dll,fn){
  const m=ensureModule(dll);
  if(m&&typeof m.findExportByName==='function'){const a=m.findExportByName(fn); if(a) return a;}
  if(typeof Module.findGlobalExportByName==='function') return Module.findGlobalExportByName(fn);
  return null;
}
const MAIN = moduleByName(MOD) || Process.enumerateModules()[0];
const base = MAIN.base, modLo = base, modHi = base.add(MAIN.size);
send({t:'info', m:'module '+MAIN.name+' base='+base+' size=0x'+MAIN.size.toString(16)});

function inMain(p){ return p.compare(modLo)>=0 && p.compare(modHi)<0; }

// ---- recv detection across ALL completion modes (sync WSARecv, recv, IOCP) ----
function firstWsabuf(lp){ return { len: lp.readU32(), buf: lp.add(4).readPointer() }; }
const pend = {};
const wr = findExport('ws2_32.dll','WSARecv');
if(wr) Interceptor.attach(wr,{
  onEnter(a){ try{ const wb=firstWsabuf(a[1]); this.buf=wb.buf; this.len=wb.len; this.lpN=a[3];
    const ov=a[5]; if(ov&&!ov.isNull()) pend[ov.toString()]={buf:wb.buf,len:wb.len}; }catch(e){} },
  onLeave(r){ try{ if(r.toInt32()!==0||this.buf===undefined) return;   // synchronous success
    const n=(this.lpN&&!this.lpN.isNull())?this.lpN.readU32():0;
    if(n>0 && n<8192) arm(this.buf,n); }catch(e){} }
});
const rv = findExport('ws2_32.dll','recv');
if(rv) Interceptor.attach(rv,{ onEnter(a){ this.buf=a[1]; },
  onLeave(r){ try{ const n=r.toInt32(); if(n>0&&n<8192) arm(this.buf,n); }catch(e){} }});
const gqs = findExport('kernel32.dll','GetQueuedCompletionStatus');
if(gqs) Interceptor.attach(gqs,{ onEnter(a){ this.pN=a[1]; this.ppOv=a[3]; },
  onLeave(r){ try{ if(!this.ppOv||this.ppOv.isNull()) return; const ov=this.ppOv.readPointer();
    const rec=pend[ov.toString()]; if(!rec) return; delete pend[ov.toString()];
    const n=this.pN.isNull()?0:this.pN.readU32(); if(n>0&&n<8192) arm(rec.buf,n); }catch(e){} }});

// ---- watchpoint machinery (from frida_probe.py) ----
const PAGE=4096;
const WATCH={};            // pageBase string -> {lo:number, hi:number}
let armed=0; const ARM_LIMIT=8;
const rearming={};
let faults=0; const FAULT_LIMIT=4000;
const seenPC={};
let readers=0;
function pageBaseOf(p){ const v=p.toUInt32(); return ptr(v-(v%PAGE)); }

function arm(buf, n){
  if(armed>=ARM_LIMIT) return;
  const lo=buf.toUInt32(), hi=lo+Math.min(n,512);   // watch just the first packet bytes
  const pb=pageBaseOf(buf).toString();
  if(WATCH[pb]) return;
  try{ Memory.protect(ptr(pb),PAGE,'---'); WATCH[pb]={lo:lo,hi:hi}; armed++;
    send({t:'info', m:'armed recv-buf watch @'+buf+' page='+pb+' ('+armed+'/'+ARM_LIMIT+')'}); }
  catch(e){ send({t:'info', m:'arm failed '+e}); }
}
function releaseAll(reason){
  for(const pb in WATCH){ try{Memory.protect(ptr(pb),PAGE,'rw-');}catch(e){} delete WATCH[pb]; }
  send({t:'done', m:'watchpoints released ('+reason+'), '+readers+' distinct readers found'});
}

Process.setExceptionHandler(function(d){
  try{
    if(d.type!=='access-violation'||!d.memory) return false;
    const pb=pageBaseOf(d.memory.address).toString();
    const w=WATCH[pb]; if(!w) return false;
    faults++;
    const a=d.memory.address.toUInt32();
    const inRegion = a>=w.lo && a<w.hi;
    const pc=d.context.pc, pcKey=pc.toString();
    if(inRegion && inMain(pc) && !seenPC[pcKey]){
      seenPC[pcKey]=1; readers++;
      let bt='';
      try{ bt=Thread.backtrace(d.context,Backtracer.ACCURATE).slice(0,10)
             .map(x=> inMain(x)?('+0x'+x.sub(base).toString(16)):x.toString()).join(' <- '); }
      catch(e){ bt='<no bt>'; }
      send({t:'reader', m:'READER #'+readers+'  pc=+0x'+pc.sub(base).toString(16)+
            '  op='+d.memory.operation+'  off=+0x'+(a-w.lo).toString(16)+'\n     bt: '+bt});
    }
    try{ Memory.protect(ptr(pb),PAGE,'rw-'); }catch(e){}   // let faulting insn complete
    if(!rearming[pb]){ rearming[pb]=true; setTimeout(function(){ rearming[pb]=false;
      if(WATCH[pb]){ try{Memory.protect(ptr(pb),PAGE,'---');}catch(e){} } },0); }
    if(faults>FAULT_LIMIT){ releaseAll('fault limit'); }
    return true;
  }catch(e){ return false; }
});

// ---- arm on each recv completion (main thread dequeues, then decrypts) ----
const gq = findExport('kernel32.dll','GetQueuedCompletionStatusEx');
if(gq) Interceptor.attach(gq,{
  onEnter(a){ this.entries=a[1]; this.pRem=a[3]; },
  onLeave(r){ try{ if(r.toInt32()===0||this.pRem.isNull()) return;
    const c=this.pRem.readU32();
    for(let i=0;i<c&&i<64;i++){ const e=this.entries.add(i*16); const ov=e.add(4).readPointer();
      const rec=pend[ov.toString()]; if(!rec) continue; delete pend[ov.toString()];
      const n=e.add(12).readU32();
      // skip the giant TLS/HTTP buffers; game packets are small
      if(n>0 && n<8192) arm(rec.buf, n);
    } }catch(e){} }
});
send({t:'info', m:'decrypt-finder installed — receive some packets (stand in world / fight)'});
""".replace("MOD", "'" + MOD + "'")


def main():
    dev = frida.get_local_device()
    if "--attach" not in sys.argv:
        print("launch the game first, then run with --attach"); return
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
    if not procs:
        print("no running", MOD); return

    def on_message(msg, data):
        if msg["type"] == "error":
            print("[frida-error]", msg.get("description")); return
        p = msg["payload"]
        tag = {"info": "[i]", "reader": ">>", "done": "[done]"}.get(p.get("t"), "?")
        print(tag, p["m"])

    print(f"instrumenting {len(procs)} {MOD} process(es): {[p.pid for p in procs]}")
    scripts = []
    for p in procs:
        try:
            session = dev.attach(p.pid)
            script = session.create_script(JS)
            script.on("message", on_message)
            script.load()
            scripts.append(script)
        except Exception as e:
            print(f"  attach {p.pid} failed: {e}")
    print("running — receive packets for ~30-60s (stand in world / fight), then Ctrl-C\n")
    try:
        sys.stdin.read()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
