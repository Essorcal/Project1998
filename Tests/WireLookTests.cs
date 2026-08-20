using Server;
using Xunit;

namespace Tests;

/// <summary>
/// ItmLook → 0x33 appearance wire-byte translation (<see cref="Session.WeaponWireLook"/> and friends).
///
/// The two id spaces are easy to conflate because they AGREE for one-handed swords below 95: ItmLook is
/// RTK's flat space (family = look/10000: 0 sword, 1 spear/2H, 2 bow, 3 fan), while the 4.95 client splits
/// its single appearance byte into family ranges (classifier 0x432fe0: sword 0x00..0x7F, spear 0x80..0xBF,
/// bow 0xC0..0xDF, fan 0xE0..0xFE). The original code sent (byte)ItmLook, which silently drew every
/// two-handed weapon as an unrelated sword — Frozen spear (10011) truncated to sword 27 — while the bag
/// icon (a separate Item.epf id space) stayed correct, so the bug read as "right item, wrong doll".
/// Values here are pinned against the client's own art tables (NexusTK.dat: 95 swords, 31 spears, 0 bows,
/// 4 fans, 13 shields, 67 bodies); see docs/NexusTK-4.95-Protocol.md §8.
/// </summary>
public class WireLookTests
{
    [Theory]
    [InlineData(0, 0x00)]          // Novice sword: sword art 0 is real art, not "empty"
    [InlineData(23, 0x17)]         // Frost sabre: 1H swords pass through untouched
    [InlineData(94, 0x5E)]         // last sword art the client ships
    [InlineData(10000, 0x80)]      // Long spear: spear family base
    [InlineData(10011, 0x8B)]      // Frozen spear — the reported bug ((byte)10011 was 27, a sword)
    [InlineData(10030, 0x9E)]      // last spear art the client ships
    [InlineData(30000, 0xE0)]      // fans: every fan item in the data is 30000 -> fan art 0
    [InlineData(30003, 0xE3)]      // last fan art
    public void WeaponFamiliesMapToTheirByteRanges(int look, byte wire) =>
        Assert.Equal(wire, Session.WeaponWireLook(look));

    [Theory]
    [InlineData(95, 0x00)]         // sword art the 4.95 client doesn't have -> family default, NOT garbage
    [InlineData(10031, 0x80)]      // ditto spears (this was Aureate Aspiration before the data re-point)
    [InlineData(30004, 0xE0)]      // ditto fans
    public void MissingArtFallsBackToTheFamilyDefault(int look, byte wire) =>
        Assert.Equal(wire, Session.WeaponWireLook(look));

    [Fact]
    public void BowsStayInTheBowRangeAndNeverOverflowIntoFans()
    {
        // 4.95 has zero bow art, so any bow byte draws an empty hand — but it must stay inside
        // 0xC0..0xDF: overflowing into 0xE0+ would draw a fan, and 0xC0+0x3F would hit 0xFF (bare-slot
        // sentinel). Shot gun (20109) is the extreme the data actually contains.
        Assert.Equal(0xC0, Session.WeaponWireLook(20000));
        Assert.Equal(0xDF, Session.WeaponWireLook(20109));
    }

    [Fact]
    public void NoWeaponLookEverProducesTheBareHandSentinel()
    {
        // 0xFF means "slot is empty" to the client; a WORN item must never encode to it.
        for (int fam = 0; fam < 4; fam++)
            for (int art = 0; art < 300; art++)
                Assert.NotEqual((byte)0xFF, Session.WeaponWireLook(fam * 10000 + art));
    }

    [Theory]
    [InlineData(0, 0)]     // Tarnished shield
    [InlineData(12, 12)]   // Honor shield: last real art
    [InlineData(18, 0)]    // later-client art (Mystic buckler's old value) -> base art
    public void ShieldsAreDirectIdsClampedToClientArt(int look, byte wire) =>
        Assert.Equal(wire, Session.ShieldWireLook(look));

    [Theory]
    [InlineData(36, 36)]     // wind armor: in-range bodies pass through
    [InlineData(66, 66)]     // last body the client ships
    [InlineData(210, 0)]     // Scale Mail: later-era body -> base, NOT (byte)210
    [InlineData(10008, 0)]   // ghost coats: the dangerous case — (byte)10008 = 40 silently drew wind armor
    public void ArmorBodiesClampInsteadOfTruncating(int look, byte wire) =>
        Assert.Equal(wire, Session.ArmorWireLook(look));
}
