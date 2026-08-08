"""Capture the LIVE 7.x full statblock (and self-profile) so equipped stats stop being
guessed from stale 4.95 session state.

Why: swings.csv needs the equipped stat vector (level/might/grace/will/dam/hit/ac) on every
row. The 0x08 sub-0x38 vitals packet decodes fine on live (hp/exp/gold), but the FULL
statblock (4.95 subs 0x58/0x59/0x78/0x79) only fires when stats change -- notably at LOGIN.
So: attach, then log out to character select and back in; every 0x08 sub and any 0x39
profile is dumped raw and run through the existing 4.95 parser to see which layout still
holds on 7.x.

Run it, then relog in the client. Ctrl-C or wait for the timeout.
"""
import sys, time, collections
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

SECS = int(sys.argv[1]) if len(sys.argv) > 1 else 180

seen = collections.Counter()
blocks = []


def pump(msg, data):
    if msg.get("type") != "send":
        return
    p = msg["payload"]
    op = p.get("op")
    if op not in (0x08, 0x39):
        return
    hexs = p.get("hex", "")
    d = bytes(int(x, 16) for x in hexs.split()) if hexs else b""
    if not d:
        return
    sub = d[1] if len(d) > 1 else None
    key = (op, sub)
    seen[key] += 1
    if op == 0x08 and sub in (0x58, 0x59, 0x78, 0x79):
        if seen[key] <= 2:
            blocks.append(("statblock", sub, d))
            print(f"\n*** FULL STATBLOCK op=0x08 sub=0x{sub:02x} len={len(d)}")
            print("   ", hexs)
            try:
                s = NA.parse_statblock(d)
                print("    4.95 parser ->", s)
            except Exception as e:
                print("    4.95 parser failed:", e)
    elif op == 0x39:
        if seen[key] <= 2:
            blocks.append(("profile", sub, d))
            print(f"\n*** SELF-PROFILE 0x39 len={len(d)}")
            print("   ", hexs[:400])
    elif seen[key] <= 1:
        print(f"  (0x{op:02x} sub 0x{sub:02x} len={len(d)})")


world = NB.World(NA.Agent())
s, sc = NB.attach(pump)
print(f"attached. Capturing 0x08 / 0x39 for {SECS}s.")
print(">>> LOG OUT to character select and LOG BACK IN to force a full statblock. <<<")
t0 = time.time()
try:
    while time.time() - t0 < SECS:
        time.sleep(1)
except KeyboardInterrupt:
    pass
print("\n--- opcode/sub histogram ---")
for (op, sub), c in seen.most_common():
    print(f"  op=0x{op:02x} sub=0x{sub:02x}  x{c}")
print(f"captured {len(blocks)} interesting block(s)")
s.detach()
