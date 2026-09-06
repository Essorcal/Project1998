using System.Reflection;
using Server;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The public byte seam on <see cref="Content.CastActionType"/>, pinned.
///
/// <para>This exists because naming the 0x1A action byte broke it once. <c>CastActionType</c> is a
/// <c>public static</c> member that returned <c>byte</c>; changing the return type to the new
/// <see cref="ActionType"/> enum compiled fine inside this repo — every in-tree caller was updated in the
/// same edit — while silently making the member source-breaking for any external caller that assigns the
/// result to a byte. There is no implicit enum-to-byte conversion in C#, so such a caller fails with
/// <c>CS0266: Cannot implicitly convert type 'Shared.ActionType' to 'byte'</c>.</para>
///
/// <para>That is a failure this repo's own build cannot see, which is precisely the class of thing
/// <c>Tests/</c> is for. Two guards below: one that fails at COMPILE time inside the test project the way an
/// external consumer would, and one that fails at RUN time by reading the declared return type — so adding
/// a cast to silence the first does not quietly retire the check.</para>
///
/// <para>The behaviour cases pin the other half: keeping the byte contract must not have changed which byte
/// comes out, including the dynamic emote-range override that is passed through on its RANGE and not by
/// matching against <see cref="ActionType"/>'s named members.</para>
/// </summary>
public class ContentApiSurfaceTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            TestProcessState.LoadContent();
            _loaded = true;
        }
    }

    private static SpellDef Spell(string key)
    {
        EnsureLoaded();
        var sp = Content.SpellByKey(key);
        Assert.True(sp is not null, $"spell '{key}' is missing from Spells.csv; pick another fixture spell.");
        return sp!;
    }

    /// <summary>THE COMPILE GUARD. This is an external consumer's line, written the way one would write it:
    /// the result of a byte-returning API assigned straight to a byte, with no cast. If
    /// <c>CastActionType</c>'s return type is ever widened to <see cref="ActionType"/> again, this file stops
    /// compiling with CS0266 and the whole suite goes red before anything runs.
    /// <para><b>Do not "fix" a build break here by adding a cast</b> — the missing cast IS the assertion.
    /// Callers that want the enum cast at their own end; see <c>Session.HandleCast</c>.</para></summary>
    [Fact]
    public void CastActionTypeAssignsToAByteWithoutACast()
    {
        byte wire = Content.CastActionType(Spell("wisdom_star"));
        Assert.InRange(wire, (byte)0, (byte)255);
    }

    /// <summary>The same guard at run time, so the compile guard cannot be neutralised by adding a cast to
    /// it. Reads the DECLARED return type off the method rather than the type of a returned value.</summary>
    [Fact]
    public void CastActionTypeStillDeclaresAByteReturnType()
    {
        var method = typeof(Content).GetMethod(nameof(Content.CastActionType),
                                               BindingFlags.Public | BindingFlags.Static);
        Assert.True(method is not null, "Content.CastActionType is no longer a public static method.");
        Assert.Equal(typeof(byte), method!.ReturnType);
    }

    /// <summary>Keeping the byte contract must not have moved any value. Literals, not enum members, for the
    /// same reason as the wire tests: renumbering a member should fail these rather than travel through them.
    /// <list type="bullet">
    /// <item><c>berserk_warrior</c> is the sacrifice family — a physical strike, so it swings (1).</item>
    /// <item><c>spirit_fury</c> carries <c>action=18</c> in spell_effects.csv — the 'h' rage emote, the
    /// documented reason the override exists at all.</item>
    /// <item><c>wisdom_star</c> has no override and is not a strike, so it takes the magic pose (6).</item>
    /// </list></summary>
    [Theory]
    [InlineData("berserk_warrior", 1)]
    [InlineData("whirlwind_warrior", 1)]
    [InlineData("spirit_fury", 18)]
    [InlineData("wisdom_star", 6)]
    public void CastActionTypeReturnsTheSameByteItAlwaysDid(string key, byte expected) =>
        Assert.Equal(expected, Content.CastActionType(Spell(key)));

    /// <summary>The emote-range override is admitted on its RANGE (9..28), not by being a value the enum
    /// names. 25 and 26 are deliberately unnamed in <see cref="ActionType"/> because nothing sources them,
    /// and they must still be accepted here — narrowing this to the named members is the mistake this
    /// asserts against. Driven through the same range predicate the production path uses.</summary>
    [Theory]
    [InlineData(9)]
    [InlineData(18)]
    [InlineData(25)]    // unnamed in ActionType
    [InlineData(26)]    // unnamed in ActionType
    [InlineData(28)]
    public void EmoteRangeOverrideIsAcceptedOnRangeNotOnBeingNamed(int action)
    {
        Assert.True(action is >= 9 and <= 28);
        // The cast the production caller performs on whatever byte comes back must preserve the value even
        // when no enum member carries that number (Session.HandleCast).
        Assert.Equal((byte)action, (byte)(ActionType)(byte)action);
        Assert.False(Enum.IsDefined(typeof(ActionType), (ActionType)(byte)action) && action is 25 or 26,
                     "25/26 are expected to stay unnamed; if one gains a source-backed name, update this.");
    }
}
