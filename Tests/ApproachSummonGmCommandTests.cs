using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The staff commands that share the two spells' names are not the spells (#313), and giving the spells a verb
/// changed neither. <c>@approach &lt;username&gt;</c> (Server/Commands.cs) still takes staff to ANY online player:
/// no group, no kingdom, no mana, no map checks. <c>@summon &lt;mob&gt;</c> still conjures a CREATURE in front of
/// you, and a player's name is not a creature's.
///
/// <para>Entered as the framed 0x0E chat packet, the way <see cref="CommandTableTests"/> enters, so the tier gate
/// and the state monitor around <c>Session.Handle</c> are in the path. On the shared fixture World, with the
/// shared "cmdgm" roster: the GM is the session the command runs on, never one found by name, so the other
/// classes' leftover GMs cannot be mistaken for it. The maps are content-free and claimed by no other class.</para>
/// </summary>
[Collection("world")]
public sealed class ApproachSummonGmCommandTests
{
    private const ushort ApproachGmMap = 61325, ApproachTargetMap = 61326, SummonGmMap = 61327, SummonTargetMap = 61328;
    private const string GmName = "cmdgm";

    private readonly SessionFixture _fx;

    public ApproachSummonGmCommandTests(SessionFixture fx)
    {
        _fx = fx;
        lock (TestProcessState.Gate)
        {
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
        }
    }

    /// <summary>
    /// <b><c>@approach</c> still needs none of the spell's conditions.</b> The target is in no group and in the
    /// other kingdom, and the GM has no mana and no spell: the GM still lands beside them (north first, the
    /// command's own <c>AdjacentFreeElseStack</c>) with the command's own reply, and spends nothing.
    /// </summary>
    [Fact]
    public void AtApproachStillTakesStaffToAnyOnlinePlayer()
    {
        var (gm, gmOut, gmChar) = _fx.PlayerWith(GmName, c =>
        {
            c.MapXs = 20; c.MapYs = 20; c.Nation = 1; c.Mp = 0; c.Spells = new List<int>();
        }, ApproachGmMap, 5, 5);
        var (_, _, target) = _fx.PlayerWith("GmApproachTarget", c =>
        {
            c.MapXs = 20; c.MapYs = 20; c.Nation = 2;
        }, ApproachTargetMap, 10, 10);
        gmOut.Clear();

        Run(gm, "@approach GmApproachTarget");

        Assert.Equal((ApproachTargetMap, (ushort)10, (ushort)9, 0u), (gmChar.Map, gmChar.X, gmChar.Y, gmChar.Mp));
        Assert.Equal((ApproachTargetMap, (ushort)10, (ushort)10), (target.Map, target.X, target.Y));
        Assert.Contains("Approached GmApproachTarget", Said(gmOut));
    }

    /// <summary>
    /// <b><c>@summon</c> still conjures a creature, and only a creature.</b> Given an online player's name it
    /// moves nobody (no mob matches a player name, so it refuses); given a mob id it puts that mob on a tile beside
    /// the GM.
    /// </summary>
    [Fact]
    public void AtSummonStillConjuresACreatureAndNeverAPlayer()
    {
        var (gm, gmOut, gmChar) = _fx.PlayerWith(GmName, c => { c.MapXs = 20; c.MapYs = 20; }, SummonGmMap, 5, 5);
        var (_, _, player) = _fx.PlayerWith("GmSummonTarget", c => { c.MapXs = 20; c.MapYs = 20; },
                                            SummonTargetMap, 10, 10);
        gmOut.Clear();

        Run(gm, "@summon GmSummonTarget");

        Assert.Equal((SummonTargetMap, (ushort)10, (ushort)10), (player.Map, player.X, player.Y));
        Assert.Contains("no mob matches", Said(gmOut));

        Run(gm, "@summon 1");   // mobs.csv id 1: Rabbit

        var beside = new[] { (5, 4), (6, 5), (5, 6), (4, 5) }
            .Select(t => _fx.World.MobAt(SummonGmMap, t.Item1, t.Item2))
            .Where(m => m is not null)
            .ToList();
        Assert.Single(beside);
        Assert.Equal("Rabbit", beside[0]!.Name);
        Assert.Equal((SummonGmMap, (ushort)5, (ushort)5), (gmChar.Map, gmChar.X, gmChar.Y));
    }

    /// <summary>Everything the GM was told, wrapped lines rejoined: a reply longer than one pane line arrives as
    /// several, and a phrase can straddle the break.</summary>
    private static string Said(RecordingOutbound outbound) =>
        string.Join(" ", CommandTableTests.Transcript(outbound).Select(l => l[(l.IndexOf('|') + 1)..]));

    /// <summary>A command the way the read loop delivers one: a framed 0x0E chat packet, type 0.</summary>
    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[0] = 0;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(0x0E, body));
    }
}
