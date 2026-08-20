#!/usr/bin/env python
"""Extract NexusTK.snd (the 4.95 client's sound PAK) to re/snd/ as NNN.wav.
Format: u32 count, then `count` × {u32 offset, char name[13]}; wav data runs from each entry's
offset to the next (last -> EOF). Names are already 001.wav..197.wav. Lets us map RTK sound ids
(Content.EffectSound) to the client's actual sounds by ear (the id spaces may be shifted)."""
import struct, os
from _paths import CLIENT
SND = str(CLIENT / "NexusTK.snd")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "snd")
def main():
    f = open(SND, "rb").read()
    n = struct.unpack_from("<I", f, 0)[0]
    ents = []
    for i in range(n):
        off = struct.unpack_from("<I", f, 4 + i*17)[0]
        name = f[8 + i*17: 8 + i*17 + 13].split(b"\0")[0].decode("latin1", "replace")
        ents.append((off, name))
    os.makedirs(OUT, exist_ok=True)
    for i, (off, name) in enumerate(ents):
        end = ents[i+1][0] if i+1 < len(ents) else len(f)
        data = f[off:end]
        with open(os.path.join(OUT, name or f"{i+1:03d}.wav"), "wb") as w:
            w.write(data)
    print(f"extracted {len(ents)} sounds to {OUT}")
    print("first:", ents[0][1], "last:", ents[-1][1])
if __name__ == "__main__":
    main()
