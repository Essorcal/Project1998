using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The '@' command surface: what a command SAYS, and which client widget it says it on.
///
/// <para>These are CHANNEL tests as much as text tests. A GM command answers on one of three unrelated
/// client widgets — the 0x0D speech bubble, the 0x0A status pane, the 0x02 login box — and picking the wrong
/// one is invisible from the server: the packet builds, the log says it went out, and the client draws it
/// somewhere else (or, for the login box, drops all but one line). Nothing but a decoded transcript catches
/// that, so <see cref="Transcript"/> reduces every frame a command produced to "widget|text" in order and
/// the assertions pin the whole list.</para>
///
/// <para>Cases are picked for CLASS coverage rather than breadth: a refusal, a one-line confirmation, a
/// multi-line readout, and the two commands whose channel is itself the point (@text auditions a raw 0x0A
/// type; @ride repeats the native mount line verbatim). The rule they pin lives above <c>Reply</c> in
/// Server/Commands.cs; these are what enforces it.</para>
/// </summary>
[Collection("world")]
public sealed class CommandTableTests
{
    private readonly SessionFixture _fx;

    public CommandTableTests(SessionFixture fx)
    {
        _fx = fx;
        GmRoster.Ensure();
    }

    /// <summary>Every command below is GM- or tester-tier, and <see cref="StaffAccounts"/> is EMPTY by
    /// default (a fresh deployment has no staff at all), so without this the table would answer every case
    /// with "Unknown command" and the suite would pin nothing. Writes the roster into the redirected test
    /// state directory — #23 points P1998_STATE at a temp dir before anything loads, so this cannot reach a
    /// real deployment's roster. Once, under the process-wide gate: <c>StaffAccounts.Load</c> replaces the
    /// rosters wholesale and content loads race it.</summary>
    private static class GmRoster
    {
        private const string Name = "cmdgm";
        private static bool _done;

        public static void Ensure()
        {
            lock (TestProcessState.Gate)
            {
                if (_done) return;
                Directory.CreateDirectory(TestProcessState.StateDirectory);
                File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), Name + "\n");
                StaffAccounts.Load();
                _done = true;
            }
        }

        /// <summary>A brand-new GM session with the world-entry chatter already dropped. Fresh per case: the
        /// commands here mutate the character they run on, and a shared one would make the assertions
        /// order-dependent.</summary>
        public static (Session session, RecordingOutbound outbound) Session(SessionFixture fx)
        {
            var (session, outbound) = fx.Player(Name);
            outbound.Clear();
            return (session, outbound);
        }
    }

    /// <summary>Every text frame a command produced, in order, as "widget|text".
    ///
    /// <para>Decoded from the recorded bytes rather than from any server-side intent, because the widget is
    /// chosen by the wire and nothing else: <c>0x0D</c> is self-speech (a bubble over the player plus a chat
    /// line, <c>chatType · id(u32BE) · len(u8) · text</c>), <c>0x0A</c> is the status/mini-text channel
    /// (<c>type(u8) · len(u16BE) · text</c>, and the <c>type</c> picks the pane, so it is part of the name
    /// here), and <c>0x02</c> is the login message box (<c>0x0F · len(u8) · text · 00</c>). Non-text frames —
    /// stats pushes, effects, item redraws — are skipped: they are the command DOING its work, not
    /// reporting it.</para></summary>
    internal static List<string> Transcript(RecordingOutbound outbound)
    {
        var lines = new List<string>();
        foreach (var frame in outbound.Frames)
        {
            if (!TkPacket.TryParse(frame, out var pkt, out _)) continue;
            var b = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
            switch (pkt.Opcode)
            {
                case 0x0D when b.Length >= 6 + b[5]:
                    lines.Add("bubble|" + Encoding.ASCII.GetString(b, 6, b[5]));
                    break;
                case 0x0A when b.Length >= 3:
                    lines.Add($"pane{b[0]}|" + Encoding.ASCII.GetString(b, 3, (b[1] << 8) | b[2]));
                    break;
                case 0x02 when b.Length >= 2 + b[1]:
                    lines.Add("modal|" + Encoding.ASCII.GetString(b, 2, b[1]));
                    break;
            }
        }
        return lines;
    }

    // ---- what each command says, and where -----------------------------------------------------------
    //
    // The text is pinned VERBATIM, transliteration included: SendLog and SendMiniText both push their line
    // through AsciiBytes, which folds em-dashes to '-' and curly quotes to straight ones, so "granted —"
    // reaches the client as "granted -", and a '·' with no ASCII counterpart at all becomes '?'. A test that
    // expected the source string would pass for the wrong reason.
    //
    // Every "pane3" below that used to read "bubble" or "modal" is the channel rule in Commands.cs being
    // applied; the text either side of the '|' is untouched. The one place the TEXT changed is @dog, and it
    // changed by not being cut: at 302 characters it overran the 0x0D speech path's 250-char clamp and used
    // to arrive as "...the NPC subpa". The status pane holds 0x8000.

    [Theory]
    // --- refusals: the command's action did not happen. Loud, on the speech bubble. ----------------------
    [InlineData("@coins x", new[] { "bubble|usage: @coins <n>   (n may be negative to remove; default +10000)" })]
    [InlineData("@nope", new[] { "bubble|Unknown command '@nope'. Try @help." })]
    [InlineData("@approach", new[] { "bubble|usage: @approach <username>" })]
    [InlineData("@dura", new[] { "bubble|usage: @dura <name or id> <n>" })]
    [InlineData("@item", new[] { "bubble|usage: @item <name or id> [amount]   (browse with  @items <name>)" })]
    [InlineData("@fistsnd", new[] { "bubble|usage: @fistsnd <id>   (current: 9; 0 = silent)" })]
    [InlineData("@exp", new[] { "bubble|exp is 0. usage: @exp <n> [kill]   (kill = eligible for the totem-time bonus)" })]
    // Was the 0x02 login box, which is the pre-world channel and shows one line only.
    [InlineData("@sweep", new[] { "bubble|@sweep is disabled (crashes the client on resource-loading opcodes). Use @s <hexop>." })]
    // A two-line refusal stays whole on one widget rather than splitting across two.
    [InlineData("@stats", new[]
    {
        "bubble|usage: @stats <vita> <mana> [<all> | <might> <grace> <will>]   e.g. @stats 50000 50000 130",
        "bubble|  now: vita 50, mana 34, might 3, grace 3, will 3",
    })]

    // --- confirmations: one line saying what changed. The status pane. ----------------------------------
    [InlineData("@coins 500", new[] { "pane3|Coins: 500 (changed by +500)." })]
    [InlineData("@quest tiger 3", new[] { "pane3|tiger = 3." })]
    [InlineData("@karma angel", new[] { "pane3|karma set to 30 (Angel)." })]
    [InlineData("@dispel", new[] { "pane3|All buffs and debuffs removed." })]
    [InlineData("@fistsnd 5", new[] { "pane3|fist swing sfx = 5" })]
    // These three were on the 0x02 login box: @lvl and friends genuinely popped a modal over the game.
    [InlineData("@nation 3", new[] { "pane3|nation set to 3 (Nagnang)." })]
    [InlineData("@hp 500", new[] { "pane3|max HP set to 500, HP refilled." })]
    [InlineData("@might 50", new[] { "pane3|might set to 50" })]
    // Already on the status pane before the rule, and still there: the toggles print the same line the
    // native 0x1b Options toggles do.
    [InlineData("@clip", new[] { "pane3|No-clip          :ON" })]
    [InlineData("@peace", new[] { "pane3|Peace            :ON" })]

    // --- readouts: a report you asked for, one line or many. The status pane. ---------------------------
    [InlineData("@quest", new[] { "pane3|No quest keys set. (@quest <key> <stage> to set one; see docs/common/Quest-Registry.md.)" })]
    [InlineData("@dog", new[] { "pane3|Dog Linguist granted - say \"secret\" to your class's Dog to be taught, or @lvl 1 to have the rebuild hand over the Dog spells you qualify for (70 and 99). NOTE: Peasant is a PC subpath and will be refused - only the four base classes and the NPC subpaths (Chung ryong ? Baekho ? Ju jak ? Hyun moo) may learn Dog spells." })]
    [InlineData("@pkt", new[]
    {
        "pane3|usage: @pkt <hexop> [xx | #u16 | %u32 | :text | $text]",
        "pane3|  @pkt add <tokens>   append to the pending packet (the chat box is short)",
        "pane3|  @pkt send <hexop>   send what's pending, then clear it",
        "pane3|  @pkt show | clear   inspect or drop the pending bytes",
        "pane3|  @pkt file <name>    send game-data/packets/<name>.txt (';' starts a comment)",
    })]

    // --- the two whose channel IS the behaviour ---------------------------------------------------------
    // @text exists to audition a raw 0x0A type, so its reply must stay on the type it was asked for; @ride
    // repeats the native mount line, which the real 'r' key puts in the status pane. Neither goes through
    // Reply, and neither may be "tidied" into it.
    [InlineData("@text 5 hello", new[] { "pane5|hello" })]
    [InlineData("@ride", new[] { "pane3|The powerful steed takes you where you want to go." })]
    public void CommandSaysAndChannel(string command, string[] expected)
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Assert.True(session.TryRunCommand(command), $"'{command}' was not recognized as a command at all");
        Assert.Equal(expected, Transcript(outbound));
    }
}
