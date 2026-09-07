#!/usr/bin/env python
"""Extract a NexusTK sound PAK to wav/mid files.

Two clients ship the same container under different names:
    4.95  NexusTK.snd  -> re/snd/      197 wavs
    5.33  Snd.dat      -> re/snd533/   188 wavs + 12 mids
Format: u32 count, then `count` x {u32 offset, char name[13]}; data runs from each entry's offset
to the next (last -> EOF). 5.33 ends with a nameless EOF terminator entry, 4.95 does not, so
unnamed entries are skipped rather than written out as a stray trailing file.

Lets us map RTK sound ids (Content.EffectSound) to the client's actual sounds by ear -- the id
spaces are shifted between clients, so the by-ear calibration is what settles it.

    python re/extract_snd.py                    # 4.95 NexusTK.snd -> re/snd/
    python re/extract_snd.py <pak> <outdir>     # anything else
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def extract(pak, out):
    f = open(pak, "rb").read()
    n = struct.unpack_from("<I", f, 0)[0]
    ents = []
    for i in range(n):
        off = struct.unpack_from("<I", f, 4 + i * 17)[0]
        name = f[8 + i * 17: 8 + i * 17 + 13].split(b"\0")[0].decode("latin1", "replace")
        ents.append((off, name))
    os.makedirs(out, exist_ok=True)
    written = 0
    for i, (off, name) in enumerate(ents):
        if not name:            # EOF terminator, not a file
            continue
        end = ents[i + 1][0] if i + 1 < len(ents) else len(f)
        with open(os.path.join(out, os.path.basename(name)), "wb") as w:
            w.write(f[off:end])
        written += 1
    return ents, written


def main():
    if len(sys.argv) > 1:
        pak, out = sys.argv[1], sys.argv[2]
    else:
        from _paths import CLIENT
        pak, out = str(CLIENT / "NexusTK.snd"), os.path.join(HERE, "snd")
    ents, written = extract(pak, out)
    print("extracted %d of %d entries to %s" % (written, len(ents), out))
    named = [e for e in ents if e[1]]
    print("first:", named[0][1], "last:", named[-1][1])


if __name__ == "__main__":
    main()
