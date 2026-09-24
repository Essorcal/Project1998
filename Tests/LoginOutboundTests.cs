using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Protocol.Tk495;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// The login channel's outbound queue, over real loopback sockets inside this process. Since the outbound
/// moved into <c>Shared</c> the type under test is the game's <c>TcpOutbound</c> driven with
/// <c>OutboundOptions.Login</c>; what these facts pin is the LOGIN profile's behaviour — its 64-frame
/// capacity, its per-write bound, its awaited drain and its slow-send warning.
///
/// <para>What these guard is a silent failure, not a crash: <c>LoginSession.Send</c> used to be a
/// synchronous <c>_stream.Write</c> under a lock, on the session's own read loop. A peer that stops reading
/// does not make it throw — it makes it WAIT, for as long as the kernel is willing to, with the read loop
/// stuck behind it and the log showing nothing at all. Every fact below is written so that restoring the old
/// shape makes it hang to its own timeout rather than fail an assert, which is exactly the point.</para>
/// </summary>
[Collection("log")]
public sealed class LoginOutboundTests
{
    // Short enough that a stalled write is observable in well under a second, long enough that a loaded
    // build agent cannot trip it by accident on a peer that IS reading.
    private const int WriteBoundMs = 300;

    /// <summary>Fact 1a, the whole reason this type exists: a peer that never reads does not hold the
    /// sender. Every Send returns promptly and the connection is dropped once the write bound is exceeded.
    ///
    /// <para>FALSIFIED by replacing the queue with the old synchronous write (<c>Stream.Write(frame, 0,
    /// frame.Length)</c> in place of <c>_out.Send</c>): the second Send never returns, and the test fails on
    /// the xunit timeout rather than on an assert — the red is a hang, because the bug is a hang.</para></summary>
    [Fact]
    public async Task APeerThatNeverReadsDoesNotBlockTheSenderAndIsDroppedAtTheWriteBound()
    {
        using var pair = await SocketPair.Connect(WriteBoundMs);
        // Fixed buffers on both ends so a few megabytes is enough to fill the path: Windows auto-tunes the
        // send buffer otherwise and absorbs megabytes on loopback without ever waiting for the peer. 64KB
        // rather than a kilobyte, for fact 3's reason — the peer below has to drain whatever the kernel
        // accepted before it can see EOF, and pushing megabytes through kilobyte buffers takes seconds on
        // Linux. The client is never read from until then: this is the "stopped reading" peer.
        pair.Server.SendBufferSize = 64 * 1024;
        pair.Client.ReceiveBufferSize = 64 * 1024;

        Task writer = pair.Outbound.RunWriterAsync();
        // Bulk, to fill the path rather than to park this particular write. Measured while fixing this: a
        // multi-megabyte WriteAsync to a peer that is not reading COMPLETES in about a millisecond on
        // Windows, because the kernel takes the bytes. What parks is a later write, once the path is
        // genuinely full — which is why the small frames below are part of the setup and not just noise.
        Assert.True(pair.Outbound.Send(new byte[4 * 1024 * 1024]));

        // The frames behind the bulk are what the read loop would be enqueuing while the peer sulks — and one
        // of them is the write that actually parks, which is what the bound below is measured against.
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 8; i++) pair.Outbound.Send(Status(0x0F, $"frame {i}"));
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 250,
                    $"enqueuing behind a stalled peer took {watch.ElapsedMilliseconds}ms; it must not wait at all");

        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal($"write stalled past {WriteBoundMs}ms", pair.Outbound.DropReason);
        // Dropped, not merely stalled. Deliberately NOT asserted by counting delivered bytes: once the peer
        // starts reading again the loopback stack happily flushes whatever it had already accepted, so a
        // byte count measures Windows, not us. What is ours is that the connection ENDS — the peer reaches
        // EOF instead of the session hanging on it forever, and further sends are refused.
        Assert.False(pair.Outbound.Send(Status(0x0F, "after the drop")));
        // ReadToEof's own 10s deadline IS the assertion: it throws if the connection never ends, which is the
        // red for a peer that was not dropped. Deliberately no tighter sub-deadline on top of it — the peer
        // must first drain whatever the kernel accepted before the drop, and how long that takes is the
        // runner's property, not ours. A 5s one here is what made this fact red on upstream CI at d6a48b4
        // ("the dropped peer took 6071ms to see the connection end") while the same commit passed on the fork.
        await ReadToEof(pair.Client.GetStream());
    }

    /// <summary>Fact 1b, the other bound. With a peer that is not reading and no write bound in play, the
    /// queue itself is finite: once <c>OutboundOptions.Login</c>'s capacity of frames are outstanding, Send refuses, and
    /// refusing is what the session turns into a drop. Neither bound relies on the other.
    ///
    /// <para>FALSIFIED by widening the channel to unbounded: Send never returns false and the assert on the
    /// refusal fails.</para></summary>
    [Fact]
    public async Task TheQueueRefusesFramesOnceCapacityIsReached()
    {
        // A generous write bound so this fact is about the queue and nothing else.
        using var pair = await SocketPair.Connect(60_000);

        // The writer is NOT started yet, and that is the whole trick: with nothing dequeuing, the count below
        // is the queue's own bound rather than a race against the thread pool. The first shape of this fact
        // started the writer and tried to park it with a four-megabyte frame; on this platform that write
        // completes in about a millisecond, so the writer went on dequeuing during the fill and `accepted`
        // came out at 65 on any run that was not under full-suite load. Nothing here depends on timing.
        int capacity = pair.Outbound.Capacity;
        Assert.Equal(64, capacity);   // the login profile's number, not the game's 2048
        int accepted = 0;
        var watch = Stopwatch.StartNew();
        // Sixteen more than the capacity: the queue takes exactly Capacity of them and refuses every one
        // after that, which is what the session turns into a drop.
        for (int i = 0; i < capacity + 16; i++)
            if (pair.Outbound.Send(Status(0x0F, $"frame {i}"))) accepted++;
        watch.Stop();

        Assert.Equal(capacity, accepted);
        Assert.Equal(capacity, pair.Outbound.QueueDepth);   // accepted AND still held, none lost
        Assert.True(watch.ElapsedMilliseconds < 250,
                    $"filling and overflowing the queue took {watch.ElapsedMilliseconds}ms; no Send may wait");

        // Only now does anything drain: the writer picks the backlog up and the teardown closes behind it.
        Task writer = pair.Outbound.RunWriterAsync();
        pair.Outbound.Close();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Fact 2: a peer that reads normally gets every frame, whole and in order, byte for byte
    /// against the same <c>TkPacket.Build</c> bodies. This is the "same bytes, same order" half of the
    /// change — the single-reader channel is what replaced the send lock, and a second writer or a reordered
    /// queue would show up here as interleaved or transposed frames.
    ///
    /// <para>The peer reads every expected byte BEFORE the teardown, and that order is the fix for a flake.
    /// This fact used to call <c>CloseAfterDrainAsync</c> first and read to EOF after, which made the drain's
    /// one-second deadline decide what arrived: the writer is a thread-pool task, the deadline is a pool timer,
    /// and in a starved pool the writer had not started its first write when the deadline closed the socket.
    /// The failures read "Actual: []" with the writer's own line <c>login writer stopped:
    /// ObjectDisposedException</c> beside it (upstream master <c>36bac49</c>, run 35906929749 attempt 1, on
    /// the suite's first seconds; also the #275 send-counters worker's local run, the first Debug suite in
    /// the gate-move-broadcast report, and finding 3 of the #57 slice 2 report). Reproduced by parking every
    /// pool thread before each run (<c>DOTNET_PROCESSOR_COUNT=4</c>): the old shape failed 7 of 20 and 8 of
    /// 100. Fourteen of those runs received nothing, and one received only the welcome, cut off at a frame
    /// boundary by the same close. With the pool idle it failed 0 of 500. This shape failed 0 of 120
    /// starved. Whether the drain waits is fact 3's claim, not this one's, so nothing here has a deadline
    /// shorter than the reads' own 10s.</para>
    ///
    /// <para>FALSIFIED by reversing the enqueue order in the loop below: the comparison fails on the first
    /// transposed frame. Also checked against this shape by breaking the writer's loop in production code:
    /// skipping the fifth frame, writing the fifth frame one byte short, and writing the fifth frame after
    /// the sixth each fail the first comparison. The first two fail at the read's 10s deadline, because the
    /// peer is still waiting for bytes that never come, and the third fails at once.</para></summary>
    [Fact]
    public async Task ANormalPeerReceivesEveryFrameWholeAndInOrder()
    {
        using var pair = await SocketPair.Connect(WriteBoundMs);
        Task writer = pair.Outbound.RunWriterAsync();

        var expected = new List<byte>();
        // The welcome, then the shapes a real login conversation actually sends: the availability OK, a
        // handful of status lines, and the redirect last.
        byte[] welcome = Welcome.Bytes;
        Assert.True(pair.Outbound.Send(welcome));
        expected.AddRange(welcome);
        for (int i = 0; i < 24; i++)
        {
            byte[] frame = Status(i % 2 == 0 ? (byte)0x0F : (byte)0x00, $"status line {i}");
            Assert.True(pair.Outbound.Send(frame));
            expected.AddRange(frame);
        }
        byte[] redirect = Redirect();
        Assert.True(pair.Outbound.Send(redirect));
        expected.AddRange(redirect);

        // The peer reads everything BEFORE the teardown starts, so no deadline decides what arrives: the
        // comparison is of the bytes the writer put on the wire, not of how far it got inside the drain's
        // one-second bound (see the summary). A dropped or torn frame leaves this short, and it ends at EOF
        // (the writer closed the socket) or at its own 10s deadline, returning what it has either way.
        Assert.Equal(expected.ToArray(), await ReadUpTo(pair.Client.GetStream(), expected.Count));

        await pair.Outbound.CloseAfterDrainAsync();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        // Nothing after the last frame: an extra write at the end shows up here.
        Assert.Empty(await ReadToEof(pair.Client.GetStream()));
        Assert.Null(pair.Outbound.DropReason);
    }

    /// <summary>Fact 3, the drain: the redirect is the last frame a successful login sends, and the client
    /// learns the game host and port from exactly those bytes. A peer that is SLOW but does eventually read
    /// still gets that final frame, and the socket closes only behind it.
    ///
    /// <para>The peer here is a window that stays shut for <c>StallMs</c> and then opens, with enough bulk
    /// ahead of the redirect to park the writer. The bulk is stand-in volume rather than a real login frame:
    /// what is being measured is the peer's window, not the opcode.</para>
    ///
    /// <para>What is asserted is that the teardown WAITS for the last queued frame rather than racing it —
    /// and not that a bare close would lose bytes, which on this platform it does not. Measured while writing
    /// this: Windows completes a pending overlapped send through a graceful <c>closesocket</c>, so every byte
    /// still arrived with the drain removed and with the socket closed mid-write in fact 1a. A byte count
    /// would therefore assert Windows' behaviour, not ours. The drain is what makes the session's
    /// <c>-- CLOSE</c> line mean the bytes are gone, and it is the guarantee on any platform or path that
    /// does NOT flush — an RST, a peer that closed first, SO_LINGER 0.</para>
    ///
    /// <para>What separates "the drain finished" from "the drain gave up at <c>DrainTimeoutMs</c> and closed
    /// anyway" is WHEN THE WRITER FINISHED, measured at the writer, not when this test got control back. The
    /// two used to be the same number, and they are not under load: on 2026-09-22 this fact failed three times
    /// on the old "the drain took Xms and hit its own bound" wall clock (upstream runs 35734743035 at 1259ms
    /// and 35753469092 at 2245ms, and once on a local Windows suite at 1506ms), each while the suite's
    /// content-loading classes were starting beside it. Reproduced with the teardown timed from both ends,
    /// the slow readings were of two kinds: the writer finished in ~150ms and the teardown only got back to
    /// this test a second later, which is scheduling and says nothing about the drain; or the peer's own
    /// <c>Task.Delay</c> and reads, queued behind the same load, left the writer parked until the drain
    /// genuinely timed out (run 35734743035 logged the writer's send being aborted by that close). So the
    /// bound is asserted on the writer's own completion time, and the peer no longer resumes on xunit's
    /// synchronization context, which is one more queue its stall and its reads were waiting in.</para>
    ///
    /// <para>That still failed on the CI runner, on the suite's first seconds, and the log says why:
    /// <c>SLOW SEND ... write 1886ms</c>, the writer's own socket write taking nearly two seconds while the
    /// thread pool was starved. The writer's socket completions are pool work items, the drain's deadline is
    /// a pool timer, and the writer is production code the test cannot take off the pool (a peer on a thread
    /// of its own was tried, and on Linux its catch-up stalled instead). Starved deliberately, by parking
    /// every pool thread just before it ran, the original fact failed 19 of 20. So each attempt runs beside
    /// <see cref="PoolLag"/>, which measures how long a pool work item waits to start, and a drain that misses
    /// its deadline while that wait reached the production watchdog's starvation line is not held against
    /// the drain: the attempt is repeated, up to <c>Attempts</c> times. A drain that misses its deadline
    /// while the pool was answering fails at once.</para>
    ///
    /// <para>FALSIFIED by replacing <c>CloseAfterDrainAsync</c>'s body with <c>await Task.Yield(); Close();</c>:
    /// it returns in about a millisecond, while the writer is still parked mid-bulk waiting on the peer, and
    /// the elapsed-time assert fails on the first attempt. A drain that genuinely times out (the peer's stall
    /// made longer than <c>DrainTimeoutMs</c>) fails on the first attempt too, on the "the drain timed out"
    /// assert, which names the timeout and the pool's worst wait. Not caught: a no-wait teardown that load
    /// delays past half the stall, which the old fact did not catch either. An "is the writer already complete
    /// when the teardown returns" assert would catch it, and was tried and dropped: the drain's continuation
    /// can run a hair before the writer's task completes, and did so for a correct drain in 5 of about 1,750
    /// runs on the Linux runner.</para></summary>
    [Fact]
    public async Task TheRedirectsLastFrameReachesASlowButReadingPeerBeforeTheClose()
    {
        const int Attempts = 3;
        var starved = new List<string>();
        for (int attempt = 1; ; attempt++)
        {
            DrainReading run = await DrainIntoASlowPeer();
            if (run.WriterMs < TcpOutbound.DrainTimeoutMs - DeadlineSlackMs)
            {
                Assert.Equal(run.Expected, run.Received);
                // ...and the redirect is the LAST thing on the wire, with the socket closed behind it.
                Assert.Equal(run.Redirect, run.Received[^run.Redirect.Length..]);
                Assert.Null(run.DropReason);
                return;
            }
            // A drain that gives up still closes, and whether the bytes then arrive anyway is the platform's
            // business, so the writer's finishing time is the one reading that tells a finished drain from a
            // timed-out one: had the writer not finished by the deadline, the close that ended it was the
            // deadline's, or would have been had the deadline's own timer not been waiting on the pool too.
            string reading = $"the writer finished at {run.WriterMs}ms, the teardown returned at "
                           + $"{run.ReturnedMs}ms, a pool work item waited up to {run.WorstPoolLagMs}ms to start";
            Assert.True(run.WorstPoolLagMs >= StarvedPoolLagMs,
                        $"the drain timed out: the writer was still sending at its {TcpOutbound.DrainTimeoutMs}ms "
                        + $"deadline, less {DeadlineSlackMs}ms of timer slack ({reading}), so the socket was closed "
                        + "by the drain's deadline, not behind the last frame");
            starved.Add($"attempt {attempt}: {reading}");
            Assert.True(attempt < Attempts,
                        $"the drain missed its {TcpOutbound.DrainTimeoutMs}ms deadline on all {Attempts} attempts, "
                        + "each with the thread pool starved, so none of them measured the drain: "
                        + string.Join("; ", starved));
        }
    }

    // The slack is the deadline timer's own resolution: with the stall forced past the deadline, the timed-out
    // teardown returned at 996ms by the attempt's stopwatch and the writer it cut off finished at 1001ms.
    private const int DeadlineSlackMs = 50;

    // P1998_POOL_LAG_MS's default: the production watchdog's own line between a busy pool and a starved one.
    // A healthy pool starts a work item in well under a millisecond.
    private const int StarvedPoolLagMs = 100;

    private sealed record DrainReading(long ReturnedMs, long WriterMs, long WorstPoolLagMs, byte[] Expected,
                                       byte[] Received, byte[] Redirect, string? DropReason);

    /// <summary>One attempt at fact 3: a parked writer, a peer that opens its window after <c>StallMs</c>,
    /// and the awaited teardown, timed from both ends while <see cref="PoolLag"/> watches the pool.</summary>
    private static async Task<DrainReading> DrainIntoASlowPeer()
    {
        const int StallMs = 150;        // shut window, then open — comfortably inside the 1s drain bound
        // Enough to park the writer several times over (the two 64KB buffers below are all the kernel can
        // hold), and small enough that the peer's catch-up read is not itself a race against the drain's
        // one-second bound. It was four megabytes, which is 4MB of catch-up through 64KB buffers on a
        // shared Linux runner: fork run 34552742122 failed here with "the drain took 1118ms and hit its own
        // bound". Same mis-sizing as fact 1a's, one fact further along. The 2026-09-22 failures were not
        // sizing (the catch-up is a few milliseconds when the peer is scheduled); see the summary above.
        const int BulkBytes = 512 * 1024;

        // A generous write bound: this fact is about the drain, and the peer's deliberate stall must not be
        // mistaken for the stalled-write drop that fact 1a covers.
        using var pair = await SocketPair.Connect(5_000);
        // Setting the socket buffers at all is what makes the sender park: Windows auto-tunes the send
        // buffer otherwise and will absorb megabytes on loopback without ever waiting for the peer (an 8MB
        // version of this fact still passed with the drain removed, for exactly that reason). 64KB, the same
        // size fact 1a uses, because a bound measured in a second cannot also be waiting on kilobyte buffers
        // to pass megabytes on Linux — which is how CI first failed this.
        pair.Server.SendBufferSize = 64 * 1024;
        pair.Client.ReceiveBufferSize = 64 * 1024;
        var expected = new List<byte>();
        // Opaque bulk, not a framed packet: TkPacket's length field is 16 bits and this is deliberately
        // larger than anything the kernel will absorb in one go. Built BEFORE the peer's stall clock starts,
        // so that filling the buffer is not silently charged against the stall.
        byte[] bulk = Enumerable.Range(0, BulkBytes).Select(i => (byte)i).ToArray();

        Task writer = pair.Outbound.RunWriterAsync();

        Assert.True(pair.Outbound.Send(bulk));
        expected.AddRange(bulk);
        byte[] redirect = Redirect();
        Assert.True(pair.Outbound.Send(redirect));
        expected.AddRange(redirect);

        using var poolLag = new PoolLag();
        // The writer cannot possibly have finished before the peer's window opens, so a teardown that returns
        // sooner than that is one that did not wait for the redirect. The peer's stall starts HERE, immediately
        // before the teardown's clock, so a test descheduled between the two cannot open the window early and
        // make a correct teardown look like one that did not wait. Nothing is reading until then either way.
        Task<byte[]> reader = ReadAfterStallToEof(pair.Client.GetStream(), StallMs);
        var drain = Stopwatch.StartNew();
        // Stamped by the writer's own completion, inline on the thread that completes it, so the reading is
        // the writer's and not however long this test then waits to be scheduled.
        Task<long> writerFinishedAtMs = writer.ContinueWith(_ => drain.ElapsedMilliseconds, CancellationToken.None,
                                                            TaskContinuationOptions.ExecuteSynchronously,
                                                            TaskScheduler.Default);
        await pair.Outbound.CloseAfterDrainAsync();
        long returnedMs = drain.ElapsedMilliseconds;
        // Half the stall, not all of it: a margin for the delay's timer resolution, and the distinction being
        // drawn is between ~1ms (did not wait) and ~150ms (waited).
        Assert.True(returnedMs >= StallMs / 2,
                    $"the teardown returned after {returnedMs}ms without waiting for the "
                    + $"queued frames; the peer had not even started reading until {StallMs}ms");
        long writerMs = await writerFinishedAtMs.WaitAsync(TimeSpan.FromSeconds(10));
        long worstPoolLagMs = poolLag.WorstMs;

        byte[] received = await reader.WaitAsync(TimeSpan.FromSeconds(10));
        return new DrainReading(returnedMs, writerMs, worstPoolLagMs, expected.ToArray(), received, redirect,
                                pair.Outbound.DropReason);
    }

    /// <summary>Fact 4: Close is idempotent, it completes the writer exactly once, and a Send afterwards is
    /// refused rather than throwing — the session's teardown races itself (the read loop's finally, the
    /// writer's, and a refused Send all reach Close), and a throw on any of those paths would surface as a
    /// bogus "error:" line on an ordinary logout.
    ///
    /// <para>FALSIFIED by restoring the old synchronous send (<c>Stream.Write(frame, 0, frame.Length)</c>):
    /// the send after the close throws ObjectDisposedException instead of being refused, which on the real
    /// session is the bogus <c>error:</c> line an ordinary logout used to be capable of printing.</para>
    ///
    /// <para>Two things this does NOT prove, checked rather than assumed: removing the
    /// <c>Interlocked.Exchange</c> gate in <c>Close</c>, and swapping <c>TrySetResult</c> for
    /// <c>SetResult</c>, both leave it green — .NET's socket Dispose is already idempotent and the writer's
    /// finally only runs once. The gate and the Try are cheap insurance, not load-bearing here.</para></summary>
    [Fact]
    public async Task CloseIsIdempotentCompletesTheWriterAndRefusesLaterSends()
    {
        using var pair = await SocketPair.Connect(WriteBoundMs);
        Task writer = pair.Outbound.RunWriterAsync();
        Assert.True(pair.Outbound.Send(Status(0x0F, "before the close")));

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => pair.Outbound.Close())));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => pair.Outbound.CloseAfterDrainAsync()));

        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pair.Outbound.Send(Status(0x0F, "after the close")));   // refused, not thrown
    }

    /// <summary>Fact 5, and the one thing this channel did NOT have before the outbound became shared: the
    /// game's <c>SLOW SEND</c> early warning. Until now the first line the login log printed about a
    /// struggling peer was the line that DROPPED it — there was no "this link is bad" between "fine" and
    /// "gone". The shared writer's watchdog is not gated on which profile it is running, and
    /// <c>OutboundOptions.Login</c> carries the same threshold as the game's, so the login channel gets it for
    /// free.
    ///
    /// <para>The stall here is a frame left sitting in the queue with nothing dequeuing it, not a stalled
    /// peer: the watchdog measures enqueue-to-pickup as well as time inside the write, and the queued half is
    /// the one that can be produced exactly, with no socket buffer sizing and no race against the thread pool
    /// (fact 1b's lesson). One small frame needs no reader at all to complete its write.</para>
    ///
    /// <para>FALSIFIED by setting <c>SlowSendMs = 0</c> in the profile this fact constructs (which is what the
    /// channel had before this change): the writer takes the <c>continue</c> at the top of the watchdog, no
    /// line is ever printed, and the assertion fails after its deadline.</para>
    ///
    /// <para><c>Log</c> writes on its own thread and only reaches this tap through <c>Console.Out</c>, which
    /// is process-global — the reason this class is in the <c>log</c> collection, as
    /// <c>SharedListenerTests</c> is. The collection is not enough on its own, because a class in ANOTHER
    /// collection runs concurrently and its own save/restore of <c>Console.Out</c> discards this one's
    /// redirect for good; that is how fork CI run 35164315790 attempt 1 failed here with all three SLOW SEND
    /// lines present in the job's stdout. <see cref="ConsoleTap"/> is the fix — while this test holds it, no
    /// other test can swap the console — and it is why one window is now enough where three were not.</para>
    ///
    /// <para>The frame is also read off the peer before the teardown, rather than trusting
    /// <c>CloseAfterDrainAsync</c> to outlast the writer's first scheduling: the drain gives up after
    /// <c>DrainTimeoutMs</c> and closes the socket, and a writer that reaches its <c>WriteAsync</c> after
    /// that writes into a closed socket, logs "writer stopped" and never measures the queued time at all. The
    /// received bytes are the proof that the write this fact is about actually happened.</para></summary>
    [Fact]
    public async Task AFrameThatWaitsTooLongToReachTheSocketIsNamedOnTheLoginChannel()
    {
        // The production threshold, and the actual claim: the login channel's early warning is the game's
        // number, not a login-only one. P1998_SLOW_SEND_MS tunes both.
        Assert.Equal(OutboundOptions.Game.SlowSendMs, OutboundOptions.Login.SlowSendMs);

        const int SlowSendMs = 50;
        const int HoldMs = 120;      // > SlowSendMs by enough that no scheduler jitter can decide the outcome
        using var captured = await ConsoleTap.AcquireAsync();

        using var pair = await SocketPair.Connect(60_000, SlowSendMs);
        byte[] frame = Status(0x0F, "the frame that waited");
        Assert.True(pair.Outbound.Send(frame));
        await Task.Delay(HoldMs);    // nothing is dequeuing: the queued time IS this delay
        Task writer = pair.Outbound.RunWriterAsync();
        // The peer reads the frame: the write completed, so the watchdog has measured its queued time and
        // the warning is on Log's queue. Everything after this is waiting for that line, not racing for it.
        Assert.Equal(frame, await ReadExactly(pair.Client.GetStream(), frame.Length));
        await pair.Outbound.CloseAfterDrainAsync();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(pair.Outbound.DropReason);   // warned about, never dropped: nothing stalled

        string wanted = $"SLOW SEND {pair.Outbound.Remote}: queued ";
        string log = await captured.WaitForAsync(wanted, TimeSpan.FromSeconds(3));
        Assert.True(log.Contains(wanted, StringComparison.Ordinal),
                    $"no SLOW SEND line for a frame that waited {HoldMs}ms against a {SlowSendMs}ms "
                    + $"threshold; the console carried:\n{log}");
    }

    /// <summary>The login channel's one server-&gt;client frame, built exactly as
    /// <c>LoginSession.SendStatus</c> builds it.</summary>
    private static byte[] Status(byte code, string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { code, (byte)t.Length };
        body.AddRange(t);
        body.Add(0);
        return TkPacket.Build(0x02, 0x02, TkCrypt.Crypt(body.ToArray(), 0x02, TkCrypt.LoginKey));
    }

    private static byte[] Redirect() =>
        LoginRedirect.Build(new byte[] { 127, 0, 0, 1 }, 2005, "drainprobe", new byte[] { 0, 1, 18, 17, 0 });

    /// <summary>Exactly <paramref name="count"/> bytes from the peer, or a failure inside the deadline — the
    /// caller uses it as evidence that the writer really put those bytes on the wire.</summary>
    private static async Task<byte[]> ReadExactly(NetworkStream stream, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    /// <summary>Up to <paramref name="count"/> bytes from the peer: stops early at EOF, at a reset, or at its
    /// 10s deadline, and returns whatever arrived rather than throwing, so a short delivery fails the caller's
    /// comparison with the bytes in hand instead of an exception that shows none of them.</summary>
    private static async Task<byte[]> ReadUpTo(NetworkStream stream, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[count];
        int got = 0;
        try
        {
            while (got < count)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(got), timeout.Token).ConfigureAwait(false);
                if (n == 0) break;
                got += n;
            }
        }
        catch (OperationCanceledException) { /* the deadline: compare what arrived */ }
        catch (IOException) { /* a reset: compare what arrived */ }
        return buffer[..got];
    }

    private static async Task<byte[]> ReadToEof(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var received = new MemoryStream();
        try { await stream.CopyToAsync(received, timeout.Token); }
        catch (IOException) { /* a dropped peer sees a reset, not a graceful EOF */ }
        return received.ToArray();
    }

    /// <summary>How long a thread-pool work item waits to start, sampled every 10ms from a thread of its own
    /// (a probe for pool starvation cannot itself live on the pool), worst reading kept. The same measurement
    /// as the production watchdog's pool-latency probe, taken continuously for the length of one attempt.</summary>
    private sealed class PoolLag : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _worstMs;
        private long _queuedAt;   // Stopwatch timestamp of the sample still waiting to start, 0 if none

        public PoolLag()
        {
            _thread = new Thread(Sample) { IsBackground = true, Name = "pool lag probe" };
            _thread.Start();
        }

        /// <summary>The worst wait so far, INCLUDING a sample that has not started yet: in a pool starved for
        /// the whole attempt, no sample ever completes, and that is the worst reading of all.</summary>
        public long WorstMs
        {
            get
            {
                long queuedAt = Interlocked.Read(ref _queuedAt);
                long pending = queuedAt == 0 ? 0 : (long)Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
                return Math.Max(Interlocked.Read(ref _worstMs), pending);
            }
        }

        private void Sample()
        {
            while (!_stop)
            {
                var started = new ManualResetEventSlim();   // not disposed: a late work item may still Set it
                long queuedAt = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _queuedAt, queuedAt);
                ThreadPool.UnsafeQueueUserWorkItem(static e => ((ManualResetEventSlim)e!).Set(), started);
                while (!started.Wait(50) && !_stop) { }
                if (!started.IsSet) return;   // stopped while the pool never got to it; WorstMs already saw it
                long ms = (long)Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
                Interlocked.Exchange(ref _queuedAt, 0);
                if (ms > Interlocked.Read(ref _worstMs)) Interlocked.Exchange(ref _worstMs, ms);
                Thread.Sleep(10);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>A peer whose receive window stays shut for <paramref name="stallMs"/> and then opens. Reading
    /// nothing at all is what parks a sender; reading eventually is what makes this a slow peer rather than a
    /// dead one. Every await resumes on the thread pool, never on the caller's context: under xunit that
    /// context is a handful of worker threads the rest of the suite is also queued on.</summary>
    private static async Task<byte[]> ReadAfterStallToEof(NetworkStream stream, int stallMs)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Delay(stallMs, timeout.Token).ConfigureAwait(false);
        using var received = new MemoryStream();
        await stream.CopyToAsync(received, timeout.Token).ConfigureAwait(false);
        return received.ToArray();
    }

    private sealed class SocketPair : IDisposable
    {
        public TcpClient Client { get; }
        public TcpClient Server { get; }
        public TcpOutbound Outbound { get; }

        private SocketPair(TcpClient client, TcpClient server, int writeTimeoutMs, int? slowSendMs)
        {
            Client = client;
            Server = server;
            // The login profile with a short per-write bound, so a stalled peer is observable in
            // milliseconds rather than the ten seconds production waits. Only fact 5 overrides the
            // slow-send threshold; every other fact runs on the production one.
            var options = OutboundOptions.Login with { WriteTimeoutMs = writeTimeoutMs };
            if (slowSendMs is int ss) options = options with { SlowSendMs = ss };
            Outbound = new TcpOutbound(server, options, remote: "127.0.0.1:0 (test)");
        }

        public static async Task<SocketPair> Connect(int writeTimeoutMs, int? slowSendMs = null)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return new SocketPair(client, await listener.AcceptTcpClientAsync(), writeTimeoutMs, slowSendMs);
        }

        public void Dispose()
        {
            Outbound.Close();
            Client.Dispose();
            Server.Dispose();
        }
    }
}
