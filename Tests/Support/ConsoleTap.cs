using System.Text;

namespace Tests.Support;

/// <summary>
/// The one way a test captures <c>Console.Out</c>, and the reason it is one way: <c>Console.Out</c> is a
/// single process-global slot, and the save/swap/restore dance every capturing test does is a read-modify-
/// write on it. Two classes in DIFFERENT xunit collections run concurrently, so their dances interleave and
/// the second one's restore puts back the writer IT saw — silently discarding the first one's redirect for
/// the rest of that test. From then on the first test's lines go to the real stdout, where its assertion
/// cannot see them, and it fails claiming the line was never written at all.
///
/// <para>That is not a hypothesis: fork CI run 35164315790 attempt 1 failed
/// <c>LoginOutboundTests.AFrameThatWaitsTooLongToReachTheSocketIsNamedOnTheLoginChannel</c> with "no SLOW
/// SEND line ... in three attempts" while all THREE of those SLOW SEND lines are in the job's own stdout,
/// printed by the code the test says never printed them. Retrying cannot help, because the console the
/// retries look at is no longer the test's.</para>
///
/// <para>So the tap is exclusive: <see cref="Acquire"/> takes a process-wide gate and holds it until the tap
/// is disposed. While a test owns the console no other test can swap it, which makes the redirect a fact for
/// the whole window rather than a race. Lines written by OTHER tests during that window land in this tap,
/// which is harmless — every caller looks for its own needle.</para>
///
/// <para>The gate itself is pinned by <c>Tests/ConsoleTapExclusionTests.cs</c>: drop it and the only thing
/// that fails is somebody else's capturing test, in another collection, on another day, blaming its own
/// production code — which is how it was found the first two times.</para>
///
/// <para>It is also a <see cref="TextWriter"/> over a locked <see cref="StringBuilder"/> rather than a
/// <c>StringWriter</c>, because <c>Log</c> writes from its own thread: reading a StringWriter's buffer while
/// that thread appends to it is a data race of its own. This shape was already in the suite, copied privately
/// into two test classes; it now lives here once.</para>
/// </summary>
internal sealed class ConsoleTap : TextWriter
{
    // Not a lock object: an async test can await the acquisition rather than block a pool thread on it.
    private static readonly SemaphoreSlim Exclusive = new(1, 1);

    /// <summary>Long enough that no honest capture window can hit it, short enough to fail as a deadlock
    /// report rather than as a suite that never ends.</summary>
    private static readonly TimeSpan AcquireBound = TimeSpan.FromSeconds(120);

    private readonly StringBuilder _text = new();
    private readonly TextWriter _prior;
    private int _released;

    private ConsoleTap(TextWriter prior) => _prior = prior;

    /// <summary>Take the console for this test. Dispose (a <c>using</c>) restores it and lets the next
    /// capturing test in.</summary>
    public static ConsoleTap Acquire()
    {
        if (!Exclusive.Wait(AcquireBound)) throw HeldTooLong();
        return Install();
    }

    /// <summary>The same, for an async test: waiting for the console must not park a pool thread that the
    /// test the console belongs to might need.</summary>
    public static async Task<ConsoleTap> AcquireAsync()
    {
        if (!await Exclusive.WaitAsync(AcquireBound)) throw HeldTooLong();
        return Install();
    }

    private static ConsoleTap Install()
    {
        // Exclusive, so this IS the process's real console writer and restoring it is exact.
        var tap = new ConsoleTap(Console.Out);
        Console.SetOut(tap);
        return tap;
    }

    private static TimeoutException HeldTooLong() =>
        new($"no test released the console tap within {AcquireBound.TotalSeconds:0}s; a capturing test is "
            + "holding it (a missing Dispose, or a capture that waits on something that never happens).");

    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) { lock (_text) _text.Append(value); }
    public override void Write(string? value) { lock (_text) _text.Append(value); }
    public override void Write(char[] buffer, int index, int count) { lock (_text) _text.Append(buffer, index, count); }

    /// <summary>Everything written to the console since this tap was installed.</summary>
    public string Text { get { lock (_text) return _text.ToString(); } }

    /// <summary>Everything written so far once <paramref name="needle"/> has appeared, or whatever was
    /// written when the deadline passed — the caller's assertion then names what was missing. The wait is
    /// for <c>Log</c>'s writer thread to reach the console, which is the only delay left once the tap is
    /// exclusive.</summary>
    public string WaitFor(string needle, TimeSpan deadline)
    {
        var until = DateTime.UtcNow + deadline;
        while (true)
        {
            string s = Text;
            if (s.Contains(needle, StringComparison.Ordinal) || DateTime.UtcNow >= until) return s;
            Thread.Sleep(20);
        }
    }

    /// <summary>The awaited form, for the same reason <see cref="AcquireAsync"/> exists.</summary>
    public async Task<string> WaitForAsync(string needle, TimeSpan deadline)
    {
        var until = DateTime.UtcNow + deadline;
        while (true)
        {
            string s = Text;
            if (s.Contains(needle, StringComparison.Ordinal) || DateTime.UtcNow >= until) return s;
            await Task.Delay(20);
        }
    }

    protected override void Dispose(bool disposing)
    {
        // Idempotent: TextWriter.Dispose can be reached twice (a using plus an explicit Close), and releasing
        // the gate twice would let two tests own the console at once — the exact bug this type exists for.
        if (disposing && Interlocked.Exchange(ref _released, 1) == 0)
        {
            Console.SetOut(_prior);
            Exclusive.Release();
        }
        base.Dispose(disposing);
    }
}
