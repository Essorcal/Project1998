#!/usr/bin/env python
"""Parameterized string/const finder over ANY NexusTK exe (disx.py is hardwired to 4.95).
Usage:
  python re/exestr.py <exe> str <substr>      # ASCII strings containing substr
  python re/exestr.py <exe> ip                # IP-like ASCII strings (d.d.d.d)
  python re/exestr.py <exe> xref <hexconst>   # 4-byte LE occurrences of a const/addr
  python re/exestr.py <exe> imp               # imported connection APIs
"""
import sys, re, pefile
exe=sys.argv[1]; mode=sys.argv[2]
pe=pefile.PE(exe, fast_load=True)
IB=pe.OPTIONAL_HEADER.ImageBase
def secs(): 
    for s in pe.sections: yield IB+s.VirtualAddress, s.get_data()
if mode=="str":
    sub=sys.argv[3].encode()
    for base,blob in secs():
        i=0
        while True:
            i=blob.find(sub,i)
            if i<0: break
            j=i
            while j>0 and 32<=blob[j-1]<127: j-=1
            k=i
            while k<len(blob) and 32<=blob[k]<127: k+=1
            print(f"0x{base+j:08x}  {blob[j:k].decode(errors='replace')!r}")
            i=k
elif mode=="ip":
    pat=re.compile(rb"(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)")
    seen=set()
    for base,blob in secs():
        for m in pat.finditer(blob):
            s=m.group().decode()
            if s not in seen:
                seen.add(s); print(f"0x{base+m.start():08x}  {s}")
elif mode=="xref":
    t=int(sys.argv[3],0); tb=t.to_bytes(4,"little")
    for base,blob in secs():
        i=0
        while True:
            i=blob.find(tb,i)
            if i<0: break
            print(f"0x{base+i:08x} -> 0x{t:08x}")
            i+=1
elif mode=="imp":
    pe.parse_data_directories()
    want=("connect","gethost","inet_","WSA","socket","send","recv","htons","ntohs")
    for e in pe.DIRECTORY_ENTRY_IMPORT:
        for imp in e.imports:
            n=(imp.name or b"").decode(errors="replace")
            if any(w.lower() in n.lower() for w in want):
                print(f"{e.dll.decode():16s} {n}  @IAT 0x{imp.address:08x}")

if len(sys.argv)>2 and sys.argv[2]=="dis":
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    data=pe.get_memory_mapped_image(); md=Cs(CS_ARCH_X86,CS_MODE_32)
    va=int(sys.argv[3],0); n=int(sys.argv[4],0) if len(sys.argv)>4 else 160
    code=data[va-IB:va-IB+n]
    for insn in md.disasm(code, va):
        print(f"0x{insn.address:08x}  {insn.bytes.hex():<14} {insn.mnemonic} {insn.op_str}")
