using Shared;

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
/// <param name="SpawnTime">RTK <c>Mobs.MobSpawnTime</c>, seconds: how long a STATIC spawn point stays
/// empty after this creature dies before the engine revives it on its own tile (<c>mob.c</c>:
/// <c>last_death + spawntime &lt;= now</c>). Per creature, not per point — the table runs 9/12/18/24/30/42/60/360
/// with a SQL default of 180, so the Mythic elites on 360 are meant to be a twenty-times-slower refill than a
/// town rat, not the one shared cadence we used to give everything. Merged in by
/// <c>re/merge_mob_spawn_time.py</c>. Nothing to do with the hunting maps, which batch-refill instead
/// (see <see cref="AreaSpawnDef.Timer"/>).</param>
public sealed record MobDef(int Id, string Key, string Name, ushort Look, byte Color, int Hp, int Exp, int Level, int MoveTime, int Will = 0, bool Aggressive = false, int MinDam = 1, int MaxDam = 1, bool IsBoss = false, int Protection = 0, int Hit = 0, int Ac = 0, int Grace = 0, bool Flees = false, bool Stationary = false, int SpawnTime = Content.DefaultSpawnTimeSec);

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
public sealed record NpcDef(
    int Id, string Key, string Name, ushort Map, ushort X, ushort Y, byte Dir,
    ushort Look, byte Color, bool IsChar, bool Shop, bool Repair, bool Bank,
    int MoveTime, int ReturnDistance, bool Enabled = true, string EraFeature = "");

/// <summary>
/// An item definition from the RTK item db (Items.csv). Field names mirror the client's item_data
/// (see RTK itemdb.h). <c>Icon</c> is the inventory-window / ground (Item.epf) frame; <c>Look</c> is the
/// worn-appearance sprite. <c>Type</c> is ITM_* (0=eat,1=use,2=smoke,3=weap,4=armor,5=shield,6=helm,
/// 7=left,8=right,9=subleft,10=subright,11=faceacc,12=crown,13=mantle,14=necklace,15=boots,16=coat,
/// 18=etc/junk…). Stat lines feed the equip bonuses.
/// <para><b><c>Armor</c> is an AC DELTA, signed lower-is-better</b> — the same units as
/// <see cref="Character.Ac"/>, mobs' <c>MobArmor</c> and SpellParams.csv's <c>armor</c> buff stat. Damage
/// taken is <c>raw x (1 + ac/100)</c>, so MORE AC = MORE DAMAGE: a spring garb is -4 (protective) and a
/// wedding dress is +30 (a real penalty). It just ADDS to the wearer's AC — nothing negates it anywhere.</para>
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
    int PathId = 0, int Mark = 0, bool BreakOnDeath = false, bool Protected = false,
    // ItmRepairable: 1 = a smith or a repair spell can restore its durability, 0 = it can never be mended.
    // POSITIVE, unlike the restriction flags beside it (ItmDroppable/ItmExchangeable/ItmDepositable all mean
    // NOT-x when set) — RTK reads it both ways round and agrees with itself: the smith's single-item gate is
    // `if choice.repairable == 0 then "Sorry, but this item cannot be repaired!"` (player.lua:1549) and the
    // repair-everything pass is `if X.repairable == 1 then <quote a cost> else "<name> is not a repairable
    // item."` (player.lua:1622). 605 of the 1241 equip rows are 0, but 482 of those are ItmIndestructible as
    // well (nothing to repair on gear that never wears), leaving ~123 that really do decay permanently —
    // the totem helms, the smith-forged subpath weapons, the headbands and the gauntlets.
    bool Repairable = true,
    // ItmExchangeable / ItmDepositable — ItmDroppable's two sibling restriction flags, same inverted sense
    // (set = you CAN'T). NoTrade refuses the exchange window (RTK clif_exchange_additem: "You cannot exchange
    // that."); NoDeposit refuses bank storage (RTK player.lua depositNoConfirm: "You cannot deposit that
    // item."). 42 / 23 registry rows set them — mostly one-shot quest tokens like this file's namesake keys.
    bool NoTrade = false, bool NoDeposit = false)
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

    /// <summary>Owner-bound gear: it binds to whoever first obtains one, only that owner may equip it, and the
    /// examine tooltip names them (<see cref="InvItem.Owner"/>). NOT the same as <see cref="NoDrop"/> — a bound
    /// item still drops and trades freely, it just stays bound wherever it goes (which is why the owner rides
    /// on the ground item) — and NOT the same as <see cref="Unrepairable"/> or <see cref="BreakOnDeath"/>, each
    /// of which is its own column in the registry. <see cref="BondedItemIds"/> says where the set comes
    /// from.</summary>
    public bool Bonded => BondedItemIds.Contains(Id);

    /// <summary>Gear no smith and no repair spell can restore — straight off <c>ItmRepairable</c>, and only
    /// meaningful for equipment (a consumable has no durability to mend). It cuts across <see cref="Bonded"/>
    /// in both directions: the Frost sabre and the whole armory shield ladder are bound AND repairable ("when
    /// it is worn, it can be repaired with ease" — blood.lua), while the headbands and gauntlets are
    /// unrepairable and bind to nobody. The Atlas prints the same two words side by side on the totem helms —
    /// "Bonded / Unrepairable" — which is what a genuine overlap looks like.</summary>
    public bool Unrepairable => IsEquip && !Repairable;

    /// <summary>The items that arrive already bound to whoever obtains them. Sourced from the Nexus Atlas's
    /// per-item <b>Special Info</b> field, which is exactly this three-flag vocabulary — its own index page
    /// advertises "bonded, break on death, unrepairable and much more" — mined out of the local mirror
    /// (<c>scraped_nexus_data/artifacts/nexus_atlas_site/mirror/{weapons,armor,items}*/</c>) and matched to
    /// the registry by display name. 521 rows matched; the Atlas and the CSV columns agree on break-on-death
    /// for 502 of them and on repairability for 505, which is what earns the field the casting vote on the
    /// one flag the CSV does NOT carry. 116 of the 120 ids below are the Atlas saying "Bonded" outright.
    /// Regenerate with <c>python re/atlas_special_info.py --ids</c>, which also prints the disagreements.
    ///
    /// <para>Bonding is not derivable from any column, because in the original it is a property of the GRANT:
    /// RTK's <c>player:addItem(key, n, dura, ownerId)</c> binds only when the calling script passes
    /// <c>player.ID</c>, which the NPCs that forge something FOR you do and nothing else does. That is why the
    /// same registry row can be bound in one bag and loose in another, and why this is a list.</para>
    ///
    /// <para><b>The three flags overlap, and they overlap unevenly.</b> The subpath weapon families are the
    /// clearest case: the base tier (Spike, Blood, Surge, Charm) is <c>Break on Death</c> and nothing more —
    /// a boss drop and a Carnage prize, and Gan sells Spike over a counter — while the Enchanted and san tiers
    /// an NPC upgrades for you read <c>Bonded / Break on Death</c>. Fourteen ids here are both. Enchanted charm
    /// (49044) is the one the Atlas goes out of its way to mark <c>Non-Bonded</c>, so it is left out.</para>
    ///
    /// <para><b>Not here, deliberately — the bond is per-INSTANCE, not per-item</b> (user, 2026-08-22): the
    /// Giasomo stick (118), Frozen spear (119) and Student cap (1005) are plain <c>Break on Death</c> rows.
    /// Their bonded copies are a different instance of the same id — "Bonded, non-break on death ones can be
    /// bought for 400,000 coins at the Arctic Smith by saying Laptev" — and RTK produces exactly that by
    /// stamping an owner at the grant (<c>smith.lua</c>'s Laptev branch, <c>museum_caretaker.lua</c>). Neither
    /// NPC is wired here yet; when they are, they pass <c>owner:</c> to <see cref="Session.GivePlaced"/> rather
    /// than joining this list. Faerie light (124) is out too — the Atlas marks it Non-Bonded.</para>
    ///
    /// <para>Four ids are kept on RTK evidence alone because no Atlas page covers them: the White moon axe
    /// (rogue trainer / guild shaman) and the Mage's, Conjurer's and Master's wards, whose ladder-mates all
    /// read "Bonded".</para></summary>
    private static readonly HashSet<int> BondedItemIds = BuildBondedItemIds();
    private static HashSet<int> BuildBondedItemIds()
    {
        var s = new HashSet<int>
        {
            1004,                                              // frost_sabre — "required to complete Staff of the Element Quest"
            26028, 26030,                                      // ice_shard, perseverance
            26034, 26035, 26036, 26037, 26038,                 // the five geomancer element orbs
            26048, 26049, 26050, 26051,                        // war/battle amulet + rune       — Nagnang shield quest
            26052, 26053, 26054, 26055,                        // magic/love amulet + rune
            29011,                                             // star_sword — Bonded AND break-on-death
            30008, 30009, 30010, 31007, 31008, 31009,          // Star/Moon/Sun armor: the trainers' quest chain,
            32007, 32008, 32009, 33007, 33008, 33009,          // per class and per sex. Bonded, and sellable
            34007, 34008, 34009, 35007, 35008, 35009,          // only at Sya's shop in KaMing's encampment.
            36007, 36008, 36009, 37007, 37008, 37009,
            40901, 40902, 40903, 40904,                        // Wind armor set (Min, "captured the wind")
            40905, 40906, 40907, 40908,
            41008, 41009, 41010, 41011,                        // totem helm   (male)   — Bonded / Unrepairable.
            41508, 41509, 41510, 41511,                        // totem helmet (female)  The circlets and casques
                                                               // read "None" and are NOT bonded.
            47002,                                             // white_moon_axe        — corroborated 2026-08-23:
                                                               // Rogue Moon step 3 IS the bonding ("display to me
                                                               // your White Moon Axe… he will bond it to you"), on
                                                               // both tswolf and Atlas. See ArmorQuest.cs.
            48018,                                             // fates_blade           — Bonded / Non-Repairable
            49026, 49027, 49028, 49029,                        // spike tiers           ) the Enchanted and san
            49032, 49033, 49034, 49035,                        // blood tiers           ) tiers BonHwaAbility
            49038, 49039, 49040, 49041, 49042,                 // surge tiers           ) upgrades for you: every
            49045, 49046, 49047, 49048,                        // charm tiers           ) one is ALSO ItmBoD.
            // 49027-49029 (Il/Ee/Sam san spike) and 49035 (Sam san blood) were absent from the Atlas-derived
            // set above — the Atlas has no page for them — but Bon-Hwa forges them bonded exactly like their
            // already-listed ladder-mates, so they belong here. Enchanted charm (49044) stays OUT: it is the
            // one tier the Atlas marks Non-Bonded, and that decision outranks RTK's uniform addItem(player.ID).
            51002, 51003, 51004, 51005, 51006, 51007, 51008,   // warrior shield ladder — the armory smiths,
            51009, 51010, 51011, 51012, 51013, 51014,          // rogue buckler ladder    "your own Stone shield"
            51015, 51016, 51017, 51018, 51019, 51020,          // mage ward ladder
            51021, 51022, 51023, 51024, 51025, 51026,          // poet charm ladder
        };
        // The smith's own subpath forge, smith.lua classes 6-9 (Chung ryong scale / Nimble blade / Ju jak staff
        // / Life lance, base + enchanted + Il/Ee/Sam/Sa-san). The Atlas confirms all 24: "Bonded / Unrepairable,
        // NPC Subpath <tier> Members". Note where the range stops -- 49025 onward is a different animal.
        for (int id = 49001; id <= 49024; id++) s.Add(id);
        return s;
    }
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
// out of C# into the data-driven verb/row Lua system, exactly like spells: game-data/ItemParams.csv is
// the "row" (each consumable's verb + numeric params), game-data/item_verbs.lua is the "verb" (the
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
/// <c>Mark</c> is the SUBPATH RANK required (SplMark: 0 = the base 1-99 class list, 1 = Il san, 2 = Ee san,
/// 3 = Sam san) — 121 of the 906 rows carry one and they all have <c>SplLevel</c> 0, which used to make them
/// look like level-1 spells and land in every level-99 character's book alongside the base list. See
/// <see cref="Content.MarkSpellLevel"/>.
/// </summary>
public sealed record SpellDef(int Id, string Key, string Name, byte Type, int PathId, int Level, int Alignment, string Question, bool CanFail = false, int Mark = 0)
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
    int PcAlign, string CureCat = "", string Class = "", int Action = 0);

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
        catch (Exception e) { Log.Warn($"formula '{expr}' failed to evaluate — treated as 0", e); return 0; }
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
    private static readonly SemaphoreSlim ReloadGate = new(1, 1);

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

    // Per-spell TARGET flavor line (game-data/SpellText.csv), CANONICAL from LIVE NexusTK — supersedes RTK.
    // The caster always just sees "You cast <name>." (Session.HandleCast); the TARGET of a spell additionally
    // sees this line when present. On a self-cast you are both, so you see the flavor THEN the cast line.
    public static IReadOnlyDictionary<string, (string Target, string Fade)> SpellTexts { get; private set; } =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
    /// <summary>The live flavor shown to the TARGET when a spell is applied, or "" if none is recorded.</summary>
    public static string TargetTextFor(string key) => SpellTexts.TryGetValue(key, out var t) ? t.Target : "";
    /// <summary>The live flavor shown when a timed buff FADES (RTK uncast), or "" if none is recorded.</summary>
    public static string FadeTextFor(string key) => SpellTexts.TryGetValue(key, out var t) ? t.Fade : "";

    /// <summary>Respawn delay for a static spawn whose creature carries no <see cref="MobDef.SpawnTime"/> —
    /// RTK's own <c>Mobs.MobSpawnTime</c> column default, so a mob missing from the dump behaves like a new
    /// row in RTK's table rather than like our old blanket cadence.</summary>
    public const int DefaultSpawnTimeSec = 180;

    // Fixed monster spawn points (game-data/Spawns.csv). One live mob per point; the world respawns it on death.
    public static IReadOnlyList<SpawnDef> Spawns { get; private set; } = new List<SpawnDef>();

    // Area spawns from RTK's Lua spawner (game-data/AreaSpawns.csv): the hunting-map mob populations
    // (Mythic caves, wilderness, dungeons) that the static Spawns table doesn't cover. See AreaSpawnDef.
    public static IReadOnlyList<AreaSpawnDef> AreaSpawns { get; private set; } = new List<AreaSpawnDef>();

    // Stationary NPCs (game-data/NPCs.csv), placed once by the world as non-fighting mobs. Keyed by NpcId for
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
    public static IReadOnlyDictionary<string, string[]> ShopBuysFrom { get; private set; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    // 0x07 colour-byte remap for the 5.33 client ONLY, keyed (Look, Colour). The colour byte is a RAMP
    // SHIFT the client applies to the mob's own base palette block (sprite indices >= 0x30 read
    // palette[(i + 8*colour) & 0xFF]), and on 5.33 colour>>5 >= 1 swaps the block for SUPER{n}.PAL —
    // palettes the era/4.x clients don't have (their 8-bit add just wraps, so era colour >= 32 meant
    // ramp colour-32). mobs.csv MobLookColor is era-tuned, so (look, colour >= 32) pairs render wrong
    // SUPER hues on 5.33 unless remapped here. Populated from Mob5xPalettes.csv (header has the full
    // derivation; Sources.csv binary-re-533 the RE). See Palette5x and Session.SendCreatureList.
    public static IReadOnlyDictionary<(ushort Look, byte Colour), byte> Mob5xPalettes { get; private set; } =
        new Dictionary<(ushort, byte), byte>();

    /// <summary>The colour byte to send a V533 client for <paramref name="look"/>, given the colour the
    /// 4.95 path would use. Returns the 5.33 remap when one exists for this (look, colour) pair, else
    /// the unchanged colour.</summary>
    public static byte Palette5x(ushort look, byte colour) =>
        Mob5xPalettes.TryGetValue((look, colour), out var p) ? p : colour;

    // Armor-dye ramp remap, keyed (bodyLook, canonicalDye) -> the ramp to actually send in appearance[4].
    // The PLAYER equivalent of Mob5xPalettes above, and it exists for the same reason: appearance[4] is a
    // ramp shift resolved against the body sprite's OWN Body.tbl palette, so one canonical number is a
    // different hue on different armor. Only pairs that disagree with palette 0 are stored; everything
    // else passes through. Populated from ArmorDyeRamps.csv, which carries the full derivation. See Session.ArmorDye().
    public static IReadOnlyDictionary<(ushort Look, byte Dye), byte> ArmorDyeRamps { get; private set; } =
        new Dictionary<(ushort, byte), byte>();

    /// <summary>The byte to put in <c>appearance[4]</c> so <paramref name="dye"/> renders as its canonical
    /// colour on the body sprite <paramref name="look"/> is wearing. Identity unless ArmorDyeRamps.csv says
    /// this body's palette disagrees with the seasonal one.</summary>
    public static byte DyeRampFor(ushort look, byte dye) =>
        ArmorDyeRamps.TryGetValue((look, dye), out var r) ? r : dye;

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
    public sealed record MapMetaInfo(int Region, bool WarpOut, bool Pvp, bool CanTalk, bool CanCast, int ReqLvl, int ReqPath, int ReqMark,
        long ReqVita, long ReqMana, int LvlMax, long VitaMax, long ManaMax, string RejectMsg, bool Indoor);

    public static IReadOnlyDictionary<ushort, MapMetaInfo> MapMeta { get; private set; } =
        new Dictionary<ushort, MapMetaInfo>();

    // Mob floor-loot tables (RTK Mobs/MobDrops.lua -> re/extract_mob_drops.py -> MobDrops.csv). Keyed by
    // MobDef.Key; a mob with no entry here drops nothing, matching RTK (no _mobDropsTable entry = no loot).
    public static IReadOnlyDictionary<string, MobDropDef> MobDrops { get; private set; } =
        new Dictionary<string, MobDropDef>();

    // Era-gating overrides for crafting skills (see Server/CraftingToggles.cs + docs/common/Crafting-Values.md).
    // File is optional and sparse: only skills listed here override CraftingToggles.DefaultDisabled;
    // anything absent keeps the code-level default. Columns: Skill,Enabled(0/1).
    public static IReadOnlyDictionary<string, bool> CraftingToggleOverrides { get; private set; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // Era-gated content (game-data/EraFeatures.csv + the EraDate scalar) is NOT loaded here: it lives in
    // Shared.EraCalendar, because the LOGIN server needs the same calendar to place new characters. Load()
    // below calls EraCalendar.Reload() so @reload still picks up edits. See Server/Era.cs.


    // ---- Mythic Nexus zodiac cave entrances (game-data/MythicCaves.csv) ------------------------------
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

    // ---- Mythic alliances (game-data/MythicAlliances.csv) -------------------------------------------
    // One row per zodiac animal, describing its OWN cave: its enemy, its two sets of bosses, and the
    // tribute an ally of its enemy must steal from it. Consumed by Server/MythicAlliance.cs, which reads a
    // quest off the ENEMY's row. An empty file simply means no mythic answers to anything, the same
    // fail-soft posture as every other table here.
    public static IReadOnlyList<MythicAllianceDef> MythicAlliances { get; private set; } = new List<MythicAllianceDef>();

    // ---- Tiered "event cave" entrances (game-data/EventCaves.csv + EventCaveTiers.csv) --------------
    // A doorway that reads the character and drops them into one of FIVE parallel copies of the same
    // dungeon. RTK keeps the ladder in one shared place (Player.getEventCaveLevel / eventCaveLevelPrompt in
    // rtklua/Accepted/player.lua) and calls it from each entrance; so do we. The Buya Library Caverns
    // doorway is the only entrance wired today. Consumed by Session.TryEventCaveEntrance.
    //
    // The LADDER is a list of disjoint bands matched in file order — first row whose level AND mark ranges
    // both contain the character wins. Alt > 0 marks a SPLIT band, where two depths are open and the player
    // picks a tunnel. No match at all = refused (below the chart's level floor). See the CSV headers for
    // where the numbers come from and which half of them is ours rather than the archive's.
    public readonly record struct EventCaveBand(int Tier, int Alt, int MinLevel, int MaxLevel,
        int MinMark, int MaxMark, string Label, string Sources)
    {
        public bool Contains(int level, int mark) =>
            level >= MinLevel && level <= MaxLevel && mark >= MinMark && mark <= MaxMark;
    }

    public static IReadOnlyList<EventCaveBand> EventCaveBands { get; private set; } = new List<EventCaveBand>();

    /// <summary>The band a character of this level/subpath-rank falls in, or null when nothing matches —
    /// which is the refusal case, not an error.</summary>
    public static EventCaveBand? EventCaveBandFor(int level, int mark)
    {
        foreach (var b in EventCaveBands) if (b.Contains(level, mark)) return b;
        return null;
    }

    public sealed record EventCaveDef(string Key, ushort EntranceMap, (ushort X, ushort Y)[] Tiles,
        ushort[] TierMaps, ushort DestX, ushort DestY, string[] Pages,
        string Prompt, string OptionNear, string OptionFar, string DenyMsg, string Sources)
    {
        /// <summary>Map id for a 1-based tier. A ladder deeper than this cave's map list clamps to the
        /// deepest map it does have, so adding a band never strands a player on a map that isn't there.</summary>
        public ushort MapForTier(int tier) =>
            TierMaps.Length == 0 ? (ushort)0 : TierMaps[Math.Clamp(tier, 1, TierMaps.Length) - 1];
    }

    public static IReadOnlyList<EventCaveDef> EventCaves { get; private set; } = new List<EventCaveDef>();

    // Derived (map,x,y) -> entrance lookup, so the per-step check is one hash probe (same shape as
    // MythicCaveTiles / ArenaDoorTiles).
    public static IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), EventCaveDef> EventCaveTiles { get; private set; }
        = new Dictionary<(ushort, ushort, ushort), EventCaveDef>();

    // ---- PvP arena doors (game-data/ArenaDoors.csv) -------------------------------------------------
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

    // ---- Board-sign locations (game-data/BoardLocations.csv) -----------------------------------------
    // RTK's onSign board-sign system (on_event.lua onSign / selectBulletinBoard): a board SPRITE tile that,
    // when faced from the south (player looking north), opens ONE specific board (Server/Boards.cs) straight
    // to its posts. Keyed by the board tile (map,x,y) + the target BoardId; consumed by Session via TryBoardAt
    // with RTK's ±1 X tolerance. Distinct from the `b` mailbox/board-list — this jumps directly to a board.
    public static IReadOnlyList<(ushort Map, ushort X, ushort Y, int BoardId)> BoardLocations { get; private set; }
        = new List<(ushort, ushort, ushort, int)>();

    // ---- Location / warp geometry (Tier-1 extraction; game-data/*.csv) ------------------------------
    // RTK/RE geometry that used to be hard-coded in the game logic, moved to flat files so it hot-reloads via
    // @reload like every other registry. Consumers read these Content.* properties.

    // Which of the two soundtracks a track belongs to. They are separate id SPACES, not one list — mp3 ids
    // 2/3/4 in the 5.x set collide with midi ids 2/3/4 in the old one — so every lookup takes a set.
    //   Old = the 12 stock midis (1.mid..12.mid). In NexusTK.snd on 4.95 and Snd.dat on 5.33, so BOTH
    //         clients can play them, and they stay the default.
    //   New = the 25 mp3s + 52 playlists in the 5.33 client's Mus000.dat. 5.33 ONLY: the 4.95 client has
    //         an mp3 engine (see Session.SendMusic) but ships none of the files, so offering it there
    //         would be silence. See docs/5.x/Wire-Divergences.md §"0x19 music".
    public enum MusicSet { Old, New }

    // The client's background tracks, by id and by NAME (the files are numbered, but the songs have real
    // names — see MusicTracks.csv, which is also what lets "@music mist" work). Type is the 0x19 channel:
    // 2 = midi, 1 = mp3. Playlist is true for the 5.x .LST/.LSR entries, where the id names a list of ten
    // tracks the client cycles by itself rather than one song.
    //
    // Shuffle separates the two kinds of playlist, and map music MUST NOT use a shuffled one. Both cycle
    // fine, but the 5.33 advance (0x4a7b40, on WM_USER+8) picks the next entry as `rand() % count + 1` for
    // an .LSR, and the play function (0x4a5f80 @0x4a6078) early-outs to a NO-OP when the index it is handed
    // equals the one already playing. On that 1-in-10 collision nothing is opened, and because the previous
    // stream has already ended there is no further end-of-stream callback — the music is dead until the
    // server sends another 0x19. An .LST advances `cur + 1` (wrapping 10 -> 1), which can never collide.
    // Measured live 2026-08-22: 2 stalls in 40 shuffled advances, 0 in 24 ordered ones.
    public sealed record MusicTrack(ushort Id, string Name, byte Type, MusicSet Set, bool Playlist,
                                    bool Shuffle = false);
    public static IReadOnlyList<MusicTrack> MusicTracks { get; private set; } = new List<MusicTrack>();

    // Area -> BGM track (BgmFor). A design assignment, not RTK data: RTK's own Maps table has one track
    // (902) on 9799 of 9850 maps, and the 4.95 client files carry no map->track table at all. Zones match by
    // explicit map id/range first, then by map-NAME glob; a map in no zone keeps whatever is already playing
    // (see Session.PlayMapMusic) so walking into a shop or a cave never restarts the song. See MapBgm.csv.
    // Track/Type is the Old (midi) pick, Track5x/Type5x the New (5.x mp3-playlist) one; a zone that names no
    // Track5x falls back to its midi, which on 5.33 still plays.
    public sealed record BgmZone(string Zone, ushort Track, byte Type, ushort Track5x, byte Type5x,
        IReadOnlyList<(ushort Lo, ushort Hi)> Maps, IReadOnlyList<string> Names);
    public static IReadOnlyList<BgmZone> BgmZones { get; private set; } = new List<BgmZone>();

    // Resolved map -> track, built once at load (BuildBgmMap): the zones' own maps at Hops 0, then every
    // other map inherits its NEAREST zone through the warp graph. That spill is what makes a building or a
    // cave play its area's theme without being listed, and — unlike leaving it to "whatever is already
    // playing" — it also works when you LOG IN inside one, where there is no previous song to inherit.
    public sealed record BgmPick(ushort Track, byte Type, ushort Track5x, byte Type5x, string Zone, int Hops);
    private static Dictionary<ushort, BgmPick> _bgmByMap = new();

    /// <summary>The track to start on a zone-less map when nothing is playing yet (a fresh session): the
    /// "Default" row of MapBgm.csv. Null leaves such a session silent until it reaches a zoned map.</summary>
    public static (ushort bgm, byte type)? DefaultBgm { get; private set; }

    /// <summary>The <see cref="MusicSet.New"/> half of the "Default" row (its <c>Track5x</c>).</summary>
    public static (ushort bgm, byte type)? DefaultBgmNew { get; private set; }

    /// <summary>The fresh-session fallback for one soundtrack, falling back to the midi when the Default row
    /// names no 5.x track.</summary>
    public static (ushort bgm, byte type)? DefaultBgmFor(MusicSet set) =>
        set == MusicSet.New ? DefaultBgmNew ?? DefaultBgm : DefaultBgm;

    // Return tiles for Return / yellow_scroll / qui_hyang (Session.ReturnToInn). Grouped by Kugnae/Buya/
    // Nagnang (chosen by nation), Wilderness (the Neutral nation's), and Sanhae/Hausson (bound by a mayor
    // and overriding the nation set). The player->group choice stays in code (Session.HomeGroup).
    // X2/Y2 are an optional bottom-right corner: the wilderness clearing has no bed, so RTK lands you on a
    // random tile in a box there. Blank X2/Y2 -> the box is the single tile X,Y, which is every tavern.
    public sealed record InnDef(ushort Map, ushort X, ushort Y, ushort X2, ushort Y2);
    public static IReadOnlyDictionary<string, IReadOnlyList<InnDef>> Inns { get; private set; } =
        new Dictionary<string, IReadOnlyList<InnDef>>(StringComparer.OrdinalIgnoreCase);

    // Ground-item forage spawn boxes (World forage tick / RTK itemspawner.lua). See ForageAreas.csv.
    public sealed record ForageAreaDef(string ItemKey, ushort Map, int MinX, int MaxX, int MinY, int MaxY,
        int Max, int MinQty, int MaxQty);
    public static IReadOnlyList<ForageAreaDef> ForageAreas { get; private set; } = new List<ForageAreaDef>();

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
    public static IReadOnlyDictionary<string, HarvestNodeDef> HarvestNodes { get; private set; } =
        new Dictionary<string, HarvestNodeDef>(StringComparer.OrdinalIgnoreCase);

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
    public static IReadOnlyDictionary<string, MobSpellDef[]> MobSpells { get; private set; } =
        new Dictionary<string, MobSpellDef[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Idle flavour lines (RTK's <c>if math.random(1,100) == 1 then mob:talk(…)</c>, which is all
    /// most "custom AI" scripts actually are). Chance is 1-in-N per move tick.</summary>
    public sealed record MobChatterDef(string MobKey, int Chance, byte Channel, string[] Lines);

    public static IReadOnlyDictionary<string, MobChatterDef> MobChatter { get; private set; } =
        new Dictionary<string, MobChatterDef>(StringComparer.OrdinalIgnoreCase);

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

    public static IReadOnlyDictionary<string, MobSpawnRuleDef> MobSpawnRules { get; private set; } =
        new Dictionary<string, MobSpawnRuleDef>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Is the global spawn HP jitter on (the <c>*</c> row)? RTK's mob_on_spawn.lua is the default
    /// hook for every creature without its own, and it does exactly one thing: vary max HP.</summary>
    public static bool MobHpJitter { get; private set; }

    /// <summary>A mythic boss's survival kit (RTK mob_ai_mythic): it can shrug off a killing blow, break its
    /// own paralysis, and regenerate while its Last Stand runs. See game-data/MobBosses.csv.</summary>
    public sealed record MobBossDef(string MobKey, int HealAmount, int HealChance, int ParaBreakChance,
        int LastStandMs, int Anim, int Sound);

    public static IReadOnlyDictionary<string, MobBossDef> MobBosses { get; private set; } =
        new Dictionary<string, MobBossDef>(StringComparer.OrdinalIgnoreCase);

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
    // tier-expanded (+0/+3000/+4000) at load. See FallRooms.csv. Most rows come straight from RTK's
    // onScriptedTilesMythicFallRooms.lua (a 1/500-per-step roll). The Tiger row (Dark Pen 109 -> Guardroom
    // 110) is an APPROXIMATION: RTK reaches the tiger guardroom via a single hidden warp-trap NPC on Dark Pen
    // (trap/tiger_spawn/warp_trap_guardroom.lua), not a fall-room tile — but this server has no trap-NPC
    // tiles, so we reuse the fall mechanic to make the pure-sentry guardroom reachable. Tagged
    // rtk-lua-warptrap in the CSV to mark it as the one non-fall-room source.
    public static IReadOnlyDictionary<ushort, (ushort Map, ushort X, ushort Y)> FallRooms { get; private set; } =
        new Dictionary<ushort, (ushort, ushort, ushort)>();

    // Ambush-trap system (game-data/AmbushBursts.csv + AmbushConfig.csv): RTK's hidden MobSpawnNpc tiles in the
    // mythic caves (mob_spawn.lua + rabbitTrap.lua + tigerTrap.lua). AmbushBursts maps a burst-table name to
    // its exact weighted variant lists (extractor-generated, re/extract_ambush_tables.py); Ambushes maps a
    // (tier-resolved) cave map to its trigger config. Warrior Watchful Eye reveals these traps. See
    // World.RefillAmbush / World.FireAmbushLocked and Session's spot-traps reveal fork.
    public static IReadOnlyDictionary<string, IReadOnlyList<int[]>> AmbushBursts { get; private set; } =
        new Dictionary<string, IReadOnlyList<int[]>>();
    public static IReadOnlyDictionary<ushort, AmbushMapDef> Ambushes { get; private set; } =
        new Dictionary<ushort, AmbushMapDef>();

    // Curated shop catalogues (game-data/ShopCatalogues.csv) — hand-authored, ORDERED sub-category buy
    // menus (e.g. SmithNpc's armor menus) that the auto-extracted flat ShopStock can't represent. Keyed by
    // NpcDef.Key; consulted first by Shops.For, else it falls back to ShopStock. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, IReadOnlyList<(string Name, string[] Keys)>> ShopCatalogues { get; private set; } =
        new Dictionary<string, IReadOnlyList<(string, string[])>>(StringComparer.OrdinalIgnoreCase);

    // Data-driven spell params (game-data/SpellParams.csv): per spell key, the raw CSV row its Lua verb
    // reads (the `verb` column + numeric params like coeff/mana/amount). The "row" half of the verb/row spell
    // model — the "verb" logic lives in spell_verbs.lua (see Server/SpellScript.cs + Session.ApplyCast). Sparse:
    // only migrated spells have a row; everything else uses the C# CastX dispatch. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SpellParams { get; private set; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    // Data-driven item use-effect params (game-data/ItemParams.csv): per item key, the raw CSV row its
    // Lua verb reads (the `verb` column + params like amount/hpcost/statuskey/duration). The "row" half of the
    // verb/row item-effect model — the "verb" logic lives in item_verbs.lua (see Server/ItemScript.cs +
    // Session.ApplyItemEffect). Items without a row fall back to the item DB's Vita/Mana. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ItemParams { get; private set; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    // NPC composition (game-data/NpcAbilities.csv): NpcKey -> the ability NAMES it's built from (a
    // pipe-list). NpcScripts.For resolves each name to its C# INpcAbility instance (NpcScripts.AbilityByName).
    // The "which abilities" is data; the ability code stays code. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, string[]> NpcCompositions { get; private set; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    // Per-class level-up HP/MP gain ranges (game-data/PathGrowth.csv), keyed by path id (0 Peasant / 1
    // Warrior / 2 Rogue / 3 Mage / 4 Poet). Each is the pair of args to Random.Shared.Next(min, max) — max is
    // EXCLUSIVE, matching the original C# switch. The which-stat-is-primary logic stays in Session.LevelUp.
    public static IReadOnlyDictionary<int, (int HpMin, int HpMax, int MpMin, int MpMax)> PathGrowth { get; private set; } =
        new Dictionary<int, (int, int, int, int)>();
    /// <summary>Level-up gain ranges for a path, falling back to Peasant (0) then a hardcoded default.</summary>
    public static (int HpMin, int HpMax, int MpMin, int MpMax) PathGrowthFor(int path) =>
        PathGrowth.TryGetValue(path, out var g) ? g : PathGrowth.TryGetValue(0, out var p) ? p : (45, 56, 32, 37);

    // Named engine scalars a deployment may retune without a rebuild (game-data/ServerTuning.csv, key,value).
    // These sit on the tier-1/tier-3 line — real mechanics, but harmless to expose as hand-editable config. Typed
    // accessors fall back to the historical hardcoded default if the key is absent, so a missing file is safe.
    public static IReadOnlyDictionary<string, double> Tuning { get; private set; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    private static double Tune(string key, double dflt) => Tuning.TryGetValue(key, out var v) ? v : dflt;
    public static int MailMinLevel => (int)Tune("MailMinLevel", 10);   // min level to view/send nmail
    public static int SpeechRange  => (int)Tune("SpeechRange", 8);     // tiles (Chebyshev) an NPC "hears" from
    public static uint BankMax     => (uint)Tune("BankMax", 100_000_000);   // per-account coin cap

    // NOTE: EraDate is deliberately NOT a Tune() accessor here. It is read by Shared.EraCalendar so the
    // login server sees the same value; a second copy on this side could drift from it by a stale default.
    // Reach it via Era.Today / EraCalendar.RawDate.
    // Highest minor-quest tier a path leader will hand out: 1 = Minor only (4.95 — the only tier that
    // existed), 2 adds Major, 3 adds Epic. The Major/Epic rows stay in MinorQuests.csv either way; this only
    // gates whether the "which type of quest?" menu is offered at all. See Server/MinorQuest.cs.
    public static int MinorQuestTiers => (int)Tune("MinorQuestTiers", 1);
    // Hours a path leader makes you wait after COMPLETING a minor quest before handing out another. RTK starts
    // its cooldown only when you ABANDON one, which leaves the completion path with no limit at all: turn one
    // in, say "quest" again, and the next is yours — an exp faucet whose only cost is one kill (the reward
    // scales with level, so it's worth more the higher you climb). 24 = one quest per real-world day, per the
    // user (2026-08-12). Real hours, not game time: the timer is a unix-second deadline like every other
    // persisted cooldown, so logging out doesn't pause it. 0 restores RTK's behavior.
    //
    // The ABANDON cooldown stays on RTK's own per-tier value (Minor = 2h) rather than following this. It
    // gates a quest you dropped without being paid for, so it isn't part of the reward rate limit — and
    // making a failed quest cost a full day would just teach players to sit on one they can't finish.
    public static int MinorQuestCooldownHours => (int)Tune("MinorQuestCooldownHours", 24);
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
    /// so this would be the only way to make 'm' behave like a mailbox key — at the cost of 'b' doing the same
    /// while mail is unread. 0 = always show the board list (Mailbox is still its last entry).
    /// <para>DEFAULT 0 BECAUSE 1 HARD-FREEZES THE 4.95 CLIENT (live 2026-08-08): answering sub-1 "Show Board"
    /// with a POSTS body (0x31 flags2=4) instead of the LIST body locks the client up — it stops pumping
    /// input entirely and never sends another packet. The identical posts bytes render fine when they answer
    /// sub-2, so the window ctor 0x406e80(1) evidently arms a list-shaped parse that a posts body walks off
    /// the end of. Don't turn this back on without RE'ing that ctor first. See Session.HandleBoard case 1.</para></summary>
    public static bool MailFirstOnBoard => Tune("MailFirstOnBoard", 0) != 0;

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

    // Door-object graphic toggle table (game-data/DoorObjects.csv, transcribed from RTK open.lua `openDoors`).
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

    // ---- authored cell overrides (game-data/MapCells.csv) ------------------------------------------
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
        Maps = LoadMaps(ResolvePath("P1998_MAP_INDEX", "map_index.csv"));
        MobFleeOverrides = LoadMobFlees(ResolvePath("P1998_MOB_FLEES", "MobFlees.csv"));   // BEFORE Mobs: LoadMobs folds it in
        MobStationaryOverrides = LoadMobStationary(ResolvePath("P1998_MOB_STATIONARY", "MobStationary.csv"));   // likewise
        Mobs = LoadMobs(ResolvePath("P1998_MOBS", "mobs.csv"));
        Items = LoadItems(ResolvePath("P1998_ITEMS", "Items.csv"));
        Warps = LoadWarps(ResolvePath("P1998_WARPS", "Warps.csv"));   // needs Maps
        Spawns = LoadSpawns(ResolvePath("P1998_SPAWNS", "Spawns.csv"));
        // Base area spawns + trap-ambush populations (tiger cave, rabbit boss-tier, trapdoor spiders) that RTK
        // spawns via trap/mob_spawn.lua rather than handleSpawn (rare-boss rows carry RespawnSec; generated by
        // re/extract_trap_spawns.py). Concatenated into a LOCAL and assigned to AreaSpawns ONCE — so a
        // concurrent reader on @reload never sees the base list without its 362 trap mobs (the old two-step
        // assign had that tear window).
        AreaSpawns = LoadAreaSpawns(ResolvePath("P1998_AREASPAWNS", "AreaSpawns.csv"))
            .Concat(LoadAreaSpawns(ResolvePath("P1998_AREASPAWNS_TRAP", "AreaSpawnsTrap.csv")))
            // …plus the crafting nodes (ore veins, ginko trees), which come from RTK's OTHER two spawner
            // NPCs — mining/woodcuttingSpawnHandler.lua. Kept in their own file for the same reason as the
            // trap rows: re-running the main extractor must not be able to drop them.
            .Concat(LoadAreaSpawns(ResolvePath("P1998_AREASPAWNS_CRAFT", "AreaSpawnsCrafting.csv")))
            .ToList();
        Shared.EraCalendar.Reload();   // era date + windows live in Shared (login server shares them)
        // BEFORE LoadNpcs, which now asks it whether an NPC existed yet (NPCs.csv EraFeature). Left where it
        // was, this read the PREVIOUS calendar on @reload, so moving EraDate and reloading placed NPCs by the
        // old date — with nothing to say so, since a wrong era never throws.
        var npcs = LoadNpcs(ResolvePath("P1998_NPCS", "NPCs.csv"));   // needs Maps + the era calendar
        _npcById = npcs.ToDictionary(n => n.Id);   // assign the index BEFORE the public list, so a reader that
        Npcs = npcs;                               // sees the new Npcs always sees the matching new _npcById
        MinorQuests = LoadMinorQuests(ResolvePath("P1998_MINORQUESTS", "MinorQuests.csv"));
        ShopStock = LoadShopStock(ResolvePath("P1998_SHOPSTOCK", "ShopStock.csv"));
        ShopBuysFrom = LoadShopBuysFrom(ResolvePath("P1998_SHOPBUYSFROM", "ShopBuysFrom.csv"));
        Paths = LoadPaths(ResolvePath("P1998_PATHS", "Paths.csv"));
        LevelExp = LoadLevelExp(ResolvePath("P1998_LEVELEXP", "LevelExp.csv"));
        SpellLevelOverrides = LoadSpellLevels(ResolvePath("P1998_SPELL_LEVELS", "SpellLevels.csv"));   // BEFORE Spells: LoadSpells reads it
        Spells = LoadSpells(ResolvePath("P1998_SPELLS", "Spells.csv"));
        // O(1) lookup indexes (0.1) — rebuilt every Load()/@reload so they swap with the lists above. Nothing
        // in Load reads them (RollDrops is the only in-Content consumer, and it runs at mob-death, not load).
        _itemById  = IndexFirst(Items, i => i.Id);
        _itemByKey = IndexFirst(Items, i => i.Key, StringComparer.OrdinalIgnoreCase);
        _mobById   = IndexFirst(Mobs, m => m.Id);
        _mobByKey  = IndexFirst(Mobs, m => m.Key, StringComparer.OrdinalIgnoreCase);
        _spellById = IndexFirst(Spells, s => s.Id);
        _spellByKey = IndexFirst(Spells, s => s.Key, StringComparer.OrdinalIgnoreCase);
        _ladderOf = BuildSpellLadders(Spells);
        // name -> id, first wins. BASE names go in first so a string that is one path's class name and
        // another's rank title (Paths.csv has a few) always resolves to the class, never the rank.
        var pathIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pathRankByName = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Paths)
            if (!string.IsNullOrEmpty(kv.Value)) { pathIdByName.TryAdd(kv.Value, kv.Key); pathRankByName.TryAdd(kv.Value, (kv.Key, 0)); }
        foreach (var (id, ladder) in PathRanks)
            for (int m = 1; m < ladder.Length; m++)
                if (ladder[m].Length > 0) { pathIdByName.TryAdd(ladder[m], id); pathRankByName.TryAdd(ladder[m], (id, m)); }
        _pathIdByName = pathIdByName;
        _pathRankByName = pathRankByName;
        SpellFx = LoadSpellFx(ResolvePath("P1998_SPELL_FX", "spell_effects.csv"));
        SpellTexts = LoadSpellTexts(ResolvePath("P1998_SPELL_TEXT", "SpellText.csv"));
        SpellCosts = LoadSpellCosts(ResolvePath("P1998_SPELL_COSTS", "SpellLearnCosts.csv"));
        Mob5xPalettes = LoadMob5xPalettes(ResolvePath("P1998_MOB_PALETTES_5X", "Mob5xPalettes.csv"));   // (Look,Colour)->Palette, V533-only remap
        ArmorDyeRamps = LoadArmorDyeRamps(ResolvePath("P1998_ARMOR_DYE_RAMPS", "ArmorDyeRamps.csv"));
        MapMeta = LoadMapMeta(ResolvePath("P1998_MAPS_FULL", "Maps.csv"));   // region + warpOut for Gateway
        MobDrops = LoadMobDrops(ResolvePath("P1998_MOB_DROPS", "MobDrops.csv"));
        CraftingToggleOverrides = LoadCraftingToggles(ResolvePath("P1998_CRAFTING_TOGGLES", "CraftingToggles.csv"));
        WarpQuestLocks = LoadWarpQuestLocks(ResolvePath("P1998_WARP_QUEST_LOCKS", "WarpQuestLocks.csv"));
        ArmorQuestGates = LoadArmorQuestGates(ResolvePath("P1998_ARMOR_QUESTS", "ArmorQuests.csv"));
        var mythicCaves = LoadMythicCaves(ResolvePath("P1998_MYTHIC_CAVES", "MythicCaves.csv"));
        MythicCaveTiles = mythicCaves   // assign the derived tile index BEFORE the public list (same reason as Npcs/_npcById)
            .SelectMany(c => c.Tiles.Select(t => (key: (c.EntranceMap, t.X, t.Y), cave: c)))
            .ToDictionary(e => e.key, e => e.cave);
        MythicCaves = mythicCaves;
        MythicAlliances = LoadMythicAlliances(ResolvePath("P1998_MYTHIC_ALLIANCES", "MythicAlliances.csv"));
        var arenaDoors = LoadArenaDoors(ResolvePath("P1998_ARENA_DOORS", "ArenaDoors.csv"));
        ArenaDoorTiles = arenaDoors   // derived tile index first, public list second (same reason as Npcs/_npcById)
            .SelectMany(d => d.Tiles.Select(t => (key: (d.Map, t.X, t.Y), door: d)))
            .ToDictionary(e => e.key, e => e.door);
        ArenaDoors = arenaDoors;
        EventCaveBands = LoadEventCaveBands(ResolvePath("P1998_EVENT_CAVE_TIERS", "EventCaveTiers.csv"));
        var eventCaves = LoadEventCaves(ResolvePath("P1998_EVENT_CAVES", "EventCaves.csv"));
        EventCaveTiles = eventCaves   // derived tile index first, public list second (same reason as Npcs/_npcById)
            .SelectMany(c => c.Tiles.Select(t => (key: (c.EntranceMap, t.X, t.Y), cave: c)))
            .ToDictionary(e => e.key, e => e.cave);
        EventCaves = eventCaves;
        MusicTracks = LoadMusicTracks(ResolvePath("P1998_MUSIC_TRACKS", "MusicTracks.csv"));
        (BgmZones, DefaultBgm, DefaultBgmNew) = LoadBgmZones(ResolvePath("P1998_MAP_BGM", "MapBgm.csv"));
        _bgmByMap = BuildBgmMap();   // needs Maps + Warps + BgmZones — resolves every map to a track
        Inns = LoadInns(ResolvePath("P1998_INNS", "Inns.csv"));
        ForageAreas = LoadForageAreas(ResolvePath("P1998_FORAGE", "ForageAreas.csv"));
        HarvestNodes = LoadHarvestNodes(ResolvePath("P1998_HARVEST", "HarvestNodes.csv"));
        MobSpells    = LoadMobSpells(ResolvePath("P1998_MOB_SPELLS", "MobSpells.csv"));
        MobChatter   = LoadMobChatter(ResolvePath("P1998_MOB_CHATTER", "MobChatter.csv"));
        MobSpawnRules = LoadMobSpawnRules(ResolvePath("P1998_MOB_SPAWN_RULES", "MobSpawnRules.csv"));
        MobBosses    = LoadMobBosses(ResolvePath("P1998_MOB_BOSSES", "MobBosses.csv"));
        PathHalls = LoadPathHalls(ResolvePath("P1998_PATHHALLS", "PathHalls.csv"));
        GatewayRegions = LoadGatewayGates(ResolvePath("P1998_GATEWAY", "GatewayGates.csv"));
        WorldDests = LoadWorldDests(ResolvePath("P1998_WORLDMAP_DESTS", "WorldMapDests.csv"));
        WorldMapTriggers = LoadWorldTriggers(ResolvePath("P1998_WORLDMAP_TRIGGERS", "WorldMapTriggers.csv"));
        FallRooms = LoadFallRooms(ResolvePath("P1998_FALLROOMS", "FallRooms.csv"));
        AmbushBursts = LoadAmbushBursts(ResolvePath("P1998_AMBUSH_BURSTS", "AmbushBursts.csv"));
        Ambushes = LoadAmbushConfig(ResolvePath("P1998_AMBUSH_CONFIG", "AmbushConfig.csv"), AmbushBursts);
        BoardLocations = LoadBoardLocations(ResolvePath("P1998_BOARD_LOCATIONS", "BoardLocations.csv"));
        ShopCatalogues = LoadShopCatalogues(ResolvePath("P1998_SHOP_CATALOGUES", "ShopCatalogues.csv"));
        SpellParams = LoadKeyedRows(ResolvePath("P1998_SPELL_PARAMS", "SpellParams.csv"));
        // The three Lua files load ATOMICALLY (see LuaVerbHost.Load): a broken edit is REJECTED and the
        // previously-loaded script keeps running. RejectedScripts records which ones didn't take so @reload can
        // say so to the GM's face — a silent "reload ok" after a typo is how you end up debugging the wrong thing.
        var rejected = new List<string>();
        if (!SpellScript.Load(ResolvePath("P1998_SPELL_VERBS", "spell_verbs.lua"))) rejected.Add("spell_verbs.lua");
        ItemParams = LoadKeyedRows(ResolvePath("P1998_ITEM_PARAMS", "ItemParams.csv"));   // same "whole row keyed by `key`" shape as SpellParams
        if (!ItemScript.Load(ResolvePath("P1998_ITEM_VERBS", "item_verbs.lua"))) rejected.Add("item_verbs.lua");
        if (!NpcScript.Load(ResolvePath("P1998_NPC_DIALOG", "npc_dialog.lua"))) rejected.Add("npc_dialog.lua");
        if (!MobScript.Load(ResolvePath("P1998_MOB_AI", "mob_ai.lua"))) rejected.Add("mob_ai.lua");
        RejectedScripts = rejected;
        // Phase-1 spell-DATA tables (extracted from Content.cs literals; see re/extract_spell_tables.py).
        PetSpells = LoadPets(ResolvePath("P1998_PETS", "Pets.csv"));
        WeaponProcs = LoadWeaponProcs(ResolvePath("P1998_WEAPON_PROCS", "WeaponProcs.csv"));
        TrapSpells = LoadTrapSpells(ResolvePath("P1998_TRAPS", "Traps.csv"));
        (MorphSpells, MorphDispatchSpells) = LoadMorphs(ResolvePath("P1998_MORPHS", "Morphs.csv"));
        (RageAmount, EnchantSpells) = LoadSpellMods(ResolvePath("P1998_SPELL_MODS", "SpellMods.csv"));
        NpcCompositions = LoadNpcCompositions(ResolvePath("P1998_NPC_ABILITIES", "NpcAbilities.csv"));
        PathGrowth = LoadPathGrowth(ResolvePath("P1998_PATH_GROWTH", "PathGrowth.csv"));
        (DoorSwaps, DoorDeltas, DoorDefaultOpen) = LoadDoorObjects(ResolvePath("P1998_DOOR_OBJECTS", "DoorObjects.csv"));
        Tuning = LoadTuning(ResolvePath("P1998_SERVER_TUNING", "ServerTuning.csv"));
        Doors.SetConfig(LoadDoors(ResolvePath("P1998_DOORS", "Doors.csv")));
        (_mapCells, var mapCellCount) = LoadMapCells(ResolvePath("P1998_MAP_CELLS", "MapCells.csv"));
        MapCellCount = mapCellCount;
        Log.Info($"content: {Maps.Count} maps ({MapMeta.Count} w/ region), {Mobs.Count} mobs, {Items.Count} items, " +
                 $"{Warps.Count} warps, {Spawns.Count} spawns, {AreaSpawns.Count} area-spawns, {Npcs.Count} npcs, {Spells.Count} spells ({SpellFx.Count} fx, {SpellCosts.Count} w/ real learn cost), {Mob5xPalettes.Count} 5x-colour remaps, {ArmorDyeRamps.Count} armor-dye ramps, {MinorQuests.Count} minor-quests, {ShopStock.Count} shop-stocks ({ShopBuysFrom.Count} buy-from lists), {LevelExp.Count} level-exp-paths, {MobDrops.Count} mob-drop-tables, {CraftingToggleOverrides.Count} crafting-toggle overrides, {MythicCaves.Count} mythic-caves ({MythicCaveTiles.Count} entrance tiles), {MythicAlliances.Count} mythic-alliances, {EventCaves.Count} event-caves ({EventCaveTiles.Count} entrance tiles, {EventCaveBands.Count} tier bands), {ArenaDoors.Count} arena-doors, {WorldDests.Count} world-map dests, {PathHalls.Count} path-halls, {GatewayRegions.Count} gateway-regions, {ForageAreas.Count} forage-areas, {FallRooms.Count} fall-rooms, {Ambushes.Count} ambush-maps ({AmbushBursts.Count} burst-tables), {BoardLocations.Count} board-signs, {PetSpells.Count} pets, {WeaponProcs.Count} weapon-procs loaded" +
                 (Maps.Count == 0 || Mobs.Count == 0
                     ? "  (some empty — run re/build_map_index.py and check game-data/mobs.csv)"
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
        ReloadGate.Wait();
        try
        {
            var before = CaptureReloadTables();
            try { Load(); }
            catch (Exception e)
            {
                var replaced = CaptureReloadTables()
                    .Where(kv => before.TryGetValue(kv.Key, out var old) && !ReferenceEquals(old, kv.Value))
                    .Select(kv => kv.Key)
                    .ToArray();
                string progress = replaced.Length == 0
                    ? "No public content tables were replaced."
                    : $"Tables replaced before failure: {string.Join(", ", replaced)}.";
                throw new InvalidOperationException($"{e.Message} {progress}", e);
            }

            var summary = $"{Maps.Count} maps, {Mobs.Count} mobs, {Items.Count} items, {Warps.Count} warps, " +
                          $"{Spawns.Count + AreaSpawns.Count} spawns, {Npcs.Count} npcs, {Spells.Count} spells, {ShopStock.Count} shops, " +
                          $"{CraftingToggleOverrides.Count} crafting-toggle overrides, " +
                          $"era {(Era.Today?.ToString("yyyy-MM-dd") ?? "off")} ({Shared.EraCalendar.FeatureCount} dated features)";
            // A rejected .lua is the single most important thing @reload can tell you: your edit did NOT take, the
            // old script is still running, and the reason is in the server log. Lead with it.
            return RejectedScripts.Count == 0 ? summary
                 : $"*** REJECTED (still running the previous version, see log): {string.Join(", ", RejectedScripts)} *** — {summary}";
        }
        finally { ReloadGate.Release(); }
    }

    /// <summary>Snapshot the public reference-backed tables so a failed pre-#33 reload can say which ones
    /// already swapped. Value settings and private derived indexes are deliberately not described as tables.</summary>
    private static Dictionary<string, object?> CaptureReloadTables() =>
        typeof(Content).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => !p.PropertyType.IsValueType && p.GetSetMethod(nonPublic: true)?.IsPrivate == true)
            .OrderBy(p => p.MetadataToken)
            .ToDictionary(p => p.Name, p => p.GetValue(null));

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

    // ---- sage geography (the reach of the Share Wisdom ladder) --------------------------------------
    //
    // WHERE A SAGE RUNG ACTUALLY REACHES THE KINGDOM. Every rung but the last works only in certain
    // places, and outside them the cast does NOT fail — it behaves as the Mentor spell, and the aether
    // burns either way ("block sage and mentor spell usage in non-saging areas instead of casting
    // Aethers — Won't be changed. Was written to be like that", Nexus Atlas answering a suggestion).
    // The policy per rung lives in spell_verbs.lua's `sage_shout`; this answers only "where am I".
    //
    // REGION 2 IS THE LIST, and it is a startlingly exact fit rather than a proxy. tswolf names the sage
    // areas as "Mythic, Wilderness, and Kamings Encampment"; region 2 holds Mythic Nexus (41), Wilderness
    // (1002), KaMing's Encampment (3800) + KaMing (3806) and the mythic zones, and nothing else. The Atlas
    // calls the same set "Mythic Nexus, Carnage, Events, and other 4.0 designated areas" — region 2 IS
    // that designation, which is also why RegionCityName already calls it "the Mythic".
    private const int SageRegion = 2;

    // Carnage and event maps, which the sources list alongside the Mythic but which sit in their kingdoms'
    // own regions rather than in region 2. Named explicitly because there are five and no flag groups them.
    // The ARENAS are deliberately NOT here: sage works in "carnage GAMES", an event state this server does
    // not model, and a permanently-sagable arena is not what any source describes. "Carnage test" (4002)
    // and "Test Event1" (8105) are left out for the same reason the rest of the test maps are — they are
    // not places a player is.
    private static readonly HashSet<ushort> SageEventMaps = new() { 2007, 3010, 4050, 4524, 4525 };

    /// <summary>Is this map one of the "4.0 designated areas" where a sage rung reaches the whole kingdom —
    /// the Mythic/Wilderness/KaMing's region, or a carnage/event map? True for every rung; rungs 3-4 also
    /// reach <see cref="IsOwnKingdom"/>, and rung 5 reaches everywhere regardless of this.</summary>
    public static bool IsSageArea(ushort mapId) =>
        RegionOf(mapId) == SageRegion || SageEventMaps.Contains(mapId);

    /// <summary>Is this map inside <paramref name="nation"/>'s own kingdom — what rungs 3 and 4 add on top
    /// of <see cref="IsSageArea"/> ("Sage also works in one's home town stated on the spell name").
    ///
    /// <para>Nation ids are Neutral 0 · Koguryo 1 · Buya 2 · Nagnang 3; map regions are Kugnae 0 · Buya 1 ·
    /// Mythic 2 · Nagnang 3. The two spaces are NOT the same numbers, which is the whole reason this exists.
    /// A NEUTRAL caster has no kingdom to add, so rungs 3-4 fall back to rung-2 behaviour for them — exactly
    /// what the tutor board says ("Works just like level 2 for neutral villagers") and why the archive's
    /// rung-3/4 names run "Buya, Kugnae, Nagnang, or NEUTRAL Wisdom".</para></summary>
    public static bool IsOwnKingdom(ushort mapId, int nation)
    {
        int region = nation switch { 1 => 0, 2 => 1, 3 => 3, _ => -1 };   // Neutral (and anything odd) -> none
        return region >= 0 && RegionOf(mapId) == region;
    }

    /// <summary>Whether a map is indoors (RTK <c>MapIndoor</c> — town interiors, caves, dungeons). The weather
    /// gate: <see cref="WeatherModel"/> never draws rain or snow here. Maps with no metadata row default to
    /// outdoor.</summary>
    public static bool IsIndoor(ushort mapId) => MapMeta.TryGetValue(mapId, out var m) && m.Indoor;

    /// <summary>Whether a map allows warp-out spells (Gateway/Return). Unknown maps default to true (only an
    /// explicit MapWarpout==0 blocks); RTK shows "It doesn't work here" when this is false.</summary>
    public static bool WarpOut(ushort mapId) => !MapMeta.TryGetValue(mapId, out var m) || m.WarpOut;

    /// <summary>Whether a map is flagged PvP (RTK MapPvP) — disables equipment durability loss there
    /// (clif_deductdura, clif.c:6650: "disable dura loss from mobs on pvp map").</summary>
    public static bool IsPvpMap(ushort mapId) => MapMeta.TryGetValue(mapId, out var m) && m.Pvp;

    /// <summary>The newbie tutorial AREA's own maps — 4711 Welcome, 4712 Open Field, 4713 Forest Path,
    /// 4714 Deep Forest, 4715 Country Farm, 4716 Mignok's Home, 4717 City Limits, 4718 Angel's Blessing.
    /// Authored as one contiguous block, hence the range.
    ///
    /// <para>This is a "where the player physically is" test, NOT an era or quest-progress test, and that is
    /// deliberate: it has to hold for a character who was GM-warped in, who abandoned the chain half way, or
    /// whose progress flag says something the map disagrees with. The area has no Shaman and no exit a ghost
    /// could walk to, so anything that would strand or eject a player from it keys off this — see
    /// <c>Session.SilverThread</c> and the ghost branch in <c>Session.RunNpcAsync</c>.</para></summary>
    public static bool IsTutorialMap(ushort mapId) => mapId is >= 4711 and <= 4718;

    // Quest-locked warps (game-data/WarpQuestLocks.csv): a warp switched OFF until a quest reaches a
    // stage. Only the warp is affected — the tile stays walkable and the player is never blocked or pushed
    // back; see Session.WarpLockedByQuest. Keyed on the map PAIR so the lock is one-way: walking back the
    // way you came is never affected.
    public sealed record WarpQuestLock(ushort FromMap, ushort ToMap, string QuestKey, int MinStage, string Message);

    public static IReadOnlyDictionary<(ushort From, ushort To), WarpQuestLock> WarpQuestLocks { get; private set; } =
        new Dictionary<(ushort, ushort), WarpQuestLock>();

    /// <summary>Whether speech (incl. whisper) is allowed on this map (RTK cantalk). False on the rare
    /// silenced map — RTK: "Your voice is swept away by a strange wind." (clif_parsewisp, clif.c:7666).</summary>
    public static bool CanTalk(ushort mapId) => !MapMeta.TryGetValue(mapId, out var m) || m.CanTalk;

    /// <summary>Whether spells may be cast on this map (RTK Maps.MapSpells → <c>map[m].spell</c>). RTK gates
    /// the whole 0x0F opcode on it — <c>clif.c:11427</c>, <c>if (map[sd->bl.m].spell || sd->status.gm_level)
    /// … else clif_sendminitext(sd, "That doesn't work here.")</c> — so it is a blanket no-casting flag, not a
    /// per-spell one. Unknown maps default to allowed; only an explicit <c>MapSpells=0</c> blocks.
    ///
    /// <para>This is what makes the towns' indoor spaces spell-free: taverns, shops, kan houses, the three
    /// Gathering halls, the class trainers' buildings. Note it is NOT the same thing as <c>MapIndoor</c>,
    /// which is set on every cave and dungeon in the game too (Bat Cave, the Mythic caves, …) — casting has
    /// to work in those, so "indoors" alone can't be the rule and this explicit column is.</para>
    ///
    /// <para>RTK's own dump is inconsistent about the class trainers' buildings: Nagnang's (2510/2512/2514/
    /// 2516) and the later-era set (3820-3835) block casting, while Kugnae's and Buya's — the same rooms one
    /// era earlier — do not. The 40 outliers (halls 11-18 / 341-344, sanctums 366-369, alignment rooms
    /// 300-323) are corrected IN Maps.csv rather than through an override file, per the rule that these
    /// tables are ours now and are never re-extracted.</para></summary>
    public static bool SpellsAllowed(ushort mapId) => !MapMeta.TryGetValue(mapId, out var m) || m.CanCast;

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
        Line($"--- Music: {MusicTracks.Count(t => t.Set == MusicSet.Old && t.Name.Length > 0)} named midis + " +
             $"{MusicTracks.Count(t => t.Set == MusicSet.New && !t.Playlist)} 5.x mp3s / " +
             $"{MusicTracks.Count(t => t.Playlist && !t.Shuffle)} ordered + " +
             $"{MusicTracks.Count(t => t.Shuffle)} shuffled playlists, {BgmZones.Count} zones, " +
             $"{_bgmByMap.Count} maps resolved, " +
             $"default {(DefaultBgm is null ? "(none)" : $"{DefaultBgm.Value.bgm} '{TrackName(DefaultBgm.Value.bgm)}'")}" +
             $" / 5.x {(DefaultBgmNew is null ? "(none)" : $"{DefaultBgmNew.Value.bgm} '{TrackName(DefaultBgmNew.Value.bgm, MusicSet.New)}'")} ---");
        foreach (var q in new[] { "mist", "tiger", "mon", "6", "10", "nope" })
            Line($"    @music {q,-6} -> " + (FindTrack(q) is { } t ? $"track {t.Id} '{t.Name}' type{t.Type}" : "(no match)"));
        // The 5.x set is a SEPARATE id space: 2/3/4 must resolve to the mp3s, not the midis of the same id.
        foreach (var q in new[] { "2", "underwater", "nexus", "902", "pole" })
            Line($"    @music {q,-10} (new) -> " + (FindTrack(q, MusicSet.New) is { } t
                ? $"track {t.Id} '{t.Name}' type{t.Type}{(t.Playlist ? " playlist" : "")}" : "(no match)"));
        // (map, expected track) — the six areas the assignment was specified for, plus a building inside each
        // hub (which must resolve to the SAME track so walking through a door never restarts the song).
        var bgmWant = new (ushort Map, string Track)[]
        {
            (137, "mist"), (3812, "mist"),        // Arctic Land / Arctic Tavern
            (3815, "mist"), (3816, "mist"),       // Crystalline Chapel / Kamchatka Ballroom — same song
            (3819, "mist"),                       // Lovers' Lake, an outdoor spot off the village
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
            (324, "mist"), (511, "mist"),         // Kwi-sin Shrine / Snow Dungeon — off the village, spill-only
            (1121, "mist"),                       // Sanhae Valley — 3 hops out through the Arctic
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

        // The 5.x soundtrack rides the SAME zone/spill resolution, so the only thing that can go wrong is a
        // zone whose Track5x didn't resolve — which shows up as a midi id leaking onto the mp3 channel.
        // Every 5.x map pick must be an ORDERED playlist (.LST): a single mp3 never advances off its one
        // song, and a SHUFFLED list (.LSR) eventually stalls dead on the client's index collision — see the
        // MusicTrack doc. Both failures are silent (the area just goes quiet), so they are asserted here.
        var bgm5xWant = new (ushort Map, string Track)[]
        {
            (0, "town2"), (2, "town2"),           // Kugnae / Walsuk Tavern (spill)
            (330, "town3"), (332, "town3"),       // Buya / Spring Tavern (spill)
            (137, "town10"), (1013, "town10"),    // Arctic Land / Haeng Tavern (spill)
            (114, "cave5"), (457, "cave5"),       // Hamgyong Nam-Do / Ruined House
            (3800, "field3"),                     // KaMing's Encampment
            (41, "nexus"),                        // Mythic Nexus — ClassicTK's own 908
        };
        bool bgm5xOk = true;
        foreach (var (map, want) in bgm5xWant)
        {
            var got = BgmFor(map, MusicSet.New);
            string name = got is null ? "(none)" : TrackName(got.Value.bgm, MusicSet.New);
            var track = got is null ? null
                : MusicTracks.FirstOrDefault(t => t.Set == MusicSet.New && t.Id == got.Value.bgm);
            bool ordered = track is { Playlist: true, Shuffle: false };
            bool hit = name.Equals(want, StringComparison.OrdinalIgnoreCase) && got?.type == 1 && ordered;
            bgm5xOk &= hit;
            string kind = track is null ? "?"
                        : !track.Playlist ? "SINGLE — never advances"
                        : track.Shuffle   ? "SHUFFLED — will stall dead"
                                          : "ordered playlist";
            Line($"    {(hit ? "ok " : "XX ")}map {map,-6} (5.x) -> {name,-8} " +
                 $"id {(got?.bgm.ToString() ?? "-"),-4} type{got?.type} {kind} (want {want})");
        }

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
                  && bgmOk && bgm5xOk && sticky && doorsOk;
        Line(ok ? "SELFTEST: PASS" : "SELFTEST: FAIL (empty registry or missing expected entry)");
    }

    // ---- background music (0x19) --------------------------------------------------------------
    // The stock 4.95 client keeps its audio in NexusTK.snd, which ships exactly 12 background tracks
    // (1.mid .. 12.mid); the 0x19 music packet plays one by id with type 2 = MIDI. There is no original
    // map->track table in the client files, so we assign them ourselves — by AREA, not by map (MapBgm.csv).
    //
    // The 5.33 client keeps those same 12 midis (in Snd.dat) AND a second, larger soundtrack in Mus000.dat:
    // 25 mp3s plus 52 playlists, played over 0x19 type 1. That is the MusicSet.New half of every table here,
    // and it is 5.33-only because 4.95 ships none of those files. Players opt in per character with
    // "@music new" (Session.PlayMusicCmd); the midis stay the default for everyone.

    /// <summary>The background track for a map in one soundtrack: (bgm id, 0x19 type), or null only for a map
    /// that no zone claims AND that has no warp path to one — in which case the caller keeps whatever is
    /// already playing (see Session.PlayMapMusic).</summary>
    public static (ushort bgm, byte type)? BgmFor(ushort mapId, MusicSet set = MusicSet.Old) =>
        _bgmByMap.TryGetValue(mapId, out var p)
            ? (set == MusicSet.New ? (p.Track5x, p.Type5x) : (p.Track, p.Type))
            : null;

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
                        byMap[(ushort)id] = new BgmPick(z.Track, z.Type, z.Track5x, z.Type5x, z.Zone, 0);

        foreach (var z in BgmZones)
            foreach (var pat in z.Names)
                foreach (var m in Maps.Values)
                    if (!byMap.ContainsKey(m.Id) && GlobMatch(m.Name, pat))
                        byMap[m.Id] = new BgmPick(z.Track, z.Type, z.Track5x, z.Type5x, z.Zone, 0);

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
    /// "monkey". Null when nothing matches.
    ///
    /// <para><paramref name="set"/> is searched FIRST and the other set second, so the id spaces can overlap
    /// (midi 2 = "dragon", mp3 2 = "buyeo") while a player in either mode can still name any track he can
    /// hear. An id with no row resolves to an unnamed track in <paramref name="set"/> rather than to null —
    /// the client will happily play a number we have never given a name.</para></summary>
    public static MusicTrack? FindTrack(string query, MusicSet set = MusicSet.Old)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        var (mine, theirs) = (MusicTracks.Where(t => t.Set == set), MusicTracks.Where(t => t.Set != set));
        if (ushort.TryParse(query, out var id))
            return mine.FirstOrDefault(t => t.Id == id)
                ?? theirs.FirstOrDefault(t => t.Id == id)
                ?? new MusicTrack(id, "", set == MusicSet.New ? (byte)1 : (byte)2, set, false);
        return mine.FirstOrDefault(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? theirs.FirstOrDefault(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? mine.FirstOrDefault(t => t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            ?? theirs.FirstOrDefault(t => t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The name of a track id within one soundtrack, or "" if it has none (only some of the 12 stock
    /// midis are named).</summary>
    public static string TrackName(ushort id, MusicSet set = MusicSet.Old) =>
        MusicTracks.FirstOrDefault(t => t.Id == id && t.Set == set)?.Name ?? "";

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

    // ---- spells / classes (used by the @lvl/@class/@mark/@align book rebuild + casting) -------

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
    /// <item>Archive-sourced (149 rows): cross-checked against the tswolf.com + boards.nexustk.com scrape
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

    // ---- the Share Wisdom ladder ---------------------------------------------------------------------
    //
    // The five rungs in order, which is the ONE definition: npc_dialog.lua's SAGE_LADDER (what the Sage
    // sells), spell_verbs.lua's SAGE_RUNGS (how far each reaches), the NpcGrantedSpells gate below, the
    // rebuild's re-grant and @sage all read the same order from here. A smoke test pins the Lua copies
    // against this array, because a rename on one side only is otherwise silent.
    public static readonly string[] SageLadder =
        { "share_wisdom", "mentors_wisdom", "apprentices_wisdom", "adepts_wisdom", "sages_wisdom" };

    /// <summary>The level the Sage teaches at — "available to all paths for people over the level 90".
    /// The ROOM is deliberately more generous (Maps.csv keeps map 1230 at 50) so a player can find him
    /// early and be told what it takes; this is the gate that actually bites.</summary>
    public const int SageLevel = 90;

    /// <summary>Registry key: which rung the character has paid for (0 = none), written by the Sage's own
    /// dialog and by <c>@sage</c>. The spell in the book is the visible half; this is the half that
    /// SURVIVES a character rebuild, exactly as <see cref="DogFlagReg"/> does for the Dog spells — without
    /// it, one <c>@lvl</c> would confiscate a 500,000-gold ladder with no way to get it back.</summary>
    public const string SageRungReg = "sage_rung";

    /// <summary>Registry key: absolute unix-SECOND deadline before the next rung may be bought. Written by
    /// the Sage (now + 90 days) and cleared by <c>@sage</c> so a tester is not stuck behind it.</summary>
    public const string SageTimerReg = "sage_timer";

    /// <summary>The spell key for a 1-based rung, or null if the rung is out of range (0 = holds none).</summary>
    public static string? SageSpellForRung(int rung) =>
        rung >= 1 && rung <= SageLadder.Length ? SageLadder[rung - 1] : null;

    /// <summary>The rung a spell key is, or 0 if it is not one of them.</summary>
    public static int SageRungOf(string key) =>
        System.Array.FindIndex(SageLadder, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) + 1;

    /// <summary>The rung a character rebuild should hand back: what they paid for, but only once they are
    /// high enough to have bought it. Level-gated for the same reason the Dog spells are — drop a character
    /// to level 5 and the entitlement stops applying until they are 90 again, at which point it returns.
    /// Returns null when there is nothing to grant.</summary>
    public static SpellDef? SageSpellFor(int rung, int maxLevel)
    {
        if (maxLevel < SageLevel) return null;
        return SageSpellForRung(rung) is { } key && SpellByKey(key) is { } sp
            ? sp with { Level = SageLevel }
            : null;
    }

    // Spells granted by ONE specific NPC flow and by nothing else — never teachable at a path trainer, never
    // handed out by an @spells rebuild. Propose comes with the engagement ring you buy at the chapel
    // (ChapelAbility.BuyRing), which is its real cost and its real gate; SpellCosts' doc has always said so,
    // but the archive merge nonetheless wrote propose rows for Mage and Poet (level 11), and a SpellCosts row
    // is exactly what makes SpellsForClass offer a spell — so path leaders were teaching it. Those rows stay
    // in the CSV as the relearn-cost record they were extracted as; this is the gate.
    // The five Sage rungs join it for the same reason. The Sage in the wilderness is their only teacher
    // ("The Share wisdom spells can be learned from 'The Sage', an old man who lives in the wilderness at
    // 0126 0007" — tutor board), and npc_dialog.lua's SageNpc is that flow; but the archive merge wrote
    // share_wisdom rows for Warrior/Mage/Poet at level 90, so path leaders were selling rung 1 out from
    // under him. The upper four are already unreachable (SplPthId 99 matches no class), and are listed
    // anyway because SpellLearnCosts.csv is GENERATED — re/merge_spell_costs.py can hand any of them a row
    // on the next merge, and this gate has to hold when it does. The set is BUILT from SageLadder rather
    // than restating it, so adding a rung cannot leave the gate one key behind.
    private static readonly HashSet<string> NpcGrantedSpells =
        new(SageLadder.Append("propose"), StringComparer.OrdinalIgnoreCase);
    public static bool IsNpcGrantedOnly(SpellDef sp) => NpcGrantedSpells.Contains(sp.Key);

    /// <summary>Spells only ONE city's trainer teaches, keyed by <see cref="BaseKey"/> → <see cref="RegionOf"/>
    /// (0 Kugnae · 1 Buya · 3 Nagnang). Keying on the base key covers all four alignment reskins of each at once.
    ///
    /// <para>The rogue self-heal Remedies, verbatim from nexusatlas 2003-07-01 "Rogue Changes" (Rachel):
    /// <i>"Maro's Remedy: Learned in Kugnae only … Maso's Remedy: Learned in Buya only … Dagger's Remedy:
    /// Learned in Nagnang only"</i>. Maro, Maso and Dagger ARE the three cities' rogue trainers, so the lock is
    /// really "each trainer's own remedy", and the three make one ladder that climbs by travelling rather than
    /// by grinding a single guild — 1500/1000 vita/mana at level 99, then 3000/2000 at Il san, then 4500/3000
    /// at Ee san. Without this every rogue trainer offered all three the moment the rank was met.</para>
    ///
    /// <para>Explorer's Remedy is deliberately NOT here: the same post files it under <i>"(Rumored) Learned ??
    /// (New wilderness/vale NPC?)"</i>, so there is no city to pin it to.</para></summary>
    private static readonly Dictionary<string, int> CityLockedSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        ["maros_remedy"]   = 0,   // Kugnae  — Maro  (trainer on map 16, Maro Sanctum)
        ["masos_remedy"]   = 1,   // Buya    — Maso  (trainer on map 368, Maso Sanctum)
        ["daggers_remedy"] = 3,   // Nagnang — Dagger
    };

    /// <summary>The one region whose trainer teaches this spell, or -1 if any of them will.</summary>
    public static int CityLockOf(SpellDef sp) =>
        CityLockedSpells.TryGetValue(BaseKey(sp), out var region) ? region : -1;

    /// <summary>May a trainer standing in <paramref name="region"/> teach this spell? True for everything that
    /// isn't city-locked, so callers can filter unconditionally.</summary>
    public static bool TeachableInRegion(SpellDef sp, int region)
    {
        int locked = CityLockOf(sp);
        return locked < 0 || locked == region;
    }

    /// <summary>City name for a <see cref="RegionOf"/> id, for the "you'll have to go there" line the trainer
    /// shows in place of a locked secret. Falls back to the raw id so an unmapped region is still legible.</summary>
    public static string RegionCityName(int region) => region switch
    {
        0 => "Kugnae", 1 => "Buya", 2 => "the Mythic", 3 => "Nagnang", _ => $"region {region}",
    };

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
    /// sensible order. Spells switched off by an era gate (see <see cref="IsOutOfEraSplitTrap"/>) or owned by
    /// a single NPC flow (see <see cref="IsNpcGrantedOnly"/> — Propose) are dropped outright, so they never
    /// reach a tutor menu, the character rebuild, or the Divine Secret preview.
    /// <para><paramref name="mark"/> is the character's subpath rank, and gates the Il/Ee/Sam san secrets:
    /// they carry <c>SplMark</c> 1-3 and are pinned to <see cref="MarkSpellLevel"/>, so a level-99 base
    /// character sees none of them and an Ee san sees ranks 1 and 2 (ranks are cumulative — you keep what Il
    /// san taught you). Before this the column was read by nothing at all, which is how every level-99
    /// character ended up holding secrets belonging to ranks they had never earned.</para></summary>
    /// <para>Dog spells are NOT here and must not be added: "The guildmaster is not involved in these spells"
    /// (nexusatlas Dog Spells listing) — the class's Dog teaches them itself, in exchange for kills and goods.
    /// They carry <c>SplPthId</c> 99, which no class filter matches, so they drop out of this list naturally;
    /// see <see cref="CanLearnDogSpells"/> and the <c>DogLinguistNpc</c> handler in npc_dialog.lua.</para></summary>
    public static List<SpellDef> SpellsForClass(int pathId, int maxLevel, int alignment, int mark = 0)
    {
        // An NPC subpath IS its base class plus a little: a Chung ryong learns the whole Warrior list (the
        // learn-cost table and every SplPthId are keyed to the base four), then its own signature spell on top.
        // PathBaseOf is RTK's classdb_path, the same mapping the gear restriction already uses, so "a Chung
        // ryong may wear warrior gear" and "a Chung ryong learns warrior secrets" now come from one source.
        int basePath = PathBaseOf(pathId);
        bool isSubpath = pathId != basePath;                      // ANY subpath, PC or NPC — used for the
                                                                  // signature-spell rule further down

        return Spells.Where(s => (s.Alignment < 0 || s.Alignment == alignment) && s.Mark <= mark)
              .Select(s =>
                    isSubpath && s.PathId == pathId ? s with { Level = MarkSpellLevel }   // the subpath's own signature spell; you only get there at the cap
                  : IsUniversalBaseSpell(s) ? s                    // taught to EVERY class at its base level; SpellCosts rows are relearn-cost only
                  : SpellCosts.TryGetValue(s.Key, out var perClass)
                      ? (perClass.TryGetValue(basePath, out var cost) ? s with { Level = cost.Level } : null)
                      : (s.PathId == basePath || s.PathId == 0 ? s : null))
              .Where(s => s is not null && s.Level <= maxLevel && !IsOutOfEraSplitTrap(s) && !IsNpcGrantedOnly(s))
              .Select(s => s!)
              .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
              .Select(g => g.OrderByDescending(s => s.Alignment == alignment).ThenBy(s => s.Level).First())
              .OrderBy(s => s.Level).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
              .ToList();
    }

    // ---- DOG spells -----------------------------------------------------------------------------------
    // Two per base class, taught by the class's Dog and by nobody else ("The guildmaster is not involved in
    // these spells" — you kill something, come back, and hand over goods). They live in Spells.csv under the
    // "===DOG SPELLS===" divider with SplPthId 99 and SplLevel 0, so no class filter can reach them; the
    // whole flow lives in the DogLinguistNpc handler in npc_dialog.lua, which owns the per-class spell,
    // level and requirement table because that data IS the dialog.
    //
    // SOURCES, both era-correct for the 4.95 client (built 2001-06-29):
    //   * tswolf.com/spells/dog.shtml + /quests/dogling/, Wayback captures 2001-02-24 / 2001-03-11
    //   * nexusatlas.com/spells/dog.php, capture 2002-12-30 (re/fx/atlas_html/class_dog.html)
    // They disagree on some numbers; the atlas listing wins, the same tie-break already used for Siege's
    // aether — and our spell_effects rows were sourced from it, so they already agree. Divergences kept on
    // record: tswolf has Survive at 1000 mana (atlas 600), Spot Traps 60 (100), Serpent's Fury level 91 and
    // 500 mana (99 / 800), Spirit Fury 500 mana (1000). RTK's own Lua is a 2019-fork rewrite that swapped
    // both level-99 kill targets for a Golden lobster and Spirit Fury's Ambrosia for a Titanium glove; it is
    // the weakest of the three and is followed only where the other two are silent.
    //
    // ELIGIBILITY: "These spells are for people not in PC Subpaths. NPC Subpaths can get these spells as
    // well" (tswolf), i.e. the four base classes AND the four NPC subpaths, never a PC subpath — which is
    // exactly what CanLearnDogSpells says. (An earlier comment here claimed NPC subpaths ONLY; the code
    // never did that, and the archive confirms the code.)

    /// <summary>An NPC subpath (Chung ryong · Baekho · Ju jak · Hyun moo) as against a PC one (Barbarian,
    /// Monk, Druid, …). Paths.csv separates them in <c>PthIcon</c>: 4 for the four NPC subpaths, 1/2/3 for
    /// the twelve PC ones, 0 for the base classes and Archon.</summary>
    public const int NpcSubpathIcon = 4;
    public static bool IsNpcSubpath(int pathId) => PathIcon.GetValueOrDefault(pathId) == NpcSubpathIcon;

    /// <summary>The four base classes — Warrior · Rogue · Mage · Poet. Peasant (0) is not one.</summary>
    public static bool IsBaseClass(int pathId) => pathId >= 1 && pathId <= 4 && PathBaseOf(pathId) == pathId;

    /// <summary>May this path learn Dog spells AT ALL (before the Dog flag is even considered)?
    /// <para>Base classes and NPC subpaths yes; PC subpaths never — a Barbarian or a Monk cannot learn them,
    /// which matches "These spells are for people not in PC Subpaths. NPC Subpaths can get these spells as
    /// well" (tswolf 2001) and the atlas's "People in PC subpaths cannot learn these spells".</para>
    /// <para>This replaces a test of <c>pathId != basePath</c>, which was exactly inverted: it let all TWELVE
    /// PC subpaths through (Barbarian, Merchant, Diviner, Druid, Chongun, Ranger, Geomancer, Monk, Do, Spy,
    /// Shaman, Muse) while excluding the four base classes that should have them.</para></summary>
    public static bool CanLearnDogSpells(int pathId) => IsBaseClass(pathId) || IsNpcSubpath(pathId);

    /// <summary>Quest-registry key for the Dog flag — set when the Spotted dog finishes the bark/woof/grrowl
    /// chain, and the gate on saying "secret" to your own class's Dog. Lives in the flat
    /// <c>Character.Quests</c> map like every other quest flag, so no schema change.</summary>
    public const string DogFlagReg = "dog_flag";

    /// <summary>The two Dog spells each BASE class may hold, in teach order, with the level each is pinned
    /// to. Spells.csv cannot say either thing — all eight rows carry <c>SplPthId</c> 99 and <c>SplLevel</c> 0
    /// so that no class filter can reach them (see <see cref="SpellsForClass"/>) — so the pairing and the
    /// levels are declared here, mirroring the <c>DOG_SPELLS</c> table in npc_dialog.lua, which owns the
    /// other half of each tier (the kills, the goods and the atlas's 20,000-vita / 10,000-mana gate on the
    /// level-99 one) because that data IS the dialog. Same source as the Lua: the nexusatlas Dog Spells
    /// listing, capture 2002-12-30. <c>DogSpellTiersAreReal</c> in ContentSmokeTests pins the keys.</summary>
    private static readonly Dictionary<int, (string Key, int Level)[]> DogSpellTiers = new()
    {
        [1] = new[] { ("greater_blessing", 70), ("spirit_fury",   99) },   // Warrior
        [2] = new[] { ("spot_traps",       70), ("serpents_fury", 99) },   // Rogue
        [3] = new[] { ("fissure",          70), ("lava_surge",    99) },   // Mage
        [4] = new[] { ("survive",          70), ("fascinate",     99) },   // Poet
    };

    /// <summary>The Dog spells a character of this path holds at <paramref name="maxLevel"/>, level-stamped
    /// from <see cref="DogSpellTiers"/>. Empty unless the path is eligible (<see cref="CanLearnDogSpells"/>);
    /// an NPC subpath reads its BASE class's pair, exactly as it inherits that class's whole spell list.
    ///
    /// <para>THE DOG FLAG IS THE CALLER'S TEST, not this one's — the only caller is
    /// <see cref="RespecSpellSet"/>, and it passes the flag in. This deliberately does NOT re-check the
    /// kills/goods each tier costs at the Dog: the character rebuild grants what a character of this class
    /// and level WOULD hold, the same way it hands over tutor spells without charging the tutor's fee.</para></summary>
    public static List<SpellDef> DogSpellsFor(int pathId, int maxLevel)
    {
        var result = new List<SpellDef>();
        if (!CanLearnDogSpells(pathId)) return result;
        if (!DogSpellTiers.TryGetValue(PathBaseOf(pathId), out var tiers)) return result;
        foreach (var (key, level) in tiers)
            if (level <= maxLevel && SpellByKey(key) is { } sp)
                result.Add(sp with { Level = level });
        return result;
    }

    /// <summary>The level a mark (subpath-rank) spell is pinned to. Marks sit ON TOP of the level cap — an
    /// Il san is, in the user's words, "level 100" — so every rank spell needs level 99 first and then the
    /// rank. The CSV can't say that (SplLevel is 0 on all 121 of them), so <see cref="LoadSpells"/> floors
    /// them here and <see cref="SpellsForClass"/>'s ordinary <c>Level &lt;= maxLevel</c> test does the rest.</summary>
    public const int MarkSpellLevel = 99;

    /// <summary>The highest subpath rank a character may hold: <b>3, Sam san</b> — the last one that exists
    /// as content. Paths.csv names five ranks per path and Items.csv carries 34 Sa san (mark 4) items, but
    /// <b>Spells.csv stops dead at mark 3</b>: 46 mark-1 rows, 57 mark-2, 18 mark-3, and zero for 4 or 5.
    /// (That asymmetry is exactly what proved Sam san was an RTK implementation gap rather than an
    /// out-of-era feature — see the nexustk-495-subpath-spells note; Sa san is the same gap, one tier up and
    /// not yet closed.) Allowing rank 4 would mint a "Sa san" whose only difference from a Sam san is one
    /// more level of stat growth and a title, so the ladder stops here until those spells are written.
    /// <para>KNOWN CONSEQUENCE: the 34 mark-4 items stay unwearable, since the ItmMark gear gate reads the
    /// same field. That is the correct behaviour for a rank nobody can hold — Sa san gear is precisely what
    /// a Sa san would wear — and it reverses the moment this constant moves.</para></summary>
    public const int MaxMark = 3;

    // ---- ability LADDERS (the same ability, restated stronger, over and over) --------------------------
    // Mage learns nine single-target zaps that differ only in magnitude (Thunder Bolt -> Spark -> Singe ->
    // Ignite -> Ion -> Impact -> Call Lightning -> Stormstrike -> Hellfire), five 4-way ones, and nine heals
    // across two ladders. Learning ALL of them is what filled the book: 57 entries for a level-99 Ee san mage
    // against a 52-slot cap, so the tail was silently dropped. Once you have Hellfire, Thunder Bolt is not a
    // spell you would ever cast — so the character-rebuild grant (RespecSpellSet) keeps ONLY the top rung of
    // each ladder you qualify for. Every class ends up between 20 and 48 entries at every mark.
    //
    // Ladders are per-class and by SHAPE, not by archetype: single-target zap, 4-way zap, self-only heal,
    // targeted heal and 4-way heal are five separate ladders because each does something the others can't,
    // and a class keeps its best of each. Anything not listed here is never trimmed — buffs, curses, cures,
    // traps, summons and the mark secrets are all one-of-a-kind and all survive.
    //
    // Only the BASE (unaligned) key of each rung is listed; BuildAlignFamilies expands each to its Kwisin /
    // Mingken / Ohaeng reskins, which is why a ladder like the mage 4-way one is 5 entries here and covers
    // the same 20 keys AreaZapMana spells out by hand.
    //
    // This is the GM/tester grant ONLY. Tutor NPCs still offer the whole ladder (SpellsForClass is
    // untouched), because a real character climbing it one rung at a time is the entire point of the ladder.
    //
    // A RUNG MUST HAVE NO AETHER. A cooldown is what separates "the attack you press" from "the button you
    // save for one moment", and the two are not tiers of one ability no matter how the damage numbers rank:
    // you cannot fight with a spell you may cast once every 19 seconds, so collapsing the ladder onto one
    // takes the class's basic attack away rather than upgrading it. Two spells used to sit on ladders they do
    // not belong to, and both were the top rung, so both were doing exactly that:
    //   Hellfire     (mage, was the 9th zap rung) — mana 1000 PLUS 70% of the pool, aether 19000, damage
    //                 ceil(magic * 2.15). A mage who ran @lvl came out holding it INSTEAD of Stormstrike.
    //   Retribution  (poet, was the 4th zap rung) — mana 500 and empties the pool, aether 24000, damage
    //                 ceil(magic * .34). Latent: Flare outranked it, so it only bit in the level band where
    //                 Retribution was the highest rung a poet qualified for.
    // Both are still LEARNED — dropping a spell from LadderRungs doesn't remove it, it exempts it from the
    // collapse, which is how every other one-of-a-kind ability (Inferno, Dooms Fire, the traps, the summons)
    // already survives. The mage now ends up with Stormstrike AND Hellfire, which is the real spellbook.
    // BuildSpellLadders enforces this at load, so a rung added back here is dropped with a log line rather
    // than quietly eating a class's attack again.
    private static readonly Dictionary<int, Dictionary<string, string[]>> LadderRungs = new()
    {
        [1] = new()   // Warrior — no zap ladder; its damage skills (Taunt/Slash/Berserk/Whirlwind) are all
        {             // different mechanics, not tiers of one.
            ["heal_self"]   = new[] { "soothe", "relief_warrior", "vigor_warrior" },
            ["heal_target"] = new[] { "fleshspeak_warrior" },
        },
        [2] = new()   // Rogue
        {
            ["zap"]         = new[] { "singe_rogue", "ignite_rogue" },
            ["heal_self"]   = new[] { "soothe", "maros_remedy_rogue" },
            ["heal_target"] = new[] { "fleshspeak_rogue", "mend_wounds_rogue", "recover_rogue", "seal_wounds_rogue" },
            // Drain is Heal-archetype but it is a life-steal ATTACK, so it is not on the heal ladder.
        },
        [3] = new()   // Mage
        {
            ["zap"]         = new[] { "thunder_bolt_mage", "spark_mage", "singe_mage", "ignite_mage", "ion_mage",
                                      "impact_mage", "call_lightning_mage", "stormstrike_mage" },
            ["zap_area"]    = new[] { "erupt_mage", "ion_charge_mage", "explode_mage", "electrocute_mage", "tempest_mage" },
            ["heal_self"]   = new[] { "soothe", "lay_hands_mage", "relief_mage" },
            ["heal_target"] = new[] { "fleshspeak_mage", "mend_wounds_mage", "recover_mage", "heal_mage", "rejuvenate_mage" },
        },
        [4] = new()   // Poet
        {
            ["zap"]         = new[] { "spark_poet", "singe_poet", "ignite_poet", "earthquake_poet", "flare_poet" },
            ["heal_area"]   = new[] { "vital_spark_poet", "anoint_poet", "remedy_poet", "heavens_kiss_poet" },
            ["heal_self"]   = new[] { "lay_hands_poet", "fortify_poet" },
            ["heal_target"] = new[] { "recover_poet", "heal_poet", "revitalize_poet", "water_of_life_poet" },
            // Poet has no Soothe rung: it is the one class the live game refuses to (re)teach it to.
        },
    };

    /// <summary>(pathId, spell key) → (ladder id, rung index), expanded from <see cref="LadderRungs"/> through
    /// the alignment families. Keyed per path because <c>soothe</c> is the bottom rung of three different
    /// classes' self-heal ladders. The rung INDEX is what <see cref="RespecSpellSet"/> ranks by — see there
    /// for why the learn level can't be trusted to order a ladder.</summary>
    private static IReadOnlyDictionary<int, Dictionary<string, (string Ladder, int Rung)>> _ladderOf =
        new Dictionary<int, Dictionary<string, (string, int)>>();

    private static Dictionary<int, Dictionary<string, (string Ladder, int Rung)>> BuildSpellLadders(IReadOnlyList<SpellDef> spells)
    {
        var family = BuildAlignFamilies(spells);
        var byLeader = spells.GroupBy(s => family.GetValueOrDefault(s.Key, s.Key), StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<int, Dictionary<string, (string, int)>>();
        foreach (var (pathId, ladders) in LadderRungs)
        {
            var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ladderId, rungs) in ladders)
                for (int i = 0; i < rungs.Length; i++)
                    if (!byLeader.TryGetValue(rungs[i], out var siblings))
                        Log.Info($"!! spell ladder {pathId}/{ladderId}: no spell keyed '{rungs[i]}' — rung ignored");
                    // A spell on a cooldown is a different ability, not a louder version of this one — see the
                    // "A RUNG MUST HAVE NO AETHER" note on LadderRungs. Dropping it here leaves it OFF every
                    // ladder, which means RespecSpellSet keeps it outright instead of letting it displace the
                    // class's actual attack.
                    else if (siblings.Select(FxFor).FirstOrDefault(fx => fx is not null)?.Aether is > 0 and var aether)
                        Log.Info($"!! spell ladder {pathId}/{ladderId}: '{rungs[i]}' has a {aether}ms aether — " +
                                 $"not a rung, granted on its own instead");
                    else
                        foreach (var s in siblings) map[s.Key] = (ladderId, i);
            result[pathId] = map;
        }
        return result;
    }

    /// <summary>The EXACT book a character of this class/level/mark/alignment should hold — what @lvl,
    /// @class, @mark and @align rebuild to. <see cref="SpellsForClass"/> (which is also the tutor menu, and
    /// stays complete) narrowed to one rung per ladder: the HIGHEST RUNG of each that the character qualifies
    /// for, ties broken by SplId so the pick is stable across reloads.
    /// <para>Ranked by the rung's position in <see cref="LadderRungs"/>, NOT by its learn level. Level looks
    /// like the same thing and isn't: the alignment reskins carry their own <see cref="SpellCosts"/> rows and
    /// those rows do not agree with the tier order. The mage top zap is the case that proved it — Hellfire is
    /// level 99, but its Kwisin/Mingken/Ohaeng twins (Consume Soul / Flesh Eaters / Hurricane) are level 72,
    /// BELOW the 77 of the rung under them (River of Blood / Natural Disaster / Winds of Disaster). Ranking by
    /// level therefore handed every aligned mage the second-best zap and deleted the best one, while the
    /// unaligned mage — whose levels happen to ascend — got the right answer. The declared order is the
    /// authority on which rung is stronger; the level column only decides whether you have REACHED it (that
    /// gate already ran, in <see cref="SpellsForClass"/>).</para>
    /// <para><paramref name="dogFlag"/> is the character's finished-the-linguist-chain flag
    /// (<see cref="DogFlagReg"/>). When it is set, the class's Dog spells are merged in as well — see
    /// <see cref="DogSpellsFor"/>. They cannot arrive through <see cref="SpellsForClass"/>, which is also the
    /// tutor menu and must never show one, so this is the single place a rebuild can pick them up.</para></summary>
    public static List<SpellDef> RespecSpellSet(int pathId, int maxLevel, int alignment, int mark,
                                               bool dogFlag = false, int sageRung = 0)
    {
        var all = SpellsForClass(pathId, maxLevel, alignment, mark);

        // The Sage rung the character has PAID for (Content.SageRungReg), for the same reason as the Dog
        // spells below: @lvl/@class/@mark/@align rebuild rather than top up, and the ladder is bought a rung
        // at a time for 100,000 gold each with a 90-day wait between them. Without this, one @lvl silently
        // confiscated the whole thing — and unlike a tutor spell there is no way to buy it straight back.
        //
        // The rung comes from the registry rather than from the old book because the book is what is being
        // wiped. SpellsForClass can never supply it (IsNpcGrantedOnly drops all five), so this is the single
        // place a rebuild can pick one up. Level-gated inside SageSpellFor: drop to level 5 and it stops
        // applying, return to 90 and it comes back, exactly as the Dog tiers behave.
        if (SageSpellFor(sageRung, maxLevel) is { } sage) all.Add(sage);
        // The Dog spells, for a character who has finished the chain. Merged in level-stamped and re-sorted
        // into place, which is what makes "@dog 1" followed by "@lvl 70/99" produce the book a linguist of
        // this class really holds. Without this the rebuild silently forgot every Dog spell — including ones
        // earned honestly at the Dog, since @lvl/@class/@mark/@align rebuild rather than top up.
        //
        // The Dog's OWN price (the kills, the goods, and the atlas's 20,000-vita / 10,000-mana gate on the
        // level-99 tier, all in npc_dialog.lua's DOG_SPELLS) is deliberately NOT re-checked here: the rebuild
        // grants what a character of this class and level would hold, exactly as it hands over tutor spells
        // without charging the tutor. Level is the gate, as it is for every other entry in the set.
        //
        // KNOWN INTERACTION: the Dog's "cleanse" forgets the spells but keeps the legend and the flag (RTK
        // resets the teach progress instead), so a rebuild afterwards hands them straight back. That is the
        // rebuild behaving as designed — it restores the whole entitlement set — and it takes a staff command
        // to reach, so a player who cleansed still has the quest to walk again.
        if (dogFlag)
        {
            var dogs = DogSpellsFor(pathId, maxLevel);
            if (dogs.Count > 0)
                all = all.Concat(dogs).OrderBy(s => s.Level)
                         .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        // Ladders are declared per BASE class, and an NPC subpath inherits its base class's whole list, so
        // it inherits the ladders with it. The two Dog spells are deliberately not on any ladder: Fissure ->
        // Lava Surge is a tier pair, but it is two spells from a separate trainer, and collapsing it would
        // erase half of the only reward the subpath grants outright.
        if (!_ladderOf.TryGetValue(PathBaseOf(pathId), out var ladders)) return all;

        var top = new Dictionary<string, (SpellDef Spell, int Rung)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in all)
            if (ladders.TryGetValue(s.Key, out var rung)
                && (!top.TryGetValue(rung.Ladder, out var best) || rung.Rung > best.Rung
                                                               || (rung.Rung == best.Rung && s.Id > best.Spell.Id)))
                top[rung.Ladder] = (s, rung.Rung);

        // Soothe is the exception to the collapse: a rebuilt character always keeps it ALONGSIDE the best
        // self-heal it qualifies for, never instead of it. It sits at the BOTTOM rung of the Warrior/Rogue/
        // Mage heal_self ladders, so the normal top-rung filter would drop it the moment a character out-levels
        // it — but Soothe is the first-steps heal every class is expected to still have, so it is exempted here
        // the same way an aether-bearing rung is. (Poet's heal_self ladder has no soothe rung — LadderRungs[4]
        // — so Poet's Soothe, granted as a universal base spell, was already passing through untouched; this
        // clause is a no-op there.)
        return all.Where(s => s.Key.Equals("soothe", StringComparison.OrdinalIgnoreCase)
                              || !ladders.TryGetValue(s.Key, out var rung)
                              || ReferenceEquals(top[rung.Ladder].Spell, s)).ToList();
    }

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
            // MapSpells is the opposite polarity to MapChat despite sitting two columns away: 1 = casting is
            // ALLOWED here, 0 = "That doesn't work here." (RTK map[m].spell, gated in clif.c's 0x0F case).
            // Unknown/blank defaults to allowed, so only an explicit 0 blocks.
            bool canCast = col.GetValueOrDefault("MapSpells", "1") != "0";
            // MapIndoor (RTK map[m].indoor) — set on every town interior, cave and dungeon. Used here only as
            // the weather gate (WeatherModel.For): no rain/snow indoors. Deliberately NOT reused as a casting
            // gate — casting has to work in caves, which is why MapSpells above is the separate no-cast flag.
            bool indoor = col.GetValueOrDefault("MapIndoor", "0") == "1";
            meta[id] = new MapMetaInfo(region, warpOut, pvp, canTalk, canCast,
                Rd("MapReqLvl"), Rd("MapReqPath"), Rd("MapReqMark"), Rl("MapReqVita"), Rl("MapReqMana"),
                Rd("MapLvlMax"), Rl("MapVitaMax"), Rl("MapManaMax"), Clean(col.GetValueOrDefault("MapRejectMsg", "")), indoor);
        }
        return meta;
    }

    // Prey creatures — see LoadMobFlees / MobDef.Flees. Loaded BEFORE Mobs so LoadMobs can fold the flag in.
    private static Dictionary<string, bool> MobFleeOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>game-data/MobFlees.csv (`Identifier,Flees`) — which creatures RUN AWAY rather than fight.
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

    private static Dictionary<string, bool> MobStationaryOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>game-data/MobStationary.csv (`Identifier,Stationary`) — creatures that never take a step.
    /// <para>Our world gives every spawned mob the same idle wander (World.Materialize sets
    /// <c>Wander = true</c>), because RTK's per-mob movement lives in each mob's own AI script rather than in
    /// its DB row: <c>Mobs/captured_leviathan.lua</c>'s <c>move</c> only turns the sprite on the spot, never
    /// calls <c>mob:move()</c>. A caged captive that strolls two tiles out of its pen looks broken AND breaks
    /// the quest tile that has to find it (see Server/LeviathanQuest.cs). Sparse, same shape and reasoning as
    /// <see cref="LoadMobFlees"/>: kept out of mobs.csv so re-running the mob extractor can't drop it, and it
    /// hot-reloads with @reload.</para></summary>
    private static Dictionary<string, bool> LoadMobStationary(string? path)
    {
        var still = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var key = Clean(col.GetValueOrDefault("Identifier", ""));
            if (key.Length == 0) continue;
            still[key] = col.GetValueOrDefault("Stationary", "0").Trim() != "0";
        }
        return still;
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
            // SpawnTime: blank (a mob the RTK dump didn't carry) falls back to that table's own SQL default
            // rather than to our old cadence. 0 is a REAL value there, not "unset" — two creatures ship with
            // it and RTK revives them on the next AI pass — so an explicit 0 is honoured.
            int spawnTime = int.TryParse(col.GetValueOrDefault("SpawnTime", ""), out var st) && st >= 0
                ? st : DefaultSpawnTimeSec;
            mobs.Add(new MobDef(id, key, name, look, color, hp <= 0 ? 1 : hp, exp, lvl, move, will, aggressive, minDam, maxDam, isBoss, protection, hit, ac, grace,
                Flees: MobFleeOverrides.GetValueOrDefault(key),
                Stationary: MobStationaryOverrides.GetValueOrDefault(key),
                SpawnTime: spawnTime));
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

    // ShopBuysFrom.csv is ShopStock.csv's shape with one addition: a lone "-" is an EXPLICIT empty list —
    // "this shop buys nothing" (RTK's chapel, with boss-drop sales off, and the druid who won't take your
    // meat). It has to survive as a present-but-empty entry, because an ABSENT key means the opposite:
    // "no list known, so buy anything" (see ShopBuysFrom / Shops.BuysFrom).
    private static Dictionary<string, string[]> LoadShopBuysFrom(string? path)
    {
        var lists = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var id = Clean(col.GetValueOrDefault("NpcIdentifier", ""));
            if (string.IsNullOrEmpty(id)) continue;
            lists[id] = Clean(col.GetValueOrDefault("ItemKeys", ""))
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Where(k => k != "-")
                .ToArray();
        }
        return lists;
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

            string lootCell = Clean(col.GetValueOrDefault("Loot", ""));
            string rareCell = Clean(col.GetValueOrDefault("RareLoot", ""));
            var loot = new List<LootRoll>();
            var rare = new List<RareRoll>();
            string? badEntry = null;

            foreach (string entry in lootCell.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = entry.Split(':');
                if (p.Length != 3)
                {
                    badEntry = $"Loot entry '{entry}'";
                    break;
                }
                if (!int.TryParse(p[1], out int amount)
                    || amount <= 0
                    || !double.TryParse(p[2], System.Globalization.NumberStyles.Float |
                        System.Globalization.NumberStyles.AllowThousands,
                        System.Globalization.CultureInfo.InvariantCulture, out double rate)
                    || !double.IsFinite(rate)
                    || rate < 0)
                {
                    badEntry = $"Loot entry '{entry}'";
                    break;
                }
                loot.Add(new LootRoll(p[0] == "GOLD" ? null : p[0], amount, rate));
            }

            if (badEntry is null)
            {
                foreach (string entry in rareCell.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = entry.Split(':');
                    if (p.Length != 2)
                    {
                        badEntry = $"RareLoot entry '{entry}'";
                        break;
                    }
                    if (!double.TryParse(p[1], System.Globalization.NumberStyles.Float |
                            System.Globalization.NumberStyles.AllowThousands,
                            System.Globalization.CultureInfo.InvariantCulture, out double rate)
                        || !double.IsFinite(rate)
                        || rate < 0)
                    {
                        badEntry = $"RareLoot entry '{entry}'";
                        break;
                    }
                    rare.Add(new RareRoll(p[0] == "GOLD" ? null : p[0], rate));
                }
            }

            if (badEntry is not null)
            {
                Log.Warn($"MobDrops.csv row MobKey='{key}' skipped: invalid {badEntry}; " +
                         $"row Loot='{lootCell}', RareLoot='{rareCell}'");
                continue;
            }

            if (loot.Count > 0 || rare.Count > 0) table[key] = new MobDropDef(loot.ToArray(), rare.ToArray());
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
                BreakOnDeath: I("ItmBoD") != 0, Protected: I("ItmProtected") != 0,
                Repairable: I("ItmRepairable") != 0,
                NoTrade: I("ItmExchangeable") != 0, NoDeposit: I("ItmDepositable") != 0));
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
    // Look,Colour,Palette from Mob5xPalettes.csv. (look, era colour byte) -> colour byte to send V533.
    private static Dictionary<(ushort, byte), byte> LoadMob5xPalettes(string? path)
    {
        var pals = new Dictionary<(ushort, byte), byte>();
        foreach (var col in ReadCsv(path))
            if (ushort.TryParse(col.GetValueOrDefault("Look"), out var look)
                && byte.TryParse(col.GetValueOrDefault("Colour"), out var colour)
                && byte.TryParse(col.GetValueOrDefault("Palette"), out var pal))
                pals[(look, colour)] = pal;
        return pals;
    }

    // Rows are body-look RANGES (a whole Body.tbl palette band is one row per dye) rather than one row per
    // look, so the file stays readable and a new armor on an existing body is covered without a data edit.
    private static Dictionary<(ushort, byte), byte> LoadArmorDyeRamps(string? path)
    {
        var map = new Dictionary<(ushort, byte), byte>();
        foreach (var col in ReadCsv(path))
            if (ushort.TryParse(col.GetValueOrDefault("BodyLookLo"), out var lo)
                && ushort.TryParse(col.GetValueOrDefault("BodyLookHi"), out var hi)
                && byte.TryParse(col.GetValueOrDefault("Dye"), out var dye)
                && byte.TryParse(col.GetValueOrDefault("Ramp"), out var ramp))
                for (ushort look = lo; look <= hi; look++) map[(look, dye)] = ramp;
        return map;
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
                // Which of the two systems this row belongs to is carried by the columns themselves rather
                // than by the file it came from: Timer > 0 (base + crafting handleSpawn rows) means the
                // batch group model, no Timer (the trap supplement) means the per-point model, and
                // RespawnSec only ever appears on the latter.
                int.TryParse(col.GetValueOrDefault("RespawnSec", "0"), out var respawnSec);
                int.TryParse(col.GetValueOrDefault("Timer", "0"), out var timer);
                int.TryParse(col.GetValueOrDefault("Group", "0"), out var group);
                spawns.Add(new AreaSpawnDef(mob, map, count, minX, minY, maxX, maxY, respawnSec, timer, group));
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

    // Stationary NPCs (our game-data/NPCs.csv). We keep only NPCs whose map the client can render and that
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
            // ...and an NPC who did not EXIST yet at the target date doesn't spawn either. This is the one era
            // gate that removes a being rather than muting one, and it is deliberately narrow: it is for an NPC
            // whose whole reason to stand there postdates us (Yarlof arrived with the 2005 Druid bouquet quest),
            // NOT for an old NPC who gained a new quest — gate that in his script and leave him standing. Blank
            // is the overwhelming majority and means undated, and an unknown key reads as present, so a typo
            // here can only leave someone in the world, never silently delete him.
            var eraFeature = Clean(col.GetValueOrDefault("EraFeature", ""));
            if (eraFeature.Length > 0 && !Era.Has(eraFeature)) enabled = false;
            npcs.Add(new NpcDef(id, key, name, map, x, y, Dir: 2, look, color,
                IsChar: Flag("NpcIsChar"), Shop: Flag("NpcIsShopNpc"),
                Repair: Flag("NpcIsRepairNpc"), Bank: Flag("NpcIsBankNpc"),
                MoveTime: move, ReturnDistance: leash, Enabled: enabled, EraFeature: eraFeature));
        }
        return npcs;
    }

    // Class/path table: PthId -> base class name (PthMark0), PLUS the whole PthMark0..15 rank ladder into
    // PathRanks — "Warrior · Il san (W) · Ee san (W) · …" for a base class, "Ju jak · Force · Inferno ·
    // Pandemonium · Catastrophe · Ju jak" for an NPC subpath. Those higher columns are what a character is
    // actually CALLED once it has a mark, and the rank names are not decoration: the warrior mark-2 spell is
    // literally "Assault" and Chung ryong's mark-2 title is "Assault", which is the clearest evidence that
    // SplMark and PthMarkN are the same rank axis. PthType is loaded alongside into PathBase (PathBaseOf).
    private const int MaxPathRank = 15;   // Paths.csv goes PthMark0..PthMark15; only 0..5 are ever populated

    private static Dictionary<int, string> LoadPaths(string? path)
    {
        var paths = new Dictionary<int, string>();
        var ranks = new Dictionary<int, string[]>();
        var bases = new Dictionary<int, int>();
        var icons = new Dictionary<int, int>();
        foreach (var col in ReadCsv(path))
            if (int.TryParse(col.GetValueOrDefault("PthId"), out var id))
            {
                var ladder = new string[MaxPathRank + 1];
                for (int m = 0; m <= MaxPathRank; m++) ladder[m] = Clean(col.GetValueOrDefault($"PthMark{m}", ""));
                paths[id] = ladder[0];
                ranks[id] = ladder;
                bases[id] = int.TryParse(col.GetValueOrDefault("PthType"), out var t) ? t : 0;
                icons[id] = int.TryParse(col.GetValueOrDefault("PthIcon"), out var ic) ? ic : 0;
            }
        PathRanks = ranks;
        PathBase = bases;
        PathIcon = icons;
        return paths;
    }

    private static Dictionary<int, string[]> PathRanks = new();

    /// <summary>What a character of this path and mark is CALLED — Paths.csv <c>PthMark&lt;mark&gt;</c>.
    /// A Ju jak is "Force" at mark 1, "Inferno" at 2, "Pandemonium" at 3, "Catastrophe" at 4; a Warrior is
    /// "Il san (W)" at 1. Falls back to the base name (mark 0) for a blank or out-of-range column, so a rank
    /// nobody named still reads as the class rather than as an empty string.</summary>
    public static string PathTitle(int pathId, int mark)
    {
        if (!PathRanks.TryGetValue(pathId, out var ladder)) return PathName(pathId);
        if (mark > 0 && mark < ladder.Length && ladder[mark].Length > 0) return ladder[mark];
        return ladder[0].Length > 0 ? ladder[0] : PathName(pathId);
    }

    /// <summary>Resolve any class OR rank name to the path and mark it denotes: "Mage" → (3, 0),
    /// "Inferno" → (8, 2), "Il san (W)" → (1, 1). Null if it names nothing. Base names are indexed first, so
    /// a string that is one path's class name and another's rank title always resolves to the class.</summary>
    public static (int PathId, int Mark)? PathRankForName(string? name)
    {
        var n = (name ?? "").Trim();
        return n.Length != 0 && _pathRankByName.TryGetValue(n, out var v) ? v : null;
    }

    private static IReadOnlyDictionary<string, (int PathId, int Mark)> _pathRankByName =
        new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths a character may actually BE: the four base classes and Peasant, plus the four NPC
    /// subpaths (Chung ryong / Baekho / Ju jak / Hyun moo). Everything else in Paths.csv is either RTK's GM
    /// branch (5 Dreamweaver, 22 Archon) or a PC subpath (10-21 Barbarian … Muse) that this server does not
    /// model — no spells, no promotion NPC, and in the PC subpaths' case a rank ladder that would silently
    /// hand out the base class's secrets under the wrong titles.</summary>
    public static readonly IReadOnlySet<int> PlayablePaths = new HashSet<int> { 0, 1, 2, 3, 4, 6, 7, 8, 9 };

    public static bool IsPlayablePath(int pathId) => PlayablePaths.Contains(pathId);

    /// <summary>The playable paths in display order, as "&lt;name&gt;" strings for a usage line.</summary>
    public static IEnumerable<string> PlayablePathNames() => PlayablePaths.Select(PathName);

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

    // ---- Star/Moon/Sun armor quest gates (game-data/ArmorQuests.csv) ------------------------------
    /// <summary>Level + karma tier each armor chain demands, keyed by (base path id, tier name). The tiers
    /// live in a file because that is the one field the period sources genuinely fight over — see the
    /// header comment in ArmorQuests.csv. A missing row falls back to <see cref="ArmorQuest"/>'s own
    /// defaults, so a deleted file degrades to the shipped values rather than an open gate.</summary>
    public static IReadOnlyDictionary<(int Path, string Tier), (int Level, string Karma)> ArmorQuestGates
    { get; private set; } = new Dictionary<(int, string), (int, string)>();

    private static Dictionary<(int, string), (int, string)> LoadArmorQuestGates(string? path)
    {
        var gates = new Dictionary<(int, string), (int, string)>();
        foreach (var col in ReadCsv(path))
        {
            if (!int.TryParse(col.GetValueOrDefault("Path"), out var p)) continue;
            var tier = col.GetValueOrDefault("Tier", "").Trim().ToLowerInvariant();
            if (tier.Length == 0) continue;
            if (!int.TryParse(col.GetValueOrDefault("Level"), out var lvl)) continue;
            var karma = col.GetValueOrDefault("Karma", "").Trim();
            if (karma.Length == 0) continue;
            gates[(p, tier)] = (lvl, karma);
        }
        return gates;
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

    // See WarpQuestLocks above. A row whose MinStage doesn't parse is skipped rather than defaulted to 0 —
    // a lock that silently became "always open" would look identical to no lock at all, and the whole
    // point of one is that the player isn't carried onward early.
    private static Dictionary<(ushort From, ushort To), WarpQuestLock> LoadWarpQuestLocks(string? path)
    {
        var bars = new Dictionary<(ushort, ushort), WarpQuestLock>();
        foreach (var col in ReadCsv(path))
        {
            if (!ushort.TryParse(col.GetValueOrDefault("FromMap"), out var from)) continue;
            if (!ushort.TryParse(col.GetValueOrDefault("ToMap"), out var to)) continue;
            var key = col.GetValueOrDefault("QuestKey", "").Trim();
            if (key.Length == 0) continue;
            if (!int.TryParse(col.GetValueOrDefault("MinStage"), out var min)) continue;
            var msg = col.GetValueOrDefault("Message", "").Trim();
            if (msg.Length == 0) msg = "You are not yet ready to proceed.";
            bars[(from, to)] = new WarpQuestLock(from, to, key, min, msg);
        }
        return bars;
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

    // See MythicAlliances above. One row per zodiac animal. KeyBosses/ItemBosses are ';'-separated, cave 1
    // first; a row is dropped unless BOTH name at least one boss and the row names an enemy, because a
    // half-declared alliance would offer a quest that can never be finished and would look to a player
    // exactly like a very hard one.
    private static List<MythicAllianceDef> LoadMythicAlliances(string? path)
    {
        var list = new List<MythicAllianceDef>();
        foreach (var col in ReadCsv(path))
        {
            var animal = col.GetValueOrDefault("Animal", "").Trim();
            var enemy  = col.GetValueOrDefault("Enemy", "").Trim();
            if (animal.Length == 0 || enemy.Length == 0) continue;

            static string[] Split(string? v) =>
                (v ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int I(string k, int dflt = 0) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : dflt;

            var keyBosses  = Split(col.GetValueOrDefault("KeyBosses"));
            var itemBosses = Split(col.GetValueOrDefault("ItemBosses"));
            if (keyBosses.Length == 0 || itemBosses.Length == 0) continue;

            list.Add(new MythicAllianceDef(
                animal, I("NpcId"), enemy, keyBosses, itemBosses,
                col.GetValueOrDefault("KeyDrop", "").Trim(),  I("KeyTribute"),
                col.GetValueOrDefault("ItemDrop", "").Trim(), I("ItemTribute"),
                col.GetValueOrDefault("Favor", "").Trim(),
                uint.TryParse(col.GetValueOrDefault("Exp"), out var xp) ? xp : 0u,
                double.TryParse(col.GetValueOrDefault("Karma"), out var km) ? km : 0.0,
                col.GetValueOrDefault("Sources", "")));
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

    // See EventCaveBands above. One row per band of the shared tier ladder, matched in file order, so the
    // FILE's order is the semantics — do not sort it. Blank/absent Mark columns give 0..0, which is what a
    // pure level band wants (a subpath rank only exists at 99). A malformed/absent file yields an empty
    // ladder, which makes every event-cave doorway refuse rather than dumping people into tier 1 blind.
    private static List<EventCaveBand> LoadEventCaveBands(string? path)
    {
        var list = new List<EventCaveBand>();
        foreach (var col in ReadCsv(path))
        {
            int I(string k, int dflt = 0) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : dflt;
            int tier = I("Tier");
            if (tier <= 0) continue;
            list.Add(new EventCaveBand(tier, I("AltTier"), I("MinLevel"), I("MaxLevel"), I("MinMark"), I("MaxMark"),
                Clean(col.GetValueOrDefault("Label", "")), col.GetValueOrDefault("Sources", "")));
        }
        return list;
    }

    // See EventCaves above. One row per entrance. EntranceTiles is ';'-separated "x:y" (same encoding as
    // MythicCaves/ArenaDoors); TierMaps and Pages are '|'-separated, shallowest page/tier first. A row with
    // no tiles or no destination maps is dropped rather than half-registered — a doorway that intercepts the
    // step and then has nowhere to send anyone is worse than one that stays an ordinary tile.
    private static List<EventCaveDef> LoadEventCaves(string? path)
    {
        var list = new List<EventCaveDef>();
        foreach (var col in ReadCsv(path))
        {
            var key = Clean(col.GetValueOrDefault("Key", ""));
            if (key.Length == 0) continue;
            ushort U(string k) => ushort.TryParse(col.GetValueOrDefault(k), out var v) ? v : (ushort)0;
            string S(string k) => Clean(col.GetValueOrDefault(k, ""));

            var tiles = new List<(ushort X, ushort Y)>();
            foreach (var pair in (col.GetValueOrDefault("EntranceTiles") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = pair.Split(':');
                if (xy.Length == 2 && ushort.TryParse(xy[0].Trim(), out var tx) && ushort.TryParse(xy[1].Trim(), out var ty))
                    tiles.Add((tx, ty));
            }
            if (tiles.Count == 0) continue;

            var maps = (col.GetValueOrDefault("TierMaps") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(m => ushort.TryParse(m.Trim(), out var mv) ? mv : (ushort)0)
                .Where(m => m != 0).ToArray();
            if (maps.Length == 0) continue;

            var pages = S("Pages").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

            list.Add(new EventCaveDef(key, U("EntranceMap"), tiles.ToArray(), maps, U("DestX"), U("DestY"),
                pages, S("Prompt"), S("OptionNear"), S("OptionFar"), S("DenyMsg"),
                col.GetValueOrDefault("Sources", "")));
        }
        return list;
    }

    // ---- Location / warp geometry loaders (see the Content.* registries near MythicCaves) ----------------

    // MusicTracks.csv: Track,Name,Kind[,Set] — the id<->name table for both soundtracks. `Kind` is what the
    // client will be asked to open, and it implies the rest:
    //   midi    -> 0x19 type 2, MusicSet.Old   (N.mid, ids 1-12 only — both clients hard-cap there)
    //   mp3     -> 0x19 type 1, MusicSet.New   (one song; on 5.33 it repeats forever, see the MusicTrack doc)
    //   list    -> 0x19 type 1, MusicSet.New   (%08d.LST — ten tracks in order, wraps forever. Map music.)
    //   shuffle -> 0x19 type 1, MusicSet.New   (%08d.LSR — the same ten from a random start, but STALLS:
    //                                           audition only, never a map assignment. See MusicTrack.)
    // An explicit `Set` column overrides the Kind's default set; a row with neither reads as an old midi,
    // which is what every row of this file was before the 5.x set existed.
    private static List<MusicTrack> LoadMusicTracks(string? path)
    {
        var list = new List<MusicTrack>();
        foreach (var col in ReadCsv(path))
        {
            if (!ushort.TryParse(col.GetValueOrDefault("Track"), out var id)) continue;
            var name = col.GetValueOrDefault("Name", "").Trim();
            var kind = col.GetValueOrDefault("Kind", "").Trim().ToLowerInvariant();
            bool playlist = kind is "list" or "shuffle";
            bool shuffle = kind is "shuffle";
            byte type = kind is "mp3" or "list" or "shuffle" ? (byte)1 : (byte)2;
            // Legacy `Type` column still wins if a deployed CSV predates `Kind`.
            if (kind.Length == 0 && byte.TryParse(col.GetValueOrDefault("Type"), out var t)) type = t;
            var set = col.GetValueOrDefault("Set", "").Trim()
                .Equals("new", StringComparison.OrdinalIgnoreCase) || type == 1 ? MusicSet.New : MusicSet.Old;
            list.Add(new MusicTrack(id, name, type, set, playlist, shuffle));
        }
        return list;
    }

    // MapBgm.csv: Zone,Track,Track5x,Maps,Names — one row per AREA. `Track` (the old/midi soundtrack) and
    // `Track5x` (the 5.x one) are each a MusicTracks.csv name or a raw id; `Maps` is a ';'-separated list of
    // ids and lo-hi ranges; `Names` is a ';'-separated list of map-name globs. The row whose Zone is
    // "Default" is pulled out as the fresh-session fallback (DefaultBgm / DefaultBgmNew).
    private static (List<BgmZone>, (ushort, byte)?, (ushort, byte)?) LoadBgmZones(string? path)
    {
        var zones = new List<BgmZone>();
        (ushort, byte)? def = null, defNew = null;

        foreach (var col in ReadCsv(path))
        {
            var zone = col.GetValueOrDefault("Zone", "").Trim();
            var track = FindTrack(col.GetValueOrDefault("Track", ""));
            if (zone.Length == 0 || track is null) continue;
            // No Track5x -> the zone's midi, which 5.33 plays too (its Snd.dat carries the same 12 files).
            var track5x = FindTrack(col.GetValueOrDefault("Track5x", ""), MusicSet.New) ?? track;

            if (zone.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                def = (track.Id, track.Type);
                defNew = (track5x.Id, track5x.Type);
                continue;
            }

            var maps = new List<(ushort, ushort)>();
            foreach (var part in col.GetValueOrDefault("Maps", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var span = part.Split('-', 2);
                if (ushort.TryParse(span[0].Trim(), out var lo))
                    maps.Add((lo, span.Length > 1 && ushort.TryParse(span[1].Trim(), out var hi) ? hi : lo));
            }
            var names = col.GetValueOrDefault("Names", "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            zones.Add(new BgmZone(zone, track.Id, track.Type, track5x.Id, track5x.Type, maps, names));
        }
        return (zones, def, defNew);
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
            // Blank/unparseable X2,Y2 collapses the box to the single tile X,Y — the normal case.
            if (!ushort.TryParse(col.GetValueOrDefault("X2"), out var x2) || x2 < x) x2 = x;
            if (!ushort.TryParse(col.GetValueOrDefault("Y2"), out var y2) || y2 < y) y2 = y;
            if (!acc.TryGetValue(g, out var list)) acc[g] = list = new List<InnDef>();
            list.Add(new InnDef(m, x, y, x2, y2));
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

    // HarvestNodes.csv. Weighted cells are `key:number` pipe-separated; a cell with no number defaults to
    // weight 1 so a single-item table can be written as just the key.
    private static Dictionary<string, HarvestNodeDef> LoadHarvestNodes(string? path)
    {
        var d = new Dictionary<string, HarvestNodeDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var node = Clean(col.GetValueOrDefault("NodeMob", ""));
            if (node.Length == 0) continue;
            int I(string k) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : 0;
            (string, double)[] Weighted(string k) =>
                Clean(col.GetValueOrDefault(k, "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Split(':'))
                    .Select(p => (p[0].Trim(),
                                  p.Length > 1 && double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 1))
                    .Where(t => t.Item1.Length > 0).ToArray();

            d[node] = new HarvestNodeDef(node,
                Clean(col.GetValueOrDefault("Tools", "")).Split('|', StringSplitOptions.RemoveEmptyEntries),
                Clean(col.GetValueOrDefault("Skill", "")),
                Weighted("Yield"), I("Rolls"), Weighted("Bonus"),
                Clean(col.GetValueOrDefault("BreakChance", "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, out var v) ? v : 0).ToArray(),
                Clean(col.GetValueOrDefault("Message", "")));
        }
        return d;
    }

    // MobSpells.csv — several rows per mob, kept in file order (the roll walks them and takes the first hit).
    private static Dictionary<string, MobSpellDef[]> LoadMobSpells(string? path)
    {
        var d = new Dictionary<string, List<MobSpellDef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var key = Clean(col.GetValueOrDefault("MobKey", ""));
            if (key.Length == 0) continue;
            int I(string k, int dflt = 0) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : dflt;
            d.TryAdd(key, new List<MobSpellDef>());
            d[key].Add(new MobSpellDef(key,
                Clean(col.GetValueOrDefault("Name", "")), Clean(col.GetValueOrDefault("Effect", "")).ToLowerInvariant(),
                I("Chance", 1), I("EveryMs"), I("Range", 1), I("Amount"),
                Clean(col.GetValueOrDefault("Stat", "")), Clean(col.GetValueOrDefault("Category", "")),
                I("DurationMs"), I("Anim"), I("Sound"), Clean(col.GetValueOrDefault("Say", "")),
                I("PerTick"), I("TickMinMs"), I("TickMaxMs"),
                Clean(col.GetValueOrDefault("Trigger", "")).ToLowerInvariant()));
        }
        return d.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    // MobSpawnRules.csv. The "*" row is the global default rather than a mob (see MobHpJitter).
    private static Dictionary<string, MobSpawnRuleDef> LoadMobSpawnRules(string? path)
    {
        var d = new Dictionary<string, MobSpawnRuleDef>(StringComparer.OrdinalIgnoreCase);
        MobHpJitter = false;
        foreach (var col in ReadCsv(path))
        {
            var key = Clean(col.GetValueOrDefault("MobKey", ""));
            if (key.Length == 0) continue;
            if (key == "*") { MobHpJitter = Clean(col.GetValueOrDefault("HpJitter", "")) == "1"; continue; }

            var rooms = Clean(col.GetValueOrDefault("Rooms", "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split(':'))
                .Where(p => p.Length == 3)
                .Select(p => (ushort.TryParse(p[0], out var mp) ? mp : (ushort)0,
                              ushort.TryParse(p[1], out var x) ? x : (ushort)0,
                              ushort.TryParse(p[2], out var y) ? y : (ushort)0))
                .Where(t => t.Item1 != 0).ToArray();
            int.TryParse(col.GetValueOrDefault("MaxAlive"), out var max);
            var capMaps = Clean(col.GetValueOrDefault("CapMaps", "")).Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => ushort.TryParse(s, out var v) ? v : (ushort)0).Where(v => v != 0).ToArray();
            int.TryParse(col.GetValueOrDefault("FleeBelowPct"), out var fleePct);
            int.TryParse(col.GetValueOrDefault("SpawnChance"), out var chance);
            int.TryParse(col.GetValueOrDefault("DeathCooldownSec"), out var cooldown);
            if (rooms.Length == 0 && max <= 0 && fleePct <= 0 && chance <= 0 && cooldown <= 0) continue;
            d[key] = new MobSpawnRuleDef(key, rooms, max, capMaps, fleePct, chance, cooldown);
        }
        return d;
    }

    private static Dictionary<string, MobBossDef> LoadMobBosses(string? path)
    {
        var d = new Dictionary<string, MobBossDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var key = Clean(col.GetValueOrDefault("MobKey", ""));
            if (key.Length == 0) continue;
            int I(string k, int dflt = 0) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : dflt;
            d[key] = new MobBossDef(key, I("HealAmount"), I("HealChance", 2), I("ParaBreakChance", 2),
                                    I("LastStandMs"), I("Anim"), I("Sound"));
        }
        return d;
    }

    // MobChatter.csv — Lines is |-separated.
    private static Dictionary<string, MobChatterDef> LoadMobChatter(string? path)
    {
        var d = new Dictionary<string, MobChatterDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in ReadCsv(path))
        {
            var key = Clean(col.GetValueOrDefault("MobKey", ""));
            if (key.Length == 0) continue;
            var lines = Clean(col.GetValueOrDefault("Lines", "")).Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;
            int.TryParse(col.GetValueOrDefault("Chance"), out var chance);
            byte.TryParse(col.GetValueOrDefault("Channel"), out var channel);
            d[key] = new MobChatterDef(key, Math.Max(1, chance), channel, lines);
        }
        return d;
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

    // AmbushBursts.csv: burst-table name -> its list of weighted variant mob-id vectors. A trap firing picks
    // one variant at random and spawns every id in it. Extractor-generated (re/extract_ambush_tables.py).
    private static Dictionary<string, IReadOnlyList<int[]>> LoadAmbushBursts(string? path)
    {
        var acc = new Dictionary<string, List<int[]>>();
        foreach (var col in ReadCsv(path))
        {
            var table = col.GetValueOrDefault("Table", "").Trim();
            if (table.Length == 0) continue;
            var ids = (col.GetValueOrDefault("MobIds") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).Where(v => v > 0).ToArray();
            if (ids.Length == 0) continue;
            if (!acc.TryGetValue(table, out var list)) { list = new(); acc[table] = list; }
            list.Add(ids);
        }
        return acc.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int[]>)kv.Value);
    }

    // AmbushConfig.csv: per-map trap trigger config. Maps column is a ';'-list of single ids and "lo-hi"
    // ranges (already the concrete tier maps — no auto-expansion, since each tier points at its own burst
    // table). Primary is "burst:<table>" | "single:<id>" | "ogre:<id>[/<altId>/<altChance>]" | "".
    private static Dictionary<ushort, AmbushMapDef> LoadAmbushConfig(string? path, IReadOnlyDictionary<string, IReadOnlyList<int[]>> bursts)
    {
        var d = new Dictionary<ushort, AmbushMapDef>();
        foreach (var col in ReadCsv(path))
        {
            var maps = ParseMapList(col.GetValueOrDefault("Maps", ""));
            if (maps.Count == 0) continue;
            int I(string k, int dflt) => int.TryParse(col.GetValueOrDefault(k), out var v) ? v : dflt;
            var def = new AmbushMapDef
            {
                Count = I("Count", 12), MobCap = I("MobCap", 50),
                Message = col.GetValueOrDefault("Message", "You stepped on a trap!"),
                SentryTable = col.GetValueOrDefault("SentryTable", "").Trim(), SentryTopY = I("SentryTopY", 0),
                BigTable = col.GetValueOrDefault("BigTable", "").Trim(), BigChance = I("BigChance", 0),
            };
            var primary = col.GetValueOrDefault("Primary", "").Trim();
            if (primary.StartsWith("burst:")) { def.PrimaryKind = "burst"; def.PrimaryTable = primary["burst:".Length..]; }
            else if (primary.StartsWith("single:")) { def.PrimaryKind = "single"; int.TryParse(primary["single:".Length..], out def.PrimaryMob); }
            else if (primary.StartsWith("ogre:"))
            {
                def.PrimaryKind = "ogre";
                var parts = primary["ogre:".Length..].Split('/');
                int.TryParse(parts[0], out def.PrimaryMob);
                if (parts.Length >= 3) { int.TryParse(parts[1], out def.OgreAltMob); int.TryParse(parts[2], out def.OgreAltChance); }
            }
            // Fail loud on a config that points at a burst table the extractor didn't produce (typo / stale name).
            foreach (var t in new[] { def.PrimaryTable, def.SentryTable, def.BigTable })
                if (t.Length > 0 && !bursts.ContainsKey(t))
                    Log.Info($"WARN AmbushConfig: map(s) '{col.GetValueOrDefault("Maps")}' reference unknown burst table '{t}'");
            foreach (var m in maps) d[m] = def;
        }
        return d;
    }

    // Parse a ';'-list of map ids where each entry is a single id ("208") or an inclusive "lo-hi" range ("90-96").
    private static List<ushort> ParseMapList(string s)
    {
        var result = new List<ushort>();
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            int dash = p.IndexOf('-');
            if (dash > 0 && ushort.TryParse(p[..dash], out var lo) && ushort.TryParse(p[(dash + 1)..], out var hi))
                for (int m = lo; m <= hi; m++) result.Add((ushort)m);
            else if (ushort.TryParse(p, out var one)) result.Add(one);
        }
        return result;
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
            // StartDx: where the run begins relative to THIS tile, same convention as DoorObjects.csv's own
            // startDx column. A two-tile door registers both of its halves, the right one with -1, so it
            // toggles as a unit whichever half the player happens to be facing.
            int.TryParse(col.GetValueOrDefault("StartDx"), out var startDx);
            d[(m, x, y)] = new Doors.DoorConfig(
                Locked: B("Locked", false),
                Key: string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
                ConsumeKey: B("ConsumeKey", true),
                ForceOpen: B("ForceOpen", false),
                ClosedObjs: closed,
                OpenObjs: open,
                DefaultClosed: B("DefaultClosed", false),
                StartDx: startDx);
        }
        return d;
    }

    // Per-path cumulative-exp-to-level table (RTK rtk/db/level_db.txt, classdb_level): LevelExp[path][level] =
    // total exp needed to LEAVE `level` (i.e. reach level+1). Long-format CSV (game-data/LevelExp.csv,
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
    // Loaded from game-data/SpellMods.csv (`rage` column) in Load() — see LoadSpellMods.
    private static IReadOnlyDictionary<string, int> RageAmount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rage multiplier this spell/skill arms, or null if it isn't a rage-tier spell. See
    /// <see cref="RageAmount"/>.</summary>
    public static int? RageAmountFor(SpellDef sp) => RageAmount.TryGetValue(sp.Key, out var r) ? r : null;

    // (IsChungRyongRage lived here: a hardcoded key check that routed Chung Ryong's Rage to its Lua verb.
    // It now binds through a SpellParams.csv row like Baekho's Cunning, the spell it mirrors, so the key
    // test is gone. It is still absent from SpellMods.csv — an INCREMENTAL fury must not reach the flat
    // RageAmountFor path, whose "already benefiting from a fury" block would forbid the tier climb.)

    // RTK warrior/enchant.lua, infuse.lua, ingress.lua, vipers_venom.lua, dragons_flame.lua + rogue/
    // tigers_fortitude.lua, baekhos_blade.lua: a weapon-enchant STANCE (player.enchant). Unlike rage (which
    // swingDamage.lua multiplies the WHOLE swing by), enchant only multiplies the raw weapon-swing term
    // (s/2) — see Session.PlayerSwingDamage. All 16 identifiers share one mutual-exclusion group (RTK
    // "enchants" checkIfCast table, spellTables.lua) — casting any one while another (or itself) is already
    // active just re-prints "This spell is already active.", never stacks/upgrades (Session.CastEnchant).
    // Mana/level are hardcoded straight from each spell's own Lua (not trusted from the CSV export — same
    // Type-5-skill gap as rage/stealth/sacrifice-strikes; tigers_fortitude_rogue genuinely costs 0 mana,
    // just consumes cast components via requirements()).
    // Loaded from game-data/SpellMods.csv (`enchantAmt`/`enchantMana` columns) in Load() — see LoadSpellMods.
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

    // ---- overhead cast shouts --------------------------------------------------------------------------------
    // A handful of warrior/rogue power strikes make the caster SHOUT a short word in blue over their own head
    // as they cast (the live game's own flavor; RTK player:talk(2, "…") without a name prefix). Berserk,
    // Whirlwind, Desperate Attack and Lethal Strike route through the sacrifice verb; Assault runs the generic
    // Damage archetype. Session.ApplyCast emits the shout (LuaShout) once the cast is confirmed. Keyed by the
    // sacrifice family for the four strikes, and by key for the Assault reskins (same DisplayName "Assault").
    private static readonly HashSet<string> AssaultShoutKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "assault_warrior", "deaths_challenge_warrior", "cold_snap_warrior", "volley_warrior", "assault",
    };
    /// <summary>The blue over-head word this spell shouts when cast, or null for the vast majority that shout
    /// nothing. See AssaultShoutKeys / the SacrificeFamily switch.</summary>
    public static string? OverheadShoutFor(SpellDef sp)
    {
        if (AssaultShoutKeys.Contains(sp.Key)) return "Assault~!";
        return SacrificeFamilyFor(sp) switch
        {
            SacrificeFamily.Berserk         => "K'YA~!",
            SacrificeFamily.Whirlwind       => "Sa-AAA~~!",
            SacrificeFamily.DesperateAttack => "Ka~!",
            SacrificeFamily.LethalStrike    => "Ka~~!",
            _                               => null,
        };
    }

    /// <summary>Physical melee power-strikes: the sacrifice family (Berserk / Whirlwind / Desperate Attack /
    /// Lethal Strike / Focused Blow / Siege) plus the vita-funded Chin-Baek warrior strikes (Slash, Assault &amp;
    /// reskins, Feral Berserk). These are swings, not spells, so <see cref="Session.HandleCast"/> plays the
    /// attack pose (0x1A type 1) rather than the magic cast pose (type 6). Everything else casts as normal.</summary>
    public static bool ShowsSwingAnim(SpellDef sp) => SacrificeFamilyFor(sp) is not null || TakesChinBaekHoRyung(sp);

    /// <summary>The 0x1A action type the caster strikes when this spell is cast: a physical strike swings
    /// (type 1); a spell whose <c>action</c> cell is an emote-range value (9–28) casts with that body emote —
    /// e.g. the furies use 18, the 'h'/rage emote; everything else uses the default magic pose (type 6).</summary>
    public static byte CastActionType(SpellDef sp)
    {
        if (ShowsSwingAnim(sp)) return 1;
        var fx = FxFor(sp);
        if (fx is not null && fx.Action >= 9 && fx.Action <= 28) return (byte)fx.Action;
        return 6;
    }

    // ---- Wisdom / "Listen to advice" hints -----------------------------------------------------------------
    // The periodic gameplay tips the "Listen to advice" option (0x1b sub-4) streams into the chat channel every
    // ~15 minutes (RTK pc_timer's advice[] via msg type 99 -> 11). Adapted from RTK's list to this server's own
    // systems and wording (Central Functions, board-signs, the group-exp bonus, level-5 path choice).
    private static readonly string[] AdviceHints =
    {
        "Your legend is the history of your character — from birth to every accomplishment since.",
        "Press F1 to open Central Functions and learn more about your character.",
        "Visit a trainer to learn new spells. At level 5, you may choose your path.",
        "Be courteous to your fellow players and obey the laws of the land.",
        "Subpaths are chosen at level 99 by seeking out the leader of the subpath you desire.",
        "Travel to the neighboring towns and cities to learn how to craft finer gear.",
        "Face a board and press it to read the notices there, or to post your own.",
        "Group with others to share the hunt — a party earns more experience together.",
    };
    /// <summary>A random "Listen to advice" hint, or null if the list is empty. See Session.SendAdvice.</summary>
    public static string? RandomAdvice() => AdviceHints.Length == 0 ? null : AdviceHints[Random.Shared.Next(AdviceHints.Length)];

    // Sam San one-offs that fit no existing archetype (nexusatlas 2004; the 2002-10-01 TSWolf announcement
    // names Mend Equipment "Luster return" and Spirit Salvation, i.e. these are alignment aliases whose other
    // identifiers we do not know - only the unaligned key is wired).
    public static bool IsMendEquipment(SpellDef sp) => sp.Key == "mend_equipment_warrior";

    // NPC-subpath guardian spells (their own SplPthId: 8 Ju Jak, 9 Hyun Moo). Both had Spells.csv rows but no
    // archetype, so they spent mana and did nothing. Hyun Moo Revival is the one spell MEANT to be cast while
    // dead, which is why Session.HandleCast exempts it from "Spirits can't cast spells".
    // ---- POST-CAST POOL DRAIN (a FRACTION of the mana you held when you cast) -------------------------------
    // A handful of mage nukes charge a share of the CURRENT pool on top of (or instead of) the flat per-spell
    // `mana` column, and every one of them computes both its damage AND its cost from the mana held BEFORE the
    // cast. Session.ApplyCast subtracts this after a successful cast, from the pre-cast reading (the amount is
    // evaluated first, so the ordering is safe and matches RTK's).
    //
    // <c>GateOnly</c> says what the row's own `mana` number MEANT in the RTK script: false = the shared
    // global_zap helper really did debit it, so the fraction is charged ON TOP; true = it was never spent at
    // all, only tested as a minimum-to-cast, so the archetype's debit is handed back and the fraction is the
    // whole price.
    //
    // 1.0 - "Takes all mana when cast and does that much damage times N" (nexusatlas): Inferno x1.5 (Ee San
    //   mage) and Dooms Fire x2.5 (Sam San mage). Their spell_effects rows carry mana=0 and an amountExpr
    //   reading player.magic, which computes the damage correctly but NEVER SPENT the pool - so before this
    //   they were free, repeatable nukes scaling off a mana bar that never moved. Retribution and its three
    //   reskins (RTK poet/retribution.lua, `player.magic = 0` after a successful global_zap) are the same
    //   thing one tier down: "deals 34% of current mana to target", and it empties you doing it.
    // 0.7 - Hellfire and its three alignment reskins. RTK Spells/mage/hellfire.lua takes its cost TWICE:
    //   global_zap debits the 1000 it is handed (which is the only number the formula extractor could see, and
    //   so the only one in spell_effects.csv), and then the script itself subtracts a second
    //   `manaTaken = floor(player.magic * .7)` computed from the pre-cast pool. That second debit was dropped
    //   on export, which is why the game's biggest zap - damage = ceil(mana * 2.15) - cost a flat 1000 out of
    //   a five-figure pool and read as consuming no mana at all.
    // 1/3 - Restore (RTK poet/restore.lua). Its own description is "Heals a target for 150% caster mana,
    //   removes 1/3 of caster mana", and the script does exactly that: `magic = 1000` is compared against and
    //   never subtracted, then `player.magic = ceil(player.magic * 2/3)`. So this is the GateOnly case - the
    //   1000 in the row is the bar you must clear to cast, not the bill.
    private readonly record struct ManaDrain(double Fraction, bool GateOnly);
    private static readonly Dictionary<string, ManaDrain> PostCastManaDrain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inferno_mage"] = new(1.0, false), ["dooms_fire_mage"] = new(1.0, false),

        ["hellfire_mage"] = new(0.7, false),     ["consume_soul_mage"] = new(0.7, false),
        ["flesh_eaters_mage"] = new(0.7, false), ["hurricane_mage"] = new(0.7, false),

        ["retribution_poet"] = new(1.0, false),   ["spirit_puppet_poet"] = new(1.0, false),
        ["palm_of_life_poet"] = new(1.0, false),  ["tornado_poet"] = new(1.0, false),

        ["restore_poet"] = new(1.0 / 3.0, true),
    };
    /// <summary>Fraction of the PRE-CAST mana pool this spell burns after it lands, and whether its
    /// <c>spell_effects.csv</c> mana cost was a minimum-to-cast gate rather than a real debit (in which case
    /// the caller hands that back first). <c>(0, false)</c> for everything not in the table above.</summary>
    public static (double Fraction, bool GateOnly) PostCastManaDrainFor(SpellDef sp) =>
        PostCastManaDrain.TryGetValue(sp.Key, out var d) ? (d.Fraction, d.GateOnly) : (0.0, false);

    // ---- POST-CAST VITA COST (the warrior "pay in blood" strikes) -------------------------------------------
    // The self-sacrifice FAMILY (Lethal Strike / Desperate Attack / Berserk / Whirlwind / Focused Blow / Siege)
    // has always charged its share of vita — that lives in the `sacrifice` verb, which owns the whole strike.
    // These two do NOT: they are ordinary Damage-archetype spells that happen to end their RTK script by
    // assigning `player.health` a fraction of itself, which no column in the export can express. So they were
    // free — all of the damage, none of the price.
    //   Slash  (RTK warrior/slash.lua):  `endvita = math.floor(player.health * 0.90)` -> keep 90%.
    //   Assault + its 3 reskins (warrior/assault.lua): `player.health = damage`, damage = ceil(health / 2)
    //     -> keep 50%. (RTK re-uses the damage variable, so a Chin Baek Ho Ryung buff up at cast time makes it
    //     keep 75% instead — a buff that REDUCES your own cost is plainly a slip in their script, and porting
    //     it is not worth the mechanic it would break, so the flat half is what runs here.)
    //   Feral Berserk (warrior/feral_berserk.lua): `player.health = ceil(player.health * 0.3333)`. It is the
    //     one-of-a-kind upgrade the Berserk family's `on_learn` REPLACES those spells with, which is why it
    //     sits outside SacrificeAliases and missed the vita charge the family's own verb applies.
    // All three are charged only when the strike LANDS, matching RTK (slash inside its `#d > 0` branch,
    // assault and feral berserk behind their `landed == 1` flag) — a swing at empty air costs the mana only.
    private static readonly Dictionary<string, double> PostCastVitaKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        ["slash_warrior"] = 0.90,
        ["assault_warrior"] = 0.50, ["deaths_challenge_warrior"] = 0.50,
        ["cold_snap_warrior"] = 0.50, ["volley_warrior"] = 0.50,
        ["feral_berserk_warrior"] = 0.3333,
    };
    /// <summary>Fraction of current vita left standing after this strike lands, or 1.0 (costs no vita) for
    /// everything outside the table above.</summary>
    public static double PostCastVitaKeepFor(SpellDef sp) => PostCastVitaKeep.GetValueOrDefault(sp.Key, 1.0);

    // ---- Chin-Baek-Ho-Ryung (the Black Potion's 10-second strike buff) --------------------------------------
    // RTK gives exactly five warrior strikes a x1.5 while `chin_baek_ho_ryung` is up, each with the same two
    // lines right after it computes its damage (warrior/{slash,assault,berserk,feral_berserk,whirlwind}.lua):
    //     if (player:hasDuration("chin_baek_ho_ryung")) then damage = math.ceil(damage * 1.5) end
    // The ward comes from ONE source, black_potion.lua, and nothing else in the tree sets it — which is what
    // makes this list exhaustive rather than a sample.
    //
    // Berserk and Whirlwind reach their x1.5 inside the `sacrifice` verb; the other three run the generic
    // Damage archetype, so their multiplier is applied to the evaluated amount in Session.CastArch. Both read
    // the same ward. (Before this they read `ctx.baekhoRage` — Baekho's RAGE, an ordinary fury tier, and a
    // ROGUE spell at that, on strikes only warriors can cast, so the bonus was unreachable. Two different
    // Baekhos; that primitive is gone, see the note where it used to live in Session.Spells.cs.)
    private static readonly HashSet<string> ChinBaekHoRyungStrikes = new(StringComparer.OrdinalIgnoreCase)
    {
        "slash_warrior", "feral_berserk_warrior",
        "assault_warrior", "deaths_challenge_warrior", "cold_snap_warrior", "volley_warrior",
    };
    /// <summary>The status key whose ward multiplies the five warrior strikes by 1.5 — one name, referenced by
    /// the Black Potion's ItemParams row, the strike list here, and the sacrifice verb.</summary>
    public const string ChinBaekHoRyung = "chin_baek_ho_ryung";
    /// <summary>Does this strike take the Chin-Baek-Ho-Ryung x1.5 when the ward is up? (Berserk and Whirlwind
    /// are absent on purpose — the sacrifice verb applies theirs.)</summary>
    public static bool TakesChinBaekHoRyung(SpellDef sp) => ChinBaekHoRyungStrikes.Contains(sp.Key);

    // ---- OVERFLOW from outside the sacrifice family ---------------------------------------------------------
    // The Overflow FAQ's warrior trigger list is "Slash, Berserk, Whirlwind, Siege", and Ixeus's revision adds
    // Feral Berserk by name — "the usual formulae like 0.75 x V for old Zerk, 0.85 x V for Feral Zerk". Nexus
    // Atlas agrees the list is broad: "Warrior overflow works on all of the warrior attacks, including their
    // Sam san attack" (Siege).
    //
    // Berserk / Whirlwind / Siege reach the splash from inside `verbs.sacrifice`. These two cannot: they are
    // ordinary Damage-archetype spells (their RTK scripts export cleanly, which is exactly why they are not in
    // SacrificeAliases — see PostCastVitaKeep), so the splash needs its own hook on that path. RTK gave neither
    // of them overflow — warrior/slash.lua calls removeHealthExtend and stops, with no Overflow.Cast in it at
    // all — so this list is the archive overriding the reimplementation, the same call as the PvP splash.
    //
    // Neither spell has alignment aliases (Spells.csv has exactly one row each), so the set is literal.
    // Era-gated with everything else (Era.WarriorOverflow), hence inert at the default 2001-07-09 date.
    //
    // ASSAULT IS DELIBERATELY ABSENT. It is a vita-funded warrior strike like these two and would be an easy
    // fifth entry, but no source lists it as an overflow trigger — and "absence of evidence never adds
    // content" cuts the same way here as it does for an era row.
    private static readonly HashSet<string> ArchetypeOverflowStrikes = new(StringComparer.OrdinalIgnoreCase)
    {
        "slash_warrior", "feral_berserk_warrior",
    };
    /// <summary>Does this spell splash its overkill even though it runs the generic <c>Damage</c> archetype
    /// rather than <c>verbs.sacrifice</c>? Slash and Feral Berserk only — see the note above.</summary>
    public static bool OverflowsFromDamageArchetype(SpellDef sp) => ArchetypeOverflowStrikes.Contains(sp.Key);

    // ---- flag-shaped wards that the engine ACTUALLY reads --------------------------------------------------
    // A ward set into the setDuration/hasDuration namespace does nothing on its own — it is a name and an
    // expiry. It matters only if something looks it up. Every one of these was, for a long time, written by a
    // potion and read by nobody, so drinking a Sanctuary potion or a Scroll of Immortality was a no-op the
    // player paid for. Wards with a spell twin now avoid this entirely by routing into the spell's own slot
    // (Session.ItemApplyWard); what remains here is the genuinely flag-shaped, and each name below is paired
    // with the code that reads it. ContentSmokeTests asserts no ItemParams ward escapes both lists.
    public static readonly IReadOnlySet<string> ReadStatusWards = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "purple_potion",                                        // Session.RegenTick — +20 to the vita regen term
        ChinBaekHoRyung,                                        // Session.CastArch + the sacrifice verb — x1.5
        "harden_body", "harden_body_poet", "deaths_guard_poet", // Session.DamageImmune — total damage immunity
        "lifes_protection_poet", "body_of_alignment_poet",
        "baekhos_cunning",                                      // spell_verbs.lua stance_cunning — its tier window
    };

    /// <summary>Ward categories <see cref="Session.ItemApplyWard"/> knows how to route into the spell system's
    /// own slots. A ward row naming anything else would silently apply nothing.</summary>
    public static readonly IReadOnlySet<string> ItemWardCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "deduction", "hardarmors", "protections", "bolsters", "curses", "minorcurses", "disheartens" };

    /// <summary>Every spell key carrying a post-cast pool cost, mana or vita. Both tables are keyed by hand,
    /// so a key that stops naming a real spell disables that cost SILENTLY — the spell keeps working, it just
    /// becomes free again. Exposed only so the content smoke test can assert they all still resolve.</summary>
    public static IEnumerable<string> PostCastCostKeys => PostCastManaDrain.Keys.Concat(PostCastVitaKeep.Keys);

    public static bool IsJuJakEvocation(SpellDef sp) => sp.Key == "ju_jak_evocation";
    public static bool IsHyunMooRevival(SpellDef sp) => sp.Key == "hyun_moo_revival";

    // ---- AREA (4-way) spells --------------------------------------------------------------------------------
    // The two ladders that hit the four cardinally-adjacent cells instead of a target: the mage zap line
    // (Erupt -> Ion Charge -> Explode -> Electrocute -> Tempest) and the poet heal line (Vital Spark -> Anoint
    // -> Remedy -> Heaven's Kiss), each with its four alignment reskins. Identified in the RTK Lua by the
    // literal `local x = {-1, 0, 1, 0}` cell walk (Spells/mage/{erupt,ion_charge,explode,electrocute,tempest}
    // .lua and Spells/poet/{vital_spark,anoint,remedy,heavens_kiss}.lua) — the ONLY spells in the whole tree
    // that have it, so the list below is exhaustive rather than a sample.
    //
    // A key set rather than a data column because the formula export can't see this: the extractor reads each
    // script's damage/heal expression and its global_zap/global_heal call, and both of those look identical to
    // a single-target spell. It is also why the export gave every one of them mana=0 — these debit up front,
    // outside the shared helper, so the helper's manacost argument is 0. The real per-family costs live in
    // AreaSpellMana below and are what Session.ApplyCast passes to the verb.
    //
    // Every one of them is SplType 5 (no target argument exists), which is what made the old single-target
    // dispatch answer "<name> finds no target." on every cast, spending nothing.
    private static readonly Dictionary<string, int> AreaZapMana = new(StringComparer.OrdinalIgnoreCase)
    {
        ["erupt_mage"] = 80,        ["soulstorm_mage"] = 80,       ["avalanche_mage"] = 80,        ["deluge_mage"] = 80,
        ["ion_charge_mage"] = 120,  ["crescendo_mage"] = 120,      ["flight_of_arrows_mage"] = 120,["blazing_sands_mage"] = 120,
        ["explode_mage"] = 180,     ["soul_chasm_mage"] = 180,     ["winters_vortex_mage"] = 180,  ["volcano_mage"] = 180,
        ["electrocute_mage"] = 250, ["eater_of_the_dead_mage"] = 250, ["forests_discord_mage"] = 250, ["shatter_storm_mage"] = 250,
        ["tempest_mage"] = 310,     ["dance_macabre_mage"] = 310,  ["wilding_mage"] = 310,         ["chain_lightning_mage"] = 310,
    };
    // The poet ladder is a flat 390 across all four tiers — the tiers differ only in how much they heal
    // (100 / 200 / 500 / 1000, which the export DID capture correctly in amountExpr).
    private static readonly HashSet<string> AreaHealSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        "vital_spark_poet", "spirits_kiss_poet", "spark_of_health_poet", "water_of_nature_poet",
        "anoint_poet", "brothers_of_spirit_poet", "gathering_of_power_poet", "natures_family_poet",
        "remedy_poet", "brethren_of_spirits_poet", "gathering_of_the_flock_poet", "gathering_of_majesty_poet",
        "heavens_kiss_poet", "clan_of_souls_poet", "healing_hand_poet", "earths_embrace_poet",
    };
    private const int AreaHealMana = 390;

    // ---- DOG (subpath) 5-way fire: Fissure and Lava Surge ----------------------------------------------------
    // Same shape as the mage 4-way ladder -- pay the mana up front, hand the shared helper manacost 0 so it
    // neither charges again nor fires a pose per hit, then one sendAction at the end -- but with two
    // differences that make them their own family (RTK Spells/dog/fissure.lua, lava_surge.lua):
    //   * the sweep is centred on the TARGET, not the caster (`target.x + x[i]`, not `player.x + x[i]`), and
    //   * the offset list leads with {0,0}, so the target's OWN tile is hit too -- FIVE cells, not four.
    // They were reaching the plain single-target Damage archetype here, i.e. hitting exactly one thing.
    //
    // RTK's own PvP branch has a bug we deliberately do not reproduce: it casts on `hits[1]` but then sends
    // the "<caster> cast X on you." line to `target` -- the ORIGINAL target -- so in a 5-cell sweep the
    // primary victim gets a line per bystander and the bystanders get none (fissure.lua:36-38). We message
    // whoever was actually hit.
    // Identified in the RTK Lua by the literal `local x = {0, -1, 0, 1, 0}` walk, exactly as the 4-way family
    // is identified by `{-1, 0, 1, 0}` — ELEVEN entries, every one of them centred on the target. This is the
    // exhaustive list, not a sample:
    //   dog   fissure · lava_surge                                     (Mage Dog, levels 70 / 99)
    //   mage  volcanic_blast_mage                                      (Il san mark spell)
    //   mage  inferno · deaths_door · natures_denial · steel_storm     (one ladder, 4 alignment reskins)
    //   poet  earthquake · tossing_the_bones · natures_fury · groundstrike  (ditto)
    // All eleven were reaching the plain single-target Damage archetype, i.e. hitting exactly one thing.
    private static readonly HashSet<string> TargetAreaZapSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        "fissure", "lava_surge", "volcanic_blast_mage",
        "inferno_mage", "deaths_door_mage", "natures_denial_mage", "steel_storm_mage",
        "earthquake_poet", "tossing_the_bones_poet", "natures_fury_poet", "groundstrike_poet",
    };

    /// <summary>The 5-way fire spells: the target's own tile plus its four sides, centred on the TARGET
    /// rather than on the caster.</summary>
    public static bool IsTargetAreaZap(SpellDef sp) => TargetAreaZapSpells.Contains(sp.Key);

    // The DOG/Il-san fire family — the subset of the eleven that Head Tutor Nussan's board entry actually
    // describes. Structurally interchangeable (volcanic_blast.lua is fissure.lua with a bigger number: same
    // 5-way walk, same 120 mana, no aether), and they are the ones carrying the two documented oddities:
    // "Can be cast extremely fast" (so: no cast delay) and "Misses sometimes if you're too far away".
    //
    // The other eight share the MECHANIC but not those properties, and nothing in the corpus says they should:
    // the Inferno ladder is gated by a 70-SECOND aether instead, and the Earthquake ladder is an ordinary
    // 90-mana poet attack. Both therefore keep the standard 1s cast delay and have no range miss.
    private static readonly HashSet<string> DogFireSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        "fissure", "lava_surge", "volcanic_blast_mage",
    };

    /// <summary>The Mage Dog / Il san fire family: castable "extremely fast" (no cast delay) and the only
    /// spells whose failure mode is range rather than a deflect roll.</summary>
    public static bool IsDogFireSpell(SpellDef sp) => DogFireSpells.Contains(sp.Key);

    // ---- CAST DELAY ----------------------------------------------------------------------------------------
    // A spell's CAST DELAY occupies the same slot as a melee swing, so casting one blocks swinging (and vice
    // versa) until it expires. It is NOT the same thing as the 3/sec action budget, which zaps also pay, and
    // it is NOT an aether -- Fissure is documented at "Aethers - 0 Seconds" while still being unspammable in
    // the ordinary sense.
    //
    // THREE INDEPENDENT SOURCES, since RTK models none of this (global_zap.lua sets no aether, canCast is
    // state-only, clif_parsemagic gates only aethers/silence/deflect, and no cast-delay field exists on the
    // user struct at all -- do not go looking for it in RTK):
    //
    //  1. LIVE CAPTURE (re/spell_rate_probe.py, 2026-08-14): a held cast key yields exactly ONE "You cast
    //     <name>." per second for Spark against exactly THREE per second for Soothe -- 36 confirmations in
    //     32 one-second windows vs 86 in 25 windows of three.
    //  2. USER, TESTING LIVE: "I cannot attack and cast Singe in the same 1s window." The delay is SHARED
    //     with the swing timer, which is why it belongs in Session.Combat.cs as _nextActionTick rather
    //     than in a per-spell cooldown table.
    //  3. PLAYER FEATURE REQUEST, official Dreams board (scraped_nexus_data .../Dreams/Mornelithe06071014.html):
    //         "~ Modify Taunt so it no longer has a cast delay and doesn't interfere with swinging"
    //         "~ Modify Invisible to not have a cast delay so it doesn't interfere with swinging"
    //     This is the source that proves the mechanism is GENERAL and named. Note what it names: Taunt is a
    //     global_zap caller (pcalign 13, "zaps w/o animation"), but INVISIBLE IS NOT A ZAP AT ALL. So a cast
    //     delay is a per-spell property that some non-zaps carry too -- hence CastDelayMs rather than an
    //     IsZap predicate. Both of those spells having one is era-correct: the post is asking for them to be
    //     changed BECAUSE they have it.
    //
    // SCOPE (user, from live play): EVERY attack spell, with the Dog fire family the single exception.
    // That includes the vita/mana-funded strikes -- Whirlwind, Lethal Strike, Slash, Siege, Assault -- and
    // the big pool-burners like Hellfire and Retribution, not just the elemental ladders.
    //
    // An earlier version of this tried to separate "real zaps" from "weapon strikes" by whether the export's
    // pcalign column held a number or the string "spellFX". That is deleted, and deliberately not coming
    // back: "spellFX" is not a game concept, it is what OUR extractor emits when a script has no zap
    // alignment, so it keyed combat behaviour off an artifact of our own tooling. It also needed a special
    // case on first contact (Assault is pcalign 0 and Volley is 3 -- inside the zap range, both 5000-mana
    // weapon strikes) and it mislabelled exactly the spells named above, all of which are "spellFX".
    //
    // There is no data signal for "vita-based" to key on either: healthCost is EMPTY on all 115 Damage rows,
    // because the in-script vita costs never survived the export (the extractor read only global_zap's
    // manacost ARGUMENT -- the same gap that lost Hellfire's 70%-of-pool). So archetype is the honest test.
    /// <summary>The cast delay, live-measured at exactly one second.</summary>
    public const int ZapCastDelayMs = 1000;

    /// <summary>How long this spell occupies the shared cast/swing slot. 0 = no cast delay, so it neither
    /// waits on a swing nor blocks the next one (it still pays the ordinary 3/sec action budget).</summary>
    public static int CastDelayMs(SpellDef sp)
    {
        // The Dog fire family is exempt on the tutor's own wording -- Head Tutor Nussan, Mages board:
        // "Aethers - 0 Seconds ... Can be cast extremely fast." That note appears on NO other attack spell
        // in the whole tutor corpus, which only means anything if the baseline attack is NOT fast -- so it
        // is corroboration for the 1s delay as much as it is an exemption from it.
        if (IsDogFireSpell(sp)) return 0;

        // INVISIBLE and its three alignment variants — the ONE non-attack family with a cast delay, and the
        // only one we have evidence for. The Dreams board post names it alongside Taunt: "Modify Invisible to
        // not have a cast delay so it doesn't interfere with swinging". Taunt needs no special case (it is a
        // Damage row), Invisible does: it is archetype Buff.
        //
        // Their export rows carry aether = 1000 -- EXACTLY the delay measured live on Spark -- and no mana at
        // all, which reads as the extractor having captured the cast delay itself into the aether column
        // rather than these having a genuinely separate 1s recast cooldown. Either way the observable
        // behaviour is the same 1s, so the slot claim is not double-charging: it just makes the wait SILENT
        // and shared with swinging, instead of the aether path's "Invisible isn't ready yet (0s)." -- a
        // message whose "(0s)" was always a sub-second remainder being floored, i.e. this same 1s.
        if (IsStealthSpell(sp)) return ZapCastDelayMs;

        var fx = FxFor(sp);
        return fx is not null && fx.Archetype == "Damage" ? ZapCastDelayMs : 0;
    }

    /// <summary>Is this one of the 4-way area spells, and if so which verb runs it and what does it cost?
    /// Null for everything else. <c>("area_zap", mana)</c> or <c>("area_heal", 390)</c>.</summary>
    public static (string Verb, int Mana)? AreaSpellFor(SpellDef sp) =>
        AreaZapMana.TryGetValue(sp.Key, out var m) ? ("area_zap", m)
        : AreaHealSpells.Contains(sp.Key) ? ("area_heal", AreaHealMana)
        : null;

    // ---- The one hold that reaches PLAYERS -------------------------------------------------------------------
    // Doze (lvl 82) and its three alignment reskins are the ONLY members of the blind/paralyze/sleep family
    // whose RTK script has a `BL_PC` branch:
    //     elseif (target.blType == BL_PC and player:canPK(target)) then …
    // Every other one — paralyze, static, blind, and Sleep (lvl 70, the cheaper-to-learn cousin that is
    // strictly stronger on monsters) — answers a player with "It doesn't work." / "Something went wrong."
    // So this is per-SPELL, not per-kind: Doze and Sleep share `debuff = sleep` and differ only here.
    // `canPK` is our IsPvpMap gate, so it lands in an arena and nowhere else.
    private static readonly HashSet<string> PlayerHoldSpells = new(StringComparer.OrdinalIgnoreCase)
        { "doze_mage", "voids_touch_mage", "still_ethers_mage", "still_waters_mage" };
    /// <summary>May this hold be cast on another PLAYER (in a PvP map)? True only for the Doze family.</summary>
    public static bool HoldHitsPlayers(SpellDef sp) => PlayerHoldSpells.Contains(sp.Key);

    // (The Sage ladder — Share Wisdom and its four upgrades, RTK Spells/common/sage.lua — needs no classifier
    // here: it is bound to the `sage_shout` verb by SpellParams rows, since its per-tier mana and cooldown are
    // the only things that differ between the five and both are plain numbers a row can carry.)

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
    // not a fixed RTK "aether"/cooldown).
    //
    // WE ADD NO COOLDOWN OF OUR OWN, and must not: per the user, Ambush runs on the ordinary 3-casts-per-
    // second action budget — three fire essentially instantaneously, a fourth in the same second does not.
    // That falls out for free, from two things worth not breaking: these rows are archetype Utility, so
    // Content.CastDelayMs gives them no cast delay; and Session.LuaAmbushStrike calls PlayerSwingDamage
    // directly rather than going through HandleAttack, so the leap-strike never claims the shared cast/swing
    // slot the way a real swing would (which would space them 333ms apart instead of letting all three land
    // together). An earlier comment here claimed a "short fixed cooldown" — there isn't one.
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
    // Loaded from game-data/Traps.csv (spell-side cast cost; kind = TrapKind enum name) in Load() — see
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
    // game-data/ServerTuning.csv and runs @reload. Bladestorm/Sword's Dance/Tiger's Ambush/Cutting Edge
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
    // Loaded from game-data/Morphs.csv (rows with an empty `answers` column) in Load() — see LoadMorphs.
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
    // Loaded from game-data/Morphs.csv (rows with a non-empty `answers` column, "ans:look;ans:look") in
    // Load() — see LoadMorphs.
    public static IReadOnlyDictionary<string, (Dictionary<string, ushort> Answers, int Mana, int DurationMs)> MorphDispatchSpells { get; private set; } =
        new Dictionary<string, (Dictionary<string, ushort>, int, int)>(StringComparer.OrdinalIgnoreCase);
    public static (Dictionary<string, ushort> Answers, int Mana, int DurationMs)? MorphDispatchFor(SpellDef sp) =>
        MorphDispatchSpells.TryGetValue(sp.Key, out var m) ? m : null;
    public static bool IsMorphSpell(SpellDef sp) => MorphSpells.ContainsKey(sp.Key) || MorphDispatchSpells.ContainsKey(sp.Key);

    // RTK Poet "Call of the Wild" pet-summon family (rtklua/Accepted/Spells/poet/cotw_*.lua): 7 tiers x 4
    // alignment reskins (28 identifiers) + a 29th, cotw_giasomo_bird_poet. That 29th is NOT part of the
    // learnable ladder: it has no requirements(), no Spells.csv row and no learn cost — it is fired only by
    // the Giasomo stick's on_swing proc (see game-data/WeaponProcs.csv). Its Lua asks for mob 807,
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
    // Loaded from game-data/Pets.csv in Load() — see LoadPets.
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
    // Loaded from game-data/SpellLevels.csv in Load() — see LoadSpellLevels. Assigned BEFORE Spells is
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

    // Weapon "on swing" procs (game-data/WeaponProcs.csv), ported from RTK's item on_swing handlers
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
            int.TryParse(col.GetValueOrDefault("SplMark", "0"), out var mark);
            // Every mark row carries SplLevel 0 (the rank IS the requirement — there is no level past 99),
            // so without this floor a mark spell reads as "learnable at level 1" and SpellsForClass hands
            // Il san and Ee san secrets to any level-99 base character. See MarkSpellLevel.
            if (mark > 0) lvl = Math.Max(lvl, MarkSpellLevel);
            var q = Clean(col.GetValueOrDefault("SplQuestion", ""));
            if (q.Equals("NO", StringComparison.OrdinalIgnoreCase)) q = "";
            bool canFail = col.GetValueOrDefault("SplCanFail", "0") == "1";   // RTK magicdb_canfail — gates the deflect roll
            spells.Add(new SpellDef(id, key, name, type, pth, lvl, align, q, canFail, mark));
        }
        return spells;
    }

    /// <summary>The alignment-reskin family each spell belongs to: key → the KEY OF ITS BASE (alignment 0 or
    /// universal) sibling. Most abilities exist four times over — <c>spark_mage</c> (unaligned) alongside
    /// <c>glimpse_of_the_void_mage</c> (Kwisin), <c>bolt_mage</c> (Mingken) and <c>natures_ire_mage</c>
    /// (Ohaeng) — and the four are stored as a consecutive run of SplIds within one (SplPthId, SplType)
    /// block, alignments ascending. That adjacency is the only thing in the data that links them: they share
    /// no name, no key stem and no level column. Walking it here means <see cref="SpellLadders"/> can be
    /// declared with ONE base key per tier instead of four, and stays correct for Kwisin/Mingken/Ohaeng
    /// characters for free.
    ///
    /// A new family starts at an alignment 0 row, at any universal (-1) row, at the first row after one, and
    /// wherever the alignment stops ascending. Validated by reconstructing <see cref="AreaZapMana"/>'s 20
    /// keys and <see cref="AreaHealSpells"/>'s 16 from their 5 and 4 base keys — both hand-curated from the
    /// RTK Lua years apart from this, and both come back exact.</summary>
    private static Dictionary<string, string> BuildAlignFamilies(IReadOnlyList<SpellDef> spells)
    {
        var leaderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in spells.OrderBy(s => s.Id).GroupBy(s => (s.PathId, s.Type)))
        {
            string leader = ""; int prev = int.MinValue;
            foreach (var s in block)
            {
                if (leader.Length == 0 || s.Alignment <= 0 || prev < 0 || s.Alignment < prev) leader = s.Key;
                leaderOf[s.Key] = leader;
                prev = s.Alignment;
            }
        }
        return leaderOf;
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
                CureCat: col.GetValueOrDefault("cureCat", "").Trim(),
                // The `class` column was read and thrown away until the zap cast-rate work needed it:
                // SplPthId can't stand in for it (Ion and Fissure are BOTH SplPthId 99, the shared path),
                // so this is the only per-spell class signal we have. See IsRateLimitedZap.
                Class: col.GetValueOrDefault("class", "").Trim(),
                // Cast POSE override (0x1A action type). RTK's sendAction(N) for the cast; normally 6 (magic
                // pose) and left at the default, but an emote-range value (>=9) lets a spell cast with a body
                // emote instead — e.g. the Rogue/Warrior furies use 18 (the 'h' rage emote). See CastActionType.
                Action: I(col, "action"));
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
        catch (Exception e) { Log.Warn($"CSV {path} is unreadable — treated as empty", e); yield break; }
        if (lines.Length < 2) yield break;

        // '#' opens a comment line, anywhere including above the header — these tables are hand-maintained and
        // the ones carrying a derivation (ArmorDyeRamps.csv) are unusable without somewhere to write it down.
        static bool Skip(string s) => string.IsNullOrWhiteSpace(s) || s.TrimStart().StartsWith('#');
        int h = 0;
        while (h < lines.Length && Skip(lines[h])) h++;
        if (h >= lines.Length - 1) yield break;

        var header = SplitCsv(lines[h]);
        for (int i = h + 1; i < lines.Length; i++)
        {
            if (Skip(lines[i])) continue;
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

    // Resolve a content file under the game-data root: per-file env override first, else
    // <root>/game-data/<parts...>. This used to carry its own copy of the walk up to the repo root, one of
    // five that had drifted apart; Shared/RepoPaths is now the single implementation, and its class doc
    // explains why every resolver has to agree on the fallback (briefly: a layout where the database
    // resolved but the content did not gave a server that started, listened and accepted logins into a
    // world with zero maps, zero mobs and zero NPCs, with nothing in the log that read as an error).
    private static string? ResolvePath(string envVar, params string[] parts) =>
        RepoPaths.GameData(envVar, parts);
}
