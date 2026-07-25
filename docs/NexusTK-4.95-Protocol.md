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
11c. [Items — bag, gear, ground, combat](#11c-items--bag-gear-ground-and-combat--built-awaiting-live-495-verification)
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

### 4.1 Login channel (port 2000)

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
   `03 04 "test" 07 "dragon5" 00`.

   **Server → client `0x03` (handoff):** tell the client where the game server is. Body layout
   (as implemented and confirmed working):
   ```
   ip[3] ip[2] ip[1] ip[0]        // 4 bytes, IP octets REVERSED (e.g. 127.0.0.1 -> 01 00 00 7F)
   port(u16, big-endian)          // game server port, e.g. 2005 -> 07 D5
   17 00 09                       // (constants observed in the working handoff)
   "NexonInc."                    // the 9-byte key string, echoed
   nameLen name                   // the username
   00 01 12 11 00                 // handoff token (echoed back by the client in 0x10)
   ```
   On receiving this the client opens a new TCP connection to the game port.

### 4.2 Game channel (port 2005)

1. **The client speaks first** — do **not** send anything on connect. The client sends **`0x10`
   (arrival)** in **plaintext**:
   ```
   10 | 09 "NexonInc." | nameLen "name" | <handoff token bytes>
   ```
   Parse the username from it: `klen = body[0]; ulen = body[1+klen]; user = body[2+klen .. +ulen]`.

2. The server now drives the **world-entry burst** (§5, §6).

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
| `0x43` | Click/inspect entity | `01 entityId(u32) 00` | Clicking a character. Reply with the click-profile `0x34` (see §9.5). |
| `0x2d` | Profile key | `2d 00` (byte 0 = self) | Pressing the profile key. Reply with the self-profile `0x39` (see §9.5). |
| `0x4f` | Change profile | `picSize(u16) pic[] blurbLen(u8) blurb[] 00` | Player saved their profile edit. Persist the picture + blurb; reply with a `0x02` message. (see §9.5) |
| `0x11` | Turn / face | `side(u8) pad` | First press in a new direction turns in place (no step). Echo `Be32(id), side, 00` so the client turns (see §10.4). |
| `0x1b` | Setting toggle | `subCmd(u8) pad pad` | Client toggle. `0x07` = realm-center (F4, §10.5); `0x09` = fast-move (§10.1). |
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
does not advance the client's logical tile by itself (see §10).

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

**`0x19` — background music.**
```
type(u8) pad(u8=0) bgm(u16BE) volume(u8)
```
Handler `0x450ad0` (dispatch-table stub `0x44bb06` → real handler): `type` selects the audio backend
inside the play fn `0x4798c0` — **2 = MIDI** (the stock `1.mid`..`12.mid` in `NexusTK.snd`, played via the
single-instance MIDI player `[0x4fd3ac]`), 1 = `%03d.MP3`, 0 = a wav/sfx channel. `bgm 0` stops the music.
`volume` is a raw byte the client **log-scales**: the handler computes `dB = 2000·log10(vol/100)` (so
`vol=100` = 0 dB = nominal full, `vol>0` audible, `vol=0` silent), and the MIDI path then compresses it
further against a base at `[snd+0x270]` — so the audible range is narrow and `>100` (up to 255) is the knob
to push louder. The client dedups (`cmp bgm,[midi+8]`) so re-sending the playing track is a no-op. Send it
on map entry / change (the server picks the track per map — `Content.BgmFor`); there is no original
map→track table in the client files, so the assignments are the server's own. Songs are numbered `N.mid`;
some tracks may reference instrument samples a given install lacks, but that only manifests at high volume.

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
(the idle "Starting" frame per Monster.tbl). Combat (melee → `0x29` number → `0x0E` despawn) works against
these because they're real entities with collision. This is the `0x33`-monster path from Mithia 7.x
(`clif_cmoblook_sub`: `look + 0x8000`) — it moved to `0x07` in 4.95 (see §17). Builder: `Session.SendCreatureList`.

**`0x0E` — despawn list** (handler `0x450440`):
```
count(u8) entityId0(u32) entityId1(u32) …
```
Destroys each entity by id (`0x44d9f0`). The loop **stops early on a `0` id**, so never send id `0`.
(Note: client→server `0x0E` is *chat*; the same opcode means despawn only in the server→client direction.)

**`0x29` — floating number** (handler `0x4504b0` → `0x44e0a0`), e.g. melee damage:
```
entityId(u32) number(u8) A(u16) B(u16) C(u16)
```
The `u8` is formatted to text and popped over the entity (0–255 — fine for damage). `A` is scaled ×1000
and feeds the pop animation offset; `B`/`C` are style. Send `A=B=C=0` for a plain centered number.

**`0x1E` / `0x20`** — acks / time, as in §6.

---

## 8. The appearance system

The **`0x33` type-0 appearance is 7 bytes.** Their meaning was decoded empirically with an in-game
"look-lab" (spawn dummies with controlled appearance bytes and observe). **This is the definitive
layout:**

| Byte | Meaning | Notes |
|---|---|---|
| `[0]` | **Body / sex** | `0` = male, `1+` = female. |
| `[1]` | **Form / state** | `0`/`4` = normal human, `1` = ghost/dead, `3` = **mounted (horse)**, `5` = invisible-spell (faded), most other values = **no sprite (blank)**. |
| `[2]` | **Face** | Distinct faces; range is larger than 8 (accepts values ≥ 0x34). |
| `[3]` | **Armor / coat** | Class armors (rogue/mage/warrior…). |
| `[4]` | ? | No visible change for 0..8; likely hair/color/skin, untested at higher values. |
| `[5]` | **Weapon** | Honor Sword, Flame Blade, Electra, Steelthorn, Blood, Primogen Blade… **`0` is a REAL weapon sprite — "no weapon" is `0xFF` (`-1`).** |
| `[6]` | **Shield** | Distinct shields. **`0` is a REAL shield — "no shield" is `0xFF` (`-1`).** |

> **⚠ Weapon/shield "empty" = `0xFF`, not `0` (proven live 2026-07-25 + RTK `clif.c`).** A row-sweep of `[5]`/`[6]`
> showed every value `0..15` renders a distinct blade/shield for **both** sexes; only `-1` (byte `0xFF`) is bare
> hands. RTK sends `0xFFFF` for weapon/shield look when `!pc_isequip(slot)`. `SendSelfLook`/`ShowPlayer`/click-
> profile therefore emit the worn item's `Look` when a weapon (`Type 3`)/shield (`Type 5`) is actually equipped,
> else `0xFF` — keyed on slot occupancy (a worn weapon with `Look == 0`, e.g. Novice sword, still shows sprite 0).

**There is no hair slot** in this form. In 4.95, hair is not renderable via `0x33` (it was set by
in-game stylist NPCs). This is a hard limit of the packet, not a server bug.

**Minimum visible self:** `appearance = [sex, 0, face, 0, 0, 0, 0]`, `renderKind = 1`. Any nonzero
value in `[1]` (the form byte) risks blanking the whole sprite — that was the root cause of the
"invisible character" saga (§14).

The parser `0x436120` stores the 7 bytes into an entity sub-struct at offsets +4,+5,+7,+8,+9,+A,+B
(with a special case: if byte[3]==0 it defaults to `byte[0]!=0`). The sprite build path is
`0x44d7d0` (create-entity) → `0x43fd80` (alloc 0x17C) → `0x463380` → `0x460760`; the appearance
pointer is stashed at `[entity+0x108]`. The byte→sprite-layer resolution lives deep in the sprite
archives and was **not** cheaply static-RE-able — it was faster to decode by observation.

---

## 9. Character creation & the creation packet

Creation is two login-channel packets:

1. **`0x02` NameCheck** — `nameLen name pwLen pw 00 00 00` (name + password). Server replies `0x02`
   with payload `00` = "available / OK".
2. **`0x04` CreateAppearance** — **5 bytes**. Decoded by **controlled experiment** (create characters
   literally named after the attribute varied):

   | Byte | Meaning | Evidence |
   |---|---|---|
   | `[0]` | **Face** | chars "faceone/two/three" gave `[0]` = `00/23/34` → three distinct correct faces. |
   | `[1]` | **Gender** | `0` = male, `1` = female. Char "male" → `[1]=00`; every female → `[1]=01`. |
   | `[2]` | near-constant (nation/totem/misc) | mostly `02`. |
   | `[3]` / `[4]` | **nation / totem** | These are **stats**, not appearance (see §16). |

   Sample blobs:
   ```
   male:      55 00 02 02 00
   female:    12 01 02 01 00
   faceone:   00 00 02 02 00
   facetwo:   23 00 01 02 00
   facethree: 34 00 01 02 00
   ```

**Creation → render mapping** (server-side): render `appearance[0]` (sex) = creation `[1]`;
render `appearance[2]` (face) = creation `[0]`. Gender and face then persist and render correctly.
Hair is unmapped (no render slot). Nation/totem are stats, deferred until the stats/HUD packet is
solved.

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
`2d 00` (`body[0]==0` = self). Reply with `0x39`, the stats/legend summary (from 7.x `clif_mystaytus`,
confirmed byte-for-byte against a real 6.x capture: AC=99, class "Peasant", legend "Born in Hyul 31,
Winter"):
```
[AC u8][dam u8][hit u8]
[clan : u8 len + bytes]           (len 0 = clanless)
[clanTitle : u8 len + bytes]
[title : u8 len + bytes]
[spouse : u8 len + bytes]
[group u8][TNL u32BE]
[className : u8 len + bytes]
14 × equip slot (10 bytes each, all zero = empty)
[exchange u8]
[00 u8][legendCount u16BE]
legendCount × { icon u8, color u8, textLen u8, text }
```

**Click-profile — request `0x43` → reply `0x34`.** Clicking a character sends `43 01 id(u32) 00`. Reply
with `0x34`, the public two-page view (portrait + gear on page 1; nation + picture + writable blurb on
page 2). **This layout was reverse-engineered from the client's OWN parser `0x48b6a0`** (profile-page
widget, vtable `0x4cee5c`, method +0x5c) — the 7.x `clif_clickonplayer` is a *different, larger* shape
and does not fit 4.95. Body:
```
5 header strings (u8 len + bytes): title, clan, clanTitle, class, name   (order confirmed live)
appearance: tag u8 (=0) + 7 look bytes                (same 7-byte form as 0x33 type-0 → correct sprite)
3 × portrait graphic id (u16BE)                       (feed FACE.EPF; 0 = default)
gear/item list (u8 len + text)                        PAGE 1; item names TAB-separated (client → CR)
scalar (u32BE)                                        (unknown; 0)
look-selector A (u8), look-selector B (u8)            (0xff = none)
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
- 4.95's click popup has **no totem slot** (`TOTEM.EPF` is unreferenced in the client) — totem only
  appears on the HUD/self-profile, not here.

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

**Attack:** client sends `0x13` (bare `13 00`) on spacebar. **Reply with `0x1A` (action), type=1** —
**not** `0x13`. (The `0x13` receive-handler `0x4508f0` computes animation `0x8f − a`; with `a=0` that
is `0x8f` = the **death** animation, which makes the character flash "dead". This bit us — §14.)

**Emotes (`:` wheel).** Client sends `0x1d` = `idx(u8) 00`. The emote plays as an action:
`type = idx + 11`, broadcast as `0x1A` to the emoter **and every peer on the map** (RTK
`clif_parseemotion`: `sendaction(&bl, RFIFOB(5)+11, 0x4E, 0)`). So `:` `l` = `idx 0x0b → type 22 =
dance`. The index sits at `dec[0]` — **not** `dec[1]`; 4.95 has no ordinal byte after the opcode, so
RTK's 6.x `RFIFOB(5)` offset is one byte later than ours (the same shift seen on every 4.95 handler).
The dance/emote sound rides along with the client's action sprite; no separate sound packet is sent.

**Weapon.** `Character.Weapon` renders in the player's `0x33` type-0 appearance slot `[5]` (the sword/blade
slot — see §8), persists in the store, and drives the melee damage bonus. This works (it draws on the
player, which renders). With no weapon the space-bar attack plays the empty-handed *throw* animation/sound.

**Combat (server-authoritative) — ⚠ WORKS SERVER-SIDE ONLY, no client mob yet.** The server owns mob HP
(`Shared/Mob.cs`, tracked in `Session._mobs`). On `0x13`: send the player swing (`0x1A`), then resolve melee
against the mob on the tile *in front* of the player (facing tracked from the last walk `0x32`); apply
`might + weapon bonus`, pop a `0x29` number over the mob, and on death `0x0E` (despawn) + exp via a fresh
`0x08`. **This targets real `0x07` monsters (§7.2, §11a) — visible, with collision, killable.** Verified
end-to-end live: spawn → melee → damage number → despawn → "You defeated" + exp.

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
`!spawn [lookId] [hp]` (a pack), `!kill`, `!weapon <n>`. The `0x16` item commands (`!mob`, `!mobrow`) are
kept for item/object discovery.

**Navigation & content commands** (data-driven, backed by the `Content` registry — §17.3): `!warp <name|id>
[x y]` (fuzzy-match a map by name or id, optionally with coords, and enter it), `!maps [query]` /
`!mobs [query]` (fuzzy list maps / mobs), `!summon <name|id>` (spawn a mob from the registry by name/id),
`!rabbit` (the spawn→wander→kill MVP: look 21, wanders on a background task via `0x0C`, cleans up on death).
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
data itself is **kept out of this repo** (logic-only server; the generated CSVs are gitignored). RTK look-ids
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
`0x0D` (real chat only — `!`-commands stay self-only via `SendLog`), attack swing `0x1A`, damage number
`0x29`, spawn `0x07`, despawn `0x0E`. The moving player's OWN client is driven by the self-walk modes
(§10), so it's excluded from the `0x0C` broadcast.

**Shared mobs + combat.** `!summon` / `!rabbit` spawn into the world (`SummonWorldMob`); the debug lab
(`!cre`/`!mob`/`!crow`/look-lab) stays session-local. `HandleAttack` hits world mobs first: `World.TryDamage`
applies damage **under the world lock** so two players can't double-kill, the number + death despawn are
broadcast to all, and exp goes to the killer. A single background `World.Tick` (~600 ms) wanders every
world mob (leashed to spawn, avoids player tiles + `Obj != 0`) and broadcasts each hop.

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

**Known limitation (MVP).** No view-distance streaming: the `0x33`/`0x07` **viewport gate** (§14) silently
skips entities outside the observer's camera rect, so two players far apart on a large map may not see each
other until one walks close and a re-sync (move/refresh) redraws them. Fine when players are near each
other (the common case). Mobs persist on a map after everyone leaves (they belong to the map, not a
session); `!kill` clears the current map's world mobs for everyone.

---

## 11c. Items — bag, gear, ground, and combat ⏳ (built; awaiting live 4.95 verification)

The full item system: an item registry, a per-character bag + worn gear (persisted), floor items, and the
pickup/drop/throw/use/equip handlers. Opcodes + wire layouts were translated from RTK 7.x `clif.c`
(`clif_sendadditem` / `clif_senddelitem` / `clif_equipit` / `clif_unequipit` and the `parse*` recv path).
Builders/handlers live in `Session.cs` (`SendAddItem`/`SendDelItem`/`SendEquip`/`SendUnequip`,
`HandlePickup`/`HandleDropItem`/`HandleThrow`/`HandleUseItem`/`HandleUnequip`/`HandleDropGold`); floor
items live in `World.cs` (`DropItem`/`PickUp`/`ItemsOn`, id pool `500000+`); the registry is `Content.Items`
(`ItemDef`) loaded from the gitignored `re/rtk-data/Items.csv` (2545 items — id, name, type, icon, look,
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
| `0x37` | equip-window entry | `equipType(u8) icon(u16) iconColor(u8) [name u8len+txt] [baseName u8len+txt] dura(u32) 00 00` |
| `0x38` | unequip-window | `spot(u8) 00` |
| `0x07` | ground item (§7.2) | floor items go through the **`0x07` static base-object** path (below), NOT `0x16` — graphic = item's `Icon` (Item.epf frame), encoded via `IconWire` |

`equipType`/`spot` wire bytes (client `clif_getequiptype`): WEAP=1 ARMOR=2 SHIELD=3 HELM=4 NECKLACE=6
LEFT=7 RIGHT=8 BOOTS=13 MANTLE=14 COAT=16 SUBLEFT=20 SUBRIGHT=21 FACEACC=22 CROWN=23. Item `Type` (ITM_*)
maps to a gear slot for `Type ∈ 3..16` (EQ index = `Type-3`).

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

**Item action animations (0x1A) — each item verb plays its bend-down pose + sound, on self AND peers.**
Every item handler broadcasts a `0x1A` action (§13, builder `Session.SendAction` / peer `ActionOver`) so the
character visibly stoops and the client plays the baked-in sound. The `(type, time)` pairs are RTK's
(`clif.c`): **pickup = `(4, 40)`** (RTK `clif_parsegetitem`), **drop = `(5, 20)`** (`clif_parsedropitem` — a
*distinct* pose from pickup), **throw = `(2, 20)`**, **eat = `(8, 40)`**. `sound` is 0 (the action sprite
carries its own sound; a non-zero 4th arg would be a separate sound id). Ordering matches RTK: pickup plays
the action **unconditionally** (even on an empty tile — the crouch fires on the keypress), while drop plays it
only **after** the `NoDrop`/valid-slot guard passes. `<` (pick-up-all) plays the action once, then loops the
tile until empty.

**Throw collision — SOLVED (2026-07-25).** `HandleThrow` walks the item **tile-by-tile** from the player in
the faced direction (0=N 1=E 2=S 3=W, capped at 3 tiles) and stops at the last *passable* tile, so items
never come to rest on a wall or an unreachable tile. Passability uses the **same two sources the client's own
collision uses** (§12): a tile is blocked if the **object layer** is non-zero **or** the **ground
passability flag** is set. That flag is the **top 2 bits of the ground `u16`** — value **`3` = solid**, `0` =
walkable (`1`/`2` never occur in real maps); `Session.Blocked` = `map.Obj(x,y) != 0 || map.Pass(x,y) != 0`.
The earlier bug ("lands on a tile I can't walk to") was `Blocked` checking only the object layer and ignoring
the ground flag — real maps (TK0/Kugnae) gate thousands of cells by the ground flag alone. Enforcement is
`NEXUS_PASS` (default on; set `0` to disable); the same `Blocked` gates player walk/turn.

**GM commands:** `!items [filter]` (browse registry), `!item <name/id> [amount]` (summon into bag),
`!clearinv` (reset bag + gear).

---

## 12. Maps

Map files live in the client's install at `Maps\TK<mapId>.map` (e.g. `Maps\TK32.map`). They are:

- **Headerless.** `width × height` cells, **4 bytes per cell**, row-major.
- Each cell = `groundTile(u16, little-endian) objectTile(u16, little-endian)`.
- Example: `TK32` = 33×33 = 4356 bytes.

The server's `0x15` (enter-map) tells the client which map id + dimensions to load; the client reads
the `.map` file itself. Pick a map that actually has content: `TK27` is a uniform "void" tile (renders
black); `TK32` (33×33, ~180 distinct tiles, ~270 objects) renders a real area.

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
| `0x06` | `0x44fb90` | (client→server walk+view variant) |
| `0x07` | `0x44fdb0` | ✓ **creature/monster list** (server→client): `count(u16)` + 12B entries `X Y id look color dir`; `look=0x8000\|monsterId` → Monster.epf. §7.2/§11a |
| `0x0b` | `0x44fb70` | **no-op** |
| `0x0c` | `0x4502c0` | ✓ move / animate entity |
| `0x0d` | `0x450170` | ✓ over-head speech |
| `0x0e` | `0x450440` | ✓ **despawn list** (server→client): `count(u8)` + `id(u32)`× (client→server = chat) |
| `0x0f` | — | ⏳ **add item to bag slot** (server→client) — §11c. (client→server = magic) |
| `0x10` | — | ⏳ **remove item from bag slot** (server→client) — §11c |
| `0x11` | `0x450350` | entity + 1 byte |
| `0x12` | `0x4509a0` | entity + 2 bytes |
| `0x13` | `0x4508f0` | ✓ attack-recv (anim `0x8f − a`; **death at a=0**) |
| `0x15` | `0x44f8b0` | ✓ enter-map |
| `0x16` | `0x450a00` | **ground ITEM/object spawn** (draws from Item.epf `"I"`): `+5 gfx(u16) +7 id(u32) +0xb X/Y …`. **NOT a monster** — §7.2, §11a. **Walk projectile → invisible at rest; the server uses `0x07` static objects for floor items instead — §11c.** |
| `0x1a` | `0x4503a0` | ✓ action (attack/sit/…) |
| `0x1b` | `0x450830` | ? |
| `0x1d` | `0x450db0` | entity + 1 byte |
| `0x1e` | — | ✓ ack |
| `0x1f` | `0x450f40` | 3-state set (thresholds 0x0b/0x63/0x65 → `[world+0x401]`) |
| `0x20` | `0x44f820` | ✓ time-of-day |
| `0x21` | `0x450f90` | UI window (`0x174`) |
| `0x26` | `0x44fb80` (main) → **`0x4903d0`** (pre-dispatch) | **Self-walk — WORKS.** Main-table handler is a no-op, but `0x26` is pre-dispatched via the self-entity vtable (`+0x38` @ `0x4cf038` → `0x48eb40` → `handlerB`). This is the 4.95 self-walk primitive (§10.3). |
| `0x29` | `0x4504b0` | ✓ **floating number** over entity: `id(u32) number(u8) A/B/C(u16)`; the u8 is the text (0–255) |
| `0x2e` | `0x450580` | list: name + looped u16 items (skills?) |
| `0x2f` | `0x44f490` | ? |
| `0x30` | `0x44f530` | ? |
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
- **Use `0x26` for self-walk in 4.95** — its main-table handler is a no-op, but it is pre-dispatched to the real self-walk handler (§10.3). A no-op in the dispatch table is NOT proof an opcode is unhandled (cf. `0x08`).

**Combat**
- **Never reply to `0x13` (attack) with `0x13`.** Its handler plays anim `0x8f − a`; `a=0` → `0x8f` =
  **death** animation ("character flashes dead"). Reply with `0x1A` action type=1 instead.

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
- **Hair** is not renderable via `0x33` in 4.95 (no slot in the 7-byte form). Likely requires a
  different mechanism (stylist NPC / equipment), if at all.
- **Creation screen auto-close.** After `0x04`, our "Account created" message shows but doesn't dismiss
  the creation UI. The correct create-ack is unknown; note the login-channel `0x02` sub-dispatch is
  *not* in the game-channel handler `0x444de0` (which only guards `opcode==2` for enter-world), so the
  login-channel `0x02` responses are handled by a different state object worth RE-ing.
- **Monsters — SOLVED (2026-07-24).** The real monster spawn is **`0x07`**: `look = 0x8000 | monsterId`
  (Monster.tbl index) draws a live, animated, collidable, killable creature from Monster.epf. Full layout
  and the trace that found it are in §7.2/§11a. The combat pipeline (`Mob`, melee, `0x29` damage, `0x0E`
  despawn, exp) now runs against real monsters end-to-end. **Next monster work:** map the Monster.tbl look
  ids to names (which id = squirrel/rabbit/etc.), give monsters server-side AI/movement (`0x0C`), and have
  them fight back (`0x1A`/`0x13` toward the player + HP bars).
- **Other players / NPCs.** Rendering is already proven (§15). Remaining: handle the client's view-rect
  refresh (`0x06`/`0x11`), spawn nearby entities on entry, and broadcast movement (`0x0C`) between
  sessions.
- **Undecoded handlers** worth probing when needed: `0x1b, 0x2f, 0x30, 0x31, 0x35, 0x36, 0x39, 0x3b,
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
| Spawns0 | 1175 | **1175** | monster placement `(mobId, mapId, x, y)` |
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
`re/rtk-data/` (also gitignored) + prints the client-overlap report; `re/rtk_analyze.py` lists all 54
tables with row counts. **None of this data is committed** — this repo is logic-only; the CSVs are
generated locally and kept out of git.

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
| **Client `Maps/TK<id>.map`** (in the client install) | authoritative *map existence* + cell count (headerless 4-byte cells) | `re/build_map_index.py` → `re/rtk-data/map_index.csv` |

### 17.3 Runtime content registry (`Server/Content.cs`) — how the data is consumed

The generated CSVs above are loaded once at startup by the static, load-once, read-only-after-load
`Content` registry (`Content.Load()` in `Program.cs`; `--selftest` exercises it offline without opening
ports). It powers the navigation commands (§11): fuzzy `FindMap`/`FindMob`/`SearchMaps`/`SearchMobs`
(score: exact < prefix < substring < subsequence), `TryWarp((map,x,y)→(map,x,y))`, and `TryMap(id)`.
Paths are env-overridable: `NEXUS_MAP_INDEX` → `map_index.csv`, `NEXUS_MOBS` → `rtk_mobs.csv`,
`NEXUS_WARPS` → `Warps.csv`.

**Map dims are client-authoritative** (`re/build_map_index.py`): every one of the client's ~1750
`TK<id>.map` files is emitted — a map the client ships is warpable, period. The `.map` is headerless, so
the only unknown is how to split `cells = filesize/4` into `(xs, ys)`. RTK's `rtkmaps/Accepted/<MapFile>`
(first 4 bytes = `xs,ys` big-endian) merely *informs* the split when the product matches; otherwise the
closest-aspect (or square) factor pair is picked. Any factor pair with the right **product** is safe — the
client reads exactly the file bytes, so a wrong split only skews row-stride (map looks sheared), it never
overruns or crashes. RTK dims never gate existence; a 7.x-resized map (e.g. JadeSpear's Home, RTK 17×15 vs
client 12×12) is kept, not dropped. `Warps` are additionally filtered to destinations that exist in the
client map set (no warping to a map the client can't render).

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
| `0x4508f0` | `0x13` attack-recv (death anim at a=0) |
| `0x424310` | spawn placement / viewport containment check |
| `0x44d7d0` → `0x43fd80` → `0x463380` → `0x460760` | sprite build path |

---

*Maintained as facts are learned. When you discover something new — an opcode, a byte meaning, a
gotcha — add it here so the next person doesn't re-derive it.*
