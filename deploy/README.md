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

Normally CI does this — see §9. By hand, on the build machine:

```bash
dotnet publish Server/Server.csproj -c Release -r linux-arm64 --self-contained true -o out/game
```

```bash
dotnet publish LoginServer/LoginServer.csproj -c Release -r linux-arm64 --self-contained true -o out/login
```

`linux-arm64` because the host is an Oracle Ampere A1. Nothing in the source is architecture-specific —
MoonSharp and BCrypt.Net-Next are pure managed, and `Microsoft.Data.Sqlite` ships a native `e_sqlite3` for
arm64 — so building for x64 instead is a one-word change if you move hosts.

**The host needs no .NET installed.** These are self-contained builds: the runtime ships inside the release,
the units exec the apphost (`current/game/Server`) rather than `dotnet Server.dll`, and `releases/<sha>` is
therefore exactly what that commit ran. With a framework-dependent build, upgrading the host runtime
silently changes the behaviour of every release already on disk — including the one you roll *back* to,
which is the worst possible moment to discover it. The cost is ~70 MB per app.

Not trimmed and not AOT-compiled: MoonSharp binds script verbs by reflection, which is exactly what the
trimmer cannot see.

Target layout on the host (`/opt/nexus`):

```
/opt/nexus/
  NexusServer.sln        <- empty marker file, so RepoPaths.Root() resolves here
  current -> releases/<sha>
  releases/<sha>/game/   <- published game binaries
  releases/<sha>/login/  <- published login binaries
  data/                  <- game-data, maps, nexus.db, chars/, logs  (the only writable path)
```

The marker file is what makes `RepoPaths.Root()` land on `/opt/nexus`; without it both processes fall back to
the working directory and you get two different `data/` dirs. An empty `Server/` + `Shared/` pair of
directories works as the marker too — but **do not put either marker inside a release directory**, or the root
resolves to the release and every deploy gets its own empty `data/`.

Both units start via `current`, so deploying is: unpack a new release, flip the symlink, restart. Rolling back
is flipping it the other way.

## 2. Ship the data directory

`data/` is a git submodule in its own private repository. CI checks it out with the rest of the tree and
bundles the tracked content into the release tarball, so a deploy carries its own data — nothing to copy by
hand. Cloning it yourself needs a token with access to *both* repos (see §9).

Two things inside it matter beyond the CSVs:

- **`data/game-data/`** — the content registry. Filenames are referenced with exact case and Linux is
  case-sensitive; copy them verbatim, don't let anything lowercase them.
- **`data/maps/`** — the ~1750 `.map` terrain files. These **are** tracked in the data repo now, so a normal
  checkout has them. What still bites is a *partial* copy: a missing `.map` throws nothing and logs one line,
  and collision plus mob/NPC spawn placement silently degrade, so players and monsters walk through walls.
  On Windows it hides even better, because the server falls back to
  `C:\Program Files (x86)\Nexon\NextAeon\Maps` — a dev box looks fine while the host is empty.

  `Tests/ContentSmokeTests.cs` asserts every map has its file, so CI fails rather than shipping that. The
  game server also logs the count at startup:

  ```
  === terrain: 1743/1743 map file(s) found; searched: ...
  ```

  Set `NEXUS_MAPS` if the files live somewhere other than `<root>/data/maps`.

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
| `NEXUS_TESTERS` | — | Comma-separated tester names; unioned with `data/tester_accounts.txt`. |
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

## 5. Staff access

`@` commands (spawn items, set level, warp, mint coins, hot-reload content) come in two tiers, each gated
on a roster that is **empty by default**, so on a fresh deploy nobody can run any of them.

| Tier | Roster | May run |
|---|---|---|
| **GM** | `data/gm_accounts.txt` / `NEXUS_GMS` | Everything. |
| **Tester** | `data/tester_accounts.txt` / `NEXUS_TESTERS` | Rebuild their own character (`@lvl`, `@mark`, `@class`, `@align`, `@stats`, `@spell`), summon items and coin, and the self-affecting toys (`@ride`, `@weapon`, `@hurt`). No warping, no spawning monsters, no `@reload`, no protocol labs. |

Add one name per line to the appropriate file, then `@reload` in game — no restart needed. A name in both
files is a GM: the tiers are a floor, not a partition. `@help` lists only what the caller may actually
run, and anything above the caller's tier answers `Unknown command.` — the same answer a typo gets, so
the tooling stays invisible rather than merely locked.

## 6. Firewall

Only 2000/2001/2005/2006 need to be open. If you only support the 4.95 client, open just 2000 and 2005.

```bash
sudo ufw allow 2000/tcp && sudo ufw allow 2005/tcp
```

**On Oracle Cloud there are two firewalls, and opening one is not enough.** The VCN Security List (or an
NSG) in the OCI console is the outer one; the instance images also ship *host* `iptables` rules that drop
everything except SSH. A port opened in only one layer looks exactly like a server that is not listening,
which is why this eats an afternoon. Open both, and check the host side with:

```bash
sudo iptables -L INPUT -n --line-numbers
```

If the rules are managed by `netfilter-persistent` (the Ubuntu images) rather than `ufw`, add them there —
`ufw allow` will appear to succeed while the pre-existing REJECT rule earlier in the chain still wins.

**Reserve the public IP.** The default ephemeral address changes when the instance is stopped and started,
and the client dials a raw IP baked into `NexusTK.dat` (§10) — an IP change means repatching every player's
client. Convert it to a Reserved Public IP in the console before handing the address out.

## 7. Backups

Everything mutable is in `data/nexus.db` — accounts, every character, board posts, mail, parcels, door
state, the world clock, moderation. Losing that file loses the server.

`deploy/nexus-backup.sh` + its systemd timer run this nightly at 04:00. Install once:

```bash
sudo cp deploy/nexus-backup.{service,timer} /etc/systemd/system/ && sudo mkdir -p /var/backups/nexus && sudo chown nexus:nexus /var/backups/nexus
```

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now nexus-backup.timer
```

```bash
systemctl list-timers nexus-backup.timer
```

Three things about it that are deliberate:

- **It uses `sqlite3 .backup`, never `cp`.** The database is in WAL mode with both servers connected, so a
  plain copy can capture a main file whose committed data is still in the `-wal` sidecar — restoring that
  gives you a torn or stale database. `.backup` uses SQLite's online backup API and needs nothing stopped.
- **Each snapshot is verified** (`PRAGMA integrity_check`, plus a character count, because an intact but
  empty database passes an integrity check). A failed verification aborts the run *before* the prune step,
  so a run of failures can never age out the last good copy.
- **`Persistent=true` on the timer.** If the host was off at 04:00 it runs on the next boot instead of
  silently skipping a day.

Local retention is 30 days (`NEXUS_BACKUP_KEEP_DAYS`). Restoring is just stopping both services, gunzipping a
snapshot over `data/nexus.db` (delete any `-wal`/`-shm` sidecars first), and starting them again.

### Offsite copy (Cloudflare R2)

A backup stored on the machine being backed up covers corruption and fat-fingers and nothing else. On an
Oracle Always Free tenancy the host going away is a routine event rather than a disaster scenario — idle
instances are reclaimed and free accounts are suspended — so the offsite copy is the one that matters.

R2 is deliberately a **different vendor from the compute**. Pushing to OCI Object Storage in the same
tenancy would share the exact failure mode being insured against.

The script **refuses to run** if `NEXUS_R2_REMOTE` is unset, rather than quietly degrading to local-only.
Set `NEXUS_BACKUP_OFFSITE=0` if you want local-only on purpose. Silent degradation is how a server ends up
with backups everyone believes are offsite.

Setup, once:

1. In the Cloudflare dashboard, create an R2 bucket (e.g. `nexus-backups`) and an **R2 API token** scoped to
   *Object Read & Write* on that bucket only. You will get an Access Key ID and a Secret Access Key, plus
   your account ID. Create these yourself — they are credentials and do not belong in this repo or in CI.
2. Install rclone (arm64 is in the Ubuntu archive):

```bash
sudo apt install -y rclone
```

3. Write `/etc/nexus/rclone.conf`, substituting your three values:

```ini
[r2]
type = s3
provider = Cloudflare
access_key_id = YOUR_ACCESS_KEY_ID
secret_access_key = YOUR_SECRET_ACCESS_KEY
endpoint = https://YOUR_ACCOUNT_ID.r2.cloudflarestorage.com
region = auto
```

4. Lock it down — it holds a live secret, and the backup runs as `nexus`:

```bash
sudo chown root:nexus /etc/nexus/rclone.conf && sudo chmod 640 /etc/nexus/rclone.conf
```

5. Verify before trusting it. This should list the bucket without error:

```bash
sudo -u nexus RCLONE_CONFIG=/etc/nexus/rclone.conf rclone lsd r2:
```

The unit points rclone at that path via `RCLONE_CONFIG`, because `ProtectHome=true` hides the default
`~/.config/rclone/rclone.conf`.

Three deliberate choices in the offsite half:

- **The push happens *before* the local prune.** A broken offsite path leaves more local copies, not fewer.
  The database is a few hundred KB, so there is no disk-pressure argument for pruning while offsite is
  failing, and a strong argument against it.
- **A failed push exits non-zero**, so the timer lands in systemd's `failed` state and `systemctl
  list-timers` / `systemctl --failed` shows it. rclone verifies the hash after transfer, so a truncated
  upload fails here rather than on the day you need it.
- **Remote retention is 90 days**, longer than local (`NEXUS_R2_KEEP_DAYS`). Remote storage is cheap and the
  scenario it covers — quiet corruption, an abusive player found late, a lost host — is usually noticed
  well after the fact. The remote prune is scoped with `--include 'nexus-*.db.gz'` so pointing this at a
  shared bucket can never delete somebody else's objects.

**Test the restore path before you need it.** An untested backup is a hope. Pull the newest object down and
open it:

```bash
rclone copy "r2:nexus-backups/$(rclone lsf r2:nexus-backups --include 'nexus-*.db.gz' | sort | tail -1)" /tmp/ && gunzip -c /tmp/nexus-*.db.gz > /tmp/verify.db && sqlite3 /tmp/verify.db "PRAGMA integrity_check; SELECT COUNT(*) FROM characters;"
```

## 7a. Moderation

GM-only chat commands, all recorded in an append-only `mod_log` table with who did it and why:

| Command | Effect |
|---|---|
| `@ban <name> [minutes] [reason]` | Refused at login and at world entry. **Kicks them immediately if online.** |
| `@unban <name>` | Lifts it. |
| `@mute <name> [minutes] [reason]` | Blocks speech, whisper and subpath chat. `@` commands still work. Applies to a live session immediately. |
| `@unmute <name>` | Lifts it. |
| `@kick <name> [reason]` | Disconnects — saving them first, so a kick never costs progress. |
| `@banip <ip> [minutes] [reason]` / `@banip remove <ip>` | Bans a source address. |
| `@bans` | Everyone currently banned or muted. |
| `@modlog [n]` | Recent actions, newest first. |

**Omitting the duration means permanent.** The alternative — a mistyped command silently expiring instantly
— fails in the direction nobody notices.

Durations are absolute deadlines, so a ban cannot be waited out by logging off and a mute survives a
restart with the right time remaining. A GM cannot be banned or muted; remove them from the roster first.

Account bans and IP bans are separate axes on purpose: an account ban is evaded by making a new character,
an IP ban catches everyone in a household. For a serious case use both.

One thing to know about the login flow: the ban is checked **after** the password, so the login screen can't
be used to enumerate who is banned. The consequence is that a banned player sees "Incorrect password" if
they also typo their password — that is intended.

## 8. Restarting the server

Never `systemctl restart nexus-game` on a populated server if you can avoid it — it is a SIGTERM, and while
that does flush every player's save (`Server/Net.cs`), they get no warning at all.

Use the schedule instead. From in game, as a GM:

```
@restart 30 shipping the pet AI fix
```

Everyone online is told immediately, then again at **20, 15, 10, 5, 2 and 1 minutes**, then once more as it
goes down. `@restart` on its own reports the time remaining; `@restart cancel` calls it off (and tells
everyone it was called off). `Server/RestartSchedule.cs` owns all of this.

The deadline is an **absolute instant**, not a countdown, so a stalled tick announces late but still restarts
on time. At zero the process announces, waits three seconds for the last packet to reach the client, and calls
`Environment.Exit(0)` — which runs the same graceful save-everyone flush that SIGTERM does. systemd's
`Restart=always` brings it straight back.

### Scheduling one without a GM online

A deploy has nobody logged in, so there is a file trigger. Write an **absolute unix-ms deadline** to
`data/restart_at`, optionally with a reason after a pipe:

```bash
echo "$(( ($(date +%s) + 1800) * 1000 ))|maintenance" > /opt/nexus/data/restart_at
```

The server polls for it every ~6 seconds and **consumes** (deletes) it on read. That deletion is load-bearing:
without it the freshly-restarted process would find the same file and restart itself again, forever. A
deadline already in the past is refused and logged rather than obeyed, for the same reason.

`data/reload_now` is the content-lane equivalent — an empty file asks for a hot content reload (the `@reload`
path) with no restart and nobody kicked. Any text in it is announced to players as a note.

## 9. CI/CD

`.github/workflows/ci.yml` builds, tests and deploys. Two lanes, chosen by what changed:

| Lane | Trigger | What happens |
|---|---|---|
| **content** | only the `data` submodule pointer moved | rsync content, drop `reload_now`, hot reload. **No restart.** |
| **code** | anything else | publish → stage `releases/<sha>` → flip `current` → schedule a warned restart |

The symlink is flipped **immediately** but the restart is scheduled. Swapping the pointer under a running
process is safe — the live server keeps its already-mapped assemblies until it exits — and it means the
restart itself *is* the deploy. It also means a crash during the warning window comes back up on the new
build rather than the old one.

The gate that matters is not `dotnet build`. It is `Tests/ContentSmokeTests.cs`: a stray comma in a CSV, a
renamed Lua verb, or a missing `.map` file all compile perfectly and all reach players. Those tests load the
real content and assert the registries are populated, every Lua script compiles, every `SpellParams` row names
a verb that exists, and every map has its terrain file.

### Secrets to set on the code repository

| Secret | What |
|---|---|
| `SUBMODULE_TOKEN` | PAT (or GitHub App token) with **read** on both repos. The default `GITHUB_TOKEN` is scoped to one repository and cannot clone the private data submodule. |
| `DEPLOY_SSH_KEY` | Private key for the deploy user. |
| `DEPLOY_KNOWN_HOSTS` | `ssh-keyscan <host>` output. Pinned deliberately — without it the deploy would hand the server's binaries to whatever answers on that IP. |
| `DEPLOY_HOST` / `DEPLOY_USER` | Where to ship. |
| `DEPLOY_PORT` | Optional; defaults to 22. |

### Sudoers entry the deploy needs

`deploy/host-deploy.sh` restarts the **login** server directly (the game restarts itself on the ladder). Login
has no players to warn, but it must not be left running an older build than the game — the handoff packet is a
contract between the two. Grant exactly that and nothing more:

```
deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart nexus-login, /usr/bin/systemd-run --on-active=* --unit=nexus-login-redeploy systemctl restart nexus-login
```

### Rollback

```bash
ln -sfn /opt/nexus/releases/<previous-sha> /opt/nexus/current && sudo systemctl restart nexus-game nexus-login
```

The last 5 releases are kept on the host, so this is a symlink flip rather than a rebuild.

## 10. Client side

The client must be pointed at the host. For 4.83/4.95 that means patching the plaintext `Address` entry in
`NexusTK.dat` (offset ~0x195a) — the IP strings in the exe are stale and patching them does nothing. See
`re/` for the tooling and `docs/NexusTK-4.95-Protocol.md` for the details.
