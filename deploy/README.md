# Hosting NexusServer on Linux

Two independent .NET 8 processes sharing one SQLite database:

| Process | Ports | Role |
|---|---|---|
| `LoginServer` | 2000 (4.95), 2001 (5.33) | account creation, login, handoff to the game server |
| `Server` (game) | 2005 (4.95), 2006 (5.33) | the world |

They are separate so the game can be restarted to ship a change while login stays up. Both bind
`0.0.0.0`. Both anchor their data directory to the repo root (the directory holding `NexusServer.sln`, or
one holding both `Server/` and `Shared/`) — **not** the working directory — so the published layout below
must keep that marker.

---

## 1. Publish

On the build machine:

```bash
dotnet publish Server/Server.csproj -c Release -r linux-x64 --self-contained false -o out/game
```

```bash
dotnet publish LoginServer/LoginServer.csproj -c Release -r linux-x64 --self-contained false -o out/login
```

Target layout on the host (`/opt/nexus`):

```
/opt/nexus/
  Server/            <- empty marker dir, so RepoPaths.Root() resolves here
  Shared/            <- empty marker dir, same reason
  game/              <- published game binaries
  login/             <- published login binaries
  data/              <- game-data, maps, nexus.db, logs   (the only writable path)
```

The two empty marker directories are what make `RepoPaths.Root()` land on `/opt/nexus`; without them both
processes fall back to the working directory and you get two different `data/` dirs. Alternatively keep an
`.sln` file at the root.

## 2. Ship the data directory

`data/` is a **git submodule pointing at a local Windows path** (`C:/Users/brian/Desktop/NexusServer-data.git`),
so `git submodule update` will not work on the host. Copy it across instead — or repoint the submodule at a
reachable remote first.

Two things inside it matter beyond the CSVs:

- **`data/game-data/`** — the content registry. Filenames are referenced with exact case and Linux is
  case-sensitive; copy them verbatim, don't let anything lowercase them.
- **`data/maps/`** — the `.map` terrain files. **This is the one that will bite.** The repo ships only
  `TK32.map`; on Windows the server silently falls back to `C:\Program Files (x86)\Nexon\NextAeon\Maps`,
  which does not exist on the host. Copy the client's `Maps` directory (~1750 files, ~10 MB) into
  `data/maps/`, or point `NEXUS_MAPS` at wherever you put it.

  Without it nothing crashes — collision and mob/NPC spawn placement just silently degrade, so players and
  monsters walk through walls. The game server logs the count at startup; check it:

  ```
  === terrain: 1743/1743 map file(s) found; searched: ...
  ```

## 3. Configure

Copy the unit files, edit `NEXUS_GAME_HOST` in **both** to the server's public IP, then:

```bash
sudo cp deploy/nexus-*.service /etc/systemd/system/ && sudo systemctl daemon-reload
```

```bash
sudo systemctl enable --now nexus-login nexus-game
```

`NEXUS_GAME_HOST` is the address the **client** dials after login — the login server writes it into the
handoff packet. Leaving it at the loopback default makes every remote client try to connect to itself and
hang on the loading screen.

### Environment variables worth knowing

| Var | Default | Notes |
|---|---|---|
| `NEXUS_GAME_HOST` | `127.0.0.1` | Public IP the handoff redirects to. **Must be set.** |
| `NEXUS_MAPS` | — | Terrain directory, if not `data/maps`. |
| `NEXUS_GMS` | — | Comma-separated GM names; unioned with `data/gm_accounts.txt`. |
| `NEXUS_LOG_WIRE` | game `1`, login `0` | Per-packet hex dump. Set `0` on the game server in production. Login defaults off because its packets contain plaintext passwords. |
| `NEXUS_LOG_MAX_BYTES` | 64 MB / 32 MB | Log rotation threshold; disk use is capped at ~2x this. |
| `NEXUS_AUTOSAVE_MS` | `15000` | Bounds worst-case data loss on a hard kill. |
| `NEXUS_LOGIN_FAILS` | `10` | Failed logins per IP per window before a temporary block. |
| `NEXUS_LOGIN_FAIL_WINDOW_MS` | `300000` | That window. |
| `NEXUS_GAME_MAXCONN` / `NEXUS_LOGIN_MAXCONN` | `2000` | Global concurrent-connection cap per process. |
| `NEXUS_GAME_PERIP` / `NEXUS_LOGIN_PERIP` | `8` | Concurrent connections per source IP. Raise for NAT'd players. |
| `NEXUS_ENFORCE_HANDOFF` | `1` | Leave on. `0` lets a client connect straight to the game port claiming any name. |
| `NEXUS_ALLOW_TOFU` | `0` | Leave off. `1` lets a passwordless legacy character adopt whatever password is first sent. |

## 4. Accounts

Login is strict: it never creates a character. Players must use the client's creation flow first.
Administration is offline, with the servers stopped:

```bash
dotnet /opt/nexus/login/LoginServer.dll --list-accounts
```

```bash
dotnet /opt/nexus/login/LoginServer.dll --set-password <name> <password>
```

```bash
dotnet /opt/nexus/login/LoginServer.dll --delete-character <name>
```

`--list-accounts` flags characters with **MISSING** passwords — records that predate the accounts table.
They cannot log in until you set one.

## 5. GM access

`!` commands (spawn items, set level, warp, mint coins, hot-reload content) are gated on a roster that is
**empty by default**, so on a fresh deploy nobody can run them. Add one name per line to
`data/gm_accounts.txt` (or set `NEXUS_GMS`), then `!reload` in game — no restart needed.

## 6. Firewall

Only 2000/2001/2005/2006 need to be open. If you only support the 4.95 client, open just 2000 and 2005.

```bash
sudo ufw allow 2000/tcp && sudo ufw allow 2005/tcp
```

## 7. Backups

Everything mutable is in `data/nexus.db` (WAL mode). Back it up with SQLite's own backup so you never
capture a torn write:

```bash
sqlite3 /opt/nexus/data/nexus.db ".backup '/var/backups/nexus-$(date +%F).db'"
```

Copying the file with `cp` while the servers run can produce an inconsistent snapshot — use `.backup`, or
stop both services first.

## 8. Client side

The client must be pointed at the host. For 4.83/4.95 that means patching the plaintext `Address` entry in
`NexusTK.dat` (offset ~0x195a) — the IP strings in the exe are stale and patching them does nothing. See
`re/` for the tooling and `docs/NexusTK-4.95-Protocol.md` for the details.
