# 5.33 client targeting + unified dual-client server

How to point the 5.33 client at the local server, and how one `Project1998` process serves both the
4.95 and 5.33 clients at once.

## Redirecting the 5.33 client to the local server

**The server list lives in `NexusTK.dat`, internal file `Connaddr`** (a DAT archive; `Connaddr` is at
offset **1028 / 0x404**, length **77 bytes**). Format is one entry per line, space-separated, CRLF:
`host port\r\n`, four entries, space-padded to keep the exact 77-byte length. Stock:

```
64.124.47.60 2000 / 64.124.47.61 2000 / tk0.nexon.net 2000 / 65.203.45.40 2000
```

The client walks the list top-to-bottom. **This file is the only thing that controls the login target.**

To point 5.33 at this server's 5.x lane, patch every entry to `127.0.0.1 2001` (note the port — see the
dual-client section). `2000`→`2001` is the same byte length, so the 77-byte layout is preserved
trivially. Tooling:

- **`re/patches/patch_533_connaddr.py`** — the current tool. Rewrites the 77-byte `Connaddr` entry in
  place on any install, with `--check` / `--revert` / `--client <dir>` / `--host` / `--port`. It generates
  the padding and then cross-checks it byte-for-byte against the proven-good dat below, so it cannot
  drift from the layout known to work.

  ```
  python re/patches/patch_533_connaddr.py --client "C:\Users\you\Desktop\NextAeon533"
  ```

- `client-5.33-redirect\NexusTK.dat.patched` — the original ready-made patched archive
  (Connaddr = `127.0.0.1 2001`); now kept as the reference layout the patcher validates against.
- `client-5.33-redirect\Deploy-Connaddr-2001.bat` — **superseded.** It copies that whole 1.9 MB archive
  over a *hardcoded* `NextAeon5` path, which clobbers every other entry in the dat and only works for one
  install location. Prefer the patcher.

Verify with `Get-NetTCPConnection` after login: the client connects loopback to `:2001` (login) then
`:2006` (game).

### Dead ends (all ignored for the game socket — confirmed)

- `/host: /portno: /id:` command-line args — sit next to the `HITEL2000`/billing strings; not the game
  connection.
- Registry `HKCU\SOFTWARE\Nexon\Kingdom of the Winds\Servers` — not read for login.
- hosts-file remap of `game.kornetworld.com` — login uses the raw IPs in `Connaddr`, and hosts can't
  remap IP→IP.

### Windowed shim

5.33 uses the same **cnc-ddraw** windowed/upscale shim as 4.95 (imports `DDRAW.dll`). Copy
`ddraw.dll` + `ddraw.ini` + `Shaders\` from the 4.95 install next to `NexusTK.exe`
(`client-5.33-redirect\Install-Shim-5.33.bat`).

## Unified dual-client server (version-tagged by port)

One `Project1998` process serves both clients. Rather than sniff the client version off the wire, each
client gets its **own listener ports**, and the session is tagged by the port it arrived on. The proven
4.95 code path is therefore **never entered by a 5.33 session**.

| Client | Login port | Game port | `Session.ClientVersion` |
|--------|-----------|-----------|--------------------------|
| 4.95   | `2000`    | `2005`    | `V495` |
| 5.33   | `2001`    | `2006`    | `V533` |

Mechanics (`Server/Session.cs`):

- Constructor: `_ver = (port == 2001 || port == 2006) ? V533 : V495;`
- Login welcome (`0x7E`) is sent on either login port (`2000` or `2001`).
- **Login → game handoff** (`HandleLogin`): a `V533` login (port 2001) redirects to game port **2006**,
  so the game session is also tagged `V533`. `V495` stays on `2005`.
- **Dispatch branches only where the protocols differ.** So far that's one opcode:
  - `0x06` from client: **4.95** → `HandleWalk` (walk+sync, unchanged). **5.33** → `HandleMapRequest`.
  - `0x05` from client: **4.95** → unused. **5.33** → `HandleMapRequest` (initial map pull).
  - Everything else (login, entity, chat, attack, profile) is shared.

As more 5.x divergences are found (stats `0x08`, self-walk, etc.), add a `switch (_ver)` at just those
points — do **not** fork the server.

## Running

`run-server.bat` starts the server on **all four ports**:

```
dotnet run --project Server -- --ports 2000,2005,2001,2006
```

> ⚠️ Do **not** run with the old `--ports 2000,2005` — that omits the 5.x lane and the 5.33 client will
> get the black void again.

Build/run needs a dotnet with an **SDK**, not just a runtime, and `run-server.bat` probes candidates
with `--list-sdks` rather than trusting that some `dotnet.exe` exists -- a runtime-only
`C:\Program Files\dotnet` on PATH answers `where dotnet` happily and then fails every build with
"No .NET SDKs were found". Order: `P1998_DOTNET`, the private `.dotnet\`, PATH,
`%LOCALAPPDATA%\Microsoft\dotnet` (which that installer does not add to PATH), `%ProgramFiles%\dotnet`.

If none of them has an SDK, the script offers to download .NET 8 from Microsoft into `.dotnet\` beside
the source -- no admin rights, no PATH or registry change, and deleting the folder undoes it. Two
things it sets up front matter more than they look:

- `DOTNET_NOLOGO` and `DOTNET_GENERATE_ASPNET_CERTIFICATE=0`, because the .NET first-run experience
  otherwise drops an HTTPS dev certificate in the user's certificate store -- outside `.dotnet\`, and
  still there after you delete it, for a feature this server never uses.
- `DOTNET_ROOT`, pointing at the SDK it chose. An apphost looks for its runtime in `DOTNET_ROOT`, then
  the registry, then `C:\Program Files\dotnet` -- so without it the launched `Server.exe` can find a
  machine-wide install with no .NET 8 runtime and exit 150 ("You must install or update .NET") before
  logging a single line. The registry knows nothing about `.dotnet\` at all.

> ⚠️ **`DOTNET_ROOT` must stay process-scoped.** `run-server.bat` sets it with a batch `set`, so it lives
> for that one script run and dies with the window. Never persist it -- not with `setx`, not into
> `HKCU\Environment`. It is a *global* redirect honoured by every framework-dependent apphost on the
> machine, not a per-project setting, so pointing it at a tree that holds only .NET 8 tells every other
> .NET app on the box that 8.x is the only runtime in existence.
>
> Observed on a contributor's box (Windows 11 26200 x64): with a user-scope `DOTNET_ROOT` left pointing
> at a private .NET 8 SDK, an unrelated .NET 10 desktop app died on launch with `You must install or
> update .NET ... Required: 'Microsoft.NETCore.App', version '10.0.0'` while 10.0.11 sat correctly
> installed in `C:\Program Files\dotnet`. Reinstalling .NET 10 could not fix it -- the installer writes
> to a directory the apphost was no longer consulting. Deleting the variable fixed it, and nothing about
> this server cared either way. The project-scoped `P1998_*` names are the pattern to copy: they collide
> with nothing.
>
> If you would rather install an SDK yourself than take the private `.dotnet\`, prefer
> `winget install Microsoft.DotNet.SDK.8` or the installer from Microsoft. Both land in
> `C:\Program Files\dotnet` beside any other version, register with the host, and need no environment
> variable at all. Running `dotnet-install.ps1` bare is the one to avoid: it installs to
> `%LOCALAPPDATA%\Microsoft\dotnet`, adds nothing to PATH, and leaves you reaching for exactly the
> variable above.

When an app insists a runtime is missing that is demonstrably installed, make the host say where it
looked:

```bat
set COREHOST_TRACE=1 && TheApp.exe
```

The probe paths in the first few lines name the `DOTNET_ROOT` it honoured, which identifies a hijack
immediately.

## Test loop for 5.33 terrain

1. (once) `client-5.33-redirect\Deploy-Connaddr-2001.bat` as admin.
2. `run-server.bat`.
3. Launch 5.33, log in → terrain renders.
4. To watch the stream from the client side: `python re\frida_probe_533_map.py` (see RE doc). Expect
   `MAPDATA 0x06 … rect(8,0) 19x17 first=[t=651 p=0 o=0]`.
