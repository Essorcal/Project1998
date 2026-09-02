using System.Linq;
using Server;
using Shared;
using Xunit;

namespace Tests;

/// <summary>
/// Sute's boss AI (<see cref="SuteAi"/>) and his two ranged zaps. The AI is a pure state machine — it
/// decides, World executes — so the whole hit-and-run cycle can be driven here without a map, a socket or a
/// tick loop, which is the reason it was written that way.
///
/// <para>What these are worth: the behaviour came from ONE eyewitness report of ONE fight, so these tests
/// do not prove the AI matches the real Sute. They prove it matches the report, and that the cycle cannot
/// silently wedge — a boss stuck in a retreat leg he can never leave, or one whose burst speed never gets
/// restored, looks like a hang rather than a bug.</para>
/// </summary>
public class SuteAiTests
{
    private static readonly object _gate = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            Content.Load();
            _loaded = true;
        }
    }

    private const int MaxHp = 17_000;

    private static Mob Sute(int hpPercent = 100)
    {
        var m = new Mob(1, 110, 5, 5, "Sute", MaxHp) { Key = SuteAi.MobKey };
        m.Hp = MaxHp * hpPercent / 100;
        return m;
    }

    /// <summary>Drive one full hit-and-run cycle at full health: open with four fast swings, back off two
    /// tiles, stand off, then come back for two. The counts and the restored swing speed are the parts that
    /// were actually observed ("4 hits within 2 seconds… then two attacks").</summary>
    [Fact]
    public void AboveHalfHealth_BurstsThenBacksOffThenReturns()
    {
        var mob = Sute();
        long now = 1_000_000;

        // Opening burst: four swings, and he speeds up to deliver them.
        Assert.Equal(SuteAi.Act.Normal, SuteAi.Decide(mob, adjacent: true, now));
        Assert.Equal(SuteAi.BurstHits, mob.SuteSwingsLeft);
        Assert.Equal(SuteAi.BurstAttackMs, mob.AttackTime);

        for (int i = 0; i < SuteAi.BurstHits; i++)
        {
            Assert.Equal(SuteAi.Act.Normal, SuteAi.Decide(mob, adjacent: true, now));
            SuteAi.OnSwung(mob);
        }

        // Burst spent -> retreat, at his ordinary swing speed again.
        Assert.Equal(SuteAi.Phase.Retreat, mob.SutePhase);
        Assert.Equal(SuteAi.RetreatTiles, mob.SuteRetreatLeft);
        Assert.Equal(mob.BaseAttackTime, mob.AttackTime);

        // The retreat is a DART: World covers all of it inside ONE move turn, so a single Retreat act is
        // asked for and the whole SuteRetreatLeft is consumed by it.
        Assert.Equal(SuteAi.Act.Retreat, SuteAi.Decide(mob, adjacent: false, now));
        mob.SuteRetreatLeft = 0;                         // stands in for a full-distance dart

        // Out of retreat -> hold, and he stays held until the timer runs out.
        Assert.Equal(SuteAi.Act.Hold, SuteAi.Decide(mob, adjacent: false, now));
        Assert.Equal(SuteAi.Phase.Hold, mob.SutePhase);
        Assert.InRange(mob.SutePhaseUntil - now, SuteAi.HoldMinMs, SuteAi.HoldMaxMs);
        Assert.Equal(SuteAi.Act.Hold, SuteAi.Decide(mob, adjacent: false, mob.SutePhaseUntil - 1));

        // Timer up, still out of reach -> step back in.
        Assert.Equal(SuteAi.Act.Approach, SuteAi.Decide(mob, adjacent: false, mob.SutePhaseUntil));
        Assert.Equal(SuteAi.Phase.Approach, mob.SutePhase);
        Assert.Equal(SuteAi.Act.Approach, SuteAi.Decide(mob, adjacent: false, mob.SutePhaseUntil));

        // Back in reach -> another FULL burst. Every burst above half health is BurstHits, not just the
        // first; shipping a shorter follow-up meant he only ever hit twice after the opening.
        Assert.Equal(SuteAi.Act.Normal, SuteAi.Decide(mob, adjacent: true, mob.SutePhaseUntil));
        Assert.Equal(SuteAi.BurstHits, mob.SuteSwingsLeft);
        Assert.Equal(SuteAi.BurstAttackMs, mob.AttackTime);
    }

    /// <summary>EVERY burst above half health is four swings — not just the opening one.
    ///
    /// <para>This is the regression that shipped: the description ("4 hits … then comes back for two
    /// attacks") was read as a 4-hit opener followed by 2s forever, so in play he opened with four once and
    /// then only ever hit twice. Counting the constant back would not have caught it — the old code was
    /// self-consistent — so this drives three whole cycles and counts the swings he actually takes.</para></summary>
    [Fact]
    public void EveryBurstAboveHalfHealthIsFourSwings()
    {
        var mob = Sute();
        long now = 0;

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            // Close and burst: count swings until he breaks off.
            int swings = 0;
            while (mob.SutePhase != SuteAi.Phase.Retreat && swings < 20)
            {
                var act = SuteAi.Decide(mob, adjacent: true, now);
                if (act != SuteAi.Act.Normal) break;
                SuteAi.OnSwung(mob);
                swings++;
            }
            Assert.Equal(SuteAi.BurstHits, swings);
            Assert.Equal(4, swings);                       // stated outright: the number the user gave

            // Break off, wait him out, and walk him back into reach for the next cycle.
            Assert.Equal(SuteAi.Act.Retreat, SuteAi.Decide(mob, adjacent: false, now));
            mob.SuteRetreatLeft = 0;
            Assert.Equal(SuteAi.Act.Hold, SuteAi.Decide(mob, adjacent: false, now));
            now = mob.SutePhaseUntil;
            SuteAi.Decide(mob, adjacent: false, now);      // hold expires -> Approach
        }
    }

    /// <summary>Wedged into a corner mid-retreat he must not sit there forever waiting for a step that can
    /// never happen — he gives up on backing off and moves to the hold leg, which the cycle can leave.</summary>
    [Fact]
    public void CorneredDuringRetreat_StillReachesTheHoldLeg()
    {
        var mob = Sute();
        SuteAi.Decide(mob, adjacent: true, 0);
        for (int i = 0; i < SuteAi.BurstHits; i++) SuteAi.OnSwung(mob);
        Assert.Equal(SuteAi.Phase.Retreat, mob.SutePhase);

        mob.SuteCornered = true;                          // StepMobAway failed — nowhere to go
        Assert.Equal(SuteAi.Act.Hold, SuteAi.Decide(mob, adjacent: true, 0));
        Assert.Equal(SuteAi.Phase.Hold, mob.SutePhase);
    }

    /// <summary>The middle band is an ordinary fight — no bursts, no backing off — and entering it from a
    /// burst has to put his swing speed back.</summary>
    [Fact]
    public void BetweenQuarterAndHalf_FightsNormallyAndRestoresSwingSpeed()
    {
        var mob = Sute();
        SuteAi.Decide(mob, adjacent: true, 0);
        Assert.Equal(SuteAi.BurstAttackMs, mob.AttackTime);

        mob.Hp = MaxHp * 40 / 100;
        Assert.Equal(SuteAi.Act.Normal, SuteAi.Decide(mob, adjacent: true, 0));
        Assert.Equal(mob.BaseAttackTime, mob.AttackTime);
        Assert.Equal(SuteAi.Phase.Engage, mob.SutePhase);

        // And it stays an ordinary fight — swinging here never starts a retreat.
        for (int i = 0; i < 10; i++) { SuteAi.Decide(mob, adjacent: true, 0); SuteAi.OnSwung(mob); }
        Assert.Equal(SuteAi.Phase.Engage, mob.SutePhase);
    }

    /// <summary>The red bar: he runs. Hitting him buys exactly one answering swing, and only while you are
    /// actually beside him — he does not chase a caster across the room to take it.</summary>
    [Fact]
    public void BelowAQuarter_RunsAndAnswersOnlyInReach()
    {
        var mob = Sute(20);
        Assert.Equal(SuteAi.Act.Retreat, SuteAi.Decide(mob, adjacent: false, 0));
        Assert.Equal(SuteAi.Phase.Flee, mob.SutePhase);

        SuteAi.OnDamaged(mob, 0);
        Assert.Equal(SuteAi.RetaliateHits, mob.SuteSwingsLeft);

        // Owed a swing, but out of reach -> keeps running, and keeps the debt.
        Assert.Equal(SuteAi.Act.Retreat, SuteAi.Decide(mob, adjacent: false, 0));
        Assert.Equal(SuteAi.RetaliateHits, mob.SuteSwingsLeft);

        // In reach -> spends it, then goes straight back to running.
        Assert.Equal(SuteAi.Act.Normal, SuteAi.Decide(mob, adjacent: true, 0));
        Assert.Equal(0, mob.SuteSwingsLeft);
        Assert.Equal(SuteAi.Act.Retreat, SuteAi.Decide(mob, adjacent: true, 0));
    }

    /// <summary>Cornered in the red band he fights — but "cornered" must be a fact about THIS beat, decided
    /// by whether the step succeeded, never a latched flag.
    ///
    /// <para>The bug this pins: <c>Decide</c> used to read <c>SuteCornered</c> and return Normal when it was
    /// set. Returning Normal meant World never entered the branch that recomputes the flag — so one blocked
    /// step latched it forever and a boss on 15% health stood and fought to the death. He must ask to run on
    /// every single beat; World decides whether he actually can.</para></summary>
    [Fact]
    public void CorneredIsNeverLatched()
    {
        var mob = Sute(15);

        // Boxed in, and hit repeatedly — he must STILL be asking to run every beat.
        mob.SuteCornered = true;
        for (int i = 0; i < 50; i++)
        {
            long now = i * 333L;
            SuteAi.OnDamaged(mob, now);
            var act = SuteAi.Decide(mob, adjacent: true, now);
            // Either he is spending an owed answer, or he is trying to run. He may never settle into
            // standing and fighting on his own account.
            if (act == SuteAi.Act.Normal) continue;
            Assert.Equal(SuteAi.Act.Retreat, act);
        }

        // Over a long stretch, running must still dominate even while permanently boxed in.
        int retreats = 0;
        for (int i = 0; i < 60; i++)
        {
            long now = 100_000 + i * 333L;
            SuteAi.OnDamaged(mob, now);
            if (SuteAi.Decide(mob, adjacent: true, now) == SuteAi.Act.Retreat) retreats++;
        }
        Assert.True(retreats > 30, $"cornered and hit, he only tried to run {retreats}/60 beats");
    }

    /// <summary>Healing back over the threshold has to let him fight again — otherwise the self-heal would
    /// leave him permanently stuck in a rout he had climbed out of.</summary>
    [Fact]
    public void HealingOutOfTheRedLeavesTheFleePhase()
    {
        var mob = Sute(20);
        Assert.Equal(SuteAi.Act.Retreat, SuteAi.Decide(mob, adjacent: false, 0));

        mob.Hp = MaxHp * 60 / 100;
        Assert.Equal(SuteAi.Act.Normal, SuteAi.Decide(mob, adjacent: true, 0));
        Assert.Equal(SuteAi.Phase.Engage, mob.SutePhase);
    }

    /// <summary>The self-heal only exists below the red bar, never tops him past full, and respects its own
    /// cooldown — the three things that would turn "a few times a fight" into a stalemate.</summary>
    [Fact]
    public void SelfHealIsGatedByHealthCooldownAndCeiling()
    {
        // Healthy: never, however many times it is rolled.
        var healthy = Sute(80);
        for (int i = 0; i < 500; i++) Assert.False(SuteAi.TryHeal(healthy, i * 1000));

        // Untouched but "in the red" can't happen, but a full-HP mob must never heal regardless.
        var full = Sute();
        full.Hp = full.MaxHp;
        Assert.False(SuteAi.TryHeal(full, 0));

        // Wounded: fires eventually, for exactly HealAmount, and then not again until the cooldown lapses.
        var hurt = Sute(20);
        int before = hurt.Hp;
        long t = 0;
        while (hurt.Hp == before && t < 5_000_000) { SuteAi.TryHeal(hurt, t); t += 1000; }
        Assert.Equal(before + SuteAi.HealAmount, hurt.Hp);

        int afterFirst = hurt.Hp;
        for (long u = t; u < hurt.SuteHealReadyAt; u += 500) SuteAi.TryHeal(hurt, u);
        Assert.Equal(afterFirst, hurt.Hp);

        // And it can never overheal. Needs a creature small enough that one heal would overshoot from inside
        // the red band — Sute himself has 17,000 HP, so his 200-point heal can never reach his ceiling.
        var small = new Mob(2, 110, 5, 5, "Sute", 250) { Key = SuteAi.MobKey };
        small.Hp = 50;                                    // 20% of 250, and 50 + 200 > 250
        long v = 0;
        while (small.Hp == 50 && v < 5_000_000) { SuteAi.TryHeal(small, v); v += 1000; }
        Assert.Equal(small.MaxHp, small.Hp);
    }

    /// <summary>The two zaps are data, so this is the row-level check: they load, they are RANGED (a melee
    /// range would silently turn the boss's signature move into something he can only do in your face), and
    /// they carry the observed damage.</summary>
    [Fact]
    public void TheTwoZapsLoadAsRangedDamageRows()
    {
        EnsureLoaded();

        Assert.True(Content.MobSpells.TryGetValue(SuteAi.MobKey, out var kit), "sute has no MobSpells rows");
        Assert.Equal(2, kit!.Length);

        var ray = kit.Single(s => s.Name == "Ice ray");
        var storm = kit.Single(s => s.Name == "Ice storms");

        foreach (var sp in kit)
        {
            Assert.Equal("damage", sp.Effect);
            Assert.True(sp.Range > 1, $"{sp.Name} is not ranged (Range {sp.Range})");
            Assert.True(sp.Chance > 1, $"{sp.Name} would fire on every cooldown, not rarely (Chance {sp.Chance})");
        }

        // Effects are the user's, and every one of them was caught BY EYE rather than by a test — a wrong
        // id breaks nothing, it just plays the wrong thing. Pinned against the rows they are borrowed from
        // wherever there is one, so a retuned source row carries them along instead of silently diverging.
        //   Ice ray  : the DART TRAP's animation, and SOOTHE's sound.
        //   Ice storms: animation 24, sound 45 — bare numbers, given directly, with no spell row behind them.
        //     These are the only effect ids here that nothing else can validate, so they are asserted flat.
        var dartTrap = Content.SpellFx.Values.FirstOrDefault(f => f.Key == "set_dart_trap");
        var soothe = Content.SpellFx.Values.FirstOrDefault(f => f.Key == "soothe");
        Assert.NotNull(dartTrap);
        Assert.NotNull(soothe);
        Assert.Equal(dartTrap!.Animation, ray.Anim);
        Assert.Equal(soothe!.Sound, ray.Sound);
        Assert.Equal(24, storm.Anim);
        Assert.Equal(45, storm.Sound);

        Assert.Equal(SuteAi.IceRayObservedDamage, ray.Amount);
        Assert.Equal(SuteAi.IceStormsAssumedDamage, storm.Amount);
        Assert.True(storm.Amount > ray.Amount, "Ice storms should hit harder than Ice ray");
    }

    /// <summary>Ice storms has two shouts and Ice ray one — the "|" alternation added for it. A row without
    /// a "|" must keep returning its single line verbatim, since every pre-existing row is that shape.</summary>
    [Fact]
    public void ShoutAlternativesArePickedFromTheList()
    {
        EnsureLoaded();

        var kit = Content.MobSpells[SuteAi.MobKey];
        var ray = kit.Single(s => s.Name == "Ice ray");
        var storm = kit.Single(s => s.Name == "Ice storms");

        Assert.Equal("Feel my power!", ray.PickSay());     // single line, unchanged

        var expected = storm.Say.Split('|');
        Assert.Equal(2, expected.Length);
        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++) seen.Add(storm.PickSay());
        Assert.Subset(expected.ToHashSet(), seen);         // never invents a line…
        Assert.Equal(2, seen.Count);                       // …and does use both
    }

    /// <summary>The three fleeing behaviours in this world — prey, the wounded rout, and Sute — all express
    /// running away as TILES PER TURN through one shared <c>World.Dart</c>, so their sizes belong together.
    ///
    /// <para>This does not exercise the movement itself (nothing in this suite drives World.Tick — it needs a
    /// map, sessions and sockets). It pins the numbers and, more usefully, the arithmetic the prey comment
    /// rests on: the switch from a shortened timer to a two-tile dart was justified by a rabbit covering the
    /// SAME ground per second either way, and that argument silently dies if anyone retunes its
    /// MobMoveTime.</para></summary>
    [Fact]
    public void TheThreeFleeDartsAgreeOnTilesPerTurn()
    {
        EnsureLoaded();

        // The prey creatures the dart is tuned for, and the pace claim about them.
        var rabbit = Content.MobByKey("rabbit");
        Assert.NotNull(rabbit);
        int rabbitPace = rabbit!.MoveTime;

        Assert.Equal(2, World.PreyDartTiles);              // rabbit / blue rooster — the user's observation
        Assert.Equal(3, World.RoutDartTiles);              // RTK's literal mob:move() x3

        // Sute is the EXCEPTION: he does not hop. He steps one tile at a time, and his speed comes from a
        // fast MobMoveTime plus the act/act/rest beat — "a normal base, just fast like a player", walking
        // rather than teleporting.
        Assert.Equal(1, SuteAi.StepTilesPerTurn);
        var sute = Content.MobByKey("sute");
        Assert.NotNull(sute);
        Assert.Equal(SuteAi.ActMs, sute!.MoveTime);
        Assert.True(sute.MoveTime < rabbitPace, "Sute should outpace ordinary mobs");

        Assert.True(rabbit!.Flees, "rabbit is no longer prey — MobFlees.csv changed");
        Assert.Equal(3000, rabbit.MoveTime);

        // "2 tiles / 3000ms == 1 tile / 1500ms" — the old shortened-timer pace it replaced (MoveTime / 2).
        Assert.Equal(rabbit.MoveTime / 2, rabbit.MoveTime / World.PreyDartTiles);

        var rooster = Content.MobByKey("blue_rooster");
        Assert.NotNull(rooster);
        Assert.True(rooster!.Flees, "blue rooster is no longer prey — MobFlees.csv changed");
    }

    /// <summary>His action rhythm: act, act, rest — twice a second at a 333ms beat, for BOTH movement and
    /// swings. The rest beat is the whole point; without it he is a steady stream rather than the paired
    /// bursts that were described.</summary>
    [Fact]
    public void HeActsOnTwoBeatsOutOfEveryThree()
    {
        var mob = Sute();
        var pattern = new List<bool>();
        for (int i = 0; i < 12; i++) pattern.Add(SuteAi.RestBeat(mob));

        // act, act, rest — repeating, starting from a fresh mob.
        Assert.Equal(new[] { false, false, true, false, false, true,
                             false, false, true, false, false, true }, pattern);

        // Two actions per three beats at 333ms = twice a second.
        Assert.Equal(SuteAi.BeatsOn * SuteAi.ActMs, 666);
        Assert.Equal(SuteAi.BeatsPerCycle * SuteAi.ActMs, 999);   // ~one second per cycle
    }

    /// <summary>The rhythm is only expressible if the world beat matches it. This is the constraint that
    /// forced World.TickMs down from 600 to 333: a mob steps at most once per beat, so at 600 the fastest
    /// possible creature acted 1.7 times a second and could not represent him at all.</summary>
    [Fact]
    public void TheWorldBeatCanExpressHisCadence()
    {
        EnsureLoaded();

        var sute = Content.MobByKey("sute");
        Assert.NotNull(sute);
        Assert.Equal(SuteAi.ActMs, sute!.MoveTime);        // one tile per acting beat
        Assert.Equal(SuteAi.ActMs, SuteAi.BurstAttackMs);  // one swing per acting beat

        // Four swings arrive as hit-hit-rest-hit-hit — the "4 hits within 2 seconds" originally reported.
        int beatsForBurst = SuteAi.BurstHits + (SuteAi.BurstHits - 1) / SuteAi.BeatsOn;
        Assert.InRange(beatsForBurst * SuteAi.ActMs, 1, 2000);
    }

    /// <summary>The cold tiles cover the whole cave, and their damage is the observed figure.</summary>
    [Fact]
    public void ColdTilesCoverEveryCaveRoom()
    {
        EnsureLoaded();

        Assert.Equal(new ushort[] { 441, 442, 443, 444, 445, 446, 447 }, SuteAi.CaveMaps);
        foreach (var id in SuteAi.CaveMaps)
            Assert.True(Content.Maps.ContainsKey(id), $"cave room {id} is missing");
        Assert.True(SuteAi.FrigidTrapsPerMap > 0);

        // The self-heal must LOOK like a heal. It shipped once as the ice-glare animation, which is the kind
        // of mistake nothing else catches: the heal still worked, it just drew an attack over him.
        var soothe = Content.SpellFx.Values.FirstOrDefault(f => f.Key == "soothe");
        Assert.NotNull(soothe);
        Assert.Equal(SuteAi.HealAnim, soothe!.Animation);
        Assert.Equal(SuteAi.HealSound, soothe.Sound);
        Assert.Equal(257, SuteAi.FrigidDamage);
    }
}
