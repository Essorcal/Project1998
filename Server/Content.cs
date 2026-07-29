namespace Server;

/// <summary>A warpable map: id (== TK&lt;id&gt;.map and the 0x15 mapId), display name, and dimensions.</summary>
public sealed record MapInfo(ushort Id, string Name, ushort Xs, ushort Ys);

/// <summary>A summonable creature definition (name, sprite look, palette colour, HP, reward, move pace).
/// <paramref name="Aggressive"/> is RTK's <c>MobBehavior</c> (mob.c: 0=Normal/fights-back-only,
/// 1=Aggressive/attacks on sight, 2=Stationary) — we don't model Stationary separately since those are
/// loaded as NPCs (Content.Npcs), not MobDef entries. <paramref name="MinDam"/>/<paramref name="MaxDam"/>
/// are RTK's per-mob swing range (SQL <c>MobMinimumDamage</c>/<c>MobMaximumDamage</c>, RTK
/// <c>swingDamage.lua</c> <c>_getMobSwingDamage</c>) — the ACTUAL melee damage a mob deals, unrelated to
/// its Level (Level is only exp/display; a level-99 dragon can carry a MinDam/MaxDam in the thousands).
/// <paramref name="Hit"/> (SQL <c>MobHit</c>) feeds its crit chance (RTK <c>hitCritChance.lua</c>).
/// <paramref name="IsBoss"/> (SQL <c>MobIsBoss</c>) selects a player weapon's Large-damage range instead of
/// Small (RTK <c>swingDamage.lua</c> <c>_getPlayerSwingDamage</c>). RTK's mob struct actually carries TWO
/// separate defense stats, both previously treated as 0 for lack of a source column: <paramref name="Ac"/>
/// (SQL <c>MobArmor</c> — signed, lower-is-better, same convention as <c>Character.Ac</c>) is what reduces
/// an incoming MELEE swing (RTK <c>swingDamage.lua</c>'s <c>target.armor</c>); <paramref name="Protection"/>
/// (SQL <c>MobProtection</c>) is a DIFFERENT stat that only feeds <see cref="Session.RollDeflect"/>'s magic
/// resist roll (RTK clif.c <c>tprotection</c>) — melee and magic defense do not share a stat in RTK.
/// <paramref name="Grace"/> (SQL <c>Grace</c>, already in the CSV but previously unparsed like the rest of
/// this list) is read as the DEFENDER's grace in <see cref="Session.PlayerSwingDamage"/>'s crit-chance roll
/// when a player attacks this mob.</summary>
public sealed record MobDef(int Id, string Key, string Name, ushort Look, byte Color, int Hp, int Exp, int Level, int MoveTime, int Will = 0, bool Aggressive = false, int MinDam = 1, int MaxDam = 1, bool IsBoss = false, int Protection = 0, int Hit = 0, int Ac = 0, int Grace = 0);

/// <summary>One independently-rolled line of a mob's RTK <c>loot</c> table (<c>MobDrops.lua</c>
/// <c>_handleLoot</c>): a null <see cref="ItemKey"/> means gold rather than an item. The dropped amount is
/// uniform between 1 and <see cref="MaxAmount"/>; <see cref="RatePercent"/> is out of 100 (may carry a
/// fraction, e.g. 7.5) and is rolled independently of every other line — a mob can drop several of its
/// loot lines at once.</summary>
public sealed record LootRoll(string? ItemKey, int MaxAmount, double RatePercent);

/// <summary>One line of a mob's RTK <c>rareLoot</c> table (<c>_handleRareLoot</c>): rolled in the listed
/// order, but only the FIRST line that hits actually drops (always amount 1) — later lines in the same
/// table never drop alongside an earlier hit.</summary>
public sealed record RareRoll(string? ItemKey, double RatePercent);

/// <summary>A mob's full drop table, extracted from RTK's real server-side Lua
/// (<c>RTK-Server/rtklua/Accepted/Mobs/MobDrops.lua</c>) by <c>re/extract_mob_drops.py</c> into
/// <c>data/game-data/MobDrops.csv</c>. Keyed by <see cref="MobDef.Key"/> in <see cref="Content.MobDrops"/>.</summary>
public sealed record MobDropDef(LootRoll[] Loot, RareRoll[] Rare);

/// <summary>One item (or gold, when <see cref="Item"/> is null) rolled off a slain mob by
/// <see cref="Content.RollDrops"/>.</summary>
public readonly record struct RolledDrop(ItemDef? Item, int Amount, bool Gold);

/// <summary>One "slay-one-of" quest target from RTK MinorQuest.lua (extracted to MinorQuests.csv). A quest is
/// picked at random among those whose Level/Stat/Mark ranges the player falls in; the objective is met by
/// killing any one of <see cref="Mobs"/>. <see cref="Tier"/> is "Minor"/"Major"/"Epic".</summary>
public sealed record MinorQuestDef(
    string Tier, string Key, string DisplayName, IReadOnlyList<string> Mobs,
    int MinLevel, int MaxLevel, long MinStat, long MaxStat, int MinMark, int MaxMark);

/// <summary>A fixed spawn point from the RTK spawn table: a mob id placed on a map tile. The world
/// materializes one live mob per point and, on its death, respawns another after a delay.</summary>
public sealed record SpawnDef(int MobId, ushort Map, ushort X, ushort Y);

/// <summary>An area spawn from RTK's Lua spawner NPC (<c>mobSpawnHandler.lua</c>'s
/// <c>handleSpawn(npc, map, {mobs}, {counts}, timer [,minX,minY,maxX,maxY])</c>): <see cref="Count"/>
/// of <see cref="MobId"/> scattered across a map, optionally within a bounding box. This is where
/// every hunting cave/dungeon (the Mythic zodiac caves, wilderness, etc.) gets its mobs — none of it
/// is in the static <see cref="SpawnDef"/> table. A zero box (all four 0) means "anywhere walkable on
/// the map". The world picks a walkable home tile per mob and respawns it there on death; RTK's
/// respawn <c>timer</c> is dropped in favour of the server's own cadence. Generated by
/// <c>re/extract_lua_spawns.py</c> into <c>data/game-data/AreaSpawns.csv</c>.</summary>
/// <param name="RespawnSec">0 = normal ~18s refill cadence. &gt;0 marks a RARE spawn (RTK's trap-ambush
/// bosses): the world starts it un-spawned, materializes it at a random time while the map is hunted, and
/// holds it dead for ~RespawnSec (plus jitter) after each kill — see <c>World.NextRespawnTick</c>. Carried
/// only by the trap-spawn supplement (<c>AreaSpawnsTrap.csv</c>); the base handleSpawn rows leave it 0.</param>
public sealed record AreaSpawnDef(int MobId, ushort Map, int Count, ushort MinX, ushort MinY, ushort MaxX, ushort MaxY, int RespawnSec = 0);

/// <summary>An NPC placement from the RTK NPCs0 table: a stationary being on a map tile. Nearly all
/// (384/385) render via the creature path (0x07) exactly like a mob — <c>Look</c>/<c>Color</c> mirror
/// <see cref="MobDef"/> — so the world spawns them as non-fighting mobs. <c>IsChar</c> marks the rare
/// human-composite NPC (0x33). The shop/repair/bank flags select the dialog behaviour on click.</summary>
public sealed record NpcDef(
    int Id, string Key, string Name, ushort Map, ushort X, ushort Y, byte Dir,
    ushort Look, byte Color, bool IsChar, bool Shop, bool Repair, bool Bank,
    int MoveTime, int ReturnDistance);

/// <summary>
/// An item definition from the RTK item db (Items.csv). Field names mirror the client's item_data
/// (see RTK itemdb.h). <c>Icon</c> is the inventory-window / ground (Item.epf) frame; <c>Look</c> is the
/// worn-appearance sprite. <c>Type</c> is ITM_* (0=eat,1=use,2=smoke,3=weap,4=armor,5=shield,6=helm,
/// 7=left,8=right,9=subleft,10=subright,11=faceacc,12=crown,13=mantle,14=necklace,15=boots,16=coat,
/// 18=etc/junk…). Stat lines feed the equip bonuses.
/// </summary>
public sealed record ItemDef(
    int Id, string Key, string Name, byte Type,
    ushort Icon, byte IconColor, ushort Look, byte LookColor,
    byte Sex, byte Level, ushort Durability, int StackAmount, int MaxAmount,
    int Armor, int Hit, int Dam, int Vita, int Mana, int Might, int Will, int Grace,
    bool NoDrop, bool Thrown, int BuyPrice, int SellPrice, int MightReq = 0, int Sound = 0,
    bool Indestructible = false,
    // A weapon's real swing range (RTK ItmMinimumSDamage/ItmMaximumSDamage/ItmMinimumLDamage/
    // ItmMaximumLDamage) — the actual source of player melee damage (swingDamage.lua
    // _getPlayerSwingDamage), previously parsed nowhere despite being present in Items.csv (same class
    // of bug as the mob MinDam/MaxDam gap). "L" (Large) replaces "S" (Small) as the roll when the target
    // is a boss mob. Protection (RTK ItmProtection) is the wearer's own magic-resist contribution,
    // folded into Session.RollDeflect the same way a mob's Protection is.
    int MinSDam = 0, int MaxSDam = 0, int MinLDam = 0, int MaxLDam = 0, int Protection = 0)
{
    /// <summary>ITM_WEAP..ITM_COAT (3..16) are wearable; everything else is consumable/junk.</summary>
    public bool IsEquip => Type is >= 3 and <= 16;
    public bool IsConsumable => Type is 0 or 1 or 2;     // EAT / USE / SMOKE
    public bool Stackable => StackAmount > 1 || MaxAmount > 1;

    /// <summary>Wire equip-slot byte for the 0x37/0x38 window + 0x1F unequip (client's clif_getequiptype).
    /// EQ index = Type-3; this maps that index to the byte the client expects. 0 = not equippable.</summary>
    public byte EquipSlot => Type switch
    {
        3  => 1,   // WEAP     4  => 2,   // ARMOR   5 => 3, // SHIELD  6 => 4, // HELM
        4  => 2,
        5  => 3,
        6  => 4,
        7  => 7,   // LEFT ring
        8  => 8,   // RIGHT ring
        9  => 20,  // SUBLEFT
        10 => 21,  // SUBRIGHT
        11 => 22,  // FACEACC
        12 => 23,  // CROWN
        13 => 14,  // MANTLE
        14 => 6,   // NECKLACE
        15 => 13,  // BOOTS
        16 => 16,  // COAT
        _  => 0,
    };
}

/// <summary>The scripted effect of an EAT/USE/SMOKE item's "use" handler, ported from RTK's real per-item
/// Lua (<c>RTK-Server/rtklua/Accepted/Items/Consumables/**/*.lua</c>). This exists because <see cref="ItemDef"/>'s
/// own Vita/Mana/Healing DB columns are almost always 0 for these — of 404 EAT/USE/SMOKE rows in Items.csv,
/// only 2 carry a nonzero stat. The real numbers (a food's heal amount, a drink's mana-for-HP trade, a
/// potion's timed ward) live ONLY in the paired Lua script's body, e.g. <c>red_potion.lua</c>'s
/// <c>addHealthExtend(player.maxHealth, ...)</c> — so <see cref="Session.HandleUseItem"/> was previously a
/// near-total no-op for real game items despite looking complete. Keyed by <see cref="ItemDef.Key"/>.</summary>
/// <param name="Kind">"heal" | "fatal" | "mana" | "status" | "hardenbody" | "cure" | "warphome".</param>
/// <param name="Amount">Heal HP / restore MP amount (Kind="heal"/"mana").</param>
/// <param name="Full">Kind="heal": ignore <see cref="Amount"/>, heal to max HP (red_potion's <c>player.maxHealth</c>).</param>
/// <param name="HpCost">Kind="mana": HP spent alongside the MP restore (RTK's drinks/smoke all trade a little HP for a lot of MP).</param>
/// <param name="StatusKey">Kind="status"/"hardenbody": the flag name tracked in <see cref="Session"/>'s status-flag table.</param>
/// <param name="DurationMs">How long the status flag lasts.</param>
/// <param name="AlreadyActiveMessage">Sent instead of (re)applying while <see cref="StatusKey"/> is already active (RTK's <c>checkIfCast</c> guard).</param>
public sealed record ItemUseEffect(string Kind, int Amount = 0, bool Full = false, int HpCost = 0,
    string StatusKey = "", int DurationMs = 0, string AlreadyActiveMessage = "");

public static partial class Content
{
    /// <summary>Ported item -> effect table. See <see cref="ItemUseEffect"/> for the source-of-truth note.
    /// Status-flag potions/scrolls (curse_protection/sanctuary/harden_armor/harden_body/chin_baek_ho_ryung/
    /// purple_potion) carry NO numeric stat delta even in their own RTK spell scripts (e.g.
    /// <c>Spells/common/curse_protection.lua</c> has no <c>recast</c> function at all, just the duration
    /// flag) — same as this project's existing <c>Session.CastCure</c> "Cure" archetype stub, so tracking the
    /// flag + honoring the re-cast guard is the full faithful behavior today, not a shortcut. "cure" (indigo_
    /// potion / clear_water_song) is a likewise-faithful no-op: there's no player poison/curse DoT model yet
    /// to clear (only <see cref="Shared.Mob.PoisonUntil"/> exists, mob-only). Not ported: cosmetic armor-dye
    /// elixir potions (Items/Consumables/potions/elixir_potions.lua — a wardrobe feature, not a use-mechanic),
    /// dragons_tooth (a full shapeshift morph with no matching entry in <see cref="Content.MorphSpells"/> yet),
    /// kawlana_potion (single-quest-area logic), quest_paper (paper-popup UI), cloth/empty_water_jug
    /// (on_drop crafting triggers, not "use" at all). qui_hyang's full menu (clan hall / subpath circle /
    /// main inn) collapses to its always-available "Home" branch, same as yellow_scroll.</summary>
    public static readonly Dictionary<string, ItemUseEffect> ItemEffects = new()
    {
        // ---- food (Items/Consumables/{fruit,meats,nuts}/*.lua): fixed HP heal, RTK's addHealthExtend amount ----
        ["apple"] = new("heal", Amount: 5),
        ["poison_apple"] = new("fatal"),
        ["watermelon"] = new("heal", Amount: 800),
        ["antler"] = new("heal", Amount: 248),
        ["bear_fur"] = new("heal", Amount: 48),
        ["bears_liver"] = new("heal", Amount: 500),
        ["beef"] = new("heal", Amount: 20),
        ["lean_beef"] = new("heal", Amount: 500),
        ["chicken_meat"] = new("heal", Amount: 40),
        ["cooked_fish"] = new("heal", Amount: 20),   // RTK doubles this to 900 when eaten adjacent to your partner — not ported
        ["egg"] = new("heal", Amount: 8),
        ["ginseng"] = new("heal", Amount: 2000),
        ["young_ginseng"] = new("heal", Amount: 40),
        ["ginseng_piece"] = new("heal", Amount: 60),
        ["mountain_ginseng"] = new("heal", Amount: 10000),
        ["green_squirrel_pelt"] = new("heal", Amount: 8),
        ["horse_meat"] = new("heal", Amount: 200),
        ["meat_scrap"] = new("heal", Amount: 5),
        ["pork"] = new("heal", Amount: 128),
        ["rare_pork"] = new("heal", Amount: 31),
        ["rabbit_meat"] = new("heal", Amount: 48),
        ["fine_rabbit_meat"] = new("heal", Amount: 200),
        ["rat_meat"] = new("heal", Amount: 8),
        ["rose_petals"] = new("heal", Amount: 20),   // RTK doubles this to 900 when eaten adjacent to your partner — not ported
        ["snake_meat"] = new("heal", Amount: 100),
        ["fine_snake_meat"] = new("heal", Amount: 320),
        ["tigers_heart"] = new("heal", Amount: 1000),
        ["wolf_meat"] = new("heal", Amount: 48),
        ["acorn"] = new("heal", Amount: 8),
        ["gold_acorn"] = new("heal", Amount: 8),

        // ---- potions: plain heal (Items/Consumables/potions/*.lua) ----
        ["blue_potion"] = new("heal", Amount: 100),
        ["violet_potion"] = new("heal", Amount: 200),
        ["yellow_potion"] = new("heal", Amount: 50),
        ["red_potion"] = new("heal", Full: true),

        // ---- drinks + smoke: restore mana at a small HP cost (Items/Consumables/{drinks,smoked}/*.lua) ----
        ["aged_wine"] = new("mana", Amount: 100, HpCost: 5),
        ["moon_wine"] = new("mana", Amount: 100, HpCost: 5),
        ["ogre_cider"] = new("mana", Amount: 100, HpCost: 10),
        ["ogre_drought"] = new("mana", Amount: 325, HpCost: 25),
        ["root_liquor"] = new("mana", Amount: 30, HpCost: 1),
        ["wine"] = new("mana", Amount: 25, HpCost: 5),
        ["thick_wine"] = new("mana", Amount: 40, HpCost: 5),
        ["rice_wine"] = new("mana", Amount: 30, HpCost: 0),
        ["herb_pipe"] = new("mana", Amount: 300, HpCost: 15),
        ["sonhi_pipe"] = new("mana", Amount: 450, HpCost: 50),
        ["iron_statue"] = new("mana", Amount: 250, HpCost: 0),

        // ---- status-flag potions/scrolls: a timed ward the item sets (see class doc for what is/isn't wired) ----
        ["aqua_potion"] = new("status", StatusKey: "sanctuary", DurationMs: 185_000, AlreadyActiveMessage: "Another spell of that type is already in effect."),
        ["green_potion"] = new("status", StatusKey: "sanctuary", DurationMs: 300_000, AlreadyActiveMessage: "Another spell of that type is already in effect."),
        ["lime_potion"] = new("status", StatusKey: "sanctuary", DurationMs: 900_000, AlreadyActiveMessage: "Another spell of that type is already in effect."),
        ["brown_potion"] = new("status", StatusKey: "harden_armor", DurationMs: 185_000, AlreadyActiveMessage: "Another spell of that type is already in effect."),
        ["muddy_potion"] = new("status", StatusKey: "harden_armor", DurationMs: 300_000, AlreadyActiveMessage: "Another spell of that type is already in effect."),
        ["black_potion"] = new("status", StatusKey: "chin_baek_ho_ryung", DurationMs: 10_000),
        ["purple_potion"] = new("status", StatusKey: "purple_potion", DurationMs: 300_000, AlreadyActiveMessage: "This spell is already active."),
        ["scroll_of_protection"] = new("status", StatusKey: "curse_protection", DurationMs: 250_000, AlreadyActiveMessage: "You are already protected."),
        ["scroll_of_defense"] = new("status", StatusKey: "curse_protection", DurationMs: 350_000, AlreadyActiveMessage: "You are already protected."),

        // scroll_of_immortality: RTK rolls ceil((120+clampedArmor)/2)% success before granting the buff — its
        // own case in Session.ApplyItemEffect (needs the live armor roll, not just a fixed duration).
        ["scroll_of_immortality"] = new("hardenbody", StatusKey: "harden_body", DurationMs: 16_000, AlreadyActiveMessage: "You already cast that spell."),

        // ---- cure: see class doc — no player poison/curse model exists yet to actually clear ----
        ["indigo_potion"] = new("cure"),
        ["clear_water_song"] = new("cure"),

        // ---- warp home (RTK yellow_scroll / qui_hyang.lua's "Home" option) ----
        ["yellow_scroll"] = new("warphome"),
        ["qui_hyang"] = new("warphome"),
    };
}

/// <summary>
/// A spell/skill definition from the RTK <c>Spells</c> table. <c>Name</c> is the display name
/// (SplDescription, e.g. "Bolt"); <c>Key</c> is the internal identifier (SplIdentifier, e.g. "bolt_mage").
/// <c>PathId</c> is the class that learns it (0=Peasant 1=Warrior 2=Rogue 3=Mage 4=Poet, 5+=subpaths,
/// 99=system/common). <c>Level</c> is the character level required to learn it. <c>Alignment</c> is the
/// sub-alignment the spell belongs to (<b>-1</b> = universal/any, <b>0</b> = base/unaligned, <b>1</b> = Kwisin,
/// <b>2</b> = Mingken, <b>3</b> = Ohaeng); a character learns only universal + their own alignment's set, so
/// the other alignments' parallel spells (which often share a display name) aren't taught as duplicates.
/// <c>Type</c> is the client's spellbook type byte (the 0x17 add-spell / 0x0F cast discriminator): <b>1</b> =
/// prompt spell (the client asks <c>Question</c> and sends the typed answer), <b>2</b> = targeted (the client
/// sends a target entity id), <b>5</b> = self / no-target. The client renders type 1/2 in the Spell book and
/// type 5 in the Skill book (both populate through the same 0x17 packet, keyed on this type).
/// </summary>
public sealed record SpellDef(int Id, string Key, string Name, byte Type, int PathId, int Level, int Alignment, string Question, bool CanFail = false)
{
    public bool NeedsTarget => Type == 2;   // client sends a target entity id (u32) when casting
    public bool NeedsPrompt => Type == 1;   // client sends the typed answer string when casting
    public bool IsSpell     => Type is 1 or 2;   // magic — goes in the Spell book
    public bool IsSkill     => Type == 5;        // physical ability — goes in the Skill book
}

/// <summary>The runtime EFFECT of a spell, extracted from RTK's Lua scripts (re/extract_spell_formulas.py →
/// spell_effects.csv). Keyed by the same identifier as <see cref="SpellDef.Key"/> (the Lua table name ==
/// SplIdentifier). <c>Archetype</c> is one of Damage / Heal / Buff / Debuff / ManaBattery / Cure / Utility /
/// Summon / Teleport / Dialog. <c>AmountExpr</c> is the spell's real damage/heal formula as a Lua arithmetic
/// string over player/target stats (evaluated by <see cref="Formula"/>); <c>Mana</c> is the true mana cost;
/// buff/debuff/cure carry their own params. Session.ApplyCast dispatches on this. Missing fx ⇒ the keyword
/// classifier is the fallback. <c>CureCat</c> is RTK's duration-category tag (<c>player:removeDuras(cat)</c>)
/// — most Buff/Debuff spells carry one (morphs/venoms/paras/curses/…); Session.ApplyCast special-cases
/// "backstabs"/"flanks" (the Warrior Backstab/Flank skills — a boolean combat STANCE, not a numeric
/// BuffStat/BuffAmt our generic buff loop can express) before the normal archetype dispatch.</summary>
public sealed record SpellFx(
    string Key, string Archetype, int Mana, string AmountExpr, string BuffStat, string BuffAmt,
    int DurationMs, string Debuff, string Chance, string HealthCost, int Animation, int Sound, int Aether,
    int PcAlign, string CureCat = "");

/// <summary>Tiny arithmetic evaluator for the Lua damage/heal formulas RTK spells use, e.g.
/// <c>"25 + math.floor(player.level / 2) + math.floor((player.will + 3) / 4)"</c> or
/// <c>"math.ceil(player.magic * 2.15)"</c>. Supports + - * /, unary sign, parens, decimal literals, dotted
/// variables (player.level, target.baseHealth — resolved against a supplied var map), and the functions
/// math.floor / math.ceil / math.abs / math.random. Unknown names resolve to 0 and a malformed expression
/// yields 0 (never throws) — a missing formula degrades to "no effect", not a crash.</summary>
public static class Formula
{
    private static readonly Random Rng = new();

    public static double Eval(string? expr, IReadOnlyDictionary<string, double> vars)
    {
        if (string.IsNullOrWhiteSpace(expr)) return 0;
        try { return new Parser(expr, vars).ParseAll(); }
        catch { return 0; }
    }

    private sealed class Parser
    {
        private readonly string _s;
        private readonly IReadOnlyDictionary<string, double> _v;
        private int _i;
        public Parser(string s, IReadOnlyDictionary<string, double> v) { _s = s; _v = v; }

        private void Ws() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        private char Cur => _i < _s.Length ? _s[_i] : '\0';

        public double ParseAll() { double v = Expr(); return v; }

        private double Expr()
        {
            double v = Term();
            while (true)
            {
                Ws();
                if (Cur == '+') { _i++; v += Term(); }
                else if (Cur == '-') { _i++; v -= Term(); }
                else return v;
            }
        }
        private double Term()
        {
            double v = Factor();
            while (true)
            {
                Ws();
                if (Cur == '*') { _i++; v *= Factor(); }
                else if (Cur == '/') { _i++; double d = Factor(); v = d == 0 ? 0 : v / d; }
                else return v;
            }
        }
        private double Factor()
        {
            Ws();
            if (Cur == '-') { _i++; return -Factor(); }
            if (Cur == '+') { _i++; return Factor(); }
            return Primary();
        }
        private double Primary()
        {
            Ws();
            if (Cur == '(') { _i++; double v = Expr(); Ws(); if (Cur == ')') _i++; return v; }
            if (char.IsDigit(Cur) || Cur == '.') return Number();

            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.')) _i++;
            string name = _s.Substring(start, _i - start);
            Ws();
            if (Cur == '(')   // function call
            {
                _i++;
                var args = new List<double>();
                Ws();
                if (Cur != ')')
                {
                    args.Add(Expr());
                    while (true) { Ws(); if (Cur == ',') { _i++; args.Add(Expr()); } else break; }
                }
                Ws(); if (Cur == ')') _i++;
                return Call(name.ToLowerInvariant(), args);
            }
            return Var(name);
        }
        private double Number()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            double.TryParse(_s.Substring(start, _i - start),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v);
            return v;
        }
        private static double Call(string name, List<double> a)
        {
            double A0 = a.Count > 0 ? a[0] : 0;
            switch (name)
            {
                case "math.floor": return Math.Floor(A0);
                case "math.ceil":  return Math.Ceiling(A0);
                case "math.abs":   return Math.Abs(A0);
                case "math.max":   return a.Count >= 2 ? Math.Max(a[0], a[1]) : A0;
                case "math.min":   return a.Count >= 2 ? Math.Min(a[0], a[1]) : A0;
                case "math.random":
                    if (a.Count >= 2) return Rng.Next((int)a[0], (int)a[1] + 1);
                    if (a.Count == 1) return Rng.Next(1, (int)a[0] + 1);
                    return Rng.NextDouble();
                default: return 0;
            }
        }
        private double Var(string name)
        {
            if (_v.TryGetValue(name, out var v)) return v;
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && _v.TryGetValue(name[(dot + 1)..], out v)) return v;
            return 0;
        }
    }
}

/// <summary>
/// In-memory game-content registries loaded ONCE at startup from EXTERNAL, gitignored data
/// (RTK-derived — see docs §17.1). The loader lives in the repo; the data does not, keeping this a
/// logic-only server. Everything here is read-only after <see cref="Load"/>, so it is safe to share
/// across all sessions without locking. Missing data degrades gracefully (empty registries + a log).
/// </summary>
public static partial class Content
{
    // id -> map. Only maps whose dims were validated against the client's own TK&lt;id&gt;.map (see
    // re/build_map_index.py) are present, so a warp target here is always renderable.
    public static IReadOnlyDictionary<ushort, MapInfo> Maps { get; private set; } =
        new Dictionary<ushort, MapInfo>();
    public static IReadOnlyList<MobDef> Mobs { get; private set; } = new List<MobDef>();
    public static IReadOnlyList<ItemDef> Items { get; private set; } = new List<ItemDef>();

    // All learnable spells/skills (RTK Spells table, section-headers + inactive rows filtered out), and the
    // class/path id -> display name table (RTK Paths table). Read-only after Load, shared lock-free.
    public static IReadOnlyList<SpellDef> Spells { get; private set; } = new List<SpellDef>();
    public static IReadOnlyDictionary<int, string> Paths { get; private set; } = new Dictionary<int, string>();

    // Per-spell runtime effect (archetype + real RTK formulas), keyed by spell identifier. Drives the magic
    // engine in Session.ApplyCast. Extracted from RTK's Lua by re/extract_spell_formulas.py; empty ⇒ every
    // cast falls back to the keyword classifier. Read-only after Load, shared lock-free.
    public static IReadOnlyDictionary<string, SpellFx> SpellFx { get; private set; } =
        new Dictionary<string, SpellFx>();

    // Fixed monster spawn points (RTK Spawns0.csv). One live mob per point; the world respawns it on death.
    public static IReadOnlyList<SpawnDef> Spawns { get; private set; } = new List<SpawnDef>();

    // Area spawns from RTK's Lua spawner (data/game-data/AreaSpawns.csv): the hunting-map mob populations
    // (Mythic caves, wilderness, dungeons) that the static Spawns0 table doesn't cover. See AreaSpawnDef.
    public static IReadOnlyList<AreaSpawnDef> AreaSpawns { get; private set; } = new List<AreaSpawnDef>();

    // Stationary NPCs (RTK NPCs0.csv), placed once by the world as non-fighting mobs. Keyed by NpcId for
    // click-time dialog lookup.
    public static IReadOnlyList<NpcDef> Npcs { get; private set; } = new List<NpcDef>();
    private static IReadOnlyDictionary<int, NpcDef> _npcById = new Dictionary<int, NpcDef>();
    public static NpcDef? NpcById(int id) => _npcById.TryGetValue(id, out var n) ? n : null;

    // "Slay one X" quest targets (RTK MinorQuest.lua -> MinorQuests.csv), grouped by tier for the trainer
    // minor-quest ability. See Server/MinorQuest.cs.
    public static IReadOnlyList<MinorQuestDef> MinorQuests { get; private set; } = new List<MinorQuestDef>();

    // NPC identifier -> its buy stock (item keys), auto-extracted from the RTK NPC scripts
    // (re/extract_shops.py -> ShopStock.csv). A fallback behind the curated Shops.cs catalogues, so every
    // shop-flagged NPC has something to sell without hand-authoring each. See Shops.For.
    public static IReadOnlyDictionary<string, string[]> ShopStock { get; private set; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    // 4.95 client Monster.tbl "Palette" per look id (0..326), decoded from the client PAK (see
    // re/monster-matcher). This is the palette the CLIENT draws a given monster with — a DIFFERENT index
    // space than RTK's MobLookColor. The 0x07 spawn color byte must carry THIS value (not RTK's) or the
    // sprite recolors wrongly (e.g. a copper rabbit instead of the plain one). Most looks are palette 0.
    public static IReadOnlyDictionary<ushort, byte> LookPalettes { get; private set; } =
        new Dictionary<ushort, byte>();

    // Portals/doors: (sourceMap, x, y) -> (destMap, x, y). Only warps whose DESTINATION is a renderable
    // client map are kept (a warp to a 7.x-only map would strand the player on a black screen).
    public static IReadOnlyDictionary<(ushort m, ushort x, ushort y), (ushort m, ushort x, ushort y)> Warps
    { get; private set; } = new Dictionary<(ushort, ushort, ushort), (ushort, ushort, ushort)>();

    // Per-map region + warp-out flag (RTK Maps table: MapRegion / MapWarpout). Region groups maps into
    // kingdoms (0 Kugnae · 1 Buya · 2 Mythic · 3 Nagnang · …) and is what the Gateway spell keys off to pick
    // the destination city; warpOut==false is a map that blocks Gateway/Return ("It doesn't work here").
    // Also carries the warp-entry gate (RTK map_data.reqlvl/reqvita/reqmana/reqmark/reqpath/*max/rejectmsg,
    // map.c:1102) and the PvP flag (MapPvP — durability loss is disabled on PvP maps, RTK clif.c:6650).
    // Loaded from the full RTK Maps.csv (map_index.csv, the renderable subset, doesn't carry these columns).
    public sealed record MapMetaInfo(int Region, bool WarpOut, bool Pvp, bool CanTalk, int ReqLvl, int ReqPath, int ReqMark,
        long ReqVita, long ReqMana, int LvlMax, long VitaMax, long ManaMax, string RejectMsg);

    public static IReadOnlyDictionary<ushort, MapMetaInfo> MapMeta { get; private set; } =
        new Dictionary<ushort, MapMetaInfo>();

    // Mob floor-loot tables (RTK Mobs/MobDrops.lua -> re/extract_mob_drops.py -> MobDrops.csv). Keyed by
    // MobDef.Key; a mob with no entry here drops nothing, matching RTK (no _mobDropsTable entry = no loot).
    public static IReadOnlyDictionary<string, MobDropDef> MobDrops { get; private set; } =
        new Dictionary<string, MobDropDef>();

    // Era-gating overrides for crafting skills (see Server/CraftingToggles.cs + docs/Crafting-Values.md).
    // File is optional and sparse: only skills listed here override CraftingToggles.DefaultDisabled;
    // anything absent keeps the code-level default. Columns: Skill,Enabled(0/1).
    public static IReadOnlyDictionary<string, bool> CraftingToggleOverrides { get; private set; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // On/off overrides for individual NPCs, by NpcId (see Server/NpcToggles.cs). Same sparse-override
    // shape as CraftingToggleOverrides: only ids listed here override NpcToggles.DefaultDisabled.
    // Columns: NpcId,Enabled(0/1).
    public static IReadOnlyDictionary<int, bool> NpcToggleOverrides { get; private set; } =
        new Dictionary<int, bool>();

    public static void Load()
    {
        Maps = LoadMaps(ResolvePath("NEXUS_MAP_INDEX", "data", "game-data", "map_index.csv"));
        Mobs = LoadMobs(ResolvePath("NEXUS_MOBS", "data", "game-data", "mobs.csv"));
        Items = LoadItems(ResolvePath("NEXUS_ITEMS", "data", "game-data", "Items.csv"));
        Warps = LoadWarps(ResolvePath("NEXUS_WARPS", "data", "game-data", "Warps.csv"));   // needs Maps
        Spawns = LoadSpawns(ResolvePath("NEXUS_SPAWNS", "data", "game-data", "Spawns0.csv"));
        AreaSpawns = LoadAreaSpawns(ResolvePath("NEXUS_AREASPAWNS", "data", "game-data", "AreaSpawns.csv"));
        // Trap-ambush populations (tiger cave, rabbit boss-tier, trapdoor spiders) that RTK spawns via
        // trap/mob_spawn.lua rather than handleSpawn — merged in as ordinary area spawns (rare-boss rows
        // carry RespawnSec). Generated by re/extract_trap_spawns.py. See that script for the calibration.
        AreaSpawns = AreaSpawns.Concat(
            LoadAreaSpawns(ResolvePath("NEXUS_AREASPAWNS_TRAP", "data", "game-data", "AreaSpawnsTrap.csv"))).ToList();
        Npcs = LoadNpcs(ResolvePath("NEXUS_NPCS", "data", "game-data", "NPCs0.csv"));   // needs Maps
        _npcById = Npcs.ToDictionary(n => n.Id);
        MinorQuests = LoadMinorQuests(ResolvePath("NEXUS_MINORQUESTS", "data", "game-data", "MinorQuests.csv"));
        ShopStock = LoadShopStock(ResolvePath("NEXUS_SHOPSTOCK", "data", "game-data", "ShopStock.csv"));
        Paths = LoadPaths(ResolvePath("NEXUS_PATHS", "data", "game-data", "Paths.csv"));
        LevelExp = LoadLevelExp(ResolvePath("NEXUS_LEVELEXP", "data", "game-data", "LevelExp.csv"));
        Spells = LoadSpells(ResolvePath("NEXUS_SPELLS", "data", "game-data", "Spells.csv"));
        SpellFx = LoadSpellFx(ResolvePath("NEXUS_SPELL_FX", "data", "game-data", "spell_effects.csv"));
        SpellCosts = LoadSpellCosts(ResolvePath("NEXUS_SPELL_COSTS", "data", "game-data", "SpellLearnCosts.csv"));
        LookPalettes = LoadLookPalettes(ResolvePath("NEXUS_MOB_PALETTES", "data", "game-data", "MobLookPalettes.csv"));
        MapMeta = LoadMapMeta(ResolvePath("NEXUS_MAPS_FULL", "data", "game-data", "Maps.csv"));   // region + warpOut for Gateway
        MobDrops = LoadMobDrops(ResolvePath("NEXUS_MOB_DROPS", "data", "game-data", "MobDrops.csv"));
        CraftingToggleOverrides = LoadCraftingToggles(ResolvePath("NEXUS_CRAFTING_TOGGLES", "data", "game-data", "CraftingToggles.csv"));
        NpcToggleOverrides = LoadNpcToggles(ResolvePath("NEXUS_NPC_TOGGLES", "data", "game-data", "NpcToggles.csv"));
        Log.Info($"content: {Maps.Count} maps ({MapMeta.Count} w/ region), {Mobs.Count} mobs, {Items.Count} items, " +
                 $"{Warps.Count} warps, {Spawns.Count} spawns, {AreaSpawns.Count} area-spawns, {Npcs.Count} npcs, {Spells.Count} spells ({SpellFx.Count} fx, {SpellCosts.Count} w/ real learn cost), {LookPalettes.Count} mob-palettes, {MinorQuests.Count} minor-quests, {ShopStock.Count} shop-stocks, {LevelExp.Count} level-exp-paths, {MobDrops.Count} mob-drop-tables, {CraftingToggleOverrides.Count} crafting-toggle overrides, {NpcToggleOverrides.Count} npc-toggle overrides loaded" +
                 (Maps.Count == 0 || Mobs.Count == 0
                     ? "  (some empty — run re/build_map_index.py and check data/game-data/mobs.csv)"
                     : ""));
    }

    /// <summary>
    /// Hot-reload every file-backed registry WITHOUT a restart (the <c>!reload</c> GM command), so content
    /// fixes ship without kicking players. Re-runs the exact ordered <see cref="Load"/> sequence — which
    /// re-reads every CSV and rebuilds the derived <c>_npcById</c> — reassigning the public registries. Each
    /// registry is a lock-free reference, and a reference assignment is atomic, so a reader always sees a whole
    /// old-or-new dictionary, never a torn one (a reader that straddles the swap across two registries is
    /// harmless — they're independent). Returns a one-line count summary.
    ///
    /// SCOPE: file-backed content only. The map <see cref="BgmByMap"/> is a compile-time dictionary in this
    /// file (not a CSV), so altering it still needs a code change + restart.
    /// Already-spawned mobs keep their stats unless the caller also re-seeds them (see World.ReloadContent).
    /// </summary>
    public static string Reload()
    {
        Load();
        return $"{Maps.Count} maps, {Mobs.Count} mobs, {Items.Count} items, {Warps.Count} warps, " +
               $"{Spawns.Count + AreaSpawns.Count} spawns, {Npcs.Count} npcs, {Spells.Count} spells, {ShopStock.Count} shops, " +
               $"{CraftingToggleOverrides.Count} crafting-toggle overrides, {NpcToggleOverrides.Count} npc-toggle overrides";
    }

    /// <summary>The portal at (map, x, y), if the player just stepped on a door tile.</summary>
    public static bool TryWarp(ushort map, ushort x, ushort y, out (ushort m, ushort x, ushort y) dest)
        => Warps.TryGetValue((map, x, y), out dest);

    /// <summary>The RTK region a map belongs to (0 Kugnae · 1 Buya · 2 Mythic · 3 Nagnang · …), or -1 if the
    /// map has no region row. Used by the Gateway spell to resolve the caster's kingdom.</summary>
    public static int RegionOf(ushort mapId) => MapMeta.TryGetValue(mapId, out var m) ? m.Region : -1;

    /// <summary>Whether a map allows warp-out spells (Gateway/Return). Unknown maps default to true (only an
    /// explicit MapWarpout==0 blocks); RTK shows "It doesn't work here" when this is false.</summary>
    public static bool WarpOut(ushort mapId) => !MapMeta.TryGetValue(mapId, out var m) || m.WarpOut;

    /// <summary>Whether a map is flagged PvP (RTK MapPvP) — disables equipment durability loss there
    /// (clif_deductdura, clif.c:6650: "disable dura loss from mobs on pvp map").</summary>
    public static bool IsPvpMap(ushort mapId) => MapMeta.TryGetValue(mapId, out var m) && m.Pvp;

    /// <summary>Whether speech (incl. whisper) is allowed on this map (RTK cantalk). False on the rare
    /// silenced map — RTK: "Your voice is swept away by a strange wind." (clif_parsewisp, clif.c:7666).</summary>
    public static bool CanTalk(ushort mapId) => !MapMeta.TryGetValue(mapId, out var m) || m.CanTalk;

    /// <summary>Totem index → display name (RTK player.lua getTotemName): 0 Ju Jak · 1 Baekho · 2 Hyun Moo ·
    /// 3 Chung Ryong · 4 (or anything else) None.</summary>
    public static string TotemName(int totem) => totem switch
    {
        0 => "Ju Jak", 1 => "Baekho", 2 => "Hyun Moo", 3 => "Chung Ryong", _ => "None"
    };

    /// <summary>Whether the game <paramref name="hour"/> (0..23) falls inside <paramref name="totem"/>'s
    /// six-hour "totem time" window (RTK player.lua isTotemTime). The four totems partition the day; totem 4
    /// (None) never has one. Chung Ryong 04–09 · Ju Jak 10–15 · Baekho 16–21 · Hyun Moo 22–03. During its
    /// window RTK grants +5% kill exp (checkTotemTimeXP) and a 1/25 bonus crafting-skill point.</summary>
    public static bool IsTotemTime(int hour, int totem) => totem switch
    {
        3 => hour >= 4  && hour <= 9,    // Chung Ryong (morning)
        0 => hour >= 10 && hour <= 15,   // Ju Jak (mid-day)
        1 => hour >= 16 && hour <= 21,   // Baekho (evening)
        2 => hour >= 22 || hour <= 3,    // Hyun Moo (mid-night, wraps midnight)
        _ => false,                       // 4 = None (or unset)
    };

    /// <summary>Offline check of the registries + fuzzy lookups (run via <c>--selftest</c>).</summary>
    public static void SelfTest()
    {
        Load();
        void Line(string s) => Log.Info(s);

        Line("--- FindMap (exact id / exact name / substring / subsequence) ---");
        foreach (var q in new[] { "0", "kugnae", "buya", "walsuk tavern", "kgne" })
        {
            var m = FindMap(q);
            Line($"  !warp {q,-16} -> " + (m is null ? "(no match)" : $"map {m.Id} '{m.Name}' {m.Xs}x{m.Ys}"));
        }

        Line("--- FindMob (name / key / id / fuzzy) ---");
        foreach (var q in new[] { "rabbit", "1", "great_horns", "great horns", "grhrn", "fox" })
        {
            var mob = FindMob(q);
            Line($"  !summon {q,-14} -> " + (mob is null ? "(no match)" : $"'{mob.Name}' look {mob.Look} c{mob.Color} {mob.Hp}hp {mob.Exp}xp"));
        }

        Line("--- FindItem (name / key / id) ---");
        foreach (var q in new[] { "apple", "stick", "leather", "sword", "0" })
        {
            var it = FindItem(q);
            Line($"  !item {q,-12} -> " + (it is null ? "(no match)"
                : $"#{it.Id} '{it.Name}' type{it.Type} icon{it.Icon} look{it.Look} {(it.IsEquip ? $"EQUIP slot{it.EquipSlot}" : "use")}"));
        }

        Line("--- SearchMaps(\"buya\", 5) ---");
        foreach (var m in SearchMaps("buya", 5)) Line($"    {m.Id}: {m.Name} ({m.Xs}x{m.Ys})");
        Line("--- SearchMobs(\"wolf\", 5) ---");
        foreach (var m in SearchMobs("wolf", 5)) Line($"    {m.Name} look {m.Look} c{m.Color} {m.Hp}hp");
        Line("--- SearchItems(\"sword\", 5) ---");
        foreach (var i in SearchItems("sword", 5)) Line($"    #{i.Id} {i.Name} type{i.Type} dam{i.Dam} icon{i.Icon}");

        // --- Magic engine: archetype coverage + formula evaluation against known RTK values ---
        Line($"--- Spell fx: {SpellFx.Count} rows ---");
        var byArch = SpellFx.Values.GroupBy(f => f.Archetype).OrderByDescending(g => g.Count());
        Line("    " + string.Join("  ", byArch.Select(g => $"{g.Key}={g.Count()}")));
        // A representative caster: level 50, will 30, grace 20, might 40, 200 mana, 1000 HP.
        var vars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["player.level"] = 50, ["player.will"] = 30, ["player.grace"] = 20, ["player.might"] = 40,
            ["player.magic"] = 200, ["player.maxMagic"] = 200, ["player.health"] = 1000, ["player.maxHealth"] = 1000,
        };
        Line("--- Formula.Eval (level50 will30 grace20 might40 mana200 hp1000) ---");
        foreach (var key in new[] { "spark_mage", "heal_mage", "invoke_mage", "thunder_bolt_mage", "singe_mage" })
        {
            if (!SpellFx.TryGetValue(key, out var fx)) { Line($"    {key,-20} (no fx row)"); continue; }
            string amt = string.IsNullOrEmpty(fx.AmountExpr) ? "" : $" amount={Formula.Eval(fx.AmountExpr, vars):0}";
            string hc  = string.IsNullOrEmpty(fx.HealthCost) ? "" : $" healthCost={Formula.Eval(fx.HealthCost, vars):0}";
            Line($"    {key,-20} {fx.Archetype,-11} mana={fx.Mana,-4}{amt}{hc}  [{fx.AmountExpr}]");
        }
        // spot-check the arithmetic evaluator itself (independent of any spell row)
        Line("--- Formula sanity ---");
        foreach (var (expr, want) in new (string, double)[]
                 {
                     ("15 + math.floor(player.level / 2) + math.floor((player.will + 3) / 4)", 48),  // spark @50/30
                     ("math.ceil(player.magic * 2.15)", 430),
                     ("100 + (player.level * 2) + math.floor(((player.will + 1) / 2) * 2)", 230),
                     ("math.floor(player.maxMagic * .4)", 80),                                        // invoke cost
                 })
        {
            double got = Formula.Eval(expr, vars);
            Line($"    {(Math.Abs(got - want) < 0.5 ? "ok " : "XX ")}{got,6:0} (want {want,4:0})  {expr}");
        }

        // --- Effect graphic resolution (pcalign ladder → Effect.tbl id) ---
        Line("--- EffectAnim (spell → Effect.tbl id) ---");
        foreach (var (key, path) in new[]
                 {
                     ("spark_mage", 3), ("glimpse_of_the_void_mage", 3), ("bolt_mage", 3),
                     ("thunder_bolt_mage", 3), ("heal_mage", 3), ("ancestors_touch_mage", 3),
                     ("invoke_mage", 3), ("might_mage", 3),
                 })
        {
            if (!SpellFx.TryGetValue(key, out var fx)) { Line($"    {key,-24} (no fx)"); continue; }
            Line($"    {key,-24} arch={fx.Archetype,-11} pcalign={fx.PcAlign,-5} -> anim {EffectAnim(fx, path),3}  sound {EffectSound(fx, path)}");
        }

        bool spellsOk = SpellFx.Count > 0
            && SpellFx.TryGetValue("spark_mage", out var spk) && spk.Archetype == "Damage"
            && Math.Abs(Formula.Eval(spk.AmountExpr, vars) - 48) < 0.5
            && Math.Abs(Formula.Eval("math.ceil(player.magic * 2.15)", vars) - 430) < 0.5
            && EffectAnim(spk, 3) == 28                                          // spark → Effect.tbl 28
            && SpellFx.TryGetValue("heal_mage", out var hl) && EffectAnim(hl, 3) == 5;   // unaligned heal → 5

        bool ok = Maps.Count > 0 && Mobs.Count > 0 && Items.Count > 0
                  && FindMap("kugnae") is not null && FindMob("rabbit") is not null && spellsOk;
        Line(ok ? "SELFTEST: PASS" : "SELFTEST: FAIL (empty registry or missing expected entry)");
    }

    // ---- background music (0x19) --------------------------------------------------------------
    // The stock 4.95 client keeps its audio in NexusTK.snd, which ships exactly 12 background tracks
    // (1.mid .. 12.mid); the 0x19 music packet plays one by id with type 2 = MIDI. There is no original
    // map->track table in the client files, so we assign them: a few iconic hubs get a fixed theme, and
    // every other map gets a stable pick from its id (so neighbouring maps tend to differ). Tune freely.
    private static readonly Dictionary<ushort, byte> BgmByMap = new()
    {
        [0]   = 1,    // Kugnae (town)
        [330] = 2,    // Buya (town)
        [41]  = 5,    // Mythic Nexus
        [24]  = 6,    // Kugnae Donjon (dungeon)
    };

    /// <summary>The background track for a map: (bgm id 1..12, type 2 = MIDI). Iconic hubs are fixed;
    /// anything else maps deterministically onto one of the 12 stock midis via its id.</summary>
    public static (ushort bgm, byte type) BgmFor(ushort mapId)
    {
        byte bgm = BgmByMap.TryGetValue(mapId, out var pick) ? pick : (byte)((mapId % 12) + 1);
        return (bgm, 2);
    }

    // ---- lookups (used by the !warp / !maps / !mobs / !summon commands) ----

    public static bool TryMap(ushort id, out MapInfo map) => Maps.TryGetValue(id, out map!);

    /// <summary>Best map for a query: exact id, then exact (case-insensitive) name, then substring, then subsequence.</summary>
    public static MapInfo? FindMap(string query)
    {
        query = query.Trim();
        if (ushort.TryParse(query, out var id) && Maps.TryGetValue(id, out var byId)) return byId;
        return BestByName(Maps.Values, query, m => m.Name);
    }

    public static MobDef? FindMob(string query)
    {
        query = query.Trim();
        if (int.TryParse(query, out var id))
        {
            var byId = Mobs.FirstOrDefault(m => m.Id == id);
            if (byId is not null) return byId;
        }
        // match on display name OR internal key ("great horns" or "great_horns")
        return BestByName(Mobs, query, m => m.Name) ?? BestByName(Mobs, query, m => m.Key);
    }

    public static List<MapInfo> SearchMaps(string query, int limit) =>
        RankByName(Maps.Values, query, m => m.Name).Take(limit).ToList();

    public static List<MobDef> SearchMobs(string query, int limit) =>
        RankByName(Mobs, query, m => m.Name).Take(limit).ToList();

    public static ItemDef? FindItem(string query)
    {
        query = query.Trim();
        if (int.TryParse(query, out var id))
        {
            var byId = Items.FirstOrDefault(i => i.Id == id);
            if (byId is not null) return byId;
        }
        return BestByName(Items, query, i => i.Name) ?? BestByName(Items, query, i => i.Key);
    }

    public static ItemDef? ItemById(int id) => Items.FirstOrDefault(i => i.Id == id);
    public static ItemDef? ItemByKey(string key) =>
        Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    public static MobDef? MobById(int id) => Mobs.FirstOrDefault(m => m.Id == id);
    public static MobDef? MobByKey(string key) => Mobs.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    // ---- spells / classes (used by !spells / !learnspell + casting) ---------------------------

    /// <summary>The display name of a class/path id (e.g. 1 -> "Warrior"); "path&lt;id&gt;" if unknown.</summary>
    public static string PathName(int pathId) =>
        Paths.TryGetValue(pathId, out var n) && !string.IsNullOrEmpty(n) ? n : $"path{pathId}";

    /// <summary>Resolve a class/path NAME (as stored on the character, e.g. "Warrior") to its path id, or
    /// -1 if it matches no known class. Case-insensitive against the base class name (Paths.PthMark0).</summary>
    public static int PathIdForClass(string? className)
    {
        var name = (className ?? "").Trim();
        if (name.Length == 0) return -1;
        foreach (var kv in Paths)
            if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        return -1;
    }

    /// <summary>Real per-class level + item/gold cost to LEARN a spell from a trainer. <c>Items</c> is
    /// checked and consumed alongside <c>Gold</c>, all-or-nothing.</summary>
    public sealed record LearnCost(int Level, int Gold, (string Item, int Amount)[] Items);

    /// <summary>Per-spell, per-class real learn data — key → {pathId → cost}. Generated 2026-07-27 by
    /// <c>re/merge_spell_costs.py</c> from two sources, per the user's explicit ranking (archive beats Lua):
    /// <list type="bullet">
    /// <item>Archive-sourced (149 rows): cross-checked against <c>C:\Users\brian\Desktop\scraped_nexus_data\</c>
    /// (tswolf.com class spell-list pages, Wayback-dated 2001, + boards.nexustk.com tutor-board posts) —
    /// covers the base 1-99 spell list for all 4 classes plus the "peasant commons" spells (which turned out
    /// to NOT be flat-universal: Return/Approach/Summon are Rogue/Mage/Poet only at different levels each,
    /// see <c>nexustk-495-restricted-commons-spells</c> memory for the full reconciliation and the 9
    /// within-archive conflicts resolved directly with the user).</item>
    /// <item>Lua-fallback (424 rows): <c>re/extract_spell_requirements.py</c>'s static parse of every RTK
    /// spell script's own <c>requirements()</c> function, used only where no archive data exists.</item>
    /// </list>
    /// Deliberately NOT covered: Propose (its only real cost is the <c>engagement_ring</c>'s shop price,
    /// already charged in <c>ChapelAbility.BuyRing</c> before it grants the spell directly), subpath-only
    /// spells (PathId 5+, unreachable via <see cref="SpellsForClass"/> regardless since only base classes
    /// 0-4 are modeled), and the Il/Ee/Sam/Sa-san alignment-tier spells (gated by a rank-progression system
    /// this server doesn't implement, not a character level — would need a fake level to force into this
    /// table, which was deliberately avoided rather than guessed). Any spell with no entry here still teaches
    /// free at its CSV <c>SplLevel</c>/<c>PathId</c>, the pre-existing behavior.</summary>
    public static IReadOnlyDictionary<string, Dictionary<int, LearnCost>> SpellCosts { get; private set; } =
        new Dictionary<string, Dictionary<int, LearnCost>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The real cost to learn <paramref name="sp"/> as class <paramref name="pathId"/>, or null if
    /// this spell has no entry in <see cref="SpellCosts"/> (learned free at its CSV level, as before).</summary>
    public static LearnCost? LearnCostFor(SpellDef sp, int pathId) =>
        SpellCosts.TryGetValue(sp.Key, out var perClass) && perClass.TryGetValue(pathId, out var cost) ? cost : null;

    /// <summary>Every spell/skill a class can learn at or below <paramref name="maxLevel"/> for a given
    /// <paramref name="alignment"/> (0 unaligned / 1 Kwisin / 2 Mingken / 3 Ohaeng) — i.e. the teachable set
    /// for "!spells". <see cref="SpellCosts"/> is checked FIRST for a spell's key: if present, the class only
    /// qualifies if its pathId has an entry in that spell's per-class table (which is how Warrior ends up
    /// correctly excluded from Return/Approach/Summon — it simply has no row there), at THAT entry's level;
    /// spells with no <see cref="SpellCosts"/> entry fall back to the old universal rule (own path OR
    /// path-0 "peasant commons", at the CSV's flat <c>SplLevel</c>). A spell qualifies if it is universal
    /// (Alignment -1) OR matches the character's alignment; the other sub-alignments' parallel spells are
    /// excluded, so an unaligned character never gets the Kwisin/Mingken/Ohaeng variants (which often share a
    /// display name → looked like duplicates). Deduped by display name as a safety net, preferring the
    /// exact-alignment version over a universal one. Ordered by level then name so the spellbook fills in a
    /// sensible order.</summary>
    public static List<SpellDef> SpellsForClass(int pathId, int maxLevel, int alignment) =>
        Spells.Where(s => s.Alignment < 0 || s.Alignment == alignment)
              .Select(s => SpellCosts.TryGetValue(s.Key, out var perClass)
                  ? (perClass.TryGetValue(pathId, out var cost) ? s with { Level = cost.Level } : null)
                  : (s.PathId == pathId || s.PathId == 0 ? s : null))
              .Where(s => s is not null && s.Level <= maxLevel)
              .Select(s => s!)
              .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
              .Select(g => g.OrderByDescending(s => s.Alignment == alignment).ThenBy(s => s.Level).First())
              .OrderBy(s => s.Level).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
              .ToList();

    public static SpellDef? SpellById(int id) => Spells.FirstOrDefault(s => s.Id == id);
    public static SpellDef? SpellByKey(string key) => Spells.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The extracted RTK effect for a spell (real formula/archetype), or null if the export has no
    /// row for its identifier (⇒ caller falls back to the keyword classifier).</summary>
    public static SpellFx? FxFor(SpellDef sp) => SpellFx.TryGetValue(sp.Key, out var fx) ? fx : null;

    // Sentinel for "this spell has no pcalign arg" (skills / non-global-helper spells).
    public const int NoPcAlign = int.MinValue;

    // ---- spell effect graphic (client 0x29) ---------------------------------------------------
    // The 4.95 client's 0x29 handler plays effect N from Effect.tbl (128 effects) over an entity — it is NOT a
    // floating damage number (proven by disassembly of 0x4504b0 → 0x44e0a0 → the index-into-table copy at
    // 0x4354b0). RTK's shared helpers pick that effect id (and a sound id) from the spell's `pcalign` argument;
    // these two tables are ported verbatim from rtklua/Accepted/Spells/common/global_zap.lua + global_heal.lua.
    // Returns the Effect.tbl id (0..127), or -1 for "no graphic". Sound ids are carried for when a 4.95 sound
    // opcode is confirmed (not wired yet).

    /// <summary>pcalign → (Effect.tbl id, sound id) for damaging casts (global_zap / global_attack). Warrior
    /// (baseClass 1) and Rogue (baseClass 2) shift their &lt;10 pcalign by +100 / +200 first — same as RTK.</summary>
    public static (int anim, int sound) ZapEffect(int pcalign, int pathId)
    {
        if (pcalign < 10)
        {
            if (pathId == 1) pcalign += 100;   // warrior
            else if (pathId == 2) pcalign += 200;   // rogue
        }
        return pcalign switch
        {
            0 => (4, 56),   1 => (17, 59),  2 => (30, 57),  3 => (4, 55),          // aligned zaps
            10 => (27, 55), 11 => (28, 55), 12 => (29, 55), 13 => (-1, 58),        // thunder bolt / spark / singe / taunt(no gfx)
            30 => (8, 88),  31 => (54, 88), 32 => (104, 88), 33 => (112, 88),      // mage hellfire/inferno/doom
            34 => (41, 88), 35 => (42, 88), 36 => (43, 88),                        // mage fissure / lava surge / volcanic blast
            40 => (51, 88), 41 => (100, 88), 42 => (86, 88), 43 => (114, 88),      // poet retribution
            99 => (6, 88),                                                          // unaligned LS/WW (attack ladder)
            100 => (7, 88), 101 => (67, 14), 102 => (7, 87), 103 => (60, 87), 104 => (31, 30),  // warrior
            119 => (9, 14), 120 => (6, 88), 121 => (7, 87), 122 => (32, 87), 123 => (68, 94),   // warrior vita edits
            124 => (7, 88), 125 => (60, 102), 126 => (67, 88), 127 => (69, 88),
            200 => (9, 88), 201 => (67, 102), 202 => (32, 88), 203 => (68, 88), 204 => (69, 88),  // rogue
            251 => (17, 59), 252 => (30, 57), 253 => (4, 55),                       // class-override zaps
            400 => (12, -1), 401 => (44, -1),                                       // dart / death trap
            _ => (4, 56),                                                           // default unaligned zap
        };
    }

    /// <summary>pcalign → (Effect.tbl id, sound id) for healing casts (global_heal): 1 Kwi-Sin, 2 Ming-Ken,
    /// 3 Ohaeng, else unaligned.</summary>
    public static (int anim, int sound) HealEffect(int pcalign) => pcalign switch
    {
        1 => (65, 98),
        2 => (64, 63),
        3 => (63, 4),
        _ => (5, 4),
    };

    /// <summary>The Effect.tbl graphic id to play for a cast (−1 = none). A spell whose own Lua body calls
    /// sendAnimation (buffs, debuffs, Invoke) carries that id directly in <see cref="SpellFx.Animation"/>;
    /// damaging/healing casts get theirs from the pcalign ladder in the shared helper.</summary>
    public static int EffectAnim(SpellFx fx, int pathId)
    {
        if (fx.Animation > 0) return fx.Animation;                                  // spell set it explicitly
        if (fx.PcAlign == NoPcAlign) return -1;                                     // no helper call, no explicit anim
        return fx.Archetype == "Heal" ? HealEffect(fx.PcAlign).anim : ZapEffect(fx.PcAlign, pathId).anim;
    }

    /// <summary>The sound id to play for a cast (−1 = none), mirroring <see cref="EffectAnim"/>: the spell's own
    /// playSound id if it has one (buffs, Invoke), else the pcalign ladder's sound.</summary>
    public static int EffectSound(SpellFx fx, int pathId)
    {
        if (fx.Sound > 0) return fx.Sound;                                          // spell set it explicitly
        if (fx.PcAlign == NoPcAlign) return -1;
        return fx.Archetype == "Heal" ? HealEffect(fx.PcAlign).sound : ZapEffect(fx.PcAlign, pathId).sound;
    }

    public static SpellDef? FindSpell(string query)
    {
        query = query.Trim();
        if (int.TryParse(query, out var id))
        {
            var byId = Spells.FirstOrDefault(s => s.Id == id);
            if (byId is not null) return byId;
        }
        return BestByName(Spells, query, s => s.Name) ?? BestByName(Spells, query, s => s.Key);
    }

    public static List<SpellDef> SearchSpells(string query, int limit) =>
        RankByName(Spells, query, s => s.Name).Take(limit).ToList();

    // A spell's coarse effect category. There is NO per-spell effect data in the export (RTK runs ~900 Lua
    // scripts), so this is a best-guess keyword classifier over the name + identifier. It drives the generic
    // cast effect in Session.HandleCast: Damage spells deal magic damage, Heal spells restore HP, Buff/Utility
    // give feedback. Refine per spell later if bespoke behaviour is wanted.
    public enum SpellEffect { Utility, Damage, Heal, Buff }

    private static readonly string[] HealWords =
        { "heal", "remedy", "cure", "mend", "recover", "regen", "soothe", "bandage", "balm", "reviv",
          "rejuven", "renew", "vitali", "refresh", "restore" };
    private static readonly string[] DamageWords =
        { "bolt", "blast", "flame", "fire", "ice", "frost", "cold", "lightn", "thunder", "zap", "storm",
          "nova", "strike", "smite", "burn", "shock", "blaze", "meteor", "quake", "slash", "wrath", "doom",
          "drain", "wound", "blood", "venom", "poison", "chaos", "shard", "spike", "fang", "claw", "bite",
          "sever", "crush", "pierce", "inferno", "electr", "avalanche", "blizzard", "assault", "ambush",
          "assassin", "attack" };
    private static readonly string[] BuffWords =
        { "bless", "augment", "armor", "shield", "harden", "protect", "guard", "bolster", "fortif", "haste",
          "aegis", "barrier", "ward", "enchant", "empower", "strength" };

    /// <summary>Best-guess effect category from the spell's name/identifier keywords. Heal wins over Damage
    /// on overlap (e.g. "Healer's Revenge").</summary>
    public static SpellEffect EffectOf(SpellDef sp)
    {
        var s = (sp.Name + " " + sp.Key).ToLowerInvariant();
        bool Any(string[] words) { foreach (var k in words) if (s.Contains(k)) return true; return false; }
        if (Any(HealWords))   return SpellEffect.Heal;
        if (Any(DamageWords)) return SpellEffect.Damage;
        if (Any(BuffWords))   return SpellEffect.Buff;
        return SpellEffect.Utility;
    }

    private static readonly string[] AlignPrefixes = { "kwisin_", "mingken_", "ohaeng_" };
    private static readonly string[] ClassSuffixes = { "_peasant", "_warrior", "_rogue", "_mage", "_poet" };

    /// <summary>The spell's identifier with its sub-alignment prefix (kwisin_/mingken_/ohaeng_) and class
    /// suffix (_mage/_poet/…) stripped — so every variant of "Invoke" (invoke_mage, invoke_poet, invoke)
    /// collapses to the base key "invoke". Session.HandleCast switches on this to run bespoke per-spell
    /// effects (which a keyword category can't express, e.g. Invoke = trade HP for MP).</summary>
    public static string BaseKey(SpellDef sp)
    {
        var k = sp.Key.ToLowerInvariant();
        foreach (var pre in AlignPrefixes) if (k.StartsWith(pre)) { k = k[pre.Length..]; break; }
        foreach (var suf in ClassSuffixes) if (k.EndsWith(suf))   { k = k[..^suf.Length]; break; }
        return k;
    }

    /// <summary>The fixed spawn points on a map (empty if none / map has no spawn data).</summary>
    public static List<SpawnDef> SpawnsFor(ushort map) => Spawns.Where(s => s.Map == map).ToList();

    // ---- mob drops (0x16 floor loot) ----------------------------------------------------------

    /// <summary>Roll the drops for a slain mob against its real RTK drop table (<see cref="MobDrops"/>):
    /// every <see cref="LootRoll"/> line rolls independently (a mob can drop several at once), then at
    /// most one <see cref="RareRoll"/> line drops — the first one, in listed order, that hits. A mob with
    /// no table entry drops nothing. Returns the concrete (item-or-gold, amount) pairs to place on the
    /// floor (may be empty).</summary>
    public static List<RolledDrop> RollDrops(MobDef def, Random rng)
    {
        var outp = new List<RolledDrop>();
        if (!MobDrops.TryGetValue(def.Key, out var table)) return outp;

        foreach (var roll in table.Loot)
        {
            if (rng.NextDouble() * 100.0 >= roll.RatePercent) continue;
            int amount = rng.Next(1, roll.MaxAmount + 1);
            if (roll.ItemKey is null) { outp.Add(new RolledDrop(null, amount, true)); continue; }
            var it = ItemByKey(roll.ItemKey);
            if (it is not null) outp.Add(new RolledDrop(it, amount, false));
        }

        foreach (var rare in table.Rare)
        {
            if (rng.NextDouble() * 100.0 >= rare.RatePercent) continue;
            if (rare.ItemKey is null) { outp.Add(new RolledDrop(null, 1, true)); break; }
            var it = ItemByKey(rare.ItemKey);
            if (it is not null) outp.Add(new RolledDrop(it, 1, false));
            break;   // RTK's _handleRareLoot: only the first line that hits actually drops
        }
        return outp;
    }

    public static List<ItemDef> SearchItems(string query, int limit) =>
        RankByName(Items, query, i => i.Name).Take(limit).ToList();

    // ---- fuzzy ranking (shared by maps + mobs) ----

    private static T? BestByName<T>(IEnumerable<T> items, string q, Func<T, string> name) where T : class =>
        RankByName(items, q, name).FirstOrDefault();

    // Rank: exact (0) < prefix (1) < substring (2) < subsequence (3); ties broken by shorter name.
    // A blank query returns everything alphabetically (so "!maps" with no arg lists all).
    private static IEnumerable<T> RankByName<T>(IEnumerable<T> items, string q, Func<T, string> name)
    {
        q = q.Trim().ToLowerInvariant();
        return items
            .Select(it => (it, s: Score((name(it) ?? "").ToLowerInvariant(), q), n: name(it) ?? ""))
            .Where(t => t.s >= 0)
            .OrderBy(t => t.s).ThenBy(t => t.n.Length).ThenBy(t => t.n, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.it);
    }

    private static int Score(string name, string q)
    {
        if (q.Length == 0) return 4;            // no filter -> keep all (alphabetical)
        if (name.Length == 0) return -1;
        if (name == q) return 0;
        if (name.StartsWith(q)) return 1;
        if (name.Contains(q)) return 2;
        return IsSubsequence(q, name) ? 3 : -1; // "grhrn" matches "great horns"
    }

    private static bool IsSubsequence(string q, string name)
    {
        int i = 0;
        foreach (var c in name) if (i < q.Length && c == q[i]) i++;
        return i == q.Length;
    }

    // ---- CSV loaders ----

    private static Dictionary<ushort, MapInfo> LoadMaps(string? path)
    {
        var maps = new Dictionary<ushort, MapInfo>();
        foreach (var col in ReadCsv(path))
        {
            if (col.TryGetValue("id", out var sid) && ushort.TryParse(sid, out var id)
                && col.TryGetValue("xs", out var sxs) && ushort.TryParse(sxs, out var xs)
                && col.TryGetValue("ys", out var sys) && ushort.TryParse(sys, out var ys))
            {
                var name = Clean(col.GetValueOrDefault("name", ""));
                maps[id] = new MapInfo(id, string.IsNullOrEmpty(name) ? $"Map {id}" : name, xs, ys);
            }
        }
        return maps;
    }

    // id -> MapMetaInfo from the full RTK Maps table. unknown/blank region defaults to -1 (no kingdom),
    // warpOut to true (allow) so only an explicit 0 blocks warp-outs; the req*/max*/rejectmsg columns
    // default to 0/"" (no gate) when absent.
    private static Dictionary<ushort, MapMetaInfo> LoadMapMeta(string? path)
    {
        var meta = new Dictionary<ushort, MapMetaInfo>();
        foreach (var col in ReadCsv(path))
        {
            if (!col.TryGetValue("MapId", out var sid) || !ushort.TryParse(sid, out var id)) continue;
            int Rd(string k) { int.TryParse(col.GetValueOrDefault(k, "0"), out var v); return v; }
            // Vita/mana caps are stored as unsigned 32-bit in RTK; "no cap" is the sentinel 4294967295, which
            // overflows int.TryParse (silently yielding 0 -- looks like "no vita/mana at all" instead of
            // "unbounded"). Parse as long so the sentinel round-trips correctly.
            long Rl(string k) { long.TryParse(col.GetValueOrDefault(k, "0"), out var v); return v; }
            if (!int.TryParse(col.GetValueOrDefault("MapRegion", "-1"), out var region)) region = -1;
            bool warpOut = col.GetValueOrDefault("MapWarpout", "1") != "0";
            bool pvp = col.GetValueOrDefault("MapPvP", "0") == "1";
            // MapChat is RTK's "cantalk" flag (map.c sscanf column order matches): 1 = talk is BLOCKED on
            // this map (only 2/9850 maps set it), not "chat allowed" despite the name.
            bool canTalk = col.GetValueOrDefault("MapChat", "0") != "1";
            meta[id] = new MapMetaInfo(region, warpOut, pvp, canTalk,
                Rd("MapReqLvl"), Rd("MapReqPath"), Rd("MapReqMark"), Rl("MapReqVita"), Rl("MapReqMana"),
                Rd("MapLvlMax"), Rl("MapVitaMax"), Rl("MapManaMax"), Clean(col.GetValueOrDefault("MapRejectMsg", "")));
        }
        return meta;
    }

    private static List<MobDef> LoadMobs(string? path)
    {
        var mobs = new List<MobDef>();
        foreach (var col in ReadCsv(path))
        {
            if (!col.TryGetValue("MobLook", out var slook) || !ushort.TryParse(slook, out var look)) continue;
            int.TryParse(col.GetValueOrDefault("MobId", "0"), out var id);
            byte.TryParse(col.GetValueOrDefault("MobLookColor", "0"), out var color);
            int.TryParse(col.GetValueOrDefault("Vita", "0"), out var hp);
            int.TryParse(col.GetValueOrDefault("Exp", "0"), out var exp);
            int.TryParse(col.GetValueOrDefault("Level", "0"), out var lvl);
            int.TryParse(col.GetValueOrDefault("Will", "0"), out var will);
            // MobMoveTime (ms between move attempts). Absent/0 in older exports -> a calm default.
            int move = int.TryParse(col.GetValueOrDefault("MobMoveTime", "0"), out var mv) && mv > 0 ? mv : 2500;
            var name = Clean(col.GetValueOrDefault("Description", ""));
            var key = Clean(col.GetValueOrDefault("Identifier", ""));
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(key) ? $"mob{id}" : key;
            bool aggressive = col.GetValueOrDefault("MobBehavior", "0") == "1";
            int.TryParse(col.GetValueOrDefault("MinDmg", "1"), out var minDam);
            int.TryParse(col.GetValueOrDefault("MaxDmg", "1"), out var maxDam);
            if (minDam <= 0) minDam = 1;
            if (maxDam < minDam) maxDam = minDam;
            bool isBoss = col.GetValueOrDefault("MobIsBoss", "0") == "1";
            int.TryParse(col.GetValueOrDefault("MobProtection", "0"), out var protection);
            int.TryParse(col.GetValueOrDefault("MobHit", "0"), out var hit);
            int.TryParse(col.GetValueOrDefault("MobArmor", "0"), out var ac);
            int.TryParse(col.GetValueOrDefault("Grace", "0"), out var grace);
            mobs.Add(new MobDef(id, key, name, look, color, hp <= 0 ? 1 : hp, exp, lvl, move, will, aggressive, minDam, maxDam, isBoss, protection, hit, ac, grace));
        }
        return mobs;
    }

    private static Dictionary<string, string[]> LoadShopStock(string? path)
    {
        var stock = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var id = Clean(col.GetValueOrDefault("NpcIdentifier", ""));
            if (string.IsNullOrEmpty(id)) continue;
            var keys = Clean(col.GetValueOrDefault("ItemKeys", "")).Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (keys.Length > 0) stock[id] = keys;
        }
        return stock;
    }

    // MobDrops.csv "Loot"/"RareLoot" cells are pipe-separated "item:amount:rate" / "item:rate" triples/pairs
    // (re/extract_mob_drops.py); item key "GOLD" -> a null ItemKey (gold rather than an item).
    private static Dictionary<string, MobDropDef> LoadMobDrops(string? path)
    {
        var table = new Dictionary<string, MobDropDef>();
        foreach (var col in ReadCsv(path))
        {
            var key = Clean(col.GetValueOrDefault("MobKey", ""));
            if (string.IsNullOrEmpty(key)) continue;

            var loot = Clean(col.GetValueOrDefault("Loot", "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split(':'))
                .Where(p => p.Length == 3)
                .Select(p => new LootRoll(p[0] == "GOLD" ? null : p[0], int.Parse(p[1]),
                    double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();
            var rare = Clean(col.GetValueOrDefault("RareLoot", "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split(':'))
                .Where(p => p.Length == 2)
                .Select(p => new RareRoll(p[0] == "GOLD" ? null : p[0],
                    double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();

            if (loot.Length > 0 || rare.Length > 0) table[key] = new MobDropDef(loot, rare);
        }
        return table;
    }

    private static List<MinorQuestDef> LoadMinorQuests(string? path)
    {
        var quests = new List<MinorQuestDef>();
        foreach (var col in ReadCsv(path))
        {
            var key = Clean(col.GetValueOrDefault("Key", ""));
            if (string.IsNullOrEmpty(key)) continue;
            long L(string k) { long.TryParse(col.GetValueOrDefault(k, "0"), out var v); return v; }
            int  I(string k) { int.TryParse(col.GetValueOrDefault(k, "0"), out var v); return v; }
            var mobs = Clean(col.GetValueOrDefault("Mobs", "")).Split('|', StringSplitOptions.RemoveEmptyEntries);
            quests.Add(new MinorQuestDef(
                Clean(col.GetValueOrDefault("Tier", "Minor")), key, Clean(col.GetValueOrDefault("DisplayName", key)),
                mobs, I("MinLevel"), I("MaxLevel"), L("MinStat"), L("MaxStat"), I("MinMark"), I("MaxMark")));
        }
        return quests;
    }

    private static List<ItemDef> LoadItems(string? path)
    {
        var items = new List<ItemDef>();
        foreach (var col in ReadCsv(path))
        {
            if (!col.TryGetValue("ItmId", out var sid) || !int.TryParse(sid, out var id)) continue;
            byte  B(string k)  { byte.TryParse(col.GetValueOrDefault(k, "0"), out var v); return v; }
            ushort U(string k) { ushort.TryParse(col.GetValueOrDefault(k, "0"), out var v); return v; }
            int  I(string k)   { int.TryParse(col.GetValueOrDefault(k, "0"), out var v); return v; }

            var name = Clean(col.GetValueOrDefault("ItmDescription", ""));
            var key  = Clean(col.GetValueOrDefault("ItmIdentifier", ""));
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(key) ? $"item{id}" : key;

            items.Add(new ItemDef(
                id, key, name, B("ItmType"),
                U("ItmIcon"), B("ItmIconColor"), U("ItmLook"), B("ItmLookColor"),
                B("ItmSex"), B("ItmLevel"), U("ItmDurability"), I("ItmStackAmount"), I("ItmMaximumAmount"),
                I("ItmArmor"), I("ItmHit"), I("ItmDam"), I("ItmVita"), I("ItmMana"),
                I("ItmMight"), I("ItmWill"), I("ItmGrace"),
                NoDrop: I("ItmDroppable") != 0, Thrown: I("ItmThrown") != 0,
                I("ItmBuyPrice"), I("ItmSellPrice"), I("ItmMightRequired"), Sound: I("ItmSound"),
                Indestructible: I("ItmIndestructible") != 0,
                MinSDam: I("ItmMinimumSDamage"), MaxSDam: I("ItmMaximumSDamage"),
                MinLDam: I("ItmMinimumLDamage"), MaxLDam: I("ItmMaximumLDamage"),
                Protection: I("ItmProtection")));
        }
        return items;
    }

    // Map ranges removed as "not classic": whole regions that are RTK-authored reskins of existing classic
    // dungeons rather than original NexusTK content, cut out of the warp graph (not deleted from the CSVs) so
    // they're simply unreachable — revertable by trimming this list.
    // 410-419 "Buya Scorpion Cave": a scorpion-reskinned clone of the Kugnae Spider Cave (90-96) — same
    // level-42 gate, same shared mob-id pool (carrion_raven/pale_scorpion/massive_scorpion) with the spider
    // ids swapped for scorpion ids (giant_spider->vile_scorpion, radiant_spider->radiant_scorpion, plus an
    // extra scorpion_lurker/crimson_scorpion boss). Entrance was Buya (68,93)/(69,93).
    private static readonly (ushort lo, ushort hi)[] ExcludedMapRanges = { (410, 419) };
    private static bool IsExcludedMap(ushort map) => Array.Exists(ExcludedMapRanges, r => map >= r.lo && map <= r.hi);

    private static Dictionary<(ushort, ushort, ushort), (ushort, ushort, ushort)> LoadWarps(string? path)
    {
        var warps = new Dictionary<(ushort, ushort, ushort), (ushort, ushort, ushort)>();
        foreach (var col in ReadCsv(path))
        {
            if (ushort.TryParse(col.GetValueOrDefault("SourceMapId"), out var sm)
                && ushort.TryParse(col.GetValueOrDefault("SourceX"), out var sx)
                && ushort.TryParse(col.GetValueOrDefault("SourceY"), out var sy)
                && ushort.TryParse(col.GetValueOrDefault("DestinationMapId"), out var dm)
                && ushort.TryParse(col.GetValueOrDefault("DestinationX"), out var dx)
                && ushort.TryParse(col.GetValueOrDefault("DestinationY"), out var dy)
                && Maps.ContainsKey(dm)            // don't warp to a map the client can't render
                && !IsExcludedMap(sm) && !IsExcludedMap(dm))
            {
                warps[(sm, sx, sy)] = (dm, dx, dy);   // last write wins on duplicate source tiles
            }
        }
        return warps;
    }

    // Spawn points: SpnMobId,SpnMapId,SpnX,SpnY (+ RTK bookkeeping columns we ignore). Rows whose mob or
    // map is unknown are still returned; the world filters them against the loaded mob/map registries.
    /// <summary>The 4.95 client palette to draw a monster look with (0x07 color byte). Falls back to 0
    /// (the plain/base palette) for looks not in the table — the correct default for most monsters.</summary>
    public static byte PaletteFor(ushort look) => LookPalettes.TryGetValue(look, out var p) ? p : (byte)0;

    // Look,Palette from the decoded client Monster.tbl (re/monster-matcher). Look id -> client palette byte.
    private static Dictionary<ushort, byte> LoadLookPalettes(string? path)
    {
        var pals = new Dictionary<ushort, byte>();
        foreach (var col in ReadCsv(path))
            if (ushort.TryParse(col.GetValueOrDefault("Look"), out var look)
                && byte.TryParse(col.GetValueOrDefault("Palette"), out var pal))
                pals[look] = pal;
        return pals;
    }

    // Spawns dropped as NOT CLASSIC: content whose only purpose is a questline we don't (and won't yet) model,
    // left standing would just be a mute, purposeless mob wandering the map — worse than not spawning at all.
    // 729 spy_hwan "Hwan" (Buya 330 @38,99): captive NPC for the Spy subpath's interrogation storyline
    // (NPCs/subpaths/spy/hwan.lua) — the whole player-subpath system is unbuilt, so he can never be interacted
    // with as designed. Revisit if/when subpaths are ported.
    private static readonly HashSet<int> ExcludedSpawnMobIds = new() { 729 };

    private static List<SpawnDef> LoadSpawns(string? path)
    {
        var spawns = new List<SpawnDef>();
        foreach (var col in ReadCsv(path))
        {
            if (int.TryParse(col.GetValueOrDefault("SpnMobId"), out var mob)
                && ushort.TryParse(col.GetValueOrDefault("SpnMapId"), out var map)
                && ushort.TryParse(col.GetValueOrDefault("SpnX"), out var x)
                && ushort.TryParse(col.GetValueOrDefault("SpnY"), out var y)
                && !ExcludedSpawnMobIds.Contains(mob))
            {
                spawns.Add(new SpawnDef(mob, map, x, y));
            }
        }
        return spawns;
    }

    private static List<AreaSpawnDef> LoadAreaSpawns(string? path)
    {
        var spawns = new List<AreaSpawnDef>();
        foreach (var col in ReadCsv(path))
        {
            if (int.TryParse(col.GetValueOrDefault("MobId"), out var mob)
                && ushort.TryParse(col.GetValueOrDefault("Map"), out var map)
                && int.TryParse(col.GetValueOrDefault("Count"), out var count) && count > 0
                && ushort.TryParse(col.GetValueOrDefault("MinX"), out var minX)
                && ushort.TryParse(col.GetValueOrDefault("MinY"), out var minY)
                && ushort.TryParse(col.GetValueOrDefault("MaxX"), out var maxX)
                && ushort.TryParse(col.GetValueOrDefault("MaxY"), out var maxY))
            {
                // RespawnSec is optional — present only in the trap-spawn supplement (rare bosses), absent
                // from the base handleSpawn rows, so a missing/blank column defaults to 0 (normal cadence).
                int.TryParse(col.GetValueOrDefault("RespawnSec", "0"), out var respawnSec);
                spawns.Add(new AreaSpawnDef(mob, map, count, minX, minY, maxX, maxY, respawnSec));
            }
        }
        return spawns;
    }

    // NPCs whose NpcLook exceeds the 4.95 client's Monster.tbl ceiling (NumMonsters 327, ids 0..326) draw
    // nothing — authored against a later RTK client's larger monster table. Of the 22 found this way, these
    // 20 are abandoned/unreachable content with no feature behind them (the Abyssal Crystal zodiac puzzle +
    // its questgivers/instance chests/jukeboxes — RTK's own team hid the crystals via a later migration
    // rather than fix them; see [[nexustk-495-broken-npc-assets]]) so they're dropped outright rather than
    // left as invisible clutter. Palace Concierge (335, a real Nagnang town NPC) is deliberately NOT in this
    // list.
    //
    // The 3 SalonNpc rows (236 Seme/Kugnae, 237 Serge/Buya, 318 Sarge/Nagnang) are dropped per user direction:
    // real RTK puts Face/Gender on the Rogue Guild Shaman (RogueGuildShamanNpc), not the salon barbers — we'd
    // duplicated the service onto the salons too, but the user wants it Shaman-only, so these three go rather
    // than stay as barbers with no haircut/dye feature behind them.
    private static readonly HashSet<int> DroppedNpcIds = new()
    {
        266,                                                              // HealerOfDoomNpc (Mythic Nexus)
        359, 363,                                                         // JukeboxNpc (Tower Arena, Carnage Hall)
        364, 365, 366, 367, 368, 369, 370, 371, 372, 373, 374, 375,       // *CrystalNpc (Abyssal Crystal x12)
        376, 377, 378,                                                    // Librarian Reeves, Assistant Yan, Exorcist Layla
        386, 387,                                                         // Baekdu Chest, Abyssal Chest
        212,                                                               // PyungPetNpc, "Pyung's pet" scorpion (Buya arena/Pyung Shop map 359)
        236, 237, 318,                                                    // SalonNpc x3 (Seme/Serge/Sarge) — Face/Gender stays Rogue-hall-only
    };

    // Per-NPC corrections layered onto the raw CSV row (position and/or look), for NPCs we keep but whose RTK
    // data is wrong for THIS client. NpcId 51 = Bagai, the "Bagai Shop" potion seller (map 363, a 12x12
    // single-room instance): CSV placed him at (2,6); user (who knows the actual room layout) says the
    // counter is at (2,3). Look (86) was already in-range, no swap needed.
    private static readonly Dictionary<int, (ushort? X, ushort? Y, ushort? Look)> NpcOverrides = new()
    {
        [51]  = (2, 3, null), // Bagai, Bagai Shop
    };

    // Stationary NPCs (RTK NPCs0.csv). We keep only NPCs whose map the client can render and that sit on a
    // real tile (skip the (0,0) placeholders — f1npc, treasure portals — which aren't placed beings). Look
    // is the creature sprite; the world draws them via the same 0x07 path as a mob (see World.PopulateNpcs).
    private static List<NpcDef> LoadNpcs(string? path)
    {
        var npcs = new List<NpcDef>();
        foreach (var col in ReadCsv(path))
        {
            if (!int.TryParse(col.GetValueOrDefault("NpcId"), out var id)) continue;
            if (DroppedNpcIds.Contains(id)) continue;
            ushort.TryParse(col.GetValueOrDefault("NpcMapId", "0"), out var map);
            ushort.TryParse(col.GetValueOrDefault("NpcX", "0"), out var x);
            ushort.TryParse(col.GetValueOrDefault("NpcY", "0"), out var y);
            ushort.TryParse(col.GetValueOrDefault("NpcLook", "0"), out var look);
            byte.TryParse(col.GetValueOrDefault("NpcLookColor", "0"), out var color);
            int.TryParse(col.GetValueOrDefault("NpcMoveTime", "0"), out var move);
            int.TryParse(col.GetValueOrDefault("NpcReturnDistance", "0"), out var leash);
            bool Flag(string k) => col.GetValueOrDefault(k, "0") == "1";
            if (NpcOverrides.TryGetValue(id, out var ov))
            { x = ov.X ?? x; y = ov.Y ?? y; look = ov.Look ?? look; }
            if (!Maps.ContainsKey(map)) continue;        // map the 4.95 client can't render
            if (x == 0 && y == 0) continue;              // (0,0) = unplaced placeholder / abstract NPC
            var name = Clean(col.GetValueOrDefault("NpcDescription", ""));
            var key = Clean(col.GetValueOrDefault("NpcIdentifier", ""));
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(key) ? $"npc{id}" : key;
            npcs.Add(new NpcDef(id, key, name, map, x, y, Dir: 2, look, color,
                IsChar: Flag("NpcIsChar"), Shop: Flag("NpcIsShopNpc"),
                Repair: Flag("NpcIsRepairNpc"), Bank: Flag("NpcIsBankNpc"),
                MoveTime: move, ReturnDistance: leash));
        }
        return npcs;
    }

    // Class/path table: PthId -> base class name (PthMark0). The higher PthMark columns are per-rank
    // titles ("Il san (W)" …) we don't need here.
    private static Dictionary<int, string> LoadPaths(string? path)
    {
        var paths = new Dictionary<int, string>();
        foreach (var col in ReadCsv(path))
            if (int.TryParse(col.GetValueOrDefault("PthId"), out var id))
                paths[id] = Clean(col.GetValueOrDefault("PthMark0", ""));
        return paths;
    }

    // See CraftingToggleOverrides above. Sparse by design — a skill missing from the file (or the file
    // missing entirely) just falls through to CraftingToggles.DefaultDisabled.
    private static Dictionary<string, bool> LoadCraftingToggles(string? path)
    {
        var overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var skill = col.GetValueOrDefault("Skill", "").Trim();
            if (skill.Length == 0) continue;
            if (int.TryParse(col.GetValueOrDefault("Enabled"), out var en)) overrides[skill] = en != 0;
        }
        return overrides;
    }

    // See NpcToggleOverrides above. Sparse by design — an id missing from the file (or the file missing
    // entirely) just falls through to NpcToggles.DefaultDisabled.
    private static Dictionary<int, bool> LoadNpcToggles(string? path)
    {
        var overrides = new Dictionary<int, bool>();
        foreach (var col in ReadCsv(path))
        {
            if (!int.TryParse(col.GetValueOrDefault("NpcId"), out var id)) continue;
            if (int.TryParse(col.GetValueOrDefault("Enabled"), out var en)) overrides[id] = en != 0;
        }
        return overrides;
    }

    // Per-path cumulative-exp-to-level table (RTK rtk/db/level_db.txt, classdb_level): LevelExp[path][level] =
    // total exp needed to LEAVE `level` (i.e. reach level+1). Long-format CSV (data/game-data/LevelExp.csv,
    // generated from the RTK file — see awk one-liner in git history) with one row per (Path, Level). Path ids
    // match PathIdForClass (0 Peasant/1 Warrior/2 Rogue/3 Mage/4 Poet); level 99 is the cap and has no entry.
    private static Dictionary<int, Dictionary<int, uint>> LevelExp = new();

    private static Dictionary<int, Dictionary<int, uint>> LoadLevelExp(string? path)
    {
        var table = new Dictionary<int, Dictionary<int, uint>>();
        foreach (var col in ReadCsv(path))
        {
            if (!int.TryParse(col.GetValueOrDefault("Path"), out var p)) continue;
            if (!int.TryParse(col.GetValueOrDefault("Level"), out var lvl)) continue;
            if (!uint.TryParse(col.GetValueOrDefault("CumExp"), out var exp)) continue;
            if (!table.TryGetValue(p, out var byLevel)) table[p] = byLevel = new Dictionary<int, uint>();
            byLevel[lvl] = exp;
        }
        return table;
    }

    /// <summary>Total exp required to advance past <paramref name="level"/> on <paramref name="pathId"/>
    /// (0 at the level-99 cap or on a lookup miss — treated as "no further threshold").</summary>
    public static uint ExpToNext(int pathId, int level)
    {
        if (level >= 99) return 0;
        if (!LevelExp.TryGetValue(pathId, out var byLevel) && !LevelExp.TryGetValue(0, out byLevel)) return 0;
        return byLevel.GetValueOrDefault(level, 0u);
    }

    // Rage-tier spells (RTK Scripts/wolfs_fury.lua, tigers_fury.lua, dragons_fury.lua, baekhos_rage.lua —
    // Warrior AND Rogue both progress through some of these, per-class level gates differ) — the flat
    // multiplier `player.rage` swingDamage.lua's _getPlayerSwingDamage multiplies the WHOLE swing by.
    // Real RTK rejects re-casting ANY fury while one is already active rather than letting a stronger tier
    // overwrite a weaker one (Session.CastRage). Values/levels straight from the Lua source, since
    // SplLevel is 0 for these in the export (see SpellLevelOverrides below — the real gate lives in each
    // spell's Lua requirements() function, which the CSV export never captured for Type-5 skills).
    private static readonly Dictionary<string, int> RageAmount = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wolfs_fury_warrior"] = 2, ["wolfs_fury_rogue"] = 2,
        ["tigers_fury_warrior"] = 3, ["tigers_fury_rogue"] = 3,
        ["dragons_fury_warrior"] = 4,
        ["baekhos_rage_rogue"] = 5,
    };

    /// <summary>The rage multiplier this spell/skill arms, or null if it isn't a rage-tier spell. See
    /// <see cref="RageAmount"/>.</summary>
    public static int? RageAmountFor(SpellDef sp) => RageAmount.TryGetValue(sp.Key, out var r) ? r : null;

    // RTK warrior/enchant.lua, infuse.lua, ingress.lua, vipers_venom.lua, dragons_flame.lua + rogue/
    // tigers_fortitude.lua, baekhos_blade.lua: a weapon-enchant STANCE (player.enchant). Unlike rage (which
    // swingDamage.lua multiplies the WHOLE swing by), enchant only multiplies the raw weapon-swing term
    // (s/2) — see Session.PlayerSwingDamage. All 16 identifiers share one mutual-exclusion group (RTK
    // "enchants" checkIfCast table, spellTables.lua) — casting any one while another (or itself) is already
    // active just re-prints "This spell is already active.", never stacks/upgrades (Session.CastEnchant).
    // Mana/level are hardcoded straight from each spell's own Lua (not trusted from the CSV export — same
    // Type-5-skill gap as rage/stealth/sacrifice-strikes; tigers_fortitude_rogue genuinely costs 0 mana,
    // just consumes cast components via requirements()).
    private static readonly Dictionary<string, (double Amt, int Mana)> EnchantSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enchant_warrior"] = (1.2, 60), ["spiritual_aid_warrior"] = (1.2, 60),
        ["oneness_warrior"] = (1.2, 60), ["strengthen_weapon_warrior"] = (1.2, 60),

        ["infuse_warrior"] = (2, 90), ["tincture_of_the_unknown_warrior"] = (2, 90),
        ["tigers_claw_warrior"] = (2, 90), ["whetstone_warrior"] = (2, 90),

        ["ingress_warrior"] = (3, 90), ["hand_of_darkness_warrior"] = (3, 90),
        ["dragons_claw_warrior"] = (3, 90), ["razors_edge_warrior"] = (3, 90),

        ["vipers_venom_warrior"] = (4, 5000),
        ["dragons_flame_warrior"] = (6, 10000),
        ["tigers_fortitude_rogue"] = (1.5, 0),
        ["baekhos_blade_rogue"] = (2.25, 6000),
    };
    public static (double Amt, int Mana)? EnchantFor(SpellDef sp) => EnchantSpells.TryGetValue(sp.Key, out var e) ? e : null;

    // Rogue Invisible (+3 same-mechanic aliases per alignment: Spirit's Form/Life's Cloak/Glass Form —
    // RTK Spells/rogue/invisible.lua): sets player.state=2, which swingDamage.lua reads as a flat 9x
    // damage multiplier on the swing that follows (a sneak-attack bonus that then breaks the stealth —
    // see Session.PlayerSwingDamage). NOTE: this only ports the DAMAGE multiplier, not real invisibility —
    // RTK's PC_INVIS state also hides the player's sprite from other clients (clif.c), which would need
    // viewport/ShowPlayer changes this pass doesn't touch.
    private static readonly HashSet<string> StealthSpells =
        new(StringComparer.OrdinalIgnoreCase) { "invisible_rogue", "spirits_form_rogue", "lifes_cloak_rogue", "glass_form_rogue" };
    public static bool IsStealthSpell(SpellDef sp) => StealthSpells.Contains(sp.Key);

    // RTK rogue/lethal_strike.lua + desperate_attack.lua, warrior/berserk.lua + whirlwind.lua: a facing-tile
    // physical attack computed from the CASTER's OWN current HP/MP that costs the caster a big chunk of
    // their own HP the instant it lands. Each base identifier here is cast by ALL 4 of its alignment aliases
    // (Kwisin/Ming-Ken/Ohaeng flavor names only — same mechanic, same formula); RTK picks the display name
    // from the caster's OWN alignment stat, not from which alias identifier was actually granted/cast, so
    // Session.CastSacrificeStrike keys off _char.Alignment rather than sp.Key for that (and for whirlwind's
    // alignment-gated damage factor/HP cost).
    public enum SacrificeFamily { LethalStrike, DesperateAttack, Berserk, Whirlwind }
    private static readonly Dictionary<string, SacrificeFamily> SacrificeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lethal_strike_rogue"] = SacrificeFamily.LethalStrike, ["afterlifes_embrace_rogue"] = SacrificeFamily.LethalStrike,
        ["mingkens_judgement_rogue"] = SacrificeFamily.LethalStrike, ["calculating_blow_rogue"] = SacrificeFamily.LethalStrike,

        ["desperate_attack_rogue"] = SacrificeFamily.DesperateAttack, ["the_voids_measure_rogue"] = SacrificeFamily.DesperateAttack,
        ["beastly_frenzy_rogue"] = SacrificeFamily.DesperateAttack, ["tilting_the_balance_rogue"] = SacrificeFamily.DesperateAttack,

        ["berserk_warrior"] = SacrificeFamily.Berserk, ["no_fear_warrior"] = SacrificeFamily.Berserk,
        ["tigers_pounce_warrior"] = SacrificeFamily.Berserk, ["winds_blast_warrior"] = SacrificeFamily.Berserk,

        ["whirlwind_warrior"] = SacrificeFamily.Whirlwind, ["deaths_angel_warrior"] = SacrificeFamily.Whirlwind,
        ["natures_own_warrior"] = SacrificeFamily.Whirlwind, ["bladedance_warrior"] = SacrificeFamily.Whirlwind,
    };
    public static SacrificeFamily? SacrificeFamilyFor(SpellDef sp) => SacrificeAliases.TryGetValue(sp.Key, out var f) ? f : null;

    // RTK poet/inspiration.lua family (Draw Energy/Harness Power/Combine Focus/Inspiration — 4 reskins, one
    // mechanic): drains a GROUP MEMBER's entire current mana into the caster's own pool.
    private static readonly HashSet<string> ManaStealSpells = new(StringComparer.OrdinalIgnoreCase)
        { "draw_energy_poet", "harness_power_poet", "combine_focus_poet", "inspiration_poet" };
    public static bool IsManaStealSpell(SpellDef sp) => ManaStealSpells.Contains(sp.Key);

    // RTK poet/inspire.lua family (Inspire/Share Energy/Bestow Power/Release Focus — 4 reskins): tops off
    // ANY other player's mana using the caster's own, no group requirement.
    private static readonly HashSet<string> ManaGiftSpells = new(StringComparer.OrdinalIgnoreCase)
        { "inspire_poet", "share_energy_poet", "bestow_power_poet", "release_focus_poet" };
    public static bool IsManaGiftSpell(SpellDef sp) => ManaGiftSpells.Contains(sp.Key);

    // RTK poet/dispell.lua family (Dispell/Remove Magic/Return Natural/Restore Balance — 4 reskins): a
    // chance-based FULL buff/debuff wipe ("flushDuration") on a targeted player.
    private static readonly HashSet<string> CleanseSpells = new(StringComparer.OrdinalIgnoreCase)
        { "dispell_poet", "remove_magic_poet", "return_natural_poet", "restore_balance_poet" };
    public static bool IsCleanseSpell(SpellDef sp) => CleanseSpells.Contains(sp.Key);

    // RTK poet/resurrect.lua family (Resurrect/Return Spirit/Ming-Ken Blessing/Death Undone — 4 reskins):
    // revives a dead/ghost player to full health.
    private static readonly HashSet<string> ReviveSpells = new(StringComparer.OrdinalIgnoreCase)
        { "resurrect_poet", "return_spirit_poet", "mingken_blessing_poet", "death_undone_poet" };
    public static bool IsReviveSpell(SpellDef sp) => ReviveSpells.Contains(sp.Key);

    // RTK rogue/race.lua family (Race/Spiritual Jump/Leap of Faith/Transport — 4 independently-authored
    // copies of the same mechanic, not alias-delegated like the others above): jump up to 3 tiles in the
    // faced direction, stopping at the last passable tile.
    private static readonly HashSet<string> LeapSpells = new(StringComparer.OrdinalIgnoreCase)
        { "race_rogue", "spiritual_jump_rogue", "leap_of_faith_rogue", "transport_rogue" };
    public static bool IsLeapSpell(SpellDef sp) => LeapSpells.Contains(sp.Key);

    // RTK rogue/filch.lua family (Filch/Spirit's Hand/Quick Fingers/Light Touch — 4 independently-authored
    // copies of the same mechanic): grabs whatever's on the SINGLE tile directly in front of the caster
    // (despite the spell description's "up to 4 tiles" claim, the Lua's own loop only ever runs i=1) — coins
    // go straight to the purse, an item stack goes to inventory — but ONLY if no player is standing on that
    // tile. RTK also skips a tile that's someone's protected deathpile; this server has no per-item
    // ownership/deathpile model yet, so every ground stack is fair game once the tile-occupant check passes
    // (same simplification precedent as the "no PvP path" skip on debuffs/sacrifice-strikes).
    private static readonly HashSet<string> GroundLootSpells = new(StringComparer.OrdinalIgnoreCase)
        { "filch_rogue", "spirits_hand_rogue", "quick_fingers_rogue", "light_touch_rogue" };
    public static bool IsGroundLootSpell(SpellDef sp) => GroundLootSpells.Contains(sp.Key);

    // RTK rogue/ambush.lua (+ displacement_rogue/waylay_rogue/reflect_rogue, alias-delegated reskins):
    // "Leap over your enemy to face their back while attacking." No mana cost in the Lua at all — only
    // player:canCast(1,1,0) gates it, and repeat-use is paced by player.ambushTimer (attackSpeed-derived,
    // not a fixed RTK "aether"/cooldown — we don't model attackSpeed, so Session.CastAmbush uses a short
    // fixed cooldown instead). Session.CastAmbush.
    private static readonly HashSet<string> AmbushSpells = new(StringComparer.OrdinalIgnoreCase)
        { "ambush_rogue", "displacement_rogue", "waylay_rogue", "reflect_rogue" };
    public static bool IsAmbushSpell(SpellDef sp) => AmbushSpells.Contains(sp.Key);

    // RTK warrior/watchful_eye.lua family (+ spirits_whisper/creatures_guidance/spot_unbalance, alias-
    // delegated reskins, 125 mana/25s cooldown) and dog/spot_traps.lua (Rogue's own lower-level reskin of
    // the same seeSpotTraps() mechanic, 100 mana/6s cooldown — its own export row already carries a real
    // aether value, the warrior family's doesn't: RTK's setAether(key, 25000) never made it into the CSV).
    // Reveals nearby hidden rogue-trap NPCs (dart/snare/repeating/flash/spear/poison/death/sleep) via a
    // caster-only marker item — see World.TrapsNear. Session.CastSpotTraps.
    private static readonly HashSet<string> SpotTrapsSpells = new(StringComparer.OrdinalIgnoreCase)
        { "watchful_eye_warrior", "spirits_whisper_warrior", "creatures_guidance_warrior", "spot_unbalance_warrior", "spot_traps" };
    public static bool IsSpotTrapsSpell(SpellDef sp) => SpotTrapsSpells.Contains(sp.Key);

    // RTK rogue/judge.lua (Judge/Spiritual Advisor/Natural Talent/Appraise — 4 reskins) + rogue/spy.lua
    // (Spy/Spiritual Guide/Nature's Handiwork/Judgement Day — 4 reskins, same popup PLUS the target's
    // inventory list): a text popup of the target's class/name/level/title/might/will/grace. The judge
    // family requires the target STRICTLY lower level than the caster (`target.level >= player.level` fails);
    // the spy family allows an EQUAL level too (`target.level > player.level` fails) — a genuine, deliberate
    // difference in the Lua source, not a typo. Session.CastDivination.
    private static readonly HashSet<string> DivinationSpells = new(StringComparer.OrdinalIgnoreCase)
        { "judge_rogue", "spiritual_advisor_rogue", "natural_talent_rogue", "appraise_rogue" };
    private static readonly HashSet<string> DivinationSpySpells = new(StringComparer.OrdinalIgnoreCase)
        { "spy_rogue", "spiritual_guide_rogue", "natures_handiwork_rogue", "judgement_day_rogue" };
    public static bool IsDivinationSpell(SpellDef sp) => DivinationSpells.Contains(sp.Key) || DivinationSpySpells.Contains(sp.Key);
    public static bool IsDivinationSpySpell(SpellDef sp) => DivinationSpySpells.Contains(sp.Key);

    // RTK rogue/set_trap.lua (dispatcher, "What trap? >" SplQuestion — Spells.csv row 2701) + the 8
    // individual set_X_trap spells (dart_trap.lua/snare_trap.lua/repeating_dart_trap.lua/flash_trap.lua/
    // spear_trap.lua/poison_dart_trap.lua/death_trap.lua/sleep_trap.lua, Spells.csv rows 2702-2709): places
    // a hidden hazard on the caster's own tile (World.PlaceTrap) that fires once a MOB steps onto it
    // (World.Tick's movement pass — see Trap/TriggerTrapLocked), then despawns. The dispatcher itself
    // spends no mana (set_trap.lua never touches player.magic) — it just re-runs the SAME level gate +
    // mana cost as casting the specific trap directly, keyed off the typed answer. Real per-kind level/mana
    // straight from each spell's own Lua, not trusted from the CSV (SplLevel is 0 for all 9 — the familiar
    // Type-5-skill export gap). NOTE: spot_traps (Spells.csv SplPthId=99) is actually a DOG/companion-pet
    // spell (rtklua/Accepted/Spells/dog/spot_traps.lua), not one a player character ever learns directly —
    // out of scope here, revisit alongside the Poet pet-summon system if pets ever cast their own spells.
    public enum TrapKind { Dart, Snare, RepeatingDart, Flash, Spear, Poison, Death, Sleep }
    private static readonly Dictionary<string, (TrapKind Kind, int Level, int Mana)> TrapSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        ["set_dart_trap"] = (TrapKind.Dart, 26, 20),
        ["set_snare_trap"] = (TrapKind.Snare, 33, 120),
        ["set_repeating_dart_trap"] = (TrapKind.RepeatingDart, 44, 120),
        ["set_flash_trap"] = (TrapKind.Flash, 55, 320),
        ["set_spear_trap"] = (TrapKind.Spear, 66, 520),
        ["set_poison_dart_trap"] = (TrapKind.Poison, 77, 620),
        ["set_death_trap"] = (TrapKind.Death, 88, 1520),
        ["set_sleep_trap"] = (TrapKind.Sleep, 99, 2500),
    };
    public static (TrapKind Kind, int Level, int Mana)? TrapSpellFor(SpellDef sp) => TrapSpells.TryGetValue(sp.Key, out var t) ? t : null;
    public static bool IsTrapDispatcher(SpellDef sp) => sp.Key.Equals("set_trap", StringComparison.OrdinalIgnoreCase);

    // set_trap.lua's own q-string match ("dart"/"snare"/"repeating"/"flash"/"spear"/"poison"/"death"/"sleep")
    // to the underlying set_X_trap identifier that TrapSpellFor understands.
    public static string? TrapKeyForAnswer(string answer) => answer.Trim().ToLowerInvariant() switch
    {
        "dart" => "set_dart_trap", "snare" => "set_snare_trap", "repeating" => "set_repeating_dart_trap",
        "flash" => "set_flash_trap", "spear" => "set_spear_trap", "poison" => "set_poison_dart_trap",
        "death" => "set_death_trap", "sleep" => "set_sleep_trap", _ => null,
    };

    // RTK rogue/bladestorm_trap.lua (Spells.csv rows 2710-2713, all 4 SplAlignment reskins byte-for-byte
    // identical Lua) — despite the similar name, a COMPLETELY different mechanic from the set_X_trap hazard
    // family above: not a hidden hit-and-forget hazard but a visible step-triggered decoy that detonates a
    // facing-cone AoE off the TRIGGER's own facing (RTK block.side), dealing ONE shared HP-PERCENT damage
    // value (not flat) to both the trigger and every mob the cone catches. The FIRST spell in this whole
    // audit where a PLAYER stepping on it also triggers it, not just a mob — RTK guards the cone's PC targets
    // with `if block.pvp > 0`, which this server has no toggle for, so that's simplified to "cone hits mobs
    // only" (the established no-PvP-damage-path precedent everywhere else in this audit); the TRIGGER's own
    // self-damage IS kept when the trigger is a player (tripping a trap isn't "PvP" the way hitting another
    // player would be), but capped to leave at least 1 HP — same "self-cost, never actually lethal" precedent
    // as CastSacrificeStrike, since a trap tripped mid-walk has no death-flow of its own to hook cleanly.
    // Level 99, 1520 mana, 125s cooldown (RTK aether), the decoy auto-expires 21s after placement if never
    // triggered. NOT ported: the Lua's NPC heartbeat implies a 5000-mana/tick owner-upkeep drain while the
    // decoy is alive — the exact drain/early-deletion formula wasn't in the captured source, so this is a
    // documented gap (flat 1520 upfront cost only), not a guess. See Session.CastBladestormTrap/
    // ApplyBladestormSelfDamage, World.CheckPlayerTrapTrigger, World.TriggerTrapLocked's "bladestorm" case.
    private static readonly HashSet<string> BladestormTrapSpells =
        new(StringComparer.OrdinalIgnoreCase) { "set_bladestorm_trap", "set_swords_dance_trap", "set_tigers_ambush_trap", "set_cutting_edge_trap" };
    public static bool IsBladestormTrap(SpellDef sp) => BladestormTrapSpells.Contains(sp.Key);

    /// <summary>The wire string World.Trap/TriggerTrapLocked switches on.</summary>
    public static string TrapWireKind(TrapKind k) => k switch
    {
        TrapKind.Dart => "dart", TrapKind.Snare => "snare", TrapKind.RepeatingDart => "repeating",
        TrapKind.Flash => "flash", TrapKind.Spear => "spear", TrapKind.Poison => "poison",
        TrapKind.Death => "death", TrapKind.Sleep => "sleep", _ => "dart",
    };

    // RTK "morphs" duration group (spellTables.lua) — ~29 real castable identifiers (Rogue/Mage/Druid/
    // Merchant/Chongun/Barbarian) that spend mana to set player.disguise (an ANIMAL/NPC look id drawn from
    // the Monster.epf archive, exactly like a real mob) + player.state=4 for a fixed duration. Purely
    // cosmetic in RTK's own Lua — no stat/combat effect anywhere, just the look swap + an animation.
    //
    // CONFIRMED CLIENT-ENGINE BLOCKED for the caster's OWN screen (re-investigated 2026-07-26 via FRESH
    // static disassembly of NexusTK_local.exe, not just re-reading the old sweep notes): the 0x33 handler
    // (0x44fef0) funnels EVERY renderKind branch (1/2/3) — for BOTH the self-update path (0x461e30) and the
    // peer-create path (0x461a50, which DOES honor a monster look via [ent+0x178]/[+0x17c]!) — through one
    // more, later, unconditional call: `push 0x4f2a84 (literal, the PLAYER archive); call 0x463380`. That
    // constant is hardcoded at the call site, never read from the packet, for every entity 0x33 ever builds
    // — so a 0x33 packet can *never* draw a monster sprite, self or peer, type-0 or type-1. §7.2/§16.
    //
    // BUT that only blocks 0x33. Session.ShowPlayer is the single choke point every peer re-sync path funnels
    // through (join, map-change, equip/mount refresh — Session.cs greps confirm no other path builds a peer's
    // look). Rerouting a morphed player's entry there to the SAME 0x07 Monster.epf creature-spawn already used
    // for real mobs (0x8000|look) works for every OTHER client's view: the click/PlayerById resolution in
    // HandleClickInfo checks `_world.MobById` before `_world.PlayerById`, and a morphed player is never added
    // to the mob list — only their RENDER packet changes — so clicks/party/trade keep resolving to the real
    // player unchanged. Deliberately accepted tradeoffs: the caster's own screen still shows themselves as
    // human (the confirmed wall above); a 0x07 entity carries no name field (§7.2), so a morphed player shows
    // no floating nametag to others. Session.CastMorph/RevertMorph, Session.ShowPlayer.
    //
    // Mutual exclusion: real RTK's `hasDuration(OWN_NAME)` check means casting a DIFFERENT morph while one is
    // active leaves BOTH durations ticking (a genuine RTK quirk/bug — whichever's timer lapses last wins the
    // visual). Simplified here to "any morph cancels any other, consistent state always": casting a morph
    // while a *different* one is active replaces it outright; re-casting the SAME one un-morphs (toggle).
    //
    // (Look, LookFemale, Mana, DurationMs) — LookFemale=0 means every sex uses Look (only the two
    // "mingken_mask" reskins are sex-dependent buck/doe, per gangrel.lua's `if player.sex==1`).
    public static readonly Dictionary<string, (ushort Look, ushort LookFemale, int Mana, int DurationMs)> MorphSpells =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // feral family (rogue, level 10) — base (feral_rogue) asks a question; these 3 reskins are fixed.
        ["kwisin_cloak_rogue"] = (32, 0, 30, 185000), ["mingken_cloak_rogue"] = (153, 0, 30, 185000), ["ohaeng_cloak_rogue"] = (98, 0, 30, 185000),
        // rodent family (rogue, level 25)
        ["kwisin_disguise_rogue"] = (97, 0, 30, 185000), ["mingken_disguise_rogue"] = (183, 0, 30, 185000), ["ohaeng_disguise_rogue"] = (126, 0, 30, 185000),
        // gangrel family (rogue, level 33)
        ["kwisin_mask_rogue"] = (30, 0, 30, 185000), ["mingken_mask_rogue"] = (89, 88, 30, 185000), ["ohaeng_mask_rogue"] = (112, 0, 30, 185000),
        // beast family (rogue, level 40)
        ["kwisin_chameleon_rogue"] = (105, 0, 30, 185000), ["mingken_chameleon_rogue"] = (121, 0, 30, 185000), ["ohaeng_chameleon_rogue"] = (170, 0, 30, 185000),
        // gangrel family (mage, level 40)
        ["kwisin_mask_mage"] = (30, 0, 30, 185000), ["mingken_mask_mage"] = (89, 88, 30, 185000), ["ohaeng_mask_mage"] = (112, 0, 30, 185000),
        // beast family (mage, level 50)
        ["kwisin_chameleon_mage"] = (105, 0, 30, 185000), ["mingken_chameleon_mage"] = (121, 0, 30, 185000), ["ohaeng_chameleon_mage"] = (170, 0, 30, 185000),
        // standalone costume morphs (not alignment reskins of a base spell)
        ["dagger_uniform"] = (102, 0, 25, 162000),     // rogue lvl 1 — "Disguise you as Dagger"
        ["chongun_uniform"] = (37, 0, 25, 162000),     // Chongun subpath lvl 99
        ["marketer_guise"] = (52, 0, 125, 60000),      // Merchant subpath lvl 33
    };
    public static (ushort Look, ushort LookFemale, int Mana, int DurationMs)? MorphFor(SpellDef sp) =>
        MorphSpells.TryGetValue(sp.Key, out var m) ? m : null;

    // Question-dispatched morphs: SplQuestion asks which animal, the typed answer picks the look (RTK
    // player.question, lowercased). rodent_rogue.lua never actually lowers `player.question` into a local
    // `q` before comparing (a genuine copy-paste bug in that one file vs. every sibling) — ported as
    // OBVIOUSLY intended (rabbit/squirrel), not as the RTK bug, since the bug has no gameplay purpose.
    // wilderness_guise (Barbarian subpath, lvl 99) is RTK's odd one out: real RTK asks a MENU then chains
    // into separate recast()-only sub-spells (wolf_guise/rabbit_guise/deer_guise/sheep_guise/
    // thirsty_ogre_guise) that have no cast() of their own and are never independently castable — folded
    // directly into wilderness_guise's own answer table instead of modeling that indirection.
    public static readonly Dictionary<string, (Dictionary<string, ushort> Answers, int Mana, int DurationMs)> MorphDispatchSpells =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["feral_rogue"] = (new(StringComparer.OrdinalIgnoreCase) { ["pig"] = 19, ["piglet"] = 20, ["puppy"] = 18, ["mongrel"] = 103, ["cat"] = 182 }, 30, 185000),
        ["gangrel_rogue"] = (new(StringComparer.OrdinalIgnoreCase) { ["fox"] = 22, ["wolf"] = 23 }, 30, 185000),
        ["rodent_rogue"] = (new(StringComparer.OrdinalIgnoreCase) { ["rabbit"] = 21, ["squirrel"] = 25 }, 30, 185000),
        ["beast_rogue"] = (new(StringComparer.OrdinalIgnoreCase) { ["tiger"] = 29, ["bear"] = 24 }, 30, 185000),
        ["gangrel_mage"] = (new(StringComparer.OrdinalIgnoreCase) { ["fox"] = 22, ["wolf"] = 23 }, 30, 185000),
        ["beast_mage"] = (new(StringComparer.OrdinalIgnoreCase) { ["tiger"] = 29, ["bear"] = 24 }, 30, 185000),
        ["druids_rodent"] = (new(StringComparer.OrdinalIgnoreCase) { ["rabbit"] = 21, ["rat"] = 91 }, 30, 155000),
        ["wilderness_guise"] = (new(StringComparer.OrdinalIgnoreCase) { ["wolf"] = 23, ["rabbit"] = 21, ["deer"] = 89, ["sheep"] = 173, ["thirsty ogre"] = 185, ["ogre"] = 185 }, 30, 300000),
    };
    public static (Dictionary<string, ushort> Answers, int Mana, int DurationMs)? MorphDispatchFor(SpellDef sp) =>
        MorphDispatchSpells.TryGetValue(sp.Key, out var m) ? m : null;
    public static bool IsMorphSpell(SpellDef sp) => MorphSpells.ContainsKey(sp.Key) || MorphDispatchSpells.ContainsKey(sp.Key);

    // RTK Poet "Call of the Wild" pet-summon family (rtklua/Accepted/Spells/poet/cotw_*.lua): 7 tiers x 4
    // alignment reskins (28 identifiers) + a 29th (cotw_giasomo_bird_poet, mob 807) that has NO matching row
    // in mobs.csv at all — even the Lua flags it broken ("@TODO: I know this doesn't belong here, but
    // the COTW structure is so terrible already") — so it's skipped here, not silently miscounted. Every
    // tier spawns a real MobDef (all 28 DO exist in mobs.csv, correctly statted) owned by the caster,
    // capped by Content.PetCapFor and expiring 300s later (World.Tick). The top "avatar" tier is the one
    // real outlier: RTK charges GOLD (via requirements(), not mana) plus an 8-minute cooldown instead of the
    // flat 10-mana every other tier uses (cotw_wind_warrior.lua has no `player.magic` check at all).
    private static readonly Dictionary<string, (string MobKey, int Level, int Mana, int CooldownMs)> PetSpells =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // companion — level 68
        ["cotw_caterpillar_poet"] = ("cotw_caterpillar", 68, 10, 0), ["kwisin_companion_poet"] = ("kwisin_companion", 68, 10, 0),
        ["mingken_companion_poet"] = ("mingken_companion", 68, 10, 0), ["ohaeng_companion_poet"] = ("ohaeng_companion", 68, 10, 0),
        // assistant — level 72
        ["cotw_fluffy_dog_poet"] = ("cotw_fluffy_dog", 72, 10, 0), ["kwisin_assistant_poet"] = ("kwisin_assistant", 72, 10, 0),
        ["mingken_assistant_poet"] = ("mingken_assistant", 72, 10, 0), ["ohaeng_assistant_poet"] = ("ohaeng_assistant", 72, 10, 0),
        // protector — level 81
        ["cotw_panda_bear_poet"] = ("cotw_panda_bear", 81, 10, 0), ["kwisin_protector_poet"] = ("kwisin_protector", 81, 10, 0),
        ["mingken_protector_poet"] = ("mingken_protector", 81, 10, 0), ["ohaeng_protector_poet"] = ("ohaeng_protector", 81, 10, 0),
        // fighter — level 90
        ["cotw_wild_monkey_poet"] = ("cotw_wild_monkey", 90, 10, 0), ["kwisin_fighter_poet"] = ("kwisin_fighter", 90, 10, 0),
        ["mingken_fighter_poet"] = ("mingken_fighter", 90, 10, 0), ["ohaeng_fighter_poet"] = ("ohaeng_fighter", 90, 10, 0),
        // warrior — level 99
        ["cotw_gorilla_poet"] = ("cotw_gorilla", 99, 10, 0), ["kwisin_warrior_poet"] = ("kwisin_warrior", 99, 10, 0),
        ["mingken_warrior_poet"] = ("mingken_warrior", 99, 10, 0), ["ohaeng_warrior_poet"] = ("ohaeng_warrior", 99, 10, 0),
        // champion — level 99
        ["cotw_wind_dancer_poet"] = ("cotw_wind_dancer", 99, 10, 0), ["kwisin_champion_poet"] = ("kwisin_champion", 99, 10, 0),
        ["mingken_champion_poet"] = ("mingken_champion", 99, 10, 0), ["ohaeng_champion_poet"] = ("ohaeng_champion", 99, 10, 0),
        // avatar — level 99, gold+cooldown instead of mana
        ["cotw_wind_warrior_poet"] = ("cotw_wind_warrior", 99, 0, 480000), ["kwisin_avatar_poet"] = ("kwisin_avatar", 99, 0, 480000),
        ["mingken_avatar_poet"] = ("mingken_avatar", 99, 0, 480000), ["ohaeng_avatar_poet"] = ("ohaeng_avatar", 99, 0, 480000),
    };
    public static (string MobKey, int Level, int Mana, int CooldownMs)? PetSpellFor(SpellDef sp) =>
        PetSpells.TryGetValue(sp.Key, out var p) ? p : null;

    /// <summary>RTK cotw_spawnCheck's live-pet cap: 4 normally, 6 at level 90+, 8 at level 99.</summary>
    public static int PetCapFor(int level) => level >= 99 ? 8 : level >= 90 ? 6 : 4;

    // SplLevel is 0 for every rage/stealth/sacrifice-strike/mana-transfer/cleanse/revive/leap spell above
    // (and, going by that, likely many other Type-5 skills) in the export — their real level gate lives in
    // each spell's Lua requirements() function, which re/extract_spell_formulas.py never captured for skills
    // (only spells with a static formula). This overrides just the ones this pass wires up; the general
    // "skills learn at level 0" gap for every OTHER skill is a separate, broader export-completeness issue,
    // not fixed here.
    private static readonly Dictionary<string, int> SpellLevelOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wolfs_fury_warrior"] = 7,  ["wolfs_fury_rogue"] = 34,
        ["tigers_fury_warrior"] = 24, ["tigers_fury_rogue"] = 56,
        ["dragons_fury_warrior"] = 45,
        ["baekhos_rage_rogue"] = 99,
        ["invisible_rogue"] = 30, ["spirits_form_rogue"] = 30, ["lifes_cloak_rogue"] = 30, ["glass_form_rogue"] = 30,

        ["lethal_strike_rogue"] = 63, ["afterlifes_embrace_rogue"] = 63, ["mingkens_judgement_rogue"] = 63, ["calculating_blow_rogue"] = 63,
        ["desperate_attack_rogue"] = 45, ["the_voids_measure_rogue"] = 45, ["beastly_frenzy_rogue"] = 45, ["tilting_the_balance_rogue"] = 45,
        ["berserk_warrior"] = 40, ["no_fear_warrior"] = 40, ["tigers_pounce_warrior"] = 40, ["winds_blast_warrior"] = 40,
        ["whirlwind_warrior"] = 63, ["deaths_angel_warrior"] = 63, ["natures_own_warrior"] = 63, ["bladedance_warrior"] = 63,

        ["draw_energy_poet"] = 35, ["harness_power_poet"] = 35, ["combine_focus_poet"] = 35, ["inspiration_poet"] = 35,
        ["inspire_poet"] = 45, ["share_energy_poet"] = 45, ["bestow_power_poet"] = 45, ["release_focus_poet"] = 45,
        ["dispell_poet"] = 88, ["remove_magic_poet"] = 88, ["return_natural_poet"] = 88, ["restore_balance_poet"] = 88,
        ["resurrect_poet"] = 99, ["return_spirit_poet"] = 99, ["mingken_blessing_poet"] = 99, ["death_undone_poet"] = 99,

        ["race_rogue"] = 99, ["spiritual_jump_rogue"] = 99, ["leap_of_faith_rogue"] = 99, ["transport_rogue"] = 99,

        ["enchant_warrior"] = 28, ["spiritual_aid_warrior"] = 28, ["oneness_warrior"] = 28, ["strengthen_weapon_warrior"] = 28,
        ["infuse_warrior"] = 55, ["tincture_of_the_unknown_warrior"] = 55, ["tigers_claw_warrior"] = 55, ["whetstone_warrior"] = 55,
        ["ingress_warrior"] = 70, ["hand_of_darkness_warrior"] = 70, ["dragons_claw_warrior"] = 70, ["razors_edge_warrior"] = 70,
        ["vipers_venom_warrior"] = 99, ["dragons_flame_warrior"] = 99,
        ["tigers_fortitude_rogue"] = 99, ["baekhos_blade_rogue"] = 99,

        ["filch_rogue"] = 65, ["spirits_hand_rogue"] = 65, ["quick_fingers_rogue"] = 65, ["light_touch_rogue"] = 65,

        ["judge_rogue"] = 17, ["spiritual_advisor_rogue"] = 17, ["natural_talent_rogue"] = 17, ["appraise_rogue"] = 17,
        ["spy_rogue"] = 28, ["spiritual_guide_rogue"] = 28, ["natures_handiwork_rogue"] = 28, ["judgement_day_rogue"] = 28,

        ["set_dart_trap"] = 26, ["set_snare_trap"] = 33, ["set_repeating_dart_trap"] = 44, ["set_flash_trap"] = 55,
        ["set_spear_trap"] = 66, ["set_poison_dart_trap"] = 77, ["set_death_trap"] = 88, ["set_sleep_trap"] = 99,

        ["cotw_caterpillar_poet"] = 68, ["kwisin_companion_poet"] = 68, ["mingken_companion_poet"] = 68, ["ohaeng_companion_poet"] = 68,
        ["cotw_fluffy_dog_poet"] = 72, ["kwisin_assistant_poet"] = 72, ["mingken_assistant_poet"] = 72, ["ohaeng_assistant_poet"] = 72,
        ["cotw_panda_bear_poet"] = 81, ["kwisin_protector_poet"] = 81, ["mingken_protector_poet"] = 81, ["ohaeng_protector_poet"] = 81,
        ["cotw_wild_monkey_poet"] = 90, ["kwisin_fighter_poet"] = 90, ["mingken_fighter_poet"] = 90, ["ohaeng_fighter_poet"] = 90,
        ["cotw_gorilla_poet"] = 99, ["kwisin_warrior_poet"] = 99, ["mingken_warrior_poet"] = 99, ["ohaeng_warrior_poet"] = 99,
        ["cotw_wind_dancer_poet"] = 99, ["kwisin_champion_poet"] = 99, ["mingken_champion_poet"] = 99, ["ohaeng_champion_poet"] = 99,
        ["cotw_wind_warrior_poet"] = 99, ["kwisin_avatar_poet"] = 99, ["mingken_avatar_poet"] = 99, ["ohaeng_avatar_poet"] = 99,

        ["feral_rogue"] = 10, ["kwisin_cloak_rogue"] = 10, ["mingken_cloak_rogue"] = 10, ["ohaeng_cloak_rogue"] = 10,
        ["rodent_rogue"] = 25, ["kwisin_disguise_rogue"] = 25, ["mingken_disguise_rogue"] = 25, ["ohaeng_disguise_rogue"] = 25,
        ["gangrel_rogue"] = 33, ["kwisin_mask_rogue"] = 33, ["mingken_mask_rogue"] = 33, ["ohaeng_mask_rogue"] = 33,
        ["beast_rogue"] = 40, ["kwisin_chameleon_rogue"] = 40, ["mingken_chameleon_rogue"] = 40, ["ohaeng_chameleon_rogue"] = 40,
        ["gangrel_mage"] = 40, ["kwisin_mask_mage"] = 40, ["mingken_mask_mage"] = 40, ["ohaeng_mask_mage"] = 40,
        ["beast_mage"] = 50, ["kwisin_chameleon_mage"] = 50, ["mingken_chameleon_mage"] = 50, ["ohaeng_chameleon_mage"] = 50,
        ["dagger_uniform"] = 1, ["chongun_uniform"] = 99, ["marketer_guise"] = 33, ["wilderness_guise"] = 99, ["druids_rodent"] = 33,

        ["set_bladestorm_trap"] = 99, ["set_swords_dance_trap"] = 99, ["set_tigers_ambush_trap"] = 99, ["set_cutting_edge_trap"] = 99,
    };

    // Spells/skills. Rows that are section headers (name/ident begins with '=') or inactive (SplActive=0)
    // are skipped — they're book dividers in the RTK data, not castable. SplQuestion "NO" means "no prompt".
    private static List<SpellDef> LoadSpells(string? path)
    {
        var spells = new List<SpellDef>();
        foreach (var col in ReadCsv(path))
        {
            if (!col.TryGetValue("SplId", out var sid) || !int.TryParse(sid, out var id)) continue;
            if (col.GetValueOrDefault("SplActive", "1") == "0") continue;
            var name  = Clean(col.GetValueOrDefault("SplDescription", ""));
            var key   = Clean(col.GetValueOrDefault("SplIdentifier", ""));
            if (string.IsNullOrEmpty(name) || name.StartsWith("=") || key.StartsWith("=")) continue;
            byte.TryParse(col.GetValueOrDefault("SplType", "5"), out var type);
            int.TryParse(col.GetValueOrDefault("SplPthId", "0"), out var pth);
            int.TryParse(col.GetValueOrDefault("SplLevel", "0"), out var lvl);
            if (SpellLevelOverrides.TryGetValue(key, out var lvlOverride)) lvl = lvlOverride;
            if (!int.TryParse(col.GetValueOrDefault("SplAlignment", "-1"), out var align)) align = -1;
            var q = Clean(col.GetValueOrDefault("SplQuestion", ""));
            if (q.Equals("NO", StringComparison.OrdinalIgnoreCase)) q = "";
            bool canFail = col.GetValueOrDefault("SplCanFail", "0") == "1";   // RTK magicdb_canfail — gates the deflect roll
            spells.Add(new SpellDef(id, key, name, type, pth, lvl, align, q, canFail));
        }
        return spells;
    }

    // Per-spell effect rows from re/extract_spell_formulas.py (spell_effects.csv). Keyed by identifier so it
    // joins to SpellDef.Key. A missing/short file just yields an empty map (every cast then uses the keyword
    // classifier). Numbers parse leniently — a blank cell is 0.
    private static Dictionary<string, SpellFx> LoadSpellFx(string? path)
    {
        var fx = new Dictionary<string, SpellFx>(StringComparer.OrdinalIgnoreCase);
        static int I(Dictionary<string, string> c, string k)
            => int.TryParse(c.GetValueOrDefault(k, "").Trim(), out var v) ? v : 0;
        foreach (var col in ReadCsv(path))
        {
            var key = col.GetValueOrDefault("key", "").Trim();
            if (string.IsNullOrEmpty(key)) continue;
            fx[key] = new SpellFx(
                Key: key,
                Archetype: col.GetValueOrDefault("archetype", "Utility").Trim(),
                Mana: I(col, "mana"),
                AmountExpr: col.GetValueOrDefault("amountExpr", "").Trim(),
                BuffStat: col.GetValueOrDefault("buffStat", "").Trim(),
                BuffAmt: col.GetValueOrDefault("buffAmt", "").Trim(),
                DurationMs: I(col, "durationMs"),
                Debuff: col.GetValueOrDefault("debuff", "").Trim(),
                Chance: col.GetValueOrDefault("chance", "").Trim(),
                HealthCost: col.GetValueOrDefault("healthCost", "").Trim(),
                Animation: I(col, "animation"),
                Sound: I(col, "sound"),
                Aether: I(col, "aether"),
                PcAlign: int.TryParse(col.GetValueOrDefault("pcalign", "").Trim(), out var pa) ? pa : NoPcAlign,
                CureCat: col.GetValueOrDefault("cureCat", "").Trim());
        }
        return fx;
    }

    // SpellLearnCosts.csv (generated by re/merge_spell_costs.py, see SpellCosts' own doc): one row per
    // (spell key, class pathId) -> level + up to 4 (item,amount) pairs + gold. Multiple rows can share a key
    // (one per class) for the handful of spells whose real level/cost differs by class.
    private static Dictionary<string, Dictionary<int, LearnCost>> LoadSpellCosts(string? path)
    {
        var costs = new Dictionary<string, Dictionary<int, LearnCost>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var key = col.GetValueOrDefault("key", "").Trim();
            if (key.Length == 0) continue;
            if (!int.TryParse(col.GetValueOrDefault("pathId", ""), out var pathId)) continue;
            if (!int.TryParse(col.GetValueOrDefault("level", ""), out var level)) continue;
            int.TryParse(col.GetValueOrDefault("gold", "0"), out var gold);

            var items = new List<(string, int)>();
            for (int i = 1; i <= 4; i++)
            {
                var itemKey = col.GetValueOrDefault($"item{i}", "").Trim();
                if (itemKey.Length == 0) continue;
                int.TryParse(col.GetValueOrDefault($"amt{i}", "0"), out var amt);
                items.Add((itemKey, amt));
            }

            if (!costs.TryGetValue(key, out var perClass))
                costs[key] = perClass = new Dictionary<int, LearnCost>();
            perClass[pathId] = new LearnCost(level, gold, items.ToArray());
        }
        return costs;
    }

    // Minimal CSV reader: header row -> per-row {column: value} dicts. Handles quoted fields with commas.
    private static IEnumerable<Dictionary<string, string>> ReadCsv(string? path)
    {
        if (path is null || !File.Exists(path)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { yield break; }
        if (lines.Length < 2) yield break;

        var header = SplitCsv(lines[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var vals = SplitCsv(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < header.Count && c < vals.Count; c++) row[header[c]] = vals[c];
            yield return row;
        }
    }

    // Undo the SQL-dump backslash escaping the RTK data carries (e.g. "JadeSpear\'s Home" -> "JadeSpear's Home").
    private static string Clean(string s) =>
        s.Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();

    private static List<string> SplitCsv(string line)
    {
        var outp = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"') { if (q && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = !q; }
            else if (ch == ',' && !q) { outp.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        outp.Add(cur.ToString());
        return outp;
    }

    // Resolve an external data file: env override first, else <repo-root>/<parts...>. Repo root is the
    // dir holding the .sln (or Server+Shared), walked up from the running binary — cwd-independent.
    private static string? ResolvePath(string envVar, params string[] parts)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            bool isRoot = dir.GetFiles("*.sln").Length > 0
                       || (Directory.Exists(Path.Combine(dir.FullName, "Server"))
                           && Directory.Exists(Path.Combine(dir.FullName, "Shared")));
            if (isRoot) return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            dir = dir.Parent;
        }
        return null;
    }
}
