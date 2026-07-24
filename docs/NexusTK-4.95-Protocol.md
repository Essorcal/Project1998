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
| `0x43` | Click/inspect entity | `01 entityId(u32) 00` | Clicking a character. Reply with the click-profile `0x34` (see §9.5). |
| `0x2d` | Profile key | `2d 00` (byte 0 = self) | Pressing the profile key. Reply with the self-profile `0x39` (see §9.5). |
| `0x4f` | Change profile | `picSize(u16) pic[] blurbLen(u8) blurb[] 00` | Player saved their profile edit. Persist the picture + blurb; reply with a `0x02` message. (see §9.5) |
| `0x11` | (view/heartbeat) | `01 00` / `02 00` | Sent periodically; no reply needed for basic play. |
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
Handler `0x4503a0`: plays the action (client scales `time` ×10). `type`: 0=stand, 1=attack, 2=throw,
3=shot, 4=sit, 6=magic, 8=eat.

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
- `look < 0x8000` or `> 0xbfff` → descriptor type 2 → `0x462ec0` (vtable `0x4cd118`) = the item/object base
  (the invisible `0x16` path). Don't use for monsters.

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
| `[5]` | **Weapon** | Honor Sword, Flame Blade, Electra, Steelthorn, Blood, Primogen Blade… |
| `[6]` | **Shield** | Distinct shields. |

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

**The client is server-authoritative for self-walk, and 4.95's model differs from 7.x.**

Per step the client sends **`0x32`** (or **`0x06`** every few steps — handle both identically):
```
dir(u8) stepCounter(u8) X(u16) Y(u16) pad
```
Direction map: **0 = North (y−1), 1 = East (x+1), 2 = South (y+1), 3 = West (x−1).**

The server computes the new tile and replies with **two** packets, in order:

1. **`0x0C`** (move/animate) — starts the walk animation. *Animation only* — it does **not** advance
   the client's logical tile.
2. **`0x04`** (coords) — **commits** the step (handler calls `0x44b140` on the self, advancing the
   logical tile) **and** scrolls the camera. Send `X, Y, anchorX, anchorY, 00` with the anchor held
   constant at the spawn's screen tile so the camera follows the player.

Sending only `0x0C` makes the character animate one step and then get stuck — you **must** follow with
`0x04`. This 2-packet-per-step dance is the 4.95 way.

> **Version gap:** 7.x (Mithia) drives self-walk with opcode `0x26`. In 4.95, `0x26`'s handler
> (`0x44fb80`) is a **no-op** — do not use it. Normal walk speed constant is 80.

---

## 11. Speech & actions

**Speech:** client sends `0x0E` = `chatType(u8) msgLen(u8) msg`. Echo it back as `0x0D` =
`chatType(u8) entityId(u32) msgLen(u8) msg` attributed to the speaker's entity id → bubble appears
over their head.

**Attack:** client sends `0x13` (bare `13 00`) on spacebar. **Reply with `0x1A` (action), type=1** —
**not** `0x13`. (The `0x13` receive-handler `0x4508f0` computes animation `0x8f − a`; with `a=0` that
is `0x8f` = the **death** animation, which makes the character flash "dead". This bit us — §14.)

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

**Commands** in `Session.HandleChat`: `!cre <lookId> [hp]` (one real monster in front, killable),
`!crow <lo> <hi> [step]` (row sweep of the Monster.tbl look space), `!spawn [lookId] [hp]` (a pack),
`!kill`, `!weapon <n>`. The `0x16` item commands (`!mob`, `!mobrow`) are kept for item/object discovery.

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
| `0x11` | `0x450350` | entity + 1 byte |
| `0x12` | `0x4509a0` | entity + 2 bytes |
| `0x13` | `0x4508f0` | ✓ attack-recv (anim `0x8f − a`; **death at a=0**) |
| `0x15` | `0x44f8b0` | ✓ enter-map |
| `0x16` | `0x450a00` | ✓ **ground ITEM/object spawn** (draws from Item.epf `"I"`): `+5 gfx(u16) +7 id(u32) +0xb X/Y …`. **NOT a monster** — §7.2, §11a |
| `0x1a` | `0x4503a0` | ✓ action (attack/sit/…) |
| `0x1b` | `0x450830` | ? |
| `0x1d` | `0x450db0` | entity + 1 byte |
| `0x1e` | — | ✓ ack |
| `0x1f` | `0x450f40` | 3-state set (thresholds 0x0b/0x63/0x65 → `[world+0x401]`) |
| `0x20` | `0x44f820` | ✓ time-of-day |
| `0x21` | `0x450f90` | UI window (`0x174`) |
| `0x26` | `0x44fb80` | **no-op** (7.x self-walk; dead in 4.95) |
| `0x29` | `0x4504b0` | ✓ **floating number** over entity: `id(u32) number(u8) A/B/C(u16)`; the u8 is the text (0–255) |
| `0x2e` | `0x450580` | list: name + looped u16 items (skills?) |
| `0x2f` | `0x44f490` | ? |
| `0x30` | `0x44f530` | ? |
| `0x31` | `0x451080` | ? |
| `0x33` | `0x44fef0` | ✓ self/entity spawn (appearance) |
| `0x34` | `0x450270` | ✓ **click-profile** window (UI `0x198`); body parsed by widget `0x48b6a0`. See §9.5 |
| `0x35` | `0x450890` | ? |
| `0x36` | `0x4515d0` | ? |
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
  `"M"` category draw — §11a.
- **`0x16` divide-by-zero crash:** its position interpolation `idiv`s by `|X-X'|+|Y-Y'|`, so a "stationary"
  spawn with from-tile == to-tile crashes the client (`arithmetic` exception at `0x4631f7`). Always send the
  from-tile ≥1 away.

**Movement**
- **One `0x0C` only animates; it does not advance the tile.** You must also send `0x04` to commit the
  step, or the character animates once and freezes. (§10)
- Don't use `0x26` for self-walk in 4.95 — it's a no-op. (7.x uses it.)

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

**Confirmed 7.x ≠ 4.95 gaps:**
- **Cipher:** 7.x = name-derived table cipher + 3 trailer bytes; 4.95 = simple NexonInc XOR, no trailer.
- **Self-walk:** 7.x = `0x26`; 4.95 = `0x0C` + `0x04` (its `0x26` is a no-op).
- **Stats:** 7.x = `0x08` (`OUT_STATUS`); 6.x = `0x08`; **4.95 = `0x08` too** (SOLVED — see the Stats
  packet reference). The field *layout* differs from 6.x/7.x, so decode offsets per-version.
- **Appearance:** 7.x `create_user` sends a rich equipment block (face, hairstyle, hair_color,
  face_color, coat…); 4.95's `0x33` type-0 is the 7-byte body/form/face/armor/weapon/shield form with
  **no hair**.

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
