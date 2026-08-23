using Shared;

namespace Server;

/// <summary>
/// Sute's boss AI — the one creature in the world that does not use the ordinary chase-and-swing loop.
/// Bespoke on purpose (the user's call, 2026-08-22): the behaviour below was observed on one mob and
/// nothing else in the bestiary wants it, so it is written plainly here rather than spread across new CSV
/// columns that would have exactly one row.
///
/// <para><b>Source: eyewitness observation, not RTK.</b> RTK has no AI script for Sute at all — he is a
/// plain melee mob there. Nexus Atlas's launch notice for the dungeon promises "a spellcasting enemy,
/// magical traps", and the user fought him and reported what he actually does. That report is the whole
/// specification, and it is the only one there is; where it is silent (exact damage of the second zap, the
/// retreat pace in ms) the constants below say so.</para>
///
/// <para><b>The behaviour, by HP band:</b></para>
/// <list type="bullet">
/// <item><b>Above 50%</b> — hit and run. He lands a burst of <see cref="BurstHits"/> swings, backs off
/// <see cref="RetreatTiles"/> tiles, holds for
/// <see cref="HoldMinMs"/>-<see cref="HoldMaxMs"/>, walks back in and does it again.</item>
/// <item><b>25%-50%</b> — he stands and fights normally. The user described the hit-and-run as a
/// "while above 50%" behaviour and the flee as a "below 25%" one, which leaves this band as the ordinary
/// loop; it is also the only stretch of the fight where he is straightforwardly meleeable.</item>
/// <item><b>At or below 25%</b> (the red bar) — he runs, "unless trapped or hit". Hit him and he turns and
/// answers once before breaking off again; corner him so he cannot retreat and he answers twice, then tries
/// to run again the moment there is anywhere to go.</item>
/// </list>
///
/// <para>The two ranged zaps are NOT here — they are ordinary MobSpells.csv rows (Ice ray, Ice storms),
/// because the existing creature-spell path already does everything they need: range, a rarity roll, a
/// shared cooldown, the "Sute attacks you with Ice ray spell." line and the "Sute: Feel my power!" shout.
/// The self-heal IS here, because MobSpells.csv only models spells thrown AT a player.</para>
/// </summary>
public static class SuteAi
{
    public const string MobKey = "sute";

    /// <summary>Which leg of the cycle he is on (<see cref="Mob.SutePhase"/>).</summary>
    public static class Phase
    {
        public const byte Engage  = 0;   // closing and swinging; SuteSwingsLeft counts the burst down
        public const byte Retreat = 1;   // backing away; SuteRetreatLeft counts the tiles down
        public const byte Hold    = 2;   // standing off, out of reach, until SutePhaseUntil
        public const byte Flee    = 3;   // wounded rout (<= FleeBelowPct), fights only when cornered or answered
        public const byte Approach = 4;  // darting back in after a hold, to start the next burst
    }

    // ---- hit-and-run (above HitAndRunAbovePct) ---------------------------------------------------
    /// <summary>Swings per burst while he is above <see cref="HitAndRunAbovePct"/>: "4 hits within 2
    /// seconds", <b>every</b> burst, not just the first.
    ///
    /// <para>This shipped wrong. The original description ran "4 hits … then comes back for two attacks
    /// repeating process", which was read as an opening burst of four followed by twos forever — so in play
    /// he opened with four once and then only ever hit twice. The user's correction is flat: above half
    /// health it is four, every time. (Below half he stops bursting altogether and just fights, so there is
    /// no second burst size left for the old "two" to describe.)</para></summary>
    public const int BurstHits = 4;

    /// <summary>Sute's action beat. He moves twice a second and strikes twice a second, in a
    /// <b>333ms, 333ms, 333ms-rest</b> rhythm — the user's own description of both, and the same cadence a
    /// player gets from the 3-actions-per-second budget, using two of the three slots.
    ///
    /// <para><see cref="ActMs"/> is deliberately equal to World.TickMs: the world heartbeat was lowered from
    /// 600ms to 333 precisely so this rhythm could be expressed at all (at 600 the fastest any creature
    /// could act was 1.7 times a second). So he acts on every beat he is not resting on.</para>
    ///
    /// <para>The rest beat is what stops him being a stream of damage: four swings arrive as
    /// hit-hit-rest-hit-hit, i.e. ~1.67s, which is the "4 hits within 2 seconds" originally reported. His
    /// movement gets the same treatment, so he covers two tiles then visibly pauses.</para></summary>
    public const int ActMs = 333;
    /// <summary>Beats he acts on, out of <see cref="BeatsPerCycle"/>.</summary>
    public const int BeatsOn = 2, BeatsPerCycle = 3;

    /// <summary>Swing interval during a burst — one per acting beat. His ordinary out-of-burst swing stays
    /// at <see cref="Mob.BaseAttackTime"/>: the twice-a-second rate was described for his bursts, and
    /// applying it to the stand-and-fight band as well would make him a wall of damage nobody described.</summary>
    public const int BurstAttackMs = ActMs;

    /// <summary>"steps away two spaces".</summary>
    public const int RetreatTiles = 2;

    /// <summary>Tiles Sute covers per move turn: <b>one</b>. He is the one fleeing creature in the world
    /// that does NOT dart.
    ///
    /// <para>The other two fleers hop several tiles inside a single turn (World.Dart — prey 2, the wounded
    /// rout 3), which is RTK's own idiom and what a rabbit does. Sute was built that way too and it was
    /// wrong: he "should move at a normal base, just fast like a player. Not the slow pace of regular mobs."
    /// So his movement is ordinary single-tile stepping, one tile per beat, and the client animates each one
    /// as a walk. It is his PACE that is unusual, not his stride — see <see cref="ActMs"/>.</para></summary>
    public const int StepTilesPerTurn = 1;

    /// <summary>"pauses for a second or two" before coming back.</summary>
    public const int HoldMinMs = 1000, HoldMaxMs = 2000;

    /// <summary>Above this share of max HP he refuses to stand and trade — the hit-and-run band.</summary>
    public const int HitAndRunAbovePct = 50;

    // ---- wounded rout (at or below FleeBelowPct) -------------------------------------------------
    /// <summary>The red bar. Below it he runs rather than fights.</summary>
    public const int FleeBelowPct = 25;

    /// <summary>"Will hit back once if hit and then flee. If trapped and getting hit, will hit twice."</summary>
    public const int RetaliateHits = 1, CorneredRetaliateHits = 2;

    /// <summary>How long after answering a blow he refuses to be goaded into answering another.
    ///
    /// <para><b>Without this he never flees at all</b> — which is exactly how it shipped first, and what the
    /// user saw: every point of damage re-armed the retaliation, so a player swinging faster than his own
    /// attack timer kept the debt permanently above zero and he stood there trading blows on an empty health
    /// bar. The description is "hit back ONCE if hit and then flee" — the answer is a parting shot, not a
    /// mode. The lockout runs a little past one move turn (2000ms) so a dart actually gets away before he
    /// can be provoked again.</para></summary>
    public const int RetaliateLockoutMs = 2500;

    // ---- the wounded self-heal -------------------------------------------------------------------
    /// <summary>"a self-heal spell healing for 200HP when at or below 25%". On a 17,000-HP boss that is
    /// 1.2% — it does not save him, it just drags the ending out, which is what was described.</summary>
    public const int HealAmount = 200;

    /// <summary>Rarity of the self-heal: at most one per <see cref="HealEveryMs"/>, and then only on a
    /// 1-in-<see cref="HealChance"/> roll per tick, because the user was explicit that he "BARELY casts
    /// spells… you should see it cast a few times" rather than spamming it.</summary>
    public const int HealEveryMs = 20_000, HealChance = 12;

    /// <summary>The heal's animation and sound over Sute's own tile — <b>Soothe's</b> exact pair
    /// (spell_effects.csv <c>soothe</c>: animation 5, sound 708). Animation 5 is the shared heal look across
    /// the whole tier-1 family (soothe, heal, mend wounds, lay hands, fleshspeak, survive), so a heal reads
    /// as a heal rather than as whatever else the caster throws. It shipped briefly as 52 — the ICE GLARE
    /// animation, picked to match his zaps — which drew an attack effect on a heal.
    ///
    /// <para>He gets a visible cast rather than a silent HP bump, but no shout: no source records what (or
    /// whether) he says when he heals, and putting invented words in a named character's mouth is worse than
    /// saying nothing.</para></summary>
    public const int HealAnim = 5, HealSound = 708;

    // ---- the room hazard -------------------------------------------------------------------------
    /// <summary>"A blast of frigid cold hits you." — a hidden trap tile, exactly the shape of the existing
    /// cave ambush traps (World.RefillAmbushLocked / CheckPlayerTrapTrigger), but with a damage payload
    /// instead of a mob burst. Sprung tiles are relocated, so the room stays randomly hazardous rather than
    /// being cleared once and walked safely forever.</summary>
    public const string FrigidTrapKind = "frigid";
    public const string FrigidText = "A blast of frigid cold hits you.";

    /// <summary>Observed 257 damage. See <see cref="IceRayObservedDamage"/> for the AC caveat that applies
    /// to this number too.</summary>
    public const int FrigidDamage = 257;

    /// <summary>Hidden cold tiles live in every room of the cave, per the user's call — Atlas lists "magical
    /// traps" as a feature of the dungeon rather than of Sute's own room.</summary>
    public static readonly ushort[] CaveMaps = { 441, 442, 443, 444, 445, 446, 447 };
    public const int FrigidTrapsPerMap = 4;

    /// <summary>Recorded for the record, and because it is the number a future re-measure would compare
    /// against: Ice ray was seen taking a player from 4,003 to 3,598 — 405 — while wearing AC -22. Our
    /// engine does NOT run creature-spell damage through AC (Session.ReceiveMobSpell: "magic ignores
    /// physical AC", the same rule the player's own spells follow), so the observed figure is used as the
    /// raw amount and reproduces the observation exactly for that player. If spell damage is ever put
    /// through AC, these want re-deriving (405 / 0.78 ≈ 519 raw). Ice storms was not measured; the user's
    /// instruction was "let's say 2x that of Ice ray", so 810 is an authorised assumption, not an
    /// observation.</summary>
    public const int IceRayObservedDamage = 405, IceStormsAssumedDamage = 810;

    /// <summary>What the AI wants to happen this tick. World.Tick owns the map, the occupancy sets and the
    /// step helpers, so this class only decides; it never moves anything itself.</summary>
    public enum Act
    {
        /// <summary>Not Sute, or nothing special this tick — run the ordinary chase-and-swing.</summary>
        Normal,
        /// <summary>Stand still, out of reach, and do not swing.</summary>
        Hold,
        /// <summary>Step directly away from the target (see <see cref="StepTilesPerTurn"/>).</summary>
        Retreat,
        /// <summary>Step back TOWARD the target, without swinging, until he is in reach.</summary>
        Approach,
    }

    /// <summary>Advance his rhythm one beat and say whether this beat is a REST — no step, no swing.
    /// Called once per world beat, before anything else he might do.</summary>
    public static bool RestBeat(Mob mob)
    {
        byte beat = mob.SuteBeat;
        mob.SuteBeat = (byte)((beat + 1) % BeatsPerCycle);
        return beat >= BeatsOn;
    }

    /// <summary>Percent of max HP, guarding a zero max.</summary>
    private static int HpPct(Mob mob) => mob.MaxHp <= 0 ? 100 : (int)(mob.Hp * 100L / mob.MaxHp);

    /// <summary>Decide this tick's behaviour and advance the phase machine. Called from World.Tick once the
    /// target is known and in range, before the adjacency/swing branch.
    ///
    /// <para><paramref name="adjacent"/> is cardinal adjacency to the target — the same test the ordinary
    /// loop uses to decide between swinging and stepping.</para></summary>
    public static Act Decide(Mob mob, bool adjacent, long now)
    {
        int pct = HpPct(mob);

        // ---- the red bar: run, unless cornered or answering a blow ------------------------------
        if (pct <= FleeBelowPct)
        {
            if (mob.SutePhase != Phase.Flee)
            {
                mob.SutePhase = Phase.Flee;
                mob.SuteSwingsLeft = 0;
                mob.AttackTime = mob.BaseAttackTime;
            }
            // Owed retaliation (set by World when he is damaged) is spent only IN REACH — he turns on
            // whoever is actually beside him rather than chasing a caster across the room, because he
            // "prefers to flee". An owed swing he never gets to take simply keeps until you close again.
            if (mob.SuteSwingsLeft > 0 && adjacent) { mob.SuteSwingsLeft--; return Act.Normal; }
            // ALWAYS ask to run. Being cornered is decided by whether the step actually succeeds, in World,
            // this same beat — it is deliberately NOT consulted here. Reading the latched flag was the bug
            // that kept him fighting at 15% health: one blocked step set it, this branch then returned
            // Normal, and the only code that could clear it again never ran.
            return Act.Retreat;
        }

        // Healed or otherwise climbed back out of the red — drop the flee state and re-enter normally.
        if (mob.SutePhase == Phase.Flee) { mob.SutePhase = Phase.Engage; mob.SuteSwingsLeft = 0; }

        // ---- 25%-50%: an ordinary fight ---------------------------------------------------------
        if (pct <= HitAndRunAbovePct)
        {
            if (mob.SutePhase != Phase.Engage || mob.AttackTime != mob.BaseAttackTime)
            {
                mob.SutePhase = Phase.Engage;
                mob.SuteSwingsLeft = 0;
                mob.AttackTime = mob.BaseAttackTime;
            }
            return Act.Normal;
        }

        // ---- above 50%: hit and run --------------------------------------------------------------
        switch (mob.SutePhase)
        {
            case Phase.Retreat:
                if (mob.SuteRetreatLeft > 0 && !mob.SuteCornered) return Act.Retreat;
                // Backed off far enough (or wedged and unable to) — stand off and wait.
                mob.SutePhase = Phase.Hold;
                mob.SutePhaseUntil = now + Random.Shared.Next(HoldMinMs, HoldMaxMs + 1);
                return Act.Hold;

            case Phase.Hold:
                if (now < mob.SutePhaseUntil) return Act.Hold;
                mob.SutePhase = Phase.Approach;
                mob.MoveTimer = mob.MoveTime;              // dart back on the very next tick
                return adjacent ? EnterBurst(mob, BurstHits) : Act.Approach;

            case Phase.Approach:
                // Dart back in until he is in reach, then open the next (shorter) burst.
                return adjacent ? EnterBurst(mob, BurstHits) : Act.Approach;

            default:   // Engage
                // First contact of the fight: open with the long burst.
                if (mob.SuteSwingsLeft <= 0 && mob.AttackTime != BurstAttackMs)
                    return EnterBurst(mob, BurstHits);
                return Act.Normal;
        }
    }

    /// <summary>Start a burst of <paramref name="swings"/> at burst speed. Always returns
    /// <see cref="Act.Normal"/>, so callers can `return EnterBurst(...)`.</summary>
    private static Act EnterBurst(Mob mob, int swings)
    {
        mob.SutePhase = Phase.Engage;
        mob.SuteSwingsLeft = swings;
        mob.AttackTime = BurstAttackMs;
        return Act.Normal;
    }

    /// <summary>Called by World once a queued Sute swing has actually landed, to count the burst down and
    /// start the retreat when it is spent. Separate from <see cref="Decide"/> because the ordinary loop
    /// queues a hit and resolves it later — counting at decision time would count swings he never took
    /// (e.g. the target stepped out of reach first).</summary>
    public static void OnSwung(Mob mob)
    {
        if (mob.SutePhase != Phase.Engage || HpPct(mob) <= HitAndRunAbovePct) return;
        if (mob.SuteSwingsLeft > 0) mob.SuteSwingsLeft--;
        if (mob.SuteSwingsLeft > 0) return;

        mob.SutePhase = Phase.Retreat;
        mob.SuteRetreatLeft = RetreatTiles;
        mob.SuteCornered = false;
        mob.AttackTime = mob.BaseAttackTime;
        mob.MoveTimer = mob.MoveTime;                      // break off on the very next tick, not up to a turn later
    }

    /// <summary>Called by World when Sute takes damage. Below the red bar a blow buys one answering swing —
    /// two if he is cornered and being worked over — after which he goes back to trying to run.
    ///
    /// <para>Rate-limited by <see cref="RetaliateLockoutMs"/>. Being hit again the instant after he answers
    /// must NOT buy another answer, or a player hitting faster than his swing timer pins him in place
    /// forever and the rout never happens at all.</para></summary>
    public static void OnDamaged(Mob mob, long now)
    {
        if (HpPct(mob) > FleeBelowPct) return;
        if (now < mob.SuteRetaliateLockedUntil) return;
        mob.SuteSwingsLeft = mob.SuteCornered ? CorneredRetaliateHits : RetaliateHits;
        // The window opens the moment the debt is CREATED, not when it is paid off. Arming it on the last
        // swing instead was a second way to pin him: while cornered he is owed two, so a hit landing between
        // the first and second answer topped the debt straight back to two, it never reached zero, and the
        // lockout therefore never armed at all.
        mob.SuteRetaliateLockedUntil = now + RetaliateLockoutMs;
    }

    /// <summary>The wounded self-heal. True if it fired this tick (the caller shows the animation and logs
    /// it). Rolled from the AI tick, so it only ever happens while he is engaged with somebody.</summary>
    public static bool TryHeal(Mob mob, long now)
    {
        if (HpPct(mob) > FleeBelowPct || mob.Hp >= mob.MaxHp) return false;
        if (now < mob.SuteHealReadyAt || Random.Shared.Next(HealChance) != 0) return false;

        mob.SuteHealReadyAt = now + HealEveryMs;
        mob.Hp = Math.Min(mob.MaxHp, mob.Hp + HealAmount);
        return true;
    }
}
