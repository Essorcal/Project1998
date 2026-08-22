#!/usr/bin/env python
"""
5.33 client: point it at the LOCAL server's 5.x lane (127.0.0.1:2001).

WHERE THE ADDRESS LIVES. Not in the exe, and not in the registry. The login server list is a plaintext
PAK entry named 'Connaddr' inside NexusTK.dat -- four CRLF-terminated `host port` lines, space-padded to
an exact fixed length (77 bytes in the stock 5.33 dat). Stock value:

    64.124.47.60 2000 / 64.124.47.61 2000 / tk0.nexon.net 2000 / 65.203.45.40 2000

The client walks the list top to bottom, and THIS FILE IS THE ONLY THING that controls the login target.
The `/host: /portno: /id:` command-line args, `HKCU\\SOFTWARE\\Nexon\\Kingdom of the Winds\\Servers`, and a
hosts-file remap of game.kornetworld.com are all confirmed dead ends (see docs/5.x/Client-Setup.md).

PORT 2001, NOT 2000. The unified server tags a session's client version by the port it arrived on --
2000/2005 = 4.95, 2001/2006 = 5.33 -- so the 4.95 code path is never entered by a 5.33 session. Pointing
5.33 at 2000 would make it a V495 session and it would be served 2-short terrain cells it cannot parse.

THE PATCH. Rewrite the entry in place with every slot set to 127.0.0.1:<port>, padded with trailing
spaces before each CRLF to preserve the exact entry length -- so it is a safe in-place edit, no repack.
The padding shape is generated, then checked byte-for-byte against the proven-good payload in
client-5.33-redirect/NexusTK.dat.patched, which is the layout known to work with this client.

This supersedes client-5.33-redirect/Deploy-Connaddr-2001.bat, which copied a whole pre-patched 1.9 MB
NexusTK.dat over a HARDCODED Program Files path. That blob is a snapshot of one build and clobbers every
other entry in the archive; this touches 77 bytes and works on any install directory.

    python re/patches/patch_533_connaddr.py --check                      # show current value, no changes
    python re/patches/patch_533_connaddr.py                              # apply (127.0.0.1:2001)
    python re/patches/patch_533_connaddr.py --revert                     # restore the original entry
    python re/patches/patch_533_connaddr.py --client "C:\\Users\\me\\NextAeon533"
    python re/patches/patch_533_connaddr.py --host 192.168.1.10 --port 2001

Close the client first. A stock Program Files install needs an elevated shell to write.
"""
import os, sys, pathlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, str(pathlib.Path(HERE).parent))     # for pak_list / _paths
from pak_list import parse
from _paths import CLIENT5, ROOT

ENTRY = "Connaddr"
BAK = os.path.join(HERE, "backups", "NextAeon533.Connaddr.orig")
PROVEN = ROOT / "client-5.33-redirect" / "NexusTK.dat.patched"


def build_payload(size, host, port, slots=4):
    """`host port` + trailing-space padding + CRLF per slot, totalling exactly `size` bytes.

    Padding goes AFTER the port and before the CRLF -- the shape the proven-good dat uses. Spare bytes
    are spread across the slots (later slots first), which reproduces the stock 17/17/18/17 split.
    """
    body = f"{host} {port}".encode()
    fixed = (len(body) + 2) * slots            # +2 for each CRLF
    spare = size - fixed
    if spare < 0:
        raise SystemExit(f"[patch_533_connaddr] '{host} {port}' does not fit in {size} bytes across {slots} slots.")
    pads = [spare // slots] * slots
    for i in range(spare % slots):             # distribute the remainder from the back
        pads[slots - 1 - i] += 1
    # the proven dat puts the extra space on slot 3 (0-based 2), not the last -- match it exactly
    if slots == 4 and spare % slots == 1:
        pads = [spare // slots] * slots
        pads[2] += 1
    return b"".join(body + b" " * p + b"\r\n" for p in pads)


def find_entry(dat):
    _data, entries = parse(dat)
    for off, name, size in entries:
        if name.lower() == ENTRY.lower():
            return off, size
    raise SystemExit(f"[patch_533_connaddr] no '{ENTRY}' entry in {dat}")


def read_at(path, off, n):
    with open(path, "rb") as f:
        f.seek(off)
        return f.read(n)


def show(b):
    return " / ".join(x.decode("latin1").strip() for x in b.split(b"\r\n") if x.strip())


def main():
    argv = sys.argv[1:]

    def opt(flag, default):
        return argv[argv.index(flag) + 1] if flag in argv else default

    client = pathlib.Path(opt("--client", str(CLIENT5)))
    host = opt("--host", "127.0.0.1")
    port = opt("--port", "2001")

    dat = str(client / "NexusTK.dat")
    if not os.path.exists(dat):
        raise SystemExit(f"[patch_533_connaddr] NexusTK.dat not found: {dat}\n"
                         f"  Pass --client <install dir> or set P1998_CLIENT5.")

    off, size = find_entry(dat)
    cur = read_at(dat, off, size)
    payload = build_payload(size, host, port)

    # Cross-check the generated padding against the layout proven to work with this client.
    if PROVEN.exists() and host == "127.0.0.1" and port == "2001":
        p_off, p_size = find_entry(str(PROVEN))
        proven = read_at(str(PROVEN), p_off, p_size)
        if p_size == size and proven != payload:
            raise SystemExit(f"[patch_533_connaddr] generated payload differs from the proven-good one -- refusing.\n"
                             f"  generated: {payload!r}\n  proven   : {proven!r}")

    print(f"[patch_533_connaddr] {dat}")
    print(f"  '{ENTRY}' @file 0x{off:x} ({size} bytes)")
    print(f"  current: {show(cur)}")
    print(f"  target : {show(payload)}")

    if "--check" in argv:
        print(f"  state: {'PATCHED' if cur == payload else 'stock / other target'}")
        return

    if "--revert" in argv:
        if not os.path.exists(BAK):
            print(f"[patch_533_connaddr] no backup at {BAK} -- nothing to revert.")
            return
        orig = open(BAK, "rb").read()
        if len(orig) != size:
            raise SystemExit(f"[patch_533_connaddr] backup is {len(orig)} bytes but the entry is {size} -- refusing.")
        with open(dat, "r+b") as f:
            f.seek(off); f.write(orig)
        print(f"[patch_533_connaddr] restored: {show(orig)}")
        return

    if cur == payload:
        print("[patch_533_connaddr] already pointing there -- nothing to do. (--revert to undo.)")
        return

    os.makedirs(os.path.dirname(BAK), exist_ok=True)
    if not os.path.exists(BAK):
        with open(BAK, "wb") as f:
            f.write(cur)
        print(f"[patch_533_connaddr] backed up the original '{ENTRY}' ({size} bytes) -> {BAK}")
    else:
        print(f"[patch_533_connaddr] backup already exists (kept): {BAK}")

    with open(dat, "r+b") as f:
        f.seek(off); f.write(payload)

    back = read_at(dat, off, size)
    if back == payload:
        print(f"[patch_533_connaddr] PATCHED -> {show(back)}")
        print(f"[patch_533_connaddr] Start the server, launch the client; it should hit :{port} then :{int(port)+5}.")
    else:
        print(f"[patch_533_connaddr] WRITE VERIFICATION FAILED -- restore from {BAK}.")
        sys.exit(1)


if __name__ == "__main__":
    main()
