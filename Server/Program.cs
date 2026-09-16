using Server;
using Shared;

// GAME server. Handles the world (movement, combat, items, NPCs) for both client versions:
//   2005 = 4.95 game (V495)   2006 = 5.33 game (V533)
// The LOGIN channel (account creation + login + game handoff) is a SEPARATE process (LoginServer,
// ports 2000/2001) so the two can crash and restart independently. The client reaches this process
// because the login server's handoff packet redirects it here (reversed IP + game port). Session tags
// the client version by the port it arrived on.

// Declare this process's logging defaults before anything can log: the wire dump is ON here (it is the
// backbone of the protocol RE work, and nothing on the game channel is a credential) and the log rotates at
// 64MB. Both stay overridable by P1998_LOG_WIRE / P1998_LOG_MAX_BYTES, which ServerConfig declares and
// resolves; these two arguments are the per-process defaults the environment overrides. First statement in
// the process, above --selftest, because the self-test logs too.
ServerConfig.ConfigureLogging(wireDefault: true, maxBytesDefault: 64L * 1024 * 1024);

int[] ports = { 2005, 2006 };
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--ports" && i + 1 < args.Length)
        ports = Array.ConvertAll(args[i + 1].Split(','), int.Parse);
}

// Confirmed by reversing NexusTK.exe: 4.95 has ONE cipher on both channels — the simple
// NexonInc XOR (no name-derived/table cipher, no index/trailer bytes). The old --mapkey/--index
// knobs were 7.x-only artifacts and have been removed so the logs don't misrepresent the wire.
// `--selftest` exercises the content registry + fuzzy lookups (the @warp/@maps/@mobs/@summon backing
// logic) and exits, WITHOUT opening ports — a quick offline check of the data layer.
if (Array.Exists(args, a => a == "--selftest"))
{
    int exitCode = Content.SelfTest();
    if (exitCode != 0)
    {
        Log.Shutdown();
        Environment.ExitCode = exitCode;
    }
    return;
}

// Crash forensics. (1) Tee all logging into logs/server.log — console output dies with the window,
// and we lost the trace of the first native-mail-send failure exactly that way. (2) Catch-and-log
// process-fatal exceptions from ANY thread, and unobserved Task faults, so a death always leaves a
// stack in the file. Note the Ctrl+C handler in TkListener: pressing Ctrl+C in the console (e.g. to
// copy text with nothing selected) triggers a CLEAN exit ("=== shutdown signal (Ctrl+C)"), which can
// masquerade as a crash — the log file now shows which one happened.
Log.AttachFile(Path.Combine(Shared.RepoPaths.LogsDir(), "server.log"));
Shared.CharacterStore.Warn = message => Log.Warn("[db] " + message);
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
        Log.Error("FATAL unhandled exception (process dying)", ex);
    else
        // Non-Exception payloads have no stack to carry, so preserve the fatal prefix through Info.
        Log.Info($"!!! FATAL unhandled exception (process dying): {e.ExceptionObject}");
};
TaskScheduler.UnobservedTaskException += (_, e) =>
    { Log.Warn("unobserved task exception", e.Exception); e.SetObserved(); };

try
{
    Shared.ChannelPorts.ConfigureGamePair(ports);
}
catch (ArgumentException e)
{
    // Log.Error deliberately requires an exception; keep this exception-free fatal text hand-prefixed.
    Log.Info($"!!! invalid --ports: {e.Message.ReplaceLineEndings(" ")}");
    Log.Shutdown();
    Environment.ExitCode = 1;
    return;
}

// The effective configuration, every knob with its value and whether it came from the environment or the
// declared default — plus a loud line for anything that was rejected or is retired. After AttachFile so it
// reaches logs/server.log too: "what was this process actually configured with" is the first question any
// after-the-fact investigation asks, and the console window is gone by then.
ServerConfig.LogEffective();
Log.Info($"=== channel pairing: game {ports[0]}/{ports[1]} -> " +
         $"login {Shared.ChannelPorts.LoginFor(ports[0])}/{Shared.ChannelPorts.LoginFor(ports[1])} ===");
Log.Info($"=== Project1998 (C#) starting; ports={string.Join(",", ports)}; " +
         $"cipher=NexonInc (login+game), framing=AA|len|op|inc|body (no trailer) ===");
Content.Load();   // maps + mobs registries (external gitignored data; powers @warp/@maps/@mobs/@summon)
StaffAccounts.Load();   // who may run the '@' tooling, and at which tier (state/{gm,tester}_accounts.txt); empty = nobody
Session.WarmCommandTable();   // build the '@' name index here, so a duplicate name kills startup and not a player's session
Doors.LoadUnlocks(); // locked doors players have already opened (map_unlocks) — must outlive a restart

// An empty content registry is a MISCONFIGURED DEPLOY, not a valid world: nothing throws, the server
// listens and accepts logins, and every player lands in a mapless void. Fail loudly instead of leaving it
// to be inferred from a "0 map(s)" line among the startup counts.
if (Content.Maps.Count == 0)
    // Log.Error deliberately requires an exception; keep this exception-free fatal text hand-prefixed.
    Log.Info("!!! NO CONTENT LOADED — game-data was not found. Expected it under the repo root " +
             $"(searched up from the binary, then the working directory: {Directory.GetCurrentDirectory()}). " +
             "The world will be empty. Fix: run from a full checkout, or set P1998_GAME_DATA to the " +
             "content directory.");

// Terrain availability. Missing .map files don't throw — collision and spawn placement just silently
// degrade (players and mobs walk through walls) — so say so at startup instead. The Windows fallback dirs
// are the client installs, which is exactly why a first Linux deploy finds nothing.
{
    var (found, total, dirs) = MapData.Availability(Content.Maps.Keys);
    Log.Info($"=== terrain: {found}/{total} map file(s) found; searched: {string.Join(" | ", dirs)}");
    if (found < total)
        Log.Warn($"{total - found} map(s) have NO .map file — collision and spawn placement are degraded on them. " +
                 "Copy the client's Maps directory into game-data/maps, or set P1998_MAPS to it.");
}
Boards.MigrateFromJsonIfNeeded();   // one-time import of any legacy state/boards.json into the shared DB
await new TkListener(ports).RunAsync();
