# NexusServer (C#/.NET 8) — game-server prototype

A clean-architecture rebuild of the 4.95 NexusTK server, structured to become the
foundation for a Unity-client game (Shared library is referenced by both sides later).

## Projects
- **Shared/**          protocol contract + (future) models & formulas — referenced by server AND your Unity client
- **Protocol.Tk495/**  DISPOSABLE 4.95 adapter: `TkCrypt` (NexonInc XOR + index bytes), `TkPacket` (framing)
- **Server/**          host: `TkListener` (accept loop), `Session` (per-conn read/dispatch), login + game handlers
- **Tools/**           formalized `Inter.dat` client-redirect patcher

## Build & run (offline)
```
dotnet build NexusServer.sln          # nuget.config clears online sources; no deps needed
dotnet run --project Server -- --ports 2000,2005
```

## Patch a client
```
dotnet run --project Tools -- /path/to/Inter.dat            # -> Inter.dat.patched (127.100.10.1), backup kept
dotnet run --project Tools -- /path/to/Inter.dat --target 127.100.10.1
```

## Status
Login + account creation + game-server handoff + arrival all working (verified against the live 2001 client).
Next: world entry (the `0x15` map-load sequence) in `Session.HandleArrival`.

## Encryption knobs (Protocol.Tk495/TkCrypt.cs)
- `MapKey`  — `NexonInc.` (4.95) or `Urk#nI7ni` (7.x)
- `MapUseIndex` — append the `13 F7 60` index bytes (7.x) or not
