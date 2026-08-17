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
    private void ListItems(string text)
    {
        string q = text.Trim();
        var found = Content.SearchItems(q, 15);
        if (found.Count == 0) { SendLog(q.Length == 0 ? "no items loaded (check game-data/Items.csv)" : $"no items match \"{q}\""); return; }
        SendLog($"items{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count} of {Content.Items.Count}):");
        foreach (var i in found)
            SendLog($"  #{i.Id} {i.Name} — {(i.IsEquip ? $"equip(dam {i.Dam}/ac {i.Armor})" : i.IsConsumable ? "use" : "etc")}   (@item {i.Name})");
    }

    // "@item <name or id> [amount]": summon an item into the bag (equip items keep a single copy per slot).
    // "@coins <n>" (alias "@gold <n>") — add n coins to the purse (updates the HUD + persists). A negative n
    // removes that many, floored at 0; "@coins" alone defaults to +10000. Coins aren't in the item registry
    // (they're a negative item id on the wire), so @item can't grant them — this is the direct GM path.
    private void GiveCoinsCmd(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int amount = 10000;
        if (parts.Length > 0 && !int.TryParse(parts[0], out amount))
        { SendLog("usage: @coins <n>   (n may be negative to remove; default +10000)"); return; }

        if (amount >= 0) AwardGold((uint)amount);
        else
        {
            uint take = Math.Min(_char.Coins, (uint)(-(long)amount));
            _char.Coins -= take;
            SendStats(); SaveChar();
        }
        SendLog($"Coins: {_char.Coins:N0} (changed by {amount:+#;-#;0}).");
    }

    // "@npc" / "@npc list" — show which NPCs are switched off. Read-only: toggling an NPC means setting the
    // Enabled column in game-data/NPCs.csv and running @reload (World.ReconcileNpcToggles spawns/despawns
    // to match), not a live GM mutation command. The tavern-hand "small guy" NPCs (Ox/Taur) are off by default.
    //
    // The two ways to be off are reported separately because the fix differs: an era-gated NPC (NPCs.csv
    // EraFeature — Yarlof, who arrives with the 2005 Druid bouquet quest) is absent because he does not exist
    // yet, and no amount of editing the Enabled column will bring him back.
    private void NpcToggleCmd(string text)
    {
        static string Describe(NpcDef n) => $"#{n.Id} {n.Name} (map {n.Map})";

        var off = Content.Npcs.Where(n => !n.Enabled).OrderBy(n => n.Id).ToList();
        var unborn = off.Where(n => n.EraFeature.Length > 0 && !Era.Has(n.EraFeature)).ToList();
        var manual = off.Except(unborn).ToList();

        SendLog((manual.Count == 0 ? "No NPCs are switched off."
                                   : $"Switched-off NPCs ({manual.Count}): " + string.Join(", ", manual.Select(Describe))) +
                "  (edit the Enabled column in game-data/NPCs.csv + @reload to change)");

        if (unborn.Count > 0)
            SendLog($"Not yet in this era ({unborn.Count}): " +
                    string.Join(", ", unborn.Select(n => $"{Describe(n)} [{n.EraFeature}]")) +
                    "  (move EraDate in game-data/ServerTuning.csv — see @era)");
    }

    // "@craft" / "@craft list" — show which crafting skills are era-gated on/off. Read-only: the toggle
    // itself is config, not live GM state — edit game-data/CraftingToggles.csv and run @reload to
    // change it (see Server/CraftingToggles.cs + docs/Crafting-Values.md for why Jewelry and Food
    // Preparation/Chef default off).
    private void CraftToggleCmd(string text)
    {
        var lines = CraftingToggles.AllSkills
            .Select(s => $"{s}={(CraftingToggles.IsEnabled(s) ? "ON" : "off")}");
        SendLog("Crafting skills (edit game-data/CraftingToggles.csv + @reload to change): " +
                string.Join(", ", lines));
    }

    // "@era" — what date the world is pretending it is, and which dated features that includes. Read-only
    // for the same reason as @craft: the target date is deployment config (ServerTuning.csv EraDate), not
    // live GM state, so it moves by editing the file and running @reload. A feature with no row in
    // EraFeatures.csv is always present and deliberately isn't listed — see Server/Era.cs.
    private void EraCmd(string text)
    {
        var now = Era.Today;
        if (now is null)
        {
            SendLog("Era gating is OFF (EraDate=0 in game-data/ServerTuning.csv) — all dated content is present.");
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
        SendLog($"Era date {now.Value:yyyy-MM-dd} — {string.Join(", ", lines)}  " +
                "(edit EraDate in game-data/ServerTuning.csv + @reload to change)");
    }

    private void GiveItemCmd(string text)
    {
        string q = text.Trim();
        if (q.Length == 0) { SendLog("usage: @item <name or id> [amount]   (browse with  @items <name>)"); return; }
        int amount = 1;
        var parts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1 && int.TryParse(parts[^1], out var n) && n > 0) { amount = n; q = string.Join(' ', parts[..^1]); }
        var def = Content.FindItem(q);
        if (def is null) { SendLog($"no item matches \"{q}\" — try  @items {q}"); return; }
        if (def.Stackable) GiveItem(def, amount);
        else for (int i = 0; i < amount; i++) if (!GiveItem(def)) break;
        SendLog($"Gave {def.Name}{(amount > 1 ? $" x{amount}" : "")} (#{def.Id}, {(def.IsEquip ? $"equip slot {def.EquipSlot}" : def.IsConsumable ? "use" : "etc")}).");
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
        SendLog("Cleared your pack and gear.");
    }

    // "@icons [start]": ICON-ID RE. Fill every bag slot with a raw 0x0F whose icon id = start+slot, named
    // "f<icon>", so a screenshot shows which client Item.epf frames render (frame index == client item id;
    // this is a DIFFERENT space from the RTK ItmIcon). Sweep with @icons 0, @icons 27, 54, 81, … and match
    // the rendered icons to re/render_items.py's contact sheet to build the RTK-item -> client-frame map.
    private void IconSweep(string text)
    {
        var a = ParseInts(text);
        int start = a.Length > 0 ? a[0] : 0;
        _char.Inventory.Clear();
        for (int i = 0; i < _char.MaxInv; i++)
            SendRawIcon((byte)i, (ushort)(start + i), $"f{start + i}");
        SendLog($"icons {start}..{start + _char.MaxInv - 1} in bag (match vs render_items.py sheet)");
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
    private void DelReasonSweep(string text)
    {
        var a = ParseInts(text);
        int lo = a.Length > 0 ? a[0] : 0, hi = a.Length > 1 ? a[1] : 15;
        lo = Math.Clamp(lo, 0, 255); hi = Math.Clamp(hi, lo, 255);
        byte slot = (byte)(_char.MaxInv - 1);          // last slot: least likely to collide with real gear
        SendLog($"0x10 reason sweep {lo}..{hi} — a reason with NO line after it is the silent one.");
        for (int r = lo; r <= hi; r++)
        {
            SendRawIcon(slot, 1, $"reason{r}");
            SendLog($"reason {r}:");
            SendDelItem(slot, (byte)r);
            System.Threading.Thread.Sleep(700);
        }
        SendLog("sweep done. Set EquipDelReason to a silent reason (or leave it) and @reload.");
        Log.Info($"   -> DELREASON SWEEP {lo}..{hi} on slot {slot}");
    }

    // "@crecol <lookId> [loColor] [hiColor] [step]": spawn the SAME look id across a GRID (12 cols/row,
    // wraps to more rows north) at increasing 0x07 color-byte values (default 0..23 — the client's color
    // byte visibly wraps mod 24, see docs) so every candidate recolor is visible in one screenshot without
    // silently truncating past 12 entries like the old single-row version did.
    private void CreatureColorRow(string text)
    {
        var a = ParseInts(text);
        int look = a.Length > 0 ? a[0] : 0;
        int lo = a.Length > 1 ? a[1] : 0;
        int hi = a.Length > 2 ? a[2] : 23;
        int step = a.Length > 3 ? Math.Max(1, a[3]) : 1;
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
        SendCreatureList(es);
        Log.Info($"   -> CREATURE color row: look {look}, color {lo}..{hi} step {step} ({es.Count} sent, {cols}/row)");
    }

    // "@crow <lo> <hi> [step]": sweep monster look ids lo..hi across a W->E row (one 0x07 packet with
    // up to 12 entries) so one screenshot maps the Monster.epf look-id space. Find squirrel/rabbit here.
    private void CreatureRow(string text)
    {
        var a = ParseInts(text);
        int lo = a.Length > 0 ? a[0] : 0;
        int hi = a.Length > 1 ? a[1] : lo + 11;
        int step = a.Length > 2 ? Math.Max(1, a[2]) : 1;
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
        SendCreatureList(es);
        Log.Info($"   -> CREATURE row: monster look sweep {lo}..{hi} step {step} ({es.Count} sent)");
    }

    private void MobOne(string text)
    {
        var a = ParseInts(text);
        int hi = a.Length > 0 ? a[0] : 0;
        int lo = a.Length > 1 ? a[1] : 1;
        int hp = a.Length > 2 ? a[2] : 6;
        ushort sprite = (ushort)((hi << 8) | (lo & 0xFF));
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SpawnMob(sprite, x, y, $"m{sprite}", hp);
    }

    private void MobRow(string text)
    {
        var a = ParseInts(text);
        int lo = a.Length > 0 ? a[0] : 1;
        int hi = a.Length > 1 ? a[1] : lo + 11;
        int step = a.Length > 2 ? Math.Max(1, a[2]) : 1;
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
        if (world + local == 0) { SendMessage("no mobs to clear"); return; }
        SendMessage($"cleared {world} world mob(s) + {local} local dummy(s)");
        Log.Info($"   -> KILL: despawned {world} world + {local} local mobs on map {_char.Map}");
    }

    // A small pack of REAL, killable monsters around the player (via 0x07 = Monster.epf). "@spawn
    // [lookId] [hp]" — lookId is the Monster.tbl monster index (0..326); defaults to 0.
    private void SpawnCritters(string text)
    {
        var a = ParseInts(text);
        int look = a.Length > 0 ? a[0] : 0;
        int hp = a.Length > 1 ? a[1] : 6;
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
    private void ToggleMount(string text)
    {
        var a = ParseInts(text);
        _char.Mounted = a.Length > 0 ? a[0] != 0 : !_char.Mounted;
        RefreshAppearance();                                              // redraw self on the horse + everyone watching
        SendMiniText(_char.Mounted ? "The powerful steed takes you where you want to go."   // same lines as the
                                   : "You precariously step again onto the ground.");       // real 'r' ride key
        Log.Info($"   -> MOUNT {( _char.Mounted ? "on" : "off")}");
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
            // table, so they were always server-sent — ours were stand-ins).
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

    // "@lvl N" / "@might N" — set a BASE character stat so wear-requirements can be exercised on the
    // fabricated bring-up character (default is level 1 / might 3, which gates out most real gear).
    private void SetBaseStat(string which, string text)
    {
        var a = ParseInts(text);
        int v = a.Length > 0 ? a[0] : 0;
        if (which == "level") _char.Level = (byte)Math.Clamp(v, 1, 99);
        else                  _char.Might = (byte)Math.Clamp(v, 0, 255);
        if (_enteredWorld) StoreSave();
        SendStats();
        SendMessage($"{which} set to {(which == "level" ? _char.Level : _char.Might)}");
        Log.Info($"   -> {which.ToUpper()} set to {(which == "level" ? _char.Level : _char.Might)}");
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
    private void SetMark(string text)
    {
        // Name the ranks of the character's OWN path, not the generic Il san ladder — a Ju jak's ranks are
        // Force / Inferno / Pandemonium, and telling them otherwise is just wrong.
        int p = Math.Max(0, CharClassId);
        string ladder = string.Join(" · ", Enumerable.Range(1, Content.MaxMark).Select(m => $"{m} {Content.PathTitle(p, m)}"));

        var a = ParseInts(text);
        if (a.Length == 0)
        {
            SendLog($"mark is {_char.Mark} ({ClassTitle}). usage: {Prefix}mark <0-{Content.MaxMark}> — {ladder}, each on top of level 99.");
            return;
        }
        // Refuse rather than clamp: silently turning "@mark 5" into Sam san would read as a working Oh san.
        if (a[0] > Content.MaxMark)
        {
            SendLog($"{Content.PathTitle(p, Content.MaxMark)} (mark {Content.MaxMark}) is as far as the ranks go — " +
                    $"there are no mark-{Content.MaxMark + 1} spells in the game data yet. Ranks: {ladder}.");
            return;
        }
        RespecTo(99, Math.Max(0, a[0]));
    }

    // "@dog [0|1]" — skip the bark/woof/grrowl chain and hand over (or take back) the Dog Linguist standing.
    // This does NOT grant spells: the Dog itself still teaches those for kills and goods, so a GM testing the
    // teach flow starts where a finished linguist starts. Eligibility for the spells is checked separately by
    // the Dog and is base classes + NPC subpaths only (Content.CanLearnDogSpells) — said here too, because a
    // PC subpath can hold the legend and still never be taught anything.
    private void SetDogFlag(string text)
    {
        int p = Math.Max(0, CharClassId);
        var a = ParseInts(text);
        bool want = a.Length == 0 ? !HasDogFlag : a[0] != 0;      // bare "@dog" toggles

        SetQuestStage(Content.DogFlagReg, want ? 1 : 0);
        SetQuestStage(DogChainReg, want ? DogChainDone : 0);
        if (want) AddLegend($"Dog linguist ({Character.GameDate})", DogChainReg, 3, 128);
        else RemoveLegend(DogChainReg);

        SendLog(want
            ? $"Dog Linguist granted — say \"secret\" to your class's Dog to be taught." +
              (Content.CanLearnDogSpells(p)
                  ? ""
                  : $" NOTE: {Content.PathTitle(p, _char.Mark)} is a PC subpath and will be refused — only the four " +
                    $"base classes and the NPC subpaths (Chung ryong · Baekho · Ju jak · Hyun moo) may learn Dog spells.")
            : "Dog Linguist cleared; the chain starts over at Mutt.");
    }

    /// <summary>The Dog Linguist chain's progress key and legend id — one name, as in RTK
    /// (npc_dialog.lua <c>DOG_LEGEND</c>). Stage 4 is the finished chain.</summary>
    private const string DogChainReg = "dog_linguist";
    private const int DogChainDone = 4;

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
    private void SetClass(string text)
    {
        string name = text.Trim();
        if (name.Length == 0)
        {
            SendLog($"class is '{ClassTitle}' (usage: {Prefix}class <name>)");
            SendLog($"  {string.Join(" · ", Content.PlayablePathNames())}  — or any rank title up to Sam san " +
                    $"(Il san (W), Fury, Inferno, …)");
            return;
        }
        if (Content.PathRankForName(name) is not { } pick)
        { SendLog($"'{name}' isn't a known class or rank. Try: {string.Join(" · ", Content.PlayablePathNames())}"); return; }
        if (!Content.IsPlayablePath(pick.PathId))
        {
            SendLog($"'{Content.PathName(pick.PathId)}' isn't playable here — this server models the base classes and the " +
                    $"NPC subpaths only. Try: {string.Join(" · ", Content.PlayablePathNames())}");
            return;
        }

        if (pick.Mark > Content.MaxMark)
        {
            SendLog($"'{name}' is rank {pick.Mark}, past the {Content.PathTitle(pick.PathId, Content.MaxMark)} cap — " +
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
    private void StatProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte op = 0x08, flags = 0xFF;
        if (parts.Length > 0) byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out op);
        if (parts.Length > 1) byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out flags);
        SendStatProbe(op, flags, level: 99);
        Log.Info($"   -> STAT PROBE op=0x{op:x2} flags=0x{flags:x2}");
    }

    // "@batch" — fire the sentinel-laden status probe at a CURATED SAFE set of opcodes (no resource
    // loaders like 0x2e, no risky memcpy/spawn), ~700ms apart with a bubble label. Paired with the
    // probe's whole-memory sentinel scan, one run reveals which opcode (if any) STORES the stats — no
    // matter where the client keeps them. Watch the HUD too and note any opcode that changes a number.
    private void StatBatch(string text)
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
    private void StatGradient(string text)
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
    private void MailFlagProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !int.TryParse(parts[0], out int off) || off < 0 || off > 79)
        {
            SendMessage("@mailflag <off 0..79> [valHex]  — sweep the 0x08 mail/parcel notify byte (default 0x11=both). Try 40..57.");
            return;
        }
        byte val = 0x11;
        if (parts.Length > 1) byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out val);

        // Rebuild the exact full-stats body SendStats sends (so every real HUD field stays correct), just
        // longer, then stamp the candidate notify byte.
        var eq = Totals();
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + eq.hp);
        uint maxMp = (uint)Math.Max(0, (int)_char.MaxMp + eq.mp);
        var d = new byte[Math.Max(58, off + 1)];
        d[0] = 0x78;
        d[1] = _char.Nation; d[2] = _char.Totem; d[4] = _char.Level;
        WriteBe32(d, 5, maxHp); WriteBe32(d, 9, maxMp);
        d[13] = (byte)Math.Clamp(_char.Might + eq.might, 0, 255);
        d[14] = (byte)Math.Clamp(_char.Will  + eq.will,  0, 255);
        d[17] = (byte)Math.Clamp(_char.Grace + eq.grace, 0, 255);
        WriteBe32(d, 24, _char.Hp); WriteBe32(d, 28, _char.Mp);
        WriteBe32(d, 32, _char.Exp); WriteBe32(d, 36, _char.Coins);
        d[off] = val;
        SendMap(0x08, _gameInc++, d, $"mailflag off={off} val=0x{val:x2}");
        SendMessage($"0x08 with body[{off}]=0x{val:x2} (0x11=mail+parcel). See an arrow/bag on the HUD?");
        Log.Info($"   -> MAILFLAG probe: 0x08 body[{off}]=0x{val:x2}");
    }

    private void StatReplay6x(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte op = 0x08;
        if (parts.Length > 0) byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out op);
        SendMap(op, _gameInc++, Stats6xFull, $"replay6x-stats(0x{op:x2})");
        Log.Info($"   -> REPLAY 6.x stats on op=0x{op:x2} (expect HUD: level 1, HP 51, MP 33, might/will/grace 3)");
    }

    // "@sweep" is DISABLED. Blind-sweeping unknown opcodes can crash the client by feeding a real handler a
    // mis-framed body (exactly how 0x2e/the world-map screen "crashed" until a one-byte framing bug in our
    // OWN packet was found -- see SendWorldMap §11m; that was never a client bug). Find the stats opcode
    // deterministically instead (self player object is [world+0x40c]); only fire "@s <op>" once a specific
    // opcode is confirmed safe by reading its
    // handler.
    private void StatSweep(string text)
    {
        SendMessage("@sweep is disabled (crashes the client on resource-loading opcodes). Use @s <hexop>.");
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
    private void WorldMapPosCmd(string args)
    {
        var dests = Content.WorldDests;
        (int X, int Y) DotOf(int i) => WorldDotOverride.TryGetValue(i, out var ov) ? ov : (dests[i].DotX, dests[i].DotY);
        var p = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length >= 3 && int.TryParse(p[0], out var wi) && int.TryParse(p[1], out var wx)
            && int.TryParse(p[2], out var wy) && wi >= 0 && wi < dests.Count)
        {
            WorldDotOverride[wi] = (Math.Clamp(wx, 0, 639), Math.Clamp(wy, 0, 479));
            var dot = DotOf(wi);
            SendMiniText($"{wi} {dests[wi].Name} -> ({dot.X},{dot.Y})  [bake into WorldMapDests.csv + {Prefix}reload]");
            SendWorldMap("field10");
        }
        else
            for (int k = 0; k < dests.Count; k++)
            {
                var dot = DotOf(k);
                SendMiniText($"{k} {dests[k].Name}: ({dot.X},{dot.Y})");
            }
    }

    // "@wmtest [name]" — native world-map screen (§11m) with an explicit background name (defaults to
    // field10 = "Map of the Kingdom", the overview world-map art). The framing bug that used to crash this
    // is fixed; this stays as a way to try alternate backgrounds (field1, title, other fieldNN).
    private void WorldMapTestCmd(string args)
        => SendWorldMap(args.Trim().Length == 0 ? "field10" : args.Trim());

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
    private void SetStatsCmd(string args)
    {
        var a = ParseInts(args);
        if (a.Length is not (2 or 3 or 5))
        {
            SendLog($"usage: {Prefix}stats <vita> <mana> [<all> | <might> <grace> <will>]   " +
                    $"e.g. {Prefix}stats 50000 50000 130");
            SendLog($"  now: vita {_char.MaxHp:N0}, mana {_char.MaxMp:N0}, might {_char.Might}, " +
                    $"grace {_char.Grace}, will {_char.Will}");
            return;
        }

        _char.MaxHp = (uint)Math.Max(1, a[0]);
        _char.MaxMp = (uint)Math.Max(0, a[1]);
        if (a.Length == 3)
            _char.Might = _char.Grace = _char.Will = (byte)Math.Clamp(a[2], 0, 255);
        else if (a.Length == 5)
        {
            _char.Might = (byte)Math.Clamp(a[2], 0, 255);
            _char.Grace = (byte)Math.Clamp(a[3], 0, 255);
            _char.Will  = (byte)Math.Clamp(a[4], 0, 255);
        }

        _char.Hp = EffMaxHp;                  // top up to the new maxima, gear/buffs included
        _char.Mp = EffMaxMp;
        if (_enteredWorld) StoreSave();
        SendStats();
        // Report BASE and EFFECTIVE separately: what you set is the base, but the HUD shows base + gear +
        // buffs, so "@stats 50000 …" reading back as 50,400 on screen is equipment, not a rounding bug.
        var eq = Totals();
        SendLog($"base: vita {_char.MaxHp:N0}, mana {_char.MaxMp:N0}, might {_char.Might}, grace {_char.Grace}, will {_char.Will}.");
        if (EffMaxHp != _char.MaxHp || EffMaxMp != _char.MaxMp || eq.might != 0 || eq.grace != 0 || eq.will != 0)
            SendLog($"with gear: vita {EffMaxHp:N0}, mana {EffMaxMp:N0}, might {_char.Might + eq.might}, " +
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
    private void RawPacketCmd(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            SendLog($"usage: {Prefix}pkt <hexop> [xx | #u16 | %u32 | :text | $text]");
            SendLog($"  {Prefix}pkt add <tokens>   append to the pending packet (the chat box is short)");
            SendLog($"  {Prefix}pkt send <hexop>   send what's pending, then clear it");
            SendLog($"  {Prefix}pkt show | clear   inspect or drop the pending bytes");
            SendLog($"  {Prefix}pkt file <name>    send game-data/packets/<name>.txt (';' starts a comment)");
            return;
        }

        // Long packets can't be typed in one line, so tokens accumulate into _pktPending across several
        // commands and go out on "send". "file" is the same parser over a file, for anything worth keeping.
        switch (parts[0].ToLowerInvariant())
        {
            case "add":
                if (!ParsePacketTokens(parts[1..], _pktPending)) return;
                SendLog($"pending {_pktPending.Count}B: {Convert.ToHexString(_pktPending.ToArray()).ToLowerInvariant()}");
                return;
            case "clear":
                _pktPending.Clear();
                SendLog("pending packet cleared.");
                return;
            case "show":
                SendLog(_pktPending.Count == 0 ? "nothing pending."
                    : $"pending {_pktPending.Count}B: {Convert.ToHexString(_pktPending.ToArray()).ToLowerInvariant()}");
                return;
            case "send":
                if (parts.Length < 2 || !byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber,
                                                       null, out byte pendOp))
                { SendLog($"usage: {Prefix}pkt send <hexop>"); return; }
                SendRawPacket(pendOp, _pktPending.ToArray());
                _pktPending.Clear();
                return;
            case "file":
                if (parts.Length < 2) { SendLog($"usage: {Prefix}pkt file <name>"); return; }
                SendPacketFile(parts[1]);
                return;
        }

        if (!byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out byte op))
        {
            SendLog($"'{parts[0]}' is not a hex opcode.");
            return;
        }
        var oneShot = new List<byte>();
        if (ParsePacketTokens(parts[1..], oneShot)) SendRawPacket(op, oneShot.ToArray());
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
            SendLog($"can't parse '{t}' — expected a hex byte, #u16, %u32, :text or $text.");
            return false;
        }
        return true;
    }

    private void SendRawPacket(byte op, byte[] bytes)
    {
        SendMap(op, _gameInc++, bytes, $"raw(0x{op:x2})");
        SendLog($"sent 0x{op:x2} + {bytes.Length}B: {Convert.ToHexString(bytes).ToLowerInvariant()}");
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
        if (!File.Exists(path)) { SendLog($"no such packet file: game-data/packets/{Path.GetFileName(name)}.txt"); return; }

        // A comment has to be stripped a line at a time, since ';' ends the LINE, not the file.
        var tokens = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            int c = line.IndexOf(';');
            tokens.AddRange((c < 0 ? line : line[..c]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        if (tokens.Count == 0) { SendLog($"{Path.GetFileName(path)} is empty."); return; }
        if (!byte.TryParse(tokens[0], System.Globalization.NumberStyles.HexNumber, null, out byte op))
        { SendLog($"first token of {Path.GetFileName(path)} must be a hex opcode, got '{tokens[0]}'."); return; }

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
