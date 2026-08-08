"""Reusable: read self position + vitals via the static pointer chain (no scan, no wiggle).
  root = [BASE + 0x29b4e4];  X=root+0xFC Y=+0x100 vitaCur=+0x104 vitaMax=+0x108
  manaCur=+0x10C manaMax=+0x110 exp=+0x114 level(u16)=+0x118
Run standalone to watch it live (prints every 0.5s)."""
import frida, sys, time

SELF_ROOT_RVA = 0x29b4e4
OFF = dict(x=0xFC, y=0x100, vc=0x104, vm=0x108, mc=0x10C, mm=0x110, exp=0x114, gold=0x11C)
LVL_OFF = 0x118  # u16

JS = r"""
rpc.exports = {
  base: function(){ const m=Process.findModuleByName('NexusTK.exe'); return m.base.toString(); },
  ru32: function(a){ try{return ptr(a).readU32();}catch(e){return null;} },
  ru16: function(a){ try{return ptr(a).readU16();}catch(e){return null;} }
};
"""

def attach():
    dev = frida.get_local_device()
    pids = [p.pid for p in dev.enumerate_processes() if p.name.lower() == "nexustk.exe"]
    if not pids:
        raise SystemExit("no client")
    s = dev.attach(pids[0]); sc = s.create_script(JS); sc.load()
    return sc, sc.exports_sync

def read_self(ex, base):
    root = ex.ru32(hex(base + SELF_ROOT_RVA))
    if not root or root < 0x100000:
        return None
    g = lambda o: ex.ru32(hex(root + o))
    v = {k: g(o) for k, o in OFF.items()}
    v["lvl"] = ex.ru16(hex(root + LVL_OFF))
    v["root"] = root
    # sanity: plausible player
    if not v["x"] or not (1 <= v["x"] <= 500) or not v["vm"] or v["vc"] > v["vm"]:
        return None
    return v

if __name__ == "__main__":
    sc, ex = attach()
    base = int(ex.base(), 16)
    print(f"base={hex(base)} root_ptr@{hex(base+SELF_ROOT_RVA)}")
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 20
    for _ in range(n):
        v = read_self(ex, base)
        if v:
            print(f"pos=({v['x']},{v['y']})  hp={v['vc']}/{v['vm']}  mp={v['mc']}/{v['mm']}  "
                  f"exp={v['exp']} lvl={v['lvl']}  root={hex(v['root'])}")
        else:
            print("read failed / implausible")
        time.sleep(0.5)
