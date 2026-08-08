"""Find the self-struct ROOT via the full TkMemory-confirmed signature, then locate the
STATIC module pointer that holds it (no-ASLR => permanent anchor for all future sessions).
Layout from root R:  X=R+0xFC  Y=R+0x100  vitaCur=+0x104 vitaMax=+0x108
                     manaCur=+0x10C manaMax=+0x110 exp=+0x114 level(u16)=+0x118 gold=+0x11C"""
import frida, sys
MOD = "NexusTK.exe"
dev = frida.get_local_device()
pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
if not pids:
    print("no client"); sys.exit(1)
JS = r"""
rpc.exports = {
  base: function(){ const m=Process.findModuleByName('NexusTK.exe'); return {base:m.base.toString(), size:m.size}; },
  ru32: function(a){ try{return ptr(a).readU32();}catch(e){return null;} },
  ru16: function(a){ try{return ptr(a).readU16();}catch(e){return null;} },
  // scan ALL rw memory for the self-struct signature: X in 0..300, Y in 0..300,
  // vitaCur==vitaMax plausible, exp large, level 1..99 -> return candidate ROOTS
  findroot: function(){
    const out=[]; let rs; try{rs=Process.enumerateRanges('rw-');}catch(e){return out;}
    for(const r of rs){
      const b=parseInt(r.base.toString(),16);
      if(b<0x100000||r.size>16*1024*1024) continue;
      let a; try{a=new Uint8Array(r.base.readByteArray(r.size));}catch(e){continue;}
      const u32=(o)=>((a[o]|(a[o+1]<<8)|(a[o+2]<<16)|(a[o+3]<<24))>>>0);
      const u16=(o)=>(a[o]|(a[o+1]<<8));
      // scan for the X field then validate the rest at +0xFC..
      for(let o=0;o+0x120<=a.length;o+=4){
        const x=u32(o+0xFC); if(x<1||x>500) continue;
        const y=u32(o+0x100); if(y<1||y>500) continue;
        const vc=u32(o+0x104); const vm=u32(o+0x108);
        if(vc<20||vc>60000||vm<20||vm>60000||vc>vm) continue;
        const mc=u32(o+0x10C); const mm=u32(o+0x110);
        if(mm<20||mm>60000||mc>mm) continue;
        const exp=u32(o+0x114); if(exp<1000||exp>2000000000) continue;
        const lv=u16(o+0x118); if(lv<1||lv>99) continue;
        out.push({root:r.base.add(o).toString(), x:x, y:y, vc:vc, vm:vm, mc:mc, mm:mm, exp:exp, lv:lv});
        if(out.length>=40) return out;
      }
    }
    return out;
  },
  // find every rw address holding the u32 value == val (pointers to root)
  ptrsto: function(val, cap){
    const b=[val&0xff,(val>>8)&0xff,(val>>16)&0xff,(val>>24)&0xff];
    const p=b.map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    const out=[]; let rs; try{rs=Process.enumerateRanges('r--').concat(Process.enumerateRanges('rw-'));}catch(e){return out;}
    for(const r of rs){ let ms; try{ms=Memory.scanSync(r.base,r.size,p);}catch(e){continue;}
      for(const m of ms){out.push(m.address.toString()); if(out.length>=cap)return out;} }
    return out;
  }
};
"""
s = dev.attach(pids[0]); sc = s.create_script(JS); sc.load(); ex = sc.exports_sync
mb = ex.base(); base = int(mb["base"], 16); size = mb["size"]
print(f"module base={hex(base)} size={hex(size)} end={hex(base+size)}")

roots = ex.findroot()
print(f"\n{len(roots)} self-struct candidate(s):")
for r in roots:
    print(f"  root={r['root']}  X={r['x']} Y={r['y']} vita={r['vc']}/{r['vm']} mana={r['mc']}/{r['mm']} exp={r['exp']} lvl={r['lv']}")
if not roots:
    print("no root found (character may have moved/relogged)."); sys.exit(0)

# pick the one with the most plausible player values (highest exp, sane hp)
roots.sort(key=lambda r: -(r["exp"] if r["vc"] >= 15*r["lv"] else 0))
R = int(roots[0]["root"], 16)
print(f"\nchosen ROOT = {hex(R)}  -> X@{hex(R+0xFC)} Y@{hex(R+0x100)}")

# find the STATIC pointer holding R (must be inside the module image => permanent RVA)
ptrs = ex.ptrsto(R, 100)
print(f"\n{len(ptrs)} addresses hold ptr->root; those INSIDE the module image (static globals):")
static = []
for a in ptrs:
    ap = int(a, 16)
    inmod = base <= ap < base + size
    if inmod:
        static.append(ap)
        print(f"  {hex(ap)}  RVA=+{hex(ap-base)}  (STATIC - stable across sessions)")
if not static:
    print("  (none inside image directly -- may be a 1-hop pointer; showing all holders:)")
    for a in ptrs[:20]:
        ap=int(a,16); print(f"    {hex(ap)} inmod={base<=ap<base+size}")
