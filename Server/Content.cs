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
public sealed record MobDef(int Id, string Key, string Name, ushort Look, byte Color, int Hp, int Exp, int Level, int MoveTime, int Will = 0, bool Aggressive = false, int MinDam = 1, int MaxDam = 1, bool IsBoss = false, int Protection = 0, int Hit = 0, int Ac = 0, int Grace = 0, bool Flees = false);

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

/// <summary>An NPC placement from our NPC table (<c>data/game-data/NPCs.csv</c>): a stationary being on a map
/// tile. Nearly all render via the creature path (0x07) exactly like a mob — <c>Look</c>/<c>Color</c> mirror
/// <see cref="MobDef"/> — so the world spawns them as non-fighting mobs. <c>IsChar</c> marks the rare
/// human-composite NPC (0x33). The shop/repair/bank flags select the dialog behaviour on click. <c>Enabled</c>
/// (the CSV's Enabled column) is the spawn on/off switch — a disabled NPC keeps its row but isn't placed.</summary>
public sealed record NpcDef(
    int Id, string Key, string Name, ushort Map, ushort X, ushort Y, byte Dir,
    ushort Look, byte Color, bool IsChar, bool Shop, bool Repair, bool Bank,
    int MoveTime, int ReturnDistance, bool Enabled = true);

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
    int MinSDam = 0, int MaxSDam = 0, int MinLDam = 0, int MaxLDam = 0, int Protection = 0,
    // ItmHealing / ItmWisdom: the last two stat columns, parsed nowhere until the item tooltip needed them
    // ("Healing increase:" is a line in the real examine box). Carried for display; nothing consumes them
    // mechanically yet.
    int Healing = 0, int Wisdom = 0,
    string Text = "",
    // ItmBuyText: the shop blurb the game itself writes for an item's restriction ("Strength of 35 req",
    // "For level 5 or higher", "For peasants") -- 182 rows carry one. Free-text, so it's shown as a note on
    // the examine popup (Session.ItemInfoText) rather than parsed; the real gates are the numeric columns.
    string BuyText = "",
    // Wear restrictions that were parsed nowhere until now (RTK pc_useitem's path/mark gate, clif_checkinvbod's
    // break-on-death flag). PathId is ItmPthId: 0 = anyone; 1..5 = a BASE path (Warrior/Rogue/Mage/Poet/
    // Dreamweaver) which every subpath under it satisfies; >=6 = one EXACT subpath class (Chung ryong, Barbarian,
    // …). Mark is ItmMark, the subpath RANK (Il san = 1 … Oh san = 5) the wearer must have reached.
    // BreakOnDeath is ItmBoD (77 items): destroyed outright when you die, wherever it sits. Protected is
    // ItmProtected — RTK consumes a charge to RESTORE the item instead of breaking it; no row in the live
    // registry sets it, so it is carried for fidelity and never fires today.
    int PathId = 0, int Mark = 0, bool BreakOnDeath = false, bool Protected = false)
{
    /// <summary>The Item.epf id the 4.95 client must be told to draw — <see cref="Icon"/> with
    /// <see cref="IconColor"/> already folded in (see Content.ResolveIconColors). 4.95 has NO colour channel
    /// for item graphics at all: the bag/equip/ground draw calls take only a frame index and pull the palette
    /// from Item.tbl, so a colour variant is a SEPARATE consecutive frame. RTK's (icon, colour) pair is the
    /// later client's encoding of the same thing. Equals <see cref="Icon"/> for everything that isn't part of
    /// a recognised colour run.</summary>
    public ushort ClientIcon { get; init; } = Icon;

    /// <summary>ITM_WEAP..ITM_COAT (3..16) are wearable; everything else is consumable/junk.</summary>
    public bool IsEquip => Type is >= 3 and <= 16;
    public bool IsConsumable => Type is 0 or 1 or 2;     // EAT / USE / SMOKE
    public bool Stackable => StackAmount > 1 || MaxAmount > 1;

    /// <summary>The most a single bag slot (or vault entry) may hold — an Acorn caps at 201, arrows at 100.
    /// <c>ItmStackAmount</c> and <c>ItmMaximumAmount</c> agree on every row that sets either, so the larger
    /// of the two is the cap and non-stacking rows fall back to 1. Both columns were parsed from the start
    /// but only ever consulted to derive <see cref="Stackable"/>, so nothing capped anything: stacks grew
    /// without limit wherever items merged (pickup, vault deposit), and a 271-Acorn slot was reachable.</summary>
    public int StackCap => Math.Max(1, Math.Max(StackAmount, MaxAmount));

    /// <summary>Most of this item the whole bag may hold, across every slot; 0 means uncapped.
    /// <para><c>ItmMaximumAmount</c> is NOT a duplicate of <c>ItmStackAmount</c> — it is the inventory-wide
    /// total. 203 rows set it and it equals the stack size on every single one, so a stackable item is
    /// limited to exactly ONE stack: you cannot carry two piles of acorns or two of wool. Wine, pipes and
    /// arrows look like the exception but aren't stacks at all (<c>stack=1, max=0</c>) — they're individual
    /// items, one per slot, so any number of slots may hold them.</para></summary>
    public int CarryCap => MaxAmount;

    /// <summary>A charged consumable (RTK ITM_SMOKE: wine/liquor/cigarettes): N uses stored in the
    /// durability field, with <see cref="Text"/> as the unit label ("sips"/"puffs"). Each use spends one
    /// charge and the item is removed only at 0 (RTK pc_useitem ITM_SMOKE). The "indestructible" items carry
    /// ItmDurability=1000000, which overflows the ushort parse to 0 and is thus already excluded by > 0;
    /// requiring a unit label excludes ordinary food/potions.</summary>
    public bool IsCharged => IsConsumable && !string.IsNullOrEmpty(Text) && Durability > 0;

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

// The old hard-coded item -> effect table (ItemUseEffect record + Content.ItemEffects dictionary) has moved
// out of C# into the data-driven verb/row Lua system, exactly like spells: data/game-data/ItemParams.csv is
// the "row" (each consumable's verb + numeric params), data/game-data/item_verbs.lua is the "verb" (the
// logic), and Session.ApplyItemEffect runs them through ItemContext (see Server/ItemScript.cs). Both files
// hot-reload via @reload, so a food's heal amount or a potion's ward duration is a CSV edit, not a rebuild.

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

    // Per-spell TARGET flavor line (data/game-data/SpellText.csv), CANONICAL from LIVE NexusTK — supersedes RTK.
    // The caster always just sees "You cast <name>." (Session.HandleCast); the TARGET of a spell additionally
    // sees this line when present. On a self-cast you are both, so you see the flavor THEN the cast line.
    public static IReadOnlyDictionary<string, (string Target, string Fade)> SpellTexts { get; private set; } =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
    /// <summary>The live flavor shown to the TARGET when a spell is applied, or "" if none is recorded.</summary>
    public static string TargetTextFor(string key) => SpellTexts.TryGetValue(key, out var t) ? t.Target : "";
    /// <summary>The live flavor shown when a timed buff FADES (RTK uncast), or "" if none is recorded.</summary>
    public static string FadeTextFor(string key) => SpellTexts.TryGetValue(key, out var t) ? t.Fade : "";

    // Fixed monster spawn points (data/game-data/Spawns.csv). One live mob per point; the world respawns it on death.
    public static IReadOnlyList<SpawnDef> Spawns { get; private set; } = new List<SpawnDef>();

    // Area spawns from RTK's Lua spawner (data/game-data/AreaSpawns.csv): the hunting-map mob populations
    // (Mythic caves, wilderness, dungeons) that the static Spawns table doesn't cover. See AreaSpawnDef.
    public static IReadOnlyList<AreaSpawnDef> AreaSpawns { get; private set; } = new List<AreaSpawnDef>();

    // Stationary NPCs (data/game-data/NPCs.csv), placed once by the world as non-fighting mobs. Keyed by NpcId for
    // click-time dialog lookup.
    public static IReadOnlyList<NpcDef> Npcs { get; private set; } = new List<NpcDef>();
    private static IReadOnlyDictionary<int, NpcDef> _npcById = new Dictionary<int, NpcDef>();
    public static NpcDef? NpcById(int id) => _npcById.TryGetValue(id, out var n) ? n : null;

    // O(1) lookup indexes over the Items/Mobs/Spells lists + the class-name→path map, all rebuilt in Load() so
    // each swaps together with its source list (same atomicity story as _npcById). These replace the old
    // per-call LINQ FirstOrDefault scans over 2.5k items / 700 mobs / 900 spells, which ran on hot paths
    // (RegenTick, combat). FIRST occurrence wins on a duplicate id/key — matches the old FirstOrDefault. Key
    // lookups are case-insensitive.
    private static IReadOnlyDictionary<int, ItemDef> _itemById = new Dictionary<int, ItemDef>();
    private static IReadOnlyDictionary<string, ItemDef> _itemByKey = new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<int, MobDef> _mobById = new Dictionary<int, MobDef>();
    private static IReadOnlyDictionary<string, MobDef> _mobByKey = new Dictionary<string, MobDef>(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<int, SpellDef> _spellById = new Dictionary<int, SpellDef>();
    private static IReadOnlyDictionary<string, SpellDef> _spellByKey = new Dictionary<string, SpellDef>(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, int> _pathIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    // Build a "first occurrence wins" index (TryAdd keeps the first, matching the replaced FirstOrDefault).
    private static Dictionary<TK, TV> IndexFirst<TK, TV>(IEnumerable<TV> items, Func<TV, TK> key, IEqualityComparer<TK>? cmp = null) where TK : notnull
    {
        var d = new Dictionary<TK, TV>(cmp);
        foreach (var v in items) d.TryAdd(key(v), v);
        return d;
    }

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


    // ---- Mythic Nexus zodiac cave entrances (data/game-data/MythicCaves.csv) ------------------------------
    // The 12 zodiac caves' entrance tiles, destination, and per-tier (cave 1/2/3) level+vita/mana gates.
    // Requirement numbers are archival (cross-referenced against 4 tutor posts — see the row Sources and
    // Sources.csv tutor-caves-*); the tile/destination geometry is RTK routing (onScriptedTilesMythic.lua).
    // Consumed by Session.TryMythicCaveEntrance. A tier is met when level >= T{n}Level AND
    // (baseMaxHP >= T{n}Vita OR baseMaxMP >= T{n}Mana); the deepest met tier wins.
    public readonly record struct MythicTier(byte Level, uint Vita, uint Mana);
    public sealed record MythicCaveDef(string Animal, ushort EntranceMap, (ushort X, ushort Y)[] Tiles,
        ushort DestMap, ushort DestX, ushort DestY, MythicTier[] Tiers, string Sources);

    public static IReadOnlyList<MythicCaveDef> MythicCaves { get; private set; } = new List<MythicCaveDef>();

    // Derived (map,x,y) -> cave lookup so the per-step entrance check is a single hash probe on any map.
    public static IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), MythicCaveDef> MythicCaveTiles { get; private set; }
        = new Dictionary<(ushort, ushort, ushort), MythicCaveDef>();

    // ---- PvP arena doors (data/game-data/ArenaDoors.csv) -------------------------------------------------
    // Tower Arena's five side doors are RTK Lua tile-scripts (onScriptedTilesArena.lua ->
    // arenaPVPCheckAndWarp.lua), NOT rows in the SQL warp table — which is why every one of them was dead
    // here: only the RETURN leg (each arena's 15:2/16:2 exit back into Tower Arena) is in Warps.csv, so the
    // ring was one-way. Each door is a level-banded gateway into one PvP arena map:
    //   west  0:5/0:6   -> Kugnae Adventure  6-35     east 21:5/21:6   -> Kugnae Legends  86-98
    //   west  0:11/0:12 -> Kugnae Heroes    36-65     east 21:11/21:12 -> Kugnae Ancients 99, capped vitals
    //   west  0:17/0:18 -> Kugnae Glory     66-85
    // Consumed by Session.TryArenaDoor. Gate = level >= MinLevel (and unmarked, when Unmarked=1), rejected
    // high when level > MaxLevel (0 = no cap) or — RTK uses OR here, unlike the engine's map-req check —
    // baseMaxHP > MaxVita or baseMaxMP > MaxMana (0 = no cap). DestX may be a "lo-hi" range (RTK picks a
    // random landing column so two entrants don't stack). Tiles is ';'-separated "x:y", same as MythicCaves.
    //
    // NOT ported: Tower Arena's NORTH row (y=2), which RTK gates on a live "carnage" minigame event id — we
    // have no minigame scheduler, so in RTK-with-no-event those tiles only ever bounce you back anyway.
    public sealed record ArenaDoorDef(ushort Map, (ushort X, ushort Y)[] Tiles, ushort DestMap,
        ushort DestX, ushort DestX2, ushort DestY, int MinLevel, int MaxLevel,
        uint MaxVita, uint MaxMana, bool Unmarked, string Label, string Sources);

    public static IReadOnlyList<ArenaDoorDef> ArenaDoors { get; private set; } = new List<ArenaDoorDef>();

    // Derived (map,x,y) -> door lookup, so the per-step check is one hash probe (same shape as MythicCaveTiles).
    public static IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), ArenaDoorDef> ArenaDoorTiles { get; private set; }
        = new Dictionary<(ushort, ushort, ushort), ArenaDoorDef>();

    // ---- Board-sign locations (data/game-data/BoardLocations.csv) -----------------------------------------
    // RTK's onSign board-sign system (on_event.lua onSign / selectBulletinBoard): a board SPRITE tile that,
    // when faced from the south (player looking north), opens ONE specific board (Server/Boards.cs) straight
    // to its posts. Keyed by the board tile (map,x,y) + the target BoardId; consumed by Session via TryBoardAt
    // with RTK's ±1 X tolerance. Distinct from the `b` mailbox/board-list — this jumps directly to a board.
    public static IReadOnlyList<(ushort Map, ushort X, ushort Y, int BoardId)> BoardLocations { get; private set; }
        = new List<(ushort, ushort, ushort, int)>();

    // ---- Location / warp geometry (Tier-1 extraction; data/game-data/*.csv) ------------------------------
    // RTK/RE geometry that used to be hard-coded in the game logic, moved to flat files so it hot-reloads via
    // @reload like every other registry. Consumers read these Content.* properties.

    // The stock client's background tracks, by id and by NAME (the midis in NexusTK.snd are numbered, but the
    // songs have real names — see MusicTracks.csv, which is also what lets "@music mist" work). Type is the
    // 0x19 channel: 2 = midi (everything the stock client ships), 1 = mp3/lsr.
    public sealed record MusicTrack(ushort Id, string Name, byte Type);
    public static IReadOnlyList<MusicTrack> MusicTracks { get; private set; } = new List<MusicTrack>();

    // Area -> BGM track (BgmFor). A design assignment, not RTK data: RTK's own Maps table has one track
    // (902) on 9799 of 9850 maps, and the 4.95 client files carry no map->track table at all. Zones match by
    // explicit map id/range first, then by map-NAME glob; a map in no zone keeps whatever is already playing
    // (see Session.PlayMapMusic) so walking into a shop or a cave never restarts the song. See MapBgm.csv.
    public sealed record BgmZone(string Zone, ushort Track, byte Type,
        IReadOnlyList<(ushort Lo, ushort Hi)> Maps, IReadOnlyList<string> Names);
    public static IReadOnlyList<BgmZone> BgmZones { get; private set; } = new List<BgmZone>();

    // Resolved map -> track, built once at load (BuildBgmMap): the zones' own maps at Hops 0, then every
    // other map inherits its NEAREST zone through the warp graph. That spill is what makes a building or a
    // cave play its area's theme without being listed, and — unlike leaving it to "whatever is already
    // playing" — it also works when you LOG IN inside one, where there is no previous song to inherit.
    public sealed record BgmPick(ushort Track, byte Type, string Zone, int Hops);
    private static Dictionary<ushort, BgmPick> _bgmByMap = new();

    /// <summary>The track to start on a zone-less map when nothing is playing yet (a fresh session): the
    /// "Default" row of MapBgm.csv. Null leaves such a session silent until it reaches a zoned map.</summary>
    public static (ushort bgm, byte type)? DefaultBgm { get; private set; }

    // Nation tavern return tiles for Return / yellow_scroll / qui_hyang (Session.ReturnToInn). Grouped by
    // Kugnae/Buya/Nagnang; the nation->group choice (incl. RTK's country>3 -> Kugnae fallback) stays in code.
    public sealed record InnDef(ushort Map, ushort X, ushort Y);
    public static IReadOnlyDictionary<string, IReadOnlyList<InnDef>> Inns { get; private set; } =
        new Dictionary<string, IReadOnlyList<InnDef>>(StringComparer.OrdinalIgnoreCase);

    // Ground-item forage spawn boxes (World forage tick / RTK itemspawner.lua). See ForageAreas.csv.
    public sealed record ForageAreaDef(string ItemKey, ushort Map, int MinX, int MaxX, int MinY, int MaxY,
        int Max, int MinQty, int MaxQty);
    public static IReadOnlyList<ForageAreaDef> ForageAreas { get; private set; } = new List<ForageAreaDef>();

    // Class path-hall doorways (Session.TryPathHallWarp), keyed by the hall map. Sanctum[0..3] indexed by
    // Character.Alignment (Unaligned/Kwisin/Mingken/Ohaeng). See PathHalls.csv.
    public sealed record PathHallDef(int BaseClass, ushort GuildMap, ushort[] Sanctum);
    public static IReadOnlyDictionary<ushort, PathHallDef> PathHalls { get; private set; } =
        new Dictionary<ushort, PathHallDef>();

    // Gateway spell gate-boxes per kingdom region 0-3 (Session.CastGateway). Gates keyed by 'n'/'e'/'s'/'w'.
    // See GatewayGates.csv.
    public sealed record GatewayDef(ushort Map, string City,
        IReadOnlyDictionary<char, (int Xlo, int Xhi, int Ylo, int Yhi)> Gates);
    public static IReadOnlyDictionary<int, GatewayDef> GatewayRegions { get; private set; } =
        new Dictionary<int, GatewayDef>();

    // Inter-continent world-map travel destinations (Session world-map), order-significant (the wire dots are
    // sent in this order). DotX/DotY are field10 pixel coords. See WorldMapDests.csv.
    public sealed record WorldDestDef(string Name, ushort Map, ushort X, ushort Y, int DotX, int DotY);
    public static IReadOnlyList<WorldDestDef> WorldDests { get; private set; } = new List<WorldDestDef>();

    // World-map trigger tiles, keyed by the source (town) map. Hits when the FixedAxis coord is in
    // [FixedLo,FixedHi] AND the other axis is in [RangeLo,RangeHi]. See WorldMapTriggers.csv.
    public sealed record WorldTriggerDef(char FixedAxis, int FixedLo, int FixedHi, int RangeLo, int RangeHi)
    {
        public bool Hits(int x, int y)
        {
            int fixedC = FixedAxis == 'x' ? x : y;
            int rangeC = FixedAxis == 'x' ? y : x;
            return fixedC >= FixedLo && fixedC <= FixedHi && rangeC >= RangeLo && rangeC <= RangeHi;
        }
    }
    public static IReadOnlyDictionary<ushort, WorldTriggerDef> WorldMapTriggers { get; private set; } =
        new Dictionary<ushort, WorldTriggerDef>();

    // Mythic cave fall-room landings (Session.TryMythicFallRoom), keyed by the source sub-map, ALREADY
    // tier-expanded (+0/+3000/+4000) at load. See FallRooms.csv.
    public static IReadOnlyDictionary<ushort, (ushort Map, ushort X, ushort Y)> FallRooms { get; private set; } =
        new Dictionary<ushort, (ushort, ushort, ushort)>();

    // Curated shop catalogues (data/game-data/ShopCatalogues.csv) — hand-authored, ORDERED sub-category buy
    // menus (e.g. SmithNpc's armor menus) that the auto-extracted flat ShopStock can't represent. Keyed by
    // NpcDef.Key; consulted first by Shops.For, else it falls back to ShopStock. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, IReadOnlyList<(string Name, string[] Keys)>> ShopCatalogues { get; private set; } =
        new Dictionary<string, IReadOnlyList<(string, string[])>>(StringComparer.OrdinalIgnoreCase);

    // Data-driven spell params (data/game-data/SpellParams.csv): per spell key, the raw CSV row its Lua verb
    // reads (the `verb` column + numeric params like coeff/mana/amount). The "row" half of the verb/row spell
    // model — the "verb" logic lives in spell_verbs.lua (see Server/SpellScript.cs + Session.ApplyCast). Sparse:
    // only migrated spells have a row; everything else uses the C# CastX dispatch. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SpellParams { get; private set; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    // Data-driven item use-effect params (data/game-data/ItemParams.csv): per item key, the raw CSV row its
    // Lua verb reads (the `verb` column + params like amount/hpcost/statuskey/duration). The "row" half of the
    // verb/row item-effect model — the "verb" logic lives in item_verbs.lua (see Server/ItemScript.cs +
    // Session.ApplyItemEffect). Items without a row fall back to the item DB's Vita/Mana. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ItemParams { get; private set; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    // NPC composition (data/game-data/NpcAbilities.csv): NpcKey -> the ability NAMES it's built from (a
    // pipe-list). NpcScripts.For resolves each name to its C# INpcAbility instance (NpcScripts.AbilityByName).
    // The "which abilities" is data; the ability code stays code. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, string[]> NpcCompositions { get; private set; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    // Per-class level-up HP/MP gain ranges (data/game-data/PathGrowth.csv), keyed by path id (0 Peasant / 1
    // Warrior / 2 Rogue / 3 Mage / 4 Poet). Each is the pair of args to Random.Shared.Next(min, max) — max is
    // EXCLUSIVE, matching the original C# switch. The which-stat-is-primary logic stays in Session.LevelUp.
    public static IReadOnlyDictionary<int, (int HpMin, int HpMax, int MpMin, int MpMax)> PathGrowth { get; private set; } =
        new Dictionary<int, (int, int, int, int)>();
    /// <summary>Level-up gain ranges for a path, falling back to Peasant (0) then a hardcoded default.</summary>
    public static (int HpMin, int HpMax, int MpMin, int MpMax) PathGrowthFor(int path) =>
        PathGrowth.TryGetValue(path, out var g) ? g : PathGrowth.TryGetValue(0, out var p) ? p : (45, 56, 32, 37);

    // Named engine scalars a deployment may retune without a rebuild (data/game-data/ServerTuning.csv, key,value).
    // These sit on the tier-1/tier-3 line — real mechanics, but harmless to expose as hand-editable config. Typed
    // accessors fall back to the historical hardcoded default if the key is absent, so a missing file is safe.
    public static IReadOnlyDictionary<string, double> Tuning { get; private set; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    private static double Tune(string key, double dflt) => Tuning.TryGetValue(key, out var v) ? v : dflt;
    public static int MailMinLevel => (int)Tune("MailMinLevel", 10);   // min level to view/send nmail
    public static int SpeechRange  => (int)Tune("SpeechRange", 8);     // tiles (Chebyshev) an NPC "hears" from
    public static uint BankMax     => (uint)Tune("BankMax", 100_000_000);   // per-account coin cap
    // Highest minor-quest tier a path leader will hand out: 1 = Minor only (4.95 — the only tier that
    // existed), 2 adds Major, 3 adds Epic. The Major/Epic rows stay in MinorQuests.csv either way; this only
    // gates whether the "which type of quest?" menu is offered at all. See Server/MinorQuest.cs.
    public static int MinorQuestTiers => (int)Tune("MinorQuestTiers", 1);
    // (SilentDelReason is GONE, 2026-08-07. It existed to probe whether an out-of-range 0x10 reason was the
    // client's silent path; the live answer was no — 15 renders "<item> removed.", the same line reason 0
    // gives, so the handler clamps/defaults and NO reason byte is silent. Every path that used it has since
    // moved to a real reason (bank deposit and shop sale both hand the item over: 10, "You gave X."), and a
    // path that must truly say nothing sends no 0x10 at all — see EquipDelReason.)
    // Equipping is the one removal that ought to be TRULY silent: the item didn't leave you, it moved onto
    // your body, and the real game says nothing. Suppressing the 0x10 entirely was tried (default -1) and is
    // WRONG — it leaves a ghost row in the bag that can't be dropped, equipped or used, because the server
    // has already dropped the item while the client still draws it.
    //
    // The reason it can't work: the equip window and the bag are SEPARATE client structures. The bag is a
    // 164-byte-stride array and the ONLY thing that clears an entry is 0x48f0b0, reached only from the 0x10
    // handler (0x48fe10) — which range-checks the slot and ignores the reason byte completely. The 0x37
    // equip-window entry never touches that array, so it cannot stand alone.
    //
    // Reason 12 is the one code that says NOTHING, so equipping gets both: the bag entry is cleared and the
    // player isn't told they "used" their armour. Full table swept live 2026-08-07 (@delreason):
    //   0 "<item> removed."   1 "You dropped"   2 "You ate"     3 "You smoked" (herb/sonhi pipes)
    //   4 "You threw"         5 "You shot"      6 "You used"    7 "You posted"
    //   8 "<item> decayed."   9 "You gave"     10 "You sold"   11 "<item> removed."
    //  12 SILENT             13 "<item> broken."               14+ all "<item> removed."
    public static int EquipDelReason => (int)Tune("EquipDelReason", 12);
    /// <summary>Open the board request straight into the MAILBOX when the player has unread n-mail, instead
    /// of the board list. 'm' is armed only while the mail arrow is up and sends the same `3b 01 00` as 'b',
    /// so this is the only way to make 'm' behave like a mailbox key — at the cost of 'b' doing the same
    /// while mail is unread. 0 = always show the board list (Mailbox is still its last entry).</summary>
    public static bool MailFirstOnBoard => Tune("MailFirstOnBoard", 1) != 0;

    /// <summary>Patch a peer's appearance with <c>0x1d</c> (look-update-in-place) instead of the
    /// despawn(<c>0x0E</c>) + respawn(<c>0x33</c>) pair. The old pair exists because a bare <c>0x33</c>
    /// re-send orphans the entity and leaks its nameplate marker; <c>0x1d</c> sidesteps that entirely by
    /// never destroying or creating anything. Morph and stealth still take the full path regardless —
    /// see Session.RefreshAppearance. 0 = always use the old pair.</summary>
    public static bool LookUpdateInPlace => Tune("LookUpdateInPlace", 1) != 0;

    /// <summary>Draw nameplates over other players. The plate is rendered from the NAME string in the
    /// <c>0x33</c> spawn, so sending an empty name is a pure server-side way to suppress it — no client
    /// patch needed (cf. re/patch_no_nametag.py, which does it on disk). Applies to PEERS only; your own
    /// name is never in a peer packet. 0 = no plates.</summary>
    public static bool ShowNameplates => Tune("ShowNameplates", 1) != 0;

    /// <summary>Which nations the user-list window (0x36) gets columns and a name for — the ids sent in the
    /// 0x59 sub-1 town table. Default is the three this server actually plays: 0 Neutral, 1 Koguryo,
    /// 2 Buya. Deliberately NOT the same thing as <c>Character.Nations</c>, which is the HUD crest id space
    /// (0x08 stats, calibrated via @nat) and must keep all 8 entries.
    /// <para>A nation absent from this table cannot be resolved by the client: it scans the table for the
    /// viewer's own nation id and falls back to entry 0 when it misses, at which point every row whose
    /// nation nibble isn't 0 drops out of the columns. So a player whose nation is off this list sees an
    /// empty window, not a partial one.</para></summary>
    /// <para>ServerTuning holds scalars only, so this is a BITMASK over the nation ids: bit i = nation i.
    /// Default 7 = 0b111 = Neutral + Koguryo + Buya. 255 restores all eight.</para></summary>
    // User-list name colours — row byte +2, a palette index measured live (`@users hunters`). 0..15 is the
    // standard 16-colour palette and **0 paints black on black**, which is what made every name invisible
    // until 2026-08-08. Same three cases RTK colours (default / same clan / GM), in the palette this client
    // actually has. Values above 15 reach further into the 256-entry palette if a deployment wants them.
    // Highest rule wins: self, then GM, then clan, then default. 0 turns an OPTIONAL rule off — safe to
    // overload that way because 0 is the invisible colour and can never be a deliberate choice. Only
    // UserListColorDefault has no off switch.
    //   0 black(invisible) 1 dk blue  2 dk green 3 teal      4 dk red  5 magenta 6 brown   7 lt gray
    //   8 dk gray          9 lt blue 10 lt green 11 lt cyan 12 red    13 pink   14 yellow 15 white
    public static int UserListColorDefault => (int)Tune("UserListColorDefault", 15);   // white
    public static int UserListColorClan    => (int)Tune("UserListColorClan",    10);   // light green — RTK's same-clan highlight
    public static int UserListColorGm      => (int)Tune("UserListColorGm",      12);   // red
    public static int UserListColorSelf    => (int)Tune("UserListColorSelf",    14);   // yellow — no RTK equivalent, ours

    public static IReadOnlyList<byte> UserListNations
    {
        get
        {
            int mask = (int)Tune("UserListNationMask", 7);
            var ids = new List<byte>();
            for (byte i = 0; i < 8; i++) if ((mask & (1 << i)) != 0) ids.Add(i);
            return ids.Count > 0 ? ids : new List<byte> { 0 };   // the client bails on an empty table
        }
    }
    // SplitTrapSpells (0/1, default 0) also lives here — accessor is next to the trap block it gates,
    // see SplitTrapSpellsEnabled / IsOutOfEraSplitTrap.

    // Door-object graphic toggle table (data/game-data/DoorObjects.csv, transcribed from RTK open.lua `openDoors`).
    // Two lookups: DoorSwaps maps a faced object id -> (startDx, new object ids) for the explicit doors (single-tile
    // swings and 3-tile-wide runs where the faced piece tells us which corner we're on); DoorDeltas is the set of
    // ranges whose open<->closed pair differs by a fixed signed delta (single tile). See Content.DoorToggleFor.
    public static IReadOnlyDictionary<int, (int StartDx, ushort[] Objs)> DoorSwaps { get; private set; } =
        new Dictionary<int, (int, ushort[])>();
    public static IReadOnlyList<(int Lo, int Hi, int Delta)> DoorDeltas { get; private set; } =
        new List<(int, int, int)>();

    // Closed-door object id -> the open id that replaces it, applied cell-by-cell as a .map file is read
    // (MapData.Load). This is how a door "starts open" without editing the client's own map files: the
    // 4.95 client draws its LOCAL copy, so opening one also needs the 0x06 cell-patch every session gets on
    // map entry (Session.SyncMapDoors). Populated from DoorObjects.csv rows flagged defaultOpen=1.
    public static IReadOnlyDictionary<int, ushort> DoorDefaultOpen { get; private set; } =
        new Dictionary<int, ushort>();

    // ---- authored cell overrides (data/game-data/MapCells.csv) ------------------------------------------
    // "The shipped map is wrong here." One row per cell: Map,X,Y,Tile,Pass,Obj — any of the three value
    // columns left BLANK is inherited from the .map file, so you can fix passability without touching the
    // graphic (or vice versa). Applied by MapData.Load as the LAST authored layer, so a hand-written row
    // beats DoorDefaultOpen / DefaultClosed / ForceOpen. The .map files themselves are never modified.
    public sealed record CellOverride(ushort Map, ushort X, ushort Y, ushort? Tile, ushort? Pass, ushort? Obj);
    private static IReadOnlyDictionary<ushort, List<CellOverride>> _mapCells =
        new Dictionary<ushort, List<CellOverride>>();
    /// <summary>Total authored cell overrides loaded (for the startup summary).</summary>
    public static int MapCellCount { get; private set; }
    /// <summary>Authored cell overrides for one map (empty if none).</summary>
    public static IReadOnlyList<CellOverride> MapCellsFor(ushort map) =>
        _mapCells.TryGetValue(map, out var l) ? l : (IReadOnlyList<CellOverride>)Array.Empty<CellOverride>();
    /// <summary>Given the object a player faces, return the swapped door run (startDx + new ids), or null if it
    /// isn't a door. Mirrors the old Session.Movement.DoorToggle switch, now data-driven.</summary>
    public static (int StartDx, ushort[] Objs)? DoorToggleFor(int obj)
    {
        if (DoorSwaps.TryGetValue(obj, out var s)) return s;
        foreach (var (lo, hi, delta) in DoorDeltas)
            if (obj >= lo && obj <= hi) return (0, new[] { (ushort)(obj + delta) });
        return null;
    }

    public static void Load()
    {
        Maps = LoadMaps(ResolvePath("NEXUS_MAP_INDEX", "data", "game-data", "map_index.csv"));
        MobFleeOverrides = LoadMobFlees(ResolvePath("NEXUS_MOB_FLEES", "data", "game-data", "MobFlees.csv"));   // BEFORE Mobs: LoadMobs folds it in
        Mobs = LoadMobs(ResolvePath("NEXUS_MOBS", "data", "game-data", "mobs.csv"));
        Items = LoadItems(ResolvePath("NEXUS_ITEMS", "data", "game-data", "Items.csv"));
        Warps = LoadWarps(ResolvePath("NEXUS_WARPS", "data", "game-data", "Warps.csv"));   // needs Maps
        Spawns = LoadSpawns(ResolvePath("NEXUS_SPAWNS", "data", "game-data", "Spawns.csv"));
        // Base area spawns + trap-ambush populations (tiger cave, rabbit boss-tier, trapdoor spiders) that RTK
        // spawns via trap/mob_spawn.lua rather than handleSpawn (rare-boss rows carry RespawnSec; generated by
        // re/extract_trap_spawns.py). Concatenated into a LOCAL and assigned to AreaSpawns ONCE — so a
        // concurrent reader on @reload never sees the base list without its 362 trap mobs (the old two-step
        // assign had that tear window).
        AreaSpawns = LoadAreaSpawns(ResolvePath("NEXUS_AREASPAWNS", "data", "game-data", "AreaSpawns.csv"))
            .Concat(LoadAreaSpawns(ResolvePath("NEXUS_AREASPAWNS_TRAP", "data", "game-data", "AreaSpawnsTrap.csv")))
            .ToList();
        var npcs = LoadNpcs(ResolvePath("NEXUS_NPCS", "data", "game-data", "NPCs.csv"));   // needs Maps
        _npcById = npcs.ToDictionary(n => n.Id);   // assign the index BEFORE the public list, so a reader that
        Npcs = npcs;                               // sees the new Npcs always sees the matching new _npcById
        MinorQuests = LoadMinorQuests(ResolvePath("NEXUS_MINORQUESTS", "data", "game-data", "MinorQuests.csv"));
        ShopStock = LoadShopStock(ResolvePath("NEXUS_SHOPSTOCK", "data", "game-data", "ShopStock.csv"));
        Paths = LoadPaths(ResolvePath("NEXUS_PATHS", "data", "game-data", "Paths.csv"));
        LevelExp = LoadLevelExp(ResolvePath("NEXUS_LEVELEXP", "data", "game-data", "LevelExp.csv"));
        SpellLevelOverrides = LoadSpellLevels(ResolvePath("NEXUS_SPELL_LEVELS", "data", "game-data", "SpellLevels.csv"));   // BEFORE Spells: LoadSpells reads it
        Spells = LoadSpells(ResolvePath("NEXUS_SPELLS", "data", "game-data", "Spells.csv"));
        // O(1) lookup indexes (0.1) — rebuilt every Load()/@reload so they swap with the lists above. Nothing
        // in Load reads them (RollDrops is the only in-Content consumer, and it runs at mob-death, not load).
        _itemById  = IndexFirst(Items, i => i.Id);
        _itemByKey = IndexFirst(Items, i => i.Key, StringComparer.OrdinalIgnoreCase);
        _mobById   = IndexFirst(Mobs, m => m.Id);
        _mobByKey  = IndexFirst(Mobs, m => m.Key, StringComparer.OrdinalIgnoreCase);
        _spellById = IndexFirst(Spells, s => s.Id);
        _spellByKey = IndexFirst(Spells, s => s.Key, StringComparer.OrdinalIgnoreCase);
        var pathIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // name -> id, first wins
        foreach (var kv in Paths) if (!string.IsNullOrEmpty(kv.Value)) pathIdByName.TryAdd(kv.Value, kv.Key);
        _pathIdByName = pathIdByName;
        SpellFx = LoadSpellFx(ResolvePath("NEXUS_SPELL_FX", "data", "game-data", "spell_effects.csv"));
        SpellTexts = LoadSpellTexts(ResolvePath("NEXUS_SPELL_TEXT", "data", "game-data", "SpellText.csv"));
        SpellCosts = LoadSpellCosts(ResolvePath("NEXUS_SPELL_COSTS", "data", "game-data", "SpellLearnCosts.csv"));
        LookPalettes = LoadLookPalettes(ResolvePath("NEXUS_MOB_PALETTES", "data", "game-data", "MobLookPalettes.csv"));
        MapMeta = LoadMapMeta(ResolvePath("NEXUS_MAPS_FULL", "data", "game-data", "Maps.csv"));   // region + warpOut for Gateway
        MobDrops = LoadMobDrops(ResolvePath("NEXUS_MOB_DROPS", "data", "game-data", "MobDrops.csv"));
        CraftingToggleOverrides = LoadCraftingToggles(ResolvePath("NEXUS_CRAFTING_TOGGLES", "data", "game-data", "CraftingToggles.csv"));
        var mythicCaves = LoadMythicCaves(ResolvePath("NEXUS_MYTHIC_CAVES", "data", "game-data", "MythicCaves.csv"));
        MythicCaveTiles = mythicCaves   // assign the derived tile index BEFORE the public list (same reason as Npcs/_npcById)
            .SelectMany(c => c.Tiles.Select(t => (key: (c.EntranceMap, t.X, t.Y), cave: c)))
            .ToDictionary(e => e.key, e => e.cave);
        MythicCaves = mythicCaves;
        var arenaDoors = LoadArenaDoors(ResolvePath("NEXUS_ARENA_DOORS", "data", "game-data", "ArenaDoors.csv"));
        ArenaDoorTiles = arenaDoors   // derived tile index first, public list second (same reason as Npcs/_npcById)
            .SelectMany(d => d.Tiles.Select(t => (key: (d.Map, t.X, t.Y), door: d)))
            .ToDictionary(e => e.key, e => e.door);
        ArenaDoors = arenaDoors;
        MusicTracks = LoadMusicTracks(ResolvePath("NEXUS_MUSIC_TRACKS", "data", "game-data", "MusicTracks.csv"));
        (BgmZones, DefaultBgm) = LoadBgmZones(ResolvePath("NEXUS_MAP_BGM", "data", "game-data", "MapBgm.csv"));
        _bgmByMap = BuildBgmMap();   // needs Maps + Warps + BgmZones — resolves every map to a track
        Inns = LoadInns(ResolvePath("NEXUS_INNS", "data", "game-data", "Inns.csv"));
        ForageAreas = LoadForageAreas(ResolvePath("NEXUS_FORAGE", "data", "game-data", "ForageAreas.csv"));
        PathHalls = LoadPathHalls(ResolvePath("NEXUS_PATHHALLS", "data", "game-data", "PathHalls.csv"));
        GatewayRegions = LoadGatewayGates(ResolvePath("NEXUS_GATEWAY", "data", "game-data", "GatewayGates.csv"));
        WorldDests = LoadWorldDests(ResolvePath("NEXUS_WORLDMAP_DESTS", "data", "game-data", "WorldMapDests.csv"));
        WorldMapTriggers = LoadWorldTriggers(ResolvePath("NEXUS_WORLDMAP_TRIGGERS", "data", "game-data", "WorldMapTriggers.csv"));
        FallRooms = LoadFallRooms(ResolvePath("NEXUS_FALLROOMS", "data", "game-data", "FallRooms.csv"));
        BoardLocations = LoadBoardLocations(ResolvePath("NEXUS_BOARD_LOCATIONS", "data", "game-data", "BoardLocations.csv"));
        ShopCatalogues = LoadShopCatalogues(ResolvePath("NEXUS_SHOP_CATALOGUES", "data", "game-data", "ShopCatalogues.csv"));
        SpellParams = LoadKeyedRows(ResolvePath("NEXUS_SPELL_PARAMS", "data", "game-data", "SpellParams.csv"));
        // The three Lua files load ATOMICALLY (see LuaVerbHost.Load): a broken edit is REJECTED and the
        // previously-loaded script keeps running. RejectedScripts records which ones didn't take so @reload can
        // say so to the GM's face — a silent "reload ok" after a typo is how you end up debugging the wrong thing.
        var rejected = new List<string>();
        if (!SpellScript.Load(ResolvePath("NEXUS_SPELL_VERBS", "data", "game-data", "spell_verbs.lua"))) rejected.Add("spell_verbs.lua");
        ItemParams = LoadKeyedRows(ResolvePath("NEXUS_ITEM_PARAMS", "data", "game-data", "ItemParams.csv"));   // same "whole row keyed by `key`" shape as SpellParams
        if (!ItemScript.Load(ResolvePath("NEXUS_ITEM_VERBS", "data", "game-data", "item_verbs.lua"))) rejected.Add("item_verbs.lua");
        if (!NpcScript.Load(ResolvePath("NEXUS_NPC_DIALOG", "data", "game-data", "npc_dialog.lua"))) rejected.Add("npc_dialog.lua");
        RejectedScripts = rejected;
        // Phase-1 spell-DATA tables (extracted from Content.cs literals; see re/extract_spell_tables.py).
        PetSpells = LoadPets(ResolvePath("NEXUS_PETS", "data", "game-data", "Pets.csv"));
        WeaponProcs = LoadWeaponProcs(ResolvePath("NEXUS_WEAPON_PROCS", "data", "game-data", "WeaponProcs.csv"));
        TrapSpells = LoadTrapSpells(ResolvePath("NEXUS_TRAPS", "data", "game-data", "Traps.csv"));
        (MorphSpells, MorphDispatchSpells) = LoadMorphs(ResolvePath("NEXUS_MORPHS", "data", "game-data", "Morphs.csv"));
        (RageAmount, EnchantSpells) = LoadSpellMods(ResolvePath("NEXUS_SPELL_MODS", "data", "game-data", "SpellMods.csv"));
        NpcCompositions = LoadNpcCompositions(ResolvePath("NEXUS_NPC_ABILITIES", "data", "game-data", "NpcAbilities.csv"));
        PathGrowth = LoadPathGrowth(ResolvePath("NEXUS_PATH_GROWTH", "data", "game-data", "PathGrowth.csv"));
        (DoorSwaps, DoorDeltas, DoorDefaultOpen) = LoadDoorObjects(ResolvePath("NEXUS_DOOR_OBJECTS", "data", "game-data", "DoorObjects.csv"));
        Tuning = LoadTuning(ResolvePath("NEXUS_SERVER_TUNING", "data", "game-data", "ServerTuning.csv"));
        Doors.SetConfig(LoadDoors(ResolvePath("NEXUS_DOORS", "data", "game-data", "Doors.csv")));
        (_mapCells, var mapCellCount) = LoadMapCells(ResolvePath("NEXUS_MAP_CELLS", "data", "game-data", "MapCells.csv"));
        MapCellCount = mapCellCount;
        Log.Info($"content: {Maps.Count} maps ({MapMeta.Count} w/ region), {Mobs.Count} mobs, {Items.Count} items, " +
                 $"{Warps.Count} warps, {Spawns.Count} spawns, {AreaSpawns.Count} area-spawns, {Npcs.Count} npcs, {Spells.Count} spells ({SpellFx.Count} fx, {SpellCosts.Count} w/ real learn cost), {LookPalettes.Count} mob-palettes, {MinorQuests.Count} minor-quests, {ShopStock.Count} shop-stocks, {LevelExp.Count} level-exp-paths, {MobDrops.Count} mob-drop-tables, {CraftingToggleOverrides.Count} crafting-toggle overrides, {MythicCaves.Count} mythic-caves ({MythicCaveTiles.Count} entrance tiles), {ArenaDoors.Count} arena-doors, {WorldDests.Count} world-map dests, {PathHalls.Count} path-halls, {GatewayRegions.Count} gateway-regions, {ForageAreas.Count} forage-areas, {FallRooms.Count} fall-rooms, {BoardLocations.Count} board-signs, {PetSpells.Count} pets, {WeaponProcs.Count} weapon-procs loaded" +
                 (Maps.Count == 0 || Mobs.Count == 0
                     ? "  (some empty — run re/build_map_index.py and check data/game-data/mobs.csv)"
                     : ""));
    }

    /// <summary>
    /// Hot-reload every file-backed registry WITHOUT a restart (the <c>@reload</c> GM command), so content
    /// fixes ship without kicking players. Re-runs the exact ordered <see cref="Load"/> sequence — which
    /// re-reads every CSV and rebuilds the derived <c>_npcById</c> — reassigning the public registries. Each
    /// registry is a lock-free reference, and a reference assignment is atomic, so a reader always sees a whole
    /// old-or-new dictionary, never a torn one (a reader that straddles the swap across two registries is
    /// harmless — they're independent). Returns a one-line count summary.
    ///
    /// SCOPE: file-backed content only (every registry above is CSV/Lua-backed now — map BGM moved to
    /// MapBgm.csv, so there's no compile-time content table left that a restart would be needed for). The
    /// world population is rebuilt separately by the @reload caller (World.RebuildPopulation), which re-reads
    /// spawns/NPCs so added/removed/repositioned rows take effect.
    /// </summary>
    public static string Reload()
    {
        Load();
        var summary = $"{Maps.Count} maps, {Mobs.Count} mobs, {Items.Count} items, {Warps.Count} warps, " +
                      $"{Spawns.Count + AreaSpawns.Count} spawns, {Npcs.Count} npcs, {Spells.Count} spells, {ShopStock.Count} shops, " +
                      $"{CraftingToggleOverrides.Count} crafting-toggle overrides";
        // A rejected .lua is the single most important thing @reload can tell you: your edit did NOT take, the
        // old script is still running, and the reason is in the server log. Lead with it.
        return RejectedScripts.Count == 0 ? summary
             : $"*** REJECTED (still running the previous version, see log): {string.Join(", ", RejectedScripts)} *** — {summary}";
    }

    /// <summary>Lua files whose most recent (re)load was rejected for a compile/shape error — their previously
    /// loaded version is still live. Empty when everything took. See <see cref="Reload"/>.</summary>
    public static IReadOnlyList<string> RejectedScripts { get; private set; } = Array.Empty<string>();

    /// <summary>The portal at (map, x, y), if the player just stepped on a door tile.</summary>
    public static bool TryWarp(ushort map, ushort x, ushort y, out (ushort m, ushort x, ushort y) dest)
        => Warps.TryGetValue((map, x, y), out dest);

    /// <summary>The board a board-sprite tile (map, x, y) belongs to, if any — RTK's onSign board-sign lookup
    /// (selectBulletinBoard). Applies RTK's ±1 X tolerance (a board sprite spans a few columns) and an exact Y,
    /// so a player facing north into any column of the board resolves the same board id.</summary>
    public static bool TryBoardAt(ushort map, int x, int y, out int boardId)
    {
        foreach (var b in BoardLocations)
            if (b.Map == map && b.Y == y && Math.Abs(b.X - x) <= 1) { boardId = b.BoardId; return true; }
        boardId = 0;
        return false;
    }

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
            Line($"  @warp {q,-16} -> " + (m is null ? "(no match)" : $"map {m.Id} '{m.Name}' {m.Xs}x{m.Ys}"));
        }

        Line("--- FindMob (name / key / id / fuzzy) ---");
        foreach (var q in new[] { "rabbit", "1", "great_horns", "great horns", "grhrn", "fox" })
        {
            var mob = FindMob(q);
            Line($"  @summon {q,-14} -> " + (mob is null ? "(no match)" : $"'{mob.Name}' look {mob.Look} c{mob.Color} {mob.Hp}hp {mob.Exp}xp"));
        }

        Line("--- FindItem (name / key / id) ---");
        foreach (var q in new[] { "apple", "stick", "leather", "sword", "0" })
        {
            var it = FindItem(q);
            Line($"  @item {q,-12} -> " + (it is null ? "(no match)"
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

        // --- Background music: track names + area zoning (MusicTracks.csv / MapBgm.csv) ---
        Line($"--- Music: {MusicTracks.Count(t => t.Name.Length > 0)} named tracks, {BgmZones.Count} zones, " +
             $"{_bgmByMap.Count} maps resolved, " +
             $"default {(DefaultBgm is null ? "(none)" : $"{DefaultBgm.Value.bgm} '{TrackName(DefaultBgm.Value.bgm)}'")} ---");
        foreach (var q in new[] { "mist", "tiger", "mon", "6", "10", "nope" })
            Line($"    @music {q,-6} -> " + (FindTrack(q) is { } t ? $"track {t.Id} '{t.Name}' type{t.Type}" : "(no match)"));
        // (map, expected track) — the six areas the assignment was specified for, plus a building inside each
        // hub (which must resolve to the SAME track so walking through a door never restarts the song).
        var bgmWant = new (ushort Map, string Track)[]
        {
            (137, "mist"), (3812, "mist"),        // Arctic Land / Arctic Tavern
            (330, "tiger"), (365, "tiger"),       // Buya / Buya Salon
            (114, "dark"), (457, "dark"),         // Hamgyong Nam-Do / Ruined House (Haunted Houses)
            (3800, "sorrow"), (3806, "sorrow"),   // KaMing's Encampment / KaMing
            (0, "dragon"), (1011, "dragon"),      // Kugnae / Kugnae Gathering
            (41, "lake"),                         // Mythic Nexus
            // Unlisted maps that must inherit their area through the warp graph, NOT the default track:
            (332, "tiger"),                       // Spring Tavern — a shop off Buya
            (367, "tiger"),                       // Eldritch Sanctum — 2 hops in from Buya (the login case)
            (2, "dragon"),                        // Walsuk Tavern — a shop off Kugnae
            (1013, "mist"),                       // Haeng Tavern — inside Arctic Village
        };
        bool bgmOk = true;
        foreach (var (map, want) in bgmWant)
        {
            var got = BgmFor(map);
            string name = got is null ? "(none)" : TrackName(got.Value.bgm);
            bool hit = name.Equals(want, StringComparison.OrdinalIgnoreCase);
            bgmOk &= hit;
            Line($"    {(hit ? "ok " : "XX ")}map {map,-6} {(Maps.TryGetValue(map, out var bm) ? bm.Name : "?"),-22} -> " +
                 $"{name,-8} zone '{BgmZoneOf(map)}' (want {want})");
        }
        int resolved = Maps.Values.Count(m => BgmFor(m.Id) is not null);
        bool sticky = resolved > 0 && resolved < Maps.Count;   // some maps have no warp path to any zone
        Line($"    {resolved}/{Maps.Count} maps resolved to a track; the rest keep whatever is playing " +
             $"(and start on the default at login)");

        // --- PvP arena doors: every configured door must lead somewhere renderable, and each destination
        // must have its return leg in Warps.csv (a one-way door strands the player in the arena).
        Line($"--- Arena doors: {ArenaDoors.Count} doors / {ArenaDoorTiles.Count} tiles ---");
        bool doorsOk = ArenaDoors.Count > 0;
        foreach (var d in ArenaDoors)
        {
            bool dest = Maps.ContainsKey(d.DestMap);
            bool back = Warps.Any(w => w.Key.m == d.DestMap && w.Value.m == d.Map);
            doorsOk &= dest && back;
            string band = d.MaxLevel > 0 ? $"{d.MinLevel}-{d.MaxLevel}"
                        : d.MaxVita > 0 ? $"{d.MinLevel}+, <= {d.MaxVita}v/{d.MaxMana}m"
                        : $"{d.MinLevel}+";
            Line($"    {(dest && back ? "ok " : "XX ")}map {d.Map} {string.Join("/", d.Tiles.Select(t => $"{t.X}:{t.Y}")),-13} -> " +
                 $"{d.DestMap} '{(Maps.TryGetValue(d.DestMap, out var am) ? am.Name : "?")}' " +
                 $"level {band}{(dest ? "" : "  [NO MAP DATA]")}{(back ? "" : "  [NO RETURN WARP]")}");
        }

        bool ok = Maps.Count > 0 && Mobs.Count > 0 && Items.Count > 0
                  && FindMap("kugnae") is not null && FindMob("rabbit") is not null && spellsOk
                  && bgmOk && sticky && doorsOk;
        Line(ok ? "SELFTEST: PASS" : "SELFTEST: FAIL (empty registry or missing expected entry)");
    }

    // ---- background music (0x19) --------------------------------------------------------------
    // The stock 4.95 client keeps its audio in NexusTK.snd, which ships exactly 12 background tracks
    // (1.mid .. 12.mid); the 0x19 music packet plays one by id with type 2 = MIDI. There is no original
    // map->track table in the client files, so we assign them ourselves — by AREA, not by map (MapBgm.csv).

    /// <summary>The background track for a map: (bgm id, type 2 = MIDI), or null only for a map that no zone
    /// claims AND that has no warp path to one — in which case the caller keeps whatever is already playing
    /// (see Session.PlayMapMusic).</summary>
    public static (ushort bgm, byte type)? BgmFor(ushort mapId) =>
        _bgmByMap.TryGetValue(mapId, out var p) ? (p.Track, p.Type) : null;

    /// <summary>The zone a map's music comes from, for "@music" feedback ("" if none). Maps that inherited
    /// it through the warp graph rather than being listed are shown with their hop distance.</summary>
    public static string BgmZoneOf(ushort mapId) =>
        _bgmByMap.TryGetValue(mapId, out var p) ? (p.Hops == 0 ? p.Zone : $"{p.Zone} +{p.Hops}") : "";

    // Resolve every map to a track, once per Load(). Three passes, each only filling maps still unclaimed:
    //   1. explicit ids/ranges  -> so a single map can be carved out of an area another zone claims by name
    //   2. map-name globs       -> "Buya *" and friends
    //   3. warp-graph spill     -> multi-source BFS from everything claimed above, so each remaining map
    //                             takes its NEAREST claimed map's track (Buya's shops/caves become Tiger
    //                             without being listed; a login inside one starts on the right song)
    private static Dictionary<ushort, BgmPick> BuildBgmMap()
    {
        var byMap = new Dictionary<ushort, BgmPick>();

        foreach (var z in BgmZones)
            foreach (var (lo, hi) in z.Maps)
                for (int id = lo; id <= hi; id++)
                    if ((Maps.ContainsKey((ushort)id) || lo == hi) && !byMap.ContainsKey((ushort)id))
                        byMap[(ushort)id] = new BgmPick(z.Track, z.Type, z.Zone, 0);

        foreach (var z in BgmZones)
            foreach (var pat in z.Names)
                foreach (var m in Maps.Values)
                    if (!byMap.ContainsKey(m.Id) && GlobMatch(m.Name, pat))
                        byMap[m.Id] = new BgmPick(z.Track, z.Type, z.Zone, 0);

        // Map-level adjacency from the tile warp table, treated as undirected: a one-way drop still tells us
        // the two maps are the same neighbourhood, and most warps are paired anyway.
        var adj = new Dictionary<ushort, List<ushort>>();
        void Link(ushort a, ushort b)
        {
            if (a == b) return;
            if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<ushort>();
            if (!l.Contains(b)) l.Add(b);
        }
        foreach (var (from, to) in Warps)
        {
            if (!Maps.ContainsKey(from.m) || !Maps.ContainsKey(to.m)) continue;
            Link(from.m, to.m);
            Link(to.m, from.m);
        }

        var queue = new Queue<ushort>(byMap.Keys.Where(Maps.ContainsKey).OrderBy(id => id));
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var neighbours)) continue;
            var here = byMap[cur];   // NB: not `from` — that's a LINQ query keyword and breaks `with`
            foreach (var n in neighbours)
            {
                if (byMap.ContainsKey(n)) continue;
                byMap[n] = here with { Hops = here.Hops + 1 };
                queue.Enqueue(n);
            }
        }
        return byMap;
    }

    /// <summary>A track by name ("mist") or by number ("6"); prefix match as a fallback so "mon" finds
    /// "monkey". Null when nothing matches.</summary>
    public static MusicTrack? FindTrack(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        if (ushort.TryParse(query, out var id))
            return MusicTracks.FirstOrDefault(t => t.Id == id) ?? new MusicTrack(id, "", 2);   // unnamed ids still play
        return MusicTracks.FirstOrDefault(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? MusicTracks.FirstOrDefault(t => t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The name of a track id, or "" if it has none (only some of the 12 stock midis are named).</summary>
    public static string TrackName(ushort id) =>
        MusicTracks.FirstOrDefault(t => t.Id == id)?.Name ?? "";

    // Case-insensitive '*' glob (no '?', no escaping — map names have neither). Used for the MapBgm.csv
    // name patterns, e.g. "Buya *" matching "Buya Kan Shop" but not "Buyan Stables".
    private static bool GlobMatch(string text, string pattern)
    {
        if (pattern.Length == 0) return false;
        var parts = pattern.Split('*');
        if (parts.Length == 1) return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        int pos = 0;
        if (!text.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) return false;
        pos = parts[0].Length;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (parts[i].Length == 0) continue;
            int at = text.IndexOf(parts[i], pos, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            pos = at + parts[i].Length;
        }
        var tail = parts[^1];
        return tail.Length == 0
            ? true
            : text.Length - pos >= tail.Length && text.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    }

    // ---- lookups (used by the @warp / @maps / @mobs / @summon commands) ----

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

    public static ItemDef? ItemById(int id) => _itemById.TryGetValue(id, out var v) ? v : null;
    public static ItemDef? ItemByKey(string? key) => key is not null && _itemByKey.TryGetValue(key, out var v) ? v : null;

    public static MobDef? MobById(int id) => _mobById.TryGetValue(id, out var v) ? v : null;
    public static MobDef? MobByKey(string? key) => key is not null && _mobByKey.TryGetValue(key, out var v) ? v : null;

    // ---- spells / classes (used by @spells / @learnspell + casting) ---------------------------

    /// <summary>The display name of a class/path id (e.g. 1 -> "Warrior"); "path&lt;id&gt;" if unknown.</summary>
    public static string PathName(int pathId) =>
        Paths.TryGetValue(pathId, out var n) && !string.IsNullOrEmpty(n) ? n : $"path{pathId}";

    /// <summary>Resolve a class/path NAME (as stored on the character, e.g. "Warrior") to its path id, or
    /// -1 if it matches no known class. Case-insensitive against the base class name (Paths.PthMark0).</summary>
    public static int PathIdForClass(string? className)
    {
        var name = (className ?? "").Trim();
        return name.Length != 0 && _pathIdByName.TryGetValue(name, out var id) ? id : -1;
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

    /// <summary>How long the caster holds the magic pose — the 0x1A action <c>time</c> field, in 1/60s frames
    /// (35 = ~583ms). ONE value for every spell, deliberately.
    /// <para>RTK varies it per script (35 ×117, 20 ×45, 25 ×9, 30 ×8 across 179 spells) and the groups are
    /// coherent families — 30 is the Poet songs, 25 is the mount/companion set — but that is the RTK author's
    /// styling, not evidence about real 4.95, and RTK coverage has burned us before. The only thing actually
    /// established is that our old hardcoded 8 matches NOTHING in RTK and is too short to survive a held key.
    /// 35 is RTK's modal value and what every attack/heal spell uses. If real per-spell values ever turn up,
    /// this becomes a lookup then — not before.</para></summary>
    public const ushort CastAnimFrames = 35;

    // Genuinely universal base spells — the Nexon manual's "base secrets for every path", learned free in the
    // newbie quest (currently just Soothe). Taught by @spells to EVERY class at their base SplLevel, and NOT
    // gated by SpellCosts even though they carry per-class rows there — for these, those rows are relearn-cost
    // data for the NPC relearn flow only, never a teachable gate. This explicit marker is what separates them
    // from RESTRICTED commons (Return/Approach/Summon): those are ALSO PathId 0 + in SpellCosts, but there the
    // rows ARE the gate — a class with no row (e.g. Warrior for Return) is correctly excluded. PathId alone
    // can't tell the two apart, hence this allowlist.
    private static readonly HashSet<string> UniversalBaseSpells = new(StringComparer.OrdinalIgnoreCase) { "soothe" };
    public static bool IsUniversalBaseSpell(SpellDef sp) => UniversalBaseSpells.Contains(sp.Key);

    /// <summary>Whether class <paramref name="pathId"/> may (re)learn <paramref name="sp"/> from a tutor NPC —
    /// the gate for the "Learn Secret" menu, distinct from the universal <c>@spells</c> grant. A universal
    /// base spell (Soothe) is granted to every class at the newbie quest, but if FORGOTTEN it can only be
    /// relearned at the Guild by a class that has a per-class <see cref="SpellCosts"/> row for it — which is
    /// how Poet is correctly refused Soothe (Warrior/Rogue/Mage have rows, Poet doesn't), matching the live
    /// game's "cannot be relearned by Poets". Any non-universal spell is unaffected (already gated upstream by
    /// <see cref="SpellsForClass"/>), so it always returns true here.</summary>
    public static bool CanRelearnAtNpc(SpellDef sp, int pathId) =>
        !IsUniversalBaseSpell(sp) || LearnCostFor(sp, pathId) is not null;

    /// <summary>Every spell/skill a class can learn at or below <paramref name="maxLevel"/> for a given
    /// <paramref name="alignment"/> (0 unaligned / 1 Kwisin / 2 Mingken / 3 Ohaeng) — i.e. the teachable set
    /// for "@spells". <see cref="SpellCosts"/> is checked FIRST for a spell's key: if present, the class only
    /// qualifies if its pathId has an entry in that spell's per-class table (which is how Warrior ends up
    /// correctly excluded from Return/Approach/Summon — it simply has no row there), at THAT entry's level;
    /// spells with no <see cref="SpellCosts"/> entry fall back to the old universal rule (own path OR
    /// path-0 "peasant commons", at the CSV's flat <c>SplLevel</c>). A spell qualifies if it is universal
    /// (Alignment -1) OR matches the character's alignment; the other sub-alignments' parallel spells are
    /// excluded, so an unaligned character never gets the Kwisin/Mingken/Ohaeng variants (which often share a
    /// display name → looked like duplicates). Deduped by display name as a safety net, preferring the
    /// exact-alignment version over a universal one. Ordered by level then name so the spellbook fills in a
    /// sensible order. Spells switched off by an era gate (see <see cref="IsOutOfEraSplitTrap"/>) are dropped
    /// outright, so they never reach a tutor menu, the @spells grant, or the Divine Secret preview.</summary>
    public static List<SpellDef> SpellsForClass(int pathId, int maxLevel, int alignment) =>
        Spells.Where(s => s.Alignment < 0 || s.Alignment == alignment)
              .Select(s => IsUniversalBaseSpell(s)
                  ? s                                             // taught to EVERY class at its base level; SpellCosts rows are relearn-cost only
                  : SpellCosts.TryGetValue(s.Key, out var perClass)
                      ? (perClass.TryGetValue(pathId, out var cost) ? s with { Level = cost.Level } : null)
                      : (s.PathId == pathId || s.PathId == 0 ? s : null))
              .Where(s => s is not null && s.Level <= maxLevel && !IsOutOfEraSplitTrap(s))
              .Select(s => s!)
              .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
              .Select(g => g.OrderByDescending(s => s.Alignment == alignment).ThenBy(s => s.Level).First())
              .OrderBy(s => s.Level).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
              .ToList();

    public static SpellDef? SpellById(int id) => _spellById.TryGetValue(id, out var v) ? v : null;
    public static SpellDef? SpellByKey(string? key) => key is not null && _spellByKey.TryGetValue(key, out var v) ? v : null;

    /// <summary>The extracted RTK effect for a spell (real formula/archetype), or null if the export has no
    /// row for its identifier (⇒ caller falls back to the keyword classifier).</summary>
    public static SpellFx? FxFor(SpellDef sp) => SpellFx.TryGetValue(sp.Key, out var fx) ? fx : null;

    // Sentinel for "this spell has no pcalign arg" (skills / non-global-helper spells).
    public const int NoPcAlign = int.MinValue;

    // ---- spell effect graphic (client 0x29) + sound (0x19) ------------------------------------
    // The 4.95 client's 0x29 handler plays effect N from Effect.tbl (128 effects) over an entity - it is NOT a
    // floating damage number (proven by disassembly of 0x4504b0 -> 0x44e0a0 -> the index-into-table copy at
    // 0x4354b0). Sound rides its own 0x19 (see Session.BroadcastFx); the two are independent.
    //
    // BOTH now come straight off the spell's own row. There used to be a hardcoded `pcalign` ladder here,
    // ported from rtklua's common/global_{zap,attack,heal}.lua, used ONLY by the Damage and Heal archetypes -
    // every other archetype already carried explicit animation/sound columns. re/fill_spell_fx.py resolved that
    // ladder once into those same columns for Damage/Heal too, so all ten archetypes now work the same way and
    // `pcalign` is provenance only, never read at runtime. Doing it as data also fixed things the ladder could
    // not express (full writeup in that script's docstring):
    //   - 8 spells whose Lua passed the wrong alignment (Rain of Fire and Winds of Disaster are Ohaeng and
    //     Nature's Wounding is Mingken, but all three passed unaligned; the whole poet vital_spark family too),
    //   - 2 whose Spells.csv SplAlignment was wrong (the recover_rogue family is tagged 0,1,1,2),
    //   - Singe rendering differently for rogue than for mage,
    //   - 4 duplicate rows from a scratch copy of rogue/singe.lua.
    // To retune a spell now: edit its animation/sound cell and @reload. No rebuild, no switch statement.

    /// <summary>The Effect.tbl graphic id to play for a cast, or -1 for "no graphic".</summary>
    public static int EffectAnim(SpellFx fx, int pathId = 0) => fx.Animation != 0 ? fx.Animation : -1;

    /// <summary>The NexusTK.snd id to play for a cast, or -1 for "silent".</summary>
    public static int EffectSound(SpellFx fx, int pathId = 0) => fx.Sound != 0 ? fx.Sound : -1;

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
    // A blank query returns everything alphabetically (so "@maps" with no arg lists all).
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

    // Prey creatures — see LoadMobFlees / MobDef.Flees. Loaded BEFORE Mobs so LoadMobs can fold the flag in.
    private static Dictionary<string, bool> MobFleeOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>data/game-data/MobFlees.csv (`Identifier,Flees`) — which creatures RUN AWAY rather than fight.
    /// <para>There is nothing to port for this: RTK's engine knows only three MobBehavior values (0 fights back,
    /// 1 attacks on sight, 2+ inert) and mob_ai_basic.lua gives a rabbit the same chase-and-swing routine as a
    /// wolf — the single <c>RunAway()</c> in the whole RTK tree belongs to one instance boss. So the MOVEMENT is
    /// ported from that boss (Mobs/mob.lua <c>RunAway</c>, Instances/mysterious_merchant.lua's
    /// <c>on_attacked</c>), and WHICH creatures use it is this file. Sparse and kept out of mobs.csv so
    /// re-running the mob extractor can't drop it; hot-reloads with @reload.</para></summary>
    private static Dictionary<string, bool> LoadMobFlees(string? path)
    {
        var flees = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var key = Clean(col.GetValueOrDefault("Identifier", ""));
            if (key.Length == 0) continue;
            flees[key] = col.GetValueOrDefault("Flees", "0").Trim() != "0";
        }
        return flees;
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
            mobs.Add(new MobDef(id, key, name, look, color, hp <= 0 ? 1 : hp, exp, lvl, move, will, aggressive, minDam, maxDam, isBoss, protection, hit, ac, grace,
                Flees: MobFleeOverrides.GetValueOrDefault(key)));
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
                Protection: I("ItmProtection"),
                Healing: I("ItmHealing"), Wisdom: I("ItmWisdom"),
                Text: Clean(col.GetValueOrDefault("ItmText", "")),
                BuyText: Clean(col.GetValueOrDefault("ItmBuyText", "")),
                PathId: I("ItmPthId"), Mark: I("ItmMark"),
                BreakOnDeath: I("ItmBoD") != 0, Protected: I("ItmProtected") != 0));
        }
        return ResolveIconColors(items);
    }

    // Item.epf ids the 4.95 client actually has art for (Item.tbl "NumItems 1310", ids 0..1309).
    private const int ItemIconCount = 1310;

    // Base icons of the colour RUNS the 4.95 Item.epf ships: `base + ItmIconColor` is a real, distinct sprite
    // of the same garment for these, and only these. Derived by decoding Item.epf directly (see the note in
    // ResolveIconColors) — every entry is a ten-frame seasonal set (spring/summer/autumn/winter/blood/earth/
    // star/moon/sun/ancient) that RTK stores as one icon plus a palette index:
    //   89 waistcoat  99 garb  120 scale mail  149 dress  159 blouse  180 mail dress  265 helm  450 gown
    // Deliberately an allow-list rather than "always add the colour": most non-zero ItmIconColor values in
    // Items.csv belong to LATER-client content whose palette index is not a 4.95 frame offset, and blindly
    // adding it there lands on an unrelated sprite (dark_casque 713+2 = another casque's icon, hyun_moo_circlet
    // 989+22 = an 8x8 blob, surge 34+7 = a different item). Those keep their base icon, which is what they
    // already drew.
    private static readonly ushort[] IconColorRuns = { 89, 99, 120, 149, 159, 180, 265, 450 };

    /// <summary>Fold <c>ItmIconColor</c> into the icon for the colour runs the 4.95 client has art for.
    /// <para>Why this exists: the 4.95 client cannot recolour an item graphic. The bag/equip draw
    /// (<c>0x435ab0</c>) and the ground-object draw both call <c>0x431020(epfName, frame, dest)</c> — a frame
    /// index and nothing else — and the palette comes from Item.tbl per frame. Sun/moon/star helms are
    /// therefore ten SEPARATE Item.epf frames (265..274), not one frame plus a colour byte, and sending the
    /// bare <c>ItmIcon</c> drew the first one for all ten (the "everything is spring" bug).</para>
    /// <para>The <paramref name="items"/> pass also refuses any target that some other item already claims as
    /// its own <c>ItmIcon</c> — that catches the rows where RTK gave the variants real icons of their own and
    /// left a stale colour behind (star_armor_dress 172+2 is sun_armor_dress's icon; kabuto 265+10 is another
    /// helm), which must keep the base.</para></summary>
    private static List<ItemDef> ResolveIconColors(List<ItemDef> items)
    {
        var claimed = new HashSet<int>();
        foreach (var it in items) claimed.Add(it.Icon);

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.IconColor == 0 || Array.IndexOf(IconColorRuns, it.Icon) < 0) continue;
            int frame = it.Icon + it.IconColor;
            if (frame >= ItemIconCount || claimed.Contains(frame)) continue;
            items[i] = it with { ClientIcon = (ushort)frame };
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

    // Board-sign tiles: MapId,X,Y,BoardId. Comment/blank/un-parseable rows are skipped, so the shipped file
    // can carry documentation and be filled in live (calibrate the tile with @boardobj, then @reload).
    private static List<(ushort, ushort, ushort, int)> LoadBoardLocations(string? path)
    {
        var list = new List<(ushort, ushort, ushort, int)>();
        foreach (var col in ReadCsv(path))
            if (ushort.TryParse(col.GetValueOrDefault("MapId"), out var m)
                && ushort.TryParse(col.GetValueOrDefault("X"), out var x)
                && ushort.TryParse(col.GetValueOrDefault("Y"), out var y)
                && int.TryParse(col.GetValueOrDefault("BoardId"), out var bid))
                list.Add((m, x, y, bid));
        return list;
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

    // NOTE: NPCs.csv is OUR data now (no re-extraction), so former "override" decisions are baked straight into
    // the rows rather than layered on at load. Historical record, in case a row looks surprising:
    //   * ~24 rows were DELETED — NPCs whose NpcLook exceeds the 4.95 client's Monster.tbl ceiling (327) so they
    //     render nothing (the Abyssal Crystal zodiac puzzle + its questgivers/instance chests/jukeboxes, which
    //     RTK's own team hid via a later migration), PyungPetNpc, and the 3 SalonNpc barbers (Face/Gender stays
    //     Rogue-hall-only per user direction). See [[nexustk-495-broken-npc-assets]].
    //   * A few rows were CORRECTED, e.g. NpcId 51 Bagai (map 363) moved from (2,6) to (2,3).
    //   * The Enabled column carries the on/off toggle (0 = keep the row but don't spawn). Nothing is switched
    //     off today — the inn keeps' assistants (InnNpc2: Ox, Taur) were, and are back, standing in their
    //     taverns with an EMPTY ability composition, so they belong to the scene without a click menu.

    // Stationary NPCs (our data/game-data/NPCs.csv). We keep only NPCs whose map the client can render and that
    // sit on a real tile (skip the (0,0) placeholders — f1npc, treasure portals — which aren't placed beings).
    // Look is the creature sprite; the world draws them via the same 0x07 path as a mob (see World.PopulateNpcs).
    // The Enabled column (default 1) is the spawn on/off switch — a disabled NPC keeps its row but World skips it.
    private static List<NpcDef> LoadNpcs(string? path)
    {
        var npcs = new List<NpcDef>();
        foreach (var col in ReadCsv(path))
        {
            if (!int.TryParse(col.GetValueOrDefault("NpcId"), out var id)) continue;
            ushort.TryParse(col.GetValueOrDefault("NpcMapId", "0"), out var map);
            ushort.TryParse(col.GetValueOrDefault("NpcX", "0"), out var x);
            ushort.TryParse(col.GetValueOrDefault("NpcY", "0"), out var y);
            ushort.TryParse(col.GetValueOrDefault("NpcLook", "0"), out var look);
            byte.TryParse(col.GetValueOrDefault("NpcLookColor", "0"), out var color);
            int.TryParse(col.GetValueOrDefault("NpcMoveTime", "0"), out var move);
            int.TryParse(col.GetValueOrDefault("NpcReturnDistance", "0"), out var leash);
            bool Flag(string k) => col.GetValueOrDefault(k, "0") == "1";
            if (!Maps.ContainsKey(map)) continue;        // map the 4.95 client can't render
            if (x == 0 && y == 0) continue;              // (0,0) = unplaced placeholder / abstract NPC
            var name = Clean(col.GetValueOrDefault("NpcDescription", ""));
            var key = Clean(col.GetValueOrDefault("NpcIdentifier", ""));
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(key) ? $"npc{id}" : key;
            // Enabled defaults ON: a blank/absent column means the NPC spawns (only an explicit 0 disables).
            bool enabled = col.GetValueOrDefault("Enabled", "1").Trim() != "0";
            npcs.Add(new NpcDef(id, key, name, map, x, y, Dir: 2, look, color,
                IsChar: Flag("NpcIsChar"), Shop: Flag("NpcIsShopNpc"),
                Repair: Flag("NpcIsRepairNpc"), Bank: Flag("NpcIsBankNpc"),
                MoveTime: move, ReturnDistance: leash, Enabled: enabled));
        }
        return npcs;
    }

    // Class/path table: PthId -> base class name (PthMark0). The higher PthMark columns are per-rank
    // titles ("Il san (W)" …) we don't need here. PthType is loaded alongside into PathBase (see PathBaseOf).
    private static Dictionary<int, string> LoadPaths(string? path)
    {
        var paths = new Dictionary<int, string>();
        var bases = new Dictionary<int, int>();
        var icons = new Dictionary<int, int>();
        foreach (var col in ReadCsv(path))
            if (int.TryParse(col.GetValueOrDefault("PthId"), out var id))
            {
                paths[id] = Clean(col.GetValueOrDefault("PthMark0", ""));
                bases[id] = int.TryParse(col.GetValueOrDefault("PthType"), out var t) ? t : 0;
                icons[id] = int.TryParse(col.GetValueOrDefault("PthIcon"), out var ic) ? ic : 0;
            }
        PathBase = bases;
        PathIcon = icons;
        return paths;
    }

    // PthId -> PthIcon, the subpath BADGE index. Read live off the user-list window 2026-08-08 (@users
    // sweep, all five columns) and it matches this column exactly: the badge is drawn RELATIVE TO THE
    // COLUMN, so one index means a different sprite per class —
    //     icon 0  (none)      base class
    //     icon 1  Barbarian / Merchant  / Diviner   / Druid
    //     icon 2  Chongun   / Ranger    / Geomancer / Monk      (Ranger draws nothing on this build)
    //     icon 3  Do        / Spy       / Shaman    / Muse
    //     icon 4  Chung ryong / Baekho  / Ju jak    / Hyun moo
    // So a character's whole user-list identity is one PthId: PthType picks the column, PthIcon the badge.
    private static Dictionary<int, int> PathIcon = new();

    /// <summary>Subpath badge index for a path id (Paths.csv PthIcon) — see <see cref="PathIcon"/>.</summary>
    public static int PathIconOf(int pathId) => PathIcon.GetValueOrDefault(pathId, 0);

    // PthId -> PthType, the BASE path a (sub)class descends from (RTK class_db.c classdb_path): every subpath
    // collapses onto 1 Warrior / 2 Rogue / 3 Mage / 4 Poet, e.g. Chung ryong (6) and Barbarian (10) are both
    // base 1. 0 = Peasant, 5 = Dreamweaver/Archon (RTK's GM branch, which skips every wear restriction).
    private static Dictionary<int, int> PathBase = new();

    /// <summary>The base path (PthType) a class/path id descends from — RTK <c>classdb_path</c>. Unknown ids
    /// and Peasant both give 0.</summary>
    public static int PathBaseOf(int pathId) => PathBase.GetValueOrDefault(pathId, 0);

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

    // See MythicCaves above. One row per zodiac animal. EntranceTiles is a ';'-separated list of "x:y" pairs
    // (2 per cave in retail). T{1,2,3}{Level,Vita,Mana} give the cave-1/2/3 gates; a 0 Vita/Mana means that
    // tier is level-only. A malformed/absent file yields an empty registry (entrances then never gate — the
    // player is held out only where a row exists), same fail-soft posture as every other loader here.
    private static List<MythicCaveDef> LoadMythicCaves(string? path)
    {
        var list = new List<MythicCaveDef>();
        foreach (var col in ReadCsv(path))
        {
            var animal = col.GetValueOrDefault("Animal", "").Trim();
            if (animal.Length == 0) continue;
            ushort U(string k) => ushort.TryParse(col.GetValueOrDefault(k), out var v) ? v : (ushort)0;
            uint U32(string k) => uint.TryParse(col.GetValueOrDefault(k), out var v) ? v : 0u;

            var tiles = new List<(ushort X, ushort Y)>();
            foreach (var pair in (col.GetValueOrDefault("EntranceTiles") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = pair.Split(':');
                if (xy.Length == 2 && ushort.TryParse(xy[0].Trim(), out var tx) && ushort.TryParse(xy[1].Trim(), out var ty))
                    tiles.Add((tx, ty));
            }

            var tiers = new MythicTier[3];
            for (int t = 1; t <= 3; t++)
                tiers[t - 1] = new MythicTier((byte)U($"T{t}Level"), U32($"T{t}Vita"), U32($"T{t}Mana"));

            list.Add(new MythicCaveDef(animal, U("EntranceMap"), tiles.ToArray(),
                U("DestMap"), U("DestX"), U("DestY"), tiers, col.GetValueOrDefault("Sources", "")));
        }
        return list;
    }

    // See ArenaDoors above. One row per door (a door is the 2 adjacent tiles the sprite occupies). Tiles is
    // ';'-separated "x:y"; DestX may be a "lo-hi" range. MaxLevel/MaxVita/MaxMana of 0 mean "no cap".
    private static List<ArenaDoorDef> LoadArenaDoors(string? path)
    {
        var list = new List<ArenaDoorDef>();
        foreach (var col in ReadCsv(path))
        {
            if (!ushort.TryParse(col.GetValueOrDefault("Map"), out var map)) continue;
            ushort U(string k) => ushort.TryParse(col.GetValueOrDefault(k), out var v) ? v : (ushort)0;
            int I(string k) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : 0;
            uint U32(string k) => uint.TryParse(col.GetValueOrDefault(k), out var v) ? v : 0u;

            var tiles = new List<(ushort X, ushort Y)>();
            foreach (var pair in (col.GetValueOrDefault("Tiles") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = pair.Split(':');
                if (xy.Length == 2 && ushort.TryParse(xy[0].Trim(), out var tx) && ushort.TryParse(xy[1].Trim(), out var ty))
                    tiles.Add((tx, ty));
            }
            if (tiles.Count == 0) continue;

            // DestX is either a single column or a "lo-hi" band the landing tile is rolled from.
            var span = (col.GetValueOrDefault("DestX") ?? "").Split('-', 2);
            ushort.TryParse(span[0].Trim(), out var dx);
            var dx2 = span.Length > 1 && ushort.TryParse(span[1].Trim(), out var hi) ? hi : dx;

            list.Add(new ArenaDoorDef(map, tiles.ToArray(), U("DestMap"), dx, dx2, U("DestY"),
                I("MinLevel"), I("MaxLevel"), U32("MaxVita"), U32("MaxMana"),
                I("Unmarked") != 0, col.GetValueOrDefault("Label", "").Trim(), col.GetValueOrDefault("Sources", "")));
        }
        return list;
    }

    // ---- Location / warp geometry loaders (see the Content.* registries near MythicCaves) ----------------

    // MusicTracks.csv: Track,Name[,Type] — the id<->name table for the stock midis. Type defaults to 2 (midi).
    private static List<MusicTrack> LoadMusicTracks(string? path)
    {
        var list = new List<MusicTrack>();
        foreach (var col in ReadCsv(path))
        {
            if (!ushort.TryParse(col.GetValueOrDefault("Track"), out var id)) continue;
            var name = col.GetValueOrDefault("Name", "").Trim();
            if (!byte.TryParse(col.GetValueOrDefault("Type"), out var type)) type = 2;
            list.Add(new MusicTrack(id, name, type));
        }
        return list;
    }

    // MapBgm.csv: Zone,Track,Maps,Names — one row per AREA. `Track` is a MusicTracks.csv name or a raw id;
    // `Maps` is a ';'-separated list of ids and lo-hi ranges; `Names` is a ';'-separated list of map-name
    // globs. The row whose Zone is "Default" is pulled out as the fresh-session fallback (DefaultBgm).
    private static (List<BgmZone>, (ushort, byte)?) LoadBgmZones(string? path)
    {
        var zones = new List<BgmZone>();
        (ushort, byte)? def = null;

        foreach (var col in ReadCsv(path))
        {
            var zone = col.GetValueOrDefault("Zone", "").Trim();
            var track = FindTrack(col.GetValueOrDefault("Track", ""));
            if (zone.Length == 0 || track is null) continue;

            if (zone.Equals("Default", StringComparison.OrdinalIgnoreCase)) { def = (track.Id, track.Type); continue; }

            var maps = new List<(ushort, ushort)>();
            foreach (var part in col.GetValueOrDefault("Maps", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var span = part.Split('-', 2);
                if (ushort.TryParse(span[0].Trim(), out var lo))
                    maps.Add((lo, span.Length > 1 && ushort.TryParse(span[1].Trim(), out var hi) ? hi : lo));
            }
            var names = col.GetValueOrDefault("Names", "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            zones.Add(new BgmZone(zone, track.Id, track.Type, maps, names));
        }
        return (zones, def);
    }

    private static Dictionary<string, IReadOnlyList<InnDef>> LoadInns(string? path)
    {
        var acc = new Dictionary<string, List<InnDef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var g = col.GetValueOrDefault("Group", "").Trim();
            if (g.Length == 0 || !ushort.TryParse(col.GetValueOrDefault("Map"), out var m)) continue;
            ushort.TryParse(col.GetValueOrDefault("X"), out var x);
            ushort.TryParse(col.GetValueOrDefault("Y"), out var y);
            if (!acc.TryGetValue(g, out var list)) acc[g] = list = new List<InnDef>();
            list.Add(new InnDef(m, x, y));
        }
        return acc.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<InnDef>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static List<ForageAreaDef> LoadForageAreas(string? path)
    {
        var list = new List<ForageAreaDef>();
        foreach (var col in ReadCsv(path))
        {
            var key = col.GetValueOrDefault("ItemKey", "").Trim();
            if (key.Length == 0) continue;
            int I(string k) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : 0;
            list.Add(new ForageAreaDef(key, (ushort)I("Map"), I("MinX"), I("MaxX"), I("MinY"), I("MaxY"),
                I("Max"), I("MinQty"), I("MaxQty")));
        }
        return list;
    }

    private static Dictionary<ushort, PathHallDef> LoadPathHalls(string? path)
    {
        var d = new Dictionary<ushort, PathHallDef>();
        foreach (var col in ReadCsv(path))
        {
            if (!ushort.TryParse(col.GetValueOrDefault("HallMap"), out var hall)) continue;
            int I(string k) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : 0;
            ushort U(string k) => (ushort)I(k);
            d[hall] = new PathHallDef(I("BaseClass"), U("GuildMap"),
                new[] { U("SanctumUnaligned"), U("SanctumKwisin"), U("SanctumMingken"), U("SanctumOhaeng") });
        }
        return d;
    }

    private static Dictionary<int, GatewayDef> LoadGatewayGates(string? path)
    {
        var acc = new Dictionary<int, (ushort map, string city, Dictionary<char, (int Xlo, int Xhi, int Ylo, int Yhi)> gates)>();
        foreach (var col in ReadCsv(path))
        {
            if (!int.TryParse(col.GetValueOrDefault("Region"), out var region)) continue;
            var gate = col.GetValueOrDefault("Gate", "").Trim().ToLowerInvariant();
            if (gate.Length == 0) continue;
            ushort.TryParse(col.GetValueOrDefault("Map"), out var map);
            var city = col.GetValueOrDefault("City", "").Trim();
            int I(string k) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : 0;
            if (!acc.TryGetValue(region, out var r)) acc[region] = r = (map, city, new());
            r.gates[gate[0]] = (I("Xlo"), I("Xhi"), I("Ylo"), I("Yhi"));
        }
        return acc.ToDictionary(kv => kv.Key, kv => new GatewayDef(kv.Value.map, kv.Value.city,
            (IReadOnlyDictionary<char, (int Xlo, int Xhi, int Ylo, int Yhi)>)kv.Value.gates));
    }

    private static List<WorldDestDef> LoadWorldDests(string? path)
    {
        var list = new List<WorldDestDef>();
        foreach (var col in ReadCsv(path))
        {
            var name = col.GetValueOrDefault("Name", "").Trim();
            if (name.Length == 0) continue;
            int I(string k) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : 0;
            list.Add(new WorldDestDef(name, (ushort)I("Map"), (ushort)I("X"), (ushort)I("Y"), I("DotX"), I("DotY")));
        }
        return list;
    }

    private static Dictionary<ushort, WorldTriggerDef> LoadWorldTriggers(string? path)
    {
        var d = new Dictionary<ushort, WorldTriggerDef>();
        foreach (var col in ReadCsv(path))
        {
            if (!ushort.TryParse(col.GetValueOrDefault("Map"), out var m)) continue;
            var axis = col.GetValueOrDefault("FixedAxis", "x").Trim().ToLowerInvariant();
            int I(string k) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : 0;
            d[m] = new WorldTriggerDef(axis.Length > 0 ? axis[0] : 'x', I("FixedLo"), I("FixedHi"), I("RangeLo"), I("RangeHi"));
        }
        return d;
    }

    private static Dictionary<ushort, (ushort Map, ushort X, ushort Y)> LoadFallRooms(string? path)
    {
        var d = new Dictionary<ushort, (ushort, ushort, ushort)>();
        foreach (var col in ReadCsv(path))
        {
            if (!ushort.TryParse(col.GetValueOrDefault("DestMap"), out var dest)) continue;
            ushort.TryParse(col.GetValueOrDefault("DestX"), out var dx);
            ushort.TryParse(col.GetValueOrDefault("DestY"), out var dy);
            bool tiered = col.GetValueOrDefault("Tiered", "0") == "1";
            foreach (var s in (col.GetValueOrDefault("SrcMaps") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!ushort.TryParse(s.Trim(), out var src)) continue;
                if (tiered)
                    for (ushort off = 0; off <= 4000; off += 3000)   // 0 = cave 1, +3000 = cave 2, +4000 = cave 3
                        d[(ushort)(src + off)] = ((ushort)(dest + off), dx, dy);
                else
                    d[src] = (dest, dx, dy);
            }
        }
        return d;
    }

    private static Dictionary<int, (int HpMin, int HpMax, int MpMin, int MpMax)> LoadPathGrowth(string? path)
    {
        var d = new Dictionary<int, (int, int, int, int)>();
        foreach (var c in ReadCsv(path))
        {
            if (!int.TryParse(c.GetValueOrDefault("path"), out var p)) continue;
            int.TryParse(c.GetValueOrDefault("hpMin", "0"), out var a);
            int.TryParse(c.GetValueOrDefault("hpMax", "0"), out var b);
            int.TryParse(c.GetValueOrDefault("mpMin", "0"), out var e);
            int.TryParse(c.GetValueOrDefault("mpMax", "0"), out var f);
            d[p] = (a, b, e, f);
        }
        return d;
    }

    // ServerTuning.csv: named scalar config, key -> double (typed accessors above apply per-key defaults).
    private static Dictionary<string, double> LoadTuning(string? path)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadCsv(path))
        {
            var k = c.GetValueOrDefault("key", "").Trim();
            if (k.Length == 0) continue;
            if (double.TryParse(c.GetValueOrDefault("value", ""), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                d[k] = v;
        }
        return d;
    }

    // DoorObjects.csv: two row kinds. `map` rows are exact faced-object swaps (result = `;`-separated new ids at
    // startDx); `delta` rows are single-tile [lo,hi] ranges whose result is a signed delta added to the faced id.
    // The optional `defaultOpen` column (1 on a `map` row) marks that row's faced id as the CLOSED state of a
    // door that should start open — MapData.Load rewrites those cells as the file is read, per cell, so a
    // multi-tile run needs the flag on every one of its pieces (see DoorDefaultOpen).
    private static (Dictionary<int, (int, ushort[])>, List<(int, int, int)>, Dictionary<int, ushort>)
        LoadDoorObjects(string? path)
    {
        var swaps = new Dictionary<int, (int, ushort[])>();
        var deltas = new List<(int, int, int)>();
        var open = new Dictionary<int, ushort>();
        foreach (var c in ReadCsv(path))
        {
            var kind = c.GetValueOrDefault("kind", "").Trim();
            if (!int.TryParse(c.GetValueOrDefault("lo"), out var lo)) continue;
            if (!int.TryParse(c.GetValueOrDefault("hi"), out var hi)) continue;
            var result = c.GetValueOrDefault("result", "").Trim();
            if (kind == "map")
            {
                int.TryParse(c.GetValueOrDefault("startDx", "0"), out var dx);
                var ids = result.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => ushort.TryParse(s, out var u) ? u : (ushort)0).ToArray();
                if (ids.Length == 0) continue;
                swaps[lo] = (dx, ids);   // map rows use lo == hi as the exact faced id
                // This piece's own counterpart sits at -startDx in the run (startDx is how far LEFT the run
                // starts from the faced tile), so the substitution stays single-cell and order-independent.
                if (c.GetValueOrDefault("defaultOpen", "").Trim() == "1" && -dx >= 0 && -dx < ids.Length)
                    open[lo] = ids[-dx];
            }
            else if (kind == "delta" && int.TryParse(result, out var d))
            {
                deltas.Add((lo, hi, d));
            }
        }
        return (swaps, deltas, open);
    }

    // NpcAbilities.csv: NpcKey -> pipe-list of ability names (resolved to instances by NpcScripts.AbilityByName).
    private static Dictionary<string, string[]> LoadNpcCompositions(string? path)
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadCsv(path))
        {
            var k = c.GetValueOrDefault("NpcKey", "").Trim();
            if (k.Length == 0) continue;
            d[k] = c.GetValueOrDefault("Abilities", "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return d;
    }

    // Load a verb/row params CSV into "key -> whole row" — shared by SpellParams and ItemParams (both feed a Lua
    // verb that reads whatever columns it needs). Rows are keyed by the `key` column; the `verb` column names
    // the Lua verb.
    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadKeyedRows(string? path)
    {
        var d = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var key = col.GetValueOrDefault("key", "").Trim();
            if (key.Length == 0) continue;
            d[key] = col;   // the whole row, verbatim — the Lua verb reads whatever columns it needs
        }
        return d;
    }

    private static Dictionary<string, IReadOnlyList<(string Name, string[] Keys)>> LoadShopCatalogues(string? path)
    {
        var acc = new Dictionary<string, List<(string Name, string[] Keys)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var npc = col.GetValueOrDefault("NpcKey", "").Trim();
            var cat = col.GetValueOrDefault("Category", "").Trim();
            if (npc.Length == 0 || cat.Length == 0) continue;
            var keys = (col.GetValueOrDefault("ItemKeys") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (!acc.TryGetValue(npc, out var list)) acc[npc] = list = new();
            list.Add((cat, keys));
        }
        return acc.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(string, string[])>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // MapCells.csv -> per-map authored cell overrides. Blank value column = inherit from the .map file, so
    // "Map,X,Y,,0," means "make this tile walkable, leave its graphics alone". Rows for maps that don't exist
    // are kept: the map may simply not be in the registry yet, and MapData only ever asks for its own id.
    private static (Dictionary<ushort, List<CellOverride>>, int) LoadMapCells(string? path)
    {
        var d = new Dictionary<ushort, List<CellOverride>>();
        int n = 0;
        foreach (var col in ReadCsv(path))
        {
            if (!ushort.TryParse(col.GetValueOrDefault("Map"), out var m)) continue;
            if (!ushort.TryParse(col.GetValueOrDefault("X"), out var x)) continue;
            if (!ushort.TryParse(col.GetValueOrDefault("Y"), out var y)) continue;
            ushort? U(string k)
            {
                var v = col.GetValueOrDefault(k);
                return string.IsNullOrWhiteSpace(v) || !ushort.TryParse(v.Trim(), out var r) ? null : r;
            }
            var tile = U("Tile"); var pass = U("Pass"); var obj = U("Obj");
            if (tile is null && pass is null && obj is null) continue;   // a row that overrides nothing
            if (!d.TryGetValue(m, out var list)) d[m] = list = new();
            list.Add(new CellOverride(m, x, y, tile, pass, obj));
            n++;
        }
        return (d, n);
    }

    private static Dictionary<(ushort, ushort, ushort), Doors.DoorConfig> LoadDoors(string? path)
    {
        var d = new Dictionary<(ushort, ushort, ushort), Doors.DoorConfig>();
        foreach (var col in ReadCsv(path))
        {
            if (!ushort.TryParse(col.GetValueOrDefault("Map"), out var m)) continue;
            ushort.TryParse(col.GetValueOrDefault("X"), out var x);
            ushort.TryParse(col.GetValueOrDefault("Y"), out var y);
            bool B(string k, bool def) { var v = col.GetValueOrDefault(k); return string.IsNullOrEmpty(v) ? def : v.Trim() == "1"; }
            var key = col.GetValueOrDefault("Key", "");
            // ClosedObj/OpenObj: ';'-separated object-id runs starting at this tile (same convention as
            // DoorObjects.csv). Both must be present and the same length to be usable — a half-configured
            // pair would give a door that opens and can never close, so drop both and log it.
            ushort[]? Run(string k)
            {
                var v = col.GetValueOrDefault(k, "");
                if (string.IsNullOrWhiteSpace(v)) return null;
                var parts = v.Split(';', StringSplitOptions.RemoveEmptyEntries);
                var outp = new List<ushort>();
                foreach (var p in parts) if (ushort.TryParse(p.Trim(), out var o)) outp.Add(o);
                return outp.Count > 0 ? outp.ToArray() : null;
            }
            var closed = Run("ClosedObj");
            var open = Run("OpenObj");
            if (closed is not null && open is not null && closed.Length != open.Length)
            {
                Log.Info($"   !! Doors.csv ({m},{x},{y}): ClosedObj has {closed.Length} id(s) but OpenObj has {open.Length} — ignoring both");
                closed = open = null;
            }
            d[(m, x, y)] = new Doors.DoorConfig(
                Locked: B("Locked", false),
                Key: string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
                ConsumeKey: B("ConsumeKey", true),
                ForceOpen: B("ForceOpen", false),
                ClosedObjs: closed,
                OpenObjs: open,
                DefaultClosed: B("DefaultClosed", false));
        }
        return d;
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
    // Loaded from data/game-data/SpellMods.csv (`rage` column) in Load() — see LoadSpellMods.
    private static IReadOnlyDictionary<string, int> RageAmount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rage multiplier this spell/skill arms, or null if it isn't a rage-tier spell. See
    /// <see cref="RageAmount"/>.</summary>
    public static int? RageAmountFor(SpellDef sp) => RageAmount.TryGetValue(sp.Key, out var r) ? r : null;

    /// <summary>Chung Ryong's Rage — the Warrior Chung Ryong subpath's INCREMENTAL fury (one spell key,
    /// recast every 120s to climb tier 1→6). Handled by its own <see cref="Session.CastChungRyongRage"/>
    /// path rather than the flat <see cref="RageAmountFor"/> one, so it never appears in SpellMods.csv.</summary>
    public static bool IsChungRyongRage(SpellDef sp) => sp.Key == "chung_ryongs_rage";

    // RTK warrior/enchant.lua, infuse.lua, ingress.lua, vipers_venom.lua, dragons_flame.lua + rogue/
    // tigers_fortitude.lua, baekhos_blade.lua: a weapon-enchant STANCE (player.enchant). Unlike rage (which
    // swingDamage.lua multiplies the WHOLE swing by), enchant only multiplies the raw weapon-swing term
    // (s/2) — see Session.PlayerSwingDamage. All 16 identifiers share one mutual-exclusion group (RTK
    // "enchants" checkIfCast table, spellTables.lua) — casting any one while another (or itself) is already
    // active just re-prints "This spell is already active.", never stacks/upgrades (Session.CastEnchant).
    // Mana/level are hardcoded straight from each spell's own Lua (not trusted from the CSV export — same
    // Type-5-skill gap as rage/stealth/sacrifice-strikes; tigers_fortitude_rogue genuinely costs 0 mana,
    // just consumes cast components via requirements()).
    // Loaded from data/game-data/SpellMods.csv (`enchantAmt`/`enchantMana` columns) in Load() — see LoadSpellMods.
    //
    // LADDER RESOLVED 2026-08-04 on an EARLIEST-SOURCE-WINS rule (user decision). Three sources exist and
    // they disagree; dating them shows the values were REBALANCED UPWARD over the years, so the earliest
    // reading is the one closest to our 4.95 client (built 2001-06-29):
    //                        nexusatlas 2003/04   DarkMaverick nmails   Melalye board post (rev. 2011)
    //     Invisible                  -                    8                       9        (tswolf 2001: 5)
    //     Rage 1..6                  -            6/9/12/18/27/81           8/14/20/26/36/81
    //     Cunning 1..5               -                  4..8                     6..12
    //     Dragon's Flame            5                     5                        6
    //     Baekho's Blade           1.5                   1.5                       2
    // Every value that moved, moved UP — so the 2011 post (boards.nexustk.com/Rogues/Melalye%2007210115.html,
    // byline "Rogue Tutor Melalye" but signed Yttribium, "Reviewed 2011, by Deimos") is the LEAST era-correct
    // despite being the most detailed. Do not treat it as authoritative on magnitudes; it IS the best source
    // for STRUCTURE (which subpath rank grants which tier) and for qualitative rules.
    // WHAT WE SHIP (earliest of each):
    //     Enchant 1.5 | Infuse 2 | Ingress 3 | Viper's Venom 4 | Dragon's Flame 5 | Spirit Blade 9
    //     Baekho's Blade (rogue, Ee San) 1.5
    // Melalye's Dragon's Harness (Sam San) 8 and Chung Ryong's Wrath (Sa San) 10 do NOT exist in our 4.95
    // Spells.csv (later-era subpath content) so they are not added. spirit_blade was ADDED here — it existed
    // in Spells.csv with no SpellMods row, i.e. it was silently INERT.
    // Klanx/Yari (also in the DM PDF) define `Ing` as 1 none | 3 Ingress | 4 "Il san NPC" | 5 "Ee san NPC",
    // agreeing on Ingress 3. Infuse 2 / Ingress 3 / Viper's Venom 4 are unanimous across all sources.
    // NOTE baekhos_blade_rogue 1.5 now EQUALS the free tigers_fortitude_rogue despite costing 6000 mana at
    // level 99. That looks wrong but it is what both early sources say; flagged, not "corrected".
    // STILL UNRESOLVED: art_of_war — DM calls it a x4 weapon enhancer, but RTK's art_of_war.lua implements
    // something ELSE entirely (an 80-mana reveal of a mob's max health). Not wired as an enchant here.
    private static IReadOnlyDictionary<string, (double Amt, int Mana)> EnchantSpells = new Dictionary<string, (double, int)>(StringComparer.OrdinalIgnoreCase);
    public static (double Amt, int Mana)? EnchantFor(SpellDef sp) => EnchantSpells.TryGetValue(sp.Key, out var e) ? e : null;

    // Rogue Invisible (+3 same-mechanic aliases per alignment: Spirit's Form/Life's Cloak/Glass Form):
    // the swing that follows gets a flat 5x damage multiplier (tswolf 8/2001, era-matched to 4.95:
    // "Invisible increases attack by 5 times"; RTK's Lua says 9x but that's a later, non-authoritative
    // rebalance), a sneak-attack bonus that then breaks the stealth —
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
    // FocusedBlow (Rogue Sam San) and Siege (Warrior Sam San) join the same family — both are "spend your own
    // vita for a big facing-tile hit". nexusatlas: Focused Blow "Takes 2/3 of current Vita in a Strong Attack.
    // The attack does 2 times current vitality in damage at 0 AC"; Siege "does a critical strike and leaves the
    // caster with 25% vita left. Damage to target is 1.875 times current vitality plus 0.5 current mana at 0 AC".
    // They have NO alignment aliases yet — the 2002-10-01 announcement lists Siege only under its Ohaeng-ish
    // name "Life's end", so the other three alias identifiers are unknown and deliberately not invented.
    public enum SacrificeFamily { LethalStrike, DesperateAttack, Berserk, Whirlwind, FocusedBlow, Siege }
    private static readonly Dictionary<string, SacrificeFamily> SacrificeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["focused_blow_rogue"] = SacrificeFamily.FocusedBlow,

        // Siege + its three alignment aliases (user-confirmed): Kwi-Sin "Soul's Freedom", Ming-Ken
        // "Life's End", Ohaeng "Winter Chill". Same mechanic; CastSacrificeStrike picks the DISPLAYED name
        // from the caster's own alignment, not from which alias was granted, exactly as the other families do.
        ["siege_warrior"]        = SacrificeFamily.Siege,
        ["souls_freedom_warrior"] = SacrificeFamily.Siege,
        ["lifes_end_warrior"]     = SacrificeFamily.Siege,
        ["winter_chill_warrior"]  = SacrificeFamily.Siege,

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

    // Sam San one-offs that fit no existing archetype (nexusatlas 2004; the 2002-10-01 TSWolf announcement
    // names Mend Equipment "Luster return" and Spirit Salvation, i.e. these are alignment aliases whose other
    // identifiers we do not know - only the unaligned key is wired).
    public static bool IsMendEquipment(SpellDef sp) => sp.Key == "mend_equipment_warrior";

    // NPC-subpath guardian spells (their own SplPthId: 8 Ju Jak, 9 Hyun Moo). Both had Spells.csv rows but no
    // archetype, so they spent mana and did nothing. Hyun Moo Revival is the one spell MEANT to be cast while
    // dead, which is why Session.HandleCast exempts it from "Spirits can't cast spells".
    // "Takes all mana when cast and does that much damage times N" (nexusatlas): Inferno x1.5 (Ee San mage)
    // and Dooms Fire x2.5 (Sam San mage). Their spell_effects rows carry mana=0 and an amountExpr reading
    // player.magic, which computes the damage correctly but NEVER SPENDS the pool - so before this they were
    // free, repeatable nukes scaling off a mana bar that never moved. Session.ApplyCast drains after a
    // successful cast (the amount is computed first, so ordering is safe).
    private static readonly HashSet<string> AllManaSpells =
        new(StringComparer.OrdinalIgnoreCase) { "inferno_mage", "dooms_fire_mage" };
    public static bool ConsumesAllMana(SpellDef sp) => AllManaSpells.Contains(sp.Key);

    public static bool IsJuJakEvocation(SpellDef sp) => sp.Key == "ju_jak_evocation";
    public static bool IsHyunMooRevival(SpellDef sp) => sp.Key == "hyun_moo_revival";

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

    // (Filch/Spirit's Hand/Quick Fingers/Light Touch — RTK rogue/filch.lua — no longer needs a key table here:
    // the four spells are bound to the `filch` verb by their SpellParams rows. The mechanic grabs whatever is on
    // the SINGLE tile in front of the caster, despite the description's "up to 4 tiles" claim — the Lua's own
    // loop only ever runs i=1 — and skips a tile a player is standing on, or one holding someone else's
    // looter-locked death pile. See Session.LuaFilch + verbs.filch.)

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
    // (No key table: the five spells are bound to the `spot_traps` verb by their SpellParams rows.)

    // RTK rogue/judge.lua (Judge/Spiritual Advisor/Natural Talent/Appraise — 4 reskins) + rogue/spy.lua
    // (Spy/Spiritual Guide/Nature's Handiwork/Judgement Day — 4 reskins, same popup PLUS the target's
    // inventory list): a text popup of the target's class/name/level/title/might/will/grace. The judge
    // family requires the target STRICTLY lower level than the caster (`target.level >= player.level` fails);
    // the spy family allows an EQUAL level too (`target.level > player.level` fails) — a genuine, deliberate
    // difference in the Lua source, not a typo. Session.CastDivination.
    // All eight are bound to the `divine` verb by their SpellParams rows, so no dispatch table is needed.
    // The judge/spy SPLIT still is: it is not a binding, it's a rule the verb reads through ctx.spyMode -
    // judge needs the target STRICTLY lower level, spy allows equal. See Session.LuaIsSpy.
    private static readonly HashSet<string> DivinationSpySpells = new(StringComparer.OrdinalIgnoreCase)
        { "spy_rogue", "spiritual_guide_rogue", "natures_handiwork_rogue", "judgement_day_rogue" };
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
    // Loaded from data/game-data/Traps.csv (spell-side cast cost; kind = TrapKind enum name) in Load() — see
    // LoadTrapSpells. The trigger-side effect (damage/durations) stays in World.TriggerTrapLocked.
    private static IReadOnlyDictionary<string, (TrapKind Kind, int Level, int Mana)> TrapSpells = new Dictionary<string, (TrapKind, int, int)>(StringComparer.OrdinalIgnoreCase);
    public static (TrapKind Kind, int Level, int Mana)? TrapSpellFor(SpellDef sp) => TrapSpells.TryGetValue(sp.Key, out var t) ? t : null;
    public static bool IsTrapDispatcher(SpellDef sp) => sp.Key.Equals("set_trap", StringComparison.OrdinalIgnoreCase);

    // ERA GATE — the 8 individual set_X_trap spells above did NOT exist in 4.95. They were added by the
    // 2003-07-01 reset, two years after this client shipped (built 2001-06-29). Three archive posts that day
    // (nexus_news.md): Growl 10:48 relaying the in-character Dream Weaver board post from Eldridge ("the
    // guild masters have also devised some new spells to help rogues ... the ability to split your trap
    // spells into several spells", tagged with the standard OOC "currently under review" patch marker);
    // Rachel 16:07 with the mechanics ("Set traps spell still exists, however you can also learn each
    // individual trap spell such as 'Sleep trap' 'Dart trap' so that you don't have to type in the name");
    // Conro 18:15 confirming the launch bug that Spot traps couldn't see them (fixed 2003-10-31). The
    // NexusAtlas pages for these spells are dated 2003-11-04, but that post is Rachel's SITE-maintenance
    // list, not a patch — her 2003-10-27 corrections say the data "will be added soon". Corroborated by the
    // rogue tutor spell list itself, where every split entry is worded "Seperate form of <X>" and carries no
    // ingredient cost (`-`), i.e. written after the split, describing derivatives of the original.
    //
    // So in-era there is exactly ONE way to set a trap: cast Set Trap (row 2701) and TYPE the trap's name at
    // its "What trap? >" prompt. Nothing about the trap MECHANICS changes here and the rows stay in
    // Spells.csv/Traps.csv — the dispatcher resolves the same set_X_trap SpellDefs internally, so every trap
    // kind still works exactly as before. This gate only removes them as spells a rogue can learn from a
    // tutor (SpellsForClass -> Learn Secret / Divine Secret / @spells) and cast directly from the book.
    //
    // Off by default; a deployment that wants the post-2003 behavior sets SplitTrapSpells=1 in
    // data/game-data/ServerTuning.csv and runs @reload. Bladestorm/Sword's Dance/Tiger's Ambush/Cutting Edge
    // (rows 2710-2713) are NOT covered — they are a different, subpath-only mechanic (the `bladestorm` verb / set_bladestorm_trap family).
    public static bool SplitTrapSpellsEnabled => Tune("SplitTrapSpells", 0) != 0;
    private static readonly HashSet<string> SplitTrapSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        "set_dart_trap", "set_flash_trap", "set_repeating_dart_trap", "set_snare_trap",
        "set_spear_trap", "set_poison_dart_trap", "set_death_trap", "set_sleep_trap",
    };
    /// <summary>True when <paramref name="sp"/> is one of the 8 post-2003 individual trap spells and the
    /// era gate is off — i.e. it may not be learned or cast directly (Set Trap still sets that trap).</summary>
    public static bool IsOutOfEraSplitTrap(SpellDef sp) => !SplitTrapSpellsEnabled && SplitTrapSpells.Contains(sp.Key);

    // set_trap.lua's own q-string match ("dart"/"snare"/"repeating"/"flash"/"spear"/"poison"/"death"/"sleep")
    // to the underlying set_X_trap identifier that TrapSpellFor understands.
    //
    // RTK's set_trap.lua PROMPTS with one string and MATCHES another: it prints `traps[i] .. " trap"` — "Snare
    // trap", "Dart trap", … — then compares `q == "snare"`. So typing back exactly what the menu just told you
    // to type falls through to the else-branch and nothing is set. Live 4.95 clients send the full label
    // (log: `dec : 0d 53 6e 61 72 65 20 74 72 61 70 00  |.Snare trap.|`), so matching RTK literally means no
    // trap can ever be set through the dispatcher. Normalise instead: lowercase, drop a trailing "trap", and
    // key off the first remaining word. That accepts "Snare trap", "snare", "SNARE TRAP" and "Repeating dart"
    // alike, and is a superset of RTK's own accepted inputs — nothing that used to work stops working.
    public static string? TrapKeyForAnswer(string answer)
    {
        var a = (answer ?? "").Trim().ToLowerInvariant();
        if (a.EndsWith(" trap", StringComparison.Ordinal)) a = a[..^5].TrimEnd();
        int sp = a.IndexOf(' ');
        if (sp > 0) a = a[..sp];              // "repeating dart" -> "repeating", "poison dart" -> "poison"
        return a switch
        {
            "dart" => "set_dart_trap", "snare" => "set_snare_trap", "repeating" => "set_repeating_dart_trap",
            "flash" => "set_flash_trap", "spear" => "set_spear_trap", "poison" => "set_poison_dart_trap",
            "death" => "set_death_trap", "sleep" => "set_sleep_trap", _ => null,
        };
    }

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
    // (No key table: the four are bound to the `bladestorm` verb by their SpellParams rows. The trap they
    // place is still the "bladestorm" wire kind that World.TriggerTrapLocked / CheckPlayerTrapTrigger switch on.)

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
    // Loaded from data/game-data/Morphs.csv (rows with an empty `answers` column) in Load() — see LoadMorphs.
    public static IReadOnlyDictionary<string, (ushort Look, ushort LookFemale, int Mana, int DurationMs)> MorphSpells { get; private set; } =
        new Dictionary<string, (ushort, ushort, int, int)>(StringComparer.OrdinalIgnoreCase);
    public static (ushort Look, ushort LookFemale, int Mana, int DurationMs)? MorphFor(SpellDef sp) =>
        MorphSpells.TryGetValue(sp.Key, out var m) ? m : null;

    // Question-dispatched morphs: SplQuestion asks which animal, the typed answer picks the look (RTK
    // player.question, lowercased). rodent_rogue.lua never actually lowers `player.question` into a local
    // `q` before comparing (a genuine copy-paste bug in that one file vs. every sibling) — ported as
    // OBVIOUSLY intended (rabbit/squirrel), not as the RTK bug, since the bug has no gameplay purpose.
    // rodent_rogue's "rabbit" answer is look 125, NOT the 21 RTK's rodent.lua hardcodes: 21 is the HARE
    // sprite (mobs.csv `hare`/`large_hare`/`red_hare`), while the actual Rabbit — mob id 1, identifier
    // `rabbit`, plus every blue/green/orange/red/magic colour variant — is look 125. Both looks carry a few
    // mob rows named the other animal, so go by mob 1, not by a name scan.
    // wilderness_guise (Barbarian subpath, lvl 99) is RTK's odd one out: real RTK asks a MENU then chains
    // into separate recast()-only sub-spells (wolf_guise/rabbit_guise/deer_guise/sheep_guise/
    // thirsty_ogre_guise) that have no cast() of their own and are never independently castable — folded
    // directly into wilderness_guise's own answer table instead of modeling that indirection.
    // Loaded from data/game-data/Morphs.csv (rows with a non-empty `answers` column, "ans:look;ans:look") in
    // Load() — see LoadMorphs.
    public static IReadOnlyDictionary<string, (Dictionary<string, ushort> Answers, int Mana, int DurationMs)> MorphDispatchSpells { get; private set; } =
        new Dictionary<string, (Dictionary<string, ushort>, int, int)>(StringComparer.OrdinalIgnoreCase);
    public static (Dictionary<string, ushort> Answers, int Mana, int DurationMs)? MorphDispatchFor(SpellDef sp) =>
        MorphDispatchSpells.TryGetValue(sp.Key, out var m) ? m : null;
    public static bool IsMorphSpell(SpellDef sp) => MorphSpells.ContainsKey(sp.Key) || MorphDispatchSpells.ContainsKey(sp.Key);

    // RTK Poet "Call of the Wild" pet-summon family (rtklua/Accepted/Spells/poet/cotw_*.lua): 7 tiers x 4
    // alignment reskins (28 identifiers) + a 29th, cotw_giasomo_bird_poet. That 29th is NOT part of the
    // learnable ladder: it has no requirements(), no Spells.csv row and no learn cost — it is fired only by
    // the Giasomo stick's on_swing proc (see data/game-data/WeaponProcs.csv). Its Lua asks for mob 807,
    // which exists nowhere; RTK's OWN SQL and our mobs.csv both put giasomo_bird at 600, and every other
    // cotw id in that file matches the SQL exactly, so 807 is an isolated typo. It is wired to mob 600
    // here. (The Lua flags itself: "@TODO: I know this doesn't belong here, but the COTW structure is so
    // terrible already".) The base cotw_controller_poet is likewise not a summon — it has no cast() at all,
    // only on_takedamage_while_cast (threat redirect) and uncast (dismiss every owned pet), which is why it
    // is learned at 63 while the first actual creature comes at 68. DELIBERATELY NOT PORTED, either half:
    // 4.95 Call of the Wild creatures leave play ONLY by being killed or by their own timer (there is no
    // dismiss), and the threat side rides RTK's AI/threat.lua aggro table, which is later-server content —
    // see the protocol doc's "RTK's threat table is later-server content". RTK ships the spell disabled
    // anyway: it is the only cotw row in Spells.csv with SplActive=0 (all 14 summons are 1), so LoadSpells
    // skips it. Every
    // tier spawns a real MobDef (all 28 DO exist in mobs.csv, correctly statted) owned by the caster,
    // capped by Content.PetCapFor and expiring 300s later (World.Tick). The top "avatar" tier is the one
    // real outlier: RTK charges GOLD (via requirements(), not mana) plus an 8-minute cooldown instead of the
    // flat 10-mana every other tier uses (cotw_wind_warrior.lua has no `player.magic` check at all).
    // Loaded from data/game-data/Pets.csv in Load() — see LoadPets.
    private static IReadOnlyDictionary<string, (string MobKey, int Level, int Mana, int CooldownMs)> PetSpells =
        new Dictionary<string, (string, int, int, int)>(StringComparer.OrdinalIgnoreCase);
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
    // Loaded from data/game-data/SpellLevels.csv in Load() — see LoadSpellLevels. Assigned BEFORE Spells is
    // loaded (LoadSpells reads it to override SplLevel for Type-5 skills whose export level is 0).
    private static IReadOnlyDictionary<string, int> SpellLevelOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    // ---- Phase-1 spell-DATA loaders (Content.cs literals -> CSV; see re/extract_spell_tables.py) ----------

    private static Dictionary<string, int> LoadSpellLevels(string? path)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadCsv(path))
        {
            var k = c.GetValueOrDefault("key", "").Trim();
            if (k.Length > 0 && int.TryParse(c.GetValueOrDefault("level"), out var lvl)) d[k] = lvl;
        }
        return d;
    }

    private static Dictionary<string, (string MobKey, int Level, int Mana, int CooldownMs)> LoadPets(string? path)
    {
        var d = new Dictionary<string, (string, int, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadCsv(path))
        {
            var k = c.GetValueOrDefault("key", "").Trim();
            if (k.Length == 0) continue;
            int.TryParse(c.GetValueOrDefault("level", "0"), out var lvl);
            int.TryParse(c.GetValueOrDefault("mana", "0"), out var mana);
            int.TryParse(c.GetValueOrDefault("cooldownMs", "0"), out var cd);
            d[k] = (c.GetValueOrDefault("mobKey", "").Trim(), lvl, mana, cd);
        }
        return d;
    }

    // Weapon "on swing" procs (data/game-data/WeaponProcs.csv), ported from RTK's item on_swing handlers
    // in rtklua/Accepted/Items/**. Every one is the same shape: roll chancePct on each swing, then cast a
    // spell at whatever you face — Blood/venom, Charm/endear, Frost sabre/chill, and the Giasomo stick,
    // whose proc summons a bird onto the caster (target=self) rather than hitting the target.
    //
    // The proc spells sit on SplPthId 99 — the shared path, castable by players and mobs alike. RTK files
    // them under Spells/NPCs/, but that folder is "shared", not "monster-only": burn.lua, for one,
    // branches on BL_PC vs BL_MOB. Each now carries a SpellParams row + Lua verb (venom/curse/blind/endear/
    // kamikaze/magic_damage) reproducing its real RTK mechanics, so a proc does what the spell does.
    //
    // `spell` may instead name "builtin:<name>" for the two RTK items that act INLINE in their Lua with no
    // spell behind them at all — shot_gun's ramping cone and viper_stick's 2s paralyze (Session.ProcShotgun /
    // Session.ProcParalyze). `target` is one of:
    //     enemy      cast at the faced creature (the default; every RTK on_swing starts with getTargetFacing)
    //     self       cast on the caster, whether or not anything is faced
    //     self_faced cast on the caster, but ONLY while facing a creature — the Giasomo stick's shape: its
    //                bird lands on YOU, yet RTK still gates the whole handler on getTargetFacing.
    public readonly record struct WeaponProc(string Item, int ChancePct, string Spell, bool SelfCast, bool NeedsFacing);

    private static IReadOnlyDictionary<string, WeaponProc> WeaponProcs =
        new Dictionary<string, WeaponProc>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The on-swing proc for an equipped weapon/armour identifier, if it has one.</summary>
    public static WeaponProc? WeaponProcFor(string? itemKey) =>
        itemKey is not null && WeaponProcs.TryGetValue(itemKey, out var p) ? p : null;

    private static Dictionary<string, WeaponProc> LoadWeaponProcs(string? path)
    {
        var d = new Dictionary<string, WeaponProc>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadCsv(path))
        {
            var item = c.GetValueOrDefault("item", "").Trim();
            var spell = c.GetValueOrDefault("spell", "").Trim();
            if (item.Length == 0 || item.StartsWith('#') || spell.Length == 0) continue;
            if (!int.TryParse(c.GetValueOrDefault("chancePct", "0"), out var pct) || pct <= 0) continue;
            var target = c.GetValueOrDefault("target", "enemy").Trim();
            bool self = target.StartsWith("self", StringComparison.OrdinalIgnoreCase);
            bool needsFacing = !self || target.Equals("self_faced", StringComparison.OrdinalIgnoreCase);
            d[item] = new WeaponProc(item, pct, spell, self, needsFacing);
        }
        return d;
    }

    private static Dictionary<string, (TrapKind Kind, int Level, int Mana)> LoadTrapSpells(string? path)
    {
        var d = new Dictionary<string, (TrapKind, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadCsv(path))
        {
            var k = c.GetValueOrDefault("key", "").Trim();
            if (k.Length == 0 || !Enum.TryParse<TrapKind>(c.GetValueOrDefault("kind", ""), true, out var kind)) continue;
            int.TryParse(c.GetValueOrDefault("level", "0"), out var lvl);
            int.TryParse(c.GetValueOrDefault("mana", "0"), out var mana);
            d[k] = (kind, lvl, mana);
        }
        return d;
    }

    // Morphs.csv holds BOTH fixed morphs (look/lookFemale set, answers empty) and question-dispatch morphs
    // (answers = "ans:look;ans:look", look/lookFemale empty) — split back into the two dicts here.
    private static (Dictionary<string, (ushort Look, ushort LookFemale, int Mana, int DurationMs)> Fixed,
                    Dictionary<string, (Dictionary<string, ushort> Answers, int Mana, int DurationMs)> Dispatch)
        LoadMorphs(string? path)
    {
        var fx = new Dictionary<string, (ushort, ushort, int, int)>(StringComparer.OrdinalIgnoreCase);
        var dp = new Dictionary<string, (Dictionary<string, ushort>, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadCsv(path))
        {
            var k = c.GetValueOrDefault("key", "").Trim();
            if (k.Length == 0) continue;
            int.TryParse(c.GetValueOrDefault("mana", "0"), out var mana);
            int.TryParse(c.GetValueOrDefault("durationMs", "0"), out var dur);
            var answers = c.GetValueOrDefault("answers", "").Trim();
            if (answers.Length > 0)
            {
                var ans = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in answers.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split(':', 2);
                    if (kv.Length == 2 && ushort.TryParse(kv[1].Trim(), out var look)) ans[kv[0].Trim()] = look;
                }
                dp[k] = (ans, mana, dur);
            }
            else
            {
                ushort.TryParse(c.GetValueOrDefault("look", "0"), out var look);
                ushort.TryParse(c.GetValueOrDefault("lookFemale", "0"), out var lookF);
                fx[k] = (look, lookF, mana, dur);
            }
        }
        return (fx, dp);
    }

    // SpellMods.csv: one row per spell, sparse — a `rage` value OR an `enchantAmt`+`enchantMana` pair.
    private static (Dictionary<string, int> Rage, Dictionary<string, (double Amt, int Mana)> Enchant) LoadSpellMods(string? path)
    {
        var rage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ench = new Dictionary<string, (double, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadCsv(path))
        {
            var k = c.GetValueOrDefault("key", "").Trim();
            if (k.Length == 0) continue;
            if (int.TryParse(c.GetValueOrDefault("rage", ""), out var r)) rage[k] = r;
            var ea = c.GetValueOrDefault("enchantAmt", "").Trim();
            if (ea.Length > 0 && double.TryParse(ea, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amt))
            {
                int.TryParse(c.GetValueOrDefault("enchantMana", "0"), out var em);
                ench[k] = (amt, em);
            }
        }
        return (rage, ench);
    }

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
    // SpellText.csv: key -> (targetText apply-line, fadeText expiry-line), both live-canonical. Only spells with
    // a recorded line have a row; a spell may set just one of the two (e.g. Valor has a known fade but not apply).
    private static Dictionary<string, (string, string)> LoadSpellTexts(string? path)
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadCsv(path))
        {
            var k = c.GetValueOrDefault("key", "").Trim();
            if (k.Length == 0) continue;
            var t = c.GetValueOrDefault("targetText", "").Trim();
            var f = c.GetValueOrDefault("fadeText", "").Trim();
            if (t.Length > 0 || f.Length > 0) d[k] = (t, f);
        }
        return d;
    }

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
    //
    // Falls back to the CURRENT DIRECTORY when no marker is found, matching Shared/RepoPaths.Root() and
    // TkListener.RepoDataDir(). It used to return null instead, and the three resolvers disagreeing was a
    // silent-failure trap for any layout without the marker (a `dotnet publish` output directory, say):
    // the character store still found data/nexus.db via ITS cwd fallback, so the server started, listened,
    // and accepted logins — into a world with zero maps, zero mobs and zero NPCs, with nothing in the log
    // that read as an error. Agreeing on the fallback means the whole process either finds the data
    // directory or misses it together, and the startup counts (0 maps) then actually mean what they say.
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
        return Path.Combine(new[] { Directory.GetCurrentDirectory() }.Concat(parts).ToArray());
    }
}
