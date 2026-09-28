using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

/// <summary>
/// Another session's gear and buff totals, read without that session's monitor (PR #286 review F2).
///
/// <para>Three paths read a PEER's totals on the reader's own thread: the PvP swing (<c>SwingTarget.Of</c>),
/// the player-target spell armor (<c>LuaTargetArmor</c>, the cleanse roll) and divination. None of them may
/// enter the target's monitor: the reader already holds its own, and a second session monitor there is a lock
/// order Locking.md does not have. Before this change they read the target's cached gear sum, a 36-byte
/// nullable tuple written with no atomicity, RECOMPUTED it on a miss by walking the target's equipment list,
/// and walked the target's buff list on every read, while the target's own thread was changing both.</para>
///
/// <para>The owner now publishes an immutable snapshot under its own monitor, and a peer reads it with one
/// reference read. The two race facts hold the owner to whole changes and fail on anything else a peer sees:
/// a state the owner never published (half a gear change, a buff list that never existed) or an exception out
/// of the read. Both compile against the pre-change tree unchanged, which is how they were shown red there
/// (the negative control in the PR).</para>
/// </summary>
[Collection("world")]
public sealed class TotalsPeerReadTests
{
    private readonly SessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public TotalsPeerReadTests(SessionFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    /// <summary>Long enough that nothing under test expires on its own.</summary>
    private const int Forever = 10 * 60 * 1000;

    /// <summary>Wall clock each side of a race fact runs for. Both threads run to it, so neither can finish
    /// while the other is still warming up.</summary>
    private const int RaceMs = 500;

    /// <summary>Peer reads taken per entry into the caster's monitor. A caster's handler reads a peer's totals
    /// while holding its OWN monitor, so the reader does too; batching keeps the monitor traffic from being
    /// what the race measures.</summary>
    private const int ReadsPerHold = 64;

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The two peer reads a race fact can drive.</summary>
    public enum Reader
    {
        /// <summary>The PvP swing's defender stats, <c>SwingTarget.Of(Session)</c>: effective AC and grace.</summary>
        Swing,
        /// <summary>The player-target spell's <c>LuaTargetArmor</c>: effective AC only.</summary>
        SpellArmor,
    }

    // =====================================================================================================
    // The race facts.
    // =====================================================================================================

    /// <summary>
    /// The owner puts on two pieces of gear and takes both off, each change one critical section under its own
    /// monitor with one <c>InvalidateEquipTotals</c> at the end, as every real equip path does. A peer reads
    /// the owner's totals in a loop. The only states the owner ever finishes a change in are "neither worn" and
    /// "both worn"; a read of the platemail alone or the band alone is half a change, and any other pair is a
    /// torn read.
    ///
    /// <para>Red before the change: the peer's cache miss walked the list mid-change, and threw or summed half
    /// of it.</para>
    /// </summary>
    [Theory]
    [InlineData(Reader.Swing)]
    [InlineData(Reader.SpellArmor)]
    public void PeerReadsSeeWholeGearChangesOnly(Reader reader)
    {
        var plate = Item("war_platemail");
        var band = Item("sages_band");
        Assert.Equal((-12, 0), (plate.Armor, plate.Grace));
        Assert.Equal((-8, 5), (band.Armor, band.Grace));

        var (owner, ownerOut, oc) = _fx.PlayerWith($"TotalsGearOwner{reader}", c => { c.MaxHp = 100; c.Hp = 100; });
        var caster = Caster($"TotalsGearPeer{reader}", owner);

        var add = Bind<Action<InvItem>>(owner, "EquipAdd");
        var remove = Bind<Action<InvItem>>(owner, "EquipRemove");
        var invalidate = Bind<Action>(owner, "InvalidateEquipTotals");
        var plateWorn = new InvItem(plate.EquipSlot, plate.Id, 1, plate.Durability);
        var bandWorn = new InvItem(band.EquipSlot, band.Id, 1, band.Durability);

        var none = (oc.Ac + 0, oc.Grace + 0);
        var both = (oc.Ac + plate.Armor + band.Armor, oc.Grace + plate.Grace + band.Grace);
        Assert.Equal((uint)100, owner.LuaMaxHp);   // the owner's own first read: primes (and pins) the bare state

        var result = Race(caster, Read(reader, owner, caster), () =>
        {
            owner.WithState(() => { add(plateWorn); add(bandWorn); invalidate(); });
            owner.WithState(() => { remove(plateWorn); remove(bandWorn); invalidate(); });
            ownerOut.Clear();
        });

        AssertOnlyPublishedStates(result, reader, new[] { none, both }, none, both, "gear");
        // Last cycle took both off. A peer that cached what it summed mid-change would leave the owner's own
        // totals wrong after the race too (band: +1000 vita).
        Assert.Equal((uint)100, owner.LuaMaxHp);
        Assert.Empty(oc.Equipment);
    }

    /// <summary>
    /// The same race over the buff list. The owner adds three buffs in one critical section (armor -3, grace
    /// +4, armor -5, through the real <c>ReceiveCurse</c>) and clears them in another (<c>BuffClear</c>). Every
    /// buff writer publishes, so a peer may legitimately see any PREFIX of the three adds and the empty list,
    /// and nothing else: the second or third buff without the first is a list the owner never had.
    ///
    /// <para>Red before the change: the peer walked the owner's live <c>List</c> and threw
    /// "Collection was modified" or read an entry being cleared.</para>
    /// </summary>
    [Theory]
    [InlineData(Reader.Swing)]
    [InlineData(Reader.SpellArmor)]
    public void PeerReadsSeeWholeBuffChangesOnly(Reader reader)
    {
        var (owner, ownerOut, oc) = _fx.PlayerWith($"TotalsBuffOwner{reader}", c => { c.MaxHp = 100; c.Hp = 100; });
        var caster = Caster($"TotalsBuffPeer{reader}", owner);
        var clear = Bind<Action>(owner, "BuffClear");

        var prefixes = new[]
        {
            (oc.Ac + 0, oc.Grace + 0),
            (oc.Ac - 3, oc.Grace + 0),
            (oc.Ac - 3, oc.Grace + 4),
            (oc.Ac - 8, oc.Grace + 4),
        };
        Assert.Equal((uint)100, owner.LuaMaxHp);

        var result = Race(caster, Read(reader, owner, caster), () =>
        {
            owner.WithState(() =>
            {
                owner.ReceiveCurse("armor", -3, Forever, "totals_race_a1", "race", "");
                owner.ReceiveCurse("grace", 4, Forever, "totals_race_g", "race", "");
                owner.ReceiveCurse("armor", -5, Forever, "totals_race_a2", "race", "");
            });
            owner.WithState(clear);
            ownerOut.Clear();
        });

        AssertOnlyPublishedStates(result, reader, prefixes, prefixes[0], prefixes[3], "buff");
    }

    // =====================================================================================================
    // Every place the totals change publishes them, so a peer never reads a stale set.
    // =====================================================================================================

    /// <summary>Each way gear or buffs change, read by a peer before and after.</summary>
    public enum Change { Equip, Unequip, UnequipAll, Break, Strip, TakeReady, EquipClear, BuffAdd, BuffCure, BuffDispel, BuffLapse }

    /// <summary>The race facts prove a peer cannot see a torn or half-made state; this proves it is not shown a
    /// STALE one either. Every invalidation point (equip, unequip, unequip-all, break, the NPC strip, the
    /// armor-quest turn-in, the GM clear) and every buff writer (add, cure, dispel, and the tick's expiry sweep)
    /// has published by the time its handler returns, and the peer's number is the one the old code gave.</summary>
    [Theory]
    [InlineData(Change.Equip)]
    [InlineData(Change.Unequip)]
    [InlineData(Change.UnequipAll)]
    [InlineData(Change.Break)]
    [InlineData(Change.Strip)]
    [InlineData(Change.TakeReady)]
    [InlineData(Change.EquipClear)]
    [InlineData(Change.BuffAdd)]
    [InlineData(Change.BuffCure)]
    [InlineData(Change.BuffDispel)]
    [InlineData(Change.BuffLapse)]
    public void APeerSeesEveryChangeAsSoonAsItsHandlerReturns(Change change)
    {
        var plate = Item("war_platemail");
        var band = Item("sages_band");
        var steel = Item("cimmerian_steel");
        ItemDef[] worn = change switch
        {
            Change.Unequip or Change.Break => new[] { band },
            Change.UnequipAll or Change.Strip or Change.EquipClear => new[] { plate, band },
            Change.TakeReady => new[] { steel },
            _ => Array.Empty<ItemDef>(),
        };
        var (owner, _, oc) = _fx.PlayerWith($"TotalsChange{change}", c =>
        {
            c.Level = 99;
            c.Mark = 0;
            c.Might = 255;
            c.MaxHp = 100;
            c.Hp = 100;
            foreach (var def in worn) c.Equipment.Add(new InvItem(def.EquipSlot, def.Id, 1, def.Durability));
            if (change == Change.Equip) c.Inventory.Add(new InvItem(0, band.Id, 1, band.Durability));
        });
        var caster = Caster($"TotalsChangePeer{change}", owner);
        var swing = SwingOf();
        (int ac, int grace) Peer() => caster.WithState(() => swing(owner));
        int PeerArmor() => caster.WithState(() => caster.LuaTargetArmor);
        (int ac, int grace) With(int armor, int grace) => (oc.Ac + armor, oc.Grace + grace);

        _ = owner.LuaMaxHp;   // the owner's own first read, as its arrival's stats push would make
        var before = With(worn.Sum(d => d.Armor), worn.Sum(d => d.Grace));
        var after = With(0, 0);
        switch (change)
        {
            case Change.Equip:
                after = With(band.Armor, band.Grace);
                owner.WithState(() => Bind<Action<int>>(owner, "EquipFromSlot")(0));
                break;
            case Change.Unequip:
                owner.WithState(() => Bind<Action<byte[]>>(owner, "HandleUnequip")(new[] { band.EquipSlot }));
                break;
            case Change.UnequipAll:
                owner.WithState(Bind<Action>(owner, "UnequipAll"));
                break;
            case Change.Break:
                owner.WithState(() => Bind<Action<InvItem, ItemDef>>(owner, "BreakItem")(oc.Equipment.Single(), band));
                break;
            case Change.Strip:
                Assert.True(owner.WithState(() => owner.StripAllEquipment()));
                break;
            case Change.TakeReady:
                Assert.True(owner.WithState(() => owner.TakeReady("cimmerian_steel", 1)));
                break;
            case Change.EquipClear:
                owner.WithState(Bind<Action>(owner, "EquipClear"));
                break;
            case Change.BuffAdd:
                after = With(-6, 0);
                owner.ReceiveCurse("armor", -6, Forever, "totals_change_add", "change", "");
                break;
            case Change.BuffCure:
                before = With(-6, 0);
                owner.ReceiveCurse("armor", -6, Forever, "totals_change_cure", "change", "curses");
                Assert.Equal(before, Peer());
                Assert.Equal(1, owner.WithState(() => owner.LuaCureCategory("curses")));
                break;
            case Change.BuffDispel:
                before = With(0, 7);
                owner.ReceiveCurse("grace", 7, Forever, "totals_change_dispel", "change", "");
                Assert.Equal(before, Peer());
                owner.WithState(owner.DispelSelf);
                break;
            case Change.BuffLapse:
                // One buff that stays and one that lapses at once. A lapsed buff stops counting the moment it
                // lapses, before the tick sweeps it (the sum has always skipped it in place), and the sweep
                // itself must not bring it back.
                before = after = With(-6, 0);
                owner.ReceiveCurse("armor", -6, Forever, "totals_change_stays", "change", "");
                owner.ReceiveCurse("armor", -4, 1, "totals_change_lapses", "change", "");
                long lapsed = Environment.TickCount64 + 5;
                while (Environment.TickCount64 < lapsed) Thread.Yield();
                Assert.Equal(before, Peer());
                owner.RegenTick(0);
                break;
        }

        if (change != Change.BuffLapse)
            Assert.NotEqual(before, after);   // the change shows in what a peer reads, or this row proves nothing
        Assert.Equal(after, Peer());
        Assert.Equal(after.ac, PeerArmor());
    }

    // =====================================================================================================
    // Divination, and the stated fallback.
    // =====================================================================================================

    /// <summary>Divination shows the target's effective Might, Will and Grace: base plus gear plus buffs, Might
    /// clamped as <c>EffMight</c> clamps it. Pinned so reading all three from one snapshot changed no number.</summary>
    [Fact]
    public void DivinationShowsTheTargetsEffectiveAttributes()
    {
        var band = Item("sages_band");
        Assert.Equal((0, 5, 5), (band.Might, band.Will, band.Grace));
        var (owner, _, _) = _fx.PlayerWith("TotalsDivined", c =>
        {
            c.Might = 10;
            c.Will = 20;
            c.Grace = 30;
            c.Equipment.Add(new InvItem(band.EquipSlot, band.Id, 1, band.Durability));
        });
        var (caster, casterOut, _) = _fx.PlayerWith("TotalsDiviner", _ => { });
        var sp = Content.Spells.First();
        Assert.True(caster.LuaResolvePcTarget(sp, owner.PlayerId));
        _ = owner.LuaMaxHp;
        owner.ReceiveCurse("might", 3, Forever, "totals_divine_might", "divine", "");
        casterOut.Clear();

        caster.WithState(() => caster.LuaDivine(sp, showInventory: false));

        var m = Regex.Match(AllText(casterOut), @"Might: (\d+) Will: (-?\d+) Grace: (-?\d+)");
        Assert.True(m.Success, "no divination text reached the caster");
        Assert.Equal(("13", "25", "35"), (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
    }

    /// <summary>A peer that finds nothing published reads zero gear and buffs and logs it; it never walks the
    /// owner's lists to make up the difference. Production cannot reach this for a session on a map (the
    /// arrival's first stats push publishes before the map entry that makes the session targetable), but a
    /// socket-free test session can: the fixture enters the map without one.</summary>
    [Fact]
    public void APeerReadBeforeAnythingIsPublishedReadsZeroAndSaysSo()
    {
        var band = Item("sages_band");
        var (owner, _, oc) = _fx.PlayerWith("TotalsUnpublished", c =>
            c.Equipment.Add(new InvItem(band.EquipSlot, band.Id, 1, band.Durability)));
        var caster = Caster("TotalsUnpublishedPeer", owner);

        using (var log = LogLineSink.Acquire())
        {
            Assert.Equal(oc.Ac + 0, caster.WithState(() => caster.LuaTargetArmor));
            Assert.Contains(log.Lines, l => l.Line.Contains("TotalsUnpublished") && l.Line.Contains("no published totals"));
        }

        _ = owner.LuaMaxHp;   // the owner's own read publishes
        Assert.Equal(oc.Ac + band.Armor, caster.WithState(() => caster.LuaTargetArmor));
    }

    // =====================================================================================================
    // Harness.
    // =====================================================================================================

    private sealed record RaceResult(
        long Reads, long Cycles, Dictionary<(int ac, int grace), long> Seen,
        Dictionary<string, long> ReadFaults, Exception? FirstReadFault, Exception? OwnerFault);

    /// <summary>The owner runs <paramref name="ownerCycle"/> and the peer runs <paramref name="read"/> under
    /// the caster's monitor, both to the same wall clock. A read that throws is counted by exception type and
    /// the peer keeps reading, so one run reports both ways a read can go wrong: an exception, and a number.</summary>
    private static RaceResult Race(Session caster, Func<(int ac, int grace)> read, Action ownerCycle)
    {
        var start = new ManualResetEventSlim();
        var progress = new StallWatch.RoundCounter();
        var seen = new Dictionary<(int ac, int grace), long>();
        var readFaults = new Dictionary<string, long>();
        Exception? firstReadFault = null, ownerFault = null;
        long reads = 0, cycles = 0, until = 0;

        var ownerThread = new Thread(() =>
        {
            start.Wait();
            try
            {
                while (Environment.TickCount64 < until)
                {
                    ownerCycle();
                    cycles++;
                    progress.Bump();
                }
            }
            catch (Exception e) { ownerFault = e; }
        });

        var peerThread = new Thread(() =>
        {
            start.Wait();
            while (Environment.TickCount64 < until)
            {
                caster.WithState(() =>
                {
                    for (int i = 0; i < ReadsPerHold; i++)
                    {
                        reads++;
                        try
                        {
                            var v = read();
                            seen[v] = seen.GetValueOrDefault(v) + 1;
                        }
                        catch (Exception e)
                        {
                            firstReadFault ??= e;
                            string kind = e.GetType().Name;
                            readFaults[kind] = readFaults.GetValueOrDefault(kind) + 1;
                        }
                    }
                });
                progress.Bump();
            }
        });

        ownerThread.Start();
        peerThread.Start();
        until = Environment.TickCount64 + RaceMs;   // published to both threads by the Set/Wait pair below
        start.Set();
        StallWatch.RunUntilDoneOrStalled(new[] { ownerThread, peerThread }, () => progress.Rounds,
            StallWatch.StallQuiet, StallWatch.StallCap, "the totals owner and its peer reader");
        return new RaceResult(reads, cycles, seen, readFaults, firstReadFault, ownerFault);
    }

    /// <summary>Fails on any read that threw, any read of a state outside <paramref name="published"/>, and a
    /// race that never overlapped (the owner barely cycled, or the peer never saw both ends).</summary>
    private void AssertOnlyPublishedStates(RaceResult r, Reader reader, (int ac, int grace)[] published,
        (int ac, int grace) first, (int ac, int grace) last, string what)
    {
        (int ac, int grace) Project((int ac, int grace) v) => reader == Reader.SpellArmor ? (v.ac, 0) : v;
        var allowed = published.Select(Project).ToHashSet();
        long unpublished = r.Seen.Where(kv => !allowed.Contains(kv.Key)).Sum(kv => kv.Value);
        long threw = r.ReadFaults.Values.Sum();

        string summary = $"{r.Reads} peer read(s) over {r.Cycles} owner {what} cycle(s): {unpublished} saw a " +
                         $"{what} state the owner never published, {threw} threw " +
                         $"[{string.Join(" ", r.ReadFaults.Select(kv => $"{kv.Key}x{kv.Value}"))}]; published " +
                         $"{string.Join(" ", allowed)}; seen {string.Join(" ", r.Seen.Select(kv => $"{kv.Key}x{kv.Value}"))}";
        _out.WriteLine(summary);
        Assert.True(r.OwnerFault is null, $"the owner's {what} change threw ({summary}): {r.OwnerFault}");
        Assert.True(unpublished == 0 && threw == 0, $"torn or half-made peer reads ({summary}); first throw: {r.FirstReadFault}");
        Assert.True(r.Cycles > 50 && r.Seen.ContainsKey(Project(first)) && r.Seen.ContainsKey(Project(last)),
            $"the race never got going: {summary}");
    }

    private Session Caster(string name, Session target)
    {
        var (caster, _, _) = _fx.PlayerWith(name, _ => { });
        Assert.True(caster.LuaResolvePcTarget(Content.Spells.First(), target.PlayerId));
        return caster;
    }

    private static Func<(int ac, int grace)> Read(Reader reader, Session owner, Session caster)
    {
        if (reader == Reader.SpellArmor) return () => (caster.LuaTargetArmor, 0);
        var swing = SwingOf();
        return () => swing(owner);
    }

    /// <summary><c>SwingTarget.Of(Session)</c>, the PvP swing's read of its defender, compiled once. It is a
    /// private nested type, reached the way the other private entry points here are; the result goes through
    /// a local so ONE call supplies both numbers.</summary>
    private static Func<Session, (int ac, int grace)> SwingOf()
    {
        var type = typeof(Session).GetNestedType("SwingTarget", BindingFlags.NonPublic)!;
        var of = type.GetMethod("Of", BindingFlags.Public | BindingFlags.Static, new[] { typeof(Session) })!;
        var s = Expression.Parameter(typeof(Session), "s");
        var t = Expression.Variable(type, "t");
        var pair = typeof(ValueTuple<int, int>).GetConstructor(new[] { typeof(int), typeof(int) })!;
        var body = Expression.Block(new[] { t },
            Expression.Assign(t, Expression.Call(of, s)),
            Expression.New(pair, Expression.Property(t, "Ac"), Expression.Property(t, "Grace")));
        return Expression.Lambda<Func<Session, (int ac, int grace)>>(body, s).Compile();
    }

    private static T Bind<T>(Session s, string method) where T : Delegate =>
        (T)typeof(Session).GetMethod(method, Private)!.CreateDelegate(typeof(T), s);

    private static ItemDef Item(string key) => Content.Items.First(i => i.Key == key);

    /// <summary>Every frame the recorder holds, decrypted and read as text.</summary>
    private static string AllText(RecordingOutbound o)
    {
        var text = new StringBuilder();
        foreach (var frame in o.Frames)
        {
            if (!TkPacket.TryParse(frame, out var pkt, out _)) continue;
            text.Append(Encoding.Latin1.GetString(TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey))).Append('\n');
        }
        return text.ToString();
    }
}
