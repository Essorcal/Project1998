"""Override SObj.tbl directional wall flags in a client's asset archive.

WHY. SObj.tbl gives each object sprite a directional wall flag (UP=1 DOWN=2 RIGHT=4 LEFT=8, naming the
direction of TRAVEL that is refused; 0x0F = solid from every side, 0x0E = everything except walking
north into it, 0x00 = fully walkable). A sprite flagged solid that sits on a warp tile makes that warp
unreachable: the client applies SObj collision to its own walk locally and never sends the step, so the
server's "warps beat collision" rule (Session.HandleWalk) never gets a chance to fire.

The id/flag list comes from game-data/ObjectFlagOverrides.csv -- the same file the server's ObjectFlags
override reads -- so the client patch and the server override can never drift apart.

WHERE THE TABLE LIVES differs by client, so just point this at the archive:
    4.95 (NextAeon)   NexusTK.dat   entry "SObj.tbl"   7608 objects
    5.33 (NextAeon5)  Tile.dat      entry "SOBJ.TBL"  12696 objects (append-only superset)
A loose .tbl works too. Entries are stored uncompressed at absolute offsets and a flag is one byte, so
the patch is written IN PLACE: table length, PAK entry offsets and file size are all unchanged. No
repack, and --revert restores the backup.

Format of SObj.tbl (see Server/ObjectFlags.cs for the full derivation):
    u32 count
    u8  flag[0]                       # object 0, unused
    per object z = 1 .. count:
        u8  tileCount
        tileCount * u16               # frame ids
        5 bytes  FF FF FF FF 00       # separator
        u8  flag[z]                   # THIS object's flag, trailing its predecessor's frames

Usage:
    python re/patch_sobj_flags.py "C:/Program Files (x86)/Nexon/NextAeon/NexusTK.dat"
    python re/patch_sobj_flags.py "C:/Program Files (x86)/Nexon/NextAeon5/Tile.dat"
    python re/patch_sobj_flags.py <archive> --out staged/NexusTK.dat
    python re/patch_sobj_flags.py <archive> --revert
    python re/patch_sobj_flags.py <archive> --check      # report current flags, write nothing

Writing into Program Files needs an elevated shell. Without one, use --out to stage the patched copy
somewhere writable and copy it in afterwards; the .bak is written next to the OUTPUT, so a staged run
never touches the install at all.
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
OVERRIDES_CSV = os.path.join(REPO, "game-data", "ObjectFlagOverrides.csv")
TABLE_NAMES = ("sobj.tbl",)


def overrides(path=OVERRIDES_CSV):
    """[(objectId, flag)] from game-data/ObjectFlagOverrides.csv. Flag may be decimal or 0x-prefixed."""
    rows = []
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for raw in fh:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            col = line.split(",")
            if len(col) < 2 or not col[0].strip().isdigit():
                continue                                   # the "Obj,Flag,Note" header skips itself
            try:
                rows.append((int(col[0].strip()), int(col[1].strip(), 0)))
            except ValueError:
                print("  !! unparseable flag %r for object %s -- row skipped" % (col[1], col[0]))
    return rows


def find_table(data):
    """(start, end, label) of the SObj table: a PAK entry if this is an archive, else the whole file."""
    if len(data) >= 4:
        (count,) = struct.unpack_from("<I", data, 0)
        if 0 < count <= 100000 and 4 + count * 17 <= len(data):
            offs = []
            pos = 4
            for _ in range(count):
                off, raw = struct.unpack_from("<I13s", data, pos)
                offs.append((off, raw.split(b"\x00", 1)[0].decode("latin1", "replace")))
                pos += 17
            for i, (off, nm) in enumerate(offs):
                if nm.lower() in TABLE_NAMES:
                    end = offs[i + 1][0] if i + 1 < len(offs) else len(data)
                    return off, end, "PAK entry %r" % nm
            return None                                    # a PAK, but no SObj table in it
    return 0, len(data), "loose .tbl"


def flag_offsets(blob):
    """(objectCount, {objectId: byte offset of its flag within blob}, bytes consumed)."""
    (count,) = struct.unpack_from("<I", blob, 0)
    out = {}
    off = 4 + 1                       # u32 header + object 0's lead flag byte
    for z in range(1, count + 1):
        if off >= len(blob):
            break
        tc = blob[off]
        off += 1 + tc * 2 + 5         # tileCount + frame ids + FF FF FF FF 00
        if off >= len(blob):
            break
        out[z] = off
        off += 1
    return count, out, off


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    target = argv[1]
    rest = argv[2:]
    revert = "--revert" in rest
    check = "--check" in rest
    out = target
    if "--out" in rest:
        i = rest.index("--out")
        if i + 1 >= len(rest):
            print("!! --out needs a path")
            return 2
        out = rest[i + 1]
    # The backup pairs with whatever we WRITE: an --out run stages a copy and must leave the install alone.
    backup = out + ".presobjpatch.bak"

    if revert:
        if not os.path.exists(backup):
            print("!! no backup at %s -- nothing to revert" % backup)
            return 1
        with open(backup, "rb") as fh:
            data = fh.read()
        with open(out, "wb") as fh:
            fh.write(data)
        print("reverted %s from %s (%d bytes)" % (out, backup, len(data)))
        return 0

    rows = overrides()
    if not rows:
        print("!! game-data/ObjectFlagOverrides.csv lists no rows -- nothing to do")
        return 1

    with open(target, "rb") as fh:
        data = bytearray(fh.read())

    span = find_table(data)
    if span is None:
        print("!! %s is a PAK archive with no SObj.tbl entry -- wrong file?" % target)
        print("   4.95 keeps it in NexusTK.dat; 5.33 keeps it in Tile.dat.")
        return 1
    base, end, label = span
    blob = data[base:end]
    count, offsets, consumed = flag_offsets(blob)
    print("%s: %s, %d bytes at offset %d" % (target, label, len(blob), base))
    print("table holds %d objects (walk consumed %d of %d bytes)" % (count, consumed, len(blob)))
    if consumed < len(blob) - 1:
        print("  !! the walk did not consume the table -- format mismatch, refusing to write")
        return 1

    changed = 0
    for oid, flag in rows:
        if oid not in offsets:
            print("  !! object %d is outside this table (count=%d) -- skipped" % (oid, count))
            continue
        at = base + offsets[oid]
        was = data[at]
        if was == flag:
            print("  == object %-5d already 0x%02x" % (oid, flag))
            continue
        if check:
            print("  ?? object %-5d is 0x%02x, csv wants 0x%02x" % (oid, was, flag))
            changed += 1
            continue
        data[at] = flag
        changed += 1
        print("  -> object %-5d flag 0x%02x -> 0x%02x at file offset %d" % (oid, was, flag, at))

    if check:
        print("--check: %d row(s) would change; nothing written." % changed)
        return 0
    if not changed:
        print("nothing to write.")
        return 0

    if not os.path.exists(backup):
        with open(backup, "wb") as fh:
            fh.write(open(target, "rb").read())
        print("backup written: %s" % backup)
    with open(out, "wb") as fh:
        fh.write(data)
    print("patched %s (%d sprite(s), size unchanged: %d bytes)" % (out, changed, len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
