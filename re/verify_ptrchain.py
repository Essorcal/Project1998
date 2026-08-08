"""Verify TkMemory static pointer chains resolve on THIS client (7.5.2.0).
Self pos/vita struct: root static -> deref -> +0xFC=X, +0x100=Y, +0x104=vitaCur ...
We know the live struct base should be 0x4d11cbf0 (found by GT scan: X@0x4d11ccec=94)."""
import frida, sys
MOD = "NexusTK.exe"
dev = frida.get_local_device()
pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == MOD.lower()]
if not pids:
    print("no client"); sys.exit(1)
JS = r"""
rpc.exports = {
  base: function(){ const m=Process.findModuleByName('NexusTK.exe'); return m?m.base.toString():null; },
  ru32: function(a){ try{return ptr(a).readU32();}catch(e){return null;} },
  ru16: function(a){ try{return ptr(a).readU16();}catch(e){return null;} },
  ru8:  function(a){ try{return ptr(a).readU8();}catch(e){return null;} },
  rstr: function(a,n){ try{return ptr(a).readUtf8String(n);}catch(e){return null;} }
};
"""
s = dev.attach(pids[0]); sc = s.create_script(JS); sc.load(); ex = sc.exports_sync
base = int(ex.base(), 16)
print(f"module base = {hex(base)}")

def deref(static_abs):
    return ex.ru32(hex(static_abs))

# Candidate roots for the SELF struct pointer
roots = {
    "abs 0x6FE238": 0x6FE238,
    "mod+0x29B4D4": base + 0x29B4D4,
    "mod+0x29B4A4(map)": base + 0x29B4A4,
    "abs 0x6FE168": 0x6FE168,
}
print("\n=== resolving SELF-struct roots (want a ptr whose +0xFC = X=94, +0x104=911) ===")
for name, r in roots.items():
    p = deref(r)
    if not p:
        print(f"  {name}: [{hex(r)}] -> null"); continue
    x = ex.ru32(hex(p + 0xFC)); y = ex.ru32(hex(p + 0x100))
    vc = ex.ru32(hex(p + 0x104)); vm = ex.ru32(hex(p + 0x108))
    mc = ex.ru32(hex(p + 0x10C)); mm = ex.ru32(hex(p + 0x110))
    exp = ex.ru32(hex(p + 0x114))
    print(f"  {name}: [{hex(r)}]->{hex(p)}  X={x} Y={y} vitaCur={vc} vitaMax={vm} manaCur={mc} manaMax={mm} exp={exp}")

# Level lives via 0x6FDB3C + 0x280
print("\n=== level root 0x6FDB3C +0x280 ===")
pl = deref(0x6FDB3C)
if pl:
    print(f"  [0x6FDB3C]->{hex(pl)}  level@+0x280 = {ex.ru32(hex(pl+0x280))}")
pl2 = deref(base + 0x1FDB3C)
if pl2:
    print(f"  [mod+0x1FDB3C]->{hex(pl2)}  level@+0x280 = {ex.ru32(hex(pl2+0x280))}")

# Entity table root 0x29BF2C -> deref -> +0x100=uid,+0x104=X,+0x108=Y,+0x12E=name
print("\n=== ENTITY root mod+0x29BF2C (want mob X/Y/uid/name) ===")
for name, r in {"mod+0x29BF2C": base+0x29BF2C, "abs 0x6FE61C": 0x6FE61C}.items():
    p = deref(r)
    if not p:
        print(f"  {name}: null"); continue
    uid = ex.ru32(hex(p+0x100)); x = ex.ru32(hex(p+0x104)); y = ex.ru32(hex(p+0x108))
    nm = ex.rstr(hex(p+0x12E), 32); dr = ex.ru8(hex(p+0x1C9))
    print(f"  {name}: [{hex(r)}]->{hex(p)}  uid={uid} X={x} Y={y} dir={dr} name={nm!r}")

# Entity count 0x27A754 -> [0x424,0x38,0xC]
print("\n=== ENTITY count mod+0x27A754 [0x424,0x38,0xC] ===")
for name, r in {"mod+0x27A754": base+0x27A754, "abs 0x6DD4AC": 0x6DD4AC}.items():
    p = deref(r)
    if not p: print(f"  {name}: null"); continue
    p2 = ex.ru32(hex(p+0x424))
    if not p2: print(f"  {name}: ->{hex(p)} +0x424 null"); continue
    p3 = ex.ru32(hex(p2+0x38))
    if not p3: print(f"  {name}: +0x38 null"); continue
    cnt = ex.ru32(hex(p3+0xC))
    print(f"  {name}: count = {cnt}")
