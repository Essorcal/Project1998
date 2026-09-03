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

    /// <summary>Run a command the way the read loop runs one: as a framed 0x0E chat packet handed to
    /// <see cref="Session.Receive"/>.
    ///
    /// <para>This used to call the dispatcher directly, which was a shortcut with teeth. #29 made a PACKET
    /// the atomic unit of work against a session and put the state monitor around the whole of
    /// <c>Session.Handle</c>; reaching past it to the dispatcher meant every command here ran outside the
    /// monitor, and its Debug.Fail duly caught @dispel and @coins touching <c>_buffs</c> and <c>_char</c>
    /// unguarded. The fix is where the test ENTERS, not what it expects — every transcript below is
    /// unchanged.</para>
    ///
    /// <para>Entering here also means the pins now cover the real path a player's message takes: the 0x0E
    /// body parse, the prefix split, the tier gate and the table lookup, not just the handler.</para>
    ///
    /// <para>Body layout is <c>chatType(u8) · len(u8) · text</c> (Session.Chat.HandleChat). Type 0 is
    /// ordinary speech — the same thing the client sends when someone types into the chat box.</para>
    /// </summary>
    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[0] = 0;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(0x0E, body));
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
    // Status-pane lines are pinned POST-WRAP, one entry per 0x0A actually sent, because that is what the
    // client draws: the pane is ~30 characters wide and wraps anything longer at a CHARACTER boundary, so a
    // line that leaves here too long arrives split mid-word. Bubble lines are not wrapped — that is a
    // different widget with its own 250-character clamp — which is why the @stats and @sweep refusals below
    // are still single long entries.
    //
    // @dog is eleven pane lines. That is a 302-character message and the pane is what it is; the point of
    // pinning it is that every break falls between words.

    [Theory]
    // --- refusals: the command's action did not happen. Loud, on the speech bubble. ----------------------
    [InlineData("@coins x", new[] { "bubble|usage: @coins/@gold [n]" })]
    [InlineData("@nope", new[] { "bubble|Unknown command '@nope'. Try @help." })]
    [InlineData("@approach", new[] { "bubble|usage: @approach <username>" })]
    [InlineData("@dura", new[] { "bubble|usage: @dura <name|id> <n>" })]
    [InlineData("@item", new[] { "bubble|usage: @item <name|id> [amount]" })]
    [InlineData("@fistsnd", new[] { "bubble|usage: @fistsnd <id>   (current: 9; 0 = silent)" })]
    [InlineData("@exp", new[] { "bubble|exp is 0. usage: @exp <n> [kill]" })]
    // Was the 0x02 login box, which is the pre-world channel and shows one line only.
    [InlineData("@sweep", new[] { "bubble|@sweep is disabled (crashes the client on resource-loading opcodes). Use @s <hexop>." })]
    // A two-line refusal stays whole on one widget rather than splitting across two.
    [InlineData("@stats", new[]
    {
        "bubble|usage: @stats <vita> <mana> <all> | <vita> <mana> <might> <grace> <will>",
        "bubble|  now: vita 50, mana 34, might 3, grace 3, will 3",
    })]

    // --- confirmations: one line saying what changed. The status pane. ----------------------------------
    [InlineData("@coins 500", new[]
    {
        "pane3|------------------------------",
        "pane3|Coins: 500 (changed by +500).",
    })]
    [InlineData("@quest tiger 3", new[]
    {
        "pane3|------------------------------",
        "pane3|tiger = 3.",
    })]
    [InlineData("@karma angel", new[]
    {
        "pane3|------------------------------",
        "pane3|karma set to 30 (Angel).",
    })]
    [InlineData("@dispel", new[]
    {
        "pane3|------------------------------",
        "pane3|All buffs and debuffs removed.",
    })]
    [InlineData("@fistsnd 5", new[]
    {
        "pane3|------------------------------",
        "pane3|fist swing sfx = 5",
    })]
    // These three were on the 0x02 login box: @lvl and friends genuinely popped a modal over the game.
    [InlineData("@nation 3", new[]
    {
        "pane3|------------------------------",
        "pane3|nation set to 3 (Nagnang).",
    })]
    [InlineData("@might 50", new[]
    {
        "pane3|------------------------------",
        "pane3|might set to 50",
    })]
    // A confirmation is wrapped like anything else once it outgrows the pane.
    [InlineData("@hp 500", new[]
    {
        "pane3|------------------------------",
        "pane3|max HP set to 500, HP",
        "pane3|refilled.",
    })]
    // Already on the status pane before the rule, and untouched by the wrapper because they already fit:
    // these repeat RTK's verbatim column-17 toggle line, padding and all.
    [InlineData("@clip", new[]
    {
        "pane3|------------------------------",
        "pane3|No-clip          :ON",
    })]
    [InlineData("@peace", new[]
    {
        "pane3|------------------------------",
        "pane3|Peace            :ON",
    })]

    // --- readouts: a report you asked for, one line or many. The status pane. ---------------------------
    [InlineData("@quest", new[]
    {
        "pane3|------------------------------",
        "pane3|No quest keys set. (@quest",
        "pane3|<key> <stage> to set one; see",
        "pane3|docs/common/Quest-Registry.md.)",
    })]
    [InlineData("@dog", new[]
    {
        "pane3|------------------------------",
        "pane3|Dog Linguist granted - say",
        "pane3|\"secret\" to your class's Dog",
        "pane3|to be taught, or @lvl 1 to",
        "pane3|have the rebuild hand over the",
        "pane3|Dog spells you qualify for (70",
        "pane3|and 99). NOTE: Peasant is a PC",
        "pane3|subpath and will be refused -",
        "pane3|only the four base classes and",
        "pane3|the NPC subpaths (Chung ryong",
        "pane3|? Baekho ? Ju jak ? Hyun moo)",
        "pane3|may learn Dog spells.",
    })]
    // A list block: header, the entries two logical lines each, closing rule.
    [InlineData("@pkt", new[]
    {
        "pane3|------------------------------",
        "pane3|usage: @pkt <hexop> [tokens] |",
        "pane3|add | send | show | clear |",
        "pane3|file <name>",
        "pane3|= @pkt sub-forms =",
        "pane3|@pkt add <tokens>",
        "pane3| append to the pending packet",
        "pane3|@pkt send <hexop>",
        "pane3| send it, then clear",
        "pane3|@pkt show | clear",
        "pane3| inspect or drop pending",
        "pane3|@pkt file <name>",
        "pane3| send packets/<name>.txt",
    })]

    // --- the two whose channel IS the behaviour ---------------------------------------------------------
    // @text exists to audition a raw 0x0A type, so its reply must stay on the type it was asked for and is
    // sent unwrapped, by number; @ride repeats the native mount line, which the real 'r' key puts in the
    // status pane. Neither goes through Reply, and neither may be "tidied" into it.
    [InlineData("@text 5 hello", new[]
    {
        "pane3|------------------------------",
        "pane5|hello",
    })]
    [InlineData("@ride", new[]
    {
        "pane3|------------------------------",
        "pane3|The powerful steed takes you",
        "pane3|where you want to go.",
    })]
    public void CommandSaysAndChannel(string command, string[] expected)
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, command);

        Assert.Equal(expected, Transcript(outbound));
    }

    // ---- the split: message -> command name + ARGUMENT TAIL --------------------------------------------
    //
    // Every handler's contract starts here, and it has been got wrong before: when the ~70 StartsWith lines
    // became a table, ParseInts kept skipping token 0 to step over a command name that was no longer in the
    // string, which silently ate the first argument of every numeric command ("@stats 50000 50000 130"
    // parsed as (50000, 130)). Nothing in the type system says whether a handler is handed the whole message
    // or just the tail, so it is pinned instead.

    [Theory]
    [InlineData("@warp", "warp", "")]
    [InlineData("@warp Kugnae", "warp", "Kugnae")]
    // The tail is handed over WHOLE: an item name with spaces and a trailing amount are one string, and
    // splitting them is the handler's job (that is what NameThenTrailingInt is for).
    [InlineData("@item Fine Sword 3", "item", "Fine Sword 3")]
    // Trimmed at the ENDS only. Interior runs of spaces survive, so a handler that splits on whitespace must
    // ask for empty entries to be removed.
    [InlineData("@item   Fine  Sword  3  ", "item", "Fine  Sword  3")]
    // Quotes are not syntax here. They reach the handler as ordinary characters, which is why @legend takes
    // its free text as "everything from token 3 on" rather than as a quoted string.
    [InlineData("@legend dog 3 128 \"Dog linguist\"", "legend", "dog 3 128 \"Dog linguist\"")]
    // The name's case is preserved; it is the LOOKUP that is case-insensitive.
    [InlineData("@WARP kugnae", "WARP", "kugnae")]
    // Only a SPACE separates the name from the tail. A tab does not, so this is one long unknown name rather
    // than a warp — a known limit of the split, pinned so a change to it is deliberate.
    [InlineData("@warp\tKugnae", "warp\tKugnae", "")]
    // "@ warp" is a command with an EMPTY name, not the warp command. It reaches the table, misses, and gets
    // the same "unknown command" answer as a typo.
    [InlineData("@ warp", "", "warp")]
    public void SplitsNameFromArgumentTail(string text, string name, string args)
    {
        Assert.True(Session.SplitCommand(text, out var gotName, out var gotArgs));
        Assert.Equal(name, gotName);
        Assert.Equal(args, gotArgs);
    }

    [Theory]
    [InlineData("")]                 // nothing at all
    [InlineData("@")]                // the prefix alone is not a command
    [InlineData("hello there")]      // ordinary speech
    [InlineData(" @warp")]           // the prefix has to be the FIRST character
    [InlineData("a@warp")]
    public void NonCommandsAreOrdinarySpeech(string text)
    {
        Assert.False(Session.SplitCommand(text, out var name, out var args));
        Assert.Equal("", name);
        Assert.Equal("", args);
    }

    // ---- the table itself ------------------------------------------------------------------------------

    /// <summary>The real table, built eagerly. This is what Program calls at startup, and it is the only
    /// thing standing between a duplicate name and an exception thrown on a connection thread the first time
    /// a player types '@'.</summary>
    [Fact]
    public void RealTableBuildsAndHasNoDuplicateName()
    {
        Session.WarmCommandTable();

        var names = Session.CommandRows().SelectMany(r => r.Names).ToList();
        Assert.Null(Session.FirstDuplicateName(names));
        Assert.NotEmpty(names);
    }

    /// <summary>The duplicate check itself, which the real table can never exercise — it must not contain a
    /// duplicate, so the only way to find out whether the check works is to hand it one.</summary>
    [Theory]
    [InlineData(null, "warp", "go", "maps")]
    [InlineData("warp", "warp", "go", "warp")]
    [InlineData("go", "warp", "go", "go")]
    // Case-insensitively, because the LOOKUP is: declaring both "@Warp" and "@warp" is the same collision as
    // declaring "warp" twice, and the loser would simply never run.
    [InlineData("WARP", "warp", "go", "WARP")]
    // An ALIAS collides with a canonical name exactly as a canonical name would.
    [InlineData("gold", "coins", "gold", "gold")]
    public void FirstDuplicateNameFindsTheCollision(string? expected, params string[] names)
        => Assert.Equal(expected, Session.FirstDuplicateName(names));

    /// <summary>Every row's Args column is the ONE place a command's argument shape is written down: @help
    /// renders it, and (since the usage strings went away) so does every refusal. A typo in it — a missing
    /// bracket, an empty placeholder — is invisible until a GM reads a mangled usage line, so check the whole
    /// column here instead.</summary>
    [Fact]
    public void EveryRowIsWellFormed()
    {
        foreach (var (names, args, help) in Session.CommandRows())
        {
            string row = "@" + names[0];
            Assert.NotEmpty(names);
            foreach (var n in names)
            {
                Assert.False(string.IsNullOrWhiteSpace(n), $"{row}: an empty name");
                Assert.DoesNotContain(" ", n);
                Assert.Equal(n.ToLowerInvariant(), n);   // the table declares names lower-case; lookup folds case
                Assert.DoesNotContain(Session.Prefix.ToString(), n);   // the prefix is added when rendering
            }
            Assert.False(string.IsNullOrWhiteSpace(help), $"{row}: no help text");
            Assert.Null(ArgSpecError(args));
        }
    }

    /// <summary>What is wrong with an Args column, or null if nothing is. The shape is
    /// <c>&lt;required&gt;</c>, <c>[optional]</c>, bare literal words, and <c>|</c> between alternatives —
    /// so brackets must balance and nest, and a placeholder must have something in it.</summary>
    private static string? ArgSpecError(string spec)
    {
        if (spec.Length == 0) return null;                                  // "takes no arguments" is a shape
        if (spec != spec.Trim()) return "leading or trailing whitespace";
        if (spec.Contains("  ")) return "a run of spaces";

        var open = new Stack<(char Bracket, int At)>();
        for (int i = 0; i < spec.Length; i++)
        {
            char c = spec[i];
            if (c is '<' or '[')
            {
                // '[' inside '<...>' would mean an optional part of a required placeholder, which the
                // renderer has no way to show. '<' inside '[...]' is fine and used: "[key] [0 | <icon> ...]".
                if (open.Count > 0 && open.Peek().Bracket == '<')
                    return $"'{c}' at {i} nested inside a <...> placeholder";
                open.Push((c, i));
            }
            else if (c is '>' or ']')
            {
                if (open.Count == 0) return $"'{c}' at {i} closes nothing";
                var (bracket, at) = open.Pop();
                if (bracket != (c == '>' ? '<' : '[')) return $"'{c}' at {i} closes a '{bracket}'";
                if (i == at + 1) return $"an empty '{bracket}{c}' at {at}";
            }
        }
        return open.Count == 0 ? null : $"{open.Count} unclosed bracket(s)";
    }

    [Theory]
    [InlineData("")]
    [InlineData("<n>")]
    [InlineData("<name|id> [amount]")]
    [InlineData("[key] [0 | <icon> <color> <text...>]")]
    [InlineData("clear|rain|snow | raw <n>")]
    public void WellFormedArgSpecsPass(string spec) => Assert.Null(ArgSpecError(spec));

    [Theory]
    [InlineData("<n")]                  // unclosed
    [InlineData("[amount")]
    [InlineData("n>")]                  // closes nothing
    [InlineData("<>")]                  // empty placeholder
    [InlineData("[]")]
    [InlineData("<name]")]              // crossed
    [InlineData("<a [b]>")]             // an optional part of a required placeholder
    [InlineData("<a>  <b>")]            // a run of spaces reaches the rendered usage line
    [InlineData(" <a>")]
    public void MalformedArgSpecsAreCaught(string spec) => Assert.NotNull(ArgSpecError(spec));
    // ---- the shared argument shapes --------------------------------------------------------------------
    //
    // "an item name, then a count" is the one shape three commands take, and each used to re-implement it.
    // It is not trivial: an item name is WORDS, so the count has to be told apart from the last word of the
    // name, and only a positive count counts. Driven through real commands against names that match no item,
    // so the reply echoes back exactly which half the splitter called the name.

    [Theory]
    // The trailing 4 is the count, so the name is the words before it.
    [InlineData("@item Nothing Here 4", "no item matches \"Nothing Here\" - try  @items Nothing Here")]
    // No trailing number: the whole tail is the name.
    [InlineData("@item Nothing Here", "no item matches \"Nothing Here\" - try  @items Nothing Here")]
    // A NEGATIVE trailing number is not a count. "@item Nothing -1" asks for an item called "Nothing -1"
    // rather than granting a negative pile of "Nothing".
    [InlineData("@item Nothing -1", "no item matches \"Nothing -1\" - try  @items Nothing -1")]
    // @take reads "all" in the count's position, and it comes off the name the same way a number does.
    [InlineData("@take Nothing Here all", "no item matches \"Nothing Here\" - try  @items Nothing Here")]
    // @dura shares the shape but REQUIRES the count...
    [InlineData("@dura Nothing Here 5", "no item matches \"Nothing Here\" - try  @items Nothing Here")]
    // ...so a name with no count, and a name with a negative one, are both refused with the table's shape.
    [InlineData("@dura Nothing", "usage: @dura <name|id> <n>")]
    [InlineData("@dura Nothing Here -3", "usage: @dura <name|id> <n>")]
    public void NameThenTrailingCountIsSplitTheSameWayEverywhere(string command, string expected)
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, command);
        Assert.Equal(new[] { "bubble|" + expected }, Transcript(outbound));
    }

    /// <summary>Every refusal's usage line is the table's own Args column, rendered — not a copy of it typed
    /// out beside the handler. Editing a row now moves @help and the refusal together, which is the whole
    /// point of there being one source; before this there were 22 hand-written copies to keep in step, and
    /// several had already drifted (@hit's row said "[dmg]" while the handler wanted a percent and a crit
    /// byte).</summary>
    [Theory]
    [InlineData("@hit", "usage: @hit <pct 0-100> [crit 0-255]")]
    [InlineData("@mtx", "usage: @mtx <type> [text...]")]
    [InlineData("@efx", "usage: @efx <id> [id2 ...]")]
    [InlineData("@coins x", "usage: @coins/@gold [n]")]        // aliases and all, exactly as @help lists them
    public void RefusalsRenderTheTableRow(string command, string expected)
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, command);
        Assert.Equal(new[] { "bubble|" + expected }, Transcript(outbound));

        // ...and it is the same string @help would print for that row, minus the "usage: " prefix.
        var row = Session.CommandRows().First(r => r.Names.Contains(command.Split(' ')[0][1..]));
        Assert.Contains(row.Args, expected);
    }
    // ---- fitting the pane ------------------------------------------------------------------------------
    //
    // Measured on a real 4.95 client: the status pane is about 30 characters of a PROPORTIONAL font, and it
    // wraps an over-long line at a CHARACTER boundary. @commands came out as "@commands @warp @go @rez @"
    // then "approach" — names split down the middle, none of them typeable. So every line has to leave the
    // server already short enough, or already broken somewhere that reads.

    /// <summary>A line that already fits is sent byte-for-byte as written — including deliberate padding.
    /// Re-flowing a line that did not need it would collapse RTK's verbatim column-17 toggle text for no
    /// gain.</summary>
    [Theory]
    [InlineData("No-clip          :ON")]
    [InlineData("tiger = 3.")]
    [InlineData("")]
    [InlineData("a line of exactly thirty chars")]   // exactly PaneWidth: the boundary case
    public void AFittingLineIsUntouched(string line)
        => Assert.Equal(new[] { line }, Session.WrapForPane(line));

    /// <summary>A token wider than the whole pane goes out ALONE and unbroken. There is nowhere to break it,
    /// and letting the next token ride along behind it would mean the client split BOTH.</summary>
    [Fact]
    public void ATokenWiderThanThePaneGoesOutAlone()
    {
        string token = new('x', 70);

        Assert.Equal(new[] { token }, Session.WrapForPane(token));
        Assert.Equal(new[] { "before", token, "after" }, Session.WrapForPane($"before {token} after"));
    }

    /// <summary>The property that matters: wrapping re-orders nothing, drops nothing, and never cuts a
    /// token in half — and no line comes out over the width unless it is one token that could not be
    /// broken.</summary>
    [Theory]
    // Three command names that do not fit on one line together. This is the @commands case.
    [InlineData("@commands @showwarps @totemsweep")]
    // Ordinary prose, the @help description case.
    [InlineData("pull an online player to your side (the inverse of @approach)")]
    // A line that is mostly one huge token.
    [InlineData("see docs/common/Quest-Registry.md for the full catalogue")]
    public void WrappingBreaksOnlyAtSpaces(string line)
    {
        var wrapped = Session.WrapForPane(line).ToList();

        Assert.True(wrapped.Count > 1, "this case is meant to need more than one pane line");
        Assert.Equal(line.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                     wrapped.SelectMany(w => w.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        foreach (var w in wrapped)
            Assert.True(w.Length <= Session.PaneWidth || !w.Trim().Contains(' '),
                        $"a {w.Length}-char line that could have been broken: \"{w}\"");
    }

    /// <summary>A listing announces itself ONCE. Its "= title =" header is the separator, so it cancels the
    /// dashed rule instead of following it, and there is no closing rule either — the next command's own
    /// separator does that job, and the pane has too few lines to spend two of them on punctuation.
    ///
    /// <para>Pinned on a listing whose contents are fixed: a brand new character carries exactly one legend,
    /// the seeded "Born in" mark, which has no key.</para></summary>
    [Fact]
    public void AListingIsItsOwnSeparator()
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, "@legend");

        Assert.Equal(new[]
        {
            "pane3|= legends (1) =",
            "pane3|(no key) - icon 0 col 128",
            "pane3| \"Born in Yuri 1, Summer\"",
        }, Transcript(outbound));
    }

    /// <summary>One rule per INVOCATION, not per Reply call and not per line: @stats prints two lines and
    /// gets one rule, and two commands in a row get one each, which is the whole point — the pane is a
    /// single scrolling column and consecutive commands used to run together.</summary>
    [Fact]
    public void EachCommandGetsOneRuleAndTheNextGetsItsOwn()
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, "@might 50");
        Run(session, "@karma angel");

        Assert.Equal(new[]
        {
            $"pane3|{Session.PaneRule}",
            "pane3|might set to 50",
            $"pane3|{Session.PaneRule}",
            "pane3|karma set to 30 (Angel).",
        }, Transcript(outbound));
    }

    /// <summary>A raw-probe command pays the separator BEFORE its raw line, so the rule still sits at the
    /// top of the invocation. @mtx used to send the probe line first — which does not spend the rule,
    /// because it bypasses Reply — and then the confirmation did, putting the dashes in the MIDDLE of one
    /// command's output.</summary>
    [Fact]
    public void ARawProbePaysTheRuleBeforeItsRawLine()
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, "@mtx 3 hello");

        Assert.Equal(new[]
        {
            $"pane3|{Session.PaneRule}",
            "pane3|hello",                              // the probe line, on the type asked for
            "pane3|sent minitext type=3: \"hello\"",     // the confirmation
        }, Transcript(outbound));
    }

    /// <summary>"@setting &lt;name&gt; on|off|1|0", and nothing else. A word it does not recognise refuses and
    /// changes NOTHING — it used to read anything that was not on/1 as OFF, so a typo silently turned the
    /// setting off and then reported the change as though it had been asked for.</summary>
    [Theory]
    [InlineData("@setting hear-sounds on", "Hear sounds      :ON")]
    [InlineData("@setting hear-sounds 1", "Hear sounds      :ON")]
    [InlineData("@setting hear-sounds off", "Hear sounds      :OFF")]
    [InlineData("@setting hear-sounds 0", "Hear sounds      :OFF")]
    public void SettingTakesOnOffOneZero(string command, string expected)
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, command);

        Assert.Equal(new[] { $"pane3|{Session.PaneRule}", "pane3|" + expected }, Transcript(outbound));
    }

    [Theory]
    [InlineData("@setting hear-sounds onn")]
    [InlineData("@setting hear-sounds yes")]
    [InlineData("@setting hear-sounds 2")]
    public void SettingRefusesAnythingElseAndChangesNothing(string command)
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, "@setting hear-sounds on");
        outbound.Clear();
        Run(session, command);

        // A bubble refusal and no pane line at all — nothing was changed, so nothing is reported changed.
        Assert.Equal(new[] { "bubble|usage: @setting [name] [on|off]" }, Transcript(outbound));

        outbound.Clear();
        Run(session, "@setting hear-sounds");        // bare toggles; ON -> OFF proves it was still ON
        Assert.Contains("pane3|Hear sounds      :OFF", Transcript(outbound));
    }

    /// <summary>A command that only REFUSES spends no pane line at all. The rule is owed by the first pane
    /// line, and a refusal answers on the bubble — heading it with a separator would cost a line of the pane
    /// to punctuate output that never arrived there.</summary>
    [Theory]
    [InlineData("@nope")]          // not a command at all
    [InlineData("@approach")]      // a real command, refused for want of an argument
    [InlineData("@dura Nothing")]  // refused for the shape of its arguments
    public void ARefusalPrintsNoRule(string command)
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, command);

        var transcript = Transcript(outbound);
        Assert.NotEmpty(transcript);
        Assert.All(transcript, line => Assert.StartsWith("bubble|", line));
        Assert.DoesNotContain(Session.PaneRule, string.Join("\n", transcript));
    }

    /// <summary>The four listings Caleb read off the real client, end to end: nothing they produce is wider
    /// than the pane unless it is a single unbreakable token. This is the acceptance check for the whole
    /// formatting pass — it walks the actual output rather than the wrapper in isolation, so a caller that
    /// bypasses Reply, or a table row whose Args column grew, fails here.</summary>
    [Theory]
    [InlineData("@commands")]
    [InlineData("@help")]
    [InlineData("@help warp")]
    [InlineData("@items apple")]
    [InlineData("@npc")]
    [InlineData("@setting")]
    [InlineData("@maps iron")]
    [InlineData("@mobs rabbit")]
    public void NothingAListingPrintsOverrunsThePane(string command)
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, command);

        var pane = Transcript(outbound)
            .Where(l => l.StartsWith("pane", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf('|') + 1)..])
            .ToList();
        Assert.NotEmpty(pane);
        foreach (var line in pane)
            Assert.True(line.Length <= Session.PaneWidth || !line.Trim().Contains(' '),
                        $"'{command}' printed a {line.Length}-char line that could have been broken: \"{line}\"");
    }

    // ---- Session.Navigation.cs, after the same pass ----------------------------------------------------
    //
    // These were pinned first against the old behaviour — everything on the 0x0D bubble, an unwrapped
    // "Warped to ..." line and a hand-written usage string — so the diff on this block is the record of the
    // move, exactly as it was for the first 24.

    [Theory]
    // The confirmation, now on the pane and wrapped to it. A map id, then a map id with coordinates.
    [InlineData("@warp 36", new[]
    {
        "pane3|------------------------------",
        "pane3|Warped to IronHeart's Home",
        "pane3|(map 36, 12x12) at (6,6).",
    })]
    [InlineData("@warp 36 4 9", new[]
    {
        "pane3|------------------------------",
        "pane3|Warped to IronHeart's Home",
        "pane3|(map 36, 12x12) at (4,9).",
    })]
    // The two refusals: no argument, and a name that matches nothing.
    [InlineData("@warp", new[] { "bubble|usage: @warp <map name|id> [x y]" })]
    [InlineData("@warp zzznosuchmap", new[] { "bubble|no map matches \"zzznosuchmap\" - try  @maps zzznosuchmap" })]
    // @go's "usage:" line is a CONFIRMATION in disguise, so it went to the pane with the success line
    // rather than to the bubble with the refusals: the command always moves you, and that line is what it
    // says when it could not use the coordinates you typed. Only the shape now comes from the table.
    [InlineData("@go 3 4", new[]
    {
        "pane3|------------------------------",
        "pane3|Moved to (3,4) on IronHeart's",
        "pane3|Home (map 36).",
    })]
    [InlineData("@go", new[]
    {
        "pane3|------------------------------",
        "pane3|usage: @go <x> <y> - 0..11 /",
        "pane3|0..11 on IronHeart's Home (map",
        "pane3|36); sent you to (0,0).",
    })]
    // The one refusal in the file that stayed a refusal.
    [InlineData("@summon", new[] { "bubble|usage: @summon <mob name|id>" })]
    public void NavigationCommandSaysAndChannel(string command, string[] expected)
    {
        var (session, outbound) = GmRoster.Session(_fx);

        Run(session, command);

        Assert.Equal(expected, Transcript(outbound));
    }
}
