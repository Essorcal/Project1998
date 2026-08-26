"""Composite every TK*.map into real tile art, exactly as the 5.33 client (NextAeon533) draws it.

WHY 5.33 AND NOT THE RETAIL CLIENT: this server emulates the 4.x world but its players run the
5.33-based client, so 5.33 is the correct look. The KRU *retail* client (Program Files\\KRU) is a
much later, RE-TILED build (48x48 tiles, 19551 SObj) whose frame indices no longer line up with our
4.x .map words -- rendering with it silently mis-draws ~30% of every map. See Server/TileTranslation.cs.

WHAT A .map IS (Server/MapData.cs): headerless, 4 bytes/cell, row-major, dims from map_index.csv:
    u16 ground   -- a TAGGED WORD, not tile|flags. Decode per Server/TileTranslation.cs:
                      v == 0        -> draw nothing (void; caves are mostly this)
                      v <  0xC000   -> sheet 1
                      v >= 0xC000   -> sheet 2, legacy frame (v - 0xC000)
                    Masking it (v & 0x3FFF) was the old bug -- it rewrote every sheet-2 cell into an
                    unrelated low tile, which is why terrain "looked mostly right but wrong in places".
    u16 object   -- SObj id (0 = nothing); shared id space across 4.x/5.33.

5.33 ASSETS live in NextAeon533\\Tile.dat (Nexon DAT archive: u32 count, then records of
{u32 offset, char[13] name}, last = EOF terminator):
  TILE.EPF/PAL/TBL   ground.  TILE.EPF is the MERGED sheet, 24x24 frames, frame 0 a NULL.
                     5.33 indexes it DIRECTLY by v (its blitter dropped the 4.x "dec eax"; the
                     prepended null cancels it), so sheet-1 word v -> TILE.EPF[v] with no math.
  TILEC.EPF/PAL/TBL  objects. Boxed 24x24 frames + a trailing RLE STENCIL (see cframe()).
  SOBJ.TBL           per object: u8 tileCount, tileCount*u16 TILEC frame ids, FF FF FF FF 00, u8 flag.
                     The frame ids are a vertical column; the LAST sits on the anchor cell and the
                     column grows NORTH one 24px cell per frame.
  .tbl format        u32 count, then count * 2 bytes = [paletteIndex, flag].  (Palette is byte 0.)
  .pal (DLPalette)   u32 count, then blocks each starting "DLPalette"; header length VARIES, so the
                     256 RGBA color entries are the block's LAST 1024 bytes. RGB, drop A.

SHEET-2 REMAP: 5.33 re-packed the second sheet (232 index deltas), so a sheet-2 word has no
arithmetic relation to a TILE.EPF frame -- game-data/Tile533Map.csv carries the lookup
(startLegacy,count,start533 runs). An unmapped legacy frame draws nothing.

Usage:
    python re/render_maps.py one <id> [out.png] [--data TILE.DAT]
    python re/render_maps.py all <outdir> [--data TILE.DAT] [--thumb N] [--maxfull N] [--only a,b]
"""
import argparse
import csv
import json
import os
import struct
import sys
import time

import numpy as np
from PIL import Image

CELL = 24
SHEET2_BASE = 0xC000
DEFAULT_DATA = r"C:\Users\brian\Desktop\NextAeon533\Tile.dat"
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
MAPS_DIR = os.path.join(REPO, "game-data", "maps")
INDEX = os.path.join(REPO, "game-data", "map_index.csv")
SHEET2_CSV = os.path.join(REPO, "game-data", "Tile533Map.csv")


# ----------------------------------------------------------------------------- archive
def read_dat(path):
    """Nexon DAT archive -> {NAME_UPPER: bytes}."""
    d = open(path, "rb").read()
    n = struct.unpack_from("<I", d, 0)[0]
    recs = []
    off = 4
    for _ in range(n):
        o = struct.unpack_from("<I", d, off)[0]
        name = d[off + 4:off + 17].split(b"\x00", 1)[0].decode("latin1")
        recs.append((name, o))
        off += 17
    return {recs[i][0].upper(): d[recs[i][1]:recs[i + 1][1]] for i in range(n - 1) if recs[i][0]}


def load_palettes(raw):
    """DLPalette collection -> list of (256,3) uint8 RGB (color table = block's LAST 1024 bytes).

    Each block is: 32-byte header ("DLPalette" + fields incl. an animation-entry count C at byte 23),
    then 2*C animation bytes, then the 256 RGBA color entries -- so the colors sit at the END of the
    block (size == 1056 + 2*C) and end-anchoring is correct for all C. Reading a fixed +32 works only
    for C==0 blocks and lands in the header/animation region for the ~24% that animate (that turns the
    whole map purple, since the ground palettes are among the animated ones)."""
    offs, i = [], 0
    while True:
        j = raw.find(b"DLPalette", i)
        if j < 0:
            break
        offs.append(j)
        i = j + 1
    ends = offs[1:] + [len(raw)]
    out = []
    for b, e in zip(offs, ends):
        arr = np.frombuffer(raw[e - 1024:e], dtype=np.uint8)
        if arr.size < 1024:
            arr = np.zeros(1024, np.uint8)
        out.append(arr.reshape(256, 4)[:, :3].copy())
    return out


def tbl_palettes(tbl):
    """5.33 .tbl -> uint8 array of palette index per frame (byte 0 of each 2-byte entry)."""
    count = struct.unpack_from("<I", tbl, 0)[0]
    body = np.frombuffer(tbl, np.uint8, count * 2, 4)
    return body[0::2].copy()


def epf_entries(epf):
    """(frameCount, list of (top,left,bot,right,pixOff,stencilOff))."""
    cnt = struct.unpack_from("<H", epf, 0)[0]
    toc = 12 + struct.unpack_from("<I", epf, 8)[0]
    return cnt, [struct.unpack_from("<hhhhII", epf, toc + i * 16) for i in range(cnt)]


def decode_frame(epf, ents, fi, pal):
    """One EPF frame -> (left, top, w, h, rgb(h,w,3), alpha(h,w) bool) or None.

    Pixels are PACKED (opaque pixels only), placed by an RLE stencil that runs left-to-right,
    top-to-bottom. Each stencil byte is a run of `b & 0x7F`: high bit set = OPAQUE (consume that
    many packed pixels), clear = TRANSPARENT (consume none). A byte 0x00 is a ROW TERMINATOR: it
    fills the rest of the current row transparent and moves to the next row's first column. That
    row snap is essential -- a row whose opaque/transparent runs don't reach the full width before
    the 0x00 would otherwise shear every row below it (the bug that scrambled the maze-wall / foliage
    frames). A fully-opaque frame (each row a single opaque run that fills the width) decodes the
    same whether or not the snap runs, which is why only partial frames were affected.
    """
    top, left, bot, right, pix, sten = ents[fi]
    w, h = right - left, bot - top
    if w <= 0 or h <= 0:
        return None
    nxt = ents[fi + 1][4] if fi + 1 < len(ents) else len(epf) - 12
    pixdata = np.frombuffer(epf, np.uint8, sten - pix, 12 + pix)
    st = epf[12 + sten:12 + nxt]
    grid = np.zeros(h * w, np.uint8)
    alpha = np.zeros(h * w, np.uint8)
    p = src = 0
    for by in st:
        if by == 0:                                   # row terminator: snap to next row start
            p = ((p + w - 1) // w) * w
            continue
        ln = by & 0x7F
        if by & 0x80:
            grid[p:p + ln] = pixdata[src:src + ln]
            alpha[p:p + ln] = 1
            src += ln
        p += ln
        if p >= h * w:
            break
    return left, top, w, h, pal[grid].reshape(h, w, 3), alpha.reshape(h, w).astype(bool)


def load_sheet2(path=SHEET2_CSV):
    """legacy sheet-2 frame index -> 5.33 TILE.EPF frame (from the startLegacy,count,start533 runs)."""
    m = {}
    if not os.path.exists(path):
        return m
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        a, c, b = (int(x) for x in line.split(","))
        for k in range(c):
            m[a + k] = b + k
    return m


# ----------------------------------------------------------------------------- tileset
class TileSet:
    def __init__(self, data=DEFAULT_DATA):
        f = read_dat(data)
        self.sheet2 = load_sheet2()
        # ground -----------------------------------------------------------
        # Ground is the bottom layer, so a transparent ground pixel is just void (black); we bake
        # each frame into a CELL x CELL RGB block (transparent left black) and block-copy per cell.
        gpal = load_palettes(f["TILE.PAL"])
        gpi = tbl_palettes(f["TILE.TBL"])
        cnt, ents = epf_entries(f["TILE.EPF"])
        epf = f["TILE.EPF"]
        self.nground = cnt
        self.ground = np.zeros((cnt, CELL, CELL, 3), np.uint8)
        for fi in range(cnt):
            pal = gpal[gpi[fi]] if fi < len(gpi) and gpi[fi] < len(gpal) else (gpal[0] if gpal else None)
            if pal is None:
                continue
            dec = decode_frame(epf, ents, fi, pal)
            if not dec:
                continue
            left, top, w, h, rgb, a = dec
            y1, x1 = min(top + h, CELL), min(left + w, CELL)
            if top < CELL and left < CELL and y1 > top and x1 > left:
                sub = self.ground[fi, top:y1, left:x1]
                m = a[:y1 - top, :x1 - left]
                sub[m] = rgb[:y1 - top, :x1 - left][m]
        # objects ----------------------------------------------------------
        self.cpal = load_palettes(f["TILEC.PAL"])
        self.cpi = tbl_palettes(f["TILEC.TBL"])
        self.cepf = f["TILEC.EPF"]
        _, self.cents = epf_entries(self.cepf)
        self._ccache = {}
        self.objs = self._parse_sobj(f["SOBJ.TBL"])

    @staticmethod
    def _parse_sobj(sd):
        count = struct.unpack_from("<I", sd, 0)[0]
        off = 5                               # u32 count + object 0's lead flag byte
        objs = [[]]
        for _ in range(1, count):
            if off >= len(sd):
                break
            tc = sd[off]
            off += 1
            objs.append(list(struct.unpack_from("<%dH" % tc, sd, off)))
            off += tc * 2 + 5 + 1             # frames + FF FF FF FF 00 + this object's flag
        return objs

    def cframe(self, fi):
        """TILEC frame fi -> (left, top, w, h, rgb(h,w,3), alpha(h,w) bool) or None."""
        if fi <= 0 or fi >= len(self.cents):
            return None
        if fi in self._ccache:
            return self._ccache[fi]
        pal = self.cpal[self.cpi[fi]] if fi < len(self.cpi) and self.cpi[fi] < len(self.cpal) else \
            (self.cpal[0] if self.cpal else None)
        out = decode_frame(self.cepf, self.cents, fi, pal) if pal is not None else None
        self._ccache[fi] = out
        return out

    def ground_frame(self, word):
        """A .map ground word -> ground frame index, or 0 for 'draw nothing'."""
        if word == 0:
            return 0
        if word >= SHEET2_BASE:
            return self.sheet2.get(word - SHEET2_BASE, 0)
        return word                            # sheet 1: TILE.EPF[v] directly (5.33)


# ----------------------------------------------------------------------------- render
def render(ts, cells, xs, ys):
    canvas = np.zeros((ys * CELL, xs * CELL, 3), np.uint8)
    ncell = min(len(cells), xs * ys)
    for i in range(ncell):
        fr = ts.ground_frame(int(cells[i, 0]))
        if 0 < fr < ts.nground:
            cy, cx = divmod(i, xs)
            canvas[cy * CELL:cy * CELL + CELL, cx * CELL:cx * CELL + CELL] = ts.ground[fr]
    for i in range(ncell):
        z = int(cells[i, 1])
        if z == 0 or z >= len(ts.objs):
            continue
        fids = ts.objs[z]
        n = len(fids)
        cy, cx = divmod(i, xs)
        for k, fid in enumerate(fids):
            dec = ts.cframe(fid)
            if not dec:
                continue
            left, top, w, h, rgb, a = dec
            ry = cy - k                        # FIRST frame on the anchor cell, column grows north (frames run base->top)
            y0, x0 = ry * CELL + top, cx * CELL + left
            sy0, sx0 = max(0, -y0), max(0, -x0)
            ey, ex = min(h, canvas.shape[0] - y0), min(w, canvas.shape[1] - x0)
            if ey <= sy0 or ex <= sx0:
                continue
            dst = canvas[y0 + sy0:y0 + ey, x0 + sx0:x0 + ex]
            m = a[sy0:ey, sx0:ex]
            dst[m] = rgb[sy0:ey, sx0:ex][m]
    return Image.fromarray(canvas)


def load_index():
    out = {}
    with open(INDEX, newline="", encoding="utf-8", errors="replace") as f:
        for row in csv.DictReader(f):
            out[int(row["id"])] = (row["name"], int(row["xs"]), int(row["ys"]))
    return out


def map_cells(mid):
    d = open(os.path.join(MAPS_DIR, f"TK{mid}.map"), "rb").read()
    return np.frombuffer(d, "<u2").reshape(-1, 2)


def render_map(ts, mid, dims):
    name, xs, ys = dims[mid]
    return render(ts, map_cells(mid), xs, ys), name, xs, ys


# ----------------------------------------------------------------------------- cli
def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    a = sub.add_parser("one")
    a.add_argument("id", type=int)
    a.add_argument("out", nargs="?", default="map.png")
    a.add_argument("--data", default=DEFAULT_DATA)
    b = sub.add_parser("all")
    b.add_argument("outdir")
    b.add_argument("--data", default=DEFAULT_DATA)
    b.add_argument("--thumb", type=int, default=0, help="longest-side px for thumbnails (0 = skip)")
    b.add_argument("--maxfull", type=int, default=0, help="cap full image's longest side (0 = native)")
    b.add_argument("--only", default="", help="comma-separated ids (default: every indexed map)")
    args = ap.parse_args()

    dims = load_index()
    print("loading 5.33 tileset ...", flush=True)
    t0 = time.time()
    ts = TileSet(args.data)
    print(f"  {ts.nground} ground frames, {len(ts.cents)} object frames, {len(ts.objs)} SObj, "
          f"{len(ts.sheet2)} sheet-2 remaps in {time.time() - t0:.1f}s", flush=True)

    if args.cmd == "one":
        img, name, xs, ys = render_map(ts, args.id, dims)
        img.save(args.out)
        print(f"TK{args.id} {name!r} {xs}x{ys} -> {args.out} ({img.width}x{img.height})")
        return

    full = os.path.join(args.outdir, "full")
    thumb = os.path.join(args.outdir, "thumb")
    os.makedirs(full, exist_ok=True)
    if args.thumb:
        os.makedirs(thumb, exist_ok=True)
    ids = [int(x) for x in args.only.split(",") if x.strip()] or sorted(dims)
    meta = []
    t0 = time.time()
    for k, mid in enumerate(ids):
        if not os.path.exists(os.path.join(MAPS_DIR, f"TK{mid}.map")):
            continue
        try:
            img, name, xs, ys = render_map(ts, mid, dims)
        except Exception as e:                       # noqa: BLE001 - keep the batch going
            print(f"  !! TK{mid}: {e}")
            continue
        native = (img.width, img.height)
        if args.maxfull and max(native) > args.maxfull:
            img = img.copy()
            img.thumbnail((args.maxfull, args.maxfull), Image.LANCZOS)
        img.save(os.path.join(full, f"TK{mid}.png"))
        if args.thumb:
            th = img.copy()
            th.thumbnail((args.thumb, args.thumb), Image.LANCZOS)
            th.save(os.path.join(thumb, f"TK{mid}.png"))
        meta.append({"id": mid, "name": name, "xs": xs, "ys": ys, "w": native[0], "h": native[1]})
        if (k + 1) % 100 == 0:
            print(f"  {k + 1}/{len(ids)}  ({time.time() - t0:.0f}s)", flush=True)
    json.dump(meta, open(os.path.join(args.outdir, "maps.json"), "w"), separators=(",", ":"))
    open(os.path.join(args.outdir, "maps.js"), "w").write(
        "window.MAPS=" + json.dumps(meta, separators=(",", ":")) + ";")
    print(f"done: {len(meta)} maps in {time.time() - t0:.0f}s -> {args.outdir}")


if __name__ == "__main__":
    main()
