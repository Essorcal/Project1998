"""Feasibility test v2: dimension-gated, native-scale shape match.
The Atlas GIF and our EPF frame are the same source art, so the content
bounding-box dims should nearly match. Gate on dims, then IoU."""
import json, base64, os
from PIL import Image

def crop_mask(mask, w, h):
    xs = [i % w for i, v in enumerate(mask) if v]
    ys = [i // w for i, v in enumerate(mask) if v]
    if not xs:
        return None
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    cw, ch = x1 - x0 + 1, y1 - y0 + 1
    img = Image.new("L", (cw, ch), 0)
    px = img.load()
    for i, v in enumerate(mask):
        if v:
            px[i % w - x0, i // w - y0] = 255
    return img  # cropped L mask

def sprite_mask(e):
    raw = base64.b64decode(e["idx"])
    w, h = e["fw"], e["fh"]
    return crop_mask([1 if b else 0 for b in raw[: w * h]], w, h)

def gif_mask(path):
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    return crop_mask([1 if p[3] > 40 else 0 for p in im.getdata()], w, h)

def score(gm, sm):
    gw, gh = gm.size
    sw, sh = sm.size
    # dimension similarity (0..1): penalize aspect/size mismatch
    dsim = (min(gw, sw) / max(gw, sw)) * (min(gh, sh) / max(gh, sh))
    if dsim < 0.5:
        return 0.0, dsim
    # resize gif mask to sprite dims, IoU
    g2 = gm.resize((sw, sh), Image.NEAREST)
    ga = [1 if v > 127 else 0 for v in g2.getdata()]
    sa = [1 if v > 127 else 0 for v in sm.getdata()]
    inter = sum(1 for a, b in zip(ga, sa) if a and b)
    uni = sum(1 for a, b in zip(ga, sa) if a or b)
    iou = inter / uni if uni else 0.0
    return iou * dsim, dsim

mons = json.load(open("monsters.json"))
sprites = [(e["id"], sprite_mask(e)) for e in mons]
sprites = [(i, m) for i, m in sprites if m]

tests = ["bunny", "squirrel", "mouse", "rat", "cat", "bluerooster", "bear",
         "bat", "deer", "buck", "snake", "wolf", "fox", "sheep", "chicken"]
for name in tests:
    p = f"atlas_img/{name}.gif"
    if not os.path.exists(p):
        continue
    gm = gif_mask(p)
    if not gm:
        continue
    scored = sorted(((score(gm, sm)[0], sid, sm.size) for sid, sm in sprites), reverse=True)
    top = scored[:5]
    print(f"{name}.gif {gm.size} -> " + ", ".join(f"id{sid}({sc:.2f},{sd})" for sc, sid, sd in top))
