namespace Server;

public static partial class Content
{

    // Era-gating overrides for crafting skills (see Server/CraftingToggles.cs + docs/common/Crafting-Values.md).
    // File is optional and sparse: only skills listed here override CraftingToggles.DefaultDisabled;
    // anything absent keeps the code-level default. Columns: Skill,Enabled(0/1).
    public static IReadOnlyDictionary<string, bool> CraftingToggleOverrides
    {
        get => _snapshotBuilder?.CraftingToggleOverrides ?? Snapshot.CraftingToggleOverrides;
        private set => Builder.CraftingToggleOverrides = value;
    }

    // Per-class level-up HP/MP gain ranges (game-data/PathGrowth.csv), keyed by path id (0 Peasant / 1
    // Warrior / 2 Rogue / 3 Mage / 4 Poet). Each is the pair of args to Random.Shared.Next(min, max) — max is
    // EXCLUSIVE, matching the original C# switch. The which-stat-is-primary logic stays in Session.LevelUp.
    public static IReadOnlyDictionary<int, (int HpMin, int HpMax, int MpMin, int MpMax)> PathGrowth
    {
        get => _snapshotBuilder?.PathGrowth ?? Snapshot.PathGrowth;
        private set => Builder.PathGrowth = value;
    }
    /// <summary>Level-up gain ranges for a path, falling back to Peasant (0) then a hardcoded default.</summary>
    public static (int HpMin, int HpMax, int MpMin, int MpMax) PathGrowthFor(int path) =>
        PathGrowth.TryGetValue(path, out var g) ? g : PathGrowth.TryGetValue(0, out var p) ? p : (45, 56, 32, 37);

    // Named engine scalars a deployment may retune without a rebuild (game-data/ServerTuning.csv, key,value).
    // These sit on the tier-1/tier-3 line — real mechanics, but harmless to expose as hand-editable config. Typed
    // accessors fall back to the historical hardcoded default if the key is absent, so a missing file is safe.
    public static IReadOnlyDictionary<string, double> Tuning
    {
        get => _snapshotBuilder?.Tuning ?? Snapshot.Tuning;
        private set => Builder.Tuning = value;
    }
    private static double Tune(string key, double dflt) => Tuning.TryGetValue(key, out var v) ? v : dflt;
    public static int MailMinLevel => (int)Tune("MailMinLevel", 10);   // min level to view/send nmail
    public static int SpeechRange  => (int)Tune("SpeechRange", 8);     // tiles (Chebyshev) an NPC "hears" from
    public static uint BankMax     => (uint)Tune("BankMax", 100_000_000);   // per-account coin cap

    // NOTE: EraDate is deliberately NOT a Tune() accessor here. It is read by Shared.EraCalendar so the
    // login server sees the same value; a second copy on this side could drift from it by a stale default.
    // Reach it via Era.Today / EraCalendar.RawDate.
    // Highest minor-quest tier a path leader will hand out: 1 = Minor only (4.95 — the only tier that
    // existed), 2 adds Major, 3 adds Epic. The Major/Epic rows stay in MinorQuests.csv either way; this only
    // gates whether the "which type of quest?" menu is offered at all. See Server/MinorQuest.cs.
    public static int MinorQuestTiers => (int)Tune("MinorQuestTiers", 1);
    // Hours a path leader makes you wait after COMPLETING a minor quest before handing out another. RTK starts
    // its cooldown only when you ABANDON one, which leaves the completion path with no limit at all: turn one
    // in, say "quest" again, and the next is yours — an exp faucet whose only cost is one kill (the reward
    // scales with level, so it's worth more the higher you climb). 24 = one quest per real-world day, per the
    // user (2026-08-12). Real hours, not game time: the timer is a unix-second deadline like every other
    // persisted cooldown, so logging out doesn't pause it. 0 restores RTK's behavior.
    //
    // The ABANDON cooldown stays on RTK's own per-tier value (Minor = 2h) rather than following this. It
    // gates a quest you dropped without being paid for, so it isn't part of the reward rate limit — and
    // making a failed quest cost a full day would just teach players to sit on one they can't finish.
    public static int MinorQuestCooldownHours => (int)Tune("MinorQuestCooldownHours", 24);
    // (SilentDelReason is GONE, 2026-08-07. It existed to probe whether an out-of-range 0x10 reason was the
    // client's silent path; the live answer was no — 15 renders "<item> removed.", the same line reason 0
    // gives, so the handler clamps/defaults and NO reason byte is silent. Every path that used it has since
    // moved to a real reason (bank deposit and shop sale both hand the item over: 10, "You gave X."), and a
    // path that must truly say nothing sends no 0x10 at all — see EquipDelReason.)
    // Equipping is the one removal that ought to be TRULY silent: the item didn't leave you, it moved onto
    // your body, and the real game says nothing. Suppressing the 0x10 entirely was tried (default -1) and is
    // WRONG — it leaves a ghost row in the bag that can't be dropped, equipped or used, because the server
    // has already dropped the item while the client still draws it.
    //
    // The reason it can't work: the equip window and the bag are SEPARATE client structures. The bag is a
    // 164-byte-stride array and the ONLY thing that clears an entry is 0x48f0b0, reached only from the 0x10
    // handler (0x48fe10) — which range-checks the slot and ignores the reason byte completely. The 0x37
    // equip-window entry never touches that array, so it cannot stand alone.
    //
    // Reason 12 is the one code that says NOTHING, so equipping gets both: the bag entry is cleared and the
    // player isn't told they "used" their armour. Full table swept live 2026-08-07 (@delreason):
    //   0 "<item> removed."   1 "You dropped"   2 "You ate"     3 "You smoked" (herb/sonhi pipes)
    //   4 "You threw"         5 "You shot"      6 "You used"    7 "You posted"
    //   8 "<item> decayed."   9 "You gave"     10 "You sold"   11 "<item> removed."
    //  12 SILENT             13 "<item> broken."               14+ all "<item> removed."
    public static int EquipDelReason => (int)Tune("EquipDelReason", 12);
    /// <summary>Open the board request straight into the MAILBOX when the player has unread n-mail, instead
    /// of the board list. 'm' is armed only while the mail arrow is up and sends the same `3b 01 00` as 'b',
    /// so this would be the only way to make 'm' behave like a mailbox key — at the cost of 'b' doing the same
    /// while mail is unread. 0 = always show the board list (Mailbox is still its last entry).
    /// <para>DEFAULT 0 BECAUSE 1 HARD-FREEZES THE 4.95 CLIENT (live 2026-08-08): answering sub-1 "Show Board"
    /// with a POSTS body (0x31 flags2=4) instead of the LIST body locks the client up — it stops pumping
    /// input entirely and never sends another packet. The identical posts bytes render fine when they answer
    /// sub-2, so the window ctor 0x406e80(1) evidently arms a list-shaped parse that a posts body walks off
    /// the end of. Don't turn this back on without RE'ing that ctor first. See Session.HandleBoard case 1.</para></summary>
    public static bool MailFirstOnBoard => Tune("MailFirstOnBoard", 0) != 0;

    /// <summary>Patch a peer's appearance with <c>0x1d</c> (look-update-in-place) instead of the
    /// despawn(<c>0x0E</c>) + respawn(<c>0x33</c>) pair. The old pair exists because a bare <c>0x33</c>
    /// re-send orphans the entity and leaks its nameplate marker; <c>0x1d</c> sidesteps that entirely by
    /// never destroying or creating anything. Morph and stealth still take the full path regardless —
    /// see Session.RefreshAppearance. 0 = always use the old pair.</summary>
    public static bool LookUpdateInPlace => Tune("LookUpdateInPlace", 1) != 0;

    /// <summary>Draw nameplates over other players. The plate is rendered from the NAME string in the
    /// <c>0x33</c> spawn, so sending an empty name is a pure server-side way to suppress it — no client
    /// patch needed (cf. re/patch_no_nametag.py, which does it on disk). Applies to PEERS only; your own
    /// name is never in a peer packet. 0 = no plates.</summary>
    public static bool ShowNameplates => Tune("ShowNameplates", 1) != 0;

    /// <summary>Which nations the user-list window (0x36) gets columns and a name for — the ids sent in the
    /// 0x59 sub-1 town table. Default is the three this server actually plays: 0 Neutral, 1 Koguryo,
    /// 2 Buya. Deliberately NOT the same thing as <c>Character.Nations</c>, which is the HUD crest id space
    /// (0x08 stats, calibrated via @nat) and must keep all 8 entries.
    /// <para>A nation absent from this table cannot be resolved by the client: it scans the table for the
    /// viewer's own nation id and falls back to entry 0 when it misses, at which point every row whose
    /// nation nibble isn't 0 drops out of the columns. So a player whose nation is off this list sees an
    /// empty window, not a partial one.</para></summary>
    /// <para>ServerTuning holds scalars only, so this is a BITMASK over the nation ids: bit i = nation i.
    /// Default 7 = 0b111 = Neutral + Koguryo + Buya. 255 restores all eight.</para></summary>
    // User-list name colours — row byte +2, a palette index measured live (`@users hunters`). 0..15 is the
    // standard 16-colour palette and **0 paints black on black**, which is what made every name invisible
    // until 2026-08-08. Same three cases RTK colours (default / same clan / GM), in the palette this client
    // actually has. Values above 15 reach further into the 256-entry palette if a deployment wants them.
    // Highest rule wins: self, then GM, then clan, then default. 0 turns an OPTIONAL rule off — safe to
    // overload that way because 0 is the invisible colour and can never be a deliberate choice. Only
    // UserListColorDefault has no off switch.
    //   0 black(invisible) 1 dk blue  2 dk green 3 teal      4 dk red  5 magenta 6 brown   7 lt gray
    //   8 dk gray          9 lt blue 10 lt green 11 lt cyan 12 red    13 pink   14 yellow 15 white
    public static int UserListColorDefault => (int)Tune("UserListColorDefault", 15);   // white
    public static int UserListColorClan    => (int)Tune("UserListColorClan",    10);   // light green — RTK's same-clan highlight
    public static int UserListColorGm      => (int)Tune("UserListColorGm",      12);   // red
    public static int UserListColorSelf    => (int)Tune("UserListColorSelf",    14);   // yellow — no RTK equivalent, ours

    public static IReadOnlyList<byte> UserListNations
    {
        get
        {
            int mask = (int)Tune("UserListNationMask", 7);
            var ids = new List<byte>();
            for (byte i = 0; i < 8; i++) if ((mask & (1 << i)) != 0) ids.Add(i);
            return ids.Count > 0 ? ids : new List<byte> { 0 };   // the client bails on an empty table
        }
    }

    // Per-path cumulative-exp-to-level table (RTK rtk/db/level_db.txt, classdb_level): LevelExp[path][level] =
    // total exp needed to LEAVE `level` (i.e. reach level+1). Long-format CSV (game-data/LevelExp.csv,
    // generated from the RTK file — see awk one-liner in git history) with one row per (Path, Level). Path ids
    // match PathIdForClass (0 Peasant/1 Warrior/2 Rogue/3 Mage/4 Poet); level 99 is the cap and has no entry.
    private static Dictionary<int, Dictionary<int, uint>> LevelExp
    {
        get => _snapshotBuilder?.LevelExp ?? Snapshot.LevelExp;
        set => Builder.LevelExp = value;
    }

    /// <summary>Total exp required to advance past <paramref name="level"/> on <paramref name="pathId"/>
    /// (0 at the level-99 cap or on a lookup miss — treated as "no further threshold").</summary>
    public static uint ExpToNext(int pathId, int level)
    {
        if (level >= 99) return 0;
        if (!LevelExp.TryGetValue(pathId, out var byLevel) && !LevelExp.TryGetValue(0, out byLevel)) return 0;
        return byLevel.GetValueOrDefault(level, 0u);
    }
}
