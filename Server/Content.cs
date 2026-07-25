namespace Server;

/// <summary>A warpable map: id (== TK&lt;id&gt;.map and the 0x15 mapId), display name, and dimensions.</summary>
public sealed record MapInfo(ushort Id, string Name, ushort Xs, ushort Ys);

/// <summary>A summonable creature definition (name, sprite look, palette colour, HP, reward).</summary>
public sealed record MobDef(int Id, string Key, string Name, ushort Look, byte Color, int Hp, int Exp, int Level);

/// <summary>
/// An item definition from the RTK item db (Items.csv). Field names mirror the client's item_data
/// (see RTK itemdb.h). <c>Icon</c> is the inventory-window / ground (Item.epf) frame; <c>Look</c> is the
/// worn-appearance sprite. <c>Type</c> is ITM_* (0=eat,1=use,2=smoke,3=weap,4=armor,5=shield,6=helm,
/// 7=left,8=right,9=subleft,10=subright,11=faceacc,12=crown,13=mantle,14=necklace,15=boots,16=coat,
/// 18=etc/junk…). Stat lines feed the equip bonuses.
/// </summary>
public sealed record ItemDef(
    int Id, string Key, string Name, byte Type,
    ushort Icon, byte IconColor, ushort Look, byte LookColor,
    byte Sex, byte Level, ushort Durability, int StackAmount, int MaxAmount,
    int Armor, int Hit, int Dam, int Vita, int Mana, int Might, int Will, int Grace,
    bool NoDrop, bool Thrown, int BuyPrice, int SellPrice)
{
    /// <summary>ITM_WEAP..ITM_COAT (3..16) are wearable; everything else is consumable/junk.</summary>
    public bool IsEquip => Type is >= 3 and <= 16;
    public bool IsConsumable => Type is 0 or 1 or 2;     // EAT / USE / SMOKE
    public bool Stackable => StackAmount > 1 || MaxAmount > 1;

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

/// <summary>
/// In-memory game-content registries loaded ONCE at startup from EXTERNAL, gitignored data
/// (RTK-derived — see docs §17.1). The loader lives in the repo; the data does not, keeping this a
/// logic-only server. Everything here is read-only after <see cref="Load"/>, so it is safe to share
/// across all sessions without locking. Missing data degrades gracefully (empty registries + a log).
/// </summary>
public static class Content
{
    // id -> map. Only maps whose dims were validated against the client's own TK&lt;id&gt;.map (see
    // re/build_map_index.py) are present, so a warp target here is always renderable.
    public static IReadOnlyDictionary<ushort, MapInfo> Maps { get; private set; } =
        new Dictionary<ushort, MapInfo>();
    public static IReadOnlyList<MobDef> Mobs { get; private set; } = new List<MobDef>();
    public static IReadOnlyList<ItemDef> Items { get; private set; } = new List<ItemDef>();

    // Portals/doors: (sourceMap, x, y) -> (destMap, x, y). Only warps whose DESTINATION is a renderable
    // client map are kept (a warp to a 7.x-only map would strand the player on a black screen).
    public static IReadOnlyDictionary<(ushort m, ushort x, ushort y), (ushort m, ushort x, ushort y)> Warps
    { get; private set; } = new Dictionary<(ushort, ushort, ushort), (ushort, ushort, ushort)>();

    public static void Load()
    {
        Maps = LoadMaps(ResolvePath("NEXUS_MAP_INDEX", "re", "rtk-data", "map_index.csv"));
        Mobs = LoadMobs(ResolvePath("NEXUS_MOBS", "re", "monster-matcher", "rtk_mobs.csv"));
        Items = LoadItems(ResolvePath("NEXUS_ITEMS", "re", "rtk-data", "Items.csv"));
        Warps = LoadWarps(ResolvePath("NEXUS_WARPS", "re", "rtk-data", "Warps.csv"));   // needs Maps
        Log.Info($"content: {Maps.Count} maps, {Mobs.Count} mobs, {Items.Count} items, {Warps.Count} warps loaded" +
                 (Maps.Count == 0 || Mobs.Count == 0
                     ? "  (some empty — run re/build_map_index.py and check re/monster-matcher/rtk_mobs.csv)"
                     : ""));
    }

    /// <summary>The portal at (map, x, y), if the player just stepped on a door tile.</summary>
    public static bool TryWarp(ushort map, ushort x, ushort y, out (ushort m, ushort x, ushort y) dest)
        => Warps.TryGetValue((map, x, y), out dest);

    /// <summary>Offline check of the registries + fuzzy lookups (run via <c>--selftest</c>).</summary>
    public static void SelfTest()
    {
        Load();
        void Line(string s) => Log.Info(s);

        Line("--- FindMap (exact id / exact name / substring / subsequence) ---");
        foreach (var q in new[] { "0", "kugnae", "buya", "walsuk tavern", "kgne" })
        {
            var m = FindMap(q);
            Line($"  !warp {q,-16} -> " + (m is null ? "(no match)" : $"map {m.Id} '{m.Name}' {m.Xs}x{m.Ys}"));
        }

        Line("--- FindMob (name / key / id / fuzzy) ---");
        foreach (var q in new[] { "rabbit", "1", "great_horns", "great horns", "grhrn", "fox" })
        {
            var mob = FindMob(q);
            Line($"  !summon {q,-14} -> " + (mob is null ? "(no match)" : $"'{mob.Name}' look {mob.Look} c{mob.Color} {mob.Hp}hp {mob.Exp}xp"));
        }

        Line("--- FindItem (name / key / id) ---");
        foreach (var q in new[] { "apple", "stick", "leather", "sword", "0" })
        {
            var it = FindItem(q);
            Line($"  !item {q,-12} -> " + (it is null ? "(no match)"
                : $"#{it.Id} '{it.Name}' type{it.Type} icon{it.Icon} look{it.Look} {(it.IsEquip ? $"EQUIP slot{it.EquipSlot}" : "use")}"));
        }

        Line("--- SearchMaps(\"buya\", 5) ---");
        foreach (var m in SearchMaps("buya", 5)) Line($"    {m.Id}: {m.Name} ({m.Xs}x{m.Ys})");
        Line("--- SearchMobs(\"wolf\", 5) ---");
        foreach (var m in SearchMobs("wolf", 5)) Line($"    {m.Name} look {m.Look} c{m.Color} {m.Hp}hp");
        Line("--- SearchItems(\"sword\", 5) ---");
        foreach (var i in SearchItems("sword", 5)) Line($"    #{i.Id} {i.Name} type{i.Type} dam{i.Dam} icon{i.Icon}");

        bool ok = Maps.Count > 0 && Mobs.Count > 0 && Items.Count > 0
                  && FindMap("kugnae") is not null && FindMob("rabbit") is not null;
        Line(ok ? "SELFTEST: PASS" : "SELFTEST: FAIL (empty registry or missing expected entry)");
    }

    // ---- background music (0x19) --------------------------------------------------------------
    // The stock 4.95 client keeps its audio in NexusTK.snd, which ships exactly 12 background tracks
    // (1.mid .. 12.mid); the 0x19 music packet plays one by id with type 2 = MIDI. There is no original
    // map->track table in the client files, so we assign them: a few iconic hubs get a fixed theme, and
    // every other map gets a stable pick from its id (so neighbouring maps tend to differ). Tune freely.
    private static readonly Dictionary<ushort, byte> BgmByMap = new()
    {
        [0]   = 1,    // Kugnae (town)
        [330] = 2,    // Buya (town)
        [41]  = 5,    // Mythic Nexus
        [24]  = 6,    // Kugnae Donjon (dungeon)
    };

    /// <summary>The background track for a map: (bgm id 1..12, type 2 = MIDI). Iconic hubs are fixed;
    /// anything else maps deterministically onto one of the 12 stock midis via its id.</summary>
    public static (ushort bgm, byte type) BgmFor(ushort mapId)
    {
        byte bgm = BgmByMap.TryGetValue(mapId, out var pick) ? pick : (byte)((mapId % 12) + 1);
        return (bgm, 2);
    }

    // ---- lookups (used by the !warp / !maps / !mobs / !summon commands) ----

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

    public static ItemDef? ItemById(int id) => Items.FirstOrDefault(i => i.Id == id);

    public static List<ItemDef> SearchItems(string query, int limit) =>
        RankByName(Items, query, i => i.Name).Take(limit).ToList();

    // ---- fuzzy ranking (shared by maps + mobs) ----

    private static T? BestByName<T>(IEnumerable<T> items, string q, Func<T, string> name) where T : class =>
        RankByName(items, q, name).FirstOrDefault();

    // Rank: exact (0) < prefix (1) < substring (2) < subsequence (3); ties broken by shorter name.
    // A blank query returns everything alphabetically (so "!maps" with no arg lists all).
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
            var name = Clean(col.GetValueOrDefault("Description", ""));
            var key = Clean(col.GetValueOrDefault("Identifier", ""));
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(key) ? $"mob{id}" : key;
            mobs.Add(new MobDef(id, key, name, look, color, hp <= 0 ? 1 : hp, exp, lvl));
        }
        return mobs;
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
                I("ItmBuyPrice"), I("ItmSellPrice")));
        }
        return items;
    }

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
                && Maps.ContainsKey(dm))          // don't warp to a map the client can't render
            {
                warps[(sm, sx, sy)] = (dm, dx, dy);   // last write wins on duplicate source tiles
            }
        }
        return warps;
    }

    // Minimal CSV reader: header row -> per-row {column: value} dicts. Handles quoted fields with commas.
    private static IEnumerable<Dictionary<string, string>> ReadCsv(string? path)
    {
        if (path is null || !File.Exists(path)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { yield break; }
        if (lines.Length < 2) yield break;

        var header = SplitCsv(lines[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
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

    // Resolve an external data file: env override first, else <repo-root>/<parts...>. Repo root is the
    // dir holding the .sln (or Server+Shared), walked up from the running binary — cwd-independent.
    private static string? ResolvePath(string envVar, params string[] parts)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            bool isRoot = dir.GetFiles("*.sln").Length > 0
                       || (Directory.Exists(Path.Combine(dir.FullName, "Server"))
                           && Directory.Exists(Path.Combine(dir.FullName, "Shared")));
            if (isRoot) return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            dir = dir.Parent;
        }
        return null;
    }
}
