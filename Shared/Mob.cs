namespace Shared;

/// <summary>
/// A world creature (squirrel, rabbit, …). Rendered via the 0x33 <b>type-1 "creature form"</b>
/// (client parser 0x4361b0): a single u16 sprite id + one trailing byte, unlike a player which uses
/// the 7-byte composite (type-0). The server owns the authoritative HP; the client only draws the
/// sprite and plays the actions/numbers we send it. Mobs are transient (not persisted) — they live
/// in <c>Session._mobs</c> for the duration of a session and are removed on death (0x0E despawn).
/// <c>internal set</c> keeps other assemblies out; World and Session share an assembly, so the
/// World/Session boundary is convention, checked by <c>Tests/MobAiLockTests.cs</c>.
/// </summary>
public sealed class Mob
{
    public uint   Id;
    public string Name   = "";
    public string Key    = "";  // MobDef identifier ("squirrel", "white_rabbit") — used for quest kill-matching
    // MobDef.Id, the numeric RTK mob id. Not interchangeable with Key: six identifiers cover several ids
    // apiece (buya_library_mob is one name for 198-203), so a spawn group counting its population by Key
    // would treat six different creatures as one. RTK counts `mobBlocks[i].mobID == mobs[z]`, and so do we.
    public int    DefId;

    // Gathering-node claim (Session.Harvest; RTK AI/crafting/*.lua's registry["attacker"]/["attackerTime"]).
    // A node belongs to the first harvester for two minutes: nobody else's tool works on it, and when the
    // claim lapses the node heals back to full so a half-mined vein is never left chipped. Meaningless for
    // ordinary creatures, which never set it.
    public uint   HarvestClaimBy;
    public long   HarvestClaimUntil;   // Environment.TickCount64 ms; 0 = unclaimed
    public ushort Sprite;      // creature graphic id — wire as u16 BE in the 0x33 type-1 appearance
    public byte   Extra;       // the trailing appearance byte (state/variant; 0 = default)
    public byte   Color;       // 0x07 palette/recolor byte (world mobs carry their registry colour)
    public ushort X, Y;
    public byte   Dir;         // facing 0=N 1=E 2=S 3=W
    public int    Hp;
    public int    MaxHp;
    public int    Exp;         // reward granted to the killer (0 for debug dummies / decorations)
    public bool   Alive => Hp > 0;

    // NPCs are stationary "mobs that don't fight": they ride the exact same 0x07 creature render + viewport
    // streaming as a real mob, but World.TryDamage rejects them (indestructible) and a click opens their
    // dialog instead of a profile. NpcDefId is the RTK NpcId (Content.NpcById) for dialog/shop lookup.
    public bool   IsNpc;
    public int    NpcDefId;

    // Shared-world AI: a wandering mob hops at random within a few tiles of its spawn (Home). Set on
    // world mobs (see World.Tick); session-local debug dummies leave Wander=false and never move.
    public ushort HomeX, HomeY;
    public bool   Wander;
    // Max Chebyshev distance a wanderer may stray from Home before being leashed back. Mobs use the world
    // default (2); pacing NPCs carry their RTK NpcReturnDistance so a roaming merchant ranges wider than a
    // town critter.
    public int    Leash = 2;

    // Move pacing (RTK MobMoveTime): the minimum gap in milliseconds between move attempts. The world
    // accumulates elapsed tick time in MoveTimer and only lets the mob act when it reaches MoveTime, so a
    // rabbit (3000ms) hops far less often than the 600ms world heartbeat — matching RTK's per-mob timer.
    public int MoveTime = 2500;   // ms between move attempts (town critters ~2000-3000)
    public int MoveTimer;         // ms accumulated since the last attempt

    // Set by a paralyze/sleep debuff (Session.CastDebuff): the Environment.TickCount64 until which the mob is
    // frozen and won't wander. 0 = not debuffed. World.Tick skips movement while frozen.
    public long FrozenUntil { get; internal set; }

    // Set by a Rogue poison trap (RTK NPCs/trap/rogue_traps/poison_dart_trap.lua): a damage-over-time tick
    // that fires every 1500ms until PoisonUntil, dealing PoisonTickDam each time — capped so it can never
    // actually finish a kill (RTK: only ticks while current HP > the tick amount). World.Tick drives this.
    public long PoisonUntil;
    public long PoisonNextTick;
    public int  PoisonTickDam;
    public uint PoisonOwnerId;   // caster's player id — credited with a poison-DOT kill (mirrors Trap.OwnerId)

    // Who this creature belongs to — a Poet's "Call of the Wild" summon (RTK Spells/poet/cotw_*.lua) or a
    // creature taken by Endear & kin. 0 = nobody's. Besides the spawn cap (World.PetCountFor) and the expiry
    // timer, this is what drives the PET AI in World.Tick: an owned mob never targets its owner, fights
    // whatever is fighting its owner, and otherwise follows them; its kills credit the owner's exp, and its
    // owner's own swings pass straight through it (Session.ResolveSwing). That mirrors RTK's own pet AI,
    // which lives in its LUA layer (AI/mob_ai_cotw.lua: assist whatever holds threat on the owner, else the
    // owner's last attacker, else walk to the owner; never fight a player; vanish if the owner leaves) —
    // the C engine's mob_find_target scans players only and never looks at the owner, so reading mob.c alone
    // gives the false impression RTK has no pet behaviour at all. Until this was wired up a CotW pet just
    // wandered off while an Endear'd creature re-aggro'd the poet who charmed it a tick later.
    // RTK's cotw_controller_poet (aggro redirect + mass dismiss) is still deliberately NOT ported: it is
    // later-server, and in 4.95 these creatures leave play ONLY by being killed or by this timer.
    public uint OwnerId { get; internal set; }
    public long PetExpiresAt { get; internal set; }

    // True only for a mob the owner CONJURED (a CotW summon / a Giasomo bird). A mob that was merely
    // mind-controlled — Endear and its poet variants, which set OwnerId on a creature that was already
    // standing there — leaves this false, and that distinction is what World.Tick keys the expiry on:
    // RTK's cotw pet vanishes when its spawnTime passes, but endear's `uncast` only does
    // `mob.owner = 0; mob.target = 0` — the creature stays in the world and turns on you again.
    public bool Summoned { get; internal set; }

    // True for a creature the WORLD placed — a static spawn point, a trap-ambush point, or a batch spawn
    // group — and false for every ad-hoc one (a summon, a dismounted horse, a GM's colour-test dummy).
    // Only these drop loot. It used to be implicit: drops were rolled inside the "did this mob belong to a
    // spawn point" branch, so a conjured pet dropped nothing purely because it had no point. Once the
    // hunting maps stopped using points that accident stopped protecting anything, and the intent had to
    // become explicit or every cave mob would have quietly gone lootless.
    public bool WorldSpawned;

    // Items a player HANDED to this creature (the 0x29 hand gesture — there's no real mob inventory otherwise).
    // They ride on the creature and fall to the ground when it is KILLED (World.TryDamage), so a sword handed
    // to a cat is recoverable by killing the cat; a no-kill DespawnMob (ridden away, quest release) does NOT
    // drop them. Null until something is handed — almost no mob ever carries one. Each entry keeps its own
    // Dura/CustomName/Owner so a bound item stays bound through the hand-and-drop.
    public List<InvItem>? Handed;

    // Set by the Blind family (RTK Spells/NPCs/blind.lua + mage blind/dark_veil/winter's shadow/ice glare,
    // all of which just set `target.blind = true` for a duration). A blinded creature cannot SEE, so
    // World.Tick drops any target it had, skips its unprovoked-aggro scan, and — unlike a frozen mob, which
    // is held rigid — leaves it standing still rather than wandering: with no sight there is nowhere to go.
    // It can still lash out at whatever it can REACH (a cardinally-adjacent player), turning to face them.
    // 0 = not blinded.
    public long BlindUntil;

    /// <summary>Categorised status slots (RTK <c>checkIfCast</c>): category -> the TickCount64 it lapses at.
    /// The mob-side twin of a player's <c>ActiveBuff.Category</c>, and the reason a second blind/paralyze/
    /// curse can't simply be re-applied on top of a running one. Categories are the same strings the spell
    /// data uses — "blinds" · "paras" · "sleeps" · "venoms" · "curses" · "minorcurses" · "disheartens" …
    /// <para>Deliberately separate from <see cref="FrozenUntil"/> / <see cref="BlindUntil"/> / <see
    /// cref="PoisonUntil"/>, which are what the AI reads: those are the MECHANIC, this is the EXCLUSIVITY
    /// bookkeeping, and they can differ (a boss's doze is shortened to 2s but still occupies the slot).</para>
    /// Null until something is applied — most mobs never carry a status, and this is per-mob on maps that
    /// hold thousands of them.
    /// <para>The slot remembers WHICH SPELL filled it, not just when it lapses, so a refusal can tell you
    /// whether you are re-casting your own running spell ("You already cast that spell.") or bouncing off a
    /// different one that got there first ("Another spell of this type is in effect."). RTK draws the same
    /// distinction — paralyze.lua answers the former on its own <c>target.paralyzed</c>, static.lua the
    /// latter — it just couldn't express it in general, having only one boolean flag per mechanic.</para></summary>
    public Dictionary<string, MobStatus>? Statuses;

    /// <summary>One occupied status slot: when it lapses, and the spell key that put it there.</summary>
    public readonly record struct MobStatus(long Until, string Key);

    /// <summary>Is a status of <paramref name="category"/> still running on this mob?</summary>
    public bool HasStatus(string category, long now) =>
        Statuses is not null && Statuses.TryGetValue(category, out var s) && s.Until > now;

    /// <summary>The spell key currently occupying <paramref name="category"/>'s slot, or "" if it is free.</summary>
    public string StatusKey(string category, long now) =>
        Statuses is not null && Statuses.TryGetValue(category, out var s) && s.Until > now ? s.Key : "";

    /// <summary>Free a slot immediately, before its timer runs out (the sleep-breaks-on-damage path).</summary>
    public void ClearStatus(string category)
    {
        if (Statuses is not null && Statuses.ContainsKey(category)) Statuses[category] = new MobStatus(0, "");
    }

    /// <summary>Damage amplifier left by a sleep-family hold: the next attack on this creature is multiplied
    /// by it, and lands the creature awake. NexusAtlas gives Doze 1.3x and Sleep 1.5x ("The next attack upon
    /// the target will do 1.3x the normal damage"). This IS RTK's <c>target.sleep = 1.3</c> and the
    /// <c>sd->sleep != 1.0f</c> guards throughout its C — a float whose default 1.0 doubles as the "not held"
    /// flag, which is exactly why reading it as a boolean makes the whole mechanic disappear.
    /// 1.0 (or 0, unset) = no amplification.</summary>
    public double DamageAmp { get; internal set; }
    public long   DamageAmpUntil { get; internal set; }

    /// <summary>Consume the amplifier if one is armed: returns the multiplier and clears it, so it applies to
    /// exactly ONE hit — "the NEXT attack", not every attack for the duration.</summary>
    public double TakeDamageAmp(long now)
    {
        if (DamageAmp <= 1.0 || DamageAmpUntil <= now) return 1.0;
        double a = DamageAmp;
        DamageAmp = 0; DamageAmpUntil = 0;
        return a;
    }

    /// <summary>Occupy <paramref name="category"/>'s slot until <paramref name="until"/> (TickCount64), on
    /// behalf of <paramref name="spellKey"/> (blank for a non-spell source such as a trap).</summary>
    public void SetStatus(string category, long until, string spellKey = "")
    {
        if (string.IsNullOrEmpty(category)) return;
        (Statuses ??= new())[category] = new MobStatus(until, spellKey);
    }

    // A repeating over-head effect, driven by World.Tick, for statuses whose animation is supposed to keep
    // playing for as long as they hold rather than firing once at cast. This is RTK's `while_cast` hook: venom
    // re-sends its animation on every 1500ms poison tick, and doze/sleep re-send theirs on every spell-timer
    // tick. Without it a 50-second hold looks identical to a fizzle after the first frame.
    // 0 = nothing repeating. FxRepeatEvery is the cadence in ms.
    public long FxRepeatUntil;
    public long FxRepeatNext;
    public int  FxRepeatEvery;
    public int  FxRepeatAnim;
    public int  FxRepeatSound;

    /// <summary>Start (or replace) the repeating over-head effect. <paramref name="everyMs"/> is the cadence;
    /// the first replay lands one cadence AFTER the cast, since the cast already drew frame one.</summary>
    public void SetFxRepeat(int anim, int sound, int everyMs, long until, long now)
    {
        if (anim <= 0 || everyMs <= 0) { FxRepeatUntil = 0; return; }
        FxRepeatAnim = anim; FxRepeatSound = sound; FxRepeatEvery = everyMs;
        FxRepeatUntil = until; FxRepeatNext = now + everyMs;
    }

    // Combat AI (RTK's mob_ai_normal.lua targeting: on_attacked sets the target, move/attack
    // chase and swing at it): 0 = passive wander. World.TryDamage sets this to the attacker's player id on a
    // landed hit; World.Tick then has the mob abandon wandering to path toward and melee that player instead,
    // until it dies, logs off, or strays past ChaseLeash. Aggressive mobs ALSO get TargetId set unprovoked —
    // World.Tick scans for a nearby player each move tick (RTK mob.c mob_find_target, gated on the engine-level
    // MobBehavior==1 "type", which is separate from and runs before the mob_ai_normal.lua script ever executes).
    public uint TargetId { get; internal set; }

    // ---- threat (RTK AI/threat.lua) --------------------------------------------------------------
    // How much grief each player has caused this creature, accumulated from damage dealt (RTK's
    // swing.lua `player:addThreat(mob.ID, damage)` and global_attack.lua's `threat + damage`). RTK's
    // mob_ai_normal re-runs threat.calcHighestThreat on every move and attack, so a mob fights whoever has
    // hurt it MOST rather than whoever hurt it LAST — which is the whole reason a group can peel a mob off
    // the person who pulled it. Null until something actually lands a hit: there are ~21k mobs in the world
    // and almost none of them are ever in a fight, so this must not allocate up front.
    public Dictionary<uint, long>? Threat;

    /// <summary>Add to a player's threat on this creature. Ignores 0 ids (debug/engine damage with no
    /// attacker) so those can never win a retarget.</summary>
    public void AddThreat(uint playerId, long amount)
    {
        if (playerId == 0 || amount <= 0) return;
        Threat ??= new Dictionary<uint, long>();
        Threat[playerId] = Threat.GetValueOrDefault(playerId) + amount;
    }

    /// <summary>This player's threat on the creature; 0 if they have never touched it.</summary>
    public long ThreatOf(uint playerId) =>
        Threat is not null && Threat.TryGetValue(playerId, out var v) ? v : 0;

    /// <summary>Wipe one player's grudge (RTK <c>setThreat(mob.ID, 0)</c>) — what Amnesia does, and what a
    /// passive creature does when its quarry leaves the map.</summary>
    public void ClearThreat(uint playerId) => Threat?.Remove(playerId);

    // Amnesia (RTK Spells/rogue/amnesia.lua): the mob has FORGOTTEN one specific player — their threat is
    // gone and it will not target them again until this lapses, though it still fights everyone else
    // normally. Hitting it again breaks the spell (RTK on_takedamage_while_cast). 0 = not amnesiac.
    public uint AmnesiaBy { get; internal set; }
    public long AmnesiaUntil { get; internal set; }   // Environment.TickCount64 ms

    /// <summary>Is this player currently forgotten by this creature?</summary>
    public bool HasForgotten(uint playerId, long nowMs) =>
        AmnesiaBy != 0 && AmnesiaBy == playerId && nowMs < AmnesiaUntil;

    /// <summary>Earliest tick this creature may cast again (MobSpells.csv <c>EveryMs</c>). RTK paces its
    /// casters off the wall clock (<c>os.time() % 15 == 0</c>), which fires for every boss in the world on
    /// the same second; a per-mob timer is the same cadence without the thundering herd.</summary>
    public long SpellReadyAt;

    // Mythic boss survival (RTK mob_ai_mythic + Spells/last_stand.lua). LastStandUntil is the 8-second window
    // a boss enters the first time a blow would kill it: it scrubs its own curses, PARALYSES ITSELF and heals
    // every tick until the window closes. SecondWindUsed is RTK's `mob.magic == 100` gate — the spell costs
    // 100 magic and nothing gives it back, so a boss gets exactly one per life.
    public long LastStandUntil;
    public bool SecondWindUsed;
    public long ParaBreakAt;    // next tick this boss may heal through a hold (RTK's `os.time() % 3` cadence)
    public long CurseShrugAt;   // next tick this boss may scrub its own curses (RTK's `os.time() % 10`)

    /// <summary>Walking home after giving up a chase (RTK <c>mob_ai_basic.move</c>'s <c>mob.returning</c>).
    /// A creature that broke off a pursuit is standing outside its wander leash, where every wander candidate
    /// tile fails the leash test — without this it would never move again. While it is set the creature
    /// sprints back to its spawn tile (RTK <c>mob.newMove = 250</c>) and ignores the leash on the way.</summary>
    public bool Returning;

    // The sideways shuffle a blocked chaser is currently committed to (World.StepMobToward): which way, and
    // how many more tiles of it are left. 0xFF = not shuffling. This exists ONLY to vary the length of the
    // shuffle — without a run counter every shuffle is exactly one tile out and one tile back, because the
    // step that closes on the target always wins the next tick. It is NOT wall-following and must not become
    // it: a chaser is meant to stay stupid (see World.StepMobToward).
    public byte DetourDir = 0xFF;
    public byte DetourLeft;

    // The MOB this mob is fighting — the other half of targeting, used by owned creatures (a Poet's Call of
    // the Wild summon or an Endear'd captive) when they assist their owner against whatever is attacking
    // them. Kept as its OWN field rather than overloading TargetId: the two id spaces don't overlap (players
    // are small character ids, World.AllocateMobId starts at 100,000 — the same split RTK makes with
    // MOB_START_NUM), but no reader should have to infer which kind of id it is holding. At most one of the
    // two is non-zero. See World.Tick's pet-AI block.
    public uint TargetMobId;

    // Copied from MobDef.Aggressive at spawn (RTK MobBehavior==1): scans for and locks onto any player within
    // AggroRadius each move tick, rather than only fighting back once hit. Most real monsters are aggressive;
    // herd/prey critters (rabbit, deer, squirrel, …) are the passive exception.
    public bool Aggressive;

    // Copied from MobDef.Flees at spawn (game-data/MobFlees.csv): a PREY creature — a rabbit, a blue
    // rooster. The opposite end of the scale from Aggressive, and mutually exclusive with it in practice: it
    // never holds a target and never swings, it BACKS AWAY from any player who gets close (World.Tick's flee
    // branch, ported from RTK Mobs/mob.lua RunAway) and bolts at double pace once spooked. Nothing in RTK's own
    // data marks these creatures — RTK gives a rabbit a wolf's AI — so the flag is ours; see Content.LoadMobFlees.
    public bool Flees;

    // While Environment.TickCount64 is under this, a prey creature is SPOOKED: it notices players from twice
    // as far off (World.FleeRadius) and so keeps running after you have stopped chasing. It does NOT change
    // how far its flee-dart carries — distance per turn is fixed (World.PreyDartTiles). Set by World.Spook
    // whenever a player swings at it, hit or miss, and refreshed by each further swing. 0 = calm.
    public long PanicUntil;

    // ---- Sute's bespoke boss AI (Server/SuteAi.cs) ----------------------------------------------
    // Only ever touched for the one mob whose Key is "sute", so every other creature carries these as four
    // untouched words. They live here rather than in a side table because the AI runs inside World.Tick's
    // hot loop, where a per-mob dictionary probe every tick would cost more than the fields do.
    /// <summary>Which leg of the hit-and-run cycle Sute is on — see <c>SuteAi.Phase</c>.</summary>
    public byte SutePhase;
    /// <summary>TickCount64 at which the current retreat/hold leg ends.</summary>
    public long SutePhaseUntil;
    /// <summary>Swings left in the current burst (hit-and-run), or retaliation swings owed while fleeing.</summary>
    public int  SuteSwingsLeft;
    /// <summary>Tiles of the current retreat still to walk.</summary>
    public int  SuteRetreatLeft;
    /// <summary>Earliest TickCount64 at which the wounded self-heal may fire again.</summary>
    public long SuteHealReadyAt;
    /// <summary>Set when a retreat/flee step could not be taken — Sute is cornered, and fights instead of
    /// running (the "unless trapped" half of his flee behaviour).</summary>
    public bool SuteCornered;

    /// <summary>TickCount64 until which a fresh blow cannot buy another answering swing (see
    /// SuteAi.RetaliateLockoutMs). Without it he is pinned in place and never breaks off at all.</summary>
    public long SuteRetaliateLockedUntil;

    /// <summary>Which beat of Sute's action rhythm this is (see SuteAi.BeatsPerCycle). He acts on the first
    /// two beats of every three and rests on the third, which is what makes his movement and his swings come
    /// in pairs rather than as a steady stream.</summary>
    public byte SuteBeat;

    public int  Level;         // copied from MobDef.Level at spawn — exp/display only, NOT melee damage (see MinDam/MaxDam)
    public int  AttackTime = 2000;   // ms between swings once adjacent to its target
    public int  AttackTimer;

    // The swing interval this creature was SPAWNED with. AttackTime is writable so an AI can speed a mob up
    // for a burst (Server/SuteAi.cs) — this is what it restores afterwards, so the boosted value can never
    // become the new normal by being read back into itself.
    public int  BaseAttackTime = 2000;

    // Copied from MobDef.MinDam/MaxDam at spawn (RTK MobMinimumDamage/MobMaximumDamage) — the actual per-swing
    // damage range, rolled via World.MobSwingDamage (RTK swingDamage.lua _getMobSwingDamage: three uniform
    // draws over the thirded range, summed). Unrelated to Level — a level-99 dragon's real threat is here.
    public int MinDam = 1, MaxDam = 1;

    // Copied from MobDef.Hit at spawn (RTK MobHit) — as ATTACKER, feeds this mob's own crit-chance roll
    // (RTK hitCritChance.lua: a mob's critical-hit odds are hit/5, on top of the base hit-chance roll that
    // gates whether a crit can happen at all — though real RTK swingDamage.lua never actually multiplies
    // MOB damage by its own crit, only a PLAYER's; see Combat.RollCritChance's doc for why).
    public int Hit;

    // Copied from MobDef.IsBoss at spawn (RTK MobIsBoss) — selects the attacking PLAYER's weapon Large-damage
    // range (minLDam/maxLDam) instead of Small (minSDam/maxSDam), RTK swingDamage.lua _getPlayerSwingDamage.
    public bool IsBoss;

    // Copied from MobDef.Grace at spawn — read as the DEFENDER's grace in a player's crit-chance roll
    // (Session.PlayerSwingDamage -> Combat.RollCritChance) when they attack this mob. Present in the source
    // CSV all along but, like MinDam/MaxDam, never actually parsed until this pass.
    public int Grace;

    // Copied from MobDef.Will/Protection at spawn — RTK's per-mob magic-resist stats, both folded into
    // Session.RollDeflect. Will has always been wired in; Protection previously had no source column.
    public int Will;
    public int Protection;

    // Copied from MobDef.Ac at spawn (RTK MobArmor) — the mob's OWN melee defense, signed/lower-is-better
    // same as Character.Ac. This is a DIFFERENT stat from Protection above: Ac reduces an incoming melee
    // swing (Session.HandleAttack's armor deduction, floored at -95 same as RTK's mob-target minimumArmor);
    // Protection only affects magic resist. Both were 0 for every mob before this — RTK's mob struct
    // carries them separately and neither had a source column until the CTK SQL dump was merged in.
    public int Ac;

    // Timed stat buffs applied to this mob by a player's targeted buff (Session.CastTargetBuff — e.g. casting
    // Valor/Harden Armor on your pet). Each entry directly mutated a stat field on apply; World.Tick reverts the
    // delta when ExpiresAt passes (so combat reads the raw fields, no per-hit "effective" recompute). Null until
    // the first buff lands, to avoid an allocation on every mob. Refresh-not-stack is keyed by Key (spell key).
    public sealed class TimedBuff { public string Stat = ""; public int Amount; public long ExpiresAt; public string Key = ""; }
    public List<TimedBuff>? Buffs;

    /// <summary>Apply (sign=+1) or revert (sign=-1) a targeted-buff stat delta onto this mob's raw combat fields.
    /// <paramref name="amount"/> is an AC DELTA in the same signed lower-is-better units as <see cref="Ac"/>
    /// itself — damage taken is raw x (1 + ac/100), so a warding buff is NEGATIVE and a curse POSITIVE — and it
    /// therefore just adds. `might` has no mob field, so it maps to the flat per-swing damage range. Shared by
    /// Session.CastTargetBuff and World.Tick so apply and revert can never drift.</summary>
    public void AdjustBuffField(string stat, int amount, int sign)
    {
        switch (stat)
        {
            case "armor": Ac += amount * sign; break;                                  // -armor => lower (better) Ac
            case "might": MinDam += amount * sign; MaxDam += amount * sign; break;      // no mob Might -> flat damage
        }
    }

    public Mob() { }

    public Mob(uint id, ushort sprite, ushort x, ushort y, string name, int hp, byte extra = 0)
    {
        Id = id; Sprite = sprite; X = x; Y = y; Name = name; Hp = hp; MaxHp = hp; Extra = extra;
        HomeX = x; HomeY = y;
    }
}
