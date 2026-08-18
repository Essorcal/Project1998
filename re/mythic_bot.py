#!/usr/bin/env python
"""Golden Hare grinder for Mythic Waters 1, with a gate-out-and-restore cycle.

Hares are NOT squirrels. They are hostile: they path to you and hit for 30-50 against a
Soothe that heals 50. So one hare on you is roughly break-even with healing, two is losing
slowly, and three or more outruns healing entirely. Everything below follows from that:
being surrounded is the failure mode, not low HP -- low HP is just how you notice.

THE CYCLE
    1. Gateway -> N -> Enter                      => Mythic Nexus
    2. click the tree (look 767, at 35,8)         => menu
       option 2 (heal+mana), 3 (ASV buff), 2      => topped up and buffed
    3. walk onto (49,11)                          => Mythic Gateway
    4. one step north                             => Mythic Waters 1
    5. hunt look 125 until in trouble, then gate and repeat

Packets are the ones a real click produces, captured from the client before encryption
(re/capture_plain.py):
    click    : 43 01 <id BE32> 00
    option N : 3a 01 <id BE32> 00 00 00 02 01 <N> 00

    python re/mythic_bot.py                # run the cycle
    python re/mythic_bot.py --seconds 900
    python re/mythic_bot.py --no-gate      # already in the cave; just hunt

Stop with Ctrl-C or `touch re/auto/STOP`.
"""
import os, sys, time, json, random, collections

D = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, D)

import nexus_bot as NB
import nexus_agent as NA
import inventory as INV                     # live inventory slot letters, for re-wearing
import roommap                              # real passability, from the client's own map
from bot_input_test import find_windows, VK
from squirrel_bot import (Simple, log, DIRS, LOOP, STEP_GAP, WALL_TTL, SWING_GAP,
                          KILL_WINDOW, WARP_JUMP, TYPE_CREATURE, TYPE_GROUND_ITEM,
                          MANA_FLOOR, TARGET_HP, MAX_BURST, MAX_BURST_EMERG, EMERG_HP,
                          HEAL_CD, HEAL_COST, SPELL_GAP, P_STOP)

P_NAV = os.path.join(NA.OUT, "nav.json")   # learned map geometry, per room, across runs


class WallMemory(dict):
    """`tile -> timestamp` exactly as the base bot expects, plus a permanent set.

    Two different things were being stored in one dict and cleared together:
      * BELIEFS -- "a step into that tile did nothing just now", which is usually a mob in
        the way and must expire (WALL_TTL), and
      * GEOMETRY -- "that tile is a wall", which is a property of the map and is true
        forever, in every run.
    Because they shared a dict, every `self.blocked.clear()` (on gate, on map change, on a
    6s stall -- three call sites in the base bot) threw the geometry away too, so each cycle
    re-discovered the same walls by walking into them. That is the "blind exploration walk
    every time".

    Reporting permanent walls as blocked-right-now means the base bot's TTL checks
    (`now - blocked.get(t, 0) < WALL_TTL`) treat them as walls without any change to
    toward()/wander()/pick_goal()/explore().
    """

    def __init__(self, perm=None):
        dict.__init__(self)
        self.perm = set(perm or ())
        self.dirty = False

    def get(self, k, d=0):
        if k in self.perm:
            return time.time()          # never expires
        return dict.get(self, k, d)

    def __contains__(self, k):
        return k in self.perm or dict.__contains__(self, k)

    def clear(self):
        dict.clear(self)                # beliefs go; the map stays

    def wall(self, t):
        if t not in self.perm:
            self.perm.add(t)
            self.dirty = True

    def unwall(self, t):
        """We are standing on it, so it is not a wall -- whatever we thought."""
        if t in self.perm:
            self.perm.discard(t)
            self.dirty = True


HUNT_LOOKS = {21, 125, 131}   # ALL rabbit/hare looks: Hare/Giant hare/Large hare (21),
                              # Rabbit/Golden hare/colored rabbits (125), Mythic hare and the
                              # boss-tier rabbit/hare (131). The room decides which show up.
TREE_LOOK = 767            # HealerOfDoom (NPCs: map 41 Mythic Nexus, 35,8)
NEXUS_ROOM = "Mythic Nexus"
GATEWAY_ROOM = "Mythic Gateway"
HUNT_ROOM = "Mythic Waters 1"
TREE_XY = (35, 8)
TRANSIT_XY = (49, 11)      # step on this in Mythic Nexus -> Mythic Gateway

# --- when to break off. These are about ATTACKERS, not just HP: HP is a lagging indicator
# and by the time it looks bad we are already surrounded and cannot walk out.
SURROUNDED = 4             # boxed in on every side -> leave regardless of how HP looks.
                           # Was 3, which cost a gate every ~90s (~40s round trip each)
                           # against a 2.8-minute kill. Ten minutes of measurement says we
                           # hold ~90% HP indefinitely against 2, so at 3 we FIGHT.
GATE_HP = 0.45             # below this while anything is on us -> gate out
GATE_RETRY = 15.0          # a gate that failed is not retried inside this; we fight instead
UNFLANK_CD = 2.0           # min seconds between corrective steps, so we swing in between

# --- fighting from cover.
# Not a fixed home tile -- the room has to be roamed to find hares, so the rule is about
# WHERE WE ENGAGE, not where we live. A tile's worth is how many things can be in contact
# with us on it: 4 in the open, 2 in a corner, 1 where a wall and an object meet. Fighting
# on a 1 makes a pack into a queue, which is a fight we already know we win (measured: 88-93%
# HP held indefinitely against two).
# These numbers come from the map now, not from bumping into things -- see roommap.py.
COVER_MAX = 2              # a tile is "cover" if at most this many can reach us on it
COVER_RADIUS = 10          # how far we will walk to reach cover when something aggros
ENGAGE_RANGE = 7           # a hare this close is incoming; get to cover before it lands
HOLD_PATIENCE = 10.0       # nothing in contact for this long -> go to it instead of waiting
WAIT_RANGE = 4             # only hold cover while a hare is this close, i.e. actually inbound
APPROACH_NEAR = 5          # when closing on a mob, stand on the best cover within this of it
COVER_GIVEUP = 2.0         # stuck on one tile this long while wanting cover -> fight instead
SWEEP_MIN = 6              # a sweep goal must be at least this far, or we just dither
SWEEP_GIVEUP = 25.0        # ...and is abandoned after this long if we cannot get there
ANCHOR = (5, 20)           # Mythic Waters 1's best tile (1 side). Only a default: --anchor
                           # overrides, and with a map loaded the bot finds its own.

# --- the +hit experiment.
# Each ring is +3 hit and they do NOT displace each other -- a second ring fills the other
# hand -- so taking one OFF is the only way to move `hit` without touching any other stat.
# That makes 2/1/0 rings a genuine controlled experiment: one variable, three levels, same
# mob, same character, same tile. Rotating rather than doing one long block per condition
# matters because level and buff state drift over an hour, and a block design would confound
# that drift with the ring.
RING_KILLS = 6             # kills per arm before rotating
RING_ARMS = (2, 1, 0)      # rings worn, in rotation order
FACE_DELTA = {0: (0, -1), 1: (1, 0), 2: (0, 1), 3: (-1, 0)}   # N E S W
BUFF_EVERY = 780.0         # ASV runs 900s fresh (reported from the client's own countdown,
                           # which shows seconds remaining). Renew at 780 for a 2-minute
                           # margin -- enough to finish a kill and walk out unhurried.
                           #
                           # This was 300, which gated with ~600s of buff still on the clock:
                           # two wasted round trips per cycle at ~40s each, against a
                           # 2.8-minute kill. It was set that low because an earlier session
                           # concluded the 15-minute figure "was wrong -- it dropped well
                           # inside it". It did not. Detection was broken by the gear/buff
                           # confound (both move armour), so a buff that was still up read as
                           # dropped, and the timer got shortened to paper over it.
HEAL_HARD = 0.80           # hares hit hard; heal earlier than we did for squirrels
TREE_GAP = 0.6             # pause between click and option, and between options
FINISH_HP = 35             # mob HP% worth staying a few more seconds to finish
FINISH_SECS = 12.0         # ...but no longer than this
FINISH_BAIL = 0.60         # ...and never below this much of our own HP

# --- self-cast buffs. Kept up on wall-clock timers: the client refuses a recast while one
# is still ticking ("Flank had 30s left, couldn't recast"), so a buff is recast only after
# it has expired, plus a small margin. Cast at the hub during the tree visit AND renewed in
# the field, because Bless (375s) drops far more often than a gate cycle comes round --
# waiting for the next hub trip would leave most of the hunt unbuffed.
SELF_BUFFS = [
    {"name": "Bless",        "letter": "d", "dur": 375.0},
    {"name": "Tiger's Fury", "letter": "f", "dur": 625.0},
    {"name": "Flank",        "letter": "e", "dur": 625.0},
    {"name": "Backstab",     "letter": "g", "dur": 625.0},
]
SELF_BUFF_MARGIN = 2.0        # recast this many seconds past expiry (the "give it 2s" margin)
SELF_BUFF_MANA_FLOOR = 0.15   # don't spend into the heal reserve to renew a buff mid-fight

# The hunt spans several connected caves. Only the HUB (where the healer NPC is) means "stop
# hunting and restore" -- landing in a DIFFERENT cave is not a reason to gate, it is somewhere
# to keep hunting. Traverse between caves; gate only to get buffed/healed at the hub.
HUB_ROOMS = (NEXUS_ROOM, GATEWAY_ROOM)
HUB_MAP_IDS = (41, 44)        # Mythic Nexus, Mythic Gateway -- never cross a warp back to these
EMPTY_TRAVERSE = 12.0         # no melee progress this long (room empty, or hares walled off /
                              # phantom) -> cross a warp to the next cave instead of oscillating


class Mythic(Simple):
    """Reuses the grinder that works -- perception, movement, healing, looting, room
    identity -- and adds the hostile-mob handling plus the gate/tree/transit cycle."""

    def __init__(self, ex, hwnd, leash=40, agent=None, world=None):
        Simple.__init__(self, ex, hwnd, leash=leash, agent=agent)
        self.world = world          # live packet-side view: worn gear, facing
        self.tree_uid = None
        self.last_buff = 0.0
        self.buff_cast = {}         # self-buff name -> ts we last cast it (0/absent = never)
        self._traverse_to = None    # warp tile we are crossing to reach the next cave
        self.last_progress_ts = time.time()   # last time we actually landed a melee hit
        self.buffed_vec = None     # our stat vector WHILE buffed -- see buff_is_up()
        self.gates = 0
        self.hp_hist = []          # (ts, hp) -- to see damage OUTPACING heals, not just low HP
        self.nav = self.load_nav()  # room -> set of wall tiles, learned in previous runs
        self.nav_room = None        # which room self.blocked's permanent set belongs to
        self.nav_saved = 0.0
        self.unbuffed_vec = None    # stat baseline before ASV -- see buff_is_up()
        self.last_creatures = []    # so wall-learning can tell a mob from a wall
        self.prev_room = None       # the room a warp tile belongs to is the one we LEFT
        self.open_tiles = set()     # tiles we have actually stood on, this room
        self.open_dirty = False
        self.blocked = WallMemory()
        self.last_unflank = 0.0
        self.gate_fail_ts = 0.0     # a gate that did not land; fight on and retry later
        self.anchor = ANCHOR
        self.rmap = None            # real passability for the current room, or None
        self.cover_list = []        # tiles in this room worth fighting on, best first
        self.hold = None            # the cover tile we are fighting from right now
        self.last_mob_ts = time.time()
        self.cover_logged = False
        self.room_pending = None    # a room name that has not proved itself yet
        self.room_pending_n = 0
        self.room_xy = None         # where we were at the last accepted room reading
        self.reached = None         # cover tile we have actually arrived on
        self.last_idle_log = 0.0    # rate-limit for the "not swinging" diagnostic
        self.last_stuck_log = 0.0
        self.stuck_steps = 0        # consecutive issued steps that changed nothing
        self.self_uid = None        # our own entity record -- never an attacker
        self._self_cand = None
        self.last_contact = 0.0     # when something was last actually in contact
        self.stall_logged = False
        self.last_frame_log = 0.0   # rate-limit for player-block vs entity-pool disagreement
        self.me_pool = None         # last position our own ENTITY reported (the real one)
        self.me_pool_ts = 0.0
        self.pool_hit = self.pool_miss = 0   # how often the scan actually contains us
        self.cover_from = None      # tile we have been trying to leave, and since when
        self.cover_since = 0.0
        self.sweep_goal = None      # where we are walking to when the room looks empty
        self.sweep_ts = 0.0
        self.visited = {}           # tile -> when we last stood there, for sweep coverage
        # --- +hit experiment state
        self.rings = []             # the two ring names, learned by taking them off
        self._sctrl = None          # foreground controller, for shifted keys only
        self.arm = None             # rings currently worn (2/1/0), None = not running it
        self.arm_i = 0
        self.arm_kill_mark = 0      # self.kills when this arm started
        self.arm_hit = {}           # rings -> the `hit` stat we measured with them on
        self.exp_on = False
        self.exp_want = False       # --rings asked for it
        self.exp_tried = False      # ...and we have had our one go at setting it up

    # ---------- learned map geometry ----------
    def load_nav(self):
        """{room: {"walls": [...], "open": [...]}} -- geometry learned in previous runs.

        `open` is the important half: tiles we have actually STOOD on. Walls alone still
        leave the walk to the tree rediscovering the route by bumping into things, because
        every unvisited tile looks equally good. Remembering where we have really walked is
        what turns the second run down a corridor into a straight line."""
        try:
            with open(P_NAV) as f:
                raw = json.load(f)
            nav = {}
            for r, v in raw.items():
                if isinstance(v, list):                     # older walls-only file
                    v = {"walls": v, "open": []}
                nav[r] = {"walls": {tuple(t) for t in v.get("walls", [])},
                          "open": {tuple(t) for t in v.get("open", [])}}
            if nav:
                log("known map: " + ", ".join(
                    f"{r} {len(d['open'])} open/{len(d['walls'])} wall"
                    for r, d in nav.items()))
            return nav
        except Exception:
            return {}

    def stash_nav(self):
        if self.nav_room:
            self.nav[self.nav_room] = {"walls": set(self.blocked.perm),
                                       "open": set(self.open_tiles)}

    def save_nav(self, force=False):
        if not (self.blocked.dirty or self.open_dirty or force):
            return
        if time.time() - self.nav_saved < 20.0 and not force:
            return
        self.stash_nav()
        try:
            os.makedirs(os.path.dirname(P_NAV), exist_ok=True)
            with open(P_NAV, "w") as f:
                json.dump({r: {"walls": sorted(map(list, d["walls"])),
                               "open": sorted(map(list, d["open"]))}
                           for r, d in self.nav.items()}, f)
            self.blocked.dirty = self.open_dirty = False
            self.nav_saved = time.time()
        except Exception as e:
            log(f"nav save failed: {e}")

    def use_room_nav(self, room):
        """Swap in what we know about this room. Coordinates mean different things in
        different rooms, so these sets must never be shared between them."""
        if room == self.nav_room:
            return
        self.stash_nav()
        self.save_nav()
        self.nav_room = room
        d = self.nav.get(room) or {"walls": set(), "open": set()}
        self.blocked = WallMemory(d["walls"])
        self.open_tiles = set(d["open"])
        # Real geometry, if the client has cached this room. Everything positional prefers
        # it; the bumped beliefs above stay only as a fallback for rooms we have no map for.
        self.rmap = roommap.for_room(room, self.room_wh)
        self.cover_list = []
        if self.rmap is not None:
            # Drop anything that cannot belong to this room. While the name buffer was
            # misreporting the room, walked tiles got filed against whichever room the bot
            # believed it was in -- (49,13) ended up in a 24x24 room's tile set, a Mythic
            # Nexus coordinate. Bad geometry is worse than none, so bin it on sight.
            n0, w0 = len(self.open_tiles), len(self.blocked.perm)
            self.open_tiles = {t for t in self.open_tiles
                               if self.rmap.in_bounds(t[0], t[1])}
            self.blocked.perm = {t for t in self.blocked.perm
                                 if self.rmap.in_bounds(t[0], t[1])}
            if len(self.open_tiles) != n0 or len(self.blocked.perm) != w0:
                self.open_dirty = self.blocked.dirty = True
                log(f"{room}: dropped {n0 - len(self.open_tiles)} stray tiles + "
                    f"{w0 - len(self.blocked.perm)} stray walls (wrong-room contamination)")
            self.cover_list = self.rmap.cover_tiles(COVER_MAX)
            log(f"{room}: MAP {self.rmap.w}x{self.rmap.h} "
                f"(max attackers -> tiles: {self.rmap.census()}), "
                f"{len(self.cover_list)} cover tiles")
        elif self.open_tiles or self.blocked.perm:
            log(f"{room}: no map -- falling back on {len(self.open_tiles)} walked tiles, "
                f"{len(self.blocked.perm)} bumped walls")

    def note_open(self, t):
        if t not in self.open_tiles:
            self.open_tiles.add(t)
            self.open_dirty = True

    def learn_wall(self, tile, creatures=None):
        """Promote a refused tile to permanent geometry -- ONLY where we have no real map.

        The creature check below was supposed to keep mobs out of the geometry and did not:
        checked afterwards against the client's map, 18 of the 30 walls this had "learned"
        in Mythic Waters 1 were mobs, one of them the tile we were standing on. The scan is
        a moment behind the step, so the hare that blocked us has often already moved by the
        time we look. Where a map exists this is dead code, which is the point."""
        if self.rmap is not None:
            return
        if creatures and any((c[1], c[2]) == tile for c in creatures):
            return
        self.blocked.wall(tile)

    # ---------- perception ----------
    def entities(self):
        """Same enumeration as the squirrel grinder, but hunting look 125.

        `creatures` is every living thing, which is what the threat logic reads -- being
        surrounded is about bodies next to us, whether or not they are our target."""
        rows = []
        for lo, hi in self.regions:
            try:
                rows.extend(self.ex.enument(NB.ENT_VTABLE, lo, hi) or [])
            except Exception:
                pass
        mobs, items, creatures = [], [], []
        for r in rows:
            if len(r) < 5:
                continue
            uid, x, y, ty, look = r[0], r[1], r[2], r[3], r[4]
            if not (1000 < uid < 100_000_000):
                continue
            if ty == TYPE_CREATURE:
                creatures.append((uid, x, y))
                if (look & 0x7FFF) in HUNT_LOOKS:
                    mobs.append((uid, x, y))
            elif ty == TYPE_GROUND_ITEM:
                items.append((uid, x, y))
        return mobs, items, creatures

    def find_by_look(self, look):
        for lo, hi in self.regions:
            try:
                rows = self.ex.enument(NB.ENT_VTABLE, lo, hi) or []
            except Exception:
                continue
            for r in rows:
                if len(r) >= 5 and 1000 < r[0] < 100_000_000 and (r[4] & 0x7FFF) == look:
                    return r[0], r[1], r[2]
        return None

    # ---------- threat ----------
    def adjacent(self, me, creatures):
        """Things standing next to us -- EXCLUDING ourselves.

        Our own character is a type-3 entity in the same pool as the mobs (look 200 for this
        rogue), sitting at distance 0, so it counted as an attacker. Every threat number was
        one too high: SURROUNDED=3 fired at two real hares and CROWD=2 at one. That is why
        the run gated out in the same second it arrived -- 'GATE OUT #2 (3 on us)' on the
        arrival tile was two hares and us.
        """
        # OUR OWN RECORD IS ONE OF THESE, AND IT IS NOT AT DISTANCE 0.
        #
        # The pool position for our character lags the client-memory position we read for
        # `me`, so the moment we step, our own entity is sitting on the tile we just left --
        # distance 1, i.e. "an attacker". Measured: uid 493989 was the ONLY thing ever
        # logged in contact-without-a-swing, at distance 1, tracking us across the map
        # ((15,18)->neighbour (14,18), then (17,16)->neighbour (17,17)). The old fix
        # excluded self at distance 0, which is only true while standing still.
        #
        # `self_uid` is learned in run(): the creature that is never huntable and never
        # more than a tile away is us. Until it is known, fall back to counting only
        # huntable mobs, which cannot include us.
        #
        # Tiles, not records: four orthogonal neighbours means "5 on us" (logged at
        # 23:42:00) is impossible, so one body must never be counted twice.
        seen = {}
        for c in creatures:
            if c[0] == self.self_uid:
                continue
            if 0 < abs(c[1] - me[0]) + abs(c[2] - me[1]) <= 1:
                seen[(c[1], c[2])] = c        # one body per tile
        return list(seen.values())

    def learn_self_uid(self, me, creatures, mobs):
        """Work out which entity record is us, so it stops counting as an attacker.

        It is the one that is never a mob we hunt and never further than a tile away. A
        real attacker fails that test the moment it dies, wanders, or we walk away."""
        if self.self_uid is not None:
            return
        hunt = {m[0] for m in mobs}
        near = {c[0] for c in creatures
                if c[0] not in hunt and abs(c[1] - me[0]) + abs(c[2] - me[1]) <= 1}
        if self._self_cand is None:
            self._self_cand = dict.fromkeys(near, 0)
        for uid in list(self._self_cand):
            if uid in near:
                self._self_cand[uid] += 1
                if self._self_cand[uid] >= 12:      # ~12 ticks glued to us -> that is us
                    self.self_uid = uid
                    log(f"our own entity is uid={uid} -- excluding it from threat counts")
                    return
            else:
                del self._self_cand[uid]            # let go of us? then it was not us
        for uid in near:
            self._self_cand.setdefault(uid, 1)

    def openness(self, t):
        """How many hares can be in contact with us at once if we stand on `t`.

        Read from the map when we have one. The old version counted sides "not currently
        believed to be a wall", where the beliefs came from bumping into things -- and
        measured against the real map, 18 of 30 such beliefs in this room were MOBS, not
        walls. It reported 3 for (5,20), a tile with exactly one open side. A number that
        wrong is worse than no number, because the positioning logic trusts it."""
        if self.rmap is not None:
            return self.rmap.threat_sides(t[0], t[1])
        n = 0
        for vx, vy in DIRS.values():
            nb = (t[0] + vx, t[1] + vy)
            if not self.in_room(nb):
                continue
            if time.time() - self.blocked.get(nb, 0) < WALL_TTL:
                continue
            n += 1
        return n

    def sweep_step(self, me):
        """Cover ground when nothing is in sight. Returns the tile stepped into, or None.

        The inherited explore() was written for open squirrel fields: it re-picks a goal
        every tick from bumped beliefs and knows nothing about the map. In a 24x24 room with
        four hares on a 360s respawn, that came out as pacing -- measured at 11:11, six ticks
        shuffling between (5,17),(6,18),(7,17),(6,16),(7,16) while the room sat empty.

        Two things fix it: pick goals from the MAP (every standable non-warp tile is a
        candidate, not just ones we have bumped our way around), and COMMIT to a goal until
        it is reached or times out. Re-deciding every tick is what produces dithering -- the
        goal is genuinely unobservable state, so unlike the target and the cover tile it is
        worth remembering.
        """
        if self.rmap is None:
            return self.explore(me)
        now = time.time()
        if (self.sweep_goal is None or me == self.sweep_goal
                or now - self.sweep_ts > SWEEP_GIVEUP):
            prev = self.sweep_goal
            self.sweep_goal = self.pick_sweep(me)
            self.sweep_ts = now
            if self.sweep_goal and self.sweep_goal != prev:
                log(f"sweeping to {self.sweep_goal} (room looks empty from {me})")
        if not self.sweep_goal:
            return None
        d = self.bfs_step(me, self.sweep_goal, allow_warp=False)
        if d is None:
            # UNREACHABLE. Stamp it as if we had been there, or pick_sweep hands back the
            # same tile next tick -- it is still the stalest, since never arriving means its
            # visit time never updates. That loop ran 3069 times in one run: choose, fail to
            # route, clear, choose the same tile again, forever. Marking it visited retires
            # it from the running and the sweep moves on to somewhere it can actually get.
            self.visited[self.sweep_goal] = now
            self.sweep_goal = None
            return None
        vx, vy = DIRS[d]
        return (me[0] + vx, me[1] + vy) if self.step(d) else None

    def pick_sweep(self, me):
        """The stalest reachable tile that is actually somewhere else.

        Oldest-visit first so the whole room gets covered, with a minimum distance so we
        commit to going somewhere rather than stepping onto the next tile and re-deciding."""
        rm = self.rmap
        best, best_key = None, None
        for y in range(rm.h):
            for x in range(rm.w):
                t = (x, y)
                if not rm.standable(x, y) or rm.is_warp(x, y):
                    continue
                d = abs(x - me[0]) + abs(y - me[1])
                if d < SWEEP_MIN:
                    continue
                key = (self.visited.get(t, 0.0), d)   # stalest, then nearest of those
                if best_key is None or key < best_key:
                    best, best_key = t, key
        return best

    def is_warp(self, t):
        """Known warps come from game-data/Warps.csv, not from falling through them.

        The base bot only knew a warp after it had been used -- so roaming an unfamiliar
        corner of a room meant discovering exits by taking them, at a full gate-and-walk
        cycle each. Run 11 lost two inside a minute: Golden Warren 1, then Mythic Owsla 1
        via (6,5). The table has every one of them, keyed by map."""
        if self.rmap is not None and self.rmap.is_warp(t[0], t[1]):
            return True
        return Simple.is_warp(self, t)

    def back_to_wall(self, me, mob):
        """If we turn to face `mob`, is the tile directly behind us solid?

        Attacking faces us at the target, so the tile on the far side is our back. When it
        is a wall, nothing can occupy it -- one fewer body able to reach us, and no attack
        from behind. With no map we cannot know, so nothing is preferred."""
        if self.rmap is None:
            return False
        dx, dy = mob[1] - me[0], mob[2] - me[1]
        behind = (me[0] - dx, me[1] - dy)
        return not self.rmap.standable(behind[0], behind[1])

    def approach_cover(self, me, tgt_xy, near=APPROACH_NEAR):
        """Where to stand to fight the mob at `tgt_xy` -- the best-covered tile near it.

        Walking AT a mob means contact happens wherever the mob is standing, and that is
        open ground, because open ground is most of the room. So the bot fought every pulled
        hare on a 3- or 4-side tile while a 1-side tile sat unused a few steps away. Picking
        the tile first and letting the mob close the last step is the entire strategy -- the
        cover logic was only ever exercised when something wandered onto us.

        Ties break toward the tile nearest US, so hugging a wall does not mean crossing the
        room to do it."""
        if self.rmap is None or not self.cover_list:
            return tgt_xy
        best, best_key = None, None
        for t in self.cover_list:
            d = abs(t[0] - tgt_xy[0]) + abs(t[1] - tgt_xy[1])
            if d > near:
                continue
            key = (self.rmap.threat_sides(t[0], t[1]), d,
                   abs(t[0] - me[0]) + abs(t[1] - me[1]))
            if best_key is None or key < best_key:
                best, best_key = t, key
        return best or tgt_xy

    def best_cover(self, me, radius=COVER_RADIUS):
        """The NEAREST tile better than where we stand. None if we are already fine.

        Nearest-adequate, deliberately not best-in-room. Sorting by quality first made the
        bot fixate on (5,20) -- the one 1-side tile for miles -- from five steps away,
        walking past perfectly good 2-side tiles beside it, and under fire it never arrived
        at all: it just displayed `->(5,20)` while fighting on 4-side ground. A wall at your
        back now beats a better wall you never reach."""
        if self.rmap is None or not self.cover_list:
            return None
        cur = self.rmap.threat_sides(me[0], me[1])
        best, best_key = None, None
        for t in self.cover_list:
            d = abs(t[0] - me[0]) + abs(t[1] - me[1])
            if d > radius or t == me:
                continue
            n = self.rmap.threat_sides(t[0], t[1])
            if n >= cur:
                continue                     # no better than where we already are
            key = (d, n)                     # closest first, then most sheltered
            if best_key is None or key < best_key:
                best, best_key = t, key
        return best

    def gate_reason(self, adj, frac):
        """THE ONLY PLACE that decides to leave. Returns (why, forced) -- (None, False) to
        stay and fight.

        Four reasons. Everything else is a fight. This used to be scattered across a
        headcount test, an HP-trend test, and two buff tests, and between them the bot gated
        three times in four minutes out of fights it was winning -- ~40s round trip each
        against a 2.8-minute kill.

        Low HP alone is NOT a reason: with nothing adjacent, nothing is taking it further
        down and Soothe puts it back. It only becomes a reason when something is hitting us
        while we are there, or when there is no mana left to answer with.

        `forced` means something is driving us out, so do not linger to finish a mob.
        """
        # The user runs this BUFFED and wants to fight surrounds, not flee them: "it's okay
        # to be surrounded now, just gate if you get low." A full headcount is no longer a
        # reason to leave -- cover-seeking thins the attackers and heal-through carries the
        # HP. We gate on HP and mana only.
        if frac < GATE_HP and adj:
            return f"hp {frac:.0%} with {len(adj)} on us", True
        v = self.vitals()
        # OUT OF MANA WITH SOMETHING ON US IS ALREADY A LOSING POSITION -- leave on the
        # mana, not on the HP. Measured at 12:01: mana hit 36/727 (below MANA_FLOOR, so
        # healing stopped dead), and HP fell 1282 -> 706 in about five seconds with two
        # hares in contact. Waiting for the 45% HP trigger spent half a health bar
        # discovering something we already knew the moment the mana ran out.
        if v and v[3] and adj and v[2] < MANA_FLOOR * v[3]:
            return f"out of mana ({v[2]}/{v[3]}) with {len(adj)} on us", True
        if v and v[3] and frac < 0.60 and v[2] < HEAL_COST * 4:
            return "hurt with no mana to heal", True
        # THE TIMER IS THE BUFF SIGNAL. Inferring it from the stat block is abandoned.
        #
        # It needs a trustworthy "this is what buffed looks like" reference and there is no
        # reliable moment to capture one: arrive already buffed and the before/after
        # snapshots are identical (11:52 -- "stats unchanged after ASV"); catch the tree
        # visit before ASV lands and the reference is an UNBUFFED vector (12:01 stored AC 36
        # when buffed is 26), after which the buff actually applying moves the vector away
        # from that reference and reads as the buff DROPPING. Both happened tonight; the
        # second cost a gate at 12:08 while the buff was freshly up.
        #
        # Two false positives, zero confirmed true ones. Meanwhile the duration is known
        # exactly -- 900s, off the client's own countdown -- so a 780s timer answers the
        # question without inferring anything. visit_tree still warns if the cast does not
        # move our stats, which is the honest place to notice a failed buff.
        if self.last_buff > 0 and time.time() - self.last_buff > BUFF_EVERY:
            return f"buff older than {BUFF_EVERY:.0f}s", False
        return None, False

    # ---------- not getting flanked ----------
    def flank_axis(self, me, creatures):
        """Which axis, if any, we are being hit on from BOTH sides.

        Taking hits front and back is the worst arrangement: every attacker gets free swings
        and no wall covers anything. On one axis it is fixable in a single step."""
        occ = {(c[1] - me[0], c[2] - me[1]) for c in creatures}
        if (-1, 0) in occ and (1, 0) in occ:
            return "x"
        if (0, -1) in occ and (0, 1) in occ:
            return "y"
        return None

    def unflank_step(self, me, creatures, axis):
        """Step ACROSS the flank, so attackers end up on adjacent sides instead of opposite
        ones -- 'they re-adjust to top and side'. Moving perpendicular to the axis we are
        pinched on does it in one move; the pair that was front-and-back is then beside each
        other, and our back is free."""
        opts = ["up", "down"] if axis == "x" else ["left", "right"]
        best, best_key = None, None
        for d in opts:
            vx, vy = DIRS[d]
            t = (me[0] + vx, me[1] + vy)
            if not self.in_room(t) or self.is_warp(t):
                continue
            if time.time() - self.blocked.get(t, 0) < WALL_TTL:
                continue
            if any((c[1], c[2]) == t for c in creatures):
                continue
            near = len([c for c in creatures
                        if abs(c[1] - t[0]) + abs(c[2] - t[1]) <= 1])
            key = (near, self.openness(t))       # fewest attackers, then most cover
            if best_key is None or key < best_key:
                best, best_key = (d, t), key
        if best and self.step(best[0]):
            self.last_unflank = time.time()
            return best[1]
        return None

    def check_room(self):
        """Same as the base tracker, but the rooms we belong in are the Mythic ones.

        Inherited straight, it judged every room here against the five squirrel rooms and
        announced 'Mythic Waters 1 is not a hunting room' on arrival at our destination."""
        now = time.time()
        if now - self.room_ts < 1.0:
            return
        self.room_ts = now
        try:
            r = self.rt.poll(self.ex)
        except Exception:
            return
        if not r or r == self.room:
            self.room_pending, self.room_pending_n = None, 0
            self.room_xy = self.me()
            return
        # BELIEVING A WRONG ROOM COSTS A FULL CYCLE. Measured twice in run 4: one second
        # after entering Mythic Waters 1 at (14,18) with four hares on screen, the name
        # buffer read back 'Mythic Nexus' -- a 60x60 room with no hares in it. The bot
        # gated out, re-buffed, walked back, and did it again, indefinitely.
        #
        # The tell is physical: a real map change TELEPORTS you, so position jumps. Our
        # position had moved one tile. So accept a new room immediately only when the
        # position also jumped; otherwise make it prove itself over several polls.
        me = self.me()
        jumped = (me is None or self.room_xy is None
                  or abs(me[0] - self.room_xy[0]) + abs(me[1] - self.room_xy[1]) > 3)
        self.room_pending_n = (self.room_pending_n + 1) if self.room_pending == r else 1
        self.room_pending = r
        if not jumped and self.room_pending_n < 3:
            return
        self.room_pending, self.room_pending_n = None, 0
        self.room_xy = me
        prev, self.room = self.room, r
        self.prev_room = prev           # which room the warp tile we just used belongs to
        self.last_progress_ts = time.time()   # fresh no-progress window for the new cave
        self._traverse_to = None
        if self.agent is not None:
            self.agent.zone = r         # every swing/kill row is labelled with WHERE
        # dims BEFORE use_room_nav -- the RTK fallback map is headerless and needs them
        wh = self.rt.names.get(r)
        self.room_wh = (int(wh[1]), int(wh[2])) if wh else None
        self.use_room_nav(r)
        size = f" ({self.room_wh[0]}x{self.room_wh[1]})" if self.room_wh else ""
        log(f"ROOM: {prev or '?'} -> {r}{size}")
        if self.arrived_at and time.time() - self.arrived_ts < 10.0:
            self.note_warp(r, self.arrived_at)

    # ---------- the buff ----------
    def stat_vec(self):
        """Armour and the stat triple, as the server reports them.

        ASV shows up here: 'your armor is strengthened' / 'your muscles develop' move the
        SAME fields that gear does. So the buff wearing off is directly observable -- no
        need to guess a duration (my 15-minute guess was made up, and it dropped well
        inside it)."""
        a = self.agent
        c = getattr(a, "cur", None) if a else None
        if not c:
            return None
        return (a.ac, c.get("might"), c.get("grace"), c.get("will"))

    def buff_is_up(self):
        """False once the stats ASV actually moved fall back to their unbuffed values.

        The first version compared the WHOLE stat vector against a snapshot taken 0.6s after
        casting -- too soon, so it captured AC 51 (unbuffed). When ASV really landed, AC
        became 31, the vector no longer matched, and the bot reported 'buff dropped' and
        gated out of a fight it was winning. Measured, both snapshots from the same run:
            (51, 18, 25, 12)  <- snapshot, buff not applied yet
            (31, 18, 25, 12)  <- actually buffed
        So: take a baseline BEFORE casting, compare after, and watch only the fields that
        genuinely changed. Anything we cannot attribute to the buff is not evidence about it.
        """
        if self.exp_on:
            # The ring experiment MOVES THE SAME FIELDS this check reads: a ring is armour
            # and hit, so every swap would register as the buff changing. re/gear_toggle.py
            # says it outright -- "casting Might looks exactly like equipping a +3 might
            # item" -- and stat deltas cannot separate the two by construction. While the
            # experiment runs, the timer is the only honest buff signal we have.
            return True
        if not (self.buffed_vec and self.unbuffed_vec):
            return True                     # nothing to compare against yet
        now = self.stat_vec()
        if now is None or None in now:
            return True                     # can't read stats -- don't thrash on a bad read
        moved = [i for i in range(len(self.buffed_vec))
                 if self.buffed_vec[i] != self.unbuffed_vec[i]]
        if moved:
            return all(now[i] == self.buffed_vec[i] for i in moved)
        # No field moved across the cast, so we have NO reference for what buffed looks
        # like and cannot answer the question. Say so (the timer renews it) rather than
        # compare the whole vector.
        #
        # Comparing the whole vector here is what gated us out one second after the first
        # kill. Measured: the first tree visit logged "stats unchanged after ASV
        # (42,19,26,12)" -- the buff had not applied -- so 42 was stored as the BUFFED
        # reference. When ASV actually landed, AC dropped 42 -> 32, the vector stopped
        # matching, and "the buff arrived" was reported as "buff dropped". A degenerate
        # reference is not weak evidence about the buff; it is no evidence, and treating
        # it as evidence inverts the signal.
        return True

    # ---------- gear, and the +hit experiment ----------
    def fetch_profile(self, tries=6):
        """Worn items straight from the SERVER (`2d 00 00` -> 0x39). Authoritative: the
        client UI is never consulted and so cannot disagree with what we record."""
        if self.world is None:
            return []
        with self.world.lock:
            self.world.equipment = None
        for i in range(tries):
            try:
                self.ex.sendraw([0x2d, 0x00, 0x00])
            except Exception:
                pass
            if i == 1:
                # sendraw rides the connection handle captured from the client's first
                # send; if none has happened the request silently no-ops. A turn is enough
                # to make it send, and turning is cosmetic -- it does not move us.
                self.press("left")
                time.sleep(0.2)
                self.press("right")
            time.sleep(0.6)
            with self.world.lock:
                eq = self.world.equipment
            if eq:
                return list(eq[2])
        return []

    def note_gear(self, worn):
        """Stamp the loadout onto every row the agent writes from here on.

        This is the experimental label, and it has to come from the profile at the moment
        the swap is verified. The `hit` field in the stat block refreshes on its own
        schedule, not on gear events, so immediately after a swap it still reports the old
        value -- a row that trusted it would be filed under the wrong arm."""
        if not worn or not self.agent:
            return
        self.agent.gear_sig = "|".join(worn)
        self.agent.weapon = worn[0] if worn else ""

    def hit_stat(self):
        return getattr(self.agent, "hit", None) if self.agent else None

    def rings_worn(self, worn=None):
        """How many rings are ON, counted WITH multiplicity.

        Both rings are called 'Black ring', so every membership test about them is a lie:
        with one still on, `'Black ring' in worn` is True and a set-difference reports that
        nothing came off. Gear has to be COUNTED, never tested. (This is not hypothetical --
        it is what the first run did, and it aborted the experiment on a real removal.)"""
        c = collections.Counter(worn if worn is not None else self.fetch_profile())
        return sum(min(c[n], k) for n, k in collections.Counter(self.rings).items())

    def _shift_ctrl(self):
        """A controller for SHIFTED keys, which do not work the way our other input does.

        Shift+Z reaches the spell prompt through frida PostMessage, so I assumed Shift+T
        would too. It does not: the spell prompt reads WM_CHAR, while Shift+T is read as a
        key COMBINATION, and PostMessage never updates real keyboard state -- the client's
        GetKeyState(VK_SHIFT) reads 'up' and drops it. Measured elsewhere in this repo:
        Shift+A was a no-op via frida and fired 19 packets through the foreground path.
        So this one operation needs the window in front. It only happens at swap points."""
        import ctypes
        if getattr(self, "_sctrl", None) is None:
            self._sctrl = NB.Controller(self.hwnd, mode="send")
            self._sctrl.fkey = None          # force the foreground path, not frida
        try:
            ctypes.windll.user32.SetForegroundWindow(self.hwnd)
        except Exception:
            pass
        time.sleep(0.5)
        return self._sctrl

    def unequip(self, slot):
        """Take one item off by slot letter (l/r are the hands). Returns what came off."""
        before = self.fetch_profile()
        if not before:
            log("unequip: profile unreadable -- refusing to touch gear blind")
            return None
        c = self._shift_ctrl()
        c.close_chat(2)
        c.press_char("T")
        time.sleep(0.45)
        c.press_char(slot)
        time.sleep(1.3)
        c.close_chat(1)
        after = self.fetch_profile()
        gone = list((collections.Counter(before) - collections.Counter(after)).elements()) \
            if after else []
        if not gone:
            log(f"unequip {slot!r}: NOTHING came off (still wearing {after})")
        return gone

    def wear_one(self, item):
        """Put ONE more `item` on: 'w', then its CURRENT inventory letter -- read live,
        because letters shift as items move around.

        Deliberately does NOT short-circuit on 'already wearing it'. With two rings of the
        same name that test is true while only one is on, so the second would never go back
        and the arm would silently be wrong."""
        before = self.fetch_profile()
        n0 = before.count(item)
        try:
            letter = INV.letter_of(self.ex, item)
        except Exception as e:
            log(f"wear {item!r}: inventory read failed ({e})")
            return False
        if not letter:
            log(f"wear {item!r}: not in inventory")
            return False
        self.clear_ui()
        time.sleep(0.3)
        self.ex.postchar(self.hwnd, ord("W"), ord("w"), False)
        time.sleep(0.8)
        self.ex.postchr(self.hwnd, ord(letter))
        time.sleep(1.3)
        self.clear_ui()
        ok = self.fetch_profile().count(item) > n0
        log(f"wear {item!r} via slot {letter!r}: {'OK' if ok else 'FAILED'}")
        return ok

    def start_experiment(self):
        """Identify the rings by taking them off and watching what the server says.

        Nothing is configured by name. Whatever leaves a hand slot IS a ring, and the drop
        in `hit` across that removal is that ring's bonus -- measured, not assumed. If it
        does not come out (nothing comes off, or only one ring is found) we put things back
        and hunt without the experiment. A swap that silently changed nothing is how you
        collect an hour of data with no variance in it and only notice afterwards.
        """
        worn0 = self.fetch_profile()
        if not worn0:
            log("experiment: cannot read worn gear -- hunting without it")
            return False
        h0 = self.hit_stat()
        log(f"experiment: starting loadout {worn0}  hit={h0}")
        for slot in ("r", "l"):
            gone = self.unequip(slot)
            if not gone:
                break
            h = self.hit_stat()
            self.rings.extend(gone)
            d = (h0 - h) if (h0 is not None and h is not None) else None
            log(f"experiment: {slot!r} hand held {gone}  hit {h0} -> {h}"
                + (f"   => that ring is +{d} hit" if d is not None else ""))
            h0 = h
        if len(self.rings) < 2:
            log(f"experiment: found {len(self.rings)} ring(s) {self.rings} -- need two to "
                f"run 2/1/0. Putting them back and hunting plain.")
            for r in self.rings:
                self.wear_one(r)
            self.rings = []
            return False
        self.exp_on = True
        self.arm_i = 0
        self.set_arm(RING_ARMS[0])
        return True

    HANDS = ("r", "l")

    def set_arm(self, n):
        """Get to exactly `n` rings worn, and label the data with what we ACTUALLY got.

        Never with what we asked for. If a swap half-lands, saying so and filing the rows
        under the real count keeps the experiment honest; retrying blindly and assuming it
        worked is how two arms get quietly mixed into one meaningless average."""
        have = self.rings_worn()
        for slot in self.HANDS:
            if have <= n:
                break
            self.unequip(slot)
            have = self.rings_worn()
        for name in self.rings:
            if have >= n:
                break
            self.wear_one(name)
            have = self.rings_worn()
        worn = self.fetch_profile()
        self.note_gear(worn)
        self.arm = self.rings_worn(worn)
        h = self.hit_stat()
        if h is not None:
            self.arm_hit[self.arm] = h
        self.arm_kill_mark = self.kills
        if self.arm != n:
            log(f"ARM: wanted {n} ring(s), got {self.arm} (worn {worn}) -- "
                f"rows will say {self.arm}")
        else:
            log(f"ARM: {self.arm} ring(s) on  hit={h}  next swap in {RING_KILLS} kills")
        return self.arm

    def maybe_rotate(self, adj, mobs, me):
        """Swap rings BETWEEN kills, never during one: this foregrounds nothing but it does
        spend several seconds typing at menus, and a hare does not wait."""
        if not self.exp_on or adj:
            return
        if self.kills - self.arm_kill_mark < RING_KILLS:
            return
        if any(abs(m[1] - me[0]) + abs(m[2] - me[1]) <= 2 for m in mobs):
            return                       # something is about to reach us; not now
        self.arm_i = (self.arm_i + 1) % len(RING_ARMS)
        self.set_arm(RING_ARMS[self.arm_i])

    # ---------- swings, and what we log about them ----------
    def swing(self, d, uid):
        """Face it and hit it -- and tell the agent WHAT we are hitting.

        Without the context, on_attack drops the attempt and logs nothing, because a swing
        with no adjacent target is a calibration swing and would feed P(hit) a guaranteed
        miss. swings.csv only ever holds LANDED hits, so with attempts unlogged the miss
        rate leaves no trace anywhere -- and the miss rate is exactly what a +hit ring is
        supposed to move. This one line is what makes the ring experiment measurable.
        """
        me = self.me()
        if me and self.agent is not None and self.target_tile:
            self.stamp_swing(uid, self.target_tile, me, d)
        Simple.swing(self, d, uid)

    FACE_OF = {"up": 0, "right": 1, "down": 2, "left": 3}

    def stamp_swing(self, uid, mob, me, d):
        """One self-contained row per attempt: who, where, and what our stats were.

        Facing comes from the direction we are ABOUT to press, not from the world's tracked
        facing -- the tracker updates off an outgoing packet that has not been sent yet at
        this point, so reading it here reports where we were looking a moment ago.
        """
        ag = self.agent
        ctx = {"eid": uid, "mob": ag.mob_names.get(uid, ""), "zone": ag.zone,
               "self_x": me[0], "self_y": me[1], "mob_x": mob[0], "mob_y": mob[1],
               "facing": self.FACE_OF.get(d, "")}
        dx, dy = mob[0] - me[0], mob[1] - me[1]
        ctx["dist"] = abs(dx) + abs(dy)
        fx, fy = FACE_DELTA.get(self.FACE_OF.get(d, -1), (0, 0))
        ctx["rel_dir"] = 0 if (dx, dy) == (fx, fy) else (
            2 if (dx, dy) == (-fx, -fy) else 1)
        c = ag.cur or {}
        ctx.update(level=c.get("level", ""), might=c.get("might", ""),
                   grace=c.get("grace", ""), will=c.get("will", ""),
                   dam=ag.dam if ag.dam is not None else "",
                   hit_stat=ag.hit if ag.hit is not None else "",
                   ac=ag.ac if ag.ac is not None else "",
                   weapon=ag.weapon, gear=ag.gear_sig)
        with ag.lock:
            ag.swing_ctx = ctx

    # ---------- getting somewhere specific ----------
    def bfs_step(self, me, dst, allow_warp=False):
        """One step along a real path to dst, not just the greedy direction.

        The squirrel rooms were open ground, so stepping toward the target always worked.
        Mythic Nexus is 60x60 with structures in it: walking east from the tree gets six
        tiles and then hits a wall, and a greedy stepper just grinds against it (measured --
        it stalled at (41,9) trying to reach (49,11)).

        Searches over what we have LEARNED: tiles that refused us are walls, everything else
        is assumed open until proven otherwise. Re-run every tick, so each new wall we
        discover reshapes the route immediately.
        """
        if me == dst:
            return None
        now = time.time()
        blocked = {t for t, ts in self.blocked.items() if now - ts < WALL_TTL}
        blocked |= self.blocked.perm            # geometry learned in earlier runs
        openk = self.open_tiles
        rm = self.rmap                          # real walls, when we have them

        # Cheapest-first rather than plain BFS, with tiles we have ALREADY WALKED ON costing
        # a quarter of unknown ground. Plain BFS treats every unvisited tile as equally
        # promising, so it walks hopefully into walls and rediscovers the same corridor every
        # single run -- the route from the north gate to the tree was being re-derived by
        # bumping, every cycle. Weighting known-walkable ground makes the second trip follow
        # the first one. Unknown tiles are still reachable, just not preferred, so a genuinely
        # new route is still found when the known one is gone.
        import heapq
        start_cost = 0
        h = []
        seen = {me: 0}
        for d in DIRS:
            vx, vy = DIRS[d]
            t = (me[0] + vx, me[1] + vy)
            if not self.in_room(t) or t in blocked:
                continue
            # standable(), not solid(): object walls sit on walkable GROUND, so a route
            # checked against the ground flag alone walks straight through the side of a
            # building. (Measured: it happily routed into the sealed pocket at (2,20).)
            if rm is not None and not rm.standable(t[0], t[1]):
                continue
            if not allow_warp and self.is_warp(t) and t != dst:
                continue
            if t == dst:
                return d
            c = start_cost + (1 if t in openk else 4)
            seen[t] = c
            heapq.heappush(h, (c, t[0], t[1], d))
        for _ in range(20000):
            if not h:
                break
            cost, tx, ty, first = heapq.heappop(h)
            if cost > seen.get((tx, ty), 1 << 30):
                continue
            for d in DIRS:
                vx, vy = DIRS[d]
                n = (tx + vx, ty + vy)
                if not self.in_room(n) or n in blocked:
                    continue
                if rm is not None and not rm.standable(n[0], n[1]):
                    continue
                if not allow_warp and self.is_warp(n) and n != dst:
                    continue
                if n == dst:
                    return first
                c = cost + (1 if n in openk else 4)
                if c < seen.get(n, 1 << 30):
                    seen[n] = c
                    heapq.heappush(h, (c, n[0], n[1], first))
        return None

    def next_cave_warp(self, me):
        """Nearest warp tile in THIS room that leads to another CAVE (not the hub), so a
        hunted-out room can be left for a fresh one without gating. None if there is none."""
        import roommap
        mid = roommap.map_ids().get(self.room)
        if mid is None:
            return None
        prev_mid = roommap.map_ids().get(self.prev_room) if self.prev_room else None
        avoid = set(HUB_MAP_IDS) | ({prev_mid} if prev_mid else set())
        dests = roommap.warp_dests(mid)
        cand = [t for t, d in dests.items() if d not in avoid]
        if not cand:                          # only exit is back the way we came -> take it
            cand = [t for t, d in dests.items() if d not in HUB_MAP_IDS]
        if not cand:
            return None
        return min(cand, key=lambda t: abs(t[0] - me[0]) + abs(t[1] - me[1]))

    def path_step(self, me, dst, allow_warp=False):
        """One step toward dst along a REAL path through the map cache, greedy stepper only as
        a last resort. This is what lets us chase a hare on the far side of the central
        structure: toward() alone just shoves at the wall between us and it and never rounds
        the corner, which reads as 'can't path to mobs'. bfs_step walks the passability read
        from TK######.cmp, so it goes around."""
        d = self.bfs_step(me, tuple(dst), allow_warp=allow_warp)
        if d is None:
            return self.toward(me, dst, allow_warp=allow_warp)
        vx, vy = DIRS[d]
        t = (me[0] + vx, me[1] + vy)
        return t if self.step(d) else None

    def walk_to(self, dst, secs=45.0, allow_warp=True, stop_on_room=None):
        """Walk to a tile, pathing around what we learn. Gives up after `secs`."""
        t0 = time.time()
        last, stuck = None, 0
        while time.time() - t0 < secs:
            me = self.me()
            if me is None:
                time.sleep(0.2)
                continue
            if stop_on_room is not None:
                self.room_ts = 0.0
                self.check_room()
                if self.room == stop_on_room:
                    return True
            if (me[0], me[1]) == tuple(dst):
                return True
            d = self.bfs_step(me, tuple(dst), allow_warp=allow_warp)
            if d is None:
                # no route through what we believe -- forget the walls and try afresh
                self.blocked.clear()
                self.fails.clear()
                d = self.bfs_step(me, tuple(dst), allow_warp=allow_warp)
                if d is None:
                    return False
            vx, vy = DIRS[d]
            tile = (me[0] + vx, me[1] + vy)
            issued = self.step(d)
            # step() refuses anything inside the walk cooldown, so sleeping EXACTLY STEP_GAP
            # gets about half the presses swallowed. Sleep past it, and never count a press
            # that was never sent as a wall -- doing so invented walls until pathing gave up
            # (measured: it never left the tree's tile).
            time.sleep(STEP_GAP + 0.06)
            if not issued:
                continue
            now = self.me()
            if now == me:                        # genuinely did not move -> learn it
                stuck += 1
                n = self.fails.get(tile, 0) + 1
                self.fails[tile] = n
                if n >= 2:
                    self.blocked[tile] = time.time()
                    # only worth a scan on the rare failure, and it keeps a hare standing in
                    # a doorway from being recorded as a permanent wall
                    try:
                        self.learn_wall(tile, self.entities()[2])
                    except Exception:
                        pass
            else:
                stuck = 0
                self.note_open(now)          # remember the route we are actually walking
                self.blocked.unwall(now)
            if stuck in (6, 14):
                # Not moving at all is far more often a dialog or chat box eating the keys
                # than four walls. Try dismissing before believing the geometry.
                self.clear_ui()
            if stuck > 25:
                log(f"walk_to({dst}): wedged at {now}")
                return False
        log(f"walk_to({dst}): out of time at {self.me()}")
        return False

    # ---------- the tree ----------
    def be32(self, v):
        return [(v >> 24) & 0xff, (v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff]

    def tree_click(self, uid):
        return self.ex.sendraw([0x43, 0x01] + self.be32(uid) + [0x00])

    def tree_option(self, uid, n):
        return self.ex.sendraw([0x3a, 0x01] + self.be32(uid)
                               + [0x00, 0x00, 0x00, 0x02, 0x01, n, 0x00])

    def cast_all_self_buffs(self):
        """Cast any self-buff that is DOWN (the hub routine). Recasting one that is still up is
        rejected by the client ("Flank had 30s left") and wastes mana, so this only touches
        the ones that have actually expired -- at start of run that is all four, and on a
        later hub trip usually none (the field upkeep keeps them lit)."""
        now = time.time()
        cast = []
        for b in SELF_BUFFS:
            if now - self.buff_cast.get(b["name"], 0.0) < b["dur"] + SELF_BUFF_MARGIN:
                continue
            self.clear_ui()
            self.cast(b["letter"])
            self.buff_cast[b["name"]] = time.time()
            cast.append(b["name"])
            time.sleep(SPELL_GAP)
        log("self-buffs cast: " + ", ".join(cast) if cast
            else "self-buffs all still up -- none recast at hub")

    def _tree_opt(self, uid, n):
        """One NPC menu pick: click, choose option n, then dismiss the dialog client-side.
        The pick goes straight to the server, so the client never closes the menu on its own
        and an open menu swallows every arrow key -- clear_ui is what frees us to walk."""
        self.tree_click(uid)
        time.sleep(TREE_GAP)
        self.tree_option(uid, n)
        time.sleep(TREE_GAP + 0.4)
        self.clear_ui()
        time.sleep(0.15)

    def upkeep_self_buffs(self, me):
        """Recast an expired self-buff on the user's timer -- EVEN WHEN SURROUNDED. Bless
        (375s) drops far more often than a gate cycle, and the user wants it back on schedule,
        not only when the room happens to clear ("it's okay to be surrounded"). Taking a hit
        during the ~0.5s cast is fine; the heal ran earlier this tick. Skipped only if mana is
        on the heal floor. One recast per call, most-overdue first."""
        v = self.vitals()
        if v and v[3] and v[2] < SELF_BUFF_MANA_FLOOR * v[3]:
            return False
        now = time.time()
        due = [b for b in SELF_BUFFS
               if now - self.buff_cast.get(b["name"], 0.0) >= b["dur"] + SELF_BUFF_MARGIN]
        if not due:
            return False
        b = max(due, key=lambda b: now - self.buff_cast.get(b["name"], 0.0))
        self.clear_ui()
        self.cast(b["letter"])
        self.buff_cast[b["name"]] = now
        log(f"re-buff {b['name']} ({b['letter']}) -- expired in the field, recast")
        return True

    def visit_tree(self):
        """heal/mana -> self-buff -> heal/mana -> ASV. Each option needs its own click; the
        menu is dismissed once an option is taken."""
        t = self.find_by_look(TREE_LOOK)
        if not t:
            self.find_regions()
            t = self.find_by_look(TREE_LOOK)
        if not t:
            # Entities only exist in memory once they are in the client's view, so from the
            # far side of the map the tree simply is not there to find. Walk to where we
            # know it stands and look again -- measured: at (49,13) the only entity in all
            # 16 regions was ourselves.
            log(f"tree not in view -- walking to {TREE_XY}")
            self.walk_to((TREE_XY[0], TREE_XY[1] + 1), secs=60.0, allow_warp=False)
            self.find_regions()
            t = self.find_by_look(TREE_LOOK)
        if not t:
            log("tree not visible -- cannot restore")
            return False
        uid, tx, ty = t
        self.tree_uid = uid
        me = self.me()
        # the server checks proximity (radius 10) before it will run the NPC script
        if me and abs(me[0] - tx) + abs(me[1] - ty) > 4:
            self.walk_to((tx, ty + 1), secs=40.0, allow_warp=False)
        v0 = self.vitals()
        # Baseline, BEFORE the buff is cast -- but only if we are actually unbuffed. Visiting
        # the tree while ASV is still up records a BUFFED vector as the baseline, the two
        # snapshots then match, and buff_is_up() loses its reference entirely ("stats
        # unchanged after ASV"). Keep the last known genuinely-unbuffed reading instead.
        pre = self.stat_vec()
        if pre and (self.buffed_vec is None or pre != self.buffed_vec):
            self.unbuffed_vec = pre
        # The hub routine, in order: heal/mana, cast our own buffs, heal/mana again (the
        # buffs just spent it), then ASV last so its stat move is the freshest reference for
        # buff_is_up(). Each pick goes to the server, which leaves the dialog open on screen,
        # so _tree_opt dismisses it every time -- an open menu eats the arrow keys.
        self._tree_opt(uid, 2)               # heal + mana
        self.cast_all_self_buffs()           # Bless / Flank / Tiger's Fury / Backstab
        self._tree_opt(uid, 2)               # refill the mana the buffs cost
        self._tree_opt(uid, 3)               # ASV
        self.clear_ui()
        # The last option-2 sometimes does not take, and we walk in on partial mana -- one
        # cycle left the tree at 457/677 where the others ended near full. Mana IS the heal
        # supply here, so check it rather than assume the dialog landed.
        for _ in range(4):
            v = self.vitals()
            if not (v and v[3]) or v[2] >= 0.90 * v[3]:
                break
            log(f"tree: mana only {v[2]}/{v[3]} -- asking again")
            self.tree_click(uid)
            time.sleep(TREE_GAP)
            self.tree_option(uid, 2)
            time.sleep(TREE_GAP + 0.4)
            self.clear_ui()
            time.sleep(0.2)
        v1 = self.vitals()
        self.last_buff = time.time()
        # Remember what we look like BUFFED; buff_is_up() watches for these falling back.
        # Wait properly for the stats to move -- 0.6s was not enough and the snapshot caught
        # us still unbuffed, which then read as "the buff dropped" a minute later.
        for _ in range(20):
            time.sleep(0.25)
            vec = self.stat_vec()
            if vec and self.unbuffed_vec and vec != self.unbuffed_vec:
                break
        self.buffed_vec = self.stat_vec()
        if self.buffed_vec == self.unbuffed_vec:
            log(f"WARNING: stats unchanged after ASV {self.buffed_vec} -- "
                f"cannot detect this buff dropping")
        if v0 and v1:
            log(f"tree 2>3>2: hp {v0[0]} -> {v1[0]}/{v1[1]}, mana {v0[2]} -> {v1[2]}/{v1[3]}"
                f"  buffed stats {self.buffed_vec}")
        return True

    # ---------- transit ----------
    def cast_gateway(self):
        """Gateway is an INPUT spell: Shift+Z -> b -> <clear> -> N -> Enter.

        The backspaces are not paranoia, they are the whole reason this used to fail. We
        inject with PostMessage, which queues WM_KEYDOWN and WM_CHAR as two independent
        messages. The client handles the keydown by opening the gate-name input box, and
        our WM_CHAR for 'b' then lands INSIDE that box -- so the field reads "bN" and the
        server gets a cast for a gate that does not exist. A real keypress does not do this:
        Windows makes the WM_CHAR inline while the keydown is being dispatched, so the spell
        menu eats the pair before the input field exists.

        The symptom was maddening precisely because the cast itself was fine: 0x0f went out
        every time, so everything looked correct and nothing moved. With the field cleared:
        (45,13) -> (30,11), "#You have arrived at the North gate".

        Keys are a full second apart. Tighter spacing (0.2s) was measured failing.
        The Enter belongs here and ONLY here -- on a simple spell it opens the chat box.
        """
        self.clear_ui()
        time.sleep(0.5)
        self.ex.postchar(self.hwnd, ord("Z"), ord("Z"), True)
        time.sleep(1.0)
        self.ex.postchar(self.hwnd, ord("B"), ord("b"), False)
        time.sleep(1.0)
        for _ in range(4):                       # erase the stray 'b' from the prompt
            self.ex.postchar(self.hwnd, 0x08, 0x08, False)
            time.sleep(0.35)
        time.sleep(0.7)
        # WM_CHAR only -- postchar() would also post a keydown, and the client translates
        # that into a second, lowercase character, so the field read "nN" instead of "N".
        self.ex.postchr(self.hwnd, ord("N"))
        time.sleep(1.0)
        self.ex.postchar(self.hwnd, 0x0D, 0x0D, False)
        time.sleep(1.2)

    def wait_room(self, name, secs=12.0):
        t0 = time.time()
        while time.time() - t0 < secs:
            self.room_ts = 0.0
            self.check_room()
            if self.room == name:
                return True
            time.sleep(0.3)
        return False

    def go_hunt(self):
        """Mythic Nexus -> (49,11) -> Mythic Gateway -> one step north -> Mythic Waters 1."""
        log(f"walking to {TRANSIT_XY}")
        # Real pathing, not greedy stepping: the last attempt walked six tiles east of the
        # tree, hit a structure, and ground against it until it gave up at (41,9).
        self.walk_to(TRANSIT_XY, secs=60.0, allow_warp=True, stop_on_room=GATEWAY_ROOM)
        if not self.wait_room(GATEWAY_ROOM, 8.0):
            log(f"expected {GATEWAY_ROOM}, got {self.room!r}")
            return False
        log(f"in {GATEWAY_ROOM} -- stepping north")
        for _ in range(12):
            self.step("up")
            time.sleep(0.35)
            self.room_ts = 0.0
            self.check_room()
            if self.room == HUNT_ROOM:
                self.hold = None
                self.cover_logged = False
                self.last_mob_ts = time.time()
                log(f"arrived in {HUNT_ROOM}")
                return True
        return self.room == HUNT_ROOM

    def restore_and_return(self, why, forced=True):
        """The whole recovery: leave, top up, buff, come back.

        `forced` says whether something is driving us out. Renewing a buff can wait the few
        seconds it takes to finish a hare that is nearly dead; being surrounded cannot."""
        self.gates += 1
        log(f"GATE OUT #{self.gates} ({why})")
        if not forced and self.room == HUNT_ROOM:
            self.finish_target()
        self.cast_gateway()
        if not self.wait_room(NEXUS_ROOM, 12.0):
            log(f"gateway did not land us in {NEXUS_ROOM} (room={self.room!r}) -- retrying")
            self.cast_gateway()
            if not self.wait_room(NEXUS_ROOM, 12.0):
                return False
        self.blocked.clear()
        self.fails.clear()
        self.seen.clear()
        self.goal = None
        self.visit_tree()
        # Gear swapping belongs HERE, in the safe room, once. Doing it in the cave means
        # standing at a menu for twenty seconds while hares walk up.
        if self.exp_want and not self.exp_tried:
            self.exp_tried = True
            self.start_experiment()
        return self.go_hunt()

    # ---------- stopping ----------
    def finish_target(self):
        """Kill whatever is nearly dead before walking away from it.

        The time limit fired mid-fight and the bot gated out leaving a hare at 5%, throwing
        away ~90 connects of work -- and it had the number the whole time: the agent tracks
        mob HP percent per entity from the damage packets. Only for UNFORCED exits (time
        limit, STOP, buff renewal). When we are surrounded or losing the damage race, leaving
        immediately is the entire point and lingering is how characters die.
        """
        mh = getattr(self.agent, "mobhp", None) or {}
        if not mh:
            return
        t0, swung = time.time(), 0
        while time.time() - t0 < FINISH_SECS:
            me = self.me()
            v = self.vitals()
            if me is None or not (v and v[1]):
                return
            if v[0] / float(v[1]) < FINISH_BAIL:
                break                                  # not worth dying for
            mobs, _, creatures = self.entities()
            if len(self.adjacent(me, creatures)) >= SURROUNDED:
                break                                  # leaving matters more now
            hurt = [m for m in mobs
                    if abs(m[1] - me[0]) + abs(m[2] - me[1]) <= 1
                    and 0 < mh.get(m[0], 999) <= FINISH_HP]
            if not hurt:
                break
            uid, mx, my = min(hurt, key=lambda m: mh.get(m[0], 999))
            d = ("right" if mx > me[0] else "left") if mx != me[0] else \
                ("down" if my > me[1] else "up")
            self.target_tile = (mx, my)
            self.swing(d, uid)
            swung += 1
            time.sleep(SWING_GAP)
        if swung:
            log(f"finished off a low-HP hare before leaving ({swung} swings)")

    def safe_exit(self, why):
        """NEVER stop inside Mythic Waters. Gate out first, then end.

        This is the lesson from the death: in the squirrel rooms stopping the bot is
        harmless because squirrels do not chase, so killing it mid-fight was a habit with
        no cost. Here the bot IS the character's healing, and the hares keep hitting after
        it goes away. Stopping in the cave is not stopping -- it is walking away from a
        fight that continues. So every exit path leaves through the gate.
        """
        self.room_ts = 0.0
        self.check_room()
        if self.room != HUNT_ROOM:
            log(f"{why} -- already out (room={self.room!r})")
            return True
        log(f"{why} -- gating out before we stop")
        self.finish_target()            # nothing is chasing us out; don't waste a near-kill
        for _ in range(3):
            self.heal_to(TARGET_HP, MAX_BURST_EMERG)     # survive the cast time
            self.cast_gateway()
            if self.wait_room(NEXUS_ROOM, 10.0):
                log(f"out safely, in {self.room!r}")
                return True
        log("COULD NOT GATE OUT -- character is still in the cave, take manual control")
        return False

    # ---------- main ----------
    def run(self, deadline=None, gate_first=True):
        self.find_regions()
        self.check_room()
        log(f"starting in {self.room!r}")
        # Label the data with what we are WEARING, always -- not only when the ring
        # experiment is driving. Measured mid-run: 235 attempt rows with gear='' and
        # weapon='', i.e. 235 rows that cannot say which loadout produced them. That is
        # invisible until someone tries to compare two arms and finds both unlabelled.
        worn = self.fetch_profile()
        if worn:
            self.note_gear(worn)
            log(f"loadout: {worn}  (hit={self.hit_stat()} ac={getattr(self.agent,'ac',None)})")
        else:
            log("WARNING: could not read worn gear -- rows will not carry a loadout")

        if gate_first or self.room != HUNT_ROOM:
            if not self.restore_and_return("start of run"):
                self.check_room()
                if self.room != HUNT_ROOM:
                    log("could not reach the hunting ground -- stopping")
                    return
                # We are in the cave anyway. Dropping out now would leave the character
                # standing in it unhealed, which is the one outcome worth avoiding.
                log("start-of-run gate failed but we are in the cave -- running the loop")
                self.gate_fail_ts = time.time()

        last_pos, stepped_into, last_print = None, None, 0.0
        while True:
            if deadline and time.time() > deadline:
                self.safe_exit("time limit reached")
                return
            if os.path.exists(P_STOP):
                os.remove(P_STOP)
                # GATE OUT BEFORE RELEASING. This is a hostile cave: dropping the controls
                # here leaves the character to be beaten to death -- which happened once, and
                # must never happen again. safe_exit casts Gateway and confirms we reached the
                # hub before we detach, so a STOP always ends with the character somewhere safe.
                log("STOP -- gating out to safety before releasing control")
                self.safe_exit("STOP requested")
                return

            me = self.me()
            if me is None:
                time.sleep(0.3)
                continue
            self.mark_seen(me)
            self.check_room()
            self.flush_data()

            if self.room in HUB_ROOMS:
                # Back at the HUB (gated out, or a transit dumped us here). Top up + buff and
                # return to a hunting cave. Being in a DIFFERENT cave is NOT a reason to leave:
                # the user wants us to traverse the caves and keep hunting, not gate every time
                # the room changes.
                log(f"at hub ({self.room!r}) -- restoring buffs and returning to a cave")
                if not self.restore_and_return("at hub"):
                    self.heal_to(TARGET_HP, MAX_BURST_EMERG)
                    time.sleep(2.0)
                last_pos = None
                continue

            if stepped_into:
                if last_pos == me:
                    n = self.fails.get(stepped_into, 0) + 1
                    self.fails[stepped_into] = n
                    if n >= 2:
                        self.blocked[stepped_into] = time.time()
                        self.learn_wall(stepped_into, self.last_creatures)
                else:
                    self.fails.pop(stepped_into, None)
            stepped_into = None
            last_pos = me
            self.blocked.unwall(me)      # standing on it settles the question
            self.note_open(me)           # ...and proves it walkable, for every future run
            self.visited[me] = time.time()      # sweep coverage: where we have just been
            self.save_nav()

            mobs, items, creatures = self.entities()
            self.last_creatures = creatures
            self.learn_self_uid(me, creatures, mobs)

            # ONE WORLD, ONE FRAME.
            #
            # Two different client-memory structures describe where we are: the player block
            # (SELF_PTR_ADDR + x/y, which me() reads) and our own record in the ENTITY POOL
            # -- the array the client renders from. They disagree, and every distance the
            # fight logic computes was mixing them: mob positions out of the pool, our
            # position out of the player block. Two symptoms, opposite signs, same cause:
            #   * our own pool record read as an ATTACKER at distance 1
            #   * adj=0 while HP fell ~100 per tick with two hares on us (10:50 run 9)
            # Whatever the client draws is the truth, and it draws the pool -- so anything
            # entity-relative is measured pool-to-pool. The player block is still what the
            # MAP was validated against (93/94), so pathing and cover keep using it.
            me_ent = None
            if self.self_uid is not None:
                for c in creatures:
                    if c[0] == self.self_uid:
                        me_ent = (c[1], c[2])
                        self.me_pool, self.me_pool_ts = me_ent, time.time()
                        break
                # NEVER FALL BACK TO THE OTHER FRAME.
                #
                # Our own record is not in every scan, and silently reverting to the player
                # block on those ticks makes the position source ALTERNATE between two
                # frames a tile apart. That is worse than consistently using the wrong one:
                # on the reverted ticks an adjacent hare computes as two away and melee
                # never fires. Measured at (11,6)/(11,7) -- the log printed the player-block
                # tile while the frame check showed the pool a tile off, HP sawtoothing,
                # nothing being hit.
                #
                # A character walks one tile at a time, so the last pool reading is at worst
                # one step stale -- far better than a reading from a different frame.
                if me_ent is None:
                    self.pool_miss += 1
                    if self.me_pool is not None and time.time() - self.me_pool_ts < 3.0:
                        me_ent = self.me_pool
                else:
                    self.pool_hit += 1
            if me_ent and me_ent != me and time.time() - self.last_frame_log > 5.0:
                self.last_frame_log = time.time()
                log(f"frame check: player-block says {me}, our entity says {me_ent} "
                    f"(delta {abs(me_ent[0]-me[0]) + abs(me_ent[1]-me[1])})")
            # THE POOL IS THE POSITION. Not just for combat -- for everything.
            #
            # Splitting them ("combat in the pool, pathing in the player block") deadlocked
            # the bot completely. Measured at (17,19)/(17,20), stationary, delta 1 held for
            # 20+ seconds -- so this is an OFFSET, not the movement lag I assumed:
            #     player block -> (17,19), 4 sides -> "I need cover"
            #     pool         -> (17,20), 2 sides -> we are already ON cover
            #     best_cover((17,19)) == (17,20)
            # It spent every tick stepping toward the tile it was already standing on, and
            # because cover-seeking outranks melee it never swung: stood in a corner being
            # chewed on, healing, forever. The player reported the tile as a corner, which
            # is 2 sides -- the pool's reading, not the player block's.
            #
            # One frame for the whole tick. Everything below -- cover, openness, pathing,
            # sweep, bookkeeping -- uses the same position the mobs are measured against.
            if me_ent:
                me = me_ent
            fight_me = me
            v = self.vitals()
            if not (v and v[1]):
                time.sleep(LOOP)
                continue
            frac = v[0] / float(v[1])
            mana = (v[2] / float(v[3])) if v[3] else 0.0
            self.hp_hist.append((time.time(), v[0]))
            adj = self.adjacent(fight_me, creatures)

            # --- SURVIVAL, in priority order -----------------------------------
            # 1. hurt -> heal, and keep fighting. FIRST, so that the decision below is made
            #    on HP we could not fix rather than HP we simply had not fixed yet.
            if frac < HEAL_HARD and mana >= MANA_FLOOR \
                    and time.time() - self.last_heal > HEAL_CD:
                cap = MAX_BURST_EMERG if frac < EMERG_HP else MAX_BURST
                got = self.heal_to(TARGET_HP, cap)
                nv = self.vitals()
                if got and nv:
                    log(f"Soothe x{got}: hp {v[0]} -> {nv[0]}/{nv[1]} "
                        f"({nv[0]/float(nv[1]):.0%})")
                    v, frac = nv, nv[0] / float(nv[1])

            # 2. THE decision to leave, in one place. Anything it does not name is a fight.
            why, forced = self.gate_reason(adj, frac)
            if why and time.time() - self.gate_fail_ts > GATE_RETRY:
                if self.restore_and_return(why, forced=forced):
                    last_pos = None
                    continue
                # The gate did not land. Returning here is exactly what stranded the
                # character: run() ended, frida detached, and the hares kept hitting
                # someone with nobody healing them. Stay in the loop -- fighting and
                # healing beats standing still -- and try the gate again shortly.
                self.gate_fail_ts = time.time()
                log(f"gate failed ({why}) -- fighting on, retry in {GATE_RETRY:.0f}s")

            # 3. hit from BOTH sides of one axis -> step across it, so the pair ends up
            #    beside each other instead of front-and-back. One move, then keep swinging.
            if len(adj) >= 2 and time.time() - self.last_unflank > UNFLANK_CD:
                ax = self.flank_axis(me, creatures)
                if ax:
                    t = self.unflank_step(me, creatures, ax)
                    if t:
                        log(f"flanked on {ax} by {len(adj)} -- stepping across to {t}")
                        stepped_into = t
                        time.sleep(LOOP)
                        continue

            # Keep buffs up ON THE TIMER, surrounded or not. The user wants them recast on the
            # schedule they gave -- waiting for a clear moment never comes when we are meant to
            # be fighting surrounds, which is exactly why the bot ended up fighting unbuffed.
            # The heal already ran this tick, so only hold off while still genuinely hurt.
            if frac >= 0.6 and self.upkeep_self_buffs(me):
                time.sleep(LOOP)
                continue

            # STUCK? No melee landed in EMPTY_TRAVERSE seconds -> the room is empty, or its
            # hares are walled off / phantom (a flickering edge mob that keeps resetting the
            # 'saw a mob' clock while we never actually reach it -- the oscillation you saw).
            # Cross to the next cave instead of jittering in place. Only when nothing is on us.
            if not adj and time.time() - self.last_progress_ts > EMPTY_TRAVERSE:
                nw = self.next_cave_warp(me)
                if nw:
                    d = self.bfs_step(me, nw, allow_warp=True)
                    if d:
                        if self._traverse_to != nw:
                            self._traverse_to = nw
                            log(f"{self.room}: no melee progress {EMPTY_TRAVERSE:.0f}s -- "
                                f"crossing to the next cave via warp {nw}")
                        vx, vy = DIRS[d]
                        t = (me[0] + vx, me[1] + vy)
                        stepped_into = t if self.step(d) else None
                        last_pos = me
                        time.sleep(LOOP)
                        continue

            # --- fight ----------------------------------------------------------
            huntable = [m for m in mobs if not self.is_warp((m[1], m[2]))]
            # ANYTHING IN CONTACT IS THE TARGET, full stop.
            #
            # Two ways the bot ended up standing next to a hare without swinging, both seen
            # live: (1) self.target is sticky, so while it held a mob that had wandered off,
            # tgt_dist was > 1 and the melee branch never fired -- it just stood there
            # healing; (2) `huntable` drops mobs standing on tiles we believe are warps, and
            # earlier runs learned warps that were not real, which can empty the list with a
            # hare in our face. Swinging does not require stepping anywhere, so neither
            # filter has any business suppressing an attack on something already adjacent.
            # TARGETING IS DERIVED, NEVER REMEMBERED.
            #
            # This used to be a sticky lock: pick a target, then keep it until it vanished
            # for 2.5s. The invalidation was about the TARGET, never about us -- so a hare
            # standing four tiles away and staying visible held the lock indefinitely while
            # a different one chewed on our flank, and the melee branch (tgt_dist <= 1)
            # never fired. Result: 76 heals, 1 kill, and a loop with no way back to reality
            # because nothing in it was re-derived from what we could see.
            #
            # A target is observable every single tick, so caching it buys nothing and costs
            # exactly this. Contact first, then nearest; a swing needs no path, so neither
            # the warp filter nor anything else may suppress hitting what is already on us.
            mh = getattr(self.agent, "mobhp", None) or {}
            contact = [m for m in mobs
                       if abs(m[1] - fight_me[0]) + abs(m[2] - fight_me[1]) <= 1]
            if contact:
                # BACK TO THE WALL. You face whatever you swing at, so choosing the target
                # chooses what is behind you. Facing the mob whose OPPOSITE side is solid
                # puts our back against the wall, and nothing can stand there to hit it.
                #
                # With three on us and one wall, this is the difference between taking three
                # frontal attackers and taking two plus one in the back. Picking purely by
                # lowest HP ignored the geometry and often turned us to face into the room.
                # Wounded-first still decides between equally-covered targets, so we keep
                # finishing what is nearly dead.
                tgt = min(contact,
                          key=lambda m: (not self.back_to_wall(fight_me, m),
                                         mh.get(m[0], 999)))
            elif huntable:
                tgt = min(huntable,
                          key=lambda m: abs(m[1] - fight_me[0]) + abs(m[2] - fight_me[1]))
            else:
                tgt = None
            self.target = tgt[0] if tgt else None
            self.target_pos = (tgt[1], tgt[2]) if tgt else None
            self.target_seen = time.time()

            alive = {u for u, _, _ in mobs}
            # COUNT kills from the agent, which only records one when a packet reports the
            # mob at hp 0. The old counter inferred death from the entity leaving the scan --
            # and a scan blip looks exactly like a death, which is why two kills kept getting
            # logged in the same second on the same tile while kills.csv (damage summing to
            # ~2700, a hare's vita) recorded them minutes apart.
            kc = getattr(self.agent, "kill_count", None)
            if kc is not None and kc > self.kills:
                for _ in range(kc - self.kills):
                    self.kills += 1
                    log(f"KILL #{self.kills} at {self.target_tile}"
                        + (f"  [{self.arm} ring(s), kill "
                           f"{self.kills - self.arm_kill_mark}/{RING_KILLS}]"
                           if self.exp_on else ""))
            self.maybe_rotate(adj, mobs, me)
            # The vanish heuristic still decides where to go STAND for loot -- being wrong
            # there just means walking onto a tile with nothing on it.
            if (self.hit_uid is not None and self.hit_uid not in alive
                    and time.time() - self.hit_ts <= KILL_WINDOW):
                if self.target_tile:
                    self.collect = (self.target_tile[0], self.target_tile[1],
                                    time.time() + 10)
                self.hit_uid = None
                if self.target not in alive:
                    self.target, self.target_pos = None, None

            if self.pickup_uid is not None and time.time() - self.pickup_ts > 0.6:
                if any(u == self.pickup_uid for u, _, _ in items):
                    n = self.no_loot.get(self.pickup_uid, 0) + 1
                    self.no_loot[self.pickup_uid] = n
                else:
                    self.looted += 1
                    log(f"picked up a drop  (total {self.looted})")
                self.pickup_uid = None

            # Drops are pool entities too, so "is it under my feet" is a pool-to-pool
            # question. Asking it with the player-block position is how the bot kept
            # walking to a drop and never picking it up (run 6: kills=2, looted=1, with
            # the drop still on the ground).
            occupied = {(c[1], c[2]) for c in creatures if (c[1], c[2]) != fight_me}
            cands = sorted((i for i in items
                            if self.no_loot.get(i[0], 0) < 2
                            and (i[1], i[2]) not in occupied
                            and not self.is_warp((i[1], i[2]))),
                           key=lambda i: abs(i[1] - fight_me[0]) + abs(i[2] - fight_me[1]))
            here = next((i for i in cands if (i[1], i[2]) == fight_me), None)
            tgt_dist = (abs(tgt[1] - fight_me[0])
                        + abs(tgt[2] - fight_me[1])) if tgt else None
            if self.collect and time.time() > self.collect[2]:
                self.collect = None

            # --- roam to find them, but fight from cover -------------------------
            # The room has to be walked to find hares, so this is not about living on one
            # tile. It is about WHERE the fight happens: as soon as something is inbound,
            # put a wall behind us, then stand and swing.
            if mobs:
                self.last_mob_ts = time.time()
            here_sides = self.openness(me)
            if not self.cover_logged and self.rmap is not None:
                self.cover_logged = True
                log(f"cover: standing on a {here_sides}-side tile; "
                    f"best nearby {self.cover_list[:4]}")
            incoming = [m for m in mobs
                        if abs(m[1] - fight_me[0])
                        + abs(m[2] - fight_me[1]) <= ENGAGE_RANGE]
            # WAITING IS ONLY A STRATEGY WHILE THINGS ARE ACTUALLY ARRIVING.
            # `incoming` is straight-line distance, so a hare five tiles away with a wall
            # between it and us counts as inbound and we hold cover for something that can
            # never reach us. Observed live. If nothing has touched us in HOLD_PATIENCE
            # seconds, stop waiting and go to it -- cover is for fighting, not for sitting.
            if adj:
                self.last_contact = time.time()
            stalled = (self.last_contact > 0
                       and time.time() - self.last_contact > HOLD_PATIENCE)
            if stalled and not self.stall_logged:
                self.stall_logged = True
                log(f"nothing has reached us in {HOLD_PATIENCE:.0f}s at {me} -- going hunting")
            elif not stalled:
                self.stall_logged = False

            # COVER IS DERIVED TOO. best_cover() is a pure function of where we are and what
            # the map says, and it already returns None when nothing beats our current tile
            # -- so it is self-stabilising and needs no remembered commitment. Storing one
            # only created a second thing that could disagree with the world, which is how
            # `hold = me` pinned us to a 4-side tile the moment two hares arrived.
            here_ok = here_sides <= COVER_MAX
            self.hold = None if (here_ok or not (incoming or adj)) else self.best_cover(me)

            # ONE adjacent hare does not pin us. It hits for ~40 while we walk and Soothe
            # answers for ~50, so dragging it to cover is close to free and it arrives
            # somewhere it can only reach us from one side.
            #
            # Requiring `not adj` here is what made the first run useless: with seven hares
            # roaming, something is adjacent essentially always, so the "free moment before
            # contact" this was waiting for never came. It fought on a 4-side tile at (5,10)
            # until four of them closed and it had to gate -- while (5,20), a 1-side tile,
            # was five steps away and already top of its own cover list.
            # What stops us moving is BEING ON GOOD GROUND, not merely being in contact.
            # Pinning on contact alone froze us at (16,18) -- a 4-side tile -- the instant
            # two hares arrived, which is the exact position that becomes a 4-surround a few
            # seconds later. Standing still on bad ground is not holding a line, it is
            # waiting to be surrounded. Above two attackers moving genuinely is worse, and
            # by then gate_reason is about to make the decision anyway.
            # `not stalled` is load-bearing. Without it, deciding to go hunting and deciding
            # to take cover cancel each other every tick: stalled sends us off cover toward
            # a hare, the tile we land on is worse ground, cover-seeking sends us straight
            # back, patience expires again. Measured live -- (11,14) <-> (11,13) forever,
            # with four hares standing around that never reached us.
            #
            # Third time tonight two branches have undone each other's work (loot vs cover,
            # then hunt vs cover). Whenever a new goal is added here, the question to ask is
            # not "is this right on its own" but "what does this fight with".
            # Repositioning is allowed WHILE IN CONTACT. This is the whole doctrine: you do
            # not fight on open ground just because that is where the mob met you. One or
            # two hares hit for ~40 while we take a step and Soothe answers for ~50, so the
            # trade is close to free -- and staying on a 4-side tile with two on us is
            # precisely how a third and fourth arrive.
            #
            # It cost run 10: the bot sat on (7,17), a 4-side tile, with three on it,
            # displaying `->(5,20)` -- a reachable 1-side tile five steps away -- and never
            # took a single step, because melee-first fired every tick and the movement
            # branch below it was unreachable while anything was adjacent.
            #
            # Guards: only from bad ground to better, never with 3+ on us (moving then is a
            # real loss), and never if we have been issuing steps that do not land -- in
            # which case fighting beats shuffling.
            # How long have we been WANTING cover without getting there? Measured in time,
            # not ticks: the walk cooldown swallows most presses, so a tick counter that
            # only advances on an issued step resets constantly and never trips. That is
            # exactly how run 13 stood on (11,6) for 45 seconds and took ZERO swings --
            # cover-seeking outranks melee, the move never landed, and the guard meant to
            # notice never fired. Wanting to move is not a reason to stop fighting.
            if me != self.cover_from:
                self.cover_from, self.cover_since = me, time.time()
            cover_stuck = time.time() - self.cover_since > COVER_GIVEUP

            # COVER-HUGGING DISABLED. The user runs this buffed and wants to fight in the open:
            # "you don't need to hug walls anymore, it's okay to be surrounded." We never
            # divert to a cover tile -- we go to the mobs and swing.
            need_cover = False
            # log dedupe only -- says nothing about what we do next
            if here_ok and adj and self.reached != me:
                self.reached = me
                log(f"IN COVER at {me} -- {here_sides} side(s) exposed, "
                    f"{len(adj)} on us, {len(mobs)} hares in the room")
            elif not here_ok:
                self.reached = None

            # DIAGNOSTIC: something is touching us and we are NOT about to hit it. Every
            # explanation I have offered for this was a guess that turned out wrong, so
            # record the facts instead: what is adjacent, what look it has, whether it is
            # in our huntable set, and what we intend to do instead. Rate-limited.
            if adj and not (tgt is not None and tgt_dist <= 1) \
                    and time.time() - self.last_idle_log > 3.0:
                self.last_idle_log = time.time()
                looks = []
                for c in adj:
                    ishare = any(m[0] == c[0] for m in mobs)
                    looks.append(f"uid={c[0]} at {(c[1], c[2])} "
                                 f"{'HARE' if ishare else 'NOT-A-HARE'}")
                log(f"NOT SWINGING with {len(adj)} in contact at {me}: [{'; '.join(looks)}]"
                    f" tgt={self.target} tgt_dist={tgt_dist} need_cover={need_cover} "
                    f"hold={self.hold} here_sides={here_sides}")

            # TAKE THE WALL FIRST, THEN FIGHT.
            #
            # `need_cover` is only true on bad ground with somewhere better close by, at
            # most two attackers, and steps that are actually landing -- so this cannot
            # deadlock into the never-swinging failure. Everywhere else, contact means we
            # swing. The invariant that matters is not "always swing"; it is NEVER IDLE
            # while something is on us: either a swing or a step, every tick.
            if need_cover:
                d = self.bfs_step(me, self.hold, allow_warp=False)
                stepped_into = None
                if d:
                    vx, vy = DIRS[d]
                    t = (me[0] + vx, me[1] + vy)
                    issued = self.step(d)
                    if issued:
                        stepped_into = t
                    self.stuck_steps = (self.stuck_steps + 1) if (issued and last_pos == me) \
                        else 0
                    if self.stuck_steps >= 12 and time.time() - self.last_stuck_log > 3.0:
                        self.last_stuck_log = time.time()
                        occ = [c[0] for c in creatures if (c[1], c[2]) == t]
                        mapinfo = "no map"
                        if self.rmap is not None:
                            mapinfo = (f"solid={self.rmap.solid(*t)} "
                                       f"obj=0x{self.rmap.oflag(*t):02x}")
                        log(f"STEP {d} {me}->{t} DID NOT MOVE US; {mapinfo}; "
                            f"occupied_by={occ or 'nothing'}")
                else:
                    self.hold = None          # unreachable -- fight where we stand
            elif tgt is not None and tgt_dist <= 1:
                # MELEE. The facing is computed pool-to-pool as well: turning toward a mob
                # using our player-block position while the mob's position came from the
                # pool is how you end up swinging at empty ground.
                uid, mx, my = tgt
                self.target_tile = (mx, my)
                self.last_progress_ts = time.time()   # in melee with a hare = making progress
                d = ("right" if mx > fight_me[0] else "left") if mx != fight_me[0] else \
                    ("down" if my > fight_me[1] else "up")
                self.swing(d, uid)
            elif here is not None:
                if self.pickup_uid is None:
                    self.pickup()
                    self.pickup_uid, self.pickup_ts = here[0], time.time()
            # LOOT ONLY WHEN THE FIGHT IS OVER. Drops do not run away; cover does not follow
            # us. Chasing a drop while engaged put the bot in a two-tick loop -- the loot
            # branch stepped off cover to (16,16), cover-seeking stepped it straight back to
            # (17,16), and it alternated indefinitely, picking up nothing and killing
            # nothing between the two.
            elif adj and not stalled:
                pass                                  # in contact: keep swinging, don't loot
            elif self.collect is not None:
                cx, cy, _ = self.collect
                stepped_into = (None if (cx, cy) == me else self.path_step(me, (cx, cy)))
                if (cx, cy) == me:
                    self.collect = None
            elif cands and abs(cands[0][1] - fight_me[0]) \
                    + abs(cands[0][2] - fight_me[1]) <= 4:
                stepped_into = self.path_step(me, (cands[0][1], cands[0][2]))
            elif tgt is not None:
                self.target_tile = (tgt[1], tgt[2])
                stepped_into = self.path_step(me, (tgt[1], tgt[2]))
            else:
                stepped_into = self.sweep_step(me)   # nothing in reach -> sweep for spawns

            if time.time() - last_print > 3.0:
                last_print = time.time()
                where = f"{here_sides}-side" + ("" if self.hold is None or me == self.hold
                                                else f" ->{self.hold}")
                tot = self.pool_hit + self.pool_miss
                miss = f" poolmiss={100.0 * self.pool_miss / tot:.0f}%" if tot else ""
                print(f"pos={me} [{where}] hp={v[0]}/{v[1]} mp={v[2]}/{v[3]} "
                      f"hares={len(mobs)} adj={len(adj)} drops={len(items)} "
                      f"kills={self.kills} looted={self.looted} gates={self.gates}{miss}",
                      flush=True)
            time.sleep(LOOP)


def main():
    args = sys.argv[1:]
    t_start = time.time()

    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    secs = opt("--seconds")
    wins = find_windows()
    if not wins:
        print("No live NexusTK.exe window found.")
        return 1
    hwnd, pid = wins[0][0], wins[0][2]
    log(f"client hwnd={hwnd} pid={pid}")

    agent = NA.Agent()
    world = NB.World(agent)
    s, sc = NB.attach(NB.build_pump(world, agent), pid=pid)
    bot = Mythic(sc.exports_sync, hwnd, leash=int(opt("--leash", "40")), agent=agent,
                 world=world)
    a = opt("--anchor")
    if a:
        bot.anchor = tuple(int(n) for n in a.replace(",", " ").split())
    log(f"anchor tile: {bot.anchor}")
    bot.exp_want = "--rings" in args
    if bot.exp_want:
        log(f"+hit experiment ON: {RING_ARMS} rings, {RING_KILLS} kills per arm")
    time.sleep(1.0)
    # sendraw needs the client's connection handle, which is captured from the first packet
    # the client sends after we attach. Nudge until it is live, or the tree clicks go nowhere.
    for i in range(20):
        if bot.ex.asktile(1, 1):
            break
        bot.press("left" if i % 2 else "right")
        time.sleep(0.4)
    else:
        log("WARNING: no connection handle -- tree interaction will not work")

    try:
        bot.run(deadline=(time.time() + float(secs)) if secs else None,
                gate_first=("--no-gate" not in args))
    except KeyboardInterrupt:
        bot.safe_exit("interrupted")
    except Exception as e:
        # An unhandled error used to just end the process, which in the cave means the
        # character stands there being hit until it dies. Leave first, then re-raise.
        log(f"ERROR: {e!r}")
        try:
            bot.safe_exit("crashed")
        except Exception:
            pass
        raise
    finally:
        bot.flush_data(force=True)
        bot.save_nav(force=True)              # keep the map for the next run
        try:
            s.detach()
            log("frida detached.")
        except Exception:
            pass
    log(f"session: {bot.kills} kills, {bot.looted} pickups, {bot.heals} heals, "
        f"{bot.gates} gates")
    if bot.exp_on:
        report_hit(t_start, bot)
    return 0


def report_hit(since, bot):
    """Hit rate per loadout, from the rows this session actually wrote.

    Read back from attempts.csv rather than tallied in memory, because the CSV is what any
    later analysis will use -- if the two ever disagree, the number printed here would be
    the comforting one and the wrong one."""
    import csv
    p = os.path.join(NA.OUT, "attempts.csv")
    try:
        with open(p, newline="", encoding="utf-8") as f:
            rows = [r for r in csv.DictReader(f) if float(r.get("ts") or 0) >= since * 1000]
    except Exception as e:
        log(f"hit-rate report unavailable: {e}")
        return
    if not rows:
        log("no attempt rows written this session -- nothing to report")
        return
    by = collections.defaultdict(lambda: [0, 0])
    want = collections.Counter(bot.rings)
    for r in rows:
        # count ring OCCURRENCES in the loadout, not membership -- both rings share a name
        c = collections.Counter(r.get("gear", "").split("|"))
        n = sum(min(c[nm], k) for nm, k in want.items())
        by[n][0] += 1
        by[n][1] += int(r.get("hit") or 0)
    log(f"--- HIT RATE by rings worn ({len(rows)} attempts this session) ---")
    for n in sorted(by, reverse=True):
        tries, hits = by[n]
        h = bot.arm_hit.get(n)
        log(f"  {n} ring(s){f'  hit={h}' if h is not None else ''}: "
            f"{hits}/{tries} = {100.0 * hits / tries:.1f}%")
    if len(by) < 2:
        log("  only one arm has data -- the rotation did not get far enough to compare")


if __name__ == "__main__":
    sys.exit(main())
