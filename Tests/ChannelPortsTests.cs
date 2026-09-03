using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// The login and game ports are two views of the same client-version channels. A mismatched mapping is
/// silent on the wire: the login succeeds, then the client reconnects to the wrong game listener.
/// </summary>
public class ChannelPortsTests
{
    [Theory]
    [InlineData(2000, 2005)]
    [InlineData(2001, 2006)]
    public void Existing_channels_keep_their_current_pairing(int loginPort, int gamePort)
    {
        Assert.Equal(gamePort, ChannelPorts.GameFor(loginPort));
        Assert.Equal(loginPort, ChannelPorts.LoginFor(gamePort));
    }
}
