using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;
using Xunit.Sdk;

namespace Tests;

/// <summary>
/// A <c>0x06</c> map reply never outgrows one frame (<c>Session.SendMapRect</c>).
///
/// <para>The frame's u16 length field counts op + inc + body, and the body is 6 header bytes plus one cell per
/// tile: 4 bytes on 4.95, 6 on 5.33 (<c>MapCell</c>). <c>TkPacket.Build</c> writes that length unchecked, so
/// before the clamp a request past 16,381 cells (4.95) or 10,921 (5.33) went out with a wrapped length field
/// and the client, which frames its stream by it, lost its place. Nothing threw and nothing warned. The retail
/// client asks about 18x16 and the stream window is 27x25, so only a hand-built <c>0x05</c> reaches it, and
/// only on the six maps big enough to answer one (seven on 5.33, where Islets' 80x200 is too). Kugnae (map 0,
/// 220x220) is one of them.</para>
///
/// <para>The clamp drops ROWS, keeping x0, y0 and w, so the header still names exactly the cells sent. Each
/// fact drives the real <c>0x05</c> (and, for the stream, the real walk) through <c>Session.Receive</c>, and
/// reads each recorded frame from its own bytes, because the length field is the thing under test:
/// <c>RecordingOutbound.BodiesOf</c> parses by that field and would agree with a wrapped one.</para>
///
/// <para>Kugnae is shared with <c>BarrierSpellTests</c>; every fact here takes its players off the map again.</para>
/// </summary>
[Collection("world")]
public sealed class MapReplyFrameTests
{
    private const ushort Kugnae = 0;
    private const int V495 = 2005, V533 = 2006;   // the ports SessionFixture.PlayerWith maps to each client
    private const byte East = 1;

    private readonly SessionFixture _fx;
    private static int _serial;

    public MapReplyFrameTests(SessionFixture fx) => _fx = fx;

    private static int BytesPerCell(int port) => port == V533 ? 6 : 4;

    /// <summary>A player on Kugnae, sized the way <c>EnterMap</c> sizes one: <c>HandleMapRequest</c> answers
    /// nothing while the character's map dimensions are 0, and <c>MapData.For</c> caches the map at the
    /// dimensions its first caller passes, so they must be the map's own.</summary>
    private (Session Session, RecordingOutbound Outbound, Character Character) OnKugnae(int port, int x = 5, int y = 5)
    {
        var dims = Content.Maps[Kugnae];
        return _fx.PlayerWith($"MapFrame{Interlocked.Increment(ref _serial)}",
                              c => { c.MapXs = dims.Xs; c.MapYs = dims.Ys; }, Kugnae, (ushort)x, (ushort)y, port);
    }

    /// <summary>The client's view-rect request: <c>x0(u16BE) y0(u16BE) w(u8) h(u8)</c>, then the four bytes of
    /// stack the 4.95 client sends after them (Protocol.md §10.7).</summary>
    private static byte[] MapRequest(int x0, int y0, int w, int h) =>
        SessionFixture.Frame(ClientOp.MapRequest,
            new byte[] { (byte)(x0 >> 8), (byte)x0, (byte)(y0 >> 8), (byte)y0, (byte)w, (byte)h, 0, 0, 0, 0 });

    private static byte[] Walk(byte dir, int x, int y) =>
        SessionFixture.Frame(ClientOp.Walk, new byte[] { dir, 0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y });

    private readonly record struct MapFrame(int LengthField, int RealLength, byte[] Body)
    {
        public (int X0, int Y0, int W, int H) Header =>
            ((Body[0] << 8) | Body[1], (Body[2] << 8) | Body[3], Body[4], Body[5]);
    }

    /// <summary>Every recorded <c>0x06</c>, read from its own bytes. The recorder keeps each send as its own
    /// array, so a frame's real length is its array's, whatever its length field says.</summary>
    private static List<MapFrame> MapFrames(RecordingOutbound o)
    {
        var frames = new List<MapFrame>();
        foreach (var f in o.Frames)
        {
            if (f.Length < 5 || f[0] != 0xAA || f[3] != ServerOp.MapCells) continue;
            frames.Add(new MapFrame((f[1] << 8) | f[2], f.Length - 3, TkCrypt.Crypt(f.AsSpan(5), f[4], TkCrypt.LoginKey)));
        }
        return frames;
    }

    /// <summary>
    /// <b>255x255 on Kugnae is one well-formed <c>0x06</c>: its length field is its length, its cells are
    /// w*h, and they are the top rows of the rect, as many as fit.</b>
    ///
    /// <para>Red on 0627024, on both clients: one frame of 220x220 cells whose length field has wrapped
    /// (62,536 against 193,608 on 4.95; 28,264 against 290,408 on 5.33).</para>
    /// </summary>
    [Theory]
    [InlineData(V495)]
    [InlineData(V533)]
    public void A255x255RequestOnKugnaeIsOneFrameWhoseLengthFieldIsItsLength(int port)
    {
        var dims = Content.Maps[Kugnae];
        var (s, o, _) = OnKugnae(port);
        try
        {
            s.Receive(MapRequest(0, 0, 255, 255));

            var f = Assert.Single(MapFrames(o));
            Assert.Equal(f.RealLength, f.LengthField);
            var (x0, y0, w, h) = f.Header;
            Assert.Equal((0, 0, (int)dims.Xs), (x0, y0, w));            // x0, y0 and w are kept
            Assert.Equal(6 + w * h * BytesPerCell(port), f.Body.Length);  // the header names exactly the cells sent
            Assert.InRange(h, 1, dims.Ys - 1);                            // fewer rows than asked for...
            Assert.True(2 + 6 + w * (h + 1) * BytesPerCell(port) > ushort.MaxValue,
                        $"{w}x{h} is not the most rows that fit: {h + 1} would too");   // ...but every row that fits

            // ...and they are the rows a request for exactly that rect carries: the top of the rect asked for.
            o.Clear();
            s.Receive(MapRequest(0, 0, w, h));
            Assert.Equal(Assert.Single(MapFrames(o)).Body, f.Body);
        }
        finally { _fx.World.LeaveMap(s, Kugnae); }
    }

    /// <summary>
    /// <b>At the limit: the largest rect that fits goes out whole, and one row more goes out as exactly that
    /// rect.</b>
    ///
    /// <para>Widths whose limit falls inside the map (so the frame, not the map edge, is what stops the rows),
    /// at y0 = 1 so a clamp that moved the origin would show. The "whole" half is green on 0627024 by
    /// construction, since it never clamped; it is the half that catches a clamp that bites too early. Red on
    /// 0627024: the one-row-more request goes out with its full height and a wrapped length field.</para>
    /// </summary>
    [Theory]
    [InlineData(V495, 220)]   // 74 rows, 16,280 cells
    [InlineData(V495, 93)]    // 176 rows, 16,368 cells: length field 65,480
    [InlineData(V495, 75)]    // 218 rows: the limit one row inside the map's 219 below y0
    [InlineData(V533, 220)]   // 49 rows, 10,780 cells
    [InlineData(V533, 61)]    // 179 rows, 10,919 cells: length field 65,522
    [InlineData(V533, 50)]    // 218 rows
    public void TheLargestRectThatFitsGoesOutWholeAndOneRowMoreIsClampedToIt(int port, int w)
    {
        const int y0 = 1;
        var dims = Content.Maps[Kugnae];
        int fit = Enumerable.Range(1, 255).Last(h => 2 + 6 + w * h * BytesPerCell(port) <= ushort.MaxValue);
        Assert.True(fit + 1 <= dims.Ys - y0, $"width {w}: the map edge, not the frame, would stop {fit + 1} rows");

        var (s, o, _) = OnKugnae(port);
        try
        {
            s.Receive(MapRequest(0, y0, w, fit));
            s.Receive(MapRequest(0, y0, w, fit + 1));

            var frames = MapFrames(o);
            Assert.Equal(2, frames.Count);
            foreach (var f in frames)
            {
                Assert.Equal(f.RealLength, f.LengthField);
                Assert.Equal((0, y0, w, fit), f.Header);
            }
            Assert.Equal(frames[0].Body, frames[1].Body);
        }
        finally { _fx.World.LeaveMap(s, Kugnae); }
    }

    /// <summary>
    /// <b>The viewport stream records the clamped rect, so a walk into the rows the clamp left out streams
    /// them.</b>
    ///
    /// <para><c>SendMapRect</c> tells the stream's tracker (<c>NoteStreamed</c>) what it sent, and
    /// <c>StreamViewport</c> pushes only what the tracker does not cover. A tracker that recorded the rect
    /// asked for would claim rows that never went out, and a player walking into them would see black. The
    /// walker stands on row 100 or below, so its 27x25 window lies wholly below every clamped Kugnae reply
    /// (74 rows on 4.95, 49 on 5.33), and takes one plain step east.</para>
    ///
    /// <para>Red on 0627024, on both clients: the tracker holds the whole 220x220 map, so the step streams
    /// nothing.</para>
    /// </summary>
    [Theory]
    [InlineData(V495)]
    [InlineData(V533)]
    public void AWalkAfterAClampedRequestStreamsTheRowsTheClampLeftOut(int port)
    {
        var (s, o, c, x, y) = WalkerBeforeAPlainStepEast(port);
        try
        {
            s.Receive(MapRequest(0, 0, 255, 255));
            o.Clear();

            s.Receive(Walk(East, x, y));

            Assert.Equal((Kugnae, (ushort)(x + 1), (ushort)y), (c.Map, s.PlayerX, s.PlayerY));   // a plain step
            var frames = MapFrames(o);
            Assert.Contains(frames, f => f.Header.X0 <= x + 1 && x + 1 < f.Header.X0 + f.Header.W
                                         && f.Header.Y0 <= y && y < f.Header.Y0 + f.Header.H);
            Assert.All(frames, f => Assert.Equal(f.RealLength, f.LengthField));
        }
        finally { _fx.World.LeaveMap(s, Kugnae); }
    }

    /// <summary>
    /// <b>The log line names the clamp.</b> An operator reading a "my terrain stopped halfway" report needs
    /// to see that the server sent fewer rows than were asked for.
    ///
    /// <para>Red on 0627024: its line says <c>-&gt; 220x220</c> and nothing about a clamp.</para>
    /// </summary>
    [Fact]
    public async Task TheLogLineNamesTheClamp()
    {
        var dims = Content.Maps[Kugnae];
        var (s, o, _) = OnKugnae(V495);
        try
        {
            using var tap = await ConsoleTap.AcquireAsync();
            s.Receive(MapRequest(0, 0, 255, 255));
            var (_, _, w, h) = Assert.Single(MapFrames(o)).Header;

            string needle = $"req 255x255 -> {w}x{h} cells={w * h} CLAMPED to one frame ({h} of {dims.Ys} rows)";
            Assert.Contains(needle, await tap.WaitForAsync(needle, TimeSpan.FromSeconds(10)), StringComparison.Ordinal);
        }
        finally { _fx.World.LeaveMap(s, Kugnae); }
    }

    /// <summary>A Kugnae player on row 100 or below, one plain step west of a tile it can walk onto: its own
    /// tile is walkable, and nothing refuses the step east (terrain, an object wall, a creature, a player) or
    /// turns it into something else (a portal). Creatures materialise when the map is entered, so the
    /// destination is checked again once the player stands there, and the next tile is tried if one did.</summary>
    private (Session, RecordingOutbound, Character, int X, int Y) WalkerBeforeAPlainStepEast(int port)
    {
        var map = MapData.For(Kugnae) ?? throw new XunitException("Kugnae has no server-side tile data");
        for (int y = 100; y < map.Ys - 13; y++)
        for (int x = 14; x < map.Xs - 14; x++)
        {
            if (map.Solid(x, y) || map.BlockedMove(x + 1, y, East)
                || Content.TryWarp(Kugnae, (ushort)(x + 1), (ushort)y, out _)) continue;
            var (s, o, c) = OnKugnae(port, x, y);
            if (_fx.World.MobAt(Kugnae, x + 1, y) is null && _fx.World.PeerAt(Kugnae, x + 1, y) is null)
                return (s, o, c, x, y);
            _fx.World.LeaveMap(s, Kugnae);
        }
        throw new XunitException("no plain step east on Kugnae at or below row 100");
    }
}
