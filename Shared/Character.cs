namespace Shared;

/// <summary>
/// Minimal fabricated character for world-entry bring-up. Real characters will come from
/// Persistence later; for now this is a hardcoded spawn so we can drive the entry sequence.
/// </summary>
public sealed class Character
{
    public uint   Id     = 1;
    public string Name   = "snuggle";

    // location — defaults to just inside Ironheart's Home in Kugnae (map 36), the home-city coordinates a
    // brand new character actually starts at (see Session.PlaceNewCharacter/HomeCityFor — the door-arrival
    // tile from RTK's Warps.csv, map 0 (87/88,146) -> map 36 (5/6,10)). This compiled-in default only
    // matters if a Character is ever built without going through PlaceNewCharacter; both real entry points
    // (account creation, and world-entry with no saved character) call it explicitly, and a Buya-nation
    // character is placed at Jadespear (map 351) instead.
    public ushort Map    = 36;
    public ushort X      = 5;
    public ushort Y      = 10;
    public ushort MapXs  = 12;
    public ushort MapYs  = 12;

    // appearance
    public ushort Sex    = 1;
    public ushort Face   = 0;
    public ushort Hair   = 0;

    // War-paint dye applied to the worn armor/coat — renders as the 0x33 type-0 appearance[4] palette byte
    // (0 = undyed base color). Set by the Arena Master (WarPaintAbility, RTK arena_master.lua's "War paint").
    // Persisted so the dye survives a relog, and re-broadcast to peers via PlayerSnapshot so they see it too.
    public byte   ArmorColor = 0;

    // Mounted on a horse — renders as the 0x33 type-0 appearance[1] form byte = 3 (the client swaps the
    // human sprite for the horse+rider composite, SPR ids 344/345). Session-visual only; not persisted.
    public bool   Mounted = false;

    // Wielded weapon — renders as the 0x33 type-0 appearance[5] slot (look-lab: 0=unarmed, then
    // Honor sword / Flame blade / Electra / Steelthorn / Blood / Primogen …). Also drives the melee
    // damage bonus in Session.HandleAttack. Persisted so the weapon survives a relog.
    public byte   Weapon = 0;

    // vitals / stats
    public byte Nation   = 1;   // kingdom id -> HUD crest (NATION_E.EPF). See NationName for the table.
    public byte Totem    = 4;   // 4 = none

    /// <summary>Nation id -> name, confirmed empirically on the 4.95 HUD (see Session `!nat`). The
    /// names live in a client data file (no strings in the exe), so this table is the source of truth.</summary>
    public static readonly string[] Nations =
        { "Neutral", "Koguryo", "Buya", "Nagnang", "Shilla", "Jinhan", "Paekjae", "Kaya" };
    public static string NationName(byte id) => id < Nations.Length ? Nations[id] : $"nation#{id}";
    // MaxHp/MaxMp compiled-in fallback only matters if a Character is ever built without going through
    // CharacterFactory.PlaceNewCharacter (see there): real RTK's Player.reset rolls baseHealth =
    // random(45,55) and baseMagic = random(32,36) per new character, not a fixed 100/50 — PlaceNewCharacter
    // does that same roll for genuinely new characters. These constants are just the midpoint of that range.
    public byte Level    = 1;
    public uint MaxHp    = 50;
    public uint MaxMp    = 34;
    public uint Hp       = 50;
    public uint Mp       = 34;
    public byte Might    = 3;
    public byte Will     = 3;
    public byte Grace    = 3;
    public uint Exp      = 0;
    public uint Coins    = 0;
    public byte Armor    = 0;
    public byte MaxInv   = 27;

    // Bag + worn gear. Inventory slots are 0-based (0..MaxInv-1); Equipment entries carry the EQ index
    // (Item.EquipSlot's source) in their Slot field. Both persist in the character JSON so a relog keeps
    // the bag. Resolved against Content.Items (Server) by id — Shared holds only the per-character state.
    public List<InvItem> Inventory = new();
    public List<InvItem> Equipment = new();

    // Bank (vault) storage, filled at an inn/bank NPC. BankMoney is coin held on account; BankItems is the
    // stored stacks (their Slot is meaningless in the vault — a fresh bag slot is assigned on withdrawal).
    // Both persist in the character JSON, so the vault survives a relog.
    public uint          BankMoney = 0;
    public List<InvItem> BankItems = new();

    // Learned spells/skills, in spellbook order. Each entry is a Content.Spells id; the book slot the client
    // uses (the 0x17 "pos" and the 0x0F cast "pos") is the index into this list. Persisted so the book
    // survives a relog; re-sent on world entry. Taught by the !spells / !learnspell GM commands.
    public List<int> Spells = new();

    // Quest progress. Key = a quest id ("trial_of_iron"); value = its stage (0 = not started, 1 = active,
    // 2 = done — quests define their own meaning). Objective counters live under composite keys
    // ("trial_of_iron.kills"), so one flat map holds both stage machine and progress tallies. Persisted in
    // the character JSON, so an accepted quest and its progress survive a relog. See Server/Quests.cs.
    public Dictionary<string, int> Quests = new();

    // String-valued quest registry (RTK's registryString): e.g. the active minor-quest key. Kept separate
    // from the int map above so a quest can hold both a numeric stage and a string selection. Persisted.
    public Dictionary<string, string> QuestStrings = new();

    // Lifetime kills per mob key ("squirrel" -> 12), tallied on every world-mob kill. Quests read a delta of
    // this (RTK's player:killCount) — kill-since-accept — so a fresh kill after accepting counts. Persisted.
    public Dictionary<string, int> Kills = new();

    // Sub-alignment: 0 = Unaligned (base), 1 = Kwisin, 2 = Mingken, 3 = Ohaeng. Gates which spell set !spells
    // teaches — a character learns only universal spells + their own alignment's set, never the other
    // sub-alignments' parallel spells. Set with !align.
    public byte Alignment = 0;

    /// <summary>Sub-alignment id -> name (index = the Alignment value). Kwisin/Mingken/Ohaeng are NexusTK's
    /// three sub-paths; 0 is the base "unaligned" set.</summary>
    public static readonly string[] Alignments = { "Unaligned", "Kwisin", "Mingken", "Ohaeng" };
    public static string AlignmentName(byte id) => id < Alignments.Length ? Alignments[id] : $"align#{id}";

    // profile / "Mind's Eye" self-profile (0x39 = clif_mystaytus). These populate the profile window
    // the client opens on the profile key (client sends 0x2D, byte 0). AC is signed in TK (lower is
    // better); Dam/Hit are the melee bonus lines. Tnl = experience "to next level".
    // Ac is the NAKED base AC, held as a cache of the real NexusTK rule "base AC (naked) = 100 - level"
    // (Warrior Tutor Yttribium, confirmed live). Default 99 = 100 - 1 for a fresh level-1 character. It is
    // recomputed from level on world entry and on every level-up (Session.cs), never decremented in place,
    // and reaches 1 at level 99 for every class. Gear/buffs modify it at display/combat time, where the
    // -80 (human) / -95 (mob) damage-mitigation caps apply.
    public sbyte  Ac        = 99;
    public byte   Dam       = 0;
    public byte   Hit       = 0;
    public uint   Tnl       = 0;
    public string Title     = "";          // grade/honorific shown above the name (e.g. "Peasant")
    public string ClassName = "Peasant";   // path/class line
    public string ClanName  = "";          // guild/clan name ("" = clanless)
    public string ClanTitle = "";          // rank within the clan
    public string Spouse    = "";           // marriage line ("" = none)

    // Marriage in progress (RTK Spells/common/propose.lua + NPCs/Common/chapel_npc.lua). Fiance holds the
    // OTHER party's name while engaged ("" = not engaged); Spouse above takes over once actually married.
    // IsProposee marks which side ACCEPTED the proposal — RTK gates "only the proposee may start the
    // ceremony" on registry["partner2"]==self.ID, which this mirrors without needing a partner1/partner2
    // pair. MarriageTimer is the unix-seconds "not yet" gate on starting the ceremony (RTK's 3-day cool-down
    // after getting engaged); RingCooldown is the separate 24h gate on buying ANOTHER engagement ring.
    public string Fiance        = "";
    public bool   IsProposee    = false;
    public long   MarriageTimer = 0;
    public long   RingCooldown  = 0;

    // Ignore list (RTK map.h sd_ignorelist / clif.c ignorelist_add/remove/clif_isignore): case-insensitive
    // names. clif_isignore blocks a whisper if EITHER side has the other listed — see Session.DoWhisper.
    // RTK never exposes an in-game "friend list" (no such struct/feature exists in the C engine at all);
    // Friends is our own addition, purely a saved name list with an online/offline check on login — no RTK
    // source to port from, so there's no wire packet or server behavior beyond that to replicate.
    public List<string> IgnoreList = new();
    public List<string> Friends    = new();

    // Profile status toggles shown as the group/exchange indicator cells (self 0x39 + other-view 0x34).
    // Grouped = "sociable" (Shift+G, 0x1b/0x02); Exchange = trade allowed (0x1b/0x08). Persisted so they
    // survive reopening the profile and a relog. Group defaults off, exchange on.
    public bool   Grouped   = false;
    public bool   Exchange  = true;

    // Subpath chat (F2 toggle, RTK clif_handle_clickgetinfo's 0xFFFFFFFE sentinel + clif_sendsubpathmessage):
    // a server-wide chat channel reaching every OTHER online player who shares this character's ClassName and
    // also has it on. Off by default, like RTK. See Session.ToggleSubpathChat / DoSubpathChat.
    public bool   SubpathChat = false;

    // The writable "profile" page shown when someone clicks you (0x34): a free-text blurb the player
    // writes about their character, plus an optional drawn portrait bitmap. ProfilePic is the raw
    // bitmap bytes WITHOUT the size prefix (null/empty = no picture). Edited via the client's
    // change-profile packet (0x2F) and persisted.
    public string ProfileText = "This character has not written a profile yet.";
    public byte[]? ProfilePic = null;

    // RTK's in-game calendar (Scripts/scripts.lua curT(): "Yuri <year>, <season>") isn't modeled — no clock
    // ticks server-side — so every legend that would normally stamp "the current date" uses this fixed
    // constant instead. Value matches the live-captured self-profile reference below ("Hyul", not RTK's own
    // "Yuri" text — the two diverge and the live capture wins). Reused anywhere else a legend needs a date
    // (e.g. ChuRuaAbility's "Aided Chu Rua (...)").
    public const string GameDate = "Hyul 31, Winter";

    // Legend marks: the scrollable list at the bottom of the profile. Each = icon + color + text.
    // Seeded with a "born" entry (mirrors the real 6.x capture) so the window has visible content.
    public List<Legend> Legends = new()
    {
        // color 0x80 matches the real 6.x capture (`01 00 80 17 "Born…"`); color 0 renders invisible.
        new Legend(icon: 0, color: 0x80, text: $"Born in {GameDate}"),
    };

    // Raw body of the creation packet (0x04), kept verbatim until its appearance-byte layout is
    // decoded from the logged dump and mapped onto the fields above. Null for the default spawn.
    public byte[]? CreationBlob = null;
}

/// <summary>
/// One stack of a held item: which slot it sits in, the item-db id, how many, and current durability.
/// The definition (name/icon/stats) is looked up from the item registry by <see cref="ItemId"/>; only
/// the mutable per-character bits live here. CustomName overrides the display name when non-empty
/// (renamed/quest items). Used for both the bag (Slot = inventory index) and worn gear (Slot = EQ index).
/// </summary>
public sealed class InvItem
{
    public byte   Slot       = 0;
    public int    ItemId     = 0;
    public int    Amount     = 1;
    public ushort Dura       = 0;
    public string CustomName = "";

    // Durability warning-threshold stage (RTK sd->status.equip[x].repair): 0 = full/no warning sent yet,
    // 1..5 = the 50/25/10/5/1% warnings already sent, so each only fires once as Dura counts down. Only
    // meaningful for worn equipment (Session.CheckDura); bag items stay at 0.
    public byte Repair = 0;

    public InvItem() { }
    public InvItem(byte slot, int itemId, int amount, ushort dura = 0)
    { Slot = slot; ItemId = itemId; Amount = amount; Dura = dura; }
}

/// <summary>One legend-mark line in the profile window: an icon id, a text color, and the text. <see cref="Name"/>
/// is an internal key (RTK's legend name, never sent to the client) so a quest can find/replace/remove its own
/// legend by identity rather than matching on display text.</summary>
public sealed class Legend
{
    public byte   Icon  = 0;
    public byte   Color = 0;
    public string Text  = "";
    public string Name  = "";

    public Legend() { }
    public Legend(byte icon, byte color, string text, string name = "") { Icon = icon; Color = color; Text = text; Name = name; }
}
