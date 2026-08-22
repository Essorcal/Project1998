#!/usr/bin/env python
"""
5.33 client: restore the 4.x OBJECT COLLISION flags in SOBJ.TBL, so 4.x maps are walkable the way
they were authored.

THE PROBLEM. Both clients run object collision LOCALLY, against their own SObj.tbl -- that is why you
cannot walk through a hut wall that sits on walkable ground. Nexon RE-AUTHORED those flag bytes for
5.33: 362 differ over the shared id range 1..7608, 234 of them blocking a direction 4.x did not. Serving
our 4.x map set to a 5.33 client therefore produces a STRUCTURALLY different world, not a cosmetic one:

    17,841 cells across 397 maps are walkable for a 4.95 player and solid for a 5.33 player
     3,296 cells go the other way -- 5.33 starts the step, the server refuses it, the player rubber-bands

The server cannot fix this. Object GRAPHIC and object COLLISION are the same u16 on the wire, so its
only levers are "send a different object" or "send none" (game-data/Obj533Fix.csv, P1998_OBJ_FIX_533).
A visually identical substitute exists for just 4 of 128 affected objects; everything else can only be
blanked, which deletes the artwork. Only the `all` scope reaches parity, and it does so by removing
building fronts and fences.

THE PATCH. Rewrite the differing flag bytes in the client's own SOBJ.TBL to their 4.x values. Same
length, in place, no repack -- SOBJ.TBL is a plain PAK entry inside Tile.dat. Verified offline against
all 1,840 shipped maps: cells reachable on 4.x but not on 5.33 goes 17,841 -> 0, with ZERO artwork loss.
Arctic Village (3811), the case that surfaced this, goes 2,437 -> 2,921 reachable == exact 4.x parity.

SCOPE. Two variants, both measured to give full parity on our current map set:
    --full     (default) all 362 differing ids. Future-proof: covers objects our maps don't place yet.
    --minimal  only the 157 ids our 1,840 maps actually place. Smaller footprint, same result today.
`--full` is the default deliberately. The obvious argument for `--minimal` is "touch less of a table
other map sets might rely on" -- but RTK/7.x's own SObj.tbl AGREES WITH 4.x on 352 of these 362 ids
(4.x vs 7.x differ by only 21 bytes over the shared range, vs 362 for 5.33). 5.33's re-authoring is a
one-generation outlier that the next era undid, so restoring 4.x values moves this client TOWARD later
map sets, not away from them.

NOT HARDCODED. The byte list is DERIVED at run time by walking both tables, so it cannot rot against a
different client build or an edited game-data/SObj.tbl -- if either side changes, the diff changes with
it. The SOBJ.TBL location comes from the PAK directory, not a fixed offset.

AFTER PATCHING: set P1998_OBJ_FIX_533=off. That workaround exists only to paper over this divergence;
leaving it on keeps substituting objects for no reason.

    python re/patches/patch_533_sobj_flags.py --check                 # report state, no changes
    python re/patches/patch_533_sobj_flags.py                         # apply (--full)
    python re/patches/patch_533_sobj_flags.py --minimal               # apply the 157-id variant
    python re/patches/patch_533_sobj_flags.py --revert                # restore the original SOBJ.TBL
    python re/patches/patch_533_sobj_flags.py --client "D:\\NextAeon533"

Close the client first. A stock Program Files install needs an elevated shell to write.
"""
import os, sys, struct, pathlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, str(pathlib.Path(HERE).parent))     # for pak_list / _paths
from pak_list import parse
from _paths import CLIENT5, DATA

ENTRY = "SOBJ.TBL"
BAK = os.path.join(HERE, "backups", "NextAeon533.SOBJ.TBL.orig")


def walk(buf):
    """Parse an SObj table. Returns (count, {objId: flagByte}, {objId: offset of that flag byte}).

    Layout (see Server/ObjectFlags.cs -- the flag PRECEDES the next record's frames):
        u32 count
        u8  flag[0]
        per z: u8 tileCount; tileCount*u16 frames; 5-byte FF FF FF FF 00; u8 flag[z+1]
    """
    count = struct.unpack_from("<I", buf, 0)[0]
    flags, offs = {}, {}
    off = 4 + 1                       # u32 count + object 0's flag byte
    for z in range(1, count + 1):
        if off >= len(buf):
            break
        tc = buf[off]; off += 1
        off += tc * 2 + 5             # frames + the FF FF FF FF 00 separator
        if off >= len(buf):
            break
        flags[z] = buf[off]; offs[z] = off
        off += 1
    return count, flags, offs


def placed_object_ids():
    """Object ids our shipped maps actually place -- the --minimal set."""
    used = set()
    maps = DATA / "maps"
    for fn in os.listdir(maps):
        if not fn.lower().endswith(".map"):
            continue
        d = (maps / fn).read_bytes()
        for i in range(len(d) // 4):
            o = struct.unpack_from("<H", d, i * 4 + 2)[0] & 0x3FFF
            if o:
                used.add(o)
    return used


def find_entry(dat):
    _data, entries = parse(dat)
    for off, name, size in entries:
        if name.upper() == ENTRY:
            return off, size
    raise SystemExit(f"[patch_533_sobj] no '{ENTRY}' entry in {dat}")


def read_at(path, off, n):
    with open(path, "rb") as f:
        f.seek(off)
        return f.read(n)


def main():
    argv = sys.argv[1:]
    client = pathlib.Path(CLIENT5)
    if "--client" in argv:
        client = pathlib.Path(argv[argv.index("--client") + 1])
    dat = str(client / "Tile.dat")
    if not os.path.exists(dat):
        raise SystemExit(f"[patch_533_sobj] Tile.dat not found: {dat}\n"
                         f"  Pass --client <install dir> or set P1998_CLIENT5.")

    src = DATA / "SObj.tbl"
    if not src.exists():
        raise SystemExit(f"[patch_533_sobj] 4.x reference table not found: {src}")

    ent_off, ent_size = find_entry(dat)
    cur = bytearray(read_at(dat, ent_off, ent_size))
    c4, f4, _ = walk(src.read_bytes())
    c5, f5, o5 = walk(cur)
    shared = min(c4, c5)

    minimal = "--minimal" in argv
    diff = [z for z in range(1, shared + 1) if z in f4 and z in f5 and f4[z] != f5[z]]
    if minimal:
        placed = placed_object_ids()
        target = [z for z in diff if z in placed]
    else:
        target = diff
    todo = [z for z in target if f5[z] != f4[z]]

    print(f"[patch_533_sobj] {dat}")
    print(f"  '{ENTRY}' @file 0x{ent_off:x} ({ent_size} bytes)  records: 4.x={c4}  client={c5}")
    print(f"  scope: {'minimal (ids our maps place)' if minimal else 'full (every differing id)'}")
    print(f"  flag bytes differing from 4.x over ids 1..{shared}: {len(diff)}")
    print(f"  in scope: {len(target)}   still to write: {len(todo)}")

    if "--check" in argv:
        state = "PATCHED (4.x flags restored)" if not todo else \
                ("PARTIAL" if len(todo) < len(target) else "UNPATCHED (stock 5.33 flags)")
        print(f"  state: {state}")
        return

    if "--revert" in argv:
        if not os.path.exists(BAK):
            print(f"[patch_533_sobj] no backup at {BAK} -- nothing to revert.")
            return
        orig = open(BAK, "rb").read()
        if len(orig) != ent_size:
            raise SystemExit(f"[patch_533_sobj] backup is {len(orig)} bytes but the entry is {ent_size} -- refusing.")
        with open(dat, "r+b") as f:
            f.seek(ent_off); f.write(orig)
        print(f"[patch_533_sobj] restored the original {ENTRY} ({ent_size} bytes) from {BAK}")
        return

    # ---- apply ----
    if not todo:
        print("[patch_533_sobj] already patched -- nothing to do. (--revert to undo.)")
        return

    os.makedirs(os.path.dirname(BAK), exist_ok=True)
    if not os.path.exists(BAK):
        with open(BAK, "wb") as f:
            f.write(cur)
        print(f"[patch_533_sobj] backed up the pristine {ENTRY} ({ent_size} bytes) -> {BAK}")
    else:
        print(f"[patch_533_sobj] backup already exists (kept): {BAK}")

    for z in todo:
        cur[o5[z]] = f4[z]

    with open(dat, "r+b") as f:
        f.seek(ent_off); f.write(bytes(cur))

    # ---- verify: re-read from disk, re-parse, confirm structure AND flags ----
    back = bytearray(read_at(dat, ent_off, ent_size))
    if len(back) != ent_size:
        raise SystemExit(f"[patch_533_sobj] WRITE VERIFICATION FAILED (size) -- restore from {BAK}.")
    c5b, f5b, _ = walk(back)
    ok_struct = (c5b == c5 and len(f5b) == len(f5))
    ok_flags = all(f5b[z] == f4[z] for z in target)
    untouched = sum(1 for z in range(shared + 1, c5b + 1) if z in f5b and z in f5 and f5b[z] == f5[z])
    changed = sum(1 for a, b in zip(back, read_at(BAK, 0, ent_size)) if a != b)

    print(f"  bytes changed: {changed} (expected {len(todo)})")
    print(f"  re-parse: records={c5b} (was {c5}) structure {'OK' if ok_struct else 'BROKEN'}")
    print(f"  flags now match 4.x for every id in scope: {'yes' if ok_flags else 'NO'}")
    print(f"  5.33-only records {shared+1}..{c5b} left untouched: {untouched}")

    if ok_struct and ok_flags and changed == len(todo):
        print("[patch_533_sobj] PATCHED. Restart the client. Set P1998_OBJ_FIX_533=off -- the server-side")
        print("                 workaround is now redundant. (--revert to undo.)")
    else:
        print(f"[patch_533_sobj] VERIFICATION FAILED -- restore from {BAK} immediately.")
        sys.exit(1)


if __name__ == "__main__":
    main()
