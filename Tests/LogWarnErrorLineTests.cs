using Shared;
using Xunit;

namespace Tests;

/// <summary>Pins the exact text and admission level of the two entry points #199 routes the last
/// hand-prefixed diagnostics through: <see cref="Log.Warn(string)"/> for the four <c>TkAcceptor</c>
/// REJECT lines, and the new <see cref="Log.Error(string)"/> overload for the five fatal/startup lines
/// in <c>Server/Program.cs</c> and <c>LoginServer/Program.cs</c> that have no exception object to carry.
/// <para>Zero behaviour change is the whole point of that PR: the text and the sink are the same as the
/// hand-written <c>Log.Info("!! ...")</c> / <c>Log.Info("!!! ...")</c> calls they replace, so these facts
/// read the formatted line back off disk (there is no in-process hook for the formatted string) and
/// compare it byte-for-byte against the marker the entry point is supposed to have written.</para>
/// <para><b>Collection "log"</b>, for the reason <see cref="LogDropPolicyTests"/> gives: the file sink and
/// the writer thread are process-global.</para></summary>
[Collection("log")]
public class LogWarnErrorLineTests
{
    /// <summary>A message logged through <see cref="Log.Warn(string)"/> reaches the file with exactly one
    /// <c>"!! "</c> marker ahead of the text — not zero, not two — at LogLevel.Warn.
    /// <para>Falsification: make <see cref="Log.Warn(string)"/> prepend <c>"!! "</c> twice and the
    /// <c>Assert.Equal</c> on <c>afterStamp</c> fails, because it would read <c>"!! !! REJECT ..."</c>
    /// against an expected <c>"!! REJECT ..."</c>. Drop the marker instead and the same assert fails the
    /// other way. Run both re-breaks, confirm red, then restore.</para></summary>
    [Fact]
    public void Warn_writes_a_reject_line_with_exactly_one_bang_pair_and_warn_level()
    {
        string tag = "warn-fact-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), "p1998-log-warn-" + tag, "server.log");
        // The exact shape TkAcceptor.cs writes for a rejected connection, minus the "!! " the hand-written
        // call used to add itself.
        string msg = $"REJECT 203.0.113.5:41000 on :7000 (not in P1998_PROXY_ALLOW); 3 live [{tag}]";

        Log.AttachFile(path);
        Log.Warn(msg);
        Log.Shutdown();
        try
        {
            string line = ReadLineContaining(path, tag);

            Assert.Equal(Log.StampLength, $"[{DateTime.Now:HH:mm:ss.fff}] ".Length);
            string afterStamp = line.Substring(Log.StampLength);
            Assert.Equal("!! " + msg, afterStamp);

            Assert.Equal(LogLevel.Warn, Log.LevelOf(line, LogLevel.Info));
        }
        finally
        {
            Log.RestartWriterForTest();
        }
    }

    /// <summary>A message logged through the new <see cref="Log.Error(string)"/> overload reaches the file
    /// with exactly one <c>"!!! "</c> marker ahead of the text — the same shape the removed
    /// <c>Log.Info("!!! ...")</c> hand-prefixed call used to write — at LogLevel.Error, and with no stack
    /// (there is no exception to print).
    /// <para>Falsification: make the overload prepend <c>"!!! "</c> twice and the <c>Assert.Equal</c> on
    /// <c>afterStamp</c> fails, reading <c>"!!! !!! invalid --ports: ..."</c> against the expected single
    /// marker. Drop the marker instead and the same assert fails the other way. Run both re-breaks, confirm
    /// red, then restore.</para></summary>
    [Fact]
    public void Error_string_overload_writes_the_triple_bang_line_with_no_stack_at_error_level()
    {
        string tag = "error-fact-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), "p1998-log-error-" + tag, "server.log");
        // The exact shape Server/Program.cs and LoginServer/Program.cs write for a bad --ports argument.
        string msg = $"invalid --ports: not-a-number [{tag}]";

        Log.AttachFile(path);
        Log.Error(msg);
        Log.Shutdown();
        try
        {
            string line = ReadLineContaining(path, tag);

            string afterStamp = line.Substring(Log.StampLength);
            Assert.Equal("!!! " + msg, afterStamp);

            Assert.Equal(LogLevel.Error, Log.LevelOf(line, LogLevel.Info));
        }
        finally
        {
            Log.RestartWriterForTest();
        }
    }

    /// <summary>Read the log while the writer still holds it open (there is no detach — see
    /// <see cref="SharedLoggerTests"/>), and return the single line carrying the given tag.</summary>
    private static string ReadLineContaining(string path, string tag)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string text = reader.ReadToEnd();
        string[] lines = text.Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.Contains(tag)) return line;
        }
        throw new Xunit.Sdk.XunitException($"no line containing '{tag}' found in {path}:\n{text}");
    }
}
