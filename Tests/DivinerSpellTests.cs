using System.Reflection;
using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The Diviner spells slice (the project owner's word, 2026-10-26 and again 2026-10-30: P5 "each spell as its RTK script",
/// P1 and P3 "on sale once it works, at its script's price", decision sheet item 17 "Divine time kept, fail-open"):
/// Read karma, Hear footstep, Divine time and Vanish do what their RTK scripts do, and the Diviner teacher sells them.
/// Before, all four fell to the <c>generic</c> verb: 5 mana and nothing else (<c>Tests/Fixtures/spell-dispatch-routes.txt</c>
/// had them on <c>verb=generic</c>). Diviner's other seven rows have no RTK behaviour (five have no script, two an empty
/// cast) and stay as they are; the route fixture pins them.
///
/// <para>The scripts: <c>RTK-Server/rtklua/Accepted/Spells/Subpaths/Diviner/</c>, <c>read_karma.lua</c>,
/// <c>hear_footstep.lua</c>, <c>divine_time.lua</c> (with the inn keeper's <c>general_npc_funcs.time</c>) and
/// <c>vanish.lua</c>, each bound by its <c>game-data/SpellParams.csv</c> row: Read karma and Divine time to their own verbs,
/// Hear footstep to Do's <c>hear_footstep</c>, Vanish to Blend's <c>hide</c>.</para>
///
/// <para>Every cast is the real 0x0F frame through <c>Session.Receive</c>, by a fresh level-99 Diviner on a content-free
/// map (no Maps.csv row, so PvP is off, as in a town), and every answer the real 0x3A. Read karma's flow runs on a thread
/// of its own, with no synchronization context, so its awaits resume inline on the thread that delivers each reply, as on
/// the read loop (<c>MentorshipLinesTests</c>' shape). Player names here are letters only: Read karma's answer takes
/// nothing else.</para>
/// </summary>
[Collection("world")]
public sealed class DivinerSpellTests
{
    private const ushort KarmaMap = 62990, KarmaFarMap = 62991, HearMap = 62992, TimeMap = 62993, VanishMap = 62994,
                         EndMap = 62995;
    private const ushort CX = 16, CY = 16;
    private const uint MaxMana = 200_000, Mana = 200_000, Hp = 10_000;

    /// <summary>Slack on a wait read straight after the cast.</summary>
    private const long SlackMs = 5_000;

    /// <summary>Vanish's sneak multiplier, its script's <c>invis = 9</c>, which its SpellParams.csv row carries as
    /// <c>amount</c>.</summary>
    private const int VanishTimes = 9;

    private const string Question = "Who's karma shall you read?";
    private const string NotOnline = "Player is not online or unavailable.";
    private const string LettersOnly = "You can only enter letters and spaces.";

    private static readonly FieldInfo TicksField =
        typeof(Session).GetField("_spellTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublishTickDue =
        typeof(Session).GetMethod("PublishSpellTickDue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StealthUntilField =
        typeof(Session).GetField("_stealthUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StealthTimesField =
        typeof(Session).GetField("_stealthTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo CaptureMi =
        typeof(Session).GetMethod("CaptureTimedEffects", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo RestoreMi =
        typeof(Session).GetMethod("RestoreTimedEffects", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo NextActionTick =
        typeof(Session).GetField("_nextActionTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ActionCount =
        typeof(Session).GetField("_actionCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LeavingField =
        typeof(Session).GetField("_leaving", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Type SwingTargetType =
        typeof(Session).GetNestedType("SwingTarget", BindingFlags.NonPublic)!;
    private static readonly MethodInfo SwingRaw =
        typeof(Session).GetMethod("PlayerSwingRaw", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public DivinerSpellTests(SessionFixture fx) => _fx = fx;

    // ===== Read karma ===========================================================================================

    /// <summary>The cast asks a name: one input box, under the caster's own entity id, with no portrait, carrying the
    /// script's question; nothing spent and no wait yet (the script takes the mana and arms the wait once a name is
    /// found), and no "You cast Read karma.". Red on 912ba3c: generic's 5 mana, the line, and no box.</summary>
    [Fact]
    public void ReadKarmaAsksANameAndSpendsNothingYet()
    {
        var (caster, cOut, cc) = Diviner("read_karma_diviner", KarmaMap, CX, CY);
        try
        {
            OnOwnThread(() => caster.Receive(SpellCastSupport.CastFrame(0)));
            var boxes = Boxes(cOut);
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            long wait = SpellCastSupport.AetherLeft(caster, "read_karma_diviner");
            Assert.True(boxes.Count == 1 && boxes[0] is { Kind: 0x0404, Head: 0, Gfx: 0, Text: Question } box
                        && box.Id == caster.PlayerId && cc.Mp == Mana && wait == 0 && lines == "" && caster.DialogBusy,
                        $"boxes [{string.Join(" / ", boxes)}], spent {Mana - cc.Mp}, wait {wait} ms, lines [{lines}], " +
                        $"waiting {caster.DialogBusy}");
        }
        finally { _fx.World.LeaveMap(caster, KarmaMap); }
    }

    /// <summary>A name typed in any case finds that player on another map. The caster pays 250, the 88 s wait is armed,
    /// and the caster reads "&lt;name&gt; is faithful to the teachings of the Gods." in a box with the next arrow and the
    /// dog's portrait (look 18, palette 12); the player read has nothing yet. Once the caster answers it, the player read
    /// gets "You are faithful to the teachings of the Gods." in a box with no button, the same portrait, under their own
    /// id, and the conversation they had waiting is dropped (RTK's <c>target:freeAsync()</c>). Red on 912ba3c (no box);
    /// red with the answer's step not run (<c>Session.RunSpellStep</c>); red without the drop.</summary>
    [Fact]
    public void ReadKarmaShowsTheCasterTheBandThenTellsThePlayerRead()
    {
        var (target, tOut, _) = Other(KarmaFarMap, CX, CY, karma: 3.5);
        var (caster, cOut, cc) = Diviner("read_karma_diviner", KarmaMap, CX, CY + 2);
        try
        {
            string them = target.CharName;
            OnOwnThread(() =>
            {
                _ = target.DlgInputPush(0, 0, "a conversation the player read has waiting");
                caster.Receive(SpellCastSupport.CastFrame(0));
                cOut.Clear();
                tOut.Clear();
                caster.Receive(InputFrame(them.ToLowerInvariant()));
            });
            var first = Boxes(cOut);
            var before = Boxes(tOut);
            long wait = SpellCastSupport.AetherLeft(caster, "read_karma_diviner");
            uint spent = Mana - cc.Mp;
            bool waiting = target.DialogBusy;
            cOut.Clear();
            OnOwnThread(() => caster.Receive(NextFrame()));
            var after = Boxes(tOut);
            Assert.True(first.Count == 1 && first[0] is { Kind: 0x0001, Head: 1, Gfx: 0x8000 | 18, Color: 12, Prev: 0, Next: 1 } c1
                        && c1.Id == caster.PlayerId && c1.Text == $"{them} is faithful to the teachings of the Gods."
                        && before.Count == 0 && waiting && spent == 250 && wait > 88_000 - SlackMs && wait <= 88_000
                        && after.Count == 1 && after[0] is { Kind: 0x0001, Head: 1, Gfx: 0x8000 | 18, Color: 12, Prev: 0, Next: 0 } t1
                        && t1.Id == target.PlayerId && t1.Text == "You are faithful to the teachings of the Gods."
                        && !target.DialogBusy && Boxes(cOut).Count == 0,
                        $"caster's box [{string.Join(" / ", first)}], read's before [{string.Join(" / ", before)}] (waiting {waiting}), " +
                        $"spent {spent}, wait {wait} ms; read's after [{string.Join(" / ", after)}] (still waiting {target.DialogBusy}), " +
                        $"caster's after [{string.Join(" / ", Boxes(cOut))}]");
        }
        finally { _fx.World.LeaveMap(target, KarmaFarMap); _fx.World.LeaveMap(caster, KarmaMap); }
    }

    /// <summary>Each band of the script's ladder, at its edges: the creature (or the Angel's tear's icon) and palette both
    /// boxes carry, and the words each player reads. -3 is the snake and just above it the rat; 0 is the cat; each band
    /// starts at its own figure. Red on 912ba3c (no box); red with <c>ctx.targetKarma</c> reading 0 (every band the cat's);
    /// red, for the Angel's tear, with the item portrait dropped.</summary>
    [Theory]
    [InlineData(-10,   184, 25, "", " is among the cursed of the earth; they crawl alone in shame.", "You are among the cursed of the earth; you crawl alone in shame.")]
    [InlineData(-3,    184, 25, "", " is among the cursed of the earth; they crawl alone in shame.", "You are among the cursed of the earth; you crawl alone in shame.")]
    [InlineData(-2.9,  91,  10, "", " is out of favor with the Gods; they must purify their soul and cleanse their spirit.", "You are out of favor with the Gods; you must purify your soul and cleanse your spirit.")]
    [InlineData(-0.1,  91,  10, "", " is out of favor with the Gods; they must purify their soul and cleanse their spirit.", "You are out of favor with the Gods; you must purify your soul and cleanse your spirit.")]
    [InlineData(0,     182, 30, "", " has begun the journey to perfection.", "You have begun the journey to perfection.")]
    [InlineData(1,     25,  9,  "", "'s education progresses; they store knowledge for the ordeals to come.", "Your education progresses; you store knowledge for the ordeals to come.")]
    [InlineData(2.5,   21,  3,  "", "'s soul moves ever quicker toward its goal.", "Your soul moves ever quicker toward its goal.")]
    [InlineData(3,     18,  12, "", " is faithful to the teachings of the Gods.", "You are faithful to the teachings of the Gods.")]
    [InlineData(5.99,  126, 5,  "", " is clever and good-hearted; the Gods smile upon them.", "You are clever and good-hearted; the Gods smile upon you.")]
    [InlineData(6,     27,  9,  "", " is stalwart and honest, worthy of trust, strong of heart.", "You are stalwart and honest, worthy of trust, strong of heart.")]
    [InlineData(8,     24,  9,  "", " holds quiet power and and great wisdom, confidence and fortitude.", "You hold quiet power and and great wisdom, confidence and fortitude.")]
    [InlineData(11,    29,  9,  "", " has entered the nobility of souls -- fierce, wise and just.", "You have entered the nobility of souls -- fierce, wise and just.")]
    [InlineData(14,    108, 2,  "", " is at the pinnacle of mortal grace; their soul is noble, beautiful and fearsome to behold.", "You are at the pinnacle of mortal grace; your soul is noble, beautiful and fearsome to behold.")]
    [InlineData(19,    167, 24, "", " has reached the threshold of immortality; they put aside mortal possessions and passions.", "You have reached the threshold of immortality; you put aside mortal possessions and passions.")]
    [InlineData(24,    0,   0,  "angels_tear", "'s journey has been long; they have achieved inner peace and tranquility; they speak with the Gods.", "Your journey has been long; you have achieved inner peace and tranquility; you speak with the Gods.")]
    [InlineData(30,    49,  5,  "", " is one with the Gods, and their soul is worthy to dwell among them.", "You are one with the Gods, and your soul is worthy to dwell among them.")]
    [InlineData(1000,  49,  5,  "", " is one with the Gods, and their soul is worthy to dwell among them.", "You are one with the Gods, and your soul is worthy to dwell among them.")]
    public void EachBandHasItsScriptsCreatureAndWords(double karma, int look, int color, string item, string them, string you)
    {
        var (target, tOut, _) = Other(KarmaFarMap, CX + 4, CY, karma);
        var (caster, cOut, _) = Diviner("read_karma_diviner", KarmaMap, CX + 4, CY + 4);
        try
        {
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                cOut.Clear();
                caster.Receive(InputFrame(target.CharName));
                caster.Receive(NextFrame());
            });
            var mine = Boxes(cOut).SingleOrDefault();
            var theirs = Boxes(tOut).SingleOrDefault();
            var def = item.Length > 0 ? Content.ItemByKey(item)! : null;
            (int Head, int Gfx, int Color) want = def is null ? (1, 0x8000 | look, color) : (2, def.ClientIcon, 0);
            Assert.True(mine is not null && theirs is not null
                        && (mine.Head, mine.Gfx, mine.Color) == want && (theirs.Head, theirs.Gfx, theirs.Color) == want
                        && mine.Text == target.CharName + them && theirs.Text == you,
                        $"karma {karma}: caster's [{mine}], read's [{theirs}]; want portrait {want}");
        }
        finally { _fx.World.LeaveMap(target, KarmaFarMap); _fx.World.LeaveMap(caster, KarmaMap); }
    }

    /// <summary>Read karma's refusals, each free and with no wait armed: short of 250, "You do not have enough mana." and
    /// no box; a name with anything but letters, spaces and an apostrophe, "You can only enter letters and spaces." in a
    /// box with no button; a name nobody online has, "Player is not online or unavailable." in a box with no button; and
    /// inside the 88 s wait after a read, "Read karma isn't ready yet." and no box. Red on 912ba3c (generic: 5 mana, no
    /// box); red, for the typo, with the letters check taken out of the verb (a name with a digit is looked up and found
    /// by nobody).</summary>
    [Fact]
    public void ReadKarmaRefusalsAreFree()
    {
        var (poor, pOut, pc) = Diviner("read_karma_diviner", KarmaMap, CX + 8, CY, mana: 249);
        var (typo, yOut, yc) = Diviner("read_karma_diviner", KarmaMap, CX + 8, CY + 2);
        var (stranger, sOut, sc) = Diviner("read_karma_diviner", KarmaMap, CX + 8, CY + 4);
        var (again, aOut, ac) = Diviner("read_karma_diviner", KarmaMap, CX + 8, CY + 6);
        try
        {
            OnOwnThread(() =>
            {
                poor.Receive(SpellCastSupport.CastFrame(0));
                typo.Receive(SpellCastSupport.CastFrame(0));
                yOut.Clear();
                typo.Receive(InputFrame(stranger.CharName + "1"));
                stranger.Receive(SpellCastSupport.CastFrame(0));
                sOut.Clear();
                stranger.Receive(InputFrame("Nobodyhereatallq"));
                again.Receive(SpellCastSupport.CastFrame(0));
                again.Receive(InputFrame(again.CharName));
                again.Receive(NextFrame());
            });
            uint afterRead = ac.Mp;
            aOut.Clear();
            FreeSlot(again);
            OnOwnThread(() => again.Receive(SpellCastSupport.CastFrame(0)));
            string N(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));
            string B(RecordingOutbound o) => string.Join(" / ", Boxes(o));
            bool OneShut(RecordingOutbound o, string text) =>
                Boxes(o) is [{ Kind: 0x0001, Prev: 0, Next: 0 } b] && b.Text == text;
            Assert.True(N(pOut) == "You do not have enough mana." && Boxes(pOut).Count == 0 && pc.Mp == 249
                        && OneShut(yOut, LettersOnly) && yc.Mp == Mana && SpellCastSupport.AetherLeft(typo, "read_karma_diviner") == 0
                        && OneShut(sOut, NotOnline) && sc.Mp == Mana && SpellCastSupport.AetherLeft(stranger, "read_karma_diviner") == 0
                        && N(aOut) == "Read karma isn't ready yet." && Boxes(aOut).Count == 0 && ac.Mp == afterRead,
                        $"short [{N(pOut)}] [{B(pOut)}] mana {pc.Mp}; typo [{B(yOut)}] spent {Mana - yc.Mp}; " +
                        $"stranger [{B(sOut)}] spent {Mana - sc.Mp}; inside the wait [{N(aOut)}] [{B(aOut)}] spent {afterRead - ac.Mp}");
        }
        finally { Leave(KarmaMap, poor, typo, stranger, again); }
    }

    /// <summary>Yourself, as the script allows (its own check is commented out): the first box names you in the third
    /// person, with the next arrow; once you answer it, the second box tells you, "You ...", with no button. 250 spent and
    /// the wait armed, as for anyone. Red on 912ba3c (no box).</summary>
    [Fact]
    public void ReadingYourOwnKarmaShowsBothBoxesToYou()
    {
        var (caster, cOut, cc) = Diviner("read_karma_diviner", KarmaMap, CX + 12, CY, configure: ch => ch.Karma = 14);
        try
        {
            string me = caster.CharName;
            List<Box> first = new(), second = new();
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                cOut.Clear();
                caster.Receive(InputFrame(me));
                first = Boxes(cOut);
                cOut.Clear();
                caster.Receive(NextFrame());
                second = Boxes(cOut);
            });
            Assert.True(first is [{ Next: 1 } a] && a.Text == $"{me} is at the pinnacle of mortal grace; their soul is noble, beautiful and fearsome to behold."
                        && second is [{ Next: 0, Prev: 0 } b] && b.Id == caster.PlayerId
                        && b.Text == "You are at the pinnacle of mortal grace; your soul is noble, beautiful and fearsome to behold."
                        && cc.Mp == Mana - 250 && SpellCastSupport.AetherLeft(caster, "read_karma_diviner") > 0,
                        $"first [{string.Join(" / ", first)}], second [{string.Join(" / ", second)}], spent {Mana - cc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, KarmaMap); }
    }

    /// <summary>A player read who has logged out by the time the caster answers the first box is told nothing, and the
    /// cast keeps its cost, as RTK's script, which finds no player behind its object then. "left": gone from the world;
    /// the second box goes to the player found again by name, never to a session that has left (red with the box sent to
    /// the session the cast resolved, kept across the wait). "leaving": still on the map but with their logout's teardown
    /// begun (<c>_leaving</c>, which the teardown sets first); the box is decided inside their monitor and goes to nobody
    /// going away (red with the going-away check taken out of <c>Session.SendSpellBoxToTarget</c>).</summary>
    [Theory]
    [InlineData("left")]
    [InlineData("leaving")]
    public void APlayerWhoHasGoneByTheCastersAnswerIsToldNothing(string how)
    {
        var (target, tOut, _) = Other(KarmaFarMap, CX + 8, CY + 8 + (how == "left" ? 0 : 2), karma: 0);
        var (caster, cOut, cc) = Diviner("read_karma_diviner", KarmaMap, CX + 12, CY + 4 + (how == "left" ? 0 : 2));
        bool left = false;
        try
        {
            OnOwnThread(() =>
            {
                caster.Receive(SpellCastSupport.CastFrame(0));
                caster.Receive(InputFrame(target.CharName));
            });
            if (how == "left") { _fx.World.LeaveMap(target, KarmaFarMap); left = true; }
            else target.WithState(() => LeavingField.SetValue(target, true));
            tOut.Clear();
            OnOwnThread(() => caster.Receive(NextFrame()));
            Assert.True(Boxes(tOut).Count == 0 && cc.Mp == Mana - 250,
                        $"the gone player was sent [{string.Join(" / ", Boxes(tOut))}]; spent {Mana - cc.Mp}");
        }
        finally
        {
            if (!left) _fx.World.LeaveMap(target, KarmaFarMap);
            _fx.World.LeaveMap(caster, KarmaMap);
        }
    }

    // ===== Hear footstep ========================================================================================

    /// <summary>Diviner's Hear footstep is Do's: a 32 s run on its caster whose beat comes every 5 s, 0 mana, the 60 s
    /// wait under its own key, "You cast Hear footstep." and graphic 11 over the caster. Red on 912ba3c: generic's 5 mana
    /// and nothing else.</summary>
    [Fact]
    public void HearFootstepStartsARunOnItsCaster()
    {
        var (caster, cOut, cc) = Diviner("hear_footstep_diviner", HearMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            var t = Ticks(caster).SingleOrDefault();
            long wait = SpellCastSupport.AetherLeft(caster, "hear_footstep_diviner");
            var drawn = Effects(cOut, caster.PlayerId);
            Assert.True(t is { Spell: "hear_footstep_diviner", Verb: "hear_footstep_tick", Self: true, Every: 5_000 }
                        && t.UntilIn - t.NextIn + t.Every == 32_000 && cc.Mp == Mana && wait > 60_000 - SlackMs && wait <= 60_000
                        && lines == "You cast Hear footstep." && drawn.SequenceEqual(new[] { 11 }),
                        $"tick {t}, spent {Mana - cc.Mp}, wait {wait} ms, lines [{lines}], graphics [{string.Join(",", drawn)}]");
        }
        finally { _fx.World.LeaveMap(caster, HearMap); }
    }

    /// <summary>Each beat names, one line each, every other player invisible within 4 tiles on both axes, and nobody
    /// further off. Red on 912ba3c: no run, so no line.</summary>
    [Fact]
    public void EachBeatNamesTheOthersHiddenWithinFourTiles()
    {
        var (near, _, _) = Other(HearMap, CX + 10 + 4, CY + 4, karma: 0);
        var (far, _, _) = Other(HearMap, CX + 10 + 5, CY, karma: 0);
        var (caster, cOut, _) = Diviner("hear_footstep_diviner", HearMap, CX + 10, CY);
        try
        {
            foreach (var s in new[] { near, far }) s.WithState(() => s.LuaSetStealth(600_000));
            caster.Receive(SpellCastSupport.CastFrame(0));
            cOut.Clear();
            Advance(caster, 5_000);
            var lines = SpellCastSupport.MiniTexts(cOut);
            Assert.True(lines.SequenceEqual(new[] { $"{near.CharName} is hidden in the area." }),
                        $"beat: [{string.Join(" | ", lines)}] (want only {near.CharName})");
        }
        finally { Leave(HearMap, caster, near, far); }
    }

    // ===== Divine time ==========================================================================================

    /// <summary>The game's time in the script's two lines: "Yuri &lt;year&gt;, &lt;month&gt;&lt;ending&gt; Moon,
    /// &lt;day&gt;&lt;ending&gt;" and "&lt;weekday&gt; &lt;hour&gt; &lt;a.m./p.m.&gt;". Year, day of the season and hour are
    /// the world clock's (set here); the Moon is the real calendar's month, at the world calendar's offset. Endings are
    /// RTK's: 1st, 2nd, 3rd, then "th" from 4 (so "21th"); the weekday is the day mod 7 into Mon to Sun; hours 0-12 are
    /// a.m. as they stand and 13-23 p.m. less 12. 5 mana, no wait, and the two lines in place of "You cast Divine
    /// time.". Red on 912ba3c: generic's 5 mana and "You cast Divine time." alone.</summary>
    [Theory]
    [InlineData(1,  0,  7,  "1st",  "Tue",   "0 a.m.")]
    [InlineData(2,  12, 7,  "2nd",  "Wed",   "12 a.m.")]
    [InlineData(3,  13, 31, "3rd",  "Thurs", "1 p.m.")]
    [InlineData(21, 23, 31, "21th", "Mon",   "11 p.m.")]
    [InlineData(90, 5,  2,  "90th", "Sun",   "5 a.m.")]
    public void DivineTimeTellsTheGamesTime(int day, int hour, int year, string dayText, string weekday, string time)
    {
        var clock = _fx.World.Clock;
        var (caster, cOut, cc) = Diviner("divine_time", TimeMap, CX, CY + day % 10);
        try
        {
            SetClock(day, year);
            clock.SetHourOverride(hour);
            int monthBefore = RealMonth();
            caster.Receive(SpellCastSupport.CastFrame(0));
            int monthAfter = RealMonth();
            var lines = SpellCastSupport.MiniTexts(cOut);
            string Want(int m) => $"Yuri {year}, {m}{Ending(m)} Moon, {dayText}";
            Assert.True(lines.Count == 2 && (lines[0] == Want(monthBefore) || lines[0] == Want(monthAfter))
                        && lines[1] == $"{weekday} {time}"
                        && cc.Mp == Mana - 5 && SpellCastSupport.AetherLeft(caster, "divine_time") == 0,
                        $"lines [{string.Join(" | ", lines)}] (want [{Want(monthBefore)} | {weekday} {time}]), spent {Mana - cc.Mp}");
        }
        finally
        {
            clock.SetHourOverride(null);
            _fx.World.TickOnceForTest();   // re-derives the whole calendar (WorldClock.Sync), the pin and the day undone
            _fx.World.LeaveMap(caster, TimeMap);
        }
    }

    /// <summary>Short of its 5 mana: "Your will is too weak.", nothing spent and no time told. Red on 912ba3c ("You do
    /// not have enough mana.", generic's line).</summary>
    [Fact]
    public void DivineTimeShortOfManaIsRefusedForFree()
    {
        var (caster, cOut, cc) = Diviner("divine_time", TimeMap, CX + 20, CY, mana: 4);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            Assert.True(lines == "Your will is too weak." && cc.Mp == 4, $"lines [{lines}], mana {cc.Mp}");
        }
        finally { _fx.World.LeaveMap(caster, TimeMap); }
    }

    // ===== Vanish ===============================================================================================

    /// <summary>The caster turns invisible (RTK state 2) for 1,000 s with the next landed swing x9, for no mana and with
    /// no wait, "You cast Vanish." and no graphic (the script sends only the cast pose); no cast slot, as Blend. Red on
    /// 912ba3c: generic's 5 mana and nothing else.</summary>
    [Fact]
    public void VanishHidesItsCasterForAThousandSeconds()
    {
        var (caster, cOut, cc) = Diviner("vanish", VanishMap, CX, CY);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            string lines = string.Join(" | ", SpellCastSupport.MiniTexts(cOut));
            long hidden = StealthLeft(caster), wait = SpellCastSupport.AetherLeft(caster, "vanish"), slot = caster.ActionSlotLeft;
            var drawn = Effects(cOut, caster.PlayerId);
            Assert.True(caster.IsInvisible && hidden > 1_000_000 - SlackMs && hidden <= 1_000_000 && cc.Mp == Mana && wait == 0
                        && lines == "You cast Vanish." && drawn.Count == 0 && StealthTimes(caster) == VanishTimes && slot == 0,
                        $"invisible {caster.IsInvisible} for {hidden} ms, spent {Mana - cc.Mp}, wait {wait} ms, lines [{lines}], " +
                        $"graphics [{string.Join(",", drawn)}], x{StealthTimes(caster)}, cast slot {slot} ms");
        }
        finally { _fx.World.LeaveMap(caster, VanishMap); }
    }

    /// <summary>A cast while already invisible casts again at once (no wait): "You cast Vanish." again, still x9, and the
    /// run keeps its end, as RTK's setDuration sets a running duration again only to a shorter time. In a creature's form,
    /// "You can't do that while transformed." (the script's canCast(1, 1, 1)), for free. Red on 912ba3c (generic).</summary>
    [Fact]
    public void VanishCastsAgainWhileInvisibleAndRefusesTransformed()
    {
        var (caster, cOut, _) = Diviner("vanish", VanishMap, CX, CY + 4);
        var (morphed, mOut, _) = Diviner("vanish", VanishMap, CX, CY + 8);
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            caster.WithState(() => StealthUntilField.SetValue(caster, Environment.TickCount64 + 60_000));
            cOut.Clear();
            FreeSlot(caster);
            caster.Receive(SpellCastSupport.CastFrame(0));
            Morph(morphed);
            mOut.Clear();
            morphed.Receive(SpellCastSupport.CastFrame(0));
            string N(RecordingOutbound o) => string.Join(" | ", SpellCastSupport.MiniTexts(o));
            long hidden = StealthLeft(caster);
            Assert.True(N(cOut) == "You cast Vanish." && caster.IsInvisible && hidden > 60_000 - SlackMs && hidden <= 60_000
                        && StealthTimes(caster) == VanishTimes
                        && N(mOut) == "You can't do that while transformed." && !morphed.IsInvisible,
                        $"again [{N(cOut)}], invisible {caster.IsInvisible} for {hidden} ms (want 60 s), x{StealthTimes(caster)}; " +
                        $"transformed [{N(mOut)}], invisible {morphed.IsInvisible}");
        }
        finally { Leave(VanishMap, caster, morphed); }
    }

    /// <summary>Vanish ends with no line, as its script's uncast has none (it has no SpellText.csv fade line): at its
    /// time, at a landed hit, at a pickup and at a won Dispell, each of which ends it. Blend's line is "You are no longer
    /// invisible."; red with a fade line given to Vanish.</summary>
    [Theory]
    [InlineData("time")]
    [InlineData("hit")]
    [InlineData("pickup")]
    [InlineData("dispell")]
    public void VanishEndsWithNoLine(string how)
    {
        var (caster, cOut, _) = Swinger(EndMap, CX + Interlocked.Increment(ref _serial) % 40, CY, "vanish");
        try
        {
            caster.Receive(SpellCastSupport.CastFrame(0));
            Assert.True(caster.IsInvisible, "vanish did not hide its caster");
            cOut.Clear();
            switch (how)
            {
                case "time":
                    caster.WithState(() => StealthUntilField.SetValue(caster, Environment.TickCount64 - 1));
                    _fx.World.TickOnceForTest();
                    break;
                case "hit":
                    for (int i = 0; i < 200 && caster.IsInvisible; i++) SwingOnce(caster);
                    break;
                case "pickup":
                    caster.Receive(SessionFixture.Frame(ClientOp.Pickup, new byte[] { 0, 0 }));
                    break;
                default:
                    caster.ReceiveFlush(endHolds: true);
                    _fx.World.TickOnceForTest();
                    break;
            }
            var said = SpellCastSupport.MiniTexts(cOut).Where(l => l.Contains("invisible", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(!caster.IsInvisible && said.Count == 0,
                        $"ended by {how}: invisible {caster.IsInvisible}, said [{string.Join(" | ", said)}]");
        }
        finally { _fx.World.LeaveMap(caster, EndMap); }
    }

    /// <summary>A Vanish that runs through a relog keeps its x9, read back from the stealth's saved name ("Vanish") by
    /// its SpellParams.csv row, with nothing new saved. Red without that row (x5, the rogue's).</summary>
    [Fact]
    public void AVanishThatRunsThroughARelogKeepsItsRule()
    {
        var (before, _, c1) = Swinger(EndMap, CX + 1, CY + 6, "vanish");
        TimedEffects saved;
        try
        {
            before.Receive(SpellCastSupport.CastFrame(0));
            before.WithState(() => CaptureMi.Invoke(before, null));
            saved = c1.Effects;
            Assert.Equal("Vanish", saved.StealthName);
        }
        finally { _fx.World.LeaveMap(before, EndMap); }

        var (after, _, _) = Swinger(EndMap, CX + 1, CY + 8, "vanish", configure: ch => ch.Effects = saved);
        try
        {
            after.WithState(() => RestoreMi.Invoke(after, null));
            Assert.True(after.IsInvisible && StealthTimes(after) == VanishTimes,
                        $"restored: invisible {after.IsInvisible}, x{StealthTimes(after)}");
        }
        finally { _fx.World.LeaveMap(after, EndMap); }
    }

    // ===== the teacher ==========================================================================================

    /// <summary>The Diviner teacher's list by the member's level: nothing at 59; Read karma alone from 60, its script's
    /// level; all four at 99. Red without the price rows: nothing at any level.</summary>
    [Theory]
    [InlineData(59, "")]
    [InlineData(60, "read_karma_diviner")]
    [InlineData(98, "read_karma_diviner")]
    [InlineData(99, "divine_time hear_footstep_diviner read_karma_diviner vanish")]
    public void TheDivinerTeacherSellsByLevel(int level, string expected)
    {
        int path = Content.PathIdForClass("Diviner");
        Assert.Equal(12, path);
        var sold = Content.SubpathTeacherSpells(path, level, Content.MaxMark);
        Assert.Equal(expected, string.Join(" ", sold.Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal)));
    }

    // ===== helpers ============================================================================================

    /// <summary>One 0x30 dialog box as the client parses it (<c>Session.SendScriptMessageP</c>, <c>SendInputBox</c>):
    /// its kind (body[0..1]: 0x0001 a text box, 0x0404 an input box), entity id, head kind, portrait graphic and palette,
    /// prev and next buttons, and text.</summary>
    private sealed record Box(int Kind, uint Id, int Head, int Gfx, int Color, int Prev, int Next, string Text)
    {
        public override string ToString() => $"{Kind:x4} id {Id} head {Head} gfx {Gfx:x4}/{Color} prev {Prev} next {Next} \"{Text}\"";
    }

    /// <summary>Every 0x30 box <paramref name="o"/> was sent, in order. The head is the sprite form (tag 1 at [7]): every
    /// box here is, so the fields after it sit where <c>Session.WriteHead</c> puts them.</summary>
    private static List<Box> Boxes(RecordingOutbound o) =>
        o.BodiesOf(ServerOp.NpcDialog).Where(b => b.Length >= 23 && b[7] == 1).Select(b =>
        {
            int len = (b[21] << 8) | b[22];
            return new Box((b[0] << 8) | b[1], (uint)((b[2] << 24) | (b[3] << 16) | (b[4] << 8) | b[5]), b[6],
                           (b[8] << 8) | b[9], b[10], b[19], b[20], Encoding.ASCII.GetString(b, 23, Math.Min(len, b.Length - 23)));
        }).ToList();

    /// <summary>An input box's answer (0x3A kind 4, a submit: step 2 at [8], the length at [10], the text from [11]).</summary>
    private static byte[] InputFrame(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var body = new byte[11 + t.Length];
        body[0] = 0x04;
        body[8] = 0x02;
        body[10] = (byte)t.Length;
        t.CopyTo(body, 11);
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>The client's "next" on a text box (0x3A kind 1, step 2).</summary>
    private static byte[] NextFrame()
    {
        var body = new byte[11];
        body[0] = 0x01;
        body[8] = 0x02;
        return SessionFixture.Frame(ClientOp.NpcDialog, body);
    }

    /// <summary>Run <paramref name="body"/> on a thread of its own, which has no synchronization context: a dialog's
    /// awaits then resume inline on the thread that delivers each reply, as on the read loop.</summary>
    private static void OnOwnThread(Action body)
    {
        Exception? fault = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { fault = e; }
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the dialog thread never finished");
        if (fault is not null) throw new Xunit.Sdk.XunitException($"the dialog thread failed: {fault}");
    }

    /// <summary>A letters-only name, unique in this process: Read karma's answer takes letters, spaces and an apostrophe
    /// only, and the fixture's other players carry digits.</summary>
    private static string Letters(string stem)
    {
        int n = Interlocked.Increment(ref _serial);
        var sb = new StringBuilder(stem);
        do { sb.Append((char)('a' + n % 26)); n /= 26; } while (n > 0);
        return sb.ToString();
    }

    private sealed record TickView(string Spell, string Verb, bool Self, int Every, long NextIn, long UntilIn);

    private static List<TickView> Ticks(Session s)
    {
        var list = new List<TickView>();
        s.WithState(() =>
        {
            long now = Environment.TickCount64;
            foreach (var t in (System.Collections.IList)TicksField.GetValue(s)!)
                list.Add(new TickView((string)F(t, "SpellKey"), (string)F(t, "Verb"), (bool)F(t, "Self"), (int)F(t, "EveryMs"),
                                      (long)F(t, "Next") - now, (long)F(t, "Until") - now));
        });
        return list;
    }

    /// <summary>Move the caster's runs <paramref name="ms"/> on, as <c>DoSpellTests</c> moves them, then run the
    /// heartbeat's call.</summary>
    private static void Advance(Session s, long ms)
    {
        s.WithState(() =>
        {
            foreach (var t in (System.Collections.IList)TicksField.GetValue(s)!)
            {
                t.GetType().GetField("Next")!.SetValue(t, (long)F(t, "Next") - ms);
                t.GetType().GetField("Until")!.SetValue(t, (long)F(t, "Until") - ms);
            }
            PublishTickDue.Invoke(s, null);
        });
        s.TickSpellTicks();
    }

    private static object F(object o, string name) => o.GetType().GetField(name)!.GetValue(o)!;

    private static uint Id(byte[] effectBody) =>
        (uint)((effectBody[0] << 24) | (effectBody[1] << 16) | (effectBody[2] << 8) | effectBody[3]);

    /// <summary>The spell graphics (0x29) <paramref name="outbound"/> was sent over <paramref name="id"/>, as effect ids.</summary>
    private static List<int> Effects(RecordingOutbound outbound, uint id) =>
        outbound.BodiesOf(ServerOp.Effect).Where(b => Id(b) == id).Select(b => b[4] - ServerConfig.Current.EfxWireOffset).ToList();

    /// <summary>Set the world clock's day of the season and year (the cached fields <c>@clock</c> reads); the caller's
    /// <c>finally</c> releases the pin and runs one beat, whose <c>WorldClock.Sync</c> derives the whole calendar again.</summary>
    private void SetClock(int day, int year)
    {
        var clock = _fx.World.Clock;
        var t = clock.GetType();
        t.GetField("_day", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(clock, day);
        t.GetField("_year", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(clock, year);
        Assert.Equal((day, year), (clock.ClockNow.day, clock.ClockNow.year));
    }

    /// <summary>The real calendar's month at the world calendar's offset, as <c>ctx.realMonth</c> reads it.</summary>
    private static int RealMonth() => DateTimeOffset.UtcNow.ToOffset(GameCalendar.Epoch.Offset).Month;

    /// <summary>RTK's ending (general_npc_funcs.time): 1st, 2nd, 3rd, "th" from 4.</summary>
    private static string Ending(int n) => n switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

    private static void FreeSlot(Session s) => s.WithState(() => { NextActionTick.SetValue(s, 0L); ActionCount.SetValue(s, 0); });

    private static long StealthLeft(Session s)
    {
        long left = 0;
        s.WithState(() => left = (long)StealthUntilField.GetValue(s)! - Environment.TickCount64);
        return left;
    }

    private static int StealthTimes(Session s)
    {
        int times = 0;
        s.WithState(() => times = (int)StealthTimesField.GetValue(s)!);
        return times;
    }

    /// <summary>One swing at an AC-0, level-1 target, run in the swinger's monitor as the swing handler runs it (a landed
    /// swing ends a stealth, as every landed swing does).</summary>
    private static void SwingOnce(Session s)
    {
        object target = Activator.CreateInstance(SwingTargetType, 0, -95, 0, 1, 11, 10, (byte)3)!;
        s.WithState(() => SwingRaw.Invoke(s, new[] { target, 1.0 }));
    }

    /// <summary>Transformed for a minute, through the same two steps the morph verb takes (<c>DoSpellTests</c>' helper).</summary>
    private static void Morph(Session s)
    {
        var sp = Content.SpellByKey("kwisin_cloak_rogue")!;
        var m = Content.MorphFor(sp)!.Value;
        s.WithState(() =>
        {
            s.LuaSetPendingMorph(m.Look, m.LookFemale, 0, 60_000);
            s.LuaApplyMorph(sp);
        });
        Assert.True(s.IsMorphed);
    }

    /// <summary>A level-99 Diviner at (<paramref name="x"/>, <paramref name="y"/>) facing east, with <paramref name="key"/>
    /// in book slot 0, on a wide content-free map.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Diviner(string key, ushort map, int x, int y,
                                                                             uint mana = Mana, Action<Character>? configure = null) =>
        _fx.PlayerWith(Letters("Diviner"), ch =>
        {
            ch.ClassName = "Diviner";
            ch.Level = 99;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = MaxMana; ch.Mp = mana;
            ch.Dir = 1;
            Wide(ch);
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    /// <summary>Another player, with <paramref name="karma"/>.</summary>
    private (Session session, RecordingOutbound outbound, Character c) Other(ushort map, int x, int y, double karma) =>
        _fx.PlayerWith(Letters("Seeker"), ch =>
        {
            ch.Level = 50;
            ch.MaxHp = 1_000; ch.Hp = 1_000;
            ch.Karma = karma;
            Wide(ch);
        }, map, (ushort)x, (ushort)y);

    /// <summary>A level-99 Diviner with a novice sword, facing east, with <paramref name="key"/> in its book and lots of
    /// mana (<c>DoSpellTests</c>' swinger).</summary>
    private (Session session, RecordingOutbound outbound, Character c) Swinger(ushort map, int x, int y, string key,
                                                                             Action<Character>? configure = null) =>
        _fx.PlayerWith(Letters("Vanisher"), ch =>
        {
            var sword = Content.ItemByKey("novice_sword")!;
            ch.ClassName = "Diviner";
            ch.Level = 99;
            ch.Ac = 0;
            ch.Might = 50; ch.Grace = 50;
            ch.MaxHp = Hp; ch.Hp = Hp;
            ch.MaxMp = MaxMana; ch.Mp = Mana;
            ch.Dir = 1;
            Wide(ch);
            ch.Equipment.Add(new InvItem { Slot = sword.EquipSlot, ItemId = sword.Id, Dura = sword.Durability });
            ch.Spells.Add(Content.SpellByKey(key)!.Id);
            configure?.Invoke(ch);
        }, map, (ushort)x, (ushort)y);

    /// <summary>Content-free maps have no size, and the cast handler needs one.</summary>
    private static void Wide(Character c) { c.MapXs = 100; c.MapYs = 100; }

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }
}
