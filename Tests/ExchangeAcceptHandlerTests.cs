using System.Text;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The first opcode-handler test in this codebase (#27). It drives <c>0x4a</c> — the native exchange window's
/// client-&gt;server packet — through the REAL dispatcher on two socket-free sessions and asserts on the exact
/// bytes each of them would have put on the wire.
///
/// <para>Nothing here is a stand-in. The World is a real <c>World</c> (constructed, never started, so no tick
/// thread can move a mob mid-assertion); the sessions are real <c>Session</c>s built on a
/// <see cref="RecordingOutbound"/> instead of a socket; the packets go in framed and encrypted exactly as the
/// client sends them and come back out framed and encrypted exactly as the client would parse them.
/// <c>ExchangeWireTests</c> pins the shape of one BODY builder in isolation; this pins what a handler
/// actually does with it — which side gets which packet, in which order, and with which latch byte.</para>
///
/// <para>The confirm latch is the part worth guarding. <c>0x42</c> sub-type 5 carries an <c>extra</c> byte the
/// client uses as a two-flag latch (<c>extra != 0</c> sets "them", <c>extra == 0</c> sets "me") and it only
/// closes the window once it has seen both — so the FIRST confirm must go out as 1 to both sides and the
/// finalizing one as 0 to both. Get that backwards and the window either closes on one confirm or never
/// closes at all, and neither failure is visible anywhere but on a client.</para>
/// </summary>
[Collection("world")]
public class ExchangeAcceptHandlerTests
{
    private const byte ExchangeIn = 0x4a;    // client -> server (RTK clif_parse_exchange)
    private const byte ExchangeOut = 0x42;   // server -> client (the window's own packet family)

    /// <summary>Verbatim from <c>Session.TradeDoneText</c>; the client renders it in the OK box, so the
    /// literal is part of the wire contract and is pinned here rather than shared with the source.</summary>
    private const string DoneText = "You exchanged, and gave away ownership of the items.";

    private readonly SessionFixture _fx;

    public ExchangeAcceptHandlerTests(SessionFixture fx) => _fx = fx;

    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>An inbound <c>0x4a</c>: sub-type, the partner's entity id, and RTK's trailing zero.</summary>
    private static byte[] ExchangeRequest(byte sub, uint targetId) =>
        SessionFixture.Frame(ExchangeIn, new byte[] { sub }.Concat(Be32(targetId)).Append((byte)0).ToArray());

    /// <summary>Sub-type 5 as it goes out: <c>05 | extra | len | text</c>.</summary>
    private static byte[] Finish(byte extra, string text) =>
        new byte[] { 5, extra, (byte)text.Length }.Concat(Encoding.ASCII.GetBytes(text)).ToArray();

    [Fact]
    public void ConfirmingAnExchangeLatchesBothWindowsThenClosesThem()
    {
        var (alpha, alphaOut) = _fx.Player("AcceptAlpha");
        var (beta, betaOut) = _fx.Player("AcceptBeta");
        // Beta's arrival broadcast reached Alpha, so clear AFTER both are standing there.
        alphaOut.Clear();
        betaOut.Clear();

        // --- sub-type 0: Alpha opens a window on Beta. Both sides get one, each naming the OTHER. ---
        alpha.Receive(ExchangeRequest(0, beta.PlayerId));

        var opened = Assert.Single(alphaOut.BodiesOf(ExchangeOut));
        Assert.Equal(new byte[] { 0 }.Concat(Be32(beta.PlayerId)).ToArray(), opened[..5]);
        // body[5] is the label length; the label is "<name>(<class title>)" and the class title comes out of
        // Paths.csv, so only the name half is pinned here.
        Assert.StartsWith("AcceptBeta(", Encoding.ASCII.GetString(opened, 6, opened[5]));

        var openedOnBeta = Assert.Single(betaOut.BodiesOf(ExchangeOut));
        Assert.Equal(new byte[] { 0 }.Concat(Be32(alpha.PlayerId)).ToArray(), openedOnBeta[..5]);
        Assert.StartsWith("AcceptAlpha(", Encoding.ASCII.GetString(openedOnBeta, 6, openedOnBeta[5]));

        // --- sub-type 5, first confirm: extra=1 to BOTH sides, and the window stays open. ---
        alphaOut.Clear();
        betaOut.Clear();
        alpha.Receive(ExchangeRequest(5, beta.PlayerId));

        Assert.Equal(Finish(1, DoneText), Assert.Single(alphaOut.BodiesOf(ExchangeOut)));
        Assert.Equal(Finish(1, DoneText), Assert.Single(betaOut.BodiesOf(ExchangeOut)));

        // --- sub-type 5, second confirm: the trade finalizes and extra=0 closes BOTH windows. ---
        alphaOut.Clear();
        betaOut.Clear();
        beta.Receive(ExchangeRequest(5, alpha.PlayerId));

        Assert.Equal(Finish(0, DoneText), Assert.Single(alphaOut.BodiesOf(ExchangeOut)));
        Assert.Equal(Finish(0, DoneText), Assert.Single(betaOut.BodiesOf(ExchangeOut)));
    }

    /// <summary>A confirm that names anyone but the partner we actually have is DROPPED — no packet at all,
    /// on either side. This is the guard that stops a stale window (one left over from a trade that already
    /// ended) from finalizing the live one; RTK disconnects the socket here instead, which is the one place
    /// this deliberately diverges.</summary>
    [Fact]
    public void AConfirmNamingTheWrongPartnerIsDropped()
    {
        var (alpha, alphaOut) = _fx.Player("StaleAlpha");
        var (beta, betaOut) = _fx.Player("StaleBeta");
        var (gamma, _) = _fx.Player("StaleGamma");
        alphaOut.Clear();
        betaOut.Clear();

        alpha.Receive(ExchangeRequest(0, beta.PlayerId));
        alphaOut.Clear();
        betaOut.Clear();

        alpha.Receive(ExchangeRequest(5, gamma.PlayerId));

        Assert.Empty(alphaOut.BodiesOf(ExchangeOut));
        Assert.Empty(betaOut.BodiesOf(ExchangeOut));

        // The real partner's confirm still works, which is what proves the packet was dropped rather than the
        // trade torn down.
        alpha.Receive(ExchangeRequest(5, beta.PlayerId));
        Assert.Equal(Finish(1, DoneText), Assert.Single(alphaOut.BodiesOf(ExchangeOut)));
    }
}
