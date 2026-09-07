namespace Shared;

/// <summary>
/// The <c>0x1A</c> action byte: which animation the client plays over an entity.
///
/// <para>Source: the 4.95 handler <c>0x4503a0</c>, decoded in <c>docs/4.x/Protocol.md</c> §"0x1A — action".
/// The client scales <c>time</c> ×10 and passes <c>param</c> as the third argument to the entity's action
/// vtable method <c>[vtbl+0x78]</c>. Emotes are actions too: the <c>0x1d</c> emote wheel replies with
/// <c>type = index + 11</c> in BYTE arithmetic (RTK <c>clif_parseemotion</c>), which is why
/// <see cref="Laughter"/> = 11 is the base of the emote block — and why two of the sixteen keys arrive as a
/// wrapped index, see the note on <see cref="Respect"/>.</para>
///
/// <para>This enum NAMES the byte; it does not constrain it. The wire field is a full byte and several
/// senders are legitimately dynamic — the emote wheel's <c>index + 11</c>, the per-spell <c>action</c>
/// column in spell_effects.csv (see <c>Content.CastActionType</c>), and the <c>@mobact</c> calibration
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
    // The wheel's sixteen keys are NOT a contiguous 11..26 run. The client's key handler (0x491560) maps
    // 'a'..'l' to 11..22, 'm'/'n' to 9/10, and 'o'/'p' to 23/24; its sender (0x491810) then transmits
    // `action - 11` in one unsigned byte. So Respect and Triumph arrive as 0xFE and 0xFF and are recovered
    // only by BYTE WRAPAROUND on the server side (0xFE + 11 = 265 -> 9). See Session.HandleEmotion, which
    // spells that `unchecked` out, and the 0x1d row in docs/4.x/Protocol.md.
    /// <summary>Wheel key 'm'. Reaches the server as index <c>0xFE</c>, not <c>-2</c> — see the note above;
    /// a clamp or range check on the emote index would silently kill this emote and <see cref="Triumph"/>
    /// while leaving the other fourteen working.</summary>
    Respect = 9,
    /// <summary>Wheel key 'n', arriving as index <c>0xFF</c>. See <see cref="Respect"/>.</summary>
    Triumph = 10,
    /// <summary>Emote wheel index 0 (key 'a'), and therefore the base the <c>0x1d</c> handler adds its
    /// index to — including the two indices that wrap.</summary>
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
/// <para><b>Source: the client's own jump table, confirming the live sweep.</b> These names follow the
/// <c>@delreason</c> sweep of 2026-08-07 recorded on <c>Content.EquipDelReason</c> (9=gave 10=sold
/// 11=removed 12=silent 13=broken), which was independently confirmed against the 4.95 binary on
/// 2026-09-06: the reason byte is narrated by handler <c>0x47c800</c> (reached from dispatcher
/// <c>0x47bb90</c>), which does <c>ecx = body[1] - 1; cmp ecx, 0xC; ja default;</c> then
/// <c>jmp [0x47c958 + ecx*4]</c> — a 13-entry SWITCH, not a linear index into the message run. The arms for
/// 9 and 10 push string indices 58 and 57, i.e. reversed against <c>Inter.dat</c> file order; 11 shares the
/// default arm; and 12's arm carries no string index at all, which is what makes it silent.</para>
///
/// <para>That last point is why <c>docs/4.x/Protocol.md</c> §11c disagrees from 9 upward (it has 9=sold
/// 10=gave 11=broken 12=removed and "no reason is silent"): it was inferred on 2026-08-06 by reading the
/// <c>Inter.dat</c> lines in file order, which a switch does not follow. The dump itself was correct; the
/// assumption of linear indexing was not. §11c carries a correction note. Reasons 1-8 were never in
/// dispute.</para>
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
    /// <summary>A SECOND code rendering the same "&lt;item&gt; removed." line as <see cref="Removed"/> —
    /// in the binary its arm IS the default arm (<c>0x47c904</c>), which is why the two are identical rather
    /// than merely observed alike. Nothing sends it; named so the table has no unexplained gap.</summary>
    RemovedAlternate = 11,
    /// <summary>The one code that prints NOTHING: its arm in the jump table pushes no string index and
    /// jumps straight to the epilogue. It is what lets wearing gear clear the bag cell without narrating —
    /// the bag and the equip window are separate client structures and only the <c>0x10</c> handler clears a
    /// bag entry, so the packet cannot simply be omitted.</summary>
    Silent = 12,
    /// <summary>"&lt;item&gt; broken."</summary>
    Broken = 13,
}
