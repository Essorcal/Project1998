"""Bring the retail client's item icons down to 4.95 / 5.33 size and append them to Item.epf.

WHY: Items.csv points 1,283 items at icons past the 4.95 client's 1,310 frames (1,004 past 5.33's
2,304), so those items draw blank. The LIVE retail client (KRU\\NexusTK\\Data\\misc.dat) carries an
Item.epf with 5,879 frames in the SAME id space -- frame N is a 2x redraw of 4.95 frame N (checked:
retail ids < 1310 use byte-identical palette blocks and the same Item.tbl palette numbers as 4.95, and
the art matches by eye), and Items.csv's highest ItmIcon (5,752) is inside it. So every missing icon
already exists at 2x; this tool halves it, re-quantizes it into its own palette block and appends the
result -- pixels, stencil, palette block and .tbl row -- to a copy of the target client's files.

FORMATS (all verified against the shipped files; the shifted "frame N+1 == item N" reading in
render_items.py is the same bytes read 4 bytes early):
  EPF     +0 u16 frameCount  +2 u16 ?  +4 u16 ?  +6 u16 0  +8 u32 tocRel   (TOC at 12 + tocRel)
          pixel area from +12; TOC entry j (16 B) = i16 top,left,bottom,right  u32 pixOff,stenOff,
          both offsets relative to +12. Frame j == client item id j.  Box is centred: left=-(w//2).
          Pixels: w*h raw 8bpp indices.  Then the STENCIL: per row, bytes until 0x00; byte > 0x80 =
          draw (byte-0x80) px, byte <= 0x80 = skip that many.  The stencil is the alpha (a few
          frames hide non-zero pixels with it), so it is written from the alpha mask, not inferred.
  PAL     u32 count, then "DLPalette" blocks of varying header length; colours = last 1024 B (RGBA).
  TBL     4.95: text  "NumItems N\\nID i, Palette p, Alpha 0.000000, Light -1\\n"...
          5.33 + retail: ENCODED. 8 cipher bytes -> one u32: XOR each byte with KEY[idx], idx starting
          at ((-0x1234568 - off) mod 27) and stepping (idx+26) mod 27, then v = a ^ ((a^b) & 0x55555555)
          over the two big-endian words (TKViewer TileTblFileHandler.decodeBytes). Decoded: u32 count,
          then 20-byte records {u32 id, u32 palette, f32 alpha, i32 light, u32 flag}.
  DAT     u32 count, count * {u32 offset, char[13] name}; entry data runs to the next offset.

Usage:
    python re/icon_downscale.py eval  [--sample N] [--sheet out.png]      score downscalers on the
                                        1,310 ids that exist in both retail and 4.95
    python re/icon_downscale.py sheet <lo> <hi> [--method M] [--out png]  preview proposed frames
    python re/icon_downscale.py build --client 495|533 [--method M] [--out DIR] [--max-id N]
                                        write Item.epf/.tbl/.pal + a repacked .dat copy into DIR
    python re/icon_downscale.py verify <DIR>                              re-decode what build wrote

Outputs default to re/out/icons/<client>/ -- never the live client directory. Install by hand.
"""
import argparse
import csv
import json
import os
import re
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _paths import CLIENT, CLIENT5, CLIENT_LIVE, DATA, RE, require  # noqa: E402

OUT_ROOT = RE / "out" / "icons"


# ----------------------------------------------------------------------------- DAT
def read_dat(path):
    d = Path(path).read_bytes()
    n, = struct.unpack_from("<I", d, 0)
    recs = []
    for i in range(n):
        off, name = struct.unpack_from("<I13s", d, 4 + 17 * i)
        recs.append((name, off))
    out = []
    for i, (name, off) in enumerate(recs):
        end = recs[i + 1][1] if i + 1 < n else len(d)
        out.append((name, d[off:end]))
    # ordered list of (raw 13-byte name field, bytes). The name field is kept raw because the shipped
    # archives carry leftover bytes after the NUL, and write_dat is meant to round-trip byte-for-byte.
    # The last entry is an EOF terminator (empty name, a few trailing bytes).
    return out


def dat_name(raw):
    return raw.split(b"\x00", 1)[0].decode("latin1")


def write_dat(entries):
    n = len(entries)
    buf = bytearray(struct.pack("<I", n))
    off = 4 + 17 * n
    body = bytearray()
    for name, blob in entries:
        buf += struct.pack("<I13s", off, name)
        body += blob
        off += len(blob)
    return bytes(buf + body)


def dat_get(entries, name):
    for n, b in entries:
        if dat_name(n).upper() == name.upper():
            return b
    raise KeyError(name)


def dat_set(entries, name, blob):
    return [(n, blob if dat_name(n).upper() == name.upper() else b) for n, b in entries]


# ----------------------------------------------------------------------------- EPF
def epf_frames(epf):
    """-> list of dict(top,left,bottom,right,pix,sten,w,h,idx: (h,w) uint8, alpha: (h,w) bool)."""
    n, = struct.unpack_from("<H", epf, 0)
    toc = 12 + struct.unpack_from("<I", epf, 8)[0]
    ents = [struct.unpack_from("<hhhhII", epf, toc + 16 * j) for j in range(n)]
    out = []
    for j, (t, l, b, r, p, s) in enumerate(ents):
        nxt = ents[j + 1][4] if j + 1 < n else toc - 12
        w, h = r - l, b - t
        fr = dict(top=t, left=l, bottom=b, right=r, pix=p, sten=s, w=w, h=h, idx=None, alpha=None,
                  sten_raw=epf[12 + s:12 + nxt])
        if w > 0 and h > 0 and s - p == w * h:
            fr["idx"] = np.frombuffer(epf[12 + p:12 + s], np.uint8).reshape(h, w)
            fr["alpha"] = stencil_decode(fr["sten_raw"], w, h)
        out.append(fr)
    return out


def stencil_decode(st, w, h):
    m = np.zeros((h, w), bool)
    i = 0
    for y in range(h):
        x = 0
        while i < len(st):
            c = st[i]
            i += 1
            if c == 0:
                break
            if c > 0x80:
                m[y, x:x + c - 0x80] = True
                x += c - 0x80
            else:
                x += c
    return m


def stencil_encode(alpha):
    """Inverse of stencil_decode, byte-identical to the shipped files: runs alternate skip/draw, a row
    ends right after its last drawn run (no trailing skip), a fully transparent row is just 0x00."""
    out = bytearray()
    for row in alpha:
        on = np.flatnonzero(row)
        end = int(on[-1]) + 1 if len(on) else 0
        x = 0
        while x < end:
            v = bool(row[x])
            n = 1
            while x + n < end and bool(row[x + n]) == v and n < 0x7F:
                n += 1
            out.append((0x80 + n) if v else n)
            x += n
        out.append(0)
    return bytes(out)


def epf_build(header, frames):
    """frames: list of (top,left,bottom,right, idx uint8 (h,w), alpha bool (h,w)[, raw stencil bytes])
    -> EPF bytes. A frame that carries its original stencil bytes keeps them verbatim (5.33 and retail
    encode a few runs differently from what stencil_encode would emit; same mask, different bytes)."""
    pix = bytearray()
    toc = bytearray()
    for fr in frames:
        t, l, b, r, idx, alpha = fr[:6]
        p = len(pix)
        pix += idx.astype(np.uint8).tobytes()
        s = len(pix)
        pix += fr[6] if len(fr) > 6 else stencil_encode(alpha)
        toc += struct.pack("<hhhhII", t, l, b, r, p, s)
    hdr = bytearray(header[:12])
    struct.pack_into("<H", hdr, 0, len(frames))
    struct.pack_into("<I", hdr, 8, len(pix))
    return bytes(hdr + pix + toc)


# ----------------------------------------------------------------------------- PAL
def pal_blocks(pal):
    """-> (count_field, [raw block bytes]); colours of a block = its last 1024 bytes as RGBA."""
    offs, i = [], 0
    while True:
        j = pal.find(b"DLPalette", i)
        if j < 0:
            break
        offs.append(j)
        i = j + 1
    blocks = [pal[offs[k]:(offs[k + 1] if k + 1 < len(offs) else len(pal))] for k in range(len(offs))]
    return struct.unpack_from("<I", pal, 0)[0], blocks


def pal_rgb(block):
    return np.frombuffer(block[-1024:], np.uint8).reshape(256, 4)[:, :3].astype(np.int32)


def pal_build(blocks):
    return struct.pack("<I", len(blocks)) + b"".join(blocks)


# ----------------------------------------------------------------------------- TBL
KEY = [75, 82, 77, 80, 74, 67, 79, 67, 16, 89, 74, 91, 70, 81, 87, 74, 67, 69, 77, 86, 74, 75, 85, 72, 75, 78, 71]
MASK = 0x55555555


def _keystream(off):
    idx = ((-0x1234568 - off) & 0xFFFFFFFF) % 27
    for _ in range(8):
        yield KEY[idx]
        idx = (idx + 26) % 27


def tbl_decode_words(enc):
    out = bytearray()
    off = 0
    for i in range(len(enc) // 8):
        blk = bytes(c ^ k for c, k in zip(enc[i * 8:i * 8 + 8], _keystream(off)))
        a, b = struct.unpack(">II", blk)
        out += struct.pack("<I", a ^ ((a ^ b) & MASK))
        off += 4
    return bytes(out)


def tbl_encode_words(plain):
    assert len(plain) % 4 == 0
    out = bytearray()
    off = 0
    for i in range(len(plain) // 4):
        v, = struct.unpack_from("<I", plain, 4 * i)
        blk = struct.pack(">II", v, v)  # a == b makes the mask term vanish: decode gives back v
        out += bytes(c ^ k for c, k in zip(blk, _keystream(off)))
        off += 4
    return bytes(out)


def tbl_parse(tbl):
    """-> ('text'|'enc', records[(id, palette, alpha, light, flag)])."""
    if tbl.startswith(b"NumItems"):
        recs = []
        for line in tbl.decode("latin1").splitlines():
            m = re.match(r"ID (\d+), Palette (\d+), Alpha ([\d.\-]+), Light (-?\d+)", line)
            if m:
                recs.append((int(m[1]), int(m[2]), float(m[3]), int(m[4]), 0))
        return "text", recs
    d = tbl_decode_words(tbl)
    n, = struct.unpack_from("<I", d, 0)
    return "enc", [struct.unpack_from("<IIfiI", d, 4 + 20 * i) for i in range(n)]


def tbl_build(kind, recs):
    if kind == "text":
        lines = [f"NumItems {len(recs)}"] + [f"ID {i}, Palette {p}, Alpha {a:.6f}, Light {l}" for i, p, a, l, _ in recs]
        return ("\n".join(lines) + "\n").encode("latin1")
    plain = struct.pack("<I", len(recs)) + b"".join(struct.pack("<IIfiI", *r) for r in recs)
    return tbl_encode_words(plain)


# ----------------------------------------------------------------------------- clients
class ItemArt:
    """One client's Item.epf/.pal/.tbl, plus the archive they came from."""

    def __init__(self, name, dat_path):
        self.name = name
        self.dat_path = Path(dat_path)
        self.entries = read_dat(require(self.dat_path, f"{name} archive", "P1998_CLIENT / P1998_CLIENT5 / P1998_CLIENT_LIVE"))
        self.epf = dat_get(self.entries, "ITEM.EPF")
        self.pal = dat_get(self.entries, "ITEM.PAL")
        self.tbl = dat_get(self.entries, "ITEM.TBL")
        self.frames = epf_frames(self.epf)
        self.pal_count, self.blocks = pal_blocks(self.pal)
        self.tbl_kind, self.recs = tbl_parse(self.tbl)
        self.rgb = [pal_rgb(b) for b in self.blocks]
        assert len(self.recs) == len(self.frames), (name, len(self.recs), len(self.frames))

    def palette(self, i):
        return self.rgb[self.recs[i][1] % len(self.rgb)]

    def rgba(self, i):
        """Frame i as (h,w,4) uint8 through its own palette; alpha from the stencil."""
        fr = self.frames[i]
        if fr["idx"] is None:
            return None
        rgb = self.palette(i)[fr["idx"]].astype(np.uint8)
        a = (fr["alpha"] * 255).astype(np.uint8)[..., None]
        return np.concatenate([rgb, a], axis=2)


def open_retail():
    return ItemArt("retail", Path(CLIENT_LIVE) / "Data" / "misc.dat")


def open_client(ver):
    if ver == "495":
        return ItemArt("4.95", Path(CLIENT) / "NexusTK.dat")
    return ItemArt("5.33", Path(CLIENT5) / "Misc.dat")


# ----------------------------------------------------------------------------- downscale
def _resize(rgba, w, h, flt):
    # premultiply so transparent neighbours do not bleed their (meaningless) colour into edges
    arr = rgba.astype(np.float32)
    a = arr[..., 3:4] / 255.0
    pm = np.concatenate([arr[..., :3] * a, arr[..., 3:4]], axis=2)
    im = Image.fromarray(pm.astype(np.uint8), "RGBA").resize((w, h), flt)
    out = np.asarray(im).astype(np.float32)
    a2 = out[..., 3:4]
    rgb = np.where(a2 > 0, out[..., :3] / np.maximum(a2, 1) * 255.0, 0)
    return rgb, a2[..., 0] / 255.0


def ds_box(rgba, w, h):
    return _resize(rgba, w, h, Image.BOX)


def ds_lanczos(rgba, w, h):
    return _resize(rgba, w, h, Image.LANCZOS)


def ds_nearest(rgba, w, h):
    return _resize(rgba, w, h, Image.NEAREST)


def ds_sharp(rgba, w, h):
    rgb, a = _resize(rgba, w, h, Image.BOX)
    im = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB").filter(
        ImageFilter.UnsharpMask(radius=1, percent=80, threshold=1))
    return np.asarray(im).astype(np.float32), a


def ds_snap(rgba, w, h):
    """Box average, then snap each output pixel to the SOURCE colour in its footprint that is nearest
    to the average. Keeps pixel-art edges and outlines crisp instead of blending them into mush."""
    H, W = rgba.shape[:2]
    arr = rgba.astype(np.float32)
    rgb = np.zeros((h, w, 3), np.float32)
    alpha = np.zeros((h, w), np.float32)
    for y in range(h):
        y0 = int(y * H / h)
        y1 = max(int((y + 1) * H / h), y0 + 1)
        for x in range(w):
            x0 = int(x * W / w)
            x1 = max(int((x + 1) * W / w), x0 + 1)
            blk = arr[y0:y1, x0:x1].reshape(-1, 4)
            op = blk[blk[:, 3] > 127]
            alpha[y, x] = len(op) / len(blk)
            if len(op):
                mean = op[:, :3].mean(axis=0)
                rgb[y, x] = op[np.argmin(((op[:, :3] - mean) ** 2).sum(axis=1)), :3]
    return rgb, alpha


METHODS = {"box": ds_box, "lanczos": ds_lanczos, "nearest": ds_nearest, "sharp": ds_sharp, "snap": ds_snap}
DEFAULT_METHOD = "snap"


def quantize(rgb, alpha, pal):
    """rgb float (h,w,3), alpha float (h,w) -> (idx uint8, mask bool). Index 0 is reserved for
    transparent; opaque pixels take the nearest of entries 1..255 of the frame's own palette block."""
    mask = alpha >= 0.5
    flat = rgb.reshape(-1, 3).astype(np.int32)
    d = ((flat[:, None, :] - pal[None, 1:, :]) ** 2).sum(axis=2)
    idx = (d.argmin(axis=1) + 1).astype(np.uint8).reshape(alpha.shape)
    idx[~mask] = 0
    return idx, mask


def make_frame(src, i, method, w=None, h=None):
    """Downscale retail frame i -> (top,left,bottom,right, idx, alpha) in its own palette."""
    rgba = src.rgba(i)
    if rgba is None:
        return None
    H, W = rgba.shape[:2]
    w = w or (W + 1) // 2
    h = h or (H + 1) // 2
    rgb, alpha = METHODS[method](rgba, w, h)
    idx, mask = quantize(rgb, alpha, src.palette(i))
    left, top = -(w // 2), -(h // 2)
    return (top, left, top + h, left + w, idx, mask)


def frame_rgba(fr_tuple, pal):
    t, l, b, r, idx, mask = fr_tuple
    rgb = pal[idx].astype(np.uint8)
    return np.concatenate([rgb, (mask * 255).astype(np.uint8)[..., None]], axis=2)


# ----------------------------------------------------------------------------- eval
def score(pred_rgba, gt_rgba):
    """Mean RGB distance over pixels opaque in either, and alpha IoU."""
    pa, ga = pred_rgba[..., 3] > 127, gt_rgba[..., 3] > 127
    union = pa | ga
    if not union.any():
        return 0.0, 1.0
    d = np.sqrt(((pred_rgba[..., :3].astype(np.float32) - gt_rgba[..., :3].astype(np.float32)) ** 2).sum(axis=2))
    d[pa != ga] = 255 * 3 ** 0.5 * 0.5  # a pixel present on one side only: half the max penalty
    return float(d[union].mean()), float((pa & ga).sum() / union.sum())


def _paste(sheet, rgba, x, y, scale):
    pil = Image.fromarray(rgba, "RGBA")
    pil = pil.resize((pil.width * scale, pil.height * scale), Image.NEAREST)
    sheet.paste(pil, (x, y), pil)


def cmd_eval(a):
    ret, old = open_retail(), open_client("495")
    ids = [i for i in range(len(old.frames)) if old.frames[i]["idx"] is not None and ret.frames[i]["idx"] is not None]
    if a.sample:
        ids = ids[:: max(1, len(ids) // a.sample)]
    exact2x = {i for i in ids if ret.frames[i]["w"] == 2 * old.frames[i]["w"] and ret.frames[i]["h"] == 2 * old.frames[i]["h"]}
    print(f"pairs: {len(ids)} ids in both clients, {len(exact2x)} of them exactly 2x")
    results = {m: [] for m in METHODS}
    for i in ids:
        gt = old.rgba(i)
        h, w = gt.shape[:2]
        for m in METHODS:
            pred = frame_rgba(make_frame(ret, i, m, w, h), ret.palette(i))
            results[m].append((i, *score(pred, gt)))
    print(f"{'method':8} {'rgb-err(all)':>13} {'rgb-err(2x)':>12} {'alpha-IoU':>10}")
    for m, rows in results.items():
        e_all = np.mean([r[1] for r in rows])
        e_2x = np.mean([r[1] for r in rows if r[0] in exact2x]) if exact2x else float("nan")
        iou = np.mean([r[2] for r in rows])
        print(f"{m:8} {e_all:13.2f} {e_2x:12.2f} {iou:10.3f}")
    if a.sheet:
        pick = ids[:: max(1, len(ids) // 24)][:24]
        cols = ["4.95", "retail"] + list(METHODS)
        S, cell = 3, 90
        sheet = Image.new("RGB", (cell * len(cols), (cell + 12) * len(pick)), (36, 36, 40))
        dr = ImageDraw.Draw(sheet)
        for row, i in enumerate(pick):
            gt = old.rgba(i)
            h, w = gt.shape[:2]
            imgs = [gt, ret.rgba(i)] + [frame_rgba(make_frame(ret, i, m, w, h), ret.palette(i)) for m in METHODS]
            for c, im in enumerate(imgs):
                s = S if c != 1 else 1
                _paste(sheet, im, c * cell + (cell - im.shape[1] * s) // 2, row * (cell + 12) + 12 + (cell - im.shape[0] * s) // 2, s)
            dr.text((2, row * (cell + 12)), f"id {i}", fill=(220, 220, 220))
        for c, name in enumerate(cols):
            dr.text((c * cell + 2, 0), name, fill=(255, 220, 120))
        Path(a.sheet).parent.mkdir(parents=True, exist_ok=True)
        sheet.save(a.sheet)
        print("sheet", a.sheet)


# ----------------------------------------------------------------------------- sheet
def item_names():
    names = {}
    with open(DATA / "Items.csv", encoding="utf-8", errors="replace", newline="") as f:
        for r in csv.DictReader(f):
            try:
                names.setdefault(int(r["ItmIcon"]), r["ItmIdentifier"])
            except ValueError:
                pass
    return names


def cmd_sheet(a):
    ret = open_retail()
    names = item_names()
    ids = [i for i in range(a.lo, min(a.hi, len(ret.frames))) if ret.frames[i]["idx"] is not None]
    S, cell, cols = 3, 90, 12
    rows = (len(ids) + cols - 1) // cols
    sheet = Image.new("RGB", (cell * 2 * cols, (cell + 12) * rows), (36, 36, 40))
    dr = ImageDraw.Draw(sheet)
    for k, i in enumerate(ids):
        r, c = divmod(k, cols)
        for j, im in enumerate((ret.rgba(i), frame_rgba(make_frame(ret, i, a.method), ret.palette(i)))):
            s = S if j else 1
            _paste(sheet, im, (c * 2 + j) * cell + (cell - im.shape[1] * s) // 2,
                   r * (cell + 12) + 12 + (cell - im.shape[0] * s) // 2, s)
        dr.text((c * 2 * cell + 2, r * (cell + 12)), f"{i} {names.get(i, '')[:18]}", fill=(220, 220, 220))
    out = a.out or str(OUT_ROOT / f"sheet_{a.lo}_{a.hi}_{a.method}.png")
    Path(out).parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out)
    print("sheet", out, f"({len(ids)} frames, retail left / {a.method} right)")


# ----------------------------------------------------------------------------- build
def cmd_build(a):
    ret = open_retail()
    cli = open_client(a.client)
    base = len(cli.frames)
    top = min(a.max_id + 1 if a.max_id is not None else len(ret.frames), len(ret.frames))
    out = Path(a.out) if a.out else OUT_ROOT / a.client
    out.mkdir(parents=True, exist_ok=True)

    # Palette blocks: reuse an existing block when the bytes are identical, else append the retail one.
    blocks = list(cli.blocks)
    where = {b: k for k, b in enumerate(blocks)}
    frames = [(f["top"], f["left"], f["bottom"], f["right"], f["idx"], f["alpha"], f["sten_raw"]) for f in cli.frames]
    recs = list(cli.recs)
    manifest = []
    names = item_names()
    skipped = 0
    for i in range(base, top):
        fr = make_frame(ret, i, a.method)
        rrec = ret.recs[i]
        if fr is None:  # retail has no drawable frame here either: keep the id space aligned with a blank
            frames.append((0, 0, 1, 1, np.zeros((1, 1), np.uint8), np.zeros((1, 1), bool)))
            recs.append((i, 0, 0.0, -1, 0))
            skipped += 1
            continue
        blk = ret.blocks[rrec[1] % len(ret.blocks)]
        if blk not in where:
            where[blk] = len(blocks)
            blocks.append(blk)
        frames.append(fr)
        recs.append((i, where[blk], float(rrec[2]), int(rrec[3]), int(rrec[4])))
        manifest.append(dict(icon=i, item=names.get(i, ""), retail=f"{ret.frames[i]['w']}x{ret.frames[i]['h']}",
                             out=f"{fr[3] - fr[1]}x{fr[2] - fr[0]}", palette=where[blk]))

    epf = epf_build(cli.epf, frames)
    pal = pal_build(blocks)
    tbl = tbl_build(cli.tbl_kind, recs)
    (out / "Item.epf").write_bytes(epf)
    (out / "Item.pal").write_bytes(pal)
    (out / "Item.tbl").write_bytes(tbl)
    ents = dat_set(dat_set(dat_set(cli.entries, "ITEM.EPF", epf), "ITEM.PAL", pal), "ITEM.TBL", tbl)
    dat_out = out / cli.dat_path.name
    dat_out.write_bytes(write_dat(ents))
    (out / "manifest.json").write_text(json.dumps(dict(
        client=cli.name, source=str(ret.dat_path), method=a.method, base_frames=base, frames=len(frames),
        palette_blocks=dict(before=len(cli.blocks), after=len(blocks)), blank=skipped, added=manifest), indent=1))
    print(f"{cli.name}: {base} -> {len(frames)} frames ({len(manifest)} added, {skipped} blank), "
          f"palette blocks {len(cli.blocks)} -> {len(blocks)}, tbl {cli.tbl_kind}")
    print(f"wrote {out / 'Item.epf'} ({len(epf):,} B), Item.pal ({len(pal):,} B), Item.tbl ({len(tbl):,} B), "
          f"{dat_out.name} ({dat_out.stat().st_size:,} B)")
    print(f"install: copy {dat_out.name} over the client's copy (keep a backup); the server's icon bound "
          f"must then allow ids up to {len(frames) - 1} (Content.ItemIconCount, GmCommands maxId).")


# ----------------------------------------------------------------------------- verify
def cmd_verify(a):
    out = Path(a.dir)
    epf = (out / "Item.epf").read_bytes()
    frames = epf_frames(epf)
    kind, recs = tbl_parse((out / "Item.tbl").read_bytes())
    _, blocks = pal_blocks((out / "Item.pal").read_bytes())
    bad = [j for j, f in enumerate(frames) if f["idx"] is None]
    print(f"Item.epf: {len(frames)} frames, {len(bad)} undecodable {bad[:8]}")
    print(f"Item.tbl: {kind}, {len(recs)} records, palette max {max(r[1] for r in recs)} of {len(blocks)} blocks")
    mism = sum(1 for f in frames if f["idx"] is not None
               and not np.array_equal(stencil_decode(stencil_encode(f["alpha"]), f["w"], f["h"]), f["alpha"]))
    print(f"stencil round-trip mismatches: {mism}")
    for d in out.glob("*.dat"):
        ents = read_dat(d)
        print(f"{d.name}: {len(ents)} entries, ITEM.EPF {len(dat_get(ents, 'ITEM.EPF')):,} B "
              f"(matches Item.epf: {dat_get(ents, 'ITEM.EPF') == epf}, ITEM.TBL matches: "
              f"{dat_get(ents, 'ITEM.TBL') == (out / 'Item.tbl').read_bytes()})")
    # independent decoder: render_items.py's shifted reading of the same bytes
    import render_items
    n = 0
    for j in range(1, len(frames)):
        r = render_items.epf_frame(epf, j)
        f = frames[j - 1]
        if r and f["idx"] is not None and (r[0], r[1]) == (f["w"], f["h"]) and r[2] == f["idx"].tobytes():
            n += 1
    print(f"render_items.epf_frame agrees on {n}/{len(frames) - 1} frames")


# ----------------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("eval")
    p.add_argument("--sample", type=int, default=0)
    p.add_argument("--sheet")
    p = sub.add_parser("sheet")
    p.add_argument("lo", type=int)
    p.add_argument("hi", type=int)
    p.add_argument("--method", default=DEFAULT_METHOD, choices=METHODS)
    p.add_argument("--out")
    p = sub.add_parser("build")
    p.add_argument("--client", default="495", choices=("495", "533"))
    p.add_argument("--method", default=DEFAULT_METHOD, choices=METHODS)
    p.add_argument("--out")
    p.add_argument("--max-id", type=int, default=None)
    p = sub.add_parser("verify")
    p.add_argument("dir")
    a = ap.parse_args()
    {"eval": cmd_eval, "sheet": cmd_sheet, "build": cmd_build, "verify": cmd_verify}[a.cmd](a)


if __name__ == "__main__":
    main()
