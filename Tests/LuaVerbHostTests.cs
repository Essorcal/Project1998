using MoonSharp.Interpreter;
using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The four <see cref="VerbResult"/> outcomes, driven through the real <see cref="LuaVerbHost"/>.
///
/// <para>The one that matters is <see cref="VerbResult.Errored"/> being distinguishable from
/// <see cref="VerbResult.Missing"/>. <c>Invoke</c> used to return <c>DynValue?</c> and <c>null</c> meant BOTH,
/// so <c>ItemScript</c> reported a raising verb as "not handled", <c>Session.ApplyItemEffect</c> read that as
/// "fall through to the item DB's Vita/Mana", returned true, and <c>HandleUseItem</c> consumed the item. One
/// typo in a hot-reloaded <c>item_verbs.lua</c> destroyed consumables with no sign to the player and one
/// stackless line in the log (upstream #25). Collapsing the two again would be silent and would look exactly
/// like a working server until someone lost an item, which is precisely the failure class Tests/ exists for.</para>
///
/// <para>This drives the host directly rather than through <see cref="ItemScript"/> or
/// <see cref="SpellScript"/>, and deliberately never calls <c>Content.Load()</c>: those wrappers own
/// process-wide hosts holding the REAL verb files, which <c>ContentSmokeTests</c> checks the shipped CSVs
/// against. Loading a scratch file into either would corrupt that check for whatever ran next.</para>
/// </summary>
public class LuaVerbHostTests
{
    /// <summary>A minimal stand-in for ItemContext/SpellContext: the facade type a verb acts through has to
    /// be MoonSharp-registered, but nothing here needs the real one.</summary>
    [MoonSharpUserData]
    public sealed class FakeContext
    {
        public int hp => 7;
    }

    static LuaVerbHostTests() => UserData.RegisterType<FakeContext>();

    private const string VerbFile = """
        verbs = {
          silent   = function(ctx, row) return end,                    -- no return at all
          affirms  = function(ctx, row) return true end,
          declines = function(ctx, row) return false end,
          raises   = function(ctx, row) error("deliberate") end,
          indexes  = function(ctx, row) local t = nil; return t.field end,
        }
        """;

    private static readonly IReadOnlyDictionary<string, string> Row =
        new Dictionary<string, string> { ["amount"] = "5", ["blank"] = "" };

    /// <summary>Load a throwaway verb file. The file is deleted before the assertions run, which also shows
    /// the host does not keep it open.</summary>
    private static LuaVerbHost Load(string lua)
    {
        var path = Path.Combine(Path.GetTempPath(), $"p1998-verbs-{Guid.NewGuid():N}.lua");
        File.WriteAllText(path, lua);
        try
        {
            var host = new LuaVerbHost("test_verbs.lua");
            Assert.True(host.Load(path), "the scratch verb file should have compiled");
            return host;
        }
        finally { File.Delete(path); }
    }

    /// <summary>A verb that raises reports Errored — NOT Missing. The item funnel refuses on one and consumes
    /// on the other, so this single distinction is the whole of the data-loss fix.</summary>
    [Fact]
    public void ARaisingVerbIsErroredAndNotMissing()
    {
        var host = Load(VerbFile);
        var ctx = new FakeContext();

        Assert.Equal(VerbResult.Errored, host.Invoke("raises", ctx, Row));    // explicit error()
        Assert.Equal(VerbResult.Errored, host.Invoke("indexes", ctx, Row));   // a nil index — the typo case
        Assert.Equal(VerbResult.Missing, host.Invoke("absent", ctx, Row));    // what Errored used to be confused with

        // One broken verb does not poison the host: the next call still runs.
        Assert.Equal(VerbResult.Ok, host.Invoke("silent", ctx, Row));
    }

    /// <summary>The other three outcomes, which the wrappers' fold depends on: only an explicit Lua
    /// <c>return false</c> declines, and a verb that returns nothing counts as success (so a verb needn't
    /// bother with <c>return true</c>).</summary>
    [Fact]
    public void OkDeclinedAndMissingAreDistinct()
    {
        var host = Load(VerbFile);
        var ctx = new FakeContext();

        Assert.Equal(VerbResult.Ok, host.Invoke("silent", ctx, Row));
        Assert.Equal(VerbResult.Ok, host.Invoke("affirms", ctx, Row));
        Assert.Equal(VerbResult.Declined, host.Invoke("declines", ctx, Row));
        Assert.Equal(VerbResult.Missing, host.Invoke("", ctx, Row));
    }

    /// <summary>A reload that does not compile is REJECTED and the previously-loaded verbs keep running —
    /// the atomic-swap guarantee in <see cref="LuaVerbHost.Load"/>. Checked here because the failure mode it
    /// prevents ("your edit didn't take" degrading into "the whole file reverted") is invisible at runtime.</summary>
    [Fact]
    public void ABrokenReloadKeepsThePreviousVerbs()
    {
        var host = Load(VerbFile);
        var bad = Path.Combine(Path.GetTempPath(), $"p1998-verbs-{Guid.NewGuid():N}.lua");
        File.WriteAllText(bad, "verbs = { silent = function() end,\n  oops = function() return 1 + end }");
        try
        {
            Assert.True(host.Load(bad), "a rejected reload leaves the host live on the old verbs");
            // 'declines' exists only in the GOOD file — still reachable, so the swap really was refused.
            Assert.Equal(VerbResult.Declined, host.Invoke("declines", new FakeContext(), Row));
        }
        finally { File.Delete(bad); }
    }
}
