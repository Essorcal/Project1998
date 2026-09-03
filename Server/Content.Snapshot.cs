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

        internal ContentSnapshot Build() => new(this);
    }

    private sealed class ContentSnapshot
    {
        internal const int MemberCount = 6;

        internal ContentSnapshot(ContentSnapshotBuilder builder)
        {
            Items = builder.Items;
            ItemById = builder.ItemById;
            ItemByKey = builder.ItemByKey;
            ArmorDyeRamps = builder.ArmorDyeRamps;
            ItemParams = builder.ItemParams;
            WeaponProcs = builder.WeaponProcs;
        }

        internal IReadOnlyList<ItemDef> Items { get; }
        internal IReadOnlyDictionary<int, ItemDef> ItemById { get; }
        internal IReadOnlyDictionary<string, ItemDef> ItemByKey { get; }
        internal IReadOnlyDictionary<(ushort Look, byte Dye), byte> ArmorDyeRamps { get; }
        internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ItemParams { get; }
        internal IReadOnlyDictionary<string, WeaponProc> WeaponProcs { get; }
    }
}
