# NexusTK 4.95 Client — Server Protocol & Implementation Reference

This document describes everything needed to stand up a server that a **2001-era NexusTK 4.95
client** (`NexusTK_local.exe`) will connect to, enter the game world with, and play in. It is
written to be self-contained: a developer should be able to implement a working server from this
document alone.

Everything here was established by **reverse-engineering the client binary** (static x86 disassembly
+ live Frida instrumentation) and confirming against a live client. Where a fact came from a
controlled experiment, that is noted so you can trust it; where something is a hypothesis or unknown,
that is called out too.

> **Binary under study:** `NexusTK_local.exe`, PE32 x86, **ImageBase `0x400000`, no ASLR** (it always
> loads at `0x400000`, so file offsets in this doc are absolute virtual addresses you can use directly
> in a disassembler or Frida). All `0x4xxxxx` addresses below refer to this binary.

---

## Table of Contents

1. [High-level architecture](#1-high-level-architecture)
2. [Wire framing](#2-wire-framing)
3. [Encryption (the "NexonInc" cipher)](#3-encryption-the-nexoninc-cipher)
4. [Connection lifecycle](#4-connection-lifecycle)
5. [The "enter world" trigger — the crux](#5-the-enter-world-trigger--the-crux)
6. [World-entry packet sequence (the working burst)](#6-world-entry-packet-sequence-the-working-burst)
7. [Packet reference](#7-packet-reference)
8. [The appearance system](#8-the-appearance-system)
9. [Character creation & the creation packet](#9-character-creation--the-creation-packet)
10. [Movement model](#10-movement-model)
11. [Speech & actions](#11-speech--actions)
11b. [Shared world (multiplayer)](#11b-shared-world-multiplayer--players-see-each-other--the-same-mobs-)
&nbsp;&nbsp;&nbsp;&nbsp;11b.1 [Persistent spawns, mob AI, collision & viewport streaming](#11b1-persistent-spawns-mob-ai-collision--viewport-streaming-)
11c. [Items — bag, gear, ground, combat](#11c-items--bag-gear-ground-and-combat--built-awaiting-live-495-verification)
11d. [Spells & skills](#11d-spells--skills--built-awaiting-live-495-verification)
11e. [NPCs & dialog](#11e-npcs--dialog--live-confirmed-on-495)
11g. [Durability, warp gating, whisper, spell resist](#11g-durability-warp-gating-whisper-and-spell-resist-added-2026-07-25)
11h. [Bulletin boards](#11h-bulletin-boards-added-2026-07-26--unverified-reply-shapes)
11i. [F2 / Subpath chat](#11i-f2--subpath-chat-added-2026-07-25--awaiting-live-confirmation)
11j. [Experience & leveling](#11j-experience--leveling-added-2026-07-25--awaiting-live-confirmation)
11k. [F1 / Central Functions menu & Silver Thread revival](#11k-f1--central-functions-menu--silver-thread-revival-added-2026-07-25--awaiting-live-confirmation)
11l. [Party & trade](#11l-party--trade-added-2026-07-26--awaiting-live-confirmation)
11m. [Inter-continent travel (world map)](#11m-inter-continent-travel-world-map-added-2026-07-26--native-screen-confirmed-broken-dialog-fallback-is-primary)
12. [Maps](#12-maps)
13. [Full opcode → client-handler table](#13-full-opcode--client-handler-table)
14. [Learnings, gotchas, things tried & failed](#14-learnings-gotchas-things-tried--failed)
15. [Reverse-engineering toolkit & methodology](#15-reverse-engineering-toolkit--methodology)
16. [Open problems / where to continue](#16-open-problems--where-to-continue)
17. [Reference servers & version gaps](#17-reference-servers--version-gaps)
18. [Key binary addresses (quick reference)](#18-key-binary-addresses-quick-reference)

---

## 1. High-level architecture

NexusTK uses **two TCP channels**, historically two separate servers:

| Channel | Default port | Role |
|---|---|---|
| **Login** | `2000` | Account creation, login, character select. Ends by handing the client off to the game server. |
| **Game** | `2005` | The actual world: enter-world, map, movement, chat, combat. |

The client connects to the login channel first, authenticates, and receives a **handoff** packet
telling it the game server's address/port. It then opens a *second* TCP connection to the game
channel and continues there. **These are separate connections** (separate sockets, separate session
objects on the server) — anything created during login (e.g. a character record) must be persisted
somewhere both channels can read (a DB or file), because the game-channel connection cannot see the
login-channel connection's memory.

Both channels use the **same framing and the same cipher** (see below). This is a 4.95-specific fact
— later versions (7.x) use a different game-channel cipher.

---

## 2. Wire framing

Every packet, both directions, both channels:

```
AA | length(u16, big-endian) | opcode(u8) | increment(u8) | body[...]
```

- `AA` — constant start-of-packet byte.
- `length` — big-endian u16. **Counts `opcode + increment + body`** (i.e. everything after the length
  field). Total bytes on the wire = `3 + length`.
- `opcode` — the packet type.
- `increment` — a per-packet sequence/nonce byte used by the cipher (see §3). The server chooses its
  own increment sequence for outgoing packets; the client does likewise. It generally starts at 0 and
  increases by 1 per packet on each side, but treat it as "whatever the sender put there" — you must
  feed the *received* increment byte back into the decrypt routine.
- `body` — opcode-specific payload, **encrypted** (except for a few plaintext opcodes, see §3).

There is **no trailer and no checksum** in 4.95. (7.x appends 3 "index" bytes — do **not** do that
for 4.95; it desyncs the client's frame assembler.)

**Multi-byte integers in packet bodies are big-endian** (u16 and u32). The client's field readers are:
- `0x475c90` — read u8
- `0x475ca0` — read u16 big-endian
- `0x475ce0` — read u32 big-endian

Length-prefixed strings are `len(u8) bytes[len]`, ASCII.

Multiple packets are commonly concatenated in a single TCP segment; parse in a loop: while at least 5
bytes and `buf[0]==0xAA`, read length, consume `3+length`, repeat. Handle partial packets by
buffering the remainder.

---

## 3. Encryption (the "NexonInc" cipher)

**4.95 uses ONE cipher on both channels**: a simple, self-inverse, 3-stage XOR keyed by the static
ASCII string `"NexonInc."` (9 bytes, including the trailing period).

> This was proven by RE: the key buffer at `0x50211c` is only ever built from `"NexonInc."`
> (replicated ×4 = 36 bytes; two build sites `0x475740` and `0x478b10`, both hardcode the string;
> key length = `strlen("NexonInc.")` = 9). Decrypt routine `0x478680`, encrypt `0x478760`, XOR
> primitive `0x478850`, identity table `0x4f3358`.
>
> There is **NO** name-derived key, **NO** per-session table cipher, **NO** `Urk#nI7ni` key, and **NO**
> trailer/index bytes in 4.95. Those are all 7.x-only and will break a 4.95 client.

The algorithm, applied to the **body only** (opcode and increment are sent in the clear), where `inc`
is the packet's increment byte and `key = "NexonInc."`:

```csharp
byte[] Crypt(ReadOnlySpan<byte> data, byte inc, byte[] key) // key.Length == 9
{
    var o = data.ToArray();
    for (int i = 0; i < o.Length; i++)
    {
        o[i] ^= key[i % 9];              // stage 1: keystream
        o[i] ^= (byte)(i / 9);           // stage 2: block counter (which group of 9)
        if ((i / 9) != inc)              // stage 3: mix in the increment, except on block == inc
            o[i] ^= inc;
    }
    return o;
}
```

It is **self-inverse**: the same function encrypts and decrypts. To send: `Crypt(plaintextBody, inc,
key)`. To receive: `Crypt(encryptedBody, receivedInc, key)`.

**Plaintext opcodes (bypass the cipher entirely):** received opcodes `0x00`, `0x03`, and `0x40` are
handled as plaintext by the client. Most importantly for the server, the **game-channel arrival packet
`0x10` is sent by the client in plaintext** (it carries the handoff token as readable ASCII). Treat
`0x10`'s body as plaintext. Everything else is ciphered.

---

## 4. Connection lifecycle

> **Process split (2026-07-27):** the login channel (2000/2001) and game channel (2005/2006) now run as
> **two separate processes** (`LoginServer` and the game `Server`) sharing one SQLite DB (`data/nexus.db`).
> They can crash/restart independently. The login server is the internet-facing front door and does not load
> the game world/content.

### 4.1 Login channel (port 2000) — handled by the LoginServer process

1. **Server → client, on connect:** a plaintext welcome banner.
   ```
   AA 00 13 7E 1B "CONNECTED SERVER\n"
   ```
   (opcode `0x7E`, then the ASCII string.) The client expects this immediately on connecting to 2000.

2. **Client → server `0x62`** — client signature, body `"baram\0"`. (The client identifies as the
   "baram" engine.) No meaningful reply required.

3. **Client → server `0x00`** — client version handshake (plaintext). No meaningful reply required.

4. Then either **account creation** (§9) or **login**:

   **Login — client → server `0x03`:** body = `nameLen name pwLen pw 00`. Example decrypted:
   `03 04 "test" 07 "dragon5" 00`. **The client DOES send a password** (3–8 chars); the server verifies it
   with **BCrypt** against the `accounts` table. A never-registered / legacy-migrated account has no hash →
   **trust-on-first-use**: the first login sets the password to what was sent (so pre-auth characters aren't
   locked out). On a wrong password the server replies with a `0x02` message box ("Incorrect password.") and
   does **not** hand off. (Creation `0x02` carries `nameLen name pwLen pw 00 00 00` — same password field.)

   **Server → client `0x03` (handoff):** tell the client where the game server is. Body layout
   (as implemented and confirmed working):
   ```
   ip[3] ip[2] ip[1] ip[0]        // 4 bytes, IP octets REVERSED (e.g. 127.0.0.1 -> 01 00 00 7F)
   port(u16, big-endian)          // game server port, e.g. 2005 -> 07 D5
   17 00 09                       // (constants observed in the working handoff)
   "NexonInc."                    // the 9-byte key string, echoed
   nameLen name                   // the username
   <token: 5 bytes>               // handoff token, echoed back by the client in 0x10
   ```
   On receiving this the client opens a new TCP connection to the game port.

   **Handoff token — single-use nonce (2026-07-27).** The 5-byte token slot was a static constant
   (`00 01 12 11 00`); it is now a **server-minted, single-use, 60s-TTL nonce** bound to the username
   (SHA-256 stored in `handoff_tokens`), so the game port can't be reached by claiming any username.
   **Two hard wire facts, learned live:**
   - **Do NOT grow the token past 5 bytes** — the client parses the `0x03` reply as a fixed-size redirect
     struct; a 16-byte token corrupts the parse and breaks login.
   - The client preserves only the **first 4 bytes** of the slot and **forces the 5th to 0** (a minted
     `xx xx xx xx <rand>` comes back `xx xx xx xx 00`). So the nonce is **4 significant (non-zero) bytes +
     a trailing 0**, and only those 4 bytes are hashed/validated. 32 bits — safe given single-use + short
     TTL + rate limiting.

### 4.2 Game channel (port 2005)

1. **The client speaks first** — do **not** send anything on connect. The client sends **`0x10`
   (arrival)** in **plaintext**:
   ```
   10 | 09 "NexonInc." | nameLen "name" | <handoff token bytes>
   ```
   Parse the username from it: `klen = body[0]; ulen = body[1+klen]; user = body[2+klen .. +ulen]`.
   The trailing bytes are the **handoff token** (`body[2+klen+ulen ..]`); the game server
   **validates and single-use-consumes** it against the username (must exist, be unexpired, unconsumed,
   and bound to this user) — otherwise it **closes the connection**. This is what stops a client from
   connecting straight to the game port and claiming any username. (`NEXUS_ENFORCE_HANDOFF=0` downgrades a
   failure to a warning as a fallback.)

2. The server now drives the **world-entry burst** (§5, §6).

3. **Re-login on the game port (Alt+X).** Exiting to the select screen does **not** drop the game socket —
   the client re-sends **`0x03`** (login, full credentials) on the still-open game connection and waits for
   a handoff redirect, exactly as on the login channel. The game server therefore also handles `0x03`
   (`Session.HandleReLogin`): re-authenticate, mint a fresh single-use token, and redirect back to its own
   game port. Without this the re-login hangs (the client never reconnects to the login server, so that
   process shows no new activity). The original single-process server answered `0x03` on every port; the
   split had to restore it on the game side.

### 4.3 Robustness / DDoS hardening (app-layer, 2026-07-27)

App-layer defenses on both front doors (they complement — do not replace — infra-layer defense: upstream
scrubbing, OS SYN-flood protection, a firewall restricting the game ports to post-handshake traffic).

- **Guarded accept loops** — a transient `AcceptTcpClientAsync` throw is logged and skipped, never faulting
  the listener task.
- **Connection admission (`Shared/ConnGuard.cs`)**, checked on every accept before a session is spawned:
  - **Global cap** (load-shed): past `NEXUS_<GAME|LOGIN>_MAXCONN` (default 2000) accept-then-close.
  - **Per-IP concurrent cap**: `NEXUS_<…>_PERIP` (default **8** — sized to reliably support ~2 players/IP
    incl. the brief login→game socket overlap and a lingering half-open "ghost" socket; it is **not** a hard
    player quota — enforce that in login logic if wanted).
  - **Per-IP open-rate limit** (fixed window): `NEXUS_<…>_RATE` opens per `_RATEWIN_MS` (default 30 / 10 s),
    catching connect/disconnect churn floods the concurrent cap wouldn't.
  - **Loopback is exempt** from the per-IP + rate gates (local dev, the client test box, and the same-box
    login→game hop all originate from 127.0.0.1) but still counts toward the global cap. Rate table is
    soft-capped (fails **open** past 100k distinct IPs) so it can't itself become a memory-DoS.
- **Handshake watchdog** — a freshly-accepted connection must send its first **valid framed** packet within
  `NEXUS_HANDSHAKE_MS` (default 15 s) or it is dropped (slow-loris defense). Only the first packet is gated
  (via an `_established` flag), so an in-world / AFK / Alt+X-idle player is never disconnected. Reads after
  the handshake are untimed. The watchdog **closes the socket** (unblocking the pending read) rather than
  relying on `NetworkStream`'s unreliable read-cancellation.
- **Non-blocking writes (the tick-stall fix — most important).** `Session.Send` no longer does a synchronous
  `_stream.Write` on the shared 600 ms `World.TickLoop` thread. It enqueues onto a **bounded per-session
  channel** (cap 2048) drained by one dedicated writer task that owns the only socket writes. A client whose
  TCP receive buffer is full (slow, or deliberately not reading) used to **block that write and freeze mob
  AI for everyone on the map**; now the tick thread does an O(1) `TryWrite` and moves on, and a client whose
  queue overflows is dropped — the world never stalls. The single-reader channel also guarantees frames
  never interleave mid-packet (what the old per-session send lock did).

---

## 5. The "enter world" trigger — the crux

**This is the single most important fact in this document, and the reason naïve ports of 6.x/7.x
servers fail with a silent black-screen hang.**

The client has two receive-dispatch objects:

- A **connection-state object** (vtable `0x4cbd8c`) that handles **only opcode `0x02`**.
- A **game-world object** (vtable `0x4cc3b8`) whose dispatcher `0x44b9c0` handles opcodes `0x03`–`0x68`
  (via `remap[opcode-3]` at `0x44bc80` → jump table `0x44bbd4` → real handler).

**Right after `0x10`, the game-world object does not exist yet.** It is created *only* when the client
receives **opcode `0x02` with first payload byte `0x00`** (handler `0x444de0` → `0x4406c0` → world
ctor `0x44a090`). Until then, every world packet (`0x15`, `0x04`, `0x33`, …) is silently dropped
because there is no dispatcher to receive it.

The 6.x/7.x reference servers never send `0x02`+`00`, which is exactly why porting them yields a
client that connects, goes quiet, and never renders. **The server must send `0x02` `[00]` FIRST**,
before any other world packet.

You can confirm it worked: Frida shows the world ctor `0x44a090` fire ("game-world object
CONSTRUCTED"), and the client begins loading the map and sending game packets back.

---

## 6. World-entry packet sequence (the working burst)

After the client's `0x10`, send this exact sequence (all game-channel, all NexonInc-encrypted, no
trailer). This is the confirmed-working `HandleArrival` burst:

| # | Opcode | Body (decrypted) | Purpose |
|---|---|---|---|
| 1 | `0x02` | `00` | **Enter-world trigger** — builds the world object (§5). Must be first. |
| 2 | `0x1E` | `06 00 00` | Ack / handshake. |
| 3 | `0x20` | `10 32` | Time-of-day (shows the day/night icon). |
| 4 | `0x05` | `entityId(u32BE) 00 00 00 02 00 00 00 00` | **Your own entity id.** Binds camera/input to the self. Without it the client stays black and won't move even after the map loads. |
| 5 | `0x15` | enter-map (see §7) | Loads `Maps\TK<mapId>.map`. |
| 6 | `0x04` | coords (see §7) | Sets camera scroll. |
| 7 | `0x33` | self appearance (see §7, §8) | Spawns & renders the player. |

Ordering notes:
- `0x05` (entity id) **must** precede `0x33`, and the id in `0x33` **must equal** the id in `0x05`,
  or the client won't recognize the entity as "self".
- `0x04` sets the camera scroll and **must** be sent before `0x33` so the spawn's placement check
  passes (see the viewport gotcha in §8/§14).

---

## 7. Packet reference

Bodies below are **decrypted** payloads (what you build before encrypting). `u16`/`u32` = big-endian.

### 7.1 Client → server

| Opcode | Name | Body | Notes |
|---|---|---|---|
| `0x62` | Signature | `"baram\0"` | Login channel, first. |
| `0x00` | Version | (plaintext) | Login channel. |
| `0x02` | NameCheck | `nameLen name pwLen pw 00 00 00` | Creation step 1. **Carries name AND password.** |
| `0x04` | CreateAppearance | 5 bytes (see §9) | Creation step 2. |
| `0x03` | Login | `nameLen name pwLen pw 00` | Login channel. |
| `0x10` | Arrival | `09 "NexonInc." nameLen name <token>` | Game channel, **plaintext**, client speaks first. |
| `0x32` | Walk step | `dir(u8) stepCounter(u8) X(u16) Y(u16) pad` | Self-walk request (see §10). |
| `0x06` | Walk + view refresh | same as `0x32` + `x0 y0 x1 y1 checksum` | Sent every few steps instead of `0x32`; handle identically for movement. |
| `0x0E` | Chat | `chatType(u8) msgLen(u8) msg` | (see §11). |
| `0x13` | Attack | `13 00` (bare trigger) | Spacebar. (see §11). |
| `0x1d` | Emote | `idx(u8) 00` | The `:` emote wheel. Reply with a `0x1A` action, `type = idx + 11` (see §11). |
| `0x43` | Click/inspect entity | `01 entityId(u32) 00` | **Left-click only** — live-confirmed 2026-07-26: right-click in this client is pure client-local walk-to-click (never reaches the server as anything but movement/`0x69` obstruction; see §10). If the id is an **NPC** → open its dialog (`0x30`, §11e); id 0/self → own click-profile `0x34` (§9.5); a real **mob** → name-only mini-text reply (deliberate divergence from stock RTK's GM-only `onLook`, see §11e). |
| `0x3a` | NPC dialog reply | `kind(u8) … step(u8@8) menu/len(u8@10) [text@11]` | Answer to a `0x30` we sent. `kind`: `01` text next/close · `02` menu pick (`@10` = 1-based index) · `04` input (`step@8`==2 = submit, `len@10`, text `@11`). See §11e. |
| `0x2d` | Profile key | `2d 00` (byte 0 = self) | Pressing the profile key. Reply with the self-profile `0x39` (see §9.5). |
| `0x4f` | Change profile | `picSize(u16) pic[] blurbLen(u8) blurb[] 00` | Player saved their profile edit. Persist the picture + blurb; reply with a `0x02` message. (see §9.5) |
| `0x11` | Turn / face | `side(u8) pad` | First press in a new direction turns in place (no step). Echo `Be32(id), side, 00` so the client turns (see §10.4). |
| `0x1b` | Setting toggle | `subCmd(u8) pad pad` | Client toggle. `0x00` = the 'r' Ride key (RTK `clif_changestatus` case 0x00 → `clif_findmount`; `Session.TryRideHorse` — mounts by despawning a real nearby "horse" world mob, dismounts by spawning one back in front of you, §8); `0x02` = group/sociable (Shift+G, §9.5); `0x07` = realm-center (F4, §10.5); `0x08` = exchange/trade (§9.5); `0x09` = fast-move (§10.1). Group/exchange are persisted profile status flags; the others are session/camera state. |
| `0x38` | Hard refresh | `38 00` | Ctrl+R. Grays the screen; reply with the in-place refresh burst `0x15`+`0x04`+`0x33`+entities (recenters). See §10.6. |
| `0x0b` | (no-op in 4.95) | `0b 00` | Handler is a no-op. |

### 7.2 Server → client

**`0x05` — self entity id**
```
entityId(u32) 00 00 00 02 00 00 00 00
```

**`0x15` — enter-map**
```
mapId(u16) width(u16) height(u16) flagA(=05) flagB(=00) nameLen name light(u16, e.g. 232=0x00E8)
```
Loads `Maps\TK<mapId>.map` from the client's install. Example working values: mapId 32, 33×33,
name "Nexus", light 232.

**`0x04` — coords / camera** (call it `SendXy`)
```
X(u16) Y(u16) scrollAnchorX(u16) scrollAnchorY(u16) 00
```
Handler `0x44faf0`: sets **camera scroll = (X − anchorX, Y − anchorY)** and commits the self's logical
tile. Keep the anchor constant (= the spawn tile) so the camera follows the player and the self stays
put on screen. See §10 and the viewport gotcha in §14.

**`0x33` — create/look (self or any entity)** — full layout, decoded from handler `0x44fef0`:
```
X(u16) Y(u16) dir(u8) entityId(u32) type(u8) <appearance> renderKind(u8) nameLen name
```
- `type = 0` → the **7-byte player appearance** form (parser `0x436120`). This is what players use.
- `type = 1` → a `u16 spriteId + u8` form (parser `0x4361b0`) — the **monster/NPC direct-sprite**
  form, *not* a rich hair/face form.
- `renderKind` (byte right after appearance): **must be 1, 2, or 3**, else the handler bails before
  allocating the sprite (→ invisible). `1` = player sprite. This byte tripped us up badly (see §14).
- The 7 appearance bytes are fully decoded in §8.

**`0x0C` — move/animate an entity**
```
entityId(u32) X(u16) Y(u16) dir(u8)
```
Handler `0x4502c0` → `0x462320`: starts the walk animation toward (X,Y) facing dir. **Animation only** —
does not advance the client's logical tile by itself (see §10). **Overshoot:** the walk ends one tile *past*
(X,Y) in `dir` and commits there, so for a peer/mob (no `0x04` self-commit) you must send the **SOURCE** tile,
not the destination — see the boxed note in §11b.

**`0x0D` — over-head speech**
```
chatType(u8) entityId(u32) msgLen(u8) msg
```
Handler `0x450170`: shows `msg` in a bubble over the entity.

**`0x1A` — action** (attack swing, sit, etc.)
```
entityId(u32) type(u8) time(u16) param(u8)
```
Handler `0x4503a0`: plays the action (client scales `time` ×10; `param` @+8 is the 3rd arg to the
entity's action vtable method `[vtbl+0x78]`). `type`: 0=stand, 1=attack, 2=throw, 3=shot,
4=sit/**pickup** (RTK `clif_parsegetitem` reuses 4 for the pick-up crouch), 5=**drop** (`clif_parsedropitem`),
6=magic, 8=eat. **Emotes are actions too** — `type` 9=respect, 10=triumph, 11=laughter, 12=grief,
13=shame, 14=affection, 15=boredom, 16=sleepiness, 17=surprise, 18=rage, 19=sarcasm, 20=shrug,
21=annoyed, **22=dance**, 23=strange, 24=kiss, 27=charge, 28=attack-after-charge. (See §11 for `0x1d`.)

**`0x13` — combat damage / over-head HP bar** (SOLVED 2026-07-25, decoded from handler `0x4508f0`).
```
entityId(u32BE) critical(u8) percent(u8) hitSound(u8)
```
One packet does three things on the target entity:
- **HP bar** — `percent` (0–100) sets the over-head health bar fill (via `0x44e870`). The client **skips the
  bar if `percent > 100`**, so clamp; `0` = empty (dead). This is the "remaining HP bar above a monster's head."
- **Hit animation** — plays overlay animation `0x8f − critical` (SIGNED) over the entity (queued for `0x78`
  ticks via `0x4622d0` → `0x41b5d0`), i.e. `critical` selects a hit spark. RTK uses `33` (normal) / `255` (crit).
- **Hit sound** — `hitSound` is played through the sound manager if nonzero (RTK appends a `u32` damage tail
  here; the 4.95 client reads only its **high byte** = `0` normally, and ignores everything past `body[7]`).

RTK's `clif_send_mob_health` / `clif_send_pc_healthscript` build the same shape. `critical` is calibratable
live via `NEXUS_HIT_CRIT`; `!hit <pct> [crit]` auditions the bar + hit anim over the faced mob. **Death beat:**
4.95 monsters have **no** death frame-set (`monsfrm.tbl` defines only walk/attack), so a "death animation" is:
send `percent = 0` (empty bar + final hit spark), then delay the `0x0E` despawn (`NEXUS_DEATH_DELAY_MS`, default
600 ms) so the corpse doesn't pop out instantly. Players die to **ghost form** (appearance `[1]=1`, §8) instead.

**`0x19` — background music.**
```
type(u8) pad(u8=0) bgm(u16BE) volume(u8)
```
Handler `0x450ad0` (dispatch-table stub `0x44bb06` → real handler): `body[1] = type` selects the audio
backend — **2 = MIDI** (the stock `1.mid`..`12.mid` in `NexusTK.snd`, played via the single-instance MIDI
player `[0x4fd3ac]`), 1 = `%03d.MP3`, **0 = a positional wav/sfx**. Types 2/1 read `sound(u16BE)@+3` +
`volume@+5` directly; `bgm 0` stops the music.
`volume` is a raw byte the client **log-scales**: the handler computes `dB = 2000·log10(vol/100)` (so
`vol=100` = 0 dB = nominal full, `vol>0` audible, `vol=0` silent), and the MIDI path then compresses it
further against a base at `[snd+0x270]` — so the audible range is narrow and `>100` (up to 255) is the knob
to push louder. The client dedups (`cmp bgm,[midi+8]`) so re-sending the playing track is a no-op. Send it
on map entry / change (the server picks the track per map — `Content.BgmFor`); there is no original
map→track table in the client files, so the assignments are the server's own. Songs are numbered `N.mid`;
some tracks may reference instrument samples a given install lacks, but that only manifests at high volume.

**`0x19` type 0 — spell sound-effect (SOLVED 2026-07-25, live-verified).** The type-0 path is **not** a flat
"play sound N" — it's a positional-sound TLV parser, and RTK's `clif_playsound` (a later-client `type 3`
layout) mis-parses on 4.95 into a garbage sound object (mode 4, random id) → silent. The real 4.95 wire, cracked
by RE (`re/frida_sound.py`; hooks the `0x19` handler → TLV tail `0x450c48` → spatial builder `0x44e6c0` → ctor
`0x463950` → play wrapper `0x463ab0` → play fn `0x4798c0`):
```
19 | 00(type=sfx) | 03(P0) | soundId(u16BE) | 64(vol) | 03 00 01 | 00 00 00 00
```
`type 0` reads `soundId@body[3..4]` + `volume@body[5]`, then runs the TLV tail starting at `body[P0]+3` (P0=3
puts it just past the 5-byte header). The tail's parsed **`C` field → the sound object's MODE** (`[obj+0x148]`);
the play wrapper dispatches modes 0–4 and **only mode 1 reaches the audio player** — so the tail bytes
`03(tagA) 00(B0) 01(C=mode 1)` with all skip bytes `0` are what make it play. `soundId`/`type`/`gain` reach the
play fn via the "type-group" (`[ebp-0x1c/18/14]`, only populated by the type-0/1/2 prologue — `type 3` leaves
them garbage, the other reason RTK's format failed). `volume` is log-scaled: `dB = 2000·log10(vol·0.01)` (const
`@0x4cc408 = 0.0`, so `vol>0` audible; `100` = 0 dB). `Session.SendSound`/`SoundAt`; `BroadcastFx` sends the
cast's effect (`0x29`) + this sound (`0x19`) to the whole map. **NOT** the `0x1A` action 4th byte: the client
picks an action's sound from a fixed *action-type → sound* table (emote 22→311, sit 4→406), and **magic type 6
→ sound 0 → silent** no matter what byte we send (proven: `byte8=0` even on the audible emotes). Effect
animation and sound are **separate** — the `0x29` effect ctor chain makes no play call, so sound rides its own
`0x19`. Per-spell sound id from RTK's `pcalign` ladder (`Content.EffectSound`) or the spell's explicit
`fx.Sound`. Sound files are `NexusTK.snd` (a PAK of `NNN.wav` + `N.mid`, non-contiguous, ids up to ~720);
`re/extract_snd.py` dumps them. **Best-effort only** — RTK's 6.x/7.x sound numbering doesn't perfectly match
4.95's, and fan sites carry no sound data. Debug: `!snd <id>` auditions raw client sound ids live.

**Missing-entity policy (2026-07-25): no fallbacks.** When an effect/sound id doesn't exist in the 4.95 client
(e.g. `Effect.tbl` has only ids 0–127, so RTK anims ≥128 like `dark_veil`'s 136 are absent; a few RTK sounds
have no matching `.wav`), we send it as-is and let the client show/play **nothing**, rather than substituting a
stand-in — so a broken entry is visibly empty and easy to spot. `!efx <id>` auditions raw `Effect.tbl` ids
(0–127) live to help identify the real 4.95 effect for a spell whose RTK id doesn't line up (a version-gap
asset task deferred to when the correct 4.95 effect assets are sourced).

**`0x33` type-1 form** (parser `0x4361b0`): `… type(=01) app0 app1 spriteId(u16) extra(u8) renderKind nameLen name`
(5 appearance bytes; `app0`/`app1` are clobbered by the `u16` at +2). **⚠ This does NOT draw creatures.**
The `0x33` sprite ctor (`0x463380`, *any* renderKind 1/2/3) always builds from the **player sprite archive
`0x4f2a84`** — so `0x33` can only render players and human-looking NPCs. Sweeping type-1 sprite ids
`1..140` all rendered *invisible* (a broken player compose). `0x33` is **not** a monster path either —
the real monster spawn is **`0x07`** (see above / §11a).

**`0x16` — ground ITEM / object spawn** (handler `0x450a00` → `0x44dbc0` → ctor `0x463020` → `0x462ec0`).
> **⚠ NOT a monster.** Originally mislabeled "creature spawn" — live RE proved otherwise (see §11a). The
> object it creates draws its sprite from category **`"I"` = Item.epf** (via `0x435ab0`, id+`0x4000`), has no
> collision and no AI. For real monsters (drawn from **`"M"` = Monster.epf**) use **`0x07`** instead (above).

Layout (offsets from opcode byte; multi-byte big-endian), verified from the handler + ctor:
```
+1 owner(u32=0)  +5 GRAPHIC(u16)  +7 entityId(u32)  +0xb X(u16) +0xd Y(u16)
+0xf X'(u16) +0x11 Y'(u16)  +0x13 flags(u32=0)  +0x17 dir(u8)
```
The `u16` at **+5 is the item graphic id** (a frame index into Item.epf; the client adds `0x4000` and looks
it up in category `"I"`), stored at sprite+`0x130` by `0x462ec0`; `+7` is the entity id (find/despawn key).
`(X,Y)` = the resting tile (entity+`0x10c/+0x110`); `(X',Y')` = the "walked-from" tile. **`(X',Y')` MUST
differ from `(X,Y)`** — the ctor computes `[obj+0x148] = |X-X'|+|Y-Y'|` and the per-frame position code
`idiv`s by it, so `(X',Y')==(X,Y)` → divide-by-zero → **client crash** (send the from-tile 1 step away).
No name field, no viewport gate (`0x44dbc0` skips the `0x424310` check), so the object can be placed anywhere.

**`0x07` — creature / monster list** ✅ **THE REAL MONSTER SPAWN** (handler `0x44fdb0`). Confirmed live:
draws real animated creatures from **`"M"` = Monster.epf**. Layout (offsets from opcode byte; big-endian):
```
+1 count(u16)   then `count` × 12-byte entries:
  +0 X(u16)  +2 Y(u16)  +4 entityId(u32)  +8 look(u16)  +0xa color(u8)  +0xb dir(u8)
```
The handler loops `count`, building each entity with the **same factory `0x44d7d0` that `0x33` (players)
uses** — the difference is the `look` field. `0x44d7d0` classifies the look descriptor:
- **`look ∈ [0x8000, 0xbfff]` → descriptor type 1 → direct creature sprite.** Entity ctor `0x461a50`
  (entity vtable `0x4cd098`), whose draw `0x461c70` sees `[ent+0x178]!=0` and routes to the **monster
  resolver `0x434020`/`0x4342e0`** → pushes `MONSTER.EPF` (`0x4f1d18`) → resolves the frame via `0x433d00`
  (Monster.tbl). **So `look = 0x8000 | monsterId`, where `monsterId` is the Monster.tbl index (0..326).**
- `look < 0x8000` or `> 0xbfff` → descriptor type 2 → `0x462ec0` (vtable `0x4cd118`) = the **static item/object
  base** (tick `0x4601a0` = no-op). Don't use for monsters — but this **is** the correct path for
  **ground items at rest** (it renders statically from Item.epf and never despawns, unlike the `0x16` walk
  projectile; see §11c). `IconWire(N)` yields a `look` in this range (`0xc000+`).

`color` = palette (→ resolver), `dir` = facing/state (→ ent+`0x18d`). **There IS a viewport gate** here
(`0x424310`, unlike `0x16`): entries outside the camera rect are silently skipped, so spawn inside view.
Verified live: `look 0x8000`→Monster.tbl frame 6, `0x8001`→26, `0x8002`→46 … i.e. **frame = 6 + 20·monsterId**
(the idle "Starting" frame per Monster.tbl). Combat (melee/spell → `0x13` HP bar + hit anim → death beat →
`0x0E` despawn, see §7.2 `0x13`) works against these because they're real entities with collision. This is the `0x33`-monster path from Mithia 7.x
(`clif_cmoblook_sub`: `look + 0x8000`) — it moved to `0x07` in 4.95 (see §17). Builder: `Session.SendCreatureList`.

**`0x0E` — despawn list** (handler `0x450440`):
```
count(u8) entityId0(u32) entityId1(u32) …
```
Destroys each entity by id (`0x44d9f0`). The loop **stops early on a `0` id**, so never send id `0`.
(Note: client→server `0x0E` is *chat*; the same opcode means despawn only in the server→client direction.)

**`0x29` — effect animation over an entity** (handler `0x4504b0` → `0x44e0a0`). **NOT a floating damage number**
— NexusTK has no floating combat numbers, and the disassembly proves it:
```
entityId(u32) effectId(u8, 1-based) A(u16) B(u16) C(u16)
```
The `u8` is a **1-based index into the client's effect table** (`Effect.tbl`, 128 effects, drawn from `Effect.epf`).
Trace: `0x4504b0` reads the fields, resolves the entity, calls `0x44e0a0`; that calls constructor `0x4354b0`,
which does `idx = u8 − 1; if (idx < 0 || idx ≥ count) bail; entry = table[idx]` and copies the 36-byte effect
template (9 dwords). A number renderer would `itoa` the value — this indexes a table, so it's a graphic. `u8 = 0`
⇒ no effect. `A` is scaled ×1000 = the vertical pop offset; `B`/`C` are style. Send `A=B=C=0` to center it.
**The wire `u8` maps directly to RTK's `sendAnimation(N)` id** (`NEXUS_EFX_WIRE_OFFSET = 0`) — proven live: Ion
(`pcalign 0` → anim 4 = unaligned zap) sent with a `+1` offset drew the anim-5 graphic (unaligned heal), so
`u8 = N` draws the anim-N effect (the handler's internal `−1` is cancelled by the table being loaded 1-based).
The effect-id + sound-id per spell come from RTK's `global_zap`/`global_heal` `pcalign` ladder (ported to
`Content.ZapEffect`/`HealEffect`; e.g. spark = 28, unaligned zap = 4, unaligned heal = 5, kwi-sin heal = 65).
Most RTK `sendAnimation(N)` ids fall in 0–127 and line up with `Effect.tbl` (128 effects), and low ids are
confirmed identity (unaligned heal 5, spark 28). But the id spaces are **not** a perfect match: RTK's 6.x/7.x
client has more/renumbered effects, so some ids land on the wrong effect (aligned-heal range) or are out of
range entirely (`dark_veil` 136, plus a handful of GM spells) → the 4.95 client draws nothing. Per the
no-fallback policy above, these stay empty until the correct 4.95 effect assets are sourced. `!efx <id>`
auditions raw ids to calibrate.

> **History:** this opcode was previously documented (and used) as a "floating number" — sending the damage
> value as the `u8`, which the client read as effect `#(dmg−1)`, an unintended graphic. Corrected 2026-07-25 by
> disassembling the handler after the absence of in-game damage numbers was questioned. The old melee `SendNumber`
> hack is now **gone**: hits send the `0x13` combat packet (over-head HP bar + hit spark, §7.2); spell casts send
> real `Effect.tbl` ids here for the cast graphic (§11d).

**`0x1E` / `0x20`** — acks / time, as in §6.

---

## 8. The appearance system

The **`0x33` type-0 appearance is 7 bytes.** Their meaning was decoded empirically with an in-game
"look-lab" (spawn dummies with controlled appearance bytes and observe). **This is the definitive
layout:**

| Byte | Meaning | Notes |
|---|---|---|
| `[0]` | **Body / sex** | `0` = male, `1+` = female. |
| `[1]` | **Form / state** | `0`/`4` = normal human, `1` = ghost/dead, `3` = **mounted (horse)**, `5` = invisible-spell (faded), most other values = **no sprite (blank)**. Driven by `Character.Mounted` via `Session.MountForm()`; toggled with `!ride`/`!mount`, re-drawn on self (`SendSelfLook`) **and** peers (`ShowPlayer` carries `Mounted` in `PlayerSnapshot`). Client swaps to the horse+rider composite (SPR `0x158`/`0x159` = 344/345). |
| `[2]` | **Face** | Distinct faces; range is larger than 8 (accepts values ≥ 0x34). |
| `[3]` | **Armor / coat** | Class armors (rogue/mage/warrior…). |
| `[4]` | **Armor color / dye palette index** (RTK `player.armorColor`) | No visible change for `0..8` (all render as the base/undyed color — the earlier sweep's range was too narrow to catch it). LIVE-confirmed 2026-07-27 via a real-time Frida byte-sweep during a weapon toggle: `16`/`32`/`64`/`128`/`255` visibly recolor the worn armor. Exact palette mapping not yet catalogued. **Now driven by `Character.ArmorColor`** — persisted, carried in `PlayerSnapshot` so peers see it too, and set by the Arena Master's war paint (§11e, `WarPaintAbility`). Sweep/calibrate the real index→color map with the `!dye <n>` GM command. |
| `[5]` | **Weapon** | Honor Sword, Flame Blade, Electra, Steelthorn, Blood, Primogen Blade… **`0` is a REAL weapon sprite — "no weapon" is `0xFF` (`-1`).** |
| `[6]` | **Shield** | Distinct shields. **`0` is a REAL shield — "no shield" is `0xFF` (`-1`).** |

> **⚠ Weapon/shield "empty" = `0xFF`, not `0` (proven live 2026-07-25 + RTK `clif.c`).** A row-sweep of `[5]`/`[6]`
> showed every value `0..15` renders a distinct blade/shield for **both** sexes; only `-1` (byte `0xFF`) is bare
> hands. RTK sends `0xFFFF` for weapon/shield look when `!pc_isequip(slot)`. `SendSelfLook`/`ShowPlayer`/click-
> profile therefore emit the worn item's `Look` when a weapon (`Type 3`)/shield (`Type 5`) is actually equipped,
> else `0xFF` — keyed on slot occupancy (a worn weapon with `Look == 0`, e.g. Novice sword, still shows sprite 0).

**There is no hair slot** in this form. In 4.95, hair is not renderable via `0x33` (it was set by
in-game stylist NPCs). This is a hard limit of the packet, not a server bug.

**The 'r' Ride key vs. `!ride`/`!mount` (fixed 2026-07-26).** These now do different things. `!ride`/
`!mount [0|1]` (`Session.ToggleMount`) is a plain GM/debug toggle — flips `Character.Mounted` unconditionally,
no world state involved. The **'r' key** (`0x1b` setting `0x00` → `Session.TryRideHorse`) is the real RTK
mechanic (`clif_findmount`): mounting requires an actual `Mob` with `MobDef.Key == "horse"` (the plain
wandering "Horse" — id 8 — spawned in Buya/Horse Valley by `Content.AreaSpawns`; combat mobs that merely
share the word, e.g. `wild_horse`/`horse_guardsman`, don't count) standing on the **single tile the player
is facing** (`FrontTile()`, checked via `World.MobNear(..., radius: 0)` — cardinal only, same reach as the
player's own melee attack; diagonal doesn't count, corrected same day after the user caught it working
diagonally) — and **despawns that mob** (`World.DespawnMob`: no loot/exp, and if it was a spawn-point mob
the point is freed to respawn normally, like a kill). With no horse faced, 'r' just replies "There is no
horse to ride here." and does nothing. **Dismounting spawns a fresh "horse" mob back onto the tile in
front of the player, facing them** (`SummonWorldMob`, same path as `!summon`) — so the horse you rode away
is physically set back down when you get off, instead of just vanishing.

**Minimum visible self:** `appearance = [sex, 0, face, 0, 0, 0, 0]`, `renderKind = 1`. Any nonzero
value in `[1]` (the form byte) risks blanking the whole sprite — that was the root cause of the
"invisible character" saga (§14).

The parser `0x436120` stores the 7 bytes into an entity sub-struct at offsets +4,+5,+7,+8,+9,+A,+B
(with a special case: if byte[3]==0 it defaults to `byte[0]!=0`). The core entity/body is built by
`0x44d7d0` (create-entity) directly (ctor `0x462ec0`/reposition `0x44c660` for self — see below); the
appearance pointer is stashed at `[entity+0x108]`. The byte→sprite-layer resolution lives deep in the
sprite archives and was **not** cheaply static-RE-able — it was faster to decode by observation.

### 8.1 The floating nameplate marker — leaked every appearance refresh, patched out (2026-07-27)

**Not classic content.** The always-on inverted-triangle marker above every player (turns into their
name on hover) does not appear in real 4.x screenshots — confirmed by the user. It comes from a
**separate decoration/marker sprite** the client builds on top of the entity, distinct from the body:
after `0x44d7d0` returns, the SAME `0x33` handler (`0x44fef0`) — for **any** player packet
(`renderKind=1`) — unconditionally does `0x43fd80` (alloc `0x17c` bytes) → **`0x463380`** (ctor, player
archive `0x4f2a84`) → **`0x462050`** (attach the new sprite to the entity) → `0x45c830` (register into the
id-keyed entity hashmap also used for lookup by `0x45cb80`/removal by `0x45c8f0`). Crucially: this
**never frees whatever marker was already attached from the entity's previous `0x33`.** Every appearance
refresh (equip/unequip weapon or armor/shield, `Type` 3/4/5 in `Content.ItemById`; mount/dismount) leaks
one marker object, self or peer alike — confirmed live via Frida (two consecutive self-refreshes
registered two different sprite pointers, the first never freed). A real despawn (`0x0E` → `0x44d9f0`,
the same path `SyncMobs`/`World.LeaveMap` already use) destructs the entity **and** its currently-attached
marker as a pair, which is why walking out of view and back (or the peer-side despawn-before-reshow now
sent by `Session.RefreshAppearance`) clears accumulated litter — but a bare appearance resend never
destructs anything.

No server-side disable flag exists: appearance byte `[4]` (see the color-index note above) was
live-swept across its full range during real weapon toggles hunting for a hide-nameplate bit — it only
ever affects armor dye, the marker never changed.

**Fix shipped as a permanent on-disk patch** (not a server workaround — there's no packet-level lever).
Live-verified first via Frida (forcing `0x463380`'s return value to `NULL`, so the caller's existing
"ctor failed → skip attach/register" fallback fires — the same path already taken outside renderKind
1/2/3): characters render **fully normally** with the marker ctor suppressed, proving it's purely
decorative. `re/patch_no_nametag.py` overwrites the first 5 bytes of `0x463380` from
`55 8B EC 6A FF` (`push ebp; mov ebp,esp; push -1`) to `33 C0 C2 14 00` (`xor eax,eax; ret 0x14` — the
`ret 0x14` matches the real function's own epilogue, so the 5-dword call-site stack stays balanced).
File offset == RVA == `0x63380` for this exe. Requires an elevated terminal (`Program Files` write);
backs up the original exe first (`re/*.prenametagpatch.bak`, gitignored — never commit a full client
binary). `--check`/`--revert` also supported. See `memory/nexustk-495-nametag-litter.md` (session memory)
for the full investigation trail and the Frida tracer (`re/frida_nametag.py`) used to find it.

---

## 9. Character creation & the creation packet

Creation is two login-channel packets:

1. **`0x02` NameCheck** — `nameLen name pwLen pw 00 00 00` (name + password). Server replies `0x02`
   with payload `00` = "available / OK".
2. **`0x04` CreateAppearance** — **5 bytes**: `[0]=face [1]=sex [2]=nation [3]=totem [4]=hair`.
   Field ORDER confirmed against the real RTK char-server source (`RTK-Server/rtk/src/char/logif.c`,
   `logif_parse_newchar`): its call `char_db_newchar(name, pass, totem=RFIFOB(39), sex=RFIFOB(37)%2,
   country=RFIFOB(38), face=RFIFOB(36), hair=RFIFOB(40), faceColor=RFIFOB(42), hairColor=RFIFOB(41))`
   shows the appearance tail (right after that server's fixed-width name+pass block) is laid out
   face, sex, nation, totem, hair, hairColor, faceColor. Our 4.95 blob is the same 5-byte prefix of
   that order, minus the two color bytes. (An earlier pass mis-read `[2]` as "near-constant misc" and
   `[3]`/`[4]` as an undecoded nation/totem pair — that guess never had a sample where nation was
   deliberately varied; re-reading with the corrected order lines up perfectly, e.g. a character
   created with Totem=JuJak + Nation=Buya logs `... 02 00 00` → `[2]=2 (Buya) [3]=0 (JuJak)`.)

   | Byte | Meaning | Evidence |
   |---|---|---|
   | `[0]` | **Face** | chars "faceone/two/three" gave `[0]` = `00/23/34` → three distinct correct faces. |
   | `[1]` | **Sex** | `0` = male, `1` = female. Char "male" → `[1]=00`; every female → `[1]=01`. |
   | `[2]` | **Nation** (`Character.Nations` index) | live report + RTK `logif.c` field order. |
   | `[3]` | **Totem** (0=JuJak 1=Baekho 2=HyunMoo 3=ChungRyong 4=None) | same; matches RTK's `Player.getTotemName`. |
   | `[4]` | **Hair** | never observed non-zero in these samples; persisted, no 4.95 render slot yet. |

   Sample blobs (re-decoded — name, sex, **nation**, **totem**):
   ```
   male:      55 00 02 02 00   M, Buya,    HyunMoo
   female:    12 01 02 01 00   F, Buya,    Baekho
   faceone:   00 00 02 02 00   M, Buya,    HyunMoo
   facetwo:   23 00 01 02 00   M, Koguryo, HyunMoo
   facethree: 34 00 01 02 00   M, Koguryo, HyunMoo
   newbie:    29 00 02 00 00   M, Buya,    JuJak      (live pick: "JuJak + Buya")
   newbiea:   32 00 02 03 00   M, Buya,    ChungRyong
   newbieb:   3d 00 02 00 00   M, Buya,    JuJak      (live pick: "JuJak + Buya")
   ```

**Creation → render mapping** (server-side, `Session.ApplyAppearance`): render `appearance[0]` (sex) =
creation `[1]`; render `appearance[2]` (face) = creation `[0]`. `Character.Nation`/`Character.Totem`
are set directly from creation `[2]`/`[3]` (validated against range). Hair (`[4]`) is persisted but has
no 4.95 render slot. `Session.PlaceNewCharacter` (run AFTER `ApplyAppearance` so it sees the real
nation) then routes a brand-new character to their home city — see §11f.

Real RTK note: in the *Lua* gameplay layer (`totem_npc.lua`), totem is normally re-assigned later by
worshipping a totem-animal shrine in the Wilderness, not fixed forever at creation — this server
currently only implements the creation-time pick, not the worship system.

> **Critical caveat:** the creation-packet byte space and the render-packet byte space are **different
> id spaces** with different field *orders* (creation `[0]`=face but render `[2]`=face; creation
> `[1]`=gender and render `[0]`=body). Do not assume they line up — map field-by-field.

### Stats packet — `0x08` (server → client)

Populates the always-on HUD (level, might, will, grace, HP/MP bars + numbers, experience, coins,
nation, totem). Send it in the world-entry burst after `0x33`. Framing is the normal
`AA | len | 08 | inc | body` with the NexonInc cipher. `body[0]` is a **flags** byte selecting the
form; `0x78` = the "full stats" form used below. Multi-byte stat fields are **big-endian `u32`**.

```
body offset  field            type
[0]          flags = 0x78     u8    (full-stats form)
[1]          nation           u8    (0=Neutral 1=Koguryo 2=Buya 3=Nagnang 4=Shilla 5=Jinhan 6=Paekjae 7=Kaya)
[2]          totem            u8    (0=JuJak 1=Baekho 2=HyunMoo 3=ChungRyong 4=None)
[4]          level            u8
[5..8]       maxHP            u32BE (offset confirmed via !hp: HP=100/max=1000 -> bar ~10% full)
[9..12]      maxMP            u32BE (confirmed)
[13]         might            u8
[14]         will             u8
[17]         grace            u8
[24..27]     HP (current)     u32BE
[28..31]     MP (current)     u32BE
[32..35]     experience       u32BE
[36..39]     coins            u32BE
```
Body length ~58 bytes (unused offsets zero-filled). See `Session.SendStats()`.

**Mail/parcel HUD notification byte (in the `0x08` tail).** The real 4.x client shows a small **arrow**
(bottom-left) when you have unread **n-mail**, and an **arrow + bag** when you have a **parcel** waiting —
the cue to visit the postmaster NPC. RTK's `clif_sendstatus` (`rtk/src/map/clif.c`) proves the driver:
its 6.x/7.x `0x08` is a *composite* packet (form byte at offset 5 is an `SFLAG_*` bitmask —
`0x40`FULLSTATS `0x20`HPMP `0x10`XPMONEY `0x08`ALWAYSON; `0x78` = all of them), and its **always-on tail**
carries `sd->flags` documented verbatim as **`1 = New parcel, 16 = New Message (n-mail), 17 = both`**
(a bitmask: bit0 parcel, bit4 mail). That tail rides the SAME `0x78` "full" form we already send.

**✅ CONFIRMED LIVE (2026-07-28): the 4.95 flag byte is `body[45]`.** Sending our full-stats `0x08` with
`body[45]=0x11` (via `!mailflag 45 11`, `Session.MailFlagProbe`) made the real client draw **both** the
mail arrow and the parcel bag in the bottom-left HUD cell; the decrypted wire packet showed `body[45]=0x11`
exactly. Bit semantics match RTK: **`0x10` = n-mail arrow, `0x01` = parcel bag, `0x11` = both, `0x00` =
clear.** So the 4.95 client IS network-wired for the notification (the render widget = vtable `0x4ce440`,
ctor `0x47a230`, draw `0x469480`, loading `MAIL.EPF`/`PARCEL.EPF` from `Inter.dat`; state bytes
`[+0x105]`=parcel-count, `[+0x106]`=hasMail). NOTE: our `SendStats` zero-fills `body[45]`, so any clean
stats resend (movement/action) CLEARS the arrow — real wiring must set `body[45]` in `SendStats` from a
persisted mail/parcel state, not one-shot. Retrieval is a plain `0x30` dialog: RTK's postmaster
`MessengerNpc` (`rtklua/.../messenger.lua`) offers *Mailbox → Send/Receive Parcel* — already supported by
our NPC dialog system. (Client-side `m` hotkey to open mail is a **dead key** in both 4.83 and 4.95 — see
the client-versions memory — so the NPC + this HUD flag are the authentic access path.)

**How this was found (methodology worth reusing):** static dispatch analysis wrongly "proved" `0x08`
was unhandled — it's a no-op in the world dispatcher remap *and* the conn-state object `0x444de0`, yet
the client processes it anyway via a pre-dispatch path. It was cracked by **replaying a real 6.x server
capture** (jeedee/TkServer `game_server.rb` has commented-out captured `send_data` packets; opcode `0x08`
= stats, decrypt with the shared NexonInc cipher — see `re/decode6x.py`). The exact 4.95 field offsets
were then pinned with a **self-describing gradient packet** (`body[i]=i`, in-game `!stg`): every HUD
number equals its own field offset, and u32 values (e.g. `0x18191A1B` at offset 24) reveal size and
big-endian order in one read. **Lesson: a "no-op in the dispatch table" is not proof an opcode is
unhandled, and a close-version reference server (6.x here) beats guessing.**

Server reply to `0x04`: we send an "Account created." message (a `0x02` message-box packet). This
does **not** currently auto-close the creation screen — the exact create-ack that dismisses the UI is
still unknown (see §16).

---

## 9.5 The profile system (self-profile, click-profile, editing)

There are **two distinct profile windows**, plus an edit path. Both windows are UI `0x198`; the client
opens each on a different request opcode, and the server replies with a different packet. All
multi-byte ints below are **big-endian**. See `Session.SendSelfProfile` / `SendClickProfile` /
`HandleChangeProfile`.

**Self-profile — request `0x2d` → reply `0x39` ("Mind's Eye").** Pressing the profile key sends
`2d 00` (`body[0]==0` = self). Reply with `0x39`, the stats/legend summary. **This layout was
reverse-engineered from the client's OWN parser `0x4732a0`** (the mode-0 self widget picked by the shared
profile dispatcher `0x424820`; vtable `0x4cdf88`, method +0x5c). Body:
```
[AC u8][dam u8][hit u8]
[clan : u8 len + bytes]           (len 0 = clanless)
[clanTitle : u8 len + bytes]
[title : u8 len + bytes]
[spouse : u8 len + bytes]
[group u8][TNL u32BE]             ← group u8 = the "sociable/group" status cell (Shift+G, 0x1b/0x02)
[className : u8 len + bytes]
[helmIcon u16BE][leftRingIcon u16BE][rightRingIcon u16BE]   ← the 3 equip-icon cells beside the doll
[buff box : u8 len + text]        ← multi-line; client maps TAB(0x09)→CR(0x0d), one line per active buff
[exchange u8]                     ← the "exchange/trade" status cell (0x1b/0x08); client field +0x935
[legendCount u8]                  ← a single u8 (NOT u16)
legendCount × { icon u8, color u8, textLen u8, text }
```
**⚠ Do NOT reuse a 6.x/RTK profile capture for 4.95.** Those forks have MORE item slots, so their `0x39`
carries a ~116-byte equipment region between `className` and the legends. 4.95's parser has no such region —
it reads only the 3 icon cells + buff box + one flag byte, then a **u8** legend count. Feeding it the 6.x
shape pushes the legend count into the padding (read as 0 → no legends) and spills icons into the wrong
fields (gear in wrong cells). Decoding a real 6.x capture with the grammar above proves it: the bytes align
perfectly up to the legend count, then the 6.x equip block is left unconsumed. The self-view doll BODY is the
LIVE on-map sprite (armor/weapon/shield only) — helm/rings have no sprite layer and show ONLY in the 3 icon
cells. The **buff box** is the self-view analog of the click-profile's gear list (issue: self=buffs,
other=gear); it holds `Name (Ns)` per active buff, grouped by spell (`Session.BuffBoxText`).

**Click-profile — request `0x43` → reply `0x34`.** Clicking a character sends `43 01 id(u32) 00`. Reply
with `0x34`, the public two-page view (portrait + gear on page 1; nation + picture + writable blurb on
page 2). **This layout was reverse-engineered from the client's OWN parser `0x48b6a0`** (profile-page
widget, vtable `0x4cee5c`, method +0x5c) — the 7.x `clif_clickonplayer` is a *different, larger* shape
and does not fit 4.95. Body:
```
5 header strings (u8 len + bytes): title, clan, clanTitle, class, name   (order confirmed live)
appearance: tag u8 (=0) + 7 look bytes                (same 7-byte form as 0x33 type-0 → correct sprite)
[helmIcon u16BE][leftRingIcon u16BE][rightRingIcon u16BE]  ← 3 equip-icon cells (SAME as 0x39; NOT portraits)
gear/item list (u8 len + text)                        PAGE 1; item names TAB-separated (client → CR)
scalar (u32BE)                                        (unknown; 0)
group (u8), exchange (u8)                             the two status cells; 0/1 = off/on, 0xff = blank WHITE box
nation (u8)                                           PAGE 2; drawn via NATION_E.EPF
picture (u16BE len + bitmap bytes)                    PAGE 2; empty = 00 00
writable blurb (u8 len + text)                        PAGE 2; the free-text box
legendCount (u8)                                      (NOTE: u8 here, unlike 0x39's u16)
legendCount × { icon u8, color u8, textLen u8, text }
```
Gotchas that cost real debugging time:
- The **gear text** (page 1) and the **writable blurb** (page 2) are two *separate* length-prefixed
  strings at different points in the packet — do not merge them. **Omitting the blurb field desyncs
  the legend count** (the parser eats the legend-count byte as the blurb length) → empty legend.
- Legend **text color `0x80`** (from the real capture); color `0` renders the text invisible (icon
  still shows).
- **Dated legend text** (fixed 2026-07-26): several RTK legends stamp the in-game date, e.g.
  `player:addLegend("Aided Chu Rua (" .. curT() .. ")", ...)` (`chu_rua.lua`) and the "born" legend
  (`login_map_tile.lua`: `"Born in " .. curT()`, `curT()` = `"Yuri <year>, <season>"`). We don't model
  RTK's in-game calendar (no server-side clock tick), so `Character.GameDate` is a fixed constant —
  `"Hyul 31, Winter"`, matching the live-captured self-profile reference above (§9.5's "Born in Hyul 31,
  Winter"; the live text says "Hyul", not RTK's own "Yuri" — the live capture wins where they diverge) —
  reused everywhere a legend needs "the current date". `ChuRuaAbility` (`NpcAbility.cs`) now grants
  `$"Aided Chu Rua ({Character.GameDate})"` instead of the undated string it shipped with.
- 4.95's click popup has **no totem slot** (`TOTEM.EPF` is unreferenced in the client) — totem only
  appears on the HUD/self-profile, not here.
- The **3 icon cells** (helm / left ring / right ring) are shared by BOTH views as three `u16BE` fields.
  4.95 has no character-sprite layer for these slots, so the profile shows them as ground-icon boxes:
  each = `IconWire(item.Icon)` (same encoding as the `0x37` equip window), `0` = empty box. They were
  briefly mistaken for "portrait graphics"; a live test (an old bug rendered the *weapon* icon in the
  helm box) proved they take an `IconWire` value. Order confirmed live: helm, left ring, right ring.
  Wire slots come from the client's own `0x1F` unequip captures: **helm=4, left ring=7, right ring=8**.
  Fed by `Session.ProfileCellIcon(wireSlot)`.
- **Group / exchange status cells.** Both views show a **group** (sociable) and **exchange** (trade)
  indicator. In `0x34` they're the two `u8` cells after the scalar (were briefly guessed as "look-selectors");
  in `0x39` they're the `group` byte (after `spouse`) and the `exchange` byte (the trailing flag before the
  legend count). **`0xff` renders a blank WHITE box** — send a real `0`/`1` (off/on). The two views MUST read
  the same source or they disagree (self showed OFF while other showed ON when only `0x34` was wired). Toggled
  by the `0x1b` setting opcode — **`0x02` = group (Shift+G), `0x08` = exchange** — and persisted on the
  character (`Character.Grouped` / `Character.Exchange`) so they survive reopening the profile and a relog.
  NOTE this is only the joinable/tradeable *flag* + its display; the actual party/trade **request** flows
  are built now — see §11l. The client's `0x2e` / `0x4a` packets seen in earlier captures are exactly RTK's
  party-invite (`0x2e`) and an undecoded opcode respectively; §11l wires `0x2e` defensively but leans on
  chat commands as the confirmed-safe interface (real binary trade/party windows are still unconfirmed on
  4.95).

**Editing — client `0x4f`.** Saving the profile edit sends
`4f | picSize(u16BE) | pic[] | blurbLen(u8) | blurb[] | 00`. Parse both (mirrors the client's own
`clif_changeprofile`: `picSize = u16BE(body[0]); text at body[2+picSize]`), persist to the character,
and reply with a `0x02` "Your profile has been saved." message. A later `0x43` click then shows the
player's own words + drawing.

---

## 10. Movement model

**The movement model is chosen by the client's FAST-MOVE setting — this is the master switch.** This was
established by RE + live Frida probing and matches RTK's `clif_parsewalk` (which gates its walk response
on `FLAG_FASTMOVE`). Getting this right is what makes 4.95 self-walk smooth.

Per step the client sends **`0x32`** (or **`0x06`** every few steps — handle both identically):
```
dir(u8) stepCounter(u8) X(u16) Y(u16) pad
```
Direction map: **0 = North (y−1), 1 = East (x+1), 2 = South (y+1), 3 = West (x−1).** `X,Y` is the client's
believed **current** tile (where it is walking *from*) — step from it (client-authoritative resync) so
collision runs on the cell the client is really on.

### 10.1 Fast-move flag — read it PER WALK, do not track the toggle

The client marks each walk with its mode in the **high bit of the step counter** (`dec[1] & 0x80`):
* **set** → fast-move ON (client-authoritative)
* **clear** → fast-move OFF (server-authoritative)

Read this per-packet. Do **not** try to track the `0x1b`/`09` toggle notification and a startup default —
the client boots fast-move OFF, persists its state across launches, and never reports state on connect, so
any default guess plus a toggle can invert and you end up sending corrections into a client-authoritative
walk (→ the character "slides" with no animation). The per-walk bit is authoritative and self-correcting.

### 10.2 Fast-move ON = client-authoritative (the smooth path) ✅

The client moves, animates the leg cycle, AND scrolls its own camera locally. **Send the walker NOTHING on
a good step.** Reserve `0x04` for *corrections only* (blocked tile or a position the server rejects — snap
the client back). This is the smooth, self-paced walk; realm-center (§10.5) works in this mode. Confirmed
live: fast-move-ON walks play a full `frameCtr 0→3` animation with zero server packets.

### 10.3 Fast-move OFF = server-authoritative — answer with `0x26` self-walk ✅

The client will **not** move until the server assigns the tile. Answer each walk with **one `0x26`
self-walk packet** — the *same primitive 5.33 uses* — sent from the tile being confirmed:
```
0x26  dir(u8)  X(u16BE)  Y(u16BE)  viewX(u16BE)  viewY(u16BE)  flag(u8)
```
This gives smooth legs, continuous movement, and a correct camera (realm-center honored) — matching
fast-move ON. Server code: `SendSelfWalk(dir, fromX, fromY)`.

> **`0x26` is NOT dead on 4.95 — the "no-op" belief was wrong.** Its *main dispatch-table* handler
> `0x44fb80` really is `mov al,1; ret` (a no-op), but that is not the whole story — exactly like the `0x08`
> stats opcode. The client **pre-dispatches** `0x26` through the self-entity **vtable** (slot `+0x38` at
> `0x4cf038` → self-move dispatch `0x48eb40`) to **`handlerB` `0x4903d0`**, which:
> 1. **move-commits** the pending step (`0x48f160`) to `(X,Y)` — completing it **without** the camera
>    scroll a `0x04` forces; move-commit stores the packet's `viewX/viewY` as the camera anchor, so
>    realm-center is respected exactly like the fast-move-ON local path; and
> 2. starts the next step's leg cycle (`selfWalkAnim 0x48f2c0`) and sets **`entity+0x65f3 = 1`** — the
>    "complete locally, don't wait" flag — so the animation runs `0→3→0` and finishes on its own.
>
> This is why `0x26` works where `0x04` fights the client. The self-walk state machine (`0x48eef4`)
> advances `frameCtr` each tick; **at `frameCtr == 2`, if `entity+0x65f3 == 0` it sets the "waiting"
> flag `entity+0x65f4 = 1` and freezes** until the server unblocks it — that freeze is what made every
> `0x04`-based approach stall or jerk. `0x26` sets `0x65f3 = 1`, so it never waits. Confirmed live
> (`handlerB(0x26)` + `moveCommit` fire per step; the same dispatch also handles the client's *own* local
> walk commands as sub-op `0x0b` → `handlerA` — so **`0x26` is literally the server-side sibling of the
> client's local walk command**).

> **Do NOT use `0x04` or `0x0C` to *drive* self-walk on 4.95.**
> * `0x04` is the **re-anchor / teleport** primitive (§10.6): its handler *always* runs the camera-scroll
>   (`0x44c660` writes a decaying settle-offset even when the origin is unchanged = a jerk) and it cannot
>   complete a step without that scroll. Reserve it for corrections and hard refresh.
> * `0x0C` (`0x462320` start-walk → `0x44b140` walk-render) renders
>   `screen = logical(dest) + forward_step*(frameCtr/4)` — a guaranteed forward OVERSHOOT for the self. It
>   is for animating *other* entities. Normal walk speed constant is 80.
>
> (Legacy `0x0C`/`0x04` walk attempts survive only behind `NEXUS_V495_SLOW_MOVE` (0–4) for comparison;
> the default `5` is the `0x26` path above.)

### 10.4 Turn (`0x11`) — first press turns, second press walks

In NexusTK the **first** key press in a new direction turns you in place (no step); only the **second**
press walks. The client sends **`0x11`** (`side(u8) pad`) for the turn. Echo it back with the entity id so
the client turns: the recv handler `0x450350` reads `id(u32)@+1, side(u8)@+5`, looks the entity up, and
calls its turn method `0x462410`. Build `Be32(id), side, 00` (same as `SendSide`). Dropping `0x11` leaves
facing unconfirmed until the next walk ("press a new direction, first step goes the OLD way").

### 10.5 Realm-center camera lock (F4 / `0x1b` sub-cmd `0x07`)

A client-side camera mode. The client sends **`0x1b`** with body `07 00 00` (F4). The server signals the
state via a byte in the `0x15` mapinfo packet (our `SendMapInfo` body[7]; the client's `0x15` handler
`0x44f8b0` reads it and feeds `(realm==0)` to the view rebuild `0x44c570`, storing it at `view+0x400`). A
second gate `entity+0x1dd` (from a settings bitfield) must also be set for the camera-clamp block to run.
Works together with fast-move ON. `0x1b` also carries other toggles: sub-cmd `0x09` = fast-move.

### 10.6 Hard refresh (`0x38`, Ctrl+R) — this is what `0x04` is *for*

Ctrl+R sends **`0x38`** (`38 00`) and grays the screen while the client waits for the server to re-assert
authoritative state. Mirror RTK's `clif_refresh`: re-send **`0x15` mapinfo** (on 4.95 this triggers a
client **map reload**, which clears the gray mask) + **`0x04` xy** + **`0x33` self** + re-draw nearby
entities. Server code: `HandleRefresh` (same in-place refresh the F4 toggle performs).

`0x04` is the **re-anchor primitive**: `X, Y, anchorX, anchorY, 00` → authoritative position + a fresh
camera origin `(X−anchorX, Y−anchorY)`. RTK's `clif_sendxy` always computes a **centered / edge-aware**
anchor (`x<8 → x`, `x ≥ xs−8 → x−xs+17`, else `8`; y analogous), with **no** realm-center handling — so a
refresh **recenters** the player even if realm-center had them parked off-center. We match this: if realm is
on, `HandleRefresh` re-locks the freeze at the new centered origin so it recenters *and* stays locked. RTK
also emits a trailing `0x22 03` terminator, but `0x22` is the default no-op on 4.95 (remap slot `0x2a`), so
it is not needed to end the refresh.

---

## 11. Speech & actions

**Speech:** client sends `0x0E` = `chatType(u8) msgLen(u8) msg`. Echo it back as `0x0D` =
`chatType(u8) entityId(u32) msgLen(u8) msg` attributed to the speaker's entity id → bubble appears
over their head.

**Name-prefixed text + shout — LIVE-CONFIRMED 2026-07-27.** `Session.HandleChat` prefixes the broadcast
text server-side before it goes out over `0x0D`, since the client renders exactly the bytes it's given in
both the bubble and the chat-log line (there's no separate "speaker name" field for the client to prepend
itself): `"Name: message"` for ordinary speech, `"Name! message"` for shout. The client's own keybind help
(extracted `Str_Eng.res`, `re/str_eng.res:102/105`) documents dedicated single-key hotkeys — **`'`** = Say,
**`!`** = Shout — confirming shout is a real, distinct native input mode and not a private-server invention.
`chatType` (the `0x0E` byte the client sends) is forwarded to `0x0D` **unchanged**: the hypothesis was that
whatever value the client uses internally to pick the Say/Shout hotkey is also what it uses to pick the
bubble/chat-log color on playback, so echoing it back would render correctly without the server inventing
its own color scheme — **confirmed live**: ordinary speech renders as plain "Name: message", and shout
renders red in the chat box with a yellow over-head bubble, using nothing but the passthrough `chatType`
byte + the server-side text prefix. The code treats any nonzero `chatType` as shout (`bool shout = chatType
!= 0`) rather than hardcoding a specific value, since that's what was tested and works. Whisper (`"`,
Shift+`'`) is a fully separate native opcode (`0x19`, see below) — not a `chatType` value on `0x0E`.

**Attack:** client sends `0x13` (bare `13 00`) on spacebar. The swing is a **`0x1A` action, type=1**. The
server→client `0x13` is a *different* packet — the combat **damage / over-head HP bar** (§7.2) — sent at the
hit resolution, not as the swing. (Historical trap: a *bare/zero* `0x13` gives `critical=0` → animation
`0x8f − 0 = 0x8f`, a "death flash"; that scared us off replying `0x13` at all until the handler was fully
decoded — the fix is simply to send a real `percent`/`critical`, which is what combat now does.)

**Emotes (`:` wheel).** Client sends `0x1d` = `idx(u8) 00`. The emote plays as an action:
`type = idx + 11`, broadcast as `0x1A` to the emoter **and every peer on the map** (RTK
`clif_parseemotion`: `sendaction(&bl, RFIFOB(5)+11, 0x4E, 0)`). So `:` `l` = `idx 0x0b → type 22 =
dance`. The index sits at `dec[0]` — **not** `dec[1]`; 4.95 has no ordinal byte after the opcode, so
RTK's 6.x `RFIFOB(5)` offset is one byte later than ours (the same shift seen on every 4.95 handler).
The dance/emote sound rides along with the client's action sprite; no separate sound packet is sent.

**Weapon.** `Character.Weapon` renders in the player's `0x33` type-0 appearance slot `[5]` (the sword/blade
slot — see §8), persists in the store, and drives the melee damage bonus. This works (it draws on the
player, which renders). With no weapon the space-bar attack plays the empty-handed *throw* animation/sound.

**Combat (server-authoritative).** The server owns mob HP (`Shared/Mob.cs` + `World`). On client `0x13`: send
the player swing (`0x1A`), then resolve melee against the mob on the tile *in front* of the player (facing
tracked from the last walk `0x32`); apply `might + weapon bonus`, broadcast the **`0x13` over-head HP bar + hit
spark** to the whole map (`ShowDamageResult`), and on death send `percent=0` (empty bar + final spark) then a
**delayed `0x0E`** despawn (the "death beat" — 4.95 has no monster death frames, see §7.2) + exp via a fresh
`0x08`. Spell damage (`CastDamage`/`ApplyCastGeneric`) shows the same HP bar on top of the cast effect. **This
targets real `0x07` monsters (§7.2, §11a) — visible, with collision, killable.** Verified end-to-end live:
spawn → melee/spell → HP bar drains → death beat → despawn → "You defeated" + exp.

## 11a. Monster rendering — the sprite category system ✅ SOLVED (`0x07`)

The client's sprite manager (`[0x4fd2f8]`) groups sprites into **categories**, each backed by an EPF archive
from `NexusTK.dat`, all loaded at startup (registry table `0x47b4c6`). The category name is a **wide (UTF-16)
string** — e.g. `"ITEM"` (`0x4f1fe8`), `"MONSTER.EPF"` (`0x4f1d18`), plus BODY/HEAD/FACE/tiles/shields/swords.
A sprite is resolved by `(categoryName, id)`; the item resolver `0x435ab0` does `id + 0x4000` in `"ITEM"`
(`0x431020`), while the **monster resolvers `0x434020` / `0x4342e0`** push `"MONSTER.EPF"` and resolve the
frame through `0x433d00` (Monster.tbl lookup — no fixed offset; it's a table map).

**The answer: opcode `0x07`** (handler `0x44fdb0`) is the monster/creature-list spawn (full layout in §7.2).
It builds each entity with the **same factory `0x44d7d0` that `0x33` uses**, but the look descriptor decides
the archive: **`look ∈ [0x8000, 0xbfff]` ⇒ descriptor type 1 ⇒ creature entity (vtable `0x4cd098`)**, whose
draw `0x461c70` (`[ent+0x178]!=0`) routes to the monster resolver → Monster.epf. So `look = 0x8000 | monsterId`
(`monsterId` = the Monster.tbl index 0..326). How it was found: enumerated every call site of the generic
sprite lookup `0x431020`/`0x430de0` → located the two that push `"MONSTER.EPF"` (`0x434020`/`0x4342e0`) →
walked callers up to the draw methods in vtable `0x4cd098` → to its ctor `0x461a50` → to the factory `0x44d7d0`
→ to its only two callers: the `0x33` handler (`0x44fef0`, players) and the **`0x07` handler (`0x44fdb0`)**.

**Why not `0x33` or `0x16`:** all three `0x33` renderKinds (1/2/3) call `0x463380` with the *player* archive
`0x4f2a84`, so `0x33` can never draw a monster in 4.95 (a gap from 7.x — see §17). `0x16` builds the item/object
class (vtable `0x4cd18c`, category `"ITEM"`), invisible for monster ids.

Monster frame layout is in `Monster.tbl` (plain text: `NumMonsters 327`, per-id `Palette/Starting/Walk/Attack…`;
`Starting` = idle frame). Live sweep confirmed frame = `6 + 20·monsterId` for the early monsters (the idle
"Starting" frame + a walk-cycle offset). Parse `NexusTK.dat` with the Nexon PAK format: `u32 count` then 17-byte
entries `{u32 offset, char name[13]}` (first offset == header size).

**Commands** in `Session.HandleChat`: `!cre <lookId> [hp] [color]` (one real monster in front, killable;
`color` = the `0x07` colour/recolor byte, see §11a.1), `!crecol <lookId> [loColor] [hiColor] [step]` (sweep
the SAME look id across colour-byte values as a **grid**, 12/row, default `0..23`; the colour byte visibly
wraps mod-24 with only 0-19 real), `!crow <lo> <hi> [step]` (row sweep of the Monster.tbl look space),
`!spawn [lookId] [hp]` (a pack), `!kill`, `!weapon <n>`, `!ride`/`!mount [0|1]` (get on/off a horse — form
byte 3, §8). The `0x16` item commands (`!mob`, `!mobrow`) are kept for item/object discovery.

**Navigation & content commands** (data-driven, backed by the `Content` registry — §17.3): `!warp <name|id>
[x y]` (fuzzy-match a map by name or id, optionally with coords, and enter it), `!maps [query]` /
`!mobs [query]` (fuzzy list maps / mobs), `!summon <name|id>` (spawn a mob from the registry by name/id, into
the shared world with wander/respawn-less one-off AI), `!rabbit` (one wandering, killable rabbit in front of
you). The persistent, auto-populated spawns (with respawns + drops) are separate — see §11b.1.
All of these print their output as **over-head speech (`0x0D`) from the player's own entity** — the
`SendLog()` helper — because `0x02`/`0x0F` are single-line message *boxes* that can't stack for a list;
`0x0D` is the in-world chat-log channel (handler `0x450170`, 3000 ms bubble). **Door/portal warps** fire
automatically in `HandleWalk`: a step onto a `(map,x,y)` that has a registry warp calls `EnterMap` and
overrides collision (checked *before* the blocked-tile test).

**Exhaustive client-data audit (2026-07-24):** confirmed monster/item **names and stats are NOT stored
client-side anywhere** in the 4.x install — parsed every archive with the Nexon PAK format directly.
`NexusTK.dat` (64 entries): `Monster.tbl`/`Item.tbl` are rendering metadata only (`Palette/Starting/Walk/
Attack/Delay/Shadow` and `Palette/Alpha/Light` respectively — no name field on either, 327 monsters / 1310
items). `Str_Eng.res` is generic `%s`-templated UI prompt text, not a name lookup table. `Inter.dat` (165
entries) is 100% UI chrome (dialogs/buttons/fonts/login art) + localized menu strings + 8 nation `.des`
blurbs — nothing entity-related. `.map` files (1750) are pure tile grids, `NexusTK.snd` is audio (same PAK
header, 197 entries). So names/stats are server-supplied text only — confirms the Nexus Atlas scrape +
matcher tool (`re/monster-matcher/`) is the only path to a name/stat table, not a shortcut we're missing.
PAK tools: `re/pak_list.py <path.dat>` (directory listing), `re/pak_extract.py <path.dat> <EntryName>
[outFile]` (extract one entry).

### 11a.1 The `0x07` color byte = a pure palette-swap (recolor), applied in a deferred blit ✅

The 12-byte `0x07` entry ends with a **`color` byte** (offset +10) then **`dir`** (+11). Live testing
(`!crecol 27 0 4` → 5 visibly different-coloured bulls, frame identical) proves **`color` is a real,
per-spawn recolor** — same sprite, different palette. This is confirmed independently three ways:

- **Field layout (static arg-trace `0x44fdb0 → 0x44d7d0(factory) → 0x461a50(ctor)`):** `look` and `color`
  travel as **one packed dword** `{u16 look, u8 color, u8 pad}` stored at `entity+0x17c`; so the colour
  byte lives at `entity+0x17e`, and `dir` at `entity+0x18d`.
- **Colour is NOT consumed in the resolve path** (so it is purely a palette op, never a frame/geometry op):
  a full-binary xref of displacement `0x17e` = **zero real reads**; all five `entity+0x17c` dword readers
  pass the whole packed value into the sprite resolvers, which use only the low 16 bits (look). All three
  resolvers (`0x433d00` frame = `Starting + dir*Walk`; `0x4342e0`; `0x434020`) call
  `catlookup(MONSTER.EPF, frame, out)` with no colour.
- **Live hook on `0x433d00` dumping its output descriptor:** byte-identical across colours 0-4 —
  `[id, Palette=0, Starting, Walk, Attack, Delay, Shadow]` = the `Monster.tbl` row, whose `Palette`
  column is the *default per-monster* palette, independent of the spawn colour byte.

⟹ The colour→palette-block selection happens in a **deferred blit stage** (the vtable `[esi+0x9c]`/`[esi+0xa0]`
draw calls at the tail of the resolvers) that was not pinned down at the instruction level — but the
*behaviour* is fully known and matches the reference server (below), so it did not need finishing.

**Range:** `!crecol` shows the client cycling through **24** distinct results before repeating (colour 24 ==
colour 0). Byte-scanning `Monster.pal` (count the `DLPalette` ASCII tags, don't divide file size) confirms
the file holds **exactly 20** palette blocks. So the client applies a hardcoded `% 24` clamp unrelated to
the real palette count; **only colours 0-19 are real recolors**; 20-23 read past the 20-slot array into
adjacent memory (stable but undefined garbage — not legitimate variants).

**Confirmed by the RTK reference server (§17.1):** RTK's creature-spawn packet is byte-identical
(`look = 0x8000|monsterId`, then a `look_color` byte), and it stores `look_color` as a first-class
per-monster DB field — recolors are the *same look with a different `look_color`*. Exactly our model.

**Frida hooks used** (`re/frida_probe.py`): `monfr` (0x433d00; correct arg map `args[1]`=packed look+color,
`args[3]`=dir, `args[7]`=out descriptor) and `monctor` (0x461a50). A page-protection software watchpoint on
the entity is also in there but is NOISY (neighbour heap objects share the 4 KB page); it also revealed two
Frida quirks on this target — `context.eflags` reads `undefined` (no single-step re-arm), and NativePointer
objects captured outside the `setExceptionHandler` callback read back as `0x0` inside it (store plain ints).

### 11a.2 Monster names / stats / colours — the data source ✅

Names/stats are **not** in the client (audit above) — they come from the **RTK reference server DB** (§17.1)
and the **Nexus Atlas** scrape (pre-6.5 exp values). Extract these **locally** with the tools in `re/` — the
data itself is **kept out of this repo** (logic-only server; the generated CSVs land in `data/game-data/`,
which is gitignored). RTK look-ids
validate against our own EPF shape-matching (rat=91, mouse=120, bull=27, rabbit=21, fox=22, wolf=23, bear=24,
squirrel=25). Colours ≤19 map to our `Monster.pal`; RTK colours >19 are 7.x-only and must be re-picked for
4.95 via `!crecol`.

---

## 11b. Shared world (multiplayer) — players see each other + the same mobs ✅

`Server/World.cs` is a single instance (created in `TkListener`, injected into every `Session`) that holds
**all connected players and all live mobs, grouped by map**. It turns the previously per-connection server
into one shared world: players on the same map see each other move/turn/speak, and everyone fights the
**same** server-authoritative mobs.

**Entity ids.** Every player is assigned a **unique** id from the world at arrival (`World.AllocatePlayerId`,
`1+`). Before this, every character defaulted to `Id = 1`, which collided on the broadcast key — the fix
that unblocked multiplayer. Shared mobs draw from a disjoint pool (`100000+`); the session-local *debug*
dummies keep their own `5000+` pool (only ever visible to their own client, so cross-session collisions
there don't matter). The self id still binds the client camera via `0x05` (`SendId`).

**Join / leave.** On world entry AND on every warp (`Session.EnterMap`): the newcomer draws everyone
already on the map (`0x33` per peer, `0x07` per mob) and `World.EnterMap` broadcasts the newcomer to them.
On disconnect/warp-out (`World.LeaveMap`) the player is despawned (`0x0E`) for the peers left behind.

**Broadcasts** (to all same-map players, usually excluding the actor): move `0x0C`, turn `0x11`, speech
`0x0D` (real chat only — `!`-commands stay self-only via `SendLog`), attack swing `0x1A`, combat damage /
HP bar `0x13`, cast effect `0x29`, spawn `0x07`, despawn `0x0E`. The moving player's OWN client is driven by
the self-walk modes (§10), so it's excluded from the `0x0C` broadcast.

> **`0x0C` for peers/mobs sends the SOURCE tile, not the destination.** The 4.95 client's `0x0C` walk always
> ends **one tile past** the packet tile in the walk direction (the forward-slide overshoot of §7.2/§10.3 —
> `logical = packet_tile`, then the render slides `+1` and commits there). For the self, `0x04` corrects it;
> for a peer/mob with no commit it sticks, so the entity renders a tile ahead of where the server has it —
> proven by live trace (server `(61,79)`, client drew `(62,79)`). Anchoring the packet on the **source** tile
> makes `client_final = source + forward(dir) = the true destination`. Both `MoveEntity` (peers, in
> `HandleWalk`) and `MoveMob` (mobs, in `World.Tick`) pass the pre-move tile for this reason.

**Shared mobs + combat.** `!summon` / `!rabbit` spawn into the world (`SummonWorldMob`); the debug lab
(`!cre`/`!mob`/`!crow`/look-lab) stays session-local. `HandleAttack` hits world mobs first: `World.TryDamage`
applies damage **under the world lock** so two players can't double-kill; the number + death despawn are
broadcast to all, **loot is rolled onto the floor**, the spawn point is scheduled to respawn, and **exp
(the mob's real `Exp`)** goes to the killer. Beyond the commands, the world is populated automatically — see
the spawn/AI system in **§11b.1**.

**Threading.** All of `World`'s collections are guarded by one lock; socket writes happen **outside** the
lock (recipient list snapshotted under it) and every cross-session send is exception-guarded, so a peer
whose socket just closed can't break a broadcast. Cross-session sends still go out through the target
session's own locked `Send()`, so bytes never interleave mid-packet.

**Gotcha — mapinfo refresh drops foreign entities.** Re-sending `0x15` in place (e.g. the realm-center
`0x1b 07` refresh) makes the client rebuild the map and **drop every foreign entity** — the self survives
via the trailing `0x33`, but peers/mobs vanish. So any in-place `0x15` resend must be followed by
`Session.RedrawWorld()` (re-asserts co-located peers + mobs via `World.View`). Since the 4.95 client sends
`0x1b 07` frequently, this refresh is also a source of walk jitter — throttling it to real state changes is
a movement-side TODO.

Mobs persist on a map after everyone leaves (they belong to the map, not a session); `!kill` clears the
current map's world mobs for everyone. (Players still have no view-distance streaming: two players far apart
on a large map may not see each other until one walks close and a re-sync redraws them — fine when they're
near each other, the common case. Only **mobs** are viewport-streamed; see §11b.1.)

---

## 11b.1 Persistent spawns, mob AI, collision & viewport streaming ✅

The world is populated automatically from **two** RTK spawn sources, not just by commands.

**Static spawn table (`Content.Spawns` ← `data/game-data/Spawns.csv`).** Each `SpawnDef` is `(mobId, map, x, y)`
— one live mob per point. This is only **1175 points across 19 maps** (Kugnae 526, Buya 408, a few specials): it
covers the towns and little else. RTK's `Spawns0` SQL table genuinely has nothing for the hunting maps.

**Excluded as "not classic" (`Content.ExcludedSpawnMobIds`).** A handful of `Spawns0` points exist only to host a
scripted storyline for a subsystem we haven't built, so spawning them is worse than not — a mute, purposeless mob
standing in for content that can never trigger. Currently: **729 `spy_hwan` "Hwan"** (Buya 330 @38,99) — a captive
NPC for the Spy subpath's interrogation questline (`NPCs/subpaths/spy/hwan.lua`); the player-subpath system doesn't
exist yet, so he's filtered out of `LoadSpawns` rather than left to wander aimlessly. Revisit if subpaths are ever
ported; add future finds of the same shape to that set rather than editing `Spawns.csv` directly.

**Excluded map range: "Buya Scorpion Cave" (`Content.ExcludedMapRanges`, maps 410-419).** RTK-authored reskin of
the classic **Kugnae Spider Cave** (maps 90-96), not original NexusTK content — same level-42 gate, same shared
generic mob pool (`carrion_raven` 99 / `pale_scorpion` 104 / `massive_scorpion` 105), with the spider-flavoured
ids swapped for scorpion ones (`giant_spider`→`vile_scorpion`, `radiant_spider`→`radiant_scorpion`, plus an extra
`scorpion_lurker`/`crimson_scorpion` boss room). Entrance was Buya `(68,93)/(69,93)`, a 10-room linear chain
(Sand Glen → Sand Edge → Scorpion Tail → Sting → Venom Den → Stream Sands → Sand Den → Desert Claw → South Sand
Den → Green's End) looping back out to Buya. Cut at the **warp-graph level**: `LoadWarps` drops any warp whose
source or destination map falls in an excluded range, so the entrance, every internal room-to-room link, and the
exit are all gone in one place — the maps/mobs/warps stay in the source CSVs untouched (lazy materialization means
nothing for them even loads), so trimming `ExcludedMapRanges` fully restores the cave. Same "flag, don't delete"
pattern as the Hwan exclusion above; add further RTK-only reskin dungeons here if found.

**Area spawns (`Content.AreaSpawns` ← `data/game-data/AreaSpawns.csv`).** *This is where every cave/dungeon gets its
mobs.* RTK spawns hunting-map populations from a Lua "spawner NPC" (`mobSpawnHandler.lua`), not the SQL table, via
`handleSpawn(npc, map, {mobIds}, {counts}, timer [,minX,minY,maxX,maxY])`. `re/extract_lua_spawns.py` parses those
617 calls into `AreaSpawnDef (mobId, map, count, box)` — **2371 rows, ~21.5k mobs across 767 maps** (Mythic Nexus
41, the zodiac caves — Ox Fury 176, Pig Path 181, Horse Valley 242 — wilderness, etc.). A zero box means "anywhere
walkable on the map"; otherwise the mob's home is a random walkable tile inside the box (`World.PickAreaHome`).
RTK's per-mob respawn `timer` is dropped in favour of the server's own cadence. *(Symptom this fixed: caves and the
Buya haunted-house/animal batch were empty because only `Spawns0` was loaded.)*

**Lazy materialization.** With ~21.5k mobs, `World.PopulateSpawns` no longer instantiates anything at boot — it just
builds a cheap per-map `Spawn` point list (static tiles known; area tiles chosen later). The first time a player
enters a map, `EnsureMaterialized(mapId)` (called under lock from `EnterMap`, before the room's mob list is read)
instantiates that map's roster and loads its map file once. A cave nobody visits costs nothing. `Materialize` still
places each mob via **`FreeSpawnTile`** — the home tile if open, else the nearest unoccupied non-solid tile within 2
— because several RTK points share a tile and a respawn can land where another mob wandered; without this they'd
stack. An area spawn's home tile is fixed on first materialize (respawns hug the same patch, like RTK sentries).

**Respawn.** On death `World.TryDamage` clears the spawn's `Live`, sets `RespawnTick = _tick + RespawnTicks`
(~18 s), and rolls loot. `World.Tick` refills any due point (only on maps with a player watching) via
`Materialize`, minting a fresh mob id.

**Drops (`Content.RollDrops`).** RTK's real per-mob drop tables, extracted from the server-side Lua
(`RTK-Server/rtklua/Accepted/Mobs/MobDrops.lua`) by `re/extract_mob_drops.py` into `data/game-data/MobDrops.csv`
and loaded into `Content.MobDrops` (382 mobs; a mob absent from the table drops nothing, matching RTK). Each
mob has independent `loot` lines (its own item/amount-range/percent, several can hit on one kill) and at most
one `rareLoot` line (rolled in listed order, only the first hit drops). A `GOLD` item key means a gold pile
(dropped as a `GroundItem` with `ItemId = -1`, same convention as a player's own `!` gold drop) rather than a
real item. Rolled under the world lock, added to the map's floor-item list, and broadcast as `0x07` ground
items (§11c) so anyone can pick them up.

**AI pacing (RTK-faithful).** `World.Tick` runs every 600 ms but a mob only *acts* when its own
**`MobMoveTime`** (ms, from the mob DB — rabbit/squirrel 3000, cat/fox/rat 2000) has accumulated in
`Mob.MoveTimer` (seeded random so they don't all move in lock-step). Even then it mirrors RTK `mob_ai_normal`:
`checkmove = rand(0..10)`; on `>=4` (~64 %) it picks a random facing and **only steps if that equals its current
facing, else just turns in place** (broadcast as `0x11`); on `<4` (~36 %) it steps straight ahead. Net: a rabbit
ambles a hop every few seconds and turns far more often than it moves — not a sprint every heartbeat.

**Collision (matches the player).** A step is rejected unless the target tile is in-bounds, within
`WanderRadius` (Chebyshev **2**) of `Home`, not on a player tile, **not on another mob**, and not solid.
Solidity is **`MapData.BlockedMove(x,y,dir)` = ground pass flag (`Solid`) OR the `SObj.tbl` directional
object-wall for that heading** — the SAME two-layer test `Session.HandleWalk` uses, so mobs respect the same
walls/water/cliffs/buildings the player and the client do (see §12). Passing the step direction matters:
object walls are directional (`UP/DOWN/RIGHT/LEFT`), and it's this layer — not the ground pass flag — that
stops a mob walking through a building's thin side wall (fixed 2026-07-26, "rabbits through Jadespear's hut
wall"; user-confirmed). Mob-vs-mob uses a per-tick `mobTiles` set kept current as each mob moves, so two mobs
can't share a tile or swap through each other in one tick. Players are also blocked from stepping onto a live
mob (`_world.MobAt` in `HandleWalk`; warp tiles still take precedence).

**Viewport streaming (`Session.SyncMobs`).** The `0x07` spawn is viewport-gated (§14): an off-screen spawn is
silently dropped, and the client culls entities that move off-screen. So mobs are streamed per player:
`_shownMobs` tracks which mob ids are drawn on that client; each tick (and on the player's own walk) `SyncMobs`
sends `0x07` for mobs that entered the camera rect and `0x0E` for ones that left. **`ShowPad = HidePad = 0`** —
the pads hug the *exact* 17×15 viewport: spawning ahead of the edge would mark a mob "shown" that the client
dropped (→ never appears), and keeping one "shown" past the edge leaves a dead zone the client already culled
(→ vanishes for good). `World.Tick` reconciles views **before** broadcasting moves, and `MoveMob`/`SideMob` are
no-ops for players who don't have the mob shown — bounding on-wire traffic to on-screen mobs even on a 400-mob
map. This is what lets the full spawn roster render without blanket-sending hundreds of entities.

**Colours.** The `0x07` colour byte uses RTK's **`MobLookColor`**. (An experiment sending the client's decoded
`Monster.tbl` palette instead rendered every mob green — RTK's per-mob colour matches the client for the common
critters, so we kept it. A proper RTK-colour → client-palette mapping is future work; the decoded table lives in
`data/game-data/MobLookPalettes.csv` / `Content.PaletteFor`, currently unused for spawns.)

**`rabbit` (MobId 1) had the wrong Look/Color — fixed live 2026-07-26.** The extracted `mobs.csv` row
for the base overworld `rabbit` mob (used by 242 fixed spawn points + 22 area spawns — the everyday wild
critter, not a debug spawn) carried `look 21, color 3`, which shares its sprite shape with the "Hare" family
(`look 21`, e.g. `hare` id 116 `color 37`) and visibly rendered like a hare, not a rabbit. Cross-referencing
RTK's own Lua spawn scripts (`rtklua/Accepted/NPCs/mobSpawnHandler.lua`) turned up a themed "Rabbit Warren"
dungeon (maps `4201-4208`: *Golden Warren*, *Rabbit Hole*, *Hare Depression*, *Mythic Owsla* — a Watership
Down homage) whose 3 progressive tiers are all built from **look 125** (`golden_rabbit`, `mad_hare`,
`giant_rabbit`, `hop`/`thump`/`fluff`, …) — confirming 125 is RTK's real "rabbit family" sprite, distinct
from 21's hare. Visually confirmed live via `re/monster-matcher/monster_matcher.py` (the sprite-matcher
tool, `http://localhost:8777`) plus in-game `!crecol 125 0 40` (spawns one of every color 0-40, each
labeled `col<N>` so `;`/look can read the exact number back — see the `HandleLookAt` fix below): the correct
plain-rabbit palette is **color 11**, not any of the named dungeon-tier colors. `rabbit`'s CSV row is now
`look 125, color 11`; since `Session.SpawnRabbit` (`!rabbit`) and every persistent spawn both read this one
registry row, the single data edit fixed the debug command and the whole overworld population at once — no
code change needed. **Lesson:** a `mobs.csv` row's Look/Color can be individually wrong even when the
extraction is right for the rest of the table; when a sprite looks off, verify visually (matcher tool or
`!crecol`) rather than trusting the row.

**Look-key gap found + fixed alongside this:** `HandleLookAt` (`;` key, §11) only checked `_world.MobAt`
(shared-world mobs), never the session-local debug-dummy list (`Session.MobAt`) that `!cre`/`!mob`/`!crow`/
`!crecol`/the look-lab spawn into — so pressing `;` at a `!crecol` row silently did nothing. Now checks both.

**Player HP/MP regen (`Session.RegenTick`).** The same heartbeat also heals **every connected player** — a
separate `World.Tick` pass, *not* gated on mobs or viewport like the steps above. This ports RTK's
`Player.regen` (`rtklua/Accepted/player.lua`, fired every 25 s from `pc_timer.lua`): while alive, restore
**2 % of max HP** and **2 % of max MP**, then push a `0x08` stats packet. RTK scales the vita portion by its
derived `healing` stat; we don't carry that, so **HP regen scales with Grace and MP regen with Will**
(`ceil(max * 0.02 * (1 + stat/100))`), keeping RTK's 2 % base and 25 s cadence. Each session owns a
`_regenAccum` that counts *real* elapsed ms, so the 25 s interval is independent of the 600 ms tick; the dead
(HP 0) and the already-full are skipped, and a packet is emitted only on an actual change. Effective max is
base + gear (`Totals()`). (Threading: this writes `_char.Hp/Mp` from the tick thread while the session's
read-loop also writes them on damage/heal — lock-free, consistent with the codebase's `_char` posture; a
collision at worst drops one small increment and self-corrects next tick.)

---

## 11c. Items — bag, gear, ground, and combat ⏳ (built; awaiting live 4.95 verification)

The full item system: an item registry, a per-character bag + worn gear (persisted), floor items, and the
pickup/drop/throw/use/equip handlers. Opcodes + wire layouts were translated from RTK 7.x `clif.c`
(`clif_sendadditem` / `clif_senddelitem` / `clif_equipit` / `clif_unequipit` and the `parse*` recv path).
Builders/handlers live in `Session.cs` (`SendAddItem`/`SendDelItem`/`SendEquip`/`SendUnequip`,
`HandlePickup`/`HandleDropItem`/`HandleThrow`/`HandleUseItem`/`HandleUnequip`/`HandleDropGold`); floor
items live in `World.cs` (`DropItem`/`PickUp`/`ItemsOn`, id pool `500000+`); the registry is `Content.Items`
(`ItemDef`) loaded from the gitignored `data/game-data/Items.csv` (2545 items — id, name, type, icon, look,
stat lines), same logic-only pattern as maps/mobs.

**Confidence.** The **recv** opcodes are trustworthy — 4.95's walk/turn/chat/attack/setting opcodes already
match this same RTK recv table exactly, so the item recv numbers align too. The **send** opcodes work on
the real 4.x client too (verified live: the bag populates + eat works), **but the 4.95 window layouts differ
from 5.x by one byte** (below).

**⚠ 4.95 `0x0F`/`0x37` drop the `iconColor` byte (proven live 2026-07-25).** 5.x (V533) carries an
`iconColor(u8)` byte right after the `icon(u16)`; **4.95 (V495) does NOT** — the name length follows the
icon directly. Sending the 5.x layout to 4.95 made the client read the name one byte early: **Apple**
(`iconColor=0`) → length 0 → empty name (`"You ate ."`); **Poison apple** (`iconColor=12`) → length 12 → a
12-char garble `"⊥Poison appl"` (the `⊥` is the real length byte 0x0C, then 11 of "Poison apple"). The wrong
*count* was the same off-by-one — everything after the name shifted by one. `SendAddItem`/`SendEquip`
branch on `_ver`; the tables below show the **V533** shape (V495 = same minus the `iconColor` byte).

**⚠ Binary note.** The RE reference binary `NexusTK_local.exe` **no-ops** `0x0A/0x0F/0x10/0x37/0x38` in its
world dispatcher (`remap[op-3]` → jump-table entry `0x2a` = the `xor al,al;ret` default at `0x44bbcd`), yet
the real 4.x client renders them — so the running 4.x client is a **different build** than
`NexusTK_local.exe`. Disassembly of that binary is NOT authoritative for the item opcodes; the layouts here
are driven by live behavior. (Identify the running exe to RE the exact layout: icon-id space still unmapped.)

**Icons — SOLVED (encoding, not a mapping).** The client's item-sprite resolver (`0x435ab0`) does
`spriteId = iconField + 0x4000`, then the frame indexer (`0x431450`) bounds-checks the **low 16 bits**
against the Item.epf frame count (**1310**). Sending a frame `N` raw makes `N+0x4000 ≥ 1310` → out of range →
blank icon (the `descriptor[0xc 0xc 0xc 0xc]` in Frida is the caller's `0x424280(out,0xc,0xc)` default written
*after* the failed lookup, not the frame). **Fix:** the packet icon field must be `(N - 0x4000) & 0xFFFF`,
which wraps back to `N` after the client's `+0x4000` (`Session.IconWire`). Item.epf has exactly 1310 frames ==
`Item.tbl`'s 1310 items, so **frame index == client item id**, and — confirmed live (frame 10 renders as an
apple, RTK apple `ItmIcon=10`) — **RTK's `ItmIcon` already equals the client frame**, so `IconWire(def.Icon)`
is all that's needed; no mapping table. `SendAddItem`/`SendEquip`/ground items all encode through `IconWire`.
(RTK icons ≥ 1310 are 7.x-only items and render blank — acceptable.) Debug: `!icons [start]` fills the bag
with frames `start..start+26`.

**Ground items at rest — SOLVED (2026-07-25): use `0x07`, not `0x16`.** A floor item must *persist* where it
lands, but `0x16` is a **walk projectile** (walk ctor `0x463020` → vtable `0x4cd18c`, walk tick `0x463270`):
it animates toward its rest tile then drops off the moving list / self-destructs → **invisible at rest** (and
it plays a throw sound on spawn). The fix is the **`0x07` static base-object** descriptor (§7.2): send an entry
whose `look` is **outside `0x8000..0xbfff`**, which the factory `0x44d7d0` classifies as **descriptor type 2**
→ base ctor `0x462ec0` (vtable `0x4cd118`, tick `0x4601a0` = `xor al,al;ret`, a **no-op**). It renders its
sprite statically from **Item.epf** every frame via the shared draw slot and never moves or despawns — exactly
what a resting item needs, with **no divide-by-zero risk** (that hazard is `0x16`-only; see §7.2). `IconWire(N)`
already lands in the type-2 range: frames `0..1310` → `0xc000..0xc51e`, all `> 0xbfff`. So `ShowGroundItem`
just calls `SendCreatureList(new[]{ (id, IconWire(gfx), x, y, (byte)0, (byte)0) })` — same builder as monsters,
different `look` class. Switching off `0x16` also silenced the spurious throw sound on plain drops.

**Server → client (draw the bag / gear):** all multi-byte big-endian; body offsets are from the first body
byte (= raw frame `+5`).
| Op | Meaning | Body |
|---|---|---|
| `0x0F` | add item to bag slot | `slot(u8=idx+1) icon(u16) iconColor(u8) [dispName u8len+txt] [baseName u8len+txt] amount(u32) [stack/0(u8) dura(u32) protected(u8)] [owner u8len+txt] 00 00 00` |
| `0x10` | remove from bag slot | `slot(u8=idx+1) reason(u8) 00 00` — reason `0`=Remove `1`=Drop `2`=Eat `4`=Throw `6`=Used … |

**⚠ Reason `1` is Drop-only, not a generic "removed" (found live 2026-07-26).** The `0x10` body carries no item
name — the client renders its status line ("You dropped your `X`") from whatever it already has drawn in
that slot, keyed purely off `reason`. Several removal paths were reusing reason `1` for non-drop cases —
`TakeItem` (quest turn-ins), `DlgSell`/`SellItemToNpcByName` (selling), `DepositItemToBank`/`BankDepositItem`
(banking) — so selling or turning in an item falsely announced "You dropped your `X`". Fixed: all of those now
send reason `0` (Remove, silent/generic), matching RTK's own `pc_dropitemmap` (`clif.c`), which is the ONLY
call site that ever passes reason `1`.

**⚠ Reason `0` is NOT silent either (found live 2026-07-26) — it renders "`X` removed."** `EquipFromSlot` was
sending reason `0` when moving an item from bag to gear, which showed the misleading "removed" line for an
item that wasn't actually removed from the character. RTK's real `pc_equipscript` (`pc.c:1668`,
`pc_delitem(sd, sd->invslot, 1, 6)`) uses reason `6` for equipping — the same code `ITM_USE` consumption uses
(`pc.c:2085`) — not reason `0`. Fixed to send reason `6` on equip. `0` remains correct for genuine
remove-without-a-line cases (sell/bank/quest-turn-in/GM-delete), which is what it was already used for
elsewhere.
| `0x37` | equip-window entry | `equipType(u8) icon(u16) iconColor(u8) [name u8len+txt] [baseName u8len+txt] dura(u32) 00 00` |
| `0x38` | unequip-window | `spot(u8) 00` |
| `0x07` | ground item (§7.2) | floor items go through the **`0x07` static base-object** path (below), NOT `0x16` — graphic = item's `Icon` (Item.epf frame), encoded via `IconWire` |

`equipType`/`spot` wire bytes (client `clif_getequiptype`): WEAP=1 ARMOR=2 SHIELD=3 HELM=4 NECKLACE=6
LEFT=7 RIGHT=8 BOOTS=13 MANTLE=14 COAT=16 SUBLEFT=20 SUBRIGHT=21 FACEACC=22 CROWN=23. Item `Type` (ITM_*)
maps to a gear slot for `Type ∈ 3..16` (EQ index = `Type-3`).

**⚠ Dual ring slots.** ALL rings/gauntlets are `Type 7` (156 items → wire slot **7**); `Type 8` carries
**zero** items in the data, so the right-ring box (wire **8**) is only ever reached by fall-through:
`EquipFromSlot` puts a ring in slot 7, and if 7 is already worn and 8 is free, wears it in **8** instead of
replacing. Only when both are full does a new ring replace the left. (Subleft/subright are distinct item
types with their own slots, so they need no such handling.)

**Client → server (handled in `Session.Handle`):**
| Op | Action | Body |
|---|---|---|
| `0x07` | pick up | `pickuptype(u8)` — `,` = 0 (grab the top item on my tile), `<`/Shift+`,` = 1 (grab **everything** stacked on the tile) |
| `0x08` | drop | `slot(u8=idx+1) all(u8)` — `all`: `0` = one, `1` = whole stack |
| `0x17` | throw | `confirm(u8) slot(u8=idx+1)` |
| `0x1A` | eat | `slot(u8=idx+1)` (ITM_EAT only) |
| `0x1C` | use / equip | `slot(u8=idx+1)` — equipment → wear, else consume |
| `0x1F` | unequip | `wireSlot(u8)` |
| `0x24` | drop gold | `amount(u32)` |
| `0x09` | look (`;` key) | *(no body — always the facing tile; RTK `clif_parselookat_2`)* |
| `0x19` | whisper (Shift+`'`) | `dstlen(u8) dst_name[dstlen] msglen(u8) msg[msglen] 00` — LIVE-confirmed 2026-07-26 |

**Look (`0x09`, added 2026-07-26).** The client sends this with an empty body whenever `;` is pressed — no
coordinates, always the tile immediately in front of the player (facing direction), matching RTK's
`clif_parselookat_2` (the client→server `0x09` here is unrelated to the server→client `0x0A` status opcode
below). `HandleLookAt` checks that tile in RTK's PC → mob/NPC → item order (`clif_parselookat_sub`'s per-type
branches) and echoes the name to the **status / mini-text box below the inventory** via `SendMiniText`
(server→client `0x0A`, see below) — a floor item gets `"name (amount)"` when stacked, same shape as RTK's
commented reference formatter. This matches RTK exactly: its look-at ends in `clif_sendminitext`, NOT a chat
bubble (the earlier `SendLog`/`0x0D` routing spoke the name out loud over the player's head — wrong channel,
fixed 2026-07-27). NPCs are stationary mobs (`IsNpc`) in the same shared per-map list, so the mob check
already covers them; an empty tile gets no reply at all, matching RTK (no `clif_sendminitext` call when
nothing's found).

**Status / mini-text box — server→client `0x0A` (`SendMiniText`).** The scrolling log pane beneath the
inventory (look-at names, item pickup/drop, "experience gained", map-entry rejections, whisper text). A
distinct channel from both the `0x0D` chat bubble (`SendLog`) and the `0x02` login message box
(`SendMessage`). Mirrors RTK `clif_sendmsg(sd, type, msg)` (clif.c:6484) byte-for-byte: body after the
opcode+inc is `type(u16 LE) · len(u8) · text[len]` (ASCII, `len` clamped to the u8). `type`: **0** = wisp
(blue), **3** = mini/status text (the default, `clif_sendminitext`), **5** = system, **11** = group, **12** =
clan. `0x0A` is one of the opcodes the RE reference binary `NexusTK_local.exe` no-ops in its dispatch table
yet the **live 4.95 client renders** — same group as the `0x0F`/`0x37` item opcodes we already rely on (see
the "Binary note" above); the `type=3` routing is live-proven, `type=0`'s exact pane is RTK's documented
intent (see the whisper-delivery note).

**State guards on item actions (added 2026-07-26).** RTK gates every one of drop/throw/drop-gold/equip on
player state before doing anything else — dead (`Hp==0`, "Spirit") or mounted can't perform them. Ours now
matches: `HandleDropItem`/`HandleThrow`/`HandleDropGold` reply `"Spirits can't do that."` / `"You cannot do
that while riding a mount."`; `EquipFromSlot` uses RTK's (differently-worded, verbatim-preserved) equip-specific
pair `"Spirit's can't do that."` / `"You can't do that while riding a mount."`. `HandleThrow` also now rejects
`NoDrop` items (`"You can't throw this item."`) — previously throw had no restriction at all, unlike drop.

**Semantics.** Equipping a **weapon** (`Type 3`), **armor** (`Type 4`) or **shield** (`Type 5`) changes the
4.95 look — the item's `Look` goes into the 7-byte type-0 form (slot [5]=weapon, [3]=armor, [6]=shield) and
re-draws self + peers; other gear slots have no 4.95 appearance. Weapon/shield use the `0xFF`-when-bare rule
(see §8). Floor items broadcast to everyone on the map
and survive until picked up; gold drops as a sentinel ground pile (`ItemId = -1`) that refills the purse on
pickup.

**Equip stat bonuses + wear requirements — SOLVED (2026-07-25).** Worn gear now feeds the HUD/profile and
combat. The character's `_char.*` stats stay the **base**; the effective values are `base + Σ(worn-gear
lines)`, recomputed on every send by `Session.EquipTotals()` — nothing is ever baked into the base, so a
relog (which reloads `Equipment` and redraws it) can't drift or double-count. Mapping: `Vita→maxHP`,
`Mana→maxMP`, `Might/Will/Grace→` those stats (`0x08`), and `Armor→AC`, `Hit`, `Dam→` the profile (`0x39`).
**AC is signed and lower is better**, so armor **subtracts**. `EquipFromSlot`/`HandleUnequip` push a fresh
`0x08` immediately; current HP/MP are clamped to the (possibly reduced) effective cap after an unequip. Melee
(`HandleAttack`) uses effective Might + gear `Dam` + the flat weapon bonus. **Wear requirements** (checked in
`EquipFromSlot`, from `ItemDef`): sex-lock (`ItmSex` — **`0`=male-only, `1`=female-only, `2`=unisex**; the
unisex `2` is the common case at 1944/2545 items, so the gate only fires when `ItmSex < 2` and mismatches
`Character.Sex`, which uses the same `0`=M/`1`=F encoding), minimum **level** (`ItmLevel`), and minimum
**might** (`ItmMightRequired`, tested against *effective* might so already-worn +might gear counts). The
client also parses a **path/class** restriction (`ItmPthId`), but the bring-up character has no path id yet,
so it is not enforced. GM setters `!lvl <n>` / `!might <n>` adjust the base stats to exercise the gates.

**Cursed/malus gear gate (added 2026-07-26, RTK `pc_canequipstats`).** 14/19 items in the registry carry a
*negative* `ItmVita`/`ItmMana` line (a stat penalty, not a bonus). RTK blocks equipping one if the penalty's
magnitude would exceed your current effective max — it'd zero the pool out entirely — replying `"You lack the
health required to wield that."` (Vita) or `"You lack the wisdom required to wield that."` (Mana, RTK's own
wording). Checked against `EffMaxHp`/`EffMaxMp` (before this item's own effect), same as the might gate above.

**Item action animations (0x1A) — each item verb plays its bend-down pose + sound, on self AND peers.**
Every item handler broadcasts a `0x1A` action (§13, builder `Session.SendAction` / peer `ActionOver`) so the
character visibly stoops and the client plays the baked-in sound. The `(type, time)` pairs are RTK's
(`clif.c`): **pickup = `(4, 40)`** (RTK `clif_parsegetitem`), **drop = `(5, 20)`** (`clif_parsedropitem` — a
*distinct* pose from pickup), **throw = `(2, 20)`**, **eat = `(8, 40)`**. `sound` is 0 (the action sprite
carries its own sound; a non-zero 4th arg would be a separate sound id). Ordering matches RTK: pickup plays
the action **unconditionally** (even on an empty tile — the crouch fires on the keypress), while drop plays it
only **after** the `NoDrop`/valid-slot guard passes. `<` (pick-up-all) plays the action once, then loops the
tile until empty.

**Throw collision — SOLVED (2026-07-25; object-wall aware 2026-07-26).** `HandleThrow` walks the item
**tile-by-tile** from the player in the faced direction (0=N 1=E 2=S 3=W, capped at 3 tiles) and stops at the
last *passable* tile, so items never come to rest on a wall or an unreachable tile. Passability is the **same
two-layer test the walk uses** (§12): the **ground pass flag** (`Blocked` = `map.Pass(x,y) != 0`; top 2 bits
of the ground `u16`, `3` = solid, `0` = walkable, `1`/`2` never occur) **OR** the **`SObj.tbl` directional
object-wall** for the throw heading (`ObjectFlags.Blocks`) — so a thrown item halts at a building's side
wall, not only at water/cliffs. Enforcement is `NEXUS_PASS` (default on; set `0` to disable); the walk
(`HandleWalk`) applies the identical two-layer check.

**Doors ('o' key / `0x20`) — WORKING (2026-07-25).** A door is an object drawn over the map. Pressing 'o'
facing a door **toggles its open/closed graphic in place** — RTK `openDoors` (`open.lua`) does
`setObject(m,x,y, closed↔open)`, e.g. Buya door `342 ↔ 364` (some doors span 3 tiles: x, x+1, x+2). It is
**cosmetic only** (passability doesn't change) and shared world state. Entering a building is done by
**walking** onto its warp tile (§warps), not by 'o'. Not every door leads anywhere — many are **decorative
facades** (a passable gap in a wall sprite with no interior); RTK's authoritative `Warps` table confirms which
doors are real entrances. `Session.HandleOpen`: reads the faced object, looks it up in the door toggle table
(transcribed from `open.lua`), mutates the shared `MapData` object layer (so the next 'o' toggles it back), and
sends the **`0x06` cell-patch** (see §13) to every client on the map to redraw. The client re-renders the
object layer over the patched rectangle regardless of whether the ground word changed. Full door toggle table:
memory `nexustk-495-doors`.

**Scripted-tile warps — Mythic Nexus zodiac caves (map 41) — WORKING (2026-07-25).** Not every warp lives in
the SQL `Warps` table. The 12 zodiac cave entrances on Mythic Nexus (map 41) are RTK **Lua tile-scripts**
(`onScriptedTiles/onScriptedTilesMythic.lua` → `NPCs/mythic/mythic_cave_selector.lua`), so a step onto one is
intercepted in `HandleWalk` **before** the collision test (like a warp), by `Session.TryMythicCaveEntrance`.
Each animal has a **two-tile footprint** on map 41 (e.g. Rabbit `(49,12)`/`(50,12)`, Dragon `(29,19)`/`(30,19)`)
and a cave-1 destination map (Rabbit→201, Ox→170, Pig→181, Horse→246, Dragon→257, …). The cave has **three
depth tiers**: cave 1 = base map, cave 2 = `base+3000`, cave 3 = `base+4000` (all renderable — e.g. `170` Red
Bull 1 / `3170` Red Bull 2 / `4170` Red Bull 3). Entry is **level/vitals-gated** (`Scripts/mythicCaveReqCheck.lua`):
per animal, tier *t* needs `level ≥ Lₜ` **and** (`maxHP ≥ Hₜ` **or** `maxMP ≥ Mₜ`) — tier-1 has no HP/MP floor,
so level alone unlocks it (Rabbit L25, Monkey L32, Dog L39, … +7 per animal, Dragon L99). RTK's picker menu is
GM/Config-only; with it off (our default) it **auto-warps to the deepest tier the player qualifies for**, which
`TryMythicCaveEntrance` reproduces. Under-levelled → refused with a flavour line (8+ levels short = *"Nightmarish
visions of your own death repel you."*, 4–7 = *"You are not yet ready…"*, ≤3 = *"You almost understand…"*) and
the step is cancelled. Test with `!lvl <N>` (the bring-up character is L1, so every cave refuses until levelled).

**More scripted tiles: fall-rooms & foraging — WORKING (2026-07-25).** RTK's `onScriptedTile` runs a stack of
per-walk handlers (`onScriptedTiles/`); `Session.OnScriptedTileStep` (fired at the END of a completed step, when
the player stands on the new tile) ports the two that are self-contained **and** live entirely on renderable 4.x
maps:
- **Mythic fall-rooms** (`onScriptedTilesMythicFallRooms.lua`) — inside a zodiac cave, every step has a **1/500
  chance to drop through the floor** to a fixed landing tile in a lower sub-room (e.g. Ox `177/178 → 180 (22,7)`,
  Monkey `167/168 → 169 (23,3)`; +3000/+4000 for cave tiers 2/3; plus the Iron lab `1302-1306 → 1307`). All ~90
  source and destination maps render. `TryMythicFallRoom` (table `FallRooms`, built from `FallGroups`) warps via
  `EnterMap`; it no-ops if the destination isn't renderable, so it can never strand a player.
- **Bush/tree foraging** (`onScriptedTilesBushTree.lua`) — standing adjacent to an **apple tree** (object ids
  `860-864`) or a **rose bush** (`876-889`), each step has a **1/50 chance** to pick an *apple* (`10001`) / *rose*
  (`21001`). `TryForage` scans the 3×3 object layer around the player (`MapData.Obj`) — the `.map` object word IS
  RTK's object id, verified: apple trees appear on 37 renderable maps (Kugnae 685×, Vale 905×, Mythic Nexus 161×),
  rose bushes on 24 (Kugnae 555×, Buya 214×, Wilderness 562×), so it fires against real map data.

**Class path-hall doorways — WORKING (2026-07-25).** Same SQL-vs-Lua split, caught live: inside a class hall the
north (path-leader) and south (guild) doors did nothing because only the *map-exit* warp is in `Warps.csv` — the
two interior doors are scripted (`onScriptedTilesPathHalls.lua`). Each of the **8 halls** (Kugnae Warrior Tebaek
`11` · Rogue Maro `15` · Mage Haedu `13` · Poet Jinsun `17`; Buya Warrior Yebaek `341` · Rogue Maso `343` · Mage
Eldritch `342` · Poet Song `344`) has:
- **South edge** `(x 1-2, y 23)` → that class's **guild hall** (Kugnae `3701-3704` / Buya `3705-3708`), arriving
  `(x+6, 3)`. **Class-gated**: only members of the hall's base class pass (`CharClassId == BaseClass`); RTK also
  admits a `player.tutor` (a staff role we don't model), so wrong-class = minitext *"You are not the right class
  to enter here."* + `SendXy` hold.
- **North edge** `(x 8-9, y 1)` → the player's **alignment sanctum** (path-leader room), indexed by
  `Character.Alignment` 0-3 (Unaligned / Kwisin / Mingken / Ohaeng), arriving `(x-3, 18)`. Mage Eldritch, e.g.,
  maps to `{367, 309, 310, 311}`.

`Session.TryPathHallWarp(x,y)` + static `PathHalls` table (`record PathHall{BaseClass, Hall, Sanctum[4]}`), called
in `HandleWalk` right after the Mythic check and **before** the collision test (these doors sit on edge tiles that
read as solid). Every one of the 24 destinations is renderable; `WarpHall` no-ops (holds) if a dest map is missing,
so it can never strand a player. Live-confirmed working 2026-07-25.

Other `onScriptedTiles/` handlers were surveyed and **skipped**: subpath-trial entrances (most destination maps
absent from the 4.95 client — only diviner `3540` / druid `3632` render), Tutor's Haven / Elixir Hall / Mount
Baekdu / Nagnang-shield / generic quest halls (host maps not renderable = 7.x content), and the Tower Arena /
Carnage Hall minigames (maps render but need a full event-manager). See memory `nexustk-495-scripted-tile-warps`.

**GM commands:** `!items [filter]` (browse registry), `!item <name/id> [amount]` (summon into bag),
`!clearinv` (reset bag + gear).

---

## 11d. Spells & skills ⏳ (built; casting live-confirmed via Gateway — effects/formulas still awaiting broad 4.95 verification)

The spellbook/skillbook is **server-authoritative**: the client ships spell *icons* (`SPELLINV.epf`,
`Icon.epf` in `Inter.dat`) but **no spell-definition table** — the server sends each spell's name, type and
prompt in the add-spell packet and the client just renders it. So the spell *roster* (names, class, level,
prompt) is external data, taken from the RTK `Spells` table (real NexusTK-lineage content — Kugnae/nation
geography matches, unlike Mithia). 906 rows → `Content.Spells` (841 after dropping section-header + inactive
rows). Each `SpellDef` = `Id, Key(SplIdentifier), Name(SplDescription), Type, PathId, Level, Question`.

- **`PathId`** = the class that learns it: **0 Peasant · 1 Warrior · 2 Rogue · 3 Mage · 4 Poet**, 5+ subpaths,
  99 = system/common. From the RTK `Paths` table (`Content.Paths`, `PthMark0` = class name; higher marks are
  per-rank titles). `Character.ClassName` (set with `!class`) resolves to a path id via `PathIdForClass`.
- **`Level`** = the character level required. Most advanced-class spells in this dump are level 0 (gated by
  path/subpath, not base level); Peasant has a 0/1/25 spread. `!spells` teaches every class spell with
  `Level ≤ Character.Level` — **plus the path-0 spells that are TRULY universal** (Soothe, Gateway, Mentor,
  Propose), which every class keeps after subpathing. `Content.SpellsForClass` unions `PathId == yourClass`
  with `PathId == 0`, so a Warrior/Rogue/Mage/Poet still gets those (Soothe & Gateway are level 1, so
  `!lvl ≥ 1`).
  > **Return/Approach/Summon are NOT part of that peasant-commons union (corrected 2026-07-27)** — the CSV's
  > `SplPthId=0` makes them look identical to Soothe/Gateway/Mentor/Propose, and none of the four scripts
  > (`return.lua`/`approach.lua`/`summon.lua`) contain a class check either, so this was genuinely
  > undecidable from the extracted data or the Lua source alone. Confirmed against the user's own play
  > knowledge: all three are **Rogue/Mage/Poet only, never Warrior**, each at a *different* level per class
  > (impossible to express in the single CSV row's one `Level` field). `Content.RestrictedCommonsLevel`
  > carries this as a `key -> {pathId -> level}` table that `SpellsForClass` consults before falling back to
  > the universal-PathId-0 rule.
  >
  > **Return's levels were corrected again the same day (2026-07-27)** after cross-checking
  > `C:\Users\brian\Desktop\scraped_nexus_data\` (tswolf.com + boards.nexustk.com tutor posts), which the
  > user ranks above the RTK Lua for real game facts: **Return is Mage 13 / Rogue 45 / Poet 32**, not 32 for
  > all three as first believed — tswolf's `mage.shtml` and a Mage tutor-board post independently agree on
  > 13. Approach stayed **Mage 20 / Poet 29 / Rogue 35**; Summon stayed **Mage 30 / Poet 38 / Rogue 53** —
  > both fully confirmed by the same archive. See the `nexustk-495-restricted-commons-spells` memory for
  > the full reconciliation (including the Soothe/Propose conflicts that came up and were NOT folded into
  > this table, since they resolved differently — see the learn-cost note below).
  >
  > **Real per-class learn COSTS (not just levels) are now enforced** for these 6 spells (Propose
  > deliberately excluded — see below) via `Content.LearnCosts` (`Dictionary<string, Dictionary<int,
  > LearnCost>>`), checked/charged in `ClassTrainerAbility.LearnSecret` (`NpcAbility.cs`) — NOT inside
  > `Session.LearnSpellFromNpc` itself, so the debug `!spells`/`!learnspell` commands (which never call that
  > method — they mutate `_char.Spells` directly) stay free, matching prior behavior:
  > - Gateway (all): 10 acorn + 10 rabbit meat, free
  > - Return: Mage 30 acorn + 50g · Rogue 100 acorn + 100g · Poet 1 yellow_scroll
  > - Approach: Mage 50 acorn + 20 snake_meat · Rogue 100 acorn + 10 fox_fur + 100g · Poet 1 gold_acorn + 100g
  > - Summon: Mage 80 acorn + 10 snake_meat + 50g · Rogue 100 acorn + 500g · Poet 70 acorn + 100g
  > - Mentor (all): 1 class item (Warrior maxcaliber / Rogue moonblade / Mage deaths_head / Poet
  >   wicked_staff) + 1000g
  > - Soothe: intentionally NOT in `LearnCosts` — tswolf confirms it's a free Newbie Quest reward (5 acorn +
  >   5 rabbit meat, no gold), matching the Lua exactly; conflicting tutor-board posts giving Mage-6/
  >   Warrior-8 gold costs are presumably a later-era trainer fallback, not the base mechanic.
  > - Propose: intentionally NOT in `LearnCosts` either — its `SplPthId=99` never matches any class's teach
  >   filter, so it was never learnable via a trainer at all. Its real "cost" is the `engagement_ring`'s
  >   shop price, already charged in the pre-existing `ChapelAbility.BuyRing` (§ marriage), which grants the
  >   spell directly via `ctx.LearnSpell(propose)`.
- **`Alignment`** (`SplAlignment`) = the sub-alignment: **-1** universal · **0** base/unaligned · **1** Kwisin ·
  **2** Mingken · **3** Ohaeng. Each class's spells split roughly evenly across 0/1/2/3 (e.g. Warrior ~21 each),
  and the four variants often **share a display name** (Rogue's "Maro's Remedy" ×4) — so teaching all of them
  produced literal duplicates *and* handed a character three alignments they can't use. `Character.Alignment`
  (0 unaligned / 1 Kwisin / 2 Mingken / 3 Ohaeng, set with `!align`) gates the teach: `SpellsForClass` keeps
  only universal (-1) + the character's own alignment, then dedupes by display name (preferring the exact
  alignment over universal). Result: **one** alignment set, no duplicates — Warrior 86→23, Mage 163→46 (also
  keeps every class under the 52-slot cap). Default alignment is 0 (Unaligned) = the base set.
- **`Type`** = the client's book discriminator (also the cast-shape selector): **1** = prompt spell (client
  asks `Question`, e.g. Gateway "Which Gate(N,E,S,W)?"), **2** = targeted (client sends a target entity id),
  **5** = self / no-target. type 1/2 render in the **Spell** book, type 5 in the **Skill** book — one packet,
  keyed on type.

**Add-spell — server→client `0x17`** (from RTK 7.x `clif_sendmagic`; body decrypted, big-endian):
```
slot(u8 = idx+1)  type(u8)  nameLen(u8) name  questionLen(u8) question
```
`0x17` is a **no-op in the main world dispatcher** (`remap[0x17-3] = 0x2a`, the default) — **exactly** like the
item opcodes `0x0F/0x10/0x37/0x38`, which are no-ops there too yet work live via the client's *secondary*
dispatcher. That shared property is the evidence the add-spell path is handled the same way (needs one live
confirm, same as items did). Learned ids persist in `Character.Spells` (book order) and are re-sent on world
entry by `RefreshSpells`. Remove-spell = **`0x18`** (`slot(u8=pos+1)`), used by `!forgetspells`.

**Cast — client→server `0x0F`** (RTK `clif_parsemagic`): `body[0] = book slot+1`, then by the learned spell's
type: **type 1** → the typed answer string (a **NUL-terminated ASCII** string right after the slot byte — RTK
`strcpy`s it from offset 6, *not* length-prefixed, unlike the server→client `0x17` strings); **type 2** → target
entity id (u32BE); **type 5** → nothing.
**Live-confirmed** (2026-07-25): casting sends `0f | slot+1 00` — the client casts with **just the slot** (no
target attached even for combat spells), e.g. `0f 1a 00` = cast book slot 26. So the server can't rely on a
packet-supplied target.

`HandleCast` plays the cast animation (`0x1A` **type 6 = magic**) for the caster + peers, then applies the
spell's effect via a **data-driven magic engine** (`Session.ApplyCast`). The effect data is extracted straight
from RTK's Lua spell scripts by `re/extract_spell_formulas.py` → `data/game-data/spell_effects.csv` (gitignored),
loaded into `Content.SpellFx` and joined to each spell by identifier (the Lua table name == `SplIdentifier`).
Each row carries an **archetype** + the spell's **real RTK numbers**: the damage/heal *formula string*, the true
mana cost, buff stat+amount+duration, debuff kind+duration+chance, cooldown ("aether"). 621 of the ~841 spells
have a row (100 % of the four caster classes' teachable sets); the rest fall back to the keyword classifier.

`ApplyCast` dispatches on the archetype:
- **Damage** → evaluates the spell's actual Lua damage formula (e.g. Spark = `15 + floor(level/2) + floor((will+3)/4)`)
  via `Content.Formula` — a tiny arithmetic evaluator over the caster's effective stats (`player.level/will/…`,
  `target.baseHealth`) supporting `+ - * /`, parens, decimals and `math.floor/ceil/random`. Spends the real mana
  cost, hits the packet target id if present else **the mob on the tile the caster faces** (like melee), reusing
  the world damage/despawn/exp path so a spell kill rewards exp/loot exactly like a melee kill.
- **Heal** → evaluates the real heal amount and restores the caster's HP (clamped to effective max).
- **Buff** → applies the spell's timed stat modifier(s) (might/hit/dam/…) for its RTK duration as a session-local
  `ActiveBuff`, folded live into the HUD/profile/melee through `Session.Totals()` (gear + buffs). Recast refreshes.
- **Debuff** → paralyze/sleep: freezes the target mob's wandering (`Mob.FrozenUntil`, honoured by `World.Tick`)
  for the RTK duration, subject to the spell's hit-chance roll.
- **ManaBattery** (Invoke / Spirit's Power / …) → verbatim RTK: HP cost = 40 % of *max* mana (HP floored at 100),
  refill mana to full, 22 s cooldown.
- **Gateway** (`Session.CastGateway`, base key `gateway`) → **live-confirmed on 4.95 (2026-07-25)** — warped
  correctly, which also proves the type-1 answer wire format above. Intercepted before the archetype dispatch (a
  teleport has no damage/heal row). Ports `Accepted/Spells/common/gateway.lua`: warp to one of the four **city gates** of the
  caster's kingdom, the gate chosen by the type-1 N/E/S/W answer, the kingdom by the caster's **region**
  (`Content.RegionOf`, from the RTK `Maps` table's `MapRegion` — 0 Kugnae · 1 Buya · 2 Mythic · 3 Nagnang; each
  region's city map + four gate spawn-boxes are the verbatim RTK coords). Lands on a random tile inside the gate's
  box (RTK's `math.random` spread) via the normal `EnterMap` redraw. Guards match RTK: the dead can't cast, a
  `MapWarpout == 0` map says *"It doesn't work here"*, a non-kingdom map *"Cannot find any gates!"*. **No mana cost**
  (RTK's gateway only calls `canCast`, a state check — no debit). Other regions (Baekdu/instances) TBD.
- **Cure / Utility / Summon / other Teleport / Dialog** → spend the real mana + acknowledge (bespoke behaviour TBD).

The formulas + costs are now **RTK-authoritative** (ported from the Lua), not server guesses. What stays a design
choice is the *targeting/routing* the 4.95 client doesn't pin down (self-heal vs. ally, front-tile vs. packet
target) and the effects we don't yet model (real summons, teleports, PvP).

**Spell effect graphics — wired via `0x29`.** Each cast plays its real `Effect.tbl` animation over the target
(Damage/Debuff) or the caster (Heal/Buff/Invoke): `Content.EffectAnim(fx, pathId)` returns the id, `Session.
SendEffect` emits the `0x29` packet (see §7.3 for the opcode). The id comes from the spell's own `sendAnimation`
call when it has one (buffs, Invoke → 11), else from the `pcalign` ladder in the shared helper — ported verbatim
to `Content.ZapEffect` / `HealEffect` (spark → 28, thunder bolt → 27, kwi-sin zap → 17, unaligned heal → 5,
kwi-sin heal → 65, …). Spells with no export row get a generic zap (4) / heal (5).

**Sound — wired via `0x19` type 0.** RTK pairs each `pcalign` with a **sound id** (zap = 56, heal = 4, fire = 88,
kwi-sin heal = 98); `Content.EffectSound` returns it and `Session.SendSound` emits a one-shot SFX using the same
`0x19` packet as background music with `type = 0` (the wav/sfx channel — see §7.3). Each cast broadcasts effect +
sound together (`Session.BroadcastFx`). The caster's `0x1A` magic pose still plays too.

**Icons:** the `0x17` add-spell carries **no icon field** (neither does RTK's `clif_sendmagic`) — the client
resolves the book icon internally, so no icon-id mapping pass is needed on the server side (unlike items/mobs).

**Slot cap:** the client's book array size is unconfirmed for 4.95; RTK 7.x uses 52 (`MAX_SPELLS`). `!spells`
caps at 52 (env `NEXUS_SPELLBOOK_CAP`) so an over-long teach can't overrun the client array — raise once a live
test confirms the real limit (Mage has 163 class spells, so the cap bites until then).

**GM commands:** `!class <Warrior|Rogue|Mage|Poet|Peasant>` (set class), `!align <Unaligned|Kwisin|Mingken|Ohaeng>`
(set sub-alignment) + `!lvl <n>` (set level), then **`!spells`** = learn every class spell ≤ level for that
alignment and fill the book; `!learnspell <name|id>` = learn one (any class, for testing); `!forgetspells` =
clear the book.

---

## 11e. NPCs & dialog ✅ (live-confirmed on 4.95)

NPCs are **stationary "mobs that don't fight."** They're placed from RTK `NPCs.csv` (`Content.Npcs` →
`World.PopulateNpcs`) as `Mob`s with `IsNpc = true`, so they reuse the entire creature pipeline for free:
`0x07` render (portrait/look = `0x8000|look`, §11a), viewport streaming (`SyncMobs`), and tile collision.
Differences from a real mob: `World.TryDamage` rejects them (indestructible), they never respawn, and a
click opens a dialog instead of a profile. NPCs with an RTK `NpcMoveTime`+`NpcReturnDistance` pace, leashed
to `Mob.Leash` (others stand still).

**Click → dialog.** The click (`0x43`, §7.1) resolves the entity via `World.MobById`; if it's an NPC, the
server runs its behaviour instead of the click-profile.

**Left-click vs. right-click (live-confirmed 2026-07-26).** `0x43` is a **left-click-only** packet. Right-
click in this client never reaches the server as an entity click at all — it's pure client-local walk-to-
click pathing (§10.3's self-walk primitive), and the "bumping into the mob" visual when you right-click
onto its tile is the client obstructing its own path locally (`0x69`, a documented no-op even in real RTK's
`clif.c` — commented out except for a GM debug branch). The server has **no way to intercept or suppress**
that walk animation; the only server-controllable feedback for "what IS that" is the left-click `0x43` path
below.

**Click a real (non-NPC) mob → name-only mini-text (changed 2026-07-26, user request).** RTK's own handler
(`clif.c` `clif_handle_clickgetinfo`, `BL_MOB` case) runs an `onLook` script whose player-facing branch is
gated on `player.gmLevel > 0` — a GM gets a minitext readout (name/id/level/HP/AC), everyone else gets no
packet at all (this was matched as-is from 2026-07-25 to 2026-07-26). We now deliberately diverge from that:
`HandleClickInfo` sends `"It's a/an {mob.Name}."` via mini-text for any regular player who left-clicks a real
mob, since right-click-to-walk can't be intercepted (above) and this is the only server-side lever available.
Previously (2026-07-25 fix) it used to fall through to `SendClickProfile`, which unconditionally serializes
**your own** `_char`, so clicking a monster rendered your own profile mislabeled with the mob's id.

**Click another real player → their real profile (fixed 2026-07-26).** RTK's `clif_clickonplayer` — same
`0x34` opcode, populated from the TARGET's own data, not the clicker's. `SendClickProfile` now takes a
target `Session` (found via `World.PlayerById`) instead of implicitly using `_char`; its helpers
(`WeaponLook`/`ShieldLook`/`ProfileCellIcon`/`GearListText`) are called ON that target session, which works
because they're private instance methods of the same `Session` class — legal to call cross-instance. This
is more than cosmetic: the group/exchange status cells in that packet (§9.5) are what the client reads to
decide whether the "Group"/"Exchange" buttons on the window are enabled, which is the REAL way a player
starts a party or trade (§11l) — not a chat command. An id matching nobody at all is still a no-op.

### `0x30` server → client — the dialog packet (three sub-kinds)

Same frame + graphic-head as everything else (RTK `WFIFO(fd,N)` ↔ this server's `body[N-5]`; ported from
RTK `clif_scriptmes` / `clif_scriptmenuseq` / `clif_inputseq`). The head is shared; the **kind bytes**
`body[0..1]` and the tail differ:

| Sub-kind | `body[0..1]` | Tail after the prompt | Purpose |
|---|---|---|---|
| Text | `00 01` | — (a close/next box) | plain message |
| Menu | `02 02` | `count(u8)` then each item = `len(u8)+ASCII` | button list |
| Input | `04 04` | `0`(dialog2 len) `*`(0x2a sep) `0`(dialog3 len) `00 00` pad | free-text entry |

Shared head/prompt layout (all three): `body[2..5]` npc id(u32BE) · `[6]` head kind (0 none / 1 npc gfx /
2 item gfx, classified from the graphic like RTK) · `[7]=1` · `[8..9]` gfx(u16BE) · `[10]` color · `[11]=1`
· `[12..13]` gfx · `[14]` color · `[15..18]`=1 · `[19]` prev-button · `[20]` next-button · `[21..22]` prompt
len(u16BE) · `[23..]` prompt (ASCII). **Portrait gfx = `0x8000|look`** (creature sprite from Monster.epf,
same encoding as the on-map spawn — RTK `clif.c:3190`); `0` → no portrait.

### `0x3a` client → server — the reply

`body[0]` kind (`01` text next/close · `02` menu · `04` input) · `[8]` step · `[10]` menu index (**1-based**;
0 = cancel) **or** input length · `[11..]` input text. For input, RTK requires `step==2` to count as a real
submit (else it's a cancel). `HandleNpcDialog` just completes the awaited `TaskCompletionSource` — see below.

### Server-side design — async dialog + composable abilities

There are no Lua coroutines, so the flow is **async/await**: each `Dlg*` primitive sends a `0x30` and awaits
a `DialogReply`; the `0x3a` handler completes that task, resuming the behaviour **inline on the read thread**
(no cross-thread state races). Behaviours therefore read as linear script — `var c = await ctx.Menu(...)`,
a `while` loop for a shop that stays open — instead of callback trees.

An NPC is a **composition of reusable abilities** (`INpcAbility` in `Server/NpcAbility.cs`): `ShopAbility`,
`BankAbility`, `TransportAbility`, `TimeAbility`, `RepairAbility`, `AppearanceAbility`, `WarPaintAbility`,
`ClassTrainerAbility`, `ShadowStatsAbility`, `ChapelAbility`, … plus `InlineAbility` for one-off options.
`Server/NpcScripts.cs` maps NPC identifier → its abilities; unregistered NPCs derive abilities from their
data flags (so a plain stocked shop is zero-config). Each NPC declares only what's unique to it.

- **Shop** (`ShopAbility` → `DlgBuy`/`DlgSell`): Buy/Sell menus; catalogue in `Server/Shops.cs` keyed by NPC
  id (smith, inn), prices from `Items.csv` (`BuyPrice`/`SellPrice`). Uses menus, **not** the `0x2f` grid.
- **Bank** (`BankAbility` → `DlgBank`): deposit/withdraw coin (via the input box, capped 100M) and items;
  stored on `Character.BankMoney`/`BankItems`, persisted in the character JSON. Joint accounts out of scope.
- **War paint** (`WarPaintAbility`, RTK `arena_master.lua` → `general_npc_funcs.warPaint`): the Arena Master's
  (`ArenaMasterNpc` — "Mountain"/"Tower") *only* service, an armor dye. Bleach back to base (10g) when dyed;
  else pick 1 of 8 team-battle colors (20g); level-99 characters are also offered special dyes (Brown/Wasabi/
  Super Wasabi, gated on base Vita/Mana) before the team menu. Writes `Character.ArmorColor` → appearance `[4]`
  (§8), redrawn on self + peers. Color values are RTK's palette indices — the 4.95 index→color map isn't
  calibrated yet (see the `!dye` note in §8), so team colors may need adjusting once swept.

**Spoken shortcuts** (`ShopAbility`/`BankAbility` also implement `INpcSayHandler`, so these skip the menu
entirely — real NexusTK commands, not an RTK-Lua invention; RTK's own NPC scripts have no buy/sell/deposit
speech grammar at all, click-menu only, which is exactly why these live in this server's own ability code
rather than being ported from a `*.lua` file):
- `"buy [my] all <item>"` — sell every one of a fuzzy-matched item to a nearby shop NPC
  (`SellItemToNpcByName`, amount = whole stack). `"all"` always needs an item after it — bare `"buy my all"`
  isn't a command and falls through as ordinary chat.
- `"buy [my] N <item>"` — sell exactly `N` of the item.
- `"buy [my] <item>"` (no quantifier) — sell one.
- Item-name matching tries the spoken word as typed, then singularized (`Content.FindItem` matches on the
  registry's singular names, e.g. `"acorn"`, while players usually say `"acorns"`). Independent of this NPC's
  own Buy catalogue — any shop-flagged NPC buys anything sellable, same set `DlgSell`'s menu offers.
- `"take my [all] <item|coin> [N]"` — **deposit** into the vault (`DepositItemToBank`); the bank "takes"
  from you. `"give my [all] <item|coin> [N]"` — **withdraw** (`WithdrawItemFromBank`); the bank "gives" to
  you. `"coin"`/`"coins"` targets money instead of an item. Note the word order is the *opposite* of the
  shop command above: `"all"` is always a prefix (`"take my all coin"`), but a specific count is a *suffix*
  after the item (`"give my coin 500"`, not `"give my 500 coin"`) — confirmed live, not a typo. No
  quantifier at all (`"take my acorn"`) moves exactly one.
- Confirmation reply is a **`0x0D` over-head bubble** (`Session.NpcBubble`), same channel a PC's own speech
  uses — **not** a `0x30` dialog box. Speech triggered these without opening a dialog, so the response
  shouldn't pop one open either; `SellItemToNpcByName`/`DepositItemToBank`/`WithdrawItemFromBank` all reply
  via `NpcBubble`, unlike the click-menu `DlgBuy`/`DlgSell`/`BankDeposit*`/`BankWithdraw*` paths, which
  correctly keep replying inside the `0x30` box the player already has open.

(An earlier draft of this also added a waypoint fast-travel network ported from RTK's `Waypoint.lua`,
reachable by saying `"transport"` or a destination keyword. That was reverted — it's an RTK-only addition
with no evidence of existing in original 4.x/5.x NexusTK, so `TransportAbility` is back to being a stub.)

---

## 11f. Monster combat AI, death/revive, and the home-city spawn

**Combat AI** (`World.Tick`, `Mob.TargetId`/`Level`/`AttackTime`) mirrors RTK's actual `mob_ai_normal.lua`
threat model for the *fights-back* half — a mob only chases once hit. `World.TryDamage` takes
an `attackerId` and, on a non-lethal hit, sets `mob.TargetId` to the attacker. Each tick, a targeted mob

**Unprovoked aggro** (fixed 2026-07-26 — "monster aggro is not working as expected, some monsters should
default to attacking the player, but they aren't"): the fights-back model above is only HALF of RTK's mob
AI, and it's the Lua half. RTK's C engine (`mob.c`) gates a mob's per-tick `move`/`attack` script call on
`mob->data->type` — `0=Normal, 1=Aggressive, 2=Stationary` (RTK SQL column `MobBehavior`) — and for
`type==1` it calls `mob_find_target` (a full-screen-ish area scan for the nearest `BL_PC`) UNPROVOKED,
*before* `mob_ai_normal.lua` ever runs. `mob_ai_normal.lua` itself never attacks first — so reviewing only
the Lua scripts (as the original port did) makes every mob look purely reactive, when most are actually
engine-level aggressive. Real data backs this up: of 713 mobs in the CTK reference dump, 603 have
`MobBehavior=1` (aggressive) and only 109 are `0` (passive — mostly herd/prey critters: rabbit, deer,
squirrel, doe, …). `MobDef.Aggressive` (`Content.cs`, parsed from `mobs.csv`'s merged-in `MobBehavior`
column) and `Mob.Aggressive` carry this through to a live mob. `World.Tick` now scans, for every mob with
`TargetId==0` and `Aggressive==true`, for the nearest living player within `AggroRadius` (8 tiles,
Chebyshev, from the mob's current tile — roughly the player's own 17x15 viewport) and locks onto them
exactly as if they'd just landed a hit; the existing chase/attack branch immediately takes over. A targeted mob
abandons wandering to path toward that player (greedy step, respecting walls/other entities) and, once
**cardinally adjacent** (fixed 2026-07-26 — was `Math.Max(|dx|,|dy|)<=1`, Chebyshev, which let a mob swing
at a player standing diagonally; user: "The horse can also attack me (melee) diagonally... not sure if
that's related" — it was the same bug as the horse-mount range below, both traced to Chebyshev distance.
Now `(dx==0 && |dy|==1) || (dy==0 && |dx|==1)` only, matching the player's own melee, which only ever
checks its single `FrontTile()` — a diagonal target falls through to the chase step instead, which moves
one axis at a time and closes to cardinal within a tick), swings on a cooldown (`AttackTime`, default 2s)
for `World.MobSwingDamage(mob.MinDam, mob.MaxDam)` damage (see below). It gives up and resumes wandering if
the target dies, disconnects, or strays more than `ChaseLeash` (8) tiles from the
mob's home tile. Both melee (`Session.HandleAttack`) and spell damage (`CastDamage`/the `Damage` case in
`ApplyCast`) pass `_char.Id` as the attacker, so either can provoke a fight.

**Mob damage was massively under-tuned (fixed 2026-07-26 — user: "Going into Dragon 1 as a newbie with
250HP, I should not be surviving even a single hit... 50 damage or something. REALLY off"):** the original
swing formula was `Level/2 + 0-2` — a level-99 dragon hit for ~50, when it should one-shot a newbie. Root
cause: `Content.LoadMobs` parsed `mobs.csv`'s `MinDmg`/`MaxDmg` columns into... nothing — `MobDef`
never carried them, so the REAL per-mob damage data (e.g. `dragon_hatchling` = 2500-3500, `old_dragon` =
146250-183750) was silently discarded in favor of a synthetic Level-based stand-in with no basis in RTK.
This turned out to be the first of several "the CSV column was right there, just never parsed" gaps — see
the full combat pass below, done in the same sitting once the user asked to "check spells, my melee, mob
melee, backstab, flank, etc — let's get it right."

**The full combat pass (2026-07-26).** Everything here is ported from RTK's REAL, LIVE combat engine —
`rtklua/Accepted/Scripts/hitCritChance.lua` (crit/hit rolls) and `swingDamage.lua` (the actual damage
pipeline) — NOT the commented-out/dead damage math sitting in the C engine's `mob.c` (which looks like the
"real" formula at first glance but is entirely inert; the live path is 100% Lua). Shared, stateless math
lives in `Server/Combat.cs` so both attack directions use one verified implementation.

- **Mob→player** (`World.MobSwingDamage` + `Session.ApplyMobHit`): raw swing = 3 independent uniform draws
  over `[MinDam/3, MaxDam/3]`, summed and floored +1 (RTK `_getMobSwingDamage`; concentrates near the
  midpoint, not a flat roll edge-to-edge). Then `Combat.ApplyArmor` — `floor(raw * (1 + max(armor,-80)/100))`
  against the player's effective AC (`_char.Ac - Totals().armor`; AC is signed/lower-is-better, armor
  SUBTRACTS from it) — floored at -80, so armor can reduce a hit to as little as 20% but never fully negate
  it. THEN the positional 2x (below). Mobs also roll a crit chance (`Combat.RollCritChance`, hit/5 once past
  the base hitchance window) purely for the 0x13 visual byte (255 vs the calibratable normal byte) — real
  RTK never multiplies MOB damage by its own crit, only a player's (see below), so this doesn't scale `dmg`.

- **Player→mob** (`Session.PlayerSwingDamage`, replacing the old flat `EffMight`-based stand-in): another
  "CSV column parsed nowhere" bug — `Items.csv` has always carried `ItmMinimumSDamage/MaximumSDamage/
  MinimumLDamage/MaximumLDamage` (a weapon's REAL swing range) but `ItemDef` never read them, so player
  melee had no damage-range component at all. Now: `s = random(weapon's Small range)`, or its Large range
  if the target is a boss mob (`MobDef.IsBoss`, merged from CTK `MobIsBoss` — RTK's actual Large-damage
  condition) and the weapon carries one. `swing = (s/2 + dam*2.5 + might/8 + classFactor) * critical`
  (`dam`/`might` floored at 1; `classFactor` is RTK's flat per-class bonus, 9 Warrior / 7.5 Rogue / 0
  everyone else; `critical` = 3 on a genuine player crit, via `Combat.RollCritChance`'s player-attacker
  branch — 55+(grace+level)*0.75+hit-(targetGrace+targetLevel)*0.5 hitchance, flat 3% crit-within-window).
  Then `Combat.ApplyArmor` against the mob's `Ac` (RTK SQL `MobArmor`, merged in — floor -95 for a mob
  target), then the positional 2x. `rage`/`invisible` multipliers are hardcoded to 1 — no rage buff or
  stealth state exists in this server yet, matching RTK's own unbuffed default.

- **The positional "attacked from behind" 2x** (`Combat.IsBehindTarget`) applies to BOTH directions,
  UNCONDITIONALLY: RTK `swingDamage.lua`'s base `side==target.side` rule (attacker and target facing the
  SAME direction, attacker on the target's blind side) — every swing gets this check regardless of class.
  Dir 0=N/1=E/2=S/3=W already matched RTK's own numbering with no translation needed.

- **Backstab/Flank (fixed 2026-07-26, corrected after initial mis-scoping — user: "Flank and Backstab come
  from the warrior spells that provide those abilities. I think only warriors ever get them"):** these were
  first assumed to be rare legendary-weapon procs (several `Items/Weapons/*.lua` scripts DO read
  `player.backstab`/`player.flank` to gate a bonus proc) and descoped as "no data to drive it." Wrong —
  they're real Warrior-only skills (`Spells.csv`: `backstab_warrior`/`flank_warrior` + 3 aligned variants
  each, all `SplPthId=1`, level 15/20 per their Lua `requirements()`), timed self-cast stances (625s) whose
  `recast`/`uncast` hooks just flip `player.backstab`/`player.flank` — this IS the data the weapon scripts
  read, not a separate system. `Content.LoadSpellFx` now parses `cureCat` (previously dropped entirely,
  despite tagging all 4 aliases of each skill "backstabs"/"flanks" — a clean, data-driven way to catch every
  alignment variant without hardcoding 8 identifiers); `Session.ApplyCast` intercepts that `CureCat` before
  the generic archetype dispatch (the old path fell into `CastBuff`, which no-ops on an empty `BuffStat` —
  it was spending mana and saying "You cast Backstab" with zero effect) and routes to `Session.CastStance`,
  which just arms `_backstabUntil`/`_flankUntil`. `Session.PlayerSwingDamage` then applies
  `Combat.IsBackstabAngle`/`Combat.IsFlankAngle` (RTK's literal position tables — OPPOSITE facings for
  backstab, PERPENDICULAR for flank, each with their own per-angle position condition) as additional
  independent 2x multipliers on top of the base positional bonus, exactly mirroring `swingDamage.lua`'s own
  three sequential (and in principle stackable) if-blocks. Mob attackers never have either stance (RTK mobs
  don't cast Warrior skills), so both checks are player-attacker only.

- **`Session.RollDeflect`** (magic resist) now folds in `target.Protection` (RTK SQL `MobProtection`, a
  DIFFERENT stat from `MobArmor`/`Ac` above — RTK melee and magic defense don't share a stat) — previously
  hardcoded to 0 for lack of a source column, now `prot = target.Protection + (willDiff/10 + 0.5)`, matching
  RTK clif.c's real deflect roll exactly.

- **Data merged in from the CTK reference SQL dump** (`RTK-Server/database/references/CTK - closing -
  08-31-2019.sql`, same source as `MobBehavior` — see §11f's aggro fix above) into `mobs.csv`:
  `MobIsBoss`, `MobProtection`, `MobArmor`, `MobHit`. Plus `Grace`, which was already a plain column in the
  CSV but — like `MinDmg`/`MaxDmg` before it — simply never parsed into `MobDef` until this pass.

- **Rage tiers and Rogue stealth (fixed 2026-07-26 — user: "Did we also implement warrior/rogue damage
  multiplier spells? they get `rage` spells until 99 I think (rogue invis also has a damage multiplier)"):**
  `swingDamage.lua`'s `rage`/`invisible` terms (previously hardcoded to 1, i.e. no-ops) are the two BIGGEST
  multipliers in the whole formula and were completely unwired, same silent-no-op failure mode as
  Backstab/Flank (a `CastBuff` call with no numeric `BuffStat` to apply). Real spells, found in
  `RTK-Server/rtklua/Accepted/Spells/{warrior,rogue}/`:
  - **Rage progression** (`player.rage`, a flat multiplier on the ENTIRE swing): Wolf's Fury (rage 2 — Warrior
    lvl 7 / Rogue lvl 34) → Tiger's Fury (rage 3 — Warrior lvl 24 / Rogue lvl 56) → Dragon's Fury (rage 4 —
    Warrior lvl 45 only) → Baekho's Rage (rage 5 — Rogue lvl 99 only, the endgame tier the user recalled).
    `Content.RageAmountFor`/`Session.CastRage` (625s duration, 90-150 mana per tier). Real RTK REJECTS
    re-casting any fury while one is already active rather than letting a stronger tier overwrite a weaker
    one — ported faithfully (`EffRage > 1` guard), not "fixed" into a friendlier overwrite.
  - **Rogue stealth** (`player.state==2` → `invisible=9`, Rogue-only, lvl 30): Invisible + 3 same-mechanic
    aliases (Spirit's Form/Life's Cloak/Glass Form). A one-shot 9x sneak-attack burst — landing the boosted
    hit immediately strips the stealth (RTK `removeDuras(invis)` after a nonzero hit), ported in
    `Session.PlayerSwingDamage` (reads `Stealthed` once, applies the 9x, then clears `_stealthUntil` — so
    only the NEXT swing gets the bonus, not every swing for the whole duration). `Content.IsStealthSpell`/
    `Session.CastStealth`. **Damage multiplier only** — RTK's `PC_INVIS` state also hides the player's
    sprite from other clients (`clif.c`), which would need viewport/`ShowPlayer` changes this pass doesn't
    touch; casting Invisible here does NOT make you invisible to other players, only boosts your next hit.
  - Both families had `SplLevel=0` in the CSV export (their real level lives in each spell's Lua
    `requirements()` function, which the extractor never captures for Type-5 skills) — overridden with the
    real values above in `Content.SpellLevelOverrides`, scoped to just these 7 identifiers. Every OTHER
    Type-5 skill likely has the same `SplLevel=0` problem; this pass does not fix that generally.

- **Full spell-functionality audit (2026-07-26 — user: "Have we not implemented all spell functionality?
  ...rogues and mages have 'shapeshifting' spells. They don't seem to work. I want all spell functionality
  to work as expected"):** cross-referencing every spell archetype our CSV export tags against
  `Session.ApplyCast`'s dispatch found the same class of gap repeated across ~120 spells: anything tagged
  `Utility`/`Summon`/`Teleport` (no numeric `BuffStat`/damage `AmountExpr` the generic archetypes can
  express) falls into `CastMisc`, a pure stub that spends mana and prints "You cast X." with **no real
  effect** — the same silent-no-op failure mode as the earlier Backstab/Flank/Rage/Stealth fixes, just at
  much greater scale (29 Rogue + 24 Warrior "skill" spells, 46 Poet spells including a whole pet-summon
  family, and ~29 Rogue/Mage/Druid/Merchant/Chongun shapeshift ("morph") identifiers). Rather than fix all
  ~120 at once, tackled in priority chunks (user chose "cheap wins first, then work up"); chunks 1-3 (below)
  fixed 138 of ~145 real candidates. Still pending: `magis_bane`'s curse dependencies (1 + 4 unmapped). The
  gold-drop gambling stance (`chance_rogue` + 3 aliases) was explicitly descoped at the user's request (high
  complexity, low value).

  **Shapeshifting — RESOLVED as a peer-visible illusion (chunk 3, 2026-07-26), overturning the chunk-2
  "confirmed blocked" verdict.** RTK groups `feral_rogue`/`rodent_rogue`/`gangrel_rogue`/`beast_rogue` (+
  their Kwisin/Ming-Ken/Ohaeng alignment aliases, Mage's `beast_mage`/`gangrel_mage`, and standalone costume
  morphs like `dagger_uniform`/`marketer_guise`/`wilderness_guise`) in a Lua table literally named `morphs` —
  29 real castable identifiers total (not ~14 — the earlier estimate missed the mage/Druid/Merchant/Chongun/
  Barbarian side of the table). Casting one sets `player.disguise` to an animal/NPC look id + `player.state=4`
  for a fixed duration; purely cosmetic in RTK's own Lua, no stat/combat effect anywhere.
  - **CORRECTED 2026-07-26 — the "caster's own screen can't render it" verdict was wrong; it only proved
    `0x33` specifically can't.** `0x33`'s handler (`0x44fef0`) really does hit a hard wall: it calls the shared
    entity factory `0x44d7d0`, which for a PEER id correctly builds a real creature entity (`0x461a50`, vtable
    `0x4cd098`, `[ent+0x178]`/`[+0x17c]` set from the packet's own look value), but then EVERY renderKind branch
    (1/2/3: `0x4500ba`/`0x4500e8`/`0x4500a7`) makes one more, LATER, unconditional call —
    `push 0x4f2a84 (literal); call 0x463380` — a hardcoded player-archive constant, never read from the
    packet, for every entity `0x33` ever builds. That part still stands: `0x33` cannot draw a monster sprite,
    full stop. Where the earlier pass went wrong was assuming this was the ONLY path into the self entity.
    Deeper tracing of `0x44d7d0` found a THIRD branch, taken whenever the incoming entity id equals the
    client's own self id (`mov ecx,[ebx+0x40c]; cmp edi,[ecx+0x108]`, `ebx+0x40c` = pointer to the self-id
    holder): it skips BOTH the peer ctor and `0x33`'s hardcoded-archive draw call entirely, instead calling
    `0x44c660` (camera recenter to the packet's x/y — a no-op when it's already your own position) followed by
    `0x461e30`, which writes the look descriptor straight into the SELF entity's own struct fields
    (`self+0x178/0x17c/0x180`) — the identical fields a real monster entity carries. Critically, this self-id
    branch is reachable from `0x07` too (`0x44fdb0` passes discriminator 1 for any look in the monster range
    `0x8000..0xBFFF`, which lands in the very same `0x44d7d0` branch `0x33`'s kind 0/1 uses) — so sending the
    CASTER their own `0x07` creature-spawn, addressed to their own id, drives the self entity down this
    self-only path and updates its look descriptor for real, with no player-archive override anywhere in
    the way. Matches actual RTK behavior (the caster does see themselves transform).
  - **Fix:** `Session.ShowPlayer` — the single choke point every peer re-sync path already funnels through
    (join, map-change, equip/mount refresh) — reroutes a morphed player's entry to the real `0x07` Monster.epf
    creature-spawn builder (the SAME one `ShowMob` uses for actual mobs, `0x8000|look`), for BOTH peers' views
    AND the caster's own (`CastMorph`/`RevertMorph` now call `ShowPlayer(this)` directly on top of the
    `except:this` peer broadcast, rather than skipping self). The target id is still the caster's own
    persistent player id — never added to `World`'s mob list — so `HandleClickInfo` (which checks `MobById`
    before `PlayerById`) keeps resolving clicks to the real player profile/party/trade flow unchanged.
    `Content.MorphSpells`/`MorphDispatchSpells`, `Session.CastMorph`/`RevertMorph`, `PlayerSnapshot.MorphLook`,
    `Session.ShowPlayer`'s branch, `World.Tick`'s expiry sweep. Also added: a synthetic zero-stat entry in
    `_buffs` for the active morph, so the self-profile's buff/duration box (`BuffBoxText`, issue #6) actually
    shows the remaining time — it was silently missing before, since morphs never touched `_buffs` at all.
    Deliberately accepted tradeoffs: a `0x07` entity carries no name field (§7.2), so a morphed player shows
    no floating nametag to others. Mutual exclusion simplified to one slot (any morph replaces any other;
    re-casting the same one toggles it off) rather than RTK's per-identifier duration quirk (casting a second
    morph while one is active leaves both timers ticking in real RTK — a bug with no gameplay purpose, not
    reproduced).

  **Chunk 1 ("cheap wins") — fixed:**
  - **Self-sacrifice strike family** (Rogue Lethal Strike/Afterlife's Embrace/Ming-Ken's Judgement/
    Calculating Blow; Desperate Attack/The Void's Measure/Beastly Frenzy/Tilting the Balance; Warrior
    Berserk/No Fear/Tiger's Pounce/Wind's Blast; Whirlwind/Death's Angel/Nature's Own/Bladedance — 16 spells,
    4 base mechanics × 4 alignment aliases each). Ported verbatim from `rogue/lethal_strike.lua`,
    `rogue/desperate_attack.lua`, `warrior/berserk.lua`, `warrior/whirlwind.lua` (+ `rogue/backflow.lua`,
    `warrior/overflow.lua`): a facing-tile physical attack (not a targeted cast) computed from the CASTER's
    OWN pre-cast HP/MP, armor-netted the same way melee is (`Combat.ApplyArmor`) to find any overkill. The
    Rogue pair "backflows" overkill — up to half refunds to the caster as HP+MP, each capped at half their
    pre-cast values; the Warrior pair "overflows" instead — splashes overkill recursively onto up to 4
    adjacent mobs. Landing the hit ALWAYS costs the caster a big chunk of their own HP (halved/thirded/
    nearly wiped, family-dependent) regardless of overkill. Whirlwind's damage factor AND post-hit HP cost
    differ by the caster's own `_char.Alignment` (RTK reads the caster's real alignment stat directly, not
    which of the 4 aliases was cast — matches since a player would only ever be granted the alias matching
    their own alignment). Baekho's Rage specifically (rage tier 5, checked via `_rageAmount==5`, not any
    lesser Fury) adds a further 1.5x to Berserk/Whirlwind. `Content.SacrificeFamilyFor`/
    `Session.CastSacrificeStrike`/`ApplyBackflow`/`ApplyOverflow`. PC targets are skipped — no PvP damage
    path exists (same precedent as `CastDebuff`'s existing PC-immune mob-only crowd control).
  - **Poet mana-transfer families** (8 spells): Draw Energy/Harness Power/Combine Focus/Inspiration drain a
    **group member's** entire current mana into the caster (`Content.IsManaStealSpell`/`CastManaSteal`, party
    check via the existing `Party` object); Inspire/Share Energy/Bestow Power/Release Focus top off **any
    other player's** mana from the caster's own, capped by whichever pool is smaller, gated on the caster
    holding ≥30 mana to attempt it (`Content.IsManaGiftSpell`/`CastManaGift`). Neither reuses the pre-existing
    `CastManaBattery` (Invoke/Spirit's Power/Life Force/Gather Magic) — that's a different, self-only
    HP↔MP-conversion mechanic; these are direct two-player MP transfers with no HP cost at all.
  - **Poet cleanse family** (Dispell/Remove Magic/Return Natural/Restore Balance, 4 spells): a chance-based
    FULL buff/debuff wipe (RTK `flushDuration`) on a targeted player (self-castable too). Success chance is
    RTK's literal formula — target's effective armor (clamped [-60,70]) minus a will-scaled protection term,
    folded into `(120+armor)/2`, floored at 10%. Fixed 200 mana, no cooldown. `Content.IsCleanseSpell`/
    `Session.CastCleanse`/`FlushDurations` (clears `_buffs`, rage, stealth, AND the Backstab/Flank stances —
    RTK's wipe is total). Player `Protection` isn't modeled (only mobs carry it), so that term is 0 for a PC
    target — a known, minor simplification.
  - **Poet revive family** (Resurrect/Return Spirit/Ming-Ken Blessing/Death Undone, 4 spells): heals a dead/
    ghost player back to full in place, reusing the existing `ReviveAt` (same code path Silver Thread uses).
    Fixed 3000 mana, 8s cooldown. `Content.IsReviveSpell`/`Session.CastRevive`. RTK also blocks reviving a
    currently-hostile PvP target (`player:canPK`) — not modeled since this server has no PvP/hostility-flag
    system yet, so that guard is simply absent, not faked.
  - **Rogue short-leap family** (Race/Spiritual Jump/Leap of Faith/Transport, 4 independently-authored
    copies of the identical mechanic in RTK's own Lua — not alias-delegated like the others): jump up to 3
    tiles in the faced direction, stopping at the last passable tile (reuses `MapData.BlockedMove`, the same
    collision test normal movement uses). 1 mana, 80s cooldown. `Content.IsLeapSpell`/`Session.CastLeap`,
    re-anchors the viewport via the same `EnterMap` call `!warp` already uses for same-map jumps.
  - All of the above had `SplLevel=0` in the CSV export (same known gap as the rage/stealth pass) —
    overridden with their real Lua-sourced levels in `Content.SpellLevelOverrides` (63/45/40/63 for the four
    strike families, 35/45/88/99/99 for the Poet and leap families).

  **Chunk 2 — fixed 2026-07-26:**
  - **Enchant weapon-multiplier family** (16 ids, 7 tiers 1.2x/1.5x/2x/2.25x/3x/4x/6x by level 28-99): a
    toggle STANCE (`player.enchant`) — RTK's shared `enchants` mutual-exclusion group means casting any one
    while another (or itself) is already active just re-prints "This spell is already active.", never
    stacks/upgrades. Unlike rage (which multiplies the WHOLE swing), `swingDamage.lua` multiplies ONLY the
    raw weapon-swing term (s/2) by `enchant`. `Content.EnchantFor`/`Session.CastEnchant`/`EffEnchant`, folded
    into `PlayerSwingDamage` as `s/2.0*EffEnchant`. Mana/level hardcoded from each spell's own Lua (not
    trusted from the CSV, same Type-5-skill gap as rage/stealth) since one tier (`tigers_fortitude_rogue`)
    is genuinely mana-FREE (item components only).
  - **Directional ground-loot family** (Filch/Spirit's Hand/Quick Fingers/Light Touch, 4 ids, level 65): grabs
    coins/an item stack from the ONE tile directly ahead — despite the in-game description's "up to 4 tiles"
    claim, the Lua's own loop only ever runs `i=1`. Skipped entirely (silently, no message) if a player is
    standing on that tile; RTK's "someone's deathpile" protection isn't modeled since this server has no
    per-item ownership at all, so every other ground stack is fair game. `Content.IsGroundLootSpell`/
    `Session.CastGroundLoot`, reusing `World.PeerAt`/`PickUp`/`DropItem` (the same primitives `HandlePickup`
    already used for the native '0x07' walk-over pickup).
  - **Divination popup family** (Judge/Spiritual Advisor/Natural Talent/Appraise + Spy/Spiritual Guide/
    Nature's Handiwork/Judgement Day, 8 ids, level 17/28): a text popup of a target player's class/name/
    level/title/might/will/grace; the spy half also lists their full inventory. Sent via the pre-existing
    0x30 NPC-dialog sender (`SendScriptMessageP`, portrait-less) rather than the 0x34 click-profile packet —
    0x34 has no numeric level/might/will/grace fields at all (see the profile-packet entry above), so it
    can't express this. The judge family requires the target STRICTLY lower level (`target.level >=
    player.level` fails in the Lua); the spy family allows an EQUAL level too (`target.level > player.level`
    fails) — a genuine, deliberate difference in the source, not a typo. `Content.IsDivinationSpell`/
    `Session.CastDivination`, target resolved via the pre-existing `ResolvePcCastTarget` (same helper the
    mana-transfer/cleanse/revive families use).
  - **Trap-placement subsystem** (9 real castable ids — `set_trap`, the dispatcher with `SplQuestion` "What
    trap? >", plus the 8 `set_X_trap` spells Dart/Snare/RepeatingDart/Flash/Spear/Poison/Death/Sleep,
    level-gated 26/33/44/55/66/77/88/99): an entirely NEW hidden-hazard-entity subsystem, `World.Trap`
    (id/x/y/kind/ownerId) held per-map alongside `GroundItem`s but NEVER broadcast/drawn (invisible until
    triggered). `World.PlaceTrap`/`TrapsNear` are the public API; triggering is wired into `World.Tick`'s
    EXISTING mob-movement loop (both the chase-toward-target branch and the normal wander-step branch) — the
    instant a mob's new tile matches a live trap, the trap is removed and `TriggerTrapLocked` fires its
    effect. **PC-triggering was deliberately left out** (mobs only) — same "no PvP damage path" precedent as
    every debuff/sacrifice-strike spell earlier in this audit; flagged here rather than silently narrowed.
    Effects: Dart/RepeatingDart/Spear/Death (500/500/3500/11650 flat damage — RepeatingDart is, despite the
    name, byte-for-byte the same single-hit script as Dart in the real Lua) go through a new deferred
    `trapDamage` queue processed after `Tick`'s lock releases (`World.ApplyTrapDamage`, mirroring the
    pre-existing mob-swing `hits` queue — a kill still credits the trap's owner with exp via the existing
    `Session.AwardExp`). Snare/Sleep/Flash are simplified to a `Mob.FrozenUntil` hold (75s/38s/10s) — RTK's
    real snare is a +20 armor debuff and flash is a `blind.cast`, neither of which this server models as a
    distinct mechanic, a deliberate simplification rather than an oversight. Poison is a REAL
    damage-over-time (`Mob.PoisonUntil`/`PoisonNextTick`/`PoisonTickDam`/`PoisonOwnerId`, new fields on
    `Shared.Mob`), ticking every 1500ms inside `World.Tick` for 1% of max HP (capped [1,1000]) — and, matching
    RTK exactly, a tick is skipped rather than allowed to land the kill (poison alone can never finish a
    mob off). **`spot_traps` (the trap-reveal spell) turned out to be a DOG/companion-pet spell**
    (`Spells.csv` `SplPthId=99`, `rtklua/Accepted/Spells/dog/spot_traps.lua`) that a player character never
    learns directly — out of scope here, not silently dropped; revisit only if pets ever cast their own
    spells.
  - **Poet "Call of the Wild" pet-summon system** (28 of ~29 ids — 7 tiers `companion`/`assistant`/
    `protector`/`fighter`/`warrior`/`champion`/`avatar` x 4 alignment reskins each, level 68-99): spawns a
    real, correctly-statted shared-world `Mob` (via the pre-existing `Session.SummonWorldMob` — the same
    helper `!summon` uses, so it already copies the full combat block) one tile ahead of the caster, falling
    back to the caster's own tile if that's blocked/occupied by a mob or player — matching RTK
    `cotw_SpawnSetThreat`'s exact fallback. Tagged `Mob.OwnerId` + `Mob.PetExpiresAt` (300s after cast, then a
    plain `World.DespawnMob` — no kill/loot/exp, same as riding a mob away), and capped at 4 concurrently
    alive pets (6 at level 90+, 8 at level 99 — `Content.PetCapFor`, RTK's `cotw_spawnCheck`), counted PER MAP
    via the new `World.PetCountFor` (matching RTK's own `getObjectsInMap` scope). The level-99 "avatar" tier
    is the one real outlier: `cotw_wind_warrior.lua` has no `player.magic` check at all — RTK charges GOLD (via
    `requirements()`) plus an 8-minute cooldown instead, ported via the pre-existing `OnCooldown`/
    `SetCooldown` plumbing (0 mana). **NOT ported:** RTK's `cotw_controller_poet` threat-transfer — pets fight
    independently via the normal wander/aggro `Mob` AI rather than sharing the owner's combat target, since
    this server has no multi-entity threat-table concept to hook into. The 29th id,
    `cotw_giasomo_bird_poet` (mob 807), has NO matching row anywhere in `mobs.csv` — even RTK's own Lua
    flags it broken ("I know this doesn't belong here, but the COTW structure is so terrible already") — so
    it's skipped, not silently miscounted.

  **Chunk 3 — fixed 2026-07-26** (user: "we need to fix shape shifting and add the position-directed
  attacks"):
  - **Shapeshift/morph family** (29 real castable identifiers — see the "Shapeshifting" writeup earlier in
    this section for the full disassembly trace that overturned chunk 2's "confirmed blocked" verdict): a
    peer-visible-only illusion. `Content.MorphSpells` (21 fixed-look reskins) + `Content.MorphDispatchFor` (8
    question-dispatched: `feral_rogue`/`gangrel_rogue`/`gangrel_mage`/`rodent_rogue`/`beast_rogue`/
    `beast_mage`/`druids_rodent`/`wilderness_guise` — `wilderness_guise`'s real RTK menu chains into 5
    separate `recast`-only sub-spells with no `cast()` of their own and no independent SplId path reachable by
    a player; folded directly into `wilderness_guise`'s own answer table instead of modeling that
    indirection). `Session.CastMorph` sets `_morphLook`/`_morphUntil` and immediately broadcasts
    `ShowPlayer` to current peers; `Session.ShowPlayer` checks `PlayerSnapshot.MorphLook` and, when set, calls
    the SAME `SendCreatureList` builder `ShowMob` uses instead of the normal `SendLook` — so every future
    re-sync (join, map-change, equip refresh) keeps rendering the correct form with zero extra plumbing.
    `World.Tick` gained a `expiredMorphs` deferred-revert sweep (same shape as `expiredPets`/`trapDamage`) so
    the disguise reverts on its own once the duration lapses, not just on a toggle-off recast.
  - **`bladestorm_trap` family** (4 real ids, not 8 as first estimated — `set_bladestorm_trap`/
    `set_swords_dance_trap`/`set_tigers_ambush_trap`/`set_cutting_edge_trap` are 4 byte-for-byte-identical
    alignment reskins of ONE spell, confirmed by reading `bladestorm_trap.lua` directly; there's no separate
    Warrior variant). Despite the similar name, a completely different mechanic from the `set_X_trap` hazard
    family: a VISIBLE, step-triggered decoy that detonates a facing-cone AoE off the TRIGGER's own facing
    (RTK `block.side` → a 4-tile fan, `World.BladestormFan`), dealing ONE shared HP-percent damage number
    (not flat) to both the trigger and every mob the cone catches. The first spell in this whole audit where
    a PLAYER stepping on a trap also triggers it, not just a mob — wired into `Session.HandleWalk` right
    after a step commits its new tile (`World.CheckPlayerTrapTrigger`), alongside the existing mob-only path
    in `World.Tick` (`TriggerTrapLocked`'s new `"bladestorm"` case). RTK's cone-AoE only hits OTHER players if
    `block.pvp > 0` (a toggle this server doesn't model) — simplified to "cone hits mobs only", the
    established no-PvP-damage-path precedent from every debuff/sacrifice-strike spell earlier in this audit.
    The trigger's OWN self-damage is kept when the trigger is a player (tripping a trap isn't "PvP" the way
    hitting another player would be) via `Session.ApplyBladestormSelfDamage` — `floor(health*0.5) +
    calculateDamage(35000)`, where RTK's `calculateDamage` armor-deduction formula turned out to be
    IDENTICAL to this codebase's existing `Combat.ApplyArmor` (both `1 + max(armor,floor)/100`), so it's
    reused verbatim rather than reimplemented — capped to leave at least 1 HP (a trap tripped mid-walk has no
    death-flow of its own to hook, same "self-cost, never actually lethal" precedent as
    `CastSacrificeStrike`). Level 99, 1520 mana, 125s cooldown, the decoy auto-expires 21s after placement if
    never triggered (`Trap.ExpiresAt`, swept silently in `World.Tick` — traps have no ground graphic, so no
    broadcast is needed either way). **NOT ported:** the Lua's NPC heartbeat implies a 5000-mana/tick
    owner-upkeep drain while the decoy is alive — the exact drain/early-deletion formula wasn't in the
    captured source, so this is a documented gap (flat 1520 upfront cost only), not a guess.

- **Still NOT ported, explicitly out of scope:** RTK's equipment "Miss" stat (a rare cursed-item flat-miss
  chance — no Miss column in our item data, so every swing here always lands for at least 1 damage, matching
  RTK's non-cursed common case).

- **Debug/GM summon commands carried none of this** (a separate, pre-existing gap found while testing the
  fix): `!rabbit`, `!summon <mob>`, and the ridden-horse re-spawn all resolve a real `MobDef` but only ever
  forwarded Look/Name/Hp/Color/Exp/MoveTime into the spawned `Mob` — Level/Will/MinDam/MaxDam/Ac/Grace/Hit/
  IsBoss/Aggressive were silently dropped, so summoning a named mob for testing would never show its real
  numbers. `Session.SummonWorldMob` now takes an optional `MobDef? def` and copies the full combat block
  when given one. (The unrelated sprite-only debug lab — `!cre`/`!crecol`/`!crow`/look-lab, which spawn by
  raw look id with no mob name at all — has no `MobDef` to pull from and is untouched, working as designed.)

**Taking a hit** (`Session.ApplyMobHit`): docks HP, pushes `SendStats()`, and broadcasts the same over-head
hit/HP-bar packet (`DamageOver`) a mob shows when hit, aimed at the player's own entity id. Hp reaching 0
calls `Die()`: the player is redrawn as a **ghost** (appearance form `1` — `MountForm()` returns
`_char.Hp==0 ? 1 : (Mounted?3:0)`; `PlayerSnapshot`/`ShowPlayer` carry a `Dead` flag so peers see it too),
attacking/casting is blocked ("Spirits cannot attack/cast spells"), and **stays that way** — RTK has no
auto-revive timer at all. **Changed 2026-07-25** (was a fixed 3s timer + auto-warp to the home city, a
deliberate simplification made before the F1 menu was understood): a ghost now revives only by pressing
**F1** and choosing **"Silver Thread"** — RTK's real answer, ported in §11k — which offers a Shaman by
nation and revives (`ReviveAt`: full heal + warp) on arrival.

**Home city** (`Session.HomeCityFor`/`PlaceNewCharacter`): a fresh character spawns, and a defeated one
revives, **just inside their nation's home**, near the real RTK door-arrival tile (`data/game-data/
Warps.csv`) rather than GmWarp's outdoor GM-teleport spot:
- Nation `2` (Buya): map **351** (Jadespear's Home) at `(3,6)`.
- every other nation: map **36** (Ironheart's Home) at `(5,10)` — the door tile from Kugnae's
  `0:(87/88,146)`.

Jadespear's tile took two corrections before landing on `(3,6)`:
- `(7,12)` — Warps.csv's raw door-arrival Y (from Buya's `330:(55/56,121)`), but map 351 is only 12 rows
  tall (valid `0..11`) — `y=12` is one past the edge. The 4.95 client's self-placement check (`0x424310`)
  silently bails on an out-of-bounds tile: the game-world object gets constructed (`0x02` handshake all
  succeeds) but the self entity is never placed, so the screen stays **black and movement keys do
  nothing** (the GUI still works — it doesn't depend on the world entity). Live symptom trace: `>>33
  0x33 handler ENTER ... place/validate 0x424310 -> al=0 <<< BAIL (placement failed) >>>`.
- `(7,11)` — in bounds, but that row is `TK351.map`'s bottom wall/threshold strip in the OBJECT layer
  (ids 636-643). At the time, passability (`Solid`) was pass-flag only, so this tile was never *blocked* —
  just visually "standing in a wall." (Since the 2026-07-26 §12 object-wall fix, movement now also honors
  `SObj.tbl` object flags, so those wall ids would additionally impede stepping off — reinforcing that an
  open interior tile, not a wall-strip tile, was the correct choice.) `TK351.map`/`TK36.map` were both checked against
  the real client install: entirely open PASSABLE floor (no solid tiles at all), but Jadespear's object
  layer draws a walled room, so a merely in-bounds tile isn't enough — it also needs to dodge every
  nonzero object id. `(3,6)` sits in the empty interior clear of all of them.

`PlaceNewCharacter` MUST run after `ApplyAppearance` has decoded the real creation-time nation (§9) — it
used to run first, silently always landing new characters at Ironheart regardless of what they picked.

**Real RTK note:** in the Lua gameplay layer, totem is normally *re-worshipped* later at one of four
totem-animal shrine NPCs in the Wilderness (`totem_npc.lua`: `JuJakNpc`/`BaekhoNpc`/`HyunMooNpc`/
`ChungRyongNpc`), and those same NPCs are also the resurrection point for a dead player (`_resurrect`,
gated on `player.state==1`). This server doesn't have that shrine/worship system yet. As of §11k, revival
itself IS the real RTK flow (F1 → Silver Thread → pick a Shaman) — the one remaining simplification is
that `ReviveAt` heals on arrival at the Shaman's map instead of requiring a second click on a standalone
Shaman NPC actor once there (we don't have those placed as real map NPCs).

**The live Buya↔Jadespear's-Home door** (`Content.Warps`/`TryWarp`, `data/game-data/Warps.csv` ids 56-59) is
a *separate* code path from the hardcoded home-city spawn above — walking onto Buya's door tile
(`330:(55/56,121)`) drives the normal warp system (`Session.HandleWalk` → `EnterMap`).

**Fixed (2026-07-26).** The raw Warps.csv data had two bugs, reported by the user as "warps in jadespears
home don't work: can't go further in, and going back outside works but nothing warps you back":
- The entry destination (`351:(7/8,12)`) was one row past map 351's bottom edge (12-row map, valid
  `0..11`) — `EnterMap`'s bounds clamp silently pulled it up to `y=11`, which is `TK351.map`'s bottom
  wall/threshold strip in the OBJECT layer (every column solid-looking except columns 5-6, the actual door
  gap — confirmed by dumping the row). Landing there technically "worked" (object-layer isn't a collision
  source, see §12) but put the player standing ON the door's own trigger tile.
- The **return trip's source tile** (`351:(7/8,13)`) was flat-out off-map (row 13 doesn't exist on a
  12-row map), so stepping "back outside" could never fire — that's the warp the user found dead.

Both are now explicit, in-bounds coordinates instead of relying on the clamp: entry lands one row *above*
the door at **`351:(5/6,10)`**, and the door-gap tiles **`351:(5/6,11)`** are the return-trip source,
matching the real gap in the object-layer wall. The return destination was also corrected to land directly
below the Buya door instead of shifted 2 tiles west (`330:(53/54,122)` → **`330:(55/56,122)`**, user:
"exiting jadespears home should be 55 122 and 56 122") — same columns as the entry trigger `(55/56,121)`,
one row south. Round trip: Buya `330:(55/56,121)` → Jadespear's `(5/6,10)` → walk south onto `(5/6,11)` →
back to Buya `330:(55/56,122)`.

---

## 11g. Durability, warp gating, whisper, and spell resist (added 2026-07-25)

Four RTK subsystems ported in one pass, each cross-checked against `RTK-Server/rtk/src/map/clif.c`.

**Item durability & breakage** (RTK `clif_deductweapon`/`clif_deductarmor`/`clif_checkdura`, clif.c:6646-6844).
`InvItem.Repair` (0-5) tracks which threshold warnings have already fired, mirroring RTK's
`sd->status.equip[x].repair`. On a landed melee hit, the weapon (EQ slot 1) has a ~49% chance
(`rnd(100) > 50`) to lose 1 durability (`Session.HandleAttack` → `DeductDura`); on TAKING a hit
(`Session.ApplyMobHit`), every worn slot rolls independently (RTK's `clif_deductarmor` checks the weapon
slot too — preserved, not "fixed"). Warnings fire once each at 50/25/10/5/1% ("Your X is at 50%.", …);
at 0 the item is destroyed ("Your X was destroyed!"), unequipped, and stats recalculated. Indestructible
items (`ItmIndestructible`) and durability-less items never decay; disabled entirely on `MapPvP` maps
(`Content.IsPvpMap`). RTK's BoD "protected" restore-instead-of-break branch isn't modelled — no item in
the live registry currently sets `ItmProtected`, so it would never fire anyway.

**Warp level/mark/path gating** (RTK clif.c:5187-5203). `Content.MapMetaInfo` now also carries
`ReqLvl/ReqPath/ReqMark/ReqVita/ReqMana/LvlMax/VitaMax/ManaMax/RejectMsg`, loaded from the (previously
ignored) `Maps.csv` columns — 1107/9850 maps set a real req, and vita/mana caps are stored as unsigned
32-bit with `4294967295` as the "no cap" sentinel (parsed as `long`, not `int`, or the sentinel overflows
to 0 and silently locks every map). `Session.TryWarpGate`, called only from `HandleWalk`'s step-onto-warp
path (NOT from the internal `Warp()` used by quests/Gateway/GM teleports — RTK's check lives in the walk
handler, not in `pc_warp` itself), reproduces RTK's denial-message cascade verbatim, including its dead
branches: because almost every gated map sets `ReqLvl`, the level-difference messages already cover every
diff value, so the mark/path-specific text is only reachable when `ReqLvl` equals the player's level
exactly and a mark/path check also fails. `CharMark` is still hardcoded to 0 (subpath marks aren't
modelled — see `MinorQuest.cs`), so any `ReqMark > 0` map stays locked for everyone until that lands.

**Reject = snap-back + status text, fixed 2026-07-27.** When the gate denies entry, RTK does two things
in order (clif.c:5188-5204): **(1)** `clif_pushback(sd)` (clif.c:15617) — a `pc_warp` that shoves the
player 2 tiles opposite their facing, re-asserting position so the client isn't left standing on the warp
tile; **(2)** `clif_sendminitext(sd, msg)` — the denial to the status box. Our reject path was missing
BOTH: it did `SendLog(denyMsg); return;`, which (a) spoke the line as a `0x0D` chat bubble and (b) sent no
`0x04`. In 4.95 self-walk is client-local — the client had already stepped onto the warp tile and was
**blocked awaiting a `0x04` ack** to release its next step; the bare `return` never cleared that gate, so
the player **froze and could not move or turn** (live-reported on the "Nightmarish visions…" entrance). Fix
mirrors RTK with our proven 4.95 primitives, identical to what `HandleWalk`'s normal `blocked` branch
already does: hold `_char.X/Y` at the from-tile, `SendXy()` (the `0x04` that both snaps the client off the
warp tile AND unblocks the next step), then `SendMiniText(denyMsg)` (status box, RTK `clif_sendminitext`).
The sibling scripted-tile rejection `TryMythicCaveEntrance` already snapped back with `SendXy()` but was
using `SendMessage` (the `0x02` login box); switched to `SendMiniText` so both rejection paths land in the
same status pane. User-confirmed working 2026-07-27.

**Full `SendMessage`→`SendMiniText` audit, 2026-07-26.** The class-gate path-hall doorway (§ above,
`TryPathHallWarp`) turned out to be one instance of a systemic mislabeling: `SendMessage` is specifically
RTK's `0x02` **login-box** packet (`TkCrypt.LoginKey`-encrypted), reserved for the pre-world / re-login
flow — not a general in-world chat channel. Cross-referenced against RTK's actual sources (`clif.c:6484`
`clif_sendmsg(sd, type, buf)` — one function, one `0x0A` packet, dispatched by `type`: `0`=wisp, `3`=mini/
status **(RTK's `clif_sendminitext` is just `clif_sendmsg(sd, 3, ...)`)**, `5`=system, `11`=group, `12`=clan
— plus a wide sample of Lua spell/quest/NPC scripts: mana checks, cooldowns, "no target", cast confirmations
("You cast X."), and target-side cast notices ("X casts Y on you.") are *all* `player:sendMinitext(...)` in
every RTK script checked (`ExpSeller.lua`, `harden_armor.lua` family, `spark.lua`/`global_zap.lua`/
`global_heal.lua`, `dart_trap.lua`, `onScriptedTilesBushTree.lua`, `onScriptedTilesPathHalls.lua`, RTK's own
`player.lua:3717` "Spirits can't do that."). Our port had defaulted most of these to `SendMessage` instead,
so ~90 call sites across `Session.cs`'s spell-casting, quest, mount, trap, and status-toggle code were
silently landing on the wrong (or no-op-looking) channel. Converted all of them to `SendMiniText`, **except**:
the four `clif_changestatus` status-line toggles (Realm-centered/Fast-Move/Sociable/Exchange — a different
RTK function, verbatim chat text), the re-login "Incorrect password." and profile-save confirmation (both
documented, intentional `0x02` uses — see §9.5/§11k above), and our own invented combat-kill flavor lines
("Your Fireball destroys Rat! (+50 exp)") which have no RTK equivalent at all (real RTK conveys a kill purely
via the HP-bar/hit-animation, no text) and were kept as a deliberate enhancement. Durability warnings
(`clif_checkdura`/`clif_deductduraequip`) are a special case: RTK tags them `type 5` ("System") rather than
the default `type 3` — same `0x0A` minitext packet, different client-rendered style — so those became
`SendMiniText(text, type: 5)` rather than the default-typed calls used everywhere else.

**Whisper / tell** (RTK `clif_parsewisp`, clif.c:7644-7790). Native input: **Shift+'** opens the whisper
prompt, then a target name + Enter, then the message + Enter. LIVE-CONFIRMED 2026-07-26 by real capture —
op `0x19`, body `dstlen(u8) dst_name[dstlen] msglen(u8) msg[msglen] 00`, e.g. `07 'destine' 01 'd' 00` —
matching RTK's wire layout exactly (`clif.c:7644`: `dstlen = RFIFOB(fd,5); msglen = RFIFOB(fd,6+dstlen)`).
Dispatched to `Session.HandleWhisperPacket`. Chat commands `!whisper <name> <message>` / `!w <name>
<message>` (over `0x0E`) remain as a fallback entry point into the same `DoWhisper` core. `Content.CanTalk`
(RTK `cantalk`, 2/9850 maps) and the not-found message (`"<name> is nowhere to be found."`) are RTK's exact
wording. `World.FindPlayer(name)` is the case-insensitive online-lookup this needed.

**Delivery, fixed + LIVE-CONFIRMED 2026-07-27 — was a same-head self-bubble, now rides the `0x0A` mini-text
channel.** Originally delivery rode `SendLog` (`0x0D`, chatType 0, attributed to the *recipient's own*
entity), because RTK's real delivery packet (`clif_sendmsg` type 0, "Wisp/blue text" — clif.c:6484) is a
dedicated non-entity system-message opcode with no proven 4.95 equivalent at the time. Live user testing
found this looked wrong: it showed as a bubble over the recipient's own head reading `"SenderName" ()
message"`, not a chat-log line attributed to the sender. Root cause: `0x0D` always draws a 3-second head
bubble (handler `0x450170`) — there's no way to suppress it, and attributing the packet to the *sender's*
entity id instead would silently vanish whenever sender and recipient aren't on the same map (the common
case, since whisper has no range limit). Fixed by switching delivery to **`Session.SendMiniText`** (`0x0A`,
the client's mini-text/status channel below the inventory, RTK `clif_sendmsg`/`clif_sendminitext` — already
proven live via the look-at-name and item-pickup-name fixes earlier this session) with **`type = 0`**,
matching RTK's own `clif_sendwisp`/`clif_retrwisp` type value for "Wisp" text. Both directions now send the
literal line `"SenderName: message"` — the sender's own echo (`DoWhisper`) and the recipient's copy
(`ReceiveWhisper`) are the same string, no RTK-style class-name suffix (dropped; it added no information
the player didn't already have and doesn't match the plain "Name: message" shape used elsewhere).
**Live-confirmed:** with `type = 0`, the line lands in the main chat window in blue with no head bubble,
exactly matching RTK's "Wisp" intent — so `0x0A`'s `type` field really does route to a different pane/color
per value (`type = 3`, used elsewhere for look-at/item names, renders in the mini-status box instead). Not
modelled: per-player whisper-on/off, silence/mute, ignore lists — none of those systems exist yet.

**Spell resist / deflect** (RTK `clif_parsemagic`'s deflect roll, clif.c:8910-8934). Only spells flagged
`SplCanFail` (317/905) roll it. `Session.RollDeflect`: `willDiff = max(0, target.Will - caster.Will)`,
`prot = round(willDiff / 10)`, `failChance = 100 - 0.9^prot * 100` — an exponential curve, not a flat
percent. `Mob.Will` is now populated from `MobDef.Will` (the RTK `Will` column, real but previously
unwired — mobs had no Will at all before this). RTK's mob struct also carries a separate per-mob
`protection` stat; our mob registry has no source column for it, so it's treated as 0 for every mob — a
real but incomplete port, not an invented number, and it means our mobs resist somewhat less than genuine
RTK ones with nonzero protection would. Wired into `CastDamage` and `CastDebuff` (the only two PvE-target
archetypes we have — there is still no PvP cast path at all) right after target resolution and BEFORE the
mana debit, matching RTK: a deflected cast spends no mana (RTK returns before ever calling into the Lua
"cast" script that would debit it). Message is RTK's exact line, caster-facing only: "The magic has been
deflected." — RTK sends nothing to the target on a resist.

---

## 11h. Bulletin boards + native nmail (reply shapes LIVE-VERIFIED 2026-07-28)

**Request: LIVE-confirmed.** Pressing **b** sends op `0x3B` — matches RTK's dispatch exactly
(`clif.c:11613`, `case 0x3B: clif_handle_boards(sd);`). Body `dec[0]` is a sub-command, then u16-BE
board/post ids: `1`=show board list, `2 board`=show a board's posts, `3 board post`=read one post,
`4 board topicLen topic bodyLen body`=make a post, `5 board post`=delete a post,
`6 toLen to topicLen topic msgLen(u16BE) msg sendCopy`=**send nmail from the native compose window**
(RTK `nmail_write`, level-10 gated), `9`=open the mailbox (== sub-2 with board 0). `Session.HandleBoard`
decodes 1–6 + 9; 7/8 (GM postcolor, scripted special-write) aren't modelled.

**Board 0 == the player's nmail mailbox** (RTK `boards_showposts`: "Board(0) == NMail"). The client's
board window is MODE-SWITCHED by the reply's first byte (`flags2`, below): mode 4 turns it into the
mailbox whose **Write button opens the recipient-field compose window** (emits sub-6); mode 2 is a
normal board (Write emits sub-4, no recipient). All live-verified on the real 4.95 client.

**Write/delete ACK (`SendBoardAck` — the reply the compose/board window BLOCKS on).** After any sub-4/
5/6 action the client waits for a dedicated `0x31` ack (RTK `nmail_sendmessage`, map.c:164):
`other(u8: 6=write ack, 7=delete ack) type(u8: 1=success — releases/closes the window, 0=failure —
keeps it open) msgLen(u8) message[...] trailer(u8=7)`. Replying with only a 0x0D text line hangs the
window ("Your post didn't go through due to an error"). RTK's canonical texts: "Your message has been
posted." / "Your message has been sent." / "User does not exist." / "The message has been deleted." /
"You can only delete your own messages.".

**Reply: NOT live-confirmed.** RTK's real board storage lives in a *separate char-server process*
talking its own SQL database (`Boards` table: `BrdBnmId/BrdPosition/BrdChaName/BrdTopic/BrdMonth/BrdDay`,
confirmed by reading `rtk/src/char/mapif.c`'s `mapif_parse_showposts`/`boardpost`/`deletepost`), which then
replies to the map-server (`rtk/src/map/intif.c`), which THEN builds the actual client packet. Three hops
of reference material were needed to reconstruct the final client-facing `0x31` reply shapes (list /
show-posts / read-post) below — but none of them have been captured live off the 4.95 client the way the
board-open *request* was, so treat these as "best-effort from source, awaiting confirmation," the same
status this doc gives other built-but-unverified systems (§11c, §11d). If the board window doesn't render
right in-game, this is the first place to check — paste a capture and the offsets get corrected.

Evidence byte 4 (this server's `inc`) isn't client-meaningful for op `0x31`: RTK's own
`intif_parse_readpost` never writes it at all (`//WFIFOB(sd->fd,4)=0x03;` is commented out), so these
replies use the normal `SendMap(0x31, _gameInc++, data)` convention like every other opcode in this
codebase, rather than copying RTK's literal (and inconsistent) byte-4 values.

- **Show board list** (`SendBoardList`): `1 titlelen title[...] boardCount` then per board
  `id(u16BE) nameLen name[...]`. This server appends **Mailbox** (board 0) as the LAST entry — the only
  reachable way into the mailbox, since the 'm' hotkey is dead (below).
- **Show a board's posts** (`SendBoardPosts`): `flags2(u8) flags1(u8) board(u16BE) boardNameLen boardName[...]
  postCount` then per post, newest first, `color(=0) postId(u16BE) authorLen author[...] month day topicLen
  topic[...]`. **`flags2` is the window-mode byte: 2 = normal board, 4 = NMAIL MAILBOX** (RTK
  char/mapif.c `mapif_parse_showposts`: `if (a.board == 0) flags2 = 4; else 2`) — mode 4 is what makes
  Write compose WITH a recipient field (sub-6). `flags1` = rights: 1=read-only, 3=write+del, special
  6="write sends a packet" for scripted boards (not modelled).
- **Read one post** (`SendBoardReadPost`): `type(u8: 3=board post, 5=nmail letter) buttons(u8=3)
  nmailFlag(u8: 1 when board 0) postId(u16BE) authorLen author[...] month day topicLen topic[...]
  bodyLen(u16BE) body[...]` — per RTK `intif_parse_readpost`/`mapif_parse_readpost`.

**The 'm' hotkey — DEAD IN THIS BUILD, proven at the dispatch table (client RE, 2026-07-28).** The
in-world letter hotkeys are dispatched by a char switch @`0x48e625` (index = `char-0x0D`, byte-index
table @`0x48eab0`, 50-case jump table @`0x48e9e8`). Full map decoded: 'b'=case 22 (board window ctor
`0x406e80(1)` → sub-1 request via `0x407100`), 'i'=28, 's'=32, 'c'=23, 'o'=29, etc. **'m'/'M' sit in
case 49 = the default do-nothing bucket** (alongside x/z/q/n) — so the "m = mailbox" help line is a
stale NexusTK.dat string and the binding never shipped. This is the *same table* that dispatches every
working hotkey, so it settles the question that the earlier VK-only search could not.

The **mail-arrow click does exactly what 'b' does — LIVE-CONFIRMED 2026-07-28**: clicking the lit arrow
puts `0x3B` sub-1 (board list) on the wire, nothing else. Its handler `0x469654` checks hasMail
(`[widget+0x106]`) and calls the same board-window ctor `0x406e80(1)`. So the arrow needs no separate
server support, and the client's own mail affordance lands in the board list — which is some evidence
that a "Mailbox" entry there is the original design, not a workaround. The arrow's PARCEL branch
(`0x469760`) instead sends **op `0x41`, empty body** = RTK `clif_parseparcel` — a "go see the
messenger" minitext there, and the same here.

> **Negative result (tested live 2026-07-28): an unsolicited mailbox `0x31` opens NO window.** Sending
> the mode-4 posts view when the player hasn't opened the board window does nothing at all — the client
> only renders a `0x31` into an ALREADY-OPEN board window. So there is no server-side way to jump
> straight into the mailbox, and no packet the dead 'm' key could be pointed at without a client patch.
> The mailbox is therefore reached via **b → Mailbox** (listed last in the board list).
> `re/patches/patch_495_mail_key.py` implements the optional one-byte fix (byteTable['m'] 49→22, making
> 'm' a second 'b') — written, verified against the build, **deliberately NOT applied**: patching the
> client wasn't wanted, and it could only duplicate 'b' anyway, not open the mailbox directly.

**Board list content.** RTK's actual board list (`db/board_db.txt`) is server-instance config not present
in the reference tree — there's no real seed data to port, unlike every other feature this session (items,
mobs, maps, spells all had real CSVs). `Boards.All` (`Server/Boards.cs`) instead reuses REAL RTK board
identifiers pulled straight from RTK's own board Lua scripts (`rtklua/Developers/Boards/*.lua`): **Lore**,
**Map**, **Poetry**, **Minigames & Carnages** — the ones that don't depend on unmodelled concepts (GM
level, tutor rank, clans/subpaths block the rest: `bugs_board`/`devs_board` are GM-only, `pathBoards.lua`'s
per-class boards gate posting on "tutor" status, `subpath_public_boards.lua` needs a subpath system). Every
board here is open to read + post by any player. This is a judgment call, not RTK ground truth — if a
different board list is wanted, it's a one-line edit to `Boards.All`.

**Storage.** RTK's posts live in a separate char-server's SQL database; this server is single-process, so
posts collapse into one server-wide JSON file (`data/boards.json`, `Server/Boards.cs`) instead — same
"RTK's shape, our storage" choice already made for characters (`Shared/CharacterStore.cs`). Delete is
author-only (RTK's broader GM/tutor `CAN_DEL` grant isn't modelled).

---

## 11i. F2 / Subpath chat (added 2026-07-25 — awaiting live confirmation)

**F2 is not a menu — it's a chat-channel toggle.** Pressing it was producing a garbage click-profile
(`0x34`) reply with a nonsense target id, because the client routes F2 through the *same* click-info
packet as a real entity click (`0x43 01 entityId(u32BE) 00`), just with a magic sentinel id instead of a
real one. RTK's `clif_handle_clickgetinfo` (`clif.c:11010`) checks for this **before** the normal
`map_id2bl` entity lookup:

```c
if (SWAP32(RFIFOL(sd->fd, 6)) == 0xFFFFFFFE) {   // subpath chat
    sd->status.subpath_chat = !sd->status.subpath_chat;
    clif_sendminitext(sd, sd->status.subpath_chat ? "Subpath Chat: ON" : "Subpath Chat: OFF");
    return 0;
}
```

Confirmed as the real F2 binding (not a guess) by `rtklua/Accepted/Scripts/welcomeNmail.lua`: *"F2 - Turn
'Subpath Chat' On/Off!"*. `Session.HandleClickInfo` now special-cases `id == 0xFFFFFFFE`
(`SubpathChatSentinel`) and calls `ToggleSubpathChat()` — flips `Character.SubpathChat` and confirms via
the same `0x0A` mini-text channel whisper uses (§11g), **before** falling through to the NPC/click-profile
paths. **Not yet live-tested against the 4.95 client** — the packet shape (id `0xFFFFFFFE` on `0x43`) is
taken on faith from the RTK 7.x source; if F2 still misbehaves, capture the raw `0x43` body to confirm
this 4.95 client sends the same sentinel.

**Delivery — `/subpathchat <msg>` (alias `/sp`).** RTK's `clif_sendsubpathmessage` (`clif.c:7402`) is a
**server-wide, not map-scoped** channel: every *other* online player whose `class` matches the sender's
AND who also has subpath chat on receives `<@Name> (ClassName) message`. `Session.DoSubpathChat` ports
this: gated on the sender's own `SubpathChat` flag and the map's `cantalk` flag (same `Content.CanTalk`
gate whisper uses), it iterates `World.AllPlayers()` (new — a server-wide roster, unlike the map-scoped
`World.Broadcast`) and compares `ClassName` (our single string stands in for RTK's finer-grained numeric
`class`/mark). Rendered via `SendMiniText` at the default `type=3` (proven-live mini/status pane) rather
than RTK's literal `type=11` (its "group" channel, reused for subpath) — **unconfirmed** whether 4.95
renders `type=11` distinctly from `type=3`; if it turns out to matter, this is a one-line change.

---

## 11j. Experience & leveling (added 2026-07-25 — awaiting live confirmation)

**There was no leveling system at all before this.** Exp was already being added to `Character.Exp` on
every mob kill (melee, spell) and shown on the HUD bar, but nothing ever read it back — `Character.Level`
never changed and `Character.Tnl` (experience-to-next-level, sent in the self-profile `0x39`, §9.5) sat at
0 forever. Ported wholesale from RTK's real level-up path: `pc.c`'s `pc_givexp`/`pc_checklevel`
(exp-gain + level-up-loop driver), `class_db.c`'s `classdb_level` (per-path cumulative exp table, backed
by `rtk/db/level_db.txt`), and `rtklua/Accepted/Scripts/onLevel.lua` (the actual stat/HP/MP gain formulas
run on every level).

**Exp table.** `rtk/db/level_db.txt` is `path,cumExpLvl1,cumExpLvl2,…,cumExpLvl98` — one row per path,
`classdb_level(path, level)` = total exp needed to leave `level` (i.e. reach `level+1`). Paths 0-4 map
1:1 onto this server's existing `Content.PathIdForClass`/`Paths.csv` scheme (`PthType` column: 0 Peasant /
1 Warrior / 2 Rogue / 3 Mage / 4 Poet — RTK's own `Paths` table, already in use for spell-book gating).
Extracted (awk one-liner over the RTK file) into a long-format `data/game-data/LevelExp.csv`
(`Path,Level,CumExp`), loaded by `Content.LoadLevelExp` into `Content.ExpToNext(pathId, level)`.

**`Session.AwardExp(amount)`** is now the single funnel every exp source goes through (quest rewards,
melee kills, spell kills — the three raw `_char.Exp += reward` sites were replaced with calls to this):
add the exp, then loop `while (Level < 99) { need = ExpToNext(path, Level); if not enough, stop; LevelUp(); }`
so one big reward (e.g. a quest turn-in) can carry a low-level character through several levels in one
call — matches RTK's `pc_checklevel` loop shape exactly. Recomputes `Character.Tnl` afterward so the
self-profile window shows the right remaining amount.

**Peasant level-5 wall.** `onLevel.lua` blocks Peasants (path 0) from leveling past 5 until they choose a
real path: `if player.class == 0 and player.level >= 5 then sendMinitext(...); return end`. Ported
verbatim — exp keeps banking up past the level-5 threshold but `AwardExp` stops calling `LevelUp` and
sends the same message, matching the existing path-hall warp gate (§"Class path-hall interior warps",
`Session.cs` ~line 2365) that already required leaving Peasant to progress further.

**`Session.LevelUp(path)`** ports `onLevel.lua`'s stat-gain formula exactly: a `secondary`/`tertiary`
bonus-point flag pair rolled off `(level+1) % 2` / `% 3` (both trip together every 6th level) for the four
real paths, or a different `% 2` / `% 3` / `% 5` combo for Peasants (who have no primary stat yet — their
roll instead decides whether the level's point goes to Might or to Grace+Will). Per-path Might/Grace/Will
assignment and HP/MP gain ranges (`Random.Shared.Next`, inclusive both ends) are RTK's literal numbers:

| Path | Primary stat | HP gain | MP gain |
|---|---|---|---|
| 0 Peasant | Might (conditional) | 45-55 | 32-36 |
| 1 Warrior | Might (always +1) | 72-81 | 8-9 |
| 2 Rogue | Grace (always +1) | 56-63 | 24-27 |
| 3 Mage | Will (always +1) | 40-45 | 40-45 |
| 4 Poet | Will (always +1) | 48-54 | 32-36 |

Also matches RTK's `Ac -= 1` per level (AC is signed/lower-is-better in this server, §9.5 — RTK's
`baseArmor` decrement is the same direction) and the level-99 special case (`Ac` snaps to `1`), and does
a full heal (`Hp`/`Mp` set to the post-gain max, gear/buffs included) exactly like `onLevel.lua`'s
`health = maxHealth; magic = maxMagic`. Confirmed via `SendMiniText` ("You have gained new insight.") —
RTK's `onLevel.lua` also plays a sound (`playSound(123)`) and a `sendAnimation(2, 0)` visual flash that
aren't ported (no calibrated opcode for a generic non-spell animation/sound pair yet; low priority next
step if the level-up feels too quiet in-game).

**Not yet live-tested.** The exp math, the level-up stat/HP/MP gains, and the Peasant wall are all
ported from RTK source with no live 4.95 combat session behind them yet — kill a mob and watch the level
counter / minitext to confirm before trusting the numbers for real balance decisions.

---

## 11k. F1 / Central Functions menu & Silver Thread revival (added 2026-07-25 — awaiting live confirmation)

**F1 is the sentinel right next to F2's.** RTK's `map.h` defines `#define F1_NPC 4294967295`
(`0xFFFFFFFF`) — one less than F2's `0xFFFFFFFE` (§11i). Clicking it fires the same `0x43` click-info
packet as any entity click, and RTK's `clif_parseclick` (`clif.c:11061`) special-cases it: the proximity
check that normally gates an NPC click is skipped outright when `nd->bl.m == 0` — the F1 NPC has no real
map, so it's reachable from anywhere. It opens **`F1Npc.click`**
(`rtklua/Accepted/NPCs/CentralFunctions/f1npc.lua`) — a normal NPC dialog (menu strings, `player:warp`,
etc.), just triggered by a hotkey instead of walking up to a placed sprite. `Session.HandleClickInfo` now
matches `id == 0xFFFFFFFF` (`F1MenuSentinel`) → `OpenF1Menu()`, reusing the SAME async dialog machinery
(`DlgMenu`/`DlgSay`, §11e) real NPCs use, fed a synthetic `Mob` (`F1VirtualNpc`, sprite 0 → no portrait,
never spawned/looked-up — it only exists to fill the `0x30` packet header).

**Real RTK's menu has ~15 entries**, most gated on systems this server doesn't model: a GM-only submenu,
Kan (cash-shop currency) donations toward "Wisdom Star," tutor appointment, minigame win/loss stats, an
RTK-hosted character webpage's visibility toggles, "Faerie Light," "AFK Message." **Trimmed to three real
entries:**

- **Silver Thread** — only offered while dead (`IsDead`; picking it alive echoes RTK's own line: *"This
  is for the dead of the land to find a path to the shaman. You are not dead, so you have no path with
  me."*). RTK branches the Shaman choice on `player.country` (0 Wilderness / 1 Kugnae / 2 Buya); this
  server only has two home nations modeled, so it collapses to `_char.Nation`: Buya (`2`) offers **Felis**
  (map 338) / **Storm** (339), everyone else **Dusk** (map 8) / **Dawn** (map 9) — all four are real RTK
  map ids, confirmed present as literal 10×10 "\* Shaman" rooms in `data/game-data/map_index.csv`. Picking one
  calls `ReviveAt` (new — factored out of the old `Revive()`): full heal (gear/buffs included) + warp,
  replacing the fixed-timer auto-revive §11f used to do. RTK's own flow only *warps near* a physical Shaman
  NPC (revival happens on a second click there); this server skips that actor and revives directly on
  arrival, since no standalone Shaman NPCs are placed.
- **Toggles** — RTK's submenu covers Clan Chat + Subpath Chat; only Subpath Chat exists here (§11i), so
  the submenu is a single toggle line for now. Same flag/toggle as F2 — this is just RTK's menu exposing
  the same switch a second way.
- **Choose a Path** — RTK's `F1Npc.level5popupDialog`, offered once a Peasant (path 0) reaches level 5
  (the same threshold `AwardExp`'s Peasant wall enforces, §11j). Warps to the guild-entrance map for the
  chosen class at `(8,7)` — RTK's own coordinate — reusing `PathHalls`' existing outer map ids (§11f's
  neighbor, "Class path-hall interior warps"). Only warps; a Guildmaster NPC inside is what actually calls
  `SetCharClass` (`NpcAbility`'s path-choice ability) — matches RTK, whose `level5popupDialog` also only
  warps.

**Not yet live-tested.** The `0xFFFFFFFF` sentinel, the trimmed menu, and the Silver Thread shaman list are
all ported from RTK source with no live 4.95 client session behind them — confirm F1 actually opens the
menu (and that ghosts can no longer wake up on their own) before relying on it.

---

## 11l. Party & trade (added 2026-07-26 — awaiting live confirmation)

Neither existed before this: the `0x1b` sub-`0x02`/`0x08` toggles (§9.5) only ever set a *willingness*
flag (`Character.Grouped`/`Exchange`) with no request/accept logic behind them, AND clicking another player
showed nothing at all (§9.5/§11e — `SendClickProfile` could only ever serialize your own character). Both
gaps are fixed together here, since the real trigger for party/trade turns out to be buttons on that same
profile window: rules are ported from RTK (`rtk/src/map/clif.c`) faithfully, presentation (the trade window
specifically) deliberately NOT — see below.

### Party (RTK "group" — `clif_addgroup`/`clif_leavegroup`/`clif_updategroup`, clif.c:13993-14148)

A `Party` (`Server/Party.cs`) is a plain in-memory leader+members list, never persisted — matches RTK's own
`groups[MAX_GROUPS][MAX_GROUP_MEMBERS]`, which is session-table state, not a DB row. The leader is always
`Members[0]`; leaving/kicking removes from the list, which naturally promotes the next member (RTK:
`group_leader = groups[groupid][0]` after a removal). **Cap is 6**, NexusTK's real historical party size —
RTK's own `MAX_GROUP_MEMBERS` (256) is just an oversized array bound for its static table, not a gameplay
rule, so it isn't copied.

Ported rules (`Session.TryPartyInvite`, RTK's literal minitext wording where it has one):

1. Target not found → *"X is nowhere to be found."* (RTK's `clif_addgroup` just `nullpo_ret`s silently on a
   bad name; this server gives feedback here, matching how whisper already handles the same case — §11.)
2. Inviting yourself → *"You can't group yourself..."*
3. **Special case:** the leader "inviting" someone already in their OWN party **kicks** them — RTK's own
   self-referential branch (`tsd->group_leader == sd->group_leader && sd->group_leader == sd->bl.id` →
   `clif_leavegroup(tsd)`).
4. Your party already at the 6-member cap → *"Your group is already full."*
5. Target is dead → *"They are unable to join your party."*
6. Target's "sociable" flag (`Character.Grouped`, Shift+G / `0x1b` sub-`0x02`) is off → *"They have refused
   to join your party."*
7. Target already in a group (even a different one) → the same *"They have refused to join your party."*
   text (RTK collapses both refusal reasons to the identical line).
8. Otherwise: join (creating a new party if you had none), then broadcast *"X is joining the group."* to
   the whole party on RTK's dedicated **type=11 "group"** minitext channel (`Session.NotifyGroup` —
   see the `SendMiniText` type table, §9.5).

**Not modelled:** RTK's per-map `canGroup` gate (no such per-map concept exists here) and RTK's explicit
allowance for a *dead* player to invite others (a ghost isn't specifically blocked from grouping here
either — it just isn't specifically un-blocked, since nothing in `TryPartyInvite` checks the inviter's own
state at all, matching RTK, which also only checks the TARGET's state).

Leaving (`RemoveFromParty`) sends the exact same *"You have left the group."* text whether you left
voluntarily or were kicked — RTK's kick branch literally calls `clif_leavegroup(tsd)`, so there is no
separate "you were removed" wording to port. Dropping to one member disbands the party (*"Your group has
disbanded."* to the straggler). Disconnecting mid-party runs the same cleanup (`Session`'s read-loop
`finally` block).

**Real trigger: the "Group" button on another player's profile window.** Clicking a player now shows
*their* real click-profile (`0x34`, fixed alongside this — see §9.5/§11e), whose group/exchange status
cells are exactly what the client reads to enable those two buttons. Clicking "Group" fires **`0x2e`**
(`HandlePartyInvite`) — RTK's real `clif_addgroup` wire shape, `nameLen(u8) name[nameLen]` (identical shape
to `0x19` whisper, §11; the client already has the target's name from the profile it's showing). This is a
CONFIRMED-real 4.95 opcode (seen in an earlier capture, unlike the untested `0x29`/`0x2A`), so it's wired as
the primary path, not a defensive guess. `!party <name>` / `!party` (roster) / `!leaveparty` remain as
name-based chat-command fallbacks for testing or when a button isn't available (there's no profile-window
equivalent for "leave"/"list roster" to trigger from).

### Trade (RTK "exchange" — `clif_handitem`/`clif_handgold`/`clif_parse_exchange`, clif.c:14548-15250)

RTK's real exchange is a dedicated **binary trade window**: hand an item/gold to the player you're facing
(`0x29`/`0x2A`, resolved via the SAME front-tile lookup this server's melee already uses), which opens a
two-sided add-item/add-gold/confirm/cancel window (a further sub-opcode dispatch keyed off a `type` byte).
None of that window's 4.95 wire format has ever been captured live — and after the profile system (§9.5)
and the password-length client crash (see memory), guessing a new binary UI packet's shape and being wrong
is a real way to crash the client, not just get an ignored packet.

So this server's trade reuses the **same async dialog primitives** NPC shops/the bank already drive
(`DlgMenu`/`DlgSay`/`DlgInput`, built on the live-confirmed `0x30`/`0x3a` NPC dialog packets — §11e) instead
of inventing a new opcode. The RULES are still ported straight from RTK; only the presentation is a menu
instead of a window (the exact same tradeoff already made for the buy/sell grid, §11e's "Remaining"
note). `Server/Trade.cs` holds the plain data (`Trade`: two `Session`s + two `TradeOffer`s of items/gold/
confirmed; `TradeOffer`).

Ported rules (`Session.HandleTradeCommand` / `RunTradeMenuAsync`):

- Both sides must have the "exchange" flag on (`Character.Exchange`, `0x1b` sub-`0x08`), be alive, on the
  same map, and not already in another trade — any failure replies with RTK's literal *"They have refused
  to exchange with you"*.
- Offering an item or gold **un-confirms both sides** — needed so a stale confirm can't sneak a changed
  offer through; RTK's own two-step `clif_exchange_sendok` confirm dance depends on the same invariant even
  though RTK's source doesn't show an explicit reset (its escrow model makes it structurally impossible to
  change an offer post-confirm, which this server doesn't replicate — see next point).
- **Deliberate simplification:** RTK escrows an item out of your bag the instant you offer it. This server
  does **not** — offering just records a snapshot (item id/durability/custom name/amount); finalize
  (`TransferItems`) re-checks you still actually hold it, and can only transfer LESS than promised (skipped
  silently) if you spent it elsewhere mid-negotiation, never more. This trades one authentic behavior
  (you can keep using an offered item until the trade actually closes) for ruling out an entire class of
  dupe/loss bug from an unescrowed, dialog-driven reimplementation.
- Finalizing (both sides confirm) moves gold and items in one `FinalizeTrade` call and sends RTK's literal
  closing line, *"You exchanged, and gave away ownership of the items."*, to both sides.
- Cancelling (either side) or disconnecting mid-trade ends it for both with RTK's *"Exchange cancelled."*
  and nothing moves (nothing was ever escrowed, so there's nothing to roll back).

**Real trigger: the "Exchange" button on another player's profile window.** Same click-profile window as
party above; clicking "Exchange" fires **`0x4a`** — RTK's `clif_parse_exchange` sub-protocol dispatcher.
Only its `type=0` ("initiate", body `00 targetId(u32BE)`) sub-case is handled (`HandleExchangeRequest`):
that's the one sub-message that means "open a trade with this id," which is all the button click needs to
say once this server takes over with dialogs instead of RTK's real trade window. RTK's other sub-types
(1 amount-ask, 2 add-item, 3 add-gold, 4 quit, 5 finish) all belong to that window and are never sent by a
client that never saw the window opened, so they're intentionally not handled. Like `0x2e`, **`0x4a` is a
CONFIRMED-real 4.95 opcode** (also seen in an earlier capture), not a speculative wiring — RTK's real
hand-item/hand-gold gesture (face the target, opcodes `0x29`/`0x2A`) is a separate, never-captured path and
still isn't wired. `!trade <name>` remains as a name-based chat-command fallback for testing.

**Not yet live-tested.** Both features are ported from RTK source with no live 4.95 client session behind
them. Confirm: clicking another player renders their real profile with the Group/Exchange buttons enabled
per their flags, that clicking either button actually sends `0x2e`/`0x4a` with the expected body shape (both
opcodes are confirmed real, but their exact trigger — is it really "click a button on the profile window,"
or some other gesture? — hasn't been pinned down live), and that `!party`/`!leaveparty`/`!trade` all produce
visible chat-log/status-box text as a fallback (built entirely on already-proven `SendMiniText`/dialog
primitives, so their main open question is UX, not wire-format risk).

---

## 11m. Inter-continent travel (world map) (added 2026-07-26 — native 0x2e screen WORKS; earlier "broken client" conclusion was our own one-byte framing bug)

Going from Mythic Nexus (the Vale) to Buya, Kugnae, and the other towns isn't a normal tile-to-tile warp —
in real NexusTK it's a special "world map" screen you reach by walking to a town's edge.

**RTK source (`RTK-Server/rtklua/Accepted/onScriptedTiles/onScriptedTilesMap.lua` +
`Scripts/sendWorldMap.lua`, `RTK-Server/rtk/src/map/clif.c:15402 clif_mapselect`):** every walk step
re-checks the player's current map name + x/y against a hardcoded per-town edge region
(`onScriptedTile`, fired unconditionally after every completed move — the same hook RTK also uses for
foraging/fall-rooms, ported here as `OnScriptedTileStep`). Stepping onto one of those tiles opens a
full-screen destination picker (`clif_mapselect`, outgoing opcode `0x2e`) listing every other continent,
built from a hardcoded table in `sendWorldMap.lua`. Clicking a destination just makes the client echo the
ordinary map-change packet (`clif.c` `case 0x3F`) with the `(map,x,y)` triple it was handed —
**`pc_warp` applies zero validation and zero level/quest/req gate** to that request; RTK's only "gate" is
that Mount Baekdu is omitted from the list entirely unless `quest["instance"] == 8`. This is a trusted,
cosmetic-only gate, not a real anti-cheat boundary — consistent with this codebase's existing trust model
for map-change requests.

**Outgoing `0x2e` — native screen, wire format re-derived from the CLIENT's own receive parser (2026-07-26).**
The format is taken from the 4.95 client itself (the only authoritative spec — **not** RTK 7.x, whose
`clif_mapselect` has a different shape). The real receive dispatch is a two-level jump table at `0x44b9c0`:
`sel = byte[0x44bc80 + (opcode-3)]`, then `jmp dword[0x44bbd4 + sel*4]`; opcode `0x2e` -> sel 22 -> stub
`0x44bac4` -> `call 0x450580` (the world-map handler). Disassembling `0x450580` (`re/disx.py 0x450580`)
gives the EXACT field-by-field parse. Body (bytes AFTER the opcode byte):

```
bgNameLen   u8        <- payload[0] IS the length. There is NO leading "kind" byte (an earlier version of
                          this doc invented one; the client reads payload[0] straight into the string length).
bgName      bgNameLen bytes
destCount   u8
?           u8        -- read by the parser (stored, [ebp-0x20]) but not referenced further; sent as 0
-- destCount times:
  x0        u16BE     -- dot position on the background art (hand-placed per destination; NOT a warp coord)
  y0        u16BE
  nameLen   u8
  name      nameLen bytes    -- the destination's display label (converted to wide via MultiByteToWideChar)
  mapId     u32BE     -- the actual destination map (client reads it across two u16 slots)
  x1        u16BE     -- the actual landing tile
  y1        u16BE
```

`Session.SendWorldMap(bgName)` builds exactly this. **The background is `field10` = "Map of the Kingdom":**
`Inter.dat` holds `field10.epf`..`field18.epf` (640x480 single-frame backgrounds — `field10` is the whole-
kingdom overview, `field11`-`field18` are the per-region maps Koguryo/Buya/Kaya/Jinhan/Nagnang/Paekjae/Shilla/
Sonhi), identified by rendering them and reading each image's baked-in title banner. `NATION_E` is only a 20KB
flag icon — too small for a 640x480 background, which is why it rendered black. Default `bgName = "field10"`.
**The `x0,y0` dot coordinates and `x1,y1` landing tiles are RTK `sendWorldMap.lua`'s real hand-placed values**
(`WorldDest.X0/Y0`), not an invented grid. RTK's coords target its 1024x768 `WMkru` canvas; 4.95's `field10`
is 640x480, so `SendWorldMap` scales `x0,y0` by **0.625** (640/1024 = 480/768). LIVE-CONFIRMED: unscaled,
every dot with `x0 > 640` fell off the right edge (only Mythic Nexus @380 and KaMing's @280 were visible);
scaled, all seven land on-screen.

**The crashes were OUR one-byte framing bug, not a client bug (corrected 2026-07-26).** For a while this
was written up as a confirmed "client memory-lifetime / dangling-pointer bug, dead end" — complete with a
fabricated `"File not found .EPF"` log line that never appeared in any capture, and two other inferred
theories ("wrong archive", "heap corruption"). All of that was wrong. The retail 4.95 client is not buggy;
this screen worked for millions of real sessions. The bug was entirely on our side.

**What was actually happening:** `Session.SendWorldMap` prepended a spurious leading `kind = 0` byte before
the length-prefixed background name. But the client's parser (`0x450580`, above) reads `payload[0]` **as the
bgName length**. So the client read our `00` as `bgNameLen = 0` -> empty name -> a `%s.epf` path builder
produced `"."` -> `catlookup2(".")`, and — critically — **every subsequent field was then shifted one byte**,
so `destCount` and the per-entry offsets became garbage, the handler computed a bogus huge allocation, the
raw allocator (`0x4adc38`) returned NULL, and the uncaught `_CxxThrowException` (`0x4abfa0`) killed the
process. The Frida capture that had looked like proof of a client bug —

```
DECR  op=0x2e ... 2e 00 04 49 74 65 6d 07 ...        <- our body: [00]=kind, [04 "Item"]=len+name
>>SPR catlookup2(".")                                 <- client read [00] as bgNameLen=0 -> "" -> "."
>>SPR *** _CxxThrowException reached ***
```

— is exactly what a one-byte-too-long packet looks like: `"Item"` never "failed to survive," it was simply
never where the client was told to look, because our first byte lied about the name length. **Fix: remove
the leading byte.** `SendWorldMap` now starts the body directly with `AddLenStr(d, bgName)`.

**How it was found:** by disassembling the client's OWN receive dispatch (`0x44b9c0` two-level jump table)
to confirm `0x2e -> 0x450580`, then reading `0x450580`'s parse field-by-field — which immediately showed
`payload[0]` being consumed as the string length with no preceding byte. This is the lesson: when a
shipped, retail client crashes on our packet, derive the format from the client's own parser and assume the
framing error is ours; do not trust a different-version reference (RTK 7.x) or a `!wmtest` test path as
ground truth, and never write an inferred narrative into the docs as observed fact.

**Response: the native screen is RE-ENABLED at the real trigger tiles.** `TryWorldMapTravel` now calls
`SendWorldMap("field10")` directly. `!travel` (`RunWorldMapMenuAsync`) is kept as a dialog fallback in case
the native screen ever regresses live; `!wmtest [name]` remains for trying alternate background graphics.
**Still to verify live:** that the corrected packet renders end-to-end and that destination clicks warp
(the `0x3F` reply shape below is still a guess).

**Incoming `0x3F` — click AND ESC reply, LIVE-CONFIRMED (2026-07-26).** Captured body (after the opcode):
`mapId(u32BE) x(u16BE) y(u16BE) 00` — e.g. clicking Kugnae sent `00 00 03 f3 00 12 00 0e 00` (`0x3f3`=1011,
x=18, y=14). This is exactly RTK's `case 0x3F` map-change (`pc_warp` with the client-supplied map/x/y,
clif.c:11619). **Note `mapId` is a u32, not the u16 an earlier guess assumed** — reading it as u16 gave 0 and
matched nothing, so clicks silently did nothing until this was fixed. **There is no separate cancel opcode:**
opening the map makes the client "leave the world", and BOTH a destination click and **ESC** send this same
`0x3F` — ESC just carries the player's ORIGINAL map/x/y. So `Session.HandleWorldMapSelect` warps to the
destination when the triple matches a `WorldDest`, and otherwise (ESC, or any unrecognized coords) returns
the player to the origin it stored when the map opened (`_worldMapReturnMap/X/Y`) — the player can never be
stranded on the map screen or mis-warped to arbitrary client-chosen coordinates. A `_worldMapPending` flag
(cleared on any map change) guards against a stray `0x3F` outside a world-map session.

**`!travel` — dialog fallback.** Opens the same destination list through the async dialog primitives
(`DlgMenu`, live-confirmed `0x30`/`0x3a`, §11e). No longer the primary path (the native screen is now
wired to the trigger tiles), but kept as a manual fallback independent of the `0x2e`/`0x3F` native path in
case that ever regresses live.

**`!wmtest [name]` — native screen with an explicit background name.** Calls `SendWorldMap(name)`, defaulting
to `field10` when no name is given, for trying alternate backgrounds (`field1`, `title`, other `fieldNN`).
The framing bug that used to crash this is fixed.

**Destinations** (`Session.cs` `WorldDests`) — 7 of RTK's 9 real destinations; Hamgyong Nam-Do (map 99,
RTK's own display-name mismatch against its actual map title) and Mount Baekdu (map 4259, the one
quest-gated entry) are both omitted because neither has renderable map data in this project's
`data/game-data/map_index.csv`:

| Destination | Map | Landing (x,y) |
|---|---|---|
| Kugnae | 1011 | (18,14) |
| Buya | 1012 | (1,11) |
| Mythic Nexus | 41 | (30,4) |
| Nagnang | 2520 | (8,8) |
| Arctic Land | 1013 | (9,9) |
| Hausson | 1025 | (10,8) |
| KaMing's Encampment | 3800 | (31,3) |

**Trigger tiles** (`Session.cs` `WorldMapTriggers`, keyed by the town map the player is standing in — each
of the 7 towns above is also a trigger source, so the picker is reachable from any of them): Kugnae
Gathering `x=19, y∈{12,13}`; Buya Gathering `x=0, y∈8..12`; Mythic Nexus `y=1, x∈28..32`; Nagnang Gathering
`y=5, x∈7..9`; Haeng Tavern `x=10, y∈{7,8}`; Kafas Tavern `x=11, y∈7..9`; KaMing's Encampment `y∈{0,1},
x∈30..34`. All seven fit inside their map's real dimensions per `map_index.csv`.

**Live status (2026-07-26).** The trigger tiles are confirmed live. The framing bug that made the native
`0x2e` screen crash (a spurious leading byte — see above) is fixed and the corrected packet builds clean,
but the fixed native screen has **not yet been re-tested live** (the earlier "3 crashes / two different
bugs" writeup was all downstream of that same one-byte error, now understood, not a real client fault). The
native screen is re-enabled at the trigger tiles (`SendWorldMap("field10")`); `!travel` remains as a dialog
fallback. Still open once tested live: whether the picker renders correctly, and the `0x3F` click-reply
shape (still a guess, validated against `WorldDests` so a wrong guess is inert).

---

## 11n. Mail/parcels, friend & ignore lists, weather, day-night, ambush & spot-traps (added 2026-07-26 — closing the RTK feature-gap audit)

Six independent additions from a single pass, each ported at a different confidence level — see each
sub-entry for what's grounded vs. original.

**Mail (`Server/Mail.cs`, `Session.HandleMailCommand`/`HandleBoard` sub-9).** RTK's nmail reuses the SAME
`boards_showposts`/`boards_readpost` machinery as a normal bulletin board (§11h), just addressed at board id
0 (a player's own mailbox). Confirmed the real `nmail_write`/`boards_post`/`boards_showposts`/
`boards_readpost`/`boards_delete` implementations don't survive anywhere in the reference tree (checked
`rtk/src/map/board_db.c` and `clif.c` both — only the `clif_handle_boards` dispatcher does), so there's no
wire evidence at all for how mail gets COMPOSED (no recipient field visible in the dispatcher). Reading an
existing mailbox reuses the same (already-`§11h`-unverified) `0x31` sub-2/sub-3 reply builders, now sourced
from a new `mail_posts` SQLite table when `boardId==0` instead of `board_posts`; composing is chat-command
only (`!mail send <name> | <subject> | <body>`, `!mail sendItem <name> <item> [amount] | <subject> | <body>`
— the "parcel" half, pulling straight from the caster's bag and removing it same as handing it over in
person). RTK gates nmail at level 10 (`clif_handle_boards` case 6's exact wording, kept verbatim); claiming
an attached item is one-shot (`Mail.ClaimItem`) regardless of whether it's read via `!mail read` or (if a
client ever sends it) the native `0x31` sub-3 path — both funnel through `Session.ReadMail`.

**Friend & ignore lists (`Character.Friends`/`IgnoreList`, `Session.HandleFriendCommand`/`HandleIgnoreCommand`).**
Ignore is real RTK (`map.h sd_ignorelist`, `clif.c` `ignorelist_add`/`ignorelist_remove`/`clif_isignore`) —
ported behaviorally (mutual: a whisper is blocked if EITHER side has the other listed, RTK's exact
`canwhisper` failure wording "They cannot hear you right now." kept verbatim in `DoWhisper`) but via chat
command (`!ignore add|remove <name>`) rather than the raw client packet (`clif_parseignore`, opcode `0x0D`
sub-dispatch) — that packet is itself a sub-switch of some other outer receive opcode never pinned down for
4.95, so wiring it blind would risk misrouting a real 4.95 client packet. **Friends has no RTK source at
all** — no such struct/feature exists anywhere in the C engine — so `!friend add|remove|list` is a pure
original addition: a saved name list plus a live online check on `!friend` (list), nothing more (no
login/logout push notification).

**Weather (`World` `MapState.Weather`/`GetWeather`/`SetWeather`, `Session.SendWeather`, opcode `0x1F`).**
RTK's `clif_sendweather` (`clif.c:4565`) is a real, complete wire format — a single byte, 0=clear/1=WRAIN/
2=WSNOW (`map.h`) — but it's gated by `sd->status.settingFlags & FLAG_WEATHER`, a per-player options toggle
with no evidence either way that the 4.95 client's older UI even has it. Ported at face value (best real
number on record, same precedent as this file's still-uncalibrated sound ids); **UNVERIFIED against a live
4.95 client** — `!weather <0-2>` lets it be audited directly. No automatic RTK scheduler exists to port for
WHEN weather changes (`setWeatherM`/`getWeatherM` are pure admin/quest-script levers, never called on a
timer anywhere in the C engine) — `World.Tick` rolls a 20% chance per populated map every ~15 real minutes
as our own substitute, clearly not an RTK-sourced cadence.

**Day-night clock (`World.Time`/`_hour`/`_day`/`_season`/`_year`, `Session.SendTime`, opcode `0x20`).** Fully
grounded: RTK's `clif_sendtime` (`clif.c:4524`, `hour(u8 0..23) year(u8)`) and `change_time_char`'s real timer
(`map.c:1661`, `timer_insert(450000, 450000, ...)` — one in-game hour per 7.5 real minutes, broadcast
server-wide to every connected session on each tick) are both complete C-engine code, ported 1:1 in
`World.Tick`. This server had sent a hardcoded placeholder here since before this session (`0x10`/`0x32` at
one entry path, `0x00`/`0x00` at another) — the starting hour/year values were kept as the live clock's
initial state so deploying this doesn't jump the clock for anyone already playing.

The wire packet itself only ever carries `hour`+`year`, but `_year`'s *cadence* depends on RTK's day/season
rollover, so `World` tracks `_day`(1..91)/`_season`(1..4) internally purely to get that right, 1:1 with
`change_time_char`: hour rolls to `_day++`, `_day==92` rolls to `_season++`, and only `_season==5` rolls
`_year++` — i.e. one year is ~368 in-game days, not one in-game day. An earlier version of this port
incremented `_year` on every 24-hour rollover instead (a bug, not an RTK deviation — fixed 2026-07-28), which
would have made in-game years pass ~368x too fast. Cross-checked against the community "Time Chart" tutor
post (`WiKiDWiND`, Poets board: "1 Yuri (365 days) ⟺ 41 days 18 hours" real time), which independently lands
in the same ~41-46-real-day-per-year ballpark that RTK's actual 368-day cadence produces.

**Ambush (`Content.IsAmbushSpell`, `Session.CastAmbush`).** RTK `rogue/ambush.lua` ("Leap over your enemy to
face their back while attacking") has no mana cost in the Lua at all. Ported as a real reposition: teleports
the caster to the tile directly behind the FACED mob (relative to the target's own facing, re-facing the
caster to match) — which lines up exactly with `Combat.IsBehindTarget`'s existing unconditional positional-
backstab bonus, so the follow-up swing gets its x2 "for free" the same way a genuine sneak-up would. Only
targets world mobs (no PvP melee path exists in this server — §"no PvP damage path" elsewhere). RTK paces
reuse with `player.ambushTimer` (attack-speed-derived, not modeled here) — substituted with a flat 3s
cooldown.

**Watchful Eye / Spot Traps (`Content.IsSpotTrapsSpell`, `Session.CastSpotTraps`, `World.TrapsNear`).** RTK's
`seeSpotTraps()` (`Scripts/spotTraps.lua`) reveals nearby hidden rogue-trap NPCs by dropping a marker item
(id 99) at each trap's tile, tagged so only the caster sees it (`addTrapSpotters`/`getTrapSpotters`). Ported
via `Session.ShowGroundItem` called directly (not `World.DropItem`) so the marker never broadcasts to the
map — matching that same caster-only visibility without needing a new per-player-visibility concept. The
warrior family (`watchful_eye_warrior` + 3 reskins) has a real RTK cooldown (`player:setAether(key, 25000)`)
that never made it into the CSV export — 25000ms is hardcoded to match the Lua; the Rogue-side `spot_traps`
already had a correct exported `aether` (6000ms) and mana (100), used as-is.

---

## 12. Maps

Map files live in the client's install at `Maps\TK<mapId>.map` (e.g. `Maps\TK32.map`). They are:

- **Headerless.** `width × height` cells, **4 bytes per cell**, row-major.
- Each cell = `groundTile(u16, little-endian) objectTile(u16, little-endian)`.
- Example: `TK32` = 33×33 = 4356 bytes.

The server's `0x15` (enter-map) tells the client which map id + dimensions to load; the client reads
the `.map` file itself. Pick a map that actually has content: `TK27` is a uniform "void" tile (renders
black); `TK32` (33×33, ~180 distinct tiles, ~270 objects) renders a real area.

**Passability — ground pass flag PLUS `SObj.tbl` directional object-walls (corrected 2026-07-26).** Two
layers combine:

1. **Ground pass flag.** A cell is pass-solid iff the **top 2 bits of the ground `u16`** are set (value
   **`3` = solid**, `0` = walkable; `1`/`2` never occur). Water, cliffs, out-of-bounds, and *some* wall
   footprints are baked here.
2. **`SObj.tbl` directional object flags.** The object `u16` indexes `SObj.tbl`; each object has a 1-byte
   **directional wall flag** — `UP=1 DOWN=2 RIGHT=4 LEFT=8` (RTK `map.h`). A move that *enters* a cell while
   heading a given way is blocked if the destination object has that side's bit (`clif_object_canmove`:
   N→UP, E→RIGHT, S→DOWN, W→LEFT). A solid wall piece = `0x0F` (all four sides ⇒ impassable). Decorative
   objects (shadows, rugs, ground decor) have flag `0x00` ⇒ never block. This is the layer that stops you
   walking through a **building's thin side wall**, where the ground pass flag is `0` (only the door graphic
   itself gets `pass=3`).

**Why the earlier "objects are purely visual" note was wrong.** A prior pass concluded collision was
pass-only after mis-reading `SObj.tbl` (it thought wall objects `1519`-`1522` were `0x00` "walls with no
flag"). Re-derived correctly, `1519`-`1522` are *ground decor* (genuinely `0x00`), while the actual Buya
Jadespear-hut wall footprints are objects `505`-`508` = `0x0F` (solid) and the doorway is `372`/`373` =
`0x00` (open). Decisive proof the client uses these flags: a player **cannot** walk through the hut wall
even though its ground `pass=0` — only the object flag can be blocking it. The old `Obj != 0` collision was
still wrong (it blocked flag-`0` decor → "**stuck on shadows**"); the fix is to block on the object's
**directional flag**, not on `Obj != 0`.

**Server implementation.** `Server/ObjectFlags.cs` parses `SObj.tbl` (format below) into a per-object flag
byte; `MapData.BlockedMove(x,y,dir) = Solid(x,y) || ObjectFlags.Blocks(obj,dir)`. Both the **mob AI**
(`World.Tick` chase + wander steps) and the **player walk** (`Session.HandleWalk`) now use it, so mobs
respect the same walls the client draws (fixes "rabbits walked through the wall near Jadespear's hut, 2026-07-26").
Note this **diverges from RTK's own mob AI**, which is pass-only (`map_canmove` has `if(obj) return 1;`
commented out) — RTK mobs clip these walls too; we match the *client's* collision instead, which is what the
player sees. RTK's player path (`clif_canmove`→`clif_object_canmove`) does use the directional flags, but
`object_flag_init()` leaves `objectFlags[z]=flag;` commented out, so even RTK's own server never populates
the table — the live client is the only thing enforcing it there. (Doors are separate — the **'o' key
(`0x20`)** toggles a door's graphic; see "Doors" below.)

**`SObj.tbl` format** (confirmed: the record walk consumes the file to the exact byte AND yields exactly the
header object count; then validated tile-by-tile against the hut geometry): `u32 count` header, **1 lead
byte**, then `count` records each = `u8 tileCount` · `tileCount × u16` frame ids · **5-byte separator
`FF FF FF FF 00`** · `u8 flag`. `flag[objId]` (1-based) is indexed directly by the map's object id; id `0` =
no object = flag `0`. The **client's** table (`NexusTK.dat` → `SObj.tbl`, 7608 objects) is authoritative for
the object-id space the `.map` files use; RTK's copy (`RTK-Server/rtk/SObj.tbl`, 18954 objects) is a superset
that agrees on every in-range id. Extract the client copy with
`python re/pak_extract.py "<install>/NexusTK.dat" SObj.tbl data/SObj.tbl`.

The **spawn coordinate must lie inside the camera viewport** at the moment `0x33` is processed, or the
placement check bails and the character never renders (§14). With scroll `(0,0)` the initial viewport
is roughly map tiles `0..14`, so spawn small (e.g. `(5,5)`), or set the scroll via `0x04` first.

---

## 13. Full opcode → client-handler table

Receive-side dispatch (server → client): connection-state object handles only `0x02`; everything else
goes through world dispatcher `0x44b9c0` = `remap[opcode-3]` (`0x44bc80`) → jump table `0x44bbd4` →
handler. Opcodes outside `0x03..0x68`, or whose remap = the default `0x44bbcd`, are **no-ops**.

`✓` = decoded/used by the working server.

| Op | Handler | Meaning |
|---|---|---|
| `0x03` | `0x44f0e0` | download/URL (reads HKLM registry) |
| `0x04` | `0x44faf0` | ✓ coords / camera scroll + commit walk |
| `0x05` | — | ✓ self entity id (server→client) |
| `0x06` | `0x44fb90` | ✓ **map CELL-PATCH** (server→client): `startX(u16BE) startY(u16BE) width(u8) height(u8)` then `width*height` cells row-major, each = `ground(u16BE) object(u16BE)`. Writes each cell into the client's live map array and redraws the object layer over the patched rect (tail `0x44df30`, independent of ground change). This is the door open/close primitive (`SendObjRow`/`HandleOpen`) — and the 5.x terrain-stream reply. (client→server `0x06` = walk+view variant, unrelated.) |
| `0x07` | `0x44fdb0` | ✓ **creature/monster list** (server→client): `count(u16)` + 12B entries `X Y id look color dir`; `look=0x8000\|monsterId` → Monster.epf. §7.2/§11a |
| `0x0b` | `0x44fb70` | **no-op** |
| `0x0c` | `0x4502c0` | ✓ move / animate entity |
| `0x0d` | `0x450170` | ✓ over-head speech |
| `0x0e` | `0x450440` | ✓ **despawn list** (server→client): `count(u8)` + `id(u32)`× (client→server = chat) |
| `0x0f` | — | ⏳ **add item to bag slot** (server→client) — §11c. **client→server = cast spell** (`clif_parsemagic`: slot+1, then per type: answer / target id / none) — §11d |
| `0x17` | — | ⏳ **add spell to book** (server→client): `slot(u8) type(u8) name(u8len) question(u8len)` — §11d. (client→server = throw item) · `0x18` = remove spell |
| `0x10` | — | ⏳ **remove item from bag slot** (server→client) — §11c |
| `0x11` | `0x450350` | entity + 1 byte |
| `0x12` | `0x4509a0` | entity + 2 bytes |
| `0x13` | `0x4508f0` | ✓ **combat damage / over-head HP bar** (server→client): `id(u32BE) crit(u8) percent(u8) hitSnd(u8)` → HP bar + hit anim `0x8f−crit` + sfx — §7.2 |
| `0x15` | `0x44f8b0` | ✓ enter-map |
| `0x16` | `0x450a00` | **ground ITEM/object spawn** (draws from Item.epf `"I"`): `+5 gfx(u16) +7 id(u32) +0xb X/Y …`. **NOT a monster** — §7.2, §11a. **Walk projectile → invisible at rest; the server uses `0x07` static objects for floor items instead — §11c.** |
| `0x1a` | `0x4503a0` | ✓ action (attack/sit/…) |
| `0x1b` | `0x450830` | ? |
| `0x1d` | `0x450db0` | entity + 1 byte |
| `0x1e` | — | ✓ ack |
| `0x1f` | `0x450f40` | 3-state set (thresholds 0x0b/0x63/0x65 → `[world+0x401]`) |
| `0x20` | `0x44f820` | ✓ time-of-day (server→client). **client→server = the 'o'/Open key** (RTK `clif_parse` case `0x20` "Clicked 'O'" → `clif_cancelafk` + `clif_open_sub` → `openDoors`): in NexusTK this **toggles the faced door object's open/closed graphic in place** (cosmetic; passability untouched) — it does NOT warp. Body is a bare `00` (no target), so the server resolves the faced tile from its own pos + facing. Confirmed live 2026-07-25: sent **only** on the 'o' keypress (deliberate, not a heartbeat — RTK's handler clears AFK). `HandleOpen` toggles the faced door via the door table and the `0x06` cell-patch (above). Doors: §12 |
| `0x21` | `0x450f90` | UI window (`0x174`) |
| `0x26` | `0x44fb80` (main) → **`0x4903d0`** (pre-dispatch) | **Self-walk — WORKS.** Main-table handler is a no-op, but `0x26` is pre-dispatched via the self-entity vtable (`+0x38` @ `0x4cf038` → `0x48eb40` → `handlerB`). This is the 4.95 self-walk primitive (§10.3). |
| `0x29` | `0x4504b0` | ✓ **effect animation** over entity: `id(u32) effectId(u8, 1-based) A/B/C(u16)`; u8 indexes `Effect.tbl` (128 fx). **Not a damage number** — §11d |
| `0x2e` | `0x450580` | list: name + looped u16 items (skills?) |
| `0x2f` | `0x44f490` | **buy/shop grid window** (RTK `clif_buydialog`) — identified, not implemented (shops use `0x30` menus instead — §11e) |
| `0x30` | `0x44f530` | ✓ **NPC dialog** (server→client): text box / menu / input, `body[0..1]` kind = `00 01`/`02 02`/`04 04`. Live-confirmed on 4.95. Reply = `0x3a`. See §11e |
| `0x31` | `0x451080` | ? |
| `0x33` | `0x44fef0` | ✓ self/entity spawn (appearance) |
| `0x34` | `0x450270` | ✓ **click-profile** window (UI `0x198`); body parsed by widget `0x48b6a0`. See §9.5 |
| `0x35` | `0x450890` | ? |
| `0x36` | `0x4515d0` | ? |
| `0x37` | — | ⏳ **equip-window entry** (server→client) — §11c |
| `0x38` | — | ⏳ **unequip-window** (server→client) — §11c |
| `0x39` | `0x4510f0` | ✓ **self-profile** ("Mind's Eye", UI `0x198`): AC/clan/title/class/legend. See §9.5 |
| `0x3b` | `0x450fe0` | ? (client heartbeat companion) |
| `0x42` | `0x451120` | ? |
| `0x44` | `0x4511a0` | ? |
| `0x46` | `0x451020` | ? |
| `0x4a` | `0x4514d0` | ? |
| `0x4b` | `0x451630` | ? |
| `0x66` | `0x4511b0` | ? |
| `0x67` | `0x4513e0` | ? |
| `0x68` | `0x4516a0` | ? |

---

## 14. Learnings, gotchas, things tried & failed

A running list of hard-won lessons. Read this before spending days on something we already burned time
on.

**Enter-world**
- **The world object doesn't exist until you send `0x02`+`00`.** Every "silent hang / black screen"
  is this. Send it first. (§5)
- **`0x05` (self id) is mandatory** and must precede/match the id in `0x33`, or input/camera never
  bind — map loads but the client stays black and won't move.

**Rendering the self**
- **`renderKind` (the byte after the 7 appearance bytes) must be 1/2/3, not 0.** With 0 the handler
  bails before allocating the sprite → invisible character, world never goes fully live. Use 1.
- **Spawn must be inside the camera viewport.** `0x33`'s placement check (`0x424310`) is a rect
  containment test against the viewport (built from scroll `[world+0x3f0/3f4]` + view size, clamped to
  map dims). With scroll `(0,0)` the viewport is ~tiles `0..14`; spawning at `(16,16)` fails silently
  (placement returns 0, entity never created). Send `0x04` to set scroll first, and/or spawn small.
- **Appearance byte `[1]` is a FORM/STATE byte, not hair.** Putting a hair value (e.g. 0x58=88) there
  selects "form 88" = no sprite = invisible. We chased "invisible character" for a long time because
  we assumed the 7 bytes were `[sex][hair][face]…`. They are not (§8). Decode by observation, not
  assumption.
- The 4.95 player look is **only** the 7-byte type-0 form. There is **no** rich equipment/hair
  appearance packet. (`0x33` type=1 is a direct-sprite form but still draws from the *player* archive,
  not monsters — don't mistake it for a creature path; see §7.2/§11a.)

**Monsters (SOLVED — `0x07`)**
- **The real monster spawn is `0x07`, not `0x33` or `0x16`.** `look = 0x8000 | monsterId` draws from
  Monster.epf; full layout + how it was traced in §7.2/§11a. Verified live: visible, collidable, killable.
- **`0x33` can never draw a monster in 4.95** — all renderKinds use the player archive `0x4f2a84`. Don't
  retry the "type-1 sprite id" sweeps; they're a dead end (a 7.x-vs-4.95 gap — 7.x monsters ARE `0x33`).
- **`0x16` is the ground-ITEM spawn, not the monster spawn.** Cost ~8 test cycles: it creates a
  monster-*class* object (vtable `0x4cd18c`, its own tick `0x463270`) but that object draws its sprite from
  category **`"I"` = Item.epf**, so it's invisible (item id doesn't exist), has no collision, and never does
  an `"M"`(Monster) lookup. The opcode-map's "creature/**obj** spawn" note meant *object/item*. Verified via
  a Frida hook that logged which sprite category each render requested. Find the real monster path via the
  `"M"` category draw — §11a. (Later: `0x16` is also wrong for **floor items** — as a walk projectile it
  despawns at rest. Resting items use the `0x07` static base-object instead — §11c.)
- **`0x16` divide-by-zero crash:** its position interpolation `idiv`s by `|X-X'|+|Y-Y'|`, so a "stationary"
  spawn with from-tile == to-tile crashes the client (`arithmetic` exception at `0x4631f7`). Always send the
  from-tile ≥1 away.

**Movement**
- **One `0x0C` only animates; it does not advance the tile.** You must also send `0x04` to commit the
  step, or the character animates once and freezes. (§10)
- **`0x0C` overshoots by one tile — send the SOURCE tile for peers/mobs.** The walk ends one tile *past* the
  packet tile in `dir`. The self gets corrected by `0x04`; a peer/mob doesn't, so `0x0C(dest)` leaves it
  rendered a tile ahead of the server (a single-stepping mob visibly sits on the wrong tile — looked like
  mobs "walking through each other"). Fix: broadcast the **source** tile (`client_final = source + forward`).
  Cost ~an afternoon of "is collision broken?" before a client-side position trace showed `client = server+1`.
  (§7.2, §11b)
- **Use `0x26` for self-walk in 4.95** — its main-table handler is a no-op, but it is pre-dispatched to the real self-walk handler (§10.3). A no-op in the dispatch table is NOT proof an opcode is unhandled (cf. `0x08`).

**Combat**
- **The swing and the damage are two different packets.** The attack *swing* is a `0x1A` action (type=1). The
  server→client `0x13` is the *combat damage* packet — over-head HP bar + hit spark (§7.2) — sent at hit
  resolution. Do send it, with a real `percent`/`critical`; a **bare/zero** `0x13` gives `crit=0` → anim
  `0x8f` (a "death flash"), which is the historical trap that made us wrongly avoid `0x13` entirely.

**Creation / appearance mapping**
- **Creation-packet field order ≠ render-packet field order, and they use different id spaces.**
  Creation `[0]`=face, `[1]`=gender; render `[0]`=body, `[2]`=face. We shipped two wrong mappings
  (invisible chars, then everyone-female) by assuming correspondence. Map each field explicitly.
- **Decode ambiguous fields with *controlled* experiments, not opportunistic samples.** We couldn't
  tell gender from nation/totem while the tester varied several attributes at once. The moment we
  created characters literally named "male"/"female" and "faceone/two/three" (one attribute varied),
  the bytes fell out immediately. This is the single biggest methodology lesson.

**Reverse engineering the send side**
- **The creation-packet *builder* is dispatched via a C++ vtable callback** (through generic dispatch
  `0x4ae65c` / `0x485b5a`), so a Frida backtrace from the encrypt routine lands in generic message-pump
  frames, not a clean "build creation packet" function. Static RE of the builder was a dead end;
  controlled experiments settled the field layout instead.

**Crypto**
- Don't port the 7.x cipher (name-derived table + trailer bytes). 4.95 is the simple NexonInc XOR on
  both channels, no trailer. Using 7.x crypto/framing desyncs the client. (§3)

**Persistence architecture**
- Login and game channels are **separate connections**. A character created on login (port 2000) must
  round-trip through a store (file/DB) before the game channel (port 2005) can read it — you cannot
  share it in memory across the two sessions.

---

## 15. Reverse-engineering toolkit & methodology

The workflow that cracked this, for whoever continues:

- **Static disassembly** — Python + `capstone` + `pefile`. Helper script `re/disx.py`:
  `python re/disx.py <va> [len]` disassembles at an absolute VA; `xref <addr>` finds references;
  `str <text>` searches strings. Because there's no ASLR, VAs are stable across runs.
- **Live instrumentation** — **Frida 17.16.4**, script `re/frida_probe.py`, launched elevated via
  `re/probe.bat` (Run as administrator — the client's `WINXPSP2` app-compat shim forces elevation, so
  Frida spawn fails with `0x2e4` unless the launching terminal is elevated). Hooks: WSOCK recv/send/
  connect, decrypt (`0x478680`) / encrypt (`0x478760`) — the encrypt hook prints the client's
  **plaintext sends** — plus the world ctor, map load, `CreateFile`, and a `0x33`/placement trace.
  It can also capture `Thread.backtrace` on chosen opcodes.
  - **Frida 17 API note:** `Module.findBaseAddress` and 2-arg `Module.findExportByName` were removed;
    use `Process.findModuleByName(name).base`, `m.findExportByName`, `Module.findGlobalExportByName`.
- **The "look-lab"** (the highest-leverage technique for the appearance work): an in-game chat command
  in the server that spawns test dummies via `0x33` with arbitrary appearance bytes, so you read the
  sprite id-space **off the screen** instead of static-RE-ing the sprite archives.
  - `!look b0 b1 b2 b3 b4 b5 b6` — spawn one dummy (named by its bytes) with those 7 appearance bytes.
  - `!row i lo hi` — spawn a west→east row sweeping `appearance[i]` from `lo..hi` (all other bytes at a
    safe baseline). One screenshot maps an entire byte's meaning.
  - This also incidentally proved that **spawning other/non-self entities via `0x33` works** (dozens of
    dummies rendered at once) — so NPCs / other players are largely a solved rendering problem.

**Meta-lesson:** when static RE descends into deep call trees (sprite composition, vtable dispatch),
switch to **observation** — controlled inputs + watching the client — which is often an order of
magnitude faster for "what does this byte mean" questions.

---

## 16. Open problems / where to continue

- **Stats / HUD — SOLVED (2026-07-24).** The self-stats opcode is **`0x08`** — see the §"Stats packet"
  reference. (Static dispatch analysis was misleading: `0x08` is a no-op in *both* the world dispatcher
  remap and the conn-state object `0x444de0`, yet the client still processes it via a pre-dispatch path,
  so it was only found by *replaying a real 6.x server capture*. Do not trust "world remap = no-op" as
  proof an opcode is unhandled.) Layout was pinned with a self-describing gradient packet (`body[i]=i`,
  read each value off the HUD). Level/might/will/grace/HP/MP/exp/coins now populate the always-on HUD and
  round-trip through the character store. `maxHP`/`maxMP` offsets (`[5]`/`[9]`) and the `nation` id table
  (0=Neutral 1=Koguryo 2=Buya 3=Nagnang 4=Shilla 5=Jinhan 6=Paekjae 7=Kaya) are confirmed empirically (`!hp`, `!nat`).
  The `totem` id table (0=JuJak 1=Baekho 2=HyunMoo 3=ChungRyong 4=None) is confirmed the same way (a live
  `!totem 0`-`!totem 3` sweep) and matches RTK's own `Player.getTotemName` — both fields are now decoded
  straight from the creation packet (§9) rather than compiled-in defaults.
- **Hair** is not renderable via `0x33` in 4.95 (no slot in the 7-byte form). Likely requires a
  different mechanism (stylist NPC / equipment), if at all.
- **Creation screen auto-close.** After `0x04`, our "Account created" message shows but doesn't dismiss
  the creation UI. The correct create-ack is unknown; note the login-channel `0x02` sub-dispatch is
  *not* in the game-channel handler `0x444de0` (which only guards `opcode==2` for enter-world), so the
  login-channel `0x02` responses are handled by a different state object worth RE-ing.
- **Monsters — SOLVED (2026-07-24).** The real monster spawn is **`0x07`**: `look = 0x8000 | monsterId`
  (Monster.tbl index) draws a live, animated, collidable, killable creature from Monster.epf. Full layout
  and the trace that found it are in §7.2/§11a. The combat pipeline (`Mob`, melee/spell, `0x13` HP bar + hit
  spark, death beat + delayed `0x0E` despawn, exp) runs against real monsters end-to-end, and the world now **auto-populates
  both spawn sources — the static `Spawns0` towns AND the ~21.5k dynamic hunting-map mobs from RTK's Lua spawner
  (every Mythic cave/dungeon), materialized lazily per-map — with wander AI (`0x0C`), collision, respawns, drops, and
  per-player viewport streaming** — see §11b.1.
  **Remaining monster work:** map the Monster.tbl look ids to names (the matcher exists but its mapping is
  empty — §11a.2); make aggressive mobs fight back (`0x1A`/`0x13` toward the player + HP bars); a proper RTK
  colour → client-palette mapping (§11b.1).
- **Other players / NPCs.** Rendering + movement broadcast are proven and shared-world is live (§11b). Peers
  are **not** viewport-streamed yet (only mobs are), so distant players may not appear until close; add the
  same `SyncMobs`-style streaming for players.
- **NPCs — built (§11e).** Stationary NPCs from RTK `NPCs0` render + stream like mobs, pace when RTK gives
  them a move timer, and open dialogs on click. Dialog (`0x30` text/menu/input + `0x3a` reply) is live on
  4.95; NPCs are composed from reusable abilities (Shop, Bank, Transport [stub — see below], Time, Repair).
  Buy/Sell and the bank's take/give also have spoken shortcuts (real NexusTK commands, not RTK-ported — see
  §11e's spoken shortcuts note). **Remaining:** the authentic buy/sell grid window (`0x2f`, currently menu-based); per-NPC
  quest/crafting scripts (RTK Lua); joint bank accounts; and the flat item-price data isn't tracked
  (`data/game-data/` ignored). **Transport is deliberately a stub** — RTK's `Waypoint.lua` fast-travel network
  has no evidence of existing in original 4.x/5.x NexusTK, so it isn't ported.
- **Undecoded handlers** worth probing when needed: `0x1b, 0x2f, 0x31, 0x35, 0x36, 0x39, 0x3b,
  0x42, 0x44, 0x46, 0x4a, 0x4b, 0x66, 0x67, 0x68`.

---

## 17. Reference servers & version gaps

Useful open-source NexusTK servers — **but verify every format against the 4.95 binary**, because
opcodes and layouts drift between versions:

- **github.com/jeedee/TkServer** — Ruby, **6.x**. `libs/TkGame.rb` `welcome_packet` is a readable
  self-spawn (`0x04` + `0x33`) reference. (It does **not** send the `0x02` enter-world trigger — that's
  why a direct port hangs.)
- **github.com/darkalucard/StarterTK** — C (eAthena-derived), **Mithia 7.x**. `mithia/src/map/clif.c`
  has every packet builder: `clif_parsewalk`, `clif_sendstatus`, `clif_parseattack`, `clif_sendxy`,
  `clif_sendaction`, etc. Great for *concepts*, but 7.x specifics (cipher, `0x26` walk, `0x08` stats)
  do **not** apply to 4.95.

### 17.1 github.com/unkmc/RTK-Server — Mithia/7.x, **content goldmine** ⭐ (2026-07-24)

C core (`rtk/src/`, eAthena-style) + Lua content, and — crucially — a **full production MySQL dump**:
`database/2020-09-02-21-55-01_RTK.sql.bak` (2 MB, 54 tables). **The creature-spawn packet is byte-identical
to our `0x07`** (`rtk/src/map/clif.c`): `WFIFOW(fd,+16)=SWAP16(32768+mob->look)` then
`WFIFOB(fd,+18)=mob->look_color`. So it validates our whole model: `look`=sprite, `look_color`=palette byte,
recolors = same look/different colour (`struct mobdb_data{int look, look_color}` in `map.h`; loaded from
SQL `MobLook`/`MobLookColor`; settable in Lua as `mob.lookColor`).

**Content available in the dump** (regenerate the CSVs locally with the tools below — **not committed**,
this repo is logic-only). Row counts and how much survives to 4.95 (cross-referenced against the client's
1750 `TK<N>.map` files and look-id range 0-326):

| Table | Rows | Usable in 4.95 | Gives |
|---|---|---|---|
| Mobs | 716 | 102 look-ids | name, look, `look_color`, HP(vita), exp, level, might/grace/will, min/max dmg |
| Maps | 9850 | **1387** match `TK<N>.map` | **real map names** (MapId ↔ our `0x15` mapId; `MapId 0 = Kugnae = TK000000.map ↔ TK0.map`), BGM, indoor, light, PvP, warpout flags |
| Warps | 4476 | **3060** (both ends exist) | portals `(srcMap,x,y)→(dstMap,x,y)` = world navigation |
| Spawns0 | 1175 | **1175** | *static* monster placement `(mobId, mapId, x, y)` — towns only (19 maps) |
| AreaSpawns² | 2371 | **2371** | *dynamic* hunting-map spawns `(mobId, map, count, box)` from `mobSpawnHandler.lua` — ~21.5k mobs / 767 maps (all caves/dungeons) |
| NPCs0 | 385 | **283** | NPC placement + look/`look_color` (same recolor mechanism) |
| Items | 2545 | names solid¹ | item name, type, look, icon, damage, armor, stat bonuses, prices |
| Spells | 906 | names/structure | spell & skill definitions |
| Paths | 23 | direct | class names + rank titles (Peasant→Warrior "Il san", Rogue, Mage, Poet…) |

**Ignore** (their live players' state, not content): `Accounts, Character, Inventory, Equipment, Banks,
Clans, Friends, Legends, Mail, Kills, SpellBook, Registry, Auctions, Boards, Parcels`.
**Caveats:** RTK is 7.x — ¹item look/icon ids and colours >19 reference 7.x archives (names reliable, sprite
ids/colours need 4.95 validation); stats are 7.x-balanced (structure correct, numbers a design choice); the
non-overlapping maps/warps/NPCs are 7.x-added content and are filtered out of the extracts.
**Reproduce:** clone `RTK-Server` (gitignored), then `python re/rtk_extract.py` writes the CSVs to
`data/game-data/` (also gitignored) + prints the client-overlap report; `re/rtk_analyze.py` lists all 54
tables with row counts. ²`AreaSpawns.csv` isn't an SQL table — it's generated from the Lua spawner by
`python re/extract_lua_spawns.py` (parses `mobSpawnHandler.lua`'s `handleSpawn(...)` calls). **None of
this data is committed** — this repo is logic-only; the CSVs are generated locally and kept out of git.

**Confirmed 7.x ≠ 4.95 gaps:**
- **Cipher:** 7.x = name-derived table cipher + 3 trailer bytes; 4.95 = simple NexonInc XOR, no trailer.
- **Self-walk:** 7.x = `0x26`; 4.95 = **also `0x26`** (fast-move OFF) — pre-dispatched past the no-op main-table handler to the real self-walk handler (§10.3). Fast-move ON: send nothing.
- **Stats:** 7.x = `0x08` (`OUT_STATUS`); 6.x = `0x08`; **4.95 = `0x08` too** (SOLVED — see the Stats
  packet reference). The field *layout* differs from 6.x/7.x, so decode offsets per-version.
- **Appearance:** 7.x `create_user` sends a rich equipment block (face, hairstyle, hair_color,
  face_color, coat…); 4.95's `0x33` type-0 is the 7-byte body/form/face/armor/weapon/shield form with
  **no hair**.

### 17.2 External data resources (kept OUTSIDE this repo)

This repo is **logic-only**. All game *data* lives outside it and is regenerated locally with the tools in
`re/`. Pointers so the data can always be re-fetched:

| Resource | What it provides | Get it / tool |
|---|---|---|
| **Client PAK** `NexusTK.dat` / `Inter.dat` (in the client install) | `Monster.epf/.tbl/.pal`, `Item.epf/.tbl`, sprites, `Str_*.res` | `re/pak_list.py`, `re/pak_extract.py` |
| **unkmc/RTK-Server** (`database/*.sql.bak`) | names, stats, `look`/`look_color`, maps, warps, spawns, NPCs, items, spells (§17.1) | clone repo → `re/rtk_extract.py` / `re/rtk_analyze.py` |
| **Nexus Atlas** via Wayback (pre-6.5, ~2005-10) | monster names, exp, type; monster art GIFs | `re/monster-matcher/` scrapers (Wayback CDX + `im_` raw fetch) |
| **DizzyThermal/TKViewer** | later-client DAT/DNA format docs (e.g. `MONSTER.DNA` struct) | GitHub (reference only) |
| **jeedee/TkServer** (6.x), **darkalucard/StarterTK** (7.x) | packet-builder concepts (§17) | GitHub (reference only) |
| **Client `Maps/TK<id>.map`** (in the client install) | authoritative *map existence* + cell count (headerless 4-byte cells) | `re/build_map_index.py` → `data/game-data/map_index.csv` |

### 17.3 Runtime content registry (`Server/Content.cs`) — how the data is consumed

The generated CSVs above are loaded once at startup by the static, load-once, read-only-after-load
`Content` registry (`Content.Load()` in `Program.cs`; `--selftest` exercises it offline without opening
ports). It powers the navigation commands (§11): fuzzy `FindMap`/`FindMob`/`SearchMaps`/`SearchMobs`
(score: exact < prefix < substring < subsequence), `TryWarp((map,x,y)→(map,x,y))`, and `TryMap(id)`.
Paths are env-overridable: `NEXUS_MAP_INDEX` → `map_index.csv`, `NEXUS_MOBS` → `mobs.csv`,
`NEXUS_WARPS` → `Warps.csv`.

**Map dims are client-authoritative** (`re/build_map_index.py`): every one of the client's ~1750
`TK<id>.map` files is emitted — a map the client ships is warpable, period. The `.map` is headerless, so
the only unknown is how to split `cells = filesize/4` into `(xs, ys)`. Any factor pair with the right
**product** is safe — the client reads exactly the file bytes, so a wrong split only skews row-stride (the
map looks sheared: tiles land in the wrong screen position, but nothing overruns or crashes). RTK dims
never gate existence; a 7.x-resized map (e.g. JadeSpear's Home, RTK 17×15 vs client 12×12) is kept, not
dropped. `Warps` are additionally filtered to destinations that exist in the client map set (no warping to
a map the client can't render). See §17.4 for how the split is actually chosen and why it matters — a wrong
guess here isn't cosmetic, it's the single most impactful data bug found in this repo to date.

### 17.4 Map-dimension mis-guessing — a real, high-impact bug, found & fixed (2026-07-27)

**The bug reports that led here:** a user played through the Mythic Rabbit dungeon and reported, room by
room: floating "black box" tiles in the middle of Mythic Owsla's corridor; Rabbit Leap "insanely broken"
with the same black-box pattern repeated ~14 times; a lava field in Hare Summit speckled with disconnected
patches of ordinary grass tile, looking like "tiles stacking with an offset"; and (separately) monster
sprites intermittently rendering *behind* terrain objects. Rabbit Hole, right next door in the same
dungeon, looked "perfect."

**First theory (wrong):** individual bad tile-id typos baked into the original client `.map` files —
i.e. real content corruption in `TK207.map` etc. This was investigated at length: RE'd the `.map` cell
format (already documented above — 4 bytes/cell, `[ground u16][object u16]`, top 2 ground bits =
passability), found specific cells whose ground-tile id broke an otherwise-perfectly-repeating decorative
motif (e.g. a fence-post pair that sits on tile `2125` fourteen times across the room, but on `2077` — the
room's own border-void filler tile — at exactly the two cells the user flagged as "black boxes"). This
looked like solid, self-consistent proof of a data typo, and does explain *individual anomalous cells* —
but it couldn't explain why entire *rooms* varied from "perfect" to "insanely broken," since a random
typo-rate wouldn't cluster that way room-by-room.

**Real root cause: wrong map dimensions sent to the client, not bad tile data.** The `.map` file is
headerless — nothing in it says how many columns wide it is — so `re/build_map_index.py` has to *guess*
`(xs, ys)` by factoring `cells = filesize/4` and picking the pair whose aspect ratio best matches RTK's
reference dimension for that map id (RTK is a **different, 7.x game version** with many rooms resized, so
its dims are only ever a hint, not authoritative). Our server then sends that guessed `(xs, ys)` straight
to the client in the `0x15` map-info packet (`Session.SendMapInfo(_char.Map, _char.MapXs, _char.MapYs,
...)`, sourced from `Content.Maps[id]` ⟵ `map_index.csv`). **The client's own `.map` file was never
corrupted at all** — we were just telling it to slice the correct bytes into the wrong number of columns,
which shears every row against the next by the wrong offset. A cell that's part of row *N*'s correct
sequence gets displayed as if it belonged to a different row — which looks *exactly* like "the same real
tile, just in the wrong place," because that's precisely what it is. Confirmed by checking RTK's dimension
hint against the real cell count for the 8 Mythic Rabbit rooms: **7 of 8 had no exact match** (only the
entrance, map 201, did) — RTK's own version had completely redesigned those rooms into different cell
counts, so the aspect-ratio fallback had nothing reliable to anchor to and silently produced a plausible-
looking but wrong guess for most of the dungeon. This one root cause explains every symptom reported: the
"black boxes" (a real tile read at a sheared offset), the lava-field speckling (grass-tile bytes from a
different logical row bleeding into what should be a solid lava block), and is the leading explanation for
the monster-behind-terrain reports too (the client's draw-order logic likely keys off row/column
adjacency, which a shear also breaks).

**How the true dimensions were found (`re/build_map_index.py`, wall-connectivity scoring):** for each
factor pair `(w, h)` of the cell count, read the ground-passability bit per cell and measure what fraction
of solid ("wall") cells have **zero** orthogonal solid neighbor (`isolation_fraction`). A correctly-strided
hand-built room draws walls as continuous lines/blobs — almost no isolated single wall pixels. A wrong
stride statistically isolates a large fraction of them, because it's effectively splicing together pieces
of unrelated rows. This needs no external reference data at all. Two extra guards were needed after the
naive version produced false positives on sparse/open maps:
- **Aspect-ratio cap (≤3.2:1) and a wall-density floor (≥5% of cells solid).** Very thin candidates
  (e.g. `280×10`) can spuriously score *better* than the true shape simply because there are too few rows
  for a wall cell to have a chance at a vertical neighbor — a metric artifact, not evidence.
- **A required improvement margin over the prior guess (`IMPROVE_EPS`), not just "passes a minimum bar."**
  An earlier, ungated version of this check flipped 440 of the 1750 maps — including dozens of *visually
  unrelated* zones (Coal Cells, Undergrowth, Tiger Stretch, Bull's Song, Northern Hordes, …) all converging
  on the same handful of dimension pairs (`50×18`, `25×36`, `18×50`). That convergence across unrelated
  content is itself the signature of the connectivity metric being fooled by geometry rather than genuinely
  reading room design — sparse/low-signal maps don't carry enough real evidence either way. Gating on a
  clear win over the existing guess (not just a technically-lower score) discards those and keeps the
  clean, high-confidence hits: e.g. the correct 20×20 render of Mythic Owsla is a strikingly symmetric,
  obviously hand-built room (nested rectangle, mirrored bracket walls) with **zero** isolated wall cells,
  vs. a messy, merely-plausible-looking 16×25 render under the old guess.
- **`Warps.csv` coordinate lower-bounds.** A real warp destination like `(x=15, y=53)` proves the room must
  be at least `16×54` — this resolves orientation ties (e.g. `20×60` vs `60×20`) that connectivity scoring
  alone can't distinguish, since it's symmetric under transpose. If the *only* candidates satisfying every
  warp on record is empty, the filter is dropped rather than vetoing every candidate outright — a single
  bad `Warps.csv` row (see the Owsla `warp 1953 → (29,27)` case below) shouldn't be able to block the fix.

**Result:** 8-of-8 Mythic Rabbit rooms resolved. 5 needed correction (all **live-verified in-game** after a
`!reload`, user-confirmed "perfffeeect!!!"):

| Room | Was sending | Corrected to |
|---|---|---|
| 201 Mythic Waters | 24×24 | *(already correct)* |
| 202 Golden Warren | 25×25 | *(already correct)* |
| 203 Rabbit Hole | 26×26 | *(already correct — matches the "looks perfect" report)* |
| **204 Rabbit Leap** | 24×50 | **20×60** |
| **205 Foraged Fields** | 26×45 | **30×39** |
| **206 Hare Depression** | 25×36 | **30×30** |
| **207 Mythic Owsla** | 16×25 | **20×20** |
| **208 Hare Summit** | 25×32 | **20×40** |

A full-client sweep (`re/build_map_index.py`, now using wall-connectivity as the *primary* method, RTK's
exact-cell-count match second, aspect-ratio guess as the last resort) found **~90 more maps** across the
whole game with the same class of bug — most memorably both city marketplaces (`Buya Marketplace` and
`Kugnae Marketplace`, `44×48 → 48×44`, an striking, obviously-correct symmetric plaza layout once fixed)
and several small named rooms (`Mhul's Chambers`, `Imperial Promenade`). These five plus the two
marketplaces are the highest-confidence tier — individually visually inspected, unambiguous. The remaining
~85 passed the same statistical gates (connectivity win + aspect/density/warp-bound filters) but were
**not** each individually eyeballed — a couple of spot-checks in that tier (`Undergrowth`, `Lilac Walk`)
looked *statistically* favored but visually ambiguous rather than a slam-dunk, so treat that broader batch
as "probably right, worth an in-game glance if a specific room still looks off" rather than fully proven
the way the dungeon rooms are.

**Also found and set aside, not fixed:** `Warps.csv` row **1953** (`SourceMapId=205, x=6, y=6 →
DestinationMapId=207, x=29, y=27`) has a destination that is *mathematically impossible* for any
rectangular interpretation of Owsla's 400-cell file (no factor pair of 400 has both dimensions `>29`× `>27`
simultaneously — the room simply isn't big enough in any orientation). This predates and is independent of
the dimension bug above; it's bad data inherited verbatim from the RTK-Server SQL dump this table was
extracted from (RTK's own `warps.txt` config doesn't even have a `205→207` connection at all, and the door
has no matching reverse warp either — likely an orphaned/stale row). Left unfixed pending a decision on
whether to redirect it or remove it outright (see `memory/nexustk-495-mythic-rabbit-dims.md`).

---

## 18. Key binary addresses (quick reference)

`NexusTK_local.exe`, ImageBase `0x400000`, no ASLR.

**Crypto**
| Addr | What |
|---|---|
| `0x478680` | decrypt |
| `0x478760` | encrypt (hook here to see plaintext sends) |
| `0x478850` | XOR primitive |
| `0x50211c` | key buffer (built from "NexonInc.") |
| `0x4f3358` | identity table |

**Dispatch & field readers**
| Addr | What |
|---|---|
| `0x444de0` | connection-state `0x02` handler (enter-world) |
| `0x44a090` | game-world object ctor |
| `0x44b9c0` | world dispatcher (opcodes 0x03–0x68) |
| `0x44bc80` | remap table (`remap[opcode-3]`) |
| `0x44bbd4` | jump table |
| `0x475c90` / `0x475ca0` / `0x475ce0` | read u8 / u16BE / u32BE |
| `0x44b120`, `0x45cb80` | find-entity-by-id |

**Key handlers**
| Addr | Opcode / role |
|---|---|
| `0x44faf0` | `0x04` coords/camera |
| `0x44f8b0` | `0x15` enter-map |
| `0x44fef0` | `0x33` self/entity spawn |
| `0x436120` | 7-byte appearance parser (`0x33` type 0) |
| `0x4361b0` | direct-sprite appearance parser (`0x33` type 1; still player archive) |
| `0x450a00` → `0x44dbc0` → `0x463020` → `0x462ec0` | `0x16` item/object spawn + ctor |
| `0x44fdb0` → `0x44d7d0` → `0x461a50` | **`0x07` monster-list handler → entity factory → creature ctor (vtable `0x4cd098`)** |
| `0x461c70` / `0x462950` | creature-entity draw methods (route to monster resolver when `[ent+0x178]!=0`) |
| `0x434020` / `0x4342e0` | **monster sprite resolvers** (push `"MONSTER.EPF"` `0x4f1d18` → `0x433d00` Monster.tbl → catlookup) |
| `0x435ab0` | item sprite resolver (`id+0x4000` in category `"ITEM"`) |
| `0x431020` / `0x430de0` | generic sprite lookup by `(categoryName, id)` |
| `0x430c30` | load/create a sprite category into the manager `[0x4fd2f8]` |
| `0x47b4c6` | A–Z sprite-category registry table |
| `0x4502c0` | `0x0C` move/animate |
| `0x450170` | `0x0D` speech |
| `0x4503a0` | `0x1A` action |
| `0x4508f0` | `0x13` combat damage / over-head HP bar (+ hit anim `0x8f−crit`) |
| `0x424310` | spawn placement / viewport containment check |
| `0x463380` | **nameplate/marker decoration ctor (player archive) — PATCHED to `xor eax,eax; ret 0x14` (see §8.1), always leaked one per appearance refresh** |
| `0x462050` | attach a built decoration/sprite to an entity |
| `0x425350` | conditional decoration-detach on entity destroy (gated on `[entity+0x104]!=5`, called from `0x44da40`) |
| `0x44da40` | full entity destroy: spatial-unlink + hashmap-unlink (`0x45c8f0`) + `0x425350` + dtor (peer/mob `0x33`+`0x07` path) |
| `0x44d9f0` | despawn-by-id destroy (real `0x0E` path, `0x450440`) — no self/peer distinction, destroys whatever the id hashmap resolves to |
| `0x45c830` / `0x45c8f0` / `0x45cb80` | id-keyed entity hashmap: register / remove / lookup |
| `0x44c660` | self camera/viewport refresh (called from `0x44d7d0`'s self-id branch; repositions the persistent self entity in place, no realloc) |
| `0x44d7d0` → `0x43fd80` → `0x463380` → `0x460760` | sprite build path |

---

*Maintained as facts are learned. When you discover something new — an opcode, a byte meaning, a
gotcha — add it here so the next person doesn't re-derive it.*
