using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The PC-subpath foundation (Server/Session.Subpaths.cs): membership of the twelve (Barbarian … Muse) by a GM's
/// or an appointed elder's grant from level 50, leaving back to the base path with the subpath's spells taken,
/// the subpath channel limited to the twelve as RTK limits it, and no trainer or rebuild handing out a PC
/// subpath's own spells before its hall teacher exists.
///
/// <para>Every fact drives the real entry point: a framed packet through <see cref="Session.Receive"/> (a
/// typed '@' command, a whisper, the F2 click), so the tier gate, the target's monitor and the reply channel
/// are all on the path. Names are unique per fact because the world is shared and targets are found by
/// name.</para>
/// </summary>
[Collection("world")]
public sealed class PcSubpathTests
{
    private readonly SessionFixture _fx;
    private static int _serial;

    public PcSubpathTests(SessionFixture fx)
    {
        _fx = fx;
        GmRoster.Ensure();
    }

    // ---- joining by grant ------------------------------------------------------------------------------

    /// <summary>A GM's grant is refused below <see cref="Content.PcSubpathJoinLevel"/> and accepted at it. The
    /// level-49 character is left exactly as they were; the level-50 one is a Barbarian at the same level and
    /// is told so with RTK's own path-change line.</summary>
    [Fact]
    public void AGmGrantRefusesLevelFortyNineAndAdmitsLevelFifty()
    {
        var (gm, gmOut) = Gm();
        var (_, out49, c49) = Player("Warrior", 49);
        var (_, out50, c50) = Player("Warrior", 50);

        Run(gm, $"@subpath admit {c49.Name} Barbarian");
        Assert.Equal(new[] { $"bubble|{c49.Name} is level 49; the Barbarian subpath admits from level 50." },
                     CommandTableTests.Transcript(gmOut));
        Assert.Equal("Warrior", c49.ClassName);
        Assert.Empty(CommandTableTests.Transcript(out49));

        gmOut.Clear();
        Run(gm, $"@subpath admit {c50.Name} Barbarian");
        Assert.Equal("Barbarian", c50.ClassName);
        Assert.Equal(50, c50.Level);
        Assert.Contains($"{c50.Name} is now a member of the Barbarian subpath.", PaneText(gmOut));
        Assert.Equal(new[] { "pane3|Path changed to Barbarian" }, CommandTableTests.Transcript(out50));
    }

    /// <summary>All twelve admit a level-50 character of their own base class, Ranger included (no era gate,
    /// the 2026-10-24 decision sheet's item 11), and each refuses the wrong base class.</summary>
    [Theory]
    [InlineData("Barbarian", "Warrior", "Poet")]
    [InlineData("Chongun",   "Warrior", "Rogue")]
    [InlineData("Do",        "Warrior", "Mage")]
    [InlineData("Merchant",  "Rogue",   "Warrior")]
    [InlineData("Ranger",    "Rogue",   "Mage")]
    [InlineData("Spy",       "Rogue",   "Poet")]
    [InlineData("Diviner",   "Mage",    "Warrior")]
    [InlineData("Geomancer", "Mage",    "Rogue")]
    [InlineData("Shaman",    "Mage",    "Poet")]
    [InlineData("Druid",     "Poet",    "Warrior")]
    [InlineData("Monk",      "Poet",    "Rogue")]
    [InlineData("Muse",      "Poet",    "Mage")]
    public void EverySubpathAdmitsItsOwnBaseClassOnly(string subpath, string baseClass, string otherClass)
    {
        var (gm, gmOut) = Gm();
        var (_, _, right) = Player(baseClass, 50);
        var (_, _, wrong) = Player(otherClass, 50);

        Run(gm, $"@subpath admit {wrong.Name} {subpath}");
        Assert.Equal(otherClass, wrong.ClassName);
        Assert.Contains($"bubble|The {subpath} subpath admits {baseClass}s only; {wrong.Name} is on the {otherClass} path.",
                        CommandTableTests.Transcript(gmOut));

        Run(gm, $"@subpath admit {right.Name} {subpath}");
        Assert.Equal(subpath, right.ClassName);
    }

    /// <summary>No PC subpath may hold Dog secrets ("These spells are for people not in PC Subpaths", tswolf;
    /// <see cref="Content.CanLearnDogSpells"/>), so a linguist who still holds one is refused until their Dog
    /// takes it back, rather than carried into the subpath with it. Already being in a subpath is refused
    /// too: a subpath is joined from the base class.</summary>
    [Fact]
    public void AnAdmissionRefusesDogSecretsAndAnotherSubpath()
    {
        var blessing = Content.SpellByKey("greater_blessing")!;
        var (gm, gmOut) = Gm();
        var (_, _, linguist) = Player("Warrior", 75, configure: c => c.Spells.Add(blessing.Id));
        var (_, _, chongun) = Player("Chongun", 75);

        Run(gm, $"@subpath admit {linguist.Name} Barbarian");
        Run(gm, $"@subpath admit {chongun.Name} Barbarian");

        Assert.Equal(new[]
        {
            $"bubble|{linguist.Name} holds Dog secrets, which no PC subpath may keep; their Dog must take them back first.",
            $"bubble|{chongun.Name} is on the Chongun path, and must leave it first.",
        }, CommandTableTests.Transcript(gmOut));
        Assert.Equal("Warrior", linguist.ClassName);
        Assert.Equal("Chongun", chongun.ClassName);
    }

    /// <summary>Joining keeps the rank (RTK's leaders end with <c>updatePath(path, player.mark)</c>), so an Il san
    /// warrior admitted to Barbarian is a Berserker, and that title is what the click profile (0x34) shows. The
    /// user-list row files them in the Warrior column with the Barbarian badge and the rank nibble kept.</summary>
    [Fact]
    public void AnAdmissionKeepsTheRankAndTheProfileAndUserListShowTheSubpath()
    {
        var (gm, gmOut) = Gm();
        var (_, _, member) = Player("Warrior", 99, mark: 1);
        Assert.Equal("Il san (W)", Session.ClassTitleOf(member));

        Run(gm, $"@subpath admit {member.Name} Barbarian");
        Assert.Equal(1, member.Mark);
        Assert.Equal("Berserker", Session.ClassTitleOf(member));

        gmOut.Clear();
        Run(gm, $"@click {member.Name}");
        var profile = Assert.Single(gmOut.BodiesOf(ServerOp.PeerProfile));
        Assert.Contains("Berserker", Encoding.ASCII.GetString(profile));

        gmOut.Clear();
        Run(gm, "@users");
        var list = Assert.Single(gmOut.BodiesOf(ServerOp.UserList));
        var (path, icon, mark) = UserListRowOf(list, member.Name);
        Assert.Equal(1, path);   // the Warrior column (Paths.csv PthType)
        Assert.Equal(1, icon);   // Barbarian's badge (PthIcon)
        Assert.Equal(1, mark);
    }

    /// <summary>An elder admits into their own subpath and no other: naming another subpath is refused, the
    /// wrong base class is refused, a plain member cannot admit at all, and an elder the GM removes loses the
    /// right with the mark.</summary>
    [Fact]
    public void AnElderAdmitsIntoTheirOwnSubpathOnly()
    {
        var (gm, gmOut) = Gm();
        var (elder, elderOut, elderChar) = Player("Warrior", 60);
        var (warriorSession, warriorOut, warrior) = Player("Warrior", 55);
        var (_, _, poet) = Player("Poet", 70);
        var (_, _, later) = Player("Warrior", 51);

        Run(gm, $"@subpath admit {elderChar.Name} Barbarian");
        Run(gm, $"@subpath elder {elderChar.Name}");
        Assert.Contains($"{elderChar.Name} is now an elder of the Barbarian subpath.", PaneText(gmOut));
        Assert.True(elder.IsPcSubpathElder);

        elderOut.Clear();
        Run(elder, $"@subpath admit {warrior.Name} Chongun");
        Assert.Equal(new[] { "bubble|You may admit members to the Barbarian subpath only." },
                     CommandTableTests.Transcript(elderOut));
        Assert.Equal("Warrior", warrior.ClassName);

        Run(elder, $"@subpath admit {poet.Name}");
        Assert.Equal("Poet", poet.ClassName);

        Run(elder, $"@subpath admit {warrior.Name}");
        Assert.Equal("Barbarian", warrior.ClassName);

        // A member who is not an elder admits nobody.
        warriorOut.Clear();
        Run(warriorSession, $"@subpath admit {later.Name}");
        Assert.Equal(new[] { "bubble|Only a GM or one of a subpath's elders may admit a member." },
                     CommandTableTests.Transcript(warriorOut));
        Assert.Equal("Warrior", later.ClassName);

        // The GM takes the mark away, and the right goes with it.
        Run(gm, $"@subpath elder {elderChar.Name} 0");
        Assert.False(elder.IsPcSubpathElder);
        Run(elder, $"@subpath admit {later.Name}");
        Assert.Equal("Warrior", later.ClassName);
    }

    // ---- leaving -------------------------------------------------------------------------------------

    /// <summary>Leaving returns the base path with the rank kept and takes exactly the subpath's own spells out
    /// of the book (RTK's leave: removeSpell, then <c>updatePath(baseClass, mark)</c>); base spells stay. An
    /// elder who leaves is nobody's elder. A GM's expulsion does the same to someone else and tells them with
    /// RTK's path-change line.</summary>
    [Fact]
    public void LeavingRestoresTheBasePathAndStripsTheSubpathSpells()
    {
        var barbarianSpells = Content.Spells.Where(s => s.PathId == 10).Select(s => s.Id).Take(3).ToList();
        var warriorSpell = Content.Spells.First(s => s.PathId == 1).Id;
        Assert.Equal(3, barbarianSpells.Count);

        var (gm, gmOut) = Gm();
        var (member, memberOut, c) = Player("Barbarian", 70, mark: 2, configure: ch =>
        {
            ch.Spells.Add(warriorSpell);
            ch.Spells.AddRange(barbarianSpells);
        });
        Run(gm, $"@subpath elder {c.Name}");
        Assert.True(member.IsPcSubpathElder);

        memberOut.Clear();
        Run(member, "@subpath leave");
        Assert.Equal("Warrior", c.ClassName);
        Assert.Equal(2, c.Mark);
        Assert.Equal(new[] { warriorSpell }, c.Spells);
        Assert.False(member.IsPcSubpathElder);
        Assert.Equal(0, member.Quest(QuestState.Registry).Get(Session.SubpathElderReg));
        Assert.Contains("You have left the Barbarian subpath and are on the Warrior path again; 3 of its " +
                        "secrets are forgotten.", PaneText(memberOut));

        // Expelled by a GM: same result, and the member is told.
        var (_, outcastOut, outcast) = Player("Chongun", 80, configure: ch =>
            ch.Spells.AddRange(Content.Spells.Where(s => s.PathId == 14).Select(s => s.Id).Take(2)));
        gmOut.Clear();
        Run(gm, $"@subpath expel {outcast.Name}");
        Assert.Equal("Warrior", outcast.ClassName);
        Assert.DoesNotContain(outcast.Spells, id => Content.SpellById(id)!.PathId == 14);
        Assert.Contains("pane3|Path changed to Warrior", CommandTableTests.Transcript(outcastOut));
        Assert.Contains($"{outcast.Name} is no longer a member of the Chongun subpath; 2 of its secrets were taken.",
                        PaneText(gmOut));
    }

    // ---- the subpath channel -------------------------------------------------------------------------

    /// <summary>A member's <c>/sp</c> — and RTK's own route, a whisper to "@" — reaches the members of their own
    /// subpath who have the channel on, and nobody else: not a Chongun (the same base class), not a Warrior,
    /// not a Barbarian with the channel off.</summary>
    [Fact]
    public void AMembersSubpathChatReachesOnlyTheirOwnSubpath()
    {
        var (sender, senderOut, s) = Player("Barbarian", 60, configure: ch => ch.SubpathChat = true);
        var (_, mateOut, _) = Player("Barbarian", 60, configure: ch => ch.SubpathChat = true);
        var (_, quietOut, _) = Player("Barbarian", 60);
        var (_, chongunOut, _) = Player("Chongun", 60, configure: ch => ch.SubpathChat = true);
        var (_, warriorOut, _) = Player("Warrior", 60, configure: ch => ch.SubpathChat = true);

        Run(sender, "/sp the field is clear");
        sender.Receive(WhisperFrame("@", "and the river"));

        string first = $"pane3|<@{s.Name}> (Barbarian) the field is clear";
        string second = $"pane3|<@{s.Name}> (Barbarian) and the river";
        Assert.Equal(new[] { first, second }, CommandTableTests.Transcript(mateOut));
        Assert.Equal(new[] { first, second }, CommandTableTests.Transcript(senderOut));   // the sender's own copy
        Assert.Empty(CommandTableTests.Transcript(quietOut));
        Assert.Empty(CommandTableTests.Transcript(chongunOut));
        Assert.Empty(CommandTableTests.Transcript(warriorOut));
    }

    /// <summary>A base class or an NPC subpath is refused the channel with RTK's line, on RTK's blue channel,
    /// whether by <c>/sp</c> or by the "@" whisper, and another Warrior with the channel on hears nothing. The
    /// gate is on sending, which is where RTK's clif_parsewisp refuses.</summary>
    [Fact]
    public void ABaseClassOrNpcSubpathIsRefusedTheChannelAsRtk()
    {
        const string refused = "pane0|You cannot do that.";
        var (warrior, warriorOut, _) = Player("Warrior", 60, configure: ch => ch.SubpathChat = true);
        var (_, listenerOut, _) = Player("Warrior", 60, configure: ch => ch.SubpathChat = true);
        var (chung, chungOut, _) = Player("Chung ryong", 99, configure: ch => ch.SubpathChat = true);

        Run(warrior, "/sp anyone there");
        warrior.Receive(WhisperFrame("@", "anyone there"));
        Assert.Equal(new[] { refused, refused }, CommandTableTests.Transcript(warriorOut));
        Assert.Empty(CommandTableTests.Transcript(listenerOut));

        Run(chung, "/sp anyone there");
        Assert.Equal(new[] { refused }, CommandTableTests.Transcript(chungOut));
    }

    /// <summary>F2 toggles subpath chat for every class, as RTK's clif_handle_clickgetinfo does with no class
    /// check: a Warrior and a Chung ryong get "Subpath Chat: ON" and "OFF" like a member. Turning it on gives
    /// a Warrior nothing to say on it: their <c>/sp</c> is still refused.</summary>
    [Fact]
    public void F2TogglesSubpathChatForEveryClassAsRtk()
    {
        var (warrior, warriorOut, w) = Player("Warrior", 60);
        var (chung, chungOut, c) = Player("Chung ryong", 99);
        var (member, memberOut, m) = Player("Barbarian", 60);

        warrior.Receive(F2Frame());
        Assert.True(w.SubpathChat);
        warrior.Receive(F2Frame());
        Assert.False(w.SubpathChat);
        Assert.Equal(new[] { "pane3|Subpath Chat: ON", "pane3|Subpath Chat: OFF" }, CommandTableTests.Transcript(warriorOut));

        chung.Receive(F2Frame());
        member.Receive(F2Frame());
        Assert.True(c.SubpathChat);
        Assert.True(m.SubpathChat);
        Assert.Equal(new[] { "pane3|Subpath Chat: ON" }, CommandTableTests.Transcript(chungOut));
        Assert.Equal(new[] { "pane3|Subpath Chat: ON" }, CommandTableTests.Transcript(memberOut));

        warrior.Receive(F2Frame());
        warriorOut.Clear();
        Run(warrior, "/sp anyone there");
        Assert.Equal(new[] { "pane0|You cannot do that." }, CommandTableTests.Transcript(warriorOut));
    }

    // ---- trainers and the guild hall -----------------------------------------------------------------

    /// <summary>A PC-subpath member's base-class trainer teaches them the base class's list, as RTK's learnSpell
    /// does for "regular path and pc subpaths" (getUnknownSpells(player.baseClass)): a level-60 Barbarian at
    /// Tabaek gets Learn Secret and Divine Secret, and exactly the list a level-60 Warrior with the same empty
    /// book gets there, Soothe's relearn included and secrets past level 50 among them. Another class's trainer
    /// still teaches them nothing, and an NPC subpath is left as it was.</summary>
    [Fact]
    public void ABaseClassTrainerTeachesAPcSubpathMemberTheBaseList()
    {
        var (member, _, _) = Player("Barbarian", 60);
        var (warrior, _, _) = Player("Warrior", 60);
        var (chung, _, _) = Player("Chung ryong", 99);

        var memberAtTabaek = At(36, member);   // Tabaek, a Warrior trainer
        var menu = Menu(memberAtTabaek);
        Assert.Contains("Learn Secret", menu);
        Assert.Contains("Divine Secret", menu);

        var keys = memberAtTabaek.LearnableSpells().Select(s => s.Key).ToList();
        Assert.Equal(At(36, warrior).LearnableSpells().Select(s => s.Key).ToList(), keys);
        Assert.Contains("soothe", keys);
        Assert.Contains(memberAtTabaek.LearnableSpells(), s => s.Level > Content.PcSubpathJoinLevel);
        Assert.DoesNotContain(memberAtTabaek.LearnableSpells(), Content.IsPcSubpathSpell);

        Assert.DoesNotContain("Learn Secret", Menu(At(37, member)));   // a Rogue trainer
        Assert.DoesNotContain("Learn Secret", Menu(At(36, chung)));
    }

    /// <summary>The guild-hall door in a path hall checks the base class, as RTK's onScriptedTilesPathHalls.lua
    /// (player.baseClass) does: a Barbarian walks into the Warriors' guild hall like a Warrior; a Poet is
    /// refused with RTK's line, and an NPC subpath is left as it was.</summary>
    [Fact]
    public void TheGuildHallDoorAdmitsAPcSubpathMemberOfItsBaseClass()
    {
        var hall = Content.PathHalls[11];   // Kugnae's Warrior path hall
        Assert.Equal(1, hall.BaseClass);

        var (warrior, _, w) = Player("Warrior", 60, map: 11, x: 1, y: 22);
        var (member, _, m) = Player("Barbarian", 60, map: 11, x: 1, y: 22);
        var (poet, poetOut, p) = Player("Poet", 60, map: 11, x: 1, y: 22);
        var (chung, _, c) = Player("Chung ryong", 99, map: 11, x: 1, y: 22);

        foreach (var s in new[] { warrior, member, poet, chung }) StepOnGuildDoor(s);

        Assert.Equal(hall.GuildMap, w.Map);
        Assert.Equal(hall.GuildMap, m.Map);
        Assert.Equal(11, p.Map);
        Assert.Equal(11, c.Map);
        Assert.Contains("pane3|You are not the right class to enter here.", CommandTableTests.Transcript(poetOut));
    }

    // ---- no PC-subpath spell from a trainer or a rebuild -----------------------------------------------

    /// <summary>Nobody teaches a PC subpath's own spells yet. A level-99 Barbarian at the Warrior trainer, which
    /// teaches them the Warrior list, is offered none of them in Learn Secret or Divine Secret; the content
    /// list behind both carries none at any rank; and a rebuild into the subpath (<c>@class</c>, which now
    /// accepts the twelve) fills the book with the Warrior list and no Barbarian spell.</summary>
    [Fact]
    public void NoTrainerOrRebuildHandsOutAPcSubpathSpell()
    {
        for (int mark = 0; mark <= Content.MaxMark; mark++)
        {
            var list = Content.SpellsForClass(10, Content.MarkSpellLevel, 0, mark);
            Assert.NotEmpty(list);
            Assert.DoesNotContain(list, Content.IsPcSubpathSpell);
        }

        var (member, _, _) = Player("Barbarian", 99);
        var ctx = At(36, member);   // Tabaek, a Warrior trainer
        Assert.NotEmpty(ctx.LearnableSpells());
        Assert.DoesNotContain(ctx.LearnableSpells(), Content.IsPcSubpathSpell);
        Assert.DoesNotContain(ctx.FutureSpells(), Content.IsPcSubpathSpell);
        Assert.DoesNotContain(member.LearnableClassSpells(), Content.IsPcSubpathSpell);

        var (gm, _, gmChar) = Player("Warrior", 99, name: GmRoster.Name);
        Run(gm, "@class Barbarian");
        Assert.Equal("Barbarian", gmChar.ClassName);
        Assert.NotEmpty(gmChar.Spells);
        Assert.DoesNotContain(gmChar.Spells, id => Content.IsPcSubpathSpell(Content.SpellById(id)!));
    }

    // ===== plumbing =====================================================================================

    /// <summary>The same roster, written the same way and with the same content, that
    /// <c>CommandTableTests</c> and <c>PeerMiniTextMonitorTests</c> write — so whichever writes first, the
    /// others' rewrite changes nothing.</summary>
    private static class GmRoster
    {
        public const string Name = "cmdgm";
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
    }

    private (Session session, RecordingOutbound outbound) Gm()
    {
        var (session, outbound, _) = Player("Peasant", 1, name: GmRoster.Name);
        return (session, outbound);
    }

    /// <summary>A fresh player of <paramref name="className"/> at <paramref name="level"/> and
    /// <paramref name="mark"/>, with a name no other fact uses.</summary>
    private (Session session, RecordingOutbound outbound, Character character) Player(
        string className, int level, int mark = 0, string? name = null, Action<Character>? configure = null,
        ushort map = SessionFixture.HomeMap, ushort x = 5, ushort y = 10)
    {
        name ??= $"pcsp{Interlocked.Increment(ref _serial)}";
        var (session, outbound, character) = _fx.PlayerWith(name, c =>
        {
            c.ClassName = className;
            c.Level = (byte)level;
            c.Mark = (byte)mark;
            configure?.Invoke(c);
        }, map, x, y);
        return (session, outbound, character);
    }

    /// <summary><paramref name="player"/> talking to placed NPC <paramref name="npcId"/>, the way
    /// NpcCompositionPinTests builds its conversations.</summary>
    private NpcContext At(int npcId, Session player)
    {
        var def = Content.NpcById(npcId)!;
        var npc = _fx.World.NpcsNear(def.Map, def.X, def.Y, 0).First(n => n.NpcDefId == npcId);
        return new NpcContext(player, npc, def);
    }

    /// <summary>The click menu an NPC shows this conversation's player, as labels.</summary>
    private static List<string> Menu(NpcContext ctx) =>
        NpcScripts.For(ctx.Def).SelectMany(a => a.Entries(ctx)).Select(e => e.label).ToList();

    private static readonly System.Reflection.MethodInfo PathHallWarp =
        typeof(Session).GetMethod("TryPathHallWarp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    /// <summary>The step onto a path hall's south doorway (1,23) that <c>Session.TryPathHallWarp</c> answers, run
    /// inside the player's own monitor as the walk handler runs it. By reflection: the walk itself would need a
    /// whole 0x06 exchange to reach a scripted tile whose handling is the point here.</summary>
    private static void StepOnGuildDoor(Session player) =>
        player.WithState(() => Assert.True((bool)PathHallWarp.Invoke(player, new object[] { (ushort)1, (ushort)23 })!));

    private static void Run(Session session, string command)
    {
        var text = Encoding.ASCII.GetBytes(command);
        var body = new byte[2 + text.Length];
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        session.Receive(SessionFixture.Frame(ClientOp.Chat, body));
    }

    /// <summary>The status-pane lines a command printed, joined back into one string so a wrapped reply can be
    /// matched whole (<see cref="Session.WrapForPane"/> breaks only at spaces, so a single space rejoins it).</summary>
    private static string PaneText(RecordingOutbound outbound) =>
        string.Join(" ", CommandTableTests.Transcript(outbound)
            .Where(l => l.StartsWith("pane3|", StringComparison.Ordinal) && !l.EndsWith(Session.PaneRule, StringComparison.Ordinal))
            .Select(l => l["pane3|".Length..]));

    /// <summary>A whisper (<c>0x19</c>): <c>dstLen(u8) dst[dstLen] msgLen(u8) msg[msgLen] 00</c>, the layout
    /// <c>Session.HandleWhisperPacket</c> reads; "@" as the target is RTK's subpath route.</summary>
    private static byte[] WhisperFrame(string dstName, string msg)
    {
        byte[] dst = Encoding.ASCII.GetBytes(dstName);
        byte[] text = Encoding.ASCII.GetBytes(msg);
        var body = new List<byte> { (byte)dst.Length };
        body.AddRange(dst);
        body.Add((byte)text.Length);
        body.AddRange(text);
        body.Add(0);
        return SessionFixture.Frame(ClientOp.Whisper, body.ToArray());
    }

    /// <summary>F2: the click-info packet (<c>0x43</c>, <c>01 id(u32BE) 00</c>) with RTK's subpath-chat sentinel
    /// <c>0xFFFFFFFE</c> as the id.</summary>
    private static byte[] F2Frame() =>
        SessionFixture.Frame(ClientOp.ClickInfo, new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFE, 0x00 });

    /// <summary>One 4.95 user-list row, found by its name: <c>nation&lt;&lt;4|path, hidden&lt;&lt;4|icon, colour,
    /// rank u32BE, mark&lt;&lt;4|nameLen, name</c> (the layout ClientVersionWireTests pins).</summary>
    private static (int Path, int Icon, int Mark) UserListRowOf(byte[] body, string name)
    {
        byte[] needle = Encoding.ASCII.GetBytes(name);
        for (int i = 8; i + needle.Length <= body.Length; i++)
        {
            if (!body.AsSpan(i, needle.Length).SequenceEqual(needle)) continue;
            if ((body[i - 1] & 0x0F) != needle.Length) continue;
            return (body[i - 8] & 0x0F, body[i - 7] & 0x0F, body[i - 1] >> 4);
        }
        throw new Xunit.Sdk.XunitException($"no user-list row named '{name}'");
    }
}
