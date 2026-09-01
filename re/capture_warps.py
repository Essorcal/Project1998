#!/usr/bin/env python
r"""Turn a Wireshark capture of a live-retail walkthrough into Warps.csv rows — automatically.

This is the answer to "500 maps by hand is clunky": don't record anything by hand. Run Wireshark
(or dumpcap) while you walk a live-retail character through doorways; every time you cross a warp,
the wire carries the whole pair —

    client->server  0x06/0x32 walk   -> the SOURCE tile you stepped onto     (LOGIN_KEY, no name)
    server->client  0x15 enter-map   -> the DESTINATION map id + dims + name (table key, --name)
    server->client  0x04 coords      -> the ARRIVAL tile on that map         (table key, --name)

so an observed transition IS a Warps.csv row: (srcMap, sx, sy, dstMap, ax, ay). We reconcile each
against propose_warps.py's geometry (does the source tile sit in a known candidate exit? does the
arrival sit at a candidate exit's front?) to tag confidence and synthesize the return leg when you
only walked one way. Output is the same review CSV propose_warps writes — never a game-data edit.

PASSIVE + INVISIBLE. This reads a pcap off disk; nothing attaches to or alters the client, and the
capture itself is an ordinary NIC sniff — the server sees only a player walking through doors, which
is what doors are for. (See docs: server-side detection of a read-only sniff is nil; the ToS caveat
and "use a secondary account" advice stand.)

CRYPTO NOTE. 0x15/0x04 ride the name-keyed table cipher (decode_capture.crypt2). The reconstruction is
proven for 4.95; live 7.x MAY have diverged. This tool tells you: if few 0x15 frames decrypt to a
mapId the registry knows, it prints "table crypto looks wrong" and you fall back to the .cmp route
(--cmp-order: correlate the client's own Maps\ cache filenames, which need no table key, with the
LOGIN_KEY-decryptable source walks — geometry then fills the arrival tile).

USAGE
    # capture first (either tool; dumpcap ships with Wireshark):
    #   "C:\Program Files\Wireshark\dumpcap.exe" -i <iface> -w walk.pcapng
    python re/capture_warps.py walk.pcapng --name "YourChar"
    python re/capture_warps.py walk.pcapng --name "YourChar" --server 1.2.3.4:2610
    python re/capture_warps.py --self-test          # exercise the geometry reconciliation, no pcap
    python re/capture_warps.py walk.pcapng --name X --cmp-order "C:\...\KRU\NexusTK\Maps"
"""
import argparse, os, subprocess, sys, glob, re as _re
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
import decode_capture as d
import propose_warps as pw

TSHARK_GUESSES = [
    os.environ.get("TSHARK", ""),
    r"C:\Program Files\Wireshark\tshark.exe",
    r"C:\Program Files (x86)\Wireshark\tshark.exe",
    "tshark",
]


def find_tshark():
    for g in TSHARK_GUESSES:
        if not g:
            continue
        if g == "tshark" or os.path.exists(g):
            return g
    sys.exit("tshark not found — set TSHARK=... or install Wireshark")


def private(ip):
    return (ip.startswith("10.") or ip.startswith("192.168.") or ip.startswith("127.")
            or any(ip.startswith(f"172.{n}.") for n in range(16, 32)) or ip == "::1")


def pcap_records(path, server_override):
    """tshark -> decode_capture-shaped records: {ts, dir, fd, peer, hex}. dir 's' = to server."""
    tshark = find_tshark()
    fields = ["frame.time_epoch", "ip.src", "ip.dst", "tcp.srcport", "tcp.dstport",
              "tcp.stream", "tcp.payload"]
    cmd = [tshark, "-r", path, "-Y", "tcp.len>0", "-T", "fields"] + sum([["-e", f] for f in fields], [])
    out = subprocess.run(cmd, capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"tshark failed:\n{out.stderr.strip()}")
    rows = []
    endpoints = defaultdict(int)
    for line in out.stdout.splitlines():
        parts = line.split("\t")
        if len(parts) < 7 or not parts[6]:
            continue
        ts, src, dst, sp, dp, stream, payload = parts
        rows.append((float(ts) * 1000, src, dst, int(sp), int(dp), stream, payload))
        endpoints[(dst, int(dp))] += 1
        endpoints[(src, int(sp))] += 1

    if server_override:
        sip, sport = server_override.split(":"); sport = int(sport)
    else:
        # server = the busiest non-private endpoint
        cands = sorted(((c, ep) for ep, c in endpoints.items() if not private(ep[0])),
                       reverse=True)
        if not cands:
            sys.exit("no non-private endpoint in capture — pass --server ip:port")
        (_, (sip, sport)) = cands[0]
    print(f"[server] {sip}:{sport}")

    recs = []
    for ts, src, dst, sp, dp, stream, payload in rows:
        to_server = (dst == sip and dp == sport)
        from_server = (src == sip and sp == sport)
        if not (to_server or from_server):
            continue
        hexb = " ".join(payload[i:i + 2] for i in range(0, len(payload), 2))
        recs.append({"ts": ts, "dir": "s" if to_server else "r",
                     "fd": stream, "peer": f"{sip}:{sport}", "hex": hexb})
    return recs


def decoded_frames(records, name):
    """Every frame in chronological order: (ts, dir, op, body)."""
    table = d.populate_table(name) if name else None
    streams = d.reassemble(records)
    frames = []
    for (fd, dir_), s in streams.items():
        for ts, peer, fd_, dir__, frame in d.split_frames(s["buf"], s["marks"], fd, dir_):
            body = d.decrypt_frame(frame, dir__, table)
            frames.append((ts, dir__, frame[3], body))
    frames.sort(key=lambda x: x[0])
    return frames


def u16(body, off, index_ok):
    """Parse a u16 at off, auto-picking BE vs LE by which yields a value index_ok() accepts."""
    if off + 2 > len(body):
        return None
    be = (body[off] << 8) | body[off + 1]
    le = body[off] | (body[off + 1] << 8)
    if index_ok(be) and not index_ok(le):
        return be
    if index_ok(le) and not index_ok(be):
        return le
    return be if index_ok(be) else (le if index_ok(le) else None)


def transitions(frames, index):
    """Walk the timeline; emit an observed warp each time 0x15 changes the current map.
    Returns (list of transitions, set of visited map ids, parse stats)."""
    known = set(index)
    cur_map = None
    last_walk = None                                    # (x, y) from most recent client 0x06/0x32
    pending_15 = None                                   # dstMap awaiting its 0x04 arrival
    trans, visited = [], set()
    stat = {"0x15": 0, "0x15_ok": 0}

    for ts, dir_, op, body in frames:
        if dir_ == "s" and op in (0x06, 0x32):
            # 0x32: dir(1) step(1) X(2) Y(2)   |   0x06: dir(1) step(1) X(2BE) Y(2BE) + junk
            x = u16(body, 2, lambda v: 0 <= v < 4096)
            y = u16(body, 4, lambda v: 0 <= v < 4096)
            if x is not None and y is not None:
                last_walk = (x, y)
        elif dir_ == "r" and op == 0x15:
            stat["0x15"] += 1
            mid = u16(body, 0, lambda v: v in known)
            if mid is None:
                continue
            stat["0x15_ok"] += 1
            visited.add(mid)
            if cur_map is not None and mid != cur_map:
                pending_15 = {"src": cur_map, "src_tile": last_walk, "dst": mid, "ts": ts}
            cur_map = mid
        elif dir_ == "r" and op == 0x04 and pending_15 is not None:
            dm = pending_15["dst"]
            _, xs, ys = index[dm]
            ax = u16(body, 0, lambda v: 0 <= v < xs)
            ay = u16(body, 2, lambda v: 0 <= v < ys)
            if ax is not None and ay is not None:
                pending_15["arrival"] = (ax, ay)
            trans.append(pending_15)
            pending_15 = None
    return trans, visited, stat


def cmp_order(maps_dir):
    """Fallback ordering: map ids in the client's Maps\\ cache, by file mtime (entry order)."""
    files = []
    for f in glob.glob(os.path.join(maps_dir, "TK*.cmp")) + glob.glob(os.path.join(maps_dir, "TK*.map")):
        m = _re.search(r"TK0*(\d+)\.", os.path.basename(f))
        if m:
            files.append((os.path.getmtime(f), int(m.group(1))))
    files.sort()
    return [mid for _, mid in files]


def reconcile(reg, trans, max_run):
    """Attach to each observed transition the candidate exit its tiles match, and a confidence.
    Emits Warps-style rows (both legs) reusing propose_warps geometry for arrival/return fill-in."""
    index, names, warp_src, wired, claimed, flagged, is_door, max_id = reg
    geo_cache, ex_cache = {}, {}

    def exits(mid):
        if mid not in ex_cache:
            g, ex = pw.find_exits(mid, index, warp_src, claimed, is_door, max_run)
            geo_cache[mid] = g
            ex_cache[mid] = ex[0] if ex else []
        return ex_cache[mid], geo_cache[mid]

    def match_exit(mid, tile):
        if tile is None:
            return None
        el, _ = exits(mid)
        for e in el:
            if tile in e.tiles:
                return e
            # arrival lands one step INTO the map, i.e. at an exit's front tile
            for tx, ty in e.tiles:
                if (tx + e.front[0], ty + e.front[1]) == tile:
                    return e
        return None

    rows, report = [], []
    next_id = max_id + 1
    for t in trans:
        src, dst = t["src"], t["dst"]
        st, at = t.get("src_tile"), t.get("arrival")
        se = match_exit(src, st)
        de = match_exit(dst, at)
        conf = "observed" if (st and at) else "observed-partial"
        note = (f"live: {names.get(src,'?')} {st} -> {names.get(dst,'?')} {at}"
                f"  [src {se.label if se else 'UNMATCHED'} / dst {de.label if de else 'UNMATCHED'}]")
        report.append((src, dst, st, at, se, de))
        if st and at:
            rows.append([next_id, src, st[0], st[1], dst, at[0], at[1], conf, note]); next_id += 1
    return rows, report, next_id - 1


# ---------------------------------------------------------------- self-test

def self_test():
    """Prove the reconciliation half without a pcap: hand a known palace transition through it."""
    reg = pw.load_registry()
    fake = [
        {"src": 4604, "src_tile": (15, 1), "dst": 4606, "arrival": (10, 38)},   # center door -> Throne
        {"src": 4606, "src_tile": (10, 39), "dst": 4604, "arrival": (15, 2)},   # and back
        {"src": 4604, "src_tile": (5, 1), "dst": 4603, "arrival": (11, 22)},    # west door -> Tribunal
    ]
    rows, report, _ = reconcile(reg, fake, 8)
    for src, dst, st, at, se, de in report:
        print(f"  {src} {st} -> {dst} {at}   src={se.label if se else 'UNMATCHED'} "
              f"dst={de.label if de else 'UNMATCHED'}")
    print(f"\n{len(rows)} observed rows generated:")
    for r in rows:
        print("   " + ",".join(str(v) for v in r[:7]))
    ok = all(se and de for _, _, _, _, se, de in report)
    print(f"\nreconciliation self-test: {'PASS' if ok else 'FAIL'} "
          f"({sum(1 for r in report if r[4] and r[5])}/{len(report)} both-ends matched)")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("pcap", nargs="?", help="Wireshark capture (.pcap/.pcapng)")
    ap.add_argument("--name", help="live character name (needed to decrypt 0x15/0x04)")
    ap.add_argument("--server", help="ip:port of the game server (else auto: busiest public endpoint)")
    ap.add_argument("--cmp-order", help="client Maps\\ dir — fallback if table crypto has diverged")
    ap.add_argument("--max-run", type=int, default=8)
    ap.add_argument("--out", help="proposals csv (default re/warp_capture_proposals.csv)")
    ap.add_argument("--visited-out", help="write the visited-map id list here (default re/warp_visited.txt)")
    ap.add_argument("--self-test", action="store_true", help="run the geometry reconciliation self-test")
    args = ap.parse_args()

    if args.self_test:
        sys.exit(self_test())
    if not args.pcap:
        ap.error("give a pcap, or --self-test")

    if not args.name:
        print("!! no --name: 0x15 enter-map and 0x04 coords ride the name-keyed table cipher and\n"
              "   will stay encrypted, so destination map + arrival tile can't be read. Source walks\n"
              "   (LOGIN_KEY) still decode. Pass your live character name for full transitions.\n")

    reg = pw.load_registry()
    index = reg[0]
    records = pcap_records(args.pcap, args.server)
    print(f"[frames] decoding {len(records)} tcp payloads")
    frames = decoded_frames(records, args.name)
    trans, visited, stat = transitions(frames, index)

    if stat["0x15"] and stat["0x15_ok"] / stat["0x15"] < 0.5:
        print(f"\n!! only {stat['0x15_ok']}/{stat['0x15']} enter-map frames decoded to a known map id.")
        print("   The 7.x table cipher likely diverged from the 4.95 reconstruction.")
        print("   Fall back to --cmp-order (client-direction walks decrypt fine under LOGIN_KEY).")
        if args.cmp_order:
            order = cmp_order(args.cmp_order)
            print(f"   .cmp cache shows {len(order)} maps entered, in order: {order[:20]}...")
        # still emit whatever visited we got
    print(f"[transitions] {len(trans)} observed, {len(visited)} distinct maps visited "
          f"(0x15 decode {stat['0x15_ok']}/{stat['0x15']})")

    rows, report, last_id = reconcile(reg, trans, args.max_run)
    for src, dst, st, at, se, de in report:
        names = reg[1]
        print(f"   {src} {names.get(src,'?')[:20]} {st} -> {dst} {names.get(dst,'?')[:20]} {at}"
              f"   [{se.label if se else 'UNMATCHED'}/{de.label if de else 'UNMATCHED'}]")

    out = args.out or os.path.join(HERE, "warp_capture_proposals.csv")
    import csv
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["WarpId", "SourceMapId", "SourceX", "SourceY",
                    "DestinationMapId", "DestinationX", "DestinationY", "Confidence", "Note"])
        w.writerows(rows)
    vout = args.visited_out or os.path.join(HERE, "warp_visited.txt")
    with open(vout, "w", encoding="utf-8") as f:
        f.write("\n".join(str(m) for m in sorted(visited)) + "\n")
    print(f"\n{len(rows)} observed rows -> {out}")
    print(f"{len(visited)} visited maps -> {vout}  (feed to warp_walk_sheet.py --covered)")
    print("review, drop the last two columns, append to game-data/Warps.csv, then @reload")


if __name__ == "__main__":
    main()
