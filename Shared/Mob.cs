namespace Shared;

/// <summary>
/// A world creature (squirrel, rabbit, …). Rendered via the 0x33 <b>type-1 "creature form"</b>
/// (client parser 0x4361b0): a single u16 sprite id + one trailing byte, unlike a player which uses
/// the 7-byte composite (type-0). The server owns the authoritative HP; the client only draws the
/// sprite and plays the actions/numbers we send it. Mobs are transient (not persisted) — they live
/// in <c>Session._mobs</c> for the duration of a session and are removed on death (0x0E despawn).
/// </summary>
public sealed class Mob
{
    public uint   Id;
    public string Name   = "";
    public string Key    = "";  // MobDef identifier ("squirrel", "white_rabbit") — used for quest kill-matching
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
    public long FrozenUntil;

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
    public uint OwnerId;
    public long PetExpiresAt;

    // True only for a mob the owner CONJURED (a CotW summon / a Giasomo bird). A mob that was merely
    // mind-controlled — Endear and its poet variants, which set OwnerId on a creature that was already
    // standing there — leaves this false, and that distinction is what World.Tick keys the expiry on:
    // RTK's cotw pet vanishes when its spawnTime passes, but endear's `uncast` only does
    // `mob.owner = 0; mob.target = 0` — the creature stays in the world and turns on you again.
    public bool Summoned;

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
    public double DamageAmp;
    public long   DamageAmpUntil;

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
    public uint TargetId;

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

    // Copied from MobDef.Flees at spawn (data/game-data/MobFlees.csv): a PREY creature — a rabbit, a blue
    // rooster. The opposite end of the scale from Aggressive, and mutually exclusive with it in practice: it
    // never holds a target and never swings, it BACKS AWAY from any player who gets close (World.Tick's flee
    // branch, ported from RTK Mobs/mob.lua RunAway) and bolts at double pace once spooked. Nothing in RTK's own
    // data marks these creatures — RTK gives a rabbit a wolf's AI — so the flag is ours; see Content.LoadMobFlees.
    public bool Flees;

    // While Environment.TickCount64 is under this, a fleeing mob moves on HALF its usual MoveTime (RTK
    // mysterious_merchant on_attacked: `mob.newMove = 500` — a spooked creature drops to a much shorter timer
    // than its idle wander pace). Set by World.Spook whenever a player swings at it, hit or miss, and refreshed
    // by each further swing. 0 = calm.
    public long PanicUntil;

    public int  Level;         // copied from MobDef.Level at spawn — exp/display only, NOT melee damage (see MinDam/MaxDam)
    public int  AttackTime = 2000;   // ms between swings once adjacent to its target
    public int  AttackTimer;

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
    /// <paramref name="amount"/> uses the player-side convention where a positive `armor` means BETTER defence;
    /// a mob's <see cref="Ac"/> is signed lower-is-better, so armor improves it by subtracting. `might` has no mob
    /// field, so it maps to the flat per-swing damage range. Shared by Session.CastTargetBuff and World.Tick so
    /// apply and revert can never drift.</summary>
    public void AdjustBuffField(string stat, int amount, int sign)
    {
        switch (stat)
        {
            case "armor": Ac -= amount * sign; break;                                  // +armor => lower (better) Ac
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
