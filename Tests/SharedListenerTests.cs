using System.Net;
using System.Net.Sockets;
using Shared;
using Xunit;

namespace Tests;

/// <summary>The accept path both processes now share (<see cref="TkAcceptor"/>), exercised over real
/// loopback sockets: what the delegate is handed, and that the admission slot a connection takes is given
/// back exactly once however that connection ends.
///
/// <para><b>Why real sockets.</b> The three properties worth pinning here are all socket-level and all
/// silent when they break. A missing <c>NoDelay</c> is inaudible in a unit test and audible in the game as
/// flammed cast sounds. A leaked <see cref="ConnGuard"/> slot does not throw — it just lowers the effective
/// connection ceiling until the process is restarted, and the log says only "global cap N" as if the server
/// were genuinely full. Both survive any test that stubs the socket out.</para>
///
/// <para><b>The proxied path is not covered here.</b> <c>ProxyProtocol.Enabled</c> is read from the
/// environment at type initialisation, which is already frozen by the time any test runs, so there is no
/// seam that reaches that branch without editing <c>Shared/ProxyProtocol.cs</c>. What holds it is the move:
/// <c>ListenAsync</c>'s proxied branch and all of <c>RunProxiedAsync</c> came across from
/// <c>Server/Net.cs</c> with zero changed lines, and <c>ProxyProtocolTests</c> covers the header parse and
/// the allow-list arithmetic on their own. Each fact below asserts the flag is off, so none of them can
/// quietly end up measuring the other branch.</para>
///
/// <para><b>Left running.</b> A production acceptor listens until the process exits — there is no stop, by
/// design — so each fact leaves one listening socket behind for the rest of the test run. That is three
/// loopback ports out of the 8000 block, and the reason each fact claims its own.</para></summary>
public class SharedListenerTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    /// <summary>Bind loopback, not 0.0.0.0. <see cref="NetBind"/> resolves P1998_BIND ONCE, on first touch,
    /// so this has to run before the first <see cref="TkAcceptor"/> is constructed — hence a static field
    /// initializer rather than per-fact setup. Nothing else in this assembly touches NetBind; if that ever
    /// changes, the assert at the top of every fact goes red instead of the suite quietly putting listening
    /// sockets on every interface of whichever box is running it.</summary>
    private static readonly bool BoundToLoopback = ForceLoopbackBind();

    private static bool ForceLoopbackBind()
    {
        Environment.SetEnvironmentVariable("P1998_BIND", "127.0.0.1");
        return IPAddress.IsLoopback(NetBind.Address);
    }

    private static int _nextPort = 8000;   // the block this assignment holds

    /// <summary>The connection reaches the delegate: the accepted socket for THIS caller, the port it came
    /// in on, a null realIp (the un-proxied path resolves no forwarded address), and NoDelay already set —
    /// set by the acceptor, before any session can see the socket.
    /// <para>Falsification: delete <c>client.NoDelay = true;</c> from <see cref="TkAcceptor"/> and the
    /// NoDelay assert fails; pass <c>peer</c> instead of <c>null</c> as the last argument of the un-proxied
    /// <c>RunAndReleaseAsync</c> call and the realIp assert fails.</para></summary>
    [Fact]
    public async Task An_admitted_connection_reaches_the_delegate_with_NoDelay_set()
    {
        Assert.True(BoundToLoopback, "P1998_BIND was already resolved by something else — see ForceLoopbackBind");
        Assert.False(ProxyProtocol.Enabled, "P1998_TRUST_PROXY is set; this fact covers the un-proxied path");

        int port = ClaimPort();
        var reached = new TaskCompletionSource<(TcpClient Client, int Port, IPAddress? RealIp)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var guard = new ConnGuard(globalMax: 4, perIpMax: 4, rateMax: 1000, rateWindowMs: 10_000);
        Start(port, guard, (client, p, realIp) => { reached.TrySetResult((client, p, realIp)); return finish.Task; });

        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Bounded);
        int callerPort = ((IPEndPoint)caller.Client.LocalEndPoint!).Port;

        var got = await reached.Task.WaitAsync(Bounded);

        Assert.Equal(port, got.Port);
        Assert.Null(got.RealIp);
        Assert.True(got.Client.NoDelay, "the accepted socket reached the session with Nagle still on");
        // The accepted socket is the other end of THIS connection, not merely some socket.
        Assert.Equal(callerPort, ((IPEndPoint)got.Client.Client.RemoteEndPoint!).Port);
        Assert.Equal(1, guard.Total);

        finish.SetResult();
        await WaitForTotal(guard, 0);
    }

    /// <summary>Load-shedding, end to end: with a global cap of one, a second connection arriving while the
    /// first is still in its session is closed without a byte — the caller sees EOF — and the cap lifts again
    /// only when the first session ends, which is the release this whole class is about.
    /// <para>Falsification: replace <c>_guard.TryAdmit(...)</c>'s result with <c>true</c> in
    /// <see cref="TkAcceptor"/> and the second connection is admitted, so the EOF read hangs until the
    /// bounded wait fails it. Remove the <c>finally { _guard.Release(ip); }</c> and the final
    /// <c>WaitForTotal(guard, 0)</c> times out.</para></summary>
    [Fact]
    public async Task A_second_connection_past_the_global_cap_is_closed_and_the_slot_comes_back()
    {
        Assert.True(BoundToLoopback, "P1998_BIND was already resolved by something else — see ForceLoopbackBind");
        Assert.False(ProxyProtocol.Enabled, "P1998_TRUST_PROXY is set; this fact covers the un-proxied path");

        int port = ClaimPort();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int admitted = 0;

        var guard = new ConnGuard(globalMax: 1, perIpMax: 8, rateMax: 1000, rateWindowMs: 10_000);
        Start(port, guard, (_, _, _) =>
        {
            Interlocked.Increment(ref admitted);
            reached.TrySetResult();
            return finish.Task;
        });

        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Bounded);
        await reached.Task.WaitAsync(Bounded);
        Assert.Equal(1, guard.Total);

        // The second connect SUCCEEDS at the TCP level — the kernel completes the handshake from the backlog
        // before the accept loop ever looks at it. The rejection is what happens next: the guard refuses the
        // slot and the acceptor closes the socket, so the first read returns 0 rather than data.
        using (var second = new TcpClient())
        {
            await second.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Bounded);
            var buf = new byte[1];
            int n = await second.GetStream().ReadAsync(buf).AsTask().WaitAsync(Bounded);
            Assert.Equal(0, n);
        }

        Assert.Equal(1, admitted);      // the refused connection never reached a session
        Assert.Equal(1, guard.Total);   // and never took a slot

        finish.SetResult();
        await WaitForTotal(guard, 0);   // the first session ends -> the cap lifts
    }

    /// <summary>A session that throws still gives its slot back. This is the failure the try/finally exists
    /// for: without it a run of session-teardown faults would ratchet the live count up until the process
    /// hit its global cap and refused everybody, logging "global cap N" as though the server were full.
    /// <para>The cap is one, so the second connection being admitted at all IS the proof the first was
    /// released. Falsification: move <c>_guard.Release(ip)</c> out of the <c>finally</c> and onto the end of
    /// the <c>try</c> in <see cref="TkAcceptor"/> — the throwing session skips it, and both the
    /// <c>WaitForTotal</c> and the second connection's arrival time out.</para></summary>
    [Fact]
    public async Task A_session_that_throws_still_releases_its_slot()
    {
        Assert.True(BoundToLoopback, "P1998_BIND was already resolved by something else — see ForceLoopbackBind");
        Assert.False(ProxyProtocol.Enabled, "P1998_TRUST_PROXY is set; this fact covers the un-proxied path");

        int port = ClaimPort();
        var secondReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        var guard = new ConnGuard(globalMax: 1, perIpMax: 8, rateMax: 1000, rateWindowMs: 10_000);
        Start(port, guard, (_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("deliberate session fault — SharedListenerTests");
            secondReached.TrySetResult();
            return finish.Task;
        });

        using (var faulting = new TcpClient())
        {
            await faulting.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Bounded);
            await WaitForTotal(guard, 0);   // released despite the throw
        }

        using var next = new TcpClient();
        await next.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Bounded);
        await secondReached.Task.WaitAsync(Bounded);   // admitted, so the cap of one was genuinely free
        Assert.Equal(2, calls);

        finish.SetResult();
        await WaitForTotal(guard, 0);
    }

    // ---- plumbing ---------------------------------------------------------------------------------------

    /// <summary>Start an acceptor that is LISTENING by the time this returns, and then runs for the rest of
    /// the process exactly as it does in production (there is no stop, by design).
    /// <para><b>Not <c>Task.Run</c>.</b> <c>RunAsync</c> executes on the caller's stack as far as its first
    /// await, which is inside <c>AcceptTcpClientAsync</c> — after <c>listener.Start()</c> — so calling it
    /// here and discarding the task leaves the port bound with no window for the connect below to fall into.
    /// Handing the bind to the thread pool instead is a race the caller can lose, and does: it cost three CI
    /// reds on the Linux runner (SocketException "Connection refused" within 6ms of the connect) on the same
    /// commit that was green on Windows, which had simply been winning it. The assert turns the assumption
    /// into a checked one, so an await appearing before Start in TkAcceptor fails here and says why.</para>
    /// <para>The synchronization context is suppressed for that one call so the accept loop's continuations
    /// go to the thread pool rather than to xUnit's per-test context, which the test itself is using and
    /// which does not outlive it.</para></summary>
    private static void Start(int port, ConnGuard guard, Func<TcpClient, int, IPAddress?, Task> runSession)
    {
        var acceptor = new TkAcceptor(new[] { port }, guard, runSession);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { _ = acceptor.RunAsync(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        Assert.False(PortIsFree(port),
            $"nothing is listening on :{port} — the acceptor either failed to bind it or returned before Start()");
    }

    /// <summary>Take the next port in this assignment's block, having checked it is actually free. A port
    /// another process owns fails inside <c>TkAcceptor.ListenAsync</c>, on a task nobody awaits, so without
    /// this the fact would fail with nothing saying why.</summary>
    private static int ClaimPort()
    {
        for (int i = 0; i < 100; i++)
        {
            int port = Interlocked.Increment(ref _nextPort) - 1;
            if (PortIsFree(port)) return port;
        }
        throw new InvalidOperationException("no free port in the 8000 block");
    }

    private static bool PortIsFree(int port)
    {
        var probe = new TcpListener(IPAddress.Loopback, port);
        try { probe.Start(); }
        catch (SocketException) { return false; }
        probe.Stop();
        return true;
    }

    /// <summary>Poll <see cref="ConnGuard.Total"/> to the expected value, or fail on the bound. The release
    /// happens on the acceptor's task, not the caller's, so there is nothing to await for it.</summary>
    private static async Task WaitForTotal(ConnGuard guard, int expected)
    {
        var deadline = DateTime.UtcNow + Bounded;
        while (guard.Total != expected && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(expected, guard.Total);
    }
}
