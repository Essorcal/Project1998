#!/usr/bin/env python
"""Extract Mus000.dat (the 5.33 client's MUSIC pak) to re/mus/ as NNN.MP3, named the way the
4.95 client wants them.

Why this works at all: the 4.95 client already HAS an MP3 engine. It statically links the XAudio
MPEG-1/2 layer I/II/III decoder (the XA_MSG_* / "MPEG %d, %s, %s, %d kbps, %d hz" strings are all
in NexusTK.exe), and the 0x19 music opcode's type 1 drives it:

    0x19 handler 0x450ad0 (type==1) -> TLV tail 0x450c48 -> builder 0x44e6c0 -> ctor 0x463950
      -> play wrapper 0x463ab0 -> 0x4798c0 -> 0x479c29 (type==1)
      -> sprintf(buf, L"%03d.MP3", trackId)      <- the WIDE string at 0x4f3cc0
      -> 0x478e20 -> XAudio cmd 8 (INPUT_OPEN) + cmd 1 (PLAY)

So track 103 = a loose file "103.MP3" in the client directory. Not an archive, not a subfolder --
XAudio gets the bare filename and opens it relative to the process CWD. (The %03d is a MINIMUM
width, so ids above 999 would simply print more digits; nothing truncates.)

Two asymmetries between the two channels, both confirmed by RE:
  * type 2 (MIDI) is HARD-CAPPED at ids 1..12 -- `cmp si, 0xd / jge bail` at 0x4588b4. You cannot
    add MIDI tracks without patching the exe. That is why the stock game has exactly 12 songs.
  * type 1 (MP3) has NO id cap -- the only guard is `bgm > 0`. This is the expandable channel.

The 5.33 archive uses the SAME container as NexusTK.snd / Snd.dat (see extract_snd.py):
u32 count, then `count` x {u32 offset, char name[13]}, data from each offset to the next. Its
entries are %08d-style ("00000103.mp3"), because 5.33 formats names with "%08d.MP3" instead;
every id in it happens to be <= 999, so they renumber onto 4.95's %03d with zero collisions
(asserted below -- if a future archive breaks that, this script fails loudly rather than
silently overwriting a track).

5.33 also ships .lst/.lsr playlists in here. Those are a 5.33-only feature (its exe has
"%08d.LST"/"%08d.LSR"; 4.95's has neither), so they are dumped to re/mus/playlists/ purely as a
reference for how Nexon grouped the songs -- the 4.95 client can only be told one track at a time.

Usage:
    python re/extract_mus.py                    # -> re/mus/NNN.MP3 + re/mus/playlists/
    python re/extract_mus.py --install          # ... and copy the MP3s into the 4.95 client dir
    python re/extract_mus.py --install --only 103
"""
import argparse, os, shutil, struct
from _paths import CLIENT, CLIENT5

MUS = str(CLIENT5 / "Mus000.dat")
CLIENT = CLIENT
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mus")

# MPEG audio header tables, for the format report (bitrate index -> kbps, by MPEG version).
BR_V1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0]   # MPEG1 layer III
BR_V2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0]       # MPEG2/2.5 layer III
SR = {3: {0: 44100, 1: 48000, 2: 32000}, 2: {0: 22050, 1: 24000, 2: 16000}, 0: {0: 11025, 1: 12000, 2: 8000}}
VER = {3: "MPEG1", 2: "MPEG2", 0: "MPEG2.5"}
LAYER = {1: "layerIII", 2: "layerII", 3: "layerI"}
CHAN = {0: "stereo", 1: "joint", 2: "dual", 3: "mono"}


def entries(blob):
    """The shared 4.95/5.33 pak index: u32 count, then count x {u32 offset, char name[13]}."""
    n = struct.unpack_from("<I", blob, 0)[0]
    out = []
    for i in range(n):
        off = struct.unpack_from("<I", blob, 4 + i * 17)[0]
        name = blob[8 + i * 17: 8 + i * 17 + 13].split(b"\0")[0].decode("latin1", "replace")
        out.append((off, name))
    # Data for entry i runs to entry i+1's offset; the last runs to EOF. The final entry is an
    # 8-byte unnamed trailer (uninitialised tail, same as NexusTK.snd) -- it carries no payload.
    return [(name, off, (out[i + 1][0] if i + 1 < n else len(blob)))
            for i, (off, name) in enumerate(out)]


def mpeg_info(data):
    """Describe the first MPEG frame, skipping any ID3v2 tag. Returns '' if no sync is found."""
    p = 0
    if data[:3] == b"ID3":
        p = 10 + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9])
    while p < len(data) - 4 and not (data[p] == 0xFF and (data[p + 1] & 0xE0) == 0xE0):
        p += 1
    if p >= len(data) - 4:
        return ""
    h = data[p:p + 4]
    ver, layer = (h[1] >> 3) & 3, (h[1] >> 1) & 3
    if ver not in SR or layer not in LAYER:
        return ""
    br = (BR_V1 if ver == 3 else BR_V2)[(h[2] >> 4) & 0xF]
    return f"{VER[ver]} {LAYER[layer]} {br}kbps {SR[ver][(h[2] >> 2) & 3]}Hz {CHAN[(h[3] >> 6) & 3]}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mus", default=MUS, help="path to the 5.33 Mus000.dat")
    ap.add_argument("--install", action="store_true", help="also copy the MP3s into the 4.95 client dir")
    ap.add_argument("--client", default=CLIENT, help="4.95 client dir for --install")
    ap.add_argument("--only", type=int, action="append", help="restrict to these track ids (repeatable)")
    args = ap.parse_args()

    blob = open(args.mus, "rb").read()
    ents = entries(blob)

    mp3s, lists = [], []
    for name, off, end in ents:
        (mp3s if name.lower().endswith(".mp3") else lists).append((name, blob[off:end]))
    lists = [(n, d) for n, d in lists if n.lower().endswith((".lst", ".lsr"))]

    # 5.33 names are %08d; 4.95 asks for %03d. Renumber, and refuse to run if that ever collides.
    renamed, seen = [], {}
    for name, data in mp3s:
        tid = int(os.path.splitext(name)[0])
        out = f"{tid:03d}.MP3"
        if out in seen:
            raise SystemExit(f"id collision: {name} and {seen[out]} both map to {out} -- "
                             f"4.95's %03d cannot represent this archive")
        seen[out] = name
        renamed.append((tid, out, data))
    renamed.sort()

    keep = set(args.only or [])
    os.makedirs(OUT, exist_ok=True)
    written = 0
    for tid, out, data in renamed:
        if keep and tid not in keep:
            continue
        with open(os.path.join(OUT, out), "wb") as w:
            w.write(data)
        written += 1
        print(f"  {out}  {len(data):>9,} B  {mpeg_info(data)}")

    if lists:
        pdir = os.path.join(OUT, "playlists")
        os.makedirs(pdir, exist_ok=True)
        for name, data in lists:
            with open(os.path.join(pdir, name), "wb") as w:
                w.write(data)
        print(f"\n{len(lists)} 5.33-only playlists (.lst/.lsr) -> {pdir}"
              f"   [reference only: 4.95 has no playlist support]")

    print(f"\nextracted {written} of {len(renamed)} tracks to {OUT}")
    print(f"ids: {', '.join(str(t) for t, _, _ in renamed)}")

    if args.install:
        n = 0
        for tid, out, _ in renamed:
            if keep and tid not in keep:
                continue
            try:
                shutil.copy2(os.path.join(OUT, out), os.path.join(args.client, out))
            except PermissionError:
                # The stock install lives under Program Files, which needs an elevated shell to write to.
                raise SystemExit(
                    f"\npermission denied writing {out} to {args.client}\n"
                    f"  -> re-run this from an ELEVATED shell, or copy re/mus/*.MP3 there by hand.\n"
                    f"     (the files are already extracted; --install only copies them.)")
            n += 1
        print(f"\ninstalled {n} tracks into {args.client}")
        print("the client reads these as loose files from its own directory -- try '@music 103 mp3'")


if __name__ == "__main__":
    main()
