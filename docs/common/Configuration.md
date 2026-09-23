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

Two sets, and nothing else. Every other active `P1998_*` variable either server process reads is in the
generated table below. The 68 per-table CSV overrides and four Lua-file overrides are retired by this
change; `P1998_GAME_DATA` is now the sole content-path override, and any retired per-file name still set is
ignored with one startup warning.

- **Launcher-only variables**, read by `run-server.bat` and never by the server: `P1998_DOTNET` (path to a
  `dotnet.exe` with a .NET 8 SDK), `P1998_NO_INSTALL` (refuse to fetch an SDK), `P1998_AUTO_INSTALL` (fetch
  one without prompting).
- **Desktop-tool variables**, read by `MapEditor` and `IconStudio` and never by either server process:
  `P1998_REPO` (repository root when the tool is run from elsewhere) and `P1998_CLIENT5` (path to a 5.33
  client install for its art). Those projects are outside `Project1998.Server.slnf` and outside CI, and
  declaring them here would put two knobs no server reads into the startup banner.

<!-- generated: config -->

### Deployment roots

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_GAME_DATA` | path | `<root>/game-data` | Authored content: the CSVs, the Lua, the .map terrain, SObj.tbl. Read-only at runtime; a deploy replaces it wholesale. |
| `P1998_STATE` | path | `<root>/state` | Live instance state: the SQLite database, the character store, the staff rosters. The whole of what a backup must capture. |
| `P1998_LOGS` | path | `<root>/logs` | Append-only stdout captures. Grows without bound, regenerable, never backed up. |
| `P1998_RUN` | path | `<root>/run` | Deploy-to-server control triggers (restart_at, reload_now), consumed and deleted by the running process. Not state. |
| `P1998_MAPS` | text | *(blank — search `<game-data>/maps` then the client installs)* | First directory searched for the 4.x headerless `.map` terrain files. Blank searches only the built-in list: `<game-data>/maps`, then the two Windows client installs. Point this at a client's `Maps` directory on a host that has no client installed. The value is used as given, not trimmed. |
| `P1998_SOBJ` | text | *(blank — try `<game-data>/SObj.tbl` then the RTK-Server copy)* | First path tried for the client's `SObj.tbl` object-collision table. Blank tries `<game-data>/SObj.tbl`, then the RTK-Server copy. Prefer the client extract: its object-id space is the one the `.map` files index. The value is used as given, not trimmed. |

### Logging

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_LOG_WIRE` | `0` / `1` | per process (see notes) | Hex-dump every frame. Unset takes the ENTRY POINT's default, which is OFF in both processes: the game server (protocol RE work asks for `=1` to turn it on, on a machine with no real players) and the login server (4.95's cipher is a fixed published XOR, so a dump writes plaintext passwords, which is also why `=1` here should only be set on a machine with no real accounts). Must be EXACTLY `0` or `1`: surrounding whitespace is not trimmed for this one knob, so a padded `" 1"` warns and keeps the process default rather than turning the dump on. |
| `P1998_LOG_MAX_BYTES` | integer ≥ 1 | per process (see notes) | Rotate the log file at this many bytes. Unset takes the entry point's default: 64MB in the game server, 32MB in the login server. |

### Transport and the accept path

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_TRUST_PROXY` | `0` / `1` | `0` (off) | Read and trust a PROXY protocol v2 header on every accepted connection. Off means the accept path behaves exactly as it always has, so a bare clone with no proxy in front never waits for a header that is not coming. |
| `P1998_PROXY_HEADER_MS` | integer ≥ 1 | `5000` | How long a trusted peer has to deliver its PROXY header before the connection is dropped. Separate from the handshake budget, which covers the first GAME packet and cannot start until this is done. |
| `P1998_PROXY_ALLOW` | text | `127.0.0.0/8,::1/128` | Peers allowed to send a PROXY header, as comma-separated addresses or CIDR blocks. This gate is the entire security model — a header is just bytes, so anyone who can reach the port could otherwise claim any source address. A containerised proxy needs its bridge network added. |
| `P1998_BIND` | text | *(blank — 0.0.0.0, every interface)* | Local interface both listeners bind to. Blank binds every interface (0.0.0.0), which is right for a real deployment. Set a specific LAN address to keep the servers OFF loopback, which matters only when the launcher's loopback proxy runs on the same box and would otherwise compete with the server for the client's connection. An address this server cannot parse falls back to 0.0.0.0 — the parse lives in `Shared/NetBind.cs`, so a bad value is not reported on the startup warning line. |
| `P1998_HANDSHAKE_MS` | integer ≥ 1 | `15000` | Slow-loris budget: a freshly accepted connection must send its first VALID framed packet within this long or it is dropped. Only the first packet is gated, so an in-world player standing AFK is never disconnected. Shared by both processes. 15s is far more than a real client needs. |
| `P1998_SLOW_SEND_MS` | integer ≥ 0 | `250` | Warn when a frame waits this long to reach the socket, or when the socket write itself takes that long. 0 disables the warning. 250ms is well under the ~1s a player would notice, so the log names the stall before anyone complains about it. |
| `P1998_LOGIN_WRITE_MS` | integer ≥ 1 | `10000` | How long ONE socket write on the LOGIN channel may take before the peer is dropped. The login conversation is a few hundred bytes: a peer that cannot accept them inside ten seconds is not a client anyone is waiting on. The game channel has no per-write bound — there the queue filling is what drops a stuck peer — so this knob does not apply to it. |

### Abuse control — connection admission and login throttling

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_LOGIN_MAXCONN` | integer ≥ 1 | `2000` | Concurrent connections the LOGIN process will hold before it sheds load by accepting and immediately closing. Load-shedding, not a player cap: past the ceiling an overload costs a closed socket rather than exhausted threads and memory. |
| `P1998_LOGIN_PERIP` | integer ≥ 1 | `8` | Live LOGIN connections one address may hold at once. 8 is sized to reliably SUPPORT about two players per address — two steady sockets, the brief login-to-game overlap, a lingering half-open ghost — without being a hard two-player quota. Raise it for NAT'd addresses sharing more players. |
| `P1998_LOGIN_RATE` | integer ≥ 1 | `30` | LOGIN connections one address may OPEN per window, which is what catches a connect/disconnect churn flood that the concurrent cap alone would not. 30 per 10s already covers two players logging in and reconnecting with retries. |
| `P1998_LOGIN_RATEWIN_MS` | integer ≥ 1 | `10000` | Length of that fixed rate window on the LOGIN front door, in milliseconds. |
| `P1998_LOGIN_EXEMPT_LOOPBACK` | `0` / `1` | `1` (on) | Exempt loopback from BOTH login-side per-address gates: the accept path's per-IP and rate caps, and the failed-login throttle. Local dev, the client test box and a same-box login-to-game hop all originate from 127.0.0.1 and must never be throttled; loopback still counts toward the global cap so load-shedding stays uniform. Set 0 on a host where loopback is not trusted. |
| `P1998_GAME_MAXCONN` | integer ≥ 1 | `2000` | The same concurrent-connection load-shedding ceiling for the GAME process. |
| `P1998_GAME_PERIP` | integer ≥ 1 | `8` | Live GAME connections one address may hold at once. Same sizing as the login door. |
| `P1998_GAME_RATE` | integer ≥ 1 | `30` | GAME connections one address may OPEN per window. |
| `P1998_GAME_RATEWIN_MS` | integer ≥ 1 | `10000` | Length of that fixed rate window on the GAME front door, in milliseconds. |
| `P1998_GAME_EXEMPT_LOOPBACK` | `0` / `1` | `1` (on) | Exempt loopback from the GAME accept path's per-IP and rate caps. Loopback still counts toward the global cap. |
| `P1998_LOGIN_FAILS` | integer ≥ 1 | `10` | Failed logins one source IP may spend inside the window before further attempts are refused without touching the password hash. |
| `P1998_LOGIN_FAIL_WINDOW_MS` | integer ≥ 1 | `300000` | Length of that rolling failure window, in milliseconds. A successful login clears the counter. |

### Login, handoff and redirects

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_GAME_HOST` | text | `127.0.0.1` | The game server's address as the CLIENT must reach it (a.b.c.d), for the login handoff redirect. Not the bind address. Anything unparseable falls back to loopback rather than sending players somewhere unreachable. |
| `P1998_LOGIN_HOST` | text | *(blank)* | The login server's address for the exit-to-select bounce. Blank falls back to P1998_GAME_HOST, because the common deployment runs both processes on one box and behind a proxy both front doors share one public address. |
| `P1998_LOGIN_PORT` | integer ≥ 1 | paired with the arrival channel | Login port the exit-to-select bounce names. Unset derives it from the port the session arrived on, because the channels are PAIRED by client version — bouncing a 5.33 player onto the 4.95 login would round-trip them straight back. |
| `P1998_ENFORCE_HANDOFF` | `0` / `1` | `1` (on) | Refuse a game connection whose single-use handoff token does not verify. 0 downgrades the failure to a warning and lets the connection in — a fallback for a deployment with a token problem, and the only thing standing between the game port and a client claiming any username. |
| `P1998_ALLOW_TOFU` | `0` / `1` | `0` (off) | TRUST SWITCH — leave it off. On, a login for a name that exists in `characters` with NO `accounts` row adopts whatever password was sent as that character's password, permanently. It is the escape hatch for the handful of characters that predate the accounts table: set it, log in once as that character, turn it back off. While it is on, anyone who guesses such a name claims the character. It never applies to a name with no character at all, so it cannot create an account. |

### Session behaviour

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_AUTOSAVE_MS` | integer ≥ 1 | `15000` | Ceiling on how often a dirty character is flushed to the store, and so on worst-case data loss in a hard crash. The session's own read loop and World's idle sweep both use this one cadence. |
| `P1998_CAST_QUEUE` | `0` / `1` | `1` (on) | Hold over-budget casts until the next action window instead of discarding them, so a held cast key lands as one animation and one sound rather than an audible flam. 0 restores the plain drop-gate. |
| `P1998_PASS` | `0` / `1` | `1` (on) | Server-side passability (collision). 0 lets players walk through anything — an escape hatch for a map whose 4.x top-2-bits polarity turns out wrong. |
| `P1998_GATE_PEER_MOVES` | `0` / `1` | `1` (on) | Send a peer's 0x0C move and 0x11 turn only to clients that have been drawn that peer, the same gate the mob moves have always had. 0 restores the ungated broadcast, which queued a frame for every session on the map — about 397 of 399 of them for clients that cannot see the walker. |

### World heartbeat

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_TICK_SWEEP_SKIP` | `0` / `1` | `1` (on) | Let the tick's viewport reconcile skip a viewer whose map has not changed since that viewer last swept it and that has no per-viewer draw pending. 0 restores the unconditional sweep, in which every player of every populated map walks every peer, every mob and every floor item on it every beat — at 400 players and 305 mobs that is 400 viewers against 704 entities a beat to decide, in the steady state, to send nothing. |
| `P1998_TICK_MS` | integer ≥ 50 | `333` | The world heartbeat in milliseconds — the smallest action interval the world can express at all. Mob timers are carried, not reset, so a 2000ms creature moves every 2000ms whatever this is; what changes is GRANULARITY. 333 divides Sute's observed 333/333/rest rhythm exactly. The tick body runs proportionally more often, so raising this back is the lever if the slow-tick watchdog starts firing. |
| `P1998_SLOW_TICK_MS` | integer ≥ 0 | a quarter of the heartbeat (83 at the default 333) | A tick this slow — work OR scheduling delay, in milliseconds — gets a diagnostic line. 0 disables the watchdog. Unset derives a quarter of the heartbeat, which is well clear of normal jitter and low enough to catch a stall long before a player would call it lag, and which is why it is derived rather than a fixed number: retuning the heartbeat retunes this with it. |

### Process-health probes

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_POOL_LAG_MS` | integer ≥ 0 | `100` | Report thread-pool scheduling latency at or above this many milliseconds. 0 disables the probe entirely. Read alongside SLOW SEND: high pool latency with a high queued time means starvation, and something is blocking pool threads. |
| `P1998_SILENT_MS` | integer ≥ 0 | `4000` | Report a client that has sent NOTHING for this long while the server is still actively sending to it. That asymmetry is the exact shape of "the mobs keep moving but my character cannot act". 0 disables the probe. |

### The status document

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_STATUS_FILE` | text | `<run>/status.json` | Where to publish the small document the launcher polls for "N online". Blank publishes `<run>/status.json`. The single value `-` disables publishing entirely. Trimmed. |
| `P1998_STATUS_MS` | integer ≥ 1000 | `10000` | How often that document is rewritten, in milliseconds. The launcher polls every 30s, so the 10s default means the number is never more than one poll stale. Values below the 1000ms floor are refused: this is a file write on a timer, not a metric. |
| `P1998_STATUS_MESSAGE` | text | *(blank — the launcher's own wording)* | Optional operator note published beside the player count. Blank leaves the launcher's own wording. Trimmed. |

### Staff rosters

| Variable | Type | Default | What it does |
|---|---|---|---|
| `P1998_GMS` | text | *(blank)* | GM account names, comma-separated. UNIONED with `<state>/gms.txt` rather than replacing it, so this adds a GM for one run without editing the file. Entries are trimmed and blank ones dropped. With no GM configured anywhere, the GM tier is disabled for everyone. |
| `P1998_TESTERS` | text | *(blank)* | Tester account names, comma-separated, unioned with `<state>/testers.txt` on the same rules as the GM roster above. Tester is the tier below GM. |

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

## What the status document publishes

The knobs above say where `run/status.json` is written and how often; the document's own fields are declared
in [`Server/StatusFile.cs`](../../Server/StatusFile.cs), which is where the full list lives. Everything after
the launcher's `online` / `players` / `message` is a since-process-start total, read as a delta between two
samples. Two of them describe the tick's viewport sweep:

| Field | What it counts |
|---|---|
| `sweepViewers` | Every (viewer, beat) pair the tick CONSIDERED — one per player of every populated map, on every beat, whether or not `P1998_TICK_SWEEP_SKIP` then skipped it. |
| `sweepsRun` | How many of those actually swept, counted after the three sweeps returned. Over a span, the share of viewers the skip saved is `1 - Δ sweepsRun / Δ sweepViewers`; with the skip off the two deltas are equal. |

Five more describe the game channel's send path. They count every game session's writer, logged in or
not; login-channel frames are never in them. A rate over a span is `Δ counter / Δ seconds`.

| Field | What it counts |
|---|---|
| `framesSent` | Game frames whose socket write completed. A frame dropped with its connection is not counted. |
| `bytesSent` | The bytes of those frames. `Δ bytesSent / Δ framesSent` is the mean frame size over the span. |
| `slowSends` | Every frame the writer's slow-send watchdog flagged (`P1998_SLOW_SEND_MS`), INCLUDING the ones the `SLOW SEND` log line suppresses under its one-line-per-second-per-session limit. 0 while the watchdog is disabled. |
| `slowSendsQueued` | Of those, the frames that waited at least the threshold between being queued and being picked up by the writer: the server's side, usually thread-pool starvation. |
| `slowSendsWrite` | Of those, the frames whose socket write itself took at least the threshold: the network's side, a client not acknowledging fast enough. A frame that was slow both ways is in both halves, so the halves can sum to more than `slowSends`. |
