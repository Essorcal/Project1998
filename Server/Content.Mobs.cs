namespace Server;

/// <summary>A summonable creature definition (name, sprite look, palette colour, HP, reward, move pace).
/// <see cref="MobDef.Aggressive"/> is RTK's <c>MobBehavior</c> (mob.c: 0=Normal/fights-back-only,
/// 1=Aggressive/attacks on sight, 2=Stationary) — we don't model Stationary separately since those are
/// loaded as NPCs (Content.Npcs), not MobDef entries. <see cref="MobDef.MinDam"/>/<see cref="MobDef.MaxDam"/>
/// are RTK's per-mob swing range (SQL <c>MobMinimumDamage</c>/<c>MobMaximumDamage</c>, RTK
/// <c>swingDamage.lua</c> <c>_getMobSwingDamage</c>) — the ACTUAL melee damage a mob deals, unrelated to
/// its Level (Level is only exp/display; a level-99 dragon can carry a MinDam/MaxDam in the thousands).
/// <see cref="MobDef.Hit"/> (SQL <c>MobHit</c>) feeds its crit chance (RTK <c>hitCritChance.lua</c>).
/// <see cref="MobDef.IsBoss"/> (SQL <c>MobIsBoss</c>) selects a player weapon's Large-damage range instead of
/// Small (RTK <c>swingDamage.lua</c> <c>_getPlayerSwingDamage</c>). RTK's mob struct actually carries TWO
/// separate defense stats, both previously treated as 0 for lack of a source column: <see cref="MobDef.Ac"/>
/// (SQL <c>MobArmor</c> — signed, lower-is-better, same convention as <c>Character.Ac</c>) is what reduces
/// an incoming MELEE swing (RTK <c>swingDamage.lua</c>'s <c>target.armor</c>); <see cref="MobDef.Protection"/>
/// (SQL <c>MobProtection</c>) is a DIFFERENT stat that only feeds <see cref="Session.RollDeflect"/>'s magic
/// resist roll (RTK clif.c <c>tprotection</c>) — melee and magic defense do not share a stat in RTK.
/// <see cref="MobDef.Grace"/> (SQL <c>Grace</c>, already in the CSV but previously unparsed like the rest of
/// this list) is read as the DEFENDER's grace in <see cref="Session.PlayerSwingDamage"/>'s crit-chance roll
/// when a player attacks this mob.
/// <para><see cref="MobDef.SpawnTime"/> is RTK <c>Mobs.MobSpawnTime</c>, in seconds: how long a STATIC spawn point stays
/// empty after this creature dies before the engine revives it on its own tile (<c>mob.c</c>:
/// <c>last_death + spawntime &lt;= now</c>). Per creature, not per point — the table runs 9/12/18/24/30/42/60/360
/// with a SQL default of 180, so the Mythic elites on 360 are meant to be a twenty-times-slower refill than a
/// town rat, not the one shared cadence we used to give everything. Merged in by
/// <c>re/merge_mob_spawn_time.py</c>. Nothing to do with the hunting maps, which batch-refill instead
/// (see <see cref="AreaSpawnDef.Timer"/>).</para></summary>
public sealed record MobDef
{
    public required int Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required ushort Look { get; init; }
    public required byte Color { get; init; }
    public required int Hp { get; init; }
    public required int Exp { get; init; }
    public required int Level { get; init; }
    public required int MoveTime { get; init; }
    public int Will { get; init; } = 0;
    public bool Aggressive { get; init; } = false;
    public int MinDam { get; init; } = 1;
    public int MaxDam { get; init; } = 1;
    public bool IsBoss { get; init; } = false;
    public int Protection { get; init; } = 0;
    public int Hit { get; init; } = 0;
    public int Ac { get; init; } = 0;
    public int Grace { get; init; } = 0;
    public bool Flees { get; init; } = false;
    public bool Stationary { get; init; } = false;
    public int SpawnTime { get; init; } = Content.DefaultSpawnTimeSec;
}

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
/// <c>game-data/MobDrops.csv</c>. Keyed by <see cref="MobDef.Key"/> in <see cref="Content.MobDrops"/>.</summary>
public sealed record MobDropDef(LootRoll[] Loot, RareRoll[] Rare);

/// <summary>One item (or gold, when <see cref="Item"/> is null) rolled off a slain mob by
/// <see cref="Content.RollDrops"/>.</summary>
public readonly record struct RolledDrop(ItemDef? Item, int Amount, bool Gold);

/// <summary>A fixed spawn point from the RTK spawn table: a mob id placed on a map tile. The world
/// materializes one live mob per point and, on its death, respawns another after a delay.</summary>
public sealed record SpawnDef(int MobId, ushort Map, ushort X, ushort Y);

/// <summary>An area spawn from RTK's Lua spawner NPC (<c>mobSpawnHandler.lua</c>'s
/// <c>handleSpawn(npc, map, {mobs}, {counts}, timer [,minX,minY,maxX,maxY])</c>): <see cref="Count"/>
/// of <see cref="MobId"/> scattered across a map, optionally within a bounding box. This is where
/// every hunting cave/dungeon (the Mythic zodiac caves, wilderness, etc.) gets its mobs — none of it
/// is in the static <see cref="SpawnDef"/> table. A zero box (all four 0) means "anywhere walkable on
/// the map". Generated by <c>re/extract_lua_spawns.py</c> into <c>game-data/AreaSpawns.csv</c>.</summary>
/// <param name="Timer">Seconds between BATCH refills, and the thing that makes clearing a room mean
/// something. &gt;0 puts this row in the group model: RTK holds one clock per <c>handleSpawn</c> call
/// (<c>spawnTable[map][mobs[1]]</c>) and, once it elapses, tops every mob the call names back up to its
/// count in a single pass — it does not respawn mobs one at a time as they die. So a cleared cave stays
/// cleared for the full timer and then comes back at once. 0 means this row is NOT a batch group and
/// falls back to the per-point model (the trap supplement below).</param>
/// <param name="Group">Which <c>handleSpawn</c> call on this map the row came from. Rows sharing
/// (Map, Group) share one clock and refill together. Meaningless when <paramref name="Timer"/> is 0.</param>
/// <param name="RespawnSec">Only for the trap-spawn supplement (<c>AreaSpawnsTrap.csv</c>), which comes
/// from RTK's separate trap-tile ambush system (<c>trap/mob_spawn.lua</c>) and stays on the per-point
/// model. 0 = respawn on the mob's own <see cref="MobDef.SpawnTime"/>. &gt;0 marks a RARE spawn: the world
/// starts it un-spawned, materializes it at a random time while the map is hunted, and holds it dead for
/// ~RespawnSec (plus jitter) after each kill — see <c>World.NextRespawnTick</c>.</param>
public sealed record AreaSpawnDef(int MobId, ushort Map, int Count, ushort MinX, ushort MinY, ushort MaxX, ushort MaxY, int RespawnSec = 0, int Timer = 0, int Group = 0);

/// <summary>One ambush map's trigger config (<c>game-data/AmbushConfig.csv</c>), already tier-resolved
/// to a concrete map id — the five mythic trap-caves, plus Buya town's rat nest. A hidden <c>ambush</c>
/// trap on this map, when a player steps on it, spawns a burst of mobs — the <see cref="SentryTable"/>
/// burst when the stepper stands at/above the top half
/// (<c>y &lt;= SentryTopY</c>), else the <see cref="BigTable"/> burst on a 1-in-<see cref="BigChance"/> roll,
/// else <see cref="PrimaryKind"/> — shows <see cref="Message"/>, then a replacement trap is placed while live
/// mobs stay under <see cref="MobCap"/>. Mirrors RTK mob_spawn.lua / rabbitTrap.lua / tigerTrap.lua; see
/// <c>World.RefillAmbush</c> / <c>World.FireAmbushLocked</c>. Bosses are NOT here — they stay on the rare
/// spawn-point system (AreaSpawnsTrap rows), which already reproduces their 1/10 + cooldown surprise.</summary>
public sealed class AmbushMapDef
{
    public int Count;            // target hidden traps per map
    public int MobCap;           // stop placing traps once the map holds this many live (non-NPC) mobs
    public string Message = "";
    public string PrimaryKind = "";   // "burst" | "single" | "ogre" | "" (a sentry-only room like the guardroom)
    public string PrimaryTable = "";  // burst-table name when PrimaryKind == "burst"
    public int PrimaryMob;            // mob id when PrimaryKind is "single" or "ogre"
    public int OgreAltMob;            // "ogre" only: a 1-in-OgreAltChance roll spawns this id instead (RTK map 135)
    public int OgreAltChance;
    public string SentryTable = "";   // burst used when the stepper is in the top half (y <= SentryTopY)
    public int SentryTopY;
    public string BigTable = "";      // 1-in-BigChance roll uses this burst instead of Primary (tiger Dark Pen)
    public int BigChance;
}

/// <summary>An NPC placement from our NPC table (<c>game-data/NPCs.csv</c>): a stationary being on a map
/// tile. Nearly all render via the creature path (0x07) exactly like a mob — <c>Look</c>/<c>Color</c> mirror
/// <see cref="MobDef"/> — so the world spawns them as non-fighting mobs. <c>IsChar</c> marks the rare
/// human-composite NPC (0x33). The shop/repair/bank flags select the dialog behaviour on click.
///
/// <para><c>Enabled</c> is the spawn on/off switch — a disabled NPC keeps its row but isn't placed. It is the
/// CSV's <c>Enabled</c> column AND the era verdict on <c>EraFeature</c> folded together, so the one flag every
/// spawn path already checks stays the whole answer to "does this being exist". <c>EraFeature</c> is kept
/// alongside it purely so a reader (<c>@npc</c>) can say WHICH of the two switched him off — the remedies are
/// different, and "edit the Enabled column" is the wrong advice for someone who isn't born yet.</para></summary>
public sealed record NpcDef
{
    public required int Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required ushort Map { get; init; }
    public required ushort X { get; init; }
    public required ushort Y { get; init; }
    public required byte Dir { get; init; }
    public required ushort Look { get; init; }
    public required byte Color { get; init; }
    public required bool IsChar { get; init; }
    public required bool Shop { get; init; }
    public required bool Repair { get; init; }
    public required bool Bank { get; init; }
    public required int MoveTime { get; init; }
    public required int ReturnDistance { get; init; }
    public bool Enabled { get; init; } = true;
    public string EraFeature { get; init; } = "";
}

public static partial class Content
{
    public static IReadOnlyList<MobDef> Mobs
    {
        get => _snapshotBuilder?.Mobs ?? Snapshot.Mobs;
        private set => Builder.Mobs = value;
    }

    /// <summary>Respawn delay for a static spawn whose creature carries no <see cref="MobDef.SpawnTime"/> —
    /// RTK's own <c>Mobs.MobSpawnTime</c> column default, so a mob missing from the dump behaves like a new
    /// row in RTK's table rather than like our old blanket cadence.</summary>
    public const int DefaultSpawnTimeSec = 180;

    // Fixed monster spawn points (game-data/Spawns.csv). One live mob per point; the world respawns it on death.
    public static IReadOnlyList<SpawnDef> Spawns
    {
        get => _snapshotBuilder?.Spawns ?? Snapshot.Spawns;
        private set => Builder.Spawns = value;
    }

    // Area spawns from RTK's Lua spawner (game-data/AreaSpawns.csv): the hunting-map mob populations
    // (Mythic caves, wilderness, dungeons) that the static Spawns table doesn't cover. See AreaSpawnDef.
    public static IReadOnlyList<AreaSpawnDef> AreaSpawns
    {
        get => _snapshotBuilder?.AreaSpawns ?? Snapshot.AreaSpawns;
        private set => Builder.AreaSpawns = value;
    }

    // Stationary NPCs (game-data/NPCs.csv), placed once by the world as non-fighting mobs. Keyed by NpcId for
    // click-time dialog lookup.
    public static IReadOnlyList<NpcDef> Npcs
    {
        get => _snapshotBuilder?.Npcs ?? Snapshot.Npcs;
        private set => Builder.Npcs = value;
    }
    private static IReadOnlyDictionary<int, NpcDef> NpcByIdIndex
    {
        get => _snapshotBuilder?.NpcById ?? Snapshot.NpcById;
        set => Builder.NpcById = value;
    }
    public static NpcDef? NpcById(int id) => NpcByIdIndex.TryGetValue(id, out var n) ? n : null;
    private static IReadOnlyDictionary<int, MobDef> MobByIdIndex
    {
        get => _snapshotBuilder?.MobById ?? Snapshot.MobById;
        set => Builder.MobById = value;
    }
    private static IReadOnlyDictionary<string, MobDef> MobByKeyIndex
    {
        get => _snapshotBuilder?.MobByKey ?? Snapshot.MobByKey;
        set => Builder.MobByKey = value;
    }

    // NPC identifier -> its buy stock (item keys), auto-extracted from the RTK NPC scripts
    // (re/extract_shops.py -> ShopStock.csv). A fallback behind the curated Shops.cs catalogues, so every
    // shop-flagged NPC has something to sell without hand-authoring each. See Shops.For.
    public static IReadOnlyDictionary<string, string[]> ShopStock
    {
        get => _snapshotBuilder?.ShopStock ?? Snapshot.ShopStock;
        private set => Builder.ShopStock = value;
    }

    // NPC identifier -> what it will BUY FROM the player (item keys), auto-extracted from the same RTK NPC
    // scripts (re/extract_shop_sell.py -> ShopBuysFrom.csv). A SEPARATE list from ShopStock: RTK's shops sell
    // a short catalogue but buy a longer one (the butcher stocks 6 items and buys 22), which is why this
    // can't be derived from the stock list. Before it existed every shop-flagged NPC bought anything with a
    // sell price, so the butcher would take your platemail. See Shops.BuysFrom.
    //
    // Two deliberate imprecisions, both erring towards accepting rather than refusing a sale:
    //   • RTK gates a few extras on the shop's MAP (Lien's butcher also buys tiger cuts and dragon's liver);
    //     those are folded into the one list rather than modelled per-map, so any butcher takes them.
    //   • An NPC with no row here still buys anything sellable, exactly as before — shops whose Lua list
    //     can't be read statically (or that RTK has no script for) keep working instead of refusing everything.
    public static IReadOnlyDictionary<string, string[]> ShopBuysFrom
    {
        get => _snapshotBuilder?.ShopBuysFrom ?? Snapshot.ShopBuysFrom;
        private set => Builder.ShopBuysFrom = value;
    }

    // 0x07 colour-byte remap for the 5.33 client ONLY, keyed (Look, Colour). The colour byte is a RAMP
    // SHIFT the client applies to the mob's own base palette block (sprite indices >= 0x30 read
    // palette[(i + 8*colour) & 0xFF]), and on 5.33 colour>>5 >= 1 swaps the block for SUPER{n}.PAL —
    // palettes the era/4.x clients don't have (their 8-bit add just wraps, so era colour >= 32 meant
    // ramp colour-32). mobs.csv MobLookColor is era-tuned, so (look, colour >= 32) pairs render wrong
    // SUPER hues on 5.33 unless remapped here. Populated from Mob5xPalettes.csv (header has the full
    // derivation; Sources.csv binary-re-533 the RE). See Palette5x and Session.SendCreatureList.
    public static IReadOnlyDictionary<(ushort Look, byte Colour), byte> Mob5xPalettes
    {
        get => _snapshotBuilder?.Mob5xPalettes ?? Snapshot.Mob5xPalettes;
        private set => Builder.Mob5xPalettes = value;
    }

    /// <summary>The colour byte to send a V533 client for <paramref name="look"/>, given the colour the
    /// 4.95 path would use. Returns the 5.33 remap when one exists for this (look, colour) pair, else
    /// the unchanged colour.</summary>
    public static byte Palette5x(ushort look, byte colour) =>
        Mob5xPalettes.TryGetValue((look, colour), out var p) ? p : colour;

    // Mob floor-loot tables (RTK Mobs/MobDrops.lua -> re/extract_mob_drops.py -> MobDrops.csv). Keyed by
    // MobDef.Key; a mob with no entry here drops nothing, matching RTK (no _mobDropsTable entry = no loot).
    public static IReadOnlyDictionary<string, MobDropDef> MobDrops
    {
        get => _snapshotBuilder?.MobDrops ?? Snapshot.MobDrops;
        private set => Builder.MobDrops = value;
    }

    /// <summary>One gathering node (wheat/ore/tree) — see game-data/HarvestNodes.csv for the column meanings
    /// and Server/Session.Harvest.cs for the loop. <see cref="Yield"/> and <see cref="Bonus"/> are weighted
    /// tables: Yield's weights are relative (out of their own sum, so one always drops), Bonus's are absolute
    /// percentages whose remainder is "nothing".</summary>
    public sealed record HarvestNodeDef(string NodeMob, string[] Tools, string Skill,
        (string Item, double Weight)[] Yield, int Rolls, (string Item, double Percent)[] Bonus,
        int[] BreakChance, string Message)
    {
        /// <summary>Index of <paramref name="toolKey"/> in <see cref="Tools"/>, or -1 if this node doesn't
        /// take that tool.</summary>
        public int ToolIndex(string toolKey) =>
            System.Array.FindIndex(Tools, t => t.Equals(toolKey, StringComparison.OrdinalIgnoreCase));

        /// <summary>Break chance for one tool: its own column entry, else the single shared value, else 0
        /// (never breaks).</summary>
        public int BreakChanceFor(int toolIndex) =>
            BreakChance.Length == 0 ? 0 : BreakChance[Math.Min(Math.Max(toolIndex, 0), BreakChance.Length - 1)];
    }

    /// <summary>Gathering nodes by mob identifier. Empty = no node is harvestable, which is exactly how the
    /// world behaved before this existed (the wheat in Kugnae's field was an inert 1200-HP shrub).</summary>
    public static IReadOnlyDictionary<string, HarvestNodeDef> HarvestNodes
    {
        get => _snapshotBuilder?.HarvestNodes ?? Snapshot.HarvestNodes;
        private set => Builder.HarvestNodes = value;
    }

    /// <summary>One spell a creature can throw at whoever it is fighting (RTK's <c>peck.cast(mob, target)</c>
    /// family — its spell scripts take a caster "block" that may be a mob as easily as a player). See
    /// game-data/MobSpells.csv for the columns and Server/Session.MobSpells.cs for the cast.</summary>
    /// <param name="PerTick">For a <c>poison</c> row: damage per DoT tick, flat. Set this instead of
    /// <paramref name="Amount"/> when the creature's venom is described per TICK rather than per second —
    /// which is the only reading that means anything once <paramref name="TickMinMs"/> makes the gap
    /// between ticks vary. 0 = fall back to <paramref name="Amount"/>-as-rate.</param>
    /// <param name="TickMinMs">For a <c>poison</c> row: shortest gap between DoT ticks. 0 = the fixed
    /// <see cref="World.PoisonTickMs"/> beat every other venom uses.</param>
    /// <param name="TickMaxMs">Longest gap between DoT ticks. The gap is drawn in whole seconds from
    /// [TickMinMs, TickMaxMs] each tick.</param>
    /// <param name="Trigger">WHEN the row is rolled. Blank/<c>timer</c> is the original behaviour: World.Tick
    /// rolls it against <paramref name="Chance"/> once the creature is off its <paramref name="EveryMs"/>
    /// cooldown, at <paramref name="Range"/>. <c>onhit</c> instead rolls it on a LANDED melee blow
    /// (Session.TryMobOnHitSpell), which is the only shape that makes <paramref name="Chance"/> mean
    /// "one swing in N" — on the timer path the roll repeats every tick until it passes, so Chance only
    /// shifts WHEN the cast lands, never whether it does. An <c>onhit</c> row ignores both
    /// <paramref name="EveryMs"/> and <paramref name="Range"/> (a landed swing is already adjacent, and the
    /// swing cadence is the cooldown).</param>
    public sealed record MobSpellDef(string MobKey, string Name, string Effect, int Chance, int EveryMs,
        int Range, int Amount, string Stat, string Category, int DurationMs, int Anim, int Sound, string Say,
        int PerTick = 0, int TickMinMs = 0, int TickMaxMs = 0, string Trigger = "")
    {
        /// <summary>Rolled on a landed melee blow rather than on the cast timer. See <see cref="Trigger"/>.</summary>
        public bool OnHit => Trigger == "onhit";

        /// <summary>The shout for ONE cast. <see cref="Say"/> may hold several alternatives separated by
        /// <c>|</c> — the same convention MobChatter.csv's <c>Lines</c> uses — in which case one is picked at
        /// random per cast, so a caster with more than one line for a spell doesn't repeat itself. A plain
        /// string (every row that predates this) has exactly one alternative and returns unchanged.</summary>
        public string PickSay()
        {
            if (Say.Length == 0) return "";
            if (!Say.Contains('|')) return Say;
            var alts = Say.Split('|', StringSplitOptions.RemoveEmptyEntries);
            return alts.Length == 0 ? "" : alts[Random.Shared.Next(alts.Length)];
        }
    }

    /// <summary>Creature spell repertoires by mob identifier, in the order the CSV lists them.</summary>
    public static IReadOnlyDictionary<string, MobSpellDef[]> MobSpells
    {
        get => _snapshotBuilder?.MobSpells ?? Snapshot.MobSpells;
        private set => Builder.MobSpells = value;
    }

    /// <summary>Idle flavour lines (RTK's <c>if math.random(1,100) == 1 then mob:talk(…)</c>, which is all
    /// most "custom AI" scripts actually are). Chance is 1-in-N per move tick.</summary>
    public sealed record MobChatterDef(string MobKey, int Chance, byte Channel, string[] Lines);

    public static IReadOnlyDictionary<string, MobChatterDef> MobChatter
    {
        get => _snapshotBuilder?.MobChatter ?? Snapshot.MobChatter;
        private set => Builder.MobChatter = value;
    }

    /// <summary>What happens when a creature spawns (RTK's <c>on_spawn</c> hooks, which are placement and
    /// population rules rather than behaviour — see game-data/MobSpawnRules.csv).</summary>
    /// <summary>Per-creature spawn and behaviour rules that RTK expresses as script rather than table.
    /// <paramref name="SpawnChance"/> is a 1-in-N roll each time the spawn point tries to fire (RTK's trap
    /// spawner: <c>local chance = math.random(1,10); if chance == 1 then</c>), and
    /// <paramref name="DeathCooldownSec"/> is the floor between one being killed and the next being allowed
    /// (its <c>lastDeath</c> map registry). Together with <paramref name="MaxAlive"/> they are what makes a
    /// creature like Citelam a find rather than a fixture.</summary>
    public sealed record MobSpawnRuleDef(string MobKey, (ushort Map, ushort X, ushort Y)[] Rooms,
        int MaxAlive, ushort[] CapMaps, int FleeBelowPct = 0, int SpawnChance = 0, int DeathCooldownSec = 0);

    public static IReadOnlyDictionary<string, MobSpawnRuleDef> MobSpawnRules
    {
        get => _snapshotBuilder?.MobSpawnRules ?? Snapshot.MobSpawnRules;
        private set => Builder.MobSpawnRules = value;
    }

    /// <summary>Is the global spawn HP jitter on (the <c>*</c> row)? RTK's mob_on_spawn.lua is the default
    /// hook for every creature without its own, and it does exactly one thing: vary max HP.</summary>
    public static bool MobHpJitter
    {
        get => _snapshotBuilder?.MobHpJitter ?? Snapshot.MobHpJitter;
        private set => Builder.MobHpJitter = value;
    }

    /// <summary>A mythic boss's survival kit (RTK mob_ai_mythic): it can shrug off a killing blow, break its
    /// own paralysis, and regenerate while its Last Stand runs. See game-data/MobBosses.csv.</summary>
    public sealed record MobBossDef(string MobKey, int HealAmount, int HealChance, int ParaBreakChance,
        int LastStandMs, int Anim, int Sound);

    public static IReadOnlyDictionary<string, MobBossDef> MobBosses
    {
        get => _snapshotBuilder?.MobBosses ?? Snapshot.MobBosses;
        private set => Builder.MobBosses = value;
    }

    // Ambush-trap system (game-data/AmbushBursts.csv + AmbushConfig.csv): RTK's hidden MobSpawnNpc tiles in the
    // mythic caves (mob_spawn.lua + rabbitTrap.lua + tigerTrap.lua). AmbushBursts maps a burst-table name to
    // its exact weighted variant lists (extractor-generated, re/extract_ambush_tables.py); Ambushes maps a
    // (tier-resolved) cave map to its trigger config. Warrior Watchful Eye reveals these traps. See
    // World.RefillAmbush / World.FireAmbushLocked and Session's spot-traps reveal fork.
    public static IReadOnlyDictionary<string, IReadOnlyList<int[]>> AmbushBursts
    {
        get => _snapshotBuilder?.AmbushBursts ?? Snapshot.AmbushBursts;
        private set => Builder.AmbushBursts = value;
    }
    public static IReadOnlyDictionary<ushort, AmbushMapDef> Ambushes
    {
        get => _snapshotBuilder?.Ambushes ?? Snapshot.Ambushes;
        private set => Builder.Ambushes = value;
    }

    // Curated shop catalogues (game-data/ShopCatalogues.csv) — hand-authored, ORDERED sub-category buy
    // menus (e.g. SmithNpc's armor menus) that the auto-extracted flat ShopStock can't represent. Keyed by
    // NpcDef.Key; consulted first by Shops.For, else it falls back to ShopStock. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, IReadOnlyList<(string Name, string[] Keys)>> ShopCatalogues
    {
        get => _snapshotBuilder?.ShopCatalogues ?? Snapshot.ShopCatalogues;
        private set => Builder.ShopCatalogues = value;
    }

    // NPC composition (game-data/NpcAbilities.csv): NpcKey -> the ability NAMES it's built from (a
    // pipe-list). NpcScripts.For resolves each name to its C# INpcAbility instance (NpcScripts.AbilityByName).
    // The "which abilities" is data; the ability code stays code. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, string[]> NpcCompositions
    {
        get => _snapshotBuilder?.NpcCompositions ?? Snapshot.NpcCompositions;
        private set => Builder.NpcCompositions = value;
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

    public static List<MobDef> SearchMobs(string query, int limit) =>
        RankByName(Mobs, query, m => m.Name).Take(limit).ToList();

    public static MobDef? MobById(int id) => MobByIdIndex.TryGetValue(id, out var v) ? v : null;
    public static MobDef? MobByKey(string? key) => key is not null && MobByKeyIndex.TryGetValue(key, out var v) ? v : null;

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

    // Prey creatures — see LoadMobFlees / MobDef.Flees. Loaded BEFORE Mobs so LoadMobs can fold the flag in.
    private static Dictionary<string, bool> MobFleeOverrides
    {
        get => _snapshotBuilder?.MobFleeOverrides ?? Snapshot.MobFleeOverrides;
        set => Builder.MobFleeOverrides = value;
    }

    private static Dictionary<string, bool> MobStationaryOverrides
    {
        get => _snapshotBuilder?.MobStationaryOverrides ?? Snapshot.MobStationaryOverrides;
        set => Builder.MobStationaryOverrides = value;
    }

    // Spawns dropped as NOT CLASSIC: content whose only purpose is a questline we don't (and won't yet) model,
    // left standing would just be a mute, purposeless mob wandering the map — worse than not spawning at all.
    // 729 spy_hwan "Hwan" (Buya 330 @38,99): captive NPC for the Spy subpath's interrogation storyline
    // (NPCs/subpaths/spy/hwan.lua) — the whole player-subpath system is unbuilt, so he can never be interacted
    // with as designed. Revisit if/when subpaths are ported.
    private static readonly HashSet<int> ExcludedSpawnMobIds = new() { 729 };
}
