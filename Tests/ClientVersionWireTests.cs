using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The 4.95 wire shapes, pinned, so that adding 5.33 support cannot quietly move a byte for the client
/// that already works.
///
/// Two packets diverge structurally between the clients, and both were found by reversing the 5.33
/// binary rather than by guessing (docs/5.x/Reverse-Engineering.md):
///
///   0x33 / 0x1d / 0x30 appearance — 4.95's parser <c>sub_436120</c> consumes SEVEN bytes; 5.33's
///     <c>sub_449880</c> consumes ELEVEN, with three fields inserted (one before face, a u16 before
///     weapon, one after shield). Feeding 5.33 the 4.95 record shifted every slot from face onward.
///
///   0x0E despawn — 4.95 is <c>count(u8)</c> then that many u32 ids; 5.33's case body reads exactly ONE
///     u32 at body offset 0 and has no count and no loop, so a multi-id despawn is N packets there.
///
/// The V495 assertions here are the regression guard: they encode what the working client received
/// before any of this existed. If a future 5.x change alters them, that is the bug, not the test.
/// </summary>
public class ClientVersionWireTests
{
    // sex, form, face, armor, dye, weapon, shield — distinct values so a shift can't hide behind a zero.
    private static readonly byte[] Look = { 1, 3, 0x56, 0x0C, 0x05, 0x8B, 0x02 };

    [Fact]
    public void V495AppearanceIsTheSameSevenBytesItAlwaysWas()
    {
        var r = Session.AppearanceRecord(Session.ClientVersion.V495, Look);
        Assert.Equal(Look, r);
    }

    [Fact]
    public void V495AppearanceIgnoresTheFieldsThatOnly533Has()
    {
        // The extra-field knob must be inert for 4.95 — it exists only to sweep 5.33's unknown slots.
        var r = Session.AppearanceRecord(Session.ClientVersion.V495, Look, hair: 0x11, tail: 0x44);
        Assert.Equal(Look, r);
    }

    [Fact]
    public void V533AppearanceIsElevenBytesInTheSweptOrder()
    {
        // Look[5] is 0x8B = the 4.95 packed byte for spear art 11, i.e. flat look 10011 = 0x271B.
        var r = Session.AppearanceRecord(Session.ClientVersion.V533, Look, hair: 0x11, tail: 0x44);
        Assert.Equal(new byte[]
        {
            1,        // [0]    body/sex        <- 4.95 [0]
            3,        // [1]    form/state      <- 4.95 [1]
            0x56,     // [2]    face/head       <- 4.95 [2], SAME slot
            0x11,     // [3]    hair colour     <- 5.33-only
            0x0C,     // [4]    armor           <- 4.95 [3]
            0x05,     // [5]    armor colour    <- 4.95 [4]
            0x27,     // [6..7] weapon as u16 BE in the flat look space (10011)
            0x1B,
            0,        // [8]    unknown, no observed effect
            0x02,     // [9]    shield          <- 4.95 [6]
            0x44,     // [10]   5.33-only
        }, r);
    }

    [Theory]
    [InlineData(0x00, 0)]          // sword art 0 — a REAL sword, which is why 0 drew a phantom weapon
    [InlineData(0x17, 23)]         // sword art 23
    [InlineData(0x80, 10000)]      // spear family base
    [InlineData(0x8B, 10011)]      // Frozen spear — round-trips through the 4.95 packing
    [InlineData(0xC0, 20000)]      // bow family base
    [InlineData(0xE0, 30000)]      // fan family base
    [InlineData(0xFF, 0xFFFF)]     // bare hands — NOT 0, or the client draws sword art 0
    public void Weapon533UnpacksThe495ByteIntoTheFlatLookSpace(int packed, int expected) =>
        Assert.Equal((ushort)expected, Session.Weapon533((byte)packed));

    [Fact]
    public void Weapon533AgreesWithTheFamilyArithmeticTheClientUses()
    {
        // The client picks the art archive by value/10000. Assert the families land where the live sweep
        // showed them, since that arithmetic is the whole reason this is a u16 and not a packed byte.
        Assert.Equal(0, Session.Weapon533(0x40) / 10000);       // sword
        Assert.Equal(1, Session.Weapon533(0x90) / 10000);       // spear / two-handed
        Assert.Equal(2, Session.Weapon533(0xD0) / 10000);       // bow
        Assert.Equal(3, Session.Weapon533(0xF0) / 10000);       // fan
    }

    [Fact]
    public void UnarmedIsNotDrawnAsSwordArtZeroOn533()
    {
        var naked = new byte[] { 0, 0, 10, 0, 0, 0xFF, 0xFF };   // no weapon, no shield
        var r = Session.AppearanceRecord(Session.ClientVersion.V533, naked);
        Assert.Equal(0xFF, r[6]);   // weapon u16 == 0xFFFF, the "no weapon" sentinel
        Assert.Equal(0xFF, r[7]);
    }

    [Fact]
    public void FaceAndHairColourAreSeparateSlotsOn533()
    {
        // The regression this guards: 4.95 carries hairstyle inside the single head byte, 5.33 split hair
        // colour into its own field right after it. Putting the face id in [3] renders an arbitrary hair
        // colour AND head id 0 — both symptoms at once, from one off-by-one.
        var r = Session.AppearanceRecord(Session.ClientVersion.V533, Look, hair: 0x11);
        Assert.Equal(0x56, r[2]);   // face is the character's face
        Assert.Equal(0x11, r[3]);   // hair colour is NOT the face id
    }

    [Fact]
    public void EveryKnown533SlotCarriesTheValue495PutsInItsOwnSlot()
    {
        var a = Session.AppearanceRecord(Session.ClientVersion.V495, Look);
        var b = Session.AppearanceRecord(Session.ClientVersion.V533, Look);
        Assert.Equal(a[0], b[0]);          // body/sex   same index
        Assert.Equal(a[1], b[1]);          // form/state same index
        Assert.Equal(a[2], b[2]);          // face       same index
        Assert.Equal(a[3], b[4]);          // armor      3 -> 4 (hair colour inserted at 3)
        Assert.Equal(a[4], b[5]);          // dye        4 -> 5
        // weapon 5 -> the [6..7] u16, unpacked from the byte rather than copied
        Assert.Equal(Session.Weapon533(a[5]), (ushort)((b[6] << 8) | b[7]));
        Assert.Equal(a[6], b[9]);          // shield     6 -> 9
    }

    [Fact]
    public void ShortLookIsPaddedRatherThanThrowing()
    {
        // 0x1d used to Array.Copy exactly 7 bytes, which threw on a short record. Padding is the lenient
        // behaviour; assert it for both clients so neither regresses into an exception on a live path.
        Assert.Equal(7, Session.AppearanceRecord(Session.ClientVersion.V495, new byte[] { 1, 2 }).Length);
        Assert.Equal(11, Session.AppearanceRecord(Session.ClientVersion.V533, new byte[] { 1, 2 }).Length);
    }

    [Fact]
    public void V495DespawnIsOneCountPrefixedListPacket()
    {
        var b = Session.DespawnBodies(Session.ClientVersion.V495, 0x00018769, 0x00018770);
        var one = Assert.Single(b);
        Assert.Equal(new byte[] { 2, 0x00, 0x01, 0x87, 0x69, 0x00, 0x01, 0x87, 0x70 }, one);
    }

    [Fact]
    public void V533DespawnIsOneBarePacketPerIdWithNoCountByte()
    {
        var b = Session.DespawnBodies(Session.ClientVersion.V533, 0x00018769, 0x00018770);
        Assert.Equal(2, b.Count);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x87, 0x69 }, b[0]);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x87, 0x70 }, b[1]);
    }

    [Fact]
    public void V495DespawnDropsIdZeroAndCountsOnlyTheSurvivors()
    {
        // A stray 0 terminates the 4.95 loop, silently swallowing every id after it — so 0 must never
        // reach the wire, and the count must describe what actually went out.
        var one = Assert.Single(Session.DespawnBodies(Session.ClientVersion.V495, 0, 0x00018769, 0));
        Assert.Equal(new byte[] { 1, 0x00, 0x01, 0x87, 0x69 }, one);
    }

    [Fact]
    public void V533DespawnDropsIdZeroToo()
    {
        // 5.33's handler treats id 0 as an explicit no-op (`test eax,eax; je done`), so a 0 would be a
        // wasted packet rather than a corrupting one — still not worth sending.
        var b = Session.DespawnBodies(Session.ClientVersion.V533, 0, 0x00018769, 0);
        var one = Assert.Single(b);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x87, 0x69 }, one);
    }

    [Fact]
    public void DespawnWithNothingToSaySendsNoPacketAtAll()
    {
        Assert.Empty(Session.DespawnBodies(Session.ClientVersion.V495));
        Assert.Empty(Session.DespawnBodies(Session.ClientVersion.V533));
        Assert.Empty(Session.DespawnBodies(Session.ClientVersion.V533, 0, 0));
    }

    // ---- 0x2e world map ----
    // Two dots, deliberately distinct in every field so a one-byte shift cannot hide behind a repeat.
    private static readonly Session.WorldMapEntry[] Dots =
    {
        new("Kugnae", 1011, 18, 14, 291, 297),
        new("Buya",   1012,  1, 11, 335, 202),
    };

    [Fact]
    public void V495WorldMapIsTheFlatDotListItAlwaysWas()
    {
        // No leading kind byte (payload[0] IS the bgName length), then count, the byte 4.95 ignores, then
        // per dot: x0 y0 name mapId(u32BE) x1 y1 -- and NOTHING after y1. A link list here would shift
        // every later entry for the client that already works.
        var b = Session.WorldMapBody(Session.ClientVersion.V495, "field10", Dots, originIndex: 0);
        Assert.Equal(new byte[]
        {
            7, (byte)'f', (byte)'i', (byte)'e', (byte)'l', (byte)'d', (byte)'1', (byte)'0',
            2,                                  // count
            0,                                  // 4.95 ignores this byte
            0x01, 0x23, 0x01, 0x29,             // dot pixel 291, 297
            6, (byte)'K', (byte)'u', (byte)'g', (byte)'n', (byte)'a', (byte)'e',
            0x00, 0x00, 0x03, 0xF3,             // map 1011, u32BE
            0x00, 0x12, 0x00, 0x0E,             // landing 18, 14
            0x01, 0x4F, 0x00, 0xCA,             // dot pixel 335, 202
            4, (byte)'B', (byte)'u', (byte)'y', (byte)'a',
            0x00, 0x00, 0x03, 0xF4,             // map 1012
            0x00, 0x01, 0x00, 0x0B,             // landing 1, 11
        }, b);
    }

    [Fact]
    public void V533WorldMapAppendsALinkListToEveryEntry()
    {
        // Same header, same per-entry prefix, plus the u16 linkCount + u16 node indexes that sub_469c80
        // folds into its n x n adjacency bitset. Omitting them is what made the client read the next
        // entry's dot-x as a link count and die with "not enough memory resources".
        var b = Session.WorldMapBody(Session.ClientVersion.V533, "field10", Dots, originIndex: 0);
        Assert.Equal(new byte[]
        {
            7, (byte)'f', (byte)'i', (byte)'e', (byte)'l', (byte)'d', (byte)'1', (byte)'0',
            2,                                  // count
            0,                                  // 5.33: origin node index
            0x01, 0x23, 0x01, 0x29,
            6, (byte)'K', (byte)'u', (byte)'g', (byte)'n', (byte)'a', (byte)'e',
            0x00, 0x00, 0x03, 0xF3,
            0x00, 0x12, 0x00, 0x0E,
            0x00, 0x01, 0x00, 0x01,             // 1 link -> node 1
            0x01, 0x4F, 0x00, 0xCA,
            4, (byte)'B', (byte)'u', (byte)'y', (byte)'a',
            0x00, 0x00, 0x03, 0xF4,
            0x00, 0x01, 0x00, 0x0B,
            0x00, 0x01, 0x00, 0x00,             // 1 link -> node 0
        }, b);
    }

    [Fact]
    public void V533CarriesTheOriginIndexAndV495NeverDoes()
    {
        // 5.33 reads the byte after the count as the BFS root / "you are here" icon / camera centre, and
        // ESC echoes that node straight back. 4.95 has no such notion, so its byte stays 0 regardless.
        var v533 = Session.WorldMapBody(Session.ClientVersion.V533, "field10", Dots, originIndex: 1);
        var v495 = Session.WorldMapBody(Session.ClientVersion.V495, "field10", Dots, originIndex: 1);
        Assert.Equal(1, v533[9]);
        Assert.Equal(0, v495[9]);
    }

    [Fact]
    public void OriginIndexIsClampedIntoTheListTheClientActuallyGot()
    {
        // node[originIndex] is dereferenced with no bounds check (sub_4e4b80 centres the camera on it
        // before anything validates it), so an out-of-range index is an OOB read on the client.
        var b = Session.WorldMapBody(Session.ClientVersion.V533, "field10", Dots, originIndex: 9);
        Assert.Equal(1, b[9]);
        var none = Session.WorldMapBody(Session.ClientVersion.V533, "field10",
                                        System.Array.Empty<Session.WorldMapEntry>(), originIndex: 3);
        Assert.Equal(new byte[] { 7, (byte)'f', (byte)'i', (byte)'e', (byte)'l', (byte)'d', (byte)'1', (byte)'0', 0, 0 }, none);
    }

    [Fact]
    public void ASingleDestinationStillCarriesAnExplicitZeroLinkCount()
    {
        // The complete graph degenerates to no edges with one node -- linkCount must still be present and
        // zero, or the client reads whatever follows the packet as a count.
        var b = Session.WorldMapBody(Session.ClientVersion.V533, "field10", new[] { Dots[0] }, originIndex: 0);
        Assert.Equal(new byte[] { 0x00, 0x00 }, b[^2..]);
    }

    // ---- 0x19 music -----------------------------------------------------------------------------------
    // The mp3 channel is the third structural divergence. 4.95 falls through to a TLV tail whose mode byte
    // decides play/loop/stop; 5.33's arm (0x46a420) reads a flat id/fallback/volume record and hands it to
    // the resolver 0x4a6360, which picks %08d.LST / .LSR / .MP3 out of Mus000.dat by itself. Feeding 5.33
    // the 4.95 TLV puts the volume byte where the fallback id belongs. MIDI is the same on both.

    [Fact]
    public void MidiIsTheSameSixByteBodyOnBothClients()
    {
        var v495 = Session.MusicBody(Session.ClientVersion.V495, 6, type: 2, volume: 100);
        var v533 = Session.MusicBody(Session.ClientVersion.V533, 6, type: 2, volume: 100);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x06, 100 }, v495);
        Assert.Equal(v495, v533);
    }

    [Fact]
    public void V495Mp3KeepsItsTlvTail()
    {
        // Live-verified 2026-08-13 (@music 103 mp3). Byte for byte what the working client received.
        var b = Session.MusicBody(Session.ClientVersion.V495, 103, type: 1, volume: 100);
        Assert.Equal(new byte[]
        {
            0x01,             // type 1 = mp3
            0x03,             // P0 -> the TLV tail starts after the 5-byte header
            0x00, 0x67,       // track 103 (u16 BE)
            100,              // volume
            0x03, 0x00, 0x02, // tagA, B0, mode 2 = loop
            0x00, 0x00, 0x00, // B1, [obj+0x154], skip
            0x00,             // pad
        }, b);
    }

    [Fact]
    public void V533Mp3IsAFlatIdFallbackVolumeRecord()
    {
        // 0x46a420: id at +3, fallback at +5, volume at +7 -- no TLV, and +2 is never read on this arm.
        var b = Session.MusicBody(Session.ClientVersion.V533, 902, type: 1, volume: 100);
        Assert.Equal(new byte[]
        {
            0x01,             // type 1 = mp3/playlist
            0x00,             // +2 unread here
            0x03, 0x86,       // 902 -> 00000902.LSR, a shuffled ten-track playlist
            0x00, 0x00,       // fallback 0 = go quiet rather than drive a resource we don't have
            100,              // volume
        }, b);
    }

    [Fact]
    public void StoppingTheMp3ChannelUsesEachClientsOwnRoute()
    {
        // 4.95 stops through the TLV's mode 0 (= StopSound, which ignores the id); 5.33 has no mode byte, so
        // the stop IS id 0 -- it resolves to no file, falls to the fallback, and 0 there means "stop".
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x01, 100, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                     Session.MusicStopBody(Session.ClientVersion.V495, 1));
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 100 },
                     Session.MusicStopBody(Session.ClientVersion.V533, 1));
        // The midi stop is the handler's own bgm-0 path on both, and needs no tail.
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00, 100 },
                     Session.MusicStopBody(Session.ClientVersion.V495, 2));
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00, 100 },
                     Session.MusicStopBody(Session.ClientVersion.V533, 2));
    }

    // ---- 0x2f sub-kind 4 buy-grid row -----------------------------------------------------------------
    // The fourth structural divergence, and the one the shop/bank grids ride on: 5.x carries an icon-COLOUR
    // byte between the icon and the price, exactly as 0x0F and 0x37 do; 4.95 has no colour channel in the
    // item graphics path at all and reads the price straight after the icon. Apple: icon frame 10, colour 0,
    // buy price 10 -- the real row that exposed this live.

    [Fact]
    public void V495BuyGridRowHasNoColourByteBetweenIconAndPrice()
    {
        var b = Session.BuyGridRowBody(Session.ClientVersion.V495, 0xC00A, 3, 10, "Apple", "eat me");
        Assert.Equal(new byte[]
        {
            0xC0, 0x0A,                                              // icon (frame 10, +49152 resolver form)
            0x00, 0x00, 0x00, 0x0A,                                  // price 10, u32BE, at descriptor+2
            5, (byte)'A', (byte)'p', (byte)'p', (byte)'l', (byte)'e',
            6, (byte)'e', (byte)'a', (byte)'t', (byte)' ', (byte)'m', (byte)'e',
        }, b);
    }

    [Fact]
    public void V533BuyGridRowCarriesTheIconColourByte()
    {
        var b = Session.BuyGridRowBody(Session.ClientVersion.V533, 0xC00A, 3, 10, "Apple", "eat me");
        Assert.Equal(new byte[]
        {
            0xC0, 0x0A,                                              // icon
            3,                                                       // 5.x-only icon colour
            0x00, 0x00, 0x00, 0x0A,                                  // price 10, now at descriptor+3
            5, (byte)'A', (byte)'p', (byte)'p', (byte)'l', (byte)'e',
            6, (byte)'e', (byte)'a', (byte)'t', (byte)' ', (byte)'m', (byte)'e',
        }, b);
    }

    [Fact]
    public void The533RowIsExactlyOneByteLongerAndOtherwiseIdentical()
    {
        // The whole bug in one assertion: a 4.95 row fed to 5.33 leaves the parser one byte short, so it
        // reads the price late, eats the name-length byte into the price, and then uses the name's FIRST
        // LETTER as the length. Nothing else about the row differs.
        var v495 = Session.BuyGridRowBody(Session.ClientVersion.V495, 0xC00A, 3, 10, "Apple", "eat me");
        var v533 = Session.BuyGridRowBody(Session.ClientVersion.V533, 0xC00A, 3, 10, "Apple", "eat me");
        Assert.Equal(v495.Length + 1, v533.Length);
        Assert.Equal(v495[..2], v533[..2]);
        Assert.Equal(v495[2..], v533[3..]);
    }

    [Fact]
    public void The533MisparseOfA495RowReproducesTheLiveSymptom()
    {
        // Decode the 4.95 row the way 5.33's loop would: u16 icon, u8 colour, u32BE price, u8 nameLen.
        // Asserting the exact garbage that appeared on screen is what pins the diagnosis -- if a future
        // change makes this stop reproducing, the theory behind the fix was wrong.
        var b = Session.BuyGridRowBody(Session.ClientVersion.V495, 0xC00A, 0, 10, "Apple", "Peasant level 0");
        int price = (b[3] << 24) | (b[4] << 16) | (b[5] << 8) | b[6];
        Assert.Equal(2565, price);          // the "2565" the grocer showed for a 10-gold apple
        Assert.Equal((byte)'A', b[7]);      // ...and the name length it then used was the letter 'A' = 65
        Assert.Equal(65, b[7]);
    }

    [Fact]
    public void ANegativeNumberIsClampedRatherThanWrappingToFourBillion()
    {
        foreach (var ver in new[] { Session.ClientVersion.V495, Session.ClientVersion.V533 })
        {
            var b = Session.BuyGridRowBody(ver, 0xC00A, 0, -5, "x", "y");
            int at = ver == Session.ClientVersion.V533 ? 3 : 2;
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, b[at..(at + 4)]);
        }
    }

}
