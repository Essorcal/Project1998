namespace Server;

public static partial class Content
{
    // A loader writes only to its thread-local builder. Runtime readers continue to see the last fully
    // published snapshot until the single Volatile.Write at the end of Load. The thread-local view also
    // preserves the loaders' existing dependency reads (for example LoadWarps reads Maps) without exposing
    // a half-built registry to another thread; #35 will make those dependencies explicit parameters.
    private static ContentSnapshot _snapshot = new ContentSnapshotBuilder().Build();

    [ThreadStatic]
    private static ContentSnapshotBuilder? _snapshotBuilder;

    private static ContentSnapshot Snapshot => Volatile.Read(ref _snapshot);

    private static ContentSnapshotBuilder Builder => _snapshotBuilder
        ?? throw new InvalidOperationException("Content registries can only be assigned while Content.Load is building a snapshot.");

    private static ContentSnapshotBuilder BeginSnapshotBuild()
    {
        if (_snapshotBuilder is not null)
            throw new InvalidOperationException("Content.Load cannot be nested on the same thread.");
        return _snapshotBuilder = new ContentSnapshotBuilder();
    }

    private static void PublishSnapshot(ContentSnapshotBuilder builder)
    {
        LoadStepForTests?.Invoke("BeforePublish");
        Volatile.Write(ref _snapshot, builder.Build());
    }

    private static void EndSnapshotBuild(ContentSnapshotBuilder builder)
    {
        if (ReferenceEquals(_snapshotBuilder, builder)) _snapshotBuilder = null;
    }

    internal static Action<string>? LoadStepForTests { get; set; }
    internal static object SnapshotIdentityForTests => Snapshot;
    internal static int SnapshotMemberCountForTests => ContentSnapshot.MemberCount;

    private sealed class ContentSnapshotBuilder
    {
        internal IReadOnlyList<ItemDef> Items { get; set; } = new List<ItemDef>();
        internal IReadOnlyDictionary<int, ItemDef> ItemById { get; set; } = new Dictionary<int, ItemDef>();
        internal IReadOnlyDictionary<string, ItemDef> ItemByKey { get; set; } =
            new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<(ushort Look, byte Dye), byte> ArmorDyeRamps { get; set; } =
            new Dictionary<(ushort, byte), byte>();
        internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ItemParams { get; set; } =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, WeaponProc> WeaponProcs { get; set; } =
            new Dictionary<string, WeaponProc>(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyDictionary<ushort, MapInfo> Maps { get; set; } = new Dictionary<ushort, MapInfo>();
        internal IReadOnlyDictionary<(ushort m, ushort x, ushort y), (ushort m, ushort x, ushort y)> Warps { get; set; } =
            new Dictionary<(ushort, ushort, ushort), (ushort, ushort, ushort)>();
        internal IReadOnlyDictionary<ushort, MapMetaInfo> MapMeta { get; set; } = new Dictionary<ushort, MapMetaInfo>();
        internal IReadOnlyList<MythicCaveDef> MythicCaves { get; set; } = new List<MythicCaveDef>();
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), MythicCaveDef> MythicCaveTiles { get; set; } =
            new Dictionary<(ushort, ushort, ushort), MythicCaveDef>();
        internal IReadOnlyList<EventCaveBand> EventCaveBands { get; set; } = new List<EventCaveBand>();
        internal IReadOnlyList<EventCaveDef> EventCaves { get; set; } = new List<EventCaveDef>();
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), EventCaveDef> EventCaveTiles { get; set; } =
            new Dictionary<(ushort, ushort, ushort), EventCaveDef>();
        internal IReadOnlyList<ArenaDoorDef> ArenaDoors { get; set; } = new List<ArenaDoorDef>();
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), ArenaDoorDef> ArenaDoorTiles { get; set; } =
            new Dictionary<(ushort, ushort, ushort), ArenaDoorDef>();
        internal IReadOnlyList<(ushort Map, ushort X, ushort Y, int BoardId)> BoardLocations { get; set; } =
            new List<(ushort, ushort, ushort, int)>();
        internal IReadOnlyDictionary<string, IReadOnlyList<InnDef>> Inns { get; set; } =
            new Dictionary<string, IReadOnlyList<InnDef>>(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyList<ForageAreaDef> ForageAreas { get; set; } = new List<ForageAreaDef>();
        internal IReadOnlyDictionary<ushort, PathHallDef> PathHalls { get; set; } = new Dictionary<ushort, PathHallDef>();
        internal IReadOnlyDictionary<int, GatewayDef> GatewayRegions { get; set; } = new Dictionary<int, GatewayDef>();
        internal IReadOnlyList<WorldDestDef> WorldDests { get; set; } = new List<WorldDestDef>();
        internal IReadOnlyDictionary<ushort, WorldTriggerDef> WorldMapTriggers { get; set; } =
            new Dictionary<ushort, WorldTriggerDef>();
        internal IReadOnlyDictionary<ushort, (ushort Map, ushort X, ushort Y)> FallRooms { get; set; } =
            new Dictionary<ushort, (ushort, ushort, ushort)>();
        internal IReadOnlyDictionary<int, (int StartDx, ushort[] Objs)> DoorSwaps { get; set; } =
            new Dictionary<int, (int, ushort[])>();
        internal IReadOnlyList<(int Lo, int Hi, int Delta)> DoorDeltas { get; set; } = new List<(int, int, int)>();
        internal IReadOnlyDictionary<int, ushort> DoorDefaultOpen { get; set; } = new Dictionary<int, ushort>();
        internal IReadOnlyDictionary<ushort, List<CellOverride>> MapCells { get; set; } =
            new Dictionary<ushort, List<CellOverride>>();
        internal int MapCellCount { get; set; }

        internal ContentSnapshot Build() => new(this);
    }

    private sealed class ContentSnapshot
    {
        internal const int MemberCount = 29;

        internal ContentSnapshot(ContentSnapshotBuilder builder)
        {
            Items = builder.Items;
            ItemById = builder.ItemById;
            ItemByKey = builder.ItemByKey;
            ArmorDyeRamps = builder.ArmorDyeRamps;
            ItemParams = builder.ItemParams;
            WeaponProcs = builder.WeaponProcs;
            Maps = builder.Maps;
            Warps = builder.Warps;
            MapMeta = builder.MapMeta;
            MythicCaves = builder.MythicCaves;
            MythicCaveTiles = builder.MythicCaveTiles;
            EventCaveBands = builder.EventCaveBands;
            EventCaves = builder.EventCaves;
            EventCaveTiles = builder.EventCaveTiles;
            ArenaDoors = builder.ArenaDoors;
            ArenaDoorTiles = builder.ArenaDoorTiles;
            BoardLocations = builder.BoardLocations;
            Inns = builder.Inns;
            ForageAreas = builder.ForageAreas;
            PathHalls = builder.PathHalls;
            GatewayRegions = builder.GatewayRegions;
            WorldDests = builder.WorldDests;
            WorldMapTriggers = builder.WorldMapTriggers;
            FallRooms = builder.FallRooms;
            DoorSwaps = builder.DoorSwaps;
            DoorDeltas = builder.DoorDeltas;
            DoorDefaultOpen = builder.DoorDefaultOpen;
            MapCells = builder.MapCells;
            MapCellCount = builder.MapCellCount;
        }

        internal IReadOnlyList<ItemDef> Items { get; }
        internal IReadOnlyDictionary<int, ItemDef> ItemById { get; }
        internal IReadOnlyDictionary<string, ItemDef> ItemByKey { get; }
        internal IReadOnlyDictionary<(ushort Look, byte Dye), byte> ArmorDyeRamps { get; }
        internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ItemParams { get; }
        internal IReadOnlyDictionary<string, WeaponProc> WeaponProcs { get; }
        internal IReadOnlyDictionary<ushort, MapInfo> Maps { get; }
        internal IReadOnlyDictionary<(ushort m, ushort x, ushort y), (ushort m, ushort x, ushort y)> Warps { get; }
        internal IReadOnlyDictionary<ushort, MapMetaInfo> MapMeta { get; }
        internal IReadOnlyList<MythicCaveDef> MythicCaves { get; }
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), MythicCaveDef> MythicCaveTiles { get; }
        internal IReadOnlyList<EventCaveBand> EventCaveBands { get; }
        internal IReadOnlyList<EventCaveDef> EventCaves { get; }
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), EventCaveDef> EventCaveTiles { get; }
        internal IReadOnlyList<ArenaDoorDef> ArenaDoors { get; }
        internal IReadOnlyDictionary<(ushort Map, ushort X, ushort Y), ArenaDoorDef> ArenaDoorTiles { get; }
        internal IReadOnlyList<(ushort Map, ushort X, ushort Y, int BoardId)> BoardLocations { get; }
        internal IReadOnlyDictionary<string, IReadOnlyList<InnDef>> Inns { get; }
        internal IReadOnlyList<ForageAreaDef> ForageAreas { get; }
        internal IReadOnlyDictionary<ushort, PathHallDef> PathHalls { get; }
        internal IReadOnlyDictionary<int, GatewayDef> GatewayRegions { get; }
        internal IReadOnlyList<WorldDestDef> WorldDests { get; }
        internal IReadOnlyDictionary<ushort, WorldTriggerDef> WorldMapTriggers { get; }
        internal IReadOnlyDictionary<ushort, (ushort Map, ushort X, ushort Y)> FallRooms { get; }
        internal IReadOnlyDictionary<int, (int StartDx, ushort[] Objs)> DoorSwaps { get; }
        internal IReadOnlyList<(int Lo, int Hi, int Delta)> DoorDeltas { get; }
        internal IReadOnlyDictionary<int, ushort> DoorDefaultOpen { get; }
        internal IReadOnlyDictionary<ushort, List<CellOverride>> MapCells { get; }
        internal int MapCellCount { get; }
    }
}
