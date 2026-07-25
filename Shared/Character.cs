namespace Shared;

/// <summary>
/// Minimal fabricated character for world-entry bring-up. Real characters will come from
/// Persistence later; for now this is a hardcoded spawn so we can drive the entry sequence.
/// </summary>
public sealed class Character
{
    public uint   Id     = 1;
    public string Name   = "snuggle";

    // location — map 32 is a real 33x33 area (180 distinct tiles, 270 objects) that actually
    // renders, unlike TK27 which is a uniform void tile. Square avoids width/height ambiguity.
    //
    // X/Y must be SMALL: the 0x33 self-placement passes only if (X,Y) is inside the camera
    // viewport rect (handler 0x424310). SendXy's 0x04 sets scroll = (A-C, B-D) = (0,0) here, so
    // the viewport starts at map origin (~0..14). (16,16) fell OUTSIDE it -> placement bailed ->
    // invisible character. (5,5) sits well inside the initial viewport. (Reference spawns at 1,3.)
    public ushort Map    = 32;
    public ushort X      = 5;
    public ushort Y      = 5;
    public ushort MapXs  = 33;
    public ushort MapYs  = 33;

    // appearance
    public ushort Sex    = 1;
    public ushort Face   = 0;
    public ushort Hair   = 0;

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
    public byte Level    = 1;
    public uint MaxHp    = 100;
    public uint MaxMp    = 50;
    public uint Hp       = 100;
    public uint Mp       = 50;
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
    public sbyte  Ac        = 0;
    public byte   Dam       = 0;
    public byte   Hit       = 0;
    public uint   Tnl       = 0;
    public string Title     = "";          // grade/honorific shown above the name (e.g. "Peasant")
    public string ClassName = "Peasant";   // path/class line
    public string ClanName  = "";          // guild/clan name ("" = clanless)
    public string ClanTitle = "";          // rank within the clan
    public string Spouse    = "";           // marriage line ("" = none)

    // The writable "profile" page shown when someone clicks you (0x34): a free-text blurb the player
    // writes about their character, plus an optional drawn portrait bitmap. ProfilePic is the raw
    // bitmap bytes WITHOUT the size prefix (null/empty = no picture). Edited via the client's
    // change-profile packet (0x2F) and persisted.
    public string ProfileText = "This character has not written a profile yet.";
    public byte[]? ProfilePic = null;

    // Legend marks: the scrollable list at the bottom of the profile. Each = icon + color + text.
    // Seeded with a "born" entry (mirrors the real 6.x capture) so the window has visible content.
    public List<Legend> Legends = new()
    {
        // color 0x80 matches the real 6.x capture (`01 00 80 17 "Born…"`); color 0 renders invisible.
        new Legend(icon: 0, color: 0x80, text: "Born in Hyul 31, Winter"),
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
