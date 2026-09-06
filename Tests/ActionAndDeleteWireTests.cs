using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The <c>0x1A</c> action and <c>0x10</c> remove-from-slot frames, pinned byte for byte.
///
/// <para>Both are silent-failure packets in the sense AGENTS Rule 3 means: nothing throws and nothing logs
/// an error if the type or reason byte is wrong. A wrong action byte plays the wrong animation — a drop
/// crouch instead of a throw. A wrong reason byte makes the CLIENT narrate the wrong sentence, because the
/// <c>0x10</c> body carries no item name and the client renders its status line purely off that byte: a sale
/// announced as "You dropped your sword", a bank deposit announced as a sale. That is the whole failure mode
/// naming these values is meant to stop hiding, so it is what these tests assert.</para>
///
/// <para><b>Every expected value here is a literal.</b> Not one assertion is written in terms of
/// <see cref="ActionType"/> or <see cref="DelReason"/>, so renaming a member cannot move an expectation with
/// it, and renumbering one fails these tests instead of silently changing the wire.</para>
///
/// <para>The port-parameterised cases assert one specific thing, and only that: <b>the SERVER emits the
/// same bytes for both client versions</b> — a session tagged V533 (port 2006) produces byte-identical
/// <c>0x1A</c> and <c>0x10</c> bodies to a V495 one (port 2005). That is a statement about our send path,
/// not about either client's parser. It is worth pinning because these two frames have no version branch
/// while neighbouring item packets do: <c>0x0F</c>/<c>0x42</c> carry an icon-colour byte that 5.33 alone
/// consumes (see <see cref="ExchangeWireTests"/>), so "no branch here" is a choice a future edit could
/// quietly reverse. No claim is made here about how the 5.33 CLIENT parses these two bodies; the 4.95 side
/// is what the in-tree handler decodes document.</para>
/// </summary>
public class ActionAndDeleteWireTests
{
    // ---- the two tables, pinned against their sources ------------------------------------------------

    /// <summary>The <c>0x1A</c> type table from <c>docs/4.x/Protocol.md</c> §"0x1A — action" (client handler
    /// <c>0x4503a0</c>), plus type 7, which that table omits and which is sourced from RTK script structure
    /// (every drink/smoke script is <c>sendAction(7, 20)</c>; see <c>Session.ItemSipAnim</c>).</summary>
    [Theory]
    [InlineData(0, "Stand")]
    [InlineData(1, "Attack")]
    [InlineData(2, "Throw")]
    [InlineData(3, "Shot")]
    [InlineData(4, "SitOrPickup")]
    [InlineData(5, "Drop")]
    [InlineData(6, "Magic")]
    [InlineData(7, "DrinkOrSmoke")]
    [InlineData(8, "Eat")]
    [InlineData(9, "Respect")]
    [InlineData(10, "Triumph")]
    [InlineData(11, "Laughter")]
    [InlineData(12, "Grief")]
    [InlineData(13, "Shame")]
    [InlineData(14, "Affection")]
    [InlineData(15, "Boredom")]
    [InlineData(16, "Sleepiness")]
    [InlineData(17, "Surprise")]
    [InlineData(18, "Rage")]
    [InlineData(19, "Sarcasm")]
    [InlineData(20, "Shrug")]
    [InlineData(21, "Annoyed")]
    [InlineData(22, "Dance")]
    [InlineData(23, "Strange")]
    [InlineData(24, "Kiss")]
    [InlineData(27, "Charge")]
    [InlineData(28, "AttackAfterCharge")]
    public void ActionTypeNamesKeepTheirDocumentedByte(byte wire, string name) =>
        Assert.Equal(wire, (byte)Enum.Parse<ActionType>(name));

    /// <summary>The <c>0x10</c> reason table from the LIVE <c>@delreason</c> sweep of 2026-08-07, since
    /// confirmed against the 4.95 binary: the reason byte is switched on by handler <c>0x47c800</c> through
    /// a 13-entry jump table at <c>0x47c958</c>, whose arms for 9 and 10 are reversed against
    /// <c>Inter.dat</c> file order and whose arm for 12 pushes no string at all. That is NOT the older
    /// Inter.dat-derived table in <c>Protocol.md</c> §11c, which read the message run linearly and so has
    /// 9=sold 10=gave 11=broken 12=removed and claims no reason is silent; §11c now carries a correction
    /// note. See the <see cref="DelReason"/> doc comment.
    /// <para>These bytes are what the client switches on, so this test is the thing that must change first
    /// if the table is ever revisited — and changing it will show every callsite whose wording moves with
    /// it.</para></summary>
    [Theory]
    [InlineData(0, "Removed")]              // "<item> removed." — the default/clamp line, not silence
    [InlineData(1, "Dropped")]              // "You dropped <item>." — drop ONLY
    [InlineData(2, "Ate")]
    [InlineData(3, "Smoked")]
    [InlineData(4, "Threw")]
    [InlineData(5, "Shot")]
    [InlineData(6, "Used")]
    [InlineData(7, "Posted")]
    [InlineData(8, "Decayed")]
    [InlineData(9, "Gave")]                 // §11c says "sold" — the jump table's 9/10 transposition
    [InlineData(10, "Sold")]                // §11c says "gave" here
    [InlineData(11, "RemovedAlternate")]    // §11c says "broken" here
    [InlineData(12, "Silent")]              // §11c says "removed" here, and that nothing is silent
    [InlineData(13, "Broken")]              // §11c has no 13 at all
    public void DelReasonNamesKeepTheirLiveSweptByte(byte wire, string name) =>
        Assert.Equal(wire, (byte)Enum.Parse<DelReason>(name));

    // ---- the two bodies, byte for byte ---------------------------------------------------------------

    /// <summary><c>entityId(u32BE) type(u8) time(u16BE) param(u8)</c>. The ids and times are chosen with
    /// distinct bytes so a byte-order flip cannot hide inside a symmetric value.</summary>
    [Theory]
    [InlineData(0x01020304u, 1, 20, 0, "0102030401001400")]   // swing: AttackSpeed 20 = 0x0014
    [InlineData(0x01020304u, 5, 20, 0, "0102030405001400")]   // drop crouch
    [InlineData(0x01020304u, 4, 40, 0, "0102030404002800")]   // pick-up crouch, time 40 = 0x0028
    [InlineData(0x01020304u, 22, 78, 0, "0102030416004e00")] // ':' l = dance, RTK emote length 0x4E
    [InlineData(0xFFFFFFFFu, 255, 65535, 255, "ffffffffffffffff")] // an UNNAMED byte passes through whole
    public void ActionBodyHasExactWireBytes(uint id, byte type, ushort time, byte param, string hex) =>
        Assert.Equal(Convert.FromHexString(hex.Replace(" ", "")),
                     Session.ActionBody(id, (ActionType)type, time, param));

    /// <summary><c>slot(u8 = index + 1) reason(u8) 00 00</c>. The +1 is the trap: bag index 0 goes out as
    /// wire slot 1, and an off-by-one clears the wrong cell — silently, since the client just redraws.</summary>
    [Theory]
    [InlineData(0, 0, "01000000")]
    [InlineData(0, 1, "01010000")]     // slot index 0 -> wire 1
    [InlineData(4, 9, "05090000")]     // "You gave"
    [InlineData(4, 10, "050a0000")]    // "You sold"
    [InlineData(4, 12, "050c0000")]    // the silent one
    [InlineData(4, 13, "050d0000")]    // "<item> broken."
    [InlineData(254, 200, "ffc80000")] // unnamed reason + the top wire slot, both passed through whole
    public void DeleteItemBodyHasExactWireBytes(byte slot, byte reason, string hex) =>
        Assert.Equal(Convert.FromHexString(hex), Session.DeleteItemBody(slot, (DelReason)reason));

    /// <summary>The dynamic senders must not be narrowed to the named set. <c>@delreason</c> sweeps 0..255
    /// looking for wording the enum does not name, and <c>EquipDelReason</c> is operator-configurable to any
    /// byte; both cast through <see cref="DelReason"/>. Every value must survive the round trip.</summary>
    [Fact]
    public void EveryUnnamedReasonByteStillReachesTheWireUnchanged()
    {
        for (int r = 0; r <= 255; r++)
            Assert.Equal(new byte[] { 1, (byte)r, 0, 0 }, Session.DeleteItemBody(0, (DelReason)r));
    }

    /// <summary>The same for the action byte: the <c>@mobact</c> probe exists precisely to sweep types the
    /// CREATURE vtable may map differently from the player's, so an unnamed type must go out untouched.</summary>
    [Fact]
    public void EveryUnnamedActionByteStillReachesTheWireUnchanged()
    {
        for (int t = 0; t <= 255; t++)
            Assert.Equal(new byte[] { 0, 0, 0, 7, (byte)t, 0, 20, 0 },
                         Session.ActionBody(7, (ActionType)t, 20, 0));
    }
}

/// <summary>
/// The same two frames as they actually leave a live session, driven by the client packets that cause them,
/// on BOTH game ports. These are the tests that would catch a wrong constant at a CALLSITE — the builder
/// pins above would happily keep passing if <c>HandleThrow</c> started sending the drop pose.
/// </summary>
[Collection("world")]
public sealed class ActionAndDeleteCallsiteWireTests
{
    private const byte ActionOp = 0x1A;
    private const byte DeleteItemOp = 0x10;

    private readonly SessionFixture _fx;

    public ActionAndDeleteCallsiteWireTests(SessionFixture fx) => _fx = fx;

    /// <summary>A plain, droppable, stackable trade good, deliberately NOT a harvest tool and not the Mythic
    /// Nexus amber — both of those are intercepted before the drop path's action packet is sent.</summary>
    private static ItemDef PlainDroppable()
    {
        var def = Content.ItemByKey("fox_fur");
        Assert.NotNull(def);
        Assert.False(def!.NoDrop, "fox_fur is expected to be droppable; pick another fixture item.");
        return def;
    }

    /// <summary>The one 0x1A body this packet produced, as the client's parser would walk it.</summary>
    private static byte[] OnlyAction(RecordingOutbound rec) => Assert.Single(rec.BodiesOf(ActionOp));

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    /// <summary>Spacebar (<c>0x13</c>) must play the ATTACK pose for <c>AttackSpeed</c> ticks. Wrong type
    /// here is exactly the historical bug this packet family already lived through once: replying on
    /// <c>0x13</c> instead of <c>0x1A</c> made the character flash the DEATH animation.</summary>
    [Theory]
    [InlineData(2005)]
    [InlineData(2006)]
    public void SpacebarSwingSendsTheAttackPose(int port)
    {
        var (session, rec, ch) = _fx.PlayerWith($"act_atk_{port}", c => c.Hp = 100, port: port);
        session.Receive(SessionFixture.Frame(0x13, new byte[] { 0 }));
        // entityId(u32BE) | type 01 = attack | time 0x0014 = 20 | param 00
        Assert.Equal(Hex(BeId(ch.Id)) + "01" + "0014" + "00", Hex(OnlyAction(rec)));
    }

    /// <summary>The ':' emote wheel: the client sends an INDEX and the server replies with an action, so the
    /// mapping is <c>index + 11</c> in BYTE arithmetic. Index 0 is Laughter and index 11 is Dance — the case
    /// the protocol notes call out by name (':' 'l'). A wrong base silently plays a different gesture for
    /// every emote there is.
    /// <para>The last four cases are the ones worth having. The client's key handler (0x491560) puts
    /// 'm'/'n' at actions 9/10, below the 11 its sender (0x491810) subtracts, so those two travel as
    /// <c>0xFE</c>/<c>0xFF</c> and are recovered only by wraparound. Nothing else in the wheel behaves that
    /// way, so a clamp or a range check added later would break precisely these two emotes and leave every
    /// other test in this file green.</para></summary>
    [Theory]
    [InlineData(2005, 0, "0b")]      // index 0  -> 11 laughter ('a')
    [InlineData(2006, 0, "0b")]
    [InlineData(2005, 11, "16")]     // index 11 -> 22 dance ('l')
    [InlineData(2006, 11, "16")]
    [InlineData(2005, 13, "18")]     // index 13 -> 24 kiss ('p')
    [InlineData(2006, 13, "18")]
    // THE TWO THAT WRAP. 'm' and 'n' are actions 9 and 10, BELOW the 11 the client subtracts before
    // sending, so they arrive as 0xFE and 0xFF and only byte wraparound recovers them. A clamp, a range
    // check, or CheckForOverflowUnderflow would break exactly these two and nothing else.
    [InlineData(2005, 0xFE, "09")]   // index 0xFE -> 9  respect ('m')
    [InlineData(2006, 0xFE, "09")]
    [InlineData(2005, 0xFF, "0a")]   // index 0xFF -> 10 triumph ('n')
    [InlineData(2006, 0xFF, "0a")]
    public void EmoteWheelIndexPlaysTheActionElevenAbove(int port, byte index, string expectedTypeHex)
    {
        var (session, rec, ch) = _fx.PlayerWith($"act_emo_{port}_{index}", c => c.Hp = 100, port: port);
        session.Receive(SessionFixture.Frame(0x1D, new byte[] { index, 0 }));
        // time 0x004E is RTK's emote length; param 00 = no extra sound
        Assert.Equal(Hex(BeId(ch.Id)) + expectedTypeHex + "004e" + "00", Hex(OnlyAction(rec)));
    }

    /// <summary>Dropping a whole stack: the DROP crouch (5), and a delitem whose reason is 1, the client's
    /// "You dropped &lt;item&gt;." line. Reason 1 is drop-ONLY — it was once reused for sales and turn-ins,
    /// which announced those as drops.</summary>
    [Theory]
    [InlineData(2005)]
    [InlineData(2006)]
    public void DroppingAWholeStackCrouchesAndNarratesADrop(int port)
    {
        var def = PlainDroppable();
        var (session, rec, ch) = _fx.PlayerWith($"act_drop_{port}", c =>
        {
            c.Hp = 100;
            c.Inventory.Add(new InvItem { Slot = 3, ItemId = def.Id, Amount = 2 });
        }, port: port);

        // 0x08 = drop; body is slot(1-based) then the "all" flag (1 = Shift+d, the whole stack).
        session.Receive(SessionFixture.Frame(0x08, new byte[] { 4, 1, 0 }));

        Assert.Equal(Hex(BeId(ch.Id)) + "05" + "0014" + "00", Hex(OnlyAction(rec)));      // type 5 = drop, time 20
        Assert.Equal("04" + "01" + "0000", Hex(Assert.Single(rec.BodiesOf(DeleteItemOp))));  // wire slot 4 = index 3
    }

    /// <summary>Dropping ONE of a stack still crouches, but must send NO delitem at all: the slot is still
    /// occupied, so it redraws through <c>0x0F</c> and the client says nothing. Sending a delitem here would
    /// both narrate a phantom line and clear a cell that still holds an item.</summary>
    [Theory]
    [InlineData(2005)]
    [InlineData(2006)]
    public void DroppingOneOfAStackSendsNoDeleteAtAll(int port)
    {
        var def = PlainDroppable();
        var (session, rec, _) = _fx.PlayerWith($"act_drop1_{port}", c =>
        {
            c.Hp = 100;
            c.Inventory.Add(new InvItem { Slot = 3, ItemId = def.Id, Amount = 5 });
        }, port: port);

        session.Receive(SessionFixture.Frame(0x08, new byte[] { 4, 0, 0 }));   // flag 0 = drop one

        Assert.Single(rec.BodiesOf(ActionOp));
        Assert.Empty(rec.BodiesOf(DeleteItemOp));
    }

    /// <summary>Throwing: a DISTINCT pose from dropping (2, not 5) and a distinct reason (4 "You threw",
    /// not 1 "You dropped"). Drop and throw are the pair most likely to be silently transposed, since they
    /// are adjacent handlers doing nearly the same thing.</summary>
    [Theory]
    [InlineData(2005)]
    [InlineData(2006)]
    public void ThrowingUsesTheThrowPoseAndTheThrewReason(int port)
    {
        var def = PlainDroppable();
        var (session, rec, ch) = _fx.PlayerWith($"act_throw_{port}", c =>
        {
            c.Hp = 100;
            c.Inventory.Add(new InvItem { Slot = 0, ItemId = def.Id, Amount = 1 });
        }, port: port);

        // 0x17 = throw; body is confirm then slot(1-based).
        session.Receive(SessionFixture.Frame(0x17, new byte[] { 1, 1, 0 }));

        Assert.Equal(Hex(BeId(ch.Id)) + "02" + "0014" + "00", Hex(OnlyAction(rec)));      // type 2 = throw
        Assert.Equal("01" + "04" + "0000", Hex(Assert.Single(rec.BodiesOf(DeleteItemOp))));  // reason 4 = threw
    }

    private static byte[] BeId(uint id) =>
        new byte[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id };
}
