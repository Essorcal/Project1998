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
    // don't special-case a dead inviter either way — nothing here stops a ghost from typing "!party").

    /// <summary>"!party &lt;name&gt;" invites (or, from the leader onto an existing member of their OWN
    /// party, KICKS — RTK's own self-referential special case in clif_addgroup) another player. "!party"
    /// alone lists the roster. The chat command is the primary trigger; the 0x2E opcode case above is wired
    /// defensively as a bonus since 4.95 has never been captured actually sending it.</summary>
    private void HandlePartyCommand(string text)
    {
        string rest = text.Length > "!party".Length ? text["!party".Length..].Trim() : "";
        if (rest.Length == 0) { ShowPartyRoster(); return; }
        TryPartyInvite(rest);
    }

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
        if (target is null) { SendLog($"{name} is nowhere to be found."); return; }   // RTK: silent nullpo_ret bail; we give feedback like whisper does
        if (ReferenceEquals(target, this)) { SendMiniText("You can't group yourself...", type: 11); return; }

        // RTK special case: the LEADER re-"inviting" someone already in their own party kicks them.
        if (_party is not null && ReferenceEquals(target._party, _party) && ReferenceEquals(_party.Leader, this))
        {
            RemoveFromParty(target);
            return;
        }

        if (_party is not null && _party.IsFull) { SendMiniText("Your group is already full.", type: 11); return; }
        if (target.IsDead) { SendMiniText("They are unable to join your party.", type: 11); return; }
        if (!target.WantsGroup) { SendMiniText("They have refused to join your party.", type: 11); return; }
        if (target._party is not null) { SendMiniText("They have refused to join your party.", type: 11); return; }

        if (_party is null) _party = new Party(this, target);
        else _party.Add(target);
        target._party = _party;

        _party.Broadcast($"{target.Snapshot().Name} is joining the group.");
    }

    /// <summary>Removes <paramref name="member"/> from their party — used for "!leaveparty", the leader-kick
    /// special case above, and disconnect cleanup. Promotes the next member to leader (Party.Remove: the
    /// leader is always Members[0]) and disbands (notifying the last straggler) if that drops the party to
    /// one person. RTK sends the exact same "You have left the group." text whether you left or were kicked
    /// (clif_addgroup's kick branch just calls clif_leavegroup(tsd) — no separate "removed" wording exists).</summary>
    private static void RemoveFromParty(Session member)
    {
        var party = member._party;
        if (party is null) return;
        string name = member.Snapshot().Name;
        bool disband = party.Remove(member);
        member._party = null;
        member.NotifyGroup("You have left the group.");
        party.Broadcast($"{name} is leaving the group.");
        if (disband && party.Members.Count == 1)
        {
            var last = party.Members[0];
            last._party = null;
            last.NotifyGroup("Your group has disbanded.");
        }
    }

    private void LeaveParty()
    {
        if (_party is null) { SendMiniText("You are not in a group.", type: 11); return; }
        RemoveFromParty(this);
    }

    private void ShowPartyRoster()
    {
        if (_party is null) { SendMiniText("You are not in a group.", type: 11); return; }
        SendMiniText($"Party ({_party.Members.Count}/{Party.MaxMembers}):", type: 11);
        foreach (var m in _party.Members)
            SendMiniText($"{(ReferenceEquals(m, _party.Leader) ? "* " : "  ")}{m.Snapshot().Name} - HP {m.CharHp}/{m.CharMaxHp}", type: 11);
    }

    // ---- trade / exchange (RTK clif_handitem / clif_handgold / clif_parse_exchange, clif.c:14548-15250) ---
    // See Trade.cs's doc comment for why this is dialog-driven instead of guessing RTK's real binary
    // exchange window. Rules ported: FLAG_EXCHANGE gate on both sides, same map, not already trading, not
    // dead; any offer change un-confirms both sides (needed so a stale confirm can't sneak a changed offer
    // through — RTK's own two-step clif_exchange_sendok confirm dance depends on the same invariant); finalize
    // re-validates each item is still actually held (TransferItems) since nothing is escrowed at offer time.

    // A virtual "npc" purely for the dialog packet header (id/sprite/name) — never spawned or looked up.
    // Distinct sentinel from F1 (0xFFFFFFFF) / subpath-chat (0xFFFFFFFE) — see HandleClickInfo.
    private static readonly Mob TradeVirtualNpc = new(0xFFFFFFFD, 0, 0, 0, "Trade", 1);

    /// <summary>"!trade &lt;name&gt;" — a name-based fallback trigger for testing/manual use. The REAL
    /// trigger is the "Exchange" button on another player's profile window (see HandleExchangeRequest,
    /// opcode 0x4A), which addresses the target by id since the client already has it from the click.</summary>
    private void HandleTradeCommand(string text)
    {
        string name = text.Length > "!trade".Length ? text["!trade".Length..].Trim() : "";
        if (name.Length == 0) { SendLog("Trade with whom? Try: !trade <name>"); return; }
        var target = _world.FindPlayer(name);
        if (target is null) { SendLog($"{name} is nowhere to be found."); return; }
        TryStartTrade(target);
    }

    // 0x4A = RTK's exchange sub-protocol dispatch (clif_parse_exchange, clif.c:14647-14754): a type(u8)
    // byte then per-type args. Only type 0 ("initiate", body: 00 targetId(u32BE)) is wired — that's the
    // "Exchange" button click on a profile window (§11l), which is the only sub-message the client would
    // ever send while THIS server is driving the rest of the negotiation through dialogs instead of RTK's
    // real trade-window sub-opcodes (types 1-5: amount-ask, add-item, add-gold, quit, finish all belong to
    // that window, which this server never opens). CONFIRMED wire-real: 4.95 has been captured actually
    // sending 0x4A before (see docs §9.5), unlike the untested 0x29/0x2A hand-item/hand-gold gesture.
    private void HandleExchangeRequest(byte[] dec)
    {
        if (dec.Length < 5 || dec[0] != 0) return;   // only "initiate" is handled; other sub-types are no-ops
        uint targetId = (uint)((dec[1] << 24) | (dec[2] << 16) | (dec[3] << 8) | dec[4]);
        var target = _world.PlayerById(targetId);
        if (target is not null) TryStartTrade(target);
    }

    /// <summary>Shared start-of-trade path for both triggers above: RTK's gates (alive, same map, not
    /// already trading, target's FLAG_EXCHANGE on) then hands off to the dialog-driven negotiation.</summary>
    private void TryStartTrade(Session target)
    {
        if (IsDead) { SendMiniText("Spirits can't do that."); return; }
        if (_trade is not null) { SendMiniText("You are already trading."); return; }
        if (ReferenceEquals(target, this)) { SendMiniText("You can't trade with yourself..."); return; }
        if (target.CharMap != CharMap) { SendMiniText("They have refused to exchange with you"); return; }
        if (target._trade is not null || target.IsDead || !target.WantsExchange)
        { SendMiniText("They have refused to exchange with you"); return; }   // RTK's literal wording

        var trade = new Trade(this, target);
        _trade = trade;
        target._trade = trade;
        SendMiniText($"You offer to trade with {target.Snapshot().Name}.");
        target.Notify($"{Snapshot().Name} wants to trade with you.");
        _ = RunTradeMenuAsync(trade);
        _ = target.RunTradeMenuAsync(trade);
    }

    /// <summary>The per-player trade menu loop — runs independently on EACH side's own Session, same
    /// pattern as every other Dlg* flow (this session's own async dialog state; a shared Trade object is
    /// the only cross-talk). Exits as soon as the trade is cancelled/finalized or this player dismisses the
    /// menu (0 = cancel, matching every other DlgMenu loop in this file).</summary>
    private async Task RunTradeMenuAsync(Trade trade)
    {
        var npc = TradeVirtualNpc;
        while (!trade.Ended)
        {
            bool theirsConfirmed = trade.OfferOf(trade.Other(this)).Confirmed;
            var opts = new List<string>
            {
                "Offer an item", "Offer gold", "Review offer",
                trade.OfferOf(this).Confirmed ? "Un-confirm" : "Confirm trade",
                "Cancel trade",
            };
            int choice = await DlgMenu(npc,
                $"Trading with {trade.Other(this).Snapshot().Name} - they have {(theirsConfirmed ? "" : "NOT ")}confirmed.",
                opts);
            if (trade.Ended) return;

            switch (choice)
            {
                case 1: await TradeOfferItem(trade); break;
                case 2: await TradeOfferGold(trade); break;
                case 3: await TradeReview(trade); break;
                case 4: TradeToggleConfirm(trade); break;
                default: EndTrade(trade, "Exchange cancelled."); return;   // 5, or 0 = dismissed the menu
            }
        }
    }

    private async Task TradeOfferItem(Trade trade)
    {
        var npc = TradeVirtualNpc;
        var mine = trade.OfferOf(this);
        var bag = _char.Inventory.OrderBy(i => i.Slot).ToList();
        if (bag.Count == 0) { await DlgSay(npc, "You have nothing to offer."); return; }

        int i = await DlgMenu(npc, "Which item will you offer?",
            bag.Select(it => $"{Content.ItemById(it.ItemId)?.Name ?? "?"} x{it.Amount}").ToList());
        if (trade.Ended || i < 1 || i > bag.Count) return;
        var chosen = bag[i - 1];

        int amount = 1;
        if (chosen.Amount > 1)
        {
            var s = await DlgInput(npc, $"You have {chosen.Amount}. How many will you offer?");
            if (trade.Ended || !int.TryParse(s, out amount) || amount <= 0) return;
            amount = Math.Min(amount, chosen.Amount);
        }

        mine.Items.RemoveAll(x => x.ItemId == chosen.ItemId && x.Dura == chosen.Dura && x.CustomName == chosen.CustomName);
        mine.Items.Add(new InvItem(0, chosen.ItemId, amount, chosen.Dura) { CustomName = chosen.CustomName });
        UnconfirmBoth(trade);
        string itemName = Content.ItemById(chosen.ItemId)?.Name ?? "?";
        trade.Other(this).Notify($"{Snapshot().Name} offers {itemName} x{amount}.");
        await DlgSay(npc, $"You offer {itemName} x{amount}.");
    }

    private async Task TradeOfferGold(Trade trade)
    {
        var npc = TradeVirtualNpc;
        var mine = trade.OfferOf(this);
        var s = await DlgInput(npc, $"You carry {_char.Coins} coins. How much will you offer?");
        if (trade.Ended || !uint.TryParse(s, out uint amount)) return;
        if (amount > _char.Coins) amount = _char.Coins;
        mine.Gold = amount;
        UnconfirmBoth(trade);
        trade.Other(this).Notify($"{Snapshot().Name} offers {amount} gold.");
        await DlgSay(npc, $"You offer {amount} gold.");
    }

    private async Task TradeReview(Trade trade)
    {
        var npc = TradeVirtualNpc;
        await DlgSay(npc, $"You offer: {DescribeOffer(trade.OfferOf(this))}");
        if (!trade.Ended) await DlgSay(npc, $"{trade.Other(this).Snapshot().Name} offers: {DescribeOffer(trade.OfferOf(trade.Other(this)))}");
    }

    private static string DescribeOffer(TradeOffer o)
    {
        var parts = o.Items.Select(it => $"{Content.ItemById(it.ItemId)?.Name ?? "?"} x{it.Amount}").ToList();
        if (o.Gold > 0) parts.Add($"{o.Gold} gold");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    private static void UnconfirmBoth(Trade trade) { trade.OfferA.Confirmed = false; trade.OfferB.Confirmed = false; }

    private void TradeToggleConfirm(Trade trade)
    {
        var mine = trade.OfferOf(this);
        mine.Confirmed = !mine.Confirmed;
        trade.Other(this).Notify(mine.Confirmed ? $"{Snapshot().Name} has confirmed the trade." : $"{Snapshot().Name} has un-confirmed.");
        if (trade.OfferA.Confirmed && trade.OfferB.Confirmed) FinalizeTrade(trade);
    }

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
        a.SaveChar(); b.SaveChar();
        EndTrade(trade, "You exchanged, and gave away ownership of the items.");
    }

    /// <summary>Moves each offered stack from <paramref name="from"/> to <paramref name="to"/>, re-checking
    /// live inventory (items aren't escrowed at offer time here — see Trade.cs) so a stale offer, where the
    /// sender dropped/sold/used the item mid-negotiation, can only under-deliver, never duplicate or destroy
    /// anything.</summary>
    private static void TransferItems(List<InvItem> offered, Session from, Session to)
    {
        foreach (var snap in offered)
        {
            var have = from._char.Inventory.FirstOrDefault(i => i.ItemId == snap.ItemId && i.Dura == snap.Dura && i.CustomName == snap.CustomName);
            if (have is null) continue;
            int amount = Math.Min(have.Amount, snap.Amount);
            if (amount <= 0) continue;
            var def = Content.ItemById(snap.ItemId);
            if (def is null) continue;
            have.Amount -= amount;
            if (have.Amount <= 0) { from._char.Inventory.Remove(have); from.SendDelItem(have.Slot, 0); }
            to.GiveItem(def, amount, snap.Dura, snap.CustomName);
        }
    }

    private static void EndTrade(Trade trade, string message)
    {
        if (trade.Ended) return;
        trade.Ended = true;
        if (ReferenceEquals(trade.A._trade, trade)) { trade.A._trade = null; trade.A.Notify(message); }
        if (ReferenceEquals(trade.B._trade, trade)) { trade.B._trade = null; trade.B.Notify(message); }
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
            case 1: SendBoardList(); break;                                                  // Show Board
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
    // Level-10 gated exactly like RTK. This is the authentic in-game "compose a letter" path (vs our
    // !mail-send chat fallback). The leading Log.Info in HandleBoard + the dump here let us confirm live
    // whether the 4.95 client's compose UI actually emits this.
    private void HandleNmailSend(byte[] dec)
    {
        if (_char.Level < MailMinLevel) { SendMiniText($"You must be at least level {MailMinLevel} to view/send nmail."); return; }
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
                Mail.Send(_char.Name, _char.Name, $"[To {toName}] {subject}", body, (byte)now.Month, (byte)now.Day, -1, 0, 0);
            _world.FindPlayer(toName)?.SendStats();   // light the recipient's HUD arrow now if they're online
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
    private void SendBoardPosts(int boardId)
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

        var d = new List<byte> { 2, 3 };
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
    // any attached parcel (see Mail.ClaimItem) the same way reading it via "!mail read" does, so a native
    // mailbox UI and the chat-command fallback behave identically regardless of which one the player uses.
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
            if (gone) SendStats();   // deleting unread mail / an unclaimed parcel may clear the HUD arrow
            return;
        }
        bool ok = Boards.Delete(boardId, postId, _char.Name);
        SendBoardAck(7, ok, ok ? "The message has been deleted." : "You can only delete your own messages.");
    }

    // ---- mail (RTK nmail — see Mail.cs's doc for why compose is chat-command-only) -------------

    private const int MailMinLevel = 10;   // RTK clif_handle_boards case 6: "You must be at least level 10 to view/send nmail."

    // Shared read path: RTK case 3 aimed at board 0 (SendBoardReadPost) and "!mail read <id>" both funnel
    // through here so reading behaves identically either way — marks it read, and if it's carrying an
    // unclaimed parcel, gives the item now (pack-full falls back to dropping it at your feet, same recovery
    // as CastGroundLoot). Always sends the native sub-3 wire reply AND a SendLog summary: the wire reply's
    // shape is unverified (see SendBoardReadPost's doc), so the chat log stays the one channel guaranteed
    // to actually show the player what they got.
    private void ReadMail(int position)
    {
        var mail = Mail.Get(_char.Name, position);
        if (mail is null) { SendLog("That letter no longer exists."); return; }

        Mail.MarkRead(_char.Name, position);
        string attachNote = "";
        var claim = Mail.ClaimItem(_char.Name, position);
        if (claim is (int itemId, int amount, int dura))
        {
            var def = Content.ItemById(itemId);
            if (def is not null)
            {
                bool gotIt = GiveItem(def, amount, (ushort)Math.Max(0, dura), "");
                if (!gotIt)
                    _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = itemId,
                        X = _char.X, Y = _char.Y, Amount = amount, Dura = (ushort)Math.Max(0, dura), Graphic = def.Icon });
                attachNote = gotIt ? $" [Parcel: {def.Name} x{amount} added to your bag]"
                                    : $" [Parcel: {def.Name} x{amount} — your bag was full, dropped at your feet]";
            }
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
        SendStats();   // reading (+ claiming any parcel) may clear the HUD mail/parcel arrow — refresh body[45]
    }

    // "!mail" (inbox list) / "!mail read <id>" / "!mail delete <id>" / "!mail send <name> | <subject> | <body>"
    // / "!mail sendItem <name> <itemKey> [amount] | <subject> | <body>". RTK gates nmail at level 10 (see
    // MailMinLevel); everything else is our own design — the real nmail_write/boards_post wire format has no
    // surviving source anywhere in this reference tree (Mail.cs's doc), so there's no RTK literal to port
    // for composing. sendItem pulls straight from the caster's own bag (by inventory slot number, or by the
    // item's Content key/display name — whichever matches) and removes it from their inventory immediately,
    // same as handing it over in person.
    private void HandleMailCommand(string text)
    {
        var rest = text.Length > "!mail".Length ? text["!mail".Length..].Trim() : "";
        if (rest.Length == 0) { ListMail(); return; }

        int sp = rest.IndexOf(' ');
        string sub = (sp < 0 ? rest : rest[..sp]).ToLowerInvariant();
        string arg = sp < 0 ? "" : rest[(sp + 1)..].Trim();

        switch (sub)
        {
            case "read":
                if (!int.TryParse(arg, out var readId)) { SendLog("usage: !mail read <id>"); return; }
                ReadMail(readId);
                break;
            case "delete":
                if (!int.TryParse(arg, out var delId)) { SendLog("usage: !mail delete <id>"); return; }
                SendLog(Mail.Delete(_char.Name, delId) ? "The letter has been deleted." : "That letter no longer exists.");
                break;
            case "send":
                SendMailCommand(arg, itemArg: null);
                break;
            case "senditem":
                {
                    int isp = arg.IndexOf(' ');
                    if (isp < 0) { SendLog("usage: !mail sendItem <name> <item> [amount] | <subject> | <body>"); return; }
                    string toName = arg[..isp];
                    SendMailCommand($"{toName} | {arg[(isp + 1)..]}", itemArg: arg[(isp + 1)..]);
                }
                break;
            default:
                ListMail();
                break;
        }
    }

    private void ListMail()
    {
        var inbox = Mail.InboxFor(_char.Name);
        if (inbox.Count == 0) { SendLog("Your mailbox is empty."); return; }
        foreach (var m in inbox)
            SendLog($"[{m.Position}]{(m.IsRead ? "" : " *NEW*")} From {m.Sender} ({m.Month}/{m.Day}): {m.Topic}{(m.ItemId >= 0 && !m.Claimed ? " [parcel attached]" : "")}");
        SendLog("!mail read <id> to open one, !mail delete <id> to remove it.");
    }

    // "<name> | <subject> | <body>" — pipe-delimited since names/subjects can contain spaces. itemArg, when
    // set, is "<item> [amount] | <subject> | <body>" (senditem's own dispatch already stripped the name).
    private void SendMailCommand(string spec, string? itemArg)
    {
        if (_char.Level < MailMinLevel) { SendMiniText($"You must be at least level {MailMinLevel} to view/send nmail."); return; }

        var parts = spec.Split('|');
        if (parts.Length < 3) { SendLog("usage: !mail send <name> | <subject> | <body>"); return; }
        string toName = parts[0].Trim();
        string subject = parts[1].Trim();
        string body = parts[2].Trim();
        if (toName.Length == 0 || subject.Length == 0 || body.Length == 0) { SendLog("Post must contain subject."); return; }
        if (toName.Equals(_char.Name, StringComparison.OrdinalIgnoreCase)) { SendLog("You can't mail yourself."); return; }

        int itemId = -1, amount = 0, dura = 0;
        if (itemArg is not null)
        {
            var iparts = itemArg.Split('|')[0].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (iparts.Length < 1) { SendLog("usage: !mail sendItem <name> <item> [amount] | <subject> | <body>"); return; }
            int amt = iparts.Length > 1 && int.TryParse(iparts[1], out var a) ? Math.Max(1, a) : 1;

            InvItem? slot = null;
            ItemDef? def = null;
            if (int.TryParse(iparts[0], out var slotNum)) slot = InvAt(slotNum - 1);   // 1-based, matching the bag UI (same convention as HandleDropItem)
            if (slot is not null) def = Content.ItemById(slot.ItemId);
            if (def is null)
            {
                slot = _char.Inventory.FirstOrDefault(i =>
                    (Content.ItemById(i.ItemId)?.Key.Equals(iparts[0], StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (Content.ItemById(i.ItemId)?.Name.Equals(iparts[0], StringComparison.OrdinalIgnoreCase) ?? false));
                def = slot is null ? null : Content.ItemById(slot.ItemId);
            }
            if (slot is null || def is null) { SendLog($"You don't have '{iparts[0]}' to send."); return; }
            amt = Math.Min(amt, slot.Amount);

            // Same removal shape as HandleDropItem: shrink the stack or clear the slot outright.
            int remaining = slot.Amount - amt;
            if (remaining <= 0) { _char.Inventory.Remove(slot); SendDelItem(slot.Slot, 1); }
            else { slot.Amount = remaining; SendAddItem(slot); }
            MarkDirty();
            itemId = def.Id; amount = amt; dura = slot.Dura;
        }

        var now = DateTime.UtcNow;
        Mail.Send(toName, _char.Name, subject, body, (byte)now.Month, (byte)now.Day, itemId, amount, dura);
        SendLog(itemId >= 0 ? $"Mailed {subject} to {toName} (with {amount}x parcel)." : $"Mailed {subject} to {toName}.");
        // If the recipient is online, light their HUD arrow/bag right away (RTK's intif_parse_findmp does the
        // same — sets the flag and re-sends status the moment mail lands, no relog needed).
        _world.FindPlayer(toName)?.SendStats();
    }

    // Route the player's spoken words to a nearby NPC's say-handler. Nearest say-capable NPC first; the first
    // handler that consumes the speech (runs a dialog) wins, so unrelated chatter just falls through. Async
    // (dialog awaits replies), so fire-and-forget like OpenNpcDialog. See INpcSayHandler / RTK onSayClick.
    private const int SpeechRange = 8;   // tiles (Chebyshev) an NPC will "hear" the player from
    private void DispatchSpeech(string text)
    {
        string say = text.Trim().ToLowerInvariant();
        if (say.Length == 0 || say[0] == '!') return;   // empty / GM command -> not NPC speech

        var candidates = new List<(Mob npc, NpcDef def, List<INpcSayHandler> handlers)>();
        foreach (var npc in _world.NpcsNear(_char.Map, _char.X, _char.Y, SpeechRange))
        {
            var def = Content.NpcById(npc.NpcDefId);
            if (def is null) continue;
            var handlers = NpcScripts.For(def).OfType<INpcSayHandler>().ToList();
            if (handlers.Count > 0) candidates.Add((npc, def, handlers));
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
                foreach (var h in handlers)
                    if (await h.OnSay(ctx, speech)) return;   // first NPC to consume the speech ends dispatch
            }
        }
        catch (Exception e) { Log.Info($"!! NPC say error: {e.Message}"); }
    }

    private uint _probeId = 1000;

    // Parse up to 7 whitespace-separated byte values after the command word.
    private static byte[] ParseBytes(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var app = new byte[7];
        for (int i = 1; i < parts.Length && i - 1 < 7; i++) byte.TryParse(parts[i], out app[i - 1]);
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

    // "!row i lo hi [body]": sweep appearance byte [i] from lo..hi across a west->east row of dummies, all
    // other bytes 0. One screenshot then maps that byte's entire id space. Optional 4th arg sets appearance
    // byte [0] (the BODY/sex) for the whole row — default 1 (female, the historically-swept base); pass 0 to
    // sweep the MALE body (its weapon/shield defaults differ from female — male frame 0 was never mapped).
    private void LookRow(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int idx = parts.Length > 1 && int.TryParse(parts[1], out var pi) ? Math.Clamp(pi, 0, 6) : 0;
        int lo = parts.Length > 2 && int.TryParse(parts[2], out var pl) ? pl : 0;
        int hi = parts.Length > 3 && int.TryParse(parts[3], out var ph) ? ph : lo + 7;
        int body = parts.Length > 4 && int.TryParse(parts[4], out var pb) ? pb : 1;
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
