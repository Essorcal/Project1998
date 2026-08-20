# NexusTK 4.x (client 4.95) — the primary target

Everything in this folder is a fact about one frozen binary: **`NexusTK.exe` build 4.95**, shipped
2001-07-09, PE32 x86, **ImageBase `0x400000`, no ASLR**. It always loads at `0x400000`, so every
`0x4xxxxx` address in these docs is a virtual address you can paste straight into a disassembler or a
Frida script without rebasing.

That binary cannot change, which is what makes this folder different from [`../common/`](../common/):
nothing here goes stale. If a statement here is wrong, it was always wrong, and the fix is to re-measure.

> The client we run is a patched 5.33 (`OTK.exe`) for some workflows and a stock 4.95 for others —
> see [`../5.x/README.md`](../5.x/README.md) and the client-targeting sections in
> [`Protocol.md`](Protocol.md). The P1998 launcher's `NexusTK.exe` is byte-identical to the local 4.95
> client, so live-measured addresses transfer between them.

## What is here

| Doc | What it answers |
|---|---|
| [Protocol.md](Protocol.md) | The reference. Every opcode, every wire format, world entry, the cipher, the client's own data tables. ~5,400 lines with a table of contents — go to the section, don't read it through. |
| [Fast-Move.md](Fast-Move.md) | Worked example of a solved RE problem: why fast-move never stuck, found by reading the stats handler. Body byte 46 of the `0x08` packet. |

## What is *not* here, and why

**Game mechanics.** Damage formulas, karma, era gating, the exp curve — those describe the *game*, not
the client, and the server implements one version of them for both clients. They live in
[`../common/`](../common/).

**5.33 differences.** [`../5.x/`](../5.x/) documents only where 5.33 diverges; it assumes this folder for
everything else. The largest divergence by far is terrain: 4.95 draws from a local `Maps\TK<id>.map`,
5.33 asks the server to stream its viewport.

## Things that bite

Collected because each one cost real time, and each one *looks* like a server bug when you hit it:

* **The `s` profile key has a hard 5-second cooldown inside the client.** First press is instant, extras
  are dropped on the floor and never reach the wire. Not fixable server-side. If profile requests seem to
  be getting lost, this is why.
* **A login password of exactly `1` crashes the client ~3 seconds in.** Use anything else.
* **The client is file-virtualized.** Writes under `Program Files` land in `VirtualStore`. When you add a
  client file, add it to *both* locations or the client will read a stale copy.
* **The view rect (17×15) is not the draw rect (19×17).** The spawn gate and the renderer disagree by two
  cells, which shows up as mobs popping out and black boxes at the edge.
* **Ground items and peers are viewport-gated.** A draw sent while the target is off-screen is lost
  forever, not queued. Both need an explicit view sync.
* **A byte-walk in Frida JS freezes the game.** Use native `Memory.scanSync` instead — see
  [`../research/Toolkit.md`](../research/Toolkit.md).

Each of these is documented properly in [`Protocol.md`](Protocol.md); the list is here so you recognise
the symptom before you spend an afternoon on it.
