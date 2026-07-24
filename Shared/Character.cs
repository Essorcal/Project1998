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

/// <summary>One legend-mark line in the profile window: an icon id, a text color, and the text.</summary>
public sealed class Legend
{
    public byte   Icon  = 0;
    public byte   Color = 0;
    public string Text  = "";

    public Legend() { }
    public Legend(byte icon, byte color, string text) { Icon = icon; Color = color; Text = text; }
}
