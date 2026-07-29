using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ---- profile window (the "Mind's Eye") ----
    // The client opens the self-profile window when the profile key is pressed by sending 0x2D. Byte 0
    // == 0 is the self-profile request (byte != 0 is group status in 7.x). We reply with 0x39, the
    // self-profile packet (clif_mystaytus): AC/clan/title/class/legend. Without this reply the window
    // never appears — that's the bug the user hit.
    // 0x66 right-click "examine item". Body (live capture): [0]=00 [1]=itemRef(varies per item) [2]=00
    // [3..6]=01 01 01 01 [7..9]=00 00 00. body[1] is the item selector — decode it against the bag/gear by
    // right-clicking KNOWN items and reading this log (does body[1] equal the slot? the icon? the item id?).
    // Once body[1] is understood AND the 0x66 REPLY format (client handler 0x4511b0) is reversed, this can
    // answer with the item-detail popup. Until then it's a decode probe (no reply -> the client stops retrying
    // only after its own timeout, which is harmless).
    private void HandleItemInfoRequest(byte[] dec)
    {
        int sel = dec.Length > 1 ? dec[1] : -1;
        var it = _char.Inventory.FirstOrDefault(i => i.Slot == sel)
              ?? _char.Equipment.FirstOrDefault(e => e.Slot == sel);
        var def = it is null ? null : Content.ItemById(it.ItemId);
        Log.Info($"   -> ITEM-INFO (0x66) sel={sel} (0x{(sel < 0 ? 0 : sel):x2}) body={Log.Hex(dec)}" +
                 (def is null ? "  [no bag/gear item at that slot]" : $"  -> '{def.Name}' #{def.Id}"));
    }

    private void HandleProfileRequest(byte[] dec)
    {
        byte sub = dec.Length > 0 ? dec[0] : (byte)0;
        Log.Info($"   -> PROFILE request (0x2D) sub={sub}");
        if (sub == 0) SendSelfProfile();
    }

    // The F2 key is NOT a menu — it's bound to "Subpath Chat" (RTK rtklua/.../welcomeNmail.lua: "F2 - Turn
    // Subpath Chat On/Off!"). It fires through the SAME 0x43 click-info packet as a real entity click, but
    // with the sentinel id 0xFFFFFFFE instead of a real entity id (RTK clif.c clif_handle_clickgetinfo:
    // `if (RFIFOL(...) == 0xFFFFFFFE) { toggle subpath_chat; sendminitext; return; }`, checked BEFORE the
    // normal map_id2bl lookup). Subpath chat is a server-wide channel to every other player of your same
    // class who also has it toggled on (clif_sendsubpathmessage) — see DoSubpathChat.
    private const uint SubpathChatSentinel = 0xFFFFFFFE;

    // F1 is the adjacent sentinel: RTK map.h `#define F1_NPC 4294967295` (0xFFFFFFFF). Clicking it opens
    // "Central Functions" — a virtual NPC dialog with no physical map presence (RTK clif.c bypasses the
    // usual click proximity check for it: `nd->bl.m == 0` — it exists on every map at once). See
    // RunF1MenuAsync / §11k.
    private const uint F1MenuSentinel = 0xFFFFFFFF;

    // The client clicks an entity to inspect it: 0x43 = 01 entityId(u32BE) 00.
    private void HandleClickInfo(byte[] dec)
    {
        uint id = 0;
        if (dec.Length >= 5) id = (uint)((dec[1] << 24) | (dec[2] << 16) | (dec[3] << 8) | dec[4]);
        Log.Info($"   -> CLICK-INFO (0x43) id={id}");

        if (id == SubpathChatSentinel) { ToggleSubpathChat(); return; }
        if (id == F1MenuSentinel) { OpenF1Menu(); return; }

        // id 0 (or explicitly our own id, e.g. "!click") -> our own public profile.
        if (id == 0 || id == _char.Id) { SendClickProfile(this); return; }

        // An NPC click opens its dialog instead of a profile. NPCs live in the shared mob list (as
        // non-fighting mobs), so MobById finds them; the IsNpc flag distinguishes them from a real creature.
        if (_world.MobById(_char.Map, id) is { IsNpc: true } npc) { OpenNpcDialog(npc); return; }

        // Clicking a real (non-NPC) mob: RTK's own handler (clif.c clif_handle_clickgetinfo, BL_MOB case)
        // runs "onLook", whose player-facing branch is gated on player.gmLevel > 0 -- stock RTK gives
        // regular players nothing back here. We deliberately diverge from that (2026-07-26, user request):
        // right-click-to-walk is client-local pathing we can't intercept (see §11 self-walk note), so the
        // only server-controllable feedback for "what IS that" is this click-info reply -- a name-only
        // mini-text readout, short of the GM-only name/id/level/HP/AC dump onLook does.
        if (_world.MobById(_char.Map, id) is { } mob)
        {
            SendMiniText($"It's {(StartsWithVowel(mob.Name) ? "an" : "a")} {mob.Name}.");
            return;
        }

        // Otherwise, if the id resolves to another connected PLAYER, show THEIR real profile (RTK
        // clif_clickonplayer, same 0x34 opcode, populated from the target's own data via the SendClickProfile
        // overload above). This is the real "view others" window — its Group/Exchange status cells are what
        // the client uses to enable those buttons, which is how a real player actually starts a party/trade
        // (§11l), not a chat command. An id matching nobody at all (stale/disconnected) is a no-op.
        var target = _world.PlayerById(id);
        if (target is not null) SendClickProfile(target);
    }

    // Cheap "a"/"an" article check for mob-name mini-text (HandleClickInfo). Good enough for our mob
    // roster (no silent-letter edge cases like "hour").
    private static bool StartsWithVowel(string s) =>
        s.Length > 0 && "AEIOUaeiou".IndexOf(s[0]) >= 0;

    // F2: flip the subpath-chat toggle and confirm via mini-text (RTK: "Subpath Chat: ON"/"OFF" — same
    // wording, same channel used elsewhere for status confirmations). Persisted so it survives a relog.
    private void ToggleSubpathChat()
    {
        _char.SubpathChat = !_char.SubpathChat;
        SendMiniText($"Subpath Chat: {(_char.SubpathChat ? "ON" : "OFF")}");
        SaveChar();
        Log.Info($"   -> subpath chat {(_char.SubpathChat ? "ON" : "OFF")} for {_char.Name}");
    }

    // "/subpathchat <msg>" (alias "/sp") — RTK clif_sendsubpathmessage: broadcast to every OTHER ONLINE
    // player who shares your class AND has subpath chat toggled on (not map-scoped — this is a server-wide
    // channel, unlike say/shout). Formatted "<@Name> (ClassName) message" per RTK, rendered via the same
    // mini-text channel as whisper/status text.
    private void DoSubpathChat(string msg)
    {
        if (!_char.SubpathChat) { SendMiniText("Turn on Subpath Chat first (F2)."); return; }
        if (!Content.CanTalk(_char.Map)) { SendMiniText("Your voice is swept away by a strange wind."); return; }
        string line = $"<@{_char.Name}> ({_char.ClassName}) {msg}";
        foreach (var p in _world.AllPlayers())
            if (p._char.SubpathChat && string.Equals(p._char.ClassName, _char.ClassName, StringComparison.OrdinalIgnoreCase))
                p.SendMiniText(line);
        Log.Info($"   -> subpath chat: \"{line}\"");
    }

    // ===== NPC dialog =============================================================================
    // Clicking an NPC (0x43) runs its behaviour here. An NPC is a COMPOSITION of reusable abilities
    // (Shop, Bank, Transport, …) declared in NpcScripts; its own definition holds only what's unique to it.
    // The flow is async: a behaviour awaits each prompt and the client's 0x3A reply (HandleNpcDialog)
    // completes that await, so behaviours read as linear code (menu -> branch -> loop) rather than a
    // callback tree — mirroring RTK's coroutine scripts. Everything runs on the read thread (the reply
    // completes the TaskCompletionSource inline), so it never races the session's other state.
    private readonly record struct DialogReply(byte Kind, int Step, int MenuIndex, string Input);
    private TaskCompletionSource<DialogReply>? _dlgReply;   // the prompt currently awaiting a 0x3A reply

    private void OpenNpcDialog(Mob npc)
    {
        var def = Content.NpcById(npc.NpcDefId);
        Log.Info($"   -> NPC dialog: id={npc.Id} '{npc.Name}' def={npc.NpcDefId}");
        if (def is null) { SendScriptMessage(npc.Id, $"{npc.Name}\n\nGreetings, traveller.", NpcPortrait(npc), npc.Color); return; }
        _ = RunNpcAsync(npc, def);   // fire-and-forget: suspends on the first prompt, resumes on the reply
    }

    // Assemble the NPC's top menu from its abilities' entries and dispatch the pick. Identical for every
    // NPC — the abilities carry all the behaviour, so nothing NPC-specific lives here.
    private async Task RunNpcAsync(Mob npc, NpcDef def)
    {
        try
        {
            var ctx = new NpcContext(this, npc, def);

            // Data-driven Lua dialog (data/game-data/npc_dialog.lua): if this NPC identifier has a Lua script,
            // it OWNS the conversation (run it, done). Strictly additive — only NPCs we've authored a script for
            // take this path; every other NPC (and a broken/absent .lua) falls straight through to the C#
            // abilities below, unchanged. Hot-reloads via !reload. See Server/NpcScript.cs.
            if (NpcScript.Has(def.Key)) { await NpcScript.RunAsync(ctx, def.Key); return; }

            var abilities = NpcScripts.For(def);
            var entries = abilities.SelectMany(a => a.Entries(ctx)).ToList();
            if (entries.Count == 0)
            {
                // A speech-only NPC (only INpcSayHandler, no click options) does nothing on click — you
                // interact by speaking to it. Only a truly featureless NPC gives the generic greeting.
                if (!abilities.OfType<INpcSayHandler>().Any()) await ctx.Say("Greetings, traveller.");
                return;
            }

            // One-option NPCs (the tutors Jadespear/Ironheart, a lone-service vendor) skip straight into that
            // service — a "How can I help you today? -> [the only thing]" wrapper menu is pure friction and
            // isn't how RTK scripts behave (they dive into their dialog on click). The picker only appears
            // when there's a real choice to make.
            if (entries.Count == 1) { await entries[0].run(ctx); return; }

            int choice = await ctx.Menu($"{def.Name}: How can I help you today?", entries.Select(e => e.label).ToList());
            if (choice >= 1 && choice <= entries.Count) await entries[choice - 1].run(ctx);
        }
        catch (Exception e) { Log.Info($"!! NPC dialog error ('{npc.Name}'): {e.Message}"); }
    }

    // ===== F1: "Central Functions" menu ===========================================================
    // RTK's f1npc.lua has ~15 entries (GM tools, Kan donations, tutor management, minigame stats, webpage
    // profile settings…) that depend on systems this server doesn't model. Trimmed to what's real here:
    // Silver Thread (shaman resurrection — RTK's actual answer to "how do you get un-ghosted", replacing
    // the old fixed-timer auto-revive) and Choose a Path (the same Peasant-level-5 guild warp §11j's Peasant
    // wall points at, offered as a menu shortcut instead of walking to the physical hall). The old "Toggles"
    // submenu (just the Subpath Chat flip) was removed — that toggle is F2's own binding (ToggleSubpathChat).

    // A virtual "npc" for the F1 dialog wire format — portrait/menu framing only. It's never spawned or
    // looked up; SendNpcMenu/SendScriptMessage just need an id+sprite for the packet header. Sprite 0 ->
    // NpcPortrait renders no portrait icon, matching "this isn't a real character".
    private static readonly Mob F1VirtualNpc = new(F1MenuSentinel, 0, 0, 0, "F1Npc", 1);

    private void OpenF1Menu() => _ = RunF1MenuAsync();

    private async Task RunF1MenuAsync()
    {
        var npc = F1VirtualNpc;
        var opts = new List<string>();
        if (IsDead) opts.Add("Silver Thread");
        if (CharClassId == 0 && _char.Level >= 5) opts.Add("Choose a Path");

        // With nothing to offer (a living, already-classed player) F1 has no function here — say so rather
        // than pop an empty picker. (The Subpath Chat toggle that used to live under "Toggles" is F2's job.)
        if (opts.Count == 0) { await DlgSay(npc, $"Hello {_char.Name}! There is nothing I can do for you right now."); return; }

        int choice = await DlgMenu(npc, $"Hello {_char.Name}! How can I help you today?", opts);
        if (choice < 1 || choice > opts.Count) return;

        switch (opts[choice - 1])
        {
            case "Silver Thread": await SilverThread(npc); break;
            case "Choose a Path": await ChoosePathMenu(npc); break;
        }
    }

    // "Silver Thread": only reachable while dead (matches RTK's own gate — picking it while alive says so
    // and does nothing). Offers a Shaman by nation (RTK's country branches collapse to our two home
    // nations); picking one revives (full heal) at that Shaman's map. See ReviveAt.
    private async Task SilverThread(Mob npc)
    {
        if (!IsDead)
        {
            await DlgSay(npc, "This is for the dead of the land to find a path to the shaman. You are not dead, so you have no path with me.");
            return;
        }

        var shamans = _char.Nation == 2
            ? new (string label, ushort map, ushort x, ushort y)[]
              { ("Felis, to the West of Buya.", 338, 4, 4), ("Storm, to the East of Buya.", 339, 3, 5) }
            : new (string label, ushort map, ushort x, ushort y)[]
              { ("Dusk, to the West of Kugnae.", 8, 6, 4), ("Dawn, to the East of Kugnae.", 9, 3, 5) };

        int choice = await DlgMenu(npc, "Which Shaman would you like to visit?", shamans.Select(s => s.label).ToList());
        if (choice < 1 || choice > shamans.Length) return;
        var s = shamans[choice - 1];
        ReviveAt(s.map, s.x, s.y, "The Shaman calls your spirit home. You awaken anew.");
    }


    // "Choose a Path": warp to the guild-entrance map for the chosen class (per-nation, PathHalls' outer map
    // ids) — a menu shortcut for the same Peasant-level-5 milestone the physical path halls gate on
    // (TryPathHallWarp). Doesn't assign the class itself; a Guildmaster NPC inside does that (NpcAbility's
    // path-choice ability, SetCharClass) — matches RTK's own level5popupDialog, which only warps too.
    private async Task ChoosePathMenu(Mob npc)
    {
        var guilds = _char.Nation == 2
            ? new (string name, ushort map)[] { ("Warrior's Guild", 341), ("Rogue's Guild", 343), ("Mage's Guild", 342), ("Poet's Guild", 344) }
            : new (string name, ushort map)[] { ("Warrior's Guild", 11), ("Rogue's Guild", 15), ("Mage's Guild", 13), ("Poet's Guild", 17) };

        int choice = await DlgMenu(npc, "Please select a guild that you'd like to visit.", guilds.Select(g => g.name).ToList());
        if (choice < 1 || choice > guilds.Length) return;
        var g = guilds[choice - 1];
        if (Content.Maps.TryGetValue(g.map, out var mi)) EnterMap(mi.Id, mi.Xs, mi.Ys, 8, 7, mi.Name);
    }

    // ---- async dialog primitives (used by NpcContext, which abilities call) ---------------------
    // Each sends a 0x30 and awaits the client's 0x3A. A menu returns the 1-based pick (0 = cancelled).
    internal async Task<int> DlgMenu(Mob npc, string prompt, IReadOnlyList<string> options)
    {
        SendNpcMenu(npc, prompt, options);
        var r = await AwaitReply();
        return r.Kind == 0x02 ? r.MenuIndex : 0;
    }

    internal async Task DlgSay(Mob npc, string text)
    {
        // next:true gives the box a "continue" affordance — the click the client answers with a 0x3A that
        // resumes this await. A prev/next-less box has "nothing to do": dismissing it sends no reply and hangs.
        SendScriptMessage(npc.Id, text, NpcPortrait(npc), npc.Color, next: true);
        await AwaitReply();   // hold the script until the player advances the box
    }

    // Free-text input box. Returns the typed string, or null if the player cancelled. The client confirms a
    // real submit with kind 4 + step 2 (RTK clif_parsenpcdialog requires RFIFOB(fd,13)==2); anything else is
    // a cancel/close.
    internal async Task<string?> DlgInput(Mob npc, string prompt)
    {
        SendInputBox(npc, prompt);
        var r = await AwaitReply();
        return r.Kind == 0x04 && r.Step == 0x02 ? r.Input : null;
    }

    private Task<DialogReply> AwaitReply()
    {
        var tcs = new TaskCompletionSource<DialogReply>();
        _dlgReply = tcs;      // a new click orphans any previous pending prompt (it's GC'd, never resumes)
        return tcs.Task;
    }

    // ---- shop ability implementation (Buy / Sell) ----------------------------------------------
    // Looped so the window stays open: pick -> confirm -> back to the list; cancel (0) to leave. Reads as a
    // shop should — the async layer is what makes this straight-line instead of a web of callbacks.
    internal async Task DlgBuy(Mob npc, Shops.Category[]? catalogue)
    {
        var cats = catalogue?.Where(c => c.Keys.Any(k => Content.ItemByKey(k) is not null)).ToList() ?? new();
        if (cats.Count == 0) { await DlgSay(npc, "I've nothing to sell right now."); return; }

        Shops.Category cat;
        if (cats.Count == 1) cat = cats[0];   // flat shop (inn) — no category step
        else
        {
            int ci = await DlgMenu(npc, "What would you like to buy?", cats.Select(c => c.Name).ToList());
            if (ci < 1 || ci > cats.Count) return;
            cat = cats[ci - 1];
        }

        var items = cat.Keys.Select(Content.ItemByKey).OfType<ItemDef>().ToList();
        while (true)
        {
            int ii = await DlgMenu(npc, "What would you like?", items.Select(it => $"{it.Name} - {it.BuyPrice}g").ToList());
            if (ii < 1 || ii > items.Count) return;   // cancelled -> done shopping
            var it = items[ii - 1];
            if (_char.Coins < (uint)it.BuyPrice) { await DlgSay(npc, $"You can't afford {it.Name} ({it.BuyPrice} gold)."); continue; }
            if (!GiveItem(it)) return;                 // pack full — GiveItem already told the player
            _char.Coins -= (uint)it.BuyPrice;
            SendStats();
            MarkDirty();
            Log.Info($"   -> BUY '{it.Name}' -{it.BuyPrice}g (coins now {_char.Coins})");
            await DlgSay(npc, $"You bought {it.Name} for {it.BuyPrice} gold.");
        }
    }

    internal async Task DlgSell(Mob npc)
    {
        while (true)
        {
            var sellable = _char.Inventory.OrderBy(i => i.Slot)
                .Select(inv => (inv, def: Content.ItemById(inv.ItemId)))
                .Where(t => t.def is { NoDrop: false } && t.def.SellPrice > 0)
                .ToList();
            if (sellable.Count == 0) { await DlgSay(npc, "You have nothing I'd buy."); return; }

            int i = await DlgMenu(npc, "What would you like to sell?",
                                  sellable.Select(t => $"{t.def!.Name} - {t.def.SellPrice}g").ToList());
            if (i < 1 || i > sellable.Count) return;
            var (inv, def) = sellable[i - 1];
            _char.Coins += (uint)def!.SellPrice;
            if (--inv.Amount <= 0) { _char.Inventory.Remove(inv); SendDelItem((byte)inv.Slot, 0); }   // reason 0 = Remove (sold, not dropped)
            else SendAddItem(inv);
            SendStats();
            MarkDirty();
            await DlgSay(npc, $"You sold {def.Name} for {def.SellPrice} gold.");
        }
    }

    // ---- parcels (MessengerAbility; RTK messenger.lua "Send Parcel"/"Receive Parcel" -> Parcel.lua) ----
    // Parcels are item/gold sent player-to-player, queued at the messenger for pickup — SEPARATE from n-mail
    // (see Parcel.cs). Both flows are ordinary 0x30 dialogs (menu/input/say), so unlike the mail compose
    // window there's nothing client-side to reverse: this is fully server-driven.

    /// <summary>Does the player have any parcel waiting (gates the messenger's "Receive Parcel" entry)?</summary>
    internal bool HasWaitingParcels => Parcel.HasAny(_char.Name);

    // RTK sendParcelTo: choose Gold or Item, name a recipient (offline OK — resolved against the char store),
    // pay a seal, and it's queued at the messenger. Items must be tradeable (not NoDrop), non-food (Type 0),
    // and fully repaired; a 5% seal fee (RTK item.price*.05) is charged per item parcel. Gold parcels are
    // free to send, matching RTK. Coin/possession are RE-checked after each async prompt before committing.
    internal async Task ParcelSendFlow(Mob npc)
    {
        int kind = await DlgMenu(npc, "What would you like to send?", new[] { "Gold", "Item" });
        if (kind < 1) return;

        if (kind == 1)   // ---- gold ----
        {
            var amtStr = await DlgInput(npc, "How much gold would you like to send?");
            if (amtStr is null) return;
            if (!int.TryParse(amtStr.Trim(), out int gold) || gold <= 0) { await DlgSay(npc, "That's not a valid amount."); return; }
            if (_char.Coins < (uint)gold) { await DlgSay(npc, "You don't have that much gold."); return; }

            var to = await DlgInput(npc, $"Who do you want to send this {gold:N0} gold to?");
            if (to is null) return;
            var recip = ResolveParcelRecipient(to.Trim());
            if (recip is null) { await DlgSay(npc, "Character does not exist."); return; }
            if (recip.Equals(_char.Name, StringComparison.OrdinalIgnoreCase)) { await DlgSay(npc, "You can't send a parcel to yourself."); return; }
            if (_char.Coins < (uint)gold) { await DlgSay(npc, "You don't have that much gold."); return; }

            _char.Coins -= (uint)gold;
            var g = DateTime.UtcNow;
            Parcel.Send(recip, _char.Name, -1, gold, 0, "", (byte)g.Month, (byte)g.Day);
            SendStats(); MarkDirty();
            NotifyParcelRecipient(recip);
            await DlgSay(npc, $"Your {gold:N0} gold has been sent in a parcel to {recip}.");
            return;
        }

        // ---- item ----
        var sendable = _char.Inventory.OrderBy(i => i.Slot)
            .Select(inv => (inv, def: Content.ItemById(inv.ItemId)))
            .Where(t => t.def is not null && t.def.Type != 0 && !t.def.NoDrop)   // no food (Type 0 EAT), no bound/no-drop
            .ToList();
        if (sendable.Count == 0) { await DlgSay(npc, "You have nothing you could send."); return; }

        int pick = await DlgMenu(npc, "What would you like to send?", sendable.Select(t => t.def!.Name).ToList());
        if (pick < 1 || pick > sendable.Count) return;
        var (item, def) = sendable[pick - 1];

        int amount = 1;
        if (def!.Stackable && item.Amount > 1)
        {
            var aStr = await DlgInput(npc, $"How many {def.Name} do you want to send?");
            if (aStr is null) return;
            if (!int.TryParse(aStr.Trim(), out amount) || amount <= 0) { await DlgSay(npc, "That's not a valid amount."); return; }
            amount = Math.Min(amount, item.Amount);
        }

        if (item.Dura != def.Durability) { await DlgSay(npc, "Item must be in perfect condition to send. Go and repair it first!"); return; }

        var to2 = await DlgInput(npc, $"Who do you want to send this {amount} {def.Name} to?");
        if (to2 is null) return;
        var recip2 = ResolveParcelRecipient(to2.Trim());
        if (recip2 is null) { await DlgSay(npc, "Character does not exist."); return; }
        if (recip2.Equals(_char.Name, StringComparison.OrdinalIgnoreCase)) { await DlgSay(npc, "You can't send a parcel to yourself."); return; }

        int fee = Math.Max(1, (int)Math.Ceiling((def.BuyPrice > 0 ? def.BuyPrice : def.SellPrice) * 0.05 * amount));
        if (_char.Coins < (uint)fee) { await DlgSay(npc, $"I need {fee:N0} gold for the seal. Come back when you can afford it."); return; }

        // Re-verify possession AFTER the async prompts, then remove the stack and charge the seal.
        if (!RemoveInventoryStack(item, amount)) { await DlgSay(npc, $"You no longer have {amount} {def.Name}."); return; }
        _char.Coins -= (uint)fee;
        var d = DateTime.UtcNow;
        Parcel.Send(recip2, _char.Name, def.Id, amount, item.Dura, item.CustomName, (byte)d.Month, (byte)d.Day);
        SendStats(); MarkDirty();
        NotifyParcelRecipient(recip2);
        await DlgSay(npc, "Your parcel has been sent.");
    }

    // RTK receiveParcelFrom: hand over the oldest waiting parcel — gold to the purse, an item to the bag (or
    // dropped at the player's feet if the pack is full, the same recovery as reading a mail attachment). Loops
    // one parcel at a time while more remain and the player wants to keep collecting.
    internal async Task ParcelReceiveFlow(Mob npc)
    {
        while (true)
        {
            var list = Parcel.ListFor(_char.Name);
            if (list.Count == 0) { await DlgSay(npc, "You have no parcels waiting."); return; }

            var p = list[0];                                   // FIFO by position
            var got = Parcel.Claim(_char.Name, p.Position);     // atomic remove-and-return (guards double-claim)
            if (got is null) continue;                          // already taken by another path — re-list

            if (got.IsGold)
            {
                _char.Coins += (uint)got.Amount;
                SendStats(); MarkDirty();
                await DlgSay(npc, $"You receive {got.Amount:N0} gold from {got.Sender}.");
            }
            else
            {
                var def = Content.ItemById(got.ItemId);
                if (def is null) { await DlgSay(npc, "One of your parcels held something I no longer recognize; I've discarded it."); }
                else
                {
                    bool gotIt = GiveItem(def, got.Amount, (ushort)Math.Max(0, got.Dura), got.Engrave);
                    if (!gotIt)
                        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
                            X = _char.X, Y = _char.Y, Amount = got.Amount, Dura = (ushort)Math.Max(0, got.Dura), Graphic = def.Icon });
                    SendStats(); MarkDirty();
                    await DlgSay(npc, gotIt
                        ? $"You receive a parcel from {got.Sender}: {def.Name} x{got.Amount}."
                        : $"A parcel from {got.Sender} held {def.Name} x{got.Amount}, but your pack was full — it's at your feet.");
                }
            }

            RefreshMailFlags();   // claiming a parcel may clear the HUD bag flag (SendStats above sent the stale cache)
            if (!Parcel.HasAny(_char.Name)) return;
            int more = await DlgMenu(npc, "You have more parcels waiting. Collect another?", new[] { "Yes", "No" });
            if (more != 1) return;
        }
    }

    /// <summary>Resolve a typed recipient name to a deliverable one: an online player, else an existing stored
    /// character (offline delivery, like mail). Null if nobody by that name exists. The table is COLLATE
    /// NOCASE so the exact casing stored here doesn't affect later lookups.</summary>
    private string? ResolveParcelRecipient(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_world.FindPlayer(name) is not null) return name;
        return _store.Exists(name) ? name : null;
    }

    /// <summary>Remove <paramref name="amount"/> from a bag stack and update the client (whole stack removed
    /// with reason 4 = "taken to parcel"). False without change if the stack is gone or too small — the
    /// possession re-check after the async send prompts.</summary>
    private bool RemoveInventoryStack(InvItem inv, int amount)
    {
        if (!_char.Inventory.Contains(inv) || inv.Amount < amount) return false;
        inv.Amount -= amount;
        if (inv.Amount <= 0) { _char.Inventory.Remove(inv); SendDelItem((byte)inv.Slot, 4); }
        else SendAddItem(inv);
        MarkDirty();
        return true;
    }

    /// <summary>Light an online recipient's HUD bag icon immediately and tell them a parcel arrived (RTK
    /// msg(12, "[PARCEL]: You got a parcel from X!")). No-op if they're offline — they'll see the icon on
    /// their next login, driven by MailParcelFlags.</summary>
    private void NotifyParcelRecipient(string name)
    {
        var p = _world.FindPlayer(name);
        if (p is null) return;
        p.RefreshMailFlags();   // recompute + push the recipient's bag flag (SendStats alone would send the stale cache)
        p.SendMiniText($"[PARCEL]: You got a parcel from {_char.Name}!");
    }

    // ---- spoken shop shortcut ("buy [my] [all|N] <item>") — see ShopAbility.OnSay ----------------
    // Spoken "buy [all|N] <item>": sell up to `amount` (whole stack if <= 0) of a fuzzy-matched
    // item, by name, from the bag. Tries the plural form as typed, then singularized (item names in the
    // registry are singular, e.g. "acorn", while the spoken word is often plural, "acorns"). Returns false
    // (not a dialog line) when nothing matches, so unrelated speech still falls through to normal chat.
    internal async Task<bool> SellItemToNpcByName(Mob npc, string name, int amount)
    {
        var def = Content.FindItem(name) ?? Content.FindItem(Singularize(name));
        if (def is null || def.SellPrice <= 0 || def.NoDrop) return false;

        var stack = _char.Inventory.Where(i => i.ItemId == def.Id).OrderBy(i => i.Slot).ToList();
        if (stack.Count == 0) { NpcBubble(npc, $"You don't have any {def.Name} to sell."); return true; }

        int remaining = amount > 0 ? amount : stack.Sum(i => i.Amount);
        int sold = 0; uint earned = 0;
        foreach (var inv in stack)
        {
            if (remaining <= 0) break;
            int take = Math.Min(remaining, inv.Amount);
            earned += (uint)def.SellPrice * (uint)take;
            sold += take;
            remaining -= take;
            inv.Amount -= take;
            if (inv.Amount <= 0) { _char.Inventory.Remove(inv); SendDelItem((byte)inv.Slot, 0); }   // reason 0 = Remove (sold, not dropped)
            else SendAddItem(inv);
        }
        _char.Coins += earned;
        SendStats();
        MarkDirty();
        NpcBubble(npc, $"You sold {sold} {def.Name} for {earned} gold.");
        return true;
    }

    private static string Singularize(string s) => s.Length > 1 && s.EndsWith('s') ? s[..^1] : s;

    // ---- bank ability implementation (vault: coin + item storage) ------------------------------
    // Looped like the shop: each action returns to the vault menu until the player cancels. Storage lives on
    // the Character (BankMoney / BankItems) and persists via the JSON store. Joint/shared accounts (RTK's
    // multi-owner vaults) are intentionally out of scope for a single-owner vault.
    internal async Task DlgBank(Mob npc)
    {
        while (true)
        {
            var opts = new List<string> { "Deposit Item", "Withdraw Item" };
            if (_char.Coins > 0)     opts.Add("Deposit Money");
            if (_char.BankMoney > 0) opts.Add("Withdraw Money");

            int c = await DlgMenu(npc, $"Your vault holds {_char.BankMoney} coins. What would you like to do?", opts);
            if (c < 1 || c > opts.Count) return;
            switch (opts[c - 1])
            {
                case "Deposit Item":   await BankDepositItem(npc);   break;
                case "Withdraw Item":  await BankWithdrawItem(npc);  break;
                case "Deposit Money":  await BankDepositMoney(npc);  break;
                case "Withdraw Money": await BankWithdrawMoney(npc); break;
            }
        }
    }

    private async Task BankDepositMoney(Mob npc)
    {
        var s = await DlgInput(npc, $"You carry {_char.Coins} coins. How much will you deposit?");
        if (s is null) return;   // cancelled
        long amt = Math.Min(Math.Min(ParseAmount(s), _char.Coins), Content.BankMax - _char.BankMoney);
        if (amt <= 0) { await DlgSay(npc, "You deposit nothing."); return; }
        _char.Coins -= (uint)amt;
        _char.BankMoney += (uint)amt;
        SendStats();
        MarkDirty();
        await DlgSay(npc, $"You deposit {amt} coins. Your vault now holds {_char.BankMoney}.");
    }

    private static bool IsCoinWord(string s) => s.Equals("coin", StringComparison.OrdinalIgnoreCase) || s.Equals("coins", StringComparison.OrdinalIgnoreCase);

    // Spoken "take my <item|coin> [count]" (BankAbility.OnSay) — deposits `amount` (whole stack if <= 0) of a
    // fuzzy-matched item, or coin if the word is "coin"/"coins", straight into the vault, no menu round trip.
    internal async Task<bool> DepositItemToBank(Mob npc, string name, int amount)
    {
        if (IsCoinWord(name))
        {
            long amt = Math.Min(Math.Min(amount > 0 ? amount : _char.Coins, _char.Coins), Content.BankMax - _char.BankMoney);
            if (amt <= 0) { NpcBubble(npc, "You deposit nothing."); return true; }
            _char.Coins -= (uint)amt;
            _char.BankMoney += (uint)amt;
            SendStats();
            MarkDirty();
            NpcBubble(npc, $"You deposit {amt} coins. Your vault now holds {_char.BankMoney}.");
            return true;
        }

        var def = Content.FindItem(name) ?? Content.FindItem(Singularize(name));
        if (def is null) return false;

        var stack = _char.Inventory.Where(i => i.ItemId == def.Id).OrderBy(i => i.Slot).ToList();
        if (stack.Count == 0) { NpcBubble(npc, $"You don't have any {def.Name} to store."); return true; }

        int remaining = amount > 0 ? amount : stack.Sum(i => i.Amount);
        int moved = 0;
        foreach (var inv in stack)
        {
            if (remaining <= 0) break;
            int take = Math.Min(remaining, inv.Amount);
            moved += take;
            remaining -= take;
            if (take >= inv.Amount) { _char.Inventory.Remove(inv); SendDelItem((byte)inv.Slot, 0); _char.BankItems.Add(inv); }   // reason 0 = Remove (stored, not dropped)
            else { inv.Amount -= take; SendAddItem(inv); _char.BankItems.Add(new InvItem(0, def.Id, take, inv.Dura)); }
        }
        SaveChar();
        NpcBubble(npc, $"You store {moved} {def.Name} in your vault.");
        return true;
    }

    // Spoken "give my <item|coin> [count]" — the withdraw mirror of the above.
    internal async Task<bool> WithdrawItemFromBank(Mob npc, string name, int amount)
    {
        if (IsCoinWord(name))
        {
            long amt = Math.Min(amount > 0 ? amount : _char.BankMoney, _char.BankMoney);
            if (amt <= 0) { NpcBubble(npc, "You withdraw nothing."); return true; }
            _char.BankMoney -= (uint)amt;
            _char.Coins += (uint)amt;
            SendStats();
            MarkDirty();
            NpcBubble(npc, $"Here are your {amt} coins. Your vault now holds {_char.BankMoney}.");
            return true;
        }

        var def = Content.FindItem(name) ?? Content.FindItem(Singularize(name));
        if (def is null) return false;

        var stack = _char.BankItems.Where(i => i.ItemId == def.Id).ToList();
        if (stack.Count == 0) { NpcBubble(npc, $"Your vault has no {def.Name}."); return true; }

        int remaining = amount > 0 ? amount : stack.Sum(i => i.Amount);
        int moved = 0;
        foreach (var bi in stack)
        {
            if (remaining <= 0) break;
            int slot = FreeSlot();
            if (slot < 0) { if (moved == 0) NpcBubble(npc, "Your pack is full."); break; }
            int take = Math.Min(remaining, bi.Amount);
            moved += take;
            remaining -= take;
            if (take >= bi.Amount) { _char.BankItems.Remove(bi); bi.Slot = (byte)slot; _char.Inventory.Add(bi); SendAddItem(bi); }
            else { bi.Amount -= take; var give = new InvItem((byte)slot, def.Id, take, bi.Dura); _char.Inventory.Add(give); SendAddItem(give); }
        }
        SaveChar();
        if (moved > 0) NpcBubble(npc, $"You withdraw {moved} {def.Name} from your vault.");
        return true;
    }

    private async Task BankWithdrawMoney(Mob npc)
    {
        var s = await DlgInput(npc, $"Your vault holds {_char.BankMoney} coins. How much will you withdraw?");
        if (s is null) return;
        long amt = Math.Min(ParseAmount(s), _char.BankMoney);
        if (amt <= 0) { await DlgSay(npc, "You withdraw nothing."); return; }
        _char.BankMoney -= (uint)amt;
        _char.Coins += (uint)amt;
        SendStats();
        MarkDirty();
        await DlgSay(npc, $"Here are your {amt} coins. Your vault now holds {_char.BankMoney}.");
    }

    private async Task BankDepositItem(Mob npc)
    {
        var items = _char.Inventory.OrderBy(i => i.Slot)
            .Select(inv => (inv, def: Content.ItemById(inv.ItemId)))
            .Where(t => t.def is not null)
            .ToList();
        if (items.Count == 0) { await DlgSay(npc, "You have nothing to store."); return; }

        int i = await DlgMenu(npc, "Which item will you store?",
                              items.Select(t => t.inv.Amount > 1 ? $"{t.def!.Name} ({t.inv.Amount})" : t.def!.Name).ToList());
        if (i < 1 || i > items.Count) return;
        var (inv, def) = items[i - 1];
        _char.Inventory.Remove(inv);
        SendDelItem((byte)inv.Slot, 0);         // reason 0 = Remove (stored, not dropped)
        _char.BankItems.Add(inv);               // whole stack goes to the vault
        MarkDirty();
        await DlgSay(npc, $"You store {def!.Name} in your vault.");
    }

    private async Task BankWithdrawItem(Mob npc)
    {
        var stored = _char.BankItems
            .Select(bi => (bi, def: Content.ItemById(bi.ItemId)))
            .Where(t => t.def is not null)
            .ToList();
        if (stored.Count == 0) { await DlgSay(npc, "Your vault is empty."); return; }

        int i = await DlgMenu(npc, "Which item will you withdraw?",
                              stored.Select(t => t.bi.Amount > 1 ? $"{t.def!.Name} ({t.bi.Amount})" : t.def!.Name).ToList());
        if (i < 1 || i > stored.Count) return;
        var (bi, def) = stored[i - 1];
        int slot = FreeSlot();
        if (slot < 0) { await DlgSay(npc, "Your pack is full."); return; }
        _char.BankItems.Remove(bi);
        bi.Slot = (byte)slot;                   // assign a fresh bag slot (vault slots are meaningless)
        _char.Inventory.Add(bi);
        SendAddItem(bi);
        MarkDirty();
        await DlgSay(npc, $"You withdraw {def!.Name} from your vault.");
    }

    // Digits-only amount parse (mirrors RTK inputNumberCheck), capped so it can't overflow the coin math.
    private static long ParseAmount(string? s)
    {
        long v = 0;
        if (s is not null)
            foreach (char ch in s)
                if (char.IsDigit(ch)) { v = v * 10 + (ch - '0'); if (v > Content.BankMax) return Content.BankMax; }
        return v;
    }

    // Portrait = the NPC's creature sprite drawn from Monster.epf — the SAME 0x8000|look encoding the on-map
    // spawn uses (RTK clif.c:3190 sends the NPC graphic as look+32768). The dialog's kind-1 "npc gfx" range
    // is exactly [32768, 49151], so an encoded creature look lands there; a look of 0 -> no portrait.
    private static ushort NpcPortrait(Mob npc) => npc.Sprite == 0 ? (ushort)0 : (ushort)(0x8000 | npc.Sprite);

    // 0x30 clif_scriptmenuseq (type-0, graphic head): a text prompt + picker buttons. Same frame mapping as
    // SendScriptMessage (RTK WFIFO(fd,N) -> body[N-5]); the menu differs only in the kind bytes
    // (body[0..1] = 02 02, RTK WFIFOB(5)=WFIFOB(6)=2) and the item list appended after the prompt:
    //   body[23+L] = item count (u8), then each item = len(u8) + ASCII text, contiguous.
    private void SendNpcMenu(Mob npc, string prompt, IReadOnlyList<string> options)
    {
        ushort gfx = NpcPortrait(npc);
        byte head = gfx == 0 ? (byte)0 : gfx >= 49152 ? (byte)2 : (byte)1;
        byte color = npc.Color;
        var pr = Encoding.ASCII.GetBytes(prompt);

        var d = new List<byte>();
        d.Add(0x02); d.Add(0x02);          // [0..1] kind = menu (RTK WFIFOB(5)=2, WFIFOB(6)=2)
        d.AddRange(Be32(npc.Id));          // [2..5] npc entity id
        d.Add(head);                       // [6]   head kind
        d.Add(1);                          // [7]
        d.AddRange(Be(gfx));               // [8..9] portrait graphic
        d.Add(color);                      // [10]  portrait palette
        d.Add(1);                          // [11]
        d.AddRange(Be(gfx));               // [12..13]
        d.Add(color);                      // [14]
        d.AddRange(Be32(1));               // [15..18]
        d.Add(0);                          // [19] prev button
        d.Add(0);                          // [20] next button
        d.AddRange(Be((ushort)pr.Length)); // [21..22] prompt length
        d.AddRange(pr);                    // [23..] prompt text
        d.Add((byte)options.Count);        // [23+L] menu item count
        foreach (var label in options)
        {
            var ob = Encoding.ASCII.GetBytes(label);
            d.Add((byte)ob.Length);
            d.AddRange(ob);
        }
        SendMap(0x30, _gameInc++, d.ToArray(), $"npc-menu(0x30) id={npc.Id} x{options.Count}");
    }

    // 0x30 clif_inputseq (type-0, graphic head): a free-text entry box. Same head as the menu; kind bytes
    // are 04 04 (RTK WFIFOB(5)=WFIFOB(6)=4). After the prompt come RTK's secondary lines we don't use:
    //   [+1] dialog2 len(=0)   [+1] '*' separator(42)   [+1] dialog3 len(=0)   [+2] trailing (0,0).
    // The client returns the text via 0x3A kind 4 (HandleNpcDialog -> DlgInput).
    private void SendInputBox(Mob npc, string prompt)
    {
        ushort gfx = NpcPortrait(npc);
        byte head = gfx == 0 ? (byte)0 : gfx >= 49152 ? (byte)2 : (byte)1;
        byte color = npc.Color;
        var pr = Encoding.ASCII.GetBytes(prompt);

        var d = new List<byte>();
        d.Add(0x04); d.Add(0x04);          // [0..1] kind = input (RTK WFIFOB(5)=WFIFOB(6)=4)
        d.AddRange(Be32(npc.Id));          // [2..5] npc entity id
        d.Add(head);                       // [6]   head kind
        d.Add(1);                          // [7]
        d.AddRange(Be(gfx));               // [8..9] portrait graphic
        d.Add(color);                      // [10]  portrait palette
        d.Add(1);                          // [11]
        d.AddRange(Be(gfx));               // [12..13]
        d.Add(color);                      // [14]
        d.AddRange(Be32(1));               // [15..18]
        d.Add(0);                          // [19] prev button
        d.Add(0);                          // [20] next button
        d.AddRange(Be((ushort)pr.Length)); // [21..22] prompt length
        d.AddRange(pr);                    // [23..] prompt text
        d.Add(0);                          // dialog2 length (unused)
        d.Add(42);                         // '*' separator
        d.Add(0);                          // dialog3 length (unused)
        d.Add(0); d.Add(0);                // trailing pad (RTK advances len by +3 past dialog3)
        SendMap(0x30, _gameInc++, d.ToArray(), $"npc-input(0x30) id={npc.Id}");
    }

    // 0x30 clif_scriptmes (type-0, graphic head): a plain NPC text box. Ported from RTK clif.c; the RTK
    // WFIFO(fd,N) offsets map to this server's body[N-5] (frame = AA len len opcode inc, body at wire+5).
    //   body[0..1] u16=1   [2..5] npc id(u32BE)   [6] head kind(0 none/1 npc gfx/2 item gfx)   [7]=1
    //   [8..9] gfx(u16BE)   [10] color   [11]=1   [12..13] gfx   [14] color   [15..18] u32=1
    //   [19] prev-button    [20] next-button   [21..22] msg len(u16BE)   [23..] msg (ASCII)
    // prev/next 0 => a single OK/close box; the client answers a close with 0x3A kind 1 (HandleNpcDialog).
    private void SendScriptMessage(uint npcId, string msg, ushort gfx, byte color,
                                   bool prev = false, bool next = false)
    {
        // Head kind, classified from the graphic id exactly as RTK does (clif_scriptmes): 0 -> none,
        // >=49152 -> item gfx (kind 2), else -> npc/creature gfx (kind 1).
        byte head = gfx == 0 ? (byte)0 : gfx >= 49152 ? (byte)2 : (byte)1;
        SendScriptMessageP(npcId, msg, new DialogPortrait(head, gfx, color), prev, next);
    }

    // A dialog portrait: the head-kind byte (0 none / 1 creature-look / 2 item-icon) plus the graphic id and
    // palette carried in the 0x30 head. The client reads head kind from the byte directly, so — unlike the
    // range-derived helper above — this lets a script pick an item-icon portrait (kind 2) whose small Item.epf
    // frame would otherwise be misread as a creature look. RTK: convertGraphic(look,"monster") = 0x8000|look.
    private readonly record struct DialogPortrait(byte Head, ushort Gfx, byte Color)
    {
        public static readonly DialogPortrait None = new(0, 0, 0);
        public static DialogPortrait Npc(Mob npc)  => npc.Sprite == 0 ? None : new(1, (ushort)(0x8000 | npc.Sprite), npc.Color);
        public static DialogPortrait Look(int look, int color) => look <= 0 ? None : new(1, (ushort)(0x8000 | look), (byte)color);
        public static DialogPortrait Item(ItemDef d) => new(2, d.Icon, d.IconColor);
    }

    // Core 0x30 text-box sender with an EXPLICIT portrait (head kind not re-derived). Same frame as
    // SendScriptMessage; only the head bytes carry the caller's portrait.
    private void SendScriptMessageP(uint npcId, string msg, DialogPortrait p, bool prev, bool next)
    {
        var m = Encoding.ASCII.GetBytes(msg);
        var d = new List<byte>();
        d.AddRange(Be(1));                 // [0..1] type/count = 1
        d.AddRange(Be32(npcId));           // [2..5] npc entity id
        d.Add(p.Head);                     // [6]   head kind
        d.Add(1);                          // [7]
        d.AddRange(Be(p.Gfx));             // [8..9] portrait graphic
        d.Add(p.Color);                    // [10]  portrait palette
        d.Add(1);                          // [11]
        d.AddRange(Be(p.Gfx));             // [12..13]
        d.Add(p.Color);                    // [14]
        d.AddRange(Be32(1));               // [15..18]
        d.Add((byte)(prev ? 1 : 0));       // [19] prev button
        d.Add((byte)(next ? 1 : 0));       // [20] next button
        d.AddRange(Be((ushort)m.Length));  // [21..22] message length
        d.AddRange(m);                     // [23..] message text
        SendMap(0x30, _gameInc++, d.ToArray(), $"npc-dialog(0x30) id={npcId} {m.Length}B head={p.Head}");
    }

    // ---- multi-page dialog (RTK dialogSeq): one portrait, N text pages the player clicks through. Non-final
    // pages show the "next" affordance; the last is a plain close. Each page awaits the client's 0x3A so the
    // whole sequence reads as linear script. The three public wrappers pick the portrait (NPC / creature / item).
    private async Task DlgSeq(Mob npc, DialogPortrait p, IReadOnlyList<string> pages)
    {
        if (pages.Count == 0) return;
        // Every page carries the "next" affordance (next:true) — the click the client answers with a 0x3A that
        // resumes the await and drives the next page. A button-less box (prev/next both off) can't be advanced:
        // dismissing it sends no reply, so the sequence hangs on page one. RTK drives multi-page dialog the same
        // way (moreFlag -> the next arrow). The last page's "next" click simply ends the sequence.
        foreach (var page in pages)
        {
            SendScriptMessageP(npc.Id, page, p, prev: false, next: true);
            await AwaitReply();
        }
    }
    internal Task DlgSayNpc(Mob npc, IReadOnlyList<string> pages)  => DlgSeq(npc, DialogPortrait.Npc(npc), pages);
    internal Task DlgSayLook(Mob npc, int look, int color, IReadOnlyList<string> pages) => DlgSeq(npc, DialogPortrait.Look(look, color), pages);
    internal Task DlgSayItem(Mob npc, string itemKey, IReadOnlyList<string> pages)
    {
        var def = Content.ItemByKey(itemKey);
        return DlgSeq(npc, def is null ? DialogPortrait.Npc(npc) : DialogPortrait.Item(def), pages);
    }

    // 0x3A = the client's reply to a 0x30 we sent (RTK clif_parsenpcdialog). body[0]=kind (01 text/close,
    // 02 menu pick, 04 input), [8]=step, [10]=menu index (1-based) or input length, [11..]=input text. We
    // just complete the prompt that's awaiting a reply; the suspended behaviour resumes and drives what's
    // next (nested menu, purchase, loop back). No routing table here — the await IS the continuation.
    private void HandleNpcDialog(byte[] dec)
    {
        byte kind = dec.Length > 0 ? dec[0] : (byte)0;
        int step = dec.Length > 8 ? dec[8] : 0;
        int menuOrLen = dec.Length > 10 ? dec[10] : 0;
        string input = "";
        if (kind == 0x04 && dec.Length > 11)   // input box returned text
        {
            int n = Math.Min(menuOrLen, dec.Length - 11);
            if (n > 0) input = Encoding.ASCII.GetString(dec, 11, n);
        }
        Log.Info($"   -> NPC-DIALOG (0x3A) kind={kind} step={step} menu/len={menuOrLen}" +
                 (input.Length > 0 ? $" input='{input}'" : ""));

        var tcs = _dlgReply;
        _dlgReply = null;
        tcs?.TrySetResult(new DialogReply(kind, step, menuOrLen, input));
    }

    // The client sends 0x4F when the player saves their profile from the edit box. Body (matches the
    // client's own change-profile parse): [picSize u16BE][picSize bytes][blurbLen u8][blurb bytes][00].
    // We persist both so a later click (0x34) shows the player's own words + drawing.
    private void HandleChangeProfile(byte[] dec)
    {
        if (dec.Length < 3) return;
        int picLen = (dec[0] << 8) | dec[1];
        int off = 2;
        if (picLen > 0 && off + picLen <= dec.Length)
        {
            _char.ProfilePic = dec[off..(off + picLen)];
            off += picLen;
        }
        else
        {
            _char.ProfilePic = null;
        }

        if (off < dec.Length)
        {
            int tlen = dec[off++];
            if (tlen >= 0 && off + tlen <= dec.Length)
                _char.ProfileText = Encoding.ASCII.GetString(dec, off, tlen);
        }

        if (_enteredWorld) _store.Save(_char);
        Log.Info($"   -> CHANGE-PROFILE (0x4F) saved: pic={_char.ProfilePic?.Length ?? 0}B text=\"{_char.ProfileText}\"");
        SendMessage("Your profile has been saved.");
    }

    // 0x39 self-profile ("Mind's Eye"). Layout decoded from the 7.x clif_mystaytus builder and confirmed
    // against a real 6.x capture (jeedee/TkServer) that decrypts to this exact shape (AC=99, class
    // "Peasant", legend "Born in Hyul 31, Winter"). Body:
    //   [AC u8][dam u8][hit u8]
    //   [clan  : len u8 + bytes]        (len 0 = clanless)
    //   [clanTitle : len u8 + bytes]
    //   [title : len u8 + bytes]
    //   [spouse : len u8 + bytes]
    //   [group u8]  [TNL u32BE]
    //   [className : len u8 + bytes]
    //   14 × equip slot (each 10 bytes, all zero = empty)
    //   [exchange u8]
    //   [0 u8] [legendCount u16BE]
    //   legendCount × { icon u8, color u8, textLen u8, text bytes }
    //
    // WIRE FORMAT (reverse-engineered from the client parser at 0x4732a0 — the mode-0 widget picked by the
    // shared profile dispatcher 0x424820; the mode-1/other-view widget 0x48b6a0 is a DIFFERENT, larger layout):
    //   [AC u8][dam u8][hit u8]
    //   [clan str][clanTitle str][title str][spouse str]       (each: u8 len + bytes)
    //   [group u8][TNL u32BE][className str]
    //   [g0 u16BE][g1 u16BE][g2 u16BE]                         (three portrait/graphic ids — see below)
    //   [box str]                                              (multi-line box; client maps TAB->CR)
    //   [flag u8]
    //   [legendCount u8]  then legendCount × { icon u8, color u8, len u8, text }
    // CRITICAL: 4.95 has NO packed equipment-icon array and the legend count is a single u8. The old code
    // sent a 6.x/RTK-shaped 14-cell/113-byte equip region (that fork has more item slots — hence the bigger
    // block); on 4.95 it pushed the legend count into the padding (count read as 0 -> no legends) and spilled
    // icons into the wrong fields (gear rendered in the wrong paperdoll slots). Proven by decoding a real 6.x
    // capture with this exact grammar: it aligns perfectly up to the legend count, then the 6.x equip block
    // remains unconsumed. The self paperdoll BODY is drawn from the live on-map character sprite, not this
    // packet, so g0/g1/g2 = 0 (default) exactly matches the known-good capture.
    private void SendSelfProfile()
    {
        var eq = Totals();                    // fold worn-gear bonuses + active buffs into the displayed AC/dam/hit
        var d = new List<byte>();
        d.Add((byte)(sbyte)Math.Clamp(_char.Ac - eq.armor, -128, 127));   // AC: lower is better, armor subtracts
        d.Add((byte)Math.Clamp(_char.Dam + eq.dam, 0, 255));
        d.Add((byte)Math.Clamp(_char.Hit + eq.hit, 0, 255));
        AddLenStr(d, _char.ClanName);
        AddLenStr(d, _char.ClanTitle);
        AddLenStr(d, _char.Title);
        AddLenStr(d, _char.Spouse);
        d.Add((byte)(_char.Grouped ? 1 : 0));   // group/sociable flag (Shift+G)
        d.AddRange(Be32(_char.Tnl));    // experience to next level
        AddLenStr(d, _char.ClassName);

        // The three equipment ICON cells beside the doll: helm, left ring, right ring. These slots have no
        // character-sprite layer in 4.95, so the profile shows them as ground-icon boxes fed by these u16s.
        d.AddRange(Be(ProfileCellIcon(4)));   // helm  (wire slot 4)
        d.AddRange(Be(ProfileCellIcon(7)));   // left ring  (wire slot 7)
        d.AddRange(Be(ProfileCellIcon(8)));   // right ring (wire slot 8)

        // The multi-line text BOX under the character. The client converts TAB(0x09)->CR(0x0d), so tab-separated
        // entries become separate lines. This is the self-view's buff/effect box (issue #6): active buff/debuff
        // names + remaining seconds. Empty when nothing is active. (The other-view 0x34 puts the GEAR list here
        // instead; self-view = buffs, other-view = gear, exactly as requested.)
        AddLenStr(d, BuffBoxText());
        d.Add((byte)(_char.Exchange ? 1 : 0));   // trailing flag = exchange/trade status (client field +0x935)

        var legs = _char.Legends ?? new List<Legend>();
        d.Add((byte)Math.Min(legs.Count, 255));   // legend count is a single u8 in 4.95 (NOT u16)
        foreach (var lg in legs)
        {
            var t = Encoding.ASCII.GetBytes(lg.Text ?? "");
            if (t.Length > 255) t = t[..255];
            d.Add(lg.Icon);
            d.Add(lg.Color);
            d.Add((byte)t.Length);
            d.AddRange(t);
        }

        SendMap(0x39, _gameInc++, d.ToArray(),
            $"self-profile(0x39) ac={_char.Ac} class='{_char.ClassName}' buffs={_buffs.Count} legends={legs.Count}");
    }

    // The self-view buff/effect box (issue #6): one tab-separated line per active buff/debuff with the remaining
    // time in seconds. Grouped by spell so a multi-stat buff shows once. The client turns the tabs into line
    // breaks (see SendSelfProfile). Reopening the profile re-reads the current durations.
    private string BuffBoxText()
    {
        long now = Environment.TickCount64;
        _buffs.RemoveAll(b => b.Expires <= now);
        var lines = _buffs
            .GroupBy(b => b.Key)
            .Select(g =>
            {
                int secs = (int)Math.Max(0, (g.Max(x => x.Expires) - now + 999) / 1000);
                var name = string.IsNullOrEmpty(g.First().Name) ? g.Key : g.First().Name;
                return $"{name} ({secs}s)";
            });
        return string.Join('\t', lines);
    }

    // length-prefixed ASCII string: [len u8][bytes]. Empty string -> a single 0 byte.
    private static void AddLenStr(List<byte> d, string? s)
    {
        var b = Encoding.ASCII.GetBytes(s ?? "");
        d.Add((byte)b.Length);
        d.AddRange(b);
    }

    // "!leg" — replay the EXACT 0x39 self-profile captured from a real 6.x server (jeedee/TkServer),
    // decrypted with the shared NexonInc cipher. Known-good content: AC 99, class "Peasant", legend
    // "Born in Hyul 31, Winter". If the 4.95 profile window opens and shows these, the format is shared
    // and our native SendSelfProfile is correct; if it garbles, we diff against this capture.
    private static readonly byte[] Profile6x =
    {
        0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x2b, 0x07,
        0x50, 0x65, 0x61, 0x73, 0x61, 0x6e, 0x74,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x80, 0x17,
        0x42, 0x6f, 0x72, 0x6e, 0x20, 0x69, 0x6e, 0x20, 0x48, 0x79, 0x75, 0x6c, 0x20, 0x33, 0x31, 0x2c,
        0x20, 0x57, 0x69, 0x6e, 0x74, 0x65, 0x72,
    };

    private void SendProfileReplay6x()
    {
        SendMap(0x39, _gameInc++, Profile6x, "replay6x-profile(0x39)");
        Log.Info("   -> REPLAY 6.x self-profile on 0x39 (expect: AC 99, class Peasant, legend 'Born in Hyul 31, Winter')");
    }

    // 0x34 = the "click" profile: the public view shown when you click a character. Distinct from the
    // profile-key window (0x39, stats/legend), it carries the character PORTRAIT, the writable profile
    // TEXT + PICTURE, nation, and legend. Layout REVERSED from the 4.95 client's own parser (0x48b6a0,
    // profile-page vtable+0x5c) — NOT the 7.x clif_clickonplayer, which is a different, much larger shape.
    // All multi-byte ints are BIG-ENDIAN. Body (after opcode/increment):
    //   5 header strings (u8 len + bytes): title, clan, clanTitle, class, name  (order confirmed live)
    //   appearance: tag u8 (=0) + 7 look bytes (same 7-byte form as 0x33 type-0)
    //   3 × portrait graphic id (u16BE) -> FACE.EPF
    //   profile TEXT blurb (u8 len + bytes)
    //   numeric attr (u32BE)   look-selector A (u8)   look-selector B (u8)   NATION (u8)
    //   profile PICTURE (u16BE len + bytes)
    //   legend count (u8) + legends { icon u8, color u8, textLen u8, text }
    // NOTE: 4.95's click popup has NO totem slot (TOTEM.EPF is unreferenced in the client).
    // <paramref name="target"/> is whoever the profile is ABOUT (self, for your own "!click"/profile key;
    // another connected player for a real click — RTK clif_clickonplayer). The packet always goes out over
    // THIS session's own socket (Send()/SendMap() are instance methods of the VIEWER); the DATA comes from
    // the target's own character/equipment, which is legal to read cross-instance here since WeaponLook,
    // ShieldLook, ProfileCellIcon and GearListText are all private instance methods of this same Session
    // class — calling target.WeaponLook() runs them against the target's own _char, not the viewer's.
    private void SendClickProfile(Session target)
    {
        var tc = target._char;
        var d = new List<byte>();

        // header strings — order pinned by the marker test (each renders in its labeled slot)
        AddLenStr(d, tc.Title);
        AddLenStr(d, tc.ClanName);
        AddLenStr(d, tc.ClanTitle);
        AddLenStr(d, tc.ClassName);
        AddLenStr(d, tc.Name);

        // appearance descriptor — tag 0 selects the 7-byte player look (identical to 0x33 self-look,
        // which already renders this character correctly): [sex, form, face, armor, 0, 0, 0]
        d.Add(0);
        d.AddRange(new byte[] { (byte)tc.Sex, 0, (byte)tc.Face, tc.Armor, 0, target.WeaponLook(), target.ShieldLook() });

        // three equipment ICON cells beside the doll: helm, left ring, right ring (no sprite layer for these
        // in 4.95, so they render as ground-icon boxes). Same IconWire encoding as the 0x37 equip window.
        d.AddRange(Be(target.ProfileCellIcon(4)));   // helm  (wire slot 4)
        d.AddRange(Be(target.ProfileCellIcon(7)));   // left ring  (wire slot 7)
        d.AddRange(Be(target.ProfileCellIcon(8)));   // right ring (wire slot 8)

        // FIELD #10 — PAGE-1 gear/item list (u8 len + text). Item names are TAB-separated (client
        // converts 0x09 -> CR for multiline). Empty until inventory/equipment exists.
        AddLenStr(d, target.GearListText());

        d.AddRange(Be32(0));      // numeric scalar — unknown, 0 for now
        // The two status cells beside the name — group (sociable) and exchange (trade). 0xff rendered as blank
        // WHITE boxes; a real 0/1 shows the off/on indicator. THIS is what the client reads to decide whether
        // the "Group"/"Exchange" buttons on this window are enabled — so a self-view always shows your own
        // flags, and (now that this takes a real target) another player's view shows THEIRS, matching RTK.
        d.Add((byte)(tc.Grouped  ? 1 : 0));   // group / sociable status
        d.Add((byte)(tc.Exchange ? 1 : 0));   // exchange / trade status
        d.Add(tc.Nation);      // nation index -> NATION_E.EPF

        // FIELD #15 — profile PICTURE bitmap: u16BE size + bytes (empty = 00 00)
        var pic = tc.ProfilePic ?? Array.Empty<byte>();
        d.AddRange(Be((ushort)pic.Length));
        d.AddRange(pic);

        // FIELD #16 — PAGE-2 writable profile BLURB (u8 len + text). This is the free-text box, a
        // SEPARATE field from the page-1 gear list. Omitting it desyncs the legend count.
        var blurb = Encoding.ASCII.GetBytes(tc.ProfileText ?? "");
        if (blurb.Length > 255) blurb = blurb[..255];
        d.Add((byte)blurb.Length);
        d.AddRange(blurb);

        // FIELD #17/#18 — legends: count u8, then each { icon u8, color u8, textLen u8, text }
        var legs = tc.Legends ?? new List<Legend>();
        d.Add((byte)Math.Min(legs.Count, 255));
        foreach (var lg in legs)
        {
            var t = Encoding.ASCII.GetBytes(lg.Text ?? "");
            if (t.Length > 255) t = t[..255];
            d.Add(lg.Icon);
            d.Add(lg.Color);
            d.Add((byte)t.Length);
            d.AddRange(t);
        }

        SendMap(0x34, _gameInc++, d.ToArray(), $"click-profile(0x34) id={tc.Id} nation={tc.Nation} blurb={blurb.Length}B legends={legs.Count}");
    }

    // Page-1 gear/item list for the click profile (the "inspect another player" view): the names of every
    // worn item, TAB-separated (the client turns 0x09 -> CR, one per line). This is the equipment list that
    // shows below the portrait when you click a character. Ordered by the canonical equip-slot byte so the
    // list reads weapon → armour → shield → helm → … regardless of the order items were put on. Called on
    // whichever Session the profile is ABOUT (see SendClickProfile), so this always reads ITS OWN _char.
    private string GearListText()
    {
        var names = _char.Equipment
            .Select(e => (worn: e, def: Content.ItemById(e.ItemId)))
            .Where(x => x.def is not null)
            .OrderBy(x => x.def!.EquipSlot)
            .Select(x => string.IsNullOrEmpty(x.worn.CustomName) ? x.def!.Name : x.worn.CustomName);
        return string.Join('\t', names);
    }

    // "!click" (self) / "!click <name>" (another connected player) — the debug entry point for the same
    // 0x34 packet a real click sends (HandleClickInfo). Useful for eyeballing the "view others" window
    // (and its Group/Exchange buttons, §11l) without needing a second live client to click you.
    private void ClickProfileCmd(string text)
    {
        string name = text.Length > "!click".Length ? text["!click".Length..].Trim() : "";
        if (name.Length == 0) { SendClickProfile(this); return; }
        var target = _world.FindPlayer(name);
        if (target is null) { SendLog($"{name} is nowhere to be found."); return; }
        SendClickProfile(target);
    }

    // "!ckm" — send a 0x34 click-profile with DISTINCT MARKER strings in every text field, so we can
    // read off which window slot each field lands in and pin the true 4.95 layout (the 7.x port
    // misaligns). Numeric appearance (nation/totem/sprite) is handled by the parser RE separately.
    private void SendClickMarker()
    {
        var save = (_char.Title, _char.ClanName, _char.ClanTitle, _char.ClassName, _char.Name, _char.ProfileText, _char.Legends);
        _char.Title     = "TTL";
        _char.ClanName  = "CLAN";
        _char.ClanTitle = "CRANK";
        _char.ClassName = "CLASS";
        _char.Name      = "NAME";
        _char.ProfileText = "BLURBTEXT";
        _char.Legends   = new List<Legend> { new Legend(0, 0, "LEGEND") };
        SendClickProfile(this);
        (_char.Title, _char.ClanName, _char.ClanTitle, _char.ClassName, _char.Name, _char.ProfileText, _char.Legends) = save;
        Log.Info("   -> MARKER click-profile sent (TTL/CLAN/CRANK/CLASS/NAME/BLURBTEXT/LEGEND)");
    }

    /// <summary>Build an encrypted game packet, send it, and log it.</summary>
    private void SendMap(byte opcode, byte inc, byte[] data, string label)
    {
        var pkt = MapBuild(opcode, inc, data);
        Send(pkt);
        Log.Info($"   -> {label}: {pkt.Length}B  {Log.Hex(pkt)}");
    }

    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private void SendMapInfo(ushort mapId, ushort xs, ushort ys, string title, ushort light, byte inc = 0)
    {
        var t = Encoding.ASCII.GetBytes(title);
        var b = new List<byte>();
        b.AddRange(Be(mapId));
        b.AddRange(Be(xs));
        b.AddRange(Be(ys));
        b.Add(5);            // flag
        b.Add(_realm);       // realm-center camera lock (0=off edge-aware, 1=on centered); toggled by F4
        b.Add((byte)t.Length);
        b.AddRange(t);
        // light field — encoding chosen by NEXUS_LIGHT_FMT so 5.33's parse can be probed live.
        var lv = LightValue;
        switch (LightFmt)
        {
            case "u8":    b.Add((byte)(lv & 0xFF)); break;                 // single byte (5.x may have narrowed it)
            case "leu16": b.Add((byte)(lv & 0xFF)); b.Add((byte)(lv >> 8)); break;  // little-endian u16
            default:      b.AddRange(Be((ushort)lv)); break;              // big-endian u16 (4.95-proven)
        }
        Log.Info($"   -> mapinfo(0x15) light={lv} fmt={LightFmt}");
        Send(MapBuild(Opcode.MapInfo, inc, b.ToArray()));
    }

    // In-world command feedback that lands in the CHAT LOG. The client's chat pane + over-head bubbles
    // are both driven by 0x0D speech (RE: handler 0x450170 → 0x44dc90 registers a 3s text object into the
    // world message-manager at world+0x418). The 0x02 SendMessage path is a login-style message BOX that
    // doesn't stack for multi-line output (why !maps/!mobs showed nothing). So command results speak as
    // the player's own entity → one chat-log line each. ASCII, clamped to the 0x0D u8 length field.
    private void SendLog(string text)
    {
        if (text.Length > 250) text = text[..250];
        SendSpeech(0, _char.Id, Encoding.ASCII.GetBytes(text));
    }

    // The client's STATUS / MINI-TEXT box — the scrolling log pane that sits below the inventory (where
    // "item dropped", "experience gained", look-at names, etc. belong). This is a DIFFERENT channel from
    // both the 0x0D chat bubble (SendLog) and the 0x02 login message box (SendMessage). RTK drives it via
    // clif_sendminitext → clif_sendmsg(sd, 3, msg): opcode 0x0A, body = `type(u16 LE) len(u8) text`.
    // type: 0=wisp(blue) · 3=mini/status text · 5=system · 11=group · 12=clan. 0x0A is one of the opcodes
    // the RE reference binary no-ops but the live 4.95 client renders — same group as the 0x0F/0x37 item
    // opcodes we already use (see protocol doc §"Binary note"). ASCII, clamped to the u8 length field.
    private void SendMiniText(string text, ushort type = 3)
    {
        if (text.Length > 255) text = text[..255];
        var t = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { (byte)(type & 0xFF), (byte)(type >> 8), (byte)t.Length };
        body.AddRange(t);
        SendMap(0x0A, _gameInc++, body.ToArray(), $"minitext(0x0A) type={type}");
    }

    // ---- helpers ----
    private void SendMessage(string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        var body = new List<byte> { 0x0F, (byte)t.Length };
        body.AddRange(t);
        body.Add(0);
        var enc = TkCrypt.Crypt(body.ToArray(), 0x02, TkCrypt.LoginKey);
        Send(TkPacket.Build(0x02, 0x02, enc));
        Log.Info($"   -> message: {text}");
    }

    /// <summary>
    /// Game packet: AA | len(u16 BE) | op | inc | body. The body is encrypted with the SAME
    /// simple NexonInc cipher as the login channel — confirmed by reversing NexusTK.exe: 4.95
    /// has ONE cipher (decrypt 0x478680 / key buffer 0x50211c built only from "NexonInc.",
    /// keylen 9, identity table 0x4f3358). No name-derived/table cipher, no 3 trailer bytes —
    /// those are 7.x-only and were the bug in the previous version of this method.
    /// </summary>
    private byte[] MapBuild(byte opcode, byte inc, byte[] data)
    {
        var enc = TkCrypt.Crypt(data, inc, TkCrypt.LoginKey);
        return TkPacket.Build(opcode, inc, enc);
    }

    private static byte[] Be(ushort v) => new[] { (byte)(v >> 8), (byte)(v & 0xFF) };

}
