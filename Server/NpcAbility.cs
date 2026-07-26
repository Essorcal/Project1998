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

    /// <summary>Show one or more text pages (the player clicks through) with the NPC's own portrait, waiting
    /// for the last to close (RTK dialogSeq with the npc graphic).</summary>
    public Task Say(params string[] pages) => _s.DlgSayNpc(_npc, pages);

    /// <summary>Like <see cref="Say"/> but drawn with a creature-look portrait (RTK convertGraphic(look,"monster")).</summary>
    public Task SayLook(int look, int color, params string[] pages) => _s.DlgSayLook(_npc, look, color, pages);

    /// <summary>Like <see cref="Say"/> but drawn with an item-icon portrait (RTK Item(key).icon).</summary>
    public Task SayItem(string itemKey, params string[] pages) => _s.DlgSayItem(_npc, itemKey, pages);

    /// <summary>Run the NPC's buy flow (its <see cref="Shops"/> catalogue, resolved by identifier).</summary>
    public Task Buy() => _s.DlgBuy(_npc, Shops.For(Def.Key));

    /// <summary>Run the sell flow (the player's droppable, sellable inventory).</summary>
    public Task Sell() => _s.DlgSell(_npc);

    /// <summary>Run the bank/vault flow (deposit &amp; withdraw coin and items).</summary>
    public Task Bank() => _s.DlgBank(_npc);

    /// <summary>Spoken "buy [my] [all|N] &lt;item&gt;" shortcut: sell `amount` (or the whole stack, if &lt;= 0)
    /// of a fuzzy-matched item by name. False if nothing in the bag matched the name, so the speech falls
    /// through instead of being silently swallowed.</summary>
    public Task<bool> SellByName(string name, int amount) => _s.SellItemToNpcByName(_npc, name, amount);

    /// <summary>Spoken "take my &lt;item|coin&gt; [count]" shortcut: deposit `amount` (or the whole stack, if
    /// &lt;= 0) of a fuzzy-matched item — or coin, if the word is "coin"/"coins" — into the vault.</summary>
    public Task<bool> Deposit(string item, int amount) => _s.DepositItemToBank(_npc, item, amount);

    /// <summary>Spoken "give my &lt;item|coin&gt; [count]" shortcut: withdraw `amount` (or the whole stack, if
    /// &lt;= 0) of a fuzzy-matched item — or coin, if the word is "coin"/"coins" — from the vault.</summary>
    public Task<bool> Withdraw(string item, int amount) => _s.WithdrawItemFromBank(_npc, item, amount);

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
    /// <summary>The player's sex byte (RTK player.sex; used to pick sex-specific quest items).</summary>
    public int  Sex => _s.CharSex;
    /// <summary>The player's nation/kingdom id (RTK player.country; 1 = Koguryo/Kugnae).</summary>
    public int  Nation => _s.CharNation;

    /// <summary>Coin on hand (RTK player.money).</summary>
    public uint Coins => _s.CharCoins;
    /// <summary>Spend coin if the player can afford it; false (no change) if they can't.</summary>
    public bool SpendGold(uint amount) => _s.SpendGold(amount);

    /// <summary>Does the player carry at least <paramref name="n"/> of an item (by key)?</summary>
    public bool HasItem(string itemKey, int n = 1) => _s.CountItem(itemKey) >= n;
    /// <summary>Is an item (by key) currently worn?</summary>
    public bool HasEquipped(string itemKey) => _s.HasEquipped(itemKey);
    /// <summary>Display name of an item by key (for dialog).</summary>
    public string ItemName(string itemKey) => _s.ItemName(itemKey);
    /// <summary>Warp the player to a map/tile; false if that map isn't renderable here (no strand).</summary>
    public bool Warp(int map, int x, int y) => _s.Warp((ushort)map, (ushort)x, (ushort)y);

    /// <summary>The player's current tile (for warps that keep the same position).</summary>
    public int  X => _s.CharX;
    public int  Y => _s.CharY;
    /// <summary>Send the player a status/minitext line (RTK sendMinitext).</summary>
    public void Notify(string text) => _s.Notify(text);
    /// <summary>Make this NPC speak an over-head bubble (RTK npc:talk), rather than open a dialog box.</summary>
    public void Bubble(string text) => _s.NpcBubble(_npc, text);

    /// <summary>Prompt the player for a line of text (RTK inputSeq); null if they cancelled.</summary>
    public Task<string?> Input(string prompt) => _s.DlgInput(_npc, prompt);

    // ---- class / path + title + spell-learning (used by ClassTrainerAbility; RTK *_trainer.lua) ---
    /// <summary>The player's path id (0 = Peasant, 1 Warrior / 2 Rogue / 3 Mage / 4 Poet); -1 if unknown.</summary>
    public int ClassId => _s.CharClassId;
    /// <summary>Set the player's path (RTK updatePath) — changes the profile class line, persists.</summary>
    public void SetClass(int pathId) => _s.SetCharClass(pathId);
    /// <summary>The player's current noble title ("" if none).</summary>
    public string Title => _s.CharTitle;
    /// <summary>Set the player's noble title (RTK setTitle), persisted.</summary>
    public void SetTitle(string title) => _s.SetCharTitle(title);

    /// <summary>Spells the player can learn now (class + level, minus known) — the Learn Secret menu.</summary>
    public List<SpellDef> LearnableSpells() => _s.LearnableClassSpells();
    /// <summary>Spells the player's class unlocks at higher levels — the Divine Secret preview.</summary>
    public List<SpellDef> FutureSpells() => _s.FutureClassSpells();
    /// <summary>Spells the player currently knows — the Forget Secret menu.</summary>
    public List<SpellDef> KnownSpells() => _s.KnownSpellList();
    /// <summary>Teach one spell; false if the spellbook is full.</summary>
    public bool LearnSpell(SpellDef sp) => _s.LearnSpellFromNpc(sp);
    /// <summary>Forget one spell (resyncs the book so later slots realign).</summary>
    public void ForgetSpell(int spellId) => _s.ForgetOneSpell(spellId);
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

/// <summary>An ability that also responds to the player SPEAKING near the NPC (RTK onSayClick — e.g. saying
/// "i'd like to fish" to Bate, or a tutor's name to the librarian). <see cref="OnSay"/> returns true if it
/// consumed the speech (ran a dialog); the dispatcher then stops, so unrelated speech falls through to normal
/// chat. Implemented alongside <see cref="INpcAbility"/> when an NPC has both a click menu and a spoken trigger.</summary>
public interface INpcSayHandler
{
    Task<bool> OnSay(NpcContext ctx, string speech);
}

/// <summary>Shared empty menu for speech-only NPCs (they respond to <see cref="INpcSayHandler"/> but add no
/// click options — clicking just shows the default greeting).</summary>
internal static class NoClickMenu
{
    public static readonly (string, Func<NpcContext, Task>)[] None = System.Array.Empty<(string, Func<NpcContext, Task>)>();
}

/// <summary>Buy + Sell, backed by the NPC's <see cref="Shops"/> catalogue. Contributes nothing if the NPC
/// has no catalogue (so a shop-flagged NPC we haven't stocked simply shows no buy/sell options).</summary>
public sealed class ShopAbility : INpcAbility, INpcSayHandler
{
    public static readonly ShopAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (Shops.For(ctx.Def.Key) is null) yield break;
        yield return ("Buy",  c => c.Buy());
        yield return ("Sell", c => c.Sell());
    }

    // Spoken shortcut for selling TO the shop without opening the menu — a real NexusTK command, e.g.
    // "buy my all acorns" (sell every acorn), "buy my 5 acorns" (sell 5), "buy my acorn" (sell 1). "my" is
    // optional filler; "all" is a quantifier and always needs an item after it — bare "buy my all" (no item)
    // isn't a valid command. Independent of this NPC's own Buy catalogue — any shop-flagged NPC buys
    // anything sellable, same as the Sell menu.
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (!speech.StartsWith("buy")) return false;
        string rest = speech["buy".Length..].Trim();
        if (rest.StartsWith("my ")) rest = rest["my ".Length..].Trim();
        else if (rest == "my") rest = "";
        if (rest.Length == 0) return false;

        int amount = 1;   // default: sell one, if no quantifier is given
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[0] == "all")
        { amount = -1; rest = string.Join(' ', parts[1..]); }                              // -1 = whole stack
        else if (parts.Length > 1 && int.TryParse(parts[0], out var n) && n > 0)
        { amount = n; rest = string.Join(' ', parts[1..]); }
        else if (parts.Length == 1 && parts[0] == "all")
        { return false; }                                                                   // "all" needs an item

        if (rest.Length == 0) return false;
        return await ctx.SellByName(rest, amount);
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
public sealed class BankAbility : INpcAbility, INpcSayHandler
{
    public static readonly BankAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Banking", c => c.Bank());
    }

    // Spoken "take my <item|coin> [count]" (deposit) / "give my <item|coin> [count]" (withdraw) — a real
    // NexusTK command, e.g. "take my coin 500" (deposit 500 coin), "give my all acorns" (withdraw every
    // acorn), "take my acorn" (deposit 1). "all" is a prefix quantifier (before the item); a trailing number
    // is the count instead — this ordering is the opposite of the shop's "buy [N] <item>" and is exactly how
    // the real command works, not a typo.
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech.StartsWith("take my "))
        {
            var (item, amount) = ParseItemAmount(speech["take my ".Length..]);
            return item.Length != 0 && await ctx.Deposit(item, amount);
        }
        if (speech.StartsWith("give my "))
        {
            var (item, amount) = ParseItemAmount(speech["give my ".Length..]);
            return item.Length != 0 && await ctx.Withdraw(item, amount);
        }
        return false;
    }

    // "[all] <item words...> [trailing count]": leading "all" -> whole stack (amount <= 0); else a trailing
    // integer -> that many; else -> 1 (bare "take my acorn" deposits a single one).
    private static (string item, int amount) ParseItemAmount(string rest)
    {
        rest = rest.Trim();
        if (rest.StartsWith("all ")) return (rest["all ".Length..].Trim(), -1);

        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && int.TryParse(parts[^1], out var n) && n > 0)
            return (string.Join(' ', parts[..^1]), n);

        return (rest, 1);
    }
}

/// <summary>Waypoint fast-travel. Stub — RTK's Waypoint.lua network didn't exist in 4.x/5.x NexusTK, so it
/// isn't ported; this only exists so InnNpc's composition (which has always offered "Transport") has
/// something to show until a period-accurate travel feature is identified.</summary>
public sealed class TransportAbility : INpcAbility
{
    public static readonly TransportAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Transport", c => c.Say("Transport isn't available yet."));
    }
}

/// <summary>Fishing (RTK fishnpc.lua / Bate &amp; Wim). Ports the beginner branch: a chance per cast at a
/// minnow + the <c>learned_to_fish</c> flag (the tutorial's stage-4 requirement). The level-15+ pole/bait/skill
/// system, magical fish, and stuck-line death aren't modelled. While the player is on tutorial stage 4 the
/// catch is guaranteed, so the tutorial doesn't hinge on the 10% roll.</summary>
public sealed class FishAbility : INpcAbility, INpcSayHandler
{
    public static readonly FishAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("I'd like to fish", Fish);
    }

    // Spoken trigger (RTK: "i'd like to fish"). The tutorial tells the player to say it out loud.
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech is not ("fish" or "i'd like to fish" or "id like to fish")) return false;
        await Fish(ctx);
        return true;
    }

    private static async Task Fish(NpcContext ctx)
    {
        await ctx.Say("You're still a youngin'! If you take up fishing now, you'll never amount to anything. " +
                      "Oh, why not? Here's some string and worms for you to try with, good luck!");

        // RTK beginner odds are 5/50 (10%). Guarantee it while the player is on the tutorial's fishing stage so
        // completing the quest isn't a grind.
        bool guaranteed = ctx.Stage("tutorial_quest") == 4;
        bool caught = guaranteed || ctx.Random(50) <= 5;

        if (caught)
        {
            ctx.SetReg("learned_to_fish", 1);
            ctx.GiveItem("minnow", 1);
            await ctx.Say("You caught a fish!");
        }
        else
        {
            await ctx.Say("You fish for quite a while, but with little success.");
        }
    }
}

/// <summary>A class-path trainer (RTK warrior_trainer.lua / rogue_trainer / mage_trainer / poet_trainer).
/// Ports the new-player core: <b>Become a &lt;Class&gt;</b> at level 5 (sex-specific starter kit + 500 gold +
/// path change), <b>Learn / Divine / Forget Secret</b> (the trainer's spell-teaching), and <b>Become Noble</b>
/// (the level-75 title grant). One instance per base path (1 Warrior / 2 Rogue / 3 Mage / 4 Poet). The
/// repeatable Minor Quest is a separate <see cref="MinorQuestAbility"/> composed alongside this one. NOT ported:
/// the level-66+ star/moon/sun armor chains and the nagnang trials — they depend on subsystems we don't model
/// (karma, crafting ranks, carnage wins, marriage/partner, mentoring).</summary>
public sealed class ClassTrainerAbility : INpcAbility
{
    private readonly int _path;                         // base path id, 1..4
    private readonly string _class;                     // "Warrior" / "Rogue" / "Mage" / "Poet"
    private readonly string _sanctuary;                 // "…, the sanctuary of X." (the become-intro flavor)
    private readonly string[] _pitch;                   // the "Tell me more" paragraphs
    private readonly string _weapon;                    // starter weapon item key
    private readonly (string male, string female) _armor, _helm;   // sex-specific starter armor + helm keys
    private readonly (string key, int qty) _food;       // starter consumable (bear's liver / herb pipe)
    private readonly string _foodBlurb;                 // the closing line describing that consumable

    private ClassTrainerAbility(int path, string cls, string sanctuary, string[] pitch, string weapon,
        (string, string) armor, (string, string) helm, (string, int) food, string foodBlurb)
    { _path = path; _class = cls; _sanctuary = sanctuary; _pitch = pitch; _weapon = weapon;
      _armor = armor; _helm = helm; _food = food; _foodBlurb = foodBlurb; }

    private const string BearBlurb =
        "I have also given you some Bear's livers, these will help you keep your strength up. Eat one when you " +
        "are feeling weak, and near death. Shop keepers around town sell them if you need more.";
    private const string PipeBlurb =
        "You also have herb pipes, these will replenish your mana. Once they are used up you should buy some " +
        "more, shop keepers around town sell them.";

    public static readonly ClassTrainerAbility Warrior = new(1, "Warrior", "the sanctuary of the mightiest of all fighters",
        new[] {
            "Tell you about warriors? Well, they are the greatest of the fighter classes. A one man army, so to speak. Warriors are fierce, and powerful, and can battle many foes at once.",
            "Warriors use little magic, instead we prefer to use skills, such as the ability to hit more than one creature at a time.",
            "We depend on the healing skills of other paths, like the poets, but they are always willing to group with a warrior for our awesome killing abilities." },
        "sword_of_power", ("jade_scale_mail", "summer_mail_dress"), ("merchant_helm", "spring_helmet"),
        ("bears_liver", 25), BearBlurb);

    public static readonly ClassTrainerAbility Rogue = new(2, "Rogue", "the sanctuary of the swiftest blades",
        new[] {
            "Tell you about rogues? Well, they are the deadliest of the fighter classes. Nimble, agile, fast, and unmatched one on one, a true assassin.",
            "Rogues use some magic during their battles, and many skills for attacking a foe. We only attack one at a time, but we kill quickly, and efficiently, moving too quick to be hit easily.",
            "We can solo single creatures with great skill, for larger battles we need a little help from a healer." },
        "swift_dagger", ("merchant_waistcoat", "summer_blouse"), ("merchant_helm", "spring_helmet"),
        ("bears_liver", 26), BearBlurb);

    public static readonly ClassTrainerAbility Mage = new(3, "Mage", "the sanctuary of the great magic users",
        new[] {
            "Tell you about mages? Well, mages are the magic users of the land, combining great offensive and defensive magic.",
            "We use magic to subdue our foes, and to conquer all who stand before us. We can also use our great powers defensively, to heal and save ourselves, or others.",
            "The mage is a self contained hunter, and can easily solo hunt without the aid of others, however it is always best to join others - safety in numbers!" },
        "staff_of_power", ("summer_garb", "summer_dress"), ("merchant_helm", "spring_helmet"),
        ("herb_pipe", 4), PipeBlurb);

    public static readonly ClassTrainerAbility Poet = new(4, "Poet", "the sanctuary of the healer",
        new[] {
            "Tell you about poets? Poets are the most sought after path, wanted by every other path to join them in adventures.",
            "Poets are masters of defense with the ability to heal and protect large numbers of people easily.",
            "Higher level poets gain the ability to charm animals, and can become an incredible power themselves if they have the skill." },
        "staff_of_defense", ("summer_robes", "summer_gown"), ("merchant_helm", "spring_helmet"),
        ("herb_pipe", 4), PipeBlurb);

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        int cls = ctx.ClassId;
        if (cls <= 0)                    // Peasant (0) or unknown -> can choose this path
            yield return ($"Become a {_class}", Become);
        else if (cls == _path)           // your own class's trainer -> teach/foresee its secrets
        {
            yield return ("Learn Secret", LearnSecret);
            yield return ("Divine Secret", DivineSecret);
        }
        yield return ("Forget Secret", ForgetSecret);   // any book, any trainer (RTK shows this always)
        yield return ("Become Noble", BecomeNoble);     // level-75 title, any trainer
    }

    private async Task Become(NpcContext ctx)
    {
        if (ctx.Level < 5)
        { await ctx.Say("Hail, little one! Please return to me when you have reached the 5th insight."); return; }

        await ctx.Say(
            $"Hail, mighty one! Welcome to my sanctuary, {_sanctuary}.",
            $"Have you come to pick your path? I think you would make a great {_class.ToLower()}, and a great hero.");

        int c = await ctx.Menu($"Will you join the path of the {_class.ToLower()}?", new[] { "Yes", "Tell me more", "No" });
        if (c == 1) { await GrantKit(ctx); return; }
        if (c == 2)
        {
            await ctx.Say(_pitch);
            int c3 = await ctx.Menu("Will you join us now?", new[] { "Yes", "No" });
            if (c3 == 1) await GrantKit(ctx);
            else await ctx.Say("Very well, I will be waiting here if you change your mind. I am seeking great people all the time to join this great path.");
            return;
        }
        await ctx.Say("Very well, I will be waiting here if you change your mind. I am seeking great people all the time to join this great path.");
    }

    private async Task GrantKit(NpcContext ctx)
    {
        await ctx.Say("Great! You have made a great decision. I see you becoming a great hero in these lands. Now let me set you up with some supplies.");

        bool female = ctx.Sex == 1;   // 0 = male, 1 = female (confirmed for the tutorial sex-item)
        ctx.GiveItem(_weapon, 1);
        ctx.GiveItem(female ? _armor.female : _armor.male, 1);
        ctx.GiveItem(female ? _helm.female : _helm.male, 1);
        if (_food.qty > 0) ctx.GiveItem(_food.key, _food.qty);
        ctx.AwardGold(500);
        ctx.SetClass(_path);          // RTK updatePath(_path, 0) — changes the profile class line

        await ctx.Say(
            $"Here is some armor, and a weapon. These are specific to the {_class.ToLower()} path, and will help get you started.",
            "I have also given you some gold, it's all I can spare right now. It will help you with repairs, and getting some other equipment like rings.",
            _foodBlurb,
            "If you wish to learn some skills let me know, I can teach you many things to help you in battle.");
    }

    // "Learn Secret" (RTK learnSpell): pick from the spells this class can learn at or below your level.
    private static async Task LearnSecret(NpcContext ctx)
    {
        var learn = ctx.LearnableSpells();
        if (learn.Count == 0)
        { await ctx.Say("You have learned every secret I can teach you for now. Grow stronger, then return."); return; }

        int pick = await ctx.Menu("Which secret shall I teach you?",
            learn.Select(s => $"{s.Name} (Lv {s.Level})").ToList());
        if (pick < 1 || pick > learn.Count) return;

        var sp = learn[pick - 1];
        if (!ctx.LearnSpell(sp)) { await ctx.Say("Your mind cannot hold any more secrets right now."); return; }
        await ctx.Say($"You have learned {sp.Name}.");
    }

    // "Divine Secret" (RTK futureSpells): a read-only preview of what this class unlocks at higher levels.
    private static async Task DivineSecret(NpcContext ctx)
    {
        var fut = ctx.FutureSpells();
        if (fut.Count == 0) { await ctx.Say("There are no further secrets awaiting you."); return; }
        await ctx.Say("These secrets await you as you grow in power:",
            string.Join("\n", fut.Select(s => $"{s.Name} — insight {s.Level}")));
    }

    // "Forget Secret" (RTK forgetSpell): drop one spell/skill from the book (works on any known ability).
    private static async Task ForgetSecret(NpcContext ctx)
    {
        var known = ctx.KnownSpells();
        if (known.Count == 0) { await ctx.Say("You know no secrets to forget."); return; }

        int pick = await ctx.Menu("Which secret do you wish to forget?", known.Select(s => s.Name).ToList());
        if (pick < 1 || pick > known.Count) return;

        var sp = known[pick - 1];
        ctx.ForgetSpell(sp.Id);
        await ctx.Say($"You have forgotten {sp.Name}.");
    }

    // "Become Noble" (RTK general_npc_funcs.setTitle): a level-75 custom title, 200 gold per character.
    private static async Task BecomeNoble(NpcContext ctx)
    {
        if (ctx.Level < 75)
        { await ctx.Say("You are still young, and not ready for this yet. Return when you have gained your 75th level."); return; }

        string? title = await ctx.Input("Your heart is in the right place. Which title shall you take?");
        if (string.IsNullOrWhiteSpace(title)) return;
        title = title.Trim();
        if (title.Length > 12) { await ctx.Say("Your entered title must be no greater than 12 characters."); return; }

        uint cost = (uint)(200 * title.Length);
        int c = await ctx.Menu($"For that title, {cost} coins are required. You want to do that?", new[] { "Yes", "No" });
        if (c != 1) return;

        if (ctx.Coins < cost) { await ctx.Say($"You do not have the required {cost} gold to set this title."); return; }
        if (ctx.Title == title) { await ctx.Say("You would be wasting your money to set the same title twice."); return; }
        if (!ctx.SpendGold(cost)) { await ctx.Say($"You do not have the required {cost} gold to set this title."); return; }

        ctx.SetTitle(title);
        ctx.Notify($"Your title has been changed to: {title}");
    }
}

/// <summary>The kingdom librarian (RTK librarian.lua). Its tutorial role: when the player speaks the tutor's
/// name ("ironheart"/"jadespear") on tutorial stage 5, it welcomes them and sets <c>talked_to_tutor</c>. Also
/// offered as a "Talk to Librarian" click option so the interaction works without voice. The book-shop Buy/Sell
/// catalogue isn't ported yet.</summary>
public sealed class LibrarianAbility : INpcAbility, INpcSayHandler
{
    public static readonly LibrarianAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Talk to Librarian", Talk);
    }

    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech is not ("ironheart" or "jadespear")) return false;
        await Talk(ctx);
        return true;
    }

    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.Stage("tutorial_quest") == 5 && ctx.Reg("talked_to_tutor") != 1)
        {
            await ctx.Say(
                "Hello there, I see you have met my friend the Tutor. I hope he is doing well these days.",
                "This is the great library of the kingdom, here we store the knowledge of the ages.",
                "One of the prized items citizens come here for is the \"Legends\", a scroll that tells the great tales.",
                "Unfortunately, this item is very expensive, but perhaps when you are richer you will be able to get your own.");
            await ctx.Say(
                "... or better yet... make your own legend to be told in the scroll!",
                "Ah, what dreams, what wonders. Well, I must get back to work now. See you around, I hope to hear tales of your adventures soon.",
                "You should go back to the tutor now, and continue to learn more, he has so much to teach you.");
            ctx.SetReg("talked_to_tutor", 1);
        }
        else
        {
            await ctx.Say("Welcome to the great library of the kingdom, where the knowledge of the ages is stored.");
        }
    }
}

/// <summary>Chu Rua, the Dragon King's turtle (RTK tutorial/chu_rua.lua). Tutorial stage 7: he asks for a
/// <c>young_ginseng</c> (a scripted-tile pickup on Guol Tiger Pass, map 1116); bring it and he grants the
/// <c>aided_chu_rua</c> legend + a sea ring + experience, then warps you home. The Lost-Legend "mermaid song"
/// branch of the script isn't ported.</summary>
public sealed class ChuRuaAbility : INpcAbility
{
    public static readonly ChuRuaAbility Instance = new();

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        yield return ("Speak with Chu Rua", Talk);
    }

    private static async Task Talk(NpcContext ctx)
    {
        if (ctx.HasLegend("aided_chu_rua"))
        {
            await ctx.Say("Thank you again for your help! I will return you home now.");
            WarpHome(ctx);
            return;
        }

        if (ctx.HasItem("young_ginseng", 1))
        {
            await ctx.Say("Ginseng. What an odd looking root.", "The Dragon king shall live. Bless you, kind one.");
            ctx.AwardExp(ctx.Stage("tutorial_quest") == 7 ? 600u : 400u);   // RTK: 400, +200 on the tutorial
            ctx.TakeItem("young_ginseng", 1);
            ctx.GiveItem("sea_ring", 1);
            ctx.AddLegend($"Aided Chu Rua ({Character.GameDate})", "aided_chu_rua", 5, 128);   // RTK: "Aided Chu Rua (" .. curT() .. ")"
            await ctx.SayItem("sea_ring", "Humbly, I offer one of the finest jewels from the sea.");
            await ctx.Say("Thank you again for your help! I will return you home now.");
            WarpHome(ctx);
            return;
        }

        await ctx.Say(
            "I have swum as hard as I could. Hey! hey you, honorable human. But a moment! I would that you would hear out an earnest request.",
            "The Lord, Dragon King, is dying as we speak, beneath the waves in his palace. The finest physician has come and declared that he must have an item we cannot procure from within the sea.",
            "I entreat you as a humble servant of the Dragon King, and the only servants who know of the land and the sea.",
            "Please, his highness's health depends upon a root of Young ginseng.");
        await ctx.SayItem("sea_ring", "Give this to me, and this ring of the Mermaid Princess I would, in return, give to thee.");
        await ctx.Say(
            "I... I wish I could point you in the way of the ginseng, but I know not where it grows. There is an old verse,",
            "'Skip north, until rabbits nibbling grass you find, is a path to a king's health and harmony,'",
            "The ginseng lies north, in the Tiger Pass — mind the tiger. Please get young ginseng for his highness's sake!");
    }

    // RTK: Koguryo (country 1) home = map 36 (7,6); otherwise Buya = map 351 (8,8).
    private static void WarpHome(NpcContext ctx)
    {
        if (ctx.Nation == 1) ctx.Warp(36, 7, 6);
        else                 ctx.Warp(351, 8, 8);
    }
}

/// <summary>The talking rabbit of Guol Valley (chu_rua_rabbit.lua) — a "magic animal" that hints at the ginseng
/// quest. Speech-triggered: "hello" / "tiger" / "ginseng".</summary>
public sealed class ChuRuaRabbitAbility : INpcAbility, INpcSayHandler
{
    public static readonly ChuRuaRabbitAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        switch (speech)
        {
            case "hello":
                await ctx.Say("Hmmm..", "What is it you want?");
                return true;
            case "tiger":
                ctx.Bubble("Fool was I to go north for ginseng. He almost ate me!");
                return true;
            case "ginseng":
                await ctx.Say(
                    "What a bitter root! It's as bad tasting as the mountains in which it grows.",
                    "Some trickster cousin told me I should go up the left path and have some of the delicious root.",
                    "Fool was I to go into the awful mountains. I followed this stream up to those horrid mountain's foot, and hopped up a dangerous path.");
                return true;
            default:
                return false;
        }
    }
}

/// <summary>The Ancient dolmen of Guol Divide (chu_rua_rock.lua) — say "hello" and it gives the key hint: to
/// pass the tiger, allude to one of the rabbits that tricked him.</summary>
public sealed class ChuRuaRockAbility : INpcAbility, INpcSayHandler
{
    public static readonly ChuRuaRockAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        if (speech != "hello") return false;
        await ctx.Say(
            "O, it must be good to have feet.",
            "You've been to the sea I'll bet from the smell of you.",
            "That is where I have lived for so long until now; by the sea.",
            "Thank you for spending a moment with this old soul. Be careful of the tiger to the north.",
            "He only thinks of food, though you might distract him if you allude to one of the rabbits that tricked him");
        return true;
    }
}

/// <summary>The tiger guarding the ginseng on Guol Tiger Pass (chu_rua_tiger.lua). Say "rabbit" and pick the
/// right place he ran off to (Forest) and he leaves — warping you to the tiger-free copy (map 1117) where you
/// can finally take the ginseng.</summary>
public sealed class ChuRuaTigerAbility : INpcAbility, INpcSayHandler
{
    public static readonly ChuRuaTigerAbility Instance = new();
    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx) => NoClickMenu.None;
    public async Task<bool> OnSay(NpcContext ctx, string speech)
    {
        switch (speech)
        {
            case "hello":
                ctx.Bubble("Hello, Dinner!");
                return true;
            case "ginseng":
                ctx.Bubble("I'd rather eat you!");
                return true;
            case "rabbit":
                await ctx.Say("What? Rabbit? Was it that foul hopping furre that trapped me in a pit?");
                int choice = await ctx.Menu("I'd love to rend his neck. Where did you see him?",
                    new[] { "Warrior's Guild", "Forest", "Town", "Mage's Guild" });
                if (choice == 2)   // Forest = correct
                {
                    await ctx.Say("Mmm. Well then I guess I'll return him a favor with grinning teeth.");
                    ctx.Notify("The tiger leaves to the south.");
                    // RTK warps to a tiger-free copy (map 1117), but that map isn't renderable in our set, so
                    // instead flag the tiger as distracted — TryGinseng then lets you take the root on 1116.
                    ctx.SetReg("chu_rua_tiger_gone", 1);
                }
                else if (choice == 4)
                    await ctx.Say("What, did someone pull him out of a hat?",
                                  "So far? Oh well, I guess I'll have a snack beforehand... and you look tasty!");
                else
                    await ctx.Say("So far? Oh well, I guess I'll have a snack beforehand... and you look tasty!");
                return true;
            default:
                return false;
        }
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
            // Label is just the quest name — a quest owns its own stage meaning (the tutorial runs 0..14, not
            // the 0/1/2 convention), so a generic "in progress/done" suffix here would be wrong.
            yield return (quest.Name, c => quest.Talk(c));
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
