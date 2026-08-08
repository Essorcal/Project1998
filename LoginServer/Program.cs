using LoginServer;
using Shared;

// LOGIN server. The internet-facing front door: account creation, login, and the handoff that redirects
// the client to the GAME server (a separate process). Deliberately does NOT load the game world/content,
// so it starts instantly and restarts independently of the game.
//   2000 = 4.95 login (V495)   2001 = 5.33 login (V533)
// Set NEXUS_GAME_HOST to the game server's public IP for a split (multi-box) deployment; it defaults to
// loopback (login + game on the same machine).
//
// Offline account admin (--list-accounts / --set-password / --delete-character) runs instead of the
// server and never opens a port — see LoginServer/Admin.cs. Login is strict now, so this is the supported
// way to reset a password or clear a test character.
if (Admin.TryRun(args)) return;

int[] ports = { 2000, 2001 };
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--ports" && i + 1 < args.Length)
        ports = Array.ConvertAll(args[i + 1].Split(','), int.Parse);
}

// Persist this process's log too (the game server has done so since the nmail "crash" whose console
// output was lost). Rotated by size — see LoginServer/Log.cs. Note WireEnabled is OFF here by default:
// login packets carry plaintext passwords.
Log.AttachFile(Path.Combine(RepoPaths.DataDir(), "login.log"));
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Info($"!!! FATAL unhandled exception (process dying): {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) =>
    { Log.Info($"!! unobserved task exception: {e.Exception}"); e.SetObserved(); };

var store = new CharacterStore(RepoPaths.CharsDir());
Log.Info($"=== NexusServer LOGIN starting; ports={string.Join(",", ports)}; " +
         $"cipher=NexonInc; store={store.Directory}; wire-log={(Log.WireEnabled ? "ON (passwords visible!)" : "off")} ===");
await new LoginListener(ports, store).RunAsync();
