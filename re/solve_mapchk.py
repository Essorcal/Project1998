#!/usr/bin/env python
"""
Solve the 4.95 view checksum from re/frida_mapchk.py captures.

The probe gives us, per outgoing 0x05/0x06, the checksum the client produced AND the exact cell array it
produced it from. So we don't have to guess the covered region: every additive and XOR reduction over a
RECTANGLE is prefix-decomposable, which means we can score all ~55k rectangles of a small map in a blink
and keep only the ones that reproduce the checksum in EVERY sample. Intersecting across samples is what
kills coincidences — a single sample has thousands of accidental matches.

If nothing survives, the reduction isn't a plain sum/XOR over a rectangle (order-dependent hash, CRC, or a
non-rectangular region), and the builder backtrace the probe captured is the next thread to pull.

Usage:  python re/solve_mapchk.py [re/mapchk.jsonl]
"""
import sys, os, json, base64, struct
from collections import defaultdict

PATH = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "mapchk.jsonl")

# Per-cell values to try reducing. A cell is 4 bytes: ground(u16 LE) object(u16 LE); `ground` packs the
# tile in its low 14 bits and passability in the top 2.
VALUES = {
    "cell u32":       lambda g, o: (o << 16) | g,
    "ground+obj":     lambda g, o: (g + o) & 0xFFFFFFFF,
    "ground only":    lambda g, o: g,
    "obj only":       lambda g, o: o,
    "ground^obj":     lambda g, o: g ^ o,
    "tile only":      lambda g, o: g & 0x3FFF,
}


def load(path):
    samples = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            r = json.loads(line)
            if r.get("t") != "pkt" or "cells_b64" not in r:
                continue
            body = r["pkt"][1:]
            if len(body) < 10:
                continue
            r["chk"] = (body[7] << 8) | body[8]
            r["body"] = body
            r["cells"] = base64.b64decode(r["cells_b64"])
            samples.append(r)
    return samples


def grid(sample, valfn):
    w, h = sample["w"], sample["h"]
    cells = sample["cells"]
    g = [[0] * w for _ in range(h)]
    for y in range(h):
        for x in range(w):
            i = (y * w + x) * 4
            gr, ob = struct.unpack_from("<HH", cells, i)
            g[y][x] = valfn(gr, ob)
    return g


def prefix(g, w, h, op):
    # P[y][x] = reduction over [0..y-1][0..x-1]; one extra row/col so the inclusion-exclusion has no
    # special cases at the edges.
    P = [[0] * (w + 1) for _ in range(h + 1)]
    for y in range(h):
        for x in range(w):
            if op == "sum":
                P[y + 1][x + 1] = P[y][x + 1] + P[y + 1][x] - P[y][x] + g[y][x]
            else:
                P[y + 1][x + 1] = P[y][x + 1] ^ P[y + 1][x] ^ P[y][x] ^ g[y][x]
    return P


def rect(P, x0, y0, x1, y1, op):
    if op == "sum":
        return P[y1 + 1][x1 + 1] - P[y0][x1 + 1] - P[y1 + 1][x0] + P[y0][x0]
    return P[y1 + 1][x1 + 1] ^ P[y0][x1 + 1] ^ P[y1 + 1][x0] ^ P[y0][x0]


def main():
    samples = load(PATH)
    if not samples:
        print(f"no usable samples in {PATH} (need records with a cell array)")
        return
    print(f"{len(samples)} sample(s) with a captured cell array")
    for s in samples[:4]:
        print(f"   map {s.get('map')} {s['w']}x{s['h']}  op=0x{s['op']:02x}  "
              f"body={' '.join(f'{b:02x}' for b in s['body'][:10])}  chk={s['chk']:#06x}")

    survivors = None
    for vname, valfn in VALUES.items():
        for op in ("sum", "xor"):
            cand = None
            for s in samples:
                w, h = s["w"], s["h"]
                P = prefix(grid(s, valfn), w, h, op)
                hits = set()
                for y0 in range(h):
                    for y1 in range(y0, h):
                        for x0 in range(w):
                            for x1 in range(x0, w):
                                if (rect(P, x0, y0, x1, y1, op) & 0xFFFF) == s["chk"]:
                                    hits.add((x0, y0, x1, y1))
                cand = hits if cand is None else (cand & hits)
                if not cand:
                    break
            if cand:
                print(f"\n*** {op.upper()} of '{vname}' matches ALL {len(samples)} samples "
                      f"for {len(cand)} rectangle(s):")
                for r in sorted(cand)[:12]:
                    print(f"      x[{r[0]}..{r[2]}] y[{r[1]}..{r[3]}]")
                survivors = (vname, op, cand)

    if survivors is None:
        print("\nNo sum/XOR-over-a-rectangle reproduces every sample.")
        print("So it is order-dependent (rolling hash / CRC) or the region isn't a rectangle.")
        print("Next: the packet BUILDER address from the probe's backtrace — hook it and read the loop.")
        for s in samples:
            if s.get("bt"):
                print(f"   op 0x{s['op']:02x} builder stack: {' <- '.join(s['bt'])}")
                break


if __name__ == "__main__":
    main()
