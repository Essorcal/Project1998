using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ---- party / group (RTK clif_addgroup / clif_leavegroup / clif_updategroup, clif.c:13993-14148) -------
    // Ported rules, RTK's literal minitext wording where it has one. Not modelled: RTK's per-map "canGroup"
    // gate (no server-side concept of a no-group map here) and RTK's ghost-can-invite-others allowance (we
    // don't special-case a dead inviter either way).
    //
    // Two native gestures, no chat commands (the "@party" / "@leaveparty" fallbacks are gone):
    //   JOIN  — the "Group" button on another player's profile window -> 0x2E -> TryPartyInvite below.
    //           Aimed by the LEADER at someone already in their own group, it kicks them instead (RTK's own
    //           self-referential special case).
    //   LEAVE — turning your own "Join a group" toggle OFF ('s' -> the Options menu, 0x1B sub-0x02). See
    //           HandleSetting: that branch calls RemoveFromParty. There is no other way out.

    private void HandlePartyInvite(byte[] dec)
    {
        if (dec.Length < 1) return;
        int nameLen = dec[0];
        if (nameLen <= 0 || 1 + nameLen > dec.Length) return;
        TryPartyInvite(Encoding.ASCII.GetString(dec, 1, nameLen));
    }

    private void TryPartyInvite(string name)
    {
        var target = _world.FindPlayer(name);
        if (target is null) { SendBlueMessage($"{name} is nowhere to be found."); return; }   // RTK: silent nullpo_ret bail; we give feedback like whisper does — same blue channel too
        // Group-attempt feedback goes to the status pane (SendMiniText default type 3, same as NotifyGroup's
        // join/leave lines) — NOT type 11, which is the group/subpath CHAT channel and drew these as blue chat
        // text. Matches the trade-error lines just below, which already use the default.
        if (ReferenceEquals(target, this)) { SendMiniText("You ask yourself to group, but get declined."); return; }

        // RTK special case: the LEADER re-"inviting" someone already in their own party kicks them.
        if (_party is not null && ReferenceEquals(target._party, _party) && ReferenceEquals(_party.Leader, this))
        {
            RemoveFromParty(target);
            return;
        }

        if (_party is not null && _party.IsFull) { SendMiniText("Your group is already full."); return; }
        if (target.IsDead) { SendMiniText("They are unable to join this group."); return; }
        // Their "Join a group" toggle is off, or they're already in someone's group. ONE line for both, as
        // RTK does — the refusal must not tell you which, or it becomes a probe for who's already grouped.
        if (!target.WantsGroup || target._party is not null)
        { SendMiniText("They refuse to join this group."); return; }

        // A group FORMING announces both of its founders, not just the invitee: the inviter is joining a
        // group they weren't in a moment ago either, and the announcement always reaches everyone it names.
        bool forming = _party is null;
        if (forming) _party = new Party(this, target);
        else _party.Add(target);
        target._party = _party;

        if (forming) _party.Broadcast($"{Snapshot().Name} is joining the group.");
        _party.Broadcast($"{target.Snapshot().Name} is joining the group.");

        // Being in a group lights your OWN "Join a group" status (RTK shows the flag ON for every party
        // member). The invitee already had it on — the gate above required WantsGroup — so only the inviter
        // can be newly transitioning here; SetGroupStatus no-ops when the flag already matches, so adding to
        // an existing party (where the inviter is already grouped) announces nothing.
        SetGroupStatus(true);
    }

    /// <summary>Sync the "Join a group" status flag — the sidebar toggle line, and the persisted profile
    /// group/sociable cell (0x39/0x34) — to <paramref name="on"/>, announcing the change exactly once.
    /// No-ops when the flag already matches, so the party-membership callers (form/join → ON,
    /// leave/kick/disband → OFF) never re-announce a state the player is already in. Persists via MarkDirty
    /// rather than an immediate FlushNow because RemoveFromParty (a caller) also runs on the disconnect
    /// teardown: the write there must be left to the read-loop's own _replaced-guarded flush so a stale
    /// session can't clobber a fresher login (see Session.cs's disconnect save guard).</summary>
    private void SetGroupStatus(bool on)
    {
        if (_char.Grouped == on) return;
        _char.Grouped = on;
        MarkDirty();
        // Minitext into the status/"spell cast" pane (RTK clif_changestatus case 2 -> clif_sendminitext),
        // NOT SendMessage's 0x02 login-style box, which the in-game client doesn't surface. Space-padded
        // through SettingLine, like every other toggle line (the 2026-08-19 tab-separated spec is retired).
        SendMiniText(SettingLine("Join a group", on));
    }

    /// <summary>Removes <paramref name="member"/> from their party — the "Join a group" toggle going off
    /// (the leave gesture), the leader-kick special case above, and disconnect cleanup all land here.
    /// Promotes the next member to leader (Party.Remove: the leader is always Members[0]) and disbands
    /// (notifying the last straggler) if that drops the party to one person. RTK sends the exact same
    /// "You have left the group." text whether you left or were kicked (clif_addgroup's kick branch just
    /// calls clif_leavegroup(tsd) — no separate "removed" wording exists).</summary>
    private static void RemoveFromParty(Session member)
    {
        var party = member._party;
        if (party is null) return;
        string name = member.Snapshot().Name;
        bool disband = party.Remove(member);
        member._party = null;
        member.NotifyGroup("You have left the group.");
        member.SetGroupStatus(false);   // left or kicked out -> your "Join a group" status goes OFF (+ line)
        party.Broadcast($"{name} is leaving the group.");
        if (disband && party.Members.Count == 1)
        {
            var last = party.Members[0];
            last._party = null;
            last.NotifyGroup("Your group has disbanded.");
            last.SetGroupStatus(false);   // party fully disbanded -> the last member's status goes OFF too
        }
    }

    // ---- trade / exchange (RTK clif_handitem / clif_handgold / clif_parse_exchange, clif.c:14548-15250) ---
    // The negotiation itself runs on the client's REAL exchange window (opcode 0x42 out, 0x4a in) — see
    // Session.Exchange.cs for the full wire format and the RE behind it. What lives here is the part either
    // trigger shares: the gates that decide a trade may start at all, the finalize/transfer, and the native
    // hand-item / hand-gold gestures that fold into the same window.
    //
    // Rules ported from RTK: FLAG_EXCHANGE gate on the target, same map, not already trading, not dead; any
    // offer change un-confirms both sides (RTK gets that free from escrow — see Session.Exchange.cs); finalize
    // re-validates each item is still actually held (TransferItems) since nothing is escrowed at offer time.

    /// <summary>Start-of-trade path behind the profile window's "Exchange" button (0x4a type 0) and behind the
    /// hand gestures: RTK <c>clif_startexchange</c>'s gates, then the native window opens on BOTH sides, each
    /// naming the other. Refusals are RTK's own lines and go to the status text, since there is no window yet
    /// to put a message box on.</summary>
    private void TryStartTrade(Session target)
    {
        if (IsDead) { SendMiniText("Spirits can't do that."); return; }
        if (_trade is not null) { SendMiniText("You are already trading."); return; }
        // RTK's own line for this (clif_startexchange's `target == sd->bl.id` branch). Reachable because the
        // self-view profile carries a real id too, so its Exchange button sends OUR id back.
        if (ReferenceEquals(target, this))
        { SendMiniText("You move your items from one hand to another, but quickly get bored."); return; }
        if (target.CharMap != CharMap) { SendMiniText("That person refuses to exchange with you."); return; }
        if (target._trade is not null || target.IsDead || !target.WantsExchange)
        { SendMiniText("That person refuses to exchange with you."); return; }   // client's literal wording (esp. their Exchange flag off)

        var trade = new Trade(this, target);
        _trade = trade;
        target._trade = trade;
        // RTK also XORs FLAG_EXCHANGE off on both players here and never restores it (clif_exchange_close and
        // clif_exchange_cleanup both leave it flipped), which silently corrupts a setting the profile window
        // displays. The `_trade is not null` checks above already do the "busy" job, so that is not ported.
        SendExchangeOpen(target);
        target.SendExchangeOpen(this);
    }

    private static void UnconfirmBoth(Trade trade) { trade.OfferA.Confirmed = false; trade.OfferB.Confirmed = false; }

    private static void FinalizeTrade(Trade trade)
    {
        var a = trade.A; var b = trade.B;
        uint goldA = Math.Min(trade.OfferA.Gold, a._char.Coins);
        uint goldB = Math.Min(trade.OfferB.Gold, b._char.Coins);
        a._char.Coins = a._char.Coins - goldA + goldB;
        b._char.Coins = b._char.Coins - goldB + goldA;

        TransferItems(trade.OfferA.Items, a, b);
        TransferItems(trade.OfferB.Items, b, a);

        a.SendStats(); b.SendStats();

        // ONE transaction for both sides. Two separate saves can commit half an exchange — and whichever
        // half lands, the result is either a duplicated stack or a destroyed one. The in-memory transfer
        // above is careful enough that it can only ever under-deliver; this is what stops the PERSISTENCE
        // layer from undoing that care. A failed write leaves both sides dirty for the next flush to retry.
        if (!FlushPair(a, b))
            Log.Info($"!! trade save FAILED for '{a._char.Name}' <-> '{b._char.Name}' — both left dirty for retry");

        EndTrade(trade, TradeDoneText, done: true);
    }

    /// <summary>Moves each offered stack from <paramref name="from"/> to <paramref name="to"/>, re-checking
    /// live inventory (items aren't escrowed at offer time here — see Trade.cs) so a stale offer, where the
    /// sender dropped/sold/used the item mid-negotiation, can only under-deliver, never duplicate or destroy
    /// anything.</summary>
    private static void TransferItems(List<InvItem> offered, Session from, Session to)
    {
        foreach (var snap in offered)
        {
            // Match the SLOT that was actually offered (the exchange window keys its rows by slot too), and
            // re-check the identity in case the slot was emptied and refilled with something else mid-trade.
            var have = from._char.Inventory.FirstOrDefault(i => i.Slot == snap.Slot && i.ItemId == snap.ItemId
                                                             && i.Dura == snap.Dura && i.CustomName == snap.CustomName);
            if (have is null) continue;
            int amount = Math.Min(have.Amount, snap.Amount);
            if (amount <= 0) continue;
            var def = Content.ItemById(snap.ItemId);
            if (def is null) continue;
            // RECEIVER FIRST, and debit only what they actually took. The old order deducted from the sender
            // and then ignored the result, so trading into a full pack — or, since carry caps landed, into
            // someone already holding their limit — DESTROYED the goods outright.
            int placed = to.GivePlaced(def, amount, snap.Dura, snap.CustomName, owner: snap.Owner);
            if (placed <= 0) continue;                  // wouldn't fit; it stays with its owner
            have.Amount -= placed;
            // reason 9 = "You gave <item>." — a trade hand-over is exactly what that client line is for
            // (10 is "You sold", which is the vendor's). See the table in Content.EquipDelReason.
            if (have.Amount <= 0) { from._char.Inventory.Remove(have); from.SendDelItem(have.Slot, 9); }
            else from.SendAddItem(have);                // partial take: redraw the shrunken stack
        }
    }

    /// <summary>Tear the trade down on both sides and CLOSE both windows. <paramref name="done"/> picks which
    /// packet does the closing: a finished exchange lands the second half of the 0x42 sub-5 confirm latch
    /// (sub-5 <c>extra=0</c>, which the client only acts on because it already saw <c>extra=1</c> from the
    /// first confirm), while everything else — cancel, walk-away, disconnect — uses sub-4, which pops its box
    /// and closes unconditionally. Both are message boxes, so no status-line notify is needed.</summary>
    private static void EndTrade(Trade trade, string message, bool done = false)
    {
        if (trade.Ended) return;
        trade.Ended = true;
        foreach (var s in new[] { trade.A, trade.B })
        {
            if (!ReferenceEquals(s._trade, trade)) continue;
            s._trade = null;
            if (done) s.SendExchangeFinish(0, message);
            else      s.SendExchangeMessage(message);
        }
    }

    // ---- native "hand item" / "hand gold" gestures (RTK clif_handitem / clif_handgold, clif.c:14452-14644) --
    // Select a bag item, face a tile and press 'h' (hand ONE) or 'H'/Shift+h (GIVE the whole stack) -> 0x29;
    // the gold gesture ('h' then '\<amount>') -> 0x2A. Both resolve the tile you're facing (the same front-cell
    // lookup melee uses) and branch on what's there. Two very different destinations (confirmed live by the user):
    //
    //   * a PLAYER -> this is an EXCHANGE, NOT a give. The item/gold is pre-offered into the trade window and
    //     the two negotiate; if they aren't accepting exchanges the attempt is refused ("That person refuses to
    //     exchange with you."). See HandItemToPlayer / HandleHandGold's player branch and TryStartTrade.
    //   * a real MOB (a creature) -> mobs don't exchange, they just TAKE the item and it's gone. Because there's
    //     no trade window to back out of, the give is a permanent one and we confirm first with the client's
    //     "...no longer own it? (Y/N)" box, then say "You gave <item> (count)." — see HandItemToMobAsync. Mobs
    //     do NOT take money, so handing GOLD to one just fails with "S/He can't take it."
    //   * an NPC (a stationary mob) -> run its receiveItem/handItem script (a quest turn-in), else the refusal
    //     line. Gold to an NPC fails the same way a real mob does. See HandItemToNpcAsync.
    //
    // The player EXCHANGE reuses the dialog-driven trade (Trade.cs) — exactly as the 0x4A "Exchange" profile
    // button does — so handing to a player and the button reach the same negotiation.

    // 0x29 hand/give item: dec[0]=slot(1-based), dec[1]=handgive (0='h' one, 1='H' whole stack).
    private void HandleHandItem(byte[] dec)
    {
        if (dec.Length < 1) return;
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }
        if (BlockedByMount()) return;
        int slot = dec[0] - 1;
        int handgive = dec.Length > 1 ? dec[1] : 0;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;
        int amount = handgive == 1 ? it.Amount : 1;

        var (tx, ty) = FrontTile();

        // PLAYER -> exchange (item pre-offered into the trade window).
        var peer = _world.PeerAt(_char.Map, tx, ty);
        if (peer is not null) { HandItemToPlayer(peer, def, it, amount); return; }

        var mob = _world.MobAt(_char.Map, tx, ty);
        if (mob is null) return;                                   // empty tile -> nothing to hand to

        // NPC -> quest turn-in script, else it refuses out loud and drops the item at your feet.
        if (mob.IsNpc) { _ = HandItemToNpcAsync(mob, slot, def, amount); return; }

        // Real creature -> it TAKES the item and carries it (drops on death). NO server confirm: the 4.95
        // client already ran the entire give gesture inline (it showed "What do you wish to give, and no longer
        // own? [a-w\?]" in the chat input line and only sent this 0x29 once you answered). 4.95 has NO
        // give-confirmation string — the "(Y/N)" box is a later-client feature, ABSENT from NexusTK.dat.
        GiveItemToMob(mob, slot, def, amount);
    }

    // 0x2A hand gold: dec[0..3]=amount(u32 BE). Only PLAYERS exchange gold; a mob/NPC in front can't take money.
    private void HandleHandGold(byte[] dec)
    {
        if (dec.Length < 4) return;
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }
        if (BlockedByMount()) return;
        uint gold = (uint)((dec[0] << 24) | (dec[1] << 16) | (dec[2] << 8) | dec[3]);
        if (gold == 0) return;
        if (gold > _char.Coins) gold = _char.Coins;

        var (tx, ty) = FrontTile();
        var target = _world.PeerAt(_char.Map, tx, ty);
        if (target is null) return;                                // only players exchange; a mob/NPC in front is a silent no-op

        var trade = OpenOrContinueTradeWith(target);
        if (trade is null) return;                                 // refused / busy (message already sent)
        OfferGold(trade, gold);                                    // stages it and paints both windows' gold cells
    }

    private void HandItemToPlayer(Session target, ItemDef def, InvItem it, int amount)
    {
        var trade = OpenOrContinueTradeWith(target);
        if (trade is null) return;   // refused / already trading elsewhere (message already sent)
        RecordTradeItemOffer(trade, it, amount);   // stages it and draws the row on both windows
    }

    /// <summary>Get a live trade with <paramref name="target"/> to fold a handed item/gold into: reuse an
    /// existing trade with them, refuse if we're mid-trade with someone else, otherwise open a new one via
    /// <see cref="TryStartTrade"/> (which applies RTK's Exchange-flag / same-map / alive gates and sends the
    /// refusal itself). Returns null if no usable trade could be established.</summary>
    private Trade? OpenOrContinueTradeWith(Session target)
    {
        var tr = _trade;
        if (tr is not null && !tr.Ended)
        {
            if (ReferenceEquals(tr.Other(this), target)) return tr;
            SendMiniText("You are already trading.");
            return null;
        }
        TryStartTrade(target);
        tr = _trade;
        return tr is not null && !tr.Ended && ReferenceEquals(tr.Other(this), target) ? tr : null;
    }

    /// <summary>Hand an item to an NPC (a stationary, unkillable mob). The quest turn-in hooks get first refusal
    /// — an <see cref="INpcHandItemHandler"/> that accepts owns taking the item and giving any reward. If none
    /// want it, the NPC refuses OUT LOUD (an over-head bubble, not a status line): "What are you trying to do?
    /// Keep your junky &lt;item&gt; with you!" — and shoves it back, so the item leaves your bag and lands on the
    /// ground on your OWN tile. A NoDrop item can't be put on the ground, so for that the NPC just speaks and the
    /// item stays. Async because a turn-in may run a dialog; the give re-reads the bag after any await.</summary>
    private async Task HandItemToNpcAsync(Mob npc, int slot, ItemDef def, int amount)
    {
        var ndef = Content.NpcById(npc.NpcDefId);
        if (ndef is null) return;
        var ctx = new NpcContext(this, npc, ndef);
        try
        {
            foreach (var h in NpcScripts.For(ndef).OfType<INpcHandItemHandler>())
                if (await h.OnHandItem(ctx, def, amount)) return;   // consumed it (handler owns the delitem/reward)
        }
        catch (Exception e) { Log.Info($"!! NPC hand-item error: {e.Message}"); }

        // Nobody wanted it: the NPC says so out loud and hands it right back onto the ground at your feet.
        NpcBubble(npc, $"What are you trying to do? Keep your junky {def.Name} with you!");

        // Re-read the slot after the await, then deduct + drop. A NoDrop item can't hit the ground (the drop
        // handler forbids it too), so it just stays in the bag with the refusal.
        var it = InvAt(slot);
        if (it is null || it.ItemId != def.Id || def.NoDrop) return;
        int give = Math.Min(amount, it.Amount);
        if (give <= 0) return;
        it.Amount -= give;
        if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)it.Slot, 12); }  // 12 = silent; the NPC already spoke
        else SendAddItem(it);
        MarkDirty();
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
            X = _char.X, Y = _char.Y, Amount = give, Dura = it.Dura, Graphic = def.Icon, CustomName = it.CustomName,
            Owner = it.Owner });
    }

    /// <summary>Hand an item to a real creature. Mobs don't exchange — they just TAKE it and it's gone. There's
    /// no mob inventory here, so "the mob takes it" IS the item leaving the bag (a creature that must DO
    /// something with a turn-in is an NPC, handled above). No confirmation: the 4.95 client already ran the give
    /// gesture inline before sending 0x29 (see HandleHandItem), and 4.95 has no give-confirm string anyway.
    /// Clearing the slot with del-reason 9 makes the client print its OWN native "You gave &lt;item&gt;." line —
    /// the whole 4.95 del-reason family is name-only, there is no "(count)" variant, so we DON'T fabricate one.
    /// A partial give (hand ONE of a stack) leaves the slot occupied, so it redraws via 0x0F and the 4.95 client
    /// shows no line for it — matching the client, which prints "You gave" only on a full slot clear.
    /// <para>The creature CARRIES what it takes (<see cref="Mob.Handed"/>) and drops it back when killed — a
    /// sword handed to a cat is recoverable by killing the cat (World.TryDamage). A quest creature gets first
    /// crack at the item (the Leviathan talisman frees a captive instead of being pocketed).</para></summary>
    private void GiveItemToMob(Mob mob, int slot, ItemDef def, int amount)
    {
        var it = InvAt(slot);
        if (it is null || it.ItemId != def.Id) return;
        // A quest creature may want this specific item for something (the captured leviathan is freed by its
        // talisman, exactly as stepping onto the tile beside it is). If so, that consumes the gesture.
        if (TryQuestHandToMob(mob, def)) return;
        // A bound (NoDrop) item — a mount — can't be given away, same as it can't be dropped or thrown
        // (HandleDropItem/HandleThrow): it can't ride on a creature either, since the creature would drop it on
        // death and a NoDrop item can't hit the ground. Silent no-op. (A bonded-but-droppable item like a totem
        // helm is fine — it rides along and drops still bound to its owner.)
        if (def.NoDrop) return;
        int give = Math.Min(amount, it.Amount);
        if (give <= 0) return;

        ushort dura = it.Dura; string cname = it.CustomName; string owner = it.Owner;   // capture before the stack mutates
        it.Amount -= give;
        if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)it.Slot, 9); }  // 9 = client "You gave %s."
        else SendAddItem(it);
        MarkDirty();
        // The creature is carrying it now; killing the creature drops it back (World.TryDamage).
        (mob.Handed ??= new()).Add(new InvItem(0, def.Id, give, dura) { CustomName = cname, Owner = owner });
    }

    /// <summary>Quest reactions to handing a specific item to a specific creature (RTK's BL_MOB collection
    /// path). Returns true if the gesture was a quest action and must NOT be pocketed as ordinary loot.</summary>
    private bool TryQuestHandToMob(Mob mob, ItemDef def)
    {
        // Leviathan quest: handing the talisman to a captured leviathan frees it — the same effect, and the
        // same one-time legend gate, as stepping onto the tile beside it (Session.TryLeviathanRelease).
        if (mob.Key == LeviathanQuest.CaptiveMob && def.Key == LeviathanQuest.Talisman)
        {
            if (!HasLegend(LeviathanQuest.LegendFreed) && !HasLegend(LeviathanQuest.LegendEnemy)
                && TakeItem(LeviathanQuest.Talisman, 1))
                FreeLeviathanCaptive(mob);
            return true;   // the talisman is a quest token either way — never stuffed into the creature
        }
        return false;
    }

    // ---- bulletin boards (RTK clif_handle_boards, clif.c:11156-11201; wire shapes cross-checked against
    // the char-server hop, rtk/src/char/mapif.c, and the map-server's reply builder, rtk/src/map/intif.c,
    // since RTK splits board storage into a separate process this single-process server doesn't have).
    // Sub-command byte is dec[0]; board/post ids that follow are u16 BIG-ENDIAN (RTK SWAP16). RTK's own
    // 0x31 reply code sometimes leaves the "inc" byte (its byte 4) unwritten entirely (intif_parse_readpost
    // comments it out) — good evidence that byte isn't client-meaningful for this opcode, just RTK's own
    // framing detail, so these replies use our normal SendMap(op, inc, data) convention like every other
    // packet in this codebase rather than copying RTK's literal byte-4 values.
    private void HandleBoard(byte[] dec)
    {
        if (dec.Length < 1) return;
        // Log every board/nmail subcommand while we verify the native compose window (sub-6) live.
        Log.Info($"   0x3B board packet: subcmd={dec[0]} len={dec.Length}: {BitConverter.ToString(dec)}");
        switch (dec[0])
        {
            // Show Board. 'b', 'm' and the mail-arrow CLICK all send this identical `3b 01 00`, so the
            // server cannot tell them apart — but it doesn't need to. 'm' is armed ONLY while the mail
            // arrow is up (the HUD widget intercepts the key before the char-dispatch table), so answering
            // an unread inbox with the MAILBOX VIEW instead of the board list makes 'm' land straight in
            // the mailbox, which is what it looks like it should do.
            //
            // ⚠ MailFirstOnBoard NOW DEFAULTS OFF — turning it on HARD-FREEZES the client (live 2026-08-08).
            // The theory below (window ctor 0x406e80 already ran on the keypress, so a posts body will render)
            // is WRONG for this case: the window being open is necessary but not sufficient. Repro from
            // logs/server.log — `3b 01 00` answered with `boardposts(0x31) mailbox n=1` and the client sent
            // nothing ever again (no walk, no turn, no keypress) until the socket was torn down 35s later.
            // The very same posts bytes render fine when they answer sub-2, so the ctor evidently arms a
            // LIST-shaped parse and a posts body walks it off the end. Sub-1 must answer with the list.
            //
            // (Superseded reasoning, kept because it is still right about the unsolicited-0x31 case:) the
            // client runs the board-window ctor on the KEYPRESS, before the request goes out, so the window
            // is already open by the time this reply lands — which is why the old "an unsolicited 0x31 opens
            // no window" note doesn't apply here. It just isn't enough to make a mode swap safe.
            case 1:
                if (Content.MailFirstOnBoard && Mail.UnreadCount(_char.Name) > 0) SendBoardPosts(0);
                else SendBoardList();
                break;
            case 2: if (dec.Length >= 3) SendBoardPosts(U16(dec, 1)); break;                  // Show posts from board # (board 0 -> own mailbox)
            case 3: if (dec.Length >= 5) SendBoardReadPost(U16(dec, 1), U16(dec, 3)); break;  // Read post (board 0 -> own mailbox)
            case 4: HandleBoardMakePost(dec); break;                                          // Make post (board 0 rejected — see its own doc)
            case 5: if (dec.Length >= 5) HandleBoardDelete(U16(dec, 1), U16(dec, 3)); break;   // Delete post (board 0 -> own mailbox)
            case 6: HandleNmailSend(dec); break;                                              // Send nmail — the NATIVE compose window's packet
            case 9: SendBoardPosts(0); break;   // "Nmail": RTK's own case 9 is just boards_showposts(sd, 0) — open the mailbox
            // 7 (GM postcolor) / 8 (special write) aren't modelled — they need a GM-level concept this server lacks.
        }
    }

    // Sub-6 "Send nmail" — the NATIVE compose window's send packet, decoded from RTK nmail_write (map.c).
    // RTK reads the fields at raw fd offsets 8+; our `dec` begins at the subcmd byte (dec[i] == fd[i+5]), so:
    //   dec[3]=toLen, dec[4..]=recipient, then topicLen(u8), topic, msgLen(u16 BE), body, sendCopy(u8).
    // Level-10 gated exactly like RTK. This is the authentic in-game "compose a letter" path, and since the
    // "@mail" fallback was removed, the only one. The leading Log.Info in HandleBoard + the dump here confirm
    // whether the 4.95 client's compose UI actually emits this.
    private void HandleNmailSend(byte[] dec)
    {
        if (_char.Level < Content.MailMinLevel) { SendMiniText($"You must be at least level {Content.MailMinLevel} to view/send nmail."); return; }
        try
        {
            if (dec.Length < 4) { SendBoardAck(6, false, "That letter didn't go through."); return; }
            int toLen = dec[3];
            int p = 4;
            string toName = Encoding.ASCII.GetString(dec, p, toLen).TrimEnd('\0').Trim(); p += toLen;
            int topicLen = dec[p]; p += 1;
            string subject = Encoding.ASCII.GetString(dec, p, topicLen).TrimEnd('\0').Trim(); p += topicLen;
            int msgLen = (dec[p] << 8) | dec[p + 1]; p += 2;
            string body = Encoding.ASCII.GetString(dec, p, msgLen).TrimEnd('\0').Trim(); p += msgLen;
            bool sendCopy = p < dec.Length && dec[p] != 0;
            Log.Info($"   -> NMAIL compose: to='{toName}' topic='{subject}' bodyLen={body.Length} sendCopy={sendCopy}");

            // Failure acks use type=0 so the compose window stays open for the player to fix the field
            // (RTK intif: "User does not exist." / nmail_write: "Mail must contain a subject."/"...body.").
            if (toName.Length == 0)  { SendBoardAck(6, false, "Who is this letter for?"); return; }
            if (subject.Length == 0) { SendBoardAck(6, false, "Mail must contain a subject."); return; }
            if (body.Length == 0)    { SendBoardAck(6, false, "Mail must contain a body."); return; }
            if (!_store.Exists(toName) && _world.FindPlayer(toName) is null)
                { SendBoardAck(6, false, "User does not exist."); return; }

            var now = DateTime.UtcNow;
            Mail.Send(toName, _char.Name, subject, body, (byte)now.Month, (byte)now.Day, -1, 0, 0);
            if (sendCopy)   // "keep a copy for myself" checkbox — RTK topics the copy "[To <name>] <topic>"
                { Mail.Send(_char.Name, _char.Name, $"[To {toName}] {subject}", body, (byte)now.Month, (byte)now.Day, -1, 0, 0); RefreshMailFlags(); }   // the self-copy lights my own arrow
            _world.FindPlayer(toName)?.RefreshMailFlags();   // light the recipient's HUD arrow now if they're online
            SendBoardAck(6, true, "Your message has been sent.");   // RTK's exact success ack — closes the compose window
        }
        catch (Exception e)
        {
            Log.Info($"   -> NMAIL parse error: {e}");
            SendBoardAck(6, false, "That letter didn't go through.");
        }
    }

    // RTK nmail_sendmessage (map.c:164) — the ACK every board/nmail WRITE or DELETE action blocks on.
    // 0x31 body = other(u8: 6=write ack, 7=delete ack) type(u8: 1=success, 0=failure — success releases/
    // closes the client's compose window, failure leaves it open) msgLen(u8) message[...] trailer(u8=7).
    // This — not a 0x0D text line or a posts refresh — is the reply the real server sends after
    // posting/sending/deleting (intif.c: "Your message has been posted."/"...sent."/"...deleted.").
    private void SendBoardAck(byte other, bool ok, string msg)
    {
        var d = new List<byte> { other, (byte)(ok ? 1 : 0) };
        var mb = Ascii(msg);
        d.Add((byte)mb.Length);
        d.AddRange(mb);
        d.Add(7);
        SendMap(0x31, _gameInc++, d.ToArray(), $"boardack(0x31) other={other} ok={ok} '{msg}'");
    }

    private static int U16(byte[] d, int i) => (d[i] << 8) | d[i + 1];

    // Sub-1 "Show Board": the board list. RTK clif_showboards: type(1) titlelen(u8) title[titlelen]
    // boardCount(u8) then per board [id(u16BE) nameLen(u8) name[nameLen]]. RTK's own board list
    // (db/board_db.txt) is server-instance config not present in the reference tree — see Boards.All's
    // doc comment for what's seeded instead and why. LIVE-CONFIRMED 2026-07-28 (the list renders and
    // selecting an entry opens it).
    private void SendBoardList()
    {
        var d = new List<byte> { 1, 13 };
        d.AddRange(Ascii("NexusTKBoards"));
        d.Add((byte)(Boards.All.Count + 1));   // +1 for the personal Mailbox
        foreach (var b in Boards.All)
        {
            d.AddRange(Be((ushort)b.Id));
            var n = Ascii(b.Name);
            d.Add((byte)n.Length);
            d.AddRange(n);
        }
        // Board 0 = the player's nmail mailbox, listed LAST. Per RTK ("Board(0) == NMail"), opening board 0
        // switches the window into mailbox mode (reply flags2=4), where Write composes WITH a recipient
        // field (sub-6) instead of a recipient-less board post (sub-4) — this menu entry IS how mail is
        // sent natively. It sits at the end because the 'm' hotkey that would otherwise reach it directly
        // is dead in this build (see the note on the 'm' key in docs §11h) and there's no way to open the
        // mailbox without going through this list: an unsolicited mailbox 0x31 is IGNORED unless the board
        // window is already open (tested live 2026-07-28 — the client opens no window from it).
        d.AddRange(Be((ushort)0));
        var mn = Ascii("Mailbox");
        d.Add((byte)mn.Length);
        d.AddRange(mn);
        SendMap(0x31, _gameInc++, d.ToArray(), "boardlist(0x31) +Mailbox");
    }

    // Sub-2 "Show posts from board #": flags2(u8) flags1(u8) board(u16BE) boardNameLen(u8) boardName[...]
    // postCount(u8) then per post [color(u8) postId(u16BE) nameLen(u8) name[...] month(u8) day(u8)
    // topicLen(u8) topic[...]], newest first. flags2 is the WINDOW-MODE byte (RTK char/mapif.c):
    // 2 = normal board, 4 = NMAIL MAILBOX (Write button becomes recipient-field compose emitting sub-6).
    // flags1 = write/del rights (1=none? 3=write+del; special 6 = "write sends a packet" for scripted
    // boards, not modelled). Board id 0 is the player's OWN mailbox (RTK case 9 == this same builder
    // called with board 0 — see Mail.cs): "name" per post becomes the sender, and an unread letter's
    // topic gets a "* " prefix so a native mailbox listing shows what's new without a separate flag byte.
    // popup=true is the RTK "showBoard" / board-sign path: the client has NO board window open yet, so the
    // header must tell it to OPEN one. RTK (char/mapif.c mapif_parse_showposts) encodes this in flags1 — bit 0
    // CLEAR = server-initiated popup, SET = a reply to the client's own board request (window already open).
    // So a writable board is flags1=2 when popped open by a sign vs flags1=3 from the `b` menu. Without this,
    // an unsolicited 0x31 is silently dropped (the exact symptom: packet sent, nothing opens). Mailbox (board
    // 0) has no sign path, so popup only applies to real boards.
    private void SendBoardPosts(int boardId, bool popup = false)
    {
        if (boardId == 0)
        {
            var inbox = Mail.InboxFor(_char.Name);
            // flags2=4 is RTK's NMAIL marker (char/mapif.c mapif_parse_showposts: "if (a.board == 0)
            // board_header.flags2 = 4; else = 2") — THE byte that flips the client's board window into
            // mailbox mode, where Write composes WITH a recipient field (sub-6) instead of a board post
            // (sub-4). flags1=3 = CAN_WRITE|CAN_DEL (board 0 always grants both in boards_showposts).
            var d0 = new List<byte> { 4, 3 };
            d0.AddRange(Be((ushort)0));
            var mbn = Ascii("Mailbox");
            d0.Add((byte)mbn.Length);
            d0.AddRange(mbn);
            d0.Add((byte)inbox.Count);
            foreach (var m in inbox)
            {
                d0.Add(0);
                d0.AddRange(Be((ushort)m.Position));
                var sn = Ascii(m.Sender);
                d0.Add((byte)sn.Length);
                d0.AddRange(sn);
                d0.Add(m.Month);
                d0.Add(m.Day);
                var topic = (m.IsRead ? "" : "* ") + m.Topic;
                var tn0 = Ascii(topic);
                d0.Add((byte)tn0.Length);
                d0.AddRange(tn0);
            }
            SendMap(0x31, _gameInc++, d0.ToArray(), $"boardposts(0x31) mailbox n={inbox.Count}");
            return;
        }

        string name = Boards.Find(boardId)?.Name ?? "";
        var posts = Boards.PostsFor(boardId);

        // flags2=2 (real board), flags1=3 normally / 2 when we pop the window open (see popup note above).
        var d = new List<byte> { 2, (byte)(popup ? 2 : 3) };
        d.AddRange(Be((ushort)boardId));
        var bn = Ascii(name);
        d.Add((byte)bn.Length);
        d.AddRange(bn);
        d.Add((byte)posts.Count);
        foreach (var p in posts)
        {
            d.Add(0);   // color/highlighted (BrdHighlighted) — not modelled, always 0
            d.AddRange(Be((ushort)p.Id));
            var an = Ascii(p.Author);
            d.Add((byte)an.Length);
            d.AddRange(an);
            d.Add(p.Month);
            d.Add(p.Day);
            var tn = Ascii(p.Topic);
            d.Add((byte)tn.Length);
            d.AddRange(tn);
        }
        SendMap(0x31, _gameInc++, d.ToArray(), $"boardposts(0x31) board={boardId} n={posts.Count}");
    }

    // Sub-3 "Read post": type(u8: 3=board post, 5=nmail letter) buttons(u8=3, always writable)
    // nmailFlag(u8: 1 when board==0) postId(u16BE) authorLen(u8) author[...] month(u8) day(u8)
    // topicLen(u8) topic[...] bodyLen(u16BE) body[...] — per RTK intif_parse_readpost/mapif_parse_readpost.
    // Board id 0 -> the mailbox: marks the letter read and auto-claims
    // any attached parcel (see Mail.ClaimItem), so opening a letter in the native mailbox UI both marks it
    // read and hands over anything it was carrying.
    private void SendBoardReadPost(int boardId, int postId)
    {
        if (boardId == 0) { ReadMail(postId); return; }

        var post = Boards.Get(boardId, postId);
        if (post is null) { SendLog("That post no longer exists."); return; }

        var d = new List<byte> { 3, 3, 0 };
        d.AddRange(Be((ushort)postId));
        var an = Ascii(post.Author);
        d.Add((byte)an.Length);
        d.AddRange(an);
        d.Add(post.Month);
        d.Add(post.Day);
        var tn = Ascii(post.Topic);
        d.Add((byte)tn.Length);
        d.AddRange(tn);
        var bn = Ascii(post.Body);
        d.AddRange(Be((ushort)bn.Length));
        d.AddRange(bn);
        SendMap(0x31, _gameInc++, d.ToArray(), $"boardread(0x31) board={boardId} post={postId}");
    }

    // Sub-4 "Make post": board(u16BE) topicLen(u8) topic[...] bodyLen(u16BE) body[...]. RTK's own denial
    // wording ("Post must contain subject."/"...body.") is kept verbatim; confirmation text adapts RTK's
    // ("Your message has been posted.") to our SendLog channel since we don't reuse the raw system-message
    // opcode (same reasoning as whisper delivery). Board id 0 is rejected outright — see Mail.cs's doc on
    // why composing mail natively isn't wired (no recipient field survives anywhere in the reference tree).
    private void HandleBoardMakePost(byte[] dec)
    {
        if (dec.Length < 4) return;
        int boardId = U16(dec, 1);
        if (boardId == 0) { SendBoardAck(6, false, "Use the mailbox Write button to send mail."); return; }
        int topicLen = dec[3];
        if (4 + topicLen + 2 > dec.Length) return;
        string topic = Encoding.ASCII.GetString(dec, 4, topicLen);
        int bodyLen = U16(dec, 4 + topicLen);
        int bodyStart = 4 + topicLen + 2;
        if (bodyStart + bodyLen > dec.Length) return;
        string body = Encoding.ASCII.GetString(dec, bodyStart, bodyLen);

        if (topic.Trim().Length == 0) { SendBoardAck(6, false, "Post must contain subject."); return; }
        if (body.Trim().Length == 0) { SendBoardAck(6, false, "Post must contain a body."); return; }

        var now = DateTime.UtcNow;
        Boards.Post(boardId, _char.Name, topic, body, (byte)now.Month, (byte)now.Day);
        // The write ack (SendBoardAck other=6 type=1) is what the real server replies after a post — the
        // client's compose window blocks on it ("didn't go through" error came from replying with only a
        // 0x0D text line). The posts-refresh workaround that also un-hung it is superseded by this
        // RTK-faithful ack (intif.c: nmail_sendmessage(sd, "Your message has been posted.", 6, 1)).
        SendBoardAck(6, true, "Your message has been posted.");
    }

    // Sub-5 "Delete post": board(u16BE) postId(u16BE). RTK only lets a post's own author delete it here
    // (the broader GM/tutor CAN_DEL grant isn't modelled). Board id 0 -> delete from your OWN mailbox
    // (ownership there is "whose mailbox it's sitting in", not authorship — see Mail.Delete).
    private void HandleBoardDelete(int boardId, int postId)
    {
        // Delete acks ride other=7 (RTK intif delete response: "The message has been deleted." type=1 /
        // "You can only delete your own messages." type=0) — the board window blocks on this like writes.
        if (boardId == 0)
        {
            bool gone = Mail.Delete(_char.Name, postId);
            SendBoardAck(7, gone, gone ? "The message has been deleted." : "That letter no longer exists.");
            if (gone) RefreshMailFlags();   // deleting unread mail / an unclaimed parcel may clear the HUD arrow
            return;
        }
        bool ok = Boards.Delete(boardId, postId, _char.Name);
        SendBoardAck(7, ok, ok ? "The message has been deleted." : "You can only delete your own messages.");
    }

    // ---- mail (RTK nmail — see Mail.cs's doc for why compose is chat-command-only) -------------


    // The one read path: RTK case 3 aimed at board 0 (SendBoardReadPost) funnels through here — it marks the
    // letter read, and if it's carrying an
    // unclaimed parcel, gives the item now (pack-full falls back to dropping it at your feet, same recovery
    // as CastGroundLoot). Always sends the native sub-3 wire reply AND a SendLog summary: the wire reply's
    // shape is unverified (see SendBoardReadPost's doc), so the chat log stays the one channel guaranteed
    // to actually show the player what they got.
    private void ReadMail(int position)
    {
        var mail = Mail.Get(_char.Name, position);
        if (mail is null) { SendLog("That letter no longer exists."); return; }

        Mail.MarkRead(_char.Name, position);

        // The attachment claim and the character save go in ONE transaction — flipping `claimed` on its own
        // connection meant a crash before the next autosave consumed the attachment without delivering it.
        // See Parcel.ClaimIn / CharacterStore.SaveWith. Reading a letter with no attachment does no DB work
        // here at all: the conditional UPDATE simply matches nothing and we skip the save.
        string attachNote = "";
        if (mail.ItemId >= 0)
        {
            var snapshot = SnapshotBag();
            string? note = null;
            // Deferred for the same reason as the parcel path: a ground item has no database row, so
            // materializing it inside a transaction that then rolls back would leave the goods on the floor
            // AND the attachment still unclaimed.
            GroundItem? pendingDrop = null;
            bool committed = _store.SaveWith(_char, (cn, tx) =>
            {
                var claim = Mail.ClaimItemIn(cn, tx, _char.Name, position);
                if (claim is not (int itemId, int amount, int dura)) return false;   // no attachment, or already claimed

                var def = Content.ItemById(itemId);
                if (def is null) return true;   // consume it; an unresolvable item id isn't deliverable

                bool gotIt = GiveItem(def, amount, (ushort)Math.Max(0, dura), "");
                if (!gotIt)
                    pendingDrop = new GroundItem { Id = _world.AllocateItemId(), ItemId = itemId,
                        X = _char.X, Y = _char.Y, Amount = amount, Dura = (ushort)Math.Max(0, dura), Graphic = def.Icon };
                note = gotIt ? $" [Parcel: {def.Name} x{amount} added to your bag]"
                             : $" [Parcel: {def.Name} x{amount} — your bag was full, dropped at your feet]";
                return true;
            });

            if (committed)
            {
                if (pendingDrop is not null) _world.DropItem(_char.Map, pendingDrop);
                attachNote = note ?? "";
                SendStats();
            }
            else RestoreBag(snapshot);   // already claimed, or the write failed — either way undo the give
        }

        // type=5/buttons=3/nmailFlag=1 are RTK's nmail read-view values (map/intif.c intif_parse_readpost:
        // body[2]=1 when board==0; char/mapif.c mapif_parse_readpost: type=5, buttons=3 for nmail) — the
        // letter view (with Reply) rather than the plain board-post view (type=3, flag=0).
        var d = new List<byte> { 5, 3, 1 };
        d.AddRange(Be((ushort)position));
        var sn = Ascii(mail.Sender);
        d.Add((byte)sn.Length);
        d.AddRange(sn);
        d.Add(mail.Month);
        d.Add(mail.Day);
        var tn = Ascii(mail.Topic);
        d.Add((byte)tn.Length);
        d.AddRange(tn);
        var bn = Ascii(mail.Body);
        d.AddRange(Be((ushort)bn.Length));
        d.AddRange(bn);
        SendMap(0x31, _gameInc++, d.ToArray(), $"boardread(0x31) mailbox post={position}");

        SendLog($"From {mail.Sender} ({mail.Month}/{mail.Day}): {mail.Topic} — {mail.Body}{attachNote}");
        RefreshMailFlags();   // reading (+ claiming any parcel) may clear the HUD mail/parcel arrow — refresh body[45]
    }

    // MAIL HAS NO CHAT COMMAND. The native 0x3B board window covers list, read, delete and compose (see
    // HandleBoard / HandleBoardWrite / HandleBoardDelete above), so "@mail" was removed along with the other
    // chat fallbacks for natively-reachable features. The one capability that went with it: attaching a
    // PARCEL to an outgoing letter ("@mail sendItem"). Native compose always posts itemId -1, and nothing
    // else in the server lets a player mail an item — Mail.Send still takes the item arguments and ReadMail
    // still claims an attachment, so a scripted/quest sender works; only the player-facing path is gone.

    // Route the player's spoken words to a nearby NPC's say-handler. Nearest say-capable NPC first; the first
    // handler that consumes the speech (runs a dialog) wins, so unrelated chatter just falls through. Async
    // (dialog awaits replies), so fire-and-forget like OpenNpcDialog. See INpcSayHandler / RTK onSayClick.
    private void DispatchSpeech(string text)
    {
        string say = text.Trim().ToLowerInvariant();
        if (say.Length == 0 || say[0] == '!') return;   // empty / GM command -> not NPC speech

        var candidates = new List<(Mob npc, NpcDef def, List<INpcSayHandler> handlers)>();
        foreach (var npc in _world.NpcsNear(_char.Map, _char.X, _char.Y, Content.SpeechRange))
        {
            var def = Content.NpcById(npc.NpcDefId);
            if (def is null) continue;
            var handlers = NpcScripts.For(def).OfType<INpcSayHandler>().ToList();
            if (handlers.Count > 0 || NpcScript.HasSay(def.Key)) candidates.Add((npc, def, handlers));   // C# and/or Lua say-handler
        }
        if (candidates.Count > 0) _ = RunNpcSayAsync(candidates, say);
    }

    private async Task RunNpcSayAsync(List<(Mob npc, NpcDef def, List<INpcSayHandler> handlers)> candidates, string speech)
    {
        try
        {
            foreach (var (npc, def, handlers) in candidates)
            {
                var ctx = new NpcContext(this, npc, def);
                // Lua speech handler wins per-NPC (like the click path); C# handlers are the fallback for
                // NPCs not yet migrated. First handler to CONSUME the speech ends dispatch.
                if (NpcScript.HasSay(def.Key) && await NpcScript.RunSayAsync(ctx, def.Key, speech)) return;
                foreach (var h in handlers)
                    if (await h.OnSay(ctx, speech)) return;
            }
        }
        catch (Exception e) { Log.Info($"!! NPC say error: {e.Message}"); }
    }

    private uint _probeId = 1000;

    // Up to 7 whitespace-separated byte values from a command's ARGUMENT TAIL (see Server/Commands.cs);
    // missing/unparseable positions stay 0. Indexes from token 0 — handlers no longer see the command name.
    private static byte[] ParseBytes(string args)
    {
        var parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var app = new byte[7];
        for (int i = 0; i < parts.Length && i < 7; i++) byte.TryParse(parts[i], out app[i]);
        return app;
    }

    // Spawn one dummy just north of the player with the given 7 appearance bytes; its name is the
    // bytes so the screen is self-labeling. New id each call so repeats don't collide.
    private void LookOne(string text)
    {
        var app = ParseBytes(text);
        uint id = ++_probeId;
        ushort x = (ushort)Math.Clamp(_char.X, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        SendLook(id, x, y, dir: 2, app, renderKind: 1, $"{app[0]}-{app[1]}-{app[2]}", $"look-lab {id}");
        Log.Info($"   -> LOOK dummy id={id} @({x},{y}) app=[{string.Join(" ", app)}]");
    }

    // "@row i lo hi [body]": sweep appearance byte [i] from lo..hi across a west->east row of dummies, all
    // other bytes 0. One screenshot then maps that byte's entire id space. Optional 4th arg sets appearance
    // byte [0] (the BODY/sex) for the whole row — default 1 (female, the historically-swept base); pass 0 to
    // sweep the MALE body (its weapon/shield defaults differ from female — male frame 0 was never mapped).
    private void LookRow(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int idx = parts.Length > 0 && int.TryParse(parts[0], out var pi) ? Math.Clamp(pi, 0, 6) : 0;
        int lo = parts.Length > 1 && int.TryParse(parts[1], out var pl) ? pl : 0;
        int hi = parts.Length > 2 && int.TryParse(parts[2], out var ph) ? ph : lo + 7;
        int body = parts.Length > 3 && int.TryParse(parts[3], out var pb) ? pb : 1;
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        int col = 0;
        for (int v = lo; v <= hi && col < 12; v++, col++)
        {
            // Base = valid body (0)=body, normal form (1)=0, so sweeping [2..6] reads cleanly instead of
            // being blanked by the form/state byte. appearance[1] itself is the form table (0/4 normal,
            // 1 ghost, 3 mounted, 5 invisible-spell, most others = no sprite).
            var app = new byte[] { (byte)body, 0, 0, 0, 0, 0, 0 };
            app[idx] = (byte)v;
            uint id = ++_probeId;
            ushort x = (ushort)Math.Clamp(_char.X - 4 + col, 0, _char.MapXs - 1);
            SendLook(id, x, y, dir: 2, app, renderKind: 1, $"{idx}={v}", $"row byte[{idx}]={v}");
        }
        Log.Info($"   -> LOOK row: appearance[{idx}] sweep {lo}..{hi}");
    }

}
