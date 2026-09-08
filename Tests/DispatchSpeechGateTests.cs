using Server;
using Xunit;

namespace Tests;

/// <summary>
/// The NPC-speech gate (<see cref="Session.IsCommandNotSpeech"/>), pinned for #56: it used to test the text
/// for the OLD '!' command prefix, which stopped meaning "GM command" once commands moved to '@' — so every
/// '@command' typed in chat was ALSO being dispatched as NPC speech. This predicate now gates on the real
/// prefix (<see cref="Commands.Prefix"/>, '@') instead.
/// </summary>
public class DispatchSpeechGateTests
{
    [Fact]
    public void RealCommandPrefixIsNotSpeech() => Assert.True(Session.IsCommandNotSpeech("@x"));

    [Fact]
    public void OldBangPrefixIsSpeechNow() => Assert.False(Session.IsCommandNotSpeech("!x"));

    [Fact]
    public void OrdinaryTextIsSpeech() => Assert.False(Session.IsCommandNotSpeech("x"));

    [Fact]
    public void EmptyTextIsNotSpeech() => Assert.True(Session.IsCommandNotSpeech(""));
}
