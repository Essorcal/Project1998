"""Scrape nexusatlas.com (via the Wayback Machine) for every spell's ANIMATION GIF, so the client's
Effect.epf animations can be matched to spell names by eye.

Each spell on a class page is one <table border="0" width="100%" cellpadding="0" cellspacing="0">
block holding: the name (first <strong>), the stat list (Mana Cost / Spell Type / Aethers / Duration
/ Target), the level+material cost, an <img src=".../photo/spells/NAME.gif"> and -- usefully -- the
names of its Kwi-Sin / Ming-Ken / Ohaeng variants, which independently corroborates our spell
families.

photo/spells/none.gif is the site's explicit "this spell has NO animation" marker, so a spell
pointing at it must end up with an empty animation cell rather than a guess.

Usage:
  python scrape_atlas_spellfx.py pages    # fetch + cache the html
  python scrape_atlas_spellfx.py parse    # parse the cache -> fx/atlas_spells.json
  python scrape_atlas_spellfx.py gifs     # download every referenced gif -> fx/atlas_gifs/
  python scrape_atlas_spellfx.py sheets   # render each gif's frames -> fx/atlas_sheets/
"""
import json
import os
import re
import sys
import time
import urllib.request

BASE = "https://web.archive.org/web/20030206214253/http://www.nexusatlas.com"
ALIGN_BASE = "https://web.archive.org/web/20030206075829/http://www.nexusatlas.com"
FX = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fx")
CACHE = os.path.join(FX, "atlas_html")
GIFS = os.path.join(FX, "atlas_gifs")
SHEETS = os.path.join(FX, "atlas_sheets")
JSON = os.path.join(FX, "atlas_spells.json")

# The /spells/ pages that actually resolve. ilsan/eesan/samsan 404 in EVERY capture -- the subpath
# spell pages were never archived -- so subpath spells simply have no atlas animation to match.
SPELL_PAGES = ["mage", "poet", "rogue", "warrior", "other", "dog", "sasan"]
ALIGN_PAGES = [a + c for a in ("kwisin", "mingken", "ohaeng")
               for c in ("mage", "poet", "rogue", "warrior")]
UA = {"User-Agent": "Mozilla/5.0 (spell-fx research; contact via nexus server project)"}


def cdx_snapshot(path):
    """EARLIEST capture of nexusatlas.com/<path> that returned 200.

    Earliest, not newest, on purpose: the 2002-2003 captures are the 4.x-era site and serve their
    art out of /photo/spells/. The 2018 captures are the 6.0-era redesign serving /photo/spells60/ --
    a different, later animation set that must NOT be matched against the 4.95 client.
    """
    url = ("https://web.archive.org/cdx/search/cdx?url=nexusatlas.com/" + path +
           "&output=json&fl=timestamp,statuscode&filter=statuscode:200&limit=3")
    try:
        rows = json.loads(urllib.request.urlopen(
            urllib.request.Request(url, headers=UA), timeout=90).read())
    except Exception as e:
        print("  cdx failed", path, e)
        return None
    return rows[1][0] if len(rows) > 1 else None


def get(url, dest, throttle=2.0):
    """Fetch with a cache; Wayback is slow and rate-limits, so never re-fetch and always pause."""
    if os.path.exists(dest) and os.path.getsize(dest) > 0:
        return open(dest, "rb").read()
    for attempt in range(4):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=90) as r:
                b = r.read()
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            open(dest, "wb").write(b)
            time.sleep(throttle)
            return b
        except Exception as e:  # 429/503 from Wayback are routine; back off and retry
            print(f"  retry {attempt + 1} {url}: {e}")
            time.sleep(8 * (attempt + 1))
    print(f"  FAILED {url}")
    return None


def cmd_pages():
    os.makedirs(CACHE, exist_ok=True)
    for kind, pages in (("spells", SPELL_PAGES), ("alignment", ALIGN_PAGES)):
        for p in pages:
            dest = os.path.join(CACHE, f"{'class' if kind == 'spells' else 'align'}_{p}.html")
            if os.path.exists(dest) and os.path.getsize(dest) > 5000:
                continue
            ts = cdx_snapshot(f"{kind}/{p}.php")
            if not ts:
                print(f"{kind}/{p}: no 200 capture, skipping")
                continue
            print(f"{kind}/{p} @ {ts}")
            get(f"https://web.archive.org/web/{ts}/http://www.nexusatlas.com/{kind}/{p}.php", dest)


TAGS = re.compile(r"<[^>]+>")
WS = re.compile(r"\s+")


def text(s):
    return WS.sub(" ", TAGS.sub(" ", s)).strip()


# A trailing parenthetical is normally a flavour note on the SAME spell ("Kwi-Sin Chameleon (Bat
# Morph)") and must be stripped -- true for 30 of the 32 occurrences. The exception is where the
# parenthetical names a genuinely DIFFERENT spell that happens to share a table row; there the
# parenthetical is the spell this row is about (that row's level, cost and animation are Death
# Trap's, not Set Trap's).
# "Set Trap (Death Trap)" heads ONE table holding two tiers -- Set Trap at level 26 with dart.gif and
# Death Trap at level 88 with deathtrap.gif. Both images share that heading, so the parenthetical
# name is only correct for its OWN image; everything else under the heading is the base spell.
PAREN_IS_SPELL = {"set trap": ("Death Trap", "deathtrap.gif")}


def clean_name(s, gif=None):
    s = text(s).strip()
    m = re.match(r"^(.*?)\s*\((.*?)\)\s*$", s)
    if m and m.group(1).strip().lower() in PAREN_IS_SPELL:
        alt, alt_gif = PAREN_IS_SPELL[m.group(1).strip().lower()]
        s = alt if gif == alt_gif else m.group(1).strip()
    else:
        s = re.sub(r"\s*\(.*?\)\s*$", "", s).strip()
    return s if s and len(s) <= 40 and not re.fullmatch(r"Level\s*\d+", s) else None


def parse_block(b, is_align):
    """One spell table -> dict, or None if it isn't a spell block.

    The two page layouts differ and must be parsed differently:
      class page  -- name is the FIRST <strong>, then a stat list (Mana Cost / Spell Type / ...),
                     then the level cell and the image.
      alignment   -- base name is bare text, then <strong>Level N</strong>, then the image, then
                     the ALIGNED variant name in a trailing <strong>. Taking the first <strong>
                     here yields "Level 20" for every row, which is what a first pass produced.
    """
    img = re.search(r'photo/spells/([^"\']+?\.gif)', b, re.I)
    if not img:
        return None
    out = {"gif": img.group(1).lower()}
    m = re.search(r"Level\s*(\d+)", text(b))
    if m:
        out["level"] = int(m.group(1))

    if is_align:
        head = re.search(r'<div align="center">\s*<font[^>]*>\s*([^<]+?)\s*<br>', b, re.S | re.I)
        base = clean_name(head.group(1)) if head else None
        strongs = [clean_name(s) for s in re.findall(r"<strong>(.*?)</strong>", b, re.S | re.I)]
        strongs = [s for s in strongs if s]
        if not base and strongs:
            base = strongs[0]
        if not base:
            return None
        out["name"] = base
        # the aligned variant name is the LAST strong that isn't just the base name repeated
        out["aligned_name"] = strongs[-1] if strongs else base
        return out

    strong = re.search(r"<strong>(.*?)</strong>", b, re.S | re.I)
    name = clean_name(strong.group(1)) if strong else None
    if not name:
        return None
    out["name"] = name
    for label, key in (("Mana Cost", "mana"), ("Spell Type", "type"), ("Aethers", "aethers"),
                       ("Duration", "duration"), ("Target", "target")):
        m = re.search(re.escape(label) + r"\s*-\s*</?[^>]*>(.*?)(?:<br>|</font>\s*<br>)", b, re.S | re.I)
        if m:
            out[key] = text(m.group(1))
    # the class page footer names the three aligned variants -- keep as a family cross-check
    for al, key in (("Kwi-?Sin", "kwisin"), ("Ming-?Ken", "mingken"), ("Ohaeng", "ohaeng")):
        m = re.search(al + r"\s*</b>\s*</font>\s*<font[^>]*>\s*-\s*([^<]+)", b, re.S | re.I)
        if not m:
            m = re.search(al + r"[^-]{0,80}?-\s*([A-Za-z][A-Za-z' ]+)", text(b))
        if m:
            v = WS.sub(" ", m.group(1)).strip()
            if v and len(v) < 40:
                out[key] = v
    return out


STRONG = re.compile(r"<strong>(.*?)</strong>", re.S | re.I)
IMG = re.compile(r"photo/spells/([A-Za-z0-9_]+\.gif)", re.I)


def parse_by_image(h, is_align):
    """Anchor on each spell IMAGE and read its name from the surrounding markup.

    Splitting on the spell <table> tag looked right but silently fails where the page stops using
    that exact tag (the tail of the rogue page merges 8 spells into one block, so all 8 inherit the
    first block's name). Every spell has exactly one image, so anchoring on the image and walking
    outwards is stable across both page layouts.

    class page  -- the name is the nearest <strong> BEFORE the image that isn't "Level N".
    alignment   -- the base name precedes and the ALIGNED name is the first <strong> AFTER it.
    """
    out = []
    for m in IMG.finditer(h):
        gif = m.group(1).lower()
        before = h[max(0, m.start() - 4000):m.start()]
        names = [clean_name(s, gif) for s in STRONG.findall(before)]
        names = [n for n in names if n]
        if is_align:
            # base name is bare text just before the "Level N" strong
            head = re.findall(r'<div align="center">\s*<font[^>]*>\s*([^<>]+?)\s*<br>', before, re.S)
            base = clean_name(head[-1], gif) if head else (names[-1] if names else None)
            after = h[m.end():m.end() + 2500]
            al = [clean_name(s, gif) for s in STRONG.findall(after)]
            al = [a for a in al if a]
            if not base:
                continue
            out.append({"name": base, "aligned_name": al[0] if al else base, "gif": gif})
        else:
            if not names:
                continue
            out.append({"name": names[-1], "gif": gif})
        lv = re.findall(r"Level\s*(\d+)", text(before[-1200:]))
        if lv:
            out[-1]["level"] = int(lv[-1])
    return out


def cmd_parse():
    spells = []
    for fn in sorted(os.listdir(CACHE)):
        if not fn.endswith(".html") or fn == "align_index.html":
            continue
        src = fn[:-5]
        is_align = src.startswith("align_")
        h = open(os.path.join(CACHE, fn), encoding="latin1").read()
        n = 0
        for d in parse_by_image(h, is_align):
            if d:
                d["source"] = src
                if is_align:
                    for a in ("kwisin", "mingken", "ohaeng"):
                        if src[6:].startswith(a):
                            d["alignment"] = a
                    d["class"] = src[6:].replace(d.get("alignment", ""), "")
                else:
                    d["alignment"] = "unaligned"
                    d["class"] = src[6:]
                spells.append(d)
                n += 1
        print(f"{fn}: {n} spells")
    # de-dup identical (source,name)
    seen, out = set(), []
    for s in spells:
        k = (s["source"], s["name"].lower())
        if k in seen:
            continue
        seen.add(k)
        out.append(s)
    json.dump(out, open(JSON, "w"), indent=1)
    gifs = {s["gif"] for s in out}
    print(f"\n{len(out)} spells, {len(gifs)} distinct gifs "
          f"({sum(1 for s in out if s['gif'] == 'none.gif')} explicitly no-animation)")


def cmd_gifs():
    os.makedirs(GIFS, exist_ok=True)
    spells = json.load(open(JSON))
    gifs = sorted({s["gif"] for s in spells})
    for i, g in enumerate(gifs):
        dest = os.path.join(GIFS, g)
        if os.path.exists(dest):
            continue
        print(f"[{i + 1}/{len(gifs)}] {g}")
        get(f"https://web.archive.org/web/20030219040255im_/http://www.nexusatlas.com/photo/spells/{g}",
            dest, throttle=1.0)


def cmd_sheets():
    from PIL import Image, ImageSequence
    os.makedirs(SHEETS, exist_ok=True)
    for g in sorted(os.listdir(GIFS)):
        if not g.endswith(".gif"):
            continue
        try:
            im = Image.open(os.path.join(GIFS, g))
        except Exception as e:
            print("bad", g, e)
            continue
        frames = [f.convert("RGB") for f in ImageSequence.Iterator(im)]
        if not frames:
            continue
        w, h = frames[0].size
        scale = 2
        sheet = Image.new("RGB", (len(frames) * (w + 2) * scale, h * scale), (24, 24, 28))
        for k, f in enumerate(frames):
            f = f.resize((w * scale, h * scale), Image.NEAREST)
            sheet.paste(f, (k * (w + 2) * scale, 0))
        sheet.save(os.path.join(SHEETS, g.replace(".gif", ".png")))
    print("sheets ->", SHEETS)


def gif_frames(path, bg=(0, 0, 0)):
    """GIF frames composited onto BLACK. These animations are light-coloured glows on a transparent
    background; flattening to the default white makes them nearly invisible and impossible to
    compare against the client's effects, which render on black."""
    from PIL import Image, ImageSequence
    im = Image.open(path)
    out = []
    for f in ImageSequence.Iterator(im):
        rgba = f.convert("RGBA")
        flat = Image.new("RGB", rgba.size, bg)
        flat.paste(rgba, (0, 0), rgba)
        out.append(flat)
    return out


def cmd_montage(per=10, scale=2):
    """Labelled multi-gif sheets: one row per animation, frames left->right. These are what gets
    eyeballed against render_effects.py's client sheets."""
    from PIL import Image, ImageDraw
    out = os.path.join(FX, "atlas_montage")
    os.makedirs(out, exist_ok=True)
    names = sorted(f for f in os.listdir(GIFS) if f.endswith(".gif") and f != "none.gif")
    for g in range(0, len(names), per):
        chunk = names[g:g + per]
        rows = [(n, gif_frames(os.path.join(GIFS, n))) for n in chunk]
        cw = max(max((f.width for f in fs), default=1) for _, fs in rows)
        ch = max(max((f.height for f in fs), default=1) for _, fs in rows)
        ncol = max(len(fs) for _, fs in rows)
        lbl, pad = 150, 3
        W = lbl + ncol * (cw + pad) * scale
        H = sum(ch + pad for _ in rows) * scale
        sheet = Image.new("RGB", (W, H), (24, 24, 28))
        dr = ImageDraw.Draw(sheet)
        y = 0
        for n, fs in rows:
            for k, f in enumerate(fs):
                f = f.resize((f.width * scale, f.height * scale), Image.NEAREST)
                sheet.paste(f, (lbl + k * (cw + pad) * scale, y))
            dr.text((4, y + 4), n.replace(".gif", ""), fill=(255, 220, 120))
            y += (ch + pad) * scale
        p = os.path.join(out, f"atlas_{g // per:02d}.png")
        sheet.save(p)
        print("wrote", p, sheet.size, [n for n, _ in rows])


def cmd_grid(out=None, cell=150, cols=9):
    """One labelled max-blend thumbnail per animation -- the atlas counterpart to
    render_effects.py's client grid, so the two can be compared side by side."""
    from PIL import Image, ImageChops, ImageDraw
    out = out or os.path.join(FX, "atlas_grid.png")
    names = sorted(f for f in os.listdir(GIFS) if f.endswith(".gif") and f != "none.gif")
    sheet = Image.new("RGB", (cols * cell, ((len(names) + cols - 1) // cols) * (cell + 14)),
                      (20, 20, 24))
    dr = ImageDraw.Draw(sheet)
    for k, n in enumerate(names):
        fs = gif_frames(os.path.join(GIFS, n))
        im = fs[0]
        for f in fs[1:]:
            im = ImageChops.lighter(im, f)
        im.thumbnail((cell - 4, cell - 18), Image.LANCZOS)
        cx, cy = (k % cols) * cell, (k // cols) * (cell + 14)
        sheet.paste(im, (cx + (cell - im.width) // 2, cy + 14 + (cell - 14 - im.height) // 2))
        dr.text((cx + 3, cy + 2), f"{n[:-4]} {len(fs)}f", fill=(255, 210, 100))
    sheet.save(out)
    print(f"wrote {out} {sheet.size}")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    {"pages": cmd_pages, "parse": cmd_parse, "gifs": cmd_gifs, "sheets": cmd_sheets,
     "montage": cmd_montage, "grid": cmd_grid}.get(cmd, lambda: print(__doc__))()
