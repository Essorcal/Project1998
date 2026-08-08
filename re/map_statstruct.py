"""Map the stats struct fully (level/might/will/grace/ac/dam/maxhp/maxmana/tnl) and find the
STATIC pointer to its base so it's reusable every session (like the position struct)."""
import frida
GT = {16: "LEVEL", 6236: "TNL", 17: "grace", 11: "might", 9: "will", 69: "ac", 1: "dam",
      911: "maxhp", 449: "maxmana", 31204: "exp"}
# tnl location anchor from find_statstruct.py
TNL_AT = 0x4d847748
dev = frida.get_local_device()
pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == "nexustk.exe"]
if not pids: raise SystemExit("no client")
JS = r"""
rpc.exports = {
  base: function(){ const m=Process.findModuleByName('NexusTK.exe'); return {base:m.base.toString(),size:m.size}; },
  ru32: function(a){ try{return ptr(a).readU32();}catch(e){return null;} },
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

# 1) Dump a wide window around the tnl anchor and label every GT hit
print("=== full struct dump around tnl anchor (offset from tnl) ===")
lab = {}
for off in range(-0x80, 0x40, 4):
    v = ex.ru32(hex(TNL_AT + off))
    if v in GT:
        lab[off] = (v, GT[v])
        print(f"  tnl{off:+#06x} (abs {hex(TNL_AT+off)}): {v:>8}  <- {GT[v]}")

# 2) Find a struct BASE: try pointers to a range of addresses at/below the tnl anchor
print("\n=== searching for a STATIC pointer to this struct's base ===")
found = False
for delta in range(0x0, -0x84, -4):     # candidate base = tnl_at + delta (delta<=0)
    cand = TNL_AT + delta
    ptrs = ex.ptrsto(cand, 60)
    static = [int(a,16) for a in ptrs if base <= int(a,16) < base+size]
    if static:
        rel_tnl = -delta                 # tnl offset from this base
        print(f"  base={hex(cand)} (tnl@+{hex(rel_tnl)}): static ptr(s): "
              + ", ".join(f"{hex(a)}(RVA+{hex(a-base)})" for a in static))
        # show the field offsets relative to THIS base
        fields = {GT[v]: (o + rel_tnl) for o, (v, n) in lab.items()}
        print(f"    field offsets from base: " +
              ", ".join(f"{n}@+{hex(off)}" for n, off in sorted(fields.items(), key=lambda x: x[1])))
        found = True
        break
if not found:
    print("  no direct in-module pointer to the struct base; may be multi-hop.")
    # show any heap holders for manual chain-tracing
    ptrs = ex.ptrsto(TNL_AT, 20)
    for a in ptrs[:10]:
        print(f"    holder {a} inmod={base<=int(a,16)<base+size}")
