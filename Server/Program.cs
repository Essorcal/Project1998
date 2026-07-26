using Server;

// GAME server. Handles the world (movement, combat, items, NPCs) for both client versions:
//   2005 = 4.95 game (V495)   2006 = 5.33 game (V533)
// The LOGIN channel (account creation + login + game handoff) is a SEPARATE process (LoginServer,
// ports 2000/2001) so the two can crash and restart independently. The client reaches this process
// because the login server's handoff packet redirects it here (reversed IP + game port). Session tags
// the client version by the port it arrived on.
int[] ports = { 2005, 2006 };
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--ports" && i + 1 < args.Length)
        ports = Array.ConvertAll(args[i + 1].Split(','), int.Parse);
}

// Confirmed by reversing NexusTK.exe: 4.95 has ONE cipher on both channels — the simple
// NexonInc XOR (no name-derived/table cipher, no index/trailer bytes). The old --mapkey/--index
// knobs were 7.x-only artifacts and have been removed so the logs don't misrepresent the wire.
// `--selftest` exercises the content registry + fuzzy lookups (the !warp/!maps/!mobs/!summon backing
// logic) and exits, WITHOUT opening ports — a quick offline check of the data layer.
if (Array.Exists(args, a => a == "--selftest")) { Content.SelfTest(); return; }

Log.Info($"=== NexusServer (C#) starting; ports={string.Join(",", ports)}; " +
         $"cipher=NexonInc (login+game), framing=AA|len|op|inc|body (no trailer) ===");
Content.Load();   // maps + mobs registries (external gitignored data; powers !warp/!maps/!mobs/!summon)
Boards.MigrateFromJsonIfNeeded();   // one-time import of any legacy data/boards.json into the shared DB
await new TkListener(ports).RunAsync();
