#!/usr/bin/env python
"""Dead-simple green-squirrel grinder.

ONE loop, ~12x/sec:
    1. read state   -- our tile, HP/mana + every entity, straight from client memory (~15ms)
    2. decide       -- hurt? Soothe. adjacent squirrel? swing. drop nearby? grab it. else walk.
    3. press a key  -- arrow to move/turn, space to swing, ',' to pick up, Shift+Z 'a' to heal
    4. repeat

No planner, no path cache, no state machine. Every decision is made fresh from the current
frame and thrown away, so there is no stale plan that can go wrong.

Green squirrel is sprite (look) 25. ONLY look 25 with entity type 3 (a creature; type 0 is
ground loot) is ever attacked, so the Leviathans sharing the Leviathan rooms (look 158) can
never become a target.

    python re/squirrel_bot.py                 # grind until stopped
    python re/squirrel_bot.py --seconds 300   # or for a fixed time
    python re/squirrel_bot.py --leash 20      # how far it may stray from its start tile

Stop with Ctrl-C or `touch re/auto/STOP` (clean detach -- don't force-kill a frida host).
"""
import os, sys, time, random, json

D = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, D)

import nexus_bot as NB
import nexus_agent as NA
import level_watch as LW
from bot_input_test import find_windows, VK

SQUIRREL_LOOK = 25
TARGET_LOOK = 25          # which sprite to hunt; overridden by --look (e.g. 617 = the
                          # Dark Fortress serpent). Verified live: for these serpents the
                          # memory look (+0x178) agrees with the wire look, so look-based
                          # targeting is reliable here.
TYPE_CREATURE = 3
TYPE_GROUND_ITEM = 0

LOOP = 0.05          # seconds between decisions
STEP_GAP = 0.18      # client walk cooldown -- a press inside it is swallowed
SWING_GAP = 0.12     # gap between spacebar swings
WALL_TTL = 25.0      # how long a tile that refused a step stays "blocked"
LOOT_RANGE = 8       # chase a drop this far away when there IS a mob competing for attention
FULL_TTL = 180.0     # how long a "pack is full of this item" belief lasts before we re-test
FULL_AFTER = 2       # distinct drops of one sprite that must refuse us before we call it full
DETOUR = 3           # ...and this far even while a mob is waiting (grab it on the way)
KILL_WINDOW = 4.0    # a target that vanishes within this long after we hit it = a kill
WARP_JUMP = 12       # a position change this big in one tick is a map change, not a step
RESCAN_EVERY = 90.0  # full memory re-scan for entity regions (~1.2s, so keep it rare)
SCAN_CAP = 5000      # max vtable hits per scan. The client has ~452; the old 400 truncated.
EMPTY_RESCAN = 20.0  # "no squirrels" this long is more likely blindness than an empty room
# --- NO ROOM ROTATION. We stay in Southern Path and wait for respawns; the room refills on
# its own. The only time we deliberately cross a warp is to come BACK from somewhere we
# shouldn't be (see check_room/leave_room -- Nagnang borders this room).
ROOM_EMPTY_LEAVE = 45.0  # confirmed empty this long -> say so, then keep sweeping and waiting
ARRIVAL_CLEAR = 3        # get this far from the tile we warped in on before doing anything
ARRIVAL_GRACE = 8.0      # ...but only bother for this long after arriving
# --- exploration by memory, not dice. Random wander re-walks the same corner and never
# checks the far side of the room, so spawns sit unfound. Remember when each tile was last
# near us and go to whatever we've neglected longest.
VISION = 5           # tiles around us that count as "seen" this tick
GOAL_TIMEOUT = 8.0   # can't reach an exploration goal in this long -> write it off, pick again
# --- where we are allowed to be. The client keeps the room name in memory and the game's own
# map_index.csv gives every room's real size, so we can both name the room and know its
# bounds. Both matter: Southern Path is only 20x30, so a 41x41 goal box around us is mostly
# OFF THE MAP, and hunting tiles that cannot exist is why exploring an empty room crawled.
ROOMS = ("Southern Path", "Southern Fork", "Leviathan Mound",
         "Leviathan Fields", "Leviathan Hermit")
HOME_ROOM = "Southern Path"
ROOM_POLL = 1.0      # how often to re-read the room name (one cheap read once located)
FLUSH_EVERY = 15.0   # how often to persist combat data (swings/kills/incoming/hit rate)
# --- target commitment. Dropping a target the instant one scan misses it made the bot give
# up on live mobs and thrash between them; a scan blips far more often than a mob dies.
TARGET_GRACE = 2.5   # keep chasing a target this long after it vanishes from a scan
ENGAGED_FOR = 3.0    # swung at it this recently -> stay on it, don't wander off to loot
# --- moving mobs. Squirrels barely moved and died in ~2 hits, so lock-onto-one-and-chase
# worked. Serpents move every second and take ~30 hits, so the picked target flees while a
# DIFFERENT serpent stands adjacent landing hits -- and the bot, tunnelled on its target,
# never turned to fight the one actually hurting it. Two rules fix it:
CHASE_GIVEUP = 3.5   # chased a target this long without ever reaching melee -> it's running
                     # (or walled off); drop it, briefly, and pick another
CHASE_COOLDOWN = 4.0 # ...and don't re-pick that same uid for this long, or we re-lock instantly
# --- ghost targets. The client's entity pool keeps stale slots that still read look=617 but
# are not live mobs. A real serpent we hit answers with an hp-bar packet (agent.mobhp[uid]
# gets a value); a ghost NEVER does, because our swings land on nothing. So "swung at it this
# long and it has never once reported an hp bar" is a clean ghost signature -- without it the
# bot stood swinging a corpse while real serpents chipped it (user: "swinging at a ghost while
# dying"). target_dead only catches slots that reached hp==0; a ghost that never had an hp bar
# reads mobhp=None and slips through, which is exactly this case.
GHOST_TIMEOUT = 2.0  # adjacent and swinging this long with NO hp-bar ever -> it's a ghost
GHOST_COOLDOWN = 12.0 # ghosts don't respawn; ban the slot a good while
# --- survival. Soothe is spellbook letter 'a', cast Shift+Z -> 'a' with NO Enter, and it
# lands instantly. So the answer to taking damage is to HEAL THROUGH IT, not to run: running
# hands the mob free hits while HP crawls back at regen speed. Backing off is only the
# fallback for the one case healing can't fix -- an empty mana bar.
# Measured on this character, not assumed: Soothe heals +50 HP, costs 3 mana, and the HP
# only moves 0.27-0.58s AFTER the cast (server round-trip). That latency is what broke the
# first attempt -- it read HP back 0.30s in, saw no change, logged "hp 981 -> 981" and gave
# up after one cast while HP kept falling. So: never read HP mid-burst. Work out the cast
# COUNT from the deficit up front, fire them back to back, and only look afterwards.
HEAL_PER_CAST = 50   # measured HP per Soothe
HEAL_COST = 3        # measured mana per Soothe (625 pool = ~200 casts; mana is not a limit)
HEAL_SETTLE = 0.35   # extra wait after the last cast so it has landed before we re-read
HEAL_HP = 0.85       # below this: heal. Cheap enough that there's no reason to wait for 75%.
TARGET_HP = 0.97     # heal back up to about full
TOPOFF_HP = 0.90     # between fights, quietly top up
EMERG_HP = 0.50      # below this, chain a lot harder
MAX_BURST = 4        # normal cap: 200 HP, ~1.4s standing still
MAX_BURST_EMERG = 8  # emergency cap
SAFE_HP = 0.92       # resume fighting after a no-mana disengage
MANA_FLOOR = 0.05    # keep a small reserve; below it, healing is off the table
HEAL_CD = 0.30       # min seconds between heal bursts
CAST_GAP = 0.12      # Shift+Z -> letter
SPELL_GAP = 0.35     # pause between chained casts (the client caps ~3/second)
BAIL_HP = 0.30       # this low AND out of mana: exit -- a stopped bot beats a dead character
P_STOP = os.path.join(NA.OUT, "STOP")
P_WARPTILES = os.path.join(NA.OUT, "warp_tiles.json")   # room -> tiles that teleported us

DIRS = {"up": (0, -1), "down": (0, 1), "left": (-1, 0), "right": (1, 0)}
_OPPOSITE = {"up": "down", "down": "up", "left": "right", "right": "left"}
FACE_OF = {"up": 0, "right": 1, "down": 2, "left": 3}
FACE_DELTA = {0: (0, -1), 1: (1, 0), 2: (0, 1), 3: (-1, 0)}   # N E S W


def log(msg):
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


class Simple:
    def __init__(self, ex, hwnd, leash=20, agent=None, world=None, attach_ts=0):
        self.ex = ex
        self.hwnd = str(hwnd)
        self.leash = leash
        # --- base-stat capture on level-up. The Agent already decodes the statblock this
        # needs, so recording it here costs nothing and, crucially, avoids a SECOND frida
        # session + packet pump on the same client fighting for the same hooks.
        self.world = world
        # Agent.__init__ RESTORES cur/level/stats_ts from disk, so a fresh Agent will happily
        # report the last character it ran as -- it read level 65 for a rogue that wasn't
        # logged in. Every reading is therefore refused unless the statblock arrived after we
        # attached; without this the first row written would be the previous character's
        # stats wearing this one's name, which is the exact contamination the `character`
        # column exists to prevent.
        self.attach_ts = attach_ts
        self.last_level = None
        # The packet pump already feeds this Agent every swing, kill and hit taken -- but it
        # only accumulates them IN MEMORY. Nothing wrote them out, so a whole evening of
        # combat data (100+ kills) died with the process. Flush it to disk periodically.
        self.agent = agent
        self.last_flush = 0.0
        self.regions = []
        self.blocked = {}         # (x,y) -> ts a step into it did nothing TWICE
        self.fails = {}           # (x,y) -> consecutive no-move steps into it
        self.last_step = 0.0
        self.stuck_since = 0.0    # when our tile last changed
        self.kills = 0
        self.looted = 0
        self.home = None          # tile we started on; we stay within `leash` of it
        self.target = None
        self.target_tile = None
        self.hit_uid = None       # last squirrel we actually swung at, and when
        self.hit_ts = 0.0
        # Drops we pressed ',' on that stayed on the ground. Once the pack is full of acorns
        # every pickup silently fails, and without this the bot stands on one drop retrying
        # forever instead of killing. Give up on an item after two tries and never revisit it.
        self.no_loot = {}         # uid -> failed pickup attempts
        self.dead_drops = set()   # drops that refused us twice -- never chased again
        self.item_look = {}       # uid -> sprite, rebuilt every tick in entities()
        # "Full of acorns" handling. A single refused drop is usually someone else's kill,
        # not a full pack -- but a full stack refuses EVERY drop of that type, each with a
        # fresh uid. So we count DISTINCT uids of the same sprite that refused us, and once
        # two independent drops of a sprite have failed we stop chasing that sprite entirely.
        # Recorded with a timestamp so it re-tests periodically: sell the acorns and the bot
        # starts collecting them again on its own within FULL_TTL.
        self.sprite_fails = {}    # sprite -> set of uids that refused pickup
        self.full_sprites = {}    # sprite -> ts it was declared full
        self.full_warned = False
        self.pickup_uid = None    # drop we just pressed ',' on, awaiting confirmation
        self.pickup_ts = 0.0
        self.pack_full = False     # latched once the server says "you can't have more than N";
                                   # while set, keep hunting but never chase/press-pickup a drop
        self.collect = None       # (x, y, deadline) -- go stand on what we just killed
        self.heals = 0
        self.last_heal = 0.0
        self.empty_since = 0.0    # when the room first looked empty (0 = it doesn't)
        self.seen = {}            # (x,y) -> last time that tile was within VISION of us
        self.goal = None          # exploration goal: the tile we've neglected longest
        self.goal_ts = 0.0
        self.target_seen = 0.0    # last tick our target was actually in the entity list
        self.target_pos = None    # its last known tile, for chasing through a scan blip
        self.rescanned_empty = False   # already re-scanned for this apparent empty room
        self.waiting = False      # room is dry -> sweeping it and waiting for respawns
        self.exits = []           # tiles in THIS room known to warp (cleared on map change)
        self.arrived_at = None    # tile we warped in on
        self.arrived_ts = 0.0
        self.rt = NB.RoomTracker(log)   # room name straight from client memory
        self.room = None
        self.room_wh = None       # (w, h) of this room from the game's own map index
        self.room_ts = 0.0
        self.evicting = False     # in a room we shouldn't be in -> walk back out
        self.unreachable = set()  # tiles we tried for and never reached (walls, off-map)
        self.warps_by_room = self.load_warps()   # room -> {tiles that teleported us}
        self.retreating = False   # backing off (only ever when out of mana to heal with)
        self.patrol = "right"
        self.patrol_until = 0.0
        self.stay = False         # --stay: farm the room we start in, don't require it to be
                                  # a known squirrel room (set from main())
        self.home_room = None     # the room we first stood in; the one we belong in
        self.target_since = 0.0   # when we committed to the current target
        self.reached_target = False  # have we been adjacent to it since committing?
        # --- position frame. self.me() reads the PLAYER BLOCK, which trails the entity pool by
        # ~1 tile (measured). Adjacency/melee are checked against `me`, so a squirrel that is
        # really adjacent reads 2 tiles away in the block frame -> we never swing, just step and
        # step back = oscillate, and an EDGE squirrel (no room to overshoot) never gets hit at
        # all. Fix (ported from mythic_bot): find our OWN entity in the pool and use ITS tile as
        # `me`. self_uid is the creature that is never a huntable mob and never more than a tile
        # from the block position -- i.e. us.
        self.self_uid = None
        self._self_cand = None
        self.me_pool = None          # last known pool position of our own entity
        self.me_pool_ts = 0.0
        self.last_frame_log = 0.0
        self.chase_ban = {}       # uid -> ts until which we won't re-pick a runner we gave up on
        self.ghost_ban = {}       # uid -> ts; a slot we swung at that never reported an hp bar
        self.swing_uid = None     # the uid we are currently meleeing, and since when -- so we
        self.swing_uid_since = 0.0  # can tell a ghost (no damage ever) from a slow kill
        self.session_char = None  # this run's character name, so a failed profile read never
                                  # writes a blank character label into level_base.csv

    # ---------- state ----------
    def find_regions(self):
        """Every memory region that could hold entity objects.

        Each entity begins with a shared vtable pointer, so scanning for that value locates
        them. We keep EVERY region the scan touches, not just the ones populated right now:
        mobs that spawn later land in blocks that are empty at startup, and locking onto one
        region is exactly what made an earlier version blind while squirrels stood 3 tiles
        away. Scanning them all costs ~15ms for ~19MB, which is free at this loop rate.
        """
        v = NB.ENT_VTABLE
        pat = " ".join(f"{(v >> (8 * i)) & 0xff:02x}" for i in range(4))   # little-endian
        try:
            hits = self.ex.scanpat(pat, SCAN_CAP)
        except Exception as e:
            log(f"region scan failed: {e}")
            return self.regions
        # The old cap was 400 and the client really has ~452 references, so the scan silently
        # truncated -- and WHICH regions survived was arbitrary. One run came up with 12
        # regions / 15 MB and reported squirrels=0 for 75 straight seconds while squirrels
        # stood in the room; the next run got 15 regions / 20 MB and farmed fine. If we ever
        # hit the cap again we are blind in exactly that way, so say so loudly.
        if len(hits) >= SCAN_CAP:
            log(f"WARNING: entity scan hit the {SCAN_CAP} cap -- regions may be missing "
                f"and mobs invisible. Raise SCAN_CAP.")
        regions = []
        for h in hits:
            a = int(h, 16)
            if any(lo <= a < hi for lo, hi in regions):
                continue                        # already covered -> skip the rangeof call
            try:
                r = self.ex.rangeof(h)
            except Exception:
                continue
            if r:
                lo = int(r[0], 16)
                regions.append((lo, lo + r[1]))
        if regions:
            self.regions = regions
            log(f"entity regions: {len(regions)} "
                f"({sum(hi - lo for lo, hi in regions) / 1048576:.0f} MB/tick)")
        return self.regions

    def me(self):
        try:
            p = self.ex.selfxy(hex(NB.SELF_PTR_ADDR), NB.SELF_OFF["x"], NB.SELF_OFF["y"])
        except Exception:
            return None
        return tuple(p) if p else None

    def vitals(self):
        try:
            v = self.ex.selfstats(hex(NB.SELF_PTR_ADDR), NB.SELF_OFF["curhp"],
                                  NB.SELF_OFF["maxhp"], NB.SELF_OFF["curmana"],
                                  NB.SELF_OFF["maxmana"], NB.SELF_OFF["exp"])
        except Exception:
            return None
        return tuple(v) if v else None

    def entities(self):
        """(squirrels, ground_items) as [(uid, x, y)], read fresh every tick."""
        rows = []
        for lo, hi in self.regions:
            try:
                rows.extend(self.ex.enument(NB.ENT_VTABLE, lo, hi) or [])
            except Exception:
                pass
        mobs, items, creatures = [], [], []
        item_look = {}
        raw = []                                  # every valid entity, unclassified -- diag
        for r in rows:
            if len(r) < 5:
                continue
            uid, x, y, ty, look = r[0], r[1], r[2], r[3], r[4]
            if not (1000 < uid < 100_000_000):    # a freed slot keeps the vtable, reads junk
                continue
            raw.append((uid, x, y, ty, look))
            if ty == TYPE_CREATURE:
                creatures.append((uid, x, y))     # EVERYTHING alive -- what to back away from
                if (look & 0x7FFF) == TARGET_LOOK:
                    mobs.append((uid, x, y))
            elif ty == TYPE_GROUND_ITEM:
                items.append((uid, x, y))
                # A ground item's sprite identifies its TYPE -- every acorn shares one look.
                # This is how "full of acorns" is recognised across the endless stream of
                # fresh per-drop uids: give up on the SPRITE, not the individual drop.
                item_look[uid] = look & 0x7FFF
        self.item_look = item_look
        self.raw_ents = raw
        return mobs, items, creatures

    def wire_look(self, uid):
        """The AUTHORITATIVE look for an entity, from its 0x07 spawn packet on the wire.
        The memory look field (+0x178) has proven unreliable this session -- it read look=10
        for entities that match no mob at all -- so the wire, which the look-calibration tool
        itself trusts, is the source of truth. None if we never saw its spawn."""
        w = self.world
        if w is None:
            return None
        try:
            with w.lock:
                e = w.ent.get(uid)
            return e.get("look") if e else None
        except Exception:
            return None

    def diag_entities(self, me, radius=12):
        """What is actually near us, memory look AND wire look side by side -- the ground
        truth for 'squirrels=0 but I can see one'. If an entity's WIRE look is 25 while its
        memory look is not, memory is misreading squirrels and classification must switch to
        the wire. If nothing is near at all, the scan is region-blind."""
        raw = getattr(self, "raw_ents", [])
        near = [(uid, x, y, ty, lk) for (uid, x, y, ty, lk) in raw
                if abs(x - me[0]) + abs(y - me[1]) <= radius]
        near.sort(key=lambda e: abs(e[1] - me[0]) + abs(e[2] - me[1]))
        nwire = 0
        if self.world is not None:
            with self.world.lock:
                nwire = len(self.world.ent)
        if not near:
            log(f"DIAG @ {me}: {len(raw)} in scan / {nwire} on wire, NONE within {radius} "
                f"tiles -- if you can see one, the scan is missing its memory region")
            return
        parts = [f"uid{uid}@({x},{y}) memlook={lk} wirelook={self.wire_look(uid)}"
                 for uid, x, y, ty, lk in near[:8]]
        log(f"DIAG @ {me}: {len(near)} near / {nwire} on wire -- " + " | ".join(parts))

    # ---------- input ----------
    def press(self, key):
        try:
            self.ex.postkey(self.hwnd, VK[key])
        except Exception:
            pass

    def step(self, d):
        """Issue one move, paced to the walk cooldown. NexusTK turns first if we aren't
        already facing that way, so press twice: turn, then step."""
        if time.time() - self.last_step < STEP_GAP:
            return False
        self.press(d)
        time.sleep(0.03)
        self.press(d)
        self.last_step = time.time()
        return True

    def stamp_swing(self, uid, mob, me, d):
        """Tell the Agent WHO we are about to hit and from where.

        Without this, Agent.on_attack sees an empty swing_ctx and drops the attempt on the
        floor -- which is why attempts.csv had not gained a row since 12:19 while swings.csv
        kept growing. swings.csv only records dmg>0, so a MISS leaves no trace anywhere and
        P(hit) cannot be fitted from it. That is the whole point of the file: hit chance is
        exactly what a +hit item is supposed to move.

        Facing comes from the direction we are ABOUT to press, not the world's tracked
        facing -- the tracker updates off a packet that has not been sent yet at this point.
        """
        ag = self.agent
        if ag is None:
            return
        ctx = {"eid": uid, "mob": ag.mob_names.get(uid, ""), "zone": ag.zone,
               "self_x": me[0], "self_y": me[1], "mob_x": mob[0], "mob_y": mob[1],
               "facing": FACE_OF.get(d, "")}
        dx, dy = mob[0] - me[0], mob[1] - me[1]
        ctx["dist"] = abs(dx) + abs(dy)
        fx, fy = FACE_DELTA.get(FACE_OF.get(d, -1), (0, 0))
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

    def learn_self_uid(self, me, creatures, mobs):
        """Work out which entity record is us, so we can read our TRUE tile from the pool.

        It is the one that is never a mob we hunt and never further than a tile from the
        player-block position. A real creature fails that test the moment it dies, wanders,
        or we walk away. ~12 ticks glued to us is enough to be sure."""
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
                if self._self_cand[uid] >= 12:
                    self.self_uid = uid
                    log(f"our own entity is uid={uid} -- reading true position from the pool")
                    return
            else:
                del self._self_cand[uid]        # broke contact -> not us
        for uid in near:                        # fold in newly-adjacent candidates
            self._self_cand.setdefault(uid, 0)

    def me_from_pool(self, me, creatures):
        """Our real tile, from our own entity in the pool. Falls back to the block frame
        (or a recent pool reading) until self_uid is known / while we blink out of a scan."""
        if self.self_uid is not None:
            for c in creatures:
                if c[0] == self.self_uid:
                    self.me_pool, self.me_pool_ts = (c[1], c[2]), time.time()
                    return (c[1], c[2])
            if self.me_pool and time.time() - self.me_pool_ts < 1.0:
                return self.me_pool              # brief scan blip -> trust the last pool tile
        return me

    def melee_pick(self, me, huntable):
        """Which adjacent mob to swing at THIS tick, or None if nothing is in reach.

        The rule that fixes tunnel-chasing: anything one tile away gets hit, whether or not
        it is the mob we committed to. Prefer the committed target if it happens to be
        adjacent (so we finish what we started), otherwise the nearest, uid breaking ties for
        determinism. Kept as a method so the fight logic is testable without a live client."""
        contact = [m for m in huntable
                   if abs(m[1] - me[0]) + abs(m[2] - me[1]) <= 1]
        if not contact:
            return None
        return next((c for c in contact if c[0] == self.target), None) \
            or min(contact, key=lambda c: (abs(c[1] - me[0]) + abs(c[2] - me[1]), c[0]))

    def target_dead(self, uid):
        """The server's own HP bar for this mob reads 0.

        The client keeps a corpse in its entity pool for several ticks after death, so
        "still in the scan" is NOT "still alive" -- and while it lingered the bot happily
        re-targeted it and fired another full burst. Those swings are not merely wasted
        time: every one resolves as a MISS in attempts.csv, dragging the measured hit rate
        down with attacks on a mob that was already dead. The 0x13 hp-bar reading is
        server-sent and arrives on the killing blow, which is why it is the signal to use.
        """
        ag = self.agent
        return ag is not None and ag.mobhp.get(uid) == 0

    def swing(self, d, uid, me=None, mob=None):
        if me is not None and mob is not None:
            self.stamp_swing(uid, mob, me, d)
        self.press(d)                  # face it (a move into an occupied tile just turns us)
        time.sleep(0.05)
        self.hit_uid, self.hit_ts = uid, time.time()
        self.stuck_since = time.time()  # standing still to swing is not being stuck
        for i in range(3):
            # Stop mid-burst the moment it dies. The hp bar is a server round trip, so at a
            # 0.12s gap this will not always beat the next press -- the reliable saving is
            # not re-targeting the corpse next tick (see `huntable`). This just trims what
            # it can, for free.
            if i and self.target_dead(uid):
                break
            self.press("space")
            time.sleep(SWING_GAP)

    def pickup(self):
        """Shift+',' (i.e. '<'), NOT plain ','. Both are item-pickup keys, but the shifted
        one takes the WHOLE stack -- plain ',' leaves the rest of a stacked drop on the
        ground. Same VK (0xBC), shift held, char '<'."""
        for _ in range(3):
            try:
                self.ex.postchar(self.hwnd, 0xBC, ord("<"), True)
            except Exception:
                pass
            time.sleep(0.10)

    def cast(self, letter):
        """Cast a simple spell: Shift+Z opens the prompt, the letter fires it. NO Enter --
        a simple spell goes off the instant the letter lands, and a stray Enter afterwards
        opens the CHAT BOX, which then swallows every arrow key and looks exactly like being
        walled in.

        Measured on the live client: the outgoing 0x0f cast frame appears reliably at a 50ms
        gap, so the 0.12 here is pure margin."""
        try:
            self.ex.postchar(self.hwnd, ord("Z"), ord("Z"), True)
            time.sleep(CAST_GAP)
            self.ex.postchar(self.hwnd, ord(letter.upper()), ord(letter), False)
        except Exception:
            pass

    def clear_ui(self):
        """Esc away any open prompt/chat box. Something stale on screen swallows the cast
        keys silently -- a Soothe attempted without this sent NO 0x0f and spent NO mana,
        while the identical sequence after an Esc cast every single time."""
        for _ in range(2):
            try:
                self.ex.postchar(self.hwnd, 0x1B, 0x1B, False)
            except Exception:
                pass
            time.sleep(0.06)

    def heal_to(self, target_frac, cap):
        """Soothe back up toward target_frac, deciding the cast count BEFORE casting.

        Deliberately does no HP checks between casts: the heal lands ~0.3-0.6s late, so a
        mid-burst read reports the HP we had before the previous cast and stops the burst
        early -- exactly the bug that made the bot cast once and keep bleeding. Overshooting
        costs 3 mana a cast, which is nothing; under-healing costs the character."""
        v = self.vitals()
        if not (v and v[1]):
            return 0
        if v[3] and v[2] / float(v[3]) < MANA_FLOOR:
            return 0
        deficit = int(target_frac * v[1]) - v[0]
        if deficit <= 0:
            return 0
        n = min(cap, max(1, (deficit + HEAL_PER_CAST - 1) // HEAL_PER_CAST))
        if v[3]:                                    # never spend into the reserve
            n = min(n, max(0, (v[2] - int(MANA_FLOOR * v[3])) // HEAL_COST))
        if n <= 0:
            return 0
        self.clear_ui()
        for _ in range(n):
            self.cast("a")
            self.heals += 1
            time.sleep(SPELL_GAP)
        time.sleep(HEAL_SETTLE)                     # let the last one land before we re-read
        self.last_heal = time.time()
        self.stuck_since = time.time()              # standing still to cast is not being stuck
        return n

    # ---------- movement ----------
    def toward(self, me, dst, allow_warp=False):
        """One greedy step toward dst, longer axis first, skipping tiles that already refused
        us. Walls are learned by simply noticing we didn't move.

        allow_warp lets us step onto a known doorway on purpose -- normally they're avoided,
        but leaving a dry room means walking through one."""
        dx, dy = dst[0] - me[0], dst[1] - me[1]
        opts = []
        if abs(dx) >= abs(dy):
            if dx: opts.append("right" if dx > 0 else "left")
            if dy: opts.append("down" if dy > 0 else "up")
        else:
            if dy: opts.append("down" if dy > 0 else "up")
            if dx: opts.append("right" if dx > 0 else "left")
        # If every neighbour is "a wall" we are not walled in -- we mislearned. Forget them.
        if all(time.time() - self.blocked.get((me[0] + vx, me[1] + vy), 0) < WALL_TTL
               for vx, vy in DIRS.values()):
            self.blocked.clear()
            self.fails.clear()
        for d in opts + ["up", "right", "down", "left"]:
            vx, vy = DIRS[d]
            tile = (me[0] + vx, me[1] + vy)
            if time.time() - self.blocked.get(tile, 0) < WALL_TTL:
                continue
            if not self.in_room(tile):
                continue                      # off the edge of the map; the step just fails
            if not allow_warp and self.is_warp(tile):
                continue                      # a known doorway; stepping on it leaves the room
            if self.step(d):
                return tile
        return None

    def room_allowed(self, r):
        """Are we allowed to hunt in room `r`? Default: --stay pins the first room we stood
        in, otherwise the squirrel/leviathan whitelist. Subclasses override to widen this
        (e.g. the rabbit zone allows any 'Rabbit' room so we can drift between them)."""
        if self.stay:
            return r == self.home_room
        return r in ROOMS

    def pre_survival(self, me, mobs, items, creatures):
        """Per-tick hook that runs BEFORE the inherited heal-through/retreat logic. Default
        does nothing (so the squirrel and serpent bots are unchanged). A subclass returns
        True to consume the tick -- used by the rabbit bot to gate out when low and to keep
        self-cast buffs up while nothing is adjacent."""
        return False

    def check_room(self):
        """Which room are we actually in? The client holds the name as a string in memory;
        RoomTracker finds it and matches it against the game's map index. Cheap to poll once
        located."""
        now = time.time()
        if now - self.room_ts < ROOM_POLL:
            return
        self.room_ts = now
        try:
            r = self.rt.poll(self.ex)
        except Exception:
            return
        if not r or r == self.room:
            return
        prev, self.room = self.room, r
        if self.agent is not None:
            self.agent.zone = r          # every swing/attempt/kill row is labelled with WHERE
        wh = self.rt.names.get(r)
        self.room_wh = (int(wh[1]), int(wh[2])) if wh else None
        size = f" ({self.room_wh[0]}x{self.room_wh[1]})" if self.room_wh else ""
        log(f"ROOM: {prev or '?'} -> {r}{size}")
        # We land standing ON the doorway back. Now that we know this room's name, record it
        # under THIS room so we don't idly step onto it and bounce straight back.
        if self.arrived_at and time.time() - self.arrived_ts < 10.0:
            self.note_warp(r, self.arrived_at)
        # In stay-here mode we farm whatever room we start in (Dark Fortress / Buya isn't in
        # the squirrel-room whitelist, but the mob IS here). The home room is whatever we
        # first stood in; leave only if a warp drops us somewhere ELSE, and come back.
        allowed = self.room_allowed(r)
        if self.home_room is None:
            self.home_room = r
            allowed = True
        if not allowed:
            log(f"{r} is not our hunting room -- leaving, back the way we came")
            self.evicting = True
        elif self.evicting:
            self.evicting = False
            log(f"back in {r} -- resuming")

    def in_room(self, t):
        """Is this tile actually on the map? Southern Path is 20x30."""
        if t[0] < 0 or t[1] < 0:
            return False
        if self.room_wh:
            return t[0] < self.room_wh[0] and t[1] < self.room_wh[1]
        return True

    # ---------- warp tiles: learned exactly, not guessed ----------
    def load_warps(self):
        try:
            with open(P_WARPTILES, encoding="utf-8") as f:
                return {k: {tuple(t) for t in v} for k, v in json.load(f).items()}
        except Exception:
            return {}

    def save_warps(self):
        try:
            with open(P_WARPTILES, "w", encoding="utf-8") as f:
                json.dump({k: sorted(list(t) for t in v)
                           for k, v in self.warps_by_room.items()}, f, indent=1)
        except Exception as e:
            log(f"could not save warp tiles: {e}")

    def note_warp(self, room, tile):
        """Record a tile that actually teleported us, for this specific room.

        Banning the whole border instead of the real doorway was a bad trade: it hid every
        squirrel standing on the outer ring, so the bot saw mobs, refused to hunt any of
        them, and paced. The border is walkable -- we stood on (6,0) and (8,1) quite safely.
        Only the doorway itself warps, so ban only that, and remember it between runs."""
        if not room or not tile:
            return
        s = self.warps_by_room.setdefault(room, set())
        if tuple(tile) not in s:
            s.add(tuple(tile))
            log(f"learned warp tile in {room}: {tuple(tile)} -- routing around it from now on")
            self.save_warps()

    def is_warp(self, t):
        return tuple(t) in self.warps_by_room.get(self.room, ())

    def leave_room(self, me):
        """Walk back onto the tile we arrived on -- that doorway leads home. We land standing
        ON it, and a warp fires on ENTERING a tile, so step off first and then back on."""
        if not self.exits:
            return self.wander(me, leashed=False, allow_warp=True)
        e = min(self.exits, key=lambda t: abs(t[0] - me[0]) + abs(t[1] - me[1]))
        if (me[0], me[1]) == e:
            return self.wander(me, leashed=False, allow_warp=True)
        return self.toward(me, e, allow_warp=True)

    def flush_data(self, force=False):
        """Write the combat log out: swings.csv (damage per hit with our stat vector),
        attempts.csv (hit/miss), kills.csv (exp per kill), incoming.csv (damage taken vs our
        AC), hitrate.csv (hit chance per mob sprite). This is the raw material for working
        out the game's attack/hit formulas, and it is free -- the Agent has already decoded
        all of it, we're only persisting it."""
        if self.agent is None:
            return
        if not force and time.time() - self.last_flush < FLUSH_EVERY:
            return
        self.last_flush = time.time()
        try:
            self.agent.flush_swings()          # append-only: swings/attempts/kills/incoming
            self.agent.flush()                 # rewrites levels/hitrate/status snapshots
        except Exception as e:
            log(f"data flush failed: {e}")     # never let bookkeeping stop the farming

    def capture_level(self):
        """Write a base-stat row whenever the level changes (and once at the start).

        Records what is MEASURED and says which is which:
          * ac_base  -- from the statblock, signed. The only AC that moves on level-up.
                        NOT agent.ac, which is the displayed total and swings ~50 points
                        with any buff or debuff. Conflating those two lost the rogue's
                        whole AC curve; they never share a field again.
          * might/grace/will/maxhp/maxmana -- equipped totals, exactly as sent.
          * the worn loadout, from the server's own 0x39 profile.
        Gear cancels across a ding, so the DELTA between two consecutive rows is the true
        base gain whether or not we know what the gear is worth.

        The 0x2d profile request costs up to a second, so this only runs on a level change,
        and it runs AFTER the survival branch -- a pause is cheap, a pause while bleeding
        is not.
        """
        a = self.agent
        if a is None or self.world is None:
            return
        s = LW.stats(a, self.attach_ts)          # refuses anything older than our attach
        if not s or s.get("level") is None or s["level"] == self.last_level:
            return
        prev, self.last_level = self.last_level, s["level"]
        try:
            who, worn = LW.profile(self.ex, self.world, tries=2)
            # NEVER write a blank character label. The 0x2d profile round trip sometimes
            # gets no reply mid-fight (the client is busy), and a row with character="" is
            # exactly the contamination the column exists to prevent -- two serpent rows
            # landed unlabelled that way. One bot run is one character, so once we have
            # learned who this is, reuse it whenever a later profile read comes back empty.
            if who:
                self.session_char = who
            elif getattr(self, "session_char", None):
                who = self.session_char
            # Label every combat row with the loadout that produced it. Refreshed here
            # (startup + each ding) rather than on a timer, because the 0x2d round trip costs
            # up to a second; gear does not change during an unattended grind, so a swap made
            # mid-run will not show up until the next level.
            if worn:
                self.agent.gear_sig = "|".join(worn)
                self.agent.weapon = worn[0]
            row = dict(ts=int(time.time() * 1000), when=time.strftime("%H:%M:%S"),
                       character=who, naked=0, weapon=worn[0] if worn else "",
                       gear="|".join(worn), exp=a.exp, tnl=a.tnl or "",
                       **s, **LW.base_of(a, s))
            LW.write_row(row)
        except Exception as e:                   # bookkeeping never stops the farming
            log(f"level capture failed: {e}")
            return
        tag = f"LEVEL {prev} -> {s['level']}" if prev is not None \
            else f"baseline at level {s['level']}"
        log(f"{tag}  base_ac={s['ac_base']}  might={s['might']} grace={s['grace']} "
            f"will={s['will']}  hp={s['maxhp']} mana={s['maxmana']}  "
            f"[{who or '?'}] {'|'.join(worn) or '(gear unknown)'}")

    def mark_seen(self, me):
        """Everything within VISION of us counts as looked at, right now."""
        now = time.time()
        for dx in range(-VISION, VISION + 1):
            for dy in range(-VISION, VISION + 1):
                if abs(dx) + abs(dy) <= VISION:
                    self.seen[(me[0] + dx, me[1] + dy)] = now

    def pick_goal(self, me):
        """Where to look next, searched over the REAL map rather than a box around us.

        Two rules, in order:
          1. FRONTIER -- the nearest never-seen tile that borders somewhere we have seen.
             Bordering seen space is what makes it plausibly reachable; an unseen tile in
             the middle of a wall block is not, and chasing those is what made exploring an
             empty room useless (most of a 41x41 box around us isn't even inside a 20x30
             map, so the bot spent its time walking at tiles that cannot exist).
          2. Once there is no frontier left, the room is swept -- go back to whatever we
             have looked at least recently, to catch respawns.
        """
        now = time.time()
        seen = self.seen
        if self.room_wh:
            xr, yr = range(self.room_wh[0]), range(self.room_wh[1])
        else:                                   # room unknown -> fall back to a box
            xr = range(max(0, me[0] - self.leash), me[0] + self.leash + 1)
            yr = range(max(0, me[1] - self.leash), me[1] + self.leash + 1)
        frontier, stale = None, None
        for x in xr:
            for y in yr:
                t = (x, y)
                if t == me or t in self.unreachable or self.is_warp(t):
                    continue                    # never make a doorway tile our destination
                if now - self.blocked.get(t, 0) < WALL_TTL:
                    continue                    # known wall
                d = abs(x - me[0]) + abs(y - me[1])
                if t not in seen:
                    if any((x + vx, y + vy) in seen for vx, vy in DIRS.values()):
                        if frontier is None or d < frontier[0]:
                            frontier = (d, t)
                elif frontier is None:
                    key = (-(now - seen[t]), d)
                    if stale is None or key < stale[0]:
                        stale = (key, t)
        if frontier:
            return frontier[1]
        return stale[1] if stale else None

    def explore(self, me):
        """Head for the stalest tile. On arrival (or if it proves unreachable) pick another."""
        now = time.time()
        if (self.goal is None or self.goal == me
                or now - self.goal_ts > GOAL_TIMEOUT
                or now - self.seen.get(self.goal, 0.0) < 1.0):   # already looked at it
            if self.goal is not None and now - self.goal_ts > GOAL_TIMEOUT:
                # unreachable in practice (wall, or off-map) -- never pick it again in
                # this room, or we re-target it every few seconds forever
                self.unreachable.add(self.goal)
                self.seen[self.goal] = now
            self.goal = self.pick_goal(me)
            self.goal_ts = now
        if self.goal is None:
            return self.wander(me)
        return self.toward(me, self.goal)

    def wander(self, me, leashed=True, allow_warp=False):
        """Fallback only -- explore() is the real one. Walks a straight line for a while,
        then turns. Used when there is no goal to pick, or when deliberately striking out
        past the leash to find a doorway.

        Normally stays on a leash around our start tile. Without it the walk eventually
        finds a map edge, crosses a warp, and the run continues in a different room entirely
        (seen live: it strolled from (9,0) into another map at (123,153))."""
        if leashed and self.home \
                and abs(me[0] - self.home[0]) + abs(me[1] - self.home[1]) > self.leash:
            return self.toward(me, self.home)          # too far out -> head back
        now = time.time()
        if now > self.patrol_until:
            self.patrol = random.choice([d for d in DIRS if d != _OPPOSITE[self.patrol]])
            self.patrol_until = now + random.uniform(2.5, 6.0)
        vx, vy = DIRS[self.patrol]
        tile = (me[0] + vx, me[1] + vy)
        # Wandering is when we blunder into doorways -- it must respect the same tile bans as
        # deliberate movement, or every warp tile we just learned gets walked straight back on.
        if ((not allow_warp and self.is_warp(tile))
                or now - self.blocked.get(tile, 0) < WALL_TTL
                or not self.in_room(tile)):
            self.patrol_until = 0.0                    # pick a different heading next tick
            return None
        if self.step(self.patrol):
            return tile
        self.patrol_until = 0.0                        # blocked -> pick a new heading
        return None

    # ---------- main loop ----------
    def run(self, deadline=None):
        self.find_regions()
        # The FIRST room lookup harvests the whole heap for strings (~8s). Do it here, once,
        # rather than stalling the decision loop with it. Later lookups re-scan only the
        # block the name lived in and cost about a millisecond.
        self.check_room()
        last_pos = None
        stepped_into = None
        last_print = 0.0
        last_rescan = time.time()

        while True:
            if deadline and time.time() > deadline:
                log("time limit reached.")
                return
            if os.path.exists(P_STOP):
                os.remove(P_STOP)
                log("STOP file seen -- exiting cleanly.")
                return

            # PACK FULL -> keep hunting, but STOP trying to loot. The server said "you can't
            # have more than N" of an item (the acorns), so every ',' press is a no-op and
            # chasing drops just makes us thrash on tiles we can't clear. We KNOW we're full, so
            # don't press pickup or walk to a single drop -- just keep killing. Latches on the
            # first full message; a fresh message keeps it latched (it never un-fills itself
            # mid-session -- only selling does, which means a relaunch).
            if self.world is not None and getattr(self.world, "pack_full_ts", 0.0):
                if not self.pack_full:
                    item = getattr(self.world, "pack_full_item", None) or "an item"
                    log(f"PACK FULL of {item!r} -- can't hold more; will keep hunting but "
                        f"STOP picking up.")
                self.pack_full = True

            me = self.me()
            if me is None:
                time.sleep(0.3)
                continue
            if self.home is None:
                self.home = me
                log(f"home tile {me} (leash {self.leash})")

            # A map change teleports us; treat it as a fresh start rather than letting the
            # leash drag us at a tile that no longer exists.
            if last_pos and abs(me[0] - last_pos[0]) + abs(me[1] - last_pos[1]) > WARP_JUMP:
                # Learn the doorway so we stop re-walking it. Two candidates, and which one
                # is the real warp tile depends on client timing, so ban BOTH:
                #   * stepped_into -- the tile we tried to enter last tick
                #   * last_pos     -- the tile we were STANDING on when the jump fired
                # The logs settle it: "MAP CHANGE (8,0) -> (134,154)" means last_pos=(8,0)
                # warped us, yet only stepped_into was being recorded -- so (8,0) and (9,29)
                # kept warping us out every run while the harmless landing tile (8,1) got
                # banned instead. Banning the FROM tile matches the logged doorway exactly.
                # We never cross a warp on purpose here (one home room, no rotation), so every
                # map change is an unwanted warp and the tile that caused it is safe to ban.
                self.note_warp(self.room, stepped_into)
                self.note_warp(self.room, last_pos)
                log(f"MAP CHANGE {last_pos} -> {me}; new room, re-homing")
                self.home = me
                self.blocked.clear()
                self.fails.clear()
                self.target = None
                self.target_pos = None
                self.hit_uid = None
                self.collect = None
                # Tile memory is per-room -- coordinates in the new room mean something else
                # entirely, so keeping the old room's would mark this one already explored.
                self.seen.clear()
                self.unreachable.clear()
                self.goal = None
                self.room_ts = 0.0               # re-read the room name immediately
                # We arrive standing on the doorway back. That's this room's known exit, and
                # also the thing to walk away from before we accidentally take it again.
                self.exits = [me]
                self.arrived_at = me
                self.arrived_ts = time.time()
                self.waiting = False
                self.empty_since = 0.0

            # A step that changed nothing MIGHT be a wall -- or a keypress the client dropped
            # while it was busy. Only believe it on the second failure in a row; believing the
            # first one let lag mark all four neighbours as walls and freeze us on our tile.
            if stepped_into:
                if last_pos == me:
                    n = self.fails.get(stepped_into, 0) + 1
                    self.fails[stepped_into] = n
                    if n >= 2:
                        self.blocked[stepped_into] = time.time()
                else:
                    self.fails.pop(stepped_into, None)
            stepped_into = None

            # Truly wedged (nothing has moved us in 6s)? Drop every belief and shove.
            if last_pos != me or self.stuck_since == 0.0:
                self.stuck_since = time.time()
            elif time.time() - self.stuck_since > 6.0:
                log("stuck 6s -> clearing learned walls and re-heading")
                self.blocked.clear()
                self.fails.clear()
                self.patrol_until = 0.0
                self.stuck_since = time.time()
                self.step(random.choice(list(DIRS)))
            last_pos = me

            self.mark_seen(me)
            self.check_room()
            self.flush_data()
            mobs, items, creatures = self.entities()

            # TRUE position: the player block trails the pool by ~1 tile, which breaks melee
            # adjacency (esp. against edge squirrels). Learn our own entity, then measure
            # everything from ITS tile. Until it is known we use the block frame unchanged.
            self.learn_self_uid(me, creatures, mobs)
            me_ent = self.me_from_pool(me, creatures)
            if me_ent != me and time.time() - self.last_frame_log > 5.0:
                self.last_frame_log = time.time()
                log(f"frame: block says {me}, pool says {me_ent} "
                    f"(delta {abs(me_ent[0]-me[0]) + abs(me_ent[1]-me[1])}) -- using pool")
            me = me_ent

            # Subclass hook (default no-op): rabbit bot gates out when low and keeps its
            # self-cast buffs up here, before the inherited heal-through runs.
            if self.pre_survival(me, mobs, items, creatures):
                last_pos = me
                time.sleep(LOOP)
                continue

            # --- SURVIVAL -----------------------------------------------------------
            # Hurt? CAST SOOTHE AND KEEP FIGHTING. Three of the five squirrel rooms also hold
            # Leviathans, which hit for 400-600 against ~1264 HP, so the heal has to win the
            # race against incoming damage -- it does, because Shift+Z -> 'a' fires instantly
            # and we can chain three casts a second. Running away was the old behaviour and it
            # was simply wrong: it gave up the kill AND took hits on the way out.
            v = self.vitals()
            if v and v[1]:
                frac = v[0] / float(v[1])
                mana = (v[2] / float(v[3])) if v[3] else 0.0

                if frac < HEAL_HP and mana >= MANA_FLOOR \
                        and time.time() - self.last_heal > HEAL_CD:
                    cap = MAX_BURST_EMERG if frac < EMERG_HP else MAX_BURST
                    got = self.heal_to(TARGET_HP, cap)
                    nv = self.vitals()
                    if got and nv:
                        log(f"Soothe x{got}: hp {v[0]} -> {nv[0]}/{nv[1]} "
                            f"({nv[0] / float(nv[1]):.0%})  mana {nv[2]}/{nv[3]}")
                    if nv and nv[1]:
                        v, frac = nv, nv[0] / float(nv[1])
                        mana = (nv[2] / float(nv[3])) if nv[3] else 0.0
                    last_pos = me

                # Only when healing is off the table does position matter. Out of mana and
                # still dropping -> break contact and let mana regen; if it gets dire anyway,
                # stop, because a stopped bot beats a dead character.
                if mana < MANA_FLOOR and frac < HEAL_HP:
                    if frac < BAIL_HP:
                        log(f"HP {v[0]}/{v[1]} ({frac:.0%}) and mana {v[2]}/{v[3]} exhausted "
                            f"-- STOPPING rather than risk dying unattended.")
                        return
                    if not self.retreating:
                        self.retreating = True
                        log(f"HP {v[0]}/{v[1]} ({frac:.0%}), no mana to heal with -- "
                            f"disengaging until {SAFE_HP:.0%}")
                    close = [c for c in creatures
                             if abs(c[1] - me[0]) + abs(c[2] - me[1]) <= 4]
                    if close:
                        c = min(close, key=lambda c: abs(c[1] - me[0]) + abs(c[2] - me[1]))
                        away = (me[0] + (me[0] - c[1]), me[1] + (me[1] - c[2]))
                        stepped_into = self.toward(me, away)
                    else:
                        stepped_into = self.wander(me)
                    last_pos = me
                    time.sleep(LOOP)
                    continue
                if self.retreating and frac >= SAFE_HP:
                    self.retreating = False
                    log(f"HP recovered to {v[0]}/{v[1]} ({frac:.0%}) -- back to work")

                # Between fights, quietly top back up. Entering the next fight at full HP is
                # what keeps us out of the emergency branch in the first place.
                elif (frac < TOPOFF_HP and mana >= 0.30
                        and not any(abs(c[1] - me[0]) + abs(c[2] - me[1]) <= 2
                                    for c in creatures)
                        and time.time() - self.last_heal > 1.0):
                    self.heal_to(TARGET_HP, 2)
            # In a room we have no business in (Nagnang borders Southern Path): don't fight,
            # don't loot, just get out the way we came. Healing above still applies.
            if self.evicting:
                stepped_into = self.leave_room(me)
                last_pos = me
                time.sleep(LOOP)
                continue

            # Deliberately AFTER the survival branch: this can stall ~1s waiting on the
            # profile reply, and stalling with HP falling is how a character dies.
            self.capture_level()

            if time.time() - last_rescan > RESCAN_EVERY:
                last_rescan = time.time()
                self.find_regions()

            # A room that looks empty is the exact signature of scan blindness, so re-scan
            # before believing it. Believing it is also what sends us wandering to the map
            # edge hunting for a fuller room -- which is how we crossed a warp at (6,0).
            if mobs:
                self.empty_since = 0.0
            elif self.empty_since == 0.0:
                self.empty_since = time.time()
            elif time.time() - self.empty_since > ROOM_EMPTY_LEAVE:
                # STAY PUT. We do not rotate rooms: leaving Southern Path is not wanted, and
                # the room repopulates on its own. Just keep sweeping it and wait.
                if not self.waiting:
                    self.waiting = True
                    log(f"room dry for {ROOM_EMPTY_LEAVE:.0f}s -- staying in "
                        f"{self.room or 'this room'}, waiting for spawns")
            elif time.time() - self.empty_since > EMPTY_RESCAN and not self.rescanned_empty:
                self.rescanned_empty = True
                last_rescan = time.time()
                log(f"no squirrels for {EMPTY_RESCAN:.0f}s -- re-scanning entity regions "
                    f"before believing the room is empty")
                self.find_regions()
            if mobs:
                self.rescanned_empty = False
                if self.waiting:
                    self.waiting = False
                    log(f"squirrels back ({len(mobs)}) -- resuming")

            # --- did the thing we were hitting die? -------------------------------
            alive = {u for u, _, _ in mobs}
            if (self.hit_uid is not None and self.hit_uid not in alive
                    and time.time() - self.hit_ts <= KILL_WINDOW):
                # Only counts if we were actually swinging at it moments ago. Any vanish
                # would otherwise count -- a despawn, a walk out of range, or a map change
                # (which once logged three "kills" in one tick).
                self.kills += 1
                log(f"KILL #{self.kills} at {self.target_tile}")
                if self.target_tile:
                    self.collect = (self.target_tile[0], self.target_tile[1],
                                    time.time() + 10)
                # The corpse has now left the pool for real, so drop its hp-bar entry. Keeping
                # it would leave a permanent 0 against that entity id, and a later spawn that
                # reused the id would read as already-dead and never be attacked.
                if self.agent is not None:
                    self.agent.mobhp.pop(self.hit_uid, None)
                self.hit_uid = None
                if self.target not in alive:
                    self.target = None

            # --- decide ------------------------------------------------------------
            # STAY ON THE TARGET. Entity enumeration blips -- a mob mid-step can be missing
            # for a tick -- and dropping the target on the first miss made us abandon live
            # mobs and thrash between them. A mob that has been gone for TARGET_GRACE is
            # really gone; one missing for a single scan is not.
            # Everything is huntable except a squirrel standing exactly ON a known doorway --
            # walking onto that tile would teleport us. That's one or two tiles, not the whole
            # border: banning the border made every edge mob invisible and left the bot pacing.
            # A corpse still sitting in the client's entity pool is not a target. Without
            # this the bot re-acquired the mob it had just killed and fired another full
            # 3-press burst at it, every kill.
            now = time.time()
            # GHOST CHECK, before choosing anything. If the mob we've been meleeing has taken
            # our swings for GHOST_TIMEOUT without ever answering with an hp-bar, our swings
            # are landing on a stale slot -- ban it so we stop and go find a real serpent.
            if (self.swing_uid is not None
                    and now - self.swing_uid_since > GHOST_TIMEOUT
                    and (self.agent is None or self.agent.mobhp.get(self.swing_uid) is None)):
                self.ghost_ban[self.swing_uid] = now + GHOST_COOLDOWN
                log(f"GHOST target {self.swing_uid} -- swung {now - self.swing_uid_since:.1f}s "
                    f"with no hp-bar ever; banning and retargeting")
                if self.target == self.swing_uid:
                    self.target, self.target_pos = None, None
                self.swing_uid = None
            for u in [u for u, t in self.ghost_ban.items() if now >= t]:
                del self.ghost_ban[u]
            huntable = [m for m in mobs
                        if not self.is_warp((m[1], m[2])) and not self.target_dead(m[0])
                        and m[0] not in self.ghost_ban]
            for u in [u for u, t in self.chase_ban.items() if now >= t]:
                del self.chase_ban[u]            # ban expired
            tgt = None
            if self.target is not None:
                cur = [m for m in huntable if m[0] == self.target]
                if cur:
                    tgt = cur[0]
                    self.target_seen = now
                    self.target_pos = (cur[0][1], cur[0][2])
                    if abs(cur[0][1] - me[0]) + abs(cur[0][2] - me[1]) <= 1:
                        self.reached_target = True
                    # A runner we've chased since committing and NEVER reached is either
                    # fleeing or walled off. Give it up and refuse to re-pick it for a bit,
                    # so we stop orbiting one serpent while others beat on us.
                    elif (not self.reached_target
                          and now - self.target_since > CHASE_GIVEUP):
                        self.chase_ban[self.target] = now + CHASE_COOLDOWN
                        self.target, self.target_pos, tgt = None, None, None
                elif now - self.target_seen > TARGET_GRACE:
                    self.target, self.target_pos = None, None
                elif self.target_pos:
                    # keep walking to where it was; it usually reappears a tile over
                    tgt = (self.target, self.target_pos[0], self.target_pos[1])
            if tgt is None and huntable:
                # A squirrel standing ON a drop is worth a couple of extra steps: that drop
                # is unreachable until the squirrel is off it, so killing it frees the loot.
                guarding = {(i[1], i[2]) for i in items}
                pick = [m for m in huntable if m[0] not in self.chase_ban] or huntable
                tgt = min(pick, key=lambda m: (abs(m[1] - me[0]) + abs(m[2] - me[1])
                                               - (2 if (m[1], m[2]) in guarding else 0)))
                self.target = tgt[0]
                self.target_pos = (tgt[1], tgt[2])
                self.target_seen = now
                self.target_since = now
                self.reached_target = (abs(tgt[1] - me[0]) + abs(tgt[2] - me[1]) <= 1)

            # --- did our last pickup actually work? --------------------------------
            # The drop vanishing from the ground IS the confirmation; nothing else tells us.
            # A full pack makes ',' a no-op, so without this the bot stands on one acorn
            # pressing pickup forever instead of killing.
            if self.pickup_uid is not None and time.time() - self.pickup_ts > 0.6:
                if any(u == self.pickup_uid for u, _, _ in items):
                    n = self.no_loot.get(self.pickup_uid, 0) + 1
                    self.no_loot[self.pickup_uid] = n
                    # ONE stubborn drop is not a full pack -- it is usually someone else's
                    # kill, which we may never pick up. Only give up on this drop after two
                    # tries...
                    if n >= 2:
                        self.dead_drops.add(self.pickup_uid)
                        # ...and only give up on the whole ITEM TYPE once two DISTINCT drops
                        # of the same sprite have each refused us. A full acorn stack rejects
                        # every acorn, each arriving with a fresh uid, so per-uid giving-up
                        # never catches up -- the bot chased an unpickable acorn forever while
                        # standing full. Keyed on sprite, two failures is enough to know.
                        sp = self.item_look.get(self.pickup_uid)
                        if sp is not None:
                            fails = self.sprite_fails.setdefault(sp, set())
                            fails.add(self.pickup_uid)
                            if len(fails) >= FULL_AFTER and sp not in self.full_sprites:
                                self.full_sprites[sp] = time.time()
                                log(f"pack is FULL of item sprite {sp} "
                                    f"({len(fails)} drops refused) -- ignoring this item type "
                                    f"for {FULL_TTL:.0f}s, then re-testing")
                else:
                    self.looted += 1
                    # A successful pickup means the pack has room for this sprite after all
                    # (e.g. the acorns got sold) -- forget that it was ever full so we resume
                    # collecting it immediately, not FULL_TTL later.
                    sp = self.item_look.get(self.pickup_uid)
                    if sp is not None:
                        self.full_sprites.pop(sp, None)
                        self.sprite_fails.pop(sp, None)
                    log(f"picked up a drop  (total {self.looted})")
                self.pickup_uid = None

            # Expire stale "full" beliefs so a pack emptied mid-session is noticed.
            if self.full_sprites:
                now_f = time.time()
                for sp in [s for s, t in self.full_sprites.items() if now_f - t > FULL_TTL]:
                    del self.full_sprites[sp]
                    self.sprite_fails.pop(sp, None)
                    log(f"re-testing item sprite {sp} -- pack may have room again")

            # Drops worth going to, nearest first (ignoring ones we've failed to pick up).
            # A drop can be sitting UNDER a mob. That tile can't be walked onto while the mob
            # stands there, so chasing it just shoves at an occupied tile until we mark our own
            # neighbour a wall and stall. Skip those -- the target ranking above already sends
            # us to kill the occupant, and the tile frees up when it dies.
            occupied = {(c[1], c[2]) for c in creatures if (c[1], c[2]) != me}
            cands = [] if self.pack_full else sorted(
                           (i for i in items
                            if self.no_loot.get(i[0], 0) < 2
                            and self.item_look.get(i[0]) not in self.full_sprites
                            and (i[1], i[2]) not in occupied
                            and not self.is_warp((i[1], i[2]))),   # not worth teleporting for
                           key=lambda i: abs(i[1] - me[0]) + abs(i[2] - me[1]))
            here = next((i for i in cands if (i[1], i[2]) == me), None)
            near = next((i for i in cands
                         if abs(i[1] - me[0]) + abs(i[2] - me[1]) <= DETOUR), None)
            far = next((i for i in cands
                        if abs(i[1] - me[0]) + abs(i[2] - me[1]) <= LOOT_RANGE), None)
            # With nothing to hunt, a pickable drop ANYWHERE in the room is worth walking to.
            # LOOT_RANGE only exists to stop us abandoning a live mob for distant loot; when
            # there is no mob, capping the chase at 8 tiles just left the drop on the floor
            # while we swept the room at random and stumbled past it (measured: one acorn sat
            # through a full 20x30 sweep). cands is nearest-first, so cands[0] is the closest.
            idle_drop = cands[0] if (cands and not huntable) else None
            if self.collect and time.time() > self.collect[2]:
                self.collect = None

            tgt_dist = (abs(tgt[1] - me[0]) + abs(tgt[2] - me[1])) if tgt else None
            # Mid-fight with the thing we're actually hitting? Then a drop three tiles away can
            # wait. Breaking off to loot is how a live mob gets abandoned half-killed.
            engaged = (self.hit_uid is not None and self.target == self.hit_uid
                       and tgt is not None and time.time() - self.hit_ts < ENGAGED_FOR)

            # PRIORITY. Looting used to sit behind "is any squirrel visible", and in a room
            # holding 1-8 squirrels that branch never ran: 5 kills left 8 acorns on the floor
            # and exactly 1 pickup. A drop underfoot costs one keypress and a drop a couple of
            # tiles away costs a step or two, so both outrank walking to the next mob -- but
            # never outrank hitting the one already in reach.
            # HIT WHAT IS IN REACH FIRST. The committed target having walked out of melee is
            # no reason to stand there while a DIFFERENT serpent one tile away beats on us --
            # that was the bug: the bot chased a runner and ignored the mob actually hitting
            # it. Any adjacent huntable mob is swung immediately and adopted as the target, so
            # a swarm gets fought instead of orbited. Outranks even loot underfoot: taking a
            # serpent's hit to grab a drop is a bad trade.
            m = self.melee_pick(me, huntable)
            if m is None:
                self.swing_uid = None            # not meleeing anyone -> reset the ghost clock
            if m is not None:
                uid, mx, my = m
                if uid != self.target:
                    self.target, self.target_pos = uid, (mx, my)
                    self.target_since, self.reached_target = time.time(), True
                # Start the ghost clock the moment we begin swinging a new uid; a real serpent
                # will populate agent.mobhp before GHOST_TIMEOUT, a ghost never will.
                if uid != self.swing_uid:
                    self.swing_uid, self.swing_uid_since = uid, time.time()
                self.target_tile = (mx, my)
                d = ("right" if mx > me[0] else "left") if mx != me[0] else \
                    ("down" if my > me[1] else "up")
                self.swing(d, uid, me, (mx, my))
            elif here is not None:
                if self.pickup_uid is None:              # one attempt, then verify it worked
                    self.pickup()
                    self.pickup_uid, self.pickup_ts = here[0], time.time()
            elif self.collect is not None and not self.pack_full:  # go stand on what we killed
                cx, cy, _ = self.collect
                if (cx, cy) == me:
                    self.collect = None                  # nothing there after all
                else:
                    stepped_into = self.toward(me, (cx, cy))
            elif near is not None and not engaged:       # grab it on the way past
                stepped_into = self.toward(me, (near[1], near[2]))
            elif tgt is not None:
                self.target_tile = (tgt[1], tgt[2])
                stepped_into = self.toward(me, (tgt[1], tgt[2]))
            elif far is not None:                        # nothing to hunt -> tidy up drops
                stepped_into = self.toward(me, (far[1], far[2]))
            elif idle_drop is not None:                  # room quiet -> go fetch it, wherever
                self.target_tile = (idle_drop[1], idle_drop[2])
                stepped_into = self.toward(me, (idle_drop[1], idle_drop[2]))
            elif (self.arrived_at and time.time() - self.arrived_ts < ARRIVAL_GRACE
                    and abs(me[0] - self.arrived_at[0]) + abs(me[1] - self.arrived_at[1])
                    < ARRIVAL_CLEAR):
                # Get off the doorway first. The tiles NEXT to a warp are often warps too,
                # which is how we bounced (6,0) -> (134,154) -> (8,1) in about one second.
                ax, ay = self.arrived_at
                away = (me[0] + (me[0] - ax) * 3, me[1] + (me[1] - ay) * 3) \
                    if (me[0], me[1]) != (ax, ay) else (me[0], me[1] + ARRIVAL_CLEAR)
                stepped_into = self.toward(me, away)
            else:
                stepped_into = self.explore(me)

            if time.time() - last_print > 3.0:
                last_print = time.time()
                v = self.vitals()
                hp = f"{v[0]}/{v[1]}" if v else "?"
                mp = f"{v[2]}/{v[3]}" if v else "?"
                t = f"{self.target}@{self.target_tile}" if self.target else "-"
                print(f"pos={me} hp={hp} mp={mp} squirrels={len(mobs)} drops={len(items)} "
                      f"target={t} kills={self.kills} looted={self.looted} "
                      f"heals={self.heals}", flush=True)
                # When we believe the room is empty, show what's actually near us. If the
                # user can see a squirrel and this prints one at ty!=3 or look!=25, the
                # classifier is wrong; if it prints nothing near, the scan is region-blind.
                if not mobs:
                    self.diag_entities(me)

            time.sleep(LOOP)


def main():
    args = sys.argv[1:]

    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    secs = opt("--seconds")
    leash = int(opt("--leash", "20"))
    # Squirrels rarely hurt us enough to drop under HEAL_HP, so the healing path would go
    # untested in a short run. `--heal-at 0.99` forces it to fire immediately: proof the whole
    # chain works in-bot (cast lands, mana moves, and movement still works afterwards -- a
    # botched cast leaves the chat box open and silently eats every arrow key).
    global HEAL_HP, TARGET_LOOK
    if opt("--heal-at"):
        HEAL_HP = float(opt("--heal-at"))
        log(f"TEST MODE: healing threshold forced to {HEAL_HP:.0%}")
    if opt("--look"):
        TARGET_LOOK = int(opt("--look"))
        log(f"hunting sprite look={TARGET_LOOK} (not the default squirrel {SQUIRREL_LOOK})")
    stay = "--stay" in args

    wins = find_windows()
    if not wins:
        print("No live NexusTK.exe window found (client must be running + logged in).")
        return 1
    hwnd, pid = wins[0][0], wins[0][2]
    log(f"client hwnd={hwnd} pid={pid}")

    LW.migrate(LW.P_OUT)          # keep level_base.csv's header in step with LW.COLS
    agent = NA.Agent()
    world = NB.World(agent)
    attach_ts = time.time() * 1000        # nothing older than this is a reading of THIS run
    s, sc = NB.attach(NB.build_pump(world, agent), pid=pid)
    bot = Simple(sc.exports_sync, hwnd, leash=leash, agent=agent,
                 world=world, attach_ts=attach_ts)
    bot.stay = stay
    if stay:
        log("--stay: farming whatever room we start in (no eviction to squirrel rooms)")
    time.sleep(1.0)

    try:
        bot.run(deadline=(time.time() + float(secs)) if secs else None)
    except KeyboardInterrupt:
        log("stopped.")
    finally:
        # Persist whatever combat data is still in memory BEFORE detaching, on every exit
        # path -- stop file, Ctrl-C, time limit or the low-HP bail.
        bot.flush_data(force=True)
        try:
            s.detach()
            log("frida detached.")
        except Exception:
            pass
    log(f"session: {bot.kills} kills, {bot.looted} pickups, {bot.heals} heals")
    return 0


if __name__ == "__main__":
    sys.exit(main())
