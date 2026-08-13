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
    // Calibrate the id live with "@swingsnd <id>" (auditions it), then it rides every armed swing; 0 = silent.
    private int _swingSfx = 0;

    // Unarmed ("bare fist") swing sfx fallback, used only when no weapon is equipped (EquippedWeaponSound()
    // returns 0). RTK's own C engine special-cases this by sending the swing action with a hardcoded param
    // (pc.c: clif_sendaction(..., 1, attackspeed, 9) when itemdb_sound(weapon)==0) — but that relies on a
    // fixed action-type->sound table baked into the 6.x/7.x client; our own live testing already proved the
    // 4.95 client's action-param byte is ignored for the swing (see the comment above _swingSfx), so we can't
    // reuse that trick here either. There's no RTK item row for "fists" to port a real id from. 009.wav,
    // calibrated live 2026-08-04 — the SAME id a mob's own swing uses (MobSwingSfx below), i.e. a bare fist
    // and a claw/bite land on one shared "unarmed" sound. "@fistsnd <id>" recalibrates or mutes it (0).
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
    // "@hitsnd <id>" recalibrates or mutes (0).
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

    // "@fistsnd <id>" — set the unarmed swing sfx (see _fistSfx). "@fistsnd 0" mutes it again.
    private void SetFistSound(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !int.TryParse(parts[0], out var id) || id < 0)
        { SendLog($"usage: @fistsnd <id>   (current: {_fistSfx}; 0 = silent)"); return; }
        _fistSfx = id;
        if (id > 0) SendSound(id, _char.Id);
        SendLog($"fist swing sfx = {id}{(id == 0 ? " (muted)" : "")}");
        Log.Info($"   -> @fistsnd {id}");
    }

    // "@hitsnd <id>" — set the on-connect impact sfx (see _hitSfx). "@hitsnd 0" mutes it again.
    private void SetHitSound(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !int.TryParse(parts[0], out var id) || id < 0)
        { SendLog($"usage: @hitsnd <id>   (current: {_hitSfx}; 0 = silent)"); return; }
        _hitSfx = id;
        if (id > 0) SendSound(id, _char.Id);
        SendLog($"hit sfx = {id}{(id == 0 ? " (muted)" : "")}");
        Log.Info($"   -> @hitsnd {id}");
    }

    // The two channels need DIFFERENT packet bodies, because they leave the 0x19 handler at different points:
    //
    //   type 2 (midi) — handled inline at 0x450b1b and RETURNS at 0x450bab, before the TLV tail. It reads
    //                   bgm(u16BE)@+3 and volume@+5 and nothing else, so a bare 6-byte body is correct.
    //   type 1 (mp3)  — like type 0 (sfx), it falls THROUGH to the TLV tail at 0x450c48, which keeps reading
    //                   past +5 to build a sound object whose MODE decides whether anything plays at all.
    //                   Send it the 6-byte body and the tail walks off the end of the buffer, producing a
    //                   garbage mode -> silence. It needs the same TLV that SendSound uses (see the long
    //                   comment on Session.SendSound, which is live-verified): tagA=3, B0=0, C=1, skips 0.
    //                   C=1 -> object mode 1 -> play wrapper 0x463ab0 branches to 0x463ae8 -> the play fn
    //                   0x4798c0(id, type, gain, 0), and type==1 there is the sprintf("%03d.MP3") path.
    //
    // body[8] is the object MODE, and it is what decides play-once vs loop vs stop (jump table @0x463c88):
    //
    //   mode 0 -> 0x463ac9 -> StopSound(id, type) @0x479d20 — dispatches BY CHANNEL: type 1 stops the mp3
    //             player (0x478eb0: XAudio cmd 3 STOP + cmd 9 INPUT_CLOSE), type 2 stops the midi player
    //             ([0x4fd3ac] -> 0x4589b0), type 0 stops a matching sfx slot. This is our channel stop.
    //   mode 1 -> 0x463ae8 -> 0x4798c0(id, type, gain, **0**)  — plays ONCE. This is what we sent first, and
    //             it is why the mp3 didn't loop: the loop flag is a hardcoded 0 on that branch.
    //   mode 2 -> 0x463b1d -> sub-dispatch on [obj+0x154] (= body[10], ctor 0x463950 <- ebp+0x24):
    //                0 -> 0x463bb5: 0x4798c0(id, type, gain, **1**)  — plays LOOPED, then returns. <-- ours
    //                1 -> 0x463b80: loop 0
    //                2 -> 0x463b36: loop 1, plus a 0x41b5d0 follow-up we don't want
    //             body[10] is already 0 in this layout, so mode 2 alone gets us a clean looping play.
    private const byte ModeStop = 0x00, ModePlayOnce = 0x01, ModeLoop = 0x02;

    // Which channel is currently playing (0 = none). The client runs the midi and mp3 players INDEPENDENTLY —
    // starting one never stops the other, so switching channels without an explicit stop leaves both audible
    // at once. We track the live channel and stop it before starting anything.
    private byte _bgmType;

    /// <summary>Stop one audio channel (1 = mp3, 2 = midi). The midi player has a dedicated stop path in the
    /// handler itself (type 2 + bgm 0), so it needs no TLV; the mp3 goes through mode 0 = StopSound, which
    /// ignores the id entirely on the type-1 branch (it stops the single player instance).</summary>
    private void SendMusicStop(byte channel)
    {
        if (channel == 0) return;
        var d = channel == 1
            ? new List<byte> { 0x01, 0x03, 0x00, 0x01, 100, 0x03, 0x00, ModeStop, 0x00, 0x00, 0x00, 0x00 }
            : new List<byte> { channel, 0x00, 0x00, 0x00, 100 };
        SendMap(0x19, _gameInc++, d.ToArray(), $"music(0x19) STOP channel={channel}");
    }

    private void SendMusic(ushort bgm, byte type = 2, byte volume = 100)
    {
        // Silence whatever is already playing first — both when stopping and when switching tracks. Without
        // this, an mp3 started over a midi (or vice versa) just layers on top of it.
        if (_bgmType != 0 && (bgm == 0 || _bgmType != type)) SendMusicStop(_bgmType);

        // bgm 0 = "stop": the line above already emitted the stop for whatever channel was live (its
        // `bgm == 0` arm fires regardless of which channel that was), so there is nothing left to send.
        if (bgm == 0) { _bgm = 0; _bgmType = 0; return; }

        var d = new List<byte>();
        if (type == 1)
        {
            d.Add(0x01);                              // +1 type 1 = mp3 (client loads "<bgm:D3>.MP3")
            d.Add(0x03);                              // +2 P0=3 -> TLV tail starts after the 5-byte header
            d.AddRange(Be(bgm));                      // +3 track id (u16BE)
            d.Add(volume);                            // +5 volume (0..100 -> dB gain; 100 = 0 dB)
            d.Add(0x03); d.Add(0x00); d.Add(ModeLoop);// +6..8 tagA=3, B0=0, mode=2 (loop; see table above)
            d.Add(0x00); d.Add(0x00); d.Add(0x00);    // +9..11 B1=0, [obj+0x154]=0, skip=0 -> the loop=1 branch
            d.Add(0x00);                              // +12 trailing pad
        }
        else
        {
            d.Add(type);            // +1 type/channel (2 = midi — returns before the TLV tail, so no tail)
            d.Add(0);               // +2 reserved
            d.AddRange(Be(bgm));    // +3 track id (u16 BE)
            d.Add(volume);          // +5 volume 0..100
        }
        SendMap(0x19, _gameInc++, d.ToArray(), $"music(0x19) bgm={bgm} type={type} vol={volume}");
        _bgm = bgm;
        _bgmType = type;
    }

    // 0x20 = time-of-day (RTK clif_sendtime, clif.c:4524): hour(u8 0..23) year(u8). This server sent a
    // hardcoded placeholder here forever (0x10/0x32 at world-entry, 0x00/0x00 on the ARRIVAL path) — now
    // fed by World's real hour/year clock (World.Time, re-derived each tick from World.Epoch) so the
    // client's day/night overlay actually advances, server-wide, exactly like RTK's own broadcast-to-every-
    // session change_time_char.
    internal void SendTime(byte hour, byte year) =>
        SendMap(0x20, _gameInc++, new byte[] { hour, year }, $"time(0x20) hour={hour} year={year}");

    // 0x1F = weather (RTK clif_sendweather, clif.c:4565): a single byte.
    //
    // CALIBRATED against the 4.95 client 2026-08-08 — and RTK's raw byte does NOT work here. Handler
    // 0x450f40 does not take the value as a state at all; it BANDS it before use:
    //     body[0] <  0x0b        -> state 0        (clear)
    //     body[0] <= 0x63        -> state 1
    //     body[0] >= 0x65        -> state 2
    //     body[0] == 0x64 (100)  -> falls through with the RAW 100 still in the slot — a client bug. Avoid.
    // then applies it via 0x44d340(state, [world+0x401]), i.e. new state + the previous one, so the client
    // can cross-fade. RTK's WRAIN=1 / WSNOW=2 are both < 0x0b, so porting its byte verbatim (which is what
    // this did before) pinned the client to CLEAR forever and no weather could ever render. WeatherWire maps
    // our 0/1/2 into the middle of each band instead.
    //
    // The player's own "Weather change" toggle (0x1b sub-6, Character.SettingFlags) gates it exactly as RTK
    // does: with it off we send 0 rather than not sending, so turning it off clears an effect already on
    // screen instead of leaving it stuck until the next map change.
    private static readonly byte[] WeatherWire = { 0, 0x32, 0x96 };   // clear / band 1 (50) / band 2 (150)

    internal void SendWeather(byte weather)
    {
        bool on = _char.HasSetting(0x06);
        byte wire = on && weather < WeatherWire.Length ? WeatherWire[weather] : (byte)0;
        SendMap(0x1F, _gameInc++, new byte[] { wire }, $"weather(0x1F) {weather} -> wire {wire}{(on ? "" : " (toggle off)")}");
    }

    /// <summary>Re-assert the current map's weather — used by the 0x1b sub-6 toggle, which has to take
    /// effect immediately rather than at the next map change.</summary>
    internal void SendWeather() => SendWeather(_world.GetWeather(_char.Map));

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
        if (bgm == _bgm && type == _bgmType) return;   // same song AND same channel — leave it playing
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

    // "@music <name|id> [vol] [mp3|midi]" — play a specific track, by song name ("@music mist") or by raw id
    // (see MusicTracks.csv for the name table). vol is the raw volume byte the client log-scales: 100 is
    // nominal "full", but the midi path compresses it, so values ABOVE 100 (up to 255) push it louder.
    // "@music 0" / "@music stop" stops the music. Bare "@music" lists the names.
    //
    // The trailing "mp3"/"midi" token overrides the track's own channel (0x19 type), which is what makes this
    // command a calibration tool for the SECOND music backend:
    //
    //   midi (type 2) — the 12 stock songs baked into NexusTK.snd. HARD-CAPPED at ids 1..12 by the client
    //                   itself (`cmp si, 0xd / jge bail` at 0x4588b4), so no id above 12 will ever play here.
    //   mp3  (type 1) — the client's XAudio MPEG decoder, fed a LOOSE FILE named "%03d.MP3" (the wide string
    //                   at 0x4f3cc0) from the client's own directory. No id cap; the only guard is bgm > 0.
    //                   Populate the files with re/extract_mus.py, which lifts them out of the 5.33 client's
    //                   Mus000.dat and renumbers them onto this %03d naming.
    //
    // Looping is NOT yet confirmed on the mp3 path: the loop flag is the 4th arg of 0x4798c0, and which of
    // the play-wrapper's branches we land in (0x463ab0, dispatching on the sound object's mode fields, which
    // come from the 0x19 TLV) decides whether it is pushed as 1 or 0. Some branches pass 1, some pass 0 — so
    // a track may simply stop at the end instead of repeating. That is what this command is for: find out.
    private void PlayMusicCmd(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

        // Pull the optional channel override out first, so it can trail any of the positional args.
        byte? forceType = null;
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            if (parts[i].Equals("mp3", StringComparison.OrdinalIgnoreCase)) forceType = 1;
            else if (parts[i].Equals("midi", StringComparison.OrdinalIgnoreCase)) forceType = 2;
            else continue;
            parts.RemoveAt(i);
        }

        if (parts.Count < 1)
        {
            var names = string.Join(", ", Content.MusicTracks
                .Where(t => t.Name.Length > 0).OrderBy(t => t.Name)
                .Select(t => $"{t.Name}({t.Id})"));
            SendLog("usage: @music <name|id> [vol 0-255, default 100] [mp3|midi]   (@music 0 or @music stop = stop)");
            SendLog($"tracks: {names}");
            SendLog("midi = the 12 stock songs (ids 1-12 only); mp3 = a loose NNN.MP3 in the client dir");
            var zone = Content.BgmZoneOf(_char.Map);
            SendLog($"now playing: {(_bgm == NoBgm ? "nothing" : Describe(_bgm))}" +
                    (zone.Length > 0 ? $"   (this map's zone: {zone})" : "   (this map has no zone)"));
            return;
        }
        if (parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase)) { SendMusic(0); SendLog("music stopped"); return; }

        var track = Content.FindTrack(parts[0]);
        if (track is null) { SendLog($"'{parts[0]}' is not a track name or number (@music with no argument lists them)"); return; }

        byte type = forceType ?? track.Type;
        byte vol = parts.Count > 1 && byte.TryParse(parts[1], out var v) ? v : (byte)100;

        // The client silently ignores a midi id it can't hold, which reads as "the server is broken" — say so.
        if (type == 2 && track.Id > 12)
            SendLog($"note: track {track.Id} is above the client's midi cap (1-12) and will be silent — try 'mp3'");

        SendMusic(track.Id, type, vol);
        SendLog(track.Id == 0 ? "music stopped"
                              : $"playing {Describe(track.Id)} (vol {vol}, " +
                                $"{(type == 1 ? $"mp3 -> {track.Id:D3}.MP3" : $"midi -> {track.Id}.mid")})");
        Log.Info($"   -> @music bgm={track.Id} type={type} vol={vol}");

        static string Describe(ushort id)
        {
            var name = Content.TrackName(id);
            return name.Length > 0 ? $"{name} (track {id})" : $"track {id}";
        }
    }

    // Play raw client sound ids (0x19 sfx) to calibrate the 4.95 NexusTK.snd id space. RTK's per-spell sound
    // ids may not line up with the client's 001.wav..197.wav numbering, and the user hears "shifted" variants.
    // `@snd 4` plays one; `@snd 4 5 6` plays several; `@snd 1 197 -` (a trailing '-') is rejected — keep it to a
    // few at a time so they don't overlap into noise. Identify each by ear to map RTK sound -> client sound.
    private void SoundProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1) { SendLog("usage: @snd <id> [id2 …]   (plays client sound ids; NexusTK.snd has 001..197.wav)"); return; }
        int played = 0;
        for (int i = 0; i < parts.Length && played < 8; i++)
        {
            if (!int.TryParse(parts[i], out var id) || id <= 0) continue;
            SendSound(id, _char.Id);
            SendLog($"playing sound {id}");
            Log.Info($"   -> @snd {id}");
            played++;
        }
        if (played == 0) SendLog("no valid sound ids (want positive integers)");
    }

    // "@mtx <type> [text...]" — fire a raw SendMiniText with any type tag, to see how the client actually
    // renders each one (0=wisp/blue, 3=mini/status — the default everything else uses, 5=system — what
    // durability warnings use, 11=group, 12=clan). No text -> a canned "test type N" line.
    private void MiniTextProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !int.TryParse(parts[0], out var type))
        { SendLog("usage: @mtx <type> [text...]   (0=wisp 3=mini/status 5=system 11=group 12=clan)"); return; }
        string msg = parts.Length > 1 ? string.Join(' ', parts[1..]) : $"test type {type}";
        SendMiniText(msg, (ushort)type);
        SendLog($"sent minitext type={type}: \"{msg}\"");
        Log.Info($"   -> @mtx type={type} \"{msg}\"");
    }

    // "@weather clear|rain|snow|0|1|2" — force THIS map's weather and broadcast it to everyone standing on
    // it. "@weather raw <n>" instead sends one byte STRAIGHT to the 0x1F handler without the band mapping,
    // which is how to explore what each band actually draws: the handler buckets its byte (<0x0b -> 0,
    // 0x0b..0x63 -> 1, >=0x65 -> 2) rather than taking a state, so only three effects exist no matter what
    // you send. Raw 100 is the one value to avoid — it falls through the buckets with the value still in the
    // slot (client bug, see SendWeather).
    private static readonly string[] WeatherNames = { "clear", "rain", "snow" };

    private void WeatherProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2 && parts[0].Equals("raw", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out var raw) && raw is >= 0 and <= 255)
        {
            SendMap(0x1F, _gameInc++, new byte[] { (byte)raw }, $"weather(0x1F) RAW {raw}");
            string band = raw < 0x0b ? "0 (clear)" : raw <= 0x63 ? "1" : raw == 0x64 ? "NONE - falls through, client bug" : "2";
            SendLog($"raw 0x1F byte {raw} -> band {band}   (not stored on the map; @weather <name> to persist)");
            return;
        }

        int w = -1;
        if (parts.Length >= 1)
        {
            w = Array.FindIndex(WeatherNames, n => n.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (w < 0 && int.TryParse(parts[0], out var n) && n is >= 0 and <= 2) w = n;
        }
        if (w < 0)
        {
            byte cur = _world.GetWeather(_char.Map);
            SendLog($"usage: @weather clear|rain|snow   |   @weather raw <0-255>");
            SendLog($"map {_char.Map} is {WeatherNames[Math.Min(cur, (byte)2)]}" +
                    $"; your 'Weather change' toggle is {(_char.HasSetting(0x06) ? "ON" : "OFF - nothing will draw")}");
            return;
        }

        _world.SetWeather(_char.Map, (byte)w);
        SendLog($"map {_char.Map} weather set to {WeatherNames[w]}" +
                (_char.HasSetting(0x06) ? "" : "   (your 'Weather change' toggle is OFF - @setting weather on)"));
        Log.Info($"   -> @weather {WeatherNames[w]} (map {_char.Map})");
    }

    // "@setting [name] [on|off]" — read or set any 0x1b Options toggle from the server side. This exists
    // because the 4.95 Options WINDOW only wires four of them: its click handlers hardcode sub-commands
    // 4 (advice), 5 (magic), 6 (weather) and 9 (fast move) — see the callers of the generic sender 0x4651a0
    // (0x464e0b/0x464e51/0x464e97/0x464edd). Everything else in the flag word either has its own key
    // (whisper F5, group Shift+G, exchange, realm F4) or has NO client affordance at all on this build, so
    // the server is the only way to reach it. See the Show Helmet note in Session.Movement.SettingLabels.
    private void SettingCmd(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            SendLog("usage: @setting <name> [on|off]   (omit on/off to toggle)");
            foreach (var (sub, label) in SettingLabels)
                SendLog($"  {label.ToLowerInvariant().Replace(" ", "-"),-18} {(_char.HasSetting(sub) ? "ON" : "OFF")}");
            return;
        }

        string want = parts[0].Replace("-", " ");
        var hit = SettingLabels.FirstOrDefault(kv =>
            kv.Value.Equals(want, StringComparison.OrdinalIgnoreCase) ||
            kv.Value.Contains(want, StringComparison.OrdinalIgnoreCase));
        if (hit.Value is null) { SendLog($"no setting matches '{parts[0]}' — run @setting for the list"); return; }

        bool on = parts.Length > 1
            ? parts[1].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[1] == "1"
            : !_char.HasSetting(hit.Key);
        if (on != _char.HasSetting(hit.Key)) _char.ToggleSetting(hit.Key);
        SaveChar();
        SendMessage(SettingLine(hit.Value, on));

        if (hit.Key == 0x06) SendWeather();
        // Show Helmet / Show Necklace are stored and announced, but 4.95 has nothing to apply them to: the
        // 7-byte look (reader 0x436120, offsets 0..6) has no helm or necklace slot at all, so a worn helm is
        // never drawn on the body regardless. RTK's own cases 14/15 are tagged "Added 4/6/17" — 2017, a later
        // client. Kept because the bit is real and costs nothing if helm rendering ever lands.
        if (hit.Key is 0x0E or 0x0F)
            SendLog("note: 4.95's appearance has no helm/necklace slot, so this toggle draws nothing on this client");
        Log.Info($"   -> @setting {hit.Value} = {(on ? "ON" : "OFF")}");
    }

    // "@swingsnd <id>" — set the melee swing sfx (and play it once so you can audition it in place). Use with
    // "@snd" to hunt the right woosh id in NexusTK.snd, then "@swingsnd <that id>" bakes it onto every armed
    // swing. "@swingsnd 0" mutes it again. Session-local (resets on relog) until we bake the final id as default.
    private void SetSwingSound(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !int.TryParse(parts[0], out var id) || id < 0)
        { SendLog($"usage: @swingsnd <id>   (current: {_swingSfx}; 0 = silent)"); return; }
        _swingSfx = id;
        if (id > 0) SendSound(id, _char.Id);
        SendLog($"swing sfx = {id}{(id == 0 ? " (muted)" : "")}");
        Log.Info($"   -> @swingsnd {id}");
    }

    // Play raw Effect.tbl animation ids (0x29) over the caster, to calibrate the 4.95 effect id space vs RTK's
    // sendAnimation ids. Low ids (unaligned heal 5, spark 28) are confirmed identity, but RTK's 6.x/7.x client may
    // have inserted effects that shift mid/high ids — e.g. the aligned heals (Ohaeng 63 / Ming-Ken 64 / Kwi-Sin 65)
    // may not line up. `@efx 5 63 64 65` plays the four heal variants so we can see which id is really which.
    private void EffectProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1) { SendLog("usage: @efx <id> [id2 …]   (play Effect.tbl anim ids 0..127 over you)"); return; }
        int played = 0;
        for (int i = 0; i < parts.Length && played < 8; i++)
        {
            if (!int.TryParse(parts[i], out var id) || id < 0 || id > 127) continue;
            SendEffect(_char.Id, id);
            SendLog($"effect {id}");
            Log.Info($"   -> @efx {id}");
            played++;
        }
        if (played == 0) SendLog("no valid effect ids (0..127)");
    }

    // "@hit <pct> [crit]" — audition the 0x13 combat packet over the mob you're facing (or yourself if none):
    // draws the over-head HP bar at <pct>% and plays the hit overlay animation 0x8f-<crit>. Use it to calibrate
    // P1998_HIT_CRIT (which hit spark looks right) and to confirm the HP bar renders. Default crit = the baked-in
    // HitCritByte. e.g. "@hit 50" (half bar) then "@hit 50 0" / "@hit 50 40" to compare hit animations.
    private void HitProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !int.TryParse(parts[0], out var pct))
        { SendLog("usage: @hit <pct 0..100> [crit 0..255]   (over-head HP bar + hit anim on the faced mob)"); return; }
        byte crit = parts.Length > 1 && byte.TryParse(parts[1], out var c) ? c : HitCritByte;
        pct = Math.Clamp(pct, 0, 100);

        var (fx, fy) = FrontTile();
        var wmob = _world.MobAt(_char.Map, fx, fy);
        uint target = wmob?.Id ?? MobAt(fx, fy)?.Id ?? _char.Id;
        if (wmob is not null) _world.Broadcast(_char.Map, p => p.DamageOver(target, (byte)pct, crit));
        else                  SendDamage(target, (byte)pct, crit);
        SendLog($"hit id={target} pct={pct} crit={crit} (anim {0x8f - (sbyte)crit})");
        Log.Info($"   -> @hit id={target} pct={pct} crit={crit}");
    }

}
