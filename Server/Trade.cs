using Shared;

namespace Server;

/// <summary>
/// One side's staged offer in a live <see cref="Trade"/> — RTK's per-player <c>sd->exchange</c> struct
/// (<c>rtk/src/map/clif.c</c>: <c>exchange.item[]</c> + <c>exchange.gold</c> + a done/confirm flag).
/// Unlike RTK, items aren't escrowed out of the bag the moment they're offered here (see <see cref="Trade"/>
/// doc) — this just remembers what was PROMISED, and <c>Session.TransferItems</c> re-checks the sender
/// still actually holds it at finalize time. Each staged <see cref="InvItem"/> keeps the BAG SLOT it was
/// offered from: that slot is both how finalize finds the stack again and the row key the client's window
/// de-duplicates on, so one slot is always exactly one row.
/// </summary>
public sealed class TradeOffer
{
    public readonly List<InvItem> Items = new();
    public uint Gold;
    public bool Confirmed;
}

/// <summary>
/// A live two-player trade — RTK's "exchange" (<c>clif_handitem</c> / <c>clif_handgold</c> /
/// <c>clif_parse_exchange</c>, <c>rtk/src/map/clif.c</c>). This is the plain state behind the client's own
/// binary exchange WINDOW: opcode <c>0x42</c> out, <c>0x4a</c> in, both reverse-engineered from the 4.95 and
/// 5.33 binaries — see <c>Session.Exchange.cs</c> for the wire format, the sub-type map, and the two places
/// this deliberately diverges from RTK (no escrow; every offer change un-confirms both sides). An earlier
/// implementation drove the negotiation through NPC dialogs instead, because the window's 4.95 layout had not
/// been read yet; nothing of that remains.
/// </summary>
public sealed class Trade
{
    public readonly Session A;
    public readonly Session B;
    public readonly TradeOffer OfferA = new();
    public readonly TradeOffer OfferB = new();

    // Set once the trade is cancelled or finalized, so a late packet from a window that has not been told yet
    // (or is mid-close) can't reopen or re-drive a dead trade. Session.EndTrade is the only writer.
    public bool Ended;

    public Trade(Session a, Session b) { A = a; B = b; }

    public Session Other(Session s) => ReferenceEquals(s, A) ? B : A;
    public TradeOffer OfferOf(Session s) => ReferenceEquals(s, A) ? OfferA : OfferB;
}
