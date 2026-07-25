"""Download every Nexus Atlas monster GIF (best pre-6.5 snapshot) into atlas_img/.
Uses curl -L (follows Wayback's redirect to the dated snapshot), low concurrency,
retries with backoff to survive archive.org throttling."""
import json, os, subprocess, time, concurrent.futures as cf

os.makedirs("atlas_img", exist_ok=True)
rows = json.load(open("cdx_monsternpc.json"))[1:]
jobs = []
seen = set()
for r in rows:
    ts, orig = r[1], r[2]
    name = orig.split("/")[-1]
    if not name.lower().endswith(".gif") or name in seen:
        continue
    seen.add(name)
    jobs.append((name, ts, orig))

def grab(job):
    name, ts, orig = job
    out = os.path.join("atlas_img", name)
    if os.path.exists(out) and os.path.getsize(out) > 20:
        return (name, "skip")
    url = f"https://web.archive.org/web/{ts}im_/{orig}"
    for attempt in range(4):
        try:
            subprocess.run(
                ["curl", "-sL", "--max-time", "40", "-A", "Mozilla/5.0", url, "-o", out],
                check=False,
            )
            if os.path.exists(out) and os.path.getsize(out) > 20:
                with open(out, "rb") as f:
                    if f.read(3) in (b"GIF", b"\x89PN"):
                        return (name, f"ok {os.path.getsize(out)}")
        except Exception:
            pass
        time.sleep(1.5 * (attempt + 1))
    return (name, "FAIL")

ok = bad = 0
fails = []
with cf.ThreadPoolExecutor(max_workers=4) as ex:
    for name, res in ex.map(grab, jobs):
        if res.startswith(("ok", "skip")):
            ok += 1
        else:
            bad += 1
            fails.append(name)
print(f"done: {ok} ok, {bad} bad, {len(jobs)} total")
if fails:
    print("fails:", fails[:40])
