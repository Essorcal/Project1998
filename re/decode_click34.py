#!/usr/bin/env python
r"""Decode a server-logged 0x34 click-profile packet against the 5.33 client grammar.

The server logs every send as `-> click-profile(0x34) id=... : NNB  AA LL LL 34 IN ...`
(TkPacket framing + the STATIC NexonInc cipher over the body). Paste that hex here and this
decrypts the body and walks it field-by-field exactly as the 5.33 parser sub_4d19c0 does, so a
desync (a wrong length, an over-read past the frame) shows up as the field where the offsets stop
making sense.

Grammar (recovered by tracing sub_4d19c0, cursor = ebx):
  5x [u8 len + body]  title, clan, clanTitle, class, name
  u8                  appearance tag (0 => read, 1 => apply)
  11 bytes            appearance record (sub_449880, FIXED 11)
  5x (u16BE + u8)     equipment icon cells
  u8 len + body       gear list
  u32BE               target entity id
  u8 u8 u8            group, exchange, nation
  u16BE + N bytes     profile picture (N=size; skipped when size==0)
  u8 len + body       blurb
  u8 count, then count x { u8 icon, u8 colour, u8 len, body }   legends
  [version-gated trailing u8 only if client build field >= 0x213 -- we never send it]

Usage:
  python re/decode_click34.py "AA 01 2C 34 07 ..."      # hex on the command line
  python re/decode_click34.py --file dump.txt            # hex from a file (whitespace/again ok)
"""
import sys
from pathlib import Path

LOGIN_KEY = b"NexonInc."


def crypt(data: bytes, inc: int) -> bytes:
    """The static 3-stage self-inverse XOR (TkCrypt.Crypt), byte-for-byte."""
    o = bytearray(data)
    for i in range(len(o)):
        group = (i // 9) & 0xFF
        o[i] ^= LOGIN_KEY[i % 9]
        o[i] ^= group
        if group != inc:
            o[i] ^= inc
    return bytes(o)


class Cur:
    def __init__(self, b):
        self.b = b
        self.i = 0

    def u8(self):
        if self.i + 1 > len(self.b):
            raise EOFError(f"u8 at {self.i} past end ({len(self.b)})")
        v = self.b[self.i]
        self.i += 1
        return v

    def u16(self):
        if self.i + 2 > len(self.b):
            raise EOFError(f"u16 at {self.i} past end ({len(self.b)})")
        v = (self.b[self.i] << 8) | self.b[self.i + 1]
        self.i += 2
        return v

    def u32(self):
        if self.i + 4 > len(self.b):
            raise EOFError(f"u32 at {self.i} past end ({len(self.b)})")
        v = int.from_bytes(self.b[self.i:self.i + 4], "big")
        self.i += 4
        return v

    def s(self):
        n = self.u8()
        if self.i + n > len(self.b):
            raise EOFError(f"string len {n} at {self.i} past end ({len(self.b)})")
        v = self.b[self.i:self.i + n]
        self.i += n
        return v.decode("ascii", "replace")


def decode(body: bytes):
    c = Cur(body)
    labels = ["title", "clan", "clanTitle", "class", "name"]
    for lbl in labels:
        off = c.i
        print(f"  [{off:4}] {lbl:10} = {c.s()!r}")
    tag = c.u8()
    print(f"  [{c.i-1:4}] appTag     = {tag}")
    app = body[c.i:c.i + 11]
    c.i += 11
    print(f"  [{c.i-11:4}] appearance = {app.hex(' ')}  (sex={app[0]} form={app[1]} face={app[2]} "
          f"hair={app[3]} armor={app[4]} dye={app[5]} weap={app[6]<<8|app[7]} ? {app[8]} "
          f"shield={app[9]} tail={app[10]})")
    for k in range(5):
        off = c.i
        icon = c.u16()
        col = c.u8()
        print(f"  [{off:4}] cell{k}      = icon 0x{icon:04x} colour {col}")
    off = c.i
    print(f"  [{off:4}] gearList   = {c.s()!r}")
    off = c.i
    print(f"  [{off:4}] entityId   = {c.u32()}")
    print(f"  [{c.i:4}] group={c.u8()} exchange={c.u8()} nation={c.u8()}")
    off = c.i
    picsz = c.u16()
    print(f"  [{off:4}] picSize    = {picsz}")
    if picsz:
        c.i += picsz
        print(f"         (+{picsz} picture bytes)")
    off = c.i
    print(f"  [{off:4}] blurb      = {c.s()!r}")
    off = c.i
    n = c.u8()
    print(f"  [{off:4}] legendCount= {n}")
    for j in range(n):
        off = c.i
        icon = c.u8(); col = c.u8()
        txt = c.s()
        print(f"  [{off:4}]   legend{j}  icon {icon} colour {col} {txt!r}")
    # FIELD #19 (5.33 only): the guardian backdrop frame. The 5.33 parser reads ONE u8 here (build-gated,
    # 0x4d2184) and uses it as the SELFLOOK.EPF frame index (0..3 = the four totems). OMITTING it makes the
    # client read garbage past the packet -> wrong/corrupted backdrop. 4.95 stops after the legends.
    if c.i < len(body):
        print(f"  [{c.i:4}] guardian   = {c.u8()}   (5.33 backdrop frame = totem 0..3)")
    else:
        print("  [  --] guardian   = MISSING (5.33 will read leftover garbage -> wrong/corrupt backdrop)")
    left = len(body) - c.i
    print(f"  -- consumed {c.i} of {len(body)} body bytes; {left} left over "
          f"({'OK' if left == 0 else 'note: extra trailing bytes'})")


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return
    if args[0] == "--file":
        raw = Path(args[1]).read_text()
    else:
        raw = " ".join(args)
    # strip anything that isn't a hex pair; keep only hex chars/spaces after the last ':' if present
    if ":" in raw:
        raw = raw.rsplit(":", 1)[1]
    hexes = "".join(ch for ch in raw if ch in "0123456789abcdefABCDEF ")
    pkt = bytes.fromhex("".join(hexes.split()))
    if len(pkt) < 5 or pkt[0] != 0xAA:
        print(f"not a framed packet (starts 0x{pkt[0]:02x}, len {len(pkt)})")
        return
    length = (pkt[1] << 8) | pkt[2]
    op = pkt[3]
    inc = pkt[4]
    enc_body = pkt[5:3 + length]
    print(f"frame: op 0x{op:02x} inc {inc} len {length} bodyBytes {len(enc_body)}")
    if op != 0x34:
        print(f"  (warning: opcode is 0x{op:02x}, not 0x34)")
    body = crypt(enc_body, inc)
    print("decrypted body:", body.hex(" "))
    print("fields:")
    try:
        decode(body)
    except EOFError as e:
        print(f"  !! DESYNC: {e}")


if __name__ == "__main__":
    main()
