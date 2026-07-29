using MoonSharp.Interpreter;

namespace Server;

/// <summary>
/// The Lua-facing facade an item-effect verb (<c>data/game-data/item_verbs.lua</c>) uses to act on the game —
/// the consumable-use analogue of <see cref="SpellContext"/>. Created per use, bound to one eater + the item
/// being consumed. Exposes a small, safe set of engine primitives (heal, restore mana, set a timed status ward,
/// warp home, …) plus read-only eater stats, so a verb reads as linear script:
/// <code>ctx:animate(); ctx:heal(row.amount or ctx.maxHp)</code>
/// Member names are lower-cased to read naturally from Lua (<c>ctx.maxHp</c>, <c>ctx:heal(n)</c>). Every
/// primitive delegates to an existing <see cref="Session"/> path, so the Lua route shares the exact same
/// plumbing the old C# <c>ApplyItemEffect</c> switch used — no divergent behavior.
/// </summary>
[MoonSharpUserData]
public sealed class ItemContext
{
    private readonly Session _s;

    internal ItemContext(Session s) => _s = s;

    // ---- read-only eater stats (Lua: ctx.maxHp, ctx.armor, ...) ----
    public double level => _s.LuaLevel;
    public double might => _s.LuaMight;
    public double hp    => _s.LuaHp;
    public double maxHp => _s.LuaMaxHp;
    public double mp    => _s.LuaMp;
    /// <summary>Clamped effective armor (RTK's harden-body success roll reads this).</summary>
    public double armor => _s.ItemArmor;

    // ---- primitives (Lua: ctx:heal(n), ctx:setStatus(k, ms), ...) ----
    /// <summary>Play the shared eat/use animation + sound on self and peers (RTK action 8).</summary>
    public void animate()                    => _s.ItemEatAnim();
    /// <summary>The harden-body cast pose on self (RTK action 6).</summary>
    public void castPose()                   => _s.ItemCastPose();
    /// <summary>Heal the eater's own HP (capped at effective max), "satiated" notice at full.</summary>
    public void heal(double amt)             => _s.ItemHeal((int)System.Math.Round(amt));
    /// <summary>Restore the eater's own mana (capped at effective max).</summary>
    public void restoreMana(double amt)      => _s.LuaRestoreMana((int)System.Math.Round(amt));
    /// <summary>Spend some of the eater's HP (never below 1 — the drink/smoke MP-for-HP trade).</summary>
    public void loseHp(double amt)           => _s.ItemLoseHp((int)System.Math.Round(amt));
    /// <summary>Kill the eater outright (poison_apple's always-lethal joke effect).</summary>
    public void kill()                       => _s.ItemKill();
    /// <summary>Is the named timed status ward currently active on the eater?</summary>
    public bool hasStatus(string key)        => _s.ItemHasStatus(key);
    /// <summary>Set/refresh a timed status ward for <paramref name="ms"/> milliseconds.</summary>
    public void setStatus(string key, double ms) => _s.ItemSetStatus(key, (int)ms);
    /// <summary>A percent success roll (1..100 &lt;= pct), for RTK's armor-scaled harden-body gate.</summary>
    public bool chance(double pct)           => _s.ItemChance((int)System.Math.Round(pct));
    /// <summary>Warp the eater to a random tavern in their nation (RTK returnToInn).</summary>
    public void warpHome()                   => _s.ItemWarpHome();
    /// <summary>Status-box text (RTK sendminitext).</summary>
    public void say(string msg)              => _s.LuaSay(msg);
    /// <summary>Chat-log message.</summary>
    public void message(string msg)          => _s.LuaMessage(msg);
}
