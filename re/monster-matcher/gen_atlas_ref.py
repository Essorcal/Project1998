"""Build atlas_ref.json: the colored, named ground-truth reference set.

For every Atlas monster GIF we extract:
  file, name (parsed+pretty), colorword, w,h (content bbox), palette (RGBs used),
  gif (raw base64 data URI, real colors), and a normalized shape mask for matching.

Then for each of our 327 look-id sprites we compute the same shape mask and rank
the Atlas GIFs by dimension-gated IoU (trying the GIF both as-is and flipped, since
Atlas art may face the opposite way). Output includes per-look-id ranked candidates.
"""
import json, base64, os, glob, re
from PIL import Image

# ---- atlas.json: name -> exp/type (for auto-fill) ----
atlas_meta = {}
for m in json.load(open("atlas.json")):
    atlas_meta[re.sub(r"[^a-z0-9]", "", m["name"].lower())] = m

COLOR_WORDS = ("blue red green yellow black white gold golden silver brown gray grey "
               "pink purple orange dark albino arctic").split()

def pretty(fn):
    # bluerooster1 -> "Blue rooster"; split camel-ish by known color prefixes
    s = re.sub(r"1$", "", fn)  # trailing frame index
    low = s.lower()
    for c in COLOR_WORDS:
        if low.startswith(c) and len(low) > len(c):
            return c.capitalize() + " " + low[len(c):]
    return s

def colorword(fn):
    low = re.sub(r"1$", "", fn).lower()
    for c in COLOR_WORDS:
        if low.startswith(c):
            return c
    return ""

def content_mask(rgba, w, h):
    px = [1 if p[3] > 40 else 0 for p in rgba]
    xs = [i % w for i, v in enumerate(px) if v]
    ys = [i // w for i, v in enumerate(px) if v]
    if not xs:
        return None, None
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    cw, ch = x1 - x0 + 1, y1 - y0 + 1
    m = Image.new("L", (cw, ch), 0)
    pp = m.load()
    for i, v in enumerate(px):
        if v:
            pp[i % w - x0, i // w - y0] = 255
    return m, (cw, ch)

# ---- process GIFs ----
refs = []
for p in sorted(glob.glob("atlas_img/*.gif")):
    if os.path.getsize(p) < 20:
        continue
    fn = os.path.basename(p)[:-4]
    try:
        im = Image.open(p).convert("RGBA")
    except Exception:
        continue
    w, h = im.size
    data = list(im.getdata())
    m, dim = content_mask(data, w, h)
    if not m:
        continue
    # palette actually used (opaque pixels), most-common first
    cnt = {}
    for pr in data:
        if pr[3] > 40:
            cnt[pr[:3]] = cnt.get(pr[:3], 0) + 1
    pal = [list(c) for c, _ in sorted(cnt.items(), key=lambda x: -x[1])]
    nm = re.sub(r"[^a-z0-9]", "", pretty(fn).lower())
    meta = atlas_meta.get(nm) or atlas_meta.get(re.sub(r"[^a-z0-9]", "", fn.lower()))
    with open(p, "rb") as f:
        b64 = base64.b64encode(f.read()).decode()
    refs.append({
        "file": fn,
        "name": pretty(fn),
        "color": colorword(fn),
        "w": dim[0], "h": dim[1],
        "pal": pal[:24],
        "gif": "data:image/gif;base64," + b64,
        "exp": (meta or {}).get("exp"),
        "mtype": (meta or {}).get("type"),
        "_mask": m,  # kept for matching, stripped before save
    })

print(f"{len(refs)} atlas refs")

# ---- sprite masks ----
def sprite_mask(e):
    raw = base64.b64decode(e["idx"])
    w, h = e["fw"], e["fh"]
    px = [1 if b else 0 for b in raw[: w * h]]
    return content_mask([(0, 0, 0, 255 if v else 0) for v in px], w, h)

def iou(gm, sw, sh, sa):
    g2 = gm.resize((sw, sh), Image.NEAREST)
    ga = [1 if v > 127 else 0 for v in g2.getdata()]
    inter = sum(1 for a, b in zip(ga, sa) if a and b)
    uni = sum(1 for a, b in zip(ga, sa) if a or b)
    return inter / uni if uni else 0.0

def score(gm, gdim, sm, sdim):
    gw, gh = gdim; sw, sh = sdim
    dsim = (min(gw, sw) / max(gw, sw)) * (min(gh, sh) / max(gh, sh))
    if dsim < 0.45:
        return 0.0
    sa = [1 if v > 127 else 0 for v in sm.getdata()]
    best = iou(gm, sw, sh, sa)
    best = max(best, iou(gm.transpose(Image.FLIP_LEFT_RIGHT), sw, sh, sa))
    return round(best * dsim, 3)

mons = json.load(open("monsters.json"))
cand = {}
for e in mons:
    sm, sdim = sprite_mask(e)
    if not sm:
        cand[e["id"]] = []
        continue
    scored = []
    for idx, r in enumerate(refs):
        s = score(r["_mask"], (r["w"], r["h"]), sm, sdim)
        if s > 0.30:
            scored.append((s, idx))
    scored.sort(reverse=True)
    cand[str(e["id"])] = [{"i": idx, "s": s} for s, idx in scored[:14]]

for r in refs:
    del r["_mask"]

json.dump({"refs": refs, "cand": cand},
          open("atlas_ref.json", "w"), separators=(",", ":"))
print("wrote atlas_ref.json", os.path.getsize("atlas_ref.json"), "bytes")
