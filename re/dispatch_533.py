#!/usr/bin/env python
r"""
Dump the 5.33 client's server->client opcode dispatch table, and optionally the case body for one
opcode.

The dispatcher (`sub_463320`, docs/5.x/Reverse-Engineering.md) is a compiler-generated jump table:

    al   = body[0]                      ; opcode
    eax  = opcode - 3
    if eax > 0x67: default              ; so only opcodes 0x03..0x6A dispatch at all
    cl   = byteMap[eax]                 ; byteMap @ 0x463d44
    jmp  ptrTable[cl]                   ; ptrTable @ 0x463c8c

Several opcodes share one `cl`, which is the point: opcodes that land on the SAME case VA are
handled by identical code, and an opcode whose case VA is the shared default is not handled at all.
That distinction is the one this tool exists to make -- "the server sends it and nothing happens" and
"the server sends it and the client mis-parses it" look the same from outside, and want opposite fixes.

Case bodies are inline in the dispatcher, so the real work is usually a `call` a few instructions in;
`--op` prints the body and flags those calls.

Usage:
  python re/dispatch_533.py                 # the whole table
  python re/dispatch_533.py --op 39         # + disassemble the 0x39 case body
  python re/dispatch_533.py --op 39 --bytes 400
  python re/dispatch_533.py --undecoded     # only opcodes that fall through to the default case
"""
import argparse
import sys

import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_32

from _paths import CLIENT5, require

BYTE_MAP = 0x463D44
PTR_TABLE = 0x463C8C
OP_LO, OP_HI = 0x03, 0x6A          # from `cmp eax, 0x67` after `add eax, -3`

# Body stream primitives (see re/grammar_533.py, which hooks these to recover packet grammars).
# Not handlers -- a case that reads a field before dispatching calls one of these first.
READERS = {
    0x4A1200,   # u8      at p            (non-advancing)
    0x4A1210,   # u16 BE  at p            (non-advancing)
    0x4A1250,   # u32 BE  at p            (non-advancing)
    0x4A3E30,   # u8      at base+*cur,   cursor += 1
    0x4A3E50,   # u16 BE  at base+*cur,   cursor += 2
}

# What we believe each opcode is, from the 4.95 protocol (docs/4.x/Protocol.md). 5.33 shares most of
# them; anywhere it does not is precisely what this table is being read to discover.
LABEL = {
    0x02: "enter-world", 0x04: "self coords", 0x05: "your entity id", 0x06: "map data",
    0x07: "entity spawn", 0x08: "stats/HUD", 0x0A: "system text", 0x0B: "exit-to-select",
    0x0C: "entity move", 0x0D: "speech", 0x0E: "despawn list", 0x0F: "add spell/item",
    0x10: "remove item/spell", 0x11: "entity turn", 0x13: "mob hp/stat", 0x15: "map info",
    0x19: "media", 0x1A: "?", 0x1E: "ack", 0x20: "time-of-day", 0x22: "?", 0x29: "?",
    0x2F: "menu window", 0x30: "npc dialog", 0x32: "?", 0x33: "self appearance",
    0x34: "click profile", 0x36: "user list", 0x37: "bag array", 0x38: "hard refresh",
    0x39: "self profile", 0x3A: "dialog", 0x3B: "mail", 0x3C: "?", 0x66: "examine item",
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--op", help="hex opcode to disassemble, e.g. 39")
    ap.add_argument("--bytes", type=int, default=220)
    ap.add_argument("--undecoded", action="store_true")
    ap.add_argument("--handlers", action="store_true",
                    help="resolve each case body to the function it calls, and group opcodes that share one")
    ap.add_argument("--parsers", type=int, metavar="MIN",
                    help="list functions that call the stream readers at least MIN times, most first — "
                         "a packet parser is mostly reader calls, so this finds one without a live trace")
    a = ap.parse_args()

    exe = require(CLIENT5 / "NexusTK.exe", "5.33 client exe", "P1998_CLIENT5")
    pe = pefile.PE(str(exe), fast_load=True)
    ib = pe.OPTIONAL_HEADER.ImageBase
    img = pe.get_memory_mapped_image()
    md = Cs(CS_ARCH_X86, CS_MODE_32)

    def u8(va):
        return img[va - ib]

    def u32(va):
        return int.from_bytes(img[va - ib:va - ib + 4], "little")

    if a.parsers is not None:
        # A parser is a function that is mostly reader calls. Find every direct CALL to one of the five
        # primitives, walk back to the enclosing function prologue, and tally. Cheaper and more certain
        # than eyeballing vtables -- the profile window's parse is reached through `call [vtbl+0x44]`,
        # which no static xref can follow.
        text = [s for s in pe.sections if s.Characteristics & 0x20000000]
        sites = []
        for sec in text:
            base, blob = ib + sec.VirtualAddress, sec.get_data()
            for i in range(len(blob) - 5):
                if blob[i] != 0xE8:
                    continue
                rel = int.from_bytes(blob[i + 1:i + 5], "little", signed=True)
                tgt = base + i + 5 + rel
                if tgt in READERS:
                    sites.append((base + i, tgt))

        def enclosing(va, back=0x1400):
            """Nearest `push ebp; mov ebp, esp` at or before va. Compilers align functions and pad with
            int3/nop, so the last prologue before the call site is the function it belongs to."""
            lo = va - back
            for p in range(va, lo, -1):
                if img[p - ib:p - ib + 3] == b"\x55\x8b\xec":
                    return p
            return None

        tally, widths = {}, {}
        for va, tgt in sites:
            f = enclosing(va)
            if f is None:
                continue
            tally[f] = tally.get(f, 0) + 1
            widths.setdefault(f, []).append(tgt)
        print(f"functions calling a stream reader >= {a.parsers} times:\n")
        print("  function     reads  u8/u16/u32/adv")
        for f, n in sorted(tally.items(), key=lambda kv: -kv[1]):
            if n < a.parsers:
                continue
            w = widths[f]
            mix = (f"{w.count(0x4A1200)}/{w.count(0x4A1210)}/{w.count(0x4A1250)}/"
                   f"{w.count(0x4A3E30) + w.count(0x4A3E50)}")
            print(f"  0x{f:08x}  {n:5d}  {mix}")
        return

    rows = []
    for op in range(OP_LO, OP_HI + 1):
        idx = u8(BYTE_MAP + (op - OP_LO))
        rows.append((op, idx, u32(PTR_TABLE + idx * 4)))

    # The default case is whatever the largest index maps to -- it is the most-shared target, and the
    # `ja` above jumps there directly too.
    tally = {}
    for _op, _idx, case in rows:
        tally[case] = tally.get(case, 0) + 1
    default_case = max(tally, key=lambda c: tally[c])

    print(f"exe: {exe}")
    print(f"byteMap @ 0x{BYTE_MAP:08x}   ptrTable @ 0x{PTR_TABLE:08x}   "
          f"default case = 0x{default_case:08x} ({tally[default_case]} opcodes)\n")
    def handler_of(case, budget=64):
        """The function a case body calls, following one `jmp` into a shared tail.

        Case bodies are tiny: push args, call the real handler, fall through to the epilogue. Some
        instead `jmp` into a tail another case shares (0x39 and 0x34 both do), so follow that once --
        without it those two look handler-less when they in fact share one parser.

        READERS are skipped. Several cases start by pulling a field out of the body inline before
        calling anything, so the first `call` is a stream primitive, not the handler. Taking it at
        face value made 0x0e/0x11/0x12/0x68 look like they shared one parser when all they share is
        "reads a u32 first" -- a grouping that would send you looking for a common grammar that does
        not exist.
        """
        seen_jmp = False
        while True:
            for insn in md.disasm(img[case - ib:case - ib + budget], case):
                if insn.mnemonic == "call" and insn.op_str.startswith("0x"):
                    t = int(insn.op_str, 16)
                    if t in READERS:
                        continue
                    return t
                if insn.mnemonic == "jmp" and insn.op_str.startswith("0x") and not seen_jmp:
                    case, seen_jmp = int(insn.op_str, 16), True
                    break
                if insn.mnemonic in ("ret", "jmp"):
                    return None
            else:
                return None

    print("  op   idx   case VA      handled  handler      label")
    shared = {}
    for op, idx, case in rows:
        handled = case != default_case
        if a.undecoded and handled:
            continue
        mark = "yes" if handled else " NO"
        hs = ""
        if a.handlers and handled:
            h = handler_of(case)
            if h:
                shared.setdefault(h, []).append(op)
                hs = f"0x{h:08x}"
            else:
                hs = "(inline)"
        print(f"  0x{op:02x}  0x{idx:02x}  0x{case:08x}   {mark}     {hs:<12} {LABEL.get(op, '')}")

    if a.handlers and shared:
        print("\nopcodes sharing one handler (same parser => same body grammar):")
        for h, ops in sorted(shared.items()):
            if len(ops) > 1:
                print(f"   0x{h:08x}  <- " + ", ".join(f"0x{o:02x}" for o in ops))

    if a.op:
        want = int(a.op, 16)
        row = next((r for r in rows if r[0] == want), None)
        if row is None:
            sys.exit(f"opcode 0x{want:02x} is outside the dispatch range 0x{OP_LO:02x}..0x{OP_HI:02x}")
        case = row[2]
        print(f"\n--- case body for 0x{want:02x} @ 0x{case:08x} "
              f"{'(DEFAULT — opcode is ignored)' if case == default_case else ''} ---")
        for insn in md.disasm(img[case - ib:case - ib + a.bytes], case):
            note = ""
            if insn.mnemonic == "call":
                note = "      <-- handler?"
            print(f"0x{insn.address:08x}  {insn.bytes.hex():<16} {insn.mnemonic} {insn.op_str}{note}")
            if insn.mnemonic in ("ret", "jmp"):
                break


if __name__ == "__main__":
    main()
