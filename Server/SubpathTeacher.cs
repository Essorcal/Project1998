namespace Server;

/// <summary>
/// The twelve PC subpaths' Circles: which map is each subpath's, and RTK's arrival box on it. One table, read by the
/// teacher who stands on each Circle (<see cref="SubpathTeacherAbility"/>, which subpath it teaches comes from the
/// map it stands on) and by a member's Qui hyang (<c>Session.ItemSubpathScroll</c>, where its "Subpath Circle"
/// lands).
///
/// <para>The maps and the boxes are RTK's <c>Player.returnToSubpath</c>
/// (<c>rtklua/Accepted/player.lua</c>, the one RTK route to a Circle, behind its Qui hyang's "Subpath Circle"
/// option): Barbarian Circle at x 10-14, y 13-16, every other Circle at x 8-14, y 9-12. RTK warps to any tile of the
/// box; here the arrival is a walkable tile of it (<see cref="ArrivalTiles"/>). Each map's <c>Maps.csv</c> name is
/// "&lt;Subpath&gt; Circle"; a test holds this table to that and to Paths.csv.</para>
/// </summary>
internal static class SubpathCircles
{
    /// <summary>One subpath's Circle: its path id (Paths.csv 10-21), its map, and RTK's arrival box (inclusive).</summary>
    internal readonly record struct Circle(int Subpath, int Map, int X1, int X2, int Y1, int Y2);

    internal static readonly IReadOnlyList<Circle> All = new Circle[]
    {
        new(10, 3510, 10, 14, 13, 16),   // Barbarian
        new(11, 3520,  8, 14,  9, 12),   // Merchant
        new(12, 3535,  8, 14,  9, 12),   // Diviner
        new(13, 3630,  8, 14,  9, 12),   // Druid
        new(14, 3610,  8, 14,  9, 12),   // Chongun
        new(15, 3615,  8, 14,  9, 12),   // Ranger
        new(16, 3620,  8, 14,  9, 12),   // Geomancer
        new(17, 3525,  8, 14,  9, 12),   // Monk
        new(18, 3605,  8, 14,  9, 12),   // Do
        new(19, 3515,  8, 14,  9, 12),   // Spy
        new(20, 3530,  8, 14,  9, 12),   // Shaman
        new(21, 3625,  8, 14,  9, 12),   // Muse
    };

    /// <summary>Circle map id -> the subpath it belongs to.</summary>
    internal static readonly IReadOnlyDictionary<int, int> SubpathByMap = All.ToDictionary(c => c.Map, c => c.Subpath);

    /// <summary>The subpath's Circle, or null for a path that has none (anything but the twelve).</summary>
    internal static Circle? For(int subpath)
    {
        foreach (var c in All) if (c.Subpath == subpath) return c;
        return null;
    }

    /// <summary>The tiles of <paramref name="c"/>'s arrival box a player may be set down on: open ground (pass
    /// flag 0, <see cref="MapData.Solid"/>), no object wall on any side (<see cref="ObjectFlags.Blocks"/>), and no
    /// NPC standing there (the Circle's teacher stands inside most boxes). Empty if the map file is missing.</summary>
    internal static List<(ushort X, ushort Y)> ArrivalTiles(Circle c)
    {
        var tiles = new List<(ushort, ushort)>();
        var md = MapData.For((ushort)c.Map);
        if (md is null) return tiles;
        var taken = Content.Npcs.Where(n => n.Map == c.Map && n.Enabled).Select(n => ((int)n.X, (int)n.Y)).ToHashSet();
        for (int y = c.Y1; y <= c.Y2; y++)
            for (int x = c.X1; x <= c.X2; x++)
            {
                if (md.Solid(x, y) || taken.Contains((x, y))) continue;
                ushort obj = md.Obj(x, y);
                if (ObjectFlags.Blocks(obj, 0) || ObjectFlags.Blocks(obj, 1) || ObjectFlags.Blocks(obj, 2) || ObjectFlags.Blocks(obj, 3))
                    continue;
                tiles.Add(((ushort)x, (ushort)y));
            }
        return tiles;
    }
}

/// <summary>
/// A PC subpath's teacher: one per subpath, named "&lt;Subpath&gt; Teacher", standing on the subpath's Circle
/// (Caleb, 2026-10-26: "Circle, via scroll"; the spot on each Circle is ours, no source places one). One ability for
/// all twelve; which subpath it teaches comes from the map it stands on (<see cref="SubpathCircles"/>), the way the
/// shrines pick their alignment (<see cref="AlignmentAbility"/>). Composed onto <c>SubpathTeacherNpc</c> on those
/// twelve maps only (<c>NpcAbilities.csv</c>, <c>subpath_teacher@map:...</c>); a test holds the two sets equal.
///
/// <para><b>Who.</b> A member of the teacher's own subpath gets Learn Secret and Forget Secret. Anyone else gets a
/// one-line refusal, in the shape of RTK's shrine refusal ("You are not Kwi-Sin, I can not help you.",
/// <c>NPCs/Common/alignment.lua</c>), and nothing else (Caleb, 2026-10-26, "Fine as stated").</para>
///
/// <para><b>What.</b> <see cref="Content.SubpathTeacherSpells"/>: the subpath's own rows that have a
/// <c>SpellLearnCosts.csv</c> row keyed to the subpath's own path id, at that row's level, minus what the member
/// knows, a marked row only at its rank. RTK has no such teacher (its <c>Player.learnSpecificSpells</c>, "for
/// mainly PC subpath spell system", is unused); the rows carry its spell scripts' <c>requirements()</c>. The talk
/// is our trainer's, line for line (<see cref="SecretLessons"/>), the fee priced on the subpath's own path.</para>
/// </summary>
public sealed class SubpathTeacherAbility : INpcAbility
{
    public static readonly SubpathTeacherAbility Instance = new();

    /// <summary>A member whose teacher has no priced row at all yet (no spell slice for the subpath has landed).</summary>
    internal const string NothingYet = "I have no secrets to teach you yet.";

    /// <summary>The one line for anyone who is not a member of this teacher's subpath.</summary>
    internal static string NotAMember(int subpath) => $"You are not a {Content.PathName(subpath)}, I can not help you.";

    public IEnumerable<(string, Func<NpcContext, Task>)> Entries(NpcContext ctx)
    {
        if (!SubpathCircles.SubpathByMap.TryGetValue(ctx.Def.Map, out int subpath)) yield break;
        if (ctx.ClassId != subpath)
        {
            // One entry, so a click goes straight to the line (the picker only appears for real choices).
            yield return ("Speak", c => c.Say(NotAMember(subpath)));
            yield break;
        }
        yield return ("Learn Secret", LearnSecret);
        yield return ("Forget Secret", ForgetSecretAbility.Forget);
    }

    // The menu showed Learn Secret to a member, but the list below is the Circle's, not the player's class's, and the
    // player can leave the subpath (@subpath leave) or be expelled while a box is up. So who is learning is read
    // again here and once the fee is accepted (SecretLessons.Teach's refuseNow): a member who left between buys
    // nothing, rather than a subpath spell their leaving already took from the book.
    private static async Task LearnSecret(NpcContext ctx)
    {
        if (!SubpathCircles.SubpathByMap.TryGetValue(ctx.Def.Map, out int subpath)) return;
        string? NotAMemberNow() => ctx.ClassId == subpath ? null : NotAMember(subpath);
        if (NotAMemberNow() is { } refusal) { await ctx.Say(refusal); return; }
        if (Content.SubpathTeacherSpells(subpath, int.MaxValue, int.MaxValue).Count == 0)
        {
            await ctx.Say(NothingYet);
            return;
        }

        var known = ctx.KnownSpells().Select(s => s.Id).ToHashSet();
        var learn = Content.SubpathTeacherSpells(subpath, ctx.Level, ctx.Mark).Where(s => !known.Contains(s.Id)).ToList();
        if (learn.Count == 0) { await ctx.Say(SecretLessons.NothingMoreForNow); return; }

        await SecretLessons.Teach(ctx, learn, subpath, NotAMemberNow);
    }
}
