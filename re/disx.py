#!/usr/bin/env python
"""Tiny linear disassembler for NexusTK_local.exe (ImageBase 0x400000, no ASLR).

Usage:
  python re/disx.py 0x44a780            # disassemble from VA until RET/limit
  python re/disx.py 0x44a780 400        # ...for up to N bytes
  python re/disx.py xref 0x50211c       # find code that references an address/const
  python re/disx.py str Maps            # find ASCII strings containing substring + their VA
"""
import sys, pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_32

EXE = r"C:\Program Files (x86)\Nexon\NextAeon\NexusTK_local.exe"
pe = pefile.PE(EXE, fast_load=True)
IB = pe.OPTIONAL_HEADER.ImageBase  # 0x400000
data = pe.get_memory_mapped_image()  # indexed by RVA
md = Cs(CS_ARCH_X86, CS_MODE_32)
md.detail = True

def va_bytes(va, n):
    rva = va - IB
    return data[rva:rva+n]

def disasm(va, maxbytes=256):
    code = va_bytes(va, maxbytes)
    for insn in md.disasm(code, va):
        print(f"0x{insn.address:08x}  {insn.bytes.hex():<16} {insn.mnemonic} {insn.op_str}")
        if insn.mnemonic == "ret":
            break

def find_xref(target):
    # scan .text for 4-byte little-endian occurrences of target
    hits = []
    tb = target.to_bytes(4, "little")
    for sec in pe.sections:
        if not (sec.Characteristics & 0x20000000):  # MEM_EXECUTE
            continue
        base = IB + sec.VirtualAddress
        blob = sec.get_data()
        start = 0
        while True:
            i = blob.find(tb, start)
            if i < 0: break
            hits.append(base + i)
            start = i + 1
    return hits

def find_str(sub):
    subb = sub.encode()
    out = []
    for sec in pe.sections:
        base = IB + sec.VirtualAddress
        blob = sec.get_data()
        start = 0
        while True:
            i = blob.find(subb, start)
            if i < 0: break
            # extend to full printable string
            j = i
            while j > 0 and 32 <= blob[j-1] < 127: j -= 1
            k = i
            while k < len(blob) and 32 <= blob[k] < 127: k += 1
            out.append((base + j, blob[j:k].decode(errors="replace")))
            start = k
    return out

if __name__ == "__main__":
    a = sys.argv
    if len(a) >= 2 and a[1] == "xref":
        t = int(a[2], 0)
        for h in find_xref(t):
            print(f"xref @ 0x{h:08x} -> 0x{t:08x}")
    elif len(a) >= 2 and a[1] == "str":
        for va, s in find_str(a[2]):
            print(f"0x{va:08x}  {s!r}")
    else:
        va = int(a[1], 0)
        n = int(a[2], 0) if len(a) > 2 else 256
        disasm(va, n)
