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

    // ---- quest helpers (used by QuestDef.Talk scripts; see Server/Quests.cs) ---------------------
    /// <summary>This player's stage for a quest (0 = not started; a quest defines the rest).</summary>
    public int  Stage(string questKey) => _s.QuestStage(questKey);
    /// <summary>Set this player's stage for a quest (persists).</summary>
    public void SetStage(string questKey, int stage) => _s.SetQuestStage(questKey, stage);
    /// <summary>A quest progress counter (e.g. "trial_of_iron.kills"); 0 if unset.</summary>
    public int  Counter(string counterKey) => _s.QuestCounter(counterKey);

    /// <summary>Award experience (updates the HUD + persists).</summary>
    public void AwardExp(uint amount)  => _s.AwardExp(amount);
    /// <summary>Award coin (updates the HUD + persists).</summary>
    public void AwardGold(uint amount) => _s.AwardGold(amount);

    /// <summary>How many of an item (by content key) the player holds.</summary>
    public int  CountItem(string itemKey) => _s.CountItem(itemKey);
    /// <summary>Consume <paramref name="amount"/> of an item by key; false if the player hasn't that many.</summary>
    public bool TakeItem(string itemKey, int amount) => _s.TakeItem(itemKey, amount);
    /// <summary>Give a reward item by key; false if the item is unknown or the pack is full.</summary>
    public bool GiveItem(string itemKey, int amount = 1) => _s.GiveRewardItem(itemKey, amount);

    /// <summary>Lifetime kills for a mob key (RTK <c>player:killCount</c>). Quests compare a snapshot delta.</summary>
    public int  KillCount(string mobKey) => _s.KillCount(mobKey);

    /// <summary>An int-valued quest registry entry (RTK registry), 0 if unset. General store for quest
    /// bookkeeping (counters, snapshots, timers) — distinct from <see cref="Stage"/>'s quest-stage meaning.</summary>
    public int  Reg(string key) => _s.QuestCounter(key);
    public void SetReg(string key, int value) => _s.SetQuestStage(key, value);

    /// <summary>A string-valued quest registry entry (RTK registryString), "" if unset.</summary>
    public string QuestStr(string key) => _s.QuestStr(key);
    public void   SetQuestStr(string key, string value) => _s.SetQuestStr(key, value);

    /// <summary>Does the player have the legend with this internal name?</summary>
    public bool HasLegend(string name) => _s.HasLegend(name);
    /// <summary>Add (or replace by name) a legend mark.</summary>
    public void AddLegend(string text, string name, byte icon, byte color) => _s.AddLegend(text, name, icon, color);
    /// <summary>Remove the legend with this internal name.</summary>
    public void RemoveLegend(string name) => _s.RemoveLegend(name);

    /// <summary>The player's level.</summary>
    public int  Level => _s.CharLevel;
    /// <summary>The "power" number quests gate on (RTK baseMagic*2 + baseHealth analog).</summary>
    public int  Stat  => _s.CharStat;
    /// <summary>Subpath mark count (0 for now).</summary>
    public int  Mark  => _s.CharMark;
    /// <summary>Random int in [1, maxInclusive].</summary>
    public int  Random(int maxInclusive) => _s.QuestRandom(maxInclusive);
    /// <summary>Wall-clock seconds since the Unix epoch (for cooldown timers).</summary>
    public long NowUnix => _s.NowUnix;
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

/// <summary>Surfaces this NPC's quests (from <see cref="Quests.ForNpc"/>) as menu entries — one per quest,
/// its label reflecting the player's progress — and runs the quest's <see cref="QuestDef.Talk"/> script when
/// picked. Added automatically to any NPC that has quests (see <see cref="NpcScripts.For"/>), so a quest is
/// wired end to end just by listing it under a giver in <see cref="Quests.ByNpc"/>.</summary>
public sealed class QuestAbility : INpcAbility
{
    public static readonly QuestAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        foreach (var q in Quests.ForNpc(ctx.Def.Id))
        {
            var quest = q;   // capture per-iteration for the closure
            int stage = ctx.Stage(quest.Key);
            string label = stage >= QuestDef.Done ? $"{quest.Name} (done)"
                         : stage > QuestDef.NotStarted ? $"{quest.Name} (in progress)"
                         : quest.Name;
            yield return (label, c => quest.Talk(c));
        }
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
