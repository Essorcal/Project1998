#!/usr/bin/env python
"""
4.95 client (NexusTK_local.exe): make the 'm' (mailbox) hotkey actually work.

STATUS: written + verified against this build, but DELIBERATELY NOT APPLIED (2026-07-28). Patching the
client wasn't wanted, and this patch can only make 'm' a duplicate of 'b' (open the board window, whose
LAST entry is "Mailbox") — it cannot jump straight into the mailbox. Why not: the client renders a
mailbox 0x31 only into an ALREADY-OPEN board window (an unsolicited one was tested live and opened
nothing), so no key could be pointed at a "show me my mailbox" packet. Kept for the RE record and in
case the hotkey is wanted later. Run with --check to confirm it still matches before applying.

RE'd 2026-07-28. The '?' help screen advertises "m = mailbox" but the key does nothing —
and now we know EXACTLY why. The in-world single-letter hotkeys are dispatched by a char
switch @0x48e625 inside the gameplay-screen key consumer:

    idx  = (char & 0xff) - 0x0d            ; bound-checked against 0x83
    case = byteTable[idx]                  ; byte-index table @0x48eab0 (132 entries)
    jmp  dwordTable[case]                  ; jump table @0x48e9e8 (50 cases)

The decoded map covers every working hotkey ('b'=case 22 -> board window, 'i'=28 inventory,
's'=32 spells, ...). 'm' (and 'M') sit in case 49 = the default do-nothing bucket, alongside
x/z/q/n. The help line is a leftover string in NexusTK.dat; the binding was never shipped.

The fix is ONE byte: point 'm' at case 22 — the exact handler the 'b' key uses (0x48e3f0:
construct the board window, which requests the 0x3B sub-1 board list; our server lists "Mail"
(board 0, the nmail mailbox) as the first entry). This is also what the NATIVE mail-arrow
click does when you have unread mail (its handler 0x469654 checks hasMail [widget+0x106] and
calls the same board-window ctor 0x406e80(1)), so 'm' behaves exactly like the client's own
"you have mail" flow. There is no richer direct-to-mailbox opener in the binary to point at.

('M' = shift+m stays default, matching the other hotkeys which are all case-specific; flip
byteTable[0x40] @0x48eaf0 the same way if shift+m is ever wanted.)

    python re/patches/patch_495_mail_key.py            # apply
    python re/patches/patch_495_mail_key.py --check     # report state
    python re/patches/patch_495_mail_key.py --revert    # undo (restores backup)
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from patchlib import Patch, run
from _paths import CLIENT

EXE = str(CLIENT / "NexusTK_local.exe")
# Per-patch pristine backup (this captures the exe as of applying THIS patch — i.e. with the
# no-nametag patch already in it, which is the desired baseline to revert to).
BAK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "NexusTK_local.exe.premailkeypatch.bak")

PATCHES = [
    Patch(
        va=0x48eab0 + (ord('m') - 0x0d),        # byteTable['m'] = 0x48eb10
        original=bytes([49]),                    # case 49 = default (do nothing)
        patched=bytes([22]),                     # case 22 = the 'b' handler (open board window)
        desc="'m' hotkey opens the board/mail window (was dead; help-string promised it)",
    ),
]

if __name__ == "__main__":
    run(EXE, os.path.normpath(BAK), PATCHES)
