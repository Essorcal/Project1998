using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // Client attack (0x13, spacebar) = just a trigger ("13 00"). Reply with an ACTION packet 0x1A
    // so the entity plays the swing. (0x13 was WRONG — its handler 0x4508f0 computes anim = 0x8f-a,
    // and a=0 -> anim 0x8f = the DEATH animation, which is why the character flashed "dead".)
    // 0x1A = entityId(u32BE) type(u8) time(u16BE) param(u8); handler 0x4503a0 plays the action
    // (client scales time x10). type: 0=stand,1=attack,2=throw,3=shot,4=sit,6=magic,8=eat.
    private void HandleAttack(byte[] dec)
    {
        if (_char.Hp == 0) { SendMiniText("Spirits cannot attack."); return; }
        SendAction(_char.Id, type: 1, time: 8, param: 0);                                 // our own swing anim
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 1, 8, 0), except: this);  // peers see us swing

        // Weapon swing sfx: the client plays no sound for the swing action itself, so send one over 0x19 on
        // EVERY swing, armed or not — weapon in hand -> its own ItmSound (RTK's per-weapon mapping — most
        // swords 331, Sword of power 337, …); bare hands -> the calibratable fist fallback (_fistSfx, see its
        // doc — no real RTK id exists to port for "no weapon"). !swingsnd overrides either case for
        // calibration. Everyone on the map hears it, bound to us.
        int weaponSwing = EquippedWeaponSound();
        int swing = _swingSfx > 0 ? _swingSfx : weaponSwing > 0 ? weaponSwing : _fistSfx;
        if (swing > 0) _world.Broadcast(_char.Map, p => p.SoundAt(swing, _char.Id));

        // Melee resolves against whatever creature stands on the tile directly in front of us (facing
        // tracked from the last walk step). Check the SHARED world FIRST — HP there is world-authoritative
        // so two players can't double-kill and both claim the reward — then fall back to session-local
        // debug dummies (look-lab / !cre / !mob sweeps, visible only to us).
        var (fx, fy) = FrontTile();

        var wmob = _world.MobAt(_char.Map, fx, fy);
        if (wmob is not null)
        {
            var (dmg, crit) = PlayerSwingDamage(wmob);
            if (_world.TryDamage(_char.Map, wmob, dmg, out bool died, _char.Id))
            {
                var weapon = _char.Equipment.FirstOrDefault(e => e.Slot == 1);   // EQ_WEAP: deductWeapon(rage) on a landed swing
                if (weapon is not null) DeductDura(weapon);
                ShowDamageResult(wmob.Id, wmob, died, crit ? (byte)0xFF : HitCritByte, (byte)Math.Clamp(_hitSfx, 0, 255));   // 0x13: over-head HP bar + hit anim + on-connect impact sfx (empty bar + delayed despawn on death)
                Log.Info($"   -> hit world mob {wmob.Id} '{wmob.Name}' for {dmg}{(crit ? " (CRIT)" : "")} -> {wmob.Hp}/{wmob.MaxHp}");
                if (died)
                {
                    uint reward = (uint)(wmob.Exp > 0 ? wmob.Exp : wmob.MaxHp);   // real mob Exp; fallback to HP
                    AwardExp(reward, killExp: true);                                             // reward to the killer only (levels too)
                    SendMessage($"You defeated {wmob.Name}. (+{reward} exp)");
                    Log.Info($"   -> world mob {wmob.Id} '{wmob.Name}' defeated (+{reward} exp)");
                    TallyKill(wmob);   // bump the lifetime kill count for quests (see TallyKill / KillCount)
                }
            }
            return;
        }

        var mob = MobAt(fx, fy);
        if (mob is null) return;

        var (dummyDmg, dummyCrit) = PlayerSwingDamage(mob);
        mob.Hp -= dummyDmg;
        bool dummyDied = !mob.Alive;
        SendDamage(mob.Id, dummyDied ? (byte)0 : HpPercent(mob), dummyCrit ? (byte)0xFF : HitCritByte, (byte)Math.Clamp(_hitSfx, 0, 255));   // 0x13: over-head HP bar + hit anim + on-connect impact sfx (dummy is session-local)
        Log.Info($"   -> hit dummy {mob.Id} '{mob.Name}' for {dummyDmg}{(dummyCrit ? " (CRIT)" : "")} -> {mob.Hp}/{mob.MaxHp}");
        if (dummyDied)
        {
            _mobs.Remove(mob);
            uint deadId = mob.Id;
            if (DeathDespawnMs <= 0) SendDespawn(deadId);   // 0x0E: remove the corpse from our client
            else _ = Task.Run(async () => { try { await Task.Delay(DeathDespawnMs); SendDespawn(deadId); } catch { } });   // after the death beat
            AwardExp((uint)mob.MaxHp, killExp: true);  // reward: exp equal to the mob's max HP (levels too)
            SendMessage($"You defeated {mob.Name}. (+{mob.MaxHp} exp)");
            Log.Info($"   -> dummy {mob.Id} '{mob.Name}' defeated");
        }
    }

    // 0x1d = the emote wheel (press ':'), body[0] = emote index. The client plays action
    // (index + 11) — see RTK clif_parseemotion: sendaction(&bl, RFIFOB(5)+11, 0x4E, 0). The +11 maps
    // index 0 -> action 11 (Laughter) ... index 11 -> action 22 (Dance) ... index 13 -> 24 (Kiss).
    // Broadcast it as a 0x1A action so we AND every peer on the map see the animation (the client's own
    // action sprite carries any looped sound). time 0x4E matches RTK's emote length; param 0 = no extra sound.
    private void HandleEmotion(byte[] dec)
    {
        if (dec.Length < 1) return;
        byte action = (byte)(dec[0] + 11);
        const ushort time = 0x4E;
        SendAction(_char.Id, action, time, 0);                                       // play it on our own client
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, action, time, 0), except: this);  // and for peers
        Log.Info($"   -> EMOTE idx={dec[0]} -> action {action} (0x1A)");
    }

    // The map tile one step ahead of the player, in the direction we're currently facing.
    private (int x, int y) FrontTile()
    {
        int x = _char.X, y = _char.Y;
        switch (_facing & 3) { case 0: y--; break; case 1: x++; break; case 2: y++; break; case 3: x--; break; }
        return (x, y);
    }

    // First living mob standing on (x,y), or null.
    private Mob? MobAt(int x, int y) =>
        _mobs.FirstOrDefault(m => m.Alive && m.X == x && m.Y == y);

    private void SendAction(uint id, byte type, ushort time, byte param)
    {
        var d = new List<byte>();
        d.AddRange(Be32(id));
        d.Add(type);
        d.AddRange(Be(time));
        d.Add(param);
        SendMap(0x1A, _gameInc++, d.ToArray(), $"action(0x1A) type={type} time={time}");
    }

}
