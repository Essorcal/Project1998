import random
D = {0:(0,-1), 1:(1,0), 2:(0,1), 3:(-1,0)}
def blk(walls,W,H): return lambda nx,ny: nx<0 or ny<0 or nx>=W or ny>=H or (nx,ny) in walls

def toward_dirs(dx,dy):
    vert = 2 if dy>0 else (0 if dy<0 else None)
    horz = 1 if dx>0 else (3 if dx<0 else None)
    if random.randint(0,2)>=1: return [d for d in (vert,horz) if d is not None]
    return [d for d in (horz,vert) if d is not None]

def make_stepper(mode):
    # state per mob: detour dir + remaining
    def stepper(state, mx,my,tx,ty,b):
        dx,dy=tx-mx,ty-my
        if dx==0 and dy==0: state['dl']=0; return mx,my
        def Step(d):
            ox,oy=D[d]; nx,ny=mx+ox,my+oy
            return None if b(nx,ny) else (nx,ny)
        # continue a committed run (modes 'old' and 'hybrid')
        if mode in ('old','hybrid') and state.get('dl',0)>0:
            r=Step(state['dd'])
            if r:
                state['dl']-=1
                return r
            state['dl']=0
        for d in toward_dirs(dx,dy):
            r=Step(d)
            if r:
                if mode in ('old','hybrid'): state['dl']=0
                return r
        # blocked
        if mode=='rtk':
            for _ in range(11):
                r=Step(random.randint(0,3))
                if r: return r
            return mx,my
        # committed-run fallbacks
        if mode=='old':
            sides=[]
            if dx==0: sides=[1,3]
            elif dy==0: sides=[0,2]
            random.shuffle(sides)
            for d in sides:
                r=Step(d)
                if r:
                    run=random.randint(1,3)+(random.randint(1,3) if random.randint(0,3)==0 else 0)
                    state['dd']=d; state['dl']=run-1
                    return r
            return mx,my
        if mode=='hybrid':
            # all four sides eligible (incl away), but COMMIT to a run so it travels laterally
            sides=[0,1,2,3]; random.shuffle(sides)
            for d in sides:
                r=Step(d)
                if r:
                    run=random.randint(1,3)+(random.randint(1,3) if random.randint(0,3)==0 else 0)
                    state['dd']=d; state['dl']=run-1
                    return r
            return mx,my
    return stepper

def sim(mode,name,W,H,start,target,walls,max_ticks=400,trials=300):
    b=blk(walls,W,H); step=make_stepper(mode); reached=0; tk=[]
    for _ in range(trials):
        mx,my=start; state={}
        for t in range(max_ticks):
            if abs(mx-target[0])+abs(my-target[1])==1 or (mx,my)==target:
                reached+=1; tk.append(t); break
            mx,my=step(state,mx,my,target[0],target[1],b)
    avg=sum(tk)/len(tk) if tk else float('nan')
    return reached/trials*100, avg

cases=[
 ("1-wide rock",12,12,(5,3),(5,8),{(5,5)},400),
 ("3-wide wall(card)",12,12,(5,3),(5,8),{(x,5) for x in range(4,7)},400),
 ("5-wide wall(card)",14,12,(6,3),(6,8),{(x,5) for x in range(4,9)},400),
 ("5-wide wall(diag)",14,12,(5,3),(7,8),{(x,5) for x in range(3,8)},400),
]
pit=set()
for x in range(2,7):
  for y in range(2,7):
    if x in (2,6) or y in (2,6): pit.add((x,y))
pit.discard((4,2))
cases.append(("pit far-exit",12,14,(4,4),(4,11),pit,800))

print(f"{'case':20s} {'RTK-literal':>14s} {'old-sideways':>14s} {'hybrid-commit':>14s}")
for name,W,H,s,t,walls,mt in cases:
    r1=sim('rtk',name,W,H,s,t,walls,mt); r2=sim('old',name,W,H,s,t,walls,mt); r3=sim('hybrid',name,W,H,s,t,walls,mt)
    print(f"{name:20s} {r1[0]:5.0f}% {r1[1]:6.1f}t {r2[0]:5.0f}% {r2[1]:6.1f}t {r3[0]:5.0f}% {r3[1]:6.1f}t")
