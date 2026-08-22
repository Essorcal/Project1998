# 5.33 wire divergences — where the 5.x protocol differs from 4.95

Everything here was established against the **5.33 client binary** (`NextAeon533\NexusTK.exe`, build
5.3.3.384, image base `0x400000`, non-ASLR) either by disassembly or by live probe. Where the two
disagreed, the live observation won and the disassembly reading was corrected.

`docs/4.x/Protocol.md` remains the base protocol. This file records only the **deltas**, plus the two
methods that produced them, plus the mistakes that cost time — so they are not repeated.

---

## 0. Read this first: the client has FOUR dispatchers, not one

This is the single biggest trap in 5.x work and it invalidated a day of conclusions before it was
caught.

The client offers every server packet to **several dispatchers in turn** until one accepts it. Each is
a virtual method on a different class, and all share one shape:

```
esi = [arg0 + 0xc]        ; packet body pointer
al  = body[0]             ; opcode
eax = al - BIAS
cmp eax, RANGE ; ja default
dl  = byteMap[eax]
jmp   ptrTable[dl]
```

| dispatcher | VA | bias | opcodes | byteMap | ptrTable | default case |
|---|---|---|---|---|---|---|
| world | `0x463320` | 3 | `0x03..0x6A` | `0x463d44` | `0x463c8c` | `0x463c77` |
| ui/item | `0x4d5f80` | 4 | `0x04..0x51` | `0x4d6254` | `0x4d620c` | `0x4d6002` |
| ui-b (login/select) | `0x4e13d0` | 4 | `0x04..0x26` | `0x4e14c0` | `0x4e14ac` | `0x4e14a0` |
| ui-c | `0x4e6b70` | — | — | — | — | not a jump table; tests one byte |

Every default case is `xor al, al; ret` — **no log, no error, no reply**. A dispatcher that does not
own an opcode returns "not handled" and the next one gets it. Live proof of the chain:

```
0x07 no   ui-b    (0x4e13d0)
0x07 no   ui/item (0x4d5f80)
0x07 ok   world   (0x463320)
```

**Who owns what** (the split that matters):

- **world** — `0x06` terrain, `0x07` spawn, `0x0c` move, `0x0d` speech, `0x0e` despawn, `0x11` turn,
  `0x13` mob hp, `0x15` map info, `0x19` media, `0x1a` action, `0x29` effect, `0x2f` menu,
  `0x30` npc dialog, `0x33` appearance, `0x34` click profile, `0x39` self profile, `0x3b` mail, `0x66`.
- **ui/item** — `0x05` entity id, `0x08` **stats/HUD**, `0x0a` system text, `0x0f` **add item/spell**,
  `0x10` **remove item/spell**, `0x17` **add spell**, plus `0x04 0x0b 0x12 0x15 0x18 0x24 0x26 0x36
  0x3e 0x4e 0x51`.
- **ui-b** — `0x04 0x08 0x0b 0x26` only.

> ### The mistake — do not repeat it
> A probe was written that hooked **only** `0x463320` and reported `handled / DROPPED` per packet. It
> declared `0x08` stats, `0x0f`/`0x10` items, `0x17` spells and `0x37` bag "ALL DROPPED", and a whole
> theory was built on top ("the spellbook is never populated, so nothing can be cast"). All of those
> are owned by `0x4d5f80` and were being handled the entire time. The probe was measuring one of four
> dispatchers and the label said "dropped" rather than "not handled here".
>
> `re/frida_dispatch_533.py` and `re/grammar_533.py` now hook **all** of them, and an opcode is only
> reported ignored when *every* dispatcher refuses it. Note also that our own
> `Server/Session.Spells.cs` comment already described this ("handled by the client's SECONDARY
> dispatcher") for 4.95 — the repo knew before the probe did.

Still refused by all three table dispatchers, and therefore genuinely unresolved: **`0x37`** (bag
array) and **`0x38`**. Treat as open, not as settled — there may be a dispatcher not yet found.

---

## 1. The method that works: the zero probe

Four layouts were recovered this way and it should be the default approach for a fifth.

Send the opcode with an **all-zero body**. Every length-prefixed string is then empty and every count
is zero, so nothing in the record has variable width — and the offsets the client reads *are* the
minimal record, one entry per field, in order, with its exact width.

```
@pkt file probe39          # game-data/packets/probe39.txt
python re/grammar_533.py --attach --op 39
```

Corollaries worth internalising:

- **Structure is not meaning.** A zero probe gives you the shape; it cannot tell you which box on
  screen a string lands in. Follow it with a *marker* probe (`probe39b.txt`) that gives every field a
  distinct value, or with a live `@look533`-style sweep.
- **A real body is useless for this.** Once the client mis-parses one field, every offset after it is
  meaningless. That is why reads at offset 146 of a 130-byte packet showed up — the parser had run off
  the end. Only the *caller* addresses stay informative, which is why the tracer records them.
- **Give the probe a generous tail.** A short body makes a real field look like an over-read.
- **An empty string looks exactly like a loose byte.** The tracer only calls a field a *string* when
  the parser copies it through the stack helper; a parser that hands the body straight to
  `MultiByteToWideChar` shows up as a bare `u8`. This cost a wrong `0x39` layout — see §4.

---

## 2. The appearance record — 11 bytes, not 7

Carried by **`0x33`** (map look), **`0x1d`** (in-place patch), **`0x30`** (dialog paperdoll) and
**`0x34`** (view panel). All four resolve to the same parser, so they must agree:

| | 4.95 | 5.33 |
|---|---|---|
| parser | `sub_436120`, returns **7** | `sub_449880`, returns **11** |

Slot meanings were established by **sweeping each byte live against the rendered sprite** (`@look533
<i> <v>`). The disassembly gives widths and read order; only the sweep says what a field *draws*, and
where they disagreed the sweep won.

| wire | 4.95 | 5.33 | how we know |
|---|---|---|---|
| `[0]` | body/sex | body/sex | swept: 0 male, 1 female |
| `[1]` | form/state | form/state | swept: 0/4 normal, 1 dead, 2+5 faded, 3 mounted, 6+ no sprite |
| `[2]` | face/head | **face/head** | swept — same slot in both |
| `[3]` | armor | **HAIR COLOUR** *(new)* | swept |
| `[4]` | colour ramp | **armor/coat** | swept: 0 unequipped, 1+ armors |
| `[5]` | weapon | **armor colour ramp** | swept (needs a coat on to show) |
| `[6..7]` | — | **WEAPON, u16 BE, flat `ItmLook` space** | swept: `[6]=50` → spear, `[6]=100` → bow |
| `[8]` | — | unknown | swept, no visible effect |
| `[9]` | — | shield | swept |
| `[10]` | — | shield colour? | swept: also moves the shield |

Two independent confirmations, worth keeping because they pin the mapping without relying on ordering:

- **The armor slot.** Both parsers carry the same defaulting rule — 4.95 `if (obj+8 == 0) obj+8 =
  (obj+4 != 0)`, 5.33 `if (obj+a == 0) obj+a = (obj+6 != 0)`. Same (armor, body) pair, so `wire[4]` is
  armor and `wire[0]` is body/sex regardless of anything else.
- **The weapon family.** 5.33 wants the **unpacked** flat look (family = value/10000), not 4.95's
  packed byte. `[6]=50` → u16 12800 → family 1 → spear. `[6]=100` → u16 25600 → family 2 → bow. This
  is why `0` drew a phantom sword on every unarmed character: flat-space 0 is sword art 0, a *real*
  sprite. "No weapon" is `0xFFFF`.

Implementation: `Session.AppearanceRecord` (pure + static), `Session.Weapon533`. Both shapes are
pinned by `Tests/ClientVersionWireTests.cs`.

---

## 3. `0x0E` despawn — one id, no count

5.33's case body is four instructions:

```
inc esi                     ; body offset 0
push esi / call 0x4a1250    ; ONE u32 BE
test eax,eax / je done      ; id 0 = no-op
push eax / call 0x466550    ; destroy that entity
```

**No count byte, no loop.** 4.95 is `count(u8)` then that many ids. Sending the 4.95 shape makes the
client read `count<<24 | id>>8` — a nonexistent entity — so nothing despawns and it still returns
"handled". Symptom: mobs stayed after death and ground items after pickup, and Ctrl+R "fixed" it only
because a map reload wipes every entity. A multi-id despawn is **N packets** here.

---

## 4. `0x39` self profile — five equipment cells

Parser `sub_49cdd0`. The record, after the zero probe was corrected by disassembly:

```
u8 u8 u8             AC / Dam / Hit
u8+str               clan            -> widget +0x112, drawn at y=48
u8+str               clan title      -> widget +0x312, drawn at y=65
u8+str               title           -> widget +0x512, drawn at y=82
u8+str               party box       -> widget +0x932, the page-2 list control
u8                   grouped
u32BE                TNL
u8+str               class title     -> widget +0x712
(u16,u8) x5                          <- FIVE (icon, colour) cells
u8+str               buff box        -> widget +0xb32, the page-1 list control
u8                   exchange
u8 + records         legend count, then { icon u8, colour u8, len u8, text }
```

**31 bytes minimum against 4.95's 22.** The one break is the equipment cells: 4.95 sends **three bare
u16 icons**, 5.33 reads **five (u16 icon + u8 colour) cells** — 15 bytes against 6. Everything after
shifted, which emptied the gear boxes, buff box, profile text and legend list simultaneously.

**The string region is 4.95's, unchanged** — and this is where the zero probe lied. It read the region
as *three strings plus a loose `u8`*, so the server sent a filler zero byte in that slot and the last
three strings each landed one slot late: clan title in the title line, title in the party box, and the
pane's title line blank. Widths still matched (an empty string and a zero byte are the same byte), so
nothing downstream broke and the bug looked like "5.33 has no title field".

> ### Zero-probe corollary — a field is invisible if the parser doesn't route it through the helper
> The tracer reports a *string* when the parser copies the body bytes through the stack helper
> `0x46cbc0`. Four of `0x39`'s six strings do that. The second one does **not**: it goes straight from
> the body into `MultiByteToWideChar` at `0x49ceb1`, writing into the widget at `+0x312`. All the
> tracer saw was its length byte, so a string with an empty value is indistinguishable from a bare
> `u8`. **A zero probe cannot tell an empty string from a loose byte** — confirm any "loose byte"
> against the parser, or with a marker probe that gives the field a non-empty value.

Confirmed by the same disassembly: the legend record kept its 4.95 `icon/colour/len/text` shape (loop
at `0x49d32d`), and the draw method `sub_49d590` paints the first three strings as stacked lines at
y=48/65/82 in wire order. The page-2 list control's own copy loop (`0x4c0170`) accepts both `0x0d` and
`0x0a` as breaks, so the CR-joined party roster renders as lines here exactly as on 4.95.

Implementation: `Session.SendSelfProfile533`.

**Still open:** which equipment slots the fourth and fifth cells are for.

---

## 5. `0x34` view profile — same break, plus a paperdoll

Parser `sub_4d19c0`. Minimal record from the zero probe:

```
[0..4]  five string lengths      title, clan, clan title, class, name
[5]     appearance tag
[6..16] the 11-byte APPEARANCE record   <- the paperdoll
[17..31] FIVE (u16 icon, u8 colour) cells
[32]    gear list string
[33..36] u32BE                   the TARGET'S ENTITY ID -- see below
[37][38][39] flags               grouped / exchange / nation
[40..41] u16                     profile-picture size
[42][43][44] u8 x3
```

Same five-cell divergence as `0x39`. Fixed; both profile panels confirmed working.

**`[33..36]` is NOT a divergence and NOT an unknown — it is the profile target's entity id, and 4.95
wants it too** (RE'd 2026-08-21). `sub_4d19c0` reads it with the `u32BE` helper `0x4a1250` and parks it
at `+0xa88`; the window's **Exchange** button (`0x4d2cb2`, reached from the hit-test jump table) does
`mov ebx,[ebx+0xa88]` and sends it straight back as the `0x4a` body `00 targetId(u32BE) 00`. 4.95 is the
same shape one field-width earlier in the packet (`0x48b6a0` → `+0xb24` → button `0x48c7c7` → builder
`0x48cd00`). This server sent a hardcoded `0` there until 2026-08-21, which is why exchange did nothing
on **either** client — both were sending a well-formed `0x4a` naming player id 0. See
`docs/4.x/Protocol.md` §9.5 / §11l.

Open: `[43]`/`[44]` — 4.95 has blurb-length then legend count and nothing more, so 5.33 has one extra
`u8` in that tail whose position (before or after the legend count) is unresolved.

---

## 5b. `0x42` exchange window — one extra byte, in the row packet only

The native trade window (`0x42` out, `0x4a` in — see `docs/4.x/Protocol.md` §11l) is the same design on both
clients: a trampoline that only handles `body[0]==0` and builds the window (4.95 `0x451120` → ctor
`0x420e80`, object `0x288`; 5.33 `0x463971` → ctor `0x42b300`, object `0x264`), then the window's own packet
handler taking sub-types 1..5 (4.95 `0x4216a0`, table `0x421704`; 5.33 `0x42be60`, table `0x42bfb4`).

**Only sub-type 2 (add/replace a list row) diverges, and it is the usual icon-colour byte:**

```
02 side(u8) rowKey(u8) icon(u16BE) [colour(u8) <- 5.33 ONLY] nameLen(u8) name[]
```

4.95's `0x4218a0` reads the name length at body[5], straight after the icon. 5.33's `0x42c240` reads a colour
byte there first and the length at body[6] — the same extra byte 5.33 adds to `0x0F`, `0x39` and `0x34`. Feed
4.95 the colour byte and it takes the colour index as the name length.

**Everything else in the family is byte-identical.** Sub-0 open (`00 targetId(u32BE) nameLen(u8) name[]
level(u16BE)` — both ctors read the id at [1..4] and a length-prefixed string at [5], and **neither reads the
trailing level**), sub-1 ask-amount, sub-3 gold (`03 side(u8) gold(u32BE)`; 5.33 `0x42bebb` matches 4.95
`0x421980` instruction for instruction), sub-4 message and sub-5 confirm (len at [3], text at [4]). The whole
inbound `0x4a` side matches too, including the amount being a `u8` — 5.33 clamps it to 255 at `0x42d489`.

---

## 6. Confirmed NON-divergences

Recording these matters as much as the deltas — each one is a hypothesis that looked obvious and was
wrong, and re-testing them is wasted effort.

- **`0x29` effect animation is byte-identical.** `entityId(u32) effectId(u8) A(u16) B(u16) C(u16)`,
  handler `sub_469ae0`, plus a special case for `effectId == 0x86`. Spell graphics failing is **not** a
  layout problem.
- **`0x19` media is dispatched** by the world dispatcher and reports handled. *Superseded in part:* the
  MIDI channel really is identical, but the **mp3** channel has its own body here — see §6.8.
- **Panel switching is client-side.** The client has four mutually-exclusive panels behind one
  switcher, `sub_435790(this, index, body)` — index 0 = character sheet, 1 = view profile, 2 =
  inventory. Equipping an item makes the client switch to panel 2 **by itself**: a backtrace at the
  moment of the switch is pure input/message-pump (`0x403d5e → 0x429110 → 0x425xxx`) with **no packet
  in flight**. *Partially superseded:* the switch is client-local, but the pane-WIPE that made panels
  look closed was server-caused after all — deferred off the stats packet's `body[46]`, see §6.5. The
  "no packet in flight" reading was correct and misleading at once: the packet only set state; the
  main loop acted on it later.
- **A "closed" panel is usually an EMPTY panel.** `sub_435790` returns immediately when the requested
  index equals the current one and is not 0 or 1:
  ```
  cmp esi, [edi+0x14] ; jne switch
  cmp esi, 1 ; je switch
  test esi, esi ; jne RETURN      <- same index, not 0/1 -> no-op
  ```
  So a panel that renders blank still counts as open, `i` will not reopen it, and cycling to another
  pane and back is the only way in. Diagnose "the window closed" as "the window has no contents"
  first.

> ### The other mistake — do not repeat it
> `0x43` arrives from the 5.33 client repeatedly with a constant body (`01 00 00 00 01 00`, a self id).
> Because replying `0x34` visibly evicted the character sheet, the reply was suppressed for V533. That
> was wrong: it broke clicking yourself, and `s` then only opened the sheet after `i` had been pressed
> first — i.e. **the client waits on that answer and its panel state stalls without one**. The reply is
> required; what was wrong was the *body*. Reverted. When a client sends something repeatedly, the
> question is what it wants back, not how to stop answering.

---

## 6.5 `0x08` stats `body[46]` — the pane-wiping "refresh" (SOLVED 2026-08-21)

**Symptom:** on 5.33, *any* action that refreshes stats — equip, unequip, eat, cast, even standing
still through a regen tick — visually wiped whatever pane was open (inventory, self view, click
profile), while the panel manager still counted it open, so `i` could not reopen the inventory until
the user cycled to another pane and back. The server log showed the client **spontaneously
re-requesting the full 19x17 map rect (`0x05`) at its unchanged position** milliseconds after each
stats burst — a self-initiated view rebuild, the same thing Ctrl+R does.

**Root cause:** the 5.33 `0x08` case (`ui/item` dispatcher case `0x4d5fa9` → parser `sub_4d8160`)
reads `flags` at parser offset 1, *skips* the flag-selected stat blocks (bit6 = 0x17-byte block,
bit5/bit4 = 8-byte blocks, bit3 = 4 HUD bytes at parser 0x29..0x2c — note it then skips TWO bytes,
so 4.95's `body[44]`/`body[45]` mail cell is unread on 5.33), and finally reads **one u8 at parser
offset 0x2f = our `body[46]`**, storing it in the runtime-state singleton at `[g+0x46c]`. On 4.95
that byte is the fast-move runtime flag, copied verbatim — so `SendStats` asserts it from `_fastMove`
(default ON = 1) on every refresh. On 5.33 the same store exists **plus a side effect**: if the byte
is nonzero and the pending-move gate `[state+0x6878]` is set, the parser calls `sub_4857c0` — a
deferred move-commit that re-derives self coordinates and triggers scene redraw (`sub_461570`,
vtable+0x70, `sub_485ee0`, `sub_486030`). That rebuild is what re-requested the map and repainted
over the open pane.

Two more structural notes from `sub_4d8160`:
- It ends `xor al, al` — deliberately reports **not handled** so the *next* dispatcher (ui-b
  `0x4e13d0`, which owns `0x04 0x08 0x0b 0x26`) also parses the same packet. "handled" flags cannot
  be trusted as ownership on this client.
- The mail/parcel HUD cell has MOVED: 5.33 reads 4 HUD bytes at parser 0x29..0x2c (our
  `body[40..43]`) and never touches `body[45]`. The 4.95 mail arrow byte lands in a hole. (Open: which
  of the four is mail.)

**Fix:** `SendStats` sends `body[46] = 0` for V533 (fast-move was never negotiated with 5.33; its
walk machinery is left alone). 4.95 path unchanged.

**A dead end worth recording: `flags` bit7 is NOT a pane gate — it is the MOUNT flag.** The
`body[46]=0` fix did not stop the wipe, and a first guess — that `flags` bit7 (stored by `sub_4d8160`
into `[obj+0x1f0]`, with bit2 forcing pose `[obj+0x163]=3`) gated the pane — was **wrong**. Setting
`flags=0xF8` made an *unmounted* character walk at horse speed: `[obj+0x1f0]` is the riding flag and
`[obj+0x163]=3` is the ridingHorse pose (appearance byte `[1]==3`). Reverted to `0x78`. `sub_4d8160`
is the entity's mount/pose/status consumer of `0x08`, not a HUD or pane path.

**SOLVED (2026-08-21): a phantom totem "change" on every `0x08`.** The bisection pinned it to `0x08`
(`@mailflag 45 0` — a lone stats packet — WIPED; `@dye`/`@snd`/`probe10`/`@mtx` did not). Live
tracing the per-field notify (`sub_4e1e90`, hooked in `re/grammar_533.py`) then showed exactly ONE
field notify per stats packet: **widget 2 = totem**, every single time, followed by the full
sidebar redraw (a `widget 0..12` enumeration) that painted over the open pane. Cause: our totem table
uses `4 = "None"`, but 5.33's `0x08` parser CLAMPS the totem field to `0..3` and stores the clamped
value. Sending `4` meant the client stored `3` yet kept reading `4` on every packet → "totem changed!"
forever → rebuild → wipe. Same rebuild is what regressed the click-profile page-1 background while it
was open. **Fix:** totem is a 0..3 crest that is never unset, so a character sitting at the legacy `4`
default is a data bug — clamp it into range at login (`Session` arrival), stop `ApplyAppearance` from
re-applying an out-of-range blob byte (`b[3] <= 3`), clamp the `@totem` setter to 0..3, and keep
`TotemWire()` (maps any stray >3 to `0xFF`, 5.33's stable "no totem" sentinel) as the wire-boundary
backstop. A valid 0..3 totem round-trips on both clients, so no notify fires on an unchanged resend.

> **DEAD END — do not repeat.** Before finding the totem clamp I read `sub_4e2450` as a 6.x/7.x SFLAG
> composite whose fields sit one byte later than 4.95 (HP at body[25], grace at body[18]) and shipped a
> `SendStats533` at those offsets. Off-by-one: the parser reads `flags` at `body[1]`, and our payload
> `d[]` has the opcode stripped (`SendMap` prepends it), so `d[k] == body[k+1]`. Converting the parser's
> `body` offsets to payload offsets gives `d[0]=flags, d[24]=HP, d[17]=grace` — identical to 4.95, which
> `@stg` pinned live. The shifted build produced 1.6-billion HP and grace 0. **5.33 and 4.95 SHARE the
> `0x08` field layout.** Reverted; `SendStats` is one function for both. Also a dead end: `flags` bit7
> is the MOUNT flag (setting `0xF8` made an unmounted char walk at horse speed), not a pane gate.

Also retired en route: §6's "equip switches panels by itself" — `sub_4d5090` is the in-game KEY
dispatcher (two `char`-indexed jump tables; `0x4d53a8` is the `i`-key case, `0x4d5810` the `w` case),
so those "client-local" switches were the player's own keystrokes, and `0x4e6b70` (the "fourth
dispatcher") is another key handler, not a packet dispatcher.

---

## 6.6 Options menu (`0x1b` / `0x23`) — don't re-seed 5.33

The `0x1b` option toggles the 5.33 client SENDS are byte-identical to 4.95 (live-captured 2026-08-21):
`0x04` Wisdom/advice, `0x05` Magic, `0x06` Weather, `0x09` Fast-move, `0x0D` Sound — same sub-commands,
same 2-byte `<sub> 00` shape, and the server flips the right `SettingFlags` bit for each. **Inbound
needs no change.**

The bug was OUTBOUND. 4.95 needs a `0x23` re-seed after every synced toggle to keep the options
window's stored byte in sync (§9.5), so `HandleSetting` calls `SendOptions()` on `0x04/0x05/0x06` and
on F10-open. But `0x23` is **not handled by any of 5.33's three receive dispatchers** (all resolve it
to the shared no-op `0x4d6002`/`0x463c77`) — it must have a separate options-window handler as on 4.95,
and our 4.95-format seed reaches it wrong, visibly flipping the NEIGHBOURING radios. Symptom: toggling
Magic also flipped Weather/Wisdom/Sound. Fix: `SendOptions()` early-returns for V533 — 5.33 tracks its
own radio state client-side, so sending nothing keeps server and client in phase. If 5.33's real seed
opcode/format is found later, seed through that instead.

**Likely tie-in to spell FX.** "Believe in magic" (`0x05`) and "Hear sounds" (`0x0D`) are CLIENT-side
render gates — the client decides whether to draw the `0x29` effect / play the cast sound based on
those radios, regardless of what the server sends. The intermingling made it impossible to reliably
leave Magic/Sound ON, which plausibly explains "no spell animations or sounds." Re-test spell FX with
those two boxes ON after this fix before assuming a separate `0x29`/sound divergence.

---

## 6.7 `0x2e` world map — a GRAPH, not a dot list (SOLVED 2026-08-21)

**Symptom:** sending the working 4.95 world-map packet to 5.33 killed the client outright —
`Win32Error: not enough memory resources`, then a crash. As with the 4.95 framing bug before it, this
was **our packet**, not a client bug.

The 5.33 parser is `sub_469c80` (world dispatcher, case `0x4636f9`). Its header and per-entry prefix
are byte-identical to 4.95's `sub_450580`, but **every entry ends with a link list 4.95 does not
have**:

```
u8      bgNameLen           <- payload[0] IS the length; still no leading kind byte
char[]  bgName              <- <= 23 chars (the wide buffer is 0x18)
u8      count n             <- <= 255 (the parser's stack arrays cap at 256 entries)
u8      originIndex         <- 4.95's "unexplained byte". NOT unexplained here; see below
n x {
  u16BE  dotX               <- label is centred horizontally on this
  u16BE  dotY               <- label TOP is dotY + 8 (4.95 centres vertically instead)
  u8     nameLen
  char[] name               <- <= 63 chars (the node struct's inline name field is 0x80 bytes)
  u16BE  mapIdHi            <- read, then DROPPED by the wrapper at 0x467870
  u16BE  mapIdLo            <- the only half the client keeps, and what it echoes back
  u16BE  destX
  u16BE  destY
  u16BE  linkCount          <-- NEW IN 5.33
  u16BE  linkIndex[linkCount]   <-- NEW IN 5.33
}
```

Each `linkIndex` sets bit `i*n + j` in an `n x n` adjacency bitset (`0x469fa8`). Without the two new
fields the client reads the **next entry's `dotX`** as `linkCount` — a several-hundred-iteration inner
loop that runs off the end of the packet and ORs bits at unbounded indexes into a heap block sized for
`n*n` bits. That is the out-of-memory crash, exactly.

**The graph is load-bearing, not decoration.** `sub_4e6360` BFSes it from the origin node and stores
predecessors at `[obj+0x1e0]`; the draw loop `sub_4e51a0` **dims any node the BFS did not reach**
(`WMICON.EPF` frame 0 + colour `0x80/0x86` instead of frame 1 + `0x8f/0x06`); and clicking a lit node
walks an animated marker hop-by-hop along the edges (`sub_4e5990`) before any reply is sent. Edges are
**directed** — the BFS only follows `i -> j` — so a one-way list leaves half the map grey. We send a
complete graph (every node links to every other), which lights every destination and puts it one hop
away. Model real routes here if the marker should ever follow roads.

**The byte after the count is the ORIGIN NODE INDEX.** `sub_4e4b80` stores it at `[obj+0x174]` /
`[obj+0x178]` and uses it three ways: BFS root, "you are here" icon, and the centre of the scrolling
camera. It is dereferenced (`node[originIndex]`, stride `0x94`) with **no bounds check**, so an
out-of-range value is an OOB read on the client — clamp it server-side.

**The reply is narrower too.** All five send sites (`0x4e5cd3` click-confirm, `0x4e5daf`
marker-arrived, `0x4e6960` clicked-own-node, `0x4e6b00`, `0x4e6be9` ESC) emit the same 7-byte frame:

| | body |
|---|---|
| 4.95 | `mapId(u32BE) x(u16BE) y(u16BE) 00` |
| 5.33 | `mapId(u16BE) x(u16BE) y(u16BE)` |

The trailing NUL those builders write at `buf[7]` is **not transmitted** — the send call passes length
7. And cancel is explicit on 5.33: the world-map key table at `0x4e6fb0` maps `VK_ESCAPE` (`0x1b`, and
F2) to `0x4e6bcf`, which sends `node[originIndex]`'s own map/x/y. That is 4.95's "ESC replies with
entry 0" behaviour made deliberate; putting the origin first (as the 4.95 ESC-cancel fix already does)
satisfies both clients with one code path.

**Two-click confirm.** `[obj+0xa8]` is the hovered/next node and `[obj+0xac]` the selected one; the
reply goes out only when they match (`0x4e5c90`). A first click selects and starts the marker walking;
arriving — or clicking the same node again — confirms.

**Assets — the "new asset system" is smaller than it looks.** The background is `<bgName>.EPF` plus
`<bgName>.PAL` (name literals `0x555678` / `0x5556b4`), and the dots come from a new sheet,
`WMICON.EPF` (frame 0 = plain, frame 1 = current/reachable), which ships in `NexusTK.dat`. The
background itself did **not** change: `field10.epf` is **byte-identical** between 4.95's `Inter.dat`
and 5.33's `NInt.dat` (md5 `c29f30071f0cc0cb3929abfaf61537dc`), so `game-data/WorldMapDests.csv` dot
pixels carry straight over. One caveat to eyeball: 4.95 centres the label box on `DotX/DotY`, 5.33
centres only horizontally and hangs the text **below** the anchor (`0x4e52e9`: `top = y + 8`), so
5.33 labels sit about a half-line lower.

The camera is new as well — 5.33 scrolls and clamps to `[320, bgW-320] x [240, bgH-240]`, so a
background **larger** than 640x480 is supported. `field10` is exactly 640x480, so it pins and behaves
like 4.95.

### The icons are NOT per-destination — asked and answered

`wmicon.epf` (5.33 `NexusTK.dat`, 2098 bytes) holds **exactly two frames, and both are the same hut**:
frame 0 plain (27x28), frame 1 with a glow outline (33x34). Its TOC entry layout is
`top(i16) left(i16) bottom(i16) right(i16) pixOff(u32) stencilOff(u32)` and `tocOff` is relative to
**+12**, not to 0 — `re/render_items.py`'s Item.epf reading does not apply here (`w*h == stencilOff -
pixOff` checks out for both frames under this layout and under nothing else).

The ctor loads exactly those two (`0x4e4e88`, `0x4e4e9f`) into `[obj+0x10c]` / `[obj+0x130]`, each with
a draw offset of `(-20, -34)` (`0x4e4eac`) — which is why the icon sits **above** the label. The draw
loop `sub_4e51a0` then picks between them on state alone:

| condition | sprite | text colour | box colour |
|---|---|---|---|
| node == origin (or, while walking, == the marker's node) | frame 1 (glow) | `0x8f` | `4` (red) |
| reachable in the BFS | frame 0 | `0x8f` | `6` (tan) |
| not reachable | frame 0 | `0x80` | `0x86` (dim) |

**No wire field selects a sprite.** `sub_469c80` reads no icon id, and the one candidate — the u16 the
parser stores in the array at `[ebp-0x588]` (read 6, 4.95's map-id high half) — is **dropped by the
wrapper at `0x467870`**, which forwards 8 of its 9 args and skips exactly that one. Per-destination
icons would therefore need a client patch, not a server change. What *is* per-destination: position,
label text, and lit-vs-dim via the graph.

**The human figure on the map is the player, not a destination icon.** After the node loop,
`sub_4e51a0` calls `sub_4e6110(markerX, markerY)`, which draws the sprite object at `[obj+0x17c]` — and
the ctor fills that (`0x4e4d30`) via `sub_484c20`, a flat **16-byte copy from `[player+0x15c]`**. That
is exactly the struct the 5.33 appearance parser `sub_449880` writes (`[esi+0x00..0x0e]`), so the
travelling marker is the player's own avatar, equipment and all. It is drawn at the animated marker
position `[obj+0x16c]/[obj+0x170]`, which starts at `node[originIndex]` and then walks the graph on a
click — while the glow stays on `[obj+0x174]` (the origin) because the walking flag `[obj+0x168]` is 0.
That is why a screenshot can show the avatar standing on one town while a *different* town glows.

Implementation: `Session.WorldMapBody` (pure + static), pinned for both clients by
`Tests/ClientVersionWireTests.cs`.

---

## 6.8 `0x19` music — a flat type-1 body, and PLAYLISTS the client walks itself

`0x19` reaches the same world dispatcher on both clients and the **MIDI** channel is identical. The
**mp3** channel is not: 4.95 falls through to a TLV tail whose `mode` byte decides play/loop/stop
(`docs/4.x/Protocol.md` §`0x19`), and 5.33 has no tail at all.

5.33's handler is `sub_46a420` (world case `0x19` @ `0x463870`). `body[1]` selects the channel exactly
as on 4.95 — 0 sfx / 1 mp3 / 2 midi — and the type-1 arm reads a flat record and stops:

```
19 | 01 | 00 | id(u16BE @+3) | fallback(u16BE @+5) | vol(u8 @+7)
```

`body[2]` is read into a local and never used on this arm. There is no mode, no tag, no skip count.
Sending 4.95's TLV here puts the **volume byte where the fallback id belongs**, and the id the client
then tries to open is garbage.

### The id is not a filename — it is a lookup

The handler hands `(id, fallback, vol, 0)` to the resolver `sub_4a6360`, which `sprintf`s **three**
candidates from the wide format strings at `0x5541dc` / `0x5541f0` / `0x554204` and takes the first that
exists in `Mus000.dat` (`0x41d1d0` → the resource manager at `[0x55bfc0]+4`, an archive lookup, not a
filesystem one):

| candidate | what it is | how it plays |
|---|---|---|
| `%08d.LST` | ten track ids, `\r\n`-separated, count on line 1 | starts at entry **1**, loop flag forced to **1** |
| `%08d.LSR` | byte-identical format | starts at `rand % count + 1`, loop flag **1** |
| `%08d.MP3` | one song | loop flag is the packet's 4th arg, which the handler **hardcodes to 0** |

So the stock archive's `801-814 / 870-873 / 880-883 / 890-893` are the ordered lists and
`901-914 / 970-973 / 980-983 / 990-993` their shuffled twins (same ten ids, `.lsr` instead of `.lst`),
and the 25 bare ids are single songs.

**The consequence for map music:** a single mp3 **plays once and stops**, on both clients — 4.95's
mode-1 branch `0x463ae8` passes the same hardcoded 0. Background music must therefore be a playlist id.
`Content.SelfTest` and `Tests/ContentSmokeTests.EveryFiveXMapPickIsALoopingPlaylist` assert that every
zone's `Track5x` is one, because the failure is silent: the area just goes quiet after one song.

`fallback` is reached only when none of the three resolve. Id 0 matches nothing and `sub_4a5f80` stops
and returns on a 0 id, so `id = fallback = 0` **is** the mp3 stop on 5.33 — there is no mode-0 to use.

Two gates gate the whole thing, and both look like "the packet was ignored": `[[0x55bfc8]+0x1564]` must
be 0 for the type-1 arm to run at all, and `[obj+8]` (the client's own sound setting) must be non-zero.

### MIDI is capped on 5.33 too

`sub_475250` carries the same `cmp si, 0xd / jge bail` as 4.95 (`0x475286` vs `0x4588b4`), and 5.33's
`Snd.dat` carries the same `1.mid`..`12.mid`. The old soundtrack is therefore playable on both clients,
which is why it stays the default and why `@music new` is the opt-in rather than the other way round.

Implementation: `Session.MusicBody` / `Session.MusicStopBody` (pure + static, pinned for both clients by
`Tests/ClientVersionWireTests.cs`); the per-character choice is `Character.NewMusic`, the tables are
`game-data/MusicTracks.csv` (`Set` column) and `MapBgm.csv` (`Track5x` column). 4.95 is refused the new
set outright — it has the mp3 engine but ships none of the files, so switching there is silence, not a
different soundtrack.

---

## 6.9 `0x2f` sub-kind 4 buy grid — the row carries an icon colour (SOLVED 2026-08-21)

Same divergence as `0x0F`/`0x37`, in the one place it had not been applied: **a buy-grid row carries an
icon-colour byte between the icon and the price on 5.x, and does not on 4.95.**

```
4.95 row:  icon(u16BE)                price(u32BE) nameLen(u8) name  textLen(u8) text
5.33 row:  icon(u16BE) iconColor(u8)  price(u32BE) nameLen(u8) name  textLen(u8) text
```

RTK `clif_buydialog` (`rtk/src/map/clif.c:12432-12447`) writes `WFIFOW(icon); WFIFOB(iconColor);
len += 3;` and agrees. The 4.95 shape is the odd one out, for the same reason it is everywhere else:
4.95 has **no colour channel in the item graphics path at all** and folds the palette into the frame
(`ItemDef.ClientIcon`, chosen by `Session.IconOf`).

Sub-kinds **3** (amount) and **5** (sell) do **not** diverge — neither has an icon field. The sell grid
puts only bag slots on the wire and the client draws each row's icon and name out of its own inventory,
so it was never affected. Nor is the shared prefix: the prompt rendered correctly the whole time, which
is what localises the break to the row loop.

### How it read on screen, and why that is the proof

Omitting the byte does not tint a row wrong — it shifts the entire rest of the packet. The client reads
the price one byte late, so the price swallows the **name-length** byte, and the **first letter of the
name** becomes the length:

| screen | should have been | decode |
|---|---|---|
| `pple♦Peasant level 0ÀL` — 2565 | Apple — 10 | price = `00 00 0A` + nameLen `05` = `0x0A05` = 2565; nameLen = `'A'` = 65 |
| `corn♦Peasant level 0ÀÖ` — 51461 | Acorn — 201 held | count = `00 00 C9` + nameLen `05` = `0xC905` = 51461; nameLen = `'A'` = 65 |

A 65-byte name then eats the blurb, the following row's `0xC0` icon high byte (rendered `À`, since every
icon is `IconWire` = frame + 49152 = `0xC0xx`) and everything after it — which is the whole trailing
mess, and why only the first row looked *nearly* right and every later row was pure junk.

Worth keeping: the numbers decode **exactly**, which is what turned "the text is garbled" into a
one-byte diagnosis without a probe. When a length-prefixed field looks shifted, check whether the field
*before* it equals `realValue << 8 | nextField` — that arithmetic names the missing byte's position.

Implementation: `Session.BuyGridRowBody` (pure + static), pinned for both clients by
`Tests/ClientVersionWireTests.cs`. Reached from the shop (`DlgBuy`) and the bank withdraw grid
(`BankWithdrawItem`), which is why both showed it and the deposit grid did not.

---

## 7. Open questions

| question | status |
|---|---|
| ~~`0x0F` add-item body shape~~ | **SOLVED.** `u8 slot · u16BE icon · u8 iconColor · u8 nameLen+name · u32BE amount · u8 …` — parser `sub_4d8290`. 4.95's SECOND (base-name) string does not exist on 5.33; sending it shifted everything after the name. Note a zero body is useless here — slot 0 makes the parser bail before reading anything. |
| `0x17` add-spell body shape | same dispatcher, same likely divergence; blocks the spellbook, hence no casting, hence no `0x29`. |
| `0x37` bag array, `0x38` | refused by all three table dispatchers. Genuinely unhandled, or a dispatcher not yet found? |
| `0x39` `[4]`, dropped string, extra cells, legend shape | see §4 |
| `0x34` `[43]`/`[44]` tail | see §5 |
| appearance `[8]`, `[10]` | see §2 |
| ~~`0x2e` world map crashes 5.33~~ | **SOLVED.** Per-entry link list + origin index; reply is 6 body bytes, not 8. See §6.7. |
| ~~`0x2f` buy grid draws garbled names on 5.33~~ | **SOLVED.** The row has an `iconColor` u8 between icon and price on 5.x. See §6.9. |
| world-map edges: real routes? | We send a complete graph. The client supports arbitrary directed edges and animates the marker along them — nobody has checked what retail actually sent. |

---

## 8. Tooling

| tool | what it answers |
|---|---|
| `re/frida_probe_533.py` | every byte in/out (WSOCK32), plus every file the client opens. Ciphertext. |
| `re/decode_probe_533.py` | decrypts + reassembles that log into frames. TCP splits frames; decode per stream, not per line. |
| `re/frida_dispatch_533.py` | per-packet handled/ignored across **all four** dispatchers, with plaintext bodies. |
| `re/grammar_533.py` | the field-by-field read order = the grammar. Hooks the five stream primitives, brackets on all dispatchers. |
| `re/frida_window_533.py` | panel switches with the previous index, the opcode in flight, and a **backtrace**. |
| `re/dispatch_533.py` | static: opcode→handler table, `--handlers`, `--parsers N` (find a parser by reader-call density). |
| `re/disx.py --533` | disassemble / `callxref` / `xref` / `str` against the 5.33 binary. |
| `@look533 <i> <v>` | pin one appearance byte live and redraw — how §2 was filled in. |
| `@pkt file <name>` | fire a hand-authored probe body (`game-data/packets/probe39.txt`, `probe39b.txt`, `probe34.txt`). |

The five stream primitives everything is built on:

| VA | reads |
|---|---|
| `0x4a1200(p)` | u8, non-advancing |
| `0x4a1210(p)` | u16 BE, non-advancing |
| `0x4a1250(p)` | u32 BE, non-advancing |
| `0x4a3e30(base,&cur)` | u8, cursor += 1 |
| `0x4a3e50(base,&cur)` | u16 BE, cursor += 2 |
| `0x4a3ec0(base,&cur)` | u32 BE, cursor += 4 — **found late**; until it was, every field read through it was invisible and showed as an unexplained gap |

---

## 9. Rule for this work

**4.95 must not change.** Every divergence above is behind `if (_ver == ClientVersion.V533)` with the
4.95 branch byte-identical to what it always was, and the two shapes are pinned by
`Tests/ClientVersionWireTests.cs` so a future 5.x edit that moves a 4.95 byte fails a test rather than
a player's client.
