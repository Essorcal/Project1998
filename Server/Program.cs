using Server;

int[] ports = { 2000, 2005 };
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--ports" && i + 1 < args.Length)
        ports = Array.ConvertAll(args[i + 1].Split(','), int.Parse);
}

// Confirmed by reversing NexusTK.exe: 4.95 has ONE cipher on both channels — the simple
// NexonInc XOR (no name-derived/table cipher, no index/trailer bytes). The old --mapkey/--index
// knobs were 7.x-only artifacts and have been removed so the logs don't misrepresent the wire.
Log.Info($"=== NexusServer (C#) starting; ports={string.Join(",", ports)}; " +
         $"cipher=NexonInc (login+game), framing=AA|len|op|inc|body (no trailer) ===");
await new TkListener(ports).RunAsync();
