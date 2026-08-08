using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // 0x0F add-item-to-slot: slot(u8=idx+1) icon(u16) iconColor(u8) [dispName u8len+txt] [baseName u8len+txt]
    //   amount(u32) [block: stack/0(u8) dura(u32) protected(u8)] [owner u8len+txt] 00 00 00.
    private void SendAddItem(InvItem it)
    {
        var def = Content.ItemById(it.ItemId);
        if (def is null) return;
        string name = string.IsNullOrEmpty(it.CustomName) ? def.Name : it.CustomName;
        // RTK clif_sendadditem (clif.c:7096) bakes the count into the display NAME: a stack as "(N)", a
        // charged consumable (ITM_SMOKE: wine/liquor/pipes) as "[N unit]" using ItmText -> "Wine [50 sips]".
        // A charged item whose Dura is still 0 (older save, not yet seeded) shows its full charge count
        // until first use lazily seeds it (see HandleUseItem), so the number never visibly jumps.
        string disp = it.Amount > 1 ? $"{name} ({it.Amount})"
                    : def.IsCharged ? $"{name} [{(it.Dura == 0 ? def.Durability : it.Dura)} {def.Text}]"
                    : name;

        var d = new List<byte> { (byte)(it.Slot + 1) };
        d.AddRange(Be(IconWire(def.Icon)));   // RTK ItmIcon == client Item.epf frame; encode for the +0x4000 resolver
        // 5.x (V533) carries an icon-color byte here; 4.95 (V495) does NOT — it reads the name length
        // right after the icon. Proven live: on 4.95 an extra byte here made the client read the name
        // one byte early (Apple iconColor=0 → empty name "You ate ."; Poison apple iconColor=12 → 12-char
        // garbled "⊥Poison appl"). See docs §11c.
        if (_ver == ClientVersion.V533) d.Add(def.IconColor);
        var dn = Ascii(disp); d.Add((byte)dn.Length); d.AddRange(dn);
        var bn = Ascii(def.Name); d.Add((byte)bn.Length); d.AddRange(bn);
        d.AddRange(Be32((uint)it.Amount));
        if (def.IsEquip) { d.Add(0); d.AddRange(Be32(it.Dura)); d.Add(0); }
        else { d.Add((byte)(def.Stackable ? 1 : 0)); d.AddRange(Be32(0)); d.Add(0); }
        d.Add(0);                 // owner name length (0 = unowned)
        d.AddRange(Be(0));        // trailing u16
        d.Add(0);                 // trailing u8
        SendMap(0x0F, _gameInc++, d.ToArray(), $"additem(0x0F) slot={it.Slot} '{name}' x{it.Amount}");
    }

    // 0x10 remove-from-slot: slot(u8=idx+1) reason(u8) 00 00. reason: 0=Remove 1=Drop 2=Eat 4=Throw 6=Used …
    private void SendDelItem(byte slot, byte reason) =>
        SendMap(0x10, _gameInc++, new byte[] { (byte)(slot + 1), reason, 0, 0 }, $"delitem(0x10) slot={slot} r={reason}");

    // 0x37 equip-window: equipType(u8) icon(u16) iconColor(u8) [name u8len+txt] [baseName u8len+txt] dura(u32) 00 00.
    private void SendEquip(InvItem worn)
    {
        var def = Content.ItemById(worn.ItemId);
        if (def is null) return;
        string name = string.IsNullOrEmpty(worn.CustomName) ? def.Name : worn.CustomName;
        var d = new List<byte> { worn.Slot };     // worn.Slot holds the wire equip-slot byte
        d.AddRange(Be(IconWire(def.Icon)));        // +0x4000 resolver encoding (see SendAddItem / IconWire)
        if (_ver == ClientVersion.V533) d.Add(def.IconColor);   // 4.95 omits the icon-color byte (see SendAddItem)
        var nn = Ascii(name); d.Add((byte)nn.Length); d.AddRange(nn);
        var bn = Ascii(def.Name); d.Add((byte)bn.Length); d.AddRange(bn);
        d.AddRange(Be32(worn.Dura));
        d.AddRange(Be(0));
        SendMap(0x37, _gameInc++, d.ToArray(), $"equip(0x37) slot={worn.Slot} '{name}'");
    }

    // The profile-screen equipment ICON cells (helm + two rings). 4.95 has no character-sprite layer for these
    // slots, so both profile views (0x39 self, 0x34 other) show them as ground-icon boxes fed by three u16
    // fields. Encoded with IconWire, exactly like the 0x37 equip window (the old bug proved these boxes render
    // an IconWire value — it wrongly showed the weapon there). Client wire slots (from 0x1F captures): helm=4,
    // left ring=7, right ring=8. Returns 0 (empty box) when nothing is worn in that slot.
    private ushort ProfileCellIcon(byte wireSlot)
    {
        var worn = _char.Equipment.FirstOrDefault(e => e.Slot == wireSlot);
        var def = worn is null ? null : Content.ItemById(worn.ItemId);
        return def is null ? (ushort)0 : IconWire(def.Icon);
    }

    // 0x38 unequip-window: spot(u8) 00.
    private void SendUnequip(byte wireSlot) =>
        SendMap(0x38, _gameInc++, new byte[] { wireSlot, 0 }, $"unequip(0x38) slot={wireSlot}");

    /// <summary>Draw a floor item AT REST via the 0x07 static-object path (NOT 0x16). Full RE (2026-07-24):
    /// 0x16 builds a WALK projectile (vtable 0x4cd18c, tick 0x463270) that interpolates in then drops off the
    /// moving-list / self-destructs on arrival -> invisible at rest (that was the bug). The 0x07 handler
    /// (0x44fdb0 @ 0x44fe7f) routes any look OUTSIDE 0x8000..0xbfff to descriptor type 2 = the BASE object
    /// (vtable 0x4cd118, tick 0x4601a0 = `xor al,al;ret` no-op) built by 0x462ec0 alone: it never moves, never
    /// self-destructs, and is drawn by the shared render loop exactly like a monster but stationary. IconWire
    /// frames (0..1310) map to 0xc000..0xc51e, all > 0xbfff, so they hit type 2 and resolve (look+0x4000)&0xffff
    /// against Item.epf -- the SAME resolver the bag/0x0F path uses. Caveat: 0x07 has a viewport gate (0x424310),
    /// so the tile must be on-screen when spawned (true for drop/throw at the player's feet).</summary>
    public void ShowGroundItem(GroundItem gi) =>
        SendCreatureList(new[] { (gi.Id, IconWire(gi.Graphic), gi.X, gi.Y, (byte)0, (byte)0) });

    // The 4.95 type-0 form has three gear-driven look bytes: weapon [5], armor [3] and shield [6]. Weapon/
    // shield are derived live from Equipment by WeaponLook()/ShieldLook() (0xFF = bare), so equipping any of
    // the three must re-draw self + peers; only armor still needs its cached _char.Armor byte written here.
    private void ApplyAppearance(ItemDef def, bool equip)
    {
        if (def.Type == 4) _char.Armor = equip ? (byte)def.Look : (byte)0;        // ITM_ARMOR (cached in [3])
        else if (def.Type == 3) _char.Weapon = equip ? (byte)def.Look : (byte)0;  // ITM_WEAP (kept for combat/GM)
        else if (def.Type != 5) return;                                           // not weapon/armor/shield -> no look change
        RefreshAppearance();
    }

    // ---- recv handlers (client -> server) ----

    // 0x07 pick up: grab whatever floor item sits on my tile; coins (sentinel ItemId<0) go to the purse.
    // The client sends pickuptype at body[0] (RTK clif_parsegetitem: RFIFOB(fd,5)): ',' = 0 (grab the top
    // item), '<'/Shift+, = 1 (grab EVERYTHING stacked on the tile). Either way, play the bend-down action
    // first — type 4, time 40; the crouch sprite carries the pickup sound — on self AND peers, even when the
    // tile is empty (matches RTK, which sends the action before it looks at the floor).
    private void HandlePickup(byte[] dec)
    {
        bool pickAll = dec.Length > 0 && dec[0] != 0;
        SendAction(_char.Id, 4, 40, 0);                                                     // our crouch + sound
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 4, 40, 0), except: this);   // peers see it too

        do
        {
            // Pass our id so someone else's death pile is passed over rather than pocketed (RTK canLoot).
            var gi = _world.PickUp(_char.Map, _char.X, _char.Y, _char.Id, ownOnly: false, out bool locked);
            if (gi is null)
            {
                if (locked) SendMiniText("That item does not belong to you.");   // RTK canLoot's own refusal
                return;                                   // tile empty (or nothing here we may take)
            }
            BreakStealth();                               // grabbing floor loot is an overt act — drops Invisible (RTK removeDuras(invis))
            if (gi.ItemId < 0) { _char.Coins += (uint)gi.Amount; SendStats(); MarkDirty(); continue; }   // coins -> purse
            var def = Content.ItemById(gi.ItemId);
            if (def is null) continue;
            if (!GiveItem(def, gi.Amount, gi.Dura, gi.CustomName))
            {
                // pack full — put it straight back on the floor so it isn't lost, and stop grabbing.
                _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = gi.ItemId,
                    X = _char.X, Y = _char.Y, Amount = gi.Amount, Dura = gi.Dura, Graphic = gi.Graphic, CustomName = gi.CustomName });
                return;
            }
        } while (pickAll);                                // ',' runs once; '<' loops until the tile is empty
    }

    // 0x08 drop: dec[0]=slot(1-based). Drop the whole stack onto my tile.
    private void HandleDropItem(byte[] dec)
    {
        if (dec.Length < 1) return;
        // RTK clif_parsedropitem gates on player state first (dead/mounted can't drop).
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }
        if (_char.Mounted) { SendMiniText("You cannot do that while riding a horse."); return; }
        int slot = dec[0] - 1;
        // dec[1] = the "all" flag: 'd' (drop one) sends 0, 'D'/Shift+d (drop whole stack) sends 1.
        // Confirmed live: client emits `08 <slot+1> 00 00` for d and `08 <slot+1> 01 00` for D.
        bool dropAll = dec.Length > 1 && dec[1] != 0;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;
        if (def.NoDrop) { SendLog($"You can't drop {def.Name}."); return; }

        // Bend-down drop animation + sound (RTK clif_parsedropitem: type 5, time 20 — a distinct pose from
        // pickup's type 4). Fired only once the drop is allowed, on self AND peers, before the item leaves the bag.
        SendAction(_char.Id, 5, 20, 0);                                                     // our drop crouch + sound
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 5, 20, 0), except: this);   // peers see it too

        int count = dropAll ? it.Amount : 1;
        int remaining = it.Amount - count;
        if (remaining <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, 1); }  // reason 1 = Drop
        else { it.Amount = remaining; SendAddItem(it); }   // stack shrinks: redraw the slot with the new count
        MarkDirty();
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
            X = _char.X, Y = _char.Y, Amount = count, Dura = it.Dura, Graphic = def.Icon, CustomName = it.CustomName });
    }

    // 0x17 throw: dec[0]=confirm, dec[1]=slot(1-based). Throw one, land it a few tiles ahead.
    private void HandleThrow(byte[] dec)
    {
        if (dec.Length < 2) return;
        // RTK clif_parsethrowitem gates on player state first (dead/mounted can't throw).
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }
        if (_char.Mounted) { SendMiniText("You cannot do that while riding a horse."); return; }
        int slot = dec[1] - 1;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;
        if (def.NoDrop) { SendLog("You can't throw this item."); return; }   // same restriction as dropping (RTK itemdb_droppable)
        SendAction(_char.Id, 2, 20, 0);                                                    // throw animation (self)
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 2, 20, 0), except: this);   // peers see the throw too
        it.Amount -= 1;
        if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, 4); }  // reason 4 = Throw
        else SendAddItem(it);
        MarkDirty();
        // Fly up to 3 tiles in the facing direction, but STOP at the last passable tile — a thrown item
        // must not land past a wall/off the map into an unreachable spot. Step tile-by-tile and halt before
        // the first blocked/off-map cell (same collision the player walk uses). If the tile directly ahead is
        // solid, the item just lands on the thrower's own tile.
        int tx = _char.X, ty = _char.Y, dx = 0, dy = 0;
        switch (_facing & 3) { case 0: dy = -1; break; case 1: dx = 1; break; case 2: dy = 1; break; case 3: dx = -1; break; }
        var tmap = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        for (int step = 0; step < 3; step++)
        {
            int cx = tx + dx, cy = ty + dy;
            if (cx < 0 || cy < 0 || cx >= _char.MapXs || cy >= _char.MapYs) break;   // off the tile grid
            // Same two-layer collision the walk uses: ground pass flag OR the SObj.tbl directional object-wall
            // for the throw heading — a thrown item halts at a building wall, not just at water/cliffs.
            if (PassEnforce && tmap != null
                && (Blocked(tmap, cx, cy) || ObjectFlags.Blocks(tmap.Obj(cx, cy), _facing & 3))) break;
            tx = cx; ty = cy;
        }
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
            X = (ushort)tx, Y = (ushort)ty, Amount = 1, Dura = it.Dura, Graphic = def.Icon, CustomName = it.CustomName });
    }

    // 0x09 ';' Look: name whatever occupies the tile we're facing, RTK's PC -> mob/NPC -> item order
    // (clif_parselookat_sub / commented clif_parselookat_scriptsub give the exact text shape per entity
    // kind — bare name, stack count in parens for a floor item). The reply goes to the STATUS/MINI-TEXT
    // box below the inventory (SendMiniText / 0x0A), NOT the chat bubble — matching RTK, whose look-at
    // ends in clif_sendminitext. NPCs are stationary mobs (IsNpc-tagged) in the same shared list, so the
    // mob check already covers them; an empty tile gets no reply, same as RTK (no clif_sendminitext call
    // when nothing's found).
    private void HandleLookAt(byte[] dec)
    {
        int tx = _char.X, ty = _char.Y;
        switch (_facing & 3) { case 0: ty--; break; case 1: tx++; break; case 2: ty++; break; case 3: tx--; break; }

        var peer = _world.PeerAt(_char.Map, tx, ty);
        if (peer is not null) { SendMiniText(peer.Snapshot().Name); return; }

        var mob = _world.MobAt(_char.Map, tx, ty);
        if (mob is not null) { SendMiniText(mob.Name); return; }

        // Session-local debug dummies (@cre/@mob/@crow/@crecol/look-lab) never join the shared world, so
        // they're invisible to _world.MobAt — check our own dummy list too (e.g. @crecol's "col<N>" labels).
        var dummy = MobAt(tx, ty);
        if (dummy is not null) { SendMiniText(dummy.Name); return; }

        var gi = _world.ItemsOn(_char.Map).LastOrDefault(i => i.X == tx && i.Y == ty);
        if (gi is null) return;
        string name = gi.ItemId < 0 ? "coins" : string.IsNullOrEmpty(gi.CustomName) ? Content.ItemById(gi.ItemId)?.Name ?? "an item" : gi.CustomName;
        SendMiniText(gi.Amount > 1 ? $"{name} ({gi.Amount})" : name);
    }

    // 0x1C use / 0x1A eat: dec[0]=slot(1-based). Equipment -> wear it; consumable -> run its RTK use-script
    // effect (see ApplyItemEffect + the ItemParams.csv/item_verbs.lua verb/row system).
    private void HandleUseItem(byte[] dec, bool eat)
    {
        if (dec.Length < 1) return;
        int slot = dec[0] - 1;
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null) return;
        // Path/mark restriction goes HERE, not in EquipFromSlot: RTK puts it in pc_useitem (pc.c:1998), which
        // is the funnel for eating, using AND wearing, so it covers the 17 non-equip items that carry an
        // ItmPthId — a Rogue's stealth powder, a Warrior's shard bomb, the eight rogue darts. See CanUsePath.
        if (!CanUsePath(def)) return;
        if (def.IsEquip) { if (eat) { SendMiniText($"You can't eat {def.Name}."); return; } EquipFromSlot(slot); return; }
        if (eat && def.Type != 0) { SendMiniText("That is not edible."); return; }   // ITM_EAT only
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }

        if (!ApplyItemEffect(def)) return;   // gate refused (e.g. ward already active) -> not consumed, RTK's own early-return

        // Charged consumables (RTK ITM_SMOKE: wine/liquor/cigarettes) hold N uses in their durability field
        // with a unit label in ItmText ("sips"/"puffs"). A use spends ONE charge, not the whole item; it is
        // removed only when charges reach 0 -- matching RTK pc_useitem's ITM_SMOKE path (pc.c:2281:
        // dura-=1; dura==0 ? delitem : re-send additem). Old saves may carry an unseeded Dura=0 -> seed here.
        if (def.IsCharged)
        {
            if (it.Dura == 0) it.Dura = def.Durability;
            it.Dura = (ushort)(it.Dura - 1);
            if (it.Dura == 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, 6); }   // 6 = Used
            else SendAddItem(it);   // re-send: the "[N unit]" charge count in the name updates in place (RTK: no minitext)
            MarkDirty();
            return;
        }

        it.Amount -= 1;
        if (it.Amount <= 0) { _char.Inventory.Remove(it); SendDelItem((byte)slot, (byte)(eat ? 2 : 6)); }
        else SendAddItem(it);
        MarkDirty();
    }

    // Runs one consumable's real RTK use-script effect via the data-driven verb/row Lua system
    // (data/game-data/ItemParams.csv names each item's verb + params; data/game-data/item_verbs.lua is the
    // logic; see Server/ItemScript.cs). The verb acts through ItemContext, whose primitives delegate to the
    // Item* methods below — the SAME plumbing the old C# switch used. Gate verbs (ward/hardenbody) check FIRST
    // and skip the eat animation on refusal, matching every reviewed script's guard-before-effect order.
    // Returns false — WITHOUT consuming the item — only when a gate verb refused. Items with no ItemParams row
    // fall back to the item DB's own Vita/Mana columns (almost none carry them). Both files hot-reload via @reload.
    private bool ApplyItemEffect(ItemDef def)
    {
        if (Content.ItemParams.TryGetValue(def.Key, out var row))
        {
            var verb = row.GetValueOrDefault("verb", "");
            var handled = ItemScript.Apply(verb, new ItemContext(this), row);
            if (handled is not null) return handled.Value;   // Lua ran it; false = a gate refused (don't consume)
            // verb missing / Lua error -> fall through to the DB Vita/Mana fallback below (never leaves a use inert-crashed)
        }

        // No effect row (or the Lua path was unavailable): the rare item that actually carries Vita/Mana in the
        // item DB heals by those columns; anything else is an inert consume.
        bool healed = false;
        if (def.Vita > 0) { _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)def.Vita); healed = true; }
        if (def.Mana > 0) { _char.Mp = Math.Min(EffMaxMp, _char.Mp + (uint)def.Mana); healed = true; }
        if (healed) { ItemEatAnim(); SendStats(); }
        return true;
    }

    // ---- Item-effect verb primitives (called by ItemContext; see Server/ItemScript.cs) --------------------
    // Thin wrappers reusing the exact plumbing the old C# ApplyItemEffect switch used, so the Lua route can't
    // drift into a second implementation. (Stat reads level/might/hp/maxHp/mp reuse the shared Lua* accessors
    // defined in Session.Spells.cs; say/message/restoreMana reuse LuaSay/LuaMessage/LuaRestoreMana.)
    internal int ItemArmor => Math.Clamp(_char.Ac - Totals().armor, -80, 70);   // RTK harden-body's clamped armor

    internal void ItemEatAnim()   // the shared eat/use pose + sound, self and peers (RTK action 8)
    {
        SendAction(_char.Id, 8, 40, 0);
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 8, 40, 0), except: this);
        PlayEatSfx();   // the action sprite carries no sound of its own — 403+006 over 0x19 (see EatSfxA/B)
    }

    internal void ItemCastPose() => SendAction(_char.Id, 6, 40, 0);   // harden-body cast pose (self only, as RTK)

    internal void ItemHeal(int amt)
    {
        if (amt <= 0) return;
        _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)amt);
        if (_char.Hp == EffMaxHp) SendMiniText("You feel satiated.");   // RTK: fires whether already full or capped here
        SendStats();
    }

    internal void ItemLoseHp(int amt)   // drink/smoke's small HP cost — never below 1
    {
        if (amt <= 0) return;
        _char.Hp = (uint)Math.Max(1, (int)_char.Hp - amt);
        SendStats();
    }

    internal void ItemKill()   // poison_apple: always-lethal
    {
        _char.Hp = 0;
        SendStats();
        Die();
    }

    internal bool ItemHasStatus(string key)          => HasStatusFlag(key);
    internal void ItemSetStatus(string key, int ms)  => SetStatusFlag(key, ms);
    internal bool ItemChance(int pct)                => Random.Shared.Next(1, 101) <= pct;   // 1..100 <= pct = success
    internal void ItemWarpHome()                     => ReturnToInn();   // RTK returnFunc -> a random tavern in your nation

    // Timed status flags set by USE items whose RTK effect is a plain ward/marker rather than a numeric stat
    // delta (the item_verbs.lua "ward"/"hardenbody" verbs) -- key -> Environment.TickCount64 expiry.
    // Separate from _buffs (which models spell buffs with real Stat/Amount deltas): these carry no stat mod
    // of their own in RTK either (e.g. Spells/common/curse_protection.lua has no recast function at all,
    // just the duration flag), so tracking presence + honoring the re-cast guard IS the full faithful
    // behavior, not a placeholder. Not persisted across a relog, same as _buffs.
    private readonly Dictionary<string, long> _statusFlags = new();
    private bool HasStatusFlag(string key) => _statusFlags.TryGetValue(key, out var exp) && exp > Environment.TickCount64;
    private void SetStatusFlag(string key, int durationMs) => _statusFlags[key] = Environment.TickCount64 + durationMs;

    // Sum of every stat line across all worn gear. Equipment NEVER writes back into the character's base
    // stats (those stay in _char.*); the effective values the client sees are base + these, recomputed on
    // every SendStats / profile / attack. That keeps a relog — which reloads Equipment and redraws it via
    // RefreshInventory — from drifting or double-counting, since nothing was ever baked into the base.
    // Cached so the ~10-slot sum isn't recomputed on every SendStats / RegenTick / ApplyMobHit (×3) / Lua stat
    // read. Equipment changes rarely; InvalidateEquipTotals() clears it at each mutation site (equip/unequip/
    // break). NOT keyed on durability — EquipTotals sums def stat lines, which dura decay never touches.
    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam)? _equipTotals;
    private void InvalidateEquipTotals() => _equipTotals = null;

    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam) EquipTotals()
    {
        if (_equipTotals is { } cached) return cached;
        int hp = 0, mp = 0, mt = 0, wl = 0, gr = 0, ar = 0, ht = 0, dm = 0;
        foreach (var e in _char.Equipment)
        {
            var def = Content.ItemById(e.ItemId); if (def is null) continue;
            hp += def.Vita; mp += def.Mana; mt += def.Might; wl += def.Will; gr += def.Grace;
            ar += def.Armor; ht += def.Hit; dm += def.Dam;
        }
        var t = (hp, mp, mt, wl, gr, ar, ht, dm);
        _equipTotals = t;
        return t;
    }

    // A weapon's real swing range, summed across worn gear like EquipTotals (RTK pc_calcstat sums
    // itemdb_minSdam/maxSdam/minLdam/maxLdam over every equip slot, same loop as Armor/Hit/Dam — it isn't
    // weapon-slot-only, though in practice only weapons carry nonzero values). Bare-handed is (0,0,0,0):
    // matches RTK, where an unarmed player still swings via the dam/might/class terms in PlayerSwingDamage,
    // just weaker. Previously unparsed entirely — Items.csv carries these columns but ItemDef never read
    // them, so player melee had no real damage-range component at all (see PlayerSwingDamage).
    private (int minSDam, int maxSDam, int minLDam, int maxLDam) WeaponTotals()
    {
        int minS = 0, maxS = 0, minL = 0, maxL = 0;
        foreach (var e in _char.Equipment)
        {
            var def = Content.ItemById(e.ItemId); if (def is null) continue;
            minS += def.MinSDam; maxS += def.MaxSDam; minL += def.MinLDam; maxL += def.MaxLDam;
        }
        return (minS, maxS, minL, maxL);
    }

    // RTK swingDamage.lua's per-class flat bonus (_classFactors, 1-indexed by baseClass+1): only Warrior
    // and Rogue get one; Peasant/Mage/Poet don't (magic users deal their real damage through spells, not
    // melee). pathId -1 (no class chosen yet) falls through to the Peasant case.
    /// <summary>Flat path bonus added to the raw swing. MEASURED AT 0 (2026-08-02) — the
    /// 9 (Warrior) / 7.5 (Rogue) from Rogue Tutor Melalye's post does not apply in this era.
    /// Derivation: with mob AC known independently (the Spark fixed-damage probe, base ~55.5),
    /// <c>K = observed/(1+ac/100) - (s/2 + dam*2.5 + might/8)</c> must be CONSTANT across mobs
    /// since it's a property of the attacker. Across five mobs spanning AC 65-95 it came out
    /// -0.71/-0.71/-0.62/-0.41/-0.31 (mean -0.55; +0.5 integer-floor bias => ~0), i.e. the rest
    /// of the formula is exact and there is no room for a +7.5 term. Two contaminants had to be
    /// removed first, both of which fake a positive K: the positional x2 (flee-prone mobs turn
    /// their backs, so Mouse showed 22% doubled hits vs Bat's 6%) and mob-label/look mixing.
    /// WHY IT MATTERED: this is a FLAT add, so at level 1 it dwarfs everything else — a wooden
    /// saber's whole raw swing is ~6.6, so +9 was +136% and one-shot an 18hp squirrel. By level
    /// 99 it's noise. That asymmetry is exactly why the game felt wrong only in the early game.
    /// Rogue is measured; WARRIOR IS INFERRED — no warrior data exists, but its 9 comes from the
    /// same post as the disproven 7.5, so it is not evidence either. Re-measure with a warrior.</summary>
    private static double ClassFactor(int pathId) => pathId switch { _ => 0 };

    /// <summary>The Might contribution to a raw swing. TWO REGIMES, because the published formula is a
    /// local linearization (see nexustk-published-formulas-are-endgame-fits):
    /// <list type="bullet">
    /// <item>Low might: <c>might/8</c> with NO intercept. LIVE-MEASURED at might 12 — the non-weapon term
    /// falls in [1.368, 1.842) and might/8 = 1.500 lands inside. (The archive's other low-might rule,
    /// "1 damage per 5 Might", gives 2.4 and is OUTSIDE that bracket, so it is not the low-end law.)</item>
    /// <item>High might: <c>might/8 + 8.8125</c>, the Klanx/Yari nmail formula, explicitly qualified
    /// "For Mights of 70 or greater". At might 130 this gives 25.1, reproducing the archive's worked
    /// example of 26 — which is what proves 8.8125 is a tangent-line INTERCEPT, not a flat bonus.</item>
    /// </list>
    /// No single line fits both ends (a through-origin fit needs slope ≤0.1535 at might 12 but 0.2 at 130),
    /// so the two regimes are real, not a measurement artifact. The 40→70 ramp between them is pure
    /// INTERPOLATION — we have zero data in that band and no source describes the transition. It is chosen
    /// only to be continuous at both ends and to keep the intercept away from the measured low-might
    /// bracket (starting the ramp at 0 would give might*0.25 = 3.0 at might 12, outside it).
    /// Applying the intercept unconditionally is what one-shot starter mobs; omitting it entirely
    /// under-powers endgame by ~35% of the Might term. Re-measure once a character reaches might 40+.</summary>
    private static double MightTerm(double might)
    {
        double term = might / 8.0;
        if (might > 40) term += 8.8125 * Math.Min(1.0, (might - 40) / 30.0);
        return term;
    }

    // The player's real melee formula (RTK swingDamage.lua _getPlayerSwingDamage + the shared armor/
    // positional resolution in swingDamage() itself), replacing the old flat EffMight-based stand-in.
    // Returns the final damage AND whether it crit (for the 0x13 visual byte at the call site).
    //   s               = weapon's Small swing range. The Large (L) range is INERT in the real 4.95 game —
    //                      every live-server tutor post says only S is used ("L makes no difference on large
    //                      enemies, only S is working currently"), so we never read minLDam/maxLDam.
    //   dam/might        = gear/buff Dam total and effective Might, each floored at 1
    //   classFactor      = ClassFactor above
    //   enchant          = EffEnchant — multiplies ONLY the raw weapon-swing term (s/2), 1 normally, up to
    //                      9x (Spirit Blade) while an enchant tier is active (Session.CastEnchant). The
    //                      ladder is archive-corroborated — see Content.EnchantSpells for sources+caveats.
    //   rage             = EffRage — 1 normally, up to 5x while a Fury tier is active (Session.CastRage)
    //   invisible        = 5 while Stealthed (Session.CastStealth), else 1 — a one-shot sneak-attack burst
    //                      (tswolf 8/2001, era-matched to the 4.95 client: "Invisible increases attack by 5
    //                      times"; later boards' 9x is a post-4.95 rebalance); landing this hit strips the
    //                      stealth immediately after (RTK "attacking breaks it")
    //   critical         = 3 on a crit (Combat.RollPlayerSwingRtk's 3% roll), else 1
    // The hit/crit roll (Combat.RollPlayerSwingRtk) happens FIRST: a failed roll is
    // a genuine MISS and returns (0, false) — the caller renders a whiff and a miss deals no damage, deducts
    // no durability, and does NOT break stealth (RTK drops invis only on a NONZERO hit). Then, on a landed
    // hit: armor deduction against the TARGET's Ac (mob-target floor -95), then ONE positional bonus of at
    // most x2 (armor BEFORE position, as swingDamage.lua orders it) — "attacked from behind while both face
    // the same way", Combat.IsBehindTarget, always live. RTK's Lua ran backstab/flank as separate sequential
    // if-blocks that could compound; we do NOT, because no source supports stacking and the board post gives
    // behind-x2 as a single universal rule. Backstab/Flank no longer multiply at all — they RETARGET, at
    // reduced damage, via the `reach` scale below (see Session.SwingTargets).
    //   reach = 1.0 on an ordinary faced-tile swing; 0.5 when Backstab reaches the rear tile or Flank
    //           reaches a side one. Applied LAST, after armor and after the positional x2, because the
    //           source states it as a fraction of "the front attack damage" — i.e. of the whole result.
    //           Spell-driven attacks (lethal strike etc.) always pass the default 1.0.
    private (int dmg, bool crit) PlayerSwingDamage(Mob target, double reach = 1.0)
    {
        var eq = Totals();
        int pathId = Content.PathIdForClass(_char.ClassName);
        double might = Math.Max(EffMight, 1);

        // Hit/crit roll first — a miss is a real whiff (0 damage). LIVE-VALIDATED clamped-RTK grace/level
        // formula (Combat.RollPlayerSwingRtk): hit chance keys off the LEVEL+GRACE gap, NOT AC. AC stays a
        // pure damage-reduction term below (ApplyArmor). Replaced the old AC-based Astrael regression, which the
        // 7.x combat tap proved wrong (it hit ~88-100% vs mobs where live is ~50-63%). pathId/HitBase/might
        // no longer feed hit chance — kept only for the damage terms.
        var outcome = Combat.RollPlayerSwingRtk(_char.Grace + eq.grace, _char.Level, _char.Hit + eq.hit,
                                                target.Grace, target.Level);
        if (outcome == Combat.SwingOutcome.Miss) return (0, false);
        bool crit = outcome == Combat.SwingOutcome.Crit;

        var w = WeaponTotals();
        int lo = w.minSDam, hi = w.maxSDam;   // S only — L is inert in 4.95 (see formula note above)
        int s = lo >= hi ? lo : Random.Shared.Next(lo, hi + 1);
        // NO floor to 1. LIVE-MEASURED 2026-08-02: a level-1 peasant with a 0-dam wooden saber
        // (S 5m10) hits an AC-100 squirrel for 4-8. The floored term alone contributes 2.5 raw
        // = 5 damage after that mob's x2 armor — more than half the observed hit, and it pushed
        // the predicted range to 10-14. A 0-dam weapon must contribute 0.
        // The term stays LINEAR (dam * 2.5) rather than the step floor(dam/2)*5 that "every 2
        // dam adds 5 more damage" also permits: the level-18 rogue data (Swift sword, dam=1)
        // only reconciles to classFactor ~0 if dam=1 contributes 2.5, not 0. So the rate reading
        // is right and it was only the artificial floor that was wrong.
        double dam = eq.dam;
        double classFactor = ClassFactor(pathId);
        bool wasStealthed = Stealthed;   // read once — landing the hit clears it below

        double swing = (s / 2.0 * EffEnchant + dam * 2.5 + MightTerm(might) + classFactor) * EffRage * (wasStealthed ? 5 : 1) * (crit ? 3 : 1);
        // Pass the RAW DOUBLE into ApplyArmor — do NOT truncate here. Flooring twice (once to int, once
        // inside ApplyArmor) is disproven live; see the ApplyArmor doc comment for the n=178 evidence.
        int dmg = Combat.ApplyArmor(swing, target.Ac, floor: -95);   // RTK minimumArmor for a mob target
        // POSITIONAL BONUS — AT MOST x2, EVER. This is deliberately ONE decision feeding ONE multiply
        // rather than the old pair of independent `if (...) dmg *= 2;` lines, which could in principle
        // compound to x4. They never actually did (IsBehindTarget requires attackerDir == targetDir while
        // IsBackstabAngle requires the OPPOSITE facing, so the two are mutually exclusive), but nothing
        // stated or enforced that — one edit to either table would have silently produced x4.
        //
        // NOTE the direction passed is Combat.AttackDir, NOT _facing: with the Flank stance the blow lands
        // on a SIDE tile, and these rules are about the geometry of the blow. On an ordinary swing the two
        // are identical, so this changes nothing outside flank — but it is what makes "flank works the same
        // as turning and swinging" actually true, including earning the rear x2 off a turned back.
        //
        // The Backstab/Flank STANCES are deliberately absent from this decision. They are targeting spells
        // (Session.SwingTargets) — "Strikes an enemy behind you" / "Enables to Attack to the Warrior's Sides"
        // — not damage ones, and Combat.IsBackstabAngle/IsFlankAngle are retired. Do not re-add them here.
        byte attackDir = Combat.AttackDir(_char.X, _char.Y, target.X, target.Y);
        if (Combat.IsBehindTarget(attackDir, target.Dir, _char.X, _char.Y, target.X, target.Y)) dmg *= 2;

        // Reach penalty LAST — the sources state it as a fraction of "the front attack damage", i.e. of the
        // finished number, so it comes after armor and after the positional x2. Still floored at 1: a
        // connected hit never deals 0 (that is what a MISS is, and it returned above).
        if (reach != 1.0) dmg = Math.Max(1, (int)Math.Floor(dmg * reach));

        if (wasStealthed) { _stealthUntil = 0; RevertStealth(); }   // RTK: landing a hit strips stealth (removeDuras(invis)) — drop the faded look now

        return (dmg, crit);
    }

    // Active timed stat buffs (from casting Buff spells). Session-local, like cooldowns — they clear on relog.
    // Each carries the stat it boosts, the amount, and the tick it expires at. Expired ones are pruned on read.
    // Category groups a status into a MUTUALLY-EXCLUSIVE family (RTK spellTables.lua: curses/venoms/minorcurses/
    // protections). Positive buffs leave it "". Only one status per non-empty category can be active at once — a
    // curse spell is blocked if the target already has one of the same category (that guard is the whole point of
    // self-pestilence: occupy your own 'curses' slot with a mild curse so an enemy can't land a worse one). Cure
    // spells remove statuses BY category. See nexustk-495-curse-status-system.
    private sealed class ActiveBuff { public string Stat = ""; public int Amount; public long Expires; public string Key = ""; public string Name = ""; public string Category = ""; }
    private readonly List<ActiveBuff> _buffs = new();

    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam) BuffTotals()
    {
        long now = Environment.TickCount64;
        int hp = 0, mp = 0, mt = 0, wl = 0, gr = 0, ar = 0, ht = 0, dm = 0;
        // Don't remove expired buffs here — Session.ExpireBuffs (RegenTick) is the single removal point (so the
        // fade line fires exactly once); just skip any that have lapsed but aren't swept yet.
        foreach (var b in _buffs) { if (b.Expires <= now) continue; switch (b.Stat)
        {
            case "hp": case "maxhp": hp += b.Amount; break;
            case "mp": case "maxmp": mp += b.Amount; break;
            case "might": mt += b.Amount; break;
            case "will":  wl += b.Amount; break;
            case "grace": gr += b.Amount; break;
            case "armor": ar += b.Amount; break;
            case "hit":   ht += b.Amount; break;
            case "dam":   dm += b.Amount; break;
        } }
        return (hp, mp, mt, wl, gr, ar, ht, dm);
    }

    // Gear + active timed buffs: the full bonus layered on the character's base stats. Everything the client
    // sees (HUD, profile) and every derived calc (heals, melee) reads through this so buffs are reflected live.
    private (int hp, int mp, int might, int will, int grace, int armor, int hit, int dam) Totals()
    {
        var e = EquipTotals(); var b = BuffTotals();
        return (e.hp + b.hp, e.mp + b.mp, e.might + b.might, e.will + b.will,
                e.grace + b.grace, e.armor + b.armor, e.hit + b.hit, e.dam + b.dam);
    }

    // Effective (base + gear + buffs) caps/attributes used by the HUD, heals and melee. AC is signed and LOWER
    // is better in TK, so armor SUBTRACTS from it.
    private uint EffMaxHp => (uint)Math.Max(1, (int)_char.MaxHp + Totals().hp);
    private uint EffMaxMp => (uint)Math.Max(0, (int)_char.MaxMp + Totals().mp);
    private int  EffMight => Math.Clamp(_char.Might + Totals().might, 0, 255);

    /// <summary>Path/class + subpath-rank restriction on USING an item at all — worn or swallowed (RTK
    /// <c>pc_useitem</c>, pc.c:1998).
    /// <para><b>Why here and not in <see cref="EquipFromSlot"/>:</b> RTK splits its item gates by what they are
    /// ABOUT, not by opcode. Path and mark are properties of the item's own identity, so they sit on the use
    /// funnel and cover consumables as well as gear — 17 non-equip items carry an ItmPthId (a Rogue's stealth
    /// powder/explosives, a Warrior's shard and shatter bombs, holy dust, and the eight rogue darts; none are
    /// ItmThrown, so the use path really is how they're spent). Level/might/sex, the 2-handed-vs-shield pairing
    /// and the cursed-stat floor are instead about putting a thing ON YOUR BODY — they need the equipment slots
    /// to reason about — so those stay in <c>pc_canequipitem</c>/EquipFromSlot. Collapsing the two would either
    /// leak body-slot checks onto food or drop the class gate off half the items that carry one.</para>
    /// ItmPthId 0 means anyone may use it (2058/2545 items, so most content is unrestricted); 1..5 names a BASE
    /// path and is satisfied by every subpath under it (a Chung ryong still counts as a Warrior — RTK compares
    /// <c>classdb_path</c> of the player's class, not the class id itself); 6+ names ONE exact subpath class. A
    /// restricted item additionally requires the player's subpath mark to have reached ItmMark.
    /// Dreamweavers/Archons (base path 5) and our own GM accounts skip the whole check, matching RTK's
    /// `classdb_path(class) == 5` branch.
    /// <para>469 of the 1241 wearable items carry a path restriction and 146 a mark — none of it was enforced
    /// before (ItmPthId/ItmMark weren't even parsed), which is why a Poet could wear a Warrior's sword.</para>
    /// Refusals are RTK's own <c>map_msg</c> lines, sent as system minitext like every other use/equip refusal.
    /// <para>Deliberately NOT applied to the throw path (0x17): RTK's <c>clif_parsethrow</c> doesn't route
    /// through pc_useitem and has no path check either, and no path-restricted item is ItmThrown anyway.</para></summary>
    private bool CanUsePath(ItemDef def)
    {
        if (def.PathId == 0) return true;                       // unrestricted
        if (IsGm) return true;                                  // RTK: GMs get no errors
        int myClass = CharClassId;                              // -1 when ClassName matches no Paths row
        int myBase  = myClass < 0 ? 0 : Content.PathBaseOf(myClass);
        if (myBase == 5) return true;                           // Dreamweaver/Archon
        bool ok = def.PathId < 6 ? myBase == def.PathId : myClass == def.PathId;
        if (!ok) { SendMiniText("Your path forbids it."); return false; }
        // RTK MAP_ERRITMMARK reuses the level message verbatim ("You need more experience."); say which rank
        // instead, since nothing in the client explains that a subpath mark is what's missing. Only equipment
        // carries a mark in the live registry (146 rows, all wearable), so "wear" is always the right verb.
        if (_char.Mark < def.Mark) { SendMiniText($"You must bear the {MarkName(def.Mark)} mark to wear {def.Name}."); return false; }
        return true;
    }

    /// <summary>Subpath rank name for a mark number (RTK Paths.PthMark1..5 — the "Il san (W)" column family,
    /// class suffix dropped since the mark itself is class-agnostic here).</summary>
    private static string MarkName(int mark) => mark switch
    {
        1 => "Il san", 2 => "Ee san", 3 => "Sam san", 4 => "Sa san", 5 => "Oh san", _ => $"rank-{mark}"
    };

    // Move a bag item onto the body: bumps any item already in that gear slot back to the bag first.
    private void EquipFromSlot(int slot)
    {
        // RTK pc_equipitem gates on player state before anything else (dead/mounted can't change gear).
        // Every refusal below is RTK clif_sendminitext (system message) -- pc_equipitem's state checks
        // (line ~1551/1557), pc_canequipitem's sex/level/might via map_msg[ret].message (line ~1575), and
        // pc_canequipstats's cursed-stat check (line ~1585) -- never a spoken clif_sendmsg chat bubble.
        if (_char.Hp == 0) { SendMiniText("Spirit's can't do that."); return; }
        if (_char.Mounted) { SendMiniText("You can't do that while riding a horse."); return; }
        var it = InvAt(slot); if (it is null) return;
        var def = Content.ItemById(it.ItemId); if (def is null || !def.IsEquip) return;
        // Wear requirements (RTK item_data): sex-locked gear, a minimum level, and a minimum MIGHT (checked
        // against effective might so already-worn +might gear counts).
        // ItmSex: 0 = male-only, 1 = female-only, 2 = UNISEX (the common case — 1944/2545 items, incl. most
        // weapons). Character.Sex uses the same 0=M/1=F encoding, so a sex-locked item (0 or 1) must match;
        // anything >= 2 is unrestricted. (The old `!= 0` test wrongly blocked every unisex item.)
        if (def.Sex < 2 && def.Sex != _char.Sex) { SendMiniText($"You can't wear {def.Name}."); return; }
        if (def.Level > _char.Level) { SendMiniText($"You must be level {def.Level} to wear {def.Name}."); return; }
        if (def.MightReq > EffMight) { SendMiniText($"You need {def.MightReq} might to wear {def.Name}."); return; }
        // (The path/mark gate is NOT here — it's on the use funnel, HandleUseItem. See CanUsePath.)
        // Two-handed weapon vs shield (RTK pc_canequipitem, MAP_ERRITM2H): a weapon whose LOOK falls in
        // 10000..29999 is two-handed art, and the client has no way to draw it alongside a shield. Blocks the
        // pairing from BOTH directions — putting a shield on over a 2H weapon, and a 2H weapon on over a shield.
        bool wearing2H = _char.Equipment.FirstOrDefault(e => e.Slot == 1) is { } wep
                         && Content.ItemById(wep.ItemId) is { Look: >= 10000 and <= 29999 };
        bool wearingShield = _char.Equipment.Any(e => e.Slot == 3);
        if ((def.Type == 5 && wearing2H) || (def.Type == 3 && def.Look is >= 10000 and <= 29999 && wearingShield))
        { SendMiniText("You cannot equip a 2-handed weapon with a shield."); return; }
        // Cursed/malus gear (negative Vita/Mana): RTK pc_canequipstats blocks it if the penalty would exceed
        // your current effective max — it'd zero out the pool entirely. 14/19 items in the registry carry a
        // negative Vita/Mana line, so this is reachable, not theoretical.
        if (def.Vita < 0 && -def.Vita > EffMaxHp) { SendMiniText("You lack the health required to wield that."); return; }
        if (def.Mana < 0 && -def.Mana > EffMaxMp) { SendMiniText("You lack the wisdom required to wield that."); return; }
        byte wire = def.EquipSlot;
        // Rings/gauntlets are all Type 7 (wire slot 7 = left ring) but share TWO interchangeable slots — 7 and
        // 8 (right ring). Wear the second one in the free right slot instead of replacing the left. Only when
        // BOTH are taken does a new ring replace the left. (Slot 8 carries no items in the data, so it's only
        // ever filled by this path.)
        if (wire == 7 && _char.Equipment.Any(e => e.Slot == 7) && _char.Equipment.All(e => e.Slot != 8))
            wire = 8;

        _char.Inventory.Remove(it);
        SendDelItem((byte)slot, 6);                   // reason 6 = Used (RTK pc_equipscript: pc_delitem(..., 1, 6) — same code as ITM_USE, not a "removed" reason 0)

        var prev = _char.Equipment.FirstOrDefault(e => e.Slot == wire);
        bool replacing = prev is not null;   // swapping over worn gear sounds different than dressing a bare slot (GearSfx)
        if (prev is not null)
        {
            _char.Equipment.Remove(prev);
            SendUnequip(wire);
            var pdef = Content.ItemById(prev.ItemId);
            if (pdef is not null) { ApplyAppearance(pdef, equip: false); GiveItem(pdef, 1, prev.Dura, prev.CustomName); }
        }

        var worn = new InvItem(wire, def.Id, 1, it.Dura == 0 ? def.Durability : it.Dura) { CustomName = it.CustomName };
        _char.Equipment.Add(worn);
        InvalidateEquipTotals();                      // gear changed (this add + any prev swap above)
        SendEquip(worn);
        ApplyAppearance(def, equip: true);
        SendStats();                                  // push the new gear bonuses to the HUD
        PlayGearSfx(wire, equipping: true, replacing: replacing);
        MarkDirty();
        // (No "Equipped X" over-head bubble — the paperdoll + gear stats are feedback enough; SendLog here
        // spoke it as 0x0D chat over the character, which the player didn't want.)
    }

    // 0x1F unequip: dec[0]=wire equip-slot byte. Take the worn item off and return it to the bag.
    private void HandleUnequip(byte[] dec)
    {
        if (dec.Length < 1) return;
        byte wire = dec[0];
        var worn = _char.Equipment.FirstOrDefault(e => e.Slot == wire);
        if (worn is null) return;
        _char.Equipment.Remove(worn);
        InvalidateEquipTotals();
        SendUnequip(wire);
        var def = Content.ItemById(worn.ItemId);
        if (def is not null) { ApplyAppearance(def, equip: false); GiveItem(def, 1, worn.Dura, worn.CustomName); }
        SendStats();                                  // drop the gear bonuses from the HUD
        PlayGearSfx(wire, equipping: false);
        MarkDirty();
    }

    // Typed-"A" bulk unequip: strips every worn slot back into the bag, same per-item plumbing as
    // HandleUnequip (SendUnequip + appearance revert + GiveItem). Stops the moment the bag can't take the
    // next item back — GiveItem already sends "Your pack is full." and leaves that item (and everything
    // after it) equipped, rather than dropping it on the ground or destroying it.
    private void UnequipAll()
    {
        foreach (var worn in _char.Equipment.ToList())
        {
            var def = Content.ItemById(worn.ItemId);
            if (def is not null && !GiveItem(def, 1, worn.Dura, worn.CustomName)) break;   // bag full — stop, leave the rest equipped
            _char.Equipment.Remove(worn);
            InvalidateEquipTotals();
            SendUnequip(worn.Slot);
            if (def is not null) ApplyAppearance(def, equip: false);
            PlayGearSfx(worn.Slot, equipping: false);
        }
        SendStats();
        MarkDirty();
    }

    // ---- durability decay / breakage (RTK clif_deductweapon/deductarmor/checkdura, clif.c:6646-6844) -----
    // On landing or taking a hit, each relevant equipped slot has a ~49% chance (rnd(100) > 50) to lose 1
    // point of durability. Indestructible gear and gear with no Durability rating never decays. Durability
    // loss is disabled entirely on PvP maps (RTK: "disable dura loss from mobs on pvp map").

    /// <summary>Roll durability loss for one worn item, warning at 50/25/10/5/1% and destroying it at 0.</summary>
    private void DeductDura(InvItem worn)
    {
        if (Content.IsPvpMap(_char.Map)) return;
        var def = Content.ItemById(worn.ItemId);
        if (def is null || def.Indestructible || def.Durability == 0) return;
        if (worn.Dura == 0) worn.Dura = def.Durability;   // lazily fill (equip already does this; belt-and-suspenders)
        if (Random.Shared.Next(100) <= 50) return;        // RTK: rnd(100) > 50 triggers the deduction
        worn.Dura = (ushort)Math.Max(0, worn.Dura - 1);
        MarkDirty();   // covers CheckDura's own equipment mutations too (a Repair-threshold flag, or BreakItem)
        CheckDura(worn, def);
    }

    /// <summary>RTK clif_checkdura: fire each threshold warning at most once (tracked by worn.Repair), then
    /// destroy the item once its durability bottoms out.</summary>
    private void CheckDura(InvItem worn, ItemDef def)
    {
        double pct = (double)worn.Dura / def.Durability;
        // RTK clif_checkdura sends these through clif_sendmsg(sd, 5, buf) -- type 5 "System", the same
        // 0x0A minitext packet as clif_sendminitext (type 3) just tagged differently -- not the chat log.
        if (pct <= .50 && worn.Repair == 0) { SendMiniText($"Your {def.Name} is at 50%.", type: 5); worn.Repair = 1; }
        if (pct <= .25 && worn.Repair == 1) { SendMiniText($"Your {def.Name} is at 25%.", type: 5); worn.Repair = 2; }
        if (pct <= .10 && worn.Repair == 2) { SendMiniText($"Your {def.Name} is at 10%.", type: 5); worn.Repair = 3; }
        if (pct <= .05 && worn.Repair == 3) { SendMiniText($"Your {def.Name} is at 5%.",  type: 5); worn.Repair = 4; }
        if (pct <= .01 && worn.Repair == 4) { SendMiniText($"Your {def.Name} is at 1%.",  type: 5); worn.Repair = 5; }
        if (worn.Dura <= 0) BreakItem(worn, def);
    }

    /// <summary>RTK clif.c:6805 onward: the item is gone for good — unequipped, appearance reverted, stats
    /// recalculated.</summary>
    private void BreakItem(InvItem worn, ItemDef def)
    {
        SendMiniText($"Your {def.Name} was destroyed!", type: 5);   // RTK clif_checkdura: type 5 "System"
        _char.Equipment.Remove(worn);
        InvalidateEquipTotals();
        SendUnequip(worn.Slot);
        ApplyAppearance(def, equip: false);
        SendStats();
    }

    /// <summary>RTK's "protected" charge (clif_checkdura / clif_checkinvbod): instead of breaking, the item is
    /// restored to full durability and one charge is spent. No row in the live registry sets ItmProtected and
    /// nothing grants a per-instance charge yet, so this never fires today — it exists so the break paths below
    /// can be written the way RTK writes them rather than silently dropping the branch.</summary>
    private bool TryProtectFromBreak(InvItem it, ItemDef def)
    {
        if (!def.Protected && it.Protected == 0) return false;
        if (it.Protected > 0) it.Protected--;
        it.Dura = def.Durability;
        it.Repair = 0;                                       // full dura -> the warning ladder starts over
        SendMiniText($"Your {def.Name} has been restored!", type: 5);
        return true;
    }

    // ---- death penalties: gear (RTK player.lua deathDuraLoss -> clif_deductduraequip + clif_checkinvbod) ----

    /// <summary>Dying batters every worn slot (RTK <c>clif_deductduraequip</c>, clif.c:6846): a flat 10% of the
    /// item's MAX durability off each, through the same warning ladder as ordinary wear, and anything that
    /// bottoms out breaks. Break-on-death gear (ItmBoD, 77 items) is destroyed outright regardless of its
    /// durability. Skipped entirely on a PvP map, like every other durability loss (RTK's early
    /// <c>if (map[..].pvp) return</c>).</summary>
    private void DeathDuraLoss()
    {
        if (Content.IsPvpMap(_char.Map)) return;
        foreach (var worn in _char.Equipment.ToArray())
        {
            var def = Content.ItemById(worn.ItemId);
            if (def is null || def.Indestructible) continue;          // "ethereal" gear never decays
            bool rated = def.Durability > 0;                          // unrated gear can't wear down — only BoD kills it
            if (rated)
            {
                if (worn.Dura == 0) worn.Dura = def.Durability;
                worn.Dura = (ushort)Math.Max(0, worn.Dura - def.Durability / 10);
            }
            if (def.BreakOnDeath || (rated && worn.Dura == 0))
            {
                if (TryProtectFromBreak(worn, def)) continue;
                BreakItem(worn, def);
            }
            else if (rated) CheckDura(worn, def);                     // 50/25/10/5/1% warnings only
        }
        MarkDirty();
    }

    /// <summary>Break-on-death items sitting in the BAG (RTK <c>clif_checkinvbod</c>, clif.c:6968) — a separate
    /// pass from the worn one above, and the reason a spare BoD weapon in your pack is no safer than the one in
    /// your hand. Unlike the gear pass this ignores durability entirely: only the ItmBoD flag matters.</summary>
    private void DeathInventoryBod()
    {
        foreach (var it in _char.Inventory.ToArray())
        {
            var def = Content.ItemById(it.ItemId);
            if (def is null || !def.BreakOnDeath) continue;
            if (TryProtectFromBreak(it, def)) continue;
            _char.Inventory.Remove(it);
            SendDelItem((byte)it.Slot, 13);                           // reason 13 = Broke (RTK clif_senddelitem table)
            SendMiniText($"Your {def.Name} was destroyed!", type: 5);
        }
        MarkDirty();
    }

    /// <summary>How long a death pile stays reserved for the player it fell off (RTK player.lua
    /// <c>canLoot</c>/<c>isYours</c>: <c>os.time() >= item.timer + 300</c>). After this it is ordinary floor
    /// loot anyone may take.</summary>
    private const int DeathPileLockMs = 300_000;   // 300 s

    /// <summary>The death pile (RTK player.lua <c>deathPileDrop</c>): every bag slot independently coin-flips,
    /// and on heads a droppable stack is spilled onto the corpse tile. Bound gear (ItmDroppable, our NoDrop)
    /// stays with you — RTK's <c>if not item.droppable</c>. Every stack is LOOTER-LOCKED to the dead player for
    /// <see cref="DeathPileLockMs"/> (RTK's <c>groundItemsToCurse[i].looters = {player.ID}</c>), which is what
    /// makes F1 "Recover Death Pile" worth having: nobody else can take it, and the owner can pull it back from
    /// two tiles off even with someone parked on top.
    /// <para>One deliberate narrowing: RTK curses every BL_ITEM already lying in the cell, so dying on top of a
    /// stranger's dropped loot silently reserves theirs too. Only what actually fell off this corpse is locked
    /// here.</para></summary>
    private void DeathPileDrop()
    {
        bool dropped = false;
        foreach (var it in _char.Inventory.ToArray())
        {
            var def = Content.ItemById(it.ItemId);
            if (def is null || def.NoDrop) continue;
            if (Random.Shared.Next(2) != 0) continue;                 // RTK: math.random(1,2) == 1
            _char.Inventory.Remove(it);
            SendDelItem((byte)it.Slot, 1);                            // reason 1 = Drop
            _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = def.Id,
                X = _char.X, Y = _char.Y, Amount = it.Amount, Dura = it.Dura, Graphic = def.Icon, CustomName = it.CustomName,
                LooterId = _char.Id, LockedUntil = Environment.TickCount64 + DeathPileLockMs });
            dropped = true;
        }
        if (dropped) SendMiniText("Your items are ripped from your body.");
        MarkDirty();
    }

    /// <summary>Coin spill on death (RTK player.lua <c>deathDropGold</c>): a random 5–35% of the purse lands on
    /// the corpse tile as one pile, tiered to the same coin icons as a manual gold drop. Looter-locked like the
    /// item pile — RTK drops the gold first and then curses the whole cell, so the coins are covered too.</summary>
    private void DeathDropGold()
    {
        if (_char.Coins == 0) return;
        uint amt = (uint)(_char.Coins * Random.Shared.Next(5, 36) / 100);
        if (amt == 0) return;
        _char.Coins -= amt;
        ushort gfx = amt < 2 ? (ushort)22 : amt < 100 ? (ushort)73 : (ushort)72;
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = -1,
            X = _char.X, Y = _char.Y, Amount = (int)amt, Graphic = gfx,
            LooterId = _char.Id, LockedUntil = Environment.TickCount64 + DeathPileLockMs });
        SendMiniText($"You dropped {amt:N0} coins.");
        MarkDirty();
    }

    /// <summary>F1 "Recover Death Pile" (RTK player.lua <c>recoverDeathPile</c>): sweep your own tile and the
    /// two tiles you're FACING, and pull back every stack still looter-locked to you — coins to the purse,
    /// items to the bag. Reads like a two-tile Filch that can only ever take your own pile; anything unlocked,
    /// or locked to someone else, is left where it lies. Returns how many stacks came back, so the caller can
    /// tell "nothing here" from "recovered". Stops early if the pack fills (RTK says as much in its own help
    /// text: "If you do not have enough room in your inventory, you will be unable to recover all of your
    /// items.") — whatever is left stays locked on the floor for the rest of the grace period.</summary>
    internal int RecoverDeathPile()
    {
        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        int taken = 0;
        // Own tile first — a corpse normally drops the pile underfoot — then 1 and 2 tiles ahead.
        for (int step = 0; step <= 2; step++)
        {
            int tx = _char.X + dx * step, ty = _char.Y + dy * step;
            if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) break;
            while (true)
            {
                var gi = _world.PickUp(_char.Map, tx, ty, _char.Id, ownOnly: true);
                if (gi is null) break;                                  // nothing of ours left on this tile
                if (gi.ItemId < 0) { _char.Coins += (uint)gi.Amount; SendStats(); taken++; continue; }
                var def = Content.ItemById(gi.ItemId);
                if (def is null) continue;
                if (!GiveItem(def, gi.Amount, gi.Dura, gi.CustomName))
                {
                    // Pack full (GiveItem already said so). Put it back exactly as it was, lock intact.
                    _world.DropItem(_char.Map, gi);
                    MarkDirty();
                    return taken;
                }
                taken++;
            }
        }
        if (taken > 0) MarkDirty();
        return taken;
    }

    /// <summary>Whether anything within reach of <see cref="RecoverDeathPile"/> is still locked to us — the
    /// look-before-you-leap the F1 branch does to decide between the help text and the actual recovery (RTK
    /// f1npc.lua's <c>deathPileFound</c> pre-scan).</summary>
    internal bool DeathPileInReach()
    {
        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        foreach (var gi in _world.ItemsOn(_char.Map))
            if (gi.BelongsTo(_char.Id))
                for (int step = 0; step <= 2; step++)
                    if (gi.X == _char.X + dx * step && gi.Y == _char.Y + dy * step) return true;
        return false;
    }

    // 0x24 drop gold: dec[0..3]=amount(u32BE). Spill coins onto my tile as a pickup-able gold pile.
    private void HandleDropGold(byte[] dec)
    {
        if (dec.Length < 4) return;
        // RTK clif_parsedropgold gates on player state first (dead/mounted can't drop gold).
        if (_char.Hp == 0) { SendMiniText("Spirits can't do that."); return; }
        if (_char.Mounted) { SendMiniText("You cannot do that while riding a horse."); return; }
        uint amt = (uint)((dec[0] << 24) | (dec[1] << 16) | (dec[2] << 8) | dec[3]);
        if (amt > _char.Coins) amt = _char.Coins;
        if (amt == 0) { SendLog("You have no coins to drop."); return; }
        _char.Coins -= amt;
        SendStats();
        MarkDirty();
        ushort gfx = amt < 2 ? (ushort)22 : amt < 100 ? (ushort)73 : (ushort)72;   // coins_1 / _2_99 / _100_999 icons
        _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = -1,
            X = _char.X, Y = _char.Y, Amount = (int)amt, Graphic = gfx });
    }

}
