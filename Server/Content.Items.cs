namespace Server;

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

public static partial class Content
{
    public static IReadOnlyList<ItemDef> Items
    {
        get => _snapshotBuilder?.Items ?? Snapshot.Items;
        private set => Builder.Items = value;
    }

    // O(1) lookup indexes over the Items/Mobs/Spells lists + the class-name→path map, all rebuilt in Load() so
    // each is built after its source list in the unpublished builder. These replace the old
    // per-call LINQ FirstOrDefault scans over 2.5k items / 700 mobs / 900 spells, which ran on hot paths
    // (RegenTick, combat). FIRST occurrence wins on a duplicate id/key — matches the old FirstOrDefault. Key
    // lookups are case-insensitive.
    private static IReadOnlyDictionary<int, ItemDef> ItemByIdIndex
    {
        get => _snapshotBuilder?.ItemById ?? Snapshot.ItemById;
        set => Builder.ItemById = value;
    }
    private static IReadOnlyDictionary<string, ItemDef> ItemByKeyIndex
    {
        get => _snapshotBuilder?.ItemByKey ?? Snapshot.ItemByKey;
        set => Builder.ItemByKey = value;
    }

    // Armor-dye ramp remap, keyed (bodyLook, canonicalDye) -> the ramp to actually send in appearance[4].
    // The PLAYER equivalent of Mob5xPalettes above, and it exists for the same reason: appearance[4] is a
    // ramp shift resolved against the body sprite's OWN Body.tbl palette, so one canonical number is a
    // different hue on different armor. Only pairs that disagree with palette 0 are stored; everything
    // else passes through. Populated from ArmorDyeRamps.csv, which carries the full derivation. See Session.ArmorDye().
    public static IReadOnlyDictionary<(ushort Look, byte Dye), byte> ArmorDyeRamps
    {
        get => _snapshotBuilder?.ArmorDyeRamps ?? Snapshot.ArmorDyeRamps;
        private set => Builder.ArmorDyeRamps = value;
    }

    /// <summary>The byte to put in <c>appearance[4]</c> so <paramref name="dye"/> renders as its canonical
    /// colour on the body sprite <paramref name="look"/> is wearing. Identity unless ArmorDyeRamps.csv says
    /// this body's palette disagrees with the seasonal one.</summary>
    public static byte DyeRampFor(ushort look, byte dye) =>
        ArmorDyeRamps.TryGetValue((look, dye), out var r) ? r : dye;

    // Data-driven item use-effect params (game-data/ItemParams.csv): per item key, the raw CSV row its
    // Lua verb reads (the `verb` column + params like amount/hpcost/statuskey/duration). The "row" half of the
    // verb/row item-effect model — the "verb" logic lives in item_verbs.lua (see Server/ItemScript.cs +
    // Session.ApplyItemEffect). Items without a row fall back to the item DB's Vita/Mana. Hot-reloads via @reload.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ItemParams
    {
        get => _snapshotBuilder?.ItemParams ?? Snapshot.ItemParams;
        private set => Builder.ItemParams = value;
    }

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

    public static ItemDef? ItemById(int id) => ItemByIdIndex.TryGetValue(id, out var v) ? v : null;
    public static ItemDef? ItemByKey(string? key) => key is not null && ItemByKeyIndex.TryGetValue(key, out var v) ? v : null;

    public static List<ItemDef> SearchItems(string query, int limit) =>
        RankByName(Items, query, i => i.Name).Take(limit).ToList();

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

    private static IReadOnlyDictionary<string, WeaponProc> WeaponProcs
    {
        get => _snapshotBuilder?.WeaponProcs ?? Snapshot.WeaponProcs;
        set => Builder.WeaponProcs = value;
    }

    /// <summary>The on-swing proc for an equipped weapon/armour identifier, if it has one.</summary>
    public static WeaponProc? WeaponProcFor(string? itemKey) =>
        itemKey is not null && WeaponProcs.TryGetValue(itemKey, out var p) ? p : null;
}
