#!/usr/bin/env python
"""
4.95 client (NexusTK_local.exe): remove the always-on floating nameplate/triangle marker
above every player.

RE'd 2026-07-27 (see memory/nexustk-495-nametag-litter.md): each 0x33 appearance packet for a
player (renderKind=1) unconditionally builds a new decoration/marker sprite (ctor 0x463380,
called from the 0x33 handler 0x44fef0) and attaches it WITHOUT freeing the previous one -> a
leaked marker per appearance refresh. NULLing the ctor's return (the caller already treats NULL
as "nothing to attach", the same path other renderKind values take) removes the marker and the
leak; the avatar body/movement/combat are unaffected. Live-confirmed via re/frida_nametag.py.

Patch: overwrite the ctor's first 5 bytes  55 8b ec 6a ff  (push ebp; mov ebp,esp; push -1)
with  33 c0 c2 14 00  (xor eax,eax; ret 0x14) -- return NULL and pop the caller's 5 dword args
(0x14), matching the original function's own "ret 0x14" epilogue so the stack stays balanced.

    python re/patches/patch_495_no_nametag.py            # apply
    python re/patches/patch_495_no_nametag.py --check     # report state
    python re/patches/patch_495_no_nametag.py --revert    # undo
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from patchlib import Patch, run
from _paths import CLIENT

EXE = str(CLIENT / "NexusTK_local.exe")
# Keep the pre-existing pristine backup so --revert still finds it (created by the original
# re/patch_no_nametag.py before this script was reorganized into re/patches/).
BAK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "NexusTK_local.exe.prenametagpatch.bak")

PATCHES = [
    Patch(
        va=0x463380,
        original=bytes.fromhex("558bec6aff"),   # push ebp; mov ebp,esp; push -1
        patched=bytes.fromhex("33c0c21400"),    # xor eax,eax; ret 0x14  (return NULL)
        desc="no floating nameplate/triangle marker (NULL the marker ctor; caller handles NULL)",
    ),
]

if __name__ == "__main__":
    run(EXE, os.path.normpath(BAK), PATCHES)
