using Server;

// 2000/2005 = 4.95 login/game (V495); 2001/2006 = 5.33 login/game (V533). One process serves both;
// Session tags the client version by the port it arrived on. Each client's Connaddr points at its
// own login port (4.95 -> 2000, 5.33 -> 2001).
int[] ports = { 2000, 2005, 2001, 2006 };
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
await new TkListener(ports).RunAsync();
