# Locking

Every lock in the game process, what it guards, and the order they may be taken in.

This file exists because the order was never written down. #29 made `Session` an actor and asserted a
two-lock order — session monitor, then `World._lock` — in a process that has **six** lock families. Two
deadlocks followed immediately, both with locks the design had not considered, and both found by review
rather than by running the server (they need two players and a specific interleave, so no amount of
playing would reliably have found them). The lesson is the file: a new lock is a change to this table.

---

## The order

Outermost first. A thread may hold locks from several rows, but **only ever in this order**.

| # | Lock | Scope | Guards |
|---|---|---|---|
| 1 | **Lua gate** — `Session.EnterScriptGate()` | one, process-wide | every MoonSharp `Script`: `LuaVerbHost` (spell + item verbs), `NpcScript`, `MobScript` |
| 2 | **Session state monitor** — `Session.EnterState()` / `WithState` | one per player | `_buffs`, `_statusFlags`, `_char` and everything hanging off it (inventory, equipment, quests, legends) |
| 3 | **World lock** — `World._lock` | one, process-wide | the maps: player lists, mob lists, ground items, spawn rosters, traps, weather |
| 3 | **Session viewport lock** — `Session.EnterView()` | one per player | `_shownMobs`, `_shownPeers`, `_shownItems`, `_edge*`, the trap/warp markers |
| 4 | **Session write gate** — `Session._writeGate` | one per player | the character row's DB write, and its ordering |

Rows 3 are siblings: nothing takes both, and neither may be held while entering row 1 or 2.

Two consequences of the gate's slow path, both easy to miss. A **contended** cast is two critical sections
with a gap, not one — the monitor is dropped while waiting — so `Handle`'s "a packet is atomic" holds for
everything except that case. And a **paired** body (`WithStatePair`: the trade finalizer, the wedding) must
never enter the gate, because the gap it would open is exactly the tear the pair form exists to prevent.

Between two session monitors (row 2), the order is **ascending `Session.StateRank`**. A handler runs under
its own monitor and can reach a peer — A casting at B while B casts at A is the standard cycle, and on a PvP
map two players can produce it by accident in the same instant. A total order makes it unrepresentable.

## What enforces it

Not comments. Each rule has an assert, and each assert has a test in `Tests/SessionActorTests.cs`.

* `Session.EnterState` asserts `!World.HoldsWorldLock` and `!Session.HoldsAnyViewLock`.
* `Session.EnterScriptGate` asserts `!Session.HoldsAnyViewLock`.
* `Session.EnterState` sorts by `StateRank`; a descending nested acquisition drops what it holds, retakes in
  order, and restores the caller's holdings.
* The viewport lock is *counted*, not merely locked (`_viewDepth`), because the lock that breaks the rule
  belongs to a **different** session and `Monitor.IsEntered` cannot see it. That is why `lock (_viewLock)`
  is spelled `using (EnterView())` everywhere.

**One rule is convention, not an assert, and it is named here so that stays visible:** nothing may enter the
Lua gate while holding `World._lock` (row 3 under row 1). `MobScript.Fire` is the only path that could —
the tick's AI hooks — and it carries the rule in its own doc comment; `MobScript.Has`, which *is* called
under `_lock` by `World.QueueHook`, is deliberately lock-free so the hot path never reaches the gate.
Asserting it properly needs `World._lock` to be counted the way `_viewLock` now is, which means routing its
~60 `lock (_lock)` sites through a guard — filed as **#90**, for the next time that file is open anyway.

## The two shapes that keep coming back

**Decide under the lock, act outside it.** `World.Broadcast` snapshots the recipient list under `_lock` and
sends outside it. `ReconcilePeer` and `SyncGroundItems` decide what to draw under `_viewLock` and build the
packet outside it. `World.Tick` queues every session-facing call (`hits`, `mobCasts`, `trapDamage`,
`expiredMorphs`) and applies them after releasing `_lock`. Any of these done the other way round is a cycle,
because the thing you call out to takes a lock of its own.

**Never wait for an outer lock while holding an inner one.** The Lua gate is the worked example. Its fast
path is `Monitor.TryEnter` with no timeout: acquiring a lock you never *block* on cannot complete a cycle,
so an ordinary cast keeps its monitor and stays atomic. Only on contention does it take the slow path, which
drops this thread's session monitors *before* waiting. That is the whole invariant — a thread waiting for
the gate holds no monitor, so whoever holds the gate can always finish.

## Adding a lock

1. Put it in the table above, with a row number.
2. Assert the rule from the side that can see the violation — usually the outer lock's entry point, since
   the inner one is the one already held.
3. Write the two-thread test that hangs without it, and **check that it hangs**: a deadlock test that has
   never deadlocked is proof of nothing. Same for a race test — falsify it against genuinely unguarded code,
   with the Debug asserts stripped as well, or what you have measured is the assert.

## Deliberately outside all of this

* The **outbound channel** (`TcpOutbound`). `Send` is a non-blocking `TryWrite`; the socket write happens on
  the transport's own writer task. Nothing above ever blocks on a client.
* `Watchdog`'s `DiagState()`. A watchdog that can block on the wedged session it is diagnosing is not a
  watchdog, so it reads plain fields and takes nothing.
* The scalar reads the tick makes under `World._lock` — `PlayerX`, `PlayerY`, `IsDead`, `IsMorphExpired` and
  friends. They are unsynchronised on purpose: taking the monitor there would invert row 2 against row 3.
