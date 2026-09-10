using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #196's acceptance line: <b>a revive is on disk before it returns, the way the death that preceded it is.</b>
///
/// <para><c>Die()</c> ends in <c>SaveChar()</c> — the penalties have to survive a crash, not just a clean
/// logout — so the moment a player becomes a ghost the store holds Hp 0 and the penalised exp. Both revive
/// paths restored Hp/Mp in memory and then left the character merely dirty, so until the next autosave sweep
/// (<c>Session.AutoSaveMs</c>, 15 s) the row on disk was still the corpse. A crash or a hard kill in that
/// window loaded a ghost: alive in nobody's memory, dead on disk, and dead again on the next login. That is
/// the silent-failure shape — nothing throws, nothing logs, the player just wakes up dead.</para>
///
/// <para>All three facts read the store back rather than asserting on the session, because the session was
/// always right: the defect is entirely in what did or did not reach SQLite. The window is real but
/// timing-bound in production, so no fact races it — each one reads the row immediately after the revive
/// returns, which is the instant the fix is about.</para>
///
/// <para>Every death happens on a content-free map in the instance band (59000-65000), which
/// <c>ApplyDeathPenalties</c> charges exp only for: no coin spills onto the floor and no gear breaks, so the
/// only thing moving between the two store reads is the revive itself.</para>
///
/// <para>Three revive paths, one per fact: <c>ReviveInPlace</c>, <c>ReviveAt</c> and
/// <c>Session.LuaReviveSelf</c> (the Hyun Moo revival's <c>ctx:reviveSelf()</c>), which is a revive in the
/// same sense — it drops ghost form and takes <c>IsDead</c> false — and had the same gap.</para>
/// </summary>
[Collection("world")]
public class ReviveSaveTests
{
    /// <summary>Content-free map ids in the instance band, one per fact. No Maps.csv row means nothing is
    /// terrain-blocked and no other test on the shared World is standing there; the band means the death
    /// costs exp and nothing else.</summary>
    private const ushort InPlaceMap = 60070, ReviveAtMap = 60071, ReviveSelfMap = 60072;

    /// <summary>Mp is left well below the cap before the kill, so "the revive's Mp reached the store" is a
    /// different assertion from "the row was written at all".</summary>
    private const uint DrainedMp = 3;

    private const uint StartExp = 1_000_000;

    private const string EnvText = "a cold tile, in a test";
    private const string RevivedText = "A test revived you.";

    private readonly SessionFixture _fx;

    public ReviveSaveTests(SessionFixture fx) => _fx = fx;

    /// <summary>The persisted row, or a failed assertion saying why there is none.</summary>
    private Character Stored(string name)
    {
        var loaded = _fx.Store.Load(name);
        Assert.Equal(CharacterLoadStatus.Ok, loaded.Status);
        return loaded.Character!;
    }

    /// <summary>
    /// <b>The Shaman/NPC revive is on disk when it returns.</b> <c>ReviveInPlace</c> is the funnel for
    /// <c>@rez</c> (both forms), the Shaman/Priest <c>ReviveAbility</c> and the NPC Rebirth cast.
    ///
    /// <para>Red at <c>e2938a8</c>, where the method ended in <c>MarkDirty()</c>: the row still held the
    /// corpse the death had saved — <c>Assert.Equal() Failure: Values differ. Expected: 50. Actual: 0</c>.</para>
    /// </summary>
    [Fact]
    public void AnInPlaceReviveIsPersistedBeforeItReturns()
    {
        const string Name = "ReviveInPlaceSaved";
        var (session, _, ch) = _fx.PlayerWith(Name, c => { c.Exp = StartExp; c.Mp = DrainedMp; },
                                              InPlaceMap, 5, 10);

        // Environment damage has no attacker, so the whole sequence is this session's: TakeDamage -> Die() ->
        // ApplyDeathPenalties -> SaveChar.
        session.ReceiveEnvironmentDamage(9999, EnvText);
        Assert.True(session.IsDead, "the kill did not land");
        Assert.Equal(0u, Stored(Name).Hp);   // Die()'s own save: the corpse is on disk, which is the point

        session.ReviveInPlace(RevivedText);
        Assert.False(session.IsDead, "the revive did not raise the player");

        var saved = Stored(Name);
        Assert.True(ch.Hp > 0, "the revive left the character on 0 Hp in memory");
        Assert.True(ch.Mp > DrainedMp, "the revive did not restore Mp in memory");
        Assert.Equal(ch.Hp, saved.Hp);
        Assert.Equal(ch.Mp, saved.Mp);
        // The death's own penalty is still what is on disk — the revive persisted the character it was
        // holding, not a pre-death copy of it.
        Assert.Equal(ch.Exp, saved.Exp);
    }

    /// <summary>
    /// <b>The revive-and-warp path is on disk when it returns.</b> <c>ReviveAt</c> is the poet Resurrect
    /// family's landing (<c>Session.LuaReviveTarget</c>, on the CASTER's thread) and the fresh-character/GM
    /// fallback. It is reached here directly rather than through a spell cast: the Lua verb needs a caster, a
    /// spell row, a target resolution and the script gate, none of which this fact is about, and
    /// <c>ReviveAt</c> is <c>internal</c> for exactly that reason.
    ///
    /// <para>The revive target is a REAL map, so this drives the <c>EnterMap</c> branch rather than the
    /// "map isn't loaded" fallback — the map change is the half of this path that had a <c>MarkDirty</c> of
    /// its own (<c>Session.EnterMap</c>'s position write), so a fact that skipped it would be proving less
    /// than it looks.</para>
    ///
    /// <para>Red at <c>e2938a8</c>, where the method ended at its log line: the row still held the corpse —
    /// <c>Assert.Equal() Failure: Values differ. Expected: 50. Actual: 0</c>.</para>
    /// </summary>
    [Fact]
    public void AReviveWithAWarpIsPersistedBeforeItReturns()
    {
        const string Name = "ReviveAtSaved";
        var (session, _, ch) = _fx.PlayerWith(Name, c => { c.Exp = StartExp; c.Mp = DrainedMp; },
                                              ReviveAtMap, 5, 10);

        session.ReceiveEnvironmentDamage(9999, EnvText);
        Assert.True(session.IsDead, "the kill did not land");
        Assert.Equal(0u, Stored(Name).Hp);

        session.ReviveAt(SessionFixture.HomeMap, 5, 10, RevivedText);
        Assert.False(session.IsDead, "the revive did not raise the player");
        Assert.Equal(SessionFixture.HomeMap, ch.Map);   // the EnterMap branch, not the SendSelfLook fallback

        var saved = Stored(Name);
        Assert.True(ch.Hp > 0, "the revive left the character on 0 Hp in memory");
        Assert.True(ch.Mp > DrainedMp, "the revive did not restore Mp in memory");
        Assert.Equal(ch.Hp, saved.Hp);
        Assert.Equal(ch.Mp, saved.Mp);
        Assert.Equal(ch.Exp, saved.Exp);
        // The warp half of the path lands in the same write: the row records where the player ACTUALLY
        // landed, so a write placed before EnterMap's PlacePlayer resolved the arrival tile would fail here
        // even on a same-map revive, which the Map assertion alone would not catch.
        Assert.Equal(SessionFixture.HomeMap, saved.Map);
        Assert.Equal(ch.X, saved.X);
        Assert.Equal(ch.Y, saved.Y);
    }

    /// <summary>
    /// <b>The self-revive is on disk when it returns.</b> <c>Session.LuaReviveSelf</c> is bound to
    /// <c>ctx:reviveSelf()</c> and cast by the shipped Hyun Moo revival (Spells.csv 31301). It is the third
    /// revive path — <c>RefreshAppearance</c> drops the ghost form and <c>IsDead</c> goes false — and it is
    /// the one #196's first round missed: it ended in <c>MarkDirty()</c>, so the row on disk was still the
    /// corpse the death had saved.
    ///
    /// <para>Reached through <see cref="Session.WithState(System.Action)"/> because <c>LuaReviveSelf</c> has
    /// no <c>EnterState</c> of its own — in production the cast handler's monitor is what
    /// <c>SaveChar</c>'s <c>AssertStateHeld</c> sees, and this stands in for it.</para>
    ///
    /// <para>Red at <c>658ba72</c>, where the method ended in <c>MarkDirty()</c>:
    /// <c>Assert.Equal() Failure: Values differ. Expected: 50. Actual: 0</c>.</para>
    /// </summary>
    [Fact]
    public void ASelfReviveIsPersistedBeforeItReturns()
    {
        const string Name = "ReviveSelfSaved";
        var (session, _, ch) = _fx.PlayerWith(Name, c => { c.Exp = StartExp; c.Mp = DrainedMp; },
                                              ReviveSelfMap, 5, 10);

        session.ReceiveEnvironmentDamage(9999, EnvText);
        Assert.True(session.IsDead, "the kill did not land");
        Assert.Equal(0u, Stored(Name).Hp);

        session.WithState(() => session.LuaReviveSelf());
        Assert.False(session.IsDead, "the revive did not raise the player");

        var saved = Stored(Name);
        Assert.True(ch.Hp > 0, "the revive left the character on 0 Hp in memory");
        Assert.Equal(ch.Hp, saved.Hp);
        Assert.Equal(ch.Exp, saved.Exp);
    }
}
