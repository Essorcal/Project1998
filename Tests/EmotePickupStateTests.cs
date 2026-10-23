using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The player states that silence the <c>0x1D</c> emote wheel and the <c>0x07</c> pickup, driven end to end: the
/// real client frame through <c>Session.Receive</c>, with a peer standing beside the player to show what the map
/// would have seen.
///
/// <para>RTK keeps one state per player: <c>PC_ALIVE, PC_DIE, PC_INVIS, PC_MOUNTED, PC_DISGUISE</c>, 0 to 4
/// (<c>rtk/src/map/map.h:72</c>). <c>clif_parseemotion</c> plays an emote only in state 0
/// (<c>clif.c:6477-6482</c>). <c>clif_parsegetitem</c> returns for states 1 and 3 before it does anything else
/// (<c>clif.c:9924</c>), so a dead or mounted player is not crouched and not un-hidden. Ours keeps those states as
/// separate flags: <see cref="Session.IsDead"/>, <see cref="Session.IsInvisible"/>, <c>Character.Mounted</c> and
/// <see cref="Session.IsMorphed"/>. Both refusals are silent, in RTK and here.</para>
///
/// <para>This is a silent-failure guard in the AGENTS Rule 3 sense. A refusal that leaks shows a ghost dancing,
/// a hidden rogue giving themselves away with a laugh, or a rider bending to the floor, and nothing complains.</para>
///
/// <para>Each fact has a content-free map of its own (62760-62770, no Maps.csv row, used by no other class), so an
/// item left behind by a failed fact cannot be picked up by the next one.</para>
/// </summary>
[Collection("world")]
public sealed class EmotePickupStateTests
{
    private const byte ActionOp = 0x1A;
    private const ushort X = 20, Y = 20;

    private readonly SessionFixture _fx;

    public EmotePickupStateTests(SessionFixture fx) => _fx = fx;

    // ---- emote ---------------------------------------------------------------------------------------------

    /// <summary>Wheel index 11 is Dance: action 22, RTK's emote length 0x4E, no sound. The control: an ordinary
    /// player's emote still plays for them and for the peer beside them, byte for byte as before.</summary>
    [Fact]
    public void AnOrdinaryEmotePlaysForTheEmoterAndThePeer()
    {
        const ushort map = 62760;
        var (actor, rec, ch, peer, peerRec) = Pair("EmoOk", map, _ => { });
        try
        {
            actor.Receive(Emote(11));

            string dance = Hex(BeId(ch.Id)) + "16" + "004e" + "00";
            Assert.Equal(dance, Hex(Assert.Single(rec.BodiesOf(ActionOp))));
            Assert.Equal(dance, Hex(Assert.Single(ActionsBy(peerRec, ch.Id))));
        }
        finally { Leave(map, actor, peer); }
    }

    /// <summary>A rider's emote does nothing: no action to self or to the peer, no line. Red on 2874006, where the
    /// emote played and the client drew the rider off the horse while it did (docs/4.x/Protocol.md:1333-1337,
    /// "Which case wins").</summary>
    [Fact]
    public void AMountedEmoteSendsNothing()
    {
        const ushort map = 62761;
        var (actor, rec, _, peer, peerRec) = Pair("EmoRide", map, c => c.Mounted = true);
        try
        {
            actor.Receive(Emote(11));
            AssertNothingSent(rec, peerRec);
        }
        finally { Leave(map, actor, peer); }
    }

    /// <summary>A ghost's emote does nothing. Red on 2874006.</summary>
    [Fact]
    public void ADeadEmoteSendsNothing()
    {
        const ushort map = 62762;
        var (actor, rec, _, peer, peerRec) = Pair("EmoGhost", map, c => c.Hp = 0);
        try
        {
            Assert.True(actor.IsDead);
            actor.Receive(Emote(11));
            AssertNothingSent(rec, peerRec);
        }
        finally { Leave(map, actor, peer); }
    }

    /// <summary>An invisible player's emote does nothing, and they stay invisible: the emote is refused, it does
    /// not break the stealth the way a pickup does. Red on 2874006.</summary>
    [Fact]
    public void AnInvisibleEmoteSendsNothing()
    {
        const ushort map = 62763;
        var (actor, rec, _, peer, peerRec) = Pair("EmoHid", map, _ => { });
        try
        {
            Hide(actor);
            Settle(rec, peerRec);

            actor.Receive(Emote(11));

            AssertNothingSent(rec, peerRec);
            Assert.True(actor.IsInvisible);
        }
        finally { Leave(map, actor, peer); }
    }

    /// <summary>A transformed player's emote does nothing (RTK's <c>PC_DISGUISE</c>, ours a morph such as Feral;
    /// Caleb's decision 2 on sheet item 9, 2026-10-22). Red on 2874006.</summary>
    [Fact]
    public void ATransformedEmoteSendsNothing()
    {
        const ushort map = 62764;
        var (actor, rec, _, peer, peerRec) = Pair("EmoMorph", map, _ => { });
        try
        {
            Morph(actor);
            Settle(rec, peerRec);

            actor.Receive(Emote(11));

            AssertNothingSent(rec, peerRec);
            Assert.True(actor.IsMorphed);
        }
        finally { Leave(map, actor, peer); }
    }

    // ---- pickup --------------------------------------------------------------------------------------------

    /// <summary>The control: an ordinary pickup crouches (type 4, time 40) for self and peer and takes the item.</summary>
    [Fact]
    public void AnOrdinaryPickupCrouchesAndTakesTheItem()
    {
        const ushort map = 62765;
        var (actor, rec, ch, peer, peerRec) = Pair("PickOk", map, _ => { });
        try
        {
            var gi = Lay(map);
            Settle(rec, peerRec);

            actor.Receive(Pickup());

            AssertCrouched(rec, peerRec, ch.Id);
            AssertTaken(map, gi, ch);
        }
        finally { Leave(map, actor, peer); }
    }

    /// <summary>A rider's pickup does nothing: no crouch for self or peer, no sound, no line, and the item stays on
    /// the floor. Red on 2874006, where the rider crouched and took the item.</summary>
    [Fact]
    public void AMountedPickupTakesNothingAndSendsNoCrouch()
    {
        const ushort map = 62766;
        var (actor, rec, ch, peer, peerRec) = Pair("PickRide", map, c => c.Mounted = true);
        try
        {
            var gi = Lay(map);
            Settle(rec, peerRec);

            actor.Receive(Pickup());

            AssertNothingSent(rec, peerRec);
            AssertLeft(map, gi, ch);
        }
        finally { Leave(map, actor, peer); }
    }

    /// <summary>A ghost's pickup does nothing, the same way. Red on 2874006, where the ghost took the item.</summary>
    [Fact]
    public void ADeadPickupTakesNothingAndSendsNoCrouch()
    {
        const ushort map = 62767;
        var (actor, rec, ch, peer, peerRec) = Pair("PickGhost", map, c => c.Hp = 0);
        try
        {
            var gi = Lay(map);
            Settle(rec, peerRec);

            actor.Receive(Pickup());

            AssertNothingSent(rec, peerRec);
            AssertLeft(map, gi, ch);
        }
        finally { Leave(map, actor, peer); }
    }

    /// <summary>An invisible rider (ours can be one: neither the ride key nor <c>@mount</c> reads stealth) is
    /// refused as a rider, before the stealth break: they stay invisible, and nothing is sent or taken. RTK cannot
    /// hold both states at once; it returns for state 3 before it reaches the state-2 break
    /// (<c>clif.c:9924-9931</c>). Red on 2874006, where the rider crouched, took the item and lost the stealth.</summary>
    [Fact]
    public void AnInvisibleRidersPickupBreaksNothing()
    {
        const ushort map = 62768;
        var (actor, rec, ch, peer, peerRec) = Pair("PickHidRide", map, c => c.Mounted = true);
        try
        {
            Hide(actor);
            var gi = Lay(map);
            Settle(rec, peerRec);

            actor.Receive(Pickup());

            AssertNothingSent(rec, peerRec);
            AssertLeft(map, gi, ch);
            Assert.True(actor.IsInvisible);
        }
        finally { Leave(map, actor, peer); }
    }

    /// <summary>The control for stealth: an invisible player on foot still picks up, and the attempt still breaks
    /// the stealth, as today and as RTK (<c>clif.c:9926-9933</c>).</summary>
    [Fact]
    public void AnInvisiblePickupOnFootStillTakesAndBreaksStealth()
    {
        const ushort map = 62769;
        var (actor, rec, ch, peer, peerRec) = Pair("PickHid", map, _ => { });
        try
        {
            Hide(actor);
            var gi = Lay(map);
            Settle(rec, peerRec);

            actor.Receive(Pickup());

            Assert.Equal(Crouch(ch.Id), Hex(Assert.Single(rec.BodiesOf(ActionOp))));
            AssertTaken(map, gi, ch);
            Assert.False(actor.IsInvisible);
        }
        finally { Leave(map, actor, peer); }
    }

    /// <summary>The control for the transform: RTK refuses only states 1 and 3 at pickup, so a transformed
    /// player still crouches and takes the item, and stays transformed (decision 2: "Pickup is unchanged for
    /// transformed players").</summary>
    [Fact]
    public void ATransformedPickupStillTakesTheItem()
    {
        const ushort map = 62770;
        var (actor, rec, ch, peer, peerRec) = Pair("PickMorph", map, _ => { });
        try
        {
            Morph(actor);
            var gi = Lay(map);
            Settle(rec, peerRec);

            actor.Receive(Pickup());

            AssertCrouched(rec, peerRec, ch.Id);
            AssertTaken(map, gi, ch);
            Assert.True(actor.IsMorphed);
        }
        finally { Leave(map, actor, peer); }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>The player at (20,20) and an ordinary peer one tile east, inside every broadcast box. Both
    /// recorders come back cleared of map-entry chatter.</summary>
    private (Session actor, RecordingOutbound rec, Character ch, Session peer, RecordingOutbound peerRec) Pair(
        string tag, ushort map, Action<Character> shape)
    {
        var (actor, rec, ch) = _fx.PlayerWith(tag, c => { c.MaxHp = 100; c.Hp = 100; shape(c); }, map, X, Y);
        var (peer, peerRec, _) = _fx.PlayerWith($"{tag}Peer", c => { c.MaxHp = 100; c.Hp = 100; }, map, X + 1, Y);
        Settle(rec, peerRec);
        return (actor, rec, ch, peer, peerRec);
    }

    private void Leave(ushort map, params Session[] sessions)
    {
        foreach (var s in sessions) _fx.World.LeaveMap(s, map);
    }

    private static void Settle(params RecordingOutbound[] recs)
    {
        foreach (var r in recs) r.Clear();
    }

    /// <summary>Invisible for a minute, through the same primitive the Invisible verb (<c>stance_stealth</c>) arms.</summary>
    private static void Hide(Session s)
    {
        s.WithState(() => s.LuaSetStealth(60_000));
        Assert.True(s.IsInvisible);
    }

    /// <summary>Transformed for a minute, through the same two steps the morph verb takes
    /// (<c>CastMorphArch</c> stages the plan, the verb applies it), with a real Morphs.csv row.</summary>
    private static void Morph(Session s)
    {
        var sp = Content.SpellByKey("kwisin_cloak_rogue");
        Assert.NotNull(sp);
        var m = Content.MorphFor(sp!);
        Assert.NotNull(m);
        s.WithState(() =>
        {
            s.LuaSetPendingMorph(m!.Value.Look, m.Value.LookFemale, 0, 60_000);
            s.LuaApplyMorph(sp!);
        });
        Assert.True(s.IsMorphed);
    }

    /// <summary>A plain fox fur on the player's tile.</summary>
    private GroundItem Lay(ushort map)
    {
        var def = Content.ItemByKey("fox_fur");
        Assert.NotNull(def);
        var gi = new GroundItem { Id = _fx.World.AllocateItemId(), ItemId = def!.Id, X = X, Y = Y, Amount = 1,
                                  Graphic = def.Icon, Dura = def.Durability };
        _fx.World.DropItem(map, gi);
        return gi;
    }

    private static byte[] Emote(byte index) => SessionFixture.Frame(ClientOp.Emotion, new byte[] { index, 0 });

    private static byte[] Pickup() => SessionFixture.Frame(ClientOp.Pickup, new byte[] { 0, 0 });

    private static string Crouch(uint id) => Hex(BeId(id)) + "04" + "0028" + "00";   // type 4, time 40, no sound

    private static void AssertCrouched(RecordingOutbound rec, RecordingOutbound peerRec, uint id)
    {
        Assert.Equal(Crouch(id), Hex(Assert.Single(rec.BodiesOf(ActionOp))));
        Assert.Equal(Crouch(id), Hex(Assert.Single(ActionsBy(peerRec, id))));
    }

    /// <summary>Nothing at all: not one frame to the player (no action, no sound, no line, no redraw) and not one
    /// to the peer.</summary>
    private static void AssertNothingSent(RecordingOutbound rec, RecordingOutbound peerRec)
    {
        Assert.Empty(rec.Frames);
        Assert.Empty(peerRec.Frames);
    }

    private void AssertTaken(ushort map, GroundItem gi, Character ch)
    {
        Assert.DoesNotContain(_fx.World.ItemsOn(map), i => i.Id == gi.Id);
        Assert.Contains(ch.Inventory, i => i.ItemId == gi.ItemId);
    }

    private void AssertLeft(ushort map, GroundItem gi, Character ch)
    {
        Assert.Contains(_fx.World.ItemsOn(map), i => i.Id == gi.Id);
        Assert.DoesNotContain(ch.Inventory, i => i.ItemId == gi.ItemId);
    }

    /// <summary>The 0x1A bodies on this recorder that animate entity <paramref name="id"/>.</summary>
    private static List<byte[]> ActionsBy(RecordingOutbound rec, uint id)
    {
        var be = BeId(id);
        return rec.BodiesOf(ActionOp).Where(b => b.Length >= 4 && b.AsSpan(0, 4).SequenceEqual(be)).ToList();
    }

    private static byte[] BeId(uint id) => new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id };

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
