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
    public ushort Sprite;      // creature graphic id — wire as u16 BE in the 0x33 type-1 appearance
    public byte   Extra;       // the trailing appearance byte (state/variant; 0 = default)
    public byte   Color;       // 0x07 palette/recolor byte (world mobs carry their registry colour)
    public ushort X, Y;
    public byte   Dir;         // facing 0=N 1=E 2=S 3=W
    public int    Hp;
    public int    MaxHp;
    public int    Exp;         // reward granted to the killer (0 for debug dummies / decorations)
    public bool   Alive => Hp > 0;

    // Shared-world AI: a wandering mob hops at random within a few tiles of its spawn (Home). Set on
    // world mobs (see World.Tick); session-local debug dummies leave Wander=false and never move.
    public ushort HomeX, HomeY;
    public bool   Wander;

    // Move pacing (RTK MobMoveTime): the minimum gap in milliseconds between move attempts. The world
    // accumulates elapsed tick time in MoveTimer and only lets the mob act when it reaches MoveTime, so a
    // rabbit (3000ms) hops far less often than the 600ms world heartbeat — matching RTK's per-mob timer.
    public int MoveTime = 2500;   // ms between move attempts (town critters ~2000-3000)
    public int MoveTimer;         // ms accumulated since the last attempt

    // Set by a paralyze/sleep debuff (Session.CastDebuff): the Environment.TickCount64 until which the mob is
    // frozen and won't wander. 0 = not debuffed. World.Tick skips movement while frozen.
    public long FrozenUntil;

    public Mob() { }

    public Mob(uint id, ushort sprite, ushort x, ushort y, string name, int hp, byte extra = 0)
    {
        Id = id; Sprite = sprite; X = x; Y = y; Name = name; Hp = hp; MaxHp = hp; Extra = extra;
        HomeX = x; HomeY = y;
    }
}
