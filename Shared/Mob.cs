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

    // Poet "Call of the Wild" pet-summon family (RTK Spells/poet/cotw_*.lua): a normal shared-world Mob,
    // just tagged with who summoned it (World.PetCountFor's spawn cap) and when it auto-expires (World.Tick
    // despawns it via World.DespawnMob, no kill/loot). 0 = not a pet. Combat-assist/threat-transfer (RTK
    // cotw_controller_poet) isn't ported — the pet is a real, correctly-statted companion, but fights
    // independently (normal Aggressive/wander AI) rather than sharing its owner's target.
    public uint OwnerId;
    public long PetExpiresAt;

    // Combat AI (RTK's threat/target model, mob_ai_normal.lua: on_attacked sets the target, move/attack
    // chase and swing at it): 0 = passive wander. World.TryDamage sets this to the attacker's player id on a
    // landed hit; World.Tick then has the mob abandon wandering to path toward and melee that player instead,
    // until it dies, logs off, or strays past ChaseLeash. Aggressive mobs ALSO get TargetId set unprovoked —
    // World.Tick scans for a nearby player each move tick (RTK mob.c mob_find_target, gated on the engine-level
    // MobBehavior==1 "type", which is separate from and runs before the mob_ai_normal.lua script ever executes).
    public uint TargetId;

    // Copied from MobDef.Aggressive at spawn (RTK MobBehavior==1): scans for and locks onto any player within
    // AggroRadius each move tick, rather than only fighting back once hit. Most real monsters are aggressive;
    // herd/prey critters (rabbit, deer, squirrel, …) are the passive exception.
    public bool Aggressive;
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

    public Mob() { }

    public Mob(uint id, ushort sprite, ushort x, ushort y, string name, int hp, byte extra = 0)
    {
        Id = id; Sprite = sprite; X = x; Y = y; Name = name; Hp = hp; MaxHp = hp; Extra = extra;
        HomeX = x; HomeY = y;
    }
}
