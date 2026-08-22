namespace Server;

/// <summary>
/// Translates a 4.x <c>.map</c> ground word into the frame number the CONNECTED CLIENT's tile sheets use.
/// Our terrain source is always the 4.x map set; 5.33 merged and re-packed its sheets, so the stream has
/// to be adapted per version rather than shipped raw.
///
/// <para><b>How a 4.x ground word is actually decoded.</b> Read out of the 4.x client's ground blitter,
/// <c>NexusTK.exe sub_431820</c> (install <c>NextAeon</c>). It is not a tile index with flags on top —
/// it is a tagged union over TWO sheets:</para>
/// <code>
///   v == 0        -> draw nothing
///   v &lt;  0xC000   -> TileA[v - 1]        // sheet 1: "dec eax"
///   v &gt;= 0xC000   -> TileB[v - 0xC000]   // sheet 2: "sub eax, 0xC000"
/// </code>
/// <para>Those two constants are not magic numbers invented by the client: each legacy <c>.tbl</c> carries
/// them. The legacy TBL header is <c>count u32, palCount u32, base u16</c>, and <c>base</c> is 0x0001 in
/// <c>TileA.tbl</c> and 0xC000 in <c>TileB.tbl</c> — exactly what the blitter subtracts.</para>
///
/// <para><b>What 5.33 does instead.</b> Its blitter, <c>sub_4443d0</c>, has no such branch. It does
/// <c>and ecx,0xffff</c> (the second short is NOT consulted for drawing), bounds-checks against the frame
/// count, and indexes one merged <c>TILE.EPF</c> (28,551 frames) <b>directly by v, with no subtraction</b>.
/// <c>v == 0</c> still draws nothing (<c>test cx,cx / jne</c>).</para>
///
/// <para><b>Therefore:</b></para>
/// <list type="bullet">
/// <item>Sheet-1 cells need <b>no change at all</b>. 5.33 prepended a null frame, so <c>TileA[i]</c> sits at
/// <c>TILE[i+1]</c> — and since 5.33 also dropped the <c>-1</c>, <c>TILE[v] == TileA[v-1]</c> is precisely
/// what the 4.x client draws for the same <c>v</c>. The null frame at <c>TILE[0]</c> exists <i>because</i>
/// the <c>dec</c> was removed. The two changes cancel. <b>Offset 0.</b></item>
/// <item>Sheet-2 cells need a <b>lookup table</b>. TileB was re-packed into the merged sheet, not appended:
/// 232 distinct index deltas. See <c>game-data/Tile533Map.csv</c>.</item>
/// </list>
///
/// <para><b>This is 30% of the world.</b> Of the 1,722,232 cells in the 1,750 shipped maps, 69.25% are
/// sheet 1, 0.17% are blank, and <b>30.58% are sheet 2</b> — spread across 1,492 of the 1,750 maps. Modelling
/// the top two bits as passability (<c>tile = v &amp; 0x3FFF, pass = v &gt;&gt; 14</c>) silently rewrote every one
/// of those cells into an unrelated low tile. That, not any off-by-one, is why terrain looked mostly right
/// but "didn't make sense in particular places".</para>
///
/// <para><b>Audit trail — a global <c>+1</c> was shipped twice and was wrong both times.</b> It was inferred
/// from rendered tile colours produced by a tool that read the wrong EPF TOC field: the 16-byte entry is
/// <c>[L,T,R,B (4x i16)][pixelOffset u32][pixelEnd u32]</c>, and the tool used <c>pixelEnd</c>, which for a
/// 24x24 frame is <c>pixelOffset + 576</c> — i.e. it rendered 576 bytes into a 624-byte stride and returned
/// mostly the NEXT frame. Every colour identification built on it was off by one, including the
/// <c>solid:67</c> "water vs flowers" test that the <c>+1</c> rested on. Two lessons: a screenshot cannot
/// settle a tile-index question (an off-by-one lands on a neighbouring variant of the same material), and
/// neither can a renderer you have not validated against a known answer.</para>
///
/// <para><b>How the current mapping was established, and how to re-check it.</b> Not by eye. Each legacy
/// frame was rendered to RGB through its own palette and matched against every frame of <c>TILE.EPF</c>
/// rendered the same way; then every cell of all 1,750 maps was rendered under both pipelines and compared.
/// <b>1,719,261 of 1,719,261 drawn cells are byte-identical.</b> Re-run that if you touch this file.</para>
///
/// <para><b>Ground and object are different index spaces.</b> The object short indexes <c>SObj.tbl</c>, and
/// 5.33's table is the 4.x table with entries appended (7,583 of the first 7,608 records byte-identical), so
/// object ids mean the same thing to both clients and pass through untouched. The superseded single
/// <c>P1998_TILE_OFF</c> knob moved ground AND object together, which is why reaching for it to straighten
/// the ground shifted every door, wall and tree at the same moment.</para>
/// </summary>
public static class TileTranslation
{
    /// <summary>Ground words at or above this select the SECOND legacy tile sheet, index <c>v - Sheet2Base</c>.
    /// From <c>TileB.tbl</c>'s <c>base</c> header field, and hardcoded as <c>sub eax,0xC000</c> in the 4.x blitter.</summary>
    public const ushort Sheet2Base = 0xC000;

    /// <summary>Legacy sheet-2 frame index -> 5.33 <c>TILE.EPF</c> frame. Empty if the table failed to load.</summary>
    private static readonly IReadOnlyDictionary<ushort, ushort> Sheet2 = LoadSheet2();

    /// <summary>Escape hatch: a uniform shift applied to sheet-1 ground on 5.33. Defaults to 0 and should stay
    /// there — it exists so a future sheet revision can be probed without a rebuild, not because 0 is in doubt.</summary>
    private static readonly int GroundOff533 = Env("P1998_TILE_OFF_533", 0);
    private static readonly int GroundOff495 = Env("P1998_TILE_OFF_495", 0);
    private static readonly int ObjectOff533 = Env("P1998_OBJ_OFF_533", 0);
    private static readonly int ObjectOff495 = Env("P1998_OBJ_OFF_495", 0);

    // Superseded knob. Kept so existing run scripts keep working, but it moves the GROUND ONLY.
    private static readonly int? LegacyGroundOff =
        int.TryParse(Environment.GetEnvironmentVariable("P1998_TILE_OFF"), out var lo) ? lo : null;

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    public static int GroundOffset(Session.ClientVersion ver) =>
        LegacyGroundOff ?? (ver == Session.ClientVersion.V533 ? GroundOff533 : GroundOff495);

    public static int ObjectOffset(Session.ClientVersion ver) =>
        ver == Session.ClientVersion.V533 ? ObjectOff533 : ObjectOff495;

    /// <summary>Number of sheet-2 entries loaded; 0 means <c>Tile533Map.csv</c> is missing or unreadable.</summary>
    public static int Sheet2Count => Sheet2.Count;

    /// <summary>Translate a 4.x ground WORD (not a pre-masked index) into the client's frame number.</summary>
    /// <param name="groundWord">The raw <c>u16</c> exactly as the <c>.map</c> stores it — see
    /// <c>MapData.GroundWord</c>. Masking it first destroys the sheet selector and is the bug this replaced.</param>
    public static ushort Ground(ushort groundWord, Session.ClientVersion ver)
    {
        // 0 means "draw nothing" to BOTH clients. Never translate it, never offset it.
        if (groundWord == 0) return 0;

        if (ver != Session.ClientVersion.V533)
            return Shift(groundWord, GroundOffset(ver));   // 4.95 gets the word verbatim; it decodes it itself.

        if (groundWord >= Sheet2Base)
        {
            // Sheet 2. No arithmetic relationship survives the re-pack, so an unmapped frame must draw
            // nothing rather than guess — a wrong index here is indistinguishable from correct terrain.
            return Sheet2.TryGetValue((ushort)(groundWord - Sheet2Base), out var f) ? f : (ushort)0;
        }

        // Sheet 1: TILE[v] IS TileA[v-1]. Identity, unless someone set the escape hatch.
        return Shift(groundWord, GroundOffset(ver));
    }

    /// <summary>Map a 4.x <c>SObj.tbl</c> object id to the client's object id.</summary>
    /// <remarks>
    /// The id space itself is shared — 5.33's table is the 4.x table with entries appended, and 7,582 of the
    /// first 7,608 records carry identical sprite frames — so this is normally identity. What is NOT shared
    /// is the COLLISION data: 5.33 re-authored 362 flag bytes, and it runs collision locally against its own
    /// table, so 234 ids block a direction there that they did not on 4.x. See <see cref="LoadObj533"/> and
    /// <c>game-data/Obj533Fix.csv</c>.
    /// </remarks>
    public static ushort Object(ushort obj, Session.ClientVersion ver)
    {
        // Object 0 is "no object" in both clients and is the value of most cells. It is a sentinel, not an
        // index, so it must never be shifted into a real object.
        if (obj == 0) return 0;

        if (ver == Session.ClientVersion.V533 && ObjFixScope != Obj533Scope.Off
            && Obj533.TryGetValue(obj, out var fix) && fix.Scope <= ObjFixScope)
            return fix.Replacement;   // 0 = suppress (blank the cell's object), else a look-alike swap

        return Shift(obj, ObjectOffset(ver));
    }

    /// <summary>How much of the 5.33 object-collision workaround to apply. Ordered: a row applies when its
    /// own scope is at or below the configured one.</summary>
    public enum Obj533Scope
    {
        /// <summary>No workaround at all.</summary>
        Off = 0,
        /// <summary>Only visually identical substitutions — a look-alike object with usable flags. Nothing
        /// on screen changes. THE DEFAULT, because every wider scope pays for walkability with artwork and
        /// that trade is the operator's to make, not ours.</summary>
        Free = 1,
        /// <summary>…plus blanking objects 4.x marks fully walkable (0x00) — pure decoration. Opt-in: it
        /// removes a visible sprite (the Arctic Village stair lip is a 24x7 strip) from 1,915 cells.</summary>
        Decor = 2,
        /// <summary>…plus blanking objects with a real 4.x directional block. Opens the path but DELETES
        /// VISIBLE STRUCTURES (building fronts, fences), so it is opt-in.</summary>
        All = 3,
    }

    private static readonly Obj533Scope ObjFixScope =
        Environment.GetEnvironmentVariable("P1998_OBJ_FIX_533")?.Trim().ToLowerInvariant() switch
        {
            "off"        => Obj533Scope.Off,
            "decor"      => Obj533Scope.Decor,
            "all"        => Obj533Scope.All,
            "structural" => Obj533Scope.All,
            _            => Obj533Scope.Free,   // default: never change what is on screen
        };

    private readonly record struct Obj533Fix(ushort Replacement, Obj533Scope Scope);

    private static readonly IReadOnlyDictionary<ushort, Obj533Fix> Obj533 = LoadObj533();

    /// <summary>Objects currently being rewritten for 5.33 at the configured scope.</summary>
    public static int Obj533FixCount => Obj533.Count(kv => kv.Value.Scope <= ObjFixScope);

    /// <summary>The scope in effect (for tests and the startup log).</summary>
    public static Obj533Scope Obj533FixScope => ObjFixScope;

    /// <summary>
    /// Reads <c>game-data/Obj533Fix.csv</c> — the workaround for 5.33's re-authored collision flags.
    ///
    /// <para>The 5.33 client collides locally against its own <c>SOBJ.TBL</c>, which Nexon re-authored: 18,025
    /// cells (1.05%, in 620 of 1,750 maps) carry an object that blocks on 5.33 but not on 4.x. Arctic Village
    /// 35,32 / 36,32 — a staircase under objects 327 and 320, both <c>0x00</c> on 4.x and <c>0x0F</c> on
    /// 5.33 — is the case that surfaced it.</para>
    ///
    /// <para><b>Why the server cannot fix this cleanly.</b> Graphic and collision are the SAME wire value, so
    /// the only moves are "send a different object" or "send none". Matching on rendered sprite content finds
    /// a look-alike with usable flags for only 4 of 128 objects; everything else can only be blanked, losing
    /// its artwork.</para>
    ///
    /// <para><b>So we patch the client instead (2026-08-20).</b>
    /// <c>re/patches/patch_533_sobj_flags.py</c> rewrites the 362 differing flag bytes in the client's own
    /// <c>SOBJ.TBL</c> to their 4.x values — same length, in place, no repack. Verified across all 1,840 maps:
    /// cells reachable on 4.x but not on 5.33 goes <b>17,841 to 0</b>, with zero artwork loss, and the 3,296
    /// under-blocked cells (5.33 starts a step the server then refuses) go with it. This table is now the
    /// FALLBACK for an unpatched client; run a patched one with <c>P1998_OBJ_FIX_533=off</c>.</para>
    ///
    /// <para>The divergence was <b>structural, not cosmetic</b> — a 4.95 and a 5.33 player in the same room
    /// had different walkable geometry — which is why no server-side scope was an acceptable answer. Note
    /// also that <c>TILEC</c> was NOT re-packed (this comment used to claim it was): it is 4.x's
    /// <c>TileC.epf</c> with a null frame prepended and frames appended, TOC entry <c>i</c> == 5.33's
    /// <c>i+1</c> for all 16,408 frames, so object ARTWORK never needed translating — only the flags.</para>
    ///
    /// <para><b>Walkability stays correct regardless</b> — the server enforces the 4.x flags itself
    /// (<c>ObjectFlags</c> in <c>HandleWalk</c>), so a blanked object still blocks exactly as 4.x intended,
    /// just as a <c>0x04</c> snap-back rather than a client-side refusal.</para>
    /// </summary>
    private static IReadOnlyDictionary<ushort, Obj533Fix> LoadObj533()
    {
        var map = new Dictionary<ushort, Obj533Fix>();
        var path = Shared.RepoPaths.GameData("P1998_OBJ533_FIX", "Obj533Fix.csv");
        try
        {
            if (!File.Exists(path))
            {
                Log.Info($"   !! Obj533Fix.csv not found at {path} — 5.33 will over-block ~18k cells");
                return map;
            }
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(',');
                if (p.Length < 7 || !ushort.TryParse(p[0], out var id)
                    || !ushort.TryParse(p[2], out var rep)) continue;
                var scope = p[6].Trim().ToLowerInvariant() switch
                {
                    "free"       => Obj533Scope.Free,
                    "decor"      => Obj533Scope.Decor,
                    "structural" => Obj533Scope.All,
                    _            => Obj533Scope.All,
                };
                map[id] = new Obj533Fix(rep, scope);
            }
            int active = map.Count(kv => kv.Value.Scope <= ObjFixScope);
            Log.Info($"   .. Obj533Fix: {map.Count} objects known, {active} active at scope={ObjFixScope}");
        }
        catch (Exception e)
        {
            Log.Info($"   !! Obj533Fix.csv unreadable ({e.Message}) — 5.33 will over-block ~18k cells");
        }
        return map;
    }

    private static ushort Shift(ushort v, int off)
    {
        if (off == 0) return v;
        int r = v + off;
        return (ushort)(r < 0 ? 0 : r > ushort.MaxValue ? ushort.MaxValue : r);
    }

    /// <summary>One-line summary for the map-stream log, so a capture says which numbering it carried.</summary>
    public static string Describe(Session.ClientVersion ver)
    {
        if (ver != Session.ClientVersion.V533)
            return $"4.95 raw ground word objOff={ObjectOffset(ver)}";
        string src = LegacyGroundOff is null ? "" : " (P1998_TILE_OFF override)";
        return $"5.33 sheet1Off={GroundOffset(ver)}{src} sheet2Lut={Sheet2.Count} objOff={ObjectOffset(ver)} " +
               $"objFix={Obj533FixCount}@{ObjFixScope}";
    }

    /// <summary>Reads <c>game-data/Tile533Map.csv</c> (run-length: <c>startLegacy,count,start533</c>).</summary>
    private static IReadOnlyDictionary<ushort, ushort> LoadSheet2()
    {
        var map = new Dictionary<ushort, ushort>();
        var path = Shared.RepoPaths.GameData("P1998_TILE533_MAP", "Tile533Map.csv");
        try
        {
            if (!File.Exists(path))
            {
                Log.Info($"   !! Tile533Map.csv not found at {path} — 5.33 sheet-2 cells (30% of terrain) will be blank");
                return map;
            }
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(',');
                if (p.Length != 3
                    || !int.TryParse(p[0], out var start) || !int.TryParse(p[1], out var count)
                    || !int.TryParse(p[2], out var target)) continue;
                for (int i = 0; i < count; i++)
                    map[(ushort)(start + i)] = (ushort)(target + i);
            }
            Log.Info($"   .. Tile533Map: {map.Count} sheet-2 tiles -> 5.33 frames");
        }
        catch (Exception e)
        {
            Log.Info($"   !! Tile533Map.csv unreadable ({e.Message}) — 5.33 sheet-2 cells will be blank");
        }
        return map;
    }
}
