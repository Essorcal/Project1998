"""Dump the self-struct region and flag offsets holding known ground-truth values, to find
the REAL level (=16) and TNL (=6236) fields (my +0x118 guess was wrong -- it isn't level)."""
import frida, sys
GT = {16: "LEVEL", 6236: "TNL", 17: "grace", 11: "might", 9: "will", 69: "ac", 1: "dam",
      31204: "exp", 911: "maxhp/vita", 449: "mana", 97: "X", 26: "Y", 873: "curhp"}
dev = frida.get_local_device()
pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == "nexustk.exe"]
if not pids: raise SystemExit("no client")
JS = r"""
rpc.exports = {
  base: function(){ return Process.findModuleByName('NexusTK.exe').base.toString(); },
  ru32: function(a){ try{return ptr(a).readU32();}catch(e){return null;} },
  ru16: function(a){ try{return ptr(a).readU16();}catch(e){return null;} },
  ptrsto: function(val, cap){
    const b=[val&0xff,(val>>8)&0xff,(val>>16)&0xff,(val>>24)&0xff];
    const p=b.map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    const out=[]; let rs; try{rs=Process.enumerateRanges('rw-');}catch(e){return out;}
    for(const r of rs){ let ms; try{ms=Memory.scanSync(r.base,r.size,p);}catch(e){continue;}
      for(const m of ms){out.push(m.address.toString()); if(out.length>=cap)return out;} }
    return out;
  }
};
"""
s = dev.attach(pids[0]); sc = s.create_script(JS); sc.load(); ex = sc.exports_sync
base = int(ex.base(), 16)
root = ex.ru32(hex(base + 0x29b4e4))
print(f"root = {hex(root)}")
print("=== self struct u32 dump (offset : u32 : u16 : flag) ===")
for off in range(0x00, 0x200, 4):
    v = ex.ru32(hex(root + off))
    v16 = ex.ru16(hex(root + off))
    flag = GT.get(v, "") or (GT.get(v16, "") + "(u16)" if GT.get(v16) else "")
    if flag or off in (0xFC,0x100,0x104,0x108,0x10C,0x110,0x114,0x118,0x11C):
        print(f"  +{off:#05x} : {v:>10} : {v16:>6} : {flag}")
# level=16 is too common in-struct; find TNL=6236 anywhere, and level via a separate chain
print("\n=== TNL=6236 locations (rw) ===")
for a in ex.ptrsto(6236, 40):
    ap = int(a,16); print(f"  {hex(ap)}  inmod={base<=ap<base+0x2b3000}  (root+{hex(ap-root) if ap>root and ap-root<0x1000 else '-'})")
# TkMemory Self.Level: [0x6FDB3C]+0x280 (old build). Try our-build near-equivalents.
print("\n=== level via TkMemory-style separate roots (want 16) ===")
for rva in (0x1FDB3C, 0x1FDB4C, 0x29B4D4, 0x29ADFC):
    p = ex.ru32(hex(base+rva))
    if p and p>0x100000:
        print(f"  [base+{hex(rva)}]->{hex(p)}  +0x280={ex.ru32(hex(p+0x280))}  +0x27C={ex.ru32(hex(p+0x27c))}")
