using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The four gameplay scalars that retired out of the environment into <c>game-data/ServerTuning.csv</c>
/// (<c>HitCrit</c>, <c>HealCrit</c>, <c>DeathDespawnMs</c>, <c>SpellBookCap</c>) keep the EXACT rule their
/// environment reads had, so a deployment that never set the old variable sees no change and one that copies
/// its old value into the CSV gets the same answer.
///
/// <para>The rules that matter are the refusals, because they are the ones a saturating rewrite would have
/// changed silently: the old reads were <c>byte.TryParse</c> (a crit outside 0..255 is not a byte, so the
/// default stands) and <c>c &gt; 0 ? c : 52</c> (a non-positive cap is refused, not floored at 1).</para>
///
/// <para>Each fact drives the real table: a throwaway <c>ServerTuning.csv</c> with the row under test, a real
/// <c>Content.Reload()</c>, then the accessor. Serialized on <see cref="TestProcessState.Gate"/> with every
/// other test that drives the same process environment variable, and the real content is loaded back in a
/// finally.</para>
/// </summary>
public class GameplayTuningRuleTests
{
    private static void WithTuningRow(string row, Action body)
    {
        lock (TestProcessState.Gate)
        {
            string dir = Path.Combine(Path.GetTempPath(), "project1998-tuning-rule-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string tuning = Path.Combine(dir, "ServerTuning.csv");
            File.WriteAllText(tuning, "key,value\n" + row + "\n");

            string? previous = Environment.GetEnvironmentVariable("P1998_SERVER_TUNING");
            try
            {
                Environment.SetEnvironmentVariable("P1998_SERVER_TUNING", tuning);
                Content.Reload();
                body();
            }
            finally
            {
                Environment.SetEnvironmentVariable("P1998_SERVER_TUNING", previous);
                TestProcessState.LoadContent();
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a fixture */ }
            }
        }
    }

    /// <summary>A cap of 0 (or below) is refused and the 52-slot default stands — the old
    /// <c>int.TryParse(env, out c) &amp;&amp; c &gt; 0 ? c : 52</c> rule. Flooring at 1 instead would hand
    /// that deployment a one-slot spellbook: every character's book emptied down to a single spell on the
    /// next rebuild, and no teach accepted past it.
    /// <para>Falsification: make the accessor <c>Math.Max(1, …)</c> and this reads 1.</para></summary>
    [Theory]
    [InlineData("SpellBookCap,0")]
    [InlineData("SpellBookCap,-5")]
    public void A_non_positive_spellbook_cap_keeps_the_fifty_two_slot_default(string row) =>
        WithTuningRow(row, () => Assert.Equal(52, Content.SpellBookCap));

    /// <summary>...and a positive one is honoured, so the refusal above is a refusal and not a constant.</summary>
    [Fact]
    public void A_positive_spellbook_cap_is_honoured() =>
        WithTuningRow("SpellBookCap,20", () => Assert.Equal(20, Content.SpellBookCap));

    /// <summary>A hit-crit byte outside 0..255 is refused and 33 (RTK's normal hit) stands — the old
    /// <c>byte.TryParse</c> rule. Saturating instead would turn a mistyped 300 into 255, which is the
    /// CRITICAL overlay: every ordinary hit in the world drawn as a crit.
    /// <para>Falsification: make the accessor <c>Math.Clamp(…, 0, 255)</c> and 300 reads 255.</para></summary>
    [Theory]
    [InlineData("HitCrit,300")]
    [InlineData("HitCrit,-1")]
    public void An_out_of_range_hit_crit_keeps_the_default(string row) =>
        WithTuningRow(row, () => Assert.Equal((byte)0x21, Content.HitCritByte));

    /// <summary>The same rule on the heal byte, whose default is 0 — the value a saturating clamp would also
    /// have produced for a negative row, but not for 300.</summary>
    [Theory]
    [InlineData("HealCrit,300")]
    [InlineData("HealCrit,-1")]
    public void An_out_of_range_heal_crit_keeps_the_default(string row) =>
        WithTuningRow(row, () => Assert.Equal((byte)0, Content.HealCritByte));

    /// <summary>An in-range crit row is honoured in both accessors.</summary>
    [Fact]
    public void An_in_range_crit_row_is_honoured() =>
        WithTuningRow("HitCrit,255\nHealCrit,7", () =>
        {
            Assert.Equal((byte)255, Content.HitCritByte);
            Assert.Equal((byte)7, Content.HealCritByte);
        });

    /// <summary>The corpse hold DOES clamp rather than refuse, because its environment read did:
    /// <c>int.TryParse(env, out v) ? Math.Clamp(v, 0, 5000) : 600</c>. Pinned here so the difference between
    /// the two rules stays deliberate.</summary>
    [Theory]
    [InlineData("DeathDespawnMs,9000", 5000)]
    [InlineData("DeathDespawnMs,-1", 0)]
    [InlineData("DeathDespawnMs,1200", 1200)]
    public void An_out_of_range_corpse_hold_clamps_as_it_always_did(string row, int expected) =>
        WithTuningRow(row, () => Assert.Equal(expected, Content.DeathDespawnMs));
}
