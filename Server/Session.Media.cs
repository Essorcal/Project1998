using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // 0x19 = background music. Handler 0x450ad0 reads: type(u8 @+1) pad(u8 @+2) bgm(u16BE @+3)
    // volume(u8 @+5, 0..100, client log-scales it). type 2 = MIDI (the stock 1.mid..12.mid in
    // NexusTK.snd); type 1 = mp3/lsr (the stock client has none). bgm 0 stops the music.
    private const ushort NoBgm = 0xFFFF;   // "nothing sent yet this session" (0 is a real value: stop)
    private ushort _bgm = NoBgm;    // last track sent, so we don't restart the same song on a refresh

    // Melee swing sfx (NexusTK.snd id). The client's action->sound table gives the swing action (0x1A type 1)
    // NO sound (like magic/type 6 -> 0), so a weapon swing is silent unless we play one explicitly over 0x19.
    // Calibrate the id live with "!swingsnd <id>" (auditions it), then it rides every armed swing; 0 = silent.
    private int _swingSfx = 0;

    // Unarmed ("bare fist") swing sfx fallback, used only when no weapon is equipped (EquippedWeaponSound()
    // returns 0). RTK's own C engine special-cases this by sending the swing action with a hardcoded param
    // (pc.c: clif_sendaction(..., 1, attackspeed, 9) when itemdb_sound(weapon)==0) — but that relies on a
    // fixed action-type->sound table baked into the 6.x/7.x client; our own live testing already proved the
    // 4.95 client's action-param byte is ignored for the swing (see the comment above _swingSfx), so we can't
    // reuse that trick here either. There's no RTK item row for "fists" to port a real id from. 009.wav,
    // calibrated live 2026-08-04 — the SAME id a mob's own swing uses (MobSwingSfx below), i.e. a bare fist
    // and a claw/bite land on one shared "unarmed" sound. "!fistsnd <id>" recalibrates or mutes it (0).
    private int _fistSfx = 9;

    // On-connect impact sfx for a PLAYER's melee, played ONLY when a swing actually lands — it stacks with the
    // weapon/fist swing sfx above, which plays on every swing attempt regardless of hit/miss. 349.wav,
    // calibrated live 2026-08-04.
    //
    // NOT sent via the 0x13 damage packet's own hitSound byte any more (SendDamage/ShowDamageResult still carry
    // that field — it's real, see docs §7.2 — but it's a BYTE, and 349 doesn't fit in one). It goes out as its
    // own 0x19 broadcast instead, via PlayHitSfx below, which also means peers hear our hits land. RTK's
    // matching per-weapon field (ItmSoundHit / itemdb_soundhit) is dead in the reference server — itemdb_read's
    // SQL SELECT never fetches `sound_hit` — so there's no per-weapon number to port and this stays global.
    // "!hitsnd <id>" recalibrates or mutes (0).
    private int _hitSfx = 349;

    // A MOB's melee, the mirror of the two player fields above and calibrated live 2026-08-04 alongside them:
    // 009.wav on every swing (World.Tick, where the swing is decided), then 001.wav layered on top only when
    // that swing actually connects (Session.ApplyMobHit). Const rather than a calibratable field because the
    // swing half is fired from World, which has no session to hold the knob.
    // NOTE: mob swings currently always land — Combat.RollCritChance's miss result (0) is rolled in
    // ApplyMobHit but only ever used for the crit flavour byte, never to skip the damage. So in practice the
    // two ids always play together today; the split is wired at the right two places for when misses land.
    internal const int MobSwingSfx = 9;   // 009.wav — every mob swing, hit or miss
    internal const int MobHitSfx   = 1;   // 001.wav — additionally, when that swing connects

    // Eating/using a consumable (Session.ItemEatAnim): TWO ids played together, live 2026-08-04 — 403.wav is
    // the chew and 006.wav the gulp; the client mixes them into the one "eat" sound.
    private const int EatSfxA = 403;
    private const int EatSfxB = 6;

    /// <summary>Play the landed-melee impact sfx (<see cref="_hitSfx"/>) over <paramref name="targetId"/> for
    /// everyone on the map. Call this ONLY on a swing that connected — the swing sfx itself is fired separately,
    /// on every attempt.</summary>
    private void PlayHitSfx(uint targetId)
    {
        if (_hitSfx > 0) _world.Broadcast(_char.Map, p => p.SoundAt(_hitSfx, targetId));
    }

    /// <summary>Play the eat/use sfx pair over us, map-wide (the eat POSE is broadcast too — see ItemEatAnim).</summary>
    private void PlayEatSfx()
    {
        _world.Broadcast(_char.Map, p => p.SoundAt(EatSfxA, _char.Id));
        _world.Broadcast(_char.Map, p => p.SoundAt(EatSfxB, _char.Id));
    }

    // Gear-change sfx (NexusTK.snd), calibrated live 2026-08-04. 410 and 411 are the two "dressing" sounds, but
    // which of them plays is per-SLOT and per-TRANSITION, not one on/off pair — the armor slot and the weapon
    // slot are near mirror images of each other:
    //
    //   ARMOR  (wire 2): onto a bare body 410 | swapping over worn armor 411 | taking it off 411
    //   WEAPON (wire 1): drawing one         411 | swapping                  411 | putting it away 410
    //
    // Every other slot is deliberately SILENT: helms (4) and rings (7/8) were confirmed to make no sound, and
    // the remaining accessory slots (shield, necklace, boots, mantle, coat, the sub/crown/face slots) have no
    // calibrated id yet, so they stay quiet rather than borrow a guess. 0 = no sound.
    private static int GearSfx(byte wireSlot, bool equipping, bool replacing) => wireSlot switch
    {
        2 => equipping && !replacing ? 410 : 411,   // ARMOR: bare -> 410; swap and take-off -> 411
        1 => equipping ? 411 : 410,                 // WEAPON: draw/swap -> 411; put away -> 410
        _ => 0,                                     // helm, rings, accessories: silent
    };

    /// <summary>Self-only gear-change sfx (it's paperdoll feedback for the wearer, not a map event like a swing
    /// or an eat pose). See <see cref="GearSfx"/> for the per-slot table.</summary>
    private void PlayGearSfx(byte wireSlot, bool equipping, bool replacing = false)
    {
        int id = GearSfx(wireSlot, equipping, replacing);
        if (id > 0) SendSound(id, _char.Id);
    }

    // "!fistsnd <id>" — set the unarmed swing sfx (see _fistSfx). "!fistsnd 0" mutes it again.
    private void SetFistSound(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var id) || id < 0)
        { SendLog($"usage: !fistsnd <id>   (current: {_fistSfx}; 0 = silent)"); return; }
        _fistSfx = id;
        if (id > 0) SendSound(id, _char.Id);
        SendLog($"fist swing sfx = {id}{(id == 0 ? " (muted)" : "")}");
        Log.Info($"   -> !fistsnd {id}");
    }

    // "!hitsnd <id>" — set the on-connect impact sfx (see _hitSfx). "!hitsnd 0" mutes it again.
    private void SetHitSound(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var id) || id < 0)
        { SendLog($"usage: !hitsnd <id>   (current: {_hitSfx}; 0 = silent)"); return; }
        _hitSfx = id;
        if (id > 0) SendSound(id, _char.Id);
        SendLog($"hit sfx = {id}{(id == 0 ? " (muted)" : "")}");
        Log.Info($"   -> !hitsnd {id}");
    }

    private void SendMusic(ushort bgm, byte type = 2, byte volume = 100)
    {
        var d = new List<byte>();
        d.Add(type);            // +1 type/channel (2 = midi)
        d.Add(0);               // +2 reserved
        d.AddRange(Be(bgm));    // +3 track id (u16 BE)
        d.Add(volume);          // +5 volume 0..100
        SendMap(0x19, _gameInc++, d.ToArray(), $"music(0x19) bgm={bgm} type={type} vol={volume}");
        _bgm = bgm;
    }

    // 0x20 = time-of-day (RTK clif_sendtime, clif.c:4524): hour(u8 0..23) year(u8). This server sent a
    // hardcoded placeholder here forever (0x10/0x32 at world-entry, 0x00/0x00 on the ARRIVAL path) — now
    // fed by World's real hour/year clock (World.Time, ticked by World.Tick — see its HourTicks doc) so the
    // client's day/night overlay actually advances, server-wide, exactly like RTK's own broadcast-to-every-
    // session change_time_char.
    internal void SendTime(byte hour, byte year) =>
        SendMap(0x20, _gameInc++, new byte[] { hour, year }, $"time(0x20) hour={hour} year={year}");

    // 0x1F = weather (RTK clif_sendweather, clif.c:4565): a single byte, 0=clear/1=WRAIN/2=WSNOW (map.h).
    // UNVERIFIED against the real 4.95 client — RTK's own send only fires this opcode when
    // `settingFlags & FLAG_WEATHER` (a later-client options toggle we have no record of on 4.95, and no
    // existing RE evidence either way that 4.95 even renders rain/snow at all); ported at face value since
    // it's the only real wire format on record, same "best real number available, flag it, let it be
    // live-checked" precedent as the still-uncalibrated sound ids elsewhere in this file. "!weather <0-2>"
    // lets the caster audition it directly.
    internal void SendWeather(byte weather) =>
        SendMap(0x1F, _gameInc++, new byte[] { weather }, $"weather(0x1F) {weather}");

    // Music follows the AREA, not the map. Re-sending a track id restarts the song from the top, so a map
    // change only touches the music when the new map's zone actually wants a DIFFERENT track (MapBgm.csv):
    //
    //   * new map's zone track == what's playing  -> nothing sent (Buya -> Buya Kan Shop keeps Tiger going)
    //   * new map is in no zone at all            -> nothing sent, the current song just keeps playing, so
    //                                                every unlisted shop/cave/field inherits its area's theme
    //   * nothing playing yet (fresh session)     -> the zone track, or Content.DefaultBgm on an unzoned map
    //
    // RTK by contrast re-sends the map's bgm on every single map change (clif.c ~4650, inside its map-info
    // send) — it gets away with it because its Maps table gives 9799 of 9850 maps the SAME track, so the
    // client is nearly always being told to (re)start the song it's already playing. We assign per area
    // instead, which means we have to do the "is it already playing?" check ourselves.
    private void PlayMapMusic(ushort mapId)
    {
        var pick = Content.BgmFor(mapId);
        if (pick is null)
        {
            if (_bgm != NoBgm) return;          // sticky: keep the area's song playing into its buildings
            pick = Content.DefaultBgm;          // ... unless nothing is playing at all yet
            if (pick is null) return;
        }
        var (bgm, type) = pick.Value;
        if (bgm == _bgm) return;
        SendMusic(bgm, type);
        Log.Info($"   -> music map {mapId} -> {bgm}.mid ({Content.TrackName(bgm)}) zone '{Content.BgmZoneOf(mapId)}' (0x19)");
    }

    // Login music. The entry burst runs while the client is still building its world object and hasn't even
    // opened Maps\TK<n>.map yet, and a 0x19 MIDI that early is dropped on the floor — the 412 login sfx sent
    // two packets later is audible, the music isn't (both are 0x19; only the midi channel is late to wake).
    // So arm it at entry and send it on the first packet the CLIENT sends back (its own 0x05 view request,
    // ~125ms later in the wire logs), which is proof the world object exists and is dispatching.
    private bool _bgmPending;

    private void ArmEntryMusic() => _bgmPending = true;

    /// <summary>Start the armed login track once the client proves it is live. Called for every inbound
    /// packet; the arrival packet itself doesn't count (it's the one that armed us).</summary>
    private void StartEntryMusicIfArmed(byte opcode)
    {
        if (!_bgmPending || opcode == Opcode.Arrival) return;
        _bgmPending = false;
        PlayMapMusic(_char.Map);
    }

    // "!music <name|id> [vol]" — play a specific track, by song name ("!music mist") or by raw id 1..12 (the
    // stock client ships 12 midis, type 2); see MusicTracks.csv for the name table. vol is the raw volume
    // byte the client log-scales: 100 is nominal "full", but the midi path compresses it, so values ABOVE 100
    // (up to 255) push it louder. "!music 0" / "!music stop" stops the music. Bare "!music" lists the names.
    private void PlayMusicCmd(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            var names = string.Join(", ", Content.MusicTracks
                .Where(t => t.Name.Length > 0).OrderBy(t => t.Name)
                .Select(t => $"{t.Name}({t.Id})"));
            SendLog("usage: !music <name|1-12> [vol 0-255, default 100]   (!music 0 or !music stop = stop)");
            SendLog($"tracks: {names}");
            var zone = Content.BgmZoneOf(_char.Map);
            SendLog($"now playing: {(_bgm == NoBgm ? "nothing" : Describe(_bgm))}" +
                    (zone.Length > 0 ? $"   (this map's zone: {zone})" : "   (this map has no zone)"));
            return;
        }
        if (parts[1].Equals("stop", StringComparison.OrdinalIgnoreCase)) { SendMusic(0); SendLog("music stopped"); return; }

        var track = Content.FindTrack(parts[1]);
        if (track is null) { SendLog($"'{parts[1]}' is not a track name or number (!music with no argument lists them)"); return; }

        byte vol = parts.Length > 2 && byte.TryParse(parts[2], out var v) ? v : (byte)100;
        SendMusic(track.Id, track.Type, vol);
        SendLog(track.Id == 0 ? "music stopped" : $"playing {Describe(track.Id)} (vol {vol})");
        Log.Info($"   -> !music bgm={track.Id} vol={vol}");

        static string Describe(ushort id)
        {
            var name = Content.TrackName(id);
            return name.Length > 0 ? $"{name} (track {id})" : $"track {id}";
        }
    }

    // Play raw client sound ids (0x19 sfx) to calibrate the 4.95 NexusTK.snd id space. RTK's per-spell sound
    // ids may not line up with the client's 001.wav..197.wav numbering, and the user hears "shifted" variants.
    // `!snd 4` plays one; `!snd 4 5 6` plays several; `!snd 1 197 -` (a trailing '-') is rejected — keep it to a
    // few at a time so they don't overlap into noise. Identify each by ear to map RTK sound -> client sound.
    private void SoundProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { SendLog("usage: !snd <id> [id2 …]   (plays client sound ids; NexusTK.snd has 001..197.wav)"); return; }
        int played = 0;
        for (int i = 1; i < parts.Length && played < 8; i++)
        {
            if (!int.TryParse(parts[i], out var id) || id <= 0) continue;
            SendSound(id, _char.Id);
            SendLog($"playing sound {id}");
            Log.Info($"   -> !snd {id}");
            played++;
        }
        if (played == 0) SendLog("no valid sound ids (want positive integers)");
    }

    // "!mtx <type> [text...]" — fire a raw SendMiniText with any type tag, to see how the client actually
    // renders each one (0=wisp/blue, 3=mini/status — the default everything else uses, 5=system — what
    // durability warnings use, 11=group, 12=clan). No text -> a canned "test type N" line.
    private void MiniTextProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var type))
        { SendLog("usage: !mtx <type> [text...]   (0=wisp 3=mini/status 5=system 11=group 12=clan)"); return; }
        string msg = parts.Length > 2 ? string.Join(' ', parts[2..]) : $"test type {type}";
        SendMiniText(msg, (ushort)type);
        SendLog($"sent minitext type={type}: \"{msg}\"");
        Log.Info($"   -> !mtx type={type} \"{msg}\"");
    }

    // "!weather <0-2>" — force THIS map's weather and broadcast it to everyone already standing on it
    // (0=clear, 1=rain/WRAIN, 2=snow/WSNOW). See SendWeather's doc: the 0x1F wire format is ported from RTK
    // at face value but has no live confirmation yet against the real 4.95 client — this is how to check it.
    private void WeatherProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var w) || w is < 0 or > 2)
        { SendLog($"usage: !weather <0-2>   (0=clear 1=rain 2=snow; current: {_world.GetWeather(_char.Map)})"); return; }
        _world.SetWeather(_char.Map, (byte)w);
        SendLog($"map {_char.Map} weather set to {w}");
        Log.Info($"   -> !weather {w} (map {_char.Map})");
    }

    // "!swingsnd <id>" — set the melee swing sfx (and play it once so you can audition it in place). Use with
    // "!snd" to hunt the right woosh id in NexusTK.snd, then "!swingsnd <that id>" bakes it onto every armed
    // swing. "!swingsnd 0" mutes it again. Session-local (resets on relog) until we bake the final id as default.
    private void SetSwingSound(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var id) || id < 0)
        { SendLog($"usage: !swingsnd <id>   (current: {_swingSfx}; 0 = silent)"); return; }
        _swingSfx = id;
        if (id > 0) SendSound(id, _char.Id);
        SendLog($"swing sfx = {id}{(id == 0 ? " (muted)" : "")}");
        Log.Info($"   -> !swingsnd {id}");
    }

    // Play raw Effect.tbl animation ids (0x29) over the caster, to calibrate the 4.95 effect id space vs RTK's
    // sendAnimation ids. Low ids (unaligned heal 5, spark 28) are confirmed identity, but RTK's 6.x/7.x client may
    // have inserted effects that shift mid/high ids — e.g. the aligned heals (Ohaeng 63 / Ming-Ken 64 / Kwi-Sin 65)
    // may not line up. `!efx 5 63 64 65` plays the four heal variants so we can see which id is really which.
    private void EffectProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { SendLog("usage: !efx <id> [id2 …]   (play Effect.tbl anim ids 0..127 over you)"); return; }
        int played = 0;
        for (int i = 1; i < parts.Length && played < 8; i++)
        {
            if (!int.TryParse(parts[i], out var id) || id < 0 || id > 127) continue;
            SendEffect(_char.Id, id);
            SendLog($"effect {id}");
            Log.Info($"   -> !efx {id}");
            played++;
        }
        if (played == 0) SendLog("no valid effect ids (0..127)");
    }

    // "!hit <pct> [crit]" — audition the 0x13 combat packet over the mob you're facing (or yourself if none):
    // draws the over-head HP bar at <pct>% and plays the hit overlay animation 0x8f-<crit>. Use it to calibrate
    // NEXUS_HIT_CRIT (which hit spark looks right) and to confirm the HP bar renders. Default crit = the baked-in
    // HitCritByte. e.g. "!hit 50" (half bar) then "!hit 50 0" / "!hit 50 40" to compare hit animations.
    private void HitProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var pct))
        { SendLog("usage: !hit <pct 0..100> [crit 0..255]   (over-head HP bar + hit anim on the faced mob)"); return; }
        byte crit = parts.Length > 2 && byte.TryParse(parts[2], out var c) ? c : HitCritByte;
        pct = Math.Clamp(pct, 0, 100);

        var (fx, fy) = FrontTile();
        var wmob = _world.MobAt(_char.Map, fx, fy);
        uint target = wmob?.Id ?? MobAt(fx, fy)?.Id ?? _char.Id;
        if (wmob is not null) _world.Broadcast(_char.Map, p => p.DamageOver(target, (byte)pct, crit));
        else                  SendDamage(target, (byte)pct, crit);
        SendLog($"hit id={target} pct={pct} crit={crit} (anim {0x8f - (sbyte)crit})");
        Log.Info($"   -> !hit id={target} pct={pct} crit={crit}");
    }

}
