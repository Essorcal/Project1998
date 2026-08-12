"""Preview the 4.95 world-map screen (opcode 0x2e) exactly as the client draws it, so
WorldMapDests.csv DotX/DotY can be picked straight off the real artwork instead of being
scaled from RTK's 7.x coordinates (which are in a different image's pixel space and never
line up).

Background: the client builds the screen from "<bgName>.epf" -- bgName is "field10" for the
world map, which lives in the client's Inter.dat as a single 640x480 8bpp frame ("Map of the
Kingdom"). It ships no field10.pal; the game's shared Baram.pal (NexusTK.dat) is the right one.

BUTTON GEOMETRY -- from the client itself, NexusTK_local.exe 0x423600 (called per entry by the
world-map window's draw loop at 0x423500):
    esi   = textWidth(name) + 0x0c
    ecx   = fontHeight * 2
    top   = y0 - ecx/2 ;  bottom = top  + ecx
    left  = x0 - esi/2 ;  right  = left + esi
So DotX/DotY is the CENTRE of the label button, not its top-left corner. Put the number on the
spot you want the label sitting on and the client does the rest.

textWidth comes from the client's own bitmap font manager, which this script does not decode;
CHAR_W below is a proportional-width table calibrated against a live screenshot of five labels
(Buya / Kugnae / Arctic Land / Mythic Nexus / KaMing's Encampment) and reproduces their drawn
widths to within ~2px. That is only used for the drawn box outline and the off-screen warning --
the centre crosshair, which is what you actually aim, is exact.

Usage:
  python re/worldmap_plot.py                       # preview game-data/WorldMapDests.csv
  python re/worldmap_plot.py --grid                # ...with a 20px coordinate grid to read off
  python re/worldmap_plot.py --add "Name:320,240"  # try an extra dot without editing the CSV
  python re/worldmap_plot.py --move "Kugnae:310,250"   # override one row's dot
Writes re/worldmap_preview.png (and re/field10.png, the bare background).
"""
import os, struct, sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
DESTS_CSV = os.path.join(REPO, "game-data", "WorldMapDests.csv")

# The client install this project ships/patches. Override with P1998_CLIENT_DIR.
CLIENT_DIR = os.environ.get(
    "P1998_CLIENT_DIR", os.path.expandvars(r"%LOCALAPPDATA%\Project1998\game"))

FONT_H = 10          # client fontHeight; box height is 2*FONT_H (measured: 20px)
PAD    = 0x0c        # the +0xc the client adds to the text width

# Inner playable area of the field10 artwork -- outside this you are drawing on the wooden frame.
FRAME = (22, 18, 617, 461)          # left, top, right, bottom (inclusive)
# The baked-in "Map of the Kingdom" banner; a label centred under it is unreadable.
BANNER = (208, 4, 437, 42)

# Proportional widths, calibrated against a live 640x480 screenshot (see module docstring).
CHAR_W = {" ": 3, "'": 3, ".": 3, ",": 3, "-": 4,
          "i": 3, "j": 3, "l": 3, "f": 4, "r": 4, "t": 4,
          "m": 9, "w": 8, "M": 9, "W": 9, "I": 3, "J": 4, "L": 5}


def text_width(s):
    return sum(CHAR_W.get(c, 7 if c.isupper() else 6) for c in s)


def box_of(name, x0, y0):
    """The exact rect the client computes for a label centred on (x0, y0)."""
    w = text_width(name) + PAD
    h = FONT_H * 2
    left, top = x0 - w // 2, y0 - h // 2
    return left, top, left + w, top + h


# ---- client asset extraction (Nexon PAK, see pak_list.py / protocol doc #11a) ----------------
def pak_entries(path):
    data = open(path, "rb").read()
    (count,) = struct.unpack_from("<I", data, 0)
    out, pos = [], 4
    for _ in range(count):
        off, name = struct.unpack_from("<I13s", data, pos)
        out.append((off, name.split(b"\x00", 1)[0].decode("latin1")))
        pos += 17
    return data, [(o, n, (out[i + 1][0] if i + 1 < len(out) else len(data)) - o)
                  for i, (o, n) in enumerate(out)]


def pak_get(path, want):
    data, entries = pak_entries(path)
    for off, name, size in entries:
        if name.lower() == want.lower():
            return data[off:off + size]
    raise SystemExit(f"{want} not found in {path}")


def load_background(name="field10"):
    cache = os.path.join(HERE, f"{name}.png")
    if os.path.exists(cache):
        return Image.open(cache).convert("RGB")
    epf = pak_get(os.path.join(CLIENT_DIR, "Inter.dat"), f"{name}.epf")
    pal = pak_get(os.path.join(CLIENT_DIR, "NexusTK.dat"), "Baram.pal")
    _, w, h = struct.unpack_from("<HHH", epf, 0)
    im = Image.frombytes("P", (w, h), epf[12:12 + w * h])
    im.putpalette([b for c in range(256) for b in pal[32 + c * 4:32 + c * 4 + 3]])
    im = im.convert("RGB")
    im.save(cache)
    return im


def read_dests():
    rows = []
    with open(DESTS_CSV, encoding="utf-8") as fh:
        header = next(fh)
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            f = line.split(",")
            rows.append({"name": f[0], "map": f[1], "x": f[2], "y": f[3],
                         "dx": int(f[4]), "dy": int(f[5])})
    return rows


def main():
    args = sys.argv[1:]
    grid = "--grid" in args
    rows = read_dests()
    for i, a in enumerate(args):
        if a in ("--add", "--move") and i + 1 < len(args):
            name, coords = args[i + 1].split(":")
            dx, dy = (int(v) for v in coords.split(","))
            hit = next((r for r in rows if r["name"].lower() == name.lower()), None)
            if hit:
                hit["dx"], hit["dy"] = dx, dy
            else:
                rows.append({"name": name, "map": "?", "x": "?", "y": "?", "dx": dx, "dy": dy})

    im = load_background()
    d = ImageDraw.Draw(im, "RGBA")

    if grid:
        for x in range(0, im.width, 20):
            d.line([(x, 0), (x, im.height)], fill=(255, 255, 255, 60))
            if x % 100 == 0:
                d.line([(x, 0), (x, im.height)], fill=(255, 255, 0, 130))
                d.text((x + 2, 2), str(x), fill=(255, 255, 0))
        for y in range(0, im.height, 20):
            d.line([(0, y), (im.width, y)], fill=(255, 255, 255, 60))
            if y % 100 == 0:
                d.line([(0, y), (im.width, y)], fill=(255, 255, 0, 130))
                d.text((2, y + 2), str(y), fill=(255, 255, 0))

    print(f"{'name':22} {'dot':>10}  {'client box (l,t,r,b)':28} notes")
    for r in rows:
        l, t, rr, b = box_of(r["name"], r["dx"], r["dy"])
        notes = []
        if l < FRAME[0] or t < FRAME[1] or rr > FRAME[2] or b > FRAME[3]:
            notes.append("OFF-MAP/ON-FRAME")
        if rr > BANNER[0] and l < BANNER[2] and b > BANNER[1] and t < BANNER[3]:
            notes.append("UNDER TITLE BANNER")
        bad = bool(notes)
        d.rectangle([l, t, rr, b], fill=(200, 40, 40, 130),
                    outline=(255, 90, 90) if not bad else (255, 255, 0), width=1)
        d.text((l + 6, t + 5), r["name"], fill=(255, 255, 255))
        d.line([(r["dx"] - 4, r["dy"]), (r["dx"] + 4, r["dy"])], fill=(255, 255, 0))
        d.line([(r["dx"], r["dy"] - 4), (r["dx"], r["dy"] + 4)], fill=(255, 255, 0))
        print(f"{r['name']:22} {r['dx']:>4},{r['dy']:<5} "
              f"({l:>3},{t:>3},{rr:>3},{b:>3})              {' '.join(notes)}")

    out = os.path.join(HERE, "worldmap_preview.png")
    im.save(out)
    print(f"\nwrote {out}")


if __name__ == "__main__":
    main()
