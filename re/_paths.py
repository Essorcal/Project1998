"""Where things live on disk, for every script in re/.

Scripts here read three quite different kinds of input and it is worth keeping them straight:

  IN THE REPO       game-data/*.csv, the Lua, maps/. Always present. Anchored to this file, so a script
                    works from any working directory and on any checkout.

  NEXT TO THE REPO  RTK-Server (the 7.x reference server, ~190 MB) and scraped_nexus_data (the
                    tswolf.com + boards.nexustk.com scrape). Neither is vendored -- they are somebody
                    else's data, they are large, and they change on their own schedule. Default to a
                    sibling of the repo root, override with an environment variable.
                    See docs/research/README.md for what they are and how to get them.

  THE CLIENT        A NexusTK install. Machine-specific by definition; P1998_CLIENT overrides.

Every one of these was a hardcoded C:\\Users\\<name>\\Desktop\\... literal before, and several still
pointed at a directory that had since been renamed -- the script had been broken for weeks and nobody
noticed because nobody re-ran it. Anchoring to __file__ means that failure mode is gone; the env vars
mean the ones that genuinely cannot be anchored fail with a name you can act on.
"""
import os
from pathlib import Path

RE = Path(__file__).resolve().parent          # re/
ROOT = RE.parent                              # repo root
DATA = ROOT / "game-data"                     # the content registry


def _env(var: str, *default: object) -> Path:
    v = os.environ.get(var)
    return Path(v) if v else Path(*[str(d) for d in default])


def _pick(var: str, *candidates: object) -> Path:
    """The env var if set, else the first candidate that exists, else the first candidate.

    A client install is not always where its installer put it. The working 5.33 tree here is a
    patched *copy* beside the shipped one, because patching under Program Files needs admin and one
    bad write to the only install costs a reinstall. Falling back to candidates[0] when none exist
    leaves require() free to name the canonical location in its error.
    """
    v = os.environ.get(var)
    if v:
        return Path(v)
    for c in candidates:
        p = Path(str(c))
        if p.exists():
            return p
    return Path(str(candidates[0]))


# ---- external trees (not in this repo; see docs/research/README.md) ----
# RTK-Server is cloned INTO the repo root (it is in .gitignore); a sibling checkout also works.
RTK = _env("P1998_RTK", ROOT, "RTK-Server")
if not RTK.exists() and (ROOT.parent / "RTK-Server").exists():
    RTK = ROOT.parent / "RTK-Server"
RTK_LUA = RTK / "rtklua" / "Accepted"
ARCHIVE = _env("P1998_ARCHIVE", ROOT.parent, "scraped_nexus_data")
# The two client installs. NextAeon is the 4.95 target; the 5.33 one is whichever NextAeon533 /
# NextAeon5 tree exists. These default to the stock Nexon install location, which is right on most
# machines and wrong on any machine that installed elsewhere -- hence the overrides.
#
# For 5.33, what every script here wants is the *patched* tree: patch_533_connaddr.py points it at
# 127.0.0.1:2001, and patch_533_sobj_flags.py restores the 4.x object-collision flags. Attaching a
# probe to a stock NextAeon5 instead means it never reaches the local server, and walls stand where
# the 4.x maps have none -- neither of which announces itself; the probe log just looks quiet. So
# prefer a side-by-side NextAeon533, which is where that patched tree normally lives.
CLIENT = _pick("P1998_CLIENT", r"C:\Program Files (x86)\Nexon\NextAeon")
CLIENT5 = _pick(
    "P1998_CLIENT5",
    Path.home() / "Desktop" / "NextAeon533",
    r"C:\Program Files (x86)\Nexon\NextAeon533",
    r"C:\Program Files (x86)\Nexon\NextAeon5",
)
# The other two the probes attach to: the LIVE retail 7.x client, and 4.83 (the oldest we have).
# Both ship under KRU rather than Nexon.
CLIENT_LIVE = _env("P1998_CLIENT_LIVE", r"C:\Program Files (x86)\KRU\NexusTK")
CLIENT483 = _env("P1998_CLIENT483", r"C:\Program Files (x86)\KRU\NexusTK483")


def require(path: Path, what: str, env: str) -> Path:
    """Fail with the fix in the message, rather than with a bare FileNotFoundError 40 lines later."""
    if not path.exists():
        raise SystemExit(
            f"{what} not found at {path}\n"
            f"  Set {env} to point at it. See docs/research/README.md for what it is and where to get it."
        )
    return path
