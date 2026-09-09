using Server;
using Xunit;

namespace Tests;

public class SelfTestTests
{
    [Fact]
    public void VerdictPassesWhenEverySectionPasses()
    {
        var verdict = Content.SelfTestVerdict(
            ("maps", true),
            ("spells", true),
            ("doors", true));

        Assert.Equal("SELFTEST: PASS", verdict.Message);
        Assert.Equal(0, verdict.ExitCode);
    }

    [Fact]
    public void VerdictNamesEveryFailedSectionAndRequestsFailureExit()
    {
        var verdict = Content.SelfTestVerdict(
            ("maps", true),
            ("spells", false),
            ("bgm", true),
            ("doors", false));

        Assert.Equal("SELFTEST: FAIL (spells, doors)", verdict.Message);
        Assert.Equal(1, verdict.ExitCode);
    }
}
