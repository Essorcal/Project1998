#!/usr/bin/env python
"""
Crack the live 7.x name-keyed TABLE cipher offline.

The static-key opcodes (0x0a etc.) already decode with the ported crypto, proving
framing + the simple 3-stage XOR are correct for 7.5.2.0. The table opcodes (the
binary combat packets: 0x07/0x08/0x0c/0x11/0x13) need generate_key2(), whose k1/k2
come from the per-session packet INDEX. 4.95 hardcodes index 13 F7 60 (rnd=0x1337);
the live server's index is different.

This probe recovers it. crypt2 leaves the last 3 body bytes (the index) UNENCRYPTED,
so we can read the real index straight off the wire. We test, against a known
structural signature (correctly-decoded table bodies start with 0x00), which index /
key-derivation variant actually decrypts the live stream.

Usage: python re/crack_table.py --name "Zalerooo"
"""
import argparse, hashlib
from collections import Counter
import decode_capture as d

STRUCT_OPS = {0x07, 0x08, 0x0c, 0x11, 0x13, 0x04, 0x19, 0x1a}   # table opcodes w/ leading-00 bodies


def gen_key2_idx(table, from_client, idx):
    """generate_key2 but with k1/k2 seeded from an EXPLICIT 3-byte index."""
    k1 = idx[1]
    k2 = (idx[2] << 8) | idx[0]
    if from_client:
        k1 ^= 0x25; k2 ^= 0x2361
    else:
        k1 ^= 0x21; k2 ^= 0x7424
    k1 = (k1 * k1) & 0xFFFFFFFF
    key = bytearray(9)
    for i in range(9):
        key[i] = table[(k1 * i + k2) & 0x3FF]
        k1 = (k1 + 3) & 0xFFFFFFFF
    return bytes(key)


def crypt2_body(frame, key):
    fb = bytearray(frame)
    d.crypt2_inplace(fb, key)
    return bytes(fb[5:])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", required=True)
    ap.add_argument("--cap", default=d.CAP)
    args = ap.parse_args()

    records = [__import__("json").loads(l) for l in open(args.cap, encoding="utf-8") if l.strip()]
    table = d.populate_table(args.name)

    frames = []
    for (fd, dir_), s in d.reassemble(records).items():
        if dir_ != "r":
            continue
        for f in d.split_frames(s["buf"], s["marks"], fd, dir_):
            frames.append(f[4])                       # raw frame bytes

    tbl_frames = [f for f in frames if f[3] in STRUCT_OPS]
    print(f"{len(frames)} recv frames, {len(tbl_frames)} table-opcode frames\n")

    # 1) Is the trailing 3-byte index session-constant or per-packet?
    tails = Counter()
    for f in tbl_frames:
        ln = (f[1] << 8) | f[2]
        total = 3 + ln
        if total <= len(f) and ln >= 5:
            tails[bytes(f[total - 3:total]).hex()] += 1
    print("raw trailing-3 (index?) distribution:")
    for h, c in tails.most_common(8):
        print(f"   {h}  x{c}")
    print()

    # 2) Try key-derivation variants; score by how many bodies decrypt to a leading 0x00.
    variants = {
        "hardcoded 13F760 (sv)": lambda f, idx: gen_key2_idx(table, False, (0x13, 0xF7, 0x60)),
        "per-packet-tail (sv)":  lambda f, idx: gen_key2_idx(table, False, idx),
        "per-packet-tail (cl)":  lambda f, idx: gen_key2_idx(table, True, idx),
    }
    scores = {k: 0 for k in variants}
    total_scored = 0
    for f in tbl_frames:
        ln = (f[1] << 8) | f[2]
        total = 3 + ln
        if total > len(f) or ln < 8:
            continue
        idx = (f[total - 3], f[total - 2], f[total - 1])
        total_scored += 1
        for name, keyfn in variants.items():
            body = crypt2_body(f[:total], keyfn(f, idx))
            if body and body[0] == 0x00:
                scores[name] += 1
    print(f"structural hits (body[0]==0x00) out of {total_scored} frames:")
    for name, sc in sorted(scores.items(), key=lambda kv: -kv[1]):
        print(f"   {sc:3d}/{total_scored}   {name}")

    # 3) Show a decoded sample under the best variant.
    best = max(scores, key=scores.get)
    print(f"\nsample 0x0c/0x13 decodes under '{best}':")
    shown = 0
    for f in tbl_frames:
        if f[3] not in (0x0c, 0x13, 0x11):
            continue
        ln = (f[1] << 8) | f[2]
        total = 3 + ln
        if total > len(f) or ln < 8:
            continue
        idx = (f[total - 3], f[total - 2], f[total - 1])
        body = crypt2_body(f[:total], variants[best](f, idx))
        print(f"   op=0x{f[3]:02x} body: {' '.join(f'{c:02x}' for c in body)}")
        shown += 1
        if shown >= 8:
            break


if __name__ == "__main__":
    main()
