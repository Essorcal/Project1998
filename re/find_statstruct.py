"""Find the memory struct the client writes the 0x08 statblock into: it holds level=16,
might=11, will=9, grace=17, ac=69, dam=1, maxhp=911, maxmana=449, tnl=6236. Anchor on the
distinctive tnl=6236 and maxhp=911, dump the neighbourhood, flag the ground-truth values."""
import frida
GT = {16: "LEVEL", 6236: "TNL", 17: "grace", 11: "might", 9: "will", 69: "ac", 1: "dam",
      911: "maxhp", 449: "mana", 31204: "exp", 6398: "tnl_old"}
dev = frida.get_local_device()
pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == "nexustk.exe"]
if not pids: raise SystemExit("no client")
JS = r"""
rpc.exports = {
  base: function(){ return Process.findModuleByName('NexusTK.exe').base.toString(); },
  ru32: function(a){ try{return ptr(a).readU32();}catch(e){return null;} },
  ru16: function(a){ try{return ptr(a).readU16();}catch(e){return null;} },
  ru8:  function(a){ try{return ptr(a).readU8();}catch(e){return null;} },
  scan: function(val, cap){
    const b=[val&0xff,(val>>8)&0xff,(val>>16)&0xff,(val>>24)&0xff];
    const p=b.map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    const out=[]; let rs; try{rs=Process.enumerateRanges('rw-');}catch(e){return out;}
    for(const r of rs){ let ms; try{ms=Memory.scanSync(r.base,r.size,p);}catch(e){continue;}
      for(const m of ms){out.push(m.address.toString()); if(out.length>=cap)return out;} }
    return out;
  },
  scan16: function(val, cap){
    const b=[val&0xff,(val>>8)&0xff];
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

def dump_around(addr, lo=-0x60, hi=0x60):
    hits = []
    for off in range(lo, hi, 4):
        v = ex.ru32(hex(addr + off))
        v16 = ex.ru16(hex(addr + off))
        f = GT.get(v) or (GT.get(v16, "") and GT.get(v16) + "(u16)")
        if f:
            hits.append(f"      +{off:+#06x}: u32={v} u16={v16} <- {f}")
    return hits

print("=== anchoring on tnl=6236 (u16 and u32), showing neighbourhoods with >=3 GT hits ===")
cands = set(ex.scan16(6236, 200))
for a in cands:
    ap = int(a, 16)
    # tnl might be u16 or u32; check both interpretations by dumping around
    hits = dump_around(ap - 0x40, 0, 0x80)
    gt_kinds = set(h.split("<- ")[1] for h in hits)
    if len(gt_kinds) >= 3:
        print(f"\n  @ {hex(ap)} (inmod={base<=ap<base+0x2b3000}):")
        for h in hits:
            print(h)

print("\n=== also: any struct with might=11,will=9,grace=17 contiguous-ish (u32 stride) ===")
# scan for grace=17 as u32, check might=11/will=9 within +/-0x20
for a in ex.scan(17, 3000):
    ap = int(a, 16)
    near = {}
    for off in range(-0x20, 0x24, 4):
        v = ex.ru32(hex(ap + off))
        if v in (11, 9, 16, 69):
            near[off] = v
    if len(set(near.values())) >= 3:   # grace + at least 2 of might/will/level/ac nearby
        print(f"  grace@{hex(ap)}: nearby {near}  (level/tnl check: "
              f"+? )  inmod={base<=ap<base+0x2b3000}")
