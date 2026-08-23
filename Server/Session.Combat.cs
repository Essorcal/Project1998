using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // Client attack (0x13, spacebar) = just a trigger ("13 00"). Reply with an ACTION packet 0x1A
    // so the entity plays the swing. (0x13 was WRONG — its handler 0x4508f0 computes anim = 0x8f-a,
    // and a=0 -> anim 0x8f = the DEATH animation, which is why the character flashed "dead".)
    // 0x1A = entityId(u32BE) type(u8) time(u16BE) param(u8); handler 0x4503a0 plays the action
    // (client scales time x10). type: 0=stand,1=attack,2=throw,3=shot,4=sit,6=magic,8=eat.
    // RTK's swing rate. `pc_calcstat` sets attack_speed = 20 FLAT for everyone (pc.c:849) and the 14-slot
    // equip loop never adds to it — itemdb HAS an attack_speed field and an accessor, but nothing reads it
    // into the player, and our Items.csv has no such column. So there is NO per-weapon swing speed in this
    // game: weapons differ by damage and hit, never by rate. The only writers are Lua `player.attackSpeed`
    // (sl.c:6877), used by exactly two things in the whole RTK tree (a 7.x shotgun and the beach_war event),
    // so a constant is the honest representation until something needs to modify it.
    private const int AttackSpeed = 20;                                   // RTK floors this at 3
    private const int SwingIntervalMs = AttackSpeed * 1000 / 60;          // = 333ms (clif.c:11450)

    // ---- THE SHARED CAST/SWING SLOT ----------------------------------------------------------------------
    // One tick governs BOTH melee swings and spells that carry a cast delay: casting a zap blocks the next
    // swing, and swinging blocks the next zap. Confirmed by the user testing live ("I cannot attack and cast
    // Singe in the same 1s window") and independently by a player feature request on the official Dreams
    // board asking that Taunt and Invisible "no longer ha[ve] a cast delay" so they don't "interfere with
    // swinging" — you only ask for that if the interference is real. See Content.CastDelayMs for the full
    // source list and for which spells carry a delay.
    //
    // RTK has no such coupling: its swing gate (`sd->attacked` + pc_atkspeed) and its cast path never consult
    // each other, and there is no cast-delay field on the user struct at all. The one place RTK DOES tie them
    // together is the 3/sec action budget, where a swing pays in without being limited by it — that stays,
    // and is separate from this. So this slot is a 4.95/7.x rule RTK dropped, sourced from live rather than code.
    //
    // Named _nextActionTick (was _nextSwingTick) because it is no longer only about swinging. A spell with a
    // 0ms delay — every heal, buff, and the dog 5-way — neither waits on this nor arms it, and so is still
    // free to be cast mid-swing at the ordinary 3/sec.
    // THE GRACE WINDOW. A swing and a delayed cast issued close enough together BOTH resolve, in either
    // order — user, from live: "invis -> atk and atk -> invis work the same, they can both stack if cast
    // fast enough", and going slower gets the swing blocked for the rest of the second. So the slot is not
    // a hard edge; one companion action may follow the claim that opened it, and only one.
    //
    // The tell that this is real (and that the two resolve in ARRIVAL ORDER rather than being merged) is
    // what the user sees on atk -> invis: the character appears to strike and vanish together, but the swing
    // gets NO 5x sneak bonus and the stealth SURVIVES the hit. That is precisely a swing resolving before
    // the buff is applied — Session.PlayerSwingDamage reads `wasStealthed` up front and only strips stealth
    // `if (wasStealthed)`. We reproduce that artifact for free by letting both through in order; it needs no
    // special case, and it must NOT be "fixed".
    //
    // 100ms is INFERRED. The real mechanism is unknown -- it could be a genuine grace period, or the live
    // server batching a tick's worth of packets and resolving both before asserting the block. The value only
    // has to be wide enough for two keys mashed together (the client repeats a held key every ~31ms) and far
    // below the 333ms swing interval, so no ordinary rhythm reaches it. Measurable with re/spell_rate_probe.py.
    private const int ActionSlotGraceMs = 100;

    private long _nextActionTick;
    private long _slotArmedAt;       // when the STANDING claim was opened (not extended)
    private bool _slotGraceUsed;     // a companion action already rode this claim in

    /// <summary>Is the shared cast/swing slot free right now — either genuinely expired, or still inside the
    /// grace window of a claim that hasn't yet carried a companion action?</summary>
    internal bool ActionSlotReady
    {
        get
        {
            long now = Environment.TickCount64;
            if (now >= _nextActionTick) return true;
            return !_slotGraceUsed && now - _slotArmedAt <= ActionSlotGraceMs;
        }
    }

    /// <summary>Milliseconds left on the shared slot (0 if it's free).</summary>
    internal long ActionSlotLeft => Math.Max(0, _nextActionTick - Environment.TickCount64);

    /// <summary>Occupy the shared cast/swing slot for <paramref name="ms"/>. A longer claim never shortens a
    /// standing one — a 1s cast delay must not be cut to 333ms by a swing that stacked inside it, which is
    /// why "invis then swing" still leaves you unable to swing again until the full second is up.</summary>
    internal void ArmActionSlot(int ms)
    {
        long now = Environment.TickCount64;
        if (now < _nextActionTick)
            _slotGraceUsed = true;                       // rode in on the grace window; nobody else may
        else
        {
            _slotArmedAt = now;                          // a fresh claim reopens the window
            _slotGraceUsed = false;
        }
        long until = now + ms;
        if (until > _nextActionTick) _nextActionTick = until;
    }

    // ---- who this player is currently trading blows with, in PvP -----------------------------------------
    // Set on BOTH sides of a player-vs-player exchange (see ReceiveSpellDamage for spells and ReceiveMeleeDamage
    // for melee — both PvP damage paths mark the foe). Exists so a Poet's pets know who to go for on a
    // PK map: RTK's own cotw AI refuses player targets outright (`blType == BL_PC -> return`), which is right
    // for the open world and wrong for an arena. It EXPIRES so a pet doesn't chase a grudge across the map
    // long after the fight moved on.
    private const int PvpFoeMs = 15_000;
    private uint _pvpFoeId;
    private long _pvpFoeUntil;
    /// <summary>The player this one is currently fighting, or 0 if that's gone stale.</summary>
    internal uint PvpFoeId => Environment.TickCount64 < _pvpFoeUntil ? _pvpFoeId : 0;
    internal void MarkPvpFoe(uint playerId) { _pvpFoeId = playerId; _pvpFoeUntil = Environment.TickCount64 + PvpFoeMs; }

    // RTK's `owner.attacker` — the last CREATURE to land a blow on this player, stamped by ApplyMobHit. Read
    // by the pet AI as the second half of "things that are attacking you"; the first half (things you have
    // attacked) is already recorded as threat on the mobs themselves. It expires for the same reason PvpFoeId
    // does: a pet should defend you from what is happening, not avenge something you walked away from
    // minutes ago.
    private const int MobAttackerMs = 30_000;
    internal uint LastMobAttackerId;
    internal long LastMobAttackerAt;
    /// <summary>The creature that last hit this player, or 0 once that has gone stale.</summary>
    internal uint RecentMobAttackerId =>
        Environment.TickCount64 < LastMobAttackerAt + MobAttackerMs ? LastMobAttackerId : 0;

    private void HandleAttack(byte[] dec)
    {
        if (_char.Hp == 0) { SendMiniText("Spirits can't attack."); return; }

        // SLEEP GATE (the Doze family's PvP branch — see Session.ReceiveSleep). A hold can't stop a 4.95
        // client walking, but it can take every action that goes through the server, and a swing is one.
        if (Asleep) { SendMiniText("You are asleep."); return; }

        // MOUNT GATE. No melee from horseback. SILENT, unlike the cast gate's minitext — the live client
        // shows nothing at all for a blocked swing, matching how an over-rate swing is dropped below.
        // Dropped before the swing gate so a mounted attack key doesn't consume the swing interval either.
        if (_char.Mounted) return;

        // SWING GATE. Holding the attack key makes the client send 0x13 about every 31ms — the same repeat
        // rate measured for casting — so without this every one of those ~31 packets/second resolved a FULL
        // swing: damage, crit roll, weapon procs, durability, on every reachable tile. That is ~10x the real
        // melee rate. RTK gates it with an `attacked` flag cleared by a pc_atkspeed timer (clif.c:11448);
        // a next-allowed tick is the same thing without a timer per session. Over-rate swings are dropped
        // SILENTLY, as in RTK (its else branch is commented out) — no animation, no message.
        // NOTE the action-budget bump for 0x13 stays at the dispatch: RTK does `sd->time++` unconditionally
        // and only then consults this gate, so a spammed attack key still burns the shared cast allowance.
        // NOT YET MEASURED whether the 4.95 client paces 0x13 itself the way it clearly does NOT pace 0x0F.
        // If this line never appears while holding the attack key, the client is self-limiting and this gate
        // is inert belt-and-braces; if it floods, the gate is load-bearing.
        // The gate is the SHARED cast/swing slot, so a zap's 1s cast delay drops swings for that whole second
        // as well — that coupling is the point (see _nextActionTick).
        if (!ActionSlotReady) { Log.Info($"   -- swing dropped: {ActionSlotLeft}ms left of the shared cast/swing slot"); return; }
        ArmActionSlot(SwingIntervalMs);

        // Swing pose length == the swing interval, which is why RTK passes attack_speed as the action `time`.
        SendAction(_char.Id, type: 1, time: AttackSpeed, param: 0);                                 // our own swing anim
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, 1, AttackSpeed, 0), except: this);  // peers see us swing

        // Weapon swing sfx: the client plays no sound for the swing action itself, so send one over 0x19 on
        // EVERY swing, armed or not — weapon in hand -> its own ItmSound (RTK's per-weapon mapping — most
        // swords 331, Sword of power 337, …); bare hands -> the calibratable fist fallback (_fistSfx, see its
        // doc — no real RTK id exists to port for "no weapon"). @swingsnd overrides either case for
        // calibration. Everyone within earshot hears it, bound to us — RTK's clif_playsound is a SAMEAREA
        // send (the +/-9/+/-8 box around the swinger), not a map-wide one.
        int weaponSwing = EquippedWeaponSound();
        int swing = _swingSfx > 0 ? _swingSfx : weaponSwing > 0 ? weaponSwing : _fistSfx;
        if (swing > 0) _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.SoundAt(swing, _char.Id));

        FireWeaponProcs();   // RTK item on_swing: rolls before damage resolves, and on a miss too

        // ONE swing can land on SEVERAL tiles. The faced tile always; the tile BEHIND us while the Backstab
        // stance is armed; ONE side tile while Flank is — so a Warrior holding both reaches 3 targets at
        // once. Each is an INDEPENDENT attack: its own hit/crit roll and its own damage, so any mix of
        // hits and misses is possible. See SwingTargets for sourcing and the per-tile damage scales.
        foreach (var (tile, reach) in SwingTargets())
            ResolveSwing(tile.x, tile.y, reach);
    }

    // Resolve ONE independent attack against whatever creature stands on (x,y). Called once per reachable
    // tile by HandleAttack. Check the SHARED world FIRST — HP there is world-authoritative so two players
    // can't double-kill and both claim the reward — then fall back to session-local debug dummies
    // (look-lab / @cre / @mob sweeps, visible only to us).
    // `reach` scales this target's damage (1.0 faced tile, <1 for a Backstab/Flank extension).
    // Durability is deducted PER LANDED HIT, not per swing: the board note is "the dura you lose on your
    // hand held weapon when you hit your target", so a 3-target swing that connects 3 times costs 3.
    private void ResolveSwing(int fx, int fy, double reach)
    {
        var wmob = _world.MobAt(_char.Map, fx, fy);
        if (wmob is not null)
        {
            // YOUR OWN CREATURE IS A LEGITIMATE TARGET. There used to be an `if (wmob.OwnerId == _char.Id)
            // return;` here, so a swing at anything you owned was silently eaten. It was well-intentioned —
            // a pet standing between you and a monster shouldn't soak the swing meant for the monster — but
            // it made two things unplayable:
            //   * a CHARM weapon could not kill anything. Its 4% on_swing proc endears whatever you are
            //     facing (see FireWeaponProcs / the `endear` verb) for 40-45s, and for all of that time
            //     every swing at the mob returned right here. Re-procs refreshed it. The weapon read as
            //     "melee does nothing", which is exactly what was reported.
            //   * you could not put down your own Call of the Wild summons, so a badly-placed pet was a
            //     wall you had to wait out.
            // A pet in the way is the price of placing it there — that is what makes summons a barricade.
            // Nothing else changes: TryDamage already let OTHER players hit your pets.

            // Prey (rabbit / blue rooster) bolts at being swung at, landed or not — this is BEFORE the hit roll
            // on purpose, since a whiff is exactly as alarming as a connect. No-op for every other creature.
            _world.Spook(wmob);

            var (dmg, crit) = PlayerSwingDamage(SwingTarget.Of(wmob), reach);
            if (dmg <= 0) return;   // whiff (Combat.RollPlayerSwingRtk) — swing anim already played; no damage/dura/reward, and no text
            if (_world.TryDamage(_char.Map, wmob, dmg, out bool died, _char.Id))
            {
                var weapon = _char.Equipment.FirstOrDefault(e => e.Slot == 1);   // EQ_WEAP: deductWeapon(rage) on a landed swing
                if (weapon is not null) DeductDura(weapon);
                ShowDamageResult(wmob.Id, wmob, died, crit ? (byte)0xFF : HitCritByte);   // 0x13: over-head HP bar + hit anim (empty bar + delayed despawn on death)
                PlayHitSfx(wmob.Id);                                                      // 0x19: on-connect impact sfx, on top of the swing sfx above
                Log.Info($"   -> hit world mob {wmob.Id} '{wmob.Name}' for {dmg}{(crit ? " (CRIT)" : "")} -> {wmob.Hp}/{wmob.MaxHp}");
                if (died)
                {
                    // A CONJURED creature (Mob.Summoned — a CotW pet, a Giasomo bird) pays NOTHING when it
                    // dies: no exp, no quest tally. It was made out of mana seconds ago, and now that its
                    // owner can hit it, paying out would be a summon-and-kill exp loop. An ENDEARED creature
                    // is a real world mob that was always standing there, so it pays normally — charming it
                    // first can't be worth more than walking up and killing it.
                    if (wmob.Summoned)
                    {
                        SendMessage($"You defeated {wmob.Name}.");
                        Log.Info($"   -> summoned mob {wmob.Id} '{wmob.Name}' destroyed (conjured — no exp)");
                    }
                    else
                    {
                        uint reward = (uint)(wmob.Exp > 0 ? wmob.Exp : wmob.MaxHp);   // real mob Exp; fallback to HP
                        AwardKillExp(reward, _char.Map, wmob.X, wmob.Y, wmob.Key);                   // exp AND quest credit: killer + any group member in range
                        SendMessage($"You defeated {wmob.Name}. (+{reward} exp)");
                        Log.Info($"   -> world mob {wmob.Id} '{wmob.Name}' defeated (+{reward} exp)");
                    }
                }
            }
            return;
        }

        // A PLAYER on the tile is a target too, but ONLY in a PvP area (RTK gates its PC branch on canPK, which
        // is our IsPvpMap). Never yourself, and never a ghost (dead players don't take hits). A living body still
        // STOPS the swing whether or not the hit was legal — RTK returns after the PC branch either way — so a
        // non-PvP bystander soaks the blow (no damage) instead of the swing passing through to a dummy behind them.
        var pc = _world.PeerAt(_char.Map, fx, fy);
        if (pc is not null)
        {
            if (!ReferenceEquals(pc, this) && !pc.IsDead && Content.IsPvpMap(_char.Map))
            {
                var (pdmg, pcrit) = PlayerSwingDamage(SwingTarget.Of(pc), reach);   // target AC + positional already applied
                if (pdmg > 0)
                {
                    var weapon = _char.Equipment.FirstOrDefault(e => e.Slot == 1);   // EQ_WEAP: dura on a landed hit, same as vs a mob
                    if (weapon is not null) DeductDura(weapon);
                    PlayHitSfx(pc._char.Id);                                          // 0x19: our on-connect impact sfx
                    pc.ReceiveMeleeDamage(pdmg, this, pcrit);                         // HP/death/PvP-foe/HP-bar on the defender side
                    Log.Info($"   -> PvP MELEE hit player {pc._char.Id} '{pc._char.Name}' for {pdmg}{(pcrit ? " (CRIT)" : "")}");
                }
            }
            return;   // a body blocks the swing regardless of whether the hit landed
        }

        var mob = MobAt(fx, fy);
        if (mob is null) return;

        var (dummyDmg, dummyCrit) = PlayerSwingDamage(SwingTarget.Of(mob), reach);
        if (dummyDmg <= 0) return;   // whiff — silent, no text
        mob.Hp -= dummyDmg;
        bool dummyDied = !mob.Alive;
        SendDamage(mob.Id, dummyDied ? (byte)0 : HpPercent(mob), dummyCrit ? (byte)0xFF : HitCritByte);   // 0x13: over-head HP bar + hit anim (dummy is session-local)
        if (_hitSfx > 0) SendSound(_hitSfx, mob.Id);                                                     // 0x19: on-connect impact sfx (self-only — so is the dummy)
        Log.Info($"   -> hit dummy {mob.Id} '{mob.Name}' for {dummyDmg}{(dummyCrit ? " (CRIT)" : "")} -> {mob.Hp}/{mob.MaxHp}");
        if (dummyDied)
        {
            _mobs.Remove(mob);
            uint deadId = mob.Id;
            if (DeathDespawnMs <= 0) SendDespawn(deadId);   // 0x0E: remove the corpse from our client
            else _ = Task.Run(async () => { try { await Task.Delay(DeathDespawnMs); SendDespawn(deadId); } catch { } });   // after the death beat
            AwardExp((uint)mob.MaxHp, killExp: true);  // reward: exp equal to the mob's max HP (levels too)
            SendMessage($"You defeated {mob.Name}. (+{mob.MaxHp} exp)");
            Log.Info($"   -> dummy {mob.Id} '{mob.Name}' defeated");
        }
    }

    // RTK gives some gear an `on_swing` handler (rtklua/Accepted/Items/**): a percentage roll per swing
    // that casts a spell at whatever you're facing — Blood/venom, Charm/endear, Frost sabre/chill, and the
    // Giasomo stick, whose proc summons a Giasomo bird onto the caster instead of hitting the target.
    // Table: game-data/WeaponProcs.csv (hot-reloads with @reload).
    //
    // Fired on the SWING, not on a landed hit, matching RTK — on_swing runs before any damage roll, so a
    // miss can still proc. Every equipped piece is checked, not just the weapon: chaos_armor procs too.
    // The cast goes through the normal ApplyCast dispatcher so a proc behaves exactly like casting the
    // spell by hand (mana, PK gating, cooldowns, fx) rather than a parallel code path that could drift.
    //
    // RTK's blood/charm handlers branch on player.backstab/player.flank and proc on every target returned by
    // getTargetsBackstab/getTargetsFlank instead of the faced one. Our engine resolves a swing against ONE
    // tile — SwingTile, which flank redirects to a side rather than multiplying damage — so that branch
    // collapses onto the same single target this loop already uses. Nothing to port.
    //
    // A `spell` value of "builtin:<name>" runs an inline handler instead of casting: RTK writes a couple of
    // these procs as raw Lua in the item file with no spell behind them (shot_gun's cone, viper_stick's
    // paralyze), so there is no spell key to name.
    private void FireWeaponProcs()
    {
        if (_char.Equipment.Count == 0) return;
        var (fx2, fy2) = FrontTile();
        uint? facingId = null;

        foreach (var worn in _char.Equipment.ToArray())
        {
            var def = Content.ItemById(worn.ItemId);
            if (def is null) continue;
            if (Content.WeaponProcFor(def.Key) is not { } proc) continue;
            if (Random.Shared.Next(100) >= proc.ChancePct) continue;

            if (proc.Spell.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
            {
                string builtin = proc.Spell["builtin:".Length..];
                switch (builtin)
                {
                    case "shotgun":  ProcShotgun(worn); break;
                    case "paralyze": ProcParalyze(fx2, fy2); break;
                    default: Log.Info($"   -> weapon proc '{def.Key}' names unknown builtin '{builtin}' — skipped"); break;
                }
                continue;
            }

            var sp = Content.SpellByKey(proc.Spell);
            if (sp is null)
            {
                Log.Info($"   -> weapon proc '{def.Key}' wants spell '{proc.Spell}' which isn't loaded — skipped");
                continue;
            }

            if (proc.NeedsFacing)
            {
                facingId ??= _world.MobAt(_char.Map, fx2, fy2)?.Id
                             ?? _world.PeerAt(_char.Map, fx2, fy2)?._char.Id;
                if (facingId is null) continue;   // nothing in front — RTK's getTargetFacing returns nil
            }
            uint? target = proc.SelfCast ? _char.Id : facingId;

            Log.Info($"   -> weapon proc: {def.Key} ({proc.ChancePct}%) casts {sp.Key} on {target}");
            try { ApplyCast(sp, target); }
            catch (Exception ex) { Log.Info($"   -x weapon proc {def.Key} -> {sp.Key} failed: {ex.Message}"); }
        }
    }

    // Viper stick's proc (RTK Items/Weapons/viper_stick.lua): a flat 2s paralyze on whatever you're facing.
    // RTK writes it inline — `mob:setDuration("paralyze", 2)` — rather than casting anything, and guards on
    // checkIfCast(paras) so it never refreshes an existing hold. Mob.FrozenUntil IS our paralyze (World.Tick
    // skips movement while it runs), which is the same field the Debuff archetype sets.
    private void ProcParalyze(int fx2, int fy2)
    {
        var mob = _world.MobAt(_char.Map, fx2, fy2);
        if (mob is null || mob.IsNpc) return;
        if (mob.FrozenUntil > Environment.TickCount64) return;   // RTK checkIfCast(paras)
        mob.FrozenUntil = Environment.TickCount64 + 2000;
        SendMiniText("You cast paralyze.");
        Log.Info($"   -> weapon proc: viper stick paralyzes mob {mob.Id} '{mob.Name}' for 2000ms");
    }

    // Shot gun's proc (RTK Items/Weapons/shot_gun.lua). A GM/dev item — Items.csv 60005, inside the block that
    // starts with the literal "===GM ITEMS===" separator row — so this is a test toy, not obtainable content.
    //
    // It turns each swing into a RANGED cone: walk up to 7 tiles along the facing direction, damage the first
    // creature found, and stop at the first impassable tile. Damage is a per-equip RAMP: a counter starts at 1
    // when the weapon goes on and climbs by 1 every swing, so the gun gets stronger the longer you hold it and
    // resets only on unequip. That is RTK's actual behaviour, unbounded and all — kept verbatim rather than
    // "fixed", since a GM item's whole point is the extreme.
    //
    // Two RTK details deliberately NOT ported: its sendAnimation ids (423/332/424) and playSound ids
    // (59/371/351/347/348) are from the LATER client's id space — 4.95 only has effects 0-120, and its sound
    // table is separately calibrated (see the protocol doc) — so firing them would draw garbage. And RTK calls
    // removeHealthExtend TWICE per target, once around its threat bookkeeping and once to apply; with no C
    // engine to tell us whether mode 0 vs mode 2 both deal damage, this applies the damage ONCE.
    private InvItem? _shotgunWorn;   // the gun instance the ramp below belongs to (see ProcShotgun)
    private void ProcShotgun(InvItem worn)
    {
        // RTK resets the counter in onEquip/onUnequip. We have no equip hooks, so the equivalent is to notice
        // the worn INSTANCE changed: taking the gun off returns it to the bag and re-equipping builds a fresh
        // InvItem, so a different reference means a fresh equip and the ramp restarts at 1.
        if (!ReferenceEquals(_shotgunWorn, worn)) { _shotgunWorn = worn; LuaSetReg("damage_shotgun", 0); }
        int dam = LuaReg("damage_shotgun") + 1;
        LuaSetReg("damage_shotgun", dam);

        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);

        for (int i = 1; i <= 7; i++)
        {
            int tx = _char.X + dx * i, ty = _char.Y + dy * i;
            if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) return;

            var mob = _world.MobAt(_char.Map, (ushort)tx, (ushort)ty);
            if (mob is not null && !mob.IsNpc)
            {
                // Armor, same as every other ranged/spell hit. Its PvP branch below gets this for free inside
                // ReceiveSpellDamage, so without it here the same proc would net against a player and not
                // against a mob.
                if (_world.TryDamage(_char.Map, mob, Combat.ApplyArmor(dam, mob.Ac, floor: -95), out bool died, _char.Id))
                {
                    ShowDamageResult(mob.Id, mob, died, HitCritByte);
                    Log.Info($"   -> weapon proc: shot gun hits mob {mob.Id} '{mob.Name}' at range {i} for {dam} raw (ac {mob.Ac})");
                    if (died)
                    {
                        uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
                        AwardKillExp(reward, _char.Map, mob.X, mob.Y, mob.Key);
                        SendMessage($"You defeated {mob.Name}. (+{reward} exp)");
                    }
                }
                return;
            }

            var pc = _world.PeerAt(_char.Map, tx, ty);
            if (pc is not null)
            {
                // RTK gates the PC branch on canPK and returns either way — a body stops the shot whether or
                // not the hit was legal.
                if (!ReferenceEquals(pc, this) && Content.IsPvpMap(_char.Map))
                {
                    pc.ReceiveSpellDamage(dam, this, "Shot gun");
                    Log.Info($"   -> weapon proc: shot gun hits player {pc._char.Id} at range {i} for {dam}");
                }
                return;
            }

            if (md is not null && md.BlockedMove(tx, ty, _facing)) return;   // RTK getPass > 0 — the shot hits a wall
        }
    }

    // 0x1d = the emote wheel (press ':'), body[0] = emote index. The client plays action
    // (index + 11) — see RTK clif_parseemotion: sendaction(&bl, RFIFOB(5)+11, 0x4E, 0). The +11 maps
    // index 0 -> action 11 (Laughter) ... index 11 -> action 22 (Dance) ... index 13 -> 24 (Kiss).
    // Broadcast it as a 0x1A action so we AND every peer on the map see the animation (the client's own
    // action sprite carries any looped sound). time 0x4E matches RTK's emote length; param 0 = no extra sound.
    private void HandleEmotion(byte[] dec)
    {
        if (dec.Length < 1) return;
        byte action = (byte)(dec[0] + 11);
        const ushort time = 0x4E;
        SendAction(_char.Id, action, time, 0);                                       // play it on our own client
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.ActionOver(_char.Id, action, time, 0), except: this);  // and for peers
        Log.Info($"   -> EMOTE idx={dec[0]} -> action {action} (0x1A)");
    }

    // The map tile one step ahead of the player, in the direction we're currently facing.
    private (int x, int y) FrontTile()
    {
        int x = _char.X, y = _char.Y;
        switch (_facing & 3) { case 0: y--; break; case 1: x++; break; case 2: y++; break; case 3: x--; break; }
        return (x, y);
    }

    // One tile from (x,y) in direction dir (0=N/1=E/2=S/3=W). FrontTile() is Step(pos, _facing).
    private static (int x, int y) Step(int x, int y, int dir) => (dir & 3) switch
    {
        0 => (x, y - 1),
        1 => (x + 1, y),
        2 => (x, y + 1),
        _ => (x - 1, y),
    };

    private static int Opposite(int dir) => (dir + 2) & 3;
    private static int LeftOf(int dir)   => (dir + 3) & 3;
    private static int RightOf(int dir)  => (dir + 1) & 3;

    private bool TileHasMob(int x, int y) =>
        _world.MobAt(_char.Map, x, y) is not null || MobAt(x, y) is not null;

    /// <summary>POLEARM extended reach — RTK <c>swing.lua</c>'s <c>extendHit</c>, documented in its
    /// <c>LUA Help File.txt</c> as "extended hitting (polearm + normal)". A polearm turns each attack
    /// direction into a 3-wide arc at range: the tile TWO out, plus the two tiles diagonally flanking the
    /// near one (<c>mobUpExtend</c> (x,y-2), <c>mobUpLeftExtend</c> (x-1,y-1), <c>mobUpRightExtend</c>
    /// (x+1,y-1) when facing north). It is gated on the NEAR tile being occupied, i.e. the polearm strikes
    /// THROUGH whatever it hits first. That gating is why the whole mechanic hangs off
    /// <see cref="AddReachTargets"/> below rather than being a separate pass.
    ///
    /// ALWAYS FALSE FOR NOW — polearms arrive late in 5.x, and more practically our Items.csv has no column
    /// identifying one (49 fields, none of them a weapon class or reach flag). The weapons themselves DO
    /// exist in 4.95 data (long_spear, frozen_spear, bekyuns_spear, destiny_spear, and the whole Chongun
    /// spike line il_san..sa_san), so turning this on is a DATA problem, not a logic one: add a column (or a
    /// curated identifier list) and return it from here. The reach logic is already written and correct.</summary>
    private bool ExtendHit => false;

    /// <summary>Add every tile reachable in one attack direction, at the damage scale that reach carries.
    /// The near tile must hold something or NOTHING in this direction is struck — including the polearm
    /// extension, which RTK gates the same way (it strikes through a target, not past empty air).</summary>
    private void AddReachTargets(List<((int x, int y) tile, double reach)> into, int dir, double reach)
    {
        var near = Step(_char.X, _char.Y, dir);
        if (!TileHasMob(near.x, near.y)) return;
        into.Add((near, reach));

        if (!ExtendHit) return;
        foreach (var t in new[] { Step(near.x, near.y, dir),                 // two tiles out
                                  Step(near.x, near.y, LeftOf(dir)),        // and the two diagonals
                                  Step(near.x, near.y, RightOf(dir)) })
            if (TileHasMob(t.x, t.y)) into.Add((t, reach));
    }

    /// <summary>Every tile this swing can land on, each with the damage scale its reach carries. ONE swing
    /// hits ALL of them — the caller resolves each as a fully INDEPENDENT attack (its own hit/crit roll and
    /// its own damage), so a Warrior holding both stances swings at 3 targets and any of them may miss.
    ///
    /// BACKSTAB AND FLANK ARE TARGETING SPELLS, NOT DAMAGE MULTIPLIERS (2026-08-04). They EXTEND which
    /// neighbouring tile a swing reaches, at reduced damage. Warrior-Tutor board post (boards.nexustk.com
    /// /Warriors/, text carried forward by Deimos and SoulHunter) and nexusatlas 2003:
    ///     Backstab (lvl 15) "Enables to Attack from warrior's back"    -> REAR tile
    ///     Flank    (lvl 20) "Enables to Attack to the Warrior's Sides" -> SIDE tiles
    /// RTK agrees on both the targeting AND the multi-target shape: its own <c>backstab.lua</c> description
    /// is literally "Strikes an enemy behind you", and <c>swing.lua</c> calls <c>swingTarget</c> over extra
    /// target SETS (mobDown/mobLeft/...) gated on player.backstab — additively, not instead of the faced
    /// one. So RTK's <c>swingDamage.lua</c> x2 angle tables were a mis-port of a reach feature into a damage
    /// one. Both <see cref="Combat.IsBackstabAngle"/> and <see cref="Combat.IsFlankAngle"/> are retired;
    /// they could never fire anyway (their positional tests are inverted vs where a struck tile actually
    /// sits).
    ///
    /// DO NOT CONFUSE with the POSITIONAL mechanic of the same nickname: striking a target that has its back
    /// to the blow is x2, always, for anyone (<see cref="Combat.IsBehindTarget"/>). That is a property of the
    /// TARGET's facing; these two spells are about which tile the attacker can reach. A backstab-spell swing
    /// into a mob that is also facing away earns BOTH — reach 0.5 and then the positional x2.
    ///
    /// DAMAGE SCALE 0.5: nexusatlas 2003 gives 50% for BOTH spells, and the (later) Warrior Tutor post gives
    /// flank 50% / backstab 95%. Earliest-source-wins => 0.5 for both. Flank's 50% is the better attested of
    /// the two (two independent sources); backstab's is a single early source contradicted later, so if one
    /// number here is wrong it is <see cref="BackstabReach"/>.
    ///
    /// THE SIDE ROLL IS BLIND, matching RTK exactly: <c>local rand = math.random(0, 1)</c> once per swing,
    /// then <c>if (#mobLeft &gt; 0 and player.flank and rand == 0)</c> / <c>rand == 1</c> for the right. The
    /// side is chosen BEFORE looking at what is there, so a flank swing with a mob on only one side has a
    /// 50% chance of hitting thin air. (We briefly picked at random among OCCUPIED sides, which always
    /// connects and is strictly more generous — reverted 2026-08-04 to match RTK pending live testing.)
    /// The Rogue-Tutor post — *"Flank only hits a 1 target to the left or right per swing, not both.
    /// (Random.)"* — is consistent with either, so this is RTK's word, not the archive's.
    ///
    /// Empty tiles are dropped, so the caller never rolls against nothing; an entirely empty neighbourhood
    /// still yields the faced tile, leaving an ordinary swing at thin air exactly as it was.</summary>
    private const double BackstabReach = 0.5;   // nexusatlas 2003; the Warrior Tutor post later says 0.95
    private const double FlankReach    = 0.5;   // nexusatlas 2003 AND the Warrior Tutor post agree

    private List<((int x, int y) tile, double reach)> SwingTargets()
    {
        var list = new List<((int x, int y) tile, double reach)>(3);

        AddReachTargets(list, _facing, 1.0);                                   // the faced tile, always
        if (FourWayStance)
        {
            // Baekho's Cunning 4+: every adjacent tile at once, no roll. This is the ONLY thing in the game
            // that grants it -- see FourWayStance for the two archive sources -- and it supersedes the two
            // lesser stances rather than stacking with them (they reach the same tiles, more meanly).
            AddReachTargets(list, Opposite(_facing), BackstabReach);
            AddReachTargets(list, LeftOf(_facing),   FlankReach);
            AddReachTargets(list, RightOf(_facing),  FlankReach);
        }
        else
        {
            if (BackstabStance) AddReachTargets(list, Opposite(_facing), BackstabReach);
            if (FlankStance)                                                   // ONE side, blind roll (RTK's `rand`)
                AddReachTargets(list, Random.Shared.Next(2) == 0 ? LeftOf(_facing) : RightOf(_facing), FlankReach);
        }

        if (list.Count == 0) list.Add((FrontTile(), 1.0));   // nothing reachable — swing at the faced tile, as before
        return list;
    }

    // First living mob standing on (x,y), or null.
    private Mob? MobAt(int x, int y) =>
        _mobs.FirstOrDefault(m => m.Alive && m.X == x && m.Y == y);

    private void SendAction(uint id, byte type, ushort time, byte param)
    {
        var d = new List<byte>();
        d.AddRange(Be32(id));
        d.Add(type);
        d.AddRange(Be(time));
        d.Add(param);
        SendMap(0x1A, _gameInc++, d.ToArray(), $"action(0x1A) type={type} time={time}");
    }

}
