#!/usr/bin/env python
"""Real passability, read from the map instead of discovered by walking into things.

WHY THIS EXISTS
    The bot used to learn geometry by bumping: a step that did not move us marked that tile
    a wall. Measured against the real map afterwards, only 12 of 30 tiles it had "learned"
    in Mythic Waters 1 were actually walls. The other 18 were MOBS standing in the way --
    including (5,20), the tile the character was standing on at the time. So it navigated a
    map that was ~60% fiction, re-derived it every run, and could not answer the one
    question that matters in a fight: how many things can reach me if I stand here.

THE SOURCE
    C:\\Users\\<user>\\Documents\\NexusTK\\Maps\\TK######.cmp -- the CLIENT's own cache,
    written as rooms are entered. The client enforces collision LOCALLY, so this is the file
    that decides where the character can actually walk; the server's copy can and does
    differ. Verified: TK000201 puts a wall at (5,19) and solid objects at (3,20),(4,20),
    leaving (5,20) with exactly ONE open side -- which is what the player reports, and what
    the RTK source map (walkable at (5,19), two open sides) gets wrong.

    Validation, both maps, against 94 tiles the character physically stood on: 93/94.

FORMAT
    "CMAP" + u32LE (height<<16 | width) + zlib( W*H*6 : per cell 3 u16LE
                                                [ground+1][passable][object+1] )
    passable != 0 -> solid ground. Ground/object are stored +1, so 0 means "none" and an
    object id must have 1 subtracted before it indexes SObj.tbl.

    Object walls sit on WALKABLE ground -- a hut's side is passable terrain with a blocking
    sprite on it -- so the object layer has to be consulted too, exactly as
    Server/MapData.cs:184 does (`Solid(x,y) || ObjectFlags.Blocks(obj, dir)`).
"""
import os, csv, glob, struct, zlib, collections

D = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(D)

CLIENT_MAPS = os.path.join(os.path.expanduser("~"), "Documents", "NexusTK", "Maps")
RTK_MAPS = os.path.join(REPO, "RTK-Server", "rtkmaps", "Accepted")
SOBJ_PATHS = [os.path.join(REPO, "RTK-Server", "rtk", "SObj.tbl"),
              os.path.join(REPO, "game-data", "SObj.tbl")]
MAP_INDEX = os.path.join(REPO, "game-data", "map_index.csv")
WARPS = os.path.join(REPO, "game-data", "Warps.csv")

SOLID_OBJ = 0x0F           # blocks all four sides (RTK map.h: UP=1 DOWN=2 RIGHT=4 LEFT=8)
UP, DOWN, RIGHT, LEFT = 1, 2, 4, 8
DIRS = {"up": (0, -1), "right": (1, 0), "down": (0, 1), "left": (-1, 0)}
# which flag bit blocks a move INTO a cell heading this way (RTK clif_object_canmove)
DIR_BIT = {"up": UP, "right": RIGHT, "down": DOWN, "left": LEFT}

_sobj = None
_names = None
_cache = {}


def sobj_flags():
    """objId -> directional flag byte, from SObj.tbl.

    The walk runs one object out of phase with the records: each object's flag byte FOLLOWS
    the previous object's frame list. Server/ObjectFlags.cs documents this as a trap worth
    not re-deriving -- attributing a record's frames and its trailing flag to the same
    object shifts every frame one ahead -- so the loop is copied from there rather than
    re-invented."""
    global _sobj
    if _sobj is not None:
        return _sobj
    for p in SOBJ_PATHS:
        if not os.path.exists(p):
            continue
        try:
            b = open(p, "rb").read()
            count = int.from_bytes(b[0:4], "little")
            flags = bytearray(count + 1)        # [0] = "no object", never blocks
            off = 4 + 1
            for z in range(1, count + 1):
                if off >= len(b):
                    break
                tc = b[off]; off += 1
                off += tc * 2 + 5               # frames, then FF FF FF FF 00
                if off >= len(b):
                    break
                flags[z] = b[off]; off += 1
            _sobj = flags
            return _sobj
        except Exception:
            continue
    _sobj = bytearray(1)                        # no table -> ground flag only
    return _sobj


_warps = None


def warps_for(mid):
    """Tiles in map `mid` that teleport you somewhere else, from game-data/Warps.csv.

    Authored data, so this is exact and known before we ever set foot in the room. The bot
    used to discover warps by falling through them -- each one costing a full gate-and-walk
    cycle (~40s) to recover from, twice inside a minute in Mythic Waters 1: (6,5) drops you
    in Mythic Owsla 1. Same lesson as the passability map: read what is written down instead
    of rediscovering it by trial."""
    global _warps
    if _warps is None:
        _warps = {}
        try:
            with open(WARPS, newline="", encoding="utf-8", errors="replace") as f:
                for r in csv.DictReader(f):
                    try:
                        m = int(r["SourceMapId"])
                        _warps.setdefault(m, set()).add(
                            (int(r["SourceX"]), int(r["SourceY"])))
                    except (ValueError, KeyError, TypeError):
                        pass
        except OSError:
            pass
    return _warps.get(mid, set())


_warp_dests = None


def warp_dests(mid):
    """{(x,y): dest_map_id} for every warp leaving map `mid` (game-data/Warps.csv).

    Lets a caller tell a warp that leads deeper into the cave system from one that leads back
    to the hub, so the bot can traverse between hunting caves without accidentally gating
    itself out."""
    global _warp_dests
    if _warp_dests is None:
        _warp_dests = {}
        try:
            with open(WARPS, newline="", encoding="utf-8", errors="replace") as f:
                for r in csv.DictReader(f):
                    try:
                        m = int(r["SourceMapId"])
                        _warp_dests.setdefault(m, {})[
                            (int(r["SourceX"]), int(r["SourceY"]))] = \
                            int(r["DestinationMapId"])
                    except (ValueError, KeyError, TypeError):
                        pass
        except OSError:
            pass
    return _warp_dests.get(mid, {})


def map_ids():
    """room name -> numeric map id (game-data/map_index.csv)."""
    global _names
    if _names is None:
        _names = {}
        try:
            with open(MAP_INDEX, newline="", encoding="utf-8", errors="replace") as f:
                for r in csv.DictReader(f):
                    try:
                        _names[r["name"].strip()] = int(r["id"])
                    except (ValueError, KeyError):
                        pass
        except OSError:
            pass
    return _names


class RoomMap:
    """Passability for one room. Everything below is derived from the file; nothing here is
    learned by walking, so it is correct on the first visit and identical on every later
    one."""

    def __init__(self, w, h, solid, obj, src="", warps=()):
        self.w, self.h, self.src = w, h, src
        self._solid, self._obj = solid, obj
        self.flags = sobj_flags()
        self.warps = set(warps)      # tiles that teleport -- walkable, but never walk there

    # ---- the primitives ----
    def in_bounds(self, x, y):
        return 0 <= x < self.w and 0 <= y < self.h

    def oflag(self, x, y):
        o = self._obj[y * self.w + x]
        return self.flags[o] if 0 <= o < len(self.flags) else 0

    def solid(self, x, y):
        """Impassable ground, or off the map. Out of range counts as solid."""
        return (not self.in_bounds(x, y)) or bool(self._solid[y * self.w + x])

    def standable(self, x, y):
        """Could a creature (us or a mob) be standing on this tile?"""
        if self.solid(x, y):
            return False
        return self.oflag(x, y) != SOLID_OBJ

    def blocked_move(self, x, y, d):
        """Is a move INTO (x,y) heading `d` blocked? Ground flag OR a directional object
        wall -- the object layer is what stops you walking through the side of a building."""
        if self.solid(x, y):
            return True
        return bool(self.oflag(x, y) & DIR_BIT.get(d, 0))

    # ---- what the fight logic actually asks ----
    def threat_sides(self, x, y):
        """How many mobs can be in contact with us at once if we stand here.

        This is the number the whole positioning strategy runs on: a tile with 1 is a duel
        no matter how many hares are in the room, and being surrounded stops being something
        to react to because it stops being reachable."""
        return sum(1 for dx, dy in DIRS.values() if self.standable(x + dx, y + dy))

    def is_warp(self, x, y):
        return (x, y) in self.warps

    def cover_tiles(self, maxthreat=2):
        """Every tile worth fighting on, best first.

        Zero-side tiles are EXCLUDED. A tile no mob can reach is also a tile WE cannot
        reach -- Mythic Waters 1 has one, (2,20), walled in behind the solid objects at
        (3,20)/(4,20) -- and sorting purely on "fewest attackers" makes it look like the
        best spot in the room. It is a hole in the wall, not a fighting position."""
        out = [(self.threat_sides(x, y), (x, y))
               for y in range(self.h) for x in range(self.w)
               if self.standable(x, y)]
        return [t for n, t in sorted(out) if 1 <= n <= maxthreat]

    def census(self):
        c = collections.Counter()
        for y in range(self.h):
            for x in range(self.w):
                if self.standable(x, y):
                    c[self.threat_sides(x, y)] += 1
        return dict(sorted(c.items()))


def _from_cmp(path):
    raw = open(path, "rb").read()
    if raw[:4] != b"CMAP":
        raise ValueError(f"not CMAP: {raw[:4]!r}")
    dims = struct.unpack_from("<I", raw, 4)[0]
    w, h = dims & 0xFFFF, (dims >> 16) & 0xFFFF
    body = zlib.decompress(raw[8:])
    if len(body) < w * h * 6:
        raise ValueError(f"payload {len(body)} < {w*h*6}")
    solid = bytearray(w * h)
    obj = [0] * (w * h)
    for i in range(w * h):
        _g, p, o = struct.unpack_from("<HHH", body, i * 6)
        solid[i] = 1 if p else 0
        obj[i] = max(0, o - 1)              # stored +1; 0 = no object
    return RoomMap(w, h, solid, obj, src=path)


def _from_rtk(path, w, h):
    """RTK's own TK######.map: 4-byte header, then the same [ground][passable][object]
    triple. Fallback only -- it disagrees with the client where it matters (it calls
    (5,19) in Mythic Waters 1 walkable, which the client does not)."""
    d = open(path, "rb").read()
    if len(d) < 4 + w * h * 6:
        raise ValueError(f"{len(d)}B < {4 + w*h*6}")
    solid = bytearray(w * h)
    obj = [0] * (w * h)
    for i in range(w * h):
        _g, p, o = struct.unpack_from("<HHH", d, 4 + i * 6)
        solid[i] = 1 if p else 0
        obj[i] = o
    return RoomMap(w, h, solid, obj, src=path)


def for_room(name, wh=None):
    """RoomMap for a room name, or None if we have no map for it.

    Prefers the client's cache, because the client is what enforces collision. Falls back
    to RTK's copy so a room the client has not cached yet is not simply blind."""
    if name in _cache:
        return _cache[name]
    mid = map_ids().get(name)
    rm = None
    if mid is not None:
        p = os.path.join(CLIENT_MAPS, f"TK{mid:06d}.cmp")
        if os.path.exists(p):
            try:
                rm = _from_cmp(p)
            except Exception:
                rm = None
        if rm is None and wh:
            p2 = os.path.join(RTK_MAPS, f"TK{mid:06d}.map")
            if os.path.exists(p2):
                try:
                    rm = _from_rtk(p2, int(wh[0]), int(wh[1]))
                except Exception:
                    rm = None
        if rm is not None:
            rm.warps = warps_for(mid)
    _cache[name] = rm
    return rm


def main():
    import sys
    room = " ".join(sys.argv[1:]) or "Mythic Waters 1"
    rm = for_room(room)
    if rm is None:
        print(f"no map for {room!r} (client cache: {CLIENT_MAPS})")
        return 1
    print(f"{room}: {rm.w}x{rm.h} from {rm.src}")
    print("  . walkable  # solid  o solid object  + partial object")
    print("     " + "".join(str(x % 10) for x in range(rm.w)))
    for y in range(rm.h):
        r = ""
        for x in range(rm.w):
            if rm.solid(x, y):
                r += "#"
            elif rm.oflag(x, y) == SOLID_OBJ:
                r += "o"
            elif rm.oflag(x, y):
                r += "+"
            else:
                r += "."
        print(f"  {y:2d} {r}")
    print(f"\n  max attackers -> tile count: {rm.census()}")
    best = rm.cover_tiles(1)
    print(f"  best cover ({len(best)} tiles at <=1): {best[:12]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
