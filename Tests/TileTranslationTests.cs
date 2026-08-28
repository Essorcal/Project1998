using Server;
using Xunit;

namespace Tests;

/// <summary>
/// Guards the 4.x -> per-client tile renumbering (<see cref="TileTranslation"/>).
///
/// <para>The bug these exist to stop is not a crash — it is a map that renders as perfectly plausible but
/// WRONG terrain, which no smoke test and no screenshot will catch. The invariant that actually matters is
/// the SHEET SELECTOR: a 4.x ground word below <c>0xC000</c> means <c>TileA[v-1]</c> and one at or above it
/// means <c>TileB[v-0xC000]</c>. Treating the top two bits as passability and masking them off turns 30.58%
/// of all cells (526,619 of 1,722,232 across 1,492 of 1,750 maps) into unrelated low tiles — terrain that
/// still looks like terrain.</para>
///
/// <para>The offsets are env-overridable, so anything config-dependent is asserted against the class's own
/// reported offset rather than a hardcoded number; the shipped defaults are skipped when an override is
/// present so a tuning session cannot produce a red suite that means nothing.</para>
/// </summary>
public class TileTranslationTests
{
    private const Session.ClientVersion V533 = Session.ClientVersion.V533;
    private const Session.ClientVersion V495 = Session.ClientVersion.V495;

    private static bool EnvSet(params string[] names)
    {
        foreach (var n in names)
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(n))) return true;
        return false;
    }

    [Theory]
    [InlineData(V495)]
    [InlineData(V533)]
    public void GroundZeroIsNeverTranslated(Session.ClientVersion ver)
    {
        // 0 means "draw nothing" to BOTH blitters (4.x sub_431820 and 5.33 sub_4443d0 both test-and-bail).
        // It is a sentinel, not an index into anything.
        Assert.Equal(0, TileTranslation.Ground(0, ver));
    }

    [Theory]
    [InlineData(V495)]
    [InlineData(V533)]
    public void ObjectZeroIsNeverShifted(Session.ClientVersion ver)
    {
        // 0 = "no object", the value of most cells. Shifting it would stamp a real object on every empty tile.
        Assert.Equal(0, TileTranslation.Object(0, ver));
    }

    [Fact]
    public void FourNineFive_GetsTheGroundWordVerbatim()
    {
        // 4.95 reads the very sheets our .map set was authored against and decodes the selector itself,
        // so the word must arrive untouched — including the sheet-2 range.
        foreach (ushort v in new ushort[] { 1, 651, 2501, 8114, 0xC000, 0xD000, 0xFFFF })
            Assert.Equal(v, TileTranslation.Ground(v, V495));
    }

    [Fact]
    public void ShippedDefaults_Sheet1IsIdentityOn533()
    {
        if (EnvSet("P1998_TILE_OFF", "P1998_TILE_OFF_495", "P1998_TILE_OFF_533")) return;   // tuning session
        // 5.33 prepended a null frame to TILE.EPF (TileA[i] == TILE[i+1]) AND dropped the 4.x client's
        // "dec eax". The two cancel exactly: TILE[v] IS the frame 4.95 draws for v. A global +1 was shipped
        // twice on the strength of a renderer that read the wrong EPF TOC field; it is wrong.
        Assert.Equal(0, TileTranslation.GroundOffset(V533));
        foreach (ushort v in new ushort[] { 1, 651, 2501, 8114, 0xBFFF })
            Assert.Equal(v, TileTranslation.Ground(v, V533));
    }

    [Fact]
    public void Sheet2IsRemappedThroughTheTable_NotShifted()
    {
        if (TileTranslation.Sheet2Count == 0) return;             // table not deployed in this environment
        // TileB was RE-PACKED into the merged sheet (232 distinct deltas), so no arithmetic relationship
        // holds. What must be true is that a sheet-2 word does NOT come back as itself or as itself+1.
        ushort v = unchecked((ushort)(TileTranslation.Sheet2Base + 0));
        ushort got = TileTranslation.Ground(v, V533);
        Assert.NotEqual(v, got);
        Assert.NotEqual((ushort)(v + 1), got);
        Assert.InRange(got, (ushort)1, (ushort)28550);            // a real frame in TILE.EPF
    }

    [Fact]
    public void Sheet2SelectorIsNotMaskedAwayAsPassability()
    {
        if (TileTranslation.Sheet2Count == 0) return;
        // The regression this whole class exists for: 0xC000|n must NOT translate to the same thing as n.
        // That is precisely what "tile = v & 0x3FFF, pass = v >> 14" did to 30% of the world.
        foreach (ushort n in new ushort[] { 1, 100, 2296, 6695 })
        {
            ushort sheet2 = (ushort)(TileTranslation.Sheet2Base + n);
            Assert.NotEqual(TileTranslation.Ground(n, V533), TileTranslation.Ground(sheet2, V533));
        }
    }

    [Fact]
    public void UnmappedSheet2FrameDrawsNothingRatherThanGuessing()
    {
        if (TileTranslation.Sheet2Count == 0) return;
        // 8 legacy sheet-2 frames have no 5.33 equivalent (none are referenced by any shipped map).
        // A wrong index would be indistinguishable from correct terrain; a blank is visible.
        ushort unmapped = (ushort)(TileTranslation.Sheet2Base + 6887);
        Assert.Equal(0, TileTranslation.Ground(unmapped, V533));
    }

    [Fact]
    public void ShippedDefaults_ObjectIdSpaceIsSharedByBothClients()
    {
        if (EnvSet("P1998_OBJ_OFF_495", "P1998_OBJ_OFF_533")) return;
        // 5.33's SObj.tbl is the 4.x table with entries APPENDED (7,583 of the first 7,608 records are
        // byte-identical), so an object id means the same thing to both clients.
        Assert.Equal(0, TileTranslation.ObjectOffset(V495));
        Assert.Equal(0, TileTranslation.ObjectOffset(V533));
        foreach (ushort o in new ushort[] { 1, 5, 1542, 5986 })
            Assert.Equal(o, TileTranslation.Object(o, V533));
    }

    [Fact]
    public void Obj533Fix_DefaultChangesNothingOnScreen()
    {
        if (EnvSet("P1998_OBJ_FIX_533")) return;                                // operator override
        // THE DEFAULT MUST BE VISUALLY INERT. Every scope above `free` buys walkability by deleting an
        // object sprite, and that trade belongs to the operator. Objects 327 and 320 sit on the Arctic
        // Village (3811) staircase at 35,32 / 36,32 — 0x00 in the 4.x SObj.tbl, 0x0F in 5.33's — and are
        // `decor` scope, so at the default they must still stream unchanged even though that leaves the
        // stairs impassable on 5.33. Fixing them losslessly needs the client's SOBJ.TBL patched.
        Assert.Equal(TileTranslation.Obj533Scope.Free, TileTranslation.Obj533FixScope);
        Assert.Equal(327, TileTranslation.Object(327, V533));
        Assert.Equal(320, TileTranslation.Object(320, V533));
    }

    [Fact]
    public void Obj533Fix_FreeScopeIsEmpty_TheLookAlikesWereFalseMatches()
    {
        if (EnvSet("P1998_OBJ_FIX_533")) return;                                // operator override
        // Scope `free` promised "substitute a visually identical object" and shipped four rows — 553->554,
        // 571->572, 600->601, 694->695 — every one of them id -> id+1, because the matcher's renderer read
        // the wrong EPF TOC field and displayed mostly the NEXT frame (the same off-by-one behind the
        // twice-shipped ground +1). SObj.tbl refutes them: 553 is frames [1202,1199], 554 is [1203,1200];
        // 571 is 2 frames tall, 572 is 3. The live symptom was every guild hall's curtain run (571..579,
        // e.g. TK2510 rows 5/10/17) streaming to 5.33 with the left pillar replaced by a curtain-rod piece.
        // So: at the default scope NOTHING may be rewritten. A future free row needs its frame lists proven
        // identical in SObj.tbl itself, then this count consciously updated alongside that proof.
        Assert.Equal(0, TileTranslation.Obj533FixCount);
        foreach (ushort o in new ushort[] { 553, 571, 600, 694 })
            Assert.Equal(o, TileTranslation.Object(o, V533));
        // The full curtain/pillar run guild halls anchor at three rows — inert end to end.
        for (ushort o = 571; o <= 579; o++)
            Assert.Equal(o, TileTranslation.Object(o, V533));
    }

    [Fact]
    public void Obj533Fix_DecorScopeClearsTheStairObjects()
    {
        if (TileTranslation.Obj533FixScope < TileTranslation.Obj533Scope.Decor) return;   // opt-in only
        Assert.Equal(0, TileTranslation.Object(327, V533));
        Assert.Equal(0, TileTranslation.Object(320, V533));
    }

    [Fact]
    public void Obj533Fix_NeverAppliesTo495()
    {
        // 4.95 reads the table these flags came from — it must never be touched at any scope.
        foreach (ushort o in new ushort[] { 327, 320, 1243, 553 })
            Assert.Equal(o, TileTranslation.Object(o, V495));
    }

    [Fact]
    public void Obj533Fix_StructuresAreNeverBlankedBelowAllScope()
    {
        if (TileTranslation.Obj533FixScope >= TileTranslation.Obj533Scope.All) return;
        // obj 1243 (1,817 cells) has a REAL 4.x directional block (0x01) — blanking it deletes visible
        // structures, so only the explicit `all` scope may do it.
        Assert.Equal(1243, TileTranslation.Object(1243, V533));
    }

    [Fact]
    public void Obj533Fix_UnaffectedObjectsPassThrough()
    {
        // The overwhelming majority of ids are not in the table at all and must be untouched — including
        // every door object (checked exhaustively: no Doors.csv / DoorObjects.csv id is affected).
        foreach (ushort o in new ushort[] { 1, 5, 1542, 5986, 366, 367, 338, 339 })
            Assert.Equal(o, TileTranslation.Object(o, V533));
    }

    [Fact]
    public void GroundIsNotTruncatedTo14Bits()
    {
        // 14 bits is a property of the 4.x ground WORD, not of the sheet: 5.33's TILE.EPF holds 28,551
        // frames and needs 15. A mask here would silently wrap the upper sheet.
        if (EnvSet("P1998_TILE_OFF", "P1998_TILE_OFF_533")) return;
        Assert.Equal(20000, TileTranslation.Ground(20000, V533));
    }

    [Fact]
    public void Sheet2TableIsDeployedAndComplete()
    {
        // Not a purity test — if this file is missing, 30% of every map streams as blank to 5.33 and the
        // only symptom is holes in the terrain. Fail loudly here instead.
        Assert.Equal(8930, TileTranslation.Sheet2Count);
    }
}
