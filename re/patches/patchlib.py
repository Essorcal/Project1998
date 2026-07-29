#!/usr/bin/env python
"""
Shared binary-patch engine for the NexusTK clients (4.83 / 4.95 / 5.33).

Each per-client script (patch_483_*.py, patch_495_*.py, patch_533_*.py) just declares:
  * EXE     -- absolute path to that client's executable
  * BAK     -- where to stash the pristine backup
  * PATCHES -- a list of Patch(va, original, patched, desc)
and calls run(EXE, BAK, PATCHES).

Safety rules enforced here (so a wrong address can't silently corrupt an exe):
  * A patch is applied ONLY if the bytes currently at its VA equal its recorded `original`.
    Any mismatch => the whole run refuses (all-or-nothing), pointing you back to re/disx.py.
  * `va=None` or `original=None` means "address not yet located for this build" -> refuse.
  * The full exe is backed up ONCE (never overwriting an existing backup) before the first
    write, so --revert always restores the true pristine binary even across multiple patches.

Addresses are build-specific. The 4.95 no-nametag VA (0x463380) does NOT transfer to 4.83
or 5.33 -- each build must be reversed on its own (find the 0x33 appearance handler, then the
renderKind==1 marker/decoration ctor it calls). See re/patches/README.md.

Modes (per-client scripts forward argv straight through):
    python re/patches/patch_495_no_nametag.py            # apply
    python re/patches/patch_495_no_nametag.py --check     # report state, no changes
    python re/patches/patch_495_no_nametag.py --revert    # restore exe from backup
"""
import sys, os, shutil, pefile
from dataclasses import dataclass


@dataclass
class Patch:
    va: int | None            # virtual address (ImageBase 0x400000, no ASLR); None = not located yet
    original: bytes | None    # exact bytes expected at `va` before patching; None = not located yet
    patched: bytes            # bytes to write (must be same length as `original` once located)
    desc: str                 # human description of what this patch does


def _offset(pe, va):
    return pe.get_offset_from_rva(va - pe.OPTIONAL_HEADER.ImageBase)


def _read(path, off, n):
    with open(path, "rb") as f:
        f.seek(off)
        return f.read(n)


def _state(cur, p):
    if p.original is not None and cur == p.original:
        return "UNPATCHED"
    if cur == p.patched:
        return "PATCHED"
    return "UNKNOWN"


def run(exe, bak, patches, argv=None):
    argv = argv if argv is not None else sys.argv[1:]
    name = os.path.basename(sys.argv[0])

    if not os.path.exists(exe):
        print(f"[{name}] EXE not found: {exe}")
        sys.exit(1)

    if not patches:
        print(f"[{name}] No patches defined for this client yet. Add a Patch(...) entry and re-run.")
        return

    pe = pefile.PE(exe, fast_load=True)

    # Resolve current bytes for each locatable patch.
    rows = []
    for p in patches:
        if p.va is None or p.original is None:
            rows.append((p, None, None))
            continue
        off = _offset(pe, p.va)
        cur = _read(exe, off, len(p.patched))
        rows.append((p, off, cur))

    # ---- --check ----
    if "--check" in argv:
        print(f"[{name}] {exe}")
        for p, off, cur in rows:
            if off is None:
                print(f"  - NOT LOCATED  (va/original unset)   : {p.desc}")
            else:
                print(f"  - {_state(cur, p):9s} @file 0x{off:x} (cur {cur.hex()}) : {p.desc}")
        return

    # ---- --revert ----
    if "--revert" in argv:
        if not os.path.exists(bak):
            print(f"[{name}] No backup at {bak} -- nothing to revert.")
            return
        shutil.copy2(bak, exe)
        print(f"[{name}] Restored {exe}\n           from {bak}")
        return

    # ---- apply (default): validate ALL first, then write ----
    unlocated = [p for p, off, cur in rows if off is None]
    if unlocated:
        print(f"[{name}] REFUSING TO PATCH: {len(unlocated)} patch(es) have no located address for this build:")
        for p in unlocated:
            print(f"    - {p.desc}")
        print("  Locate them with re/disx.py, fill in va + original, then re-run.")
        sys.exit(1)

    already = all(cur == p.patched for p, off, cur in rows)
    if already:
        print(f"[{name}] Already patched -- nothing to do. (--revert to undo.)")
        return

    bad = [(p, off, cur) for p, off, cur in rows if cur != p.original and cur != p.patched]
    if bad:
        print(f"[{name}] REFUSING TO PATCH: bytes don't match the recorded original (exe updated/replaced?):")
        for p, off, cur in bad:
            print(f"    @file 0x{off:x}: found {cur.hex()}, expected {p.original.hex()}  ({p.desc})")
        print("  Re-verify the address in THIS build with re/disx.py before patching.")
        sys.exit(1)

    for p, off, cur in rows:
        if len(p.patched) != len(p.original):
            print(f"[{name}] REFUSING: patched/original length mismatch for '{p.desc}'.")
            sys.exit(1)

    # Back up the pristine exe once (never clobber an existing backup).
    os.makedirs(os.path.dirname(bak), exist_ok=True)
    if not os.path.exists(bak):
        shutil.copy2(exe, bak)
        print(f"[{name}] Backed up pristine exe -> {bak}")
    else:
        print(f"[{name}] Backup already exists (kept): {bak}")

    with open(exe, "r+b") as f:
        for p, off, cur in rows:
            f.seek(off)
            f.write(p.patched)

    # Verify.
    ok = True
    for p, off, cur in rows:
        v = _read(exe, off, len(p.patched))
        state = "OK" if v == p.patched else "FAILED"
        if v != p.patched:
            ok = False
        print(f"  {state:6s} @file 0x{off:x} -> {v.hex()}  ({p.desc})")
    if ok:
        print(f"[{name}] Patched successfully. Restart the client to take effect. (--revert to undo.)")
    else:
        print(f"[{name}] WRITE VERIFICATION FAILED -- restore from {bak} immediately.")
        sys.exit(1)
