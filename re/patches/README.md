# NexusTK client binary patches

On-disk patchers for the three client builds, one script per client. All share the engine in
[`patchlib.py`](patchlib.py), which only writes if the bytes at each target address match the
recorded original (so a wrong/stale address is refused, never silently corrupted), backs the exe
up once before the first write, and supports `--check` / `--revert`.

| Client | Target file | Script | State |
|--------|-------------|--------|-------|
| 4.83 | `KRU\NexusTK483\NexusTK.dat` (`Address` entry) | [`patch_483_localhost.py`](patch_483_localhost.py) | working — redirects the client to `127.0.0.1:2000` |
| 4.95 | `Nexon\NextAeon\NexusTK_local.exe` | [`patch_495_no_nametag.py`](patch_495_no_nametag.py) | working — removes the floating nameplate marker |
| 5.33 | `Nexon\NextAeon5\NexusTK.exe` (+ its dat host list) | [`patch_533.py`](patch_533.py) | scaffold — no patches defined |

## Two patch mechanisms

- **Exe code/string patches** (`patchlib.py` engine): overwrite bytes at a virtual address in the
  client exe. Used by `patch_495_no_nametag.py`. Refuses unless the recorded original bytes match.
- **Dat host-list patch** (`patch_483_localhost.py`): the *server address* isn't hardcoded in the
  exe — the exe strings are stale defaults. The live address is a plaintext PAK entry named
  `Address` inside `NexusTK.dat`: a `<ip>.<port>;` list (last dotted segment = port). Redirecting
  the client = rewriting that entry in place to `127.0.0.1.2000;`. This is how 4.83 (and, per prior
  RE, 5.33 via its own dat host list / "Connaddr") get pointed at a local server.

## Usage

```bash
python re/patches/patch_495_no_nametag.py --check     # report state, no changes
python re/patches/patch_495_no_nametag.py             # apply
python re/patches/patch_495_no_nametag.py --revert    # restore from backup
```

Backups live in `re/patches/backups/` (except 4.95, which keeps its pre-existing pristine backup
at `re/NexusTK_local.exe.prenametagpatch.bak` so `--revert` still finds it).

## Adding a patch to a build

Addresses are **build-specific** — the 4.95 no-nametag VA (`0x463380`) does not transfer to 4.83
or 5.33. For each build, reverse it on its own binary:

1. Find the target VA with `re/disx.py` (e.g. the `0x33` appearance handler and the
   `renderKind==1` marker/decoration ctor it calls — this is how the 4.95 patch was found).
2. Read the exact bytes at that VA → they become the patch's `original`; choose the replacement
   `patched` (same length).
3. Add a `Patch(va, original, patched, desc)` to that client's `PATCHES` list.
4. Run with `--check` first, then apply.

The 4.95 nametag marker was an era-specific regression (it isn't present in the original 4.x
client), so a 4.83 "no-nametag" patch is likely unnecessary — confirm the behavior in that build
before porting anything.
