using System.Buffers.Binary;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The GM's <c>@kill</c> (<c>Session.KillMobs</c>, which calls <c>World.ClearMap</c>) takes off a map what a
/// despawn may take off it, and does a despawn's bookkeeping for each creature it takes. The model is
/// <c>World.DespawnMob</c>: it refuses an NPC and frees a spawn point's creature's point, so the point respawns on
/// its own timer.
///
/// <para>Driven through the real command (a framed chat line into <c>Session.Receive</c>, the shape
/// <see cref="CommandTableTests"/> uses) and the real beat (<c>TickOnceForTest</c>). The spawn point and the
/// creatures are synthetic, as in <see cref="SpawnDirectorTests"/>: stationary, unaggressive, ids no content row
/// uses. The NPC fact runs on a real map (7, Redcap Message) so its NPC is the one <c>World.PlaceNpc</c> put
/// there at start-up. The map's contents are asserted, not assumed. Every fact undoes what it placed in a
/// <c>finally</c>, since the fixture World is shared by the collection.</para>
///
/// <para>Each fact's doc names the edit that turns it red; each was run red against that edit in Debug and in
/// Release. On master's <c>ClearMap</c> the first three facts are red and the local-dummy fact is green.</para>
/// </summary>
[Collection("world")]
public sealed class GmKillTests
{
    private readonly SessionFixture _fx;

    public GmKillTests(SessionFixture fx)
    {
        _fx = fx;
        EnsureGmRoster();
    }

    // Content-free maps in the instance band (no dimensions, no spawn rows), one per fact.
    private const ushort PointMap = 61780, MixedMap = 61781, DummyMap = 61782;
    private const ushort NpcMap = 7;    // Redcap Message, 12x12: one NPC and nothing that spawns

    private World.SpawnDirector Spawns => _fx.World.SpawnsForTest;

    private static MobDef Creature(int id, string key) =>
        new()
        {
            Id = id,
            Key = key,
            Name = "Test " + key,
            Look = 1,
            Color = 0,
            Hp = 100,
            Exp = 0,
            Level = 1,
            MoveTime = 1_000_000,
            Stationary = true,
        };

    // =====================================================================================================
    // A spawn point's creature comes back after @kill.
    // =====================================================================================================

    /// <summary>One spawn point, three-beat respawn timer. Entering the map materialises it. <c>@kill</c> takes
    /// the creature off, and the point refills on its own timer: not on beats one and two, and on beat three a
    /// new creature stands on the point's home tile. The point's index entry for the killed creature goes too:
    /// after the refill the index holds as many entries as before the kill.
    ///
    /// <para>Falsified by deleting the <c>ReleasePoint</c> call in <c>World.ClearMap</c>: red with "the point
    /// should refill on beat 3 after @kill; 0 creature(s) on the map", which is what master did. Deleting
    /// <c>_mobSpawn.Remove</c> from <c>SpawnDirector.ReleasePoint</c> turns the last line red instead (2 index
    /// entries, expected 1).</para></summary>
    [Fact]
    public void AKilledSpawnPointCreatureComesBackOnItsOwnTimer()
    {
        const int respawnEvery = 3;
        var def = Creature(990_101, "test_kill_point");
        _fx.World.UnderWorldLockForTest(() => Spawns.AddPointForTest(PointMap, def, 5, 5, respawnEvery));
        var (gm, gmOut, _) = _fx.PlayerWith(GmName, _ => { }, PointMap, 5, 10);
        try
        {
            var first = Assert.Single(MobsOf(gm, PointMap, def));          // EnterMap materialised the point
            Assert.Equal(((ushort)5, (ushort)5), (first.X, first.Y));
            int indexed = Locked(() => Spawns.MobSpawnIndexCountForTest());

            gmOut.Clear();
            Run(gm, "@kill");
            Assert.Equal(Paned("cleared 1 world mob(s) + 0 local dummy(s)"), CommandTableTests.Transcript(gmOut));
            Assert.Empty(MobsOf(gm, PointMap, def));

            for (int beat = 1; beat < respawnEvery; beat++)
            {
                _fx.World.TickOnceForTest();
                Assert.True(MobsOf(gm, PointMap, def).Count == 0,
                            $"the point refilled on beat {beat}, before its {respawnEvery}-beat timer");
            }
            _fx.World.TickOnceForTest();

            var back = MobsOf(gm, PointMap, def);
            Assert.True(back.Count == 1,
                        $"the point should refill on beat {respawnEvery} after @kill; {back.Count} creature(s) on the map");
            Assert.NotEqual(first.Id, back[0].Id);                          // a new creature, not the old one back
            Assert.Equal(((ushort)5, (ushort)5), (back[0].X, back[0].Y));   // on the point's home tile
            Assert.Equal(indexed, Locked(() => Spawns.MobSpawnIndexCountForTest()));   // old entry out, new one in
        }
        finally
        {
            _fx.World.LeaveMap(gm, PointMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(PointMap));
        }
    }

    // =====================================================================================================
    // An NPC stays.
    // =====================================================================================================

    /// <summary>A GM and a watcher on a town map with one NPC and one ordinary creature, both drawn on the
    /// watcher's screen. <c>@kill</c> takes the creature and leaves the NPC: the NPC is still in the map's list
    /// under the same id, and the only despawn either player receives carries the creature's id.
    ///
    /// <para>Falsified three ways in <c>World.ClearMap</c>. Taking every entry, NPCs included, is red with "@kill
    /// removed NPC #… (Redcap)", which is what master did. Keeping the NPC but adding the ids still on the map
    /// to the despawn broadcast is red on the watcher's despawn list, which then carries the NPC's id. Returning
    /// the list's size before the removal is red on the reply ("cleared 2 world mob(s)").</para></summary>
    [Fact]
    public void AnNpcSurvivesKillAndNoPlayerIsToldItLeft()
    {
        var npcDef = AssertNpcMapIsWhatTheDocSays();
        var (watcher, watcherOut, _) = _fx.PlayerWith("KillNpcWatcher", _ => { }, NpcMap, 8, 6);
        var (gm, gmOut, _) = _fx.PlayerWith(GmName, _ => { }, NpcMap, 4, 9);
        var creature = new Mob(_fx.World.AllocateMobId(), 1, 6, 6, "KillNpcCreature", 100);
        try
        {
            var npc = Assert.Single(_fx.World.View(gm, NpcMap).mobs, m => m.IsNpc);
            Assert.Equal(npcDef.Id, npc.NpcDefId);
            _fx.World.AddMob(NpcMap, creature);
            _fx.World.TickOnceForTest();                                     // the watcher's sweep draws the NPC
            Assert.Contains(npc.Id, CreatureIds(watcherOut));
            Assert.Contains(creature.Id, CreatureIds(watcherOut));

            watcherOut.Clear();
            gmOut.Clear();
            Run(gm, "@kill");

            var left = _fx.World.View(gm, NpcMap).mobs;
            Assert.True(left.Any(m => m.Id == npc.Id), $"@kill removed NPC #{npc.Id} ({npcDef.Name})");
            Assert.DoesNotContain(left, m => m.Id == creature.Id);
            Assert.Equal(new[] { creature.Id }, DespawnedIds(watcherOut));   // the creature, and only it
            Assert.Equal(new[] { creature.Id }, DespawnedIds(gmOut));
            Assert.Equal(Paned("cleared 1 world mob(s) + 0 local dummy(s)"), CommandTableTests.Transcript(gmOut));
        }
        finally
        {
            _fx.World.LeaveMap(gm, NpcMap);
            _fx.World.LeaveMap(watcher, NpcMap);
            _fx.World.DespawnMob(NpcMap, creature);    // still there only if @kill missed it
            _fx.World.EnableNpc(npcDef.Id);            // re-placed only if @kill took it; a no-op otherwise
        }
    }

    /// <summary>Map 7 is what the class doc says: exactly one enabled NPC, and no spawn row, area spawn,
    /// ambush or forage area that could put anything else on it. Returns the NPC's def.</summary>
    private static NpcDef AssertNpcMapIsWhatTheDocSays()
    {
        Assert.True(Content.Maps.TryGetValue(NpcMap, out var info) && info.Xs == 12 && info.Ys == 12);
        var npcDef = Assert.Single(Content.Npcs, n => n.Map == NpcMap && n.Enabled);
        Assert.Empty(Content.SpawnsFor(NpcMap));
        Assert.DoesNotContain(Content.AreaSpawns, a => a.Map == NpcMap);
        Assert.False(Content.Ambushes.ContainsKey(NpcMap));
        Assert.DoesNotContain(Content.ForageAreas, f => f.Map == NpcMap);
        return npcDef;
    }

    // =====================================================================================================
    // Every non-NPC creature goes, and the reply counts them.
    // =====================================================================================================

    /// <summary>Three kinds of non-NPC creature and an NPC on one map: a spawn point's creature, a GM summon
    /// (<c>@mob 1 20</c>), and a GM summon made with no hit points (<c>@mob 1 0</c>), which no client draws,
    /// nothing can damage and <c>DespawnMob</c> refuses, so <c>@kill</c> is the only way to remove it short of a
    /// reload. <c>@kill</c> takes all three, leaves the NPC, and its reply counts the three.
    ///
    /// <para>Falsified by narrowing <c>World.ClearMap</c>'s filter to <c>DespawnMob</c>'s whole predicate
    /// (<c>Alive &amp;&amp; !IsNpc</c>): red with the no-HP summon still on the map. Also red when ClearMap takes
    /// the NPC, when it despawns an id it did not take, and when it returns the list's size before the removal
    /// ("cleared 4 world mob(s)").</para></summary>
    [Fact]
    public void KillTakesEveryNonNpcCreatureAndCountsOnlyThose()
    {
        var def = Creature(990_102, "test_kill_mixed");
        const int npcDefId = 990_103;   // no NPCs.csv row uses it; the cleanup removes by it
        _fx.World.UnderWorldLockForTest(() => Spawns.AddPointForTest(MixedMap, def, 2, 2, respawnEvery: 1_000));
        var (gm, gmOut, _) = _fx.PlayerWith(GmName, c => c.Dir = 0, MixedMap, 5, 10);   // facing north
        var npc = new Mob(_fx.World.AllocateMobId(), 1, 8, 8, "Test kill NPC", 1) { IsNpc = true, NpcDefId = npcDefId };
        try
        {
            _fx.World.AddMob(MixedMap, npc);
            Run(gm, "@mob 1 20");
            Run(gm, "@mob 1 0");
            var before = _fx.World.View(gm, MixedMap).mobs;
            Assert.Equal(4, before.Length);
            Assert.Single(before, m => m.DefId == def.Id);                  // the point's creature
            Assert.Single(before, m => m.Hp == 20 && !m.IsNpc);              // the summon
            Assert.Single(before, m => m.Hp == 0 && !m.Alive);               // the summon with no hit points

            gmOut.Clear();
            Run(gm, "@kill");

            var left = _fx.World.View(gm, MixedMap).mobs;
            Assert.True(left.Length == 1 && left[0].Id == npc.Id,
                        $"@kill should leave only the NPC; left: {string.Join(", ", left.Select(m => $"#{m.Id} {m.Name} hp {m.Hp} npc {m.IsNpc}"))}");
            Assert.Equal(before.Where(m => !m.IsNpc).Select(m => m.Id).OrderBy(id => id), DespawnedIds(gmOut).OrderBy(id => id));
            Assert.Equal(Paned("cleared 3 world mob(s) + 0 local dummy(s)"), CommandTableTests.Transcript(gmOut));
        }
        finally
        {
            _fx.World.LeaveMap(gm, MixedMap);
            _fx.World.UnderWorldLockForTest(() => Spawns.ForgetMapForTest(MixedMap));
            _fx.World.ClearMap(MixedMap);         // anything @kill left behind
            _fx.World.DisableNpc(npcDefId);       // the NPC, which ClearMap leaves
        }
    }

    // =====================================================================================================
    // Local dummies are unchanged.
    // =====================================================================================================

    /// <summary><c>@spawn</c> draws four session-local dummies on the GM's own screen. <c>@kill</c> despawns
    /// them for the GM only, in one 0x0E, and counts them as local; a watcher on the same map receives no
    /// despawn, and the map's shared list was empty before and after. This half of <c>@kill</c> is not changed;
    /// the fact pins that it stays as it was.
    ///
    /// <para>Falsified by deleting the local-dummy <c>SendDespawn</c> in <c>Session.KillMobs</c>: red on the
    /// GM's despawn ids (none, where the four dummies were expected).</para></summary>
    [Fact]
    public void KillDespawnsLocalDummiesForTheCallerOnly()
    {
        var (watcher, watcherOut, _) = _fx.PlayerWith("KillDummyWatcher", _ => { }, DummyMap, 6, 6);
        var (gm, gmOut, _) = _fx.PlayerWith(GmName, _ => { }, DummyMap, 5, 5);
        try
        {
            gmOut.Clear();
            Run(gm, "@spawn 1 6");
            var dummies = CreatureIds(gmOut);
            Assert.Equal(4, dummies.Count);
            Assert.Empty(_fx.World.View(gm, DummyMap).mobs);                 // session-local: not in the world

            gmOut.Clear();
            watcherOut.Clear();
            Run(gm, "@kill");

            Assert.Equal(dummies.OrderBy(id => id), DespawnedIds(gmOut).OrderBy(id => id));
            Assert.Single(gmOut.BodiesOf(ServerOp.Despawn));                   // one 0x0E on 4.95, all four ids
            Assert.Empty(DespawnedIds(watcherOut));
            Assert.Empty(_fx.World.View(gm, DummyMap).mobs);
            Assert.Equal(Paned("cleared 0 world mob(s) + 4 local dummy(s)"), CommandTableTests.Transcript(gmOut));
        }
        finally
        {
            _fx.World.LeaveMap(gm, DummyMap);
            _fx.World.LeaveMap(watcher, DummyMap);
        }
    }

    // ===== plumbing =========================================================================================

    private List<Mob> MobsOf(Session viewer, ushort map, MobDef def) =>
        _fx.World.View(viewer, map).mobs.Where(m => m.DefId == def.Id).ToList();

    private T Locked<T>(Func<T> read)
    {
        T value = default!;
        _fx.World.UnderWorldLockForTest(() => value = read());
        return value;
    }

    /// <summary>Every entity id in every 0x0E a session received, in order. 4.95 shape (the fixture's default
    /// port): <c>count(u8)</c> then that many <c>id(u32 BE)</c>.</summary>
    private static List<uint> DespawnedIds(RecordingOutbound outbound)
    {
        var ids = new List<uint>();
        foreach (var b in outbound.BodiesOf(ServerOp.Despawn))
            for (int i = 0; i < b[0]; i++) ids.Add(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(1 + i * 4)));
        return ids;
    }

    /// <summary>Every entity id in every 0x07 creature list a session received: <c>count(u16 BE)</c>, then
    /// 12-byte entries with the id at +4 (Session.SendCreatureList).</summary>
    private static List<uint> CreatureIds(RecordingOutbound outbound)
    {
        var ids = new List<uint>();
        foreach (var b in outbound.BodiesOf(ServerOp.CreatureList))
        {
            int n = BinaryPrimitives.ReadUInt16BigEndian(b);
            for (int i = 0; i < n; i++) ids.Add(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(2 + i * 12 + 4)));
        }
        return ids;
    }

    /// <summary>A one-line command reply as the status pane receives it: the invocation's rule, then the line
    /// wrapped to the pane (CommandTableTests' shape).</summary>
    private static IEnumerable<string> Paned(string reply) =>
        new[] { $"pane3|{Session.PaneRule}" }.Concat(Session.WrapForPane(reply).Select(l => "pane3|" + l));

    /// <summary>Run a command the way the read loop runs one: a framed chat packet into <c>Session.Receive</c>.</summary>
    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[0] = 0;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(ClientOp.Chat, body));
    }

    /// <summary>The staff name these facts run as: the same name and roster content <see cref="CommandTableTests"/>
    /// writes, because <c>StaffAccounts.Load</c> replaces the roster wholesale.</summary>
    private const string GmName = "cmdgm";
    private static bool _rosterWritten;

    private static void EnsureGmRoster()
    {
        lock (TestProcessState.Gate)
        {
            if (_rosterWritten) return;
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
            _rosterWritten = true;
        }
    }
}
