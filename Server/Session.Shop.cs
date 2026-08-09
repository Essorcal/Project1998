using System.Text;
using Shared;

namespace Server;

/// <summary>
/// The native merchant windows — `0x2f`, the icon grid the real game uses for buying and selling, replacing
/// the text menus the shop ability used to drive. `0x2f` is a sub-kind dispatcher (client `0x452c50` switches
/// `body[0]` over 0..10 and hands each sub-handler `&amp;body[1]`); this file implements the two that matter:
/// <list type="bullet">
///   <item>sub-kind 4 = the buy grid — icon, price, name and a blurb per row (RTK <c>clif_buydialog</c>)</item>
///   <item>sub-kind 5 = the sell grid — just a list of YOUR bag slots, since the client already has the
///         names and icons for those (RTK <c>clif_selldialog</c>)</item>
/// </list>
/// Both answer on inbound <b>`0x39`</b> (RTK <c>case 0x39</c> -> <c>clif_handle_menuinput</c>), which dispatches
/// on a tag byte the server chose and the client echoed back.
/// </summary>
public sealed partial class Session
{
    // The tag written at body[1] and echoed back at reply body[0]. RTK's "//For parsing purposes" byte: it is
    // how the server tells which of its own windows is answering, since every 0x2f sub-kind replies on 0x39.
    private const byte ShopTagBuy = 2;
    private const byte ShopTagSell = 4;

    private const byte ShopTagAmount = 3;

    /// <summary>What a merchant window answered. <see cref="Name"/> is empty when the player closed the
    /// window without picking (RTK treats an empty name as "cancelled" the same way). <see cref="Slot"/> is
    /// the raw wire byte — for the sell grid that is the ONE-based wire slot, see <see cref="SendSellGrid"/>.
    /// <see cref="Second"/> is the amount window's second string.</summary>
    private readonly record struct ShopReply(byte Tag, byte Slot, string Name, string Second = "");

    private TaskCompletionSource<ShopReply>? _shopReply;

    private Task<ShopReply> AwaitShopReply()
    {
        var tcs = new TaskCompletionSource<ShopReply>();
        _shopReply = tcs;    // a fresh window orphans any pending one, exactly like _dlgReply
        return tcs.Task;
    }

    // ---- wire ------------------------------------------------------------------------------------

    /// <summary>The prefix every `0x2f` sub-kind shares. body[6..] is the SAME head layout the `0x30` dialogs
    /// use — head kind, a discriminated look descriptor, then a 4-byte trailing descriptor the client parses
    /// past — so <see cref="WriteHead"/> is reused rather than duplicated.</summary>
    private void WriteGridPrefix(List<byte> d, byte subKind, byte tag, Mob npc, string prompt)
    {
        d.Add(subKind);                 // [0] which window
        d.Add(tag);                     // [1] echoed back at reply body[0]
        d.AddRange(Be32(npc.Id));       // [2..5] npc entity id
        WriteHead(d, DialogPortrait.Npc(npc));   // [6] head kind, [7..] descriptor, then 4 skipped bytes
        var p = Encoding.ASCII.GetBytes(prompt);
        d.AddRange(Be((ushort)p.Length));
        d.AddRange(p);
    }

    /// <summary>Buy grid (sub-kind 4). Each row is <c>icon(u16BE) price(u32BE) nameLen(u8) name[] textLen(u8)
    /// text[]</c>.</summary>
    /// <remarks>
    /// Two places RTK is a bad guide here, both because it is a 7.x server:
    /// <para>• It writes a colour byte after the icon. The 4.95 client reads the price <c>u32BE</c> at
    /// descriptor+2 — there is no colour byte in a row. Colour is folded into the frame by
    /// <see cref="IconOf"/>/<c>IconWire</c> instead, the same as the bag.</para>
    /// <para>• Its "normal item" branch sends the raw <c>ItmIcon</c> and only its custom-icon branch adds
    /// 49152. For 4.95 the +49152 form (== <c>IconWire</c>) is correct for EVERY row; a raw frame draws
    /// nothing at all.</para>
    /// The reply identifies the row by NAME, not index, so two rows with the same name are indistinguishable —
    /// the lookup takes the first match.
    /// </remarks>
    private void SendBuyGrid(Mob npc, string prompt, IReadOnlyList<GridRow> rows)
    {
        var d = new List<byte>();
        WriteGridPrefix(d, 4, ShopTagBuy, npc, prompt);
        // An unknown u16BE the client feeds straight into the grid widget ctor (0x454fa0). RTK fills it with
        // strlen(dialog) UNSWAPPED, which cannot be meaningful, so 0 is as good a value as any until it's swept.
        d.AddRange(Be(0));
        d.AddRange(Be((ushort)rows.Count));
        foreach (var r in rows)
        {
            d.AddRange(Be(r.Icon));
            d.AddRange(Be32((uint)Math.Max(0, r.Number)));
            WriteAscii8(d, r.Name);
            WriteAscii8(d, r.Blurb);
        }
        SendMap(0x2F, _gameInc++, d.ToArray(), $"buy-grid(0x2f) npc={npc.Id} x{rows.Count}");
    }

    /// <summary>One buy-grid row. <see cref="Number"/> is the big figure beside the icon — a price in a shop,
    /// but the bank puts the STORED COUNT there, which is what RTK does too (its Lua hands `bankCountTable`
    /// to the argument `clif_buydialog` calls `price[]`). <see cref="Name"/> is what comes back in the reply,
    /// so it must be unique across the grid.</summary>
    private readonly record struct GridRow(ushort Icon, int Number, string Name, string Blurb);

    /// <summary>A row for an ordinary shop item: real icon, price, catalogue name and buy blurb.</summary>
    private GridRow ShopRow(ItemDef def, int price) =>
        new(IconWire(IconOf(def)), price, def.Name, BuyBlurb(def));

    /// <summary>Open the amount window and return the number typed, or null if it was cancelled. Deliberately
    /// NOT clamped to <paramref name="max"/> — the caller decides whether too-large is a clamp or a refusal,
    /// and the bank refuses.</summary>
    private async Task<int?> AskAmount(Mob npc, string prompt, string itemName, int max)
    {
        SendAmountPrompt(npc, prompt, itemName, max);
        var r = await AwaitShopReply();
        // Either of the amount window's two strings may hold the typed number; take whichever parses.
        if (!int.TryParse(r.Second, out int n) && !int.TryParse(r.Name, out n)) return null;
        return n;
    }

    /// <summary>Sell grid (sub-kind 5). No names, icons or prices on the wire — the client already has them
    /// for your own bag, so this is a <c>u8</c> count then the wire slots to offer. Note the count is a
    /// <c>u8</c> here where the buy grid's is a <c>u16BE</c>.</summary>
    /// <remarks>
    /// <b>These are the SAME one-based wire slots <c>0x0F</c> uses — pass <see cref="WireSlot"/>, never the
    /// raw zero-based <see cref="InvItem.Slot"/>.</b> The row loop (`0x455541`) does `edx = 41 * byte` then
    /// reads `[invBase + edx*4 + 0x13e]`, which is byte-for-byte the address the `0x0F` store computes
    /// (`0x48f070`), and the `0x0F` handler range-checks that slot as `1..0x34`. So the two are one scale.
    /// Sending anything lower draws each row further down the bag, and the loop's `test cl,cl; je`
    /// empty-entry check renders the overshoot as a BLANK row rather than skipping it — which reads as a
    /// list that's shifted, not one that's misindexed. RTK's `item[i] + 1` agrees. The reply echoes the byte
    /// back unchanged, so compare it against <see cref="WireSlot"/> rather than adjusting it.
    /// </remarks>
    private void SendSellGrid(Mob npc, string prompt, IReadOnlyList<byte> slots)
    {
        var d = new List<byte>();
        WriteGridPrefix(d, 5, ShopTagSell, npc, prompt);
        d.AddRange(Be(0));
        d.Add((byte)slots.Count);
        d.AddRange(slots);
        SendMap(0x2F, _gameInc++, d.ToArray(), $"sell-grid(0x2f) npc={npc.Id} x{slots.Count}");
    }

    /// <summary>Quantity prompt (sub-kind 3) — the native "how many?" box, used when selling a stack. After
    /// the shared prefix it is the dialog text, then the item name as <c>u8 len + text</c>, then a
    /// <c>u16BE</c> the client stores at <c>+0x286</c> (parse `0x4543af`) and RTK hardcodes to 76. Read as the
    /// maximum enterable amount, so we send the real stack size rather than a constant that would cap a large
    /// stack at 76.</summary>
    private void SendAmountPrompt(Mob npc, string prompt, string itemName, int max)
    {
        var d = new List<byte>();
        WriteGridPrefix(d, 3, ShopTagAmount, npc, prompt);
        WriteAscii8(d, itemName);
        d.AddRange(Be((ushort)Math.Clamp(max, 1, 9999)));
        SendMap(0x2F, _gameInc++, d.ToArray(), $"amount(0x2f) npc={npc.Id} '{itemName}' max={max}");
    }

    private static void WriteAscii8(List<byte> d, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        if (b.Length > 255) b = b[..255];
        d.Add((byte)b.Length);
        d.AddRange(b);
    }

    /// <summary>The per-row blurb under the item name. Mirrors RTK's fallback chain: the item's own
    /// <c>ItmBuyText</c> when it has one, otherwise the path/level line the shop shows for gear.</summary>
    private static string BuyBlurb(ItemDef def)
    {
        if (!string.IsNullOrWhiteSpace(def.BuyText)) return def.BuyText;
        string path = Content.PathName(def.PathId);
        return string.IsNullOrEmpty(path) ? "" : $"{path} level {def.Level}";
    }

    // ---- reply -----------------------------------------------------------------------------------

    /// <summary>Inbound `0x39` — every `0x2f` window answers here, tagged by the byte the server put at
    /// body[1]. Buy returns the item NAME (RTK <c>clif_parsebuy</c> memcpy's it out); sell returns a bag slot
    /// (<c>clif_parsesell</c>). Both payloads start at body[7].</summary>
    /// <remarks>
    /// The body[7] offset comes from RTK's reads rather than from a capture, so the length byte is
    /// sanity-checked and, if it doesn't fit, the parser scans for the position where a length byte would be
    /// self-consistent. That converts a wrong-by-a-byte guess from "silently buys nothing" into a log line
    /// naming the real offset. The raw body is logged either way.
    /// </remarks>
    private void HandleShopReply(byte[] dec)
    {
        byte tag = dec.Length > 0 ? dec[0] : (byte)0;
        byte slot = dec.Length > 7 ? dec[7] : (byte)0;
        string name = "", second = "";

        if (tag == ShopTagBuy || tag == ShopTagAmount)
        {
            int at = 7;
            if (at >= dec.Length || at + 1 + dec[at] > dec.Length)
            {
                // Find the first offset whose length byte doesn't overrun the packet AND lands on printable
                // text, so a wrong-by-a-byte guess names the real offset instead of silently reading junk.
                int found = -1;
                for (int p = 1; p < dec.Length; p++)
                    if (dec[p] > 0 && p + 1 + dec[p] <= dec.Length
                        && dec.Skip(p + 1).Take(dec[p]).All(b => b >= 0x20 && b < 0x7F)) { found = p; break; }
                if (found >= 0) Log.Info($"   !! 0x39 string length is at body[{found}], not body[7] — fix HandleShopReply");
                at = found;
            }
            if (at >= 0)
            {
                name = Encoding.ASCII.GetString(dec, at + 1, dec[at]).Trim();
                // The amount window answers with TWO strings (RTK clif_parseinput). Which one carries the
                // typed number isn't settled, so both are captured and the caller takes whichever parses.
                int at2 = at + 1 + dec[at];
                if (at2 < dec.Length && at2 + 1 + dec[at2] <= dec.Length)
                    second = Encoding.ASCII.GetString(dec, at2 + 1, dec[at2]).Trim();
            }
        }

        Log.Info($"   -> SHOP REPLY (0x39) tag={tag} slot={slot}" +
                 (name.Length > 0 ? $" name='{name}'" : "") +
                 (second.Length > 0 ? $" second='{second}'" : "") +
                 $"  {dec.Length}B: {Convert.ToHexString(dec).ToLowerInvariant()}");

        var tcs = _shopReply;
        _shopReply = null;
        tcs?.TrySetResult(new ShopReply(tag, slot, name, second));
    }
}
