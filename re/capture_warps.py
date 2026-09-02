#!/usr/bin/env python
r"""Recover warp connectivity from a passive Wireshark capture of a live-retail walkthrough.

WHY THIS SHAPE. The live 7.x client re-keys its server->client "table" cipher PER PACKET (the
9-byte key is generate_key2 over md5(charName), but with a per-inc (k1,k2) schedule that lives in
client code -- see docs). So we cannot blanket-decrypt the stream offline. BUT the enter-map packet
(0x15) is self-identifying: its 9-byte header is mapId(u16) + w(u16) + h(u16) + 05 00 + nameLen, all
of which we know for any candidate map from game-data/map_index.csv. Brute the candidate map: the
right one makes the header decrypt to an all-hex key AND makes the trailing bytes decode to that
map's NAME. That recovers the destination map id for every doorway you walk through -- which is the
connectivity the disconnected-map problem actually needs -- with no key schedule required.

WHAT YOU GET. The chronological sequence of maps entered -> the warp GRAPH (srcMap <-> dstMap edges).
Reconciled against propose_warps.py geometry:
  * both ends single-exit  -> a finished Warps.csv row pair (source+arrival tiles from geometry).
  * a hub (many exits)     -> the SET of rooms it connects to, as a propose_warps --pair worklist:
                              you supply the door<->room permutation (walk one door at a time, or
                              read it off the client), geometry supplies the coordinates.
Exact source/arrival TILES are NOT read from the wire (that needs the per-inc key schedule, unbroken);
geometry provides them. Everything here is passive: an offline read of a pcap, nothing touches the
client, the server sees only a player walking through doors.

CRYPTO NOTE. Framing (AA|len|op|inc) and the static-key lane are unchanged in 7.x; only the table-key
SCHEDULE moved. If a future client changes the 0x15 layout, self-identification stops matching and the
tool reports 0 identified enter-maps -- capture a short known walk and check the layout before a sweep.

USAGE
    python re/capture_warps.py walk.pcapng                    # one capture
    python re/capture_warps.py a.pcapng b.pcapng --name Seolta
    python re/capture_warps.py walk.pcapng --server 1.2.3.4:2001   # skip auto-detect
    #   dumpcap ships with Wireshark:  dumpcap -i <iface> -f "tcp port 2000 or tcp port 2001" -w walk.pcapng
"""
import argparse, csv, os, subprocess, sys
from collections import defaultdict, Counter

HERE = os.path.dirname(os.path.abspath(__file__))
import decode_capture as d
import propose_warps as pw

HEX = set(b"0123456789abcdef")
TSHARK_GUESSES = [os.environ.get("TSHARK", ""), r"C:\Program Files\Wireshark\tshark.exe",
                  r"C:\Program Files (x86)\Wireshark\tshark.exe", "tshark"]


# ---------------------------------------------------------------- pcap -> frames

def find_tshark():
    for g in TSHARK_GUESSES:
        if g and (g == "tshark" or os.path.exists(g)):
            return g
    sys.exit("tshark not found -- set TSHARK=... or install Wireshark")


def _private(ip):
    return (ip.startswith(("10.", "192.168.", "127.")) or ip == "::1"
            or any(ip.startswith(f"172.{n}.") for n in range(16, 32)))


def pcap_records(path, server_override):
    """tshark -> decode_capture records ({ts,dir,fd,peer,hex}); server auto-detected by AA framing."""
    if not os.path.exists(path):
        sys.exit(f"no such capture: {path}")
    fields = ["frame.time_epoch", "ip.src", "ip.dst", "tcp.srcport", "tcp.dstport", "tcp.stream",
              "tcp.payload"]
    cmd = [find_tshark(), "-r", path, "-Y", "tcp.len>0", "-T", "fields"] + \
          sum([["-e", f] for f in fields], [])
    out = subprocess.run(cmd, capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"tshark failed on {path}:\n{out.stderr.strip()}\n"
                 f"(a capture killed mid-write is truncated -- repair with: editcap in.pcapng out.pcapng)")

    rows, aa_hits = [], Counter()
    for line in out.stdout.splitlines():
        p = line.split("\t")
        if len(p) < 7 or not p[6]:
            continue
        ts, src, dst, sp, dp, stream, payload = p
        rows.append((float(ts) * 1000, src, dst, int(sp), int(dp), stream, payload))
        if payload[:2].lower() == "aa":                 # NexusTK frame magic
            aa_hits[(src, int(sp))] += 1                 # count the SENDER endpoint

    if server_override:
        sip, sport = server_override.split(":"); sport = int(sport)
    else:
        pub = [(c, ep) for ep, c in aa_hits.items() if not _private(ep[0])]
        if not pub:
            sys.exit(f"no NexusTK (AA-framed) traffic from a public host in {path} -- pass --server ip:port")
        sip, sport = max(pub)[1]

    recs = []
    for ts, src, dst, sp, dp, stream, payload in rows:
        to_srv = (dst == sip and dp == sport)
        fr_srv = (src == sip and sp == sport)
        if not (to_srv or fr_srv):
            continue
        recs.append({"ts": ts, "dir": "s" if to_srv else "r", "fd": f"{path}:{stream}",
                     "peer": f"{sip}:{sport}", "hex": " ".join(payload[i:i+2] for i in range(0, len(payload), 2))})
    return recs, f"{sip}:{sport}"


def server_frames(records):
    """Chronological server->client frames as (ts, frame_bytes)."""
    out = []
    for (fd, dir_), s in d.reassemble(records).items():
        if dir_ != "r":
            continue
        for ts, peer, fd_, dir__, frame in d.split_frames(s["buf"], s["marks"], fd, dir_):
            out.append((ts, frame))
    out.sort(key=lambda x: x[0])
    return out


# ---------------------------------------------------------------- enter-map self-id

def _norm(s):
    return "".join(c for c in s.lower() if c.isalnum())


def enter_key(frame, mapid, xs, ys, endian, namelen):
    """Recover the 9-byte per-frame key from the enter-map header, ASSUMING this map. None if not all-hex.
    Header (9 bytes, all in cipher group 0 so the crypt term is just `inc`): mapId,w,h,05,00,nameLen."""
    inc = frame[4]; body = frame[5:]
    if len(body) < 9:
        return None
    try:
        hdr = mapid.to_bytes(2, endian) + xs.to_bytes(2, "big") + ys.to_bytes(2, "big") \
            + bytes([5, 0, namelen & 0xFF])
    except OverflowError:
        return None
    key = bytearray(9)
    for i in range(9):
        kv = body[i] ^ hdr[i] ^ inc
        if kv not in HEX:
            return None
        key[i] = kv
    return bytes(key)


def decode_name(frame, key):
    inc = frame[4]; body = frame[5:]
    out = bytearray()
    for i in range(9, len(body) - 2):
        grp = (i // 9) & 0xFF
        out.append(body[i] ^ key[i % 9] ^ (grp ^ (inc if grp != inc else 0)))
    return "".join(chr(c) if 32 <= c < 127 else "." for c in out)


def identify(frame, index):
    """Return (mapid, name, decoded_name) for a 0x15 frame, or None. Verified by name match."""
    best = None
    for mid, (nm, xs, ys) in index.items():
        n = _norm(nm)
        if len(n) < 3:
            continue
        for endian in ("big", "little"):
            key = enter_key(frame, mid, xs, ys, endian, len(nm))
            if not key:
                continue
            dn = decode_name(frame, key).rstrip(".")
            # case/punctuation-insensitive prefix match (allow one trailing char off from group-8 slip)
            if _norm(dn).startswith(n[:max(3, len(n) - 1)]):
                score = len(n)                          # prefer the longest confirmed name
                if best is None or score > best[0]:
                    best = (score, mid, nm, dn)
    return best[1:] if best else None


# ---------------------------------------------------------------- sequence -> edges

def map_sequence(frames, index):
    """[(ts, inc, mapid|None, name|None)] for every enter-map, in capture order."""
    seq = []
    for ts, fr in frames:
        if fr[3] != 0x15:
            continue
        r = identify(fr, index)
        seq.append((ts, fr[4], r[0] if r else None, r[1] if r else None))
    return seq


def edges_from_sequences(seqs):
    """Directed edge counts over consecutive identified maps, pooled across captures.
    Returns (edge_count: {(src,dst):n}, visited:set, unident:int)."""
    edge = Counter(); visited = set(); unident = 0
    for seq in seqs:
        prev = None
        for ts, inc, mid, nm in seq:
            if mid is None:
                unident += 1; prev = None; continue
            visited.add(mid)
            if prev is not None and prev != mid:
                edge[(prev, mid)] += 1
            prev = mid
    return edge, visited, unident


# ---------------------------------------------------------------- existing warps

def existing_edges():
    """Set of (srcMap,dstMap) already in Warps.csv, so we can flag NEW connections."""
    _, rows = pw.rows_of("Warps.csv")
    out = set()
    for r in rows:
        out.add((int(r[1]), int(r[4])))
    return out


# ---------------------------------------------------------------- reconcile w/ geometry

def reconcile(edge_count, reg, max_run):
    """Turn observed edges into (a) finished rows for unambiguous pairs and (b) hub worklists."""
    index, names, warp_src, wired, claimed, flagged, is_door, max_id = reg
    known = existing_edges()
    geo_cache, ex_cache = {}, {}

    def exits(mid):
        if mid not in ex_cache:
            g, ex = pw.find_exits(mid, index, warp_src, claimed, is_door, max_run)
            geo_cache[mid], ex_cache[mid] = g, (ex[0] if ex else [])
        return ex_cache[mid], geo_cache[mid]

    undirected = {}                                     # {frozenset({A,B}): total count}
    for (a, b), n in edge_count.items():
        undirected[frozenset((a, b))] = undirected.get(frozenset((a, b)), 0) + n

    rows, next_id = [], max_id + 1
    auto, hubs, already = [], [], []
    for pair, n in sorted(undirected.items(), key=lambda kv: -kv[1]):
        a, b = sorted(pair)
        a_new = (a, b) not in known and (b, a) not in known
        exA, geoA = exits(a); exB, geoB = exits(b)
        label = f"{a} {names.get(a,'?')} <-> {b} {names.get(b,'?')}  (seen x{n})"
        if not a_new:
            already.append(label); continue
        if geoA is None or geoB is None:
            hubs.append((label, a, exA, b, exB, "no geometry for one side")); continue
        if len(exA) == 1 and len(exB) == 1:
            r, next_id = pw.make_rows(a, exA[0], geoA, b, exB[0], geoB, next_id, "observed",
                                      f"live edge {names.get(a,'?')} <-> {names.get(b,'?')} (x{n})")
            rows += r
            auto.append((label, exA[0], exB[0]))
        else:
            hubs.append((label, a, exA, b, exB, "hub: assign door(s)"))
    return rows, auto, hubs, already, next_id - 1


# ---------------------------------------------------------------- CLI

def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("pcaps", nargs="+", help="Wireshark capture(s) of the walkthrough")
    ap.add_argument("--name", help="live character name (only used to sanity-check crypto; self-id needs no key)")
    ap.add_argument("--server", help="ip:port of the game server (else auto: the AA-framed public endpoint)")
    ap.add_argument("--max-run", type=int, default=8)
    ap.add_argument("--out", help="proposals csv (default re/warp_capture_proposals.csv)")
    ap.add_argument("--visited-out", help="visited-map id list (default re/warp_visited.txt)")
    args = ap.parse_args()

    reg = pw.load_registry()
    index = reg[0]

    seqs = []
    for pc in args.pcaps:
        recs, srv = pcap_records(pc, args.server)
        frames = server_frames(recs)
        seq = map_sequence(frames, index)
        ident = sum(1 for _, _, m, _ in seq if m is not None)
        print(f"[{os.path.basename(pc)}] server {srv}: {len(seq)} enter-maps, {ident} identified")
        seqs.append(seq)

    edge_count, visited, unident = edges_from_sequences(seqs)
    print(f"\n{len(visited)} distinct maps visited, {len(edge_count)} directed edges, "
          f"{unident} enter-maps unidentified (live-only maps not in our registry)")

    rows, auto, hubs, already, last_id = reconcile(edge_count, reg, args.max_run)
    names = reg[1]

    if auto:
        print(f"\n=== {len(auto)} unambiguous connections -> {len(rows)} finished rows ===")
        for label, ea, eb in auto:
            print(f"  {label}   [{ea.label} <-> {eb.label}]")
    if hubs:
        print(f"\n=== {len(hubs)} hub / multi-exit connections need a door assignment ===")
        # group by hub map for a tidy propose_warps worklist
        by_hub = defaultdict(list)
        for label, a, exA, b, exB, why in hubs:
            print(f"  {label}  ({why})")
            hubmap = a if len(exA) >= len(exB) else b
            room = b if hubmap == a else a
            by_hub[hubmap].append(room)
        for hubmap, rooms in by_hub.items():
            exH = pw.find_exits(hubmap, index, reg[2], reg[4], reg[6], args.max_run)[1]
            exH = exH[0] if exH else []
            print(f"\n  hub {hubmap} {names.get(hubmap,'?')} connects to {len(set(rooms))} room(s): "
                  + ", ".join(f"{r} {names.get(r,'?')}" for r in sorted(set(rooms))))
            print(f"    doors available: {[e.label for e in exH]} -- assign with propose_warps:")
            print(f"    python re/propose_warps.py --cluster {hubmap} "
                  + " ".join(f"--pair {hubmap}.<door>={r}.e0" for r in sorted(set(rooms))))
    if already:
        print(f"\n=== {len(already)} observed connections already in Warps.csv (confirmed) ===")
        for label in already[:20]:
            print(f"  {label}")
        if len(already) > 20:
            print(f"  ... (+{len(already)-20} more)")

    out = args.out or os.path.join(HERE, "warp_capture_proposals.csv")
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["WarpId", "SourceMapId", "SourceX", "SourceY", "DestinationMapId",
                    "DestinationX", "DestinationY", "Confidence", "Note"])
        w.writerows(rows)
    vout = args.visited_out or os.path.join(HERE, "warp_visited.txt")
    with open(vout, "w", encoding="utf-8") as f:
        f.write("\n".join(str(m) for m in sorted(visited)) + "\n")

    print(f"\n{len(rows)} finished rows -> {out}")
    print(f"{len(visited)} visited maps -> {vout}  (feed to warp_walk_sheet.py --covered)")
    print("review rows, drop the last two columns, append to game-data/Warps.csv, then @reload")


if __name__ == "__main__":
    main()
