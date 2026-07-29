#!/usr/bin/env python
"""
5.33 client (Nexon\\NextAeon5\\NexusTK.exe): per-client patch script.

No patches are defined yet -- this build has not been reversed for binary patching. The 4.95
no-nametag address (0x463380) is 4.95-specific and must NOT be reused here; locate any 5.33
target in THIS binary first.

To add a patch:
  1. Find the target VA in this exe (re/disx.py; note 5.33 may differ in ImageBase/layout -- verify).
  2. Read the exact bytes at that VA (-> `original`), choose the replacement (`patched`, same length).
  3. Add a Patch(...) below; run --check first, then apply. The engine refuses on any mismatch.

    python re/patches/patch_533.py --check
    python re/patches/patch_533.py            # apply (once a Patch is defined)
    python re/patches/patch_533.py --revert
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from patchlib import Patch, run

EXE = r"C:\Program Files (x86)\Nexon\NextAeon5\NexusTK.exe"
BAK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "backups", "NexusTK533.exe.bak")

PATCHES = [
    # Example shape (leave commented until the address is confirmed IN THE 5.33 BUILD):
    # Patch(
    #     va=0x00000000,
    #     original=bytes.fromhex("...."),
    #     patched=bytes.fromhex("...."),
    #     desc="what this does",
    # ),
]

if __name__ == "__main__":
    run(EXE, BAK, PATCHES)
