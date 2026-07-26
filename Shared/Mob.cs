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

    // Combat AI (RTK's threat/target model, mob_ai_normal.lua: on_attacked sets the target, move/attack
    // chase and swing at it): 0 = passive wander. World.TryDamage sets this to the attacker's player id on a
    // landed hit; World.Tick then has the mob abandon wandering to path toward and melee that player instead,
    // until it dies, logs off, or strays past ChaseLeash — mirroring "fights back" rather than unprovoked
    // aggro (nothing here attacks a player who never hit it first, matching every normal-mob AI reviewed).
    public uint TargetId;
    public int  Level;         // copied from MobDef.Level at spawn — drives its melee damage (World.Tick)
    public int  AttackTime = 2000;   // ms between swings once adjacent to its target
    public int  AttackTimer;

    // Copied from MobDef.Will at spawn — RTK's per-mob magic-resist stat (Session.RollDeflect). RTK's mob
    // struct also carries a separate "protection" stat we have no source data for, so it's treated as 0.
    public int Will;

    public Mob() { }

    public Mob(uint id, ushort sprite, ushort x, ushort y, string name, int hp, byte extra = 0)
    {
        Id = id; Sprite = sprite; X = x; Y = y; Name = name; Hp = hp; MaxHp = hp; Extra = extra;
        HomeX = x; HomeY = y;
    }
}
