using System.Text;
using Shared;

namespace Server;

/// <summary>
/// The game port's answer to a plain HTTP "GET" — a one-shot JSON status line for the docs site's
/// status probe (p1998.essorcal.com reads it via a scheduled poll and shows an online/players/restart
/// strip; see project1998-docs' status workflow).
///
/// WHY THE GAME PORT AND NOT A NEW ONE: a status endpoint on its own port would need a firewall rule
/// and a reverse-proxy/TLS story on the host — infra changes for a read-only counter. The game port is
/// already public (players connect to it), and on the GAME channel the CLIENT speaks first — the server
/// sends nothing until the client's 0x10 arrives (see Session.RunAsync) — so a connection whose FIRST
/// bytes are "GET " can never be a real client mid-handshake. Sniffing those four bytes is therefore
/// collision-free by protocol, not by luck. The login port is excluded: it greets first, and a probe
/// there would race the welcome.
///
/// The response is deliberately bare HTTP/1.0-style (status line, three headers, close): the consumer
/// is curl in a scheduled workflow, not a browser — though CORS * is sent anyway so a same-machine
/// debug fetch works. No request parsing beyond the sniff: whatever the path, the answer is the status.
/// </summary>
public static class StatusResponder
{
    /// <summary>Do these first bytes open an HTTP GET? Checked only before the first valid game frame
    /// (Session._established == 0) and only on game ports, so the cost on real clients is four byte
    /// compares on their first chunk.</summary>
    public static bool LooksLikeHttp(List<byte> buf) =>
        buf.Count >= 4 && buf[0] == (byte)'G' && buf[1] == (byte)'E' && buf[2] == (byte)'T' && buf[3] == (byte)' ';

    /// <summary>The full HTTP response, built fresh per probe — the values are the point.</summary>
    public static byte[] Build(World world)
    {
        int players = world.OnlinePlayerCount();

        // Restart countdown in whole minutes, rounded UP (a restart 30s away is "1", not "0" — the strip
        // says "restart in ~N min" and 0 would read as "now" while the server is still up).
        long remMs = world.Restarts.RemainingMs;
        string restart = remMs >= 0 ? ((remMs + 59_999) / 60_000).ToString() : "null";

        string era = Era.Today is { } d ? $"\"{d:yyyy-MM-dd}\"" : "null";

        // The deployed content revision, stamped into game-data/.version by CI's bundle step. Absent on
        // dev checkouts (git is the version there) — null then, not an error.
        string ver = "null";
        try
        {
            var p = Path.Combine(RepoPaths.GameDataDir(), ".version");
            if (File.Exists(p))
            {
                var sha = File.ReadAllText(p).Trim();
                if (sha.Length > 0) ver = $"\"{sha[..Math.Min(7, sha.Length)]}\"";
            }
        }
        catch (Exception e) { Log.Warn("status: game-data/.version unreadable — reporting version null", e); }   // status must never fail on a version stamp

        string json = $"{{\"up\":true,\"players\":{players},\"era\":{era},\"restartInMin\":{restart},\"version\":{ver}}}";
        var body = Encoding.ASCII.GetBytes(json);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        var res = new byte[head.Length + body.Length];
        head.CopyTo(res, 0);
        body.CopyTo(res, head.Length);
        return res;
    }
}
