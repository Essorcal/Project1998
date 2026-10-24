using System.Linq;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Experience stops at 4,294,967,295 instead of wrapping (RTK <c>player.lua</c> <c>giveXPStacked</c>, lines
/// 4285-4290; Caleb, 2026-10-24, decision 9: "As RTK").
///
/// <para><c>Character.Exp</c> is a <c>uint</c>, and <c>AwardExp</c> added to it with nothing checking the sum,
/// in a build with no overflow checking. A level-99 already holds 2 to 3 billion (<c>game-data/LevelExp.csv</c>,
/// level 98 of each path), so banking for the rank trials (1.2 billion of exp each) could carry the total
/// past the top and leave a small number: the player's whole bank, gone, with nothing complaining. RTK caps
/// it. An award that reaches the cap (RTK's test is <c>&gt;=</c>, so landing exactly on it counts) sets exp to
/// the cap, sends "You cannot gain any more experience until you sell some." in place of "N experience!", and
/// returns before the level check.</para>
///
/// <para>Every exp grant funnels through <c>AwardExp</c> (kills solo and grouped, NPC and quest rewards, Lua's
/// <c>awardExp</c>, <c>@exp</c>), so these facts drive it directly. The <c>@exp</c> pair that wrapped through
/// the real command path is pinned in <see cref="GmExpCommandTests"/>. The guards at the bottom are green on
/// both sides: below the cap, an award and its level-up read exactly as they did, and a spend that is larger
/// than the balance is refused rather than wrapping the other way.</para>
/// </summary>
[Collection("world")]
public sealed class ExpCapTests
{
    private const string CapLine = "You cannot gain any more experience until you sell some.";

    private readonly SessionFixture _fx;

    public ExpCapTests(SessionFixture fx) => _fx = fx;

    /// <summary>The defect. A level-99 holding 4,000,000,000 is awarded 500,000,000: exp stops at the cap.
    /// <para>Red on master: 4.5 billion wrapped to 205,032,704.</para></summary>
    [Fact]
    public void AnAwardThatWouldPassTheCapLeavesExpAtTheCap()
    {
        var (session, _, character) = Warrior("ExpCapPast", level: 99, exp: 4_000_000_000);

        session.WithState(() => session.AwardExp(500_000_000));

        Assert.Equal(uint.MaxValue, character.Exp);
    }

    /// <summary>An award that lands exactly on 4,294,967,295 reads RTK's line, not the amount: RTK's test is
    /// <c>&gt;=</c>. Once at the cap the next award cannot add anything, so the line is true here too.
    /// <para>Red on master: the line was "100 experience!" (the exp itself already landed on the cap).</para></summary>
    [Fact]
    public void AnAwardThatReachesTheCapReadsTheCapLineInPlaceOfTheAmount()
    {
        var (session, outbound, character) = Warrior("ExpCapExact", level: 99, exp: uint.MaxValue - 100);

        session.WithState(() => session.AwardExp(100));

        Assert.Equal(new[] { CapLine }, MiniText(outbound));
        Assert.Equal(uint.MaxValue, character.Exp);
    }

    /// <summary>Already at the cap, any award adds nothing and says so again.
    /// <para>Red on master: 4,294,967,295 + 1 wrapped to 0, the whole bank.</para></summary>
    [Fact]
    public void AnAwardAtTheCapAddsNothing()
    {
        var (session, outbound, character) = Warrior("ExpCapFull", level: 99, exp: uint.MaxValue);

        session.WithState(() => session.AwardExp(1));

        Assert.Equal(uint.MaxValue, character.Exp);
        Assert.Equal(new[] { CapLine }, MiniText(outbound));
    }

    /// <summary>RTK returns from the cap branch before <c>checkLevel</c>, and so does this. Only a GM grant can
    /// carry a character below 99 to the cap, so the case is a level-1 Warrior given the whole range at once:
    /// it stays level 1, with no level-up line.
    /// <para>Red on master: the grant ran the level loop to 99.</para></summary>
    [Fact]
    public void AnAwardThatReachesTheCapRunsNoLevelCheck()
    {
        var (session, outbound, character) = Warrior("ExpCapNoLevel", level: 1, exp: 0);

        session.WithState(() => session.AwardExp(uint.MaxValue));

        Assert.Equal(((byte)1, uint.MaxValue), (character.Level, character.Exp));
        Assert.Equal(new[] { CapLine }, MiniText(outbound));
    }

    /// <summary>The totem bonus is clamped before the cap test. 4,100,000,000 at totem time is 4,305,000,000, a
    /// double past the top of a <c>uint</c>; it now counts as the cap. Only <c>@exp &lt;n&gt; kill</c> can send
    /// an award that big.
    /// <para>Red on master: the out-of-range conversion kept the low 32 bits, so the character got 10,032,704
    /// and read "10,032,704 experience!".</para></summary>
    [Fact]
    public void ATotemAwardPastTheTopOfAUintIsClampedToTheCap()
    {
        var (session, outbound, character) = Warrior("ExpCapTotem", level: 99, exp: 0);

        session.WithState(() => session.AwardExp(4_100_000_000, killExp: true, totemTime: true));

        Assert.Equal(uint.MaxValue, character.Exp);
        Assert.Equal(new[] { CapLine }, MiniText(outbound));
    }

    // ---- guards: green on master and here --------------------------------------------------------------

    /// <summary>Guard: an ordinary award and its level-up are unchanged. A level-1 Warrior given five more
    /// than its first threshold reaches level 2 and reads the amount, then the level-up line, as before.</summary>
    [Fact]
    public void AnOrdinaryAwardAndLevelUpAreUnchanged()
    {
        var (session, outbound, character) = Warrior("ExpCapOrdinary", level: 1, exp: 0);
        uint award = Content.ExpToNext(1, 1) + 5;

        session.WithState(() => session.AwardExp(award));

        Assert.Equal(((byte)2, award, Content.ExpToNext(1, 2) - award),
                     (character.Level, character.Exp, character.Tnl));
        Assert.Equal(new[] { $"{award:N0} experience!", "You have gained new insight." }, MiniText(outbound));
    }

    /// <summary>Guard: one short of the cap is still an ordinary award. The cap line starts exactly at
    /// 4,294,967,295 and not before.</summary>
    [Fact]
    public void AnAwardThatStopsOneShortOfTheCapIsAnOrdinaryAward()
    {
        var (session, outbound, character) = Warrior("ExpCapShort", level: 99, exp: uint.MaxValue - 101);

        session.WithState(() => session.AwardExp(100));

        Assert.Equal(uint.MaxValue - 1, character.Exp);
        Assert.Equal(new[] { "100 experience!" }, MiniText(outbound));
    }

    /// <summary>Guard: spending cannot wrap the other way. A spend larger than the balance is refused and
    /// leaves exp alone; one equal to it empties the bank to 0.</summary>
    [Fact]
    public void ASpendLargerThanTheBalanceIsRefused()
    {
        var (session, _, character) = Warrior("ExpCapSpend", level: 99, exp: 1_000);

        bool over = true, exact = false;
        uint afterOver = 0;
        session.WithState(() =>
        {
            over = session.SpendExp(1_001);
            afterOver = character.Exp;
            exact = session.SpendExp(1_000);
        });

        Assert.Equal((false, (uint)1_000, true, (uint)0), (over, afterOver, exact, character.Exp));
    }

    // ---- fixture ---------------------------------------------------------------------------------------

    /// <summary>A Warrior (path 1, so no Peasant wall) at <paramref name="level"/> holding
    /// <paramref name="exp"/>, totem-less so no window adds 5% the case did not ask for.</summary>
    private (Session session, RecordingOutbound outbound, Character character) Warrior(string name, byte level, uint exp)
    {
        var (session, outbound, character) = _fx.PlayerWith(name, c =>
        {
            c.ClassName = "Warrior";
            c.Level = level;
            c.Exp = exp;
            c.Totem = 4;
        });
        outbound.Clear();
        return (session, outbound, character);
    }

    /// <summary>The status-box lines (0x0A type 3) the award sent, in order.</summary>
    private static string[] MiniText(RecordingOutbound outbound) =>
        CommandTableTests.Transcript(outbound)
            .Where(l => l.StartsWith("pane3|", System.StringComparison.Ordinal))
            .Select(l => l["pane3|".Length..])
            .ToArray();
}
