using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>The outbound game-packet dump follows the process-wide wire switch.</summary>
[Collection("log")]
public sealed class SendMapLogTests
{
    private static readonly FieldInfo WireFlag = typeof(Log).GetField(
        "_wireEnabled", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo SendMap = typeof(Session).GetMethod(
        "SendMap", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task SendMapWritesOnlyWhenWireLoggingIsEnabled()
    {
        var outbound = new RecordingOutbound();
        var session = new Session(
            outbound,
            2005,
            new CharacterStore(Path.Combine(TestProcessState.StateDirectory, "send-map-log-chars")),
            new World(),
            new Character { Name = "WireGuard" });
        bool original = Log.WireEnabled;
        var captured = new StringWriter();
        TextWriter prior = Console.Out;
        Console.SetOut(TextWriter.Synchronized(captured));
        try
        {
            string offLabel = "wire-off-" + Guid.NewGuid().ToString("N");
            WireFlag.SetValue(null, false);
            SendMap.Invoke(session, new object[] { (byte)0x0A, (byte)0, Array.Empty<byte>(), offLabel });
            await WaitForBarrier(captured);
            Assert.DoesNotContain(offLabel, captured.ToString(), StringComparison.Ordinal);

            string onLabel = "wire-on-" + Guid.NewGuid().ToString("N");
            WireFlag.SetValue(null, true);
            SendMap.Invoke(session, new object[] { (byte)0x0A, (byte)1, Array.Empty<byte>(), onLabel });
            await WaitForBarrier(captured);
            Assert.Contains(onLabel, captured.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, outbound.Frames.Count);
        }
        finally
        {
            WireFlag.SetValue(null, original);
            Console.SetOut(prior);
        }
    }

    private static async Task WaitForBarrier(StringWriter captured)
    {
        string barrier = "send-map-log-barrier-" + Guid.NewGuid().ToString("N");
        Log.Info(barrier);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!captured.ToString().Contains(barrier, StringComparison.Ordinal)
               && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Contains(barrier, captured.ToString(), StringComparison.Ordinal);
    }
}
