using Protocol.Tk495;
using Server;

namespace Tests.Support;

/// <summary>
/// An <see cref="IOutbound"/> that keeps every frame instead of writing it to a socket — the test half of the
/// seam added for #27.
///
/// <para>Frames are recorded EXACTLY as they would have hit the wire (<c>AA | len(u16 BE) | opcode | inc |
/// encrypted body</c>), because that is the only thing a test can meaningfully pin: the client parses bytes,
/// not intent. <see cref="BodiesOf"/> decrypts them back with the same cipher <c>Session.MapBuild</c> encrypts
/// with, so an assertion reads as the layout the client's own parser walks.</para>
///
/// <para>Not thread-safe and deliberately not made so: a handler test drives one session from one thread, and
/// a lock here would hide it if that ever stopped being true.</para>
/// </summary>
public sealed class RecordingOutbound : IOutbound
{
    private readonly List<byte[]> _frames = new();

    public RecordingOutbound(string remote = "recorder") => Remote = remote;

    public string Remote { get; }

    /// <summary>A recorder never backs up, so <see cref="Send"/> never refuses and this number is never used
    /// for anything but the log line naming a dropped slow client.</summary>
    public int Capacity => int.MaxValue;

    /// <summary>Always 0: Send delivers, so nothing is ever in flight.</summary>
    public int QueueDepth => 0;

    /// <summary>True once the session tore the connection down. Recording continues to be accepted after it —
    /// Session.Send's own <c>_closed</c> gate is what stops frames, and a test asserting on a drop wants to
    /// see whether anything slipped past it.</summary>
    public bool Closed { get; private set; }

    /// <summary>Every frame handed over, in order.</summary>
    public IReadOnlyList<byte[]> Frames => _frames;

    public bool Send(byte[] frame)
    {
        _frames.Add(frame);
        return true;
    }

    public void Close() => Closed = true;

    /// <summary>Forget everything recorded so far — used to drop world-entry chatter so a test asserts only on
    /// what its own packet produced.</summary>
    public void Clear() => _frames.Clear();

    /// <summary>The DECRYPTED bodies of every recorded frame carrying <paramref name="opcode"/>, in order.
    /// 4.95 has one cipher on both channels (see TkCrypt), and <c>Session.MapBuild</c> encrypts every game
    /// packet with it, so this is the reverse of the exact step the send path took.</summary>
    public List<byte[]> BodiesOf(byte opcode)
    {
        var bodies = new List<byte[]>();
        foreach (var frame in _frames)
        {
            if (!TkPacket.TryParse(frame, out var pkt, out _)) continue;
            if (pkt.Opcode != opcode) continue;
            bodies.Add(TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey));
        }
        return bodies;
    }
}
