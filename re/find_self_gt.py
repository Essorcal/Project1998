"""Find the true self-position field from ground truth (x,y) given by the user.
Scans for x as a u32 LE and keeps only addresses whose +4 u32 == y (the x,y=@A,@A+4
layout). Prints candidates with a little surrounding context so we can pick the real one."""
import sys, frida

MOD = "NexusTK.exe"
X = int(sys.argv[1]) if len(sys.argv) > 1 else 94
Y = int(sys.argv[2]) if len(sys.argv) > 2 else 25

JS = r"""
rpc.exports = {
  scanu32: function(v, cap){
    const b=[v&0xff,(v>>8)&0xff,(v>>16)&0xff,(v>>24)&0xff];
    const p=b.map(x=>('0'+x.toString(16)).slice(-2)).join(' ');
    const out=[]; let rs; try{rs=Process.enumerateRanges('rw-');}catch(e){return out;}
    for(const r of rs){ let ms; try{ms=Memory.scanSync(r.base,r.size,p);}catch(e){continue;}
      for(const m of ms){out.push(m.address.toString()); if(out.length>=cap)return out;} }
    return out;
  },
  ru32: function(a){ try{return ptr(a).readU32();}catch(e){return null;} },
  ru16: function(a){ try{return ptr(a).readU16();}catch(e){return null;} },
  readhex: function(a,n){ try{const b=new Uint8Array(ptr(a).readByteArray(n));
    return Array.from(b).map(x=>('0'+x.toString(16)).slice(-2)).join(' ');}catch(e){return '';} }
};
"""

dev = frida.get_local_device()
pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
if not pids:
    print("no client"); sys.exit(1)
s = dev.attach(pids[0])
sc = s.create_script(JS)
sc.load()
ex = sc.exports_sync

print(f"looking for x={X} with y={Y} at +4 ...")
hits = ex.scanu32(X, 200000)
print(f"{len(hits)} raw x=={X} matches; filtering by +4=={Y} ...")
pairs = []
for a in hits:
    ap = int(a, 16)
    y = ex.ru32(a_hex := hex(ap + 4))
    if y == Y:
        pairs.append(ap)
print(f"{len(pairs)} addresses have (x={X}, y={Y}) adjacent:")
for ap in pairs:
    # context: 16 bytes before .. 24 after, plus interpret neighbours
    ctx = ex.readhex(hex(ap - 8), 40)
    print(f"  {hex(ap)}  ctx[-8..+32]= {ctx}")
# Also: is either candidate also matched if we scan the OTHER order (y@A, x@A+4)?
print("\n(reverse-order check: y first, x at +4)")
hits2 = ex.scanu32(Y, 200000)
rev = [int(a,16) for a in hits2 if ex.ru32(hex(int(a,16)+4)) == X]
for ap in rev:
    print(f"  REV {hex(ap)}")
