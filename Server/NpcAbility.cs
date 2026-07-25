using Shared;

namespace Server;

/// <summary>
/// The API an NPC behaviour (an <see cref="INpcAbility"/> or a unique script) uses to talk to the player.
/// It's a thin, awaitable facade over the owning <see cref="Session"/>'s dialog primitives, bound to one
/// NPC, so ability code reads as linear script — <c>var c = await ctx.Menu(...); if (c == 1) ...</c> — with
/// no packet or continuation plumbing. One context is created per click (<see cref="Session.RunNpcAsync"/>).
/// </summary>
public sealed class NpcContext
{
    private readonly Session _s;
    private readonly Mob _npc;

    /// <summary>The NPC's static definition (name, identifier, shop/bank flags, …).</summary>
    public NpcDef Def { get; }

    internal NpcContext(Session s, Mob npc, NpcDef def) { _s = s; _npc = npc; Def = def; }

    /// <summary>Show a prompt + picker buttons; returns the 1-based pick (0 = the player cancelled).</summary>
    public Task<int> Menu(string prompt, IReadOnlyList<string> options) => _s.DlgMenu(_npc, prompt, options);

    /// <summary>Show a text box and wait for the player to close it.</summary>
    public Task Say(string text) => _s.DlgSay(_npc, text);

    /// <summary>Run the NPC's buy flow (its <see cref="Shops"/> catalogue, resolved by identifier).</summary>
    public Task Buy() => _s.DlgBuy(_npc, Shops.For(Def.Key));

    /// <summary>Run the sell flow (the player's droppable, sellable inventory).</summary>
    public Task Sell() => _s.DlgSell(_npc);

    /// <summary>Run the bank/vault flow (deposit &amp; withdraw coin and items).</summary>
    public Task Bank() => _s.DlgBank(_npc);
}

/// <summary>
/// A reusable NPC feature (shopkeeping, banking, transport, …). An NPC is COMPOSED of abilities in
/// <see cref="NpcScripts"/>; each ability contributes zero or more entries to the NPC's top menu and
/// supplies the behaviour behind each. This is how shared features live in ONE place and NPCs declare
/// only what they are — not how each feature works.
/// </summary>
public interface INpcAbility
{
    IEnumerable<(string label, Func<NpcContext, Task> run)> Entries(NpcContext ctx);
}

/// <summary>Buy + Sell, backed by the NPC's <see cref="Shops"/> catalogue. Contributes nothing if the NPC
/// has no catalogue (so a shop-flagged NPC we haven't stocked simply shows no buy/sell options).</summary>
public sealed class ShopAbility : INpcAbility
{
    public static readonly ShopAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (Shops.For(ctx.Def.Key) is null) yield break;
        yield return ("Buy",  c => c.Buy());
        yield return ("Sell", c => c.Sell());
    }
}

/// <summary>Weapon/armour repair. Stub until the durability-repair flow is built.</summary>
public sealed class RepairAbility : INpcAbility
{
    public static readonly RepairAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Fix Item", c => c.Say("Bring me your worn gear — repairs aren't open yet."));
    }
}

/// <summary>Vault storage for coin + items (deposit / withdraw), persisted per character.</summary>
public sealed class BankAbility : INpcAbility
{
    public static readonly BankAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Banking", c => c.Bank());
    }
}

/// <summary>Waypoint fast-travel. Stub until the waypoint network is built.</summary>
public sealed class TransportAbility : INpcAbility
{
    public static readonly TransportAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Transport", c => c.Say("Transport isn't available yet."));
    }
}

/// <summary>Tells the current server date + time (a real, self-contained feature many NPCs share).</summary>
public sealed class TimeAbility : INpcAbility
{
    public static readonly TimeAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Date & Time", c => c.Say($"It is {DateTime.Now:dddd, MMMM d} — {DateTime.Now:h:mm tt}."));
    }
}

/// <summary>Ad-hoc entries unique to one NPC (a quest option, a one-off line), so a bespoke NPC can add
/// its own menu items without needing a whole ability class.</summary>
public sealed class InlineAbility : INpcAbility
{
    private readonly (string, Func<NpcContext, Task>)[] _entries;
    public InlineAbility(params (string label, Func<NpcContext, Task> run)[] entries) { _entries = entries; }
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => _entries;
}
