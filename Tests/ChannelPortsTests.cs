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

    [Theory]
    [InlineData(2000, true, false)]
    [InlineData(2001, true, true)]
    [InlineData(2005, false, false)]
    [InlineData(2006, false, true)]
    public void Existing_channels_keep_their_roles_and_versions(int port, bool isLogin, bool isV533)
    {
        Assert.Equal(isLogin, ChannelPorts.IsLogin(port));
        Assert.Equal(isV533, ChannelPorts.IsV533(port));
    }

    [Theory]
    [InlineData(3000, 3005, true, false)]
    [InlineData(3001, 3006, true, true)]
    public void Non_default_base_keeps_pair_position(int loginPort, int gamePort, bool isLogin, bool isV533)
    {
        ChannelPorts.ConfigureLoginPair(new[] { 3000, 3001 });
        try
        {
            Assert.Equal(gamePort, ChannelPorts.GameFor(loginPort));
            Assert.Equal(loginPort, ChannelPorts.LoginFor(gamePort));
            Assert.Equal(isLogin, ChannelPorts.IsLogin(loginPort));
            Assert.False(ChannelPorts.IsLogin(gamePort));
            Assert.Equal(isV533, ChannelPorts.IsV533(loginPort));
            Assert.Equal(isV533, ChannelPorts.IsV533(gamePort));
        }
        finally
        {
            ChannelPorts.ResetForTests();
        }
    }
}
