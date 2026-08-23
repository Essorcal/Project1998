# Architecture

How Project1998 is put together, and why. Read this before changing anything structural — several of the
shapes below look arbitrary and are not.

---

## 1. Two processes, not one

The server runs as **two independent processes**:

| Process | Ports | Owns |
|---|---|---|
| **LoginServer** | 2000 (4.95), 2001 (5.33) | Account creation, login, and the handoff that redirects a client to the game server |
| **Server** (game) | 2005 (4.95), 2006 (5.33) | The world: movement, combat, items, NPCs, chat, everything else |

They share a SQLite database and nothing else. The split buys two things:

* **The game can be restarted without closing the front door.** A code change ships by stopping and
  relaunching the game process; players stay connected to login and reconnect.
* **The internet-facing surface is small.** Login is the process that takes unauthenticated traffic from
  anyone, so throttling, connection admission, and ban enforcement all live there, in front of the
  process that holds the world.

**The handoff.** Login never proxies game traffic. On a successful login it mints a random 5-byte nonce —
exactly the size of the 4.95 client's handoff field — records it against the username and the client's IP,
and tells the client to reconnect to the game port with it. The game server consumes the token once and
refuses it thereafter. `Shared/HandoffTokens.cs` is the whole mechanism; `Tests/HandoffTokenTests.cs`
pins it, including that a token minted for one IP cannot be redeemed from another.

Both ports per process are the *same server* speaking to two different client versions. There is no
separate 5.x build.

## 2. The projects

```
Shared/           ← referenced by everything. No dependencies of its own beyond SQLite + BCrypt.
  ├── Protocol.Tk495/   ← the 4.95/5.33 wire adapter: cipher + framing
  │     ├── LoginServer/     ← login process
  │     ├── Server/          ← game process (+ MoonSharp for Lua)
  │     └── LoginProbe/      ← a headless test client: login → handoff → world entry
  ├── Tools/            ← standalone: the Inter.dat client-redirect patcher
  └── Tests/            ← xunit; references Server + Shared
```

| Project | What it is |
|---|---|
| **`Shared/`** | Everything both processes must agree on: the accounts and characters tables, the SQLite handle, opcodes, the handoff token, path resolution, connection admission, PROXY-protocol parsing, the world calendar. If login and game could ever disagree about it, it belongs here. |
| **`Protocol.Tk495/`** | The wire adapter — `TkCrypt` (the NexonInc cipher, both schemes) and `TkPacket` (framing). Deliberately isolated: it is the one piece that is specific to *these* 2001-era clients, and a future non-NexusTK client would replace this project and keep the rest. |
| **`Server/`** | The game. ~62 files, of which 20 are `Session.*.cs` partials. |
| **`LoginServer/`** | The front door. Also hosts offline account administration (`--set-password`, `--list`) as one-shot arguments, so an operator can fix an account without opening the ports. |
| **`Tools/`** | The client-redirect patcher. Rewrites the server address inside a client's `Inter.dat` so it points at your machine. |
| **`LoginProbe/`** | A headless client that walks login → handoff → world entry. Useful for checking the handshake without launching a real client. |
| **`Tests/`** | xunit. See §5 — these are not what you would guess. |

### `Session` is one class in twenty files

`Server/Session.cs` plus nineteen `Session.*.cs` partials is one class per connection: it reads packets,
dispatches on opcode, and holds that player's state. The partials are a filing system, not a design —
`Session.Combat.cs`, `Session.Items.cs`, `Session.Spells.cs` and so on. When you are looking for the
handler for an opcode, the opcode→handler table is the thing to grep; the file it lives in is incidental.

## 3. The three tiers: code, content, state

This is the split that matters most, and getting it wrong is how things break quietly.

| Tier | Lives in | Changed by | Reload |
|---|---|---|---|
| **Content** | `game-data/*.csv`, `*.lua`, `maps/`, `SObj.tbl` | Edit the file | `@reload`, **no restart** |
| **Live state** | `state/project1998.db` (SQLite) | In-game actions, GM commands | n/a — it is the live truth |
| **Engine** | `Server/*.cs` | Edit code | Rebuild + restart |

> **Golden rule: data describes *what*; code implements *how*.** A monster's HP is data; the combat
> formula is code. A spell's mana cost is data; the packet that draws its animation is code.

[`Modding.md`](Modding.md) is the full map of which file holds what. The important structural point is
that the boundary is **directories that cannot see each other**, enforced by `Shared/RepoPaths.cs`:

```
<repo root>/
  game-data/   CONTENT.  Authored, identical on every deployment, READ-ONLY at runtime.
                         A deploy replaces this wholesale.
  state/       INSTANCE. The database, the character store, the staff rosters. Created by running
                         the server, unique to this deployment, irreplaceable. The entire backup target.
  logs/        Append-only stdout captures. Regenerable, never backed up.
  run/         Control triggers (restart_at, reload_now). A deploy writes them; the running server
                         consumes and deletes them.
```

These were all one `data/` directory once. The cost showed up everywhere downstream: the deploy rsync
carried a hand-maintained exclude list of live-state filenames and could not use `--delete` without
eating the character store, and the content tree needed its own `.gitignore` purely to keep the database
it was sitting on top of out of its history. Separate roots delete both problems instead of managing them.

Every root takes an environment override (`P1998_GAME_DATA`, `P1998_STATE`, `P1998_LOGS`, `P1998_RUN`) so
a container or a test can put any of them anywhere.

## 4. Lua is a first-class part of the engine

Three subsystems are **data-driven through Lua** rather than compiled, hosted on MoonSharp (a pure-C# Lua
implementation, so there is no native dependency and it runs unchanged on the Linux host):

| File | Drives |
|---|---|
| `game-data/spell_verbs.lua` | Spell effects. A CSV row (`SpellParams.csv`) names a verb; the verb is a Lua function. |
| `game-data/item_verbs.lua` | Consumable-use effects (heal / drink / ward / warphome / …), same row-plus-verb model. |
| `game-data/npc_dialog.lua` | NPC conversations. Async — driven by a coroutine, because a dialog is a sequence of round trips to the client. |
| `game-data/mob_ai.lua` | Per-creature AI hooks, for the handful of creatures whose behaviour is genuinely bespoke rather than data. |

`Server/LuaVerbHost.cs` is the reusable host: it owns one `.lua` file defining a global `verbs` table and
hot-reloads it. **This means most new spells and items are a CSV row plus a Lua function, with no
rebuild.** Reach for C# only when the thing you need is a new *kind* of effect.

> **Gotcha:** MoonSharp's `HardSandbox` preset omits the `coroutine` module, which the NPC dialog driver
> requires. If dialog scripts start failing to load after a MoonSharp change, check the sandbox preset.

## 5. What the tests actually test

`Tests/` is **not** a unit-test suite for game logic, and reading it as one will mislead you. It is a set
of guards around the failures that are *silent* — the ones where nothing throws, nothing logs an error,
and the game is simply wrong:

* **`ContentSmokeTests`** — the CI gate. `dotnet build` cannot see a bad CSV row, a renamed Lua verb, or
  a missing `.map` file. This can.
* **`MapCellTests`, `TileTranslationTests`, `WireLookTests`, `CipherTests`** — pin wire formats and id
  translations. A wrong tile number renders a *plausible but wrong* map; a wrong look byte draws the
  wrong weapon. Neither throws.
* **`EraGatingTests`, `KarmaTests`, `AreaBoxTests`** — pin thresholds whose failure is invisible: a wrong
  karma band silently hands out the wrong reward, a wrong hearing range silently drops speech.
* **`HandoffTokenTests`, `PersistenceTests`, `ProxyProtocolTests`** — the security-shaped paths, run
  against the real database with throwaway usernames.

The bar for adding a test here is not "is this code correct" but **"would this fail loudly?"** If yes,
you probably do not need a test. If it would fail *silently*, you do.

## 6. Deployment

CI has two lanes, chosen by what changed:

* **Content-only push** (nothing outside `game-data/`) → mirror the content and `@reload`. **No restart;
  nobody is disconnected.** This is by far the common case, which is the entire reason the lane exists.
* **Anything else** → publish self-contained `linux-x64` binaries, stage a new release on the host, and
  schedule a *warned* restart (default 5 minutes of in-game warnings).

Releases are **self-contained**: the .NET runtime rides along in the payload, so `releases/<sha>/` is
exactly what that commit ran. With a framework-dependent build, a host runtime upgrade silently changes
how every existing release behaves — including the one you roll *back* to in an emergency.

Everything host-shaped — HAProxy, the systemd units, the deploy and backup scripts — lives in a separate
repository, [Project1998-infra](https://github.com/project1998/Project1998-infra). The dividing line is
one question: **does the game server process behave differently?** If no, it belongs there. This repo
should be clonable and runnable by anyone on their own PC with `dotnet run`, and every file encoding
*our* host works against that.

The one place that rule is bent deliberately: `Shared/ProxyProtocol.cs` is in this repo, off by default.
It is a real code change driven purely by our choice to front the server with a proxy — but anyone else
who puts a load balancer in front hits the identical problem.
