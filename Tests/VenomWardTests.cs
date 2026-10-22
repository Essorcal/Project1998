using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #82: a Harden Body ward (the poet spell, its three alignment reskins, or the Scroll of Immortality's flag) blocks
/// a venom tick's HP loss. Caleb, 2026-10-21 (decision sheet item 14, and "82: ward blocks every tick").
///
/// <para>RTK's <c>Player.removeHealthExtend</c> opens on the ward check (player.lua:164-169), and venom.lua's tick goes
/// through it, so a warded player loses nothing; the animation and "Poison is spreading through your veins." are sent
/// outside that call and still show, and the venom's clock is never touched (Spells/NPCs/venom.lua:57-70). RTK's tick
/// has one branch that skips the ward: when the tick would not leave 1 HP it sets <c>health = 1</c> directly. That
/// branch is NOT copied: a ward blocks every tick, the drop to 1 included.</para>
///
/// <para>Each fact applies a venom through <c>ReceivePoison</c>, the writer every venom uses, with a 1 ms tick gap, and
/// drives <c>TickPoison</c> (the world heartbeat's call) until a tick has run, which shows as the line. The map is
/// content-free and no other class uses it.</para>
/// </summary>
[Collection("world")]
public sealed class VenomWardTests
{
    private const ushort Field = 62622;
    private const uint MaxHp = 10_000;
    private const int Minute = 60_000;
    private const int FlashAnim = 7;
    private const string TickLine = "Poison is spreading through your veins.";

    /// <summary>The four poet wards RTK's removeHealthExtend checks, and the flag the Scroll of Immortality sets
    /// (Session.Entity.cs, <c>HardenBodyWards</c>).</summary>
    public static TheoryData<string> Wards => new()
    {
        "harden_body", "harden_body_poet", "deaths_guard_poet", "lifes_protection_poet", "body_of_alignment_poet",
    };

    private static readonly FieldInfo PoisonUntil =
        typeof(Session).GetField("_poisonUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SessionFixture _fx;
    private static int _serial;

    public VenomWardTests(SessionFixture fx) => _fx = fx;

    /// <summary>Under a ward a 300 tick takes nothing, and nothing a loss brings: no over-head bar, no wake for a
    /// sleeper. The venom runs on: the flash goes out, the line prints, the timer is where it was and the player is
    /// still poisoned.
    /// <para>Red on 88d179d: the tick takes 300 (1000 to 700), sends the bar and wakes the sleeper.</para></summary>
    [Theory]
    [MemberData(nameof(Wards))]
    public void AWardedPlayerLosesNothingToAVenomTickAndStillSeesItRun(string ward)
    {
        var (p, o, c) = Player(hp: 1000);
        try
        {
            p.ItemSetStatus(ward, Minute);
            p.ReceiveSleep("sleeps", Minute, "venom_ward_sleep", "Probe Sleep", anim: 0, repeatFxMs: 0);
            Venom(p, perTick: 300);
            long until = Until(p);
            o.Clear();

            Tick(p, o);

            Assert.Equal(1000u, c.Hp);
            Assert.Empty(o.BodiesOf(ServerOp.Damage));
            Assert.True(p.Asleep, "a blocked tick woke the sleeper");
            Assert.NotEmpty(o.BodiesOf(ServerOp.Effect));
            Assert.True(p.Poisoned);
            Assert.Equal(until, Until(p));
        }
        finally { _fx.World.LeaveMap(p, Field); }
    }

    /// <summary>The low-HP drop is blocked too (Caleb: "ward blocks every tick"). On 5 HP a 10 tick would leave 1,
    /// and RTK's branch for that skips its ward and sets 1. Here the ward holds: 5 stays 5.
    /// <para>Red on 88d179d: 1.</para></summary>
    [Theory]
    [InlineData("harden_body")]
    [InlineData("harden_body_poet")]
    public void AWardBlocksTheDropToOneThatRtksLowHpBranchWouldMake(string ward)
    {
        var (p, o, c) = Player(hp: 5);
        try
        {
            p.ItemSetStatus(ward, Minute);
            Venom(p, perTick: 10);
            o.Clear();

            Tick(p, o);

            Assert.Equal(5u, c.Hp);
            Assert.Empty(o.BodiesOf(ServerOp.Damage));
        }
        finally { _fx.World.LeaveMap(p, Field); }
    }

    /// <summary>The control: with no ward the tick lands as it always has. 1000 loses 300 with the bar; on 5 HP a 10
    /// tick leaves 1 and never kills; a sleeper wakes.</summary>
    [Theory]
    [InlineData(1000u, 300, 700u)]
    [InlineData(5u, 10, 1u)]
    public void AnUnwardedPlayerStillTakesTheTick(uint hp, int perTick, uint left)
    {
        var (p, o, c) = Player(hp);
        try
        {
            p.ReceiveSleep("sleeps", Minute, "venom_ward_sleep", "Probe Sleep", anim: 0, repeatFxMs: 0);
            Venom(p, perTick);
            o.Clear();

            Tick(p, o);

            Assert.Equal(left, c.Hp);
            Assert.NotEmpty(o.BodiesOf(ServerOp.Damage));
            Assert.False(p.Asleep, "the tick did not wake the sleeper");
        }
        finally { _fx.World.LeaveMap(p, Field); }
    }

    // ===== helpers =======================================================================================

    private (Session session, RecordingOutbound outbound, Character c) Player(uint hp) =>
        _fx.PlayerWith($"VenomWard{Interlocked.Increment(ref _serial)}", c =>
        {
            c.MaxHp = MaxHp; c.Hp = hp; c.Level = 50;
            c.MapXs = 100; c.MapYs = 100;
        }, Field, 10, 10);

    /// <summary>A minute-long venom from no one, ticking <paramref name="perTick"/> every 1 ms, with a flash.</summary>
    private static void Venom(Session p, int perTick)
    {
        p.ReceivePoison(dps: 1, durMs: Minute, by: 0, anim: FlashAnim, key: "venom_ward_probe", name: "Probe Venom",
                        perTick: perTick, tickMinMs: 1, tickMaxMs: 1);
        Assert.True(p.Poisoned, "the venom did not take");
    }

    /// <summary>Run the heartbeat's call until a tick has run, which every tick that takes health, or would but
    /// for a ward, shows by its line.</summary>
    private static void Tick(Session p, RecordingOutbound o) =>
        Assert.True(SpinWait.SpinUntil(() =>
        {
            p.TickPoison();
            return SpellCastSupport.MiniTexts(o).Contains(TickLine);
        }, 1000), "no venom tick ran within one second");

    private static long Until(Session p)
    {
        long v = 0;
        p.WithState(() => v = (long)PoisonUntil.GetValue(p)!);
        return v;
    }
}
