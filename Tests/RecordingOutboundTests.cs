using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// #188. <see cref="RecordingOutbound"/> is what every end-to-end fact reads a player's screen back from, so
/// it has to be exact under the concurrency the race facts exist to exercise: two threads sending into the
/// same session. With a plain <c>List.Add</c> a lost or doubled frame could flip "told exactly once" either
/// way, and the assertion proved nothing about the code under test.
///
/// <para>These run no session and touch no World, so they sit outside the <c>world</c> collection.</para>
/// </summary>
public sealed class RecordingOutboundTests
{
    /// <summary>Per thread. Large enough that the two pumps overlap for many list growths, which is where an
    /// unguarded <c>List.Add</c> loses, duplicates or throws; small enough to finish in well under a second.
    /// </summary>
    private const int PerThread = 200_000;

    /// <summary>
    /// Two threads, released together by a barrier, each send <see cref="PerThread"/> frames into ONE
    /// recorder. Exactly 2N come back, N of each sender's, and nothing threw.
    ///
    /// <para><b>Falsification.</b> Red against the pre-#188 recorder (the bare <c>_frames.Add</c>, the live
    /// list handed out by <c>Frames</c>): the count comes back short, or a sender throws out of
    /// <c>List.Add</c> mid-resize. The report records how many runs were tried and how many went red.</para>
    /// </summary>
    [Fact]
    public void TwoThreadsSendingIntoOneRecorderLoseAndDuplicateNothing()
    {
        var recorder = new RecordingOutbound();
        byte[] fromA = { 0xA1 }, fromB = { 0xB2 };
        var faults = new List<Exception>();
        using var start = new Barrier(2);

        Thread Pump(byte[] frame) => new(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int i = 0; i < PerThread; i++) recorder.Send(frame);
            }
            catch (Exception e) { lock (faults) faults.Add(e); }
        });

        var a = Pump(fromA);
        var b = Pump(fromB);
        a.Start(); b.Start();
        a.Join(); b.Join();

        Assert.Empty(faults);
        var frames = recorder.Frames;
        Assert.Equal(2 * PerThread, frames.Count);
        Assert.Equal(PerThread, frames.Count(f => ReferenceEquals(f, fromA)));
        Assert.Equal(PerThread, frames.Count(f => ReferenceEquals(f, fromB)));
    }

    /// <summary>
    /// The read side of the same guarantee, and the reason <see cref="RecordingOutbound.Frames"/> is a
    /// snapshot rather than the live list: a test thread enumerating what a player has been told, while
    /// another thread is still telling them things, neither throws nor sees the list shrink.
    ///
    /// <para><b>Falsification.</b> Hand the live list back from <c>Frames</c> (the pre-#188 getter) and this
    /// goes red with "Collection was modified" out of the enumeration, even with <c>Send</c> locked.</para>
    /// </summary>
    [Fact]
    public void ReadingFramesWhileAnotherThreadSendsSeesAGrowingSnapshot()
    {
        var recorder = new RecordingOutbound();
        byte[] frame = { 0xC3 };
        Exception? senderFault = null;
        using var start = new Barrier(2);
        var sender = new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int i = 0; i < PerThread; i++) recorder.Send(frame);
            }
            catch (Exception e) { senderFault = e; }
        });
        sender.Start();
        start.SignalAndWait();

        int last = 0;
        while (sender.IsAlive)
        {
            int seen = 0;
            foreach (var f in recorder.Frames) { Assert.Same(frame, f); seen++; }
            Assert.True(seen >= last, $"a later read saw {seen} frames after an earlier one saw {last}");
            last = seen;
        }
        sender.Join();

        Assert.Null(senderFault);
        Assert.Equal(PerThread, recorder.Frames.Count);
    }
}
