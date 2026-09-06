namespace Shared;

/// <summary>
/// The <c>0x1A</c> action byte: which animation the client plays over an entity.
///
/// <para>Source: the 4.95 handler <c>0x4503a0</c>, decoded in <c>docs/4.x/Protocol.md</c> §"0x1A — action".
/// The client scales <c>time</c> ×10 and passes <c>param</c> as the third argument to the entity's action
/// vtable method <c>[vtbl+0x78]</c>. Emotes are actions too: the <c>0x1d</c> emote wheel replies with
/// <c>type = index + 11</c> (RTK <c>clif_parseemotion</c>), which is why <see cref="Laughter"/> = 11 is the
/// base of the emote block.</para>
///
/// <para>This enum NAMES the byte; it does not constrain it. The wire field is a full byte and several
/// senders are legitimately dynamic — the emote wheel's <c>index + 11</c>, the per-spell <c>action</c>
/// column in SpellEffects.csv (see <c>Content.CastActionType</c>), and the <c>@mobact</c> calibration
/// probe, which sweeps types the creature vtable may interpret differently from the player's. Casting an
/// unnamed byte to this type is expected and preserves it exactly; nothing here validates against the
/// named set.</para>
///
/// <para>Values 25 and 26 are deliberately absent: nothing sources them. 4.95 has no death frame-set
/// (see the death beat in Protocol.md §7.2), so there is no death action either.</para>
/// </summary>
public enum ActionType : byte
{
    Stand = 0,
    Attack = 1,
    Throw = 2,
    Shot = 3,
    /// <summary>The crouch. RTK's <c>clif_parsegetitem</c> reuses the sit pose for the pick-up bend-down,
    /// so one byte covers both; every server-side use of it is the pick-up.</summary>
    SitOrPickup = 4,
    /// <summary>A DISTINCT pose from <see cref="SitOrPickup"/> — RTK <c>clif_parsedropitem</c>.</summary>
    Drop = 5,
    Magic = 6,
    /// <summary>The sip/puff pose. Not in the Protocol.md table; sourced from RTK script structure, which
    /// is identity rather than balance (AGENTS Rule 2): every drink and smoke script — wine.lua,
    /// herb_pipe.lua, sonhi_pipe.lua — is <c>sendAction(7, 20)</c>, while every food script is
    /// <c>sendAction(8, 25)</c>. See <c>Session.ItemSipAnim</c>, which also records why RTK's accompanying
    /// <c>playSound(22)</c> is deliberately NOT ported.</summary>
    DrinkOrSmoke = 7,
    Eat = 8,

    // ---- the emote block: 0x1d wheel index + 11 -------------------------------------------------------
    Respect = 9,
    Triumph = 10,
    /// <summary>Emote wheel index 0, and therefore the base the <c>0x1d</c> handler adds its index to.</summary>
    Laughter = 11,
    Grief = 12,
    Shame = 13,
    Affection = 14,
    Boredom = 15,
    Sleepiness = 16,
    Surprise = 17,
    /// <summary>The 'h' rage emote — also the documented <c>action</c>-column override the Rogue/Warrior
    /// furies cast with instead of <see cref="Magic"/>.</summary>
    Rage = 18,
    Sarcasm = 19,
    Shrug = 20,
    Annoyed = 21,
    Dance = 22,
    Strange = 23,
    Kiss = 24,
    Charge = 27,
    AttackAfterCharge = 28,
}

/// <summary>
/// The <c>0x10</c> remove-from-bag-slot reason byte: which line the CLIENT prints as it clears the slot.
///
/// <para>The <c>0x10</c> body carries no item name — the client renders its status line from whatever it
/// has already drawn in that slot, keyed purely off this byte. So the reason is the entire wording of the
/// removal, and picking the wrong one narrates a sale as a drop.</para>
///
/// <para><b>Source, and a conflict worth knowing about.</b> Two tables exist in this repo and they
/// disagree from 9 upward. <c>docs/4.x/Protocol.md</c> §11c carries a table inferred on 2026-08-06 from
/// the <c>Inter.dat</c> message lines (9=sold 10=gave 11=broken 12=removed, and "no reason is silent").
/// The names below instead follow the LIVE <c>@delreason</c> sweep of 2026-08-07 recorded on
/// <c>Content.EquipDelReason</c> (9=gave 10=sold 11=removed 12=silent 13=broken), because:
/// it is later and is direct observation rather than a line-index inference — and the doc itself shows why
/// that inference is unsafe, since the handler CLAMPS/defaults rather than indexing raw;
/// Protocol.md's own later prose (the give-to-mob section, RE'd 2026-08-18) uses 9 for "You gave" and
/// calls 12 the silent reason, agreeing with the sweep and not with §11c;
/// and §11c is stale in two further respects — it says no byte is silent (only 15 was probed) and that
/// <c>EquipDelReason</c> defaults to -1, while the shipped default is 12.</para>
///
/// <para>Like <see cref="ActionType"/> this names the byte without constraining it. <c>EquipDelReason</c>
/// is operator-configurable to any byte (or -1 to send no packet at all) and <c>@delreason</c> sweeps
/// 0..255; both cast through this type unchanged.</para>
/// </summary>
public enum DelReason : byte
{
    /// <summary>"&lt;item&gt; removed." — the client's default/clamp line, NOT a silent removal. Anything
    /// from 14 up lands here too, as does the out-of-range 15 that was probed live.</summary>
    Removed = 0,
    /// <summary>"You dropped &lt;item&gt;." Drop-ONLY: it is not a generic removal, and reusing it for
    /// sales or turn-ins announced them as drops (found live 2026-07-26).</summary>
    Dropped = 1,
    Ate = 2,
    /// <summary>"You smoked &lt;item&gt;." Real, and it would fit the herb/sonhi pipes exactly — but the
    /// pipes deliberately send <see cref="Used"/> instead (user's call, 2026-08-07).</summary>
    Smoked = 3,
    Threw = 4,
    Shot = 5,
    Used = 6,
    Posted = 7,
    Decayed = 8,
    /// <summary>"You gave &lt;item&gt;." The hand-over line: trades, gives, and bank deposits.</summary>
    Gave = 9,
    /// <summary>"You sold &lt;item&gt;." The vendor's line.</summary>
    Sold = 10,
    /// <summary>A SECOND code rendering the same "&lt;item&gt; removed." line as <see cref="Removed"/>,
    /// per the live sweep. Nothing sends it; named only so the table has no unexplained gap.</summary>
    RemovedAlternate = 11,
    /// <summary>The one code that prints NOTHING. It is what lets wearing gear clear the bag cell without
    /// narrating — the bag and the equip window are separate client structures and only the <c>0x10</c>
    /// handler clears a bag entry, so the packet cannot simply be omitted.</summary>
    Silent = 12,
    /// <summary>"&lt;item&gt; broken."</summary>
    Broken = 13,
}
