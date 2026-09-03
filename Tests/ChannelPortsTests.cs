using System.Diagnostics;
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

    [Fact]
    public void Reversed_pair_is_refused()
    {
        try
        {
            var error = Assert.Throws<ArgumentException>(
                () => ChannelPorts.ConfigureLoginPair(new[] { 2001, 2000 }));

            Assert.Contains("consecutive and ordered", error.Message);
        }
        finally
        {
            ChannelPorts.ResetForTests();
        }
    }

    [Fact]
    public void Unknown_port_falls_back_to_game_495()
    {
        ChannelPorts.ConfigureLoginPair(new[] { 3000, 3001 });
        try
        {
            Assert.False(ChannelPorts.IsLogin(4000));
            Assert.False(ChannelPorts.IsV533(4000));
        }
        finally
        {
            ChannelPorts.ResetForTests();
        }
    }

    [Fact]
    public void Pair_can_be_reconfigured()
    {
        ChannelPorts.ConfigureLoginPair(new[] { 3000, 3001 });
        try
        {
            ChannelPorts.ConfigureLoginPair(new[] { 4000, 4001 });

            Assert.True(ChannelPorts.IsLogin(4000));
            Assert.True(ChannelPorts.IsV533(4001));
            Assert.False(ChannelPorts.IsLogin(4005));
            Assert.False(ChannelPorts.IsV533(3001));
        }
        finally
        {
            ChannelPorts.ResetForTests();
        }
    }

    [Theory]
    [InlineData(3000, true, false)]
    [InlineData(3001, true, true)]
    [InlineData(3005, false, false)]
    [InlineData(3006, false, true)]
    public void Game_pair_configures_both_channel_halves(int port, bool isLogin, bool isV533)
    {
        ChannelPorts.ConfigureGamePair(new[] { 3005, 3006 });
        try
        {
            Assert.Equal(isLogin, ChannelPorts.IsLogin(port));
            Assert.Equal(isV533, ChannelPorts.IsV533(port));
        }
        finally
        {
            ChannelPorts.ResetForTests();
        }
    }

    [Fact]
    public async Task Bad_arity_is_logged_and_returns_failure_without_throwing()
    {
        string logDirectory = Path.Combine(Path.GetTempPath(), $"p1998-port-arity-{Guid.NewGuid():N}");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(Server.Session).Assembly.Location);
        start.ArgumentList.Add("--ports");
        start.ArgumentList.Add("2005");
        start.Environment["P1998_LOGS"] = logDirectory;

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Server.");
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool exited;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                exited = true;
            }
            catch (OperationCanceledException)
            {
                exited = false;
            }
            if (!exited) process.Kill(entireProcessTree: true);

            Assert.True(exited, "Server did not reject the malformed port pair promptly.");
            string console = await stdout + await stderr;
            Assert.NotEqual(0, process.ExitCode);
            Assert.False(console.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase), console);

            string[] failures = File.ReadAllLines(Path.Combine(logDirectory, "server.log"))
                .Where(line => line.Contains("!!! invalid --ports:", StringComparison.Ordinal))
                .ToArray();
            string failure = Assert.Single(failures);
            Assert.Contains("Exactly two ports are required: first 4.95, then 5.33.", failure);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true);
        }
    }
}
