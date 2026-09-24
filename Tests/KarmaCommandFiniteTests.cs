using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// <c>@karma &lt;value&gt;</c> — the value must be a finite double (pre-existing defect found by the PR #282
/// Fable review, "Pre-existing defects").
///
/// <para><c>double.TryParse(arg, NumberStyles.Float, InvariantCulture)</c> accepts "NaN", "Infinity" and
/// "-Infinity" as well as ordinary numbers. Storing one of those in <c>_char.Karma</c> then calling
/// <c>StoreSave()</c> used to throw right there — <c>System.Text.Json</c> refuses to serialize a non-finite
/// double — and the throw is swallowed by <c>Session.Handle</c>'s catch-all (packet dropped, session kept
/// alive), so nothing visible happened. But the assignment ran BEFORE the throw, so the in-memory character
/// was left with a non-finite Karma. Every later whole-character save — another GM command's own
/// <c>StoreSave()</c>, the autosave sweep, <c>FlushNow</c>, logout — serializes that same field and throws
/// too, so the row on disk stops moving until a GM sets karma again.</para>
///
/// <para>Entered as a framed 0x0E chat packet, the same entry point <see cref="GmExpCommandTests"/> and
/// <see cref="CommandTableTests"/> use: a GM command that skipped the dispatcher would also skip the tier
/// gate this fix relies on.</para>
/// </summary>
[Collection("world")]
public sealed class KarmaCommandFiniteTests
{
    private readonly SessionFixture _fx;

    public KarmaCommandFiniteTests(SessionFixture fx)
    {
        _fx = fx;
        EnsureGmRoster();
    }

    /// <summary>The value is refused, karma is left at whatever it was, and — the actual failure mode this
    /// guards against — a LATER unrelated GM command that unconditionally saves the whole character
    /// (<c>@totem</c>) still lands on disk. Before the fix, the NaN/Infinity assignment poisoned every
    /// following <c>StoreSave()</c> (thrown, swallowed by <c>Session.Handle</c>), so the totem change below
    /// would silently never reach the store.</summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void NonFiniteValueIsRefusedAndLaterSavesStillLand(string arg)
    {
        var (caller, outbound, character) = GmPlayer();
        character.Karma = 5;
        outbound.Clear();

        Run(caller, $"@karma {arg}");
        Assert.Equal(5, character.Karma);

        Run(caller, "@totem 2");   // an unrelated GM setter that unconditionally StoreSave()s the whole char

        var loaded = _fx.Store.Load(caller.CharName);
        Assert.Equal(CharacterLoadStatus.Ok, loaded.Status);
        Assert.Equal((byte)2, loaded.Character!.Totem);
        Assert.True(double.IsFinite(loaded.Character.Karma));
        Assert.Equal(5, loaded.Character.Karma);
    }

    [Fact]
    public void NormalValueIsStillAcceptedAndSaves()
    {
        var (caller, _, character) = GmPlayer();

        Run(caller, "@karma 42.5");

        Assert.Equal(42.5, character.Karma);

        var loaded = _fx.Store.Load(caller.CharName);
        Assert.Equal(CharacterLoadStatus.Ok, loaded.Status);
        Assert.Equal(42.5, loaded.Character!.Karma);
    }

    // ---- fixture -------------------------------------------------------------------------------------

    /// <summary>Same GM name <see cref="GmExpCommandTests"/> and <see cref="CommandTableTests"/> use: all
    /// three classes write the roster file, and writing identical content makes run order irrelevant.</summary>
    private const string GmName = "cmdgm";

    private static bool _rosterWritten;

    private static void EnsureGmRoster()
    {
        lock (TestProcessState.Gate)
        {
            if (_rosterWritten) return;
            System.IO.Directory.CreateDirectory(TestProcessState.StateDirectory);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
            _rosterWritten = true;
        }
    }

    private (Session session, RecordingOutbound outbound, Character character) GmPlayer()
    {
        var (session, outbound, character) = _fx.PlayerWith(GmName, c =>
        {
            c.Level = 1;
            c.Mark = 0;
            c.Totem = 4;
        });
        outbound.Clear();
        return (session, outbound, character);
    }

    /// <summary>A command the way the read loop delivers one: a framed 0x0E chat packet, type 0 (ordinary
    /// speech). Same entry point as <c>GmExpCommandTests.Run</c>.</summary>
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
