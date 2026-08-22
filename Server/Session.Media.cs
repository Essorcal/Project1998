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

    // The 0x1A action that makes a mob visibly SWING, not just play the sound above. RTK's native mob:attack
    // broadcasts this from the C engine; its boss AI does the same thing explicitly with sendAction(2, 20)
    // (rtklua Accepted/Instances/instance_boss.lua) — action type 2, pose length 20 ticks. That's the only RTK
    // reference we have for a MONSTER's melee-pose index: players swing on type 1 (Session.HandleAttack), but a
    // monster sprite sheet indexes its poses differently, and the boss script is a mob using type 2. Broadcast
    // alongside MobSwingSfx wherever a mob commits to a swing (World.Tick's mob->player pass and ApplyMobOnMobHit).
    // TODO(live): confirm type (2 vs 1) and the pose length against the 4.95 client — these two knobs are why
    // they're named constants here rather than inline literals.
    // NOT const: live-tunable via "@mobact <type> [time]" so the attack-pose index can be swept against the
    // client in ONE server session (the creature entity uses vtable 0x4cd098, not the player's, so its type->
    // Monster.tbl-frame mapping isn't the player's 0=stand/1=attack/2=throw table and has to be found by eye).
    internal static byte   MobSwingActionType = 1;    // action type for a mob's attack pose (player attack = 1)
    internal static ushort MobSwingActionTime = 20;   // pose length in ticks (RTK boss uses 20)

    // Eating/using a consumable (Session.ItemEatAnim): TWO ids played together, live 2026-08-04 — 403.wav is
    // the chew and 006.wav the gulp; the client mixes them into the one "eat" sound.
    private const int EatSfxA = 403;
    private const int EatSfxB = 6;

    /// <summary>Play the landed-melee impact sfx (<see cref="_hitSfx"/>) over <paramref name="targetId"/> for
    /// everyone in earshot of the TARGET — RTK binds a landed hit to the thing that got hit, not to the swinger
    /// (clif.c: <c>clif_playsound(&amp;mob-&gt;bl, itemdb_soundhit(...))</c>), and clif_playsound is a SAMEAREA
    /// send. Falls back to our own tile if the target died on this very swing. Call this ONLY on a swing that
    /// connected — the swing sfx itself is fired separately, on every attempt.</summary>
    private void PlayHitSfx(uint targetId)
    {
        if (_hitSfx <= 0) return;
        var (cx, cy) = _world.EntityPos(_char.Map, targetId) ?? (_char.X, _char.Y);
        _world.BroadcastSameArea(_char.Map, cx, cy, p => p.SoundAt(_hitSfx, targetId));
    }

    /// <summary>Play the eat/use sfx pair over us, to everyone in earshot (the eat POSE is broadcast too — see
    /// ItemEatAnim).</summary>
    private void PlayEatSfx()
    {
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.SoundAt(EatSfxA, _char.Id));
        _world.BroadcastSameArea(_char.Map, _char.X, _char.Y, p => p.SoundAt(EatSfxB, _char.Id));
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

    // ---- 0x19 background music ------------------------------------------------------------------------
    //
    // 5.33 DIVERGES on the type-1 (mp3) body — see docs/5.x/Wire-Divergences.md. Its handler is 0x46a420
    // (world dispatcher case 0x19 @ 0x463870) and the type-1 arm reads a flat, TLV-free record:
    //
    //     19 | 01 | 00 | id(u16BE @+3) | fallback(u16BE @+5) | vol(u8 @+7)
    //
    // then calls the resolver 0x4a6360(id, fallback, vol, 0), which sprintf()s THREE candidate names in the
    // wide format strings at 0x5541dc/f0/0x554204 and takes the first that exists in Mus000.dat:
    //
    //     %08d.LST -> an ordered ten-track playlist; plays entry 1, loop flag forced to 1
    //     %08d.LSR -> the same file format; starts at a RANDOM entry (rand % count + 1), loop flag 1
    //     %08d.MP3 -> one song, and the loop flag here is the packet's 4th arg, which the handler
    //                 hardcodes to 0 — so a single mp3 plays ONCE and stops. Background music must be a
    //                 playlist id; single ids are for auditioning with "@music <name>".
    //
    // `fallback` is only reached when none of the three exist, and id 0 matches nothing, so
    // id = fallback = 0 lands on 0x4a5f80(0, …) -> stop-current-then-return: that is the 5.33 mp3 stop.
    //
    // Everything below this line is the 4.95 shape, unchanged. The two channels need DIFFERENT packet
    // bodies there, because they leave 4.95's 0x19 handler at different points:
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
    /// handler itself (type 2 + bgm 0), so it needs no TLV; on 4.95 the mp3 goes through mode 0 = StopSound,
    /// which ignores the id entirely on the type-1 branch (it stops the single player instance), and on 5.33
    /// through the id-0 resolver miss described above.</summary>
    private void SendMusicStop(byte channel)
    {
        if (channel == 0) return;
        SendMap(0x19, _gameInc++, MusicStopBody(_ver, channel), $"music(0x19) STOP channel={channel}");
    }

    /// <summary>The 0x19 body that silences one audio channel (1 = mp3, 2 = midi), per client. Static and
    /// internal so <c>Tests/ClientVersionWireTests</c> can pin both shapes.</summary>
    public static byte[] MusicStopBody(ClientVersion ver, byte channel) =>
        channel != 1                ? new byte[] { channel, 0x00, 0x00, 0x00, 100 }
      : ver == ClientVersion.V533   ? new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 100 }
                                    : new byte[] { 0x01, 0x03, 0x00, 0x01, 100, 0x03, 0x00, ModeStop, 0x00, 0x00, 0x00, 0x00 };

    private void SendMusic(ushort bgm, byte type = 2, byte volume = 100)
    {
        // Silence whatever is already playing first — both when stopping and when switching tracks. Without
        // this, an mp3 started over a midi (or vice versa) just layers on top of it.
        if (_bgmType != 0 && (bgm == 0 || _bgmType != type)) SendMusicStop(_bgmType);

        // bgm 0 = "stop": the line above already emitted the stop for whatever channel was live (its
        // `bgm == 0` arm fires regardless of which channel that was), so there is nothing left to send.
        if (bgm == 0) { _bgm = 0; _bgmType = 0; return; }

        SendMap(0x19, _gameInc++, MusicBody(_ver, bgm, type, volume),
                $"music(0x19) bgm={bgm} type={type} vol={volume}");
        _bgm = bgm;
        _bgmType = type;
    }

    /// <summary>The 0x19 body that starts <paramref name="bgm"/> on <paramref name="type"/>, per client — the
    /// three shapes described at the top of this file. Static and internal so the wire tests can pin them.</summary>
    public static byte[] MusicBody(ClientVersion ver, ushort bgm, byte type, byte volume)
    {
        var d = new List<byte>();
        if (type == 1 && ver == ClientVersion.V533)
        {
            d.Add(0x01);                // +1 type 1 = mp3/playlist (client opens %08d.LST / .LSR / .MP3)
            d.Add(0x00);                // +2 unread on this arm
            d.AddRange(Be(bgm));        // +3 track or playlist id (u16BE)
            d.AddRange(Be((ushort)0));  // +5 fallback id, reached only if NOTHING resolves for +3. 0 = stop,
                                        //    i.e. a track we don't actually have goes quiet instead of
                                        //    driving the player at a resource that isn't there.
            d.Add(volume);              // +7 volume 0..100
        }
        else if (type == 1)
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
        return d.ToArray();
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

    // clif_sendoptions — seeds the options-menu checkboxes for the four SERVER-synced toggles. Opcode 0x23,
    // handled NOT by the main receive table (which defaults it) but by the client's SECOND dispatcher (0x4650d0
    // -> the options-window seed at 0x465200) — the mail-button precedent again. Wire format, RE-verified
    // 2026-08-16 from that handler: four state bytes DIRECTLY after the opcode, in order
    //     [weather(sub 6)] [magic(5)] [advice(4)] [fastmove(9)]
    // and NO RTK-style 0x03 sub-command byte (that shape is a later client — sending it shifts every field by
    // one, which is why the first @sendopts did nothing). The client sets its stored box byte CHECKED iff
    // byte == 0 (seed handler 0x465200 does `sete` on the stored byte) — but that "checked" flag selects the
    // OFF (right) radio, NOT ON. Confirmed by a live capture: with magic bit ON the server was seeding byte 0,
    // the box showed OFF, and the effect still played (inverted). So the byte tracks the OPPOSITE radio: send
    // byte = 1 when the feature is ON (leave the OFF radio unchecked → ON shown) and 0 when off. This also
    // keeps the server bit in phase with the on-screen radio, so the bare `1b <sub>` toggle lands the way the
    // user intended instead of flipping to the opposite state. The handler lives on the options-window object,
    // so this only takes effect once that window exists — hence we send it on F10-open (HandleSetting's sub-0
    // branch) and re-seed after every synced toggle.
    internal void SendOptions()
    {
        // 5.33 DIVERGENCE (live-captured 2026-08-21): do NOT re-seed the 5.33 options window. Its inbound
        // 0x1b toggles carry the SAME sub-commands as 4.95 (0x04 advice / 0x05 magic / 0x06 weather /
        // 0x09 fast-move / 0x0D sounds — verified from the server log), and each toggle already updates
        // that client's own radio correctly, so the server state stays right without any seed. But 0x23
        // is NOT handled by any of 5.33's three receive dispatchers (all resolve it to the shared no-op),
        // so our 4.95-format seed lands somewhere it shouldn't and visibly flips the NEIGHBOURING radios —
        // the reported "toggling Magic also flips Weather/Wisdom/Sound" intermingling. 5.33 tracks its own
        // option state client-side; sending nothing is correct. If 5.33's real seed opcode/format is ever
        // found, seed through that instead. 4.95 keeps the re-seed it needs (stored-byte sync, §9.5).
        if (_ver == ClientVersion.V533) return;

        byte Box(int sub) => (byte)(_char.HasSetting(sub) ? 1 : 0);   // 1 = feature ON (OFF-radio unchecked → ON shown)
        // Fast-move (bit 9) is now persisted in SettingFlags like the other three (set by the sub-9 toggle), so
        // it reads uniformly here and survives relog. Fast-move behaviour stays client-authoritative per walk;
        // this bit is just the remembered preference the checkbox reflects.
        var body = new byte[] { Box(0x06), Box(0x05), Box(0x04), Box(0x09) };
        SendMap(0x23, _gameInc++, body, "options(0x23) weather/magic/advice/fastmove");
    }

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
    /// <summary>Which soundtrack this session hears. The 5.x set needs the 5.33 client's Mus000.dat, so a
    /// 4.95 session stays on the midis no matter what the character's stored preference says (the same
    /// account can be played from either client).</summary>
    private Content.MusicSet MusicSet =>
        _char.NewMusic && IsV533 ? Content.MusicSet.New : Content.MusicSet.Old;

    private void PlayMapMusic(ushort mapId)
    {
        var set = MusicSet;
        var pick = Content.BgmFor(mapId, set);
        if (pick is null)
        {
            if (_bgm != NoBgm) return;          // sticky: keep the area's song playing into its buildings
            pick = Content.DefaultBgmFor(set);  // ... unless nothing is playing at all yet
            if (pick is null) return;
        }
        var (bgm, type) = pick.Value;
        if (bgm == _bgm && type == _bgmType) return;   // same song AND same channel — leave it playing
        SendMusic(bgm, type);
        Log.Info($"   -> music map {mapId} -> {(type == 1 ? $"{bgm:D8}" : $"{bgm}.mid")} " +
                 $"({Content.TrackName(bgm, set)}) zone '{Content.BgmZoneOf(mapId)}' set={set} (0x19)");
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
    // "@music old" / "@music new" pick which of the two SOUNDTRACKS the server draws map music from, and the
    // choice is remembered on the character (Character.NewMusic):
    //
    //   old — the 12 stock midis. Both clients ship them (4.95 NexusTK.snd / 5.33 Snd.dat). The default.
    //   new — the 25 mp3s and 52 playlists in the 5.33 client's Mus000.dat. 5.33 ONLY: 4.95 has the mp3
    //         engine but not one of the files, so offering it there just means silence. Refused with an
    //         explanation on a 4.95 session rather than accepted-and-ignored.
    //
    // The trailing "mp3"/"midi" token overrides the track's own channel (0x19 type), which is what makes this
    // command a calibration tool for the second music backend:
    //
    //   midi (type 2) — the 12 stock songs. HARD-CAPPED at ids 1..12 by BOTH clients (`cmp si, 0xd / jge
    //                   bail`, 4.95 @0x4588b4 / 5.33 @0x475286), so no id above 12 will ever play here.
    //   mp3  (type 1) — 4.95: its XAudio MPEG decoder, fed a LOOSE FILE named "%03d.MP3" (the wide string at
    //                   0x4f3cc0) from the client's own directory — populate those with re/extract_mus.py.
    //                   5.33: an archive lookup in Mus000.dat, and the id may name a playlist (see the wire
    //                   comment above SendMusicStop). No id cap on either; the only guard is bgm > 0.
    //
    // A single mp3 does not loop on either client — the loop flag reaching the play function is a hardcoded
    // 0 (4.95's mode-1 branch @0x463ae8, 5.33's handler pushing 0 as 0x4a6360's 4th arg). Only the 5.33
    // playlist ids get loop=1, which is why the map assignments in MapBgm.csv use them.
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

        var set = MusicSet;

        if (parts.Count < 1)
        {
            var names = string.Join(", ", Content.MusicTracks
                .Where(t => t.Set == set && t.Name.Length > 0).OrderBy(t => t.Name)
                .Select(t => $"{t.Name}({t.Id})"));
            SendLog("usage: @music <name|id> [vol 0-255, default 100] [mp3|midi]   (@music 0 or @music stop = stop)");
            SendLog($"tracks ({(set == Content.MusicSet.New ? "new" : "old")}): {names}");
            if (set == Content.MusicSet.New)
                SendLog("the -seq names are the same ten tracks in order; the plain ones start at a random song");
            SendLog(IsV533
                ? $"soundtrack: {(set == Content.MusicSet.New ? "NEW (5.x mp3 playlists)" : "OLD (the 12 stock midis)")}" +
                  "   — switch with '@music old' or '@music new'"
                : "soundtrack: OLD (the 12 stock midis) — the 5.x set needs a 5.x client, which ships the files");
            var zone = Content.BgmZoneOf(_char.Map);
            SendLog($"now playing: {(_bgm == NoBgm ? "nothing" : Describe(_bgm))}" +
                    (zone.Length > 0 ? $"   (this map's zone: {zone})" : "   (this map has no zone)"));
            return;
        }
        if (parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase)) { SendMusic(0); SendLog("music stopped"); return; }
        if (parts[0].Equals("old", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("new", StringComparison.OrdinalIgnoreCase))
        { SetMusicSet(parts[0].Equals("new", StringComparison.OrdinalIgnoreCase)); return; }

        var track = Content.FindTrack(parts[0], set);
        if (track is null) { SendLog($"'{parts[0]}' is not a track name or number (@music with no argument lists them)"); return; }

        byte type = forceType ?? track.Type;
        byte vol = parts.Count > 1 && byte.TryParse(parts[1], out var v) ? v : (byte)100;

        // The client silently ignores a midi id it can't hold, which reads as "the server is broken" — say so.
        if (type == 2 && track.Id > 12)
            SendLog($"note: track {track.Id} is above the client's midi cap (1-12) and will be silent — try 'mp3'");
        if (type == 1 && !IsV533)
            SendLog($"note: this client has no {track.Id:D3}.MP3 unless you installed one (re/extract_mus.py)");

        SendMusic(track.Id, type, vol);
        SendLog(track.Id == 0 ? "music stopped"
                              : $"playing {Describe(track.Id)} (vol {vol}, " +
                                $"{(type != 1 ? $"midi -> {track.Id}.mid" : IsV533 ? $"mp3 -> {track.Id:D8}" : $"mp3 -> {track.Id:D3}.MP3")}" +
                                $"{(track.Playlist ? ", a 10-track playlist" : type == 1 ? ", plays once" : "")})");
        Log.Info($"   -> @music bgm={track.Id} type={type} vol={vol} set={set}");

        string Describe(ushort id)
        {
            var name = Content.TrackName(id, set);
            return name.Length > 0 ? $"{name} (track {id})" : $"track {id}";
        }
    }

    /// <summary>"@music old" / "@music new" — pick the soundtrack for this character and restart the current
    /// map's track from it. 4.95 is refused outright: it ships none of the 5.x files, so switching there would
    /// be a silent world rather than a different one.</summary>
    private void SetMusicSet(bool wantNew)
    {
        if (wantNew && !IsV533)
        {
            SendLog("the new soundtrack lives in the 5.x client's Mus000.dat — this client doesn't have it.");
            SendLog("staying on the old music (the 12 stock songs).");
            return;
        }
        if (_char.NewMusic == wantNew)
        {
            SendLog($"already on the {(wantNew ? "new" : "old")} music.");
            return;
        }
        _char.NewMusic = wantNew;
        _bgm = NoBgm;                        // forget what was playing so the new set's track is actually sent
        PlayMapMusic(_char.Map);
        SendLog(wantNew ? "new music: the 5.x soundtrack (mp3 playlists)."
                        : "old music: the original twelve songs.");
        Log.Info($"   -> @music set={(wantNew ? "new" : "old")} for {_char.Name}");
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

    // "@weather clear|rain|snow|0|1|2" — pin THIS map's whole region-zone to a weather state (an admin
    // override of the seasonal WeatherModel) and broadcast it to everyone on that zone. "@weather auto" drops
    // the override so the zone returns to season-driven weather. "@weather raw <n>" instead sends one byte
    // STRAIGHT to the 0x1F handler without the band mapping,
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

        if (parts.Length >= 1 && parts[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _world.ClearWeatherOverride(_char.Map);
            byte now = _world.GetWeather(_char.Map);
            SendLog($"map {_char.Map} weather override cleared; season-driven weather is now {WeatherNames[Math.Min(now, (byte)2)]}");
            Log.Info($"   -> @weather auto (map {_char.Map})");
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
            SendLog($"usage: @weather clear|rain|snow   |   @weather auto   |   @weather raw <0-255>");
            SendLog($"map {_char.Map} is {WeatherNames[Math.Min(cur, (byte)2)]}" +
                    (Content.IsIndoor(_char.Map) ? " (indoor - always clear)" : "") +
                    $"; your 'Weather change' toggle is {(_char.HasSetting(0x06) ? "ON" : "OFF - nothing will draw")}");
            return;
        }

        _world.SetWeather(_char.Map, (byte)w);
        SendLog($"zone weather pinned to {WeatherNames[w]} (map {_char.Map} region; @weather auto to release)" +
                (Content.IsIndoor(_char.Map) ? "   (this map is indoor - it stays clear regardless)" : "") +
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
        SendMiniText(SettingLine(hit.Value, on));   // same status-pane line the native 0x1b toggles show

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

    // "@mobact <type> [time]" — calibrate the mob attack-pose action (0x1A). Sets the global MobSwingActionType/
    // Time used by every real mob swing (World.cs), AND immediately plays that action on the mob you're facing so
    // you can eyeball it without waiting for a swing. Sweep <type> 0..8 to find which one drives a creature's
    // Attack frames (Monster.tbl has a per-id Attack field, so the frames exist — the question is the type index
    // for the CREATURE entity vtable, which differs from the player's 1=attack). No mob faced = just sets + says.
    private void MobActionProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !byte.TryParse(parts[0], out var type))
        { SendLog($"usage: @mobact <type> [time]   (current: type={MobSwingActionType} time={MobSwingActionTime})"); return; }
        ushort time = MobSwingActionTime;
        if (parts.Length >= 2 && ushort.TryParse(parts[1], out var t)) time = t;
        MobSwingActionType = type;
        MobSwingActionTime = time;

        var (fx, fy) = FrontTile();
        var wmob = _world.MobAt(_char.Map, fx, fy);
        if (wmob is not null)
        {
            _world.BroadcastSameArea(_char.Map, wmob.X, wmob.Y, p => p.ActionOver(wmob.Id, type, time, 0));   // play it NOW on the faced mob
            SendLog($"mob action type={type} time={time} -> played on '{wmob.Name}' ({wmob.Id})");
        }
        else SendLog($"mob action type={type} time={time} set (face a mob to preview it instantly)");
        Log.Info($"   -> @mobact type={type} time={time} faced={(wmob?.Name ?? "none")}");
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
        if (wmob is not null) _world.BroadcastWideArea(_char.Map, wmob.X, wmob.Y, p => p.DamageOver(target, (byte)pct, crit));
        else                  SendDamage(target, (byte)pct, crit);
        SendLog($"hit id={target} pct={pct} crit={crit} (anim {0x8f - (sbyte)crit})");
        Log.Info($"   -> @hit id={target} pct={pct} crit={crit}");
    }

}
