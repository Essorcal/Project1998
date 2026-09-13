# Configuration

Every `P1998_*` environment variable the server reads, what it does, and what it does when you leave it
alone. This is the reference an operator should be able to answer "what is this deployment actually doing"
from, without grepping the source.

**The table below is GENERATED.** It is rendered from the declarations in
[`Shared/ServerConfig.cs`](../../Shared/ServerConfig.cs) — each knob's name, type, default and description
are stated there once, and nowhere else. Do not hand-edit between the markers; run

```
dotnet run --project Tools -- config-doc
```

`Tests/ServerConfigTests.cs` asserts that the committed block equals the generator's output, so a knob added
without regenerating this file fails CI rather than shipping an out-of-date reference.

## How a value is read

Every knob is resolved **once**, when the process starts, and is immutable afterwards. Changing an
environment variable under a running server does nothing; restart it. Both processes print their effective
configuration at startup — every knob, its value, and whether it came from the environment or the declared
default — so the log says what the process is running with rather than what somebody meant to set.

A value the server cannot use is **never silently taken as zero or as false**. It keeps the declared
default and logs a `!!` line naming the variable, the value you set and what is in force instead:

- a numeric knob given something that is not a whole number, or a number outside its declared range;
- a boolean knob given anything but `0` or `1` (`true`, `yes` and `on` are all mistakes — the two spellings
  this rule replaced disagreed about what `true` meant, in opposite directions, in the two processes);
- a word-list knob given a word that is not on its list;
- a **retired** knob that is still set. Those are listed in their own section below: they are gameplay
  values, so they are rows in `game-data/ServerTuning.csv` now. The variable is ignored, and the warning
  tells you the key to set instead.

Nothing in this set is a secret — it is addresses, sizes, timeouts, paths and diagnostic toggles — so the
startup banner prints values verbatim. A future knob that IS a secret must not be printed there.

## What this file does not cover yet

- **The 68 per-table content path overrides** (`P1998_MOBS`, `P1998_SPELLS`, `P1998_MAP_CELLS`, …). They are
  declared by `TableSpec` in [`Server/Content.Tables.cs`](../../Server/Content.Tables.cs) and listed in the
  generated table block in [`game-data/README.md`](../../game-data/README.md). Retiring them in favour of
  `P1998_GAME_DATA` is a behaviour change that belongs with the `TableSpec` work.
- **Reads still inline in a handful of files** — `Shared/TcpOutbound.cs`, `LoginServer/LoginSession.cs`,
  `Server/Session.Dialog.cs`, `Server/Session.UserList.cs`, `Server/World.cs`, `Server/Watchdog.cs`,
  `Server/StatusFile.cs`, `Server/StaffAccounts.cs`, `Server/MapData.cs`, `Server/ObjectFlags.cs`,
  `Shared/NetBind.cs`, `Shared/ConnGuard.cs`, `Shared/LoginAuth.cs`, `Shared/EraCalendar.cs`,
  `Shared/TkAcceptor.cs`, `Protocol.Tk495/FrameReader.cs`. Those knobs are real and supported; they are
  simply not declared here yet. (`Protocol.Tk495` does not reference `Shared`, so `P1998_HANDSHAKE_MS`
  needs a project-structure decision rather than a one-line move.)
- **Launcher-only variables**, read by `run-server.bat` and never by the server: `P1998_DOTNET` (path to a
  `dotnet.exe` with a .NET 8 SDK), `P1998_NO_INSTALL` (refuse to fetch an SDK), `P1998_AUTO_INSTALL` (fetch
  one without prompting).

<!-- generated: config -->

### Deployment roots

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_GAME_DATA` | path | `<root>/game-data` | Authored content: the CSVs, the Lua, the .map terrain, SObj.tbl. Read-only at runtime; a deploy replaces it wholesale. |
| `P1998_STATE` | path | `<root>/state` | Live instance state: the SQLite database, the character store, the staff rosters. The whole of what a backup must capture. |
| `P1998_LOGS` | path | `<root>/logs` | Append-only stdout captures. Grows without bound, regenerable, never backed up. |
| `P1998_RUN` | path | `<root>/run` | Deploy-to-server control triggers (restart_at, reload_now), consumed and deleted by the running process. Not state. |

### Logging

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_LOG_WIRE` | `0` / `1` | per process (see notes) | Hex-dump every frame. Unset takes the ENTRY POINT's default, which differs by process: ON in the game server (the backbone of the protocol RE work, no credentials on that channel) and OFF in the login server (4.95's cipher is a fixed published XOR, so a dump writes plaintext passwords). |
| `P1998_LOG_MAX_BYTES` | integer ≥ 1 | per process (see notes) | Rotate the log file at this many bytes. Unset takes the entry point's default: 64MB in the game server, 32MB in the login server. |

### Transport and the accept path

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_TRUST_PROXY` | `0` / `1` | `0` (off) | Read and trust a PROXY protocol v2 header on every accepted connection. Off means the accept path behaves exactly as it always has, so a bare clone with no proxy in front never waits for a header that is not coming. |
| `P1998_PROXY_HEADER_MS` | integer ≥ 1 | `5000` | How long a trusted peer has to deliver its PROXY header before the connection is dropped. Separate from the handshake budget, which covers the first GAME packet and cannot start until this is done. |
| `P1998_PROXY_ALLOW` | text | `127.0.0.0/8,::1/128` | Peers allowed to send a PROXY header, as comma-separated addresses or CIDR blocks. This gate is the entire security model — a header is just bytes, so anyone who can reach the port could otherwise claim any source address. A containerised proxy needs its bridge network added. |

### Login, handoff and redirects

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_GAME_HOST` | text | `127.0.0.1` | The game server's address as the CLIENT must reach it (a.b.c.d), for the login handoff redirect. Not the bind address. Anything unparseable falls back to loopback rather than sending players somewhere unreachable. |
| `P1998_LOGIN_HOST` | text | *(blank)* | The login server's address for the exit-to-select bounce. Blank falls back to P1998_GAME_HOST, because the common deployment runs both processes on one box and behind a proxy both front doors share one public address. |
| `P1998_LOGIN_PORT` | integer ≥ 1 | paired with the arrival channel | Login port the exit-to-select bounce names. Unset derives it from the port the session arrived on, because the channels are PAIRED by client version — bouncing a 5.33 player onto the 4.95 login would round-trip them straight back. |
| `P1998_ENFORCE_HANDOFF` | `0` / `1` | `1` (on) | Refuse a game connection whose single-use handoff token does not verify. 0 downgrades the failure to a warning and lets the connection in — a fallback for a deployment with a token problem, and the only thing standing between the game port and a client claiming any username. |
| `P1998_LOGIN_FAILS` | integer ≥ 1 | `10` | Failed logins one source IP may spend inside the window before further attempts are refused without touching the password hash. |
| `P1998_LOGIN_FAIL_WINDOW_MS` | integer ≥ 1 | `300000` | Length of that rolling failure window, in milliseconds. A successful login clears the counter. |
| `P1998_LOGIN_EXEMPT_LOOPBACK` | `0` / `1` | `1` (on) | Exempt loopback from the failed-login throttle (local dev and the same-box login->game hop). Set 0 on a host where loopback is not automatically trusted. |

### Session behaviour

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_AUTOSAVE_MS` | integer ≥ 1 | `15000` | Ceiling on how often a dirty character is flushed to the store, and so on worst-case data loss in a hard crash. The session's own read loop and World's idle sweep both use this one cadence. |
| `P1998_CAST_QUEUE` | `0` / `1` | `1` (on) | Hold over-budget casts until the next action window instead of discarding them, so a held cast key lands as one animation and one sound rather than an audible flam. 0 restores the plain drop-gate. |
| `P1998_PASS` | `0` / `1` | `1` (on) | Server-side passability (collision). 0 lets players walk through anything — an escape hatch for a map whose 4.x top-2-bits polarity turns out wrong. |

### 4.95 movement calibration

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_V495_WALK_MS` | integer | `200` | Delay before the 0x04 that completes a 4.95 self-walk. The client caps local prediction at ~180ms and then freezes, so 0x04 must land just after that window. 0 is the old same-frame slide. |
| `P1998_V495_SELF_MOVE` | integer | `7` | Self-walk drive mode with fast-move ON. 0 = 0x04 only (no legs); 1 = legacy 0x0C(dest)+0x04; 2 = 0x0C(source)+0x04; 3 = send nothing; 5 = delay then 0x04; 7 = nothing on a good walk, 0x04 only as a correction (default, RTK-faithful). |
| `P1998_V495_ACK_MS` | integer | `360` | Delay before the mode-5 unblock 0x04. Must be at least the client's local walk animation (~360ms) or the legs are cut short. |
| `P1998_V495_SLOW_MOVE` | integer | `5` | Self-walk drive mode with fast-move OFF. 0 = 0x04 only; 1 = 0x0C(dest); 2 = 0x0C(source)+delayed 0x04; 3 = 0x0C(dest)+delayed 0x04; 4 = 0x0C(source) only; 5 = the 0x26 self-walk primitive (default). |
| `P1998_V495_REALM` | `0` / `1` | `0` (off) | Set the 0x15 realm-center flag, which locks the client camera dead-centre on the character. |
| `P1998_V495_FASTMOVE_DEFAULT` | `0` / `1` | `0` (off) | Pre-world-entry placeholder for a session's fast-move flag. The real value is restored from the character (SettingFlags bit 9); 1 forces the old assumed-ON placeholder, which was the desync bug. |
| `P1998_V495_FASTMOVE_TRUST_TOGGLE` | `0` / `1` | `1` (on) | Drive the walk reply off the session's fast-move flag (toggled in lockstep via 0x1b/09). 0 forces the old always-0x26 behaviour. |
| `P1998_V495_PUSHMAP` | `0` / `1` | `1` (on) | Push a margin strip of terrain ahead of a walking 5.33 client instead of waiting for it to ask. 0 disables the push entirely and restores the visible black cells. |
| `P1998_V495_PUSHGRACE` | integer ≥ 0 | `0` | Steps to defer that push for. 0 pushes on every step; a higher value restores the old deferral to the client's own requests. |

### Rendering and tile diagnostics

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_LIGHT` | integer | `232` | The map light/darkness value sent on the 0x15, 0..65535. 232 is proven bright on 4.95. |
| `P1998_LIGHT_FMT` | one of `beu16` / `leu16` / `u8` | `beu16` | How that light value is encoded on the 0x15. Sweeping this isolates whether a client reads the field at a different width or endianness. |
| `P1998_MAP_DIAG` | text | *(blank)* | 5.33 terrain-render probe for the 0x06 stream. Blank sends real map tiles. "sweep" ramps the ground index across the visible rect, "solid:N" fills with index N (both untranslated), "ground:N" puts the 4.x ground WORD N through the real translation, "passtest:N" fills the pass bits with N. |
| `P1998_TILE_OFF_533` | integer | `0` | Uniform shift applied to sheet-1 ground indices on 5.33. Should stay 0; it exists so a future sheet revision can be probed without a rebuild. |
| `P1998_TILE_OFF_495` | integer | `0` | The same shift for 4.95 ground indices. |
| `P1998_OBJ_OFF_533` | integer | `0` | Uniform shift applied to SObj.tbl object ids on 5.33. |
| `P1998_OBJ_OFF_495` | integer | `0` | The same shift for 4.95 object ids. |
| `P1998_TILE_OFF` | integer | unset (per-version offsets apply) | Superseded single knob that shifts GROUND ONLY, on both client versions. Kept so existing run scripts keep working; when set it overrides both per-version ground offsets. |
| `P1998_OBJ_FIX_533` | one of `off` / `free` / `decor` / `all` / `structural` | `free` | How much of the 5.33 object-collision workaround to apply. "off" none; "free" only proven visually identical substitutions (default — nothing on screen changes); "decor" also blanks fully walkable decoration; "all" (alias "structural") also blanks real directional blockers, which deletes visible structures. |
| `P1998_EFX_WIRE_OFFSET` | integer | `0` | Adjustment added to the effect id on the 0x29 spell-animation packet. Proven 0 live; kept as a calibration hatch. |
| `P1998_LOOK533_EXTRA` | text | *(blank)* | Defaults for the two 5.33 appearance slots 4.95 has no equivalent for, as "hair,tail". Blank sends 0 for both. |

### Retired — moved into ServerTuning.csv

| Variable | Now lives in | What it was |
|---|---|---|
| `P1998_HIT_CRIT` | `HitCrit` in `game-data/ServerTuning.csv` | The 0x13 hit-type byte, which selects the over-head hit overlay (RTK uses 33 normal / 255 crit). |
| `P1998_HEAL_CRIT` | `HealCrit` in `game-data/ServerTuning.csv` | The 0x13 critical byte a HEAL carries. RTK passes 0. |
| `P1998_DEATH_DELAY_MS` | `DeathDespawnMs` in `game-data/ServerTuning.csv` | How long a killed mob's corpse is held before the 0x0E despawn. |
| `P1998_SPELLBOOK_CAP` | `SpellBookCap` in `game-data/ServerTuning.csv` | Slots the client's spellbook array can hold before a teach would overrun it. |

<!-- /generated -->
