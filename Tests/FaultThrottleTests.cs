using Server;
using Xunit;

namespace Tests;

/// <summary>
/// <see cref="FaultThrottle{TKey}"/> on plain numbers: which occurrence of a repeating fault gets its full
/// record (#109). The tick drives it with beat numbers (<c>MobAiTickTests</c> pins that end to end); these
/// pin the two boundaries the tick cannot reach cheaply — exactly at the quiet span, and the restack span
/// of a fault that never stops.
/// </summary>
public class FaultThrottleTests
{
    /// <summary>The first occurrence is written in full, every repeat inside the quiet span is counted,
    /// and a gap of exactly <c>QuietSpan</c> still counts as "not quiet": the stack comes back only after
    /// MORE than that.</summary>
    [Fact]
    public void AKeyLogsInFullFirstAndAgainOnlyAfterMoreThanTheQuietSpan()
    {
        var t = new FaultThrottle<string>(quietSpan: 30, restackSpan: 1_000_000);

        Assert.True(t.Admit("row", 100));
        Assert.False(t.Admit("row", 100));    // same beat
        Assert.False(t.Admit("row", 101));
        Assert.False(t.Admit("row", 131));    // gap of exactly 30: not quiet
        Assert.True(t.Admit("row", 162));     // gap of 31: quiet, so this return is written in full
        Assert.False(t.Admit("row", 163));
    }

    /// <summary>A fault that never goes quiet is written in full again once its last full record is
    /// <c>RestackSpan</c> old, and not before — so a log rotation cannot strand its count lines.</summary>
    [Fact]
    public void AKeyThatNeverStopsLogsInFullAgainEveryRestackSpan()
    {
        var t = new FaultThrottle<string>(quietSpan: 5, restackSpan: 50);

        Assert.True(t.Admit("row", 0));
        for (long now = 1; now < 50; now++) Assert.False(t.Admit("row", now));
        Assert.True(t.Admit("row", 50));
        for (long now = 51; now < 100; now++) Assert.False(t.Admit("row", now));
        Assert.True(t.Admit("row", 100));
    }

    /// <summary>Keys are independent: a second fault is written in full even while the first is being
    /// counted, and repeats of the second do not refresh the first.</summary>
    [Fact]
    public void KeysAreIndependent()
    {
        var t = new FaultThrottle<(string Row, Type Error)>(quietSpan: 3, restackSpan: 1_000);

        Assert.True(t.Admit(("row", typeof(NullReferenceException)), 0));
        Assert.True(t.Admit(("row", typeof(ArgumentNullException)), 0));
        Assert.False(t.Admit(("row", typeof(NullReferenceException)), 1));
        for (long now = 2; now < 10; now++) Assert.False(t.Admit(("row", typeof(ArgumentNullException)), now));
        Assert.True(t.Admit(("row", typeof(NullReferenceException)), 10));   // quiet since beat 1
        Assert.Equal(2, t.Count);
    }
}
