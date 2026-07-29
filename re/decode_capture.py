#!/usr/bin/env python
"""
Offline decoder for combat_capture.jsonl (produced by frida_combat_tap.py).

Reassembles the per-connection TCP streams, splits NexusTK frames
(AA | len_u16be | opcode | inc | body), decrypts each direction with the ported
TkCrypt scheme, and prints an opcode histogram + decoded samples so we can
identify the damage-pop-up, HP-bar, vitals, and exp opcodes.

Ciphers (ported verbatim from Protocol.Tk495/TkCrypt.cs):
  * client->server (dir 's')            : Crypt(body, inc, "NexonInc.")   simple 3-stage XOR
  * server->client (dir 'r'), SvKey1 op : Crypt(body, inc, "Urk#nI7ni")   static-key XOR
  * server->client (dir 'r'), other op  : Crypt2InPlace w/ name-keyed MD5 table

The table key needs the CHARACTER NAME (pass --name). If decrypted server->client
bodies look like garbage but the login/name packets decode to readable ASCII, the
live 7.x crypto has diverged from the reconstruction and we pivot to hooking the
client's internal decrypt() instead.

Usage:
  python re/decode_capture.py --name "YourCharName"
  python re/decode_capture.py --name "X" --opcode 0x08        # focus one opcode
  python re/decode_capture.py --name "X" --peer 2001          # focus one server port
  python re/decode_capture.py --name "X" --timeline           # chronological dump
"""
import sys, os, json, argparse, hashlib
from collections import defaultdict

CAP = os.path.join(os.path.dirname(os.path.abspath(__file__)), "combat_capture.jsonl")

LOGIN_KEY = b"NexonInc."
MAP_KEY   = b"Urk#nI7ni"
SVKEY1    = {2, 3, 10, 64, 68, 94, 96, 98, 102, 111}   # server->client static-key opcodes

# Loose opcode hints from the 4.95 map (docs/memory). 7.x MAY differ — treat as a
# starting label, not gospel. The whole point of the capture is to correct these.
HINTS = {
    "r": {0x04: "coords/move?", 0x07: "creature-list", 0x08: "vitals?", 0x0D: "speech",
          0x0F: "cast?", 0x13: "stats/HUD?", 0x15: "enter-map", 0x33: "self-entity",
          0x34: "other-profile?", 0x39: "self-profile?", 0x3A: "menu?"},
    "s": {0x06: "move-step?", 0x0D: "speech", 0x0F: "cast?", 0x11: "turn",
          0x13: "attack/swing?", 0x32: "walk", 0x38: "click/target?"},
}


# ---- ciphers ---------------------------------------------------------------
def crypt(data: bytes, inc: int, key: bytes) -> bytes:
    o = bytearray(data)
    for i in range(len(o)):
        o[i] ^= key[i % 9]
        grp = i // 9
        o[i] ^= grp & 0xFF
        if grp != inc:
            o[i] ^= inc
    return bytes(o)


def populate_table(name: str) -> bytes:
    md5hex = lambda s: hashlib.md5(s.encode("ascii")).hexdigest()
    t = md5hex(name)
    t = md5hex(t)
    for _ in range(32):
        t += md5hex(t)
    return t.encode("ascii")


def generate_key2(table: bytes, from_client: bool) -> bytes:
    k1, k2 = 0xF7, 0x6013
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


def crypt2_inplace(packet: bytearray, key: bytes) -> None:
    ln = (packet[1] << 8) | packet[2]
    plen = ln - 5
    inc = packet[4]
    group = gc = 0
    for i in range(plen):
        p = 5 + i
        packet[p] ^= key[i % 9]
        kv = group & 0xFF
        if kv != inc:
            packet[p] ^= kv
        packet[p] ^= inc
        gc += 1
        if gc == 9:
            group += 1
            gc = 0


def decrypt_scheme(frame: bytes, scheme: str, table: bytes) -> bytes:
    """Decrypt a frame's body under an EXPLICIT scheme (for --try-all)."""
    inc = frame[4]
    body = frame[5:]
    if scheme == "login":
        return crypt(body, inc, LOGIN_KEY)
    if scheme == "mapkey":
        return crypt(body, inc, MAP_KEY)
    if scheme in ("table_sv", "table_cl") and table is not None:
        fb = bytearray(frame)
        crypt2_inplace(fb, generate_key2(table, from_client=(scheme == "table_cl")))
        return bytes(fb[5:])
    return body


def decrypt_frame(frame: bytes, dir_: str, table: bytes) -> bytes:
    """Return the decrypted BODY (bytes after opcode+inc)."""
    op = frame[3]
    inc = frame[4]
    body = frame[5:]
    if dir_ == "s":                                   # client->server
        return crypt(body, inc, LOGIN_KEY)
    if op in SVKEY1:                                  # server->client static key
        return crypt(body, inc, MAP_KEY)
    # server->client table key (in-place over the full frame, then slice body)
    if table is None:
        return body                                   # can't decrypt without a name
    fb = bytearray(frame)
    crypt2_inplace(fb, generate_key2(table, from_client=False))
    return bytes(fb[5:])


# ---- framing ---------------------------------------------------------------
def reassemble(records):
    """Group capture chunks into per-(fd,dir) byte streams with offset->ts marks."""
    streams = defaultdict(lambda: {"buf": bytearray(), "marks": []})   # (fd,dir) -> {..}
    for r in sorted(records, key=lambda x: x["ts"]):
        b = bytes(int(x, 16) for x in r["hex"].split()) if r["hex"] else b""
        if not b:
            continue
        s = streams[(r["fd"], r["dir"])]
        s["marks"].append((len(s["buf"]), r["ts"], r["peer"]))
        s["buf"].extend(b)
    return streams


def ts_at(marks, off):
    ts, peer = marks[0][1], marks[0][2]
    for m_off, m_ts, m_peer in marks:
        if m_off <= off:
            ts, peer = m_ts, m_peer
        else:
            break
    return ts, peer


def split_frames(buf, marks, fd, dir_):
    """Yield (ts, peer, fd, dir, frame_bytes), resyncing on any non-AA byte."""
    i, n = 0, len(buf)
    while i < n:
        if buf[i] != 0xAA:
            i += 1                                    # resync
            continue
        if i + 5 > n:
            break
        ln = (buf[i + 1] << 8) | buf[i + 2]
        total = 3 + ln
        if ln < 2 or total > 0x10000:                 # implausible length -> resync
            i += 1
            continue
        if i + total > n:
            break                                     # partial frame at stream end
        frame = bytes(buf[i:i + total])
        ts, peer = ts_at(marks, i)
        yield ts, peer, fd, dir_, frame
        i += total


# ---- reporting -------------------------------------------------------------
def ascii_ratio(b: bytes) -> float:
    if not b:
        return 0.0
    return sum(1 for c in b if 32 <= c < 127) / len(b)


def as_ascii(b: bytes) -> str:
    return "".join(chr(c) if 32 <= c < 127 else "." for c in b)


def field_guesses(b: bytes) -> str:
    """Show leading candidate fields so numbers (damage/HP/entity-id) pop out."""
    out = []
    if len(b) >= 1:
        out.append(f"u8={b[0]}")
    if len(b) >= 2:
        out.append(f"u16be={(b[0] << 8) | b[1]}")
    if len(b) >= 4:
        out.append(f"u32be={(b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]}")
    return "  ".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", help="character name (needed for table-key server->client)")
    ap.add_argument("--cap", default=CAP)
    ap.add_argument("--opcode", help="focus one opcode, e.g. 0x08")
    ap.add_argument("--peer", help="only frames whose peer contains this substring (e.g. a port)")
    ap.add_argument("--samples", type=int, default=4)
    ap.add_argument("--timeline", action="store_true", help="chronological dump instead of histogram")
    ap.add_argument("--try-all", action="store_true",
                    help="report mean readability of each opcode under all 4 cipher schemes")
    args = ap.parse_args()

    if not os.path.exists(args.cap):
        print(f"no capture at {args.cap} — run frida_combat_tap.py first")
        return
    records = [json.loads(l) for l in open(args.cap, encoding="utf-8") if l.strip()]
    if not records:
        print("capture is empty")
        return

    table = populate_table(args.name) if args.name else None
    if table is None:
        print("!! no --name given: server->client table-key packets will NOT be decrypted.\n")

    focus_op = int(args.opcode, 16) if args.opcode else None

    frames = []
    for (fd, dir_), s in reassemble(records).items():
        for f in split_frames(s["buf"], s["marks"], fd, dir_):
            frames.append(f)
    frames.sort(key=lambda x: x[0])

    if args.peer:
        frames = [f for f in frames if args.peer in (f[1] or "")]

    print(f"{len(records)} chunks -> {len(frames)} frames"
          + (f" (peer~{args.peer})" if args.peer else "")
          + (f", char='{args.name}'" if args.name else "") + "\n")

    if args.try_all:
        # For each (dir,opcode), decrypt every sample under all 4 schemes and report
        # the mean printable-ASCII ratio. The scheme that lights up on the readable
        # opcodes (names/chat/menus) is the correct one for that channel/direction.
        schemes = ["login", "mapkey", "table_sv", "table_cl"]
        by_op = defaultdict(list)
        for ts, peer, fd, dir_, frame in frames:
            op = frame[3]
            if focus_op is not None and op != focus_op:
                continue
            by_op[(dir_, op)].append(frame)
        print(f"{'dir op':8s} {'n':>4s}  " + "  ".join(f"{s:>9s}" for s in schemes))
        for (dir_, op) in sorted(by_op, key=lambda k: (k[0], k[1])):
            fs = by_op[(dir_, op)]
            row = []
            for sc in schemes:
                r = sum(ascii_ratio(decrypt_scheme(f, sc, table)) for f in fs) / len(fs)
                row.append(f"{r:>8.0%} ")
            arrow = "r<-" if dir_ == "r" else "s->"
            print(f"{arrow}0x{op:02x} {len(fs):>4d}  " + "  ".join(row))
        print("\n(high ASCII% on a readable opcode = that channel's scheme; "
              "binary combat packets stay low ASCII under every scheme.)")
        return

    if args.timeline:
        t0 = frames[0][0] if frames else 0
        for ts, peer, fd, dir_, frame in frames:
            op = frame[3]
            if focus_op is not None and op != focus_op:
                continue
            body = decrypt_frame(frame, dir_, table)
            arrow = "<-" if dir_ == "r" else "->"
            hint = HINTS[dir_].get(op, "")
            print(f"+{(ts - t0)/1000:7.2f}s {arrow} op=0x{op:02x} {hint:14s} "
                  f"len={len(body):3d} {field_guesses(body)}")
            print(f"          hex: {' '.join(f'{c:02x}' for c in body)}")
            if ascii_ratio(body) > 0.5:
                print(f"          asc: {as_ascii(body)}")
        return

    # histogram
    buckets = defaultdict(list)                        # (dir,op) -> [bodies]
    for ts, peer, fd, dir_, frame in frames:
        op = frame[3]
        if focus_op is not None and op != focus_op:
            continue
        buckets[(dir_, op)].append(decrypt_frame(frame, dir_, table))

    for dir_ in ("r", "s"):
        label = "SERVER -> CLIENT (recv)" if dir_ == "r" else "CLIENT -> SERVER (send)"
        keys = sorted([k for k in buckets if k[0] == dir_], key=lambda k: -len(buckets[k]))
        if not keys:
            continue
        print(f"===== {label} =====")
        for (d, op) in keys:
            bodies = buckets[(d, op)]
            lens = [len(b) for b in bodies]
            avg_ascii = sum(ascii_ratio(b) for b in bodies) / len(bodies)
            hint = HINTS[dir_].get(op, "")
            print(f"\nop=0x{op:02x} {hint:16s} n={len(bodies):4d} "
                  f"len={min(lens)}..{max(lens)} ascii={avg_ascii:.0%}")
            seen = set()
            shown = 0
            for b in bodies:
                sig = (len(b), b[:6])
                if sig in seen:
                    continue
                seen.add(sig)
                print(f"    {field_guesses(b)}")
                print(f"    hex: {' '.join(f'{c:02x}' for c in b)}")
                if ascii_ratio(b) > 0.5:
                    print(f"    asc: {as_ascii(b)}")
                shown += 1
                if shown >= args.samples:
                    break
        print()


if __name__ == "__main__":
    main()
