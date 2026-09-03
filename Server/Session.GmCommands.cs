using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{
    /// <summary>Which tier of the '@' command table this session may reach. Read fresh from
    /// <see cref="StaffAccounts"/> on every command rather than cached at login, so a @reload-driven demotion
    /// takes effect immediately instead of at the offender's next relog. Keyed on the PERSISTED character
    /// name (case-insensitively), which world entry now refuses to invent — so staff status can't be claimed
    /// by connecting with a made-up name.</summary>
    private AccessLevel Access => _enteredWorld ? StaffAccounts.LevelFor(_char.Name) : AccessLevel.Player;

    /// <summary>Full operator access. Testers are deliberately excluded: the three things this gates (the
    /// user-list colour, the death-penalty exemption, the '!'-prefix nudge) are world-facing privileges
    /// rather than tooling, and a tester is an ordinary player as far as the world is concerned.</summary>
    private bool IsGm => Access == AccessLevel.Gm;

    // ---- item GM commands ----

    // "@items [filter]": browse the item registry, fuzzy-ranked by name.
    private void ListItems(CommandArgs a)
    {
        string q = a.Raw;
        var found = Content.SearchItems(q, 15);
        if (found.Count == 0) { Reply(q.Length == 0 ? "no items loaded (check game-data/Items.csv)" : $"no items match \"{q}\""); return; }
        Reply($"items{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count} of {Content.Items.Count}):");
        foreach (var i in found)
            Reply($"  #{i.Id} {i.Name} — {(i.IsEquip ? $"equip(dam {i.Dam}/ac {i.Armor})" : i.IsConsumable ? "use" : "etc")}   (@item {i.Name})");
    }

    // "@coins <n>" (alias "@gold <n>") — add n coins to the purse (updates the HUD + persists). A negative n
    // removes that many, floored at 0; "@coins" alone defaults to +10000. Coins aren't in the item registry
    // (they're a negative item id on the wire), so @item can't grant them — this is the direct GM path.
    private void GiveCoinsCmd(CommandArgs a)
    {
        int amount = 10000;                       // bare @coins is the common case; see the row's help
        if (!a.None && !a.Int(0, out amount)) { Refuse(a.Usage()); return; }

        if (amount >= 0) AwardGold((uint)amount);
        else
        {
            uint take = Math.Min(_char.Coins, (uint)(-(long)amount));
            _char.Coins -= take;
            SendStats(); SaveChar();
        }
        Reply($"Coins: {_char.Coins:N0} (changed by {amount:+#;-#;0}).");
    }

    // "@npc [name|id]" — bare: the switched-off readout below. With a name: find the NPC and jump beside
    // it. Quest testing is NPC-centric, and reaching one used to mean already knowing its map for @warp —
    // this removes the lookup step. An exact name wins outright; an ambiguous fragment lists the matches
    // rather than guessing. A disabled/era-gated NPC still resolves (you land at its empty spot, told so).
    private void NpcCmd(CommandArgs a)
    {
        string q = a.Raw;
        if (q.Length == 0) { NpcToggleCmd(a); return; }

        var matches = (int.TryParse(q, out var id)
            ? Content.Npcs.Where(n => n.Id == id)
            : Content.Npcs.Where(n => n.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count == 0) { Refuse($"no NPC matches \"{q}\"."); return; }

        var npc = matches.FirstOrDefault(n => n.Name.Equals(q, StringComparison.OrdinalIgnoreCase))
                  ?? (matches.Count == 1 ? matches[0] : null);
        if (npc is null)
        {
            Reply($"NPCs matching \"{q}\" ({matches.Count}):");
            foreach (var n in matches.Take(10))
                Reply($"  #{n.Id} {n.Name} — map {n.Map} ({n.X},{n.Y}){(n.Enabled ? "" : " [disabled]")}");
            return;
        }

        if (!Content.TryMap(npc.Map, out var md))
        { Refuse($"{npc.Name} (#{npc.Id}) is on map {npc.Map}, which isn't in the map registry."); return; }
        var (x, y) = ApproachTile(npc.Map, md.Xs, md.Ys, npc.X, npc.Y);
        EnterMap(npc.Map, md.Xs, md.Ys, x, y, md.Name);
        Reply($"{npc.Name} (#{npc.Id}) — {md.Name} (map {npc.Map}) at ({npc.X},{npc.Y})." +
                (npc.Enabled ? "" : "  [disabled — the spot is empty]"));
    }

    // "@npc" / "@npc list" — show which NPCs are switched off. Read-only: toggling an NPC means setting the
    // Enabled column in game-data/NPCs.csv and running @reload (World.ReconcileNpcToggles spawns/despawns
    // to match), not a live GM mutation command. The tavern-hand "small guy" NPCs (Ox/Taur) are off by default.
    //
    // The two ways to be off are reported separately because the fix differs: an era-gated NPC (NPCs.csv
    // EraFeature — Yarlof, who arrives with the 2005 Druid bouquet quest) is absent because he does not exist
    // yet, and no amount of editing the Enabled column will bring him back.
    private void NpcToggleCmd(CommandArgs a)
    {
        static string Describe(NpcDef n) => $"#{n.Id} {n.Name} (map {n.Map})";

        var off = Content.Npcs.Where(n => !n.Enabled).OrderBy(n => n.Id).ToList();
        var unborn = off.Where(n => n.EraFeature.Length > 0 && !Era.Has(n.EraFeature)).ToList();
        var manual = off.Except(unborn).ToList();

        Reply((manual.Count == 0 ? "No NPCs are switched off."
                                   : $"Switched-off NPCs ({manual.Count}): " + string.Join(", ", manual.Select(Describe))) +
                "  (edit the Enabled column in game-data/NPCs.csv + @reload to change)");

        if (unborn.Count > 0)
            Reply($"Not yet in this era ({unborn.Count}): " +
                    string.Join(", ", unborn.Select(n => $"{Describe(n)} [{n.EraFeature}]")) +
                    "  (move EraDate in game-data/ServerTuning.csv — see @era)");
    }

    // "@craft" / "@craft list" — show which crafting skills are era-gated on/off. Read-only: the toggle
    // itself is config, not live GM state — edit game-data/CraftingToggles.csv and run @reload to
    // change it (see Server/CraftingToggles.cs + docs/common/Crafting-Values.md for why Jewelry and Food
    // Preparation/Chef default off).
    private void CraftToggleCmd(CommandArgs a)
    {
        var lines = CraftingToggles.AllSkills
            .Select(s => $"{s}={(CraftingToggles.IsEnabled(s) ? "ON" : "off")}");
        Reply("Crafting skills (edit game-data/CraftingToggles.csv + @reload to change): " +
                string.Join(", ", lines));
    }

    // "@era" — what date the world is pretending it is, and which dated features that includes. Read-only
    // for the same reason as @craft: the target date is deployment config (ServerTuning.csv EraDate), not
    // live GM state, so it moves by editing the file and running @reload. A feature with no row in
    // EraFeatures.csv is always present and deliberately isn't listed — see Server/Era.cs.
    private void EraCmd(CommandArgs a)
    {
        var now = Era.Today;
        if (now is null)
        {
            Reply("Era gating is OFF (EraDate=0 in game-data/ServerTuning.csv) — all dated content is present.");
            return;
        }

        // "from X" / "until X" / "X..Y" / "undated" — the window as declared, independent of whether the
        // current date happens to fall inside it (the ON/off flag already says that).
        static string Window(Shared.EraWindow? w) => w switch
        {
            null                                          => "undated",
            { Introduced: { } i, Retired: { } r }         => $"{i:yyyy-MM-dd}..{r:yyyy-MM-dd}",
            { Introduced: { } i }                         => $"from {i:yyyy-MM-dd}",
            { Retired:    { } r }                         => $"until {r:yyyy-MM-dd}",
            _                                             => "undated",
        };

        var lines = Era.KnownFeatures.Select(f =>
            $"{f}={(Era.Has(f) ? "ON" : "off")} ({Window(Era.Window(f))})");
        Reply($"Era date {now.Value:yyyy-MM-dd} — {string.Join(", ", lines)}  " +
                "(edit EraDate in game-data/ServerTuning.csv + @reload to change)");
    }

    // "@clock [0-23 | real]" — read or pin the shared in-game hour. The calendar otherwise derives strictly
    // from the real-world epoch (World.SyncClock), so the totem-time window — the thing @exp kill exists to
    // exercise — was only testable when the real clock happened to land in it. Pinning is WORLD-scoped (the
    // hour is one shared value; every session's 0x20 clock follows within a tick) and hour-only: day, season
    // and year keep deriving, because nothing behavioral hangs off them. `real` releases the pin.
    private void ClockCmd(CommandArgs a)
    {
        if (!a.None)
        {
            if (a.Is(0, "real")) _world.SetHourOverride(null);
            else if (a.Int(0, out var h) && h is >= 0 and <= 23) _world.SetHourOverride(h);
            else { Refuse(a.Usage()); return; }
            Log.Info($"   -> @clock '{_char.Name}': {(a.Is(0, "real") ? "released" : $"hour pinned to {a.Word(0)}")}");
        }

        var (hour, day, year) = _world.ClockNow;
        var totems = string.Join(", ", Enumerable.Range(0, 4)
            .Where(t => Content.IsTotemTime(hour, t)).Select(Content.TotemName));
        Reply($"In-game time: hour {hour} — day {day} of {_world.SeasonName}, Yuri {year}. " +
                $"Totem time: {(totems.Length > 0 ? totems : "none")}." +
                (_world.HourOverride is not null ? $"  [hour pinned — {Prefix}clock real to release]" : ""));
    }

    // "@killtrack [clear]" — the eight-slot kill track, most-recent-first, which is what the mythic
    // alliances count (NOT the lifetime tally). Without this there is no way to see WHY a hand-in was
    // refused: a boss that has been pushed off the end looks exactly like a boss that was never killed.
    // `clear` wipes it, which is what accepting an alliance does. See Server/MythicAlliance.cs.
    private void KillTrackCmd(CommandArgs a)
    {
        if (a.Is(0, "clear"))
        {
            ClearKillTrack();
            Reply("Kill track cleared (this is what accepting a mythic alliance does).");
            return;
        }

        var rows = KillTrackRows;
        if (rows.Count == 0) { Reply("Kill track is empty."); return; }

        var lines = rows.Select((e, i) => $"{i + 1}. {Content.MobByKey(e.Mob)?.Name ?? e.Mob} x{e.Count}");
        Reply($"Kill track ({rows.Count}/{KillTrack.Slots} kinds, newest first): " + string.Join(", ", lines));
    }

    private void GiveItemCmd(CommandArgs a)
    {
        if (a.None) { Refuse(a.Usage()); return; }
        // A trailing number is the AMOUNT, not part of the name — but only a positive one, so "@item Rice -1"
        // still looks for an item called "Rice -1" rather than granting a negative pile.
        int amount = 1;
        if (a.NameThenTrailingInt(out string q, out int n) && n > 0) amount = n; else q = a.Raw;
        var def = Content.FindItem(q);
        if (def is null) { Refuse($"no item matches \"{q}\" — try  {Prefix}items {q}"); return; }
        if (def.Stackable) GiveItem(def, amount);
        else for (int i = 0; i < amount; i++) if (!GiveItem(def)) break;
        Reply($"Gave {def.Name}{(amount > 1 ? $" x{amount}" : "")} (#{def.Id}, {(def.IsEquip ? $"equip slot {def.EquipSlot}" : def.IsConsumable ? "use" : "etc")}).");
    }

    // "@take <name|id> [amount|all]" — remove an item from the BAG (worn gear is untouched: unequip first).
    // The single-item cleanup @clearinv is too blunt for — testing "the NPC takes your item" without nuking
    // the rest of the pack. Goes through TakeItem, the same removal path quests use, so stacks drain low
    // slots first and every touched slot is redrawn. Asking for more than you hold takes all of them.
    private void TakeItemCmd(CommandArgs a)
    {
        if (a.None) { Refuse(a.Usage()); return; }
        // Same trailing-count shape as @item, plus the "all" keyword in the same position.
        int amount = 1;
        string q = a.Raw;
        if (a.Count > 1 && a.Is(a.Count - 1, "all")) { amount = int.MaxValue; q = a.Rest(0, a.Count - 1); }
        else if (a.NameThenTrailingInt(out var name, out var n) && n > 0) { amount = n; q = name; }
        var def = Content.FindItem(q);
        if (def is null) { Refuse($"no item matches \"{q}\" — try  {Prefix}items {q}"); return; }
        int held = CountItem(def.Key);
        if (held == 0) { Refuse($"You aren't carrying any {def.Name}."); return; }
        int take = Math.Min(amount, held);
        TakeItem(def.Key, take);
        Reply($"Took {def.Name}{(take > 1 ? $" x{take}" : "")} — {held - take} left.");
    }

    // "@exp <n> [kill]" — award raw experience through AwardExp, the same funnel every real grant uses, so
    // the whole leveling path runs for real: the exp curve, multi-level carries, the Peasant wall, LevelUp's
    // stat/HP/MP gains. @lvl can't test any of that — it REBUILDS at a level. `kill` marks the grant as kill
    // exp, which is what opts into the 1.05 totem-time bonus (quest-style grants never take it). Bare @exp
    // reports where you stand.
    private void ExpCmd(CommandArgs a)
    {
        // uint, not int: the grant feeds AwardExp, and a negative one has no meaning there.
        if (!uint.TryParse(a.Word(0), out var n) || n == 0)
        { Refuse($"exp is {_char.Exp:N0}. {a.Usage()}"); return; }
        AwardExp(n, killExp: a.Is(1, "kill"));
    }

    // "@dura <name|id> <n>" — set an item's current durability, bag first then worn, clamped to the item's
    // max. The only other way to wear something down is to actually grind it down, which makes repair NPCs
    // and breakage untestable in any reasonable time. Redraws the touched slot so the client shows the new
    // value immediately.
    private void DuraCmd(CommandArgs a)
    {
        // The same name-then-trailing-int shape as @item and @take, but the count is REQUIRED here.
        if (!a.NameThenTrailingInt(out var q, out var n) || n < 0) { Refuse(a.Usage()); return; }
        var def = Content.FindItem(q);
        if (def is null) { Refuse($"no item matches \"{q}\" — try  {Prefix}items {q}"); return; }
        ushort v = (ushort)Math.Min(n, (int)def.Durability);

        var bag = _char.Inventory.FirstOrDefault(i => i.ItemId == def.Id);
        var worn = bag is null ? _char.Equipment.FirstOrDefault(e => e.ItemId == def.Id) : null;
        if (bag is null && worn is null) { Refuse($"You aren't carrying or wearing {def.Name}."); return; }
        if (bag is not null) { bag.Dura = v; SendAddItem(bag); }
        else { worn!.Dura = v; SendEquip(worn); }
        MarkDirty();
        Reply($"{def.Name}: durability {v}/{def.Durability}{(bag is null ? " (worn)" : "")}.");
    }

    // "@clearinv": empty the bag + gear (test reset).
    private void ClearInventory()
    {
        foreach (var it in _char.Inventory.ToList()) SendDelItem(it.Slot, 0);
        _char.Inventory.Clear();
        foreach (var e in _char.Equipment.ToList()) SendUnequip(e.Slot);
        _char.Equipment.Clear();
        if (_char.Weapon != 0 || _char.Armor != 0)
        {
            _char.Weapon = 0; _char.Armor = 0;
            RefreshAppearance();
        }
        Reply("Cleared your pack and gear.");
    }

    // "@icons [start]": ICON-ID RE. Fill every bag slot with a raw 0x0F whose icon id = start+slot, named
    // "f<icon>", so a screenshot shows which client Item.epf frames render (frame index == client item id;
    // this is a DIFFERENT space from the RTK ItmIcon). Sweep with @icons 0, @icons 27, 54, 81, … and match
    // the rendered icons to re/render_items.py's contact sheet to build the RTK-item -> client-frame map.
    private void IconSweep(CommandArgs a)
    {
        int start = a.Int(0, 0);
        _char.Inventory.Clear();
        for (int i = 0; i < _char.MaxInv; i++)
            SendRawIcon((byte)i, (ushort)(start + i), $"f{start + i}");
        Reply($"icons {start}..{start + _char.MaxInv - 1} in bag (match vs render_items.py sheet)");
    }

    // The client's item-sprite resolver (0x435ab0) does `spriteId = iconField + 0x4000`, then the frame
    // indexer (0x431450) bounds-checks the LOW 16 BITS against the Item.epf frame count (1310) — so to
    // render Item.epf frame N (== client item id), the packet icon field must be (N - 0x4000) & 0xFFFF,
    // which wraps back to N after the client's +0x4000. Sending N raw overflows (N+0x4000 >= 1310 → blank).
    private static ushort IconWire(int clientFrame) => (ushort)((clientFrame - 0x4000) & 0xFFFF);

    // Build a 0x0F for a raw client-frame + label with no registry item behind it — for the @icons sweep.
    private void SendRawIcon(byte slot, ushort frame, string label)
    {
        var d = new List<byte> { (byte)(slot + 1) };
        d.AddRange(Be(IconWire(frame)));
        if (_ver == ClientVersion.V533) d.Add(0);          // 5.x icon-color byte (4.95 omits, see SendAddItem)
        var nn = Ascii(label);
        d.Add((byte)nn.Length); d.AddRange(nn);            // display name
        d.Add((byte)nn.Length); d.AddRange(nn);            // base name
        d.AddRange(Be32(1));                               // amount
        d.Add(0); d.AddRange(Be32(0)); d.Add(0);           // stack/dura/protected block
        d.Add(0); d.AddRange(Be(0)); d.Add(0);             // owner len 0 + trailing u16 + u8
        SendMap(0x0F, _gameInc++, d.ToArray(), $"rawicon(0x0F) slot={slot} frame={frame} wire=0x{IconWire(frame):x4}");
    }

    // "@delreason [lo] [hi]" — walk the 0x10 del-item REASON byte and print the line each one narrates, to
    // find one that says nothing. Equipping needs a silent removal: the bag entry can only be cleared by a
    // 0x10 (the equip window is a separate structure), but every reason tried so far speaks — 0/15 "<item>
    // removed.", 2 "You ate", 5 "You shot", 6 "You used". See Content.EquipDelReason.
    //
    // Each step paints a THROWAWAY item into the last bag slot with a raw 0x0F and then deletes it, so the
    // real inventory is never touched and the sweep is safe to run anywhere. The label goes out first, so the
    // transcript reads "reason N:" immediately followed by whatever the client says (or nothing).
    private void DelReasonSweep(CommandArgs a)
    {
        int lo = a.Int(0, 0), hi = a.Int(1, 15);
        lo = Math.Clamp(lo, 0, 255); hi = Math.Clamp(hi, lo, 255);
        byte slot = (byte)(_char.MaxInv - 1);          // last slot: least likely to collide with real gear
        Reply($"0x10 reason sweep {lo}..{hi} — a reason with NO line after it is the silent one.");
        for (int r = lo; r <= hi; r++)
        {
            SendRawIcon(slot, 1, $"reason{r}");
            Reply($"reason {r}:");
            SendDelItem(slot, (byte)r);
            System.Threading.Thread.Sleep(700);
        }
        Reply("sweep done. Set EquipDelReason to a silent reason (or leave it) and @reload.");
        Log.Info($"   -> DELREASON SWEEP {lo}..{hi} on slot {slot}");
    }

    // "@crecol <lookId> [loColor] [hiColor] [step]": spawn the SAME look id across a GRID (12 cols/row,
    // wraps to more rows north) at increasing 0x07 color-byte values (default 0..23 — the client's color
    // byte visibly wraps mod 24, see docs) so every candidate recolor is visible in one screenshot without
    // silently truncating past 12 entries like the old single-row version did.
    private void CreatureColorRow(CommandArgs a)
    {
        int look = a.Int(0, 0);
        int lo = a.Int(1, 0);
        int hi = a.Int(2, 23);
        int step = Math.Max(1, a.Int(3, 1));
        const int cols = 12;
        var es = new List<(uint, ushort, ushort, ushort, byte, byte)>();
        int n = 0;
        for (int c = lo; c <= hi; c += step, n++)
        {
            int col = n % cols, row = n / cols;
            ushort x = (ushort)Math.Clamp(_char.X - 4 + col, 0, _char.MapXs - 1);
            ushort y = (ushort)Math.Clamp(_char.Y - 2 - row * 2, 0, _char.MapYs - 1);
            var mob = new Mob(_nextMobId++, (ushort)look, x, y, $"col{c}", 6) { Dir = 2 };
            _mobs.Add(mob);
            es.Add((mob.Id, (ushort)(0x8000 | look), x, y, (byte)c, (byte)2));
        }
        SendCreatureList(es, rawColor: true);   // sweep tool: send raw colours so the V533 palette remap can't pre-empt the very index it's meant to find
        Log.Info($"   -> CREATURE color row: look {look}, color {lo}..{hi} step {step} ({es.Count} sent, {cols}/row)");
    }

    // "@crow <lo> <hi> [step]": sweep monster look ids lo..hi across a W->E row (one 0x07 packet with
    // up to 12 entries) so one screenshot maps the Monster.epf look-id space. Find squirrel/rabbit here.
    private void CreatureRow(CommandArgs a)
    {
        int lo = a.Int(0, 0);
        int hi = a.Int(1, lo + 11);
        int step = Math.Max(1, a.Int(2, 1));
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        var es = new List<(uint, ushort, ushort, ushort, byte, byte)>();
        int col = 0;
        for (int v = lo; v <= hi && col < 12; v += step, col++)
        {
            ushort x = (ushort)Math.Clamp(_char.X - 4 + col, 0, _char.MapXs - 1);
            var mob = new Mob(_nextMobId++, (ushort)v, x, y, $"c{v}", 6) { Dir = 2 };
            _mobs.Add(mob);
            es.Add((mob.Id, (ushort)(0x8000 | v), x, y, (byte)0, (byte)2));
        }
        SendCreatureList(es, rawColor: true);   // look sweep: raw colours too (see @crecol)
        Log.Info($"   -> CREATURE row: monster look sweep {lo}..{hi} step {step} ({es.Count} sent)");
    }

    // "@mob <look> [hp] [color]": drop one monster on the tile in front of you as a REAL, SHARED world
    // entity — registered with World, streamed to every player whose viewport it enters, and fought by all
    // of them against one authoritative HP pool. Same path as @rabbit / @summon; the difference is that this
    // one takes a bare Monster.tbl look id and needs no registry row, so it can show anything in Monster.epf.
    //
    // It used to be a SESSION-LOCAL dummy (drawn straight to the caller over 0x16, never registered), which
    // meant nobody else could see what a GM spawned — and, more quietly, that it sat outside the _shownMobs
    // bookkeeping every other entity is tracked by. The raw-sprite 0x16 probe that behaviour existed for is
    // still available, under @mobraw.
    //
    // Stationary on purpose (wander: false): these are calibration dummies for melee / sfx / sprite work, and
    // one that wanders off mid-measurement is worthless. Use @summon for a mob with its registry AI.
    private void MobOne(CommandArgs a)
    {
        int look = a.Int(0, 0);
        int hp = a.Int(1, 6);
        int color = a.Int(2, 0);
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        var mob = SummonWorldMob((ushort)look, x, y, $"m{look}", hp,
                                 dir: (byte)((_facing + 2) & 3),   // face the spawner on arrival
                                 color: (byte)color, wander: false);
        Reply($"spawned look {look} (hp {hp}, colour {color}) — everyone on this map can see it");
        Log.Info($"   -> MOB world spawn {mob.Id} look={look} c{color} hp={hp} @({x},{y}) map {_char.Map}");
    }

    // "@mobraw <hi> <lo> [hp]": the OLD @mob — one creature drawn straight to the caller over 0x16, from a
    // RAW 16-bit sprite word rather than a Monster.tbl look id. Kept because 0x16 is a genuinely different
    // client path (its own graphic field, no viewport gate) whose id-space is still unmapped, and it is the
    // only way to poke at it. Session-local by nature: the shared world draws mobs over 0x07, so anything
    // spawned here CANNOT be a world entity. See SendCreature for the divide-by-zero crash it dodges.
    private void MobRaw(CommandArgs a)
    {
        int hi = a.Int(0, 0);
        int lo = a.Int(1, 1);
        int hp = a.Int(2, 6);
        ushort sprite = (ushort)((hi << 8) | (lo & 0xFF));
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SpawnMob(sprite, x, y, $"m{sprite}", hp);
        Reply($"raw sprite 0x{sprite:X4} over 0x16 — visible to you only (use @mob for a shared one)");
    }

    private void MobRow(CommandArgs a)
    {
        int lo = a.Int(0, 1);
        int hi = a.Int(1, lo + 11);
        int step = Math.Max(1, a.Int(2, 1));
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        int col = 0;
        for (int v = lo; v <= hi && col < 12; v += step, col++)
        {
            ushort x = (ushort)Math.Clamp(_char.X - 4 + col, 0, _char.MapXs - 1);
            SpawnMob((ushort)v, x, y, $"g{v}", 6);
        }
        Log.Info($"   -> MOB row: graphic id sweep {lo}..{hi} step {step}");
    }

    private void KillMobs()
    {
        int world = _world.ClearMap(_char.Map);   // shared mobs -> despawned for EVERYONE on this map
        int local = _mobs.Count;                  // session-local debug dummies -> just us
        if (local > 0) { SendDespawn(_mobs.Select(m => m.Id).ToArray()); _mobs.Clear(); }
        if (world + local == 0) { Reply("no mobs to clear"); return; }
        Reply($"cleared {world} world mob(s) + {local} local dummy(s)");
        Log.Info($"   -> KILL: despawned {world} world + {local} local mobs on map {_char.Map}");
    }

    // A small pack of REAL, killable monsters around the player (via 0x07 = Monster.epf). "@spawn
    // [lookId] [hp]" — lookId is the Monster.tbl monster index (0..326); defaults to 0.
    private void SpawnCritters(CommandArgs a)
    {
        int look = a.Int(0, 0);
        int hp = a.Int(1, 6);
        (int dx, int dy)[] spots = { (0, -2), (2, 0), (-2, 0), (0, 2) };
        foreach (var (dx, dy) in spots)
        {
            ushort x = (ushort)Math.Clamp(_char.X + dx, 0, _char.MapXs - 1);
            ushort y = (ushort)Math.Clamp(_char.Y + dy, 0, _char.MapYs - 1);
            SpawnMonster((ushort)look, x, y, $"monster{look}", hp, dir: 2);
        }
        Log.Info($"   -> SPAWN monster pack look={look}");
    }

    // "@ride" / "@mount [0|1]" — toggle (or set) the mounted-on-horse state. Flips appearance[1] to the
    // form byte 3, which makes the client draw the horse+rider composite (SPR 344/345) instead of the human
    // sprite. Re-draws self and every co-located peer in place (same path ApplyAppearance uses for gear).
    private void ToggleMount(CommandArgs a)
    {
        _char.Mounted = a.Toggle(0, _char.Mounted);
        RefreshAppearance();                                              // redraw self on the horse + everyone watching
        Reply(_char.Mounted ? "The powerful steed takes you where you want to go."   // same lines as the
                                   : "You precariously step again onto the ground.");       // real 'r' ride key
        Log.Info($"   -> MOUNT {( _char.Mounted ? "on" : "off")}");
    }

    // "@clip [0|1]" — no-clip, for walking quest routes without fighting the geometry. Session-scoped and
    // always OFF at login (a persisted flag could strand a tester's ordinary character inside a wall on a
    // later login with no staff around). Two layers must move together, because collision is enforced twice:
    // HandleWalk stops refusing the step server-side, and the streamed pass layer goes out as "walkable
    // everywhere" (SendMapRect) so the CLIENT's own local prediction stops refusing it too — without the
    // second half the toggle would be a no-op, since the client never sends a walk it believes is blocked.
    // The re-prime below re-stamps the already-streamed window immediately; every later strip follows suit.
    // Two holes, both accepted: the 5.33 client also collides locally against its own SOBJ.TBL directional
    // flags, which no stream can override without blanking the artwork — so a thin object-wall (hut sides)
    // may still refuse client-side there. And on 4.95 the pass bits ARE the sheet-2 art selector (one word),
    // so blocked terrain draws as the wrong tile while clip is on; it heals the moment clip turns off.
    // Warps deliberately still fire — @clip changes collision, not doorways; a tester walking a quest route
    // still wants portals to carry them. Mob/AI behaviour is untouched: you can share a tile with a mob and
    // it can still hit you.
    private bool _noClip;
    private void ClipCmd(CommandArgs a)
    {
        _noClip = a.Toggle(0, _noClip);
        PrimeViewport("clip");   // re-stamp the visible window so the client's pass layer flips NOW, not next strip
        Reply(SettingLine("No-clip", _noClip));
        Log.Info($"   -> NOCLIP {(_noClip ? "on" : "off")} for '{_char.Name}' at map {_char.Map} ({_char.X},{_char.Y})");
    }

    // "@peace [0|1]" — unprovoked mobs don't notice you: the survey companion to @clip, for walking a
    // hostile map (@showwarps, quest routes) without collecting a train. Session-scoped and always OFF at
    // login, same reasoning as @clip. Switch-on also makes every mob already chasing you let go
    // (World.PacifyPlayer clears target + threat); staying ignored is the aggro-scan exclusions in
    // World.Tick. Deliberately NOT invulnerability: anything you attack re-acquires you through TryDamage
    // and fights back — combat stays testable with the toggle on.
    private bool _peace;
    internal bool PeaceMode => _peace;
    private void PeaceCmd(CommandArgs a)
    {
        _peace = a.Toggle(0, _peace);
        if (_peace) _world.PacifyPlayer(PlayerId);
        Reply(SettingLine("Peace", _peace));
        Log.Info($"   -> PEACE {(_peace ? "on" : "off")} for '{_char.Name}'");
    }

    // "@anywarp [0|1]" — use any walk-onto warp or gated doorway regardless of the destination's
    // requirements. Waives every character-requirement refusal a doorway can make: the Maps.csv entry gate
    // (level/vitals/mark/path and the max-level barrier — TryWarpGate), the WarpQuestLocks.csv quest
    // switches, and the scripted doorways with their own entry rules — mythic zodiac caves, event caves,
    // arena side doors and the path-hall class doors (each waive point lives in that doorway's handler,
    // Session.Movement/Navigation). The checks still RUN; a failing one is echoed as "[anywarp] … would
    // have said: <denial>" instead of refusing, so a tester can verify the gate's verdict while being
    // carried through it. The tiered doorways enter tier 1 when waived — an unqualified character gives the
    // depth picker nothing to read, so the shallowest copy is the predictable choice. Session-scoped and
    // OFF at login for the same reason as @clip: a persisted waiver could follow a tester's ordinary
    // character into normal play. Purely server-side, unlike @clip — the client has no idea entry
    // requirements exist, so there is no second half. The quest-ITEM tiles (the Hermit's door, Sute's
    // sealed cave mouth, the lava row) are waived too — while on, each behaves as plain ground or a plain
    // portal, and NOTHING is spent (the powder and the shoes are kept): the command exists to test the map
    // behind a gate, not the gate's economy, so the mechanic is only narrated, never run.
    private bool _waiveWarpGate;
    private void AnyWarpCmd(CommandArgs a)
    {
        _waiveWarpGate = a.Toggle(0, _waiveWarpGate);
        Reply(SettingLine("Any-warp", _waiveWarpGate));
        Log.Info($"   -> ANYWARP {(_waiveWarpGate ? "on" : "off")} for '{_char.Name}' at map {_char.Map} ({_char.X},{_char.Y})");
    }

    // "@showwarps [0|1]" — overlay a marker on every warp and gated doorway of the current map, visible to
    // THIS session only. Rides the spot-traps marker machinery (Session.WorldApi.SyncGroundItems): each
    // marker is a synthetic GroundItem — ItemId -1 like a coin pile, so it is no registry item, has no name,
    // and can never be picked up (pickup reads the WORLD's floor list, which never holds these) — drawn and
    // hidden by the same viewport reconcile as real floor items, so markers beyond the view rect appear as
    // you walk toward them. Ground items occupy no tile: a marked warp still fires when stepped on.
    // Follows you across maps (EnterMap re-stamps the overlay) until toggled off; session-scoped and OFF at
    // login like @clip/@anywarp. Toggling on also prints the map's doorway list with destinations, with
    // quest-locked warps flagged.
    //
    // "@showwarps look [warpFrame] [doorFrame]" — tune the marker sprites live (re-stamps at once). FRAMES,
    // not colours: 4.95's item graphics path has NO colour channel anywhere — the draw takes only a frame
    // index and pulls the palette from Item.tbl, so a colour variant IS a separate frame (ItemDef.ClientIcon
    // remarks; a first cut of this command sent the 0x07 colour byte and it was silently ignored). So "make
    // it blue" means "find a natively blue frame": run "@icons <start>" to page the frame space in the bag,
    // then "look <frame>" what you found onto the floor — and trust the IN-GAME sweep over a rendered
    // contact sheet: sheet labels don't reliably line up with wire ids and already mis-picked one default.
    // Both kinds default to frame 877, a blue pinwheel confirmed in-game; one shape for both was the
    // operator's call ("doors are basically warps"), and the two-argument form still splits them for anyone
    // who wants warp and doorway told apart. Frames are per-CLIENT art: 877 exists on both shipped clients,
    // but anything found on a 5.33 sheet past 1310 simply does not exist in 4.95's Item.epf.
    private bool _showWarps;
    private ushort _warpMarkFrame = 877;
    private ushort _doorMarkFrame = 877;

    private void ShowWarpsCmd(CommandArgs a)
    {
        if (a.Is(0, "look"))
        {
            // Per-version bound: 4.95's Item.epf has 1310 frames, 5.33's 2304 (counted from the shipped
            // Misc.dat) — an id past the client's own count draws blank, so clamp to the session's client.
            int maxId = _ver == ClientVersion.V533 ? 2303 : 1310;
            if (a.Int(1, out var warpFrame)) _warpMarkFrame = (ushort)Math.Clamp(warpFrame, 0, maxId);
            if (a.Int(2, out var doorFrame)) _doorMarkFrame = (ushort)Math.Clamp(doorFrame, 0, maxId);
            Reply($"Marker look: warp frame {_warpMarkFrame}, doorway frame {_doorMarkFrame} " +
                    $"(find frames with {Prefix}icons <start>; ids run 0..{maxId} on this client).");
            // Say what the re-stamp actually painted: "look <n> did nothing" has already been reported once
            // when every marker in view was the OTHER kind and the changed frame had nothing to redraw.
            if (_showWarps)
            {
                var (w, d) = StampWarpMarkers();
                Reply($"Re-stamped {w} warp + {d} doorway marker(s) on this map.");
            }
            return;
        }

        _showWarps = a.Toggle(0, _showWarps);
        Reply(SettingLine("Show warps", _showWarps));
        if (_showWarps) StampWarpMarkers(list: true);
        else ClearWarpMarkers();
        Log.Info($"   -> SHOWWARPS {(_showWarps ? "on" : "off")} for '{_char.Name}' at map {_char.Map}");
    }

    /// <summary>Stamp the @showwarps overlay for the CURRENT map (replacing any previous overlay). Doorway
    /// sources: Warps.csv, plus every scripted walk-onto doorway with a tile index — mythic zodiac caves,
    /// event caves, arena side doors, path-hall doors, and the Forever Tree crevasse (whose tile is a
    /// literal in TryForeverTreeEntrance, mirrored here). The world-map travel edges are deliberately NOT
    /// marked: they span whole map borders, and a border of diamonds is noise, not signal.</summary>
    private (int warps, int doors) StampWarpMarkers(bool list = false)
    {
        ClearWarpMarkers();
        ushort map = _char.Map;
        var marks = new List<GroundItem>();
        var lines = new List<string>();

        void Mark(ushort x, ushort y, ushort frame) =>
            marks.Add(new GroundItem { Id = _world.AllocateItemId(), ItemId = -1, X = x, Y = y, Graphic = frame });

        foreach (var (from, to) in Content.Warps.Where(w => w.Key.m == map)
                                                .OrderBy(w => w.Key.y).ThenBy(w => w.Key.x))
        {
            Mark(from.x, from.y, _warpMarkFrame);
            string dest = Content.TryMap(to.m, out var dm) ? dm.Name : $"map {to.m}";
            lines.Add($"  ({from.x},{from.y}) -> {dest} ({to.x},{to.y})" +
                      (Content.WarpQuestLocks.ContainsKey((map, to.m)) ? "  [quest-locked]" : ""));
        }
        int warpCount = marks.Count;   // everything added past here is a scripted doorway

        void Door(ushort x, ushort y, string what) { Mark(x, y, _doorMarkFrame); lines.Add($"  ({x},{y}) {what}"); }

        foreach (var (k, cave) in Content.MythicCaveTiles) if (k.Map == map) Door(k.X, k.Y, $"mythic {cave.Animal} cave (tiered)");
        foreach (var (k, cave) in Content.EventCaveTiles)  if (k.Map == map) Door(k.X, k.Y, $"event cave '{cave.Key}' (tiered)");
        foreach (var (k, door) in Content.ArenaDoorTiles)  if (k.Map == map) Door(k.X, k.Y, $"arena door '{door.Label}'");
        if (Content.PathHalls.ContainsKey(map))
        {
            Door(1, 23, "path hall guild door");   Door(2, 23, "path hall guild door");
            Door(8, 1, "path hall sanctum door");  Door(9, 1, "path hall sanctum door");
        }
        if (map == 1002) Door(19, 91, "Forever Tree crevasse");

        lock (_viewLock) _warpMarkers.AddRange(marks);
        SyncGroundItems(_world.ItemsOn(map));   // draw the in-view markers now; the rest appear as you walk

        var counts = (warps: warpCount, doors: marks.Count - warpCount);
        if (!list) return counts;
        if (marks.Count == 0) { Reply("No warps or scripted doorways on this map."); return counts; }
        Reply($"Doorways here ({counts.warps} warp(s), {counts.doors} scripted doorway(s)):");
        const int Cap = 18;   // a screenful; past it the markers themselves are the better map
        Reply(lines.Take(Cap));
        if (lines.Count > Cap) Reply($"  ...and {lines.Count - Cap} more - the markers show them all.");
        return counts;
    }

    /// <summary>Take down the @showwarps overlay: despawn every marker this client actually drew (the
    /// viewport gate means most were never sent) and forget the set. Same shape as ClearTrapMarker.</summary>
    private void ClearWarpMarkers()
    {
        var gone = new List<uint>();
        lock (_viewLock)
        {
            foreach (var m in _warpMarkers) if (_shownItems.Remove(m.Id)) gone.Add(m.Id);
            _warpMarkers.Clear();
        }
        if (gone.Count > 0) SendDespawn(gone.ToArray());
    }

    // The 'r' Ride key (HandleSetting case 0x00): a real RTK-shaped find-a-horse mount, distinct from the
    // @ride/@mount GM toggle above. Mounting requires an actual "horse" mob (MobDef key "horse" — the plain
    // wild horse wandering Buya/Horse Valley, not a combat mob like "wild_horse"/"horse_guardsman" that just
    // shares the word) standing on the SINGLE tile you're facing (cardinal only, same FrontTile() the melee
    // attack uses — RTK has no 8-way/diagonal reach and neither does the player's own swing) and despawns it
    // (ridden away, no loot/exp — see World.DespawnMob). Dismounting sets a fresh horse back down on the
    // first free tile clockwise from the one you face (see DismountTile).
    private void TryRideHorse()
    {
        if (!_char.Mounted)
        {
            var (hx, hy) = FrontTile();
            var horse = _world.MobNear(_char.Map, hx, hy, 0, mo => mo.Key == "horse");   // radius 0 = exact tile
            // The three ride lines are the real game's, verbatim (they aren't in the client's Inter.dat line
            // table, so they were always server-sent — ours were stand-ins). This is the 'r' KEY, not a
            // command, so it sends its own status-pane lines rather than going through Reply.
            if (horse is null) { SendMiniText("Good try, but there is nothing here that you can ride."); return; }
            _world.DespawnMob(_char.Map, horse);
            _char.Mounted = true;
            RefreshAppearance();
            SendMiniText("The powerful steed takes you where you want to go.");
            Log.Info($"   -> MOUNT on (rode away world horse {horse.Id})");
        }
        else
        {
            _char.Mounted = false;
            RefreshAppearance();
            SendMiniText("You precariously step again onto the ground.");

            var def = Content.Mobs.FirstOrDefault(m => m.Key == "horse");
            if (def is not null)
            {
                var (x, y, dir) = DismountTile();
                SummonWorldMob(def.Look, x, y, def.Name, def.Hp, dir: dir,
                                color: def.Color, exp: def.Exp, moveTime: def.MoveTime, key: def.Key, def: def);
                Log.Info($"   -> MOUNT off (set horse down at {x},{y} dir {dir})");
            }
            else Log.Info("   -> MOUNT off (no 'horse' MobDef — nothing set down)");
        }
    }

    /// <summary>Where to set the horse down when dismounting: the first free CARDINAL neighbour, checked
    /// CLOCKWISE from the tile the player faces (faced, right, behind, left — dir, dir+1, dir+2, dir+3 in
    /// the 0=N 1=E 2=S 3=W encoding, which is already clockwise); if all four are taken, the player's OWN
    /// tile. Returns the tile plus the direction the horse should face (always back toward the rider).
    /// Only 4 slots: this game has no diagonal adjacency anywhere — movement, melee reach and mount range
    /// are all cardinal — so a horse must never land on a corner tile.
    /// Free = in bounds, not blocked (<see cref="MapData.BlockedMove"/> — ground pass flag AND the SObj
    /// directional object-wall, the same two-layer test the player's walk uses), and not already holding a
    /// mob or another player.
    /// The old code just clamped the faced tile to the map bounds and dropped the horse there, which put it
    /// inside walls, in water, and on top of whatever already stood in front of you.
    /// Stacking on the rider is the deliberate last resort (same principle as World.FreeSpawnTile's
    /// accept-the-overlap fallback): a boxed-in player must still get their horse back.</summary>
    private (ushort x, ushort y, byte dir) DismountTile()
    {
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        for (int i = 0; i < 4; i++)
        {
            int side = (_facing + i) & 3;                  // i=0 is the faced tile, then clockwise
            var (tx, ty) = Step(_char.X, _char.Y, side);
            if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) continue;
            if (md is not null && md.BlockedMove(tx, ty, side)) continue;
            if (TileHasMob(tx, ty)) continue;
            if (_world.PeerAt(_char.Map, tx, ty) is not null) continue;
            return ((ushort)tx, (ushort)ty, (byte)Opposite(side));   // face back toward the rider
        }
        return (_char.X, _char.Y, (byte)Opposite(_facing));   // fully boxed in — stack it on us
    }

    // "@might N" / "@will N" / "@grace N" — set one BASE character stat so wear-requirements can be exercised
    // on the fabricated bring-up character. The base stats are bytes, so they clamp to 0-255. A later @lvl/
    // @class/@mark/@align recomputes the stats from the class curve and discards whatever was set here — set
    // the class/level first, then poke individual stats. (@stats sets all three plus the pools in one shot.)
    private void SetBaseStat(string which, CommandArgs a)
    {
        byte b = (byte)Math.Clamp(a.Int(0, 0), 0, 255);
        switch (which)
        {
            case "level": _char.Level = (byte)Math.Clamp(a.Int(0, 1), 1, 99); break;
            case "might": _char.Might = b; break;
            case "will":  _char.Will  = b; break;
            case "grace": _char.Grace = b; break;
        }
        if (_enteredWorld) StoreSave();
        SendStats();
        byte now = which switch { "level" => _char.Level, "will" => _char.Will, "grace" => _char.Grace, _ => _char.Might };
        Reply($"{which} set to {now}");
        Log.Info($"   -> {which.ToUpperInvariant()} set to {now}");
    }

    // "@hp <n>" / "@mp <n>" — set the BASE max pool (vita/mana) and top the current value up to the new max.
    // The individual-stat counterpart to @stats' first two arguments; the reply shows the effective max after
    // gear/buffs. Like @might/@will/@grace, a later @lvl/@class/@mark recomputes vitals from the curve and
    // discards this — set the class/level first, then the pools.
    private void SetMaxPool(bool hp, CommandArgs a)
    {
        if (!a.Int(0, out var n)) { Refuse(a.Usage()); return; }
        if (hp) { _char.MaxHp = (uint)Math.Max(1, n); _char.Hp = EffMaxHp; }
        else    { _char.MaxMp = (uint)Math.Max(0, n); _char.Mp = EffMaxMp; }
        if (_enteredWorld) StoreSave();
        SendStats();
        Reply(hp ? $"max HP set to {_char.MaxHp:N0}{(EffMaxHp != _char.MaxHp ? $" ({EffMaxHp:N0} with gear)" : "")}, HP refilled."
                       : $"max MP set to {_char.MaxMp:N0}{(EffMaxMp != _char.MaxMp ? $" ({EffMaxMp:N0} with gear)" : "")}, MP refilled.");
        Log.Info($"   -> {(hp ? "MAXHP" : "MAXMP")} set to {(hp ? _char.MaxHp : _char.MaxMp)}");
    }

    // "@nation <id>" / "@totem <id>" — set the character's kingdom / totem crest and PERSIST it (survives
    // relog), then push the HUD. Distinct from the GM @nat / @totemsweep RE probes, which only flash a crest
    // at the HUD for a single packet without touching the saved character.
    private void SetNationCmd(CommandArgs a)
    {
        // The current value is live state, not a usage string: the shape comes from the table, the number
        // from the character.
        if (!a.Int(0, out var id))
        { Refuse($"{a.Usage()}   (now: {_char.Nation} — {Character.NationName(_char.Nation)})"); return; }
        _char.Nation = (byte)Math.Clamp(id, 0, 255);
        if (_enteredWorld) StoreSave();
        SendStats();
        Reply($"nation set to {_char.Nation} ({Character.NationName(_char.Nation)}).");
        Log.Info($"   -> NATION set to {_char.Nation}");
    }

    private void SetTotemCmd(CommandArgs a)
    {
        if (!a.Int(0, out var id)) { Refuse($"{a.Usage()}   (now: {_char.Totem})"); return; }
        _char.Totem = (byte)Math.Clamp(id, 0, 3);   // 0..3 only — 5.33 clamps out-of-range and then reports a phantom change every stats packet (pane wipe); see TotemWire
        if (_enteredWorld) StoreSave();
        SendStats();
        Reply($"totem set to {_char.Totem}.");
        Log.Info($"   -> TOTEM set to {_char.Totem}");
    }

    // "@karma <value|tier>" — set the hidden virtue score outright. A number sets it exactly (may be negative
    // or fractional); a tier NAME (cat, dog, angel, …) snaps it into that band, using the same ladder every
    // gate reads (see Karma.ValueForName). Persisted like the other character setters. Note karma is NOT on
    // the 4.95 profile (Karma.cs remarks), so this reply is the only feedback — and a later @lvl/@class/@mark
    // rebuild leaves karma alone, unlike the stat curve, so it doesn't get discarded.
    private void SetKarmaCmd(CommandArgs a)
    {
        string arg = a.Raw;
        if (arg.Length == 0)
        {
            Reply($"karma is {_char.Karma:0.###} ({Karma.LevelName(_char.Karma)}).");
            Reply($"{a.Usage()}   tiers: {string.Join(" · ", Karma.TierNames)}");
            return;
        }

        // A tier name wins over number parsing, but no tier name is a number, so there's no ambiguity.
        double value;
        if (Karma.ValueForName(arg) is { } byName) value = byName;
        else if (double.TryParse(arg, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var byNum)) value = byNum;
        else { Refuse($"'{arg}' isn't a number or a karma tier. Tiers: {string.Join(" · ", Karma.TierNames)}"); return; }

        _char.Karma = value;
        if (_enteredWorld) StoreSave();
        SendEffect(_char.Id, Karma.Effect);        // the same sparkle a real karma change plays
        Reply($"karma set to {_char.Karma:0.###} ({Karma.LevelName(_char.Karma)}).");
        Log.Info($"   -> KARMA set to {_char.Karma:0.###} ({Karma.LevelName(_char.Karma)}) by '{_char.Name}'");
    }

    // "@dispel" — strip every buff and debuff currently on you (see DispelSelf). Handy for resetting a test
    // character to a clean baseline between casts, or shaking off a curse/hold applied during a fight.
    private void DispelCmd()
    {
        DispelSelf();
        Reply("All buffs and debuffs removed.");
    }

    // "@die" — lay yourself out exactly as a mob's killing blow would: ghost form plus the real death
    // penalties, since a tester is an ordinary player as far as the world is concerned. Revive with @rez.
    // Reuses the poison-apple lethal path (ItemKill: HP -> 0, push HUD, run Die()).
    private void DieCmd()
    {
        if (IsDead) { Refuse($"You're already down — {Prefix}rez to get back up."); return; }
        ItemKill();
        Log.Info($"   -> @die by '{_char.Name}' on map {_char.Map}");
    }

    // "@carnage <name> [n]" — record carnage victories on an online player (default 1; a negative n takes
    // them away, and the tally never goes below zero). Carnage was a GM-hosted PvP event, so who won it is
    // knowledge only the host has: there is no fight for the server to score. Warrior Sun armor's first step
    // wants two wins (nexusatlas + the tutor guide), and this is what feeds it. Self is allowed by naming
    // yourself, deliberately — an unnamed form would make the most common misuse the easiest one to type.
    private void CarnageWinCmd(CommandArgs a)
    {
        // "name = n" and "name n" both work; the '=' form is here because names can contain spaces.
        string arg = a.Raw, name = arg;
        int add = 1;
        int eq = arg.LastIndexOf('=');
        if (eq >= 0)
        {
            name = arg[..eq].Trim();
            if (!int.TryParse(arg[(eq + 1)..].Trim(), out add)) { Refuse(a.Usage()); return; }
        }
        else if (a.NameThenTrailingInt(out var named, out var n)) { name = named; add = n; }

        if (name.Length == 0) { Refuse(a.Usage()); return; }
        var target = _world.FindPlayer(name);
        if (target is null) { Refuse($"'{name}' isn't online."); return; }

        int now = Math.Max(0, target.QuestCounter(ArmorQuest.CarnageWinsReg) + add);
        target.SetQuestStage(ArmorQuest.CarnageWinsReg, now);
        // Addressed to the TARGET, so it is not a reply to the operator and does not go through Reply —
        // see the channel rule in Commands.cs. The operator's own confirmation is the next line.
        target.SendMiniText(add >= 0 ? "Your victory in the Carnage is recorded."
                                     : "Your Carnage record has been amended.");
        Reply($"{target._char.Name}: {now} carnage victory(ies).");
        Log.Info($"   -> @carnage '{target._char.Name}' {add:+#;-#;0} -> {now}");
    }

    // "@approach <username>" — teleport to an online player: their map, on a free tile beside them (their own
    // tile if they're boxed in). EnterMap is the only reliable self-relocate on 4.95 (a bare 0x04 snaps back —
    // see GoCmd), so this jumps the same way the world-map/leap paths do.
    private void ApproachCmd(CommandArgs a)
    {
        string name = a.Raw;
        if (name.Length == 0) { Refuse(a.Usage()); return; }
        var target = _world.FindPlayer(name);
        if (target is null) { Refuse($"'{name}' isn't online."); return; }
        if (ReferenceEquals(target, this)) { Refuse("You're already right here."); return; }

        // A peer's character is directly reachable — private is type-scoped, and the reader is a Session too
        // (same as LuaHealTarget reading pc._char). No accessor needed.
        ushort map = target._char.Map, xs = target._char.MapXs, ys = target._char.MapYs;
        var (x, y) = ApproachTile(target, map, xs, ys);
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        EnterMap(map, xs, ys, x, y, mapName);
        Reply($"Approached {target._char.Name} on {mapName} at ({_char.X},{_char.Y}).");
        Log.Info($"   -> @approach '{_char.Name}' -> '{target._char.Name}' at map {map} ({x},{y})");
    }

    // "@where [username]" — the read-only half of @approach: report where a player is without going there.
    // Bare @where lists everyone online with their location — the ops "who's where" view the client's own
    // 0x36 user list doesn't give (it shows names, not places).
    private void WhereCmd(CommandArgs a)
    {
        static string Line(Session p)
        {
            string mapName = Content.TryMap(p._char.Map, out var md) ? md.Name : "Nexus";
            return $"{p._char.Name} — {mapName} (map {p._char.Map}) at ({p._char.X},{p._char.Y})";
        }

        string name = a.Raw;
        if (name.Length == 0)
        {
            var all = _world.AllPlayers().OrderBy(p => p._char.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Reply($"Online ({all.Count}):");
            Reply(all.Select(p => "  " + Line(p)));
            return;
        }
        var target = _world.FindPlayer(name);
        if (target is null) { Refuse($"'{name}' isn't online."); return; }
        Reply(Line(target));
    }

    // "@bring <username>" — the inverse of @approach: pull an online player to a free tile beside YOU (your
    // own tile if you're boxed in), via the same EnterMap jump run on the TARGET's session. Also the rescue
    // for someone wedged in geometry. The player is told who moved them, so it doesn't read as a bug.
    private void BringCmd(CommandArgs a)
    {
        string name = a.Raw;
        if (name.Length == 0) { Refuse(a.Usage()); return; }
        var target = _world.FindPlayer(name);
        if (target is null) { Refuse($"'{name}' isn't online."); return; }
        if (ReferenceEquals(target, this)) { Refuse("You're already right here."); return; }

        ushort map = _char.Map, xs = _char.MapXs, ys = _char.MapYs;
        var (x, y) = ApproachTile(this, map, xs, ys);
        string mapName = Content.TryMap(map, out var md) ? md.Name : "Nexus";
        target.EnterMap(map, xs, ys, x, y, mapName);
        target.SendMiniText($"You have been summoned by {_char.Name}.");   // to the TARGET, not a Reply
        Reply($"Brought {target._char.Name} to ({x},{y}).");
        Log.Info($"   -> @bring '{_char.Name}' <- '{target._char.Name}' to map {map} ({x},{y})");
    }

    // "@announce <message>" — say something to every player online on the same 0x0A system channel the
    // restart countdown uses. Deliberately NOT prefixed with the GM's name: this is the server speaking
    // (event notices, "carnage starts in five minutes"), and staff who want to speak as themselves have
    // ordinary chat. The per-session try/catch mirrors RestartSchedule.Announce — one dead socket must not
    // stop the message reaching everyone else.
    private void AnnounceCmd(CommandArgs a)
    {
        if (a.None) { Refuse(a.Usage()); return; }
        int heard = 0;
        foreach (var s in _world.AllPlayers())
        {
            try { s.SystemAnnounce(a.Raw); heard++; }
            catch (Exception e) { Log.Error($"@announce to {s.Remote} threw — the others still hear it", e); }
        }
        Reply($"Announced to {heard} player(s).");
        Log.Info($"   -> @announce '{_char.Name}': \"{a.Raw}\"");
    }

    // First free CARDINAL neighbour of the target (checked N/E/S/W), else the target's own tile (stack).
    // Free = in bounds, not blocked (the same ground+object-wall test the player's walk uses), and holding
    // neither a mob nor another player. The map may not be the one WE'RE on, so all lookups take it explicitly.
    private (ushort x, ushort y) ApproachTile(Session target, ushort map, ushort xs, ushort ys)
        => ApproachTile(map, xs, ys, target._char.X, target._char.Y);

    private (ushort x, ushort y) ApproachTile(ushort map, ushort xs, ushort ys, int tx, int ty)
    {
        var md = MapData.For(map, xs, ys);
        for (int dir = 0; dir < 4; dir++)
        {
            var (nx, ny) = Step(tx, ty, dir);
            if (nx < 0 || ny < 0 || nx >= xs || ny >= ys) continue;
            if (md is not null && md.BlockedMove(nx, ny, dir)) continue;
            if (_world.PeerAt(map, nx, ny) is not null) continue;
            if (_world.MobAt(map, nx, ny) is not null) continue;
            return ((ushort)nx, (ushort)ny);
        }
        return ((ushort)tx, (ushort)ty);
    }

    // "@mark <0-3>" — set the subpath rank (RTK status.mark: 0 base · 1 Il san · 2 Ee san · 3 Sam san) and
    // rebuild the character at it. A rank is levels PAST 99, not an alternative to them, so this FORCES level
    // 99 first: "@lvl 99" is the base class, "@mark 1" is Il san on top of it, and so on. Each rank brings its
    // own stat growth and its own secrets; the base 1-99 book is unchanged underneath.
    //
    // Stops at Sam san — see Content.MaxMark: Spells.csv has no mark-4 or mark-5 rows, so Sa san and Oh san
    // would be a title and a stat bump over nothing.
    //
    // No NPC advances the rank yet, so this is also still the only way to satisfy the other gates that read
    // it: mark-restricted gear (ItmMark), map entry (MapReqMark), unmarked-only doors, and minor-quest
    // eligibility.
    private void SetMark(CommandArgs a)
    {
        // Name the ranks of the character's OWN path, not the generic Il san ladder — a Ju jak's ranks are
        // Force / Inferno / Pandemonium, and telling them otherwise is just wrong.
        int p = Math.Max(0, CharClassId);
        string ladder = string.Join(" · ", Enumerable.Range(1, Content.MaxMark).Select(m => $"{m} {Content.PathTitle(p, m)}"));

        // A missing OR non-numeric argument is the readout, so "@mark" and "@mark soon" both report rather
        // than rebuilding the character at rank 0.
        if (!a.Int(0, out var want))
        {
            Reply($"mark is {_char.Mark} ({ClassTitle}). {a.Usage()} — {ladder}, each on top of level 99.");
            return;
        }
        // Refuse rather than clamp: silently turning "@mark 5" into Sam san would read as a working Oh san.
        if (want > Content.MaxMark)
        {
            Refuse($"{Content.PathTitle(p, Content.MaxMark)} (mark {Content.MaxMark}) is as far as the ranks go — " +
                    $"there are no mark-{Content.MaxMark + 1} spells in the game data yet. Ranks: {ladder}.");
            return;
        }
        RespecTo(99, Math.Max(0, want));
    }

    // "@dog [0|1]" — skip the bark/woof/grrowl chain and hand over (or take back) the Dog Linguist standing.
    // The flag ALONE grants no spells: the Dog itself still teaches those for kills and goods, so a GM testing
    // the teach flow sets the flag, walks to the Dog, and starts where a finished linguist starts.
    //
    // What the flag DOES change is the character rebuild. @lvl / @class / @mark / @align rebuild the book from
    // the entitlement set (Content.RespecSpellSet), and a finished linguist IS entitled to its class's Dog
    // spells at 70 and 99 — so "@dog 1" then "@lvl 99" hands them over, and "@dog 0" then a rebuild takes them
    // back. Before this the rebuild had no idea the Dog set existed and silently forgot every one of them,
    // including spells earned honestly at the Dog.
    //
    // Eligibility is base classes + NPC subpaths only (Content.CanLearnDogSpells), checked by the Dog and by
    // the rebuild alike — said here too, because a PC subpath can hold the legend and still get nothing.
    // "@rez [username]" (tester/GM): bring a target player — or yourself, if no name is given — back to life at
    // full HP/MP. ReviveInPlace drops the ghost form, refills both bars and pushes the HUD — harmless on a
    // living character (just a full heal), so no dead-only guard.
    private void RezCmd(CommandArgs a)
    {
        string name = a.Raw;
        if (name.Length == 0)
        {
            ReviveInPlace(IsDead ? "You have been restored to life." : "You are restored to full health.");
            return;
        }
        var target = _world.FindPlayer(name);
        if (target is null) { Refuse($"'{name}' isn't online."); return; }
        target.ReviveInPlace(target.IsDead ? "You have been restored to life." : "You are restored to full health.");
        Reply($"Restored {target._char.Name} to full health.");
        Log.Info($"   -> @rez '{_char.Name}' -> '{target._char.Name}'");
    }

    private void SetDogFlag(CommandArgs a)
    {
        int p = Math.Max(0, CharClassId);
        bool want = a.Toggle(0, HasDogFlag);      // bare "@dog" toggles

        SetQuestStage(Content.DogFlagReg, want ? 1 : 0);
        SetQuestStage(DogChainReg, want ? DogChainDone : 0);
        if (want) AddLegend($"Dog linguist ({Character.GameDate})", DogChainReg, 3, 128);
        else RemoveLegend(DogChainReg);

        Reply(want
            ? $"Dog Linguist granted — say \"secret\" to your class's Dog to be taught, or {Prefix}lvl " +
              $"{_char.Level} to have the rebuild hand over the Dog spells you qualify for (70 and 99)." +
              (Content.CanLearnDogSpells(p)
                  ? ""
                  : $" NOTE: {Content.PathTitle(p, _char.Mark)} is a PC subpath and will be refused — only the four " +
                    $"base classes and the NPC subpaths (Chung ryong · Baekho · Ju jak · Hyun moo) may learn Dog spells.")
            : $"Dog Linguist cleared; the chain starts over at Mutt. ({Prefix}lvl {_char.Level} to drop the " +
              $"Dog spells from the book.)");
    }

    // "@text [type] [message]" — send yourself one 0x0A line on a chosen channel, or sweep the channels
    // side by side to see which pane and colour each one lands in.
    //
    // This exists because 0x0A's `type` is the server's only handle on WHERE a line appears, we know the
    // meaning of five values out of a byte, and getting it wrong is invisible from this side: the packet
    // goes out, the log says it went out, and the client draws nothing. That is exactly how the Sage's world
    // shout shipped broken — it was on the 0x02 login box, which no in-world widget listens to. A sweep
    // answers "which type is red?" in one cast instead of a rebuild per guess.
    //
    // Only 8 is held out of the sweep: on 5.33 it is a true modal with an OK button and would sit on top of
    // everything after it. (§11g called 2/3/8 all "overlay"; the live sweep found 2 and 3 in the status box,
    // so 2 is swept.) Send 8 on its own with "@text 8" if you want to see it.
    private static readonly ushort[] TextSweepTypes = { 0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13 };

    private void TextChannelCmd(CommandArgs a)
    {
        if (a.None)
        {
            Reply($"-- {Prefix}text sweep: which pane and colour does each 0x0A type use? --");
            // Raw, by number: the swept type IS what the command is asking about, so these must not be
            // routed through Reply — that would answer every one of them on type 3 and sweep nothing.
            foreach (var t in TextSweepTypes)
                SendMiniText($"0x0A type {t,2} -- the quick brown fox jumps over the lazy dog", t);
            Reply($"-- end. {Prefix}text <type> <message> to send one; 8 is a modal OK box and is not " +
                         $"swept. Observed on 5.33: 0 blue, 2/3 status box, 4 RED (sage), 5 light blue " +
                         $"(restarts), 11 blue, 12 green. 4.95 unswept — see docs/5.x §6.11.");
            return;
        }

        if (!ushort.TryParse(a.Word(0), out var type) || type > 255) { Refuse(a.Usage()); return; }

        string msg = a.Count > 1 ? a.Rest(1) : $"0x0A type {type} -- the quick brown fox";
        SendMiniText(msg, type);   // raw, by number — see the sweep above
        Log.Info($"   -> @text '{_char.Name}' type {type}: {msg}");
    }

    // "@sage [0-5]" — set the Share Wisdom rung outright, skipping the Sage's price and his 90-day wait.
    //
    // It exists because the ladder is otherwise UNREACHABLE from the staff tooling and slow by design: the
    // spells are gated to one NPC (Content.IsNpcGrantedOnly), so no @lvl/@class rebuild will ever hand one
    // over on its own, and buying the ladder honestly is 500,000 gold and 360 real days of waiting. A tester
    // who wants to see what rung 3 reaches cannot get there any other way.
    //
    // Sets BOTH halves, because they answer different questions: the spell in the book is what you can cast
    // now, and Content.SageRungReg is what a rebuild hands back afterwards. Setting only the book would mean
    // "@sage 5" followed by "@lvl 99" silently undid itself, which is the exact trap this command was asked
    // for after. Also clears the wait, so the Sage himself will sell the next rung immediately — the point is
    // to test the flow, not to sit out a quarter of a year.
    private void SetSageRung(CommandArgs a)
    {
        int held = Content.SageLadder.Select(Content.SpellByKey)
                          .Select((sp, i) => sp is not null && KnowsSpellId(sp.Id) ? i + 1 : 0)
                          .DefaultIfEmpty(0).Max();
        // A missing OR non-numeric rung reports, like bare "@dog" toggles: the read is the common case.
        if (!a.Int(0, out var asked))
        {
            long left = QuestCounter(Content.SageTimerReg) - NowUnix;
            string name = Content.SageSpellForRung(held) is { } k && Content.SpellByKey(k) is { } s ? s.Name : "none";
            Reply($"Sage rung {held}/{Content.SageLadder.Length} ({name})" +
                    $"; paid-for rung on record: {QuestCounter(Content.SageRungReg)}" +
                    (left > 0 ? $"; next upgrade in {left / 86400}d {left % 86400 / 3600}h." : "; no wait outstanding.") +
                    $"  {a.Usage()} to set it.");
            return;
        }

        int rung = Math.Clamp(asked, 0, Content.SageLadder.Length);

        // One rung at a time, as the ladder itself works — every other rung comes out of the book first.
        foreach (var key in Content.SageLadder)
            if (Content.SpellByKey(key) is { } sp && KnowsSpellId(sp.Id) && Content.SageRungOf(key) != rung)
                ForgetOneSpell(sp.Id);

        SetQuestStage(Content.SageRungReg, rung);
        SetQuestStage(Content.SageTimerReg, 0);

        if (rung == 0)
        {
            Reply($"Sage ladder cleared. The Sage will start you again at {Content.SageLadder[0]} " +
                    $"(map 1230, from the Wilderness at 126,7).");
            Log.Info($"   -> @sage '{_char.Name}' -> rung 0 (cleared)");
            return;
        }

        var want = Content.SpellByKey(Content.SageSpellForRung(rung)!)!;
        if (!KnowsSpellId(want.Id) && !LearnSpellFromNpc(want))
        {
            Refuse($"Your spellbook is full — free a slot and run {Prefix}sage {rung} again.");
            return;
        }

        Reply($"Sage rung {rung}/{Content.SageLadder.Length}: {want.Name}, and the upgrade wait is cleared." +
                (_char.Level < Content.SageLevel
                    ? $"  NOTE: level {_char.Level} is below the Sage's {Content.SageLevel}, so a rebuild " +
                      $"({Prefix}lvl/{Prefix}class/{Prefix}mark/{Prefix}align) will drop it until you are {Content.SageLevel} again."
                    : ""));
        Log.Info($"   -> @sage '{_char.Name}' -> rung {rung} ({want.Key})");
    }

    /// <summary>The Dog Linguist chain's progress key and legend id — one name, as in RTK
    /// (npc_dialog.lua <c>DOG_LEGEND</c>). Stage 4 is the finished chain.</summary>
    private const string DogChainReg = "dog_linguist";
    private const int DogChainDone = 4;

    // "@quest [key] [stage]" — the raw quest registry, readable and writable, so any quest can be re-tested
    // without a purpose-built command per chain. Bare @quest dumps every key the character carries (the int
    // stage machine AND the string registry), which is how a tester DISCOVERS the key in the first place —
    // none of this is visible in-game anywhere else. "@quest <key>" reads one; "@quest <key> <n>" sets it
    // (0 removes the entry outright — same read-back as 0, and the dump stays clean); a non-numeric value
    // sets the STRING registry instead (e.g. the minor-quest selection). Stage meanings are per-quest — see
    // docs/common/Quest-Registry.md for the full catalogue of keys, stages and the legends that pair with
    // them (most chains gate on the LEGEND, not the stage, so a re-test usually needs @legend too).
    private void QuestCmd(CommandArgs a)
    {
        if (a.None)
        {
            if (_char.Quests.Count == 0 && _char.QuestStrings.Count == 0)
            { Reply($"No quest keys set. ({Prefix}quest <key> <stage> to set one; see docs/common/Quest-Registry.md.)"); return; }
            Reply($"quest registry ({_char.Quests.Count} key{(_char.Quests.Count == 1 ? "" : "s")}" +
                    $"{(_char.QuestStrings.Count > 0 ? $" + {_char.QuestStrings.Count} string" : "")}):");
            foreach (var (k, v) in _char.Quests.OrderBy(e => e.Key, StringComparer.Ordinal)) Reply($"  {k} = {v}");
            foreach (var (k, v) in _char.QuestStrings.OrderBy(e => e.Key, StringComparer.Ordinal)) Reply($"  {k} = \"{v}\"");
            return;
        }

        string key = a.Word(0);
        if (a.Count == 1)
        {
            if (_char.Quests.TryGetValue(key, out int cur)) Reply($"{key} = {cur}");
            else if (_char.QuestStrings.TryGetValue(key, out var cs)) Reply($"{key} = \"{cs}\"");
            else Reply($"{key} is not set (reads as stage 0).");
            return;
        }

        string val = a.Rest(1);
        if (int.TryParse(val, out int stage))
        {
            if (stage == 0)
            {
                bool had = _char.Quests.Remove(key) | _char.QuestStrings.Remove(key);
                SaveChar();
                Reply(had ? $"{key} cleared (was set; now reads as stage 0)." : $"{key} was not set — nothing to clear.");
            }
            else
            {
                SetQuestStage(key, stage);
                Reply($"{key} = {stage}.");
            }
        }
        else
        {
            _char.QuestStrings[key] = val;
            SaveChar();
            Reply($"{key} = \"{val}\" (string registry).");
        }
        Log.Info($"   -> @quest '{_char.Name}': {key} <- {val}");
    }

    // "@legend [key] [0 | <icon> <color> <text...>]" — the legend list with its INTERNAL keys showing. The
    // profile window renders only each mark's text; the key (RTK's legend name) is what quests gate on
    // (HasLegend), so this is the only place a tester can see which key a mark answers to. "@legend <key> 0"
    // removes a mark; "@legend <key> <icon> <color> <text...>" (re)creates one — replace-by-key, same as
    // AddLegend everywhere — so a post-quest state can be entered directly with the values from
    // docs/common/Quest-Registry.md. The seeded "Born in …" mark has no key and so can't be addressed here,
    // which doubles as its protection. NOTE: a legend and its quest stage are independent — most chains
    // check the legend, so clearing only the stage usually re-tests nothing.
    private void LegendCmd(CommandArgs a)
    {
        if (a.None)
        {
            Reply($"legend marks ({_char.Legends.Count}):");
            foreach (var l in _char.Legends)
                Reply($"  {(string.IsNullOrEmpty(l.Name) ? "(no key)" : l.Name)}: \"{l.Text}\" (icon {l.Icon}, color {l.Color})");
            return;
        }

        string key = a.Word(0);
        var held = _char.Legends.FirstOrDefault(l => l.Name == key);
        if (a.Count == 1)
        {
            Reply(held is null ? $"{key}: not held."
                                 : $"{key}: \"{held.Text}\" (icon {held.Icon}, color {held.Color})");
            return;
        }

        if (a.Count == 2 && a.Word(1) == "0")
        {
            RemoveLegend(key);
            Reply(held is null ? $"{key} was not held — nothing to remove." : $"{key} removed (\"{held.Text}\").");
            Log.Info($"   -> @legend '{_char.Name}': removed {key}");
            return;
        }

        if (a.Count >= 4 && byte.TryParse(a.Word(1), out byte icon) && byte.TryParse(a.Word(2), out byte color))
        {
            string body = a.Rest(3);
            AddLegend(body, key, icon, color);
            Reply($"{key} {(held is null ? "added" : "replaced")}: \"{body}\" (icon {icon}, color {color}).");
            Log.Info($"   -> @legend '{_char.Name}': {key} <- icon {icon} color {color} \"{body}\"");
            return;
        }

        Refuse(a.Usage());
    }

    // "@class <name>" — set the class/path and rebuild the character as one. `Character.ClassName` stores the
    // BASE name and is the single source of truth for the path id (Content.PathIdForClass), which drives spell
    // learning, the ItmPthId gear restriction and the subpath chat channel; what a player SEES is
    // ClassTitle (base + rank), so the stored string stays stable while the displayed one changes with @mark.
    //
    // ACCEPTS RANK NAMES TOO. "@class Inferno" is Ju jak at mark 2, "@class Il san (W)" is Warrior at mark 1 —
    // the rank titles are what a character is actually called, so refusing them would mean the name shown in
    // the profile is one you can't type back. A base name leaves the current rank alone.
    //
    // RESTRICTED to Content.PlayablePaths: the four base classes, Peasant, and the four NPC subpaths (Chung
    // ryong / Baekho / Ju jak / Hyun moo). RTK's Paths.csv also lists a GM branch and twelve PC subpaths, none
    // of which this server models — accepting one would produce a character with a rank ladder and no spells
    // behind it. NPC SUBPATHS FORCE LEVEL 99, for the same reason @mark does: you subpath at the cap, and the
    // subpath's own spell is pinned there.
    //
    // The rebuild keeps the current level and rank and re-derives everything else from the new class: stats
    // follow the new path's growth curve (a Warrior turned Mage loses the warrior HP roll and gains the mage
    // MP one) and the book becomes the new class's. Without that, "@class Mage" left a Warrior's HP, a
    // Warrior's skills, and a class line that no longer described either.
    private void SetClass(CommandArgs a)
    {
        string name = a.Raw;
        if (name.Length == 0)
        {
            Reply($"class is '{ClassTitle}' ({a.Usage()})");
            Reply($"  {string.Join(" · ", Content.PlayablePathNames())}  — or any rank title up to Sam san " +
                    $"(Il san (W), Fury, Inferno, …)");
            return;
        }
        if (Content.PathRankForName(name) is not { } pick)
        { Refuse($"'{name}' isn't a known class or rank. Try: {string.Join(" · ", Content.PlayablePathNames())}"); return; }
        if (!Content.IsPlayablePath(pick.PathId))
        {
            Refuse($"'{Content.PathName(pick.PathId)}' isn't playable here — this server models the base classes and the " +
                    $"NPC subpaths only. Try: {string.Join(" · ", Content.PlayablePathNames())}");
            return;
        }

        if (pick.Mark > Content.MaxMark)
        {
            Refuse($"'{name}' is rank {pick.Mark}, past the {Content.PathTitle(pick.PathId, Content.MaxMark)} cap — " +
                    $"there are no mark-{Content.MaxMark + 1} spells in the game data yet. " +
                    $"Try  {Prefix}class {Content.PathTitle(pick.PathId, Content.MaxMark)}.");
            return;
        }

        bool npcSubpath = Content.PathBaseOf(pick.PathId) != pick.PathId;
        int mark  = pick.Mark > 0 ? pick.Mark : Math.Min((int)_char.Mark, Content.MaxMark);   // a rank name sets the rank; a base name keeps it
        int level = npcSubpath || mark > 0 ? 99 : _char.Level;            // subpaths and ranks live at the cap

        _char.ClassName = Content.PathName(pick.PathId);
        RespecTo(level, mark);
        SendSelfProfile();
    }

    // ---- stats/HUD probe lab ----
    // The 4.95 self-stats opcode is unknown (0x08 is a no-op here, unlike 7.x). Static RE narrowed
    // the candidates but can't confirm which opcode drives the persistent HUD. So we probe live:
    // "@s <hexop> [hexflags]" fires a 7.x-shaped status packet full of unmistakable SENTINEL values
    // on the given opcode; whichever opcode makes the HUD numbers change is the stats opcode. Once
    // found, we decode the exact field layout by varying one sentinel at a time (look-lab style).
    // Sentinels chosen to be visually unmistakable and distinct from each other:
    //   level=99  might=11 will=22 grace=33  maxHP=1000 maxMP=500  hp=987 mp=456  exp=54321 coins=777
    private void StatProbe(CommandArgs a)
    {
        byte op = a.Hex(0, out var wantOp) ? wantOp : (byte)0x08;
        byte flags = a.Hex(1, out var wantFlags) ? wantFlags : (byte)0xFF;
        SendStatProbe(op, flags, level: 99);
        Log.Info($"   -> STAT PROBE op=0x{op:x2} flags=0x{flags:x2}");
    }

    // "@batch" — fire the sentinel-laden status probe at a CURATED SAFE set of opcodes (no resource
    // loaders like 0x2e, no risky memcpy/spawn), ~700ms apart with a bubble label. Paired with the
    // probe's whole-memory sentinel scan, one run reveals which opcode (if any) STORES the stats — no
    // matter where the client keeps them. Watch the HUD too and note any opcode that changes a number.
    private void StatBatch(CommandArgs a)
    {
        byte[] safe = { 0x11, 0x12, 0x1d, 0x1f, 0x1b, 0x21, 0x29, 0x2f, 0x30, 0x31,
                        0x35, 0x36, 0x42, 0x46, 0x59, 0x34, 0x39 };
        Log.Info($"   -> STAT BATCH over {safe.Length} opcodes");
        foreach (var op in safe)
        {
            SendSpeech(0, _char.Id, Encoding.ASCII.GetBytes($"op 0x{op:x2}"));
            SendStatProbe(op, 0xFF, level: 99);
            System.Threading.Thread.Sleep(700);
        }
        SendSpeech(0, _char.Id, "batch done"u8.ToArray());
        Log.Info("   -> STAT BATCH done");
    }

    // "@r6 [hexop]" — replay the EXACT stats packet captured from a real 6.x server (jeedee/TkServer
    // game_server.rb), decrypted with the shared NexonInc cipher. 6.x uses opcode 0x08 for stats; this
    // is a valid low-level character: level=1, maxHP=51, maxMP=33, might/will/grace=3. If the 4.95 HUD
    // populates, 0x08 is (still) the stats opcode here; if not, its opcode shifted and we match this
    // KNOWN-GOOD layout against 4.95 handlers. Default op 0x08; pass another hex op to try the layout
    // on a different opcode.
    private static readonly byte[] Stats6xFull =
    {
        0x78,                               // flags (full)
        0x00, 0x00, 0x00, 0x00,             // unk, nation, totem, unk
        0x01,                               // level = 1
        0x00, 0x00, 0x00, 0x33,             // maxHP u32BE = 51
        0x00, 0x00, 0x00, 0x21,             // maxMP u32BE = 33
        0x03, 0x03, 0x03, 0x03, 0x03,       // might, will, ?, ?, grace
        0x00, 0x00,
        0x63, 0xdf, 0x9c, 0x5f,             // (captured; ac/exp-ish region)
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x33, 0x00, 0x00, 0x00, 0x21,       // repeat 51/33 -> current HP/MP block
        0x00, 0x00, 0x00, 0x01,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0xb3, 0x3d, 0x00, // trailing settings/flags
    };

    // "@stg" — self-describing GRADIENT stats packet on 0x08 (the confirmed 4.95 stats opcode). Body
    // byte[i] = i, so every HUD number reveals its own field offset: a byte field shows its offset; a
    // u32 field shows 0xNN.. from which the offset AND endianness fall out. Flags kept at 0x78 (the
    // captured 6.x "full" value that lit every field). One read maps the entire 4.95 layout.
    private void StatGradient(CommandArgs a)
    {
        var d = new byte[60];
        d[0] = 0x78;                                   // flags (full-stats)
        for (int i = 1; i < d.Length; i++) d[i] = (byte)i;
        SendMap(0x08, _gameInc++, d, "stat-gradient(0x08)");
        Log.Info("   -> STAT GRADIENT on 0x08 (body[i]=i); read each HUD number = that field's offset");
    }

    // "@mailflag <off> [valHex]" — pin the 0x08 mail/parcel NOTIFICATION byte (the HUD arrow / arrow+bag
    // the real 4.x client shows when you have n-mail / a parcel waiting at the postmaster).
    //
    // RTK's clif_sendstatus (map/clif.c) puts `sd->flags` in the ALWAYS-ON tail of the 0x08 status packet,
    // documented in-source as: 1 = New parcel, 16 = New Message (n-mail), 17 = both. That tail rides the
    // SAME 0x78 "full" form our SendStats already sends — it's just sitting in our currently-zero [40..]
    // region. The exact 4.95 body offset differs from RTK 6.x/7.x (version-shifted ~1 byte), so sweep it:
    // send our real full-stats body with byte[off]=val and watch the HUD. When the arrow (and bag, since
    // 0x11 sets both bits) lights up, `off` is the notify byte. Default val 0x11 = mail+parcel.
    //   @mailflag 51        -> set body[51]=0x11, look for the arrow+bag
    //   @mailflag 51 10     -> body[51]=0x10 (n-mail only), 01 = parcel only, 00 = clear
    // Move (which re-sends clean stats) or `@mailflag 51 00` to clear. Purely a client-render probe; sets
    // one otherwise-unused byte, so it can't corrupt a real stat field.
    private void MailFlagProbe(CommandArgs a)
    {
        if (!a.Int(0, out int off) || off < 0 || off > 79) { Refuse(a.Usage()); return; }
        byte val = a.Hex(1, out var wantVal) ? wantVal : (byte)0x11;

        // Rebuild the exact full-stats body SendStats sends (so every real HUD field stays correct), just
        // longer, then stamp the candidate notify byte.
        var eq = Totals();
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + eq.hp);
        uint maxMp = (uint)Math.Max(0, (int)_char.MaxMp + eq.mp);
        var d = new byte[Math.Max(58, off + 1)];
        d[0] = 0x78;
        d[1] = _char.Nation; d[2] = TotemWire(); d[4] = _char.Level;   // TotemWire: 5.33 clamps 4->3, so send 0xFF — see SendStats
        WriteBe32(d, 5, maxHp); WriteBe32(d, 9, maxMp);
        d[13] = (byte)Math.Clamp(_char.Might + eq.might, 0, 255);
        d[14] = (byte)Math.Clamp(_char.Will  + eq.will,  0, 255);
        d[17] = (byte)Math.Clamp(_char.Grace + eq.grace, 0, 255);
        WriteBe32(d, 24, _char.Hp); WriteBe32(d, 28, _char.Mp);
        WriteBe32(d, 32, _char.Exp); WriteBe32(d, 36, _char.Coins);
        d[off] = val;
        SendMap(0x08, _gameInc++, d, $"mailflag off={off} val=0x{val:x2}");
        Reply($"0x08 with body[{off}]=0x{val:x2} (0x11=mail+parcel). See an arrow/bag on the HUD?");
        Log.Info($"   -> MAILFLAG probe: 0x08 body[{off}]=0x{val:x2}");
    }

    private void StatReplay6x(CommandArgs a)
    {
        byte op = a.Hex(0, out var wantOp) ? wantOp : (byte)0x08;
        SendMap(op, _gameInc++, Stats6xFull, $"replay6x-stats(0x{op:x2})");
        Log.Info($"   -> REPLAY 6.x stats on op=0x{op:x2} (expect HUD: level 1, HP 51, MP 33, might/will/grace 3)");
    }

    // "@sweep" is DISABLED. Blind-sweeping unknown opcodes can crash the client by feeding a real handler a
    // mis-framed body (exactly how 0x2e/the world-map screen "crashed" until a one-byte framing bug in our
    // OWN packet was found -- see SendWorldMap §11m; that was never a client bug). Find the stats opcode
    // deterministically instead (self player object is [world+0x40c]); only fire "@s <op>" once a specific
    // opcode is confirmed safe by reading its
    // handler.
    private void StatSweep(CommandArgs a)
    {
        Refuse("@sweep is disabled (crashes the client on resource-loading opcodes). Use @s <hexop>.");
        Log.Info("   -> @sweep refused (unsafe blind probe)");
    }

    // Build a 7.x-style status packet (flags byte then FULLSTATS/HPMP/XPMONEY/ALWAYS blocks) with the
    // given opcode, flags, and level sentinel. Layout mirrors Mithia clif_sendstatus so that if the
    // 4.95 handler is structurally similar, recognizable numbers land on the HUD.
    private void SendStatProbe(byte op, byte flags, byte level)
    {
        var d = new List<byte> { flags };
        // FULLSTATS block
        d.Add(0);                    // unknown
        d.Add(_char.Nation);         // nation
        d.Add(_char.Totem);          // totem
        d.Add(0);                    // unknown
        d.Add(level);                // level (sentinel)
        d.AddRange(Be32(1000));      // maxHP
        d.AddRange(Be32(500));       // maxMP
        d.Add(11);                   // might
        d.Add(22);                   // will
        d.Add(3); d.Add(3);          // (7.x constants)
        d.Add(33);                   // grace
        d.Add(0); d.Add(0);
        d.Add(0);                    // AC
        d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0);
        d.Add(_char.MaxInv);         // maxinv
        // HPMP block
        d.AddRange(Be32(987));       // hp
        d.AddRange(Be32(456));       // mp
        // XPMONEY block — zero-free distinctive sentinels so a memory scan finds the STORED copy cleanly
        d.AddRange(Be32(0x11223344));  // exp   -> wire 11 22 33 44 ; stored LE 44 33 22 11
        d.AddRange(Be32(0x55667788));  // coins -> wire 55 66 77 88 ; stored LE 88 77 66 55
        d.Add(50);                   // exp %
        // ALWAYS block
        d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0);
        d.AddRange(Be32(0));         // settingFlags
        SendMap(op, _gameInc++, d.ToArray(), $"statprobe(0x{op:x2})");
    }

    // "@wmpos <i> <x> <y>" — live-tune destination i's clickable dot to field10 pixel (x,y) and re-open the
    // map so you can eyeball it against the real town. i is the index in Content.WorldDests (0=Kugnae,
    // 1=Buya, 2=Mythic Nexus, 3=Arctic Land, 4=KaMing's). The tweak is an ephemeral in-session override
    // (WorldDotOverride); once happy, bake the number into game-data/WorldMapDests.csv (DotX/DotY) +
    // @reload. "@wmpos" with no args lists the effective positions. See §11m.
    private void WorldMapPosCmd(CommandArgs a)
    {
        var dests = Content.WorldDests;
        (int X, int Y) DotOf(int i) => WorldDotOverride.TryGetValue(i, out var ov) ? ov : (dests[i].DotX, dests[i].DotY);
        if (a.Int(0, out var wi) && a.Int(1, out var wx) && a.Int(2, out var wy)
            && wi >= 0 && wi < dests.Count)
        {
            WorldDotOverride[wi] = (Math.Clamp(wx, 0, 639), Math.Clamp(wy, 0, 479));
            var dot = DotOf(wi);
            Reply($"{wi} {dests[wi].Name} -> ({dot.X},{dot.Y})  [bake into WorldMapDests.csv + {Prefix}reload]");
            SendWorldMap("field10");
        }
        else
            for (int k = 0; k < dests.Count; k++)
            {
                var dot = DotOf(k);
                Reply($"{k} {dests[k].Name}: ({dot.X},{dot.Y})");
            }
    }

    // "@wmtest [name]" — native world-map screen (§11m) with an explicit background name (defaults to
    // field10 = "Map of the Kingdom", the overview world-map art). The framing bug that used to crash this
    // is fixed; this stays as a way to try alternate backgrounds (field1, title, other fieldNN).
    private void WorldMapTestCmd(CommandArgs a) => SendWorldMap(a.None ? "field10" : a.Raw);

    /// <summary>"@stats &lt;vita&gt; &lt;mana&gt; &lt;all&gt;" or "@stats &lt;vita&gt; &lt;mana&gt;
    /// &lt;might&gt; &lt;grace&gt; &lt;will&gt;" — set the vitals and the three base stats outright, for
    /// setting up a test character without grinding a level curve. Two arguments sets the pools alone.
    ///
    /// This is the DIRECT counterpart to <c>@lvl</c>: that one resets to the level-1 baseline and re-applies
    /// real LevelUps so every number is legitimate for the level, which is what you want to test progression
    /// — and is exactly wrong when you just want 50k vita to stand in front of a boss. Note the two fight
    /// each other by design: a later @lvl (or a @class/@mark resync) recomputes vitals from the curve and
    /// discards whatever was set here. Set the class/level first, then @stats.
    ///
    /// Might/grace/will are bytes on the character, so they clamp to 0-255; the pools are u32 and clamp to
    /// at least 1 vita (a max HP of 0 would make the character permanently dead). HP/MP are refilled to the
    /// new maxima, gear bonuses included.</summary>
    private void SetStatsCmd(CommandArgs a)
    {
        // Positional, so every word has to BE a number: "@stats 1 2 x" is a three-argument call with a bad
        // third argument, not a two-argument one. ParseInts used to drop the "x" and quietly run the short
        // form.
        if (a.Count is not (2 or 3 or 5) || !a.Int(0, out var vita) || !a.Int(1, out var mana))
        {
            Refuse(a.Usage());
            Refuse($"  now: vita {_char.MaxHp:N0}, mana {_char.MaxMp:N0}, might {_char.Might}, " +
                    $"grace {_char.Grace}, will {_char.Will}");
            return;
        }

        _char.MaxHp = (uint)Math.Max(1, vita);
        _char.MaxMp = (uint)Math.Max(0, mana);
        if (a.Count == 3)
            _char.Might = _char.Grace = _char.Will = (byte)Math.Clamp(a.Int(2, 0), 0, 255);
        else if (a.Count == 5)
        {
            _char.Might = (byte)Math.Clamp(a.Int(2, 0), 0, 255);
            _char.Grace = (byte)Math.Clamp(a.Int(3, 0), 0, 255);
            _char.Will  = (byte)Math.Clamp(a.Int(4, 0), 0, 255);
        }

        _char.Hp = EffMaxHp;                  // top up to the new maxima, gear/buffs included
        _char.Mp = EffMaxMp;
        if (_enteredWorld) StoreSave();
        SendStats();
        // Report BASE and EFFECTIVE separately: what you set is the base, but the HUD shows base + gear +
        // buffs, so "@stats 50000 …" reading back as 50,400 on screen is equipment, not a rounding bug.
        var eq = Totals();
        Reply($"base: vita {_char.MaxHp:N0}, mana {_char.MaxMp:N0}, might {_char.Might}, grace {_char.Grace}, will {_char.Will}.");
        if (EffMaxHp != _char.MaxHp || EffMaxMp != _char.MaxMp || eq.might != 0 || eq.grace != 0 || eq.will != 0)
            Reply($"with gear: vita {EffMaxHp:N0}, mana {EffMaxMp:N0}, might {_char.Might + eq.might}, " +
                    $"grace {_char.Grace + eq.grace}, will {_char.Will + eq.will}.");
        Log.Info($"   -> {Prefix}stats: hp {_char.MaxHp} mp {_char.MaxMp} " +
                 $"M{_char.Might}/G{_char.Grace}/W{_char.Will}");
    }

    // "@pkt <op> [token...]" — put an ARBITRARY server->client packet on the wire. Every undecoded opcode
    // used to need its own throwaway command before it could be poked once; this replaces that whole class
    // of one-offs. Tokens (whitespace-separated):
    //   xx      one raw hex byte                       "0a", "ff"
    //   #n      u16 big-endian, decimal                "#300"
    //   %n      u32 big-endian, decimal                "%3600"
    //   :text   ASCII bytes, no length prefix
    //   $text   ASCII bytes behind a u16BE length      (the shape most string fields here want)
    // In ':'/'$' an underscore means a space, so a string stays ONE token and fields can still follow it —
    // several of these packets put a length or a level AFTER the text. No opcode is filtered: some of them
    // do crash the client, and finding out which is the point.
    private void RawPacketCmd(CommandArgs a)
    {
        if (a.None)
        {
            // The shape comes from the table; what each sub-form DOES does not fit in an Args column.
            Reply(a.Usage());
            Reply($"  {Prefix}pkt add <tokens>   append to the pending packet (the chat box is short)");
            Reply($"  {Prefix}pkt send <hexop>   send what's pending, then clear it");
            Reply($"  {Prefix}pkt show | clear   inspect or drop the pending bytes");
            Reply($"  {Prefix}pkt file <name>    send game-data/packets/<name>.txt (';' starts a comment)");
            return;
        }

        // Long packets can't be typed in one line, so tokens accumulate into _pktPending across several
        // commands and go out on "send". "file" is the same parser over a file, for anything worth keeping.
        switch (a.Word(0).ToLowerInvariant())
        {
            case "add":
                if (!ParsePacketTokens(a.Words(1), _pktPending)) return;
                Reply($"pending {_pktPending.Count}B: {Convert.ToHexString(_pktPending.ToArray()).ToLowerInvariant()}");
                return;
            case "clear":
                _pktPending.Clear();
                Reply("pending packet cleared.");
                return;
            case "show":
                Reply(_pktPending.Count == 0 ? "nothing pending."
                    : $"pending {_pktPending.Count}B: {Convert.ToHexString(_pktPending.ToArray()).ToLowerInvariant()}");
                return;
            case "send":
                if (!a.Hex(1, out byte pendOp)) { Refuse(a.Usage()); return; }
                SendRawPacket(pendOp, _pktPending.ToArray());
                _pktPending.Clear();
                return;
            case "file":
                if (a.Count < 2) { Refuse(a.Usage()); return; }
                SendPacketFile(a.Word(1));
                return;
        }

        if (!a.Hex(0, out byte op))
        {
            Refuse($"'{a.Word(0)}' is not a hex opcode.");
            return;
        }
        var oneShot = new List<byte>();
        if (ParsePacketTokens(a.Words(1), oneShot)) SendRawPacket(op, oneShot.ToArray());
    }

    /// <summary>Bytes accumulated by "@pkt add", flushed by "@pkt send". Per-session so two GMs building
    /// different probes can't scribble on each other.</summary>
    private readonly List<byte> _pktPending = new();

    /// <summary>Append one packet's worth of tokens to <paramref name="body"/>. False (and a message to the
    /// player) on the first token that doesn't parse, leaving whatever parsed before it in place.</summary>
    private bool ParsePacketTokens(string[] parts, List<byte> body)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            string t = parts[i];
            if (t.StartsWith(';')) break;                       // rest of the line is a comment
            if (t[0] == ':' || t[0] == '$')
            {
                var b = Encoding.ASCII.GetBytes(t[1..].Replace('_', ' '));
                if (t[0] == '$') body.AddRange(Be((ushort)b.Length));
                body.AddRange(b);
                continue;
            }
            if (t[0] == '#' && ushort.TryParse(t[1..], out ushort u16)) { body.AddRange(Be(u16)); continue; }
            if (t[0] == '%' && uint.TryParse(t[1..], out uint u32)) { body.AddRange(Be32(u32)); continue; }
            if (byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out byte raw))
            { body.Add(raw); continue; }
            Refuse($"can't parse '{t}' — expected a hex byte, #u16, %u32, :text or $text.");
            return false;
        }
        return true;
    }

    private void SendRawPacket(byte op, byte[] bytes)
    {
        SendMap(op, _gameInc++, bytes, $"raw(0x{op:x2})");
        Reply($"sent 0x{op:x2} + {bytes.Length}B: {Convert.ToHexString(bytes).ToLowerInvariant()}");
        Log.Info($"   -> RAW PKT 0x{op:x2} {bytes.Length}B {Convert.ToHexString(bytes).ToLowerInvariant()}");
    }

    /// <summary>"@pkt file &lt;name&gt;" — send game-data/packets/&lt;name&gt;.txt. The file's FIRST token is the
    /// opcode and the rest is the body, so one file is one complete packet you can keep editing in a real
    /// text editor and re-fire with one short command. Newlines are just whitespace; ';' starts a comment.</summary>
    private void SendPacketFile(string name)
    {
        // Content, not state: these probes are hand-authored, versioned, and identical on every deployment.
        string dir = Path.Combine(Shared.RepoPaths.GameDataDir(), "packets");
        string path = Path.Combine(dir, Path.GetFileName(name) + ".txt");
        if (!File.Exists(path)) { Refuse($"no such packet file: game-data/packets/{Path.GetFileName(name)}.txt"); return; }

        // A comment has to be stripped a line at a time, since ';' ends the LINE, not the file.
        var tokens = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            int c = line.IndexOf(';');
            tokens.AddRange((c < 0 ? line : line[..c]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        if (tokens.Count == 0) { Refuse($"{Path.GetFileName(path)} is empty."); return; }
        if (!byte.TryParse(tokens[0], System.Globalization.NumberStyles.HexNumber, null, out byte op))
        { Refuse($"first token of {Path.GetFileName(path)} must be a hex opcode, got '{tokens[0]}'."); return; }

        var body = new List<byte>();
        if (ParsePacketTokens(tokens.ToArray()[1..], body)) SendRawPacket(op, body.ToArray());
    }

    // 0x0D speech: chatType(u8) entityId(u32BE) msgLen(u8) msg[]. Handler 0x450170 shows msg
    // over the entity's head.
    private void SendSpeech(byte chatType, uint id, byte[] msg)
    {
        var d = new List<byte> { chatType };
        d.AddRange(Be32(id));
        d.Add((byte)msg.Length);
        d.AddRange(msg);
        SendMap(0x0D, _gameInc++, d.ToArray(), "speech(0x0D)");
    }


}
