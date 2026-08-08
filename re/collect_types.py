"""Collect named entity structs over time (bot is killing -> drops appear), then diff the
raw struct bytes between ITEM-named and MOB-named entities to find the type/layer field."""
import sys, time, collections
sys.path.insert(0, ".")
import nexus_bot as NB, nexus_agent as NA

JS = r"""
let THIS=null;
Interceptor.attach(ptr('0x576660'),{onEnter(args){if(!THIS){THIS=this.context.ecx;
 send({t:'this'});Interceptor.detachAll();}}});
const fn=new NativeFunction(ptr('0x576660'),'int',['pointer','pointer','uint'],'thiscall');
rpc.exports={ready:function(){return !!THIS;},
 ask:function(x,y){if(!THIS)return false;const m=Memory.alloc(6);m.writeU8(0x0a);
  m.add(1).writeU8((x>>8)&0xff);m.add(2).writeU8(x&0xff);
  m.add(3).writeU8((y>>8)&0xff);m.add(4).writeU8(y&0xff);m.add(5).writeU8(0);
  try{fn(THIS,m,6);return true;}catch(e){return false;}}};
"""
agent=NA.Agent(); world=NB.World(agent); last=[]
def pump(msg,data):
    if msg.get("type")!="send": return
    p=msg["payload"]
    if p.get("t")=="this": return
    if p.get("op")==0x0a and p.get("t")!="send":
        b=bytes(int(x,16) for x in p["hex"].split())
        try:
            if 0<b[3]<40: last.append((b[1], b[4:4+b[3]].decode('latin-1')))
        except Exception: pass
    try: world.on_packet(p) if p.get("t")!="send" else world.on_send(p)
    except Exception: pass
s,sc=NB.attach(pump); ex=sc.exports_sync; world.mem_ex=ex
sc2=s.create_script(JS); sc2.on("message",pump); sc2.load(); ex2=sc2.exports_sync
for _ in range(60):
    if ex2.ready(): break
    time.sleep(0.5)
def base_of(uid):
    for hs in ex.scanu32(uid,60):
        a=int(hs,16)
        if ex.ru32(hex(a-0xF8))==NB.ENT_VTABLE: return a-0xF8
    return None
seen={}
t_end=time.time()+150
while time.time()<t_end:
    me=world.read_self_now()
    if not me: time.sleep(1); continue
    if world.pool is None: world.bootstrap_pool()
    rows=ex.enument(NB.ENT_VTABLE,*world.pool) if world.pool else []
    bytile=collections.defaultdict(list)
    for u,x,y in rows: bytile[(x,y)].append(u)
    cand=[(abs(x-me[0])+abs(y-me[1]),u,x,y) for (x,y),us in bytile.items()
          if len(us)==1 for u in us]
    cand.sort()
    for d,u,x,y in cand[:4]:
        if u in seen or d>10: continue
        n0=len(last); ex2.ask(x,y); time.sleep(0.8)
        if len(last)<=n0: continue
        kind,nm=last[-1]
        if kind!=2: continue
        b=base_of(u)
        if not b: continue
        h=ex.readbytes(hex(b),0x220)
        if h:
            seen[u]=(nm,bytes.fromhex(h))
            print(f"  {nm!r} eid={u} ({x},{y})", flush=True)
print(f"\ncollected {len(seen)} named structs")
ITEMWORDS=("meat","ginseng","coin","gold","potion","herb","leaf","fur","pelt","skin","bone",
           "egg","mushroom","flower","root","seed","stone","wood","scale","feather","claw","tooth")
items={u:v for u,v in seen.items() if any(w in v[0].lower() for w in ITEMWORDS)}
mobs={u:v for u,v in seen.items() if u not in items}
print(f"items={len(items)} {[v[0] for v in items.values()]}")
print(f"mobs ={len(mobs)} {sorted(set(v[0] for v in mobs.values()))}")
if items and mobs:
    print("\noffsets where ITEM values and MOB values never overlap:")
    hits=0
    for off in range(0,0x21c):
        iv={v[1][off] for v in items.values()}
        mv={v[1][off] for v in mobs.values()}
        if iv and mv and not (iv & mv):
            print(f"  +{off:#05x}: items={sorted(iv)} mobs={sorted(mv)}")
            hits+=1
            if hits>14: break
    if not hits: print("  (none at byte level)")
else:
    print("\nnot enough of both classes to diff")
s.detach()
