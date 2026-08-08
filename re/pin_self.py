"""Pin the REAL player struct by scanning for a unique/known value (exp, else a given x,y),
validate the TkMemory layout around it, derive root R, then find the STATIC module pointer
that holds R (no-ASLR => a permanent anchor).  Usage: python pin_self.py [exp] [x] [y]"""
import frida, sys
MOD = "NexusTK.exe"
EXP = int(sys.argv[1]) if len(sys.argv) > 1 else None
GX  = int(sys.argv[2]) if len(sys.argv) > 2 else None
GY  = int(sys.argv[3]) if len(sys.argv) > 3 else None

dev = frida.get_local_device()
pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
if not pids:
    print("no client"); sys.exit(1)
JS = r"""
rpc.exports = {
  base: function(){ const m=Process.findModuleByName('NexusTK.exe'); return {base:m.base.toString(), size:m.size}; },
  ru32: function(a){ try{return ptr(a).readU32();}catch(e){return null;} },
  ru16: function(a){ try{return ptr(a).readU16();}catch(e){return null;} },
  scan: function(val, cap){
    const b=[val&0xff,(val>>8)&0xff,(val>>16)&0xff,(val>>24)&0xff];
    const p=b.map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    const out=[]; let rs; try{rs=Process.enumerateRanges('rw-');}catch(e){return out;}
    for(const r of rs){ let ms; try{ms=Memory.scanSync(r.base,r.size,p);}catch(e){continue;}
      for(const m of ms){out.push(m.address.toString()); if(out.length>=cap)return out;} }
    return out;
  },
  ptrsto: function(val, cap){
    const b=[val&0xff,(val>>8)&0xff,(val>>16)&0xff,(val>>24)&0xff];
    const p=b.map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    const out=[]; let rs;
    try{rs=Process.enumerateRanges('r--').concat(Process.enumerateRanges('rw-'));}catch(e){return out;}
    for(const r of rs){ let ms; try{ms=Memory.scanSync(r.base,r.size,p);}catch(e){continue;}
      for(const m of ms){out.push(m.address.toString()); if(out.length>=cap)return out;} }
    return out;
  }
};
"""
s = dev.attach(pids[0]); sc = s.create_script(JS); sc.load(); ex = sc.exports_sync
mb = ex.base(); base = int(mb["base"], 16); size = mb["size"]
print(f"module base={hex(base)} size={hex(size)} end={hex(base+size)}")

def read_struct(R):
    g = lambda o: ex.ru32(hex(R + o))
    return dict(x=g(0xFC), y=g(0x100), vc=g(0x104), vm=g(0x108),
               mc=g(0x10C), mm=g(0x110), exp=g(0x114), lvl=ex.ru16(hex(R + 0x118)), gold=g(0x11C))

roots = []
if EXP:
    print(f"\nscanning for exp=={EXP} (unique player value) ...")
    for a in ex.scan(EXP, 200):
        R = int(a, 16) - 0x114                     # exp sits at R+0x114
        st = read_struct(R)
        if st["x"] and 1 <= st["x"] <= 500 and st["vc"] and st["vc"] <= st["vm"] and st["lvl"] and 1 <= st["lvl"] <= 99:
            roots.append((R, st))
if not roots and GX is not None:
    print(f"\nscanning for X=={GX} with Y=={GY} at +4 ...")
    for a in ex.scan(GX, 100000):
        R = int(a, 16) - 0xFC                       # X sits at R+0xFC
        if ex.ru32(hex(R + 0x100)) == GY:
            roots.append((R, read_struct(R)))

print(f"\n{len(roots)} validated player struct(s):")
for R, st in roots:
    print(f"  root={hex(R)}  X={st['x']} Y={st['y']} vita={st['vc']}/{st['vm']} "
          f"mana={st['mc']}/{st['mm']} exp={st['exp']} lvl={st['lvl']} gold={st['gold']}")
if not roots:
    print("no struct matched. Pass the CURRENT exp or x y from the HUD."); sys.exit(0)

R = roots[0][0]
print(f"\nlocating STATIC pointer(s) to root {hex(R)} ...")
ptrs = ex.ptrsto(R, 200)
inmod = [int(a, 16) for a in ptrs if base <= int(a, 16) < base + size]
if inmod:
    for ap in inmod:
        print(f"  STATIC  {hex(ap)}  RVA=+{hex(ap - base)}   (self X = [base+{hex(ap-base)}] + 0xFC)")
else:
    print(f"  no direct in-module holder among {len(ptrs)} pointers; nearest holders:")
    for a in ptrs[:15]:
        ap = int(a, 16)
        print(f"    {hex(ap)}  inmod={base <= ap < base+size}")
    # try a 2-hop: find pointers to those holders that ARE in-module
    if ptrs:
        h = int(ptrs[0], 16)
        print(f"  looking for in-module pointers to holder {hex(h)} (2-hop) ...")
        for a in ex.ptrsto(h, 100):
            ap = int(a, 16)
            if base <= ap < base + size:
                print(f"    STATIC(2hop) {hex(ap)} RVA=+{hex(ap-base)} -> holder -> root")
