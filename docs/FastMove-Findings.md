# Fast-move persistence: how the 4.95 client actually handles it (SOLVED)

Static + live reverse-engineering of the 4.95 client, 2026-08-19. The running P1998 client
(`%LOCALAPPDATA%\Project1998\game\NexusTK.exe`) is **byte-identical** to `NexusTK_local.exe`
(ImageBase 0x400000, no ASLR), so every address below is valid on the live client.

## TL;DR

The client's runtime fast-move flag is **`[state+0x451]`** (state singleton ptr `@0x4fd390`). It is
driven straight off the **`0x08` stats packet**: the stats handler copies **body byte 46** (client
`payload[47]`) verbatim into that flag on *every* stats update. Our `SendStats` never set that byte,
so it was **0 on every packet — silently forcing fast-move OFF** on every mob swing / heal / regen.
That, not a missing login mechanism, is why fast-move never "stuck".

**Fix (server-only):** `SendStats` sets `body[46] = _fastMove ? 1 : 0`, and `_fastMove` is restored
from the persisted `SettingFlags` bit 9 at `HandleArrival`. The login entry-burst stats packet then
sets the client flag before the first step, and every later stats packet reasserts it. Server and
client stay in lockstep; the hotkey `0x1b/09` toggle updates `_fastMove`, which the next stats packet
confirms.

## The runtime flag

- Game-state singleton pointer: **`0x4fd390`** (points at a heap object, e.g. `0x2aaa5f8` live).
- Runtime fast-move flag: **`[state+0x451]`** (0 = server-authoritative, 1 = client draws its own
  steps). Read by the walk path at `0x4901f9`, `0x4904d7`, `0x4972e9`, `0x4974f9`
  (`cmp byte [ecx+0x451], 1`, ecx = the same singleton).

## The stats packet drives the flag (the real mechanism)

Opcode `0x08` (our stats/HUD packet, flags `0x78`) is dispatched by the self-controller's
**network** command dispatcher `0x48eb40` (self-controller vtable `0x4cefec`, slot 19). Confirmed
live: the dispatcher fires with raw wire opcodes — `0x0c` (walk), `0x11` (turn), `0x08` (stats) — and
the `0x08` backtrace runs through the receive/message pump (`…0x48fc40 <- 0x41c473 <- 0x41c2c0 <-
recv`). Command `0x08` → handler **`0x48fc40`**, which:

- reads `payload[1]` = the flags byte (`0x78`) and, per its set bits, walks a variable number of
  following fields into `[self+0x1dc..0x1e1]` / `[self+0x17d]`;
- for flags `0x78`, ends by reading the byte at **`payload[47]`** (via `0x475c90`, which is literally
  `mov al,[arg]; ret` — a plain byte read, no decryption) and storing it: `mov [state+0x451], al`
  (`0x48fd3a`). A straight copy.

Live confirmation (`re/frida_fastmove3.py` / `frida_fastmove4.py`): the write's base equals the state
singleton, and the source offset is exactly 47.

### Packet-offset ↔ our body byte

The client's `payload` is `[opcode][decrypted body…]` (the `inc`/sequence byte is consumed as the
crypt key, not present in the payload view), so `payload[k] == our body d[k-1]`. Verified live against
six known fields (flags/nation/totem/level/maxHP/might). Therefore the fast-move source is:

```
client payload[47]  ==  SendStats body d[46]
```

`d[46]` sat between the mail-flag byte `d[45]` (client `payload[46]`) and unused padding, always 0.

## Why it never persisted (and flickered)

`[state+0x451]` is *reasserted from the stats packet constantly* — stats fire on every HP/MP change,
mob swing, heal, cast, and the 25s regen tick. With `d[46]` hard-0, each one wrote the flag back to 0:

- Hotkey toggle ON → client sets `[state+0x451]=1`, sends `0x1b/09`; the very next stats packet
  (`d[46]=0`) reset it to 0 → the toggle "didn't take".
- At login the entry-burst stats packet forced 0 → always booted server-authoritative.

The earlier "the 0x23 seed inverts the flag" theory was wrong: the `0x23` options seed only paints the
checkbox (`0x465200` writes the widget's `[+0x114]`, never `[state+0x451]`). The real clobberer was
always the stats packet.

## The fix

`Server/Session.Entity.cs` `SendStats`:

```csharp
d[46] = (byte)(FastMoveTrustToggle && _fastMove ? 1 : 0);
```

`Server/Session.cs` `HandleArrival` (before the entry burst's `SendStats`):

```csharp
_fastMove = _char.HasSetting(9);   // restore the persisted preference; the stats packet now sets the client flag
```

Lockstep after the fix:

- **Login:** bit 9 → `_fastMove` → entry-burst `d[46]` → client `[state+0x451]` set before the first
  step; server's `clientFast` branch (also `_fastMove`) sends the per-step no-scroll `0x04`. No freeze.
- **Toggle:** client flips its flag + sends `0x1b/09` → server flips `_fastMove` → next stats packet
  reasserts the same value. Consistent.
- **Safety valve:** `P1998_V495_FASTMOVE_TRUST_TOGGLE=0` forces `d[46]=0` *and* the wire-bit walk path,
  so both sides fall back to server-authoritative consistently.

## Address index

| addr | what |
|---|---|
| 0x4fd390 | game-state singleton pointer |
| state+0x451 | runtime fast-move flag (walk path reads it) |
| 0x48eb40 | self-controller **network** command dispatcher (vtable 0x4cefec slot 19); key = payload[0] = opcode |
| 0x48fc40 | opcode-0x08 (stats) handler → copies payload[47] into [state+0x451] |
| 0x48fd3a | the copy: `mov [state+0x451], al` |
| 0x475c90 | getbyte helper = `mov al,[arg]; ret` (plain read, no crypto) |
| 0x48fc00 | hotkey toggle (flips flag, sends 0x1b/09) |
| 0x464c00 | options-window apply (checkbox #8 → state+0x451); internal UI event, not a wire opcode |
| 0x4650d0 | options-window network dispatcher (payload[0] = 0x21/0x23/0x25) |
| 0x465200 | 0x23 seed handler — checkbox visuals only, never touches the flag |

Probes: `re/frida_fastmove2.py` (dispatcher is packet-driven), `frida_fastmove4.py` (offset 47),
`frida_fastmove5.py` (payload[47] == resulting flag), `frida_fastmove6.py` (verbatim-copy sentinel).
