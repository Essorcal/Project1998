namespace Server;

/// <summary>
/// The single place that knows how one terrain cell goes on the wire for opcode <c>0x06</c>.
///
/// <para>Both the viewport stream (<c>Session.SendMapRect</c>) and the door cell patch
/// (<c>Session.SendObjRow</c>) send <c>0x06</c>, and each client's handler reads a <b>fixed</b> cell width
/// with no length check and no alternate path:</para>
/// <list type="bullet">
/// <item>4.95 — recv handler <c>0x44fb90</c>: <b>4 bytes/cell</b>, <c>{ ground u16BE, object u16BE }</c>,
/// with passability packed into the ground word's top 2 bits (the same word the <c>.map</c> stores).</item>
/// <item>5.33 — <c>sub_469060</c>: <b>6 bytes/cell</b>, <c>{ tile u16BE, pass u16BE, object u16BE }</c>
/// — three reads storing to <c>[esi]</c>, <c>[esi+2]</c>, <c>[esi+4]</c> on a six-byte stride.</item>
/// </list>
///
/// <para><b>Why this is a class and not two inline loops.</b> It used to be two inline loops, they drifted,
/// and the patch path kept emitting 2-short cells to 5.33 long after the stream path had moved to 3. The
/// client then consumed the next cell's bytes as its own and read past the end of the body, so opening a
/// door repainted the strip with garbage that only corrected itself on the next full refresh. Getting the
/// width wrong does not fail loudly — it desyncs a run and produces plausible-looking wrong terrain.</para>
///
/// <para><b>The middle short is one bit.</b> 5.33 merges it as <c>new = old ^ ((old ^ read) &amp; 1)</c>:
/// it takes only bit 0 and preserves whatever else was in the cell. Our 4.x-derived value is 0 or 3, and
/// 3 is therefore equivalent to 1.</para>
/// </summary>
public static class MapCell
{
    /// <summary>Shorts per cell on the wire for this client — 3 on 5.33, 2 on 4.95.</summary>
    public static int ShortsPerCell(Session.ClientVersion ver) =>
        ver == Session.ClientVersion.V533 ? 3 : 2;

    /// <summary>Bytes per cell on the wire for this client.</summary>
    public static int BytesPerCell(Session.ClientVersion ver) => ShortsPerCell(ver) * 2;

    /// <summary>Append one cell.</summary>
    /// <param name="tile">For 5.33, the CLIENT frame index (already through <see cref="TileTranslation"/>).
    /// For 4.95, the 4.x ground word — <c>(tile &amp; 0x3FFF) | (pass &lt;&lt; 14)</c> reproduces it exactly,
    /// so passing the whole word with its own <paramref name="pass"/> is a round trip.</param>
    /// <param name="pass">Passability. Its own short on 5.33; the ground word's top 2 bits on 4.95.</param>
    /// <param name="obj">Object id (shared id space; never version-shifted).</param>
    public static void Write(List<byte> into, ushort tile, ushort pass, ushort obj, Session.ClientVersion ver)
    {
        if (ver == Session.ClientVersion.V533)
        {
            Be(into, tile);
            Be(into, pass);
            Be(into, obj);
        }
        else
        {
            Be(into, (ushort)((tile & 0x3FFF) | (pass << 14)));
            Be(into, obj);
        }
    }

    private static void Be(List<byte> into, ushort v)
    {
        into.Add((byte)(v >> 8));
        into.Add((byte)(v & 0xFF));
    }
}
