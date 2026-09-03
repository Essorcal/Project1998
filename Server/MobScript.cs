using System.Diagnostics;
using MoonSharp.Interpreter;
using Shared;

namespace Server;

/// <summary>
/// Per-creature AI hooks in Lua (<c>game-data/mob_ai.lua</c>) — the escape hatch for the handful of RTK mobs
/// whose behaviour is genuinely bespoke rather than data.
///
/// <para><b>Event hooks only, and that is deliberate.</b> RTK gives a mob six hooks; four of them
/// (<c>on_attacked</c>, <c>on_healed</c>, <c>after_death</c>, <c>on_spawn</c>) fire on events, which happen
/// rarely and can safely take a lock — and of those four this host implements three, because
/// <c>on_healed</c> is NOT wired here: see the note above the hook constants for which heal would have had
/// to fire it. The other two (<c>move</c>, <c>attack</c>) fire for every mob on every heartbeat — and a
/// survey of all 265 RTK mob tables found that what they actually contain is idle chatter,
/// a chance to cast, and paralysis immunity, every one of which is now DATA
/// (MobChatter.csv / MobSpells.csv / MobBosses.csv). So the per-tick path stays pure C# and this host never
/// runs inside <c>World._lock</c>, which is what makes it safe: a Lua hook that re-entered the world under
/// the lock would deadlock it.</para>
///
/// <para>Hooks are opt-in per mob AND per hook, resolved into a set at load time, so a creature with no
/// script never touches MoonSharp. A missing or broken file disables the whole path and logs — every mob
/// keeps its C# behaviour, exactly as if the file had never existed.</para>
/// </summary>
public static class MobScript
{
    private static Script? _script;
    private static Table? _mobs;
    // mobKey|hook pairs that actually exist, so the hot path is a hash lookup and never a Lua call.
    private static HashSet<string> _defined = new(StringComparer.OrdinalIgnoreCase);

    // THREE of RTK's four event hooks, not four. `on_healed` was resolved at load and advertised in
    // mob_ai.lua but nothing ever queued it (World.QueueHook is called with OnSpawn, OnAttacked and
    // AfterDeath and nothing else), so a script defining it loaded and then silently never fired — the worst
    // shape a hook can have, because it looks supported. Removed rather than wired up, and #103 records why:
    // wiring it means choosing WHICH heal fires it, and this tree has six distinct ones — World.HealMob from
    // a player's cast, World.HealMobFromScript from a Lua heal reaching back in (#100), the boss last-stand
    // save-heal in TryDamage, the two boss self-heals in the tick, and SuteAi's wounded self-heal. (A seventh
    // write of a mob's HP to full, the harvest node's lapse reset in TryClaimHarvestNode, is a claim being
    // settled rather than anything a healer did.) Nothing in the RTK material this repo has says which of
    // those RTK's on_healed corresponds to, and picking one would be inventing a game fact (AGENTS.md rule 2)
    // in a PR whose whole point is that behaviour does not change. It comes back the day a source says what
    // it fires on, and that is a two-line change plus a QueueHook call.
    public const string OnAttacked = "on_attacked";
    public const string AfterDeath = "after_death";
    public const string OnSpawn    = "on_spawn";

    /// <summary>(Re)load the AI script atomically — same contract as the other hosts: the new script only
    /// replaces the running one once it has compiled and produced a global <c>mobs</c> table.</summary>
    public static bool Load(string? path)
    {
        using (Session.EnterScriptGate())
        {
            if (path is null || !File.Exists(path))
            {
                Log.Info($"!! mob_ai.lua: no file at '{path ?? "(null)"}' — {(_mobs is null ? "Lua mob hooks disabled" : "keeping the previously-loaded hooks")}");
                return _mobs is not null;
            }
            try
            {
                var s = new Script(CoreModules.Preset_HardSandbox);
                s.DoString(File.ReadAllText(path), null, "mob_ai");
                var m = s.Globals.Get("mobs");
                if (m.Type != DataType.Table)
                {
                    Log.Info("!! mob_ai.lua missing global `mobs` table — reload REJECTED, keeping the previous hooks");
                    return _mobs is not null;
                }

                var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in m.Table.Pairs)
                {
                    if (pair.Value.Type != DataType.Table) continue;
                    string key = pair.Key.CastToString() ?? "";
                    foreach (var hook in new[] { OnAttacked, AfterDeath, OnSpawn })
                        if (pair.Value.Table.Get(hook).Type == DataType.Function) defined.Add($"{key}|{hook}");
                }

                _script = s; _mobs = m.Table; _defined = defined;
                UserData.RegisterType<MobContext>();
                Log.Info($"   mob_ai.lua: {defined.Count} hooks across {m.Table.Pairs.Count()} creatures");
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"mob_ai.lua load failed: {LuaVerbHost.Describe(e)} — reload REJECTED, keeping the previous hooks", e);
                return _mobs is not null;
            }
        }
    }

    /// <summary>Does this creature define this hook? The tick calls this before building a context, so the
    /// common answer (no) costs one hash lookup.</summary>
    public static bool Has(string mobKey, string hook) =>
        _defined.Count > 0 && mobKey.Length > 0 && _defined.Contains($"{mobKey}|{hook}");

    /// <summary>Run one hook. MUST be called from outside <c>World._lock</c>. A Lua error is logged and
    /// swallowed — one broken hook can't take down a tick.</summary>
    public static void Fire(string mobKey, string hook, MobContext ctx)
    {
        // The #90 rule's second half, asserted here rather than in Session.EnterScriptGate: the gate is
        // static and has no World to ask, and this is the entry that can actually be reached from inside the
        // lock — World.Tick queues these hooks precisely so it can drain them after releasing it. A hook is
        // free to call INTO the world (vanish, say, heal all do); a thread already holding the world lock
        // entering Lua is the direction that deadlocks.
        Debug.Assert(!ctx.World.HoldsWorldLock,
            "lock order violated: World._lock is held while firing a Lua mob hook (#90). Queue the hook and " +
            "run it after the lock is released, the way World.Tick does.");
        if (!Has(mobKey, hook)) return;
        try
        {
            using (Session.EnterScriptGate())
            {
                var table = _mobs?.Get(mobKey);
                if (table is null || table.Type != DataType.Table) return;
                var fn = table.Table.Get(hook);
                if (fn.Type != DataType.Function) return;
                _script!.Call(fn, UserData.Create(ctx));
            }
        }
        catch (Exception e) { Log.Error($"mob_ai.lua {mobKey}.{hook} raised: {LuaVerbHost.Describe(e)}", e); }
    }
}

/// <summary>
/// What a Lua mob hook can see and do. Deliberately small: a creature's hook exists to bend one rule, not to
/// re-implement the AI, and everything the surveyed scripts actually need is here (speak, heal, vanish, read
/// your own state, and touch the player who just hit you). Names are lower-case to read from Lua.
/// </summary>
[MoonSharpUserData]
public sealed class MobContext
{
    private readonly World _world;
    private readonly Mob _mob;
    private readonly ushort _map;       // a Mob doesn't carry its own map; the caller knows it
    private readonly Session? _actor;   // whoever attacked/healed, when the hook has one

    internal MobContext(World world, ushort map, Mob mob, Session? actor)
    { _world = world; _map = map; _mob = mob; _actor = actor; }

    /// <summary>The world this hook is running against — for <see cref="MobScript.Fire"/>'s lock-order
    /// assert, which needs a World and is the only reason this is exposed. Not visible to Lua: the
    /// MoonSharp binding only surfaces the lower-case members below.</summary>
    internal World World => _world;

    public string key => _mob.Key;
    public string name => _mob.Name;
    public double hp => _mob.Hp;
    public double maxHp => _mob.MaxHp;
    public double x => _mob.X;
    public double y => _mob.Y;
    public bool alive => _mob.Alive;

    /// <summary>Is a status of this family on the creature (RTK <c>mob:checkIfCast(curses)</c>)? Goes through
    /// the world for the same reason <see cref="heal"/> does, and it is the read that made "the rest of these
    /// are simple field reads" wrong: <c>Mob.HasStatus</c> walks a <c>Dictionary</c> the world writes under
    /// <c>_lock</c>, so reading it from a lock-free hook can fault inside the lookup rather than merely
    /// return a stale answer.</summary>
    public bool hasStatus(string category) => _world.MobHasStatusFromScript(_mob, category);

    /// <summary>Speak over the creature's head. Channel 0 attributes the line to it, 2 does not — RTK's own
    /// <c>mob:talk(0|2, …)</c> split. Heard only by players near the creature: RTK's bll_talk broadcasts via
    /// map_foreachinarea(..., AREA, ...) around the speaker, so it's proximity-gated like player say.</summary>
    public void say(string line, double channel = 0)
    {
        var text = channel == 0 ? $"{_mob.Name}: {line}" : line;
        var bytes = System.Text.Encoding.ASCII.GetBytes(text.Length > 250 ? text[..250] : text);
        _world.BroadcastArea(_map, _mob.X, _mob.Y, Session.SayHalfW, Session.SayHalfH,
            p => p.SpeakEntity((byte)channel, _mob.Id, bytes));
    }

    /// <summary>Heal the creature, capped at its maximum. Goes through the world rather than writing
    /// <c>_mob.Hp</c> here: mob HP is state <c>World._lock</c> owns, and a hook runs OUTSIDE that lock (see
    /// the class doc above), so the write used to land with no lock held while the tick could be reading the
    /// same field. Same direction <see cref="vanish"/> and <see cref="say"/> already take — the gate calling
    /// into the world is legal, it is holding the world lock on the way INTO the gate that is not (#90).
    /// <see cref="World.HealMobFromScript"/> keeps this method's exact arithmetic, ungated as it has always
    /// been; what changed is only when the write happens.</summary>
    public void heal(double amount) => _world.HealMobFromScript(_mob, (int)amount);

    /// <summary>Remove the creature with no kill credit, loot or exp (RTK <c>mob:vanish()</c>). Its spawn
    /// point refills normally.</summary>
    public void vanish() => _world.DespawnMob(_map, _mob);

    /// <summary>The player this hook is about (the attacker/healer), or "" when there isn't one.</summary>
    public string actorName => _actor?.Snapshot().Name ?? "";

    /// <summary>Set a quest-registry flag on that player — how RTK's yin/yang mice record that you zapped
    /// them while they were cursed.</summary>
    public void actorQuest(string questKey, double value)
    {
        if (_actor is null || questKey.Length == 0) return;
        _actor.SetQuestStage(questKey, (int)value);
    }

    /// <summary>Their current stage for a quest key (0 if unset).</summary>
    public double actorQuestStage(string questKey) => _actor?.QuestStage(questKey) ?? 0;

    /// <summary>Does that player already carry this legend mark?</summary>
    public bool actorHasLegend(string mark) => _actor?.HasLegend(mark) ?? false;

    /// <summary>Brand them — RTK's <c>attacker:addLegend(text, key, icon, colour)</c>. How the leviathans
    /// record that you broke your word and attacked one.</summary>
    public void actorAddLegend(string text, string mark, double icon, double colour)
    {
        if (_actor is null || mark.Length == 0 || _actor.HasLegend(mark)) return;
        _actor.AddLegend(text, mark, (byte)icon, (byte)colour);
    }

    /// <summary>Say a line to that player alone (RTK follows its brand with a <c>dialogSeq</c>; a line over
    /// their own head is the same beat without stealing their screen mid-fight).</summary>
    public void actorTell(string line) => _actor?.LuaMessage(line);

    /// <summary>The in-world date, for stamping a legend the way every other legend on this server is
    /// stamped (RTK's <c>curT()</c>).</summary>
    public string gameDate => Character.GameDate;
}
