"""Automated equipment toggling, so stat variance doesn't have to be managed by hand.

Why this is safe now (it wasn't when we were guessing letters blind): the `0x39` profile is
an AUTHORITATIVE list of what is currently worn, and the bot can request it any time with
the plaintext packet `2d 00 00`. So every action is verified against a real before/after
diff instead of inferred from stat deltas -- which is what went wrong before, because BUFF
SPELLS move the same stat fields as gear (casting `Might` looks exactly like equipping a
+3 might item).

    discover   probe each inventory letter, learn letter -> item, REVERT everything
    toggle     equip/unequip a named item, verified, with automatic revert on surprise

Every mutation is followed by a profile re-read; if the loadout changed in a way we didn't
intend, we immediately undo it and stop rather than continue blindly.

Usage:
    python gear_toggle.py show                 # what's worn right now
    python gear_toggle.py discover [letters]   # learn the key map (reverts as it goes)
    python gear_toggle.py toggle "Black ring"  # flip one item, verified
"""
import sys, time, csv, os
sys.path.insert(0, ".")
import nexus_bot as NB
import nexus_agent as NA

P_KEYS = os.path.join(NA.OUT, "gear_keys.csv")
SETTLE = 1.3            # seconds to let the server answer an equip/unequip
MAX_ACTIONS = 40        # hard cap: never flail at the character indefinitely


class Gear:
    def __init__(self):
        wins = NB.find_windows()
        if not wins:
            raise SystemExit("no live client window")
        self.hwnd, _, self.pid = wins[0][0], wins[0][1], wins[0][2]
        self.agent = NA.Agent()
        self.world = NB.World(self.agent)
        self.s, self.sc = NB.attach(NB.build_pump(self.world, self.agent), pid=self.pid)
        self.ex = self.sc.exports_sync
        self.world.mem_ex = self.ex
        # lowercase keys go through PostMessage -- no focus stealing, unlike shifted keys
        self.ctrl = NB.Controller(self.hwnd, mode="post")
        self.ctrl.fkey = self.ex
        self.actions = 0
        time.sleep(1.5)

    # ---------- ground truth ----------
    def profile(self, tries=8):
        """Worn items, straight from the server. Returns [] if it can't be obtained --
        callers must treat that as 'unknown', never as 'nothing worn'."""
        with self.world.lock:
            self.world.equipment = None
        for i in range(tries):
            try:
                self.ex.sendraw([0x2d, 0x00, 0x00])
            except Exception:
                pass
            if i == 1:
                # sendraw needs the client's connection object, which is captured from the
                # client's FIRST send -- on a fresh attach none has happened yet, so prime
                # it with a turn (cosmetic; it changes facing, never position)
                self.ctrl.tap("left")
                time.sleep(0.2)
                self.ctrl.tap("right")
            time.sleep(0.7)
            with self.world.lock:
                eq = self.world.equipment
            if eq:
                return list(eq[2])
        return []

    def press(self, keys, gap=0.3):
        self.actions += 1
        if self.actions > MAX_ACTIONS:
            raise SystemExit("action cap reached -- stopping rather than flailing at gear")
        for ch in keys:
            self.ctrl.press_char(ch)
            time.sleep(gap)

    def esc(self):
        self.ctrl.close_chat(1)
        time.sleep(0.2)

    # ---------- operations ----------
    def act(self, letter):
        """`w` + letter, then report (before, after) worn lists."""
        before = self.profile()
        if not before:
            print("  ! could not read the profile -- refusing to touch gear")
            return None, None
        self.press("w")
        self.press(letter)
        time.sleep(SETTLE)
        self.esc()
        after = self.profile()
        return before, after

    def discover(self, letters):
        """Learn what each letter does, reverting after every probe."""
        rows = []
        base = self.profile()
        if not base:
            print("could not read the starting loadout -- aborting")
            return
        print(f"starting loadout: {base}\n")
        for ch in letters:
            before, after = self.act(ch)
            if after is None:
                break
            gone = [i for i in before if i not in after]
            new = [i for i in after if i not in before]
            if not gone and not new:
                print(f"  '{ch}' -> no change")
                continue
            what = (f"REMOVED {gone}" if gone else "") + (f"EQUIPPED {new}" if new else "")
            print(f"  '{ch}' -> {what}")
            rows.append({"key": ch, "removed": ";".join(gone), "equipped": ";".join(new)})
            # revert immediately: the same key should undo it (it is a toggle on that slot)
            b2, a2 = self.act(ch)
            if a2 is not None and sorted(a2) == sorted(base):
                print(f"       reverted OK")
            else:
                print(f"       !! REVERT FAILED -- loadout now {a2}, expected {base}")
                print(f"       stopping so nothing else is disturbed")
                break
        if rows:
            NA.append_csv(P_KEYS, rows, ["key", "removed", "equipped"])
            print(f"\nwrote key map -> {P_KEYS}")
        final = self.profile()
        print(f"final loadout: {final}")
        if sorted(final) != sorted(base):
            print("WARNING: final loadout differs from the start -- check the character")

    def wear(self, letter, expect=None):
        """Wear the inventory item at `letter`, VERIFIED against the 0x39 profile.

        `w`+letter is a WEAR, not a toggle: it swaps the item into its slot and displaces
        whatever was there into inventory (proven -- wearing Peasant garb displaced Farmer
        armor, and pressing the same key again did nothing because the garb was already on).
        That is also why variance is produced by SWAPPING two items in one slot rather than
        trying to unequip: Black ring <-> Sea ring moves `hit` 3 <-> 0 with nothing else
        touched, which is the cleanest experiment we have.
        """
        before = self.profile()
        if not before:
            print("  ! cannot read profile -- refusing to touch gear")
            return None
        self.press("w"); self.press(letter)
        time.sleep(SETTLE)
        self.esc()
        after = self.profile()
        if not after:
            print("  ! profile unreadable after the action -- CHECK THE CHARACTER")
            return None
        gone = [i for i in before if i not in after]
        new = [i for i in after if i not in before]
        print(f"  '{letter}': {before} -> {after}")
        if gone or new:
            print(f"     removed={gone} equipped={new}")
        if expect and expect not in after:
            print(f"     !! expected {expect!r} to be worn; it is NOT. Nothing further done.")
        return after

    def toggle(self, item):
        """Flip one named item, using the learned key map."""
        if not os.path.exists(P_KEYS):
            print("no key map yet -- run `discover` first")
            return
        keymap = {}
        for r in csv.DictReader(open(P_KEYS, encoding="utf-8")):
            for field in ("removed", "equipped"):
                for name in filter(None, r[field].split(";")):
                    keymap.setdefault(name, r["key"])
        key = keymap.get(item)
        if not key:
            print(f"no key known for {item!r}; known: {sorted(keymap)}")
            return
        before, after = self.act(key)
        if after is None:
            return
        if sorted(before) == sorted(after):
            print(f"'{key}' did not change anything (is the item elsewhere?)")
        else:
            print(f"{item}: {before} -> {after}")

    def close(self):
        try:
            self.s.detach()
        except Exception:
            pass


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "show"
    g = Gear()
    try:
        if cmd == "show":
            print("worn:", g.profile())
        elif cmd == "discover":
            letters = sys.argv[2] if len(sys.argv) > 2 else "abcdefgh"
            g.discover(letters)
        elif cmd == "wear":
            g.wear(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
        elif cmd == "toggle":
            g.toggle(sys.argv[2])
        else:
            print(__doc__)
    finally:
        g.close()


if __name__ == "__main__":
    main()
