using System.Reflection;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// Which branch of <c>Session.ApplyCast</c> every spell takes, pinned against a table recorded on
/// upstream/master 9c00b98: <c>Tests/Fixtures/spell-dispatch-routes.txt</c>.
///
/// <para>Written for the dispatch fall-through slice. Its Spirit Blade fix adds a branch inside the no-row
/// fallback, and its Inferno fix sends the 5-way family through the archetypes' post-cast tail instead of
/// returning early. Both must leave every other spell on the branch it took before. The fixture is that
/// "before", recorded by this fact on 9c00b98; the one line the slice changed in it is spirit_blade's, from
/// <c>generic</c> to <c>stance_enchant</c>. Any other difference is a spell that changed branch, which a player
/// would see, so it needs a decision, not a fixture edit.</para>
///
/// <para>How a branch is observed. The fact loads a probe copy of <c>spell_verbs.lua</c>: the real file with
/// every verb wrapped. For a caster whose registry carries <c>dispatch_route_probe</c> the wrapper names its verb
/// (and says "row" when a SpellParams row bound it), then declines, so nothing is spent or armed and every spell
/// starts from the same state; for any other caster it calls the real verb. <c>ApplyCast</c> then runs once per
/// spell for one probe caster with no target and no typed answer, and the route is every mini-text line that
/// cast sent: the wrapper's line, or the engine's own answer where no verb ran ("Become what?" for a morph cast
/// with nothing typed).</para>
///
/// <para>Process-wide state: the verb script is one host for the whole process. The fact holds
/// <see cref="TestProcessState.Gate"/> from loading the probe until the real file is back, and every content load
/// takes that gate, so no reload can swap the probe out mid-run or in afterwards. The wrapper calls through for
/// every other caster, so a cast made anywhere else meanwhile behaves exactly as with the real file.</para>
///
/// <para>To re-record the table, set <c>P1998_SPELL_ROUTES_OUT</c> to a file path and run this fact; it writes
/// what it observed there before it compares.</para>
/// </summary>
[Collection("world")]
public sealed class SpellDispatchRouteTests
{
    private const ushort Map = 62030;
    private const string ProbeSlot = "dispatch_route_probe";

    private static readonly MethodInfo ApplyCastMi =
        typeof(Session).GetMethod("ApplyCast", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Appended to the real verb file: wraps every verb it defined. <c>row</c> is the SpellParams row
    /// for a row-bound call and an empty table for a call from a C# dispatch site.</summary>
    private const string ProbeWrapper = """


        -- Route probe (Tests/SpellDispatchRouteTests.cs): name the verb for the probe caster, call through for anyone else.
        do
          local real = {}
          for name, fn in pairs(verbs) do
            if type(fn) == "function" then real[name] = fn end
          end
          for name, fn in pairs(real) do
            verbs[name] = function(ctx, row)
              if ctx:reg("dispatch_route_probe") ~= 1 then return fn(ctx, row) end
              if next(row) ~= nil then ctx:say("verb=" .. name .. " row") else ctx:say("verb=" .. name) end
              return false
            end
          end
        end
        """;

    private readonly SessionFixture _fx;

    public SpellDispatchRouteTests(SessionFixture fx) => _fx = fx;

    private static string FixturePath => Path.Combine(RepoPaths.Root(), "Tests", "Fixtures", "spell-dispatch-routes.txt");

    /// <summary>Every spell takes the branch it took on 9c00b98, except Spirit Blade, which now reaches the
    /// enchant stance. Red on 9c00b98 on exactly that one line.</summary>
    [Fact]
    public void EverySpellTakesTheBranchItTookBeforeExceptSpiritBlade()
    {
        var observed = Record();

        string? dump = Environment.GetEnvironmentVariable("P1998_SPELL_ROUTES_OUT");
        if (!string.IsNullOrEmpty(dump)) File.WriteAllLines(dump, Render(observed));

        var expected = Parse(File.ReadAllLines(FixturePath));
        Assert.True(expected.Count > 800, $"the fixture holds only {expected.Count} spells");

        var moved = new List<string>();
        foreach (var key in expected.Keys.Union(observed.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            string want = expected.GetValueOrDefault(key, "(not in the fixture)");
            string got = observed.GetValueOrDefault(key, "(not loaded)");
            if (want != got) moved.Add($"{key.Replace('\t', ' ')}: was '{want}', now '{got}'");
        }
        Assert.Empty(moved);
    }

    /// <summary>One probe cast per spell; the key is <c>identifier TAB id</c>, since two identifiers
    /// (apply_stealth, mass_resurrect) name two rows each.</summary>
    private SortedDictionary<string, string> Record()
    {
        var routes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string real = RepoPaths.GameData("spell_verbs.lua");
        string probe = Path.Combine(TestProcessState.StateDirectory, $"spell_verbs.route-probe.{Guid.NewGuid():N}.lua");
        File.WriteAllText(probe, File.ReadAllText(real) + ProbeWrapper);

        lock (TestProcessState.Gate)
        {
            var (session, outbound, _) = _fx.PlayerWith("RouteProbe", c =>
            {
                c.Level = 99;
                c.MaxHp = 1_000; c.Hp = 1_000;
                c.MaxMp = 100_000; c.Mp = 100_000;
                QuestState.Over(c, QuestState.Registry).Set(ProbeSlot, 1);
            }, Map, x: 5, y: 5);
            try
            {
                Assert.True(SpellScript.Load(probe), "the probe copy of spell_verbs.lua did not load");
                foreach (var sp in Content.Spells)
                {
                    outbound.Clear();
                    string route;
                    try
                    {
                        session.WithState(() => ApplyCastMi.Invoke(session, new object?[] { sp, null, null }));
                        var lines = SpellCastSupport.MiniTexts(outbound);
                        route = lines.Count == 0 ? "(silent)" : string.Join(" | ", lines);
                    }
                    catch (TargetInvocationException e)
                    {
                        route = $"throws {e.InnerException?.GetType().Name}";
                    }
                    routes[$"{sp.Key}\t{sp.Id}"] = route;
                }
            }
            finally
            {
                // The real verbs go back before the gate is released; a failed reload falls back to a full
                // content load, which reads the same file.
                if (!SpellScript.Load(real)) TestProcessState.LoadContent();
                _fx.World.LeaveMap(session, Map);
                try { File.Delete(probe); } catch { /* best-effort: the state directory goes at process exit */ }
            }
        }
        return routes;
    }

    private static IEnumerable<string> Render(SortedDictionary<string, string> routes)
    {
        yield return "# The branch Session.ApplyCast takes for every spell, as the route probe in";
        yield return "# Tests/SpellDispatchRouteTests.cs observes it: identifier, Spells.csv id, then the mini-text the";
        yield return "# probe cast sent (\"verb=<name>\", \"row\" when a SpellParams row bound it, else the engine's own answer).";
        yield return "# Recorded on upstream/master 9c00b98; this slice changed spirit_blade from verb=generic to";
        yield return "# verb=stance_enchant. Re-record with P1998_SPELL_ROUTES_OUT (see the fact's doc).";
        foreach (var (key, route) in routes) yield return $"{key}\t{route}";
    }

    private static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t', 3);
            Assert.True(parts.Length == 3, $"malformed fixture line: {line}");
            table.Add($"{parts[0]}\t{parts[1]}", parts[2]);
        }
        return table;
    }
}
