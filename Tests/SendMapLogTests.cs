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
        // Exclusive: Console.Out is one process-global slot and an unguarded swap here loses, or is lost by,
        // a capture running in another collection at the same time (Tests/Support/ConsoleTap.cs).
        using var captured = await ConsoleTap.AcquireAsync();
        try
        {
            string offLabel = "wire-off-" + Guid.NewGuid().ToString("N");
            WireFlag.SetValue(null, false);
            SendMap.Invoke(session, new object[] { (byte)0x0A, (byte)0, Array.Empty<byte>(), offLabel });
            await WaitForBarrier(captured);
            Assert.DoesNotContain(offLabel, captured.Text, StringComparison.Ordinal);

            string onLabel = "wire-on-" + Guid.NewGuid().ToString("N");
            WireFlag.SetValue(null, true);
            SendMap.Invoke(session, new object[] { (byte)0x0A, (byte)1, Array.Empty<byte>(), onLabel });
            await WaitForBarrier(captured);
            Assert.Contains(onLabel, captured.Text, StringComparison.Ordinal);
            Assert.Equal(2, outbound.Frames.Count);
        }
        finally
        {
            WireFlag.SetValue(null, original);
        }
    }

    private static async Task WaitForBarrier(ConsoleTap captured)
    {
        string barrier = "send-map-log-barrier-" + Guid.NewGuid().ToString("N");
        Log.Info(barrier);
        Assert.Contains(barrier, await captured.WaitForAsync(barrier, TimeSpan.FromSeconds(10)),
                        StringComparison.Ordinal);
    }
}
