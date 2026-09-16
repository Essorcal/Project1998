using Shared;

namespace Server;

/// <summary>
/// Every staff OVERRIDE a running server can be put into, in one place (#57 finding 31).
///
/// <para>These used to be nine loose fields spread over three <c>Session</c> partials plus two statics and a
/// static dictionary, each next to the command that writes it. Nothing named them as a group, so "what is
/// this session overriding?" had no answer short of grepping, and two of them (the mob swing pair) were
/// world-wide state a per-session command mutated with no actor in the log line. <c>@toggles</c> is the
/// readout that group now has, and <see cref="Log(Session, string, object, object)"/> is the one line every write goes through.</para>
///
/// <para><b>Read-path cost.</b> The session fields are plain fields on a sealed class held by one
/// <c>readonly</c> reference (<c>Session._gm</c>), so every read site compiles to the same field load it
/// did before, one indirection further in. That matters: <c>Session.Movement.SendMapRect</c> reads
/// <see cref="NoClip"/> once per streamed cell and <c>World.MobAiTick</c> reads <c>Session.PeaceMode</c>
/// for every player on a map under <c>World._lock</c>. No property with a body, no dictionary, no
/// delegate and no lock may appear on this type's read path.</para>
///
/// <para><b>Lock discipline.</b> Unchanged by the gathering. The session fields are written by the owning
/// session's command handler and read by that same session (plus <c>World.MobAiTick</c>, which reads
/// <c>PeaceMode</c> under <c>World._lock</c> exactly as it did when the field lived on
/// <c>Session.GmCommands</c>). The two statics and <see cref="WorldDotOverride"/> are deliberately
/// unlocked, as they were before the move — see their own remarks.</para>
/// </summary>
internal sealed class GmOverrides
{
    // ---- session-scoped overrides -----------------------------------------------------------------

    /// <summary>"@clip": no-clip. Read by <c>HandleWalk</c> and by the per-cell pass word in
    /// <c>SendMapRect</c>, which is the hot path this type may not make more expensive.</summary>
    internal bool NoClip;

    /// <summary>"@peace": unprovoked mobs do not notice this player. Exposed to the world as
    /// <c>Session.PeaceMode</c>, which is what <c>World.MobAiTick</c>'s aggro scans read.</summary>
    internal bool Peace;

    /// <summary>"@anywarp": every doorway entry requirement is narrated instead of enforced.</summary>
    internal bool WaiveWarpGate;

    /// <summary>"@showwarps": the per-session warp/doorway marker overlay is on.</summary>
    internal bool ShowWarps;

    /// <summary>"@showwarps look": the marker frames. Both default to 877, a blue pinwheel confirmed
    /// in-game on both shipped clients.</summary>
    internal ushort WarpMarkFrame = 877;
    internal ushort DoorMarkFrame = 877;

    // Melee swing sfx (NexusTK.snd id). The client's action->sound table gives the swing action (0x1A type 1)
    // NO sound (like magic/type 6 -> 0), so a weapon swing is silent unless we play one explicitly over 0x19.
    // Calibrate the id live with "@swingsnd <id>" (auditions it), then it rides every armed swing; 0 = silent.
    internal int SwingSfx = 0;

    // Unarmed ("bare fist") swing sfx fallback, used only when no weapon is equipped (EquippedWeaponSound()
    // returns 0). RTK's own C engine special-cases this by sending the swing action with a hardcoded param
    // (pc.c: clif_sendaction(..., 1, attackspeed, 9) when itemdb_sound(weapon)==0) — but that relies on a
    // fixed action-type->sound table baked into the 6.x/7.x client; our own live testing already proved the
    // 4.95 client's action-param byte is ignored for the swing (see the comment above SwingSfx), so we can't
    // reuse that trick here either. There's no RTK item row for "fists" to port a real id from. 009.wav,
    // calibrated live 2026-08-04 — the SAME id a mob's own swing uses (Session.MobSwingSfx), i.e. a bare fist
    // and a claw/bite land on one shared "unarmed" sound. "@fistsnd <id>" recalibrates or mutes it (0).
    internal int FistSfx = 9;

    // On-connect impact sfx for a PLAYER's melee, played ONLY when a swing actually lands — it stacks with the
    // weapon/fist swing sfx above, which plays on every swing attempt regardless of hit/miss. 349.wav,
    // calibrated live 2026-08-04.
    //
    // NOT sent via the 0x13 damage packet's own hitSound byte any more (SendDamage/ShowDamageResult still carry
    // that field — it's real, see docs §7.2 — but it's a BYTE, and 349 doesn't fit in one). It goes out as its
    // own 0x19 broadcast instead, via Session.PlayHitSfx, which also means peers hear our hits land. RTK's
    // matching per-weapon field (ItmSoundHit / itemdb_soundhit) is dead in the reference server — itemdb_read's
    // SQL SELECT never fetches `sound_hit` — so there's no per-weapon number to port and this stays global.
    // "@hitsnd <id>" recalibrates or mutes (0).
    internal int HitSfx = 349;

    // ---- world-wide overrides ---------------------------------------------------------------------
    //
    // STATIC on purpose, and kept static by the #57 gathering: World decides a mob's swing with no session in
    // hand, so there is nothing per-session to hang these on. The pair is read by World.FlushTick — the back
    // half of the heartbeat, which runs OUTSIDE World._lock — and written by "@mobact" on a session thread.
    // No lock: a byte and a ushort, where a torn read is a wrong attack POSE for one frame and nothing else.

    // The 0x1A action that makes a mob visibly SWING, not just play the sound. RTK's native mob:attack
    // broadcasts this from the C engine; its boss AI does the same thing explicitly with sendAction(2, 20)
    // (rtklua Accepted/Instances/instance_boss.lua) — action type 2, pose length 20 ticks. That's the only RTK
    // reference we have for a MONSTER's melee-pose index: players swing on type 1 (Session.HandleAttack), but a
    // monster sprite sheet indexes its poses differently, and the boss script is a mob using type 2. Broadcast
    // alongside Session.MobSwingSfx wherever a mob commits to a swing (World.Tick's mob->player pass and
    // ApplyMobOnMobHit).
    // TODO(live): confirm type (2 vs 1) and the pose length against the 4.95 client — these two knobs are why
    // they're named fields here rather than inline literals.
    // NOT const: live-tunable via "@mobact <type> [time]" so the attack-pose index can be swept against the
    // client in ONE server session (the creature entity uses vtable 0x4cd098, not the player's, so its type->
    // Monster.tbl-frame mapping isn't the player's 0=stand/1=attack/2=throw table and has to be found by eye).
    internal static byte   MobSwingActionType = (byte)ActionType.Attack;   // a mob's attack pose (the player's is Attack too)
    internal static ushort MobSwingActionTime = 20;   // pose length in ticks (RTK boss uses 20)

    // Ephemeral live-tuning overrides for the world-map dot pixels, set by "@wmpos <i> <x> <y>" (index into
    // Content.WorldDests). Not persisted — you eyeball a dot live, then bake the final number into
    // WorldMapDests.csv and @reload. Empty = every dot uses its CSV DotX/DotY.
    //
    // Static and world-wide for the same reason the swing pair above is: the numbers being hunted are
    // CONTENT, not a property of whoever is hunting them. Read by Session.SendWorldMap.
    internal static readonly Dictionary<int, (int X, int Y)> WorldDotOverride = new();

    // ---- actor logging ----------------------------------------------------------------------------

    /// <summary>The ONE log line a staff override write produces. Before #57 part 3 each command wrote its
    /// own: three of them (<c>@mobact</c>, the three <c>@*snd</c> commands and <c>@wmpos</c>) named no actor
    /// at all, and none of them said what the value had been. A server-wide knob changed by an unnamed
    /// session is exactly the line you want when reading a log backwards.</summary>
    internal static void Log(Session actor, string what, object from, object to) =>
        Shared.Log.Info(LogLine(actor.CharName, what, from, to));

    /// <summary>The boolean toggles, said the way the player's own reply says them ("on"/"off") rather than
    /// the way <c>bool.ToString</c> would ("True"/"False").</summary>
    internal static void Log(Session actor, string what, bool from, bool to) =>
        Log(actor, what, from ? "on" : "off", to ? "on" : "off");

    /// <summary>The line <see cref="Log(Session, string, object, object)"/> emits, as a pure function so a test can pin its shape without
    /// racing the log writer thread.</summary>
    internal static string LogLine(string actor, string what, object from, object to) =>
        $"   -> OVERRIDE '{actor}' {what}: {from} -> {to}";
}
