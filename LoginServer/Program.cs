using LoginServer;
using Shared;

// LOGIN server. The internet-facing front door: account creation, login, and the handoff that redirects
// the client to the GAME server (a separate process). Deliberately does NOT load the game world/content,
// so it starts instantly and restarts independently of the game.
//   2000 = 4.95 login (V495)   2001 = 5.33 login (V533)
// Set NEXUS_GAME_HOST to the game server's public IP for a split (multi-box) deployment; it defaults to
// loopback (login + game on the same machine).
int[] ports = { 2000, 2001 };
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--ports" && i + 1 < args.Length)
        ports = Array.ConvertAll(args[i + 1].Split(','), int.Parse);
}

var store = new CharacterStore(RepoPaths.CharsDir());
Log.Info($"=== NexusServer LOGIN starting; ports={string.Join(",", ports)}; " +
         $"cipher=NexonInc; store={store.Directory} ===");
await new LoginListener(ports, store).RunAsync();
