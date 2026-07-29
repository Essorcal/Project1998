#!/usr/bin/env python
"""
Recover the live session's server->client TABLE key by known-plaintext, bypassing
the unknown name/index entirely.

crypt2 is a periodic XOR: cipher[i] = plain[i] ^ key[i%9] ^ (g if g!=inc else 0) ^ inc,
where g = i//9 and inc = frame[4]. The 0x39 self-profile packet contains a long run of
ZERO plaintext (empty profile fields), so over that run:
    key[i%9] = cipher[i] ^ (g if g!=inc else 0) ^ inc
Majority-vote per residue class across the whole body: the big zero-run wins, non-zero
bytes are scattered noise. One recovered 9-byte key decrypts EVERY server->client table
packet for the session (inc/group are per-packet but computable).

Usage: python re/recover_key.py [--peer 2001] [--op 0x39]
"""
import argparse, json
from collections import Counter
import decode_capture as d


# Two candidate per-byte schemes (zero-plaintext keystream = cipher):
#   crypt2 : cipher[i] = key[i%9] ^ (g if g!=inc else 0) ^ inc      (table carve trailing 3)
#   simple : cipher[i] = key[i%9] ^ g ^ (inc if g!=inc else 0)      (3-stage, whole body)
def _key_byte(scheme, c, g, inc):
    if scheme == "crypt2":
        return c ^ (g if g != inc else 0) ^ inc
    return c ^ g ^ (inc if g != inc else 0)          # simple 3-stage


def recover_from_frame(frame, scheme):
    ln = (frame[1] << 8) | frame[2]
    total = 3 + ln
    inc = frame[4]
    body = frame[5:total - 3] if scheme == "crypt2" else frame[5:total]
    votes = [Counter() for _ in range(9)]
    for i, c in enumerate(body):
        votes[i % 9][_key_byte(scheme, c, (i // 9) & 0xFF, inc)] += 1
    key = bytes(v.most_common(1)[0][0] for v in votes)
    conf = min(v.most_common(1)[0][1] / max(1, sum(v.values())) for v in votes)
    return key, conf


def decrypt_with_key(frame, key, scheme):
    if scheme == "crypt2":
        fb = bytearray(frame)
        d.crypt2_inplace(fb, key)
        return bytes(fb[5:])
    ln = (frame[1] << 8) | frame[2]
    return d.crypt(frame[5:3 + ln], frame[4], key)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cap", default=d.CAP)
    ap.add_argument("--peer", default="2001")
    ap.add_argument("--op", default="0x39", help="opcode with a long zero-run to key off")
    args = ap.parse_args()
    op = int(args.op, 16)

    records = [json.loads(l) for l in open(args.cap, encoding="utf-8") if l.strip()]
    frames = []
    for (fd, dir_), s in d.reassemble(records).items():
        if dir_ != "r":
            continue
        for f in d.split_frames(s["buf"], s["marks"], fd, dir_):
            if args.peer in (f[1] or ""):
                frames.append(f[4])

    cands = sorted([f for f in frames if f[3] == op], key=len, reverse=True)
    if not cands:
        print(f"no 0x{op:02x} frame on peer~{args.peer}")
        return

    STRUCT = {0x07, 0x08, 0x0c, 0x11, 0x04, 0x19, 0x1a, 0x13}

    def validate(key, scheme):
        hits = tot = 0
        for f in frames:
            if f[3] not in STRUCT:
                continue
            ln = (f[1] << 8) | f[2]
            if ln < 8:
                continue
            tot += 1
            if decrypt_with_key(f[:3 + ln], key, scheme)[0] == 0x00:
                hits += 1
        return hits, tot

    best = None
    for scheme in ("simple", "crypt2"):
        key, conf = recover_from_frame(cands[0], scheme)
        hits, tot = validate(key, scheme)
        rate = hits / tot if tot else 0
        print(f"[{scheme:6s}] key={' '.join(f'{b:02x}' for b in key)} "
              f"(\"{''.join(chr(b) if 32<=b<127 else '.' for b in key)}\")  "
              f"conf={conf:.0%}  validate={hits}/{tot} ({rate:.0%})")
        if best is None or rate > best[3]:
            best = (scheme, key, conf, rate)

    scheme, key, conf, rate = best
    verdict = "CRACKED" if rate > 0.8 else "still weak"
    print(f"\nbest = {scheme}  ->  {verdict}\n")
    if rate <= 0.8:
        print("try another --op with a big zero-run (0x08, 0x15), or capture a longer session.")
        return

    print("sample decodes:")
    shown = 0
    for f in frames:
        if f[3] not in (0x0c, 0x11, 0x13, 0x07, 0x08):
            continue
        ln = (f[1] << 8) | f[2]
        if ln < 6:
            continue
        body = decrypt_with_key(f[:3 + ln], key, scheme)
        asc = "".join(chr(c) if 32 <= c < 127 else "." for c in body)
        print(f"   op=0x{f[3]:02x}  {' '.join(f'{c:02x}' for c in body)}   |{asc}|")
        shown += 1
        if shown >= 12:
            break


if __name__ == "__main__":
    main()
