using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ===== items ================================================================================
    // Wire layouts translated from RTK 7.x clif.c (clif_sendadditem/senddelitem/equipit/unequipit and
    // the parse* handlers). Multi-byte ints are big-endian, same as every other packet here. The
    // send-side opcodes (0x0F/0x10/0x37/0x38) are the historically-stable TK inventory opcodes; the
    // recv-side (0x07/0x08/0x17/0x1A/0x1C/0x1F/0x24) are confirmed to line up with 4.95 because the
    // walk/turn/chat/attack/setting opcodes already do. See docs §11c.

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s ?? "");

    private InvItem? InvAt(int slot) => _char.Inventory.FirstOrDefault(i => i.Slot == slot);

    private int FreeSlot()
    {
        for (int i = 0; i < _char.MaxInv; i++)
            if (_char.Inventory.All(it => it.Slot != i)) return i;
        return -1;
    }

    /// <summary>Put <paramref name="amount"/> of <paramref name="def"/> into the bag (stacking if the item
    /// stacks and a stack already exists), draw the slot (0x0F), and return false if any of it didn't fit.
    /// <para>Callers that TAKE the item from somewhere else must use <see cref="GivePlaced"/> instead — a
    /// bool can't distinguish "none of it fit" from "some of it fit", and deducting the full amount from the
    /// source after a partial give duplicates or destroys goods.</para></summary>
    private bool GiveItem(ItemDef def, int amount = 1, ushort dura = 0, string customName = "", bool quiet = false, string owner = "")
        => GivePlaced(def, amount, dura, customName, quiet, owner) == Math.Max(1, amount);

    /// <summary>As <see cref="GiveItem"/>, but returns HOW MANY actually landed in the bag (0..amount). This
    /// is the form any hand-over needs — trade, vault withdrawal, mail attachment — so the source can be
    /// debited by exactly what the destination accepted and no more.</summary>
    private int GivePlaced(ItemDef def, int amount = 1, ushort dura = 0, string customName = "", bool quiet = false, string owner = "")
    {
        // Seed durability from the item DB: worn gear starts at full durability, and a charged consumable
        // (wine/liquor/cigarettes) starts with its full charge count -- see ItemDef.IsCharged / HandleUseItem.
        if (dura == 0 && (def.IsEquip || def.IsCharged)) dura = def.Durability;

        // Bonded gear (totem helms, subpath weapons) binds to whoever obtains it: a caller that already knows
        // the owner (a pickup carrying the ground item's owner, a trade) passes it through to PRESERVE the bond;
        // otherwise a fresh bound item stamps THIS character as its owner. Ordinary items stay unowned. Bound
        // gear is never stackable (it's equipment), so only the fresh-slot path below can carry an owner.
        string boundOwner = !string.IsNullOrEmpty(owner) ? owner : (def.Bonded ? _char.Name : "");

        // Fill part-full stacks first, then spill into fresh slots, none of them past ItemDef.StackCap. This
        // used to be a single unbounded `stack.Amount += amount`, so a slot could hold any number at all.
        // Existing over-cap stacks are left alone rather than force-split: the `< cap` filter simply skips
        // them, so they drain naturally instead of a save-load rewriting a player's bag underneath them.
        int cap = def.StackCap, want = Math.Max(1, amount);
        // The inventory-wide cap comes first, since it's what stops a second stack existing at all.
        int allowed = Math.Min(want, CarryRoom(def, customName));
        if (allowed <= 0) { CarryCapNotice(def); return 0; }
        int left = allowed;
        if (def.Stackable)
            foreach (var stack in _char.Inventory
                         .Where(i => i.ItemId == def.Id && i.CustomName == customName && i.Amount < cap)
                         .ToList())
            {
                if (left <= 0) break;
                int put = Math.Min(cap - stack.Amount, left);
                stack.Amount += put;
                left -= put;
                SendAddItem(stack);
            }

        while (left > 0)
        {
            int slot = FreeSlot();
            // Pack full. Generic callers (a pickup, a drop, a quest reward) get the bare minitext; a shop
            // buy passes quiet:true because the NPC speaks a longer line of its own instead.
            if (slot < 0) { if (!quiet) SendMiniText("You can't have more."); break; }
            int put = Math.Min(cap, left);
            var it = new InvItem((byte)slot, def.Id, put, dura) { CustomName = customName, Owner = boundOwner };
            _char.Inventory.Add(it);
            SendAddItem(it);
            left -= put;
        }
        MarkDirty();
        // What actually landed. Short of `want` when the carry cap trimmed it (allowed < want) or the pack
        // ran out of slots partway (left > 0). Whatever did fit is kept rather than rolled back.
        return allowed - left;
    }

    /// <summary>How many more of <paramref name="def"/> the bag may take before hitting its inventory-wide
    /// limit (<see cref="ItemDef.CarryCap"/>), or <see cref="int.MaxValue"/> when the item has none. Since
    /// that limit equals one stack for every item that sets it, this is what stops a second stack forming —
    /// as opposed to <see cref="ItemDef.StackCap"/>, which only bounds a single slot.</summary>
    private int CarryRoom(ItemDef def, string customName = "")
    {
        if (!def.Stackable || def.CarryCap <= 0) return int.MaxValue;
        int held = _char.Inventory.Where(i => i.ItemId == def.Id && i.CustomName == customName).Sum(i => i.Amount);
        return Math.Max(0, def.CarryCap - held);
    }

    /// <summary>Hitting the carry cap is a RULE, not something a character says — it's the same answer
    /// whether the item came from a mob, a shop or the vault, so it goes to minitext rather than out of an
    /// NPC's mouth. (Distinct from the pack-being-full line, which a shopkeeper does speak.) The number is
    /// the cap itself, not the room left, so the message teaches the limit rather than the moment.</summary>
    private void CarryCapNotice(ItemDef def) =>
        SendMiniText($"{def.Name}, You can't have more than {def.CarryCap}.");

    /// <summary>True (and complains) when the player is on a mount, for the handlers that horseback forbids:
    /// using/eating, equipping, unequipping, dropping, throwing, dropping gold and casting. Centralised
    /// because the same refusal had drifted into three different wordings across six call sites.
    /// <para>MELEE IS NOT IN HERE. A blocked swing shows nothing at all — see HandleAttack, which returns
    /// silently and deliberately doesn't route through this.</para></summary>
    private bool BlockedByMount()
    {
        if (!_char.Mounted) return false;
        SendMiniText("You can't do that while riding a mount.");
        return true;
    }

    // Redraw the whole bag + worn gear (on world entry / warp): one 0x0F per bag slot, one 0x37 per gear slot.
    private void RefreshInventory()
    {
        foreach (var it in _char.Inventory.OrderBy(i => i.Slot)) SendAddItem(it);
        foreach (var e in _char.Equipment) SendEquip(e);
    }

    /// <summary>Immediate save for a discrete high-value event (level-up, quest completion, trade, profile
    /// edit, GM command). Routes through the same MarkDirty+FlushNow path as the throttled autosave so it
    /// gets the same failure-retry and cross-thread-safety guarantees, just without waiting for the next
    /// AutoSaveMs tick.</summary>
    private void SaveChar() { if (_enteredWorld) { _dirty = true; FlushNow(); } }

    /// <summary>
    /// A restorable snapshot of the bag and purse, for a transfer that commits against the database and has
    /// to be undone if that commit fails (claiming a parcel or a mail attachment — see
    /// <see cref="CharacterStore.SaveWith"/>).
    ///
    /// <para>Deep-copies each stack rather than copying the list, because giving an item can INCREASE the
    /// Amount on a stack the player already had. A shallow copy would restore the list shape and leave the
    /// duplicated count in place — which is the exact bug this whole path exists to prevent.</para>
    /// </summary>
    private (List<InvItem> Inv, uint Coins) SnapshotBag()
        => (_char.Inventory.Select(i => i.Clone()).ToList(), _char.Coins);

    /// <summary>Roll the bag and purse back to a <see cref="SnapshotBag"/>, and redraw. The redraw matters:
    /// the client was already told about the item by the give that we are now undoing, so without it their
    /// bag would show a stack the server no longer believes in.</summary>
    private void RestoreBag((List<InvItem> Inv, uint Coins) snap)
    {
        _char.Inventory = snap.Inv;
        _char.Coins = snap.Coins;
        RefreshInventory();
        SendStats();
    }

    /// <summary>Mark this session's character dirty without an immediate save — the mutation sites that
    /// used to be entirely unpersisted (pickup/drop/equip/durability/shop/bank/movement, see the
    /// persistence audit) call this instead. Picked up by this session's own FlushIfDue (active player) or
    /// World's periodic AutoSaveLoop sweep (idle player), whichever comes first — at most AutoSaveMs later.</summary>
    internal void MarkDirty() { if (_enteredWorld) _dirty = true; }

    /// <summary>Called once per read-loop iteration, after the packets received in this chunk are handled.
    /// Runs on the session's OWN thread — the same thread every MarkDirty() mutation runs on — so this is
    /// race-free by construction and is the primary autosave path for an ACTIVE player. It only needs
    /// World.AutoSaveLoop as a backstop for an IDLE dirty player (mutated, then stopped sending packets).</summary>
    private void FlushIfDue()
    {
        if (!_dirty || !_enteredWorld) return;
        if (Environment.TickCount64 - _lastSaveAtMs < AutoSaveMs) return;
        FlushNow();
    }

    /// <summary>Force-save now if dirty, ignoring the AutoSaveMs throttle. Used by SaveChar's immediate
    /// high-value saves, World's periodic sweep (idle players), the graceful-shutdown flush
    /// (World.SaveAllPlayers), and KickForReplacement. Safe to call from a thread other than this
    /// session's own read loop: _saveGate serializes concurrent callers for this one session.
    ///
    /// _dirty is cleared BEFORE the write (not after), so a mutation that lands on the session's own
    /// thread WHILE a save from another thread is in flight re-dirties us and is guaranteed to be picked
    /// up by the next flush — it can never be silently treated as "saved" without actually being captured.
    /// If the write itself fails (a bad disk, or a collection mutated mid-serialize racing this exact
    /// scenario — CharacterStore.Save returns false), _dirty is restored so the next flush retries.</summary>
    internal void FlushNow()
    {
        if (!_enteredWorld) return;
        lock (_saveGate)
        {
            if (!_dirty) return;
            _dirty = false;
            if (StoreSave()) _lastSaveAtMs = Environment.TickCount64;
            else _dirty = true;
        }
    }

    /// <summary>Normalized account identity (matches CharacterStore's DB key), used as the key into
    /// World's online-session registry for the duplicate-login guard. Only meaningful once _enteredWorld.</summary>
    internal string UserKey => CharacterStore.Key(_char.Name);

    /// <summary>Force this session out because the same account just logged in elsewhere (World.RegisterOnline
    /// detected the collision in HandleArrival). Flushes any pending mutation FIRST so the new session's
    /// upcoming _store.Load sees our latest state, marks us _replaced (so the read-loop's own disconnect
    /// save — which could otherwise fire moments later with now-stale data — is skipped), then tears the
    /// connection down. Safe to call from the NEW session's thread: FlushNow's _saveGate serializes against
    /// anything this (old) session's own thread might concurrently be doing, and CloseConnection is
    /// idempotent either way.</summary>
    internal void KickForReplacement()
    {
        Volatile.Write(ref _replaced, 1);
        FlushNow();
        SendMiniText("You have logged in from another location.");
        CloseConnection("replaced by new login");
    }

    // ===== quests (see Server/Quests.cs, NpcContext quest helpers) ================================
    // Quest state lives in _char.Quests (a flat key->int map, persisted): a quest's stage under its key, its
    // progress tallies under composite counter keys. These internal helpers are the whole surface the quest
    // scripts (via NpcContext) and the kill hook touch, so quest logic never reaches into session internals.
    internal int  QuestStage(string questKey) => _char.Quests.GetValueOrDefault(questKey);

    /// <summary>Has this character finished the Dog Linguist chain? Set by the Spotted dog (npc_dialog.lua
    /// <c>npcs_say.DogLinguistNpc</c>) or by <c>@dog</c>; it is what lets you say "secret" to your own class's
    /// Dog. Learning the spells ALSO needs an eligible path (Content.CanLearnDogSpells — base classes and NPC
    /// subpaths, never a PC subpath), which the Dog checks separately.</summary>
    internal bool HasDogFlag => QuestStage(Content.DogFlagReg) > 0;
    internal void SetQuestStage(string questKey, int stage) { _char.Quests[questKey] = stage; SaveChar(); }
    internal int  QuestCounter(string counterKey) => _char.Quests.GetValueOrDefault(counterKey);

    // ===== group experience (RTK Scripts/exp.lua onGetExp) =======================================
    // Per-head share by group size. The whole group is worth MORE than a solo kill — two people take
    // 0.703 each, so 1.41x the mob's experience enters the world — which is the mechanical reason the
    // tutor tells you to find a covenant. Index = number of eligible members; [0] is the solo case.
    // 1-INDEXED by member count, exactly like the Lua table it comes from — slot 0 is an unused placeholder
    // so GroupExpShare[n] reads the same here as expTable[n] does there.
    private static readonly double[] GroupExpShare =
    {
        0.0,                                                                          // unused
        1.0,      0.70339,  0.67339,  0.64339,  0.61339,  0.58339,  0.55339,  0.53339,
        0.51339,  0.49339,  0.47339,  0.45339,  0.44339,  0.43339,  0.42339,
    };
    private static double ShareFor(int members) =>
        GroupExpShare[Math.Clamp(members, 1, GroupExpShare.Length - 1)];

    /// <summary>Award a kill's experience to the killer AND to every eligible group member, RTK-style
    /// (<c>Scripts/exp.lua onGetExp</c>). Eligible = in the same group, ALIVE, on the mob's map, and within
    /// 12 tiles of the CORPSE on both axes (RTK <c>distanceSquare(member, mob, 12)</c> — a square, not a
    /// radius). Each eligible member takes <c>share(n)</c> of the mob's experience scaled by their own
    /// standing against the group's highest: <c>finalxp = amount * (level + mark*10) / highest</c>, so a
    /// level 10 towed through a level 50 cave earns a fifth of what the 50 earns rather than a free ride.
    /// <para>One deliberate divergence: the KILLER is always eligible, even if somehow out of range of the
    /// corpse. RTK would silently pay nobody in that case; here the swing that landed always pays the person
    /// who landed it. Everyone else is filtered exactly as RTK filters them.</para>
    /// <para>Each share still goes through <see cref="AwardExp"/> per member, so the Peasant wall, level-ups
    /// and the save all apply to each of them individually.</para>
    /// <para>The totem window is the exception, and a DELIBERATE divergence from RTK: retail spread it across
    /// the group. See the <c>anyTotem</c> comment below.</para>
    /// <para><b>QUEST CREDIT FOLLOWS THE EXPERIENCE.</b> <paramref name="mobKey"/> is tallied for everyone
    /// this kill pays, which is the rule the archived Poet's Restore page states in as many words — "You can
    /// be part of a group as long as you get experience then your quest will succeed". It also closes a hole:
    /// <see cref="TallyKill"/> used to be called by hand at three of the twelve kill sites, so a mob killed by
    /// a SPELL or by a summoned pet counted toward no quest at all. Routing it through here means every path
    /// that pays for a kill also records it, once, for the same set of people.</para></summary>
    internal void AwardKillExp(uint reward, ushort mobMap, int mobX, int mobY, string? mobKey = null)
    {
        static long Eff(Session s) => s.CharLevel + s.CharMark * 10L;

        // Who this kill counts for: the killer always, plus every group member alive, on the mob's map and
        // within GroupExpRange of the corpse on both axes.
        var eligible = new List<Session> { this };
        if (_party is not null)
            foreach (var m in _party.Members)
            {
                if (ReferenceEquals(m, this)) continue;                    // the killer, added above
                if (m.IsDead || m.CharMap != mobMap) continue;
                if (Math.Abs(m.CharX - mobX) > GroupExpRange || Math.Abs(m.CharY - mobY) > GroupExpRange) continue;
                eligible.Add(m);
            }

        foreach (var m in eligible) m.TallyKill(mobKey);

        if (reward == 0) return;
        if (eligible.Count <= 1) { AwardExp(reward, killExp: true); return; }   // solo, or nobody else in range

        long highest = eligible.Max(Eff);
        if (highest <= 0) highest = 1;
        uint amount = (uint)Math.Ceiling(reward * ShareFor(eligible.Count));

        // TOTEM TIME IS GROUP-WIDE (brian, played retail): if it is ANY member's totem time, EVERY member's
        // share gets the +5%, not just the ones whose own totem is up. So the window is resolved once, here,
        // across the group rather than per-member inside AwardExp.
        //
        // This is a deliberate divergence from RTK, which calls checkTotemTimeXP(finalxp) inside its
        // per-member loop (Scripts/exp.lua:66) and so pays the bonus only to members whose own totem is in
        // window. Retail behaviour wins over the reference server — the four windows partition the day, so
        // under RTK's reading a mixed-totem group could never have more than one member bonused at a time,
        // which is exactly the "group with people unlike you" incentive the tutor's stage-8 lecture is built
        // around. Do not "fix" this back to the Lua.
        //
        // Scoped to the ELIGIBLE members — the ones actually being paid — rather than the whole party: a
        // member out of range or on another map draws nothing from this kill, so letting their totem raise
        // everyone else's share would pay a bonus sourced from someone the kill never touched.
        bool anyTotem = eligible.Any(m => _world.IsTotemTime(m.CharTotem));

        foreach (var m in eligible)
        {
            uint share = (uint)Math.Ceiling(amount * (double)Eff(m) / highest);
            m.AwardExp(share, killExp: true, totemTime: anyTotem);
        }
        Log.Info($"   -> group exp: {reward} -> {amount} x{eligible.Count} members " +
                 $"(highest eff {highest}{(anyTotem ? ", TOTEM TIME" : "")})");
    }

    /// <summary>How far from the corpse a group member may stand and still be paid, on each axis
    /// (RTK <c>distanceSquare(..., 12)</c>). Comfortably more than a screen, so the whole group gets paid
    /// even when spread across the room.</summary>
    private const int GroupExpRange = 12;

    /// <summary>Award experience: add exp, then run RTK's pc_checklevel loop (0+ level-ups — a single big
    /// reward can carry a low-level character through several levels at once), refresh TNL, push the HUD exp
    /// bar, and persist. Every exp source (quests, melee/spell kills) funnels through here so leveling happens
    /// the same way regardless of who granted it. See LevelUp for the per-level stat/HP/MP gain formulas.
    /// <para>Kill experience should go through <see cref="AwardKillExp"/> instead, which splits it across the
    /// group first and then calls this once per member.</para></summary>
    internal void AwardExp(uint amount, bool killExp = false, bool? totemTime = null)
    {
        if (amount == 0) return;
        // THE PEASANT WALL (RTK player.lua giveXPStacked:4279). A Peasant at level 5 gains NOTHING — the Lua
        // returns before `player.exp = player.exp + get`, so the exp is refused outright, not banked for later.
        // That distinction is the whole point: banking it would let a walled Peasant hoard exp and then flood
        // through several levels the instant they pick a path, and would hand the shadow-stat vendors (which
        // spend banked exp) a supply RTK never lets a Peasant have. Sits above the totem bonus and the
        // "N experience!" notice because RTK's check does too — a refused grant is silent about the amount.
        if (CharBasePathId == 0 && _char.Level >= 5)
        {
            SendMiniText("Upon attaining level five, choose a Hero's Path.  You will not progress until you choose a path.");
            return;
        }
        // Totem time (RTK Scripts/exp.lua → Player.checkTotemTimeXP): kill exp earned during your totem's
        // six-hour window is multiplied by 1.05. Only combat kills opt in via killExp — quest/tutorial/NPC
        // rewards do NOT, matching RTK where the multiplier lives in the mob-kill exp split, not the generic
        // grant. Totem 4 (None), or a clock hour outside the window, yields no bonus.
        //
        // totemTime lets the caller answer the window question instead: AwardKillExp passes the GROUP's
        // answer, because retail gives the bonus to everyone in the group whenever it is any member's totem
        // time (see the anyTotem comment there). Null — every other caller — means "decide from my own
        // totem", which is the solo case and identical to what this always did.
        bool totem = killExp && (totemTime ?? _world.IsTotemTime(_char.Totem));
        if (totem) amount = (uint)Math.Round(amount * 1.05, MidpointRounding.AwayFromZero);
        // RTK player.lua giveXPStacked: every exp grant pops a status-box message, not just combat —
        // quest/tutorial/NPC rewards get the same notice retail players see on a kill.
        SendMiniText($"{amount:N0} experience!");
        _char.Exp += amount;
        int path = CharBasePathId;
        while (_char.Level < 99)
        {
            uint need = Content.ExpToNext(path, _char.Level);
            if (need == 0 || _char.Exp < need) break;   // no table entry, or not enough exp yet -> done
            // RTK onLevel.lua:40 repeats the wall inside the level-up script, and so do we. The gate at the top
            // of this method means a normal grant never reaches here, but exp can arrive at level 5 by other
            // routes — a character rebuilt onto path 0 by @class/@lvl, or one carrying exp banked before the
            // gate existed — and those must not level either.
            if (path == 0 && _char.Level >= 5)
            {
                SendMiniText("Upon attaining level five, choose a Hero's Path.  You will not progress until you choose a path.");
                break;
            }
            LevelUp(path);
        }
        uint tnlNext = Content.ExpToNext(path, _char.Level);
        _char.Tnl = tnlNext > _char.Exp ? tnlNext - _char.Exp : 0;
        SendStats();
        // NO SendSelfProfile() here. AC/Dam/Hit/Tnl do live in the 0x39 profile rather than the 0x08 HUD
        // packet, but the 4.95 client treats an unsolicited 0x39 as "OPEN the profile window" — pushing one to
        // refresh Tnl popped the character sheet in the player's face on every single kill (reported live
        // while casting Ion Charge; the log shows 0x39 going out with no 0x2D having come in). 0x39 is now
        // strictly a RESPONSE to the client's own 0x2D request, which re-reads these values anyway.
        SaveChar();
    }

    /// <summary>Experience lost on death (RTK player.lua <c>deathExpLoss</c>). Below 99 the loss is a flat 20%
    /// of the CURRENT LEVEL'S BAND — the exp between this level's threshold and the previous one — so it costs
    /// the same fifth of a level whether you just dinged or are one kill from the next one, and it can push you
    /// back below your own level threshold (RTK never de-levels you for it, and neither do we: the level stands,
    /// the bar just refills). At 99 there is no band left, so it takes <paramref name="percent"/> of the total
    /// banked exp instead — 50% out in the world, 10% inside an instance.</summary>
    private void DeathExpLoss(double percent)
    {
        int path = CharBasePathId;
        uint lost;
        if (_char.Level < 99)
        {
            uint here = Content.ExpToNext(path, _char.Level);
            uint prev = _char.Level > 1 ? Content.ExpToNext(path, _char.Level - 1) : 0;
            if (here <= prev) return;                                  // no table entry for this level -> nothing to take
            lost = (uint)Math.Ceiling((here - prev) * 0.20);
        }
        else lost = (uint)Math.Ceiling(_char.Exp * percent);

        if (lost == 0) return;
        _char.Exp = lost >= _char.Exp ? 0 : _char.Exp - lost;
        uint tnlNext = Content.ExpToNext(path, _char.Level);
        _char.Tnl = tnlNext > _char.Exp ? tnlNext - _char.Exp : 0;
        SendMiniText($"You've lost {lost:N0} exp!");
        SendStats();
        MarkDirty();
        Log.Info($"   -> death exp loss: -{lost} -> {_char.Exp} (tnl {_char.Tnl})");
    }

    /// <summary>Award coin (refresh the HUD + persist).</summary>
    internal void AwardGold(uint amount) { if (amount == 0) return; _char.Coins += amount; SendStats(); SaveChar(); }

    // One level-up: RTK onLevel.lua, ported verbatim. `secondary`/`tertiary` are the "does this level also
    // bump a non-primary stat" flags — non-Peasant paths roll them off (level+1)%2 and %3 (both on every 6th
    // level); Peasants (no primary stat until they pick a path) roll a different %2/%3/%5 combo that instead
    // decides whether THIS level's single point goes to might (primary) or grace+will (secondary+tertiary).
    // Might/Grace/Will are bytes and RTK's own calc caps them at 255 elsewhere (SendStats clamps on send), so
    // no clamp needed here. HP/MP gains are RTK's per-path random ranges (inclusive both ends).
    private void LevelUp(int path, bool announce = true)
    {
        int nextLevel = _char.Level + 1;
        int secondary = 0, tertiary = 0, primary = 0;
        if (path != 0)
        {
            if (nextLevel % 2 == 0 && nextLevel % 3 == 0) { secondary = 1; tertiary = 1; }
            else if (nextLevel % 2 == 0) secondary = 1;
            else if (nextLevel % 3 == 0) tertiary = 1;
        }
        else
        {
            if (nextLevel % 2 == 0) primary = 1;
            else if (nextLevel % 3 == 0 || nextLevel % 5 == 0) { secondary = 1; tertiary = 1; }
        }

        // Which stat is PRIMARY per class stays here (mechanic); the HP/MP gain RANGES are tunable balance data
        // in game-data/PathGrowth.csv (Content.PathGrowthFor) — max is the exclusive Random.Next arg.
        switch (path)
        {
            case 1:  _char.Might += 1; _char.Grace += (byte)secondary; _char.Will += (byte)tertiary; break;   // Warrior: might primary
            case 2:  _char.Might += (byte)secondary; _char.Grace += 1; _char.Will += (byte)tertiary; break;   // Rogue: grace primary
            case 3:  _char.Might += (byte)tertiary; _char.Grace += (byte)secondary; _char.Will += 1; break;   // Mage: will primary
            case 4:  _char.Might += (byte)tertiary; _char.Grace += (byte)tertiary; _char.Will += 1; break;    // Poet: will primary
            default: _char.Might += (byte)primary; _char.Grace += (byte)secondary; _char.Will += (byte)tertiary; break;   // Peasant
        }
        var g = Content.PathGrowthFor(path);
        int hpGain = Random.Shared.Next(g.HpMin, g.HpMax);
        int mpGain = Random.Shared.Next(g.MpMin, g.MpMax);

        _char.MaxHp = (uint)((int)_char.MaxHp + hpGain);
        _char.MaxMp = (uint)((int)_char.MaxMp + mpGain);
        _char.Level = (byte)nextLevel;
        // AC is signed/lower-is-better. Naked base AC = 100 - level, LINEAR and CLASS-INDEPENDENT — the real
        // NexusTK rule, documented by Warrior Tutor Yttribium ("Armour Class and you": "Your base AC (naked)
        // is +100 - level"; scraped_nexus_data boards_tutors/spells_formulas.md). Every class reaches AC 1 at
        // level 99 (100-99). This supersedes both the earlier RTK onLevel.lua port (-1/level from a stored
        // value) AND the brief Peasant-gate experiment — both wrong; the value is purely a function of the
        // current level, so we recompute it rather than decrement. Gear/buffs modify it at display/combat
        // time, where the -80 (human) / -95 (mob) mitigation caps also apply — NOT here. _char.Ac caches the
        // naked base so cross-session readers (PvP, other-player profile) stay a simple field read.
        _char.Ac = (sbyte)Math.Clamp(100 - _char.Level, -128, 127);

        // Full heal on level-up (RTK: health = maxHealth; magic = maxMagic), including gear/buff bonuses.
        _char.Hp = EffMaxHp;
        _char.Mp = EffMaxMp;

        // RTK onLevel.lua: sendAnimation(2, 0) + playSound(123) — anim 2 is the same Effect.tbl id Harden
        // Armor uses (confirmed live), but RTK's raw sound numbering is known not to map cleanly onto the
        // 4.95 client (see docs §7.3) — 123 here is the same unverified best-effort port as Harden Armor's
        // 5; both want a correct id from `@snd <id>` before this is right.
        if (announce)
        {
            BroadcastFx(_char.Id, 2, 123);
            SendMiniText("You have gained new insight.");
            Log.Info($"   -> LEVEL UP: {_char.Name} is now level {_char.Level} ({Content.PathName(path)}) HP+{hpGain} MP+{mpGain}");
        }
    }

    // "@lvl <n>" — rebuild as a clean level-n character of the current class. Anything a level-n character of
    // that class would have, this character now has; anything it wouldn't, this character now doesn't.
    //
    // MARK IS CLEARED. A subpath rank sits ON TOP of the cap — Il san is "level 100" — so it can't survive a
    // move to level 40, and leaving it on at 99 would make "@lvl 99" and "@mark 0" mean different things for
    // no reason. @mark is how you put it back, and it re-runs this at 99 first.
    internal void RespecLevel(int target) => RespecTo(Math.Clamp(target, 1, 99), mark: 0);

    /// <summary>The one character rebuild: @lvl, @class, @mark and @align all end up here. Resets to the RTK
    /// level-1 baseline (Player.reset / CharacterFactory) and applies real LevelUps up to
    /// <paramref name="level"/>, so MaxHP/MaxMP, Might/Will/Grace and AC accumulate legitimately (the same
    /// growth a natural progression uses) and HP/MP end full. Works both up and down. Staff-only, so it
    /// bypasses the Peasant level-5 wall; growth follows the character's CURRENT path (a Peasant gets peasant
    /// HP/MP curves — pick a real path first for class-appropriate stats). Then
    /// <see cref="SyncSpellbook"/> rebuilds the book to match, which is what makes this a clean slate rather
    /// than a stat edit: no ability from a previous class, level or rank can survive it.
    ///
    /// <para><paramref name="mark"/> (the subpath rank, 0-<see cref="Content.MaxMark"/>) is levels PAST the cap: each rank runs one more
    /// LevelUp with the level counter reading 100, 101, … so an Il san keeps growing on the same curve and
    /// its AC keeps falling past 1 (100 − effective level). The stored level then goes back to 99, because
    /// that is what the character sheet and the exp table understand; only the accumulated stats and the
    /// rank's spells reveal the difference. Nothing in the live game's data says what a rank is worth in
    /// stats, so "one more level per rank" is our own model, not a ported number.</para></summary>
    internal void RespecTo(int level, int mark)
    {
        level = Math.Clamp(level, 1, 99);
        mark  = Math.Clamp(mark, 0, Content.MaxMark);
        // Growth and the exp table are keyed to the BASE four (PathGrowth.csv and LevelExp.csv have no rows
        // past 4), so an NPC subpath levels on its base class's curve — a Chung ryong grows like the Warrior
        // it is. Reading CharClassId here instead would silently drop a subpath onto the Peasant curve.
        int path = CharBasePathId;

        _char.Level = 1;
        _char.Might = 3; _char.Grace = 3; _char.Will = 3;
        _char.MaxHp = (uint)Random.Shared.Next(45, 56);   // RTK Player.reset baseline
        _char.MaxMp = (uint)Random.Shared.Next(32, 37);
        _char.Ac = (sbyte)(100 - 1);
        for (int lvl = 1; lvl < level + mark; lvl++) LevelUp(path, announce: false);

        _char.Mark  = (byte)mark;
        _char.Level = (byte)level;                                      // the ranks' levels are not real levels
        _char.Hp = EffMaxHp; _char.Mp = EffMaxMp;                       // full vitals for the new level
        _char.Exp = level > 1 ? Content.ExpToNext(path, level - 1) : 0;  // exp at the start of this level
        uint tnlNext = Content.ExpToNext(path, level);
        _char.Tnl = tnlNext > _char.Exp ? tnlNext - _char.Exp : 0;

        SyncSpellbook(announce: false);                                 // also StoreSaves, and reports the count below
        if (_enteredWorld) StoreSave();
        BroadcastFx(_char.Id, 2, 123);   // one level-up sparkle for the whole jump
        SendStats();
        // Same reason as AwardExp above: an unsolicited 0x39 OPENS the profile window on 4.95, so don't push
        // one here either. This is the GM path, where the player is standing right there and can open the
        // sheet themselves.
        SendMessage($"Now level {_char.Level} ({ClassTitle}) — HP {_char.MaxHp}, MP {_char.MaxMp}, " +
                    $"might {_char.Might}, will {_char.Will}, grace {_char.Grace}, AC {_char.Ac}, " +
                    $"{_char.Spells.Count} ability(ies).");
        Log.Info($"   -> respec lvl {level} mark {mark}: reset+leveled ({ClassTitle}, base {Content.PathName(path)}) " +
                 $"HP{_char.MaxHp} MP{_char.MaxMp} M{_char.Might}/W{_char.Will}/G{_char.Grace} AC{_char.Ac} book{_char.Spells.Count}");
    }

    /// <summary>How many of an item (by content key) the player is carrying, summed across stacks.</summary>
    internal int CountItem(string itemKey)
    {
        var def = Content.ItemByKey(itemKey);
        return def is null ? 0 : _char.Inventory.Where(i => i.ItemId == def.Id).Sum(i => i.Amount);
    }

    /// <summary>Consume <paramref name="amount"/> of an item by key (across stacks, low slots first), redrawing
    /// each touched slot. Returns false and takes nothing if the player doesn't have that many.</summary>
    internal bool TakeItem(string itemKey, int amount)
    {
        var def = Content.ItemByKey(itemKey);
        if (def is null || amount <= 0 || CountItem(itemKey) < amount) return false;
        int remaining = amount;
        foreach (var it in _char.Inventory.Where(i => i.ItemId == def.Id).OrderBy(i => i.Slot).ToList())
        {
            if (remaining <= 0) break;
            int take = Math.Min(remaining, it.Amount);
            it.Amount -= take; remaining -= take;
            if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)it.Slot, 0); }   // reason 0 -> "<item> removed." (see the §11c table; NOT silent)
            else SendAddItem(it);
        }
        SaveChar();
        return true;
    }

    /// <summary>Give a reward item by key (stacking; one call per unit for non-stackables). False if the item is
    /// unknown or the pack filled mid-give (GiveItem already told the player).</summary>
    internal bool GiveRewardItem(string itemKey, int amount)
    {
        var def = Content.ItemByKey(itemKey);
        if (def is null || amount <= 0) return false;
        if (def.Stackable) { if (!GiveItem(def, amount)) return false; }
        else for (int i = 0; i < amount; i++) if (!GiveItem(def)) return false;
        SaveChar();
        return true;
    }

    /// <summary>Bump the lifetime kill tally for a mob key (RTK's per-mob kill count). Quests read a DELTA of
    /// this — kills since they were accepted — so nothing else is needed here. Keyless kills (debug summons)
    /// are ignored. Called only from <see cref="AwardKillExp"/>, for every player that kill pays; do not call
    /// it at a kill site, or that site double-counts for the killer and still misses their group.</summary>
    private void TallyKill(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _char.Kills[key] = _char.Kills.GetValueOrDefault(key) + 1;
        _char.Kills[TotalKillsKey] = _char.Kills.GetValueOrDefault(TotalKillsKey) + 1;
        // Was SaveChar() (a full-blob rewrite per kill — the dominant write-amplification source while
        // grinding). MarkDirty lets the throttled autosave coalesce a whole grinding session into one
        // save every AutoSaveMs instead of one per kill.
        MarkDirty();
    }

    /// <summary>Lifetime kills recorded for a mob key (RTK's <c>player:killCount</c>).</summary>
    internal int KillCount(string mobKey) => _char.Kills.GetValueOrDefault(mobKey);

    /// <summary>Tally key for "anything at all", kept in the same map so it persists with no schema change.
    /// The leading space cannot collide with a mob key. Read by <see cref="TotalKills"/>, which the Old dog's
    /// Restore quest uses to enforce its "do NOT kill anything else along the way" rule.</summary>
    private const string TotalKillsKey = " total";

    /// <summary>Lifetime kills of ANY mob. Only counts kills recorded since this tally was added, which is
    /// fine for its only use: every reader compares a delta taken after the quest was accepted.</summary>
    internal int TotalKills => _char.Kills.GetValueOrDefault(TotalKillsKey);

    // ---- string quest registry (RTK registryString): the active minor-quest key, etc. -----------
    internal string QuestStr(string key) => _char.QuestStrings.GetValueOrDefault(key, "");
    internal void   SetQuestStr(string key, string value) { _char.QuestStrings[key] = value; SaveChar(); }

    // ---- legends by internal name (add/replace/remove/query) -------------------------------------
    // A quest owns a legend by its Name key, so it can update or clear its own line without matching text.
    internal bool HasLegend(string name) => _char.Legends.Any(l => l.Name == name);
    internal void RemoveLegend(string name) { if (_char.Legends.RemoveAll(l => l.Name == name) > 0) SaveChar(); }
    internal void AddLegend(string text, string name, byte icon, byte color)
    {
        if (!string.IsNullOrEmpty(name)) _char.Legends.RemoveAll(l => l.Name == name);   // replace-by-name
        _char.Legends.Add(new Legend(icon, color, text, name));
        SaveChar();
    }

    // ---- player facts quests read (level / a stat total / random / wall-clock) -------------------
    internal int  CharLevel => _char.Level;
    /// <summary>A single "power" number quests gate on (RTK's baseMagic*2 + baseHealth analog).</summary>
    internal int  CharStat  => (int)(_char.MaxMp * 2 + _char.MaxHp);
    /// <summary>Subpath mark/rank (RTK <c>status.mark</c>) — see <see cref="Character.Mark"/>. 0 until a GM
    /// sets it, since no subpath-promotion NPC is ported yet.</summary>
    internal int  CharMark  => _char.Mark;
    internal int  QuestRandom(int maxInclusive) => Random.Shared.Next(1, Math.Max(1, maxInclusive) + 1);
    internal long NowUnix   => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    internal int  CharSex    => _char.Sex;
    internal int  CharFace   => _char.Face;
    internal int  CharNation => _char.Nation;
    internal bool CharMounted => _char.Mounted;
    /// <summary>Karma score (RTK <c>player.karma</c>) — fractional; see <see cref="Karma"/>.</summary>
    internal double CharKarma => _char.Karma;
    internal int  CharX      => _char.X;
    internal int  CharY      => _char.Y;
    internal uint CharCoins  => _char.Coins;
    internal ushort CharMap  => _char.Map;
    internal uint CharHp     => _char.Hp;
    internal uint CharMaxHp  => _char.MaxHp;
    internal uint CharMaxMp  => _char.MaxMp;
    internal uint CharExp    => _char.Exp;
    internal int  CharTotem  => _char.Totem;

    /// <summary>Emigrate to another kingdom (RTK <c>player:updateCountry</c>, the town criers' "Move to …"
    /// and Rotah's "Become Neutral"). Persists, clears any bound home, and repaints the HUD.
    ///
    /// <para>The bound home has to go: it is a room in a town of the kingdom you just left, and RTK clears it
    /// on every one of its four move paths (<c>general_npc_funcs.moveToCountry</c>). Without that a Buyan who
    /// took a room in Sanhae and then moved to Kugnae would still be sleeping in Buya's back country.</para>
    ///
    /// <para>The nation byte is HUD state the client only learns from the stats packet, so this repaints it —
    /// otherwise the crest keeps showing the old kingdom until something else happens to push stats. Nothing
    /// else needs telling: the revive point (<see cref="CharacterFactory.HomeCityFor"/>) and the tavern set
    /// (<see cref="HomeGroup"/>) both read <c>_char.Nation</c> live, and the 0x33 appearance carries no
    /// nation.</para>
    ///
    /// <para>NOT modelled, because neither exists here: RTK also drops your clan on the way out ("you will
    /// leave all that you have behind, your clan, your loyalties, your home") and its subpath-hall home
    /// (<c>registry["home"] == 2</c>).</para></summary>
    internal void SetNation(byte nation)
    {
        _char.Nation = nation;
        _char.Quests[HomeReg] = HomeNone;
        SaveChar();
        SendStats();
    }

    internal int  CharMight  => _char.Might;
    internal int  CharGrace  => _char.Grace;
    internal int  CharWill   => _char.Will;
    // Willingness flags a peer's party/trade request is gated on (RTK settingFlags FLAG_GROUP/FLAG_EXCHANGE;
    // §9.5 profile status cells, toggled by 0x1b sub-cmd 0x02/0x08).
    internal bool WantsGroup    => _char.Grouped;
    internal bool WantsExchange => _char.Exchange;

    // ---- marriage state (ChapelAbility; RTK propose.lua / chapel_npc.lua) -------------------------
    internal string CharFiance        => _char.Fiance;
    internal bool   CharIsProposee    => _char.IsProposee;
    internal long   CharMarriageTimer => _char.MarriageTimer;
    internal long   CharRingCooldown  => _char.RingCooldown;
    internal string CharSpouseName    => _char.Spouse;
    internal void SetRingCooldown(long unixSeconds) { _char.RingCooldown = unixSeconds; SaveChar(); }
    internal void SetSpouse(string name) { _char.Spouse = name; SaveChar(); }
    internal void SetEngaged(string fianceName, bool isProposee, long timerUnix)
    {
        _char.Fiance = fianceName; _char.IsProposee = isProposee; _char.MarriageTimer = timerUnix;
        SaveChar();
    }
    internal void ClearEngagement() { _char.Fiance = ""; _char.IsProposee = false; _char.MarriageTimer = 0; SaveChar(); }

    /// <summary>Break off an engagement on BOTH sides, if the fiancé is reachable — RTK's own script only
    /// clears the clicking player's registry, leaving the other party's "engaged" legend dangling forever if
    /// they never separately visit a Chapel; clearing both sides avoids that permanently-stuck state.</summary>
    internal void BreakOffEngagement()
    {
        var fiance = _world.FindPlayer(_char.Fiance);
        RemoveLegend("engaged"); ClearEngagement();
        if (fiance is not null) { fiance.RemoveLegend("engaged"); fiance.ClearEngagement(); }
    }

    /// <summary>Finalize a divorce on BOTH sides (same "don't dangle the other party" reasoning as
    /// <see cref="BreakOffEngagement"/>).</summary>
    internal void FinishDivorce()
    {
        var spouse = _world.FindPlayer(_char.Spouse);
        RemoveLegend("married"); SetSpouse(""); TakeItem("love", 1);
        if (spouse is not null) { spouse.RemoveLegend("married"); spouse.SetSpouse(""); spouse.TakeItem("love", 1); }
    }

    /// <summary>Spend coin if the player can afford it (refresh HUD + persist); false, unchanged, if they can't.</summary>
    internal bool SpendGold(uint amount)
    {
        if (amount > 0 && _char.Coins < amount) return false;
        _char.Coins -= amount;
        SendStats();
        SaveChar();
        return true;
    }

    // ---- shadow-stat vendors (ShadowStatsAbility; RTK NPCs/Common/ExpSeller.lua) — trade banked exp for
    // permanent stat growth once leveling itself no longer spends it (the vendor gates at level 90). Tnl is
    // recomputed the same way AwardExp does, since Exp changed (rarely matters at these levels, but keeps
    // the HUD's "to next level" honest if a Peasant-capped or sub-99 character somehow gets here).
    /// <summary>Spend banked exp if the player has enough (refresh HUD + persist); false, unchanged, if not.</summary>
    internal bool SpendExp(uint amount)
    {
        if (amount > 0 && _char.Exp < amount) return false;
        _char.Exp -= amount;
        uint tnlNext = Content.ExpToNext(CharBasePathId, _char.Level);
        _char.Tnl = tnlNext > _char.Exp ? tnlNext - _char.Exp : 0;
        SendStats();
        SaveChar();
        return true;
    }

    internal void RaiseMight(int by) { _char.Might = (byte)Math.Clamp(_char.Might + by, 0, 255); SendStats(); SaveChar(); }
    internal void RaiseGrace(int by) { _char.Grace = (byte)Math.Clamp(_char.Grace + by, 0, 255); SendStats(); SaveChar(); }
    internal void RaiseWill(int by)  { _char.Will  = (byte)Math.Clamp(_char.Will  + by, 0, 255); SendStats(); SaveChar(); }
    internal void RaiseMaxHp(uint by) { _char.MaxHp += by; SendStats(); SaveChar(); }
    internal void RaiseMaxMp(uint by) { _char.MaxMp += by; SendStats(); SaveChar(); }
    // ---- Chapel divorce's physical-sacrifice penalty (RTK player.baseHealth/baseMagic -= penalty) --------
    internal void LowerMaxHp(uint by) { _char.MaxHp = _char.MaxHp > by ? _char.MaxHp - by : 0; SendStats(); SaveChar(); }
    internal void LowerMaxMp(uint by) { _char.MaxMp = _char.MaxMp > by ? _char.MaxMp - by : 0; SendStats(); SaveChar(); }

    // ---- appearance change (AppearanceAbility; RTK rogue_guild_shaman.lua changeFace/changeGender —
    // "Eyes" isn't ported, out of scope). Face IS a real byte in the 4.95 7-byte appearance form (§8), so
    // unlike hair/beard this is genuinely visible: live-preview mutates _char.Face directly and redraws via
    // SendSelfLook (no save), so a cancelled browse just restores the original value with one more redraw;
    // only a confirmed pick calls SaveChar. Sex change reuses the same pattern, then also re-broadcasts to
    // peers (Snapshot()/ShowPlayer already read _char.Sex/_char.Face live, so no separate wire format needed
    // the way the morph workaround required).
    // Browsing doesn't come through here at all — a candidate face is previewed by drawing the player's own
    // paperdoll in the dialog portrait (Session.DlgMenuFace), which mutates nothing. Only the paid-for pick
    // lands, and it goes out via RefreshAppearance so peers see the new head immediately; before, a bought
    // face only reached other players when something else happened to redraw us (equip, map change, walking
    // out of view and back).
    internal void CommitFace(int face) { _char.Face = (ushort)face; RefreshAppearance(); SaveChar(); }

    // War-paint dye (RTK arena_master.lua / general_npc_funcs.warPaint). ArmorColor is the 0x33 appearance[4]
    // palette byte; HasVisibleArmor mirrors RTK's "you need armor or a coat equipped to see your war paint"
    // check (app[3] is the combined armor/coat slot on 4.95, so a non-zero _char.Armor means something is
    // drawn there to recolor). Setting it redraws self + peers (RTK player:refresh) and persists.
    internal byte CharArmorColor => _char.ArmorColor;
    internal bool HasVisibleArmor => _char.Armor != 0;
    internal void SetArmorColor(byte color) { _char.ArmorColor = color; RefreshAppearance(); SaveChar(); }

    internal bool IsEquipped => _char.Equipment.Count > 0;
    internal int FreeSlotCount => _char.MaxInv - _char.Inventory.Count;

    /// <summary>Unequip everything back into the bag (gender change requires a bare paperdoll — RTK
    /// player:isEquipped() gate). False, unchanged, if the bag doesn't have room for all of it.</summary>
    internal bool StripAllEquipment()
    {
        if (_char.Equipment.Count > FreeSlotCount) return false;
        foreach (var e in _char.Equipment.ToList())
        {
            _char.Equipment.Remove(e);
            SendUnequip(e.Slot);
            var def = Content.ItemById(e.ItemId);
            if (def is not null) { ApplyAppearance(def, equip: false); GiveItem(def, 1, e.Dura, e.CustomName, owner: e.Owner); }
        }
        SendStats();
        MarkDirty();
        return true;
    }

    /// <summary>Flip sex, persist, and redraw self + every co-located peer (same broadcast convention as the
    /// other appearance-affecting flows — equip refresh, mount toggle, morph — all `except: this` since our
    /// own view is refreshed directly above).</summary>
    internal void CommitSexChange()
    {
        _char.Sex = (ushort)(_char.Sex == 0 ? 1 : 0);
        RefreshAppearance();
        SaveChar();
    }

    // ---- class / path + title + trainer spell-learning (RTK warrior_trainer.lua &c.) -------------
    // The character's path is stored as the ClassName string ("Peasant"/"Warrior"/…) — the same field
    // @class/@spells already read — so there's one source of truth; CharClassId maps it to the numeric
    // path id (0 Peasant / 1 Warrior / 2 Rogue / 3 Mage / 4 Poet). RTK's separate class/baseClass split
    // (for 5+ subpaths) isn't modelled: base paths only, so ClassName fully captures it.
    internal int CharClassId => Content.PathIdForClass(_char.ClassName);

    /// <summary>The BASE path this character's class descends from (RTK <c>classdb_path</c>) — a Chung ryong
    /// reads 1 (Warrior). Everything keyed to the base four goes through this: level-up growth
    /// (PathGrowth.csv), the exp table (LevelExp.csv), the learn-cost table and the spell ladders. Peasant
    /// and an unrecognized class both read 0.</summary>
    internal int CharBasePathId => Content.PathBaseOf(Math.Max(0, CharClassId));

    /// <summary>What this character is CALLED right now — class plus rank (Paths.csv PthMark&lt;mark&gt;):
    /// "Warrior" at mark 0, "Il san (W)" at 1; a Ju jak reads "Force" at 1 and "Inferno" at 2. Every place
    /// that SHOWS a class to a player uses this; <see cref="Character.ClassName"/> stays the base name so the
    /// path id, the subpath chat channel and the gear gate keep resolving off one stable string.</summary>
    internal string ClassTitle => ClassTitleOf(_char);

    internal static string ClassTitleOf(Character c)
    {
        int p = Content.PathIdForClass(c.ClassName);
        return p < 0 ? c.ClassName : Content.PathTitle(p, c.Mark);
    }

    internal string CharTitle => _char.Title;

    /// <summary>Set the player's path (RTK <c>updatePath</c>): change the profile class line + persist. We
    /// don't model class-based stat growth, so there's no calcStat step — HP/MP are unchanged.</summary>
    internal void SetCharClass(int pathId) { _char.ClassName = Content.PathName(pathId); SaveChar(); }

    /// <summary>Set the noble title shown above the name / in the profile (RTK <c>setTitle</c>). Persisted;
    /// the new title shows next time the profile is opened.</summary>
    internal void SetCharTitle(string title) { _char.Title = title ?? ""; SaveChar(); }

    /// <summary>Spells this class can learn AT or below the player's level that aren't already known —
    /// the "Learn Secret" menu (RTK <c>learnSpell</c>). Empty if the player has no class.</summary>
    internal List<SpellDef> LearnableClassSpells()
    {
        int p = CharClassId;
        if (p < 0) return new();
        return Content.SpellsForClass(p, _char.Level, _char.Alignment, _char.Mark)
                      .Where(s => !_char.Spells.Contains(s.Id))
                      .Where(s => Content.CanRelearnAtNpc(s, p)).ToList();
    }

    /// <summary>Spells this class will unlock at a HIGHER level (RTK "Divine Secret" preview) — not yet
    /// learnable. Ordered by level. Windowed to the next <paramref name="levelsAhead"/> insights the way RTK's
    /// <c>futureSpells</c> windows its own list (it uses 10; we use 5 per the user), so the preview stays a
    /// "what's next" peek rather than the whole remaining ladder.</summary>
    internal List<SpellDef> FutureClassSpells(int levelsAhead = 5)
    {
        int p = CharClassId;
        if (p < 0) return new();
        return Content.SpellsForClass(p, _char.Level + levelsAhead, _char.Alignment, _char.Mark)
                      .Where(s => s.Level > _char.Level && !_char.Spells.Contains(s.Id))
                      .Where(s => Content.CanRelearnAtNpc(s, p))
                      .OrderBy(s => s.Level).ThenBy(s => s.Name).ToList();
    }

    /// <summary>Does the spellbook hold this spell id?</summary>
    internal bool KnowsSpellId(int spellId) => _char.Spells.Contains(spellId);

    /// <summary>Spells the player currently knows, for the "Forget Secret" menu.</summary>
    internal List<SpellDef> KnownSpellList() =>
        _char.Spells.Select(Content.SpellById).Where(s => s is not null).Select(s => s!).ToList();

    /// <summary>Teach one spell via a trainer (Learn Secret). False if the book is full.</summary>
    internal bool LearnSpellFromNpc(SpellDef sp)
    {
        if (_char.Spells.Contains(sp.Id)) return true;
        if (_char.Spells.Count >= SpellBookCap) return false;
        _char.Spells.Add(sp.Id);
        SendAddSpell(_char.Spells.Count - 1, sp);
        SaveChar();
        return true;
    }

    /// <summary>Forget a single spell (Forget Secret). Removing a mid-book entry shifts every later slot,
    /// so we resync the whole client book to the new list rather than trying to patch one slot.</summary>
    internal void ForgetOneSpell(int spellId)
    {
        int old = _char.Spells.Count;
        if (!_char.Spells.Remove(spellId)) return;
        for (int slot = old - 1; slot >= 0; slot--)
            SendMap(0x18, _gameInc++, new byte[] { (byte)(slot + 1) }, $"removespell(0x18) slot={slot}");
        for (int i = 0; i < _char.Spells.Count; i++)
        {
            var sp = Content.SpellById(_char.Spells[i]);
            if (sp is not null) SendAddSpell(i, sp);
        }
        SaveChar();
    }

    /// <summary>Send the player a status/minitext line (RTK sendMinitext).</summary>
    internal void Notify(string text) => SendMiniText(text);

    /// <summary>Make an NPC speak an over-head bubble to everyone on its map (RTK npc:talk).
    ///
    /// <para>Prefixed "&lt;Name&gt;: " exactly like a player's own speech (see Session.Chat's say path): the
    /// 0x0D bubble carries no speaker field, so the client draws whatever string it is handed and the name
    /// has to be IN the text. Without it, everything an NPC says by voice — the whole "i buy &lt;item&gt;" /
    /// "buy my &lt;item&gt;" / vault-command family, which answers over this channel rather than in a dialog
    /// box — arrived as a bare unattributed line in the chat log.</para></summary>
    internal void NpcBubble(Mob npc, string text) =>
        _world.BroadcastArea(_char.Map, npc.X, npc.Y, SayHalfW, SayHalfH,
            p => p.SpeakEntity(0, npc.Id, Encoding.ASCII.GetBytes($"{npc.Name}: {text}")));

    /// <summary>Is an item (by content key) currently worn?</summary>
    internal bool HasEquipped(string itemKey)
    {
        var def = Content.ItemByKey(itemKey);
        return def is not null && _char.Equipment.Any(e => e.ItemId == def.Id);
    }

    /// <summary>Display name of an item by key (for quest dialog), or the key if unknown.</summary>
    internal string ItemName(string itemKey) => Content.ItemByKey(itemKey)?.Name ?? itemKey;

    /// <summary>Warp the player to a map/tile (RTK player:warp). False (and a gentle note) if the destination
    /// map isn't one the 4.95 client can render, so a quest can't strand the player on a black screen.</summary>
    internal bool Warp(ushort map, ushort x, ushort y)
    {
        if (!Content.TryMap(map, out var dm) || dm is null) { SendLog("You can't reach that place yet."); return false; }
        EnterMap(dm.Id, dm.Xs, dm.Ys, x, y, dm.Name);
        return true;
    }

}
