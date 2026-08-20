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


# ---- external trees (not in this repo; see docs/research/README.md) ----
# RTK-Server is cloned INTO the repo root (it is in .gitignore); a sibling checkout also works.
RTK = _env("P1998_RTK", ROOT, "RTK-Server")
if not RTK.exists() and (ROOT.parent / "RTK-Server").exists():
    RTK = ROOT.parent / "RTK-Server"
RTK_LUA = RTK / "rtklua" / "Accepted"
ARCHIVE = _env("P1998_ARCHIVE", ROOT.parent, "scraped_nexus_data")
# The two client installs. NextAeon is the 4.95 target; NextAeon5 is the 5.33 one. These default to
# the stock Nexon install location, which is right on most machines and wrong on any machine that
# installed elsewhere -- hence the overrides.
CLIENT = _env("P1998_CLIENT", r"C:\Program Files (x86)\Nexon\NextAeon")
CLIENT5 = _env("P1998_CLIENT5", r"C:\Program Files (x86)\Nexon\NextAeon5")
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
