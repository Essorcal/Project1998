using Shared;

namespace Server;

/// <summary>
/// One side's staged offer in a live <see cref="Trade"/> — RTK's per-player <c>sd->exchange</c> struct
/// (<c>rtk/src/map/clif.c</c>: <c>exchange.item[]</c> + <c>exchange.gold</c> + a done/confirm flag).
/// Unlike RTK, items aren't escrowed out of the bag the moment they're offered here (see <see cref="Trade"/>
/// doc) — this just remembers what was PROMISED, and <c>Session.TransferItems</c> re-checks the sender
/// still actually holds it at finalize time.
/// </summary>
public sealed class TradeOffer
{
    public readonly List<InvItem> Items = new();
    public uint Gold;
    public bool Confirmed;
}

/// <summary>
/// A live two-player trade — RTK's "exchange" (<c>clif_handitem</c> / <c>clif_handgold</c> /
/// <c>clif_parse_exchange</c>, <c>rtk/src/map/clif.c</c>). RTK's real exchange is a dedicated binary
/// window (add-item / add-gold / confirm / cancel sub-packets); that window's 4.95 wire format has never
/// been captured live, and guessing a new binary UI packet risks the same class of client crash a
/// wrong-shaped packet has caused before (see the password-length crash writeup in memory/docs). So this
/// reuses the SAME async dialog primitives NPC shops and the bank already drive (<c>DlgMenu</c>/<c>DlgSay</c>/
/// <c>DlgInput</c>, built on the live-confirmed 0x30/0x3a NPC dialog packets) instead of a new opcode —
/// the RULES below are ported straight from RTK; only the presentation differs. See docs §11 (party+trade).
/// </summary>
public sealed class Trade
{
    public readonly Session A;
    public readonly Session B;
    public readonly TradeOffer OfferA = new();
    public readonly TradeOffer OfferB = new();

    // Set once the trade is cancelled or finalized so both sides' menu loops (Session.RunTradeMenuAsync)
    // know to stop, even if that session is mid-await on its own next click.
    public bool Ended;

    public Trade(Session a, Session b) { A = a; B = b; }

    public Session Other(Session s) => ReferenceEquals(s, A) ? B : A;
    public TradeOffer OfferOf(Session s) => ReferenceEquals(s, A) ? OfferA : OfferB;
}
