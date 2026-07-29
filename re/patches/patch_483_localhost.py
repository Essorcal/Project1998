#!/usr/bin/env python
"""
4.83 client (KRU\\NexusTK483): redirect it to the LOCAL server (127.0.0.1:2000).

WHERE THE ADDRESS LIVES (RE'd 2026-07-27): NOT in the exe. The exe strings 210.101.85.30 / 2022
(at 0x4e9320 / 0x4e9318) are STALE compiled defaults that are overridden at runtime. The live
server list is a plaintext PAK entry named 'Address' inside NexusTK.dat -- a semicolon-separated
list of '<ip>.<port>;' where the LAST dotted segment is the port. Stock KRU value (60 bytes):
    210.192.90.2.2000;207.171.233.100.2000;209.144.160.157.2000;
All three use port 2000 -- which already matches the local login server -- so only the host(s)
need changing. (This mirrors how the 5.33 client is redirected via its own dat host list; the
4.95 client uses the same scheme.)

THE PATCH: overwrite that 60-byte 'Address' entry in place with four localhost:2000 slots:
    127.0.0.1.2000;127.0.0.1.2000;127.0.0.1.2000;127.0.0.1.2000;   (15 bytes x4 = exactly 60)
Every slot is 127.0.0.1:2000, so the client connects to the local server whichever slot it picks.
Same length => safe in-place edit, no repack. The entry is raw plaintext in the .dat (verified:
pak_extract is a straight slice, no decryption), and the PAK has no integrity checksum that
blocks this (same edit class used to retarget 4.95/5.33). Backs up only the original 60 bytes,
not the 57 MB dat.

CAVEAT (protocol): this only redirects the CONNECTION. The 4.83 client speaks an older protocol
than this server implements (4.95/5.33); it will reach 127.0.0.1:2000 and you'll see the connect
in the login-server log, but the handshake/login may not complete until/unless the server handles
the 4.83 wire format. Getting it to connect is step one.

    python re/patches/patch_483_localhost.py --check     # show current Address entry, no changes
    python re/patches/patch_483_localhost.py             # apply (client must be closed; needs write access to Program Files)
    python re/patches/patch_483_localhost.py --revert    # restore the original Address bytes

NOTE: writes under C:\\Program Files -- run from an elevated shell if you get PermissionError
(same as the 4.95 exe patch). Close the client first.
"""
import os, sys, shutil

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, ".."))   # for pak_list
from pak_list import parse

DAT   = r"C:\Program Files (x86)\KRU\NexusTK483\NexusTK.dat"
ENTRY = "Address"
BAK   = os.path.join(HERE, "backups", "NexusTK483.Address.orig")
LOCAL_UNIT = b"127.0.0.1.2000;"   # 15 bytes; one localhost:2000 slot


def find_entry(dat):
    _data, entries = parse(dat)
    for off, name, size in entries:
        if name.lower() == ENTRY.lower():
            return off, size
    raise SystemExit(f"[patch_483] no '{ENTRY}' entry in {dat}")


def read_at(path, off, n):
    with open(path, "rb") as f:
        f.seek(off)
        return f.read(n)


def ascii_of(b):
    return "".join(chr(x) if 32 <= x < 127 else "." for x in b)


def main():
    argv = sys.argv[1:]
    if not os.path.exists(DAT):
        raise SystemExit(f"[patch_483] dat not found: {DAT}")

    off, size = find_entry(DAT)
    cur = read_at(DAT, off, size)

    if size % len(LOCAL_UNIT) != 0:
        raise SystemExit(f"[patch_483] 'Address' is {size} bytes, not a multiple of {len(LOCAL_UNIT)} "
                         f"-- inspect it manually before patching (found: {ascii_of(cur)!r}).")
    payload = LOCAL_UNIT * (size // len(LOCAL_UNIT))   # exactly `size` bytes, all localhost:2000
    is_local = cur == payload

    if "--check" in argv:
        print(f"[patch_483] {DAT}")
        print(f"  'Address' @file 0x{off:x} ({size} bytes): {ascii_of(cur)}")
        print(f"  state: {'LOCALHOST (patched)' if is_local else 'not localhost'}")
        return

    if "--revert" in argv:
        if not os.path.exists(BAK):
            print(f"[patch_483] no backup at {BAK} -- nothing to revert.")
            return
        orig = open(BAK, "rb").read()
        if len(orig) != size:
            raise SystemExit(f"[patch_483] backup is {len(orig)} bytes but entry is {size} -- refusing.")
        with open(DAT, "r+b") as f:
            f.seek(off); f.write(orig)
        print(f"[patch_483] restored original 'Address': {ascii_of(orig)}")
        return

    # ---- apply ----
    if is_local:
        print("[patch_483] already pointing at localhost -- nothing to do. (--revert to undo.)")
        return

    os.makedirs(os.path.dirname(BAK), exist_ok=True)
    if not os.path.exists(BAK):
        with open(BAK, "wb") as f:
            f.write(cur)
        print(f"[patch_483] backed up original 'Address' ({size} bytes) -> {BAK}")
    else:
        print(f"[patch_483] backup already exists (kept): {BAK}")

    with open(DAT, "r+b") as f:
        f.seek(off); f.write(payload)

    check = read_at(DAT, off, size)
    if check == payload:
        print(f"[patch_483] PATCHED 'Address' -> {ascii_of(check)}")
        print("[patch_483] Boot the 4.83 client; watch the login-server log for the connection. (--revert to undo.)")
    else:
        print(f"[patch_483] WRITE VERIFICATION FAILED -- restore from {BAK}.")
        sys.exit(1)


if __name__ == "__main__":
    main()
