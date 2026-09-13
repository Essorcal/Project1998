using LoginServer;
using Shared;

// LOGIN server. The internet-facing front door: account creation, login, and the handoff that redirects
// the client to the GAME server (a separate process). Deliberately does NOT load the game world/content,
// so it starts instantly and restarts independently of the game.
//   2000 = 4.95 login (V495)   2001 = 5.33 login (V533)
// Set P1998_GAME_HOST to the game server's public IP for a split (multi-box) deployment; it defaults to
// loopback (login + game on the same machine).
//
// Offline account admin (--list-accounts / --set-password / --delete-character) runs instead of the
// server and never opens a port — see LoginServer/Admin.cs. Login is strict now, so this is the supported
// way to reset a password or clear a test character.
if (Admin.TryRun(args)) return;

// Declare this process's logging defaults before anything can log. The wire dump is OFF here, unlike the
// game server: login packets carry the player's password in the clear and 4.95's cipher is a fixed published
// XOR, so a dump writes plaintext passwords into logs/login.log — the whole reason Shared.Log.WireEnabled
// takes its default from the entry point rather than from the environment alone. The log rotates at 32MB,
// this process's historical limit. Both stay overridable by P1998_LOG_WIRE / P1998_LOG_MAX_BYTES, which
// ServerConfig declares and resolves.
ServerConfig.ConfigureLogging(wireDefault: false, maxBytesDefault: 32L * 1024 * 1024);

int[] ports = { 2000, 2001 };
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--ports" && i + 1 < args.Length)
        ports = Array.ConvertAll(args[i + 1].Split(','), int.Parse);
}

// Persist this process's log too (the game server has done so since the nmail "crash" whose console
// output was lost). Rotated by size — see Shared.Log.
Log.AttachFile(Path.Combine(RepoPaths.LogsDir(), "login.log"));
// The log is a queue drained by a background thread now, so the tail is still in memory when the process
// stops. Flush it on Ctrl+C, SIGTERM and ProcessExit — this process has nothing else to do on the way out.
Log.FlushOnExit();
Csv.Warn = Log.Warn;
CharacterStore.Warn = message => Log.Warn("[db] " + message);
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
        Log.Error("FATAL unhandled exception (process dying)", ex);
    else
        // Non-Exception payloads have no stack to carry, so preserve the fatal prefix through Info.
        Log.Info($"!!! FATAL unhandled exception (process dying): {e.ExceptionObject}");
    // Flush here rather than leave it to Log.FlushOnExit: the runtime ABORTS after this handler and never
    // raises ProcessExit, so the trace we just queued would die in the queue — which is the one line this
    // whole hook exists to preserve.
    Log.Shutdown();
};
TaskScheduler.UnobservedTaskException += (_, e) =>
    { Log.Warn("unobserved task exception", e.Exception); e.SetObserved(); };

try
{
    ChannelPorts.ConfigureLoginPair(ports);
}
catch (ArgumentException e)
{
    // Log.Error deliberately requires an exception; keep this exception-free fatal text hand-prefixed.
    Log.Info($"!!! invalid --ports: {e.Message.ReplaceLineEndings(" ")}");
    Environment.ExitCode = 1;
    return;
}

// The effective configuration, every knob with its value and its source. After AttachFile so it reaches
// logs/login.log as well as the console window.
ServerConfig.LogEffective();
Log.Info($"=== channel pairing: login {ports[0]}/{ports[1]} -> " +
         $"game {ChannelPorts.GameFor(ports[0])}/{ChannelPorts.GameFor(ports[1])} ===");
var store = new CharacterStore(RepoPaths.CharsDir());
Log.Info($"=== Project1998 LOGIN starting; ports={string.Join(",", ports)}; " +
         $"cipher=NexonInc; store={store.Directory}; wire-log={(Log.WireEnabled ? "ON (passwords visible!)" : "off")} ===");
await new LoginListener(ports, store).RunAsync();
