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
    public byte Nation   = 1;
    public byte Totem    = 4;   // 4 = none
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

    // Raw body of the creation packet (0x04), kept verbatim until its appearance-byte layout is
    // decoded from the logged dump and mapped onto the fields above. Null for the default spawn.
    public byte[]? CreationBlob = null;
}
