#!/usr/bin/env python
r"""
Offline decoder for re/probe_log_533.txt (produced by re/frida_probe_533.py).

The probe hooks WSOCK32 recv/send, so what it logs is what crosses the wire: CIPHERTEXT. That is the
right place to hook -- it is version-independent, it needs no client addresses, and it sees both
directions -- but it means the log is unreadable until you undo the cipher, which is what this does.

5.33 uses the SAME static NexonInc 3-stage XOR as 4.95 on both channels and every opcode exercised so
far (docs/5.x/Reverse-Engineering.md "Cipher"), so no session key and no character name are needed.

One thing worth knowing before you read the output: a single recv() can carry SEVERAL frames, and a
frame can be SPLIT across two recv() calls -- TCP owes us a byte stream, not messages. So this
reassembles per (direction, peer) rather than decoding each logged line on its own; decoding lines
independently silently drops every frame after the first in a burst, which is most of world entry.

Usage:
  python re/decode_probe_533.py                     # whole log, one line per frame
  python re/decode_probe_533.py --op 39             # only opcode 0x39
  python re/decode_probe_533.py --op 39 --full      # ...with the whole body, 16 bytes a row
  python re/decode_probe_533.py --dir r             # only server->client ('s' = client->server)
  python re/decode_probe_533.py --log some_other.txt
"""
import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "probe_log_533.txt")

LOGIN_KEY = b"NexonInc."

# Server->client labels for 4.95 (docs/4.x/Protocol.md). 5.33 shares most of them; where it does not,
# that divergence is exactly what this tool exists to find -- so treat these as a starting label.
LABEL_R = {
    0x02: "enter-world/text", 0x04: "self coords", 0x05: "your entity id", 0x06: "MAP DATA",
    0x07: "entity spawn", 0x08: "stats/HUD", 0x0A: "system text", 0x0B: "exit-to-select",
    0x0C: "entity move", 0x0E: "entity remove", 0x0F: "add spell/item", 0x10: "remove item",
    0x11: "entity turn", 0x15: "MAP INFO", 0x19: "media", 0x1A: "?", 0x1E: "ack",
    0x20: "time-of-day", 0x29: "?", 0x2F: "menu window", 0x30: "npc dialog", 0x32: "?",
    0x33: "self appearance", 0x34: "click profile", 0x37: "bag array", 0x39: "SELF PROFILE",
    0x3A: "dialog", 0x3B: "mail", 0x66: "examine item",
}
LABEL_S = {
    0x03: "login", 0x05: "map request (init)", 0x06: "map request (refresh)", 0x08: "?",
    0x0B: "exit to select", 0x0D: "speech", 0x0F: "cast", 0x10: "arrival", 0x11: "turn",
    0x13: "attack", 0x1B: "options", 0x1F: "?", 0x2D: "profile key", 0x2E: "worldmap/party",
    0x2F: "menu reply", 0x32: "walk", 0x38: "click", 0x39: "menu answer", 0x43: "chat channel",
}


def crypt(data: bytes, inc: int, key: bytes = LOGIN_KEY) -> bytes:
    """Self-inverse 3-stage XOR (Protocol.Tk495/TkCrypt.Crypt). Group counter is a BYTE."""
    o = bytearray(data)
    for i in range(len(o)):
        group = (i // 9) & 0xFF
        o[i] ^= key[i % 9]
        o[i] ^= group
        if group != inc:
            o[i] ^= inc
    return bytes(o)


LINE = re.compile(r"\[(\d\d:\d\d:\d\d)\]\s+(<~ RECV|~> SEND)\s+\(([^,]+),\s*(\d+)B\)\s+([0-9a-f ]+)")


def parse_log(path):
    """Yield (ts, dir, peer, raw_bytes) per logged recv/send, in order."""
    with open(path, "rb") as fh:
        text = fh.read().decode("utf-8", "replace")
    for line in text.splitlines():
        line = re.sub(r"\s+", " ", line).strip()
        m = LINE.match(line)
        if not m:
            continue
        ts, tag, peer, _n, hexs = m.groups()
        raw = bytes.fromhex(hexs.replace(" ", ""))
        yield ts, ("r" if "RECV" in tag else "s"), peer, raw


def frames(chunks):
    """Reassemble each (dir, peer) byte stream and split it into AA-framed packets.

    Frame: AA | len u16be | op | inc | body, where len = 2 + len(body), so the whole frame is
    3 + len bytes. A short tail is kept for the next chunk rather than dropped.
    """
    buf = {}
    for ts, d, peer, raw in chunks:
        k = (d, peer)
        b = buf.get(k, b"") + raw
        while True:
            # Resync: the stream should start on 0xAA; if it does not, something upstream lost bytes.
            if not b:
                break
            if b[0] != 0xAA:
                i = b.find(b"\xaa")
                if i < 0:
                    b = b""
                    break
                b = b[i:]
            if len(b) < 5:
                break
            ln = (b[1] << 8) | b[2]
            total = 3 + ln
            if ln < 2 or len(b) < total:
                break
            op, inc, body = b[3], b[4], b[5:total]
            yield ts, d, peer, op, inc, crypt(body, inc)
            b = b[total:]
        buf[k] = b


def hexdump(body, indent="        "):
    for off in range(0, len(body), 16):
        row = body[off:off + 16]
        h = " ".join(f"{c:02x}" for c in row)
        a = "".join(chr(c) if 32 <= c < 127 else "." for c in row)
        print(f"{indent}{off:04x}  {h:<47}  {a}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default=LOG)
    ap.add_argument("--op", help="hex opcode filter, e.g. 39 or 0x39")
    ap.add_argument("--dir", choices=("r", "s"), help="r = server->client, s = client->server")
    ap.add_argument("--full", action="store_true", help="hexdump the whole body")
    ap.add_argument("--bytes", type=int, default=48, help="preview width when not --full")
    a = ap.parse_args()

    want = int(a.op, 16) if a.op else None
    if not os.path.exists(a.log):
        sys.exit(f"no log at {a.log} -- run re/frida_probe_533.py first")

    counts = {}
    shown = 0
    for ts, d, peer, op, inc, body in frames(parse_log(a.log)):
        counts[(d, op)] = counts.get((d, op), 0) + 1
        if want is not None and op != want:
            continue
        if a.dir and d != a.dir:
            continue
        shown += 1
        label = (LABEL_R if d == "r" else LABEL_S).get(op, "?")
        arrow = "<~" if d == "r" else "~>"
        print(f"[{ts}] {arrow} {peer} op=0x{op:02x} inc={inc:3d} len={len(body):4d}  {label}")
        if a.full:
            hexdump(body)
        else:
            prev = body[:a.bytes]
            h = " ".join(f"{c:02x}" for c in prev)
            asc = "".join(chr(c) if 32 <= c < 127 else "." for c in prev)
            print(f"        {h}{' ...' if len(body) > a.bytes else ''}")
            print(f"        |{asc}|")

    print(f"\n-- {shown} frame(s) shown --")
    print("opcode histogram (dir op count label):")
    for (d, op), n in sorted(counts.items(), key=lambda kv: (kv[0][0], kv[0][1])):
        label = (LABEL_R if d == "r" else LABEL_S).get(op, "?")
        print(f"   {d}  0x{op:02x}  {n:5d}  {label}")


if __name__ == "__main__":
    main()
