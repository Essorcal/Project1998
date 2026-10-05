using System.Reflection;
using System.Text;
using Server;
using Shared;

namespace Tests.Support;

/// <summary>
/// The pieces the spell-dispatch facts share: the 0x0F cast frame a client sends, the mini-text lines a cast
/// answered with, and the two numbers a cast leaves behind that <see cref="Session"/> keeps private (the
/// enchant multiplier and a spell's aether).
///
/// <para>Reflection into Session's non-public state is the pattern <c>EnchantSwapOrderTests</c> and
/// <c>FuryRelogDrainTests</c> already use; both reads run under the session's state monitor, the lock the cast
/// itself wrote them under.</para>
/// </summary>
internal static class SpellCastSupport
{
    private static readonly FieldInfo EnchantField =
        typeof(Session).GetField("_enchantAmount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo AetherField =
        typeof(Session).GetField("_aether", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>The 0x0F cast frame for 0-based book <paramref name="slot"/> (RTK clif_parsemagic,
    /// <c>Session.HandleCast</c>): the slot byte (slot + 1), then, for a "Which target? &gt;" spell, the target's
    /// entity id as a u32 big-endian. With no target the client sends the slot alone.</summary>
    public static byte[] CastFrame(int slot, uint? targetId = null)
    {
        var body = new List<byte> { (byte)(slot + 1) };
        if (targetId is uint id)
            body.AddRange(new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id });
        return SessionFixture.Frame(ClientOp.Cast, body.ToArray());
    }

    /// <summary>Every 0x0A mini-text line the recorder was sent. The body is
    /// <c>type(u8) | len(u16 BE) | ascii[len]</c> (<c>Session.SendMiniText</c>).</summary>
    public static List<string> MiniTexts(RecordingOutbound outbound) =>
        outbound.BodiesOf(ServerOp.MiniText)
                .Where(b => b.Length >= 3)
                .Select(b => Encoding.ASCII.GetString(b, 3, Math.Min((b[1] << 8) | b[2], b.Length - 3)))
                .ToList();

    /// <summary>The armed enchant multiplier (1 = none), which <c>Session.PlayerSwingDamage</c> applies to the
    /// raw weapon term.</summary>
    public static double Enchant(Session session)
    {
        double value = 0;
        session.WithState(() => value = (double)EnchantField.GetValue(session)!);
        return value;
    }

    /// <summary>Milliseconds left on the aether (RTK's per-spell cooldown) armed under
    /// <paramref name="key"/>, or 0 when none is running. <c>OnCooldown</c>, the gate a recast meets, reads the
    /// same dictionary.</summary>
    public static long AetherLeft(Session session, string key)
    {
        long left = 0;
        session.WithState(() =>
        {
            var aether = (Dictionary<string, long>)AetherField.GetValue(session)!;
            if (aether.TryGetValue(key, out long until)) left = Math.Max(0, until - Environment.TickCount64);
        });
        return left;
    }
}
