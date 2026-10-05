using System.Text;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Approach and Summon (#313): the two travel spells the Mage, Rogue and Poet trainers teach. Until they had a
/// verb they ran the keyword fallback (<c>verbs.generic</c>): 5 mana spent, "You cast X.", and nothing else.
///
/// <para><b>What the sources say.</b> tswolf's 2001 class spell pages (Wayback, 2001-02-23 to 2001-08-21) and
/// Nexus Atlas's 2002-12-30 class pages agree: Approach brings you to a person, Summon brings a person to you,
/// 30 mana, the name is typed into the prompt, and the person must be in your group and a citizen of the same
/// kingdom, or the spell fizzles. RTK's <c>Spells/common/approach.lua</c> and <c>summon.lua</c> agree on the
/// group, the 30 mana and "Fizzle.", and add the map checks this file pins: indoors, PvP, warp-out, the
/// destination's level/vita/mana bands, staff. The Atlas's "area that allows approaching" is RTK's
/// <c>MapCanSummon</c> flag, which this server does not load; it is listed in #313's report, not invented.</para>
///
/// <para><b>Its own World.</b> The <see cref="SessionFixture"/> is a CLASS fixture, as in
/// <see cref="GmOverridesTests"/>, so this class runs on a World nothing else touches. Two reasons. These spells
/// find their target by NAME, and the shared fixture World keeps a "cmdgm" session from every class that runs a
/// GM command, so the staff fact's lookup could land on any of them. And the refusals need real maps (Vale,
/// Purgatory, the Dark Forest) that no other class stands on; on a World of their own, nothing here leaves a
/// session or a spawned mob behind on them for anyone else.</para>
///
/// <para>Every cast is the real <c>0x0F</c> frame through <c>Session.Receive</c>: the book slot, then the typed
/// answer NUL-terminated, the way <c>HandleCast</c> parses a type-1 spell. So the SpellParams row, the verb, the
/// mana and the central "You cast X." line are all part of what is under test.</para>
/// </summary>
public sealed class ApproachSummonTests : IClassFixture<SessionFixture>
{
    // Content-free maps in the instance band: no Maps.csv row, so none is indoors, PvP, warp-locked or gated.
    // Each success case gets its own pair because a landing tile is asserted exactly and a session is never
    // unregistered from a map. 61313-61332 are claimed by no other class (61325-61328 are
    // ApproachSummonGmCommandTests', on the shared World).
    private const int ApproachFromA = 61313, ApproachToA = 61314, SummonHereA = 61315, SummonFromA = 61316;
    private const int ApproachFromB = 61319, ApproachToB = 61320, SummonHereB = 61321, SummonFromB = 61322;
    private const ushort RefuseFrom = 61317, RefuseTo = 61318, StaffMap = 61323, StaffFrom = 61324;
    private const ushort BandFrom = 61329, BandTo = 61330, DepartFrom = 61331, DepartTo = 61332;

    // Real maps, each picked for exactly one flag (game-data/Maps.csv): IronHeart's Home is indoors; Vale is the
    // only outdoor PvP map the 4.95 client renders; The Dark Forest (5840) is outdoors, not PvP, and asks level 69;
    // Purgatory (600) refuses warp-outs but allows casting.
    private const ushort IndoorMap = 36, PvpMap = 1005, GatedMap = 5840, WarpLockedMap = 600;

    private const int ApproachSlot = 0, SummonSlot = 1;
    private const uint StartMp = 100;
    private const string Fizzle = "Fizzle.";

    /// <summary>The staff name. Same roster file and content every GM-command class writes (see
    /// <see cref="ResolveOnlinePlayerTests"/>), so the run order of the classes cannot demote anyone.</summary>
    private const string GmName = "cmdgm";

    private readonly SessionFixture _fx;

    public ApproachSummonTests(SessionFixture fx)
    {
        _fx = fx;
        lock (TestProcessState.Gate)
        {
            Directory.CreateDirectory(TestProcessState.StateDirectory);
            File.WriteAllText(Path.Combine(TestProcessState.StateDirectory, "gm_accounts.txt"), GmName + "\n");
            StaffAccounts.Load();
        }
    }

    // ---- success --------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Approach takes the caster to the named group member.</b> The caster lands on a free tile beside them
    /// (north first: the @approach move, <c>ArrivalPolicy.AdjacentFreeElseStack</c>), 30 mana is spent, the
    /// target does not move, and the cast says "You cast Approach.". Both kingdoms, and a name typed in the
    /// wrong case (the lookup is case-insensitive, like whisper's).
    /// <para>Red on 9c00b98: the caster stays home and 5 mana goes (the generic fallback).</para>
    /// </summary>
    [Theory]
    [InlineData("ApKoguryo", 1, ApproachFromA, ApproachToA, false)]
    [InlineData("ApBuya", 2, ApproachFromB, ApproachToB, true)]
    public void ApproachTakesTheCasterBesideAGroupMember(string tag, int nation, int from, int to, bool lowerCase)
    {
        var s = Pair(tag, (ushort)from, (ushort)to, c => c.Nation = (byte)nation, t => t.Nation = (byte)nation);
        string typed = lowerCase ? s.Target.Name.ToLowerInvariant() : s.Target.Name;

        Cast(s.Caster, ApproachSlot, typed);

        Assert.Equal(((ushort)to, (ushort)10, (ushort)9, StartMp - 30),
                     (s.CasterChar.Map, s.CasterChar.X, s.CasterChar.Y, s.CasterChar.Mp));
        Assert.Equal(((ushort)to, (ushort)10, (ushort)10), (s.Target.Map, s.Target.X, s.Target.Y));
        Assert.Equal(new[] { "You cast Approach." }, MiniTexts(s.CasterOut));
    }

    /// <summary>
    /// <b>Summon brings the named group member to the caster.</b> The target lands on a free tile beside the
    /// caster (north first: the @bring move), 30 mana is spent, the caster does not move, and the cast says
    /// "You cast Summon.". The target is told nothing: RTK's summon.lua sends them no line.
    /// <para>Red on 9c00b98: the target stays where they were and 5 mana goes.</para>
    /// </summary>
    [Theory]
    [InlineData("SuKoguryo", 1, SummonHereA, SummonFromA)]
    [InlineData("SuBuya", 2, SummonHereB, SummonFromB)]
    public void SummonBringsAGroupMemberBesideTheCaster(string tag, int nation, int here, int from)
    {
        var s = Pair(tag, (ushort)here, (ushort)from, c => c.Nation = (byte)nation, t => t.Nation = (byte)nation);

        Cast(s.Caster, SummonSlot, s.Target.Name);

        Assert.Equal(((ushort)here, (ushort)5, (ushort)4), (s.Target.Map, s.Target.X, s.Target.Y));
        Assert.Equal(((ushort)here, (ushort)5, (ushort)5, StartMp - 30),
                     (s.CasterChar.Map, s.CasterChar.X, s.CasterChar.Y, s.CasterChar.Mp));
        Assert.Equal(new[] { "You cast Summon." }, MiniTexts(s.CasterOut));
        Assert.Empty(MiniTexts(s.TargetOut));
    }

    // ---- refusals -------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Every refusal: one line, nothing spent, nobody moves.</b> Each case sets up exactly one reason to
    /// refuse on top of a pair that would otherwise succeed (grouped, same kingdom, content-free maps).
    /// <list type="bullet">
    /// <item>offline / blank: no online player has the typed name (or none was typed). RTK: "Fizzle.".</item>
    /// <item>self: the caster typed their own name. RTK: "Fizzle.".</item>
    /// <item>not-grouped: the target is not in the caster's group. RTK, tswolf 2001 and Atlas 2002.</item>
    /// <item>other-kingdom / both-neutral: not citizens of the same kingdom. tswolf 2001 ("From Same Kingdom")
    ///   and Atlas 2002 ("citizenship in the same kingdom"); RTK has no such check. Two Neutrals hold no
    ///   citizenship, so they fizzle too.</item>
    /// <item>indoor: the destination is indoors (Approach: the target's map; Summon: the caster's). RTK.</item>
    /// <item>level-band: the mover is below the destination's level band (5840 asks 69; the mover is 30). RTK.</item>
    /// <item>warp-locked: the mover's own map refuses warp-outs. RTK's one non-Fizzle line.</item>
    /// <item>pvp-target / pvp-caster: either side stands on a PvP map (RTK's two canPK checks).</item>
    /// <item>dead-target: Summon only; RTK's approach.lua has no such check.</item>
    /// <item>no-mana: 29 mana, one short. RTK's line, checked before the name is even looked up.</item>
    /// </list>
    /// <para>Red on 9c00b98 for every case: the generic fallback spends 5 mana and says "You cast X.".</para>
    /// </summary>
    [Theory]
    [InlineData("approach", "offline", Fizzle)]
    [InlineData("summon", "offline", Fizzle)]
    [InlineData("approach", "blank", Fizzle)]
    [InlineData("summon", "blank", Fizzle)]
    [InlineData("approach", "self", Fizzle)]
    [InlineData("summon", "self", Fizzle)]
    [InlineData("approach", "not-grouped", Fizzle)]
    [InlineData("summon", "not-grouped", Fizzle)]
    [InlineData("approach", "other-kingdom", Fizzle)]
    [InlineData("summon", "other-kingdom", Fizzle)]
    [InlineData("approach", "both-neutral", Fizzle)]
    [InlineData("summon", "both-neutral", Fizzle)]
    [InlineData("approach", "indoor", Fizzle)]
    [InlineData("summon", "indoor", Fizzle)]
    [InlineData("approach", "level-band", Fizzle)]
    [InlineData("summon", "level-band", Fizzle)]
    [InlineData("approach", "warp-locked", "That does not work here.")]
    [InlineData("summon", "warp-locked", "That does not work here.")]
    [InlineData("approach", "pvp-target", Fizzle)]
    [InlineData("summon", "pvp-target", Fizzle)]
    [InlineData("approach", "pvp-caster", Fizzle)]
    [InlineData("summon", "pvp-caster", Fizzle)]
    [InlineData("summon", "dead-target", Fizzle)]
    [InlineData("approach", "no-mana", "You do not have enough mana.")]
    [InlineData("summon", "no-mana", "You do not have enough mana.")]
    public void ARefusedCastSaysOneLineSpendsNothingAndMovesNobody(string spell, string why, string line)
    {
        bool approach = spell == "approach";
        ushort casterMap = RefuseFrom, targetMap = RefuseTo;
        byte casterNation = 1, targetNation = 1;
        bool grouped = true, targetDead = false;
        uint mp = StartMp;

        switch (why)
        {
            case "not-grouped":   grouped = false; break;
            case "other-kingdom": targetNation = 2; break;
            case "both-neutral":  casterNation = 0; targetNation = 0; break;
            case "indoor":        if (approach) targetMap = IndoorMap; else casterMap = IndoorMap; break;
            case "level-band":    if (approach) targetMap = GatedMap; else casterMap = GatedMap; break;
            case "warp-locked":   if (approach) casterMap = WarpLockedMap; else targetMap = WarpLockedMap; break;
            case "pvp-target":    targetMap = PvpMap; break;
            case "pvp-caster":    casterMap = PvpMap; break;
            case "dead-target":   targetDead = true; break;
            case "no-mana":       mp = 29; break;
        }

        string tag = (approach ? "Ap" : "Su") + why.Replace("-", "");
        var s = Pair(tag, casterMap, targetMap,
                     c => { c.Nation = casterNation; c.Mp = mp; },
                     t => t.Nation = targetNation,
                     grouped);
        if (targetDead)
        {
            // Killed AFTER the group formed (an invite refuses a ghost), through the real death, so being dead is
            // the only thing wrong with this target. A death does not take anyone out of their group.
            s.TargetSession.ReceiveEnvironmentDamage(9999, "a test");
            Assert.True(s.TargetSession.IsDead, "the kill did not land");
            s.CasterOut.Clear();
        }
        string typed = why switch
        {
            "offline" => "NoSuchSoul",
            "blank"   => "",
            "self"    => s.CasterChar.Name,
            _         => s.Target.Name,
        };

        Cast(s.Caster, approach ? ApproachSlot : SummonSlot, typed);

        Assert.Equal(new[] { line }, MiniTexts(s.CasterOut));
        Assert.Equal((casterMap, (ushort)5, (ushort)5, mp),
                     (s.CasterChar.Map, s.CasterChar.X, s.CasterChar.Y, s.CasterChar.Mp));
        Assert.Equal((targetMap, (ushort)10, (ushort)10), (s.Target.Map, s.Target.X, s.Target.Y));
    }

    /// <summary>
    /// <b>A player cannot approach or summon staff</b> (RTK: <c>player.gmLevel == 0 and target.gmLevel ~= 0</c>
    /// fizzles both). One GM, grouped with both casters, and no other reason to refuse: each cast says "Fizzle.",
    /// spends nothing and moves nobody. The only session named <see cref="GmName"/> on this class's World is the
    /// one made here, so the name lookup cannot land on another class's GM.
    /// <para>Red on 9c00b98: both casts spend 5 mana and say "You cast X.".</para>
    /// </summary>
    [Fact]
    public void APlayerCannotApproachOrSummonStaff()
    {
        var (approacher, approacherOut, approacherChar) = Caster("StaffApproacher", StaffFrom, c => c.Grouped = true);
        var (summoner, summonerOut, summonerChar) = Caster("StaffSummoner", StaffFrom, c => { });
        var (gm, gmOut, gmChar) = _fx.PlayerWith(GmName, c => { Dims(c, StaffMap); c.Grouped = true; c.Nation = 1; },
                                                 StaffMap, 10, 10);
        Group(summoner, approacher, approacherOut, approacherChar.Name);   // summoner leads; approacher joins
        Group(summoner, gm, gmOut, GmName);                                // and the GM joins the same group
        approacherOut.Clear(); summonerOut.Clear();

        Cast(approacher, ApproachSlot, GmName);
        Cast(summoner, SummonSlot, GmName);

        Assert.Equal(new[] { Fizzle }, MiniTexts(approacherOut));
        Assert.Equal(new[] { Fizzle }, MiniTexts(summonerOut));
        Assert.Equal((StaffFrom, StartMp, StartMp), (approacherChar.Map, approacherChar.Mp, summonerChar.Mp));
        Assert.Equal((StaffMap, (ushort)10, (ushort)10), (gmChar.Map, gmChar.X, gmChar.Y));
    }

    // ---- the binding's own edges ----------------------------------------------------------------------

    /// <summary>
    /// <b>The destination's bands, at their edges</b> (<c>ctx:mapAdmits</c>, the four comparisons RTK's
    /// approach.lua and summon.lua make). Only the level floor can be reached through a cast today: every
    /// rendered map that caps level, vita or mana is indoors, and the indoor check refuses first. So the rest is
    /// pinned here, on real Maps.csv rows picked by what they carry rather than by id: a wrong comparison at an
    /// edge would otherwise pass nothing and fail nothing.
    /// <list type="bullet">
    /// <item>level: one below <c>MapReqLvl</c> refused, at it and at <c>MapLvlMax</c> admitted, one above refused;</item>
    /// <item>vita OR mana meets its floor (RTK's <c>baseHealth &lt; reqVita and baseMagic &lt; reqMana</c>
    ///   refuses only when both fall short);</item>
    /// <item>either one over its cap refuses (<c>baseHealth &gt; maxVita or baseMagic &gt; maxMana</c>);</item>
    /// <item>a map with no row has no bands; "target" reads the resolved target, not the caster.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void TheDestinationBandsAdmitAtTheirEdgesAndRefuseOnePast()
    {
        var levels = Content.MapMeta.Where(kv => kv.Value.ReqLvl > 1 && kv.Value.LvlMax < 99)
                                    .OrderBy(kv => kv.Key).First();
        var stats = Content.MapMeta.Where(kv => kv.Value.ReqVita > 0 && kv.Value.ReqMana > 0
                                                && kv.Value.VitaMax < uint.MaxValue && kv.Value.ManaMax < uint.MaxValue)
                                   .OrderBy(kv => kv.Key).First();
        var (session, _, me) = Caster("BandProbe", BandFrom, c => { });

        bool Admits(ushort map, int level, long vita, long mana)
        {
            me.Level = (byte)level;
            me.MaxHp = (uint)vita;
            me.MaxMp = (uint)mana;
            var ctx = new SpellContext(session, Content.SpellByKey("approach_spell")!, null, null);
            return session.WithState(() => ctx.mapAdmits(map, "caster"));
        }

        var lv = levels.Value;
        Assert.Equal((false, true, true, false),
                     (Admits(levels.Key, lv.ReqLvl - 1, 100, 100), Admits(levels.Key, lv.ReqLvl, 100, 100),
                      Admits(levels.Key, lv.LvlMax, 100, 100), Admits(levels.Key, lv.LvlMax + 1, 100, 100)));

        var st = stats.Value;
        int inBand = Math.Max(1, st.ReqLvl);
        Assert.Equal((false, true, true, false, false),
                     (Admits(stats.Key, inBand, st.ReqVita - 1, st.ReqMana - 1),
                      Admits(stats.Key, inBand, st.ReqVita, 0),
                      Admits(stats.Key, inBand, 0, st.ReqMana),
                      Admits(stats.Key, inBand, st.VitaMax + 1, st.ReqMana),
                      Admits(stats.Key, inBand, st.ReqVita, st.ManaMax + 1)));

        Assert.True(Admits(BandFrom, 1, 1, 1), "a map with no Maps.csv row has no bands");
    }

    /// <summary><c>ctx:mapAdmits(map, "target")</c> judges the resolved target, not the caster: a level-99 caster
    /// and a level-30 target against The Dark Forest's level-69 floor. Summon depends on exactly this.</summary>
    [Fact]
    public void TheTargetBandReadsTheTargetNotTheCaster()
    {
        var s = Pair("BandWho", BandFrom, BandTo, c => c.Level = 99, t => t.Level = 30);
        var ctx = new SpellContext(s.Caster, Content.SpellByKey("summon_spell")!, null, s.Target.Name);

        var (resolved, caster, target) = s.Caster.WithState(() =>
            (ctx.pcTargetNamed(ctx.answer), ctx.mapAdmits(GatedMap, "caster"), ctx.mapAdmits(GatedMap, "target")));

        Assert.Equal((true, true, false), (resolved, caster, target));
    }

    /// <summary>
    /// <b>Summon leaves a target who logged out after the lookup where the logout put them: off every map.</b>
    /// The name resolves, then the target's teardown runs (the real <c>TearDownWorldState</c>, under its own
    /// monitor), then the move is asked for. <c>Session.LuaSummonTarget</c> finds <c>_leaving</c> set under the
    /// target's monitor and moves nobody; without that check <c>EnterMap</c> would put the departed session back on
    /// the caster's map, a player nobody is playing.
    /// </summary>
    [Fact]
    public void SummonDoesNotPutALoggedOutTargetBackOnAMap()
    {
        var s = Pair("Departed", DepartFrom, DepartTo, c => { }, t => { });
        var ctx = new SpellContext(s.Caster, Content.SpellByKey("summon_spell")!, null, s.Target.Name);
        Assert.True(s.Caster.WithState(() => ctx.pcTargetNamed(ctx.answer)), "the target did not resolve");

        s.TargetSession.WithState(() => TearDown.Invoke(s.TargetSession, null));
        Assert.Null(_fx.World.Online.FindPlayer(s.Target.Name));   // the logout took them off their map

        bool moved = s.Caster.WithState(() => ctx.summonTarget());

        // One assertion, so a failure shows both halves: the move reported, and the logged-out player back on a map.
        Assert.Equal((false, false), (moved, _fx.World.Online.FindPlayer(s.Target.Name) is not null));
        Assert.Equal((DepartTo, (ushort)10, (ushort)10), (s.Target.Map, s.Target.X, s.Target.Y));
    }

    // ---- fixture --------------------------------------------------------------------------------------

    private static readonly System.Reflection.MethodInfo TearDown =
        typeof(Session).GetMethod("TearDownWorldState",
                                  System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    private sealed record Scene(Session Caster, RecordingOutbound CasterOut, Character CasterChar,
                                Session TargetSession, RecordingOutbound TargetOut, Character Target);

    /// <summary>A caster who knows both spells (slot 0 Approach, slot 1 Summon) at (5,5), and a target at (10,10),
    /// grouped unless asked otherwise. Level 30, 100 mana, Koguryo, before the hooks run.</summary>
    private Scene Pair(string tag, ushort casterMap, ushort targetMap,
                       Action<Character> caster, Action<Character> target, bool grouped = true)
    {
        var (c, co, cc) = Caster(tag + "C", casterMap, caster);
        var (t, to, tc) = _fx.PlayerWith(tag + "T", ch =>
        {
            Dims(ch, targetMap);
            ch.Level = 30;
            ch.Nation = 1;
            ch.Grouped = true;   // willing: the invite below needs it, and it changes nothing when not grouped
            target(ch);
        }, targetMap, 10, 10);
        if (grouped) Group(c, t, to, tc.Name);
        co.Clear();
        to.Clear();
        return new Scene(c, co, cc, t, to, tc);
    }

    /// <summary><paramref name="leader"/> invites <paramref name="member"/> through the real 0x2E frame, and
    /// the join announcement has to reach the member. Without that check a refusal fact whose group never formed
    /// would pass on the group check, whatever it set out to test.</summary>
    private static void Group(Session leader, Session member, RecordingOutbound memberOut, string memberName)
    {
        SessionFixture.FormParty(leader, member);
        Assert.Contains($"{memberName} is joining the group.", MiniTexts(memberOut));
    }

    private (Session, RecordingOutbound, Character) Caster(string name, ushort map, Action<Character> configure)
    {
        var (session, outbound, character) = _fx.PlayerWith(name, ch =>
        {
            Dims(ch, map);
            ch.Level = 30;
            ch.Nation = 1;
            ch.MaxMp = StartMp;
            ch.Mp = StartMp;
            ch.Spells = new List<int> { SpellId("approach_spell"), SpellId("summon_spell") };
            configure(ch);
        }, map, 5, 5);
        outbound.Clear();
        return (session, outbound, character);
    }

    /// <summary>A real map's own dimensions, else 20x20 for a content-free one. A mover takes the anchor's
    /// dimensions with it, so a 12x12 default would clamp a landing tile it should not.</summary>
    private static void Dims(Character c, ushort map)
    {
        if (Content.TryMap(map, out var mi)) { c.MapXs = mi.Xs; c.MapYs = mi.Ys; }
        else { c.MapXs = 20; c.MapYs = 20; }
    }

    private static int SpellId(string key) =>
        Content.SpellByKey(key)?.Id ?? throw new InvalidDataException($"Spells.csv has no '{key}'");

    /// <summary>The real 0x0F cast frame for a type-1 (typed answer) spell: <c>slot+1</c>, the answer, a NUL.</summary>
    private static void Cast(Session caster, int slot, string answer)
    {
        byte[] text = Encoding.ASCII.GetBytes(answer);
        byte[] body = new byte[text.Length + 2];
        body[0] = (byte)(slot + 1);
        text.CopyTo(body, 1);
        caster.Receive(SessionFixture.Frame(ClientOp.Cast, body));
    }

    /// <summary>The text of every 0x0A minitext frame recorded: <c>type(u8) len(u16BE) text</c>.</summary>
    private static List<string> MiniTexts(RecordingOutbound o)
    {
        var lines = new List<string>();
        foreach (var body in o.BodiesOf(ServerOp.MiniText))
            if (body.Length >= 3) lines.Add(Encoding.ASCII.GetString(body, 3, body.Length - 3));
        return lines;
    }
}
