using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    /// <summary>
    /// Best-effort world-entry burst, extrapolated from the RTK 6.x/7.x reference sequence
    /// (intif.c char-load callback). ROUGH: formats/increments/flags are first-pass guesses —
    /// each packet is logged so we can see exactly where the client stops or reacts.
    /// </summary>
    private void SendWorldEntry()
    {
        Log.Info($"   == WORLD ENTRY burst for '{_char.Name}' (map={_char.Map} @ {_char.X},{_char.Y}) ==");

        SendMap(0x1E, 0, new byte[] { 0x06, 0x00 }, "ack(0x1E)");
        { var (h, y) = _world.Time; SendMap(0x20, 3, new byte[] { h, y }, $"time(0x20) hour={h} year={y}"); }
        SendId();
        SendMapInfo(_char.Map, _char.MapXs, _char.MapYs, "Nexus", 232);
        Log.Info("   -> mapinfo(0x15)");
        SendStats();
        SendSelfLook();
        SendXy();
        SendMap(0x22, 3, Array.Empty<byte>(), "map-done(0x22)");
        PlayMapMusic(_char.Map);   // 0x19: start this map's background track
        SendWeather(_world.GetWeather(_char.Map));   // 0x1F: whatever this map's weather already is

        Log.Info("   == burst sent; watching for client packets (walk/request = progress, disconnect = a packet was rejected) ==");
    }

    // 0x05 = the client's OWN entity id, decoded from the working 6.x capture:
    //   05 | entityId(u32BE) | 00 00 00 02 | 00 00 00 00
    private void SendId()
    {
        var d = new List<byte>();
        d.AddRange(Be32(_char.Id));   // your entity id
        d.AddRange(Be32(2));          // field2 = 2 (per 6.x)
        d.AddRange(Be32(0));          // field3 = 0
        SendMap(0x05, _gameInc++, d.ToArray(), "id(0x05) — YOUR entity id");
    }

    // 0x08 self-stats -> the always-on HUD. Opcode + full byte layout decoded empirically (2026-07-24):
    // a real 6.x server capture (jeedee/TkServer) proved stats = 0x08; a self-describing gradient packet
    // (!stg, body[i]=i) then pinned every 4.95 field offset by reading the value off the HUD. flags=0x78
    // selects the full-stats form. Multi-byte stat fields are big-endian u32 (verified: HP=0x18191A1B at
    // offset 24, Exp=0x20212223 at 32, etc.). maxHP[5]/maxMP[9] CONFIRMED via !hp (sending 100/1000
    // drops the bar to ~10%). Nation id table (CONFIRMED via !nat, see Character.NationName).
    //   [0]=flags(0x78) [1]=nation [2]=totem [4]=level [5..8]=maxHP u32BE [9..12]=maxMP u32BE
    //   [13]=might [14]=will [17]=grace [24..27]=HP u32BE [28..31]=MP u32BE [32..35]=exp u32BE
    //   [36..39]=coins u32BE
    private void SendStats()
    {
        // Effective = base (_char.*) + worn-gear bonuses + active timed buffs. Clamp current HP/MP to the
        // effective caps so an unequip that lowers max Vita/Mana can't leave the bar reading over 100%.
        var eq = Totals();
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + eq.hp);
        uint maxMp = (uint)Math.Max(0, (int)_char.MaxMp + eq.mp);
        if (_char.Hp > maxHp) _char.Hp = maxHp;
        if (_char.Mp > maxMp) _char.Mp = maxMp;

        var d = new byte[58];
        d[0] = 0x78;                        // flags: full-stats form
        d[1] = _char.Nation;
        d[2] = _char.Totem;
        d[4] = _char.Level;
        WriteBe32(d, 5, maxHp);             // maxHP  (offset [5] confirmed via !hp bar-fill test) — base + gear
        WriteBe32(d, 9, maxMp);             // maxMP  (offset [9] confirmed) — base + gear
        d[13] = (byte)Math.Clamp(_char.Might + eq.might, 0, 255);
        d[14] = (byte)Math.Clamp(_char.Will  + eq.will,  0, 255);
        d[17] = (byte)Math.Clamp(_char.Grace + eq.grace, 0, 255);
        WriteBe32(d, 24, _char.Hp);         // current HP (confirmed)
        WriteBe32(d, 28, _char.Mp);         // current MP (confirmed)
        WriteBe32(d, 32, _char.Exp);        // experience (confirmed)
        WriteBe32(d, 36, _char.Coins);      // coins      (confirmed)
        d[45] = MailParcelFlags();          // bottom-left HUD notify: 0x10=n-mail arrow, 0x01=parcel bag (body[45] confirmed live 2026-07-28)
        SendMap(0x08, _gameInc++, d, "stats(0x08)");
    }

    // The 0x08 body[45] mail/parcel HUD-notification byte (RTK FLAG_MAIL=0x10 / FLAG_PARCEL=0x01, both=0x11).
    // Confirmed live: the 4.95 client draws the bottom-left arrow (unread n-mail) / bag (unclaimed parcel)
    // straight off this byte. Driven from the persisted mailbox so it survives clean stats resends and
    // relog, and self-clears when the mail is read / the parcel claimed. See docs §"Stats packet 0x08".
    private byte MailParcelFlags()
    {
        byte f = 0;
        var inbox = Mail.InboxFor(_char.Name);
        if (inbox.Any(m => !m.IsRead))                       f |= 0x10;  // an unread letter -> arrow
        // Bag icon = a parcel waiting: either a real messenger parcel (Parcel.cs) or a reward-mail's
        // unclaimed attachment (RTK's own reward mail carries its parcel on the letter).
        if (Parcel.HasAny(_char.Name)
            || inbox.Any(m => m.ItemId >= 0 && !m.Claimed))  f |= 0x01;
        return f;
    }

    private static void WriteBe32(byte[] d, int off, uint v)
    {
        d[off]     = (byte)(v >> 24);
        d[off + 1] = (byte)(v >> 16);
        d[off + 2] = (byte)(v >> 8);
        d[off + 3] = (byte)v;
    }

    // ---- natural HP/MP regeneration (RTK Player.regen migration) -------------------------------
    // RTK heals a resting player every 25s: Accepted/player.lua `Player.regen` fires on timerTick%50
    // (the timer runs at 0.5s/tick), restoring ceil(maxHP * 0.02 * (1 + healing/100)) vita and
    // ceil(maxMP * 0.02) mana, then pushing a status update. We don't carry RTK's derived `healing`
    // stat, so HP regen scales with Grace and MP regen with Will (so vitals come back "based on your
    // stats", as expected), keeping RTK's 2% base and 25s cadence. The world heartbeat (World.Tick)
    // calls this once per 600ms tick for every player; we accumulate real elapsed ms so the 25s
    // cadence is independent of the tick period.
    //
    // Threading: runs on the world-tick thread and writes _char.Hp/Mp, which the session's own
    // read-loop also writes (damage/heal). Both are plain field writes with no lock — consistent with
    // the codebase's lock-free _char posture (see PlayerSnapshot) — so a regen tick landing in the
    // same instant as a hit could at worst drop one small increment. The 25s cadence makes that
    // vanishingly rare and self-correcting on the next tick.
    private long _regenAccum;
    private const int RegenIntervalMs = 25_000;   // RTK regen period (timerTick%50 @ 0.5s/tick)

    public void RegenTick(int ms)
    {
        if (_char.Hp == 0) return;   // dead: no natural regen (RTK bails on health==0 / state==1)

        var eq = Totals();
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + eq.hp);
        uint maxMp = (uint)Math.Max(0, (int)_char.MaxMp + eq.mp);
        if (_char.Hp >= maxHp && _char.Mp >= maxMp) { _regenAccum = 0; return; }   // already topped off

        _regenAccum += ms;
        if (_regenAccum < RegenIntervalMs) return;
        _regenAccum -= RegenIntervalMs;

        // 2% of max per tick, scaled by the governing attribute (Grace->vita, Will->mana). Ceil keeps a
        // low-level character (small max) ticking up by at least 1 instead of rounding to nothing.
        int hpGain = (int)Math.Ceiling(maxHp * 0.02 * (1 + (_char.Grace + eq.grace) / 100.0));
        int mpGain = (int)Math.Ceiling(maxMp * 0.02 * (1 + (_char.Will  + eq.will)  / 100.0));

        uint newHp = Math.Min(maxHp, _char.Hp + (uint)hpGain);
        uint newMp = Math.Min(maxMp, _char.Mp + (uint)mpGain);
        if (newHp == _char.Hp && newMp == _char.Mp) return;   // no change -> skip the HUD packet

        _char.Hp = newHp;
        _char.Mp = newMp;
        SendStats();   // push the refreshed HP/MP to the always-on HUD
    }

    // "!nat <n>" — send stats with nation byte = n so we can read which kingdom name/crest the HUD shows.
    // Nation names live in a client data file (no strings in the exe; NATION_E.EPF is a graphic set), so
    // the id -> nation mapping can only be built empirically. Sweep 0,1,2,... and record each.
    private void StatNation(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte n = 0;
        if (parts.Length > 1) byte.TryParse(parts[1], out n);
        byte save = _char.Nation;
        _char.Nation = n;
        SendStats();
        _char.Nation = save;
        Log.Info($"   -> NATION probe: sent nation={n}; read the HUD nation name/crest");
    }

    // "!totem <n>" — same idea as !nat, for the totem crest: send stats with totem byte = n and read which
    // name/graphic the HUD shows. Our documented table (0=JuJak 1=Baekho 2=HyunMoo 3=ChungRyong 4=None) was
    // NEVER actually swept like nation was (§9/§16) — a live report showed a fresh character (Totem defaults
    // to 4, "None" per that table) rendering as ChungRyong, so the table is probably wrong. Sweep 0..4 here
    // to pin the real mapping before wiring totem selection up from the creation packet.
    private void StatTotem(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte n = 0;
        if (parts.Length > 1) byte.TryParse(parts[1], out n);
        byte save = _char.Totem;
        _char.Totem = n;
        SendStats();
        _char.Totem = save;
        Log.Info($"   -> TOTEM probe: sent totem={n}; read the HUD totem name/crest");
    }

    // "!time" — report the shared world clock (hour 0-23, year) and whether YOU are in your totem's totem-time
    // window (the +5% kill-exp bonus). The clock advances one game hour per 7.5 real minutes (World.HourTicks).
    private void ShowTime()
    {
        var (h, y) = _world.Time;
        bool totem = _world.IsTotemTime(_char.Totem);
        string mine = Content.TotemName(_char.Totem);
        SendLog($"Game time: hour {h}:00, year {y}. Your totem: {mine}. " +
                (totem ? "TOTEM TIME — kill exp +5%." : "Not your totem time (no exp bonus)."));
    }

    // "!dye <n>" — calibrate the war-paint dye. Sets the persistent armor-dye byte (0x33 appearance[4]) to n
    // and redraws, so we can catalogue which palette index renders as which visible color on THIS 4.95 client
    // (the look-lab confirmed 16/32/64/128/255 recolor and 0..8 stay base, but 9..31 — the range RTK's team
    // colors live in — was never swept). Wear an armor/coat first, or there's nothing to recolor. "!dye" with
    // no number resets to 0 (undyed). Feeds the real color values back into WarPaintAbility's team table.
    private void DyeProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte n = 0;
        if (parts.Length > 1) byte.TryParse(parts[1], out n);
        SetArmorColor(n);
        SendMiniText($"dye = {n}" + (HasVisibleArmor ? "" : "  (no armor/coat worn — nothing to recolor)"), type: 3);
        Log.Info($"   -> DYE probe: appearance[4] = {n}");
    }

    // "!hp <cur> <max>" — send stats with HP=cur, maxHP=max (and the same for MP) to PIN the maxHP/maxMP
    // offsets: if [5]/[9] are really maxHP/maxMP, the HP/MP bar fill becomes cur/max (e.g. 100/1000 = 10%
    // full) and any "cur/max" text shows those numbers. If the bar stays full, the offset is wrong.
    private void StatHpTest(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        uint cur = 100, max = 1000;
        if (parts.Length > 1) uint.TryParse(parts[1], out cur);
        if (parts.Length > 2) uint.TryParse(parts[2], out max);
        var (sh, sm, smh, smm) = (_char.Hp, _char.Mp, _char.MaxHp, _char.MaxMp);
        _char.Hp = cur; _char.MaxHp = max; _char.Mp = cur; _char.MaxMp = max;
        SendStats();
        (_char.Hp, _char.Mp, _char.MaxHp, _char.MaxMp) = (sh, sm, smh, smm);
        Log.Info($"   -> HP/MAX probe: sent HP={cur}/max={max}; expect bar fill = {cur}/{max} and text '{cur}/{max}' if offsets [5]/[9] are correct");
    }

    /// <summary>
    /// 0x33 self/character appearance. Format decoded from handler 0x44fef0:
    ///   X(u16BE) Y(u16BE) dir(u8) entityId(u32BE) type(u8=0)
    ///   [7 appearance bytes] flag(u8) nameLen(u8) name[]
    /// type=0 selects the 7-byte appearance form (reader 0x436120). type must be 0 or 1 or the
    /// client bails. The 7 bytes are body-form/hair/face/colors/gear; exact semantics TBD, using
    /// plausible values so the sprite is visible.
    /// </summary>
    private void SendSelfLook()
    {
        // 4.95 type-0 appearance layout (decoded via the look-lab sweeps):
        //   [0]=body/sex(0=M,1=F)  [1]=form/state(0=normal; 1 ghost, 3 mount, 5 invisible-spell)
        //   [2]=face  [3]=armor/coat  [4]=armor dye/palette index (0..8 all render as base color; 16/32/64/
        //   128/255 visibly recolor it, LIVE-confirmed 2026-07-27 -- exact mapping not catalogued, hardcoded
        //   0 for now)  [5]=weapon  [6]=shield
        // NOTE the old code put "Hair" in [1] = the FORM byte — that's what blanked the character.
        //   ... [5]=weapon (Honor sword/Flame blade/…), [6]=shield
        var app = new byte[] { (byte)_char.Sex, MountForm(), (byte)_char.Face, (byte)_char.Armor, _char.ArmorColor, WeaponLook(), ShieldLook() };
        SendLook(_char.Id, _char.X, _char.Y, dir: _facing, app, renderKind: 1, _char.Name, "self(0x33)");
    }

    // Weapon/shield look bytes for the 0x33 appearance. CRITICAL: look 0 is a REAL weapon/shield sprite,
    // so an EMPTY slot must send 0xFF ("-1", proven live and matching RTK clif.c, which sends 0xFFFF for
    // weapon/shield when !pc_isequip). The slot is "occupied" iff a matching item is actually worn — a worn
    // weapon whose Look happens to be 0 (e.g. Novice sword) still shows sprite 0, only a bare slot is 0xFF.
    // Form/state byte for appearance[1]: 1 = ghost (Hp==0, see Die()), 3 = mounted (horse+rider composite),
    // 0 = normal human. Dead outranks mounted — a horse doesn't survive its rider's death. Other documented
    // values (5 invisible-spell) aren't driven from here.
    private byte MountForm() => _char.Hp == 0 ? (byte)1 : (_char.Mounted ? (byte)3 : (byte)0);

    /// <summary>Redraw self + broadcast to every co-located peer after an appearance change (equip/unequip,
    /// mount/dismount, ghost/revive, morph, gender). RE'd root cause (2026-07-27) of the "abandoned red
    /// triangle nametag" litter bug: a peer's client renders us via its own 0x33 handler (0x44fef0 ->
    /// entity factory 0x44d7d0), which is SUPPOSED to look up our existing entity by id and destroy it
    /// before building the new one — but that destroy-before-create branch doesn't reliably fire for a
    /// bare re-send of 0x33 with an unchanged id (unlike a real 0x0E despawn, which walks a distinct,
    /// always-thorough teardown path — confirmed by disassembling both `0x44da40`/`0x44d9f0` destroy
    /// helpers). When it's skipped, the OLD entity (and its floating nameplate marker) is orphaned
    /// alongside the new one — exactly the "walk away and back" repro, since leaving view fully despawns
    /// (0x0E) before anything re-spawns. Fix: force the proven-reliable despawn ourselves before every
    /// peer redraw, instead of trusting the client to replace in place. Self's own view isn't affected —
    /// SendSelfLook updates the persistent self entity directly rather than destroying/recreating it.</summary>
    private void RefreshAppearance()
    {
        _world.Broadcast(_char.Map, p => p.DespawnEntity(_char.Id), except: this);
        SendSelfLook();
        _world.Broadcast(_char.Map, p => p.ShowPlayer(this), except: this);
    }

    /// <summary>Hp==0 is this server's whole "dead" state (matches the pre-existing Gateway/regen checks) —
    /// a ghost that can't fight, can't cast, and won't regen until <see cref="Revive"/> restores it.</summary>
    public bool IsDead => _char.Hp == 0;

    private byte WeaponLook() => EquippedLook(3, _char.Weapon != 0 ? _char.Weapon : (byte)0xFF);  // Type 3 = weapon; !weapon GM override
    private byte ShieldLook() => EquippedLook(5, 0xFF);                                            // Type 5 = shield
    private byte EquippedLook(int itmType, byte none)
    {
        var e = _char.Equipment.FirstOrDefault(w => Content.ItemById(w.ItemId)?.Type == itmType);
        return e is null ? none : (byte)(Content.ItemById(e.ItemId)?.Look ?? 0);
    }

    // The swing sfx of the weapon currently in hand, by weapon CATEGORY (see IsStaffWeapon) rather than
    // RTK's own ItmSound field. That field turned out to be nearly useless for this: 331 ("most swords") is
    // shared not just by Maxcaliber/Wooden Saber/Novice Sword but by EVERY staff too (augury_staff,
    // staff_of_defense, wand_of_fire, the whole ju_jak/hsiao_chu/staff_of_chi families, …) — checked directly
    // against Items.csv. Remapping by that field (331 -> 002.wav) therefore made every weapon of every
    // category share one swing sound, which is exactly the "everything sounds like Maxcaliber" bug reported
    // live 2026-07-27. 0 if unarmed or the weapon has no sound.
    private int EquippedWeaponSound()
    {
        var e = _char.Equipment.FirstOrDefault(w => Content.ItemById(w.ItemId)?.Type == 3);
        var def = e is null ? null : Content.ItemById(e.ItemId);
        if (def is null) return 0;
        return IsStaffWeapon(def.Key) ? 335 : 2;   // both confirmed live 2026-07-27
    }

    // Staff/wand/rod-category weapons swing differently (335.wav, confirmed live via a staff) than blades
    // (002.wav, confirmed live via Maxcaliber/Spike). Every Items.csv row actually named "staff" or "wand"
    // (augury_staff, staff_of_power, wand_of_fire, the ju_jak/hsiao_chu/staff_of_chi upgrade chains, …) is a
    // genuine caster weapon, so a plain identifier match is reliable here — unlike ItmSound (see
    // EquippedWeaponSound's doc), Items.csv actually differentiates these by name consistently.
    private static bool IsStaffWeapon(string key) =>
        key.Contains("staff", StringComparison.OrdinalIgnoreCase) || key.Contains("wand", StringComparison.OrdinalIgnoreCase);

    // General 0x33 "create/look" for ANY entity (self or a test dummy). Type=0 = the 7-byte player
    // appearance form (parser 0x436120). renderKind (byte after the 7 appearance bytes) MUST be 1/2/3
    // or handler 0x44fef0 bails before allocating the sprite (1 = player sprite). appearance[0] is the
    // body form; [1]/[2] are the sprite layers whose valid id space we're mapping with the look-lab.
    private void SendLook(uint id, ushort x, ushort y, byte dir, byte[] app, byte renderKind,
                          string name, string label)
    {
        var nm = Encoding.ASCII.GetBytes(name);
        var d = new List<byte>();
        d.AddRange(Be(x));
        d.AddRange(Be(y));
        d.Add(dir);
        d.AddRange(Be32(id));
        d.Add(0);                                   // type = 0 (7-byte appearance form)
        for (int i = 0; i < 7; i++) d.Add(i < app.Length ? app[i] : (byte)0);
        d.Add(renderKind);
        d.Add((byte)nm.Length);
        d.AddRange(nm);
        SendMap(0x33, _gameInc++, d.ToArray(), label);
    }

    // 0x16 CREATURE spawn (handler 0x450a00 -> builder 0x44dbc0 -> ctor 0x463020 -> base 0x462ec0).
    // Unlike 0x33 (which ALWAYS draws from the player sprite archive 0x4f2a84, so it can only render
    // players/NPCs that look human), 0x16 has its OWN graphic — this is the real monster/creature path.
    // Decoded field layout (offsets from the opcode byte; multi-byte = big-endian):
    //   +1  u32 owner/parent id (unused by the ctor; send 0)
    //   +5  u16 GRAPHIC id  -> stored at sprite+0x130 by 0x462ec0  (THE creature sprite)
    //   +7  u32 entity id   -> find/despawn key (must match what we track + pass to 0x0E)
    //   +0xb u16 X   +0xd u16 Y            (resting tile -> stored at entity+0x10c/+0x110)
    //   +0xf u16 X'  +0x11 u16 Y'          (the "walked-from" tile)
    //   +0x13 u32 flags (color/hp-bar?; send 0)   +0x17 u8 dir
    // There is NO name field and NO viewport gate (0x44dbc0 skips the 0x424310 check), so a creature
    // can be placed anywhere. The graphic id-space is unknown — swept live via !mobrow.
    //
    // *** CRITICAL: (X',Y') MUST differ from (X,Y). *** The ctor 0x463020 computes the walk distance
    // `[obj+0x148] = |X-X'| + |Y-Y'|`, and the per-frame screen-position code (0x463160) does
    // `idiv [obj+0x148]`. A stationary spawn with (X',Y')==(X,Y) → distance 0 → **integer
    // divide-by-zero → client crash** (found live via the Frida crash-catcher). So a creature always
    // "walks in" one tile: we send the from-tile one step north (or south at the top edge), distance 1.
    private void SendCreature(uint id, ushort sprite, ushort x, ushort y, byte dir, string label)
    {
        ushort fromX = x;
        ushort fromY = (ushort)(y > 0 ? y - 1 : y + 1);   // 1 tile away so the walk distance != 0
        var d = new List<byte>();
        d.AddRange(Be32(0));         // +1  owner/parent id
        d.AddRange(Be(sprite));      // +5  graphic id
        d.AddRange(Be32(id));        // +7  entity id
        d.AddRange(Be(x));           // +0xb resting X
        d.AddRange(Be(y));           // +0xd resting Y
        d.AddRange(Be(fromX));       // +0xf walked-from X
        d.AddRange(Be(fromY));       // +0x11 walked-from Y
        d.AddRange(Be32(0));         // +0x13 flags
        d.Add(dir);                  // +0x17 dir
        SendMap(0x16, _gameInc++, d.ToArray(), label);
    }

    // *** 0x07 = the REAL creature/monster spawn (the "area characters" list). *** Handler 0x44fdb0
    // loops a u16 count of entities, each built by the SAME entity factory 0x44d7d0 that 0x33 uses —
    // BUT here the look descriptor's `look` field selects a DIRECT sprite instead of the 7-byte player
    // appearance. RE'd path: 0x44fdb0 -> 0x44d7d0 -> (look in 0x8000..0xbfff => descriptor type 1) ->
    // 0x44d8c8 -> ctor 0x461a50 (entity vtable 0x4cd098). That entity's draw 0x461c70 branches on
    //   [ent+0x178] (=type): type!=0 -> 0x461d37 -> monster resolver 0x434020/0x4342e0 which pushes
    //   "MONSTER.EPF" (0x4f1d18) and resolves the sprite from Monster.epf via 0x433d00 (Monster.tbl).
    // So look = 0x8000 + monsterLookId draws a real monster. (look < 0x8000 or > 0xbfff => descriptor
    // type 2 -> 0x462ec0, vtable 0x4cd118 = the item/object base, i.e. the invisible 0x16 path.)
    //
    // Per-entry layout (12 bytes; body[0..1] = count, entries follow; multi-byte = big-endian):
    //   +0 X(u16)  +2 Y(u16)  +4 id(u32)  +8 look(u16=0x8000|monsterLookId)  +10 color(u8)  +11 dir(u8)
    // color -> palette (ent+0x18e via resolver), dir/state -> ent+0x18d. Unlike 0x16 there IS a viewport
    // gate (0x424310): entries outside the camera rect are silently skipped, so spawn inside view.
    private void SendCreatureList(IReadOnlyList<(uint id, ushort look, ushort x, ushort y, byte color, byte dir)> es)
    {
        if (es.Count == 0) return;
        var d = new List<byte>();
        d.AddRange(Be((ushort)es.Count));           // body[0..1] = entity count
        foreach (var e in es)
        {
            d.AddRange(Be(e.x));                    // +0  X
            d.AddRange(Be(e.y));                    // +2  Y
            d.AddRange(Be32(e.id));                 // +4  entity id
            d.AddRange(Be(e.look));                 // +8  look (0x8000|monsterId => Monster.epf)
            d.Add(e.color);                         // +10 palette/color
            d.Add(e.dir);                           // +11 dir/state
        }
        SendMap(0x07, _gameInc++, d.ToArray(), $"creature-list(0x07) x{es.Count}");
    }

    // Register a monster server-side AND draw it via the real Monster.epf path (0x07). lookId is the
    // Monster.tbl look index (0..~326); we OR in 0x8000 to hit the direct-monster-sprite branch.
    private Mob SpawnMonster(ushort lookId, ushort x, ushort y, string name, int hp, byte dir = 2, byte color = 0)
    {
        var mob = new Mob(_nextMobId++, lookId, x, y, name, hp) { Dir = dir };
        _mobs.Add(mob);
        SendCreatureList(new[] { (mob.Id, (ushort)(0x8000 | lookId), x, y, color, dir) });
        Log.Info($"   -> spawn MONSTER {mob.Id} '{name}' look={lookId} @({x},{y}) hp={hp}");
        return mob;
    }

    // 0x0E despawn (server->client; handler 0x450440): count(u8) then that many entity ids (u32BE).
    // The client destroys each by id (0x44d9f0) and stops early on a 0 id, so never pass id 0.
    private void SendDespawn(params uint[] ids)
    {
        if (ids.Length == 0) return;
        var d = new List<byte> { (byte)Math.Min(ids.Length, 255) };
        foreach (var id in ids) d.AddRange(Be32(id));
        SendMap(0x0E, _gameInc++, d.ToArray(), $"despawn(0x0E) x{ids.Length}");
    }

    // NexusTK has NO floating combat numbers — combat feedback is the over-head HP bar (0x13, below) plus the
    // effect/spell animations (0x29, SendEffect). The old melee path used to abuse 0x29 with a raw damage value
    // (which played effect #(dmg-1), an unintended graphic); that is gone — hits now send the 0x13 damage packet.
    //
    // 0x13 (server->client) = the combat DAMAGE packet: over-head HP bar + a hit-reaction animation +
    // an optional hit sound, all in ONE packet. Decoded from the 4.95 client handler 0x4508f0 (disx):
    //   body = id(u32BE) | critical(u8) | percent(u8) | hitSound(u8)
    //   * critical -> plays overlay animation (0x8f - critical, SIGNED) over the entity (a hit spark; the
    //     hit-effect is queued for 0x78 ticks via 0x4622d0->0x41b5d0). RTK uses 33 (normal) / 255 (crit).
    //   * percent  -> the over-head HP bar fill, 0..100 (the client skips the bar if percent > 100; 0 = empty).
    //   * hitSound -> played through the sound manager if nonzero (RTK's u32 damage tail lands here as its high
    //     byte = 0, so no hit sound normally; the 4.95 client ignores everything past body[7]).
    // This is what draws the "remaining HP bar above a monster's head" on every hit. RTK's clif_send_mob_health
    // builds the same shape (plus the ignored u32 damage). critical is calibratable live via NEXUS_HIT_CRIT.
    private static readonly byte HitCritByte =
        byte.TryParse(Environment.GetEnvironmentVariable("NEXUS_HIT_CRIT"), out var c) ? c : (byte)0x21; // 33 = RTK normal hit
    private void SendDamage(uint id, byte percent, byte critical, byte hitSound = 0)
    {
        if (percent > 100) percent = 100;                 // >100 would make the client skip the bar entirely
        var d = new List<byte>();
        d.AddRange(Be32(id));        // body[1..4] entity id (u32BE)
        d.Add(critical);            // body[5] hit type -> overlay anim 0x8f-critical
        d.Add(percent);             // body[6] HP bar fill 0..100
        d.Add(hitSound);            // body[7] optional hit sfx (0 = none)
        SendMap(0x13, _gameInc++, d.ToArray(), $"damage(0x13) id={id} pct={percent} crit={critical}");
    }
    public void DamageOver(uint id, byte percent, byte critical, byte hitSound = 0) => SendDamage(id, percent, critical, hitSound);  // peer-facing

    // Remaining-HP percent for the over-head bar (1..100 for a live mob so its bar never reads empty; caller
    // passes 0 explicitly on death). Guards against MaxHp<=0 and negative Hp (a killing blow overshoots).
    private static byte HpPercent(Mob m)
    {
        int max = Math.Max(1, m.MaxHp);
        int cur = Math.Clamp(m.Hp, 0, max);
        if (cur <= 0) return 0;
        int pct = (int)((long)cur * 100 / max);
        return (byte)Math.Clamp(pct, 1, 100);
    }

    // Death beat: on a killing blow, empty the target's HP bar (0x13 percent=0, which also plays the final hit
    // overlay), then remove the corpse (0x0E) after a short delay so it doesn't just pop out of existence. 4.95
    // monsters have no death frame-set (monsfrm.tbl defines only walk/attack states), so the "death animation" is
    // this beat: last hit spark + empty bar, held briefly, then despawn. Delay is calibratable via NEXUS_DEATH_DELAY_MS.
    private static readonly int DeathDespawnMs =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_DEATH_DELAY_MS"), out var v) ? Math.Clamp(v, 0, 5000) : 600;

    // Show the result of a hit that already resolved in the world: draw the over-head HP bar for everyone on the
    // map, and on death run the death beat (empty bar + delayed despawn). `mob` is read for its remaining HP%.
    private void ShowDamageResult(uint mobId, Mob mob, bool died, byte critical = 0, byte hitSound = 0)
    {
        byte pct = died ? (byte)0 : HpPercent(mob);
        _world.Broadcast(_char.Map, p => p.DamageOver(mobId, pct, critical != 0 ? critical : HitCritByte, hitSound));
        if (died) ScheduleDespawn(_char.Map, mobId, DeathDespawnMs);
    }

    // Remaining-HP percent for OUR OWN over-head bar (mirrors HpPercent(Mob) above), reflecting worn-gear
    // bonuses like SendStats does. 0 only when actually dead (Hp==0) — a live player's bar never reads empty.
    private byte PlayerHpPercent()
    {
        uint maxHp = (uint)Math.Max(1, (int)_char.MaxHp + Totals().hp);
        uint cur = Math.Min(_char.Hp, maxHp);
        if (cur == 0) return 0;
        return (byte)Math.Clamp((int)(cur * 100 / maxHp), 1, 100);
    }

    // Called by World.Tick (the shared mob-AI heartbeat) when a provoked mob lands a swing on us: apply the
    // damage, refresh our HUD, and show the over-head hit/HP-bar to the whole map (the same 0x13 feedback a
    // mob takes, just aimed at our own entity id) — dying (Hp hits 0) triggers Die() below.
    public void ApplyMobHit(Mob mob, int rawDmg)
    {
        if (IsDead) return;   // already down — don't re-trigger Die() while the revive delay is pending

        // RTK hitCritChance.lua: mobs DO roll a crit chance, but real swingDamage.lua's _getMobSwingDamage
        // never multiplies mob damage by it — only a PLAYER's own swing gets the x3 (see
        // Combat.RollCritChance's doc). We still roll it here purely for the wire-visual crit byte below.
        int critChance = Combat.RollCritChance(attackerIsMob: true,
            atkGrace: 0 /* unused on the mob-attacker branch */, atkLevel: mob.Level, atkHit: mob.Hit,
            tgtGrace: _char.Grace + Totals().grace, tgtLevel: _char.Level);

        // RTK swingDamage.lua: finalDamage = floor(finalDamage * (1 + max(armor,-80)/100)). AC is signed and
        // LOWER is better (armor SUBTRACTS from it, same convention as SendStats/EffMight elsewhere), so a
        // well-armored (very negative effective AC) player takes as little as 20% of the raw swing, while a
        // naked/positive-AC player takes MORE than raw — armor can't fully negate a hit (-80 floor = min 20%).
        int effectiveAc = _char.Ac - Totals().armor;
        int dmg = Combat.ApplyArmor(rawDmg, effectiveAc, floor: -80);

        // Positional "attacked from behind while both face the same way" 2x (RTK swingDamage.lua's
        // side==target.side rule; applied AFTER armor, matching the Lua's own order). NOT ported: the
        // item-flag-gated backstab/flank abilities a handful of legendary weapons grant (see Combat.cs).
        bool behind = Combat.IsBehindTarget(mob.Dir, _facing, mob.X, mob.Y, _char.X, _char.Y);
        if (behind) dmg *= 2;

        _char.Hp = (uint)Math.Max(0, (int)_char.Hp - dmg);
        // RTK clif_deductarmor: taking a hit rolls durability loss on every worn slot (not just armor —
        // the reference implementation checks the weapon slot here too).
        foreach (var worn in _char.Equipment.ToArray()) DeductDura(worn);
        SendStats();
        byte critByte = critChance == 2 ? (byte)0xFF : HitCritByte;   // RTK: 33 normal / 255 critical
        _world.Broadcast(_char.Map, p => p.DamageOver(_char.Id, PlayerHpPercent(), critByte));
        Log.Info($"   -> mob {mob.Id} '{mob.Name}' hit {_char.Name} for {dmg}{(behind ? " (from behind x2)" : "")}{(critChance == 2 ? " (crit flavor)" : "")} -> {_char.Hp}/{_char.MaxHp}");
        if (IsDead) Die();
    }

    // Defeated by a mob: redraw as a ghost (appearance[1]=1 via MountForm(), see IsDead/Snapshot/ShowPlayer)
    // and STAY that way — RTK has no auto-revive timer at all. A ghost wakes up only by pressing F1 and
    // picking "Silver Thread" (RunF1MenuAsync/SilverThread, §11k), which offers a choice of Shaman to warp
    // to and revives on arrival. Matches "players die to ghost form" (§8) with the real RTK revival gate,
    // replacing the earlier simplified fixed-timer/home-city stand-in.
    private void Die()
    {
        Log.Info($"   -> DIED: {_char.Name} on map {_char.Map} @ ({_char.X},{_char.Y})");
        SendMiniText("You have been defeated! Press F1 and choose \"Silver Thread\" to find your way back.");
        _char.Mounted = false;                                            // a horse doesn't carry a ghost
        RefreshAppearance();                                              // redraw self as a ghost + everyone watching
    }

    // Leave ghost state: full heal (gear/buffs included) + warp to (map,x,y). Used by Silver Thread to
    // revive at the chosen Shaman; also reachable as a fresh-character/GM fallback via HomeCityFor.
    private void ReviveAt(ushort map, ushort x, ushort y, string arrivalMsg)
    {
        _char.Hp = EffMaxHp;
        _char.Mp = EffMaxMp;
        if (Content.Maps.TryGetValue(map, out var mi)) EnterMap(mi.Id, mi.Xs, mi.Ys, x, y, mi.Name);
        else SendSelfLook();   // fallback: just heal in place if the map isn't loaded
        SendStats();           // push the restored HP/MP to the HUD (EnterMap doesn't send stats itself)
        SendMiniText(arrivalMsg);
        Log.Info($"   -> REVIVED: {_char.Name} at map {_char.Map} @ ({_char.X},{_char.Y})");
    }

    // Home-city placement (HomeCityFor / PlaceNewCharacter) moved to Shared/CharacterFactory so the login
    // server (account creation) and this game server (world entry for never-seen accounts) agree on the
    // spawn. Revive points that reused HomeCityFor call CharacterFactory.HomeCityFor.

    // Broadcast a corpse despawn (0x0E) to the map after `ms`, so the death beat is visible first. The mob is
    // already gone from the world's mob list (World.TryDamage removed it), so nothing ticks it in the meantime,
    // and mob ids are monotonic so the id can't be reused before this fires. 0 ms = despawn immediately.
    private void ScheduleDespawn(ushort map, uint id, int ms)
    {
        if (ms <= 0) { _world.Broadcast(map, p => p.DespawnEntity(id)); return; }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ms); _world.Broadcast(map, p => p.DespawnEntity(id)); }
            catch (Exception ex) { Log.Info($"   -x delayed despawn {id} failed: {ex.Message}"); }
        });
    }

    // Play effect `effectId` over an entity via 0x29 — the real 4.95 spell-animation path. The wire byte maps
    // DIRECTLY to RTK's sendAnimation id (EfxWireOffset = 0), proven live: casting Ion (pcalign 0 → anim 4 =
    // unaligned zap) with a +1 offset showed the anim-5 graphic (unaligned heal) — i.e. wire N draws the anim-N
    // effect, so no adjustment. (The handler's internal index-1 is cancelled by the effect table being loaded
    // 1-based.) Overridable via NEXUS_EFX_WIRE_OFFSET. A/B/C = 0 → centered, default style. effectId < 0 = no-op.
    private static readonly int EfxWireOffset =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_EFX_WIRE_OFFSET"), out var w) ? w : 0;
    private void SendEffect(uint id, int effectId)
    {
        if (effectId < 0) return;
        int b = effectId + EfxWireOffset;
        if (b < 1 || b > 255) return;   // outside the u8 / effect-table range — skip rather than send garbage
        var d = new List<byte>();
        d.AddRange(Be32(id));
        d.Add((byte)b);
        d.AddRange(Be(0)); d.AddRange(Be(0)); d.AddRange(Be(0));   // A/B/C = centered, default style
        SendMap(0x29, _gameInc++, d.ToArray(), $"effect(0x29) id={id} efx={effectId}");
    }

    // One-shot sound effect via 0x19. RTK's clif_playsound (type 3, a positional descriptor) is a LATER-client
    // format that the 4.95 client's TLV parser mis-walks into garbage (verified live: it built a sound object with
    // mode 4 / random soundId and stayed silent). This is the reverse-engineered 4.95 layout instead.
    //
    // The 0x19 handler (0x450ad0): body[1]=type. type 0 = sfx -> reads soundId(u16BE)@body[3..4], volume(u8)@body[5],
    // then runs a TLV tail (0x450c48) starting at body[2 (=P0)]+3. The tail's "C" field becomes the sound object's
    // MODE (ctor 0x463950 -> [obj+0x148]); the play wrapper (0x463ab0) plays only for mode 1 (-> 0x463ae8 ->
    // play fn 0x4798c0(soundId, type, gain, 0)). soundId/type/gain come from body[3..5] via the type-group. So we
    // put a minimal TLV *after* the 5-byte header (P0=3) that yields tagA=3, B0=0, C=1 with all skip bytes 0:
    //
    //   19 | 00(type=sfx) | 03(P0) | soundId u16BE | 64(vol=100 -> 0 dB) | 03(tagA) 00(B0) 01(C=mode1) 00 00 00 …
    //
    // volume 100 -> gain 0 (full): the client does log10(vol*0.01)*2000 dB (const @0x4cc408 = 0.0, so >0 is audible).
    // Sound id space is NexusTK.snd (zap 56, heal 4, fire 88, …). entityId is unused (this plays as a global sfx,
    // which is what spell sounds want — full volume, not distance-attenuated). Does NOT touch _bgm.
    private void SendSound(int soundId, uint entityId, byte volume = 100)
    {
        // soundId rides body[3..4] as a u16; the play wrapper reads [obj+0x134] as a WORD, so keep it in range.
        if (soundId <= 0 || soundId > 0xffff) { Log.Info($"   -x sound skipped sfx={soundId} (out of 1..65535)"); return; }
        var d = new List<byte> { 0x00, 0x03 };   // type 0 (sfx); P0=3 -> TLV tail begins after the 5-byte header
        d.AddRange(Be((ushort)soundId));          // body[3..4] soundId (u16BE)
        d.Add(volume);                            // body[5] volume (0..100 -> dB gain; 100 = 0 dB)
        d.Add(0x03); d.Add(0x00); d.Add(0x01);    // body[6..8] tagA=3, B0=0, C=1 (C -> object mode 1 = "play")
        d.Add(0x00); d.Add(0x00); d.Add(0x00);    // body[9..11] B1=0, F=0, B2=0 (all skips 0 -> clean parse exit)
        d.Add(0x00);                              // trailing pad
        SendMap(0x19, _gameInc++, d.ToArray(), $"sound(0x19) sfx={soundId}");
    }
    public void SoundAt(int soundId, uint entityId) => SendSound(soundId, entityId);   // peer-facing (broadcast)

    // Broadcast a cast's effect graphic (0x29) + its sound (0x19) over `overId` to everyone on the map, caster
    // included, so visuals + audio match RTK. Effect id / sound id come from the pcalign ladder
    // (Content.EffectAnim / EffectSound). anim/sound < 0 are skipped.
    //
    // Sound uses RTK's clif_playsound layout (0x19 type 3 = a positional sound bound to the source entity). Static
    // RE of the 4.95 client confirms this path IS wired to the audio player: 0x19 handler (0x450ad0) routes type>=2
    // through the TLV tail 0x450c48 -> spatial builder 0x44e6c0 -> 0x463ab0 -> play fn 0x4798c0. (The earlier
    // action-4th-byte route was a dead end: the client picks an action's sound from a fixed type->sound table, so
    // magic/type 6 -> soundId 0 -> silent regardless of the byte we send.)
    private void BroadcastFx(uint overId, int anim, int sound)
    {
        if (anim >= 0)  _world.Broadcast(_char.Map, p => p.EffectOver(overId, anim));
        if (sound > 0)  _world.Broadcast(_char.Map, p => p.SoundAt(sound, overId));
    }

    // Register a creature server-side AND draw it on the client (via 0x16). Used by the mob commands.
    private Mob SpawnMob(ushort sprite, ushort x, ushort y, string name, int hp, byte dir = 2)
    {
        var mob = new Mob(_nextMobId++, sprite, x, y, name, hp) { Dir = dir };
        _mobs.Add(mob);
        SendCreature(mob.Id, sprite, x, y, dir, $"mob '{name}' gfx={sprite}");
        Log.Info($"   -> spawn mob {mob.Id} '{name}' gfx={sprite} @({x},{y}) hp={hp}");
        return mob;
    }

    // Screen tile where the self is drawn (viewport anchor): the client's own tile->screen conversion
    // centers the player around here, NOT at a fixed spot -- it's edge-aware (clamped near map borders),
    // per Mithia 7.x's clif_sendxy. We previously hardcoded a flat (5,5), which only roughly matched near
    // spawn; anywhere else it diverged from where the CLIENT itself draws the character on screen, and
    // the mismatch is what showed up as "teleport -> animate -> pulled back": our camera scroll (driven by
    // this wrong anchor) fought the client's own correctly-centered render position every step.
    // Default mid-anchor (8,7) matches a 17-wide x 15-tall viewport, same as 5.33's SendSelfWalk.
    private (ushort vx, ushort vy) ViewAnchor()
    {
        // Realm-center (F4) FREEZES the camera: the client stops scrolling and the character walks across a
        // static view. The 0x04 handler writes the map origin as (X - vx, Y - vy) [0x44c660], so to keep the
        // origin pinned at the value captured when F4 was pressed (_lockOx,_lockOy) we draw the self at screen
        // (X - Ox, Y - Oy). That makes (X-vx, Y-vy) == (Ox,Oy) every step -> no scroll. If a step carries the
        // anchor outside the viewport, the client's scroll-gate (0x44c8f0 bounds check) rejects it, which ALSO
        // leaves the origin frozen — so the camera never moves regardless. Without realm-center, use the normal
        // edge-aware anchor (self centered, clamped near map borders).
        if (_realm != 0)
            return ((ushort)(_char.X - _lockOx), (ushort)(_char.Y - _lockOy));
        return EdgeAwareAnchor(_char.X, _char.Y);
    }

    // The normal follow-camera anchor: the screen tile the self is drawn at (mid-view, clamped near borders).
    private (ushort vx, ushort vy) EdgeAwareAnchor(int cx, int cy)
    {
        int xs = _char.MapXs, ys = _char.MapYs;
        int vx = cx < 8 ? cx : (cx >= xs - 8 ? cx - xs + 17 : 8);
        int vy = cy < 7 ? cy : (cy >= ys - 7 ? cy - ys + 15 : 7);
        return ((ushort)Math.Clamp(vx, 0, 16), (ushort)Math.Clamp(vy, 0, 14));
    }

    // 0x04: absolute self (X,Y) + screen anchor. The handler (0x44faf0) sets camera scroll via
    // 0x44c660 AND calls 0x44b140 on the self entity, which advances/commits the self's walk that
    // 0x0C started. Sent at spawn and after every walk step.
    private void SendXy() => SendXyAt(_char.X, _char.Y);

    private void SendXyAt(ushort x, ushort y)
    {
        var (vx, vy) = ViewAnchor();
        var d = new List<byte>();
        d.AddRange(Be(x));
        d.AddRange(Be(y));
        d.AddRange(Be(vx));
        d.AddRange(Be(vy));
        d.Add(0);
        SendMap(0x04, _gameInc++, d.ToArray(),
                $"xy(0x04) pos=({x},{y}) scroll=({x - vx},{y - vy})");
    }

    // Complete/unblock a server-authoritative walk WITHOUT scrolling the camera. The 0x04 handler
    // (0x44faf0) always calls the camera fn (0x44c660) THEN the walk-completion (0x44b140). 0x44c660
    // skips its ENTIRE scroll block — settle-offset, origin write, viewport rebuild — when the bounds-gate
    // 0x44c8f0 fails, and that gate rejects any view anchor outside the viewport. So sending vx/vy way
    // out of range makes the camera code a no-op (zero jerk) while 0x44b140 still runs: it advances the
    // self's logical to the walk destination and clears the walk-active gate so the next step is allowed.
    // This is how realm-center (frozen camera) coexists with fast-move OFF (which REQUIRES the 0x04 to
    // unblock — 0x0C commits the tile but never clears the gate, proven live: the client freezes at
    // frameCtr 2 until a 0x04 arrives).
    private void SendXyCommitNoScroll()
    {
        var d = new List<byte>();
        d.AddRange(Be(_char.X));
        d.AddRange(Be(_char.Y));
        d.AddRange(Be(0xFFFF));   // vx: out of viewport -> scroll-gate (0x44c8f0) fails -> camera untouched
        d.AddRange(Be(0xFFFF));   // vy: out of viewport
        d.Add(0);
        SendMap(0x04, _gameInc++, d.ToArray(), $"xy-commit(0x04 no-scroll) pos=({_char.X},{_char.Y})");
    }

}
