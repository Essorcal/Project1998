namespace Shared;

/// <summary>
/// Where a session's outbound frames go.
///
/// <para>This is the seam the whole test story rests on. Everything above it — the 42 opcode handlers, world
/// entry, trade, combat, the tick's broadcasts — only ever calls <see cref="Send"/>, so whether the bytes end
/// up on a socket or in a list is invisible to all of it. The production implementation is
/// <see cref="TcpOutbound"/>; a test supplies a recorder and asserts on the exact frames a handler produced.</para>
///
/// <para>Deliberately outbound-ONLY. The inbound half is a read loop that owns framing, the handshake
/// watchdog and the autosave cadence (<c>Session.RunAsync</c>), and pretending a recorder could stand
/// in for that would be a lie; a socket-free session simply has no read loop, and a test feeds it packets
/// directly (<c>Session.Receive</c>).</para>
/// </summary>
public interface IOutbound
{
    /// <summary>Peer label for log lines — <c>"1.2.3.4:5555"</c>, or <c>"1.2.3.4 (via 10.0.0.1)"</c> behind a
    /// trusted proxy.</summary>
    string Remote { get; }

    /// <summary>How many frames this sink will hold before <see cref="Send"/> starts refusing them. Only used
    /// in the log line that names a dropped slow client.</summary>
    int Capacity { get; }

    /// <summary>Frames handed over but not yet delivered. Diagnostics only (<c>Session.DiagState</c>).</summary>
    int QueueDepth { get; }

    /// <summary>Hand over one framed packet. MUST NOT block: peer broadcasts and mob AI call this on the
    /// shared <c>World.TickLoop</c> thread. False means the sink is backed up past <see cref="Capacity"/>,
    /// which the caller answers by dropping the peer — see <c>Session.Send</c>.</summary>
    bool Send(byte[] frame);

    /// <summary>Stop accepting frames and drop the transport. Idempotent.</summary>
    void Close();
}
