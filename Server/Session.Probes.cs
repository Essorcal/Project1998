using System.Text;
using Protocol.Tk495;
using Shared;

namespace Server;

/// <summary>
/// The reverse-engineering PROBES: the <c>@</c> commands whose only job is to put a shape on the wire and
/// let a human look at what the 2001 client does with it (#57 finding 31).
///
/// <para><b>Why they are a file.</b> Nothing in the server calls any of these. Every one of them is reached
/// from exactly one command-table row in <c>Server/Commands.cs</c> and from nowhere else, which is the rule
/// that decided membership: a method moved here if and only if every caller is a table row or another method
/// that moved. Before this they were spread over seven <c>Session</c> partials, interleaved with the
/// production code that shares their subject — the stats probes next to the stats sender, the appearance
/// sweeps next to the appearance sender — so reading any of those files meant reading past a lab bench.</para>
///
/// <para><b>What is NOT here.</b> Operator tools that happen to print under the same table blocks stayed
/// where they were: <c>@weather</c>, <c>@clock</c>, <c>@setting</c>, <c>@music</c>, the three <c>@*snd</c>
/// slots, <c>@npc</c>, <c>@craft</c>, <c>@era</c>, <c>@kill</c>, <c>@summon</c>, <c>@rabbit</c>,
/// <c>@mob</c>, <c>@spawn</c>, <c>@doze</c> and <c>@users</c>. So did the handlers with production callers
/// (<c>SendSelfProfile</c>, <c>SendSpeech</c>) and the two the profile window drives
/// (<c>SendResendProfilePic</c>, <c>ClickProfileCmd</c>). They are not probes, whatever block they sit
/// in.</para>
///
/// <para><b>This was a pure move.</b> Every member below is what it was, byte for byte, comments included,
/// in the order it stood in its source file; the files are in the order the #57 brief lists them. The table
/// rows did not move and did not change — a probe that vanished from the table would be a behaviour change,
/// and <c>CommandTableTests</c> is what catches that.</para>
/// </summary>
public sealed partial class Session
{

    // ---- from Session.GmCommands.cs -------------------------------------------------------------------------------

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

    // Build a 0x0F for a raw client-frame + label with no registry item behind it — for the @icons sweep.
    private void SendRawIcon(byte slot, ushort frame, string label)
    {
        var d = new List<byte> { (byte)(slot + 1) };
        d.AddRange(PacketWriter.U16BEBytes(IconWire(frame)));
        if (_ver == ClientVersion.V533) d.Add(0);          // 5.x icon-color byte (4.95 omits, see SendAddItem)
        var nn = Ascii(label);
        d.Add((byte)nn.Length); d.AddRange(nn);            // display name
        d.Add((byte)nn.Length); d.AddRange(nn);            // base name
        d.AddRange(PacketWriter.U32BEBytes(1));                               // amount
        d.Add(0); d.AddRange(PacketWriter.U32BEBytes(0)); d.Add(0);           // stack/dura/protected block
        d.Add(0); d.AddRange(PacketWriter.U16BEBytes(0)); d.Add(0);             // owner len 0 + trailing u16 + u8
        SendMap(ServerOp.AddItem, _gameInc++, d.ToArray(), $"rawicon(0x0F) slot={slot} frame={frame} wire=0x{IconWire(frame):x4}");
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
            SendDelItem(slot, (DelReason)r);   // the whole point of the sweep is UNNAMED bytes too
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
        PayPaneRule();             // same reason as @mtx: this is the invocation's first pane line
        SendMiniText(msg, type);   // raw, by number — see the sweep above
        Log.Info($"   -> @text '{_char.Name}' type {type}: {msg}");
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
        SendMap(ServerOp.Stats, _gameInc++, d, "stat-gradient(0x08)");
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
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(5), maxHp); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(9), maxMp);
        d[13] = (byte)Math.Clamp(_char.Might + eq.might, 0, 255);
        d[14] = (byte)Math.Clamp(_char.Will  + eq.will,  0, 255);
        d[17] = (byte)Math.Clamp(_char.Grace + eq.grace, 0, 255);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(24), _char.Hp); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(28), _char.Mp);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(32), _char.Exp); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(36), _char.Coins);
        d[off] = val;
        SendMap(ServerOp.Stats, _gameInc++, d, $"mailflag off={off} val=0x{val:x2}");
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
        d.AddRange(PacketWriter.U32BEBytes(1000));      // maxHP
        d.AddRange(PacketWriter.U32BEBytes(500));       // maxMP
        d.Add(11);                   // might
        d.Add(22);                   // will
        d.Add(3); d.Add(3);          // (7.x constants)
        d.Add(33);                   // grace
        d.Add(0); d.Add(0);
        d.Add(0);                    // AC
        d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0);
        d.Add(_char.MaxInv);         // maxinv
        // HPMP block
        d.AddRange(PacketWriter.U32BEBytes(987));       // hp
        d.AddRange(PacketWriter.U32BEBytes(456));       // mp
        // XPMONEY block — zero-free distinctive sentinels so a memory scan finds the STORED copy cleanly
        d.AddRange(PacketWriter.U32BEBytes(0x11223344));  // exp   -> wire 11 22 33 44 ; stored LE 44 33 22 11
        d.AddRange(PacketWriter.U32BEBytes(0x55667788));  // coins -> wire 55 66 77 88 ; stored LE 88 77 66 55
        d.Add(50);                   // exp %
        // ALWAYS block
        d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0); d.Add(0);
        d.AddRange(PacketWriter.U32BEBytes(0));         // settingFlags
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
        (int X, int Y) DotOf(int i) => GmOverrides.WorldDotOverride.TryGetValue(i, out var ov) ? ov : (dests[i].DotX, dests[i].DotY);
        if (a.Int(0, out var wi) && a.Int(1, out var wx) && a.Int(2, out var wy)
            && wi >= 0 && wi < dests.Count)
        {
            var wasDot = DotOf(wi);
            GmOverrides.WorldDotOverride[wi] = (Math.Clamp(wx, 0, 639), Math.Clamp(wy, 0, 479));
            var dot = DotOf(wi);
            GmOverrides.Log(this, $"world-map dot {wi}", $"({wasDot.X},{wasDot.Y})", $"({dot.X},{dot.Y})");
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
            // The shape comes from the table; what each sub-form DOES does not fit in an Args column. Two
            // lines each — the sub-form, then what it does — for the same reason @help splits its rows.
            Reply(a.Usage());
            ReplyList($"{Prefix}pkt sub-forms", new[]
            {
                $"{Prefix}pkt add <tokens>",       " append to the pending packet",
                $"{Prefix}pkt send <hexop>",       " send it, then clear",
                $"{Prefix}pkt show | clear",       " inspect or drop pending",
                $"{Prefix}pkt file <name>",        " send packets/<name>.txt",
            });
            // The token grammar, which this handler really does parse (ParsePacketTokens) and which no Args
            // column can hold. Without it @pkt is unusable from its own help: you can see that it takes
            // "[tokens]" and have no idea what one looks like.
            ReplyList("tokens", new[]
            {
                "xx  one hex byte",
                "#n  u16 big-endian, decimal",
                "%n  u32 big-endian, decimal",
                ":text  ASCII, no length",
                "$text  ASCII, u16BE length",
                "_ inside : or $ = a space",
                "; starts a comment",
            });
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
                if (t[0] == '$') body.AddRange(PacketWriter.U16BEBytes((ushort)b.Length));
                body.AddRange(b);
                continue;
            }
            if (t[0] == '#' && ushort.TryParse(t[1..], out ushort u16)) { body.AddRange(PacketWriter.U16BEBytes(u16)); continue; }
            if (t[0] == '%' && uint.TryParse(t[1..], out uint u32)) { body.AddRange(PacketWriter.U32BEBytes(u32)); continue; }
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

    // ---- from Session.Entity.cs -----------------------------------------------------------------------------------

    // "@nat <n>" — send stats with nation byte = n so we can read which kingdom name/crest the HUD shows.
    // Nation names live in a client data file (no strings in the exe; NATION_E.EPF is a graphic set), so
    // the id -> nation mapping can only be built empirically. Sweep 0,1,2,... and record each.
    private void StatNation(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte n = 0;
        if (parts.Length > 0) byte.TryParse(parts[0], out n);
        byte save = _char.Nation;
        _char.Nation = n;
        SendStats();
        _char.Nation = save;
        Log.Info($"   -> NATION probe: sent nation={n}; read the HUD nation name/crest");
    }

    // "@totem <n>" — same idea as @nat, for the totem crest: send stats with totem byte = n and read which
    // name/graphic the HUD shows. Our documented table (0=JuJak 1=Baekho 2=HyunMoo 3=ChungRyong 4=None) was
    // NEVER actually swept like nation was (§9/§16) — a live report showed a fresh character (Totem defaults
    // to 4, "None" per that table) rendering as ChungRyong, so the table is probably wrong. Sweep 0..4 here
    // to pin the real mapping before wiring totem selection up from the creation packet.
    private void StatTotem(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte n = 0;
        if (parts.Length > 0) byte.TryParse(parts[0], out n);
        byte save = _char.Totem;
        _char.Totem = n;
        SendStats();
        _char.Totem = save;
        Log.Info($"   -> TOTEM probe: sent totem={n}; read the HUD totem name/crest");
    }

    // "@dye <n>" — calibrate the war-paint dye. Sets the persistent armor-dye byte (0x33 appearance[4]) to n
    // and redraws, so we can catalogue which palette index renders as which visible color on THIS 4.95 client
    // (the look-lab confirmed 16/32/64/128/255 recolor and 0..8 stay base, but 9..31 — the range RTK's team
    // colors live in — was never swept). Wear an armor/coat first, or there's nothing to recolor. "@dye" with
    // no number resets to 0 (undyed). Feeds the real color values back into WarPaintAbility's team table.
    private void DyeProbe(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte n = 0;
        if (parts.Length > 0) byte.TryParse(parts[0], out n);
        SetArmorColor(n);
        SendMiniText($"dye = {n}" + (HasVisibleArmor ? "" : "  (no armor/coat worn — nothing to recolor)"), type: 3);
        Log.Info($"   -> DYE probe: appearance[4] = {n}");
    }

    // "@hp <cur> <max>" — send stats with HP=cur, maxHP=max (and the same for MP) to PIN the maxHP/maxMP
    // offsets: if [5]/[9] are really maxHP/maxMP, the HP/MP bar fill becomes cur/max (e.g. 100/1000 = 10%
    // full) and any "cur/max" text shows those numbers. If the bar stays full, the offset is wrong.
    private void StatHpTest(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        uint cur = 100, max = 1000;
        if (parts.Length > 0) uint.TryParse(parts[0], out cur);
        if (parts.Length > 1) uint.TryParse(parts[1], out max);
        var (sh, sm, smh, smm) = (_char.Hp, _char.Mp, _char.MaxHp, _char.MaxMp);
        _char.Hp = cur; _char.MaxHp = max; _char.Mp = cur; _char.MaxMp = max;
        SendStats();
        (_char.Hp, _char.Mp, _char.MaxHp, _char.MaxMp) = (sh, sm, smh, smm);
        Log.Info($"   -> HP/MAX probe: sent HP={cur}/max={max}; expect bar fill = {cur}/{max} and text '{cur}/{max}' if offsets [5]/[9] are correct");
    }

    /// <summary>"@look533" — show the 11 bytes we're sending; "@look533 &lt;i&gt; &lt;v&gt;" — pin byte i to v
    /// and redraw; "@look533 clear" — drop the pins. Each set redraws self and every peer watching, so the
    /// effect is visible immediately without a server restart.</summary>
    internal void Look533Cmd(string args)
    {
        var a = (args ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length >= 1 && a[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            Array.Clear(_look533Override);
            RefreshAppearance();
            SendLog("look533: overrides cleared");
            return;
        }
        if (a.Length >= 2 && int.TryParse(a[0], out var idx) && int.TryParse(a[1], out var val))
        {
            if (idx < 0 || idx >= Look533Len) { SendLog($"look533: index must be 0..{Look533Len - 1}"); return; }
            _look533Override[idx] = (byte)(val & 0xFF);
            RefreshAppearance();
            SendLog($"look533: [{idx}] = {val & 0xFF}");
            return;
        }
        var cur = AppearanceFor(SelfAppearance());
        SendLog("look533 " + string.Join(" ", cur.Select((b, i) => $"{i}:{b}")));
        SendLog("0 sex 1 form 2 face 3 haircolour 4 armor 5 dye 6-7 weapon(u16 flat look) 8 ? 9 shield 10 shield?");
    }

    // ---- from Session.Media.cs ------------------------------------------------------------------------------------

    // Play raw client sound ids (0x19 sfx) to calibrate the 4.95 NexusTK.snd id space. RTK's per-spell sound
    // ids may not line up with the client's 001.wav..197.wav numbering, and the user hears "shifted" variants.
    // `@snd 4` plays one; `@snd 4 5 6` plays several; `@snd 1 197 -` (a trailing '-') is rejected — keep it to a
    // few at a time so they don't overlap into noise. Identify each by ear to map RTK sound -> client sound.
    private void SoundProbe(CommandArgs a)
    {
        if (a.None) { Refuse(a.Usage()); return; }
        int played = 0;
        for (int i = 0; i < a.Count && played < 8; i++)
        {
            if (!a.Int(i, out var id) || id <= 0) continue;
            SendSound(id, _char.Id);
            Reply($"playing sound {id}");
            Log.Info($"   -> @snd {id}");
            played++;
        }
        if (played == 0) Refuse("no valid sound ids (want positive integers)");
    }

    // "@mtx <type> [text...]" — fire a raw SendMiniText with any type tag, to see how the client actually
    // renders each one (0=wisp/blue, 3=mini/status — the default everything else uses, 5=system — what
    // durability warnings use, 11=group, 12=clan). No text -> a canned "test type N" line.
    private void MiniTextProbe(CommandArgs a)
    {
        if (!a.Int(0, out var type)) { Refuse(a.Usage()); return; }
        string msg = a.Count > 1 ? a.Rest(1) : $"test type {type}";
        PayPaneRule();                     // the probe line is this command's first pane line — head it
        SendMiniText(msg, (ushort)type);   // raw, by number: the type under test is the whole point of @mtx
        Reply($"sent minitext type={type}: \"{msg}\"");
        Log.Info($"   -> @mtx type={type} \"{msg}\"");
    }

    // "@mobact <type> [time]" — calibrate the mob attack-pose action (0x1A). Sets the global MobSwingActionType/
    // Time used by every real mob swing (World.cs), AND immediately plays that action on the mob you're facing so
    // you can eyeball it without waiting for a swing. Sweep <type> 0..8 to find which one drives a creature's
    // Attack frames (Monster.tbl has a per-id Attack field, so the frames exist — the question is the type index
    // for the CREATURE entity vtable, which differs from the player's 1=attack). No mob faced = just sets + says.
    private void MobActionProbe(CommandArgs a)
    {
        if (!byte.TryParse(a.Word(0), out var type))
        { Refuse($"{a.Usage()}   (current: type={GmOverrides.MobSwingActionType} time={GmOverrides.MobSwingActionTime})"); return; }
        ushort time = ushort.TryParse(a.Word(1), out var t) ? t : GmOverrides.MobSwingActionTime;
        string was = $"type={GmOverrides.MobSwingActionType} time={GmOverrides.MobSwingActionTime}";
        GmOverrides.MobSwingActionType = type;
        GmOverrides.MobSwingActionTime = time;
        GmOverrides.Log(this, "mob swing action", was, $"type={type} time={time}");

        var (fx, fy) = FrontTile();
        var wmob = _world.MobAt(_char.Map, fx, fy);
        if (wmob is not null)
        {
            _world.BroadcastSameArea(_char.Map, wmob.X, wmob.Y, p => p.ActionOver(wmob.Id, type, time, 0));   // play it NOW on the faced mob
            Reply($"mob action type={type} time={time} -> played on '{wmob.Name}' ({wmob.Id})");
        }
        else Reply($"mob action type={type} time={time} set (face a mob to preview it instantly)");
    }

    // Play raw Effect.tbl animation ids (0x29) over the caster, to calibrate the 4.95 effect id space vs RTK's
    // sendAnimation ids. Low ids (unaligned heal 5, spark 28) are confirmed identity, but RTK's 6.x/7.x client may
    // have inserted effects that shift mid/high ids — e.g. the aligned heals (Ohaeng 63 / Ming-Ken 64 / Kwi-Sin 65)
    // may not line up. `@efx 5 63 64 65` plays the four heal variants so we can see which id is really which.
    private void EffectProbe(CommandArgs a)
    {
        if (a.None) { Refuse(a.Usage()); return; }
        int played = 0;
        for (int i = 0; i < a.Count && played < 8; i++)
        {
            if (!a.Int(i, out var id) || id < 0 || id > 127) continue;
            SendEffect(_char.Id, id);
            Reply($"effect {id}");
            Log.Info($"   -> @efx {id}");
            played++;
        }
        if (played == 0) Refuse("no valid effect ids (0..127)");
    }

    // "@hit <pct> [crit]" — audition the 0x13 combat packet over the mob you're facing (or yourself if none):
    // draws the over-head HP bar at <pct>% and plays the hit overlay animation 0x8f-<crit>. Use it to calibrate
    // P1998_HIT_CRIT (which hit spark looks right) and to confirm the HP bar renders. Default crit = the baked-in
    // HitCritByte. e.g. "@hit 50" (half bar) then "@hit 50 0" / "@hit 50 40" to compare hit animations.
    private void HitProbe(CommandArgs a)
    {
        if (!a.Int(0, out var pct)) { Refuse(a.Usage()); return; }
        byte crit = byte.TryParse(a.Word(1), out var c) ? c : HitCritByte;
        pct = Math.Clamp(pct, 0, 100);

        var (fx, fy) = FrontTile();
        var wmob = _world.MobAt(_char.Map, fx, fy);
        uint target = wmob?.Id ?? MobAt(fx, fy)?.Id ?? _char.Id;
        if (wmob is not null) _world.BroadcastWideArea(_char.Map, wmob.X, wmob.Y, p => p.DamageOver(target, (byte)pct, crit));
        else                  SendDamage(target, (byte)pct, crit);
        Reply($"hit id={target} pct={pct} crit={crit} (anim {0x8f - (sbyte)crit})");
        Log.Info($"   -> @hit id={target} pct={pct} crit={crit}");
    }

    // ---- from Session.Dialog.cs -----------------------------------------------------------------------------------

    // "@leg" — replay the EXACT 0x39 self-profile captured from a real 6.x server (jeedee/TkServer),
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
        SendMap(ServerOp.SelfProfile, _gameInc++, Profile6x, "replay6x-profile(0x39)");
        Log.Info("   -> REPLAY 6.x self-profile on 0x39 (expect: AC 99, class Peasant, legend 'Born in Hyul 31, Winter')");
    }

    // "@ckm" — send a 0x34 click-profile with DISTINCT MARKER strings in every text field, so we can
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

    // ---- from Session.Movement.cs ---------------------------------------------------------------------------------

    // "@boardobj" — board-sign calibration probe. Reports the tile you're FACING, the object sprite id sitting
    // there (RTK's board sprites are 1619/1620; the 4.95 id is TBD), and whether a BoardLocations row already
    // matches. Stand below a board looking north and run this to capture the (map,x,y) for BoardLocations.csv.
    private void BoardObjProbe()
    {
        int dx = 0, dy = 0;
        switch (_facing & 3) { case 0: dy = -1; break; case 1: dx = 1; break; case 2: dy = 1; break; case 3: dx = -1; break; }
        int fx = _char.X + dx, fy = _char.Y + dy;
        string dir = (_facing & 3) switch { 0 => "N", 1 => "E", 2 => "S", _ => "W" };
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        int obj = (md != null && fx >= 0 && fy >= 0 && fx < _char.MapXs && fy < _char.MapYs) ? md.Obj(fx, fy) : -1;
        bool match = Content.TryBoardAt(_char.Map, fx, fy, out var bid);
        string boardName = match ? (Boards.Find(bid)?.Name ?? "?") : "-";
        SendLog($"boardobj: map {_char.Map} you@({_char.X},{_char.Y}) facing {dir} -> tile ({fx},{fy}) obj={obj} | board={(match ? $"{bid} \"{boardName}\"" : "none")}");
        SendLog($"  to register: add  {_char.Map},{fx},{fy},<BoardId>  to BoardLocations.csv then @reload");
        // Board ids come straight from Boards.All so this hint can't go stale when the roster changes; a few
        // per line because one chat line can't hold the whole roster.
        foreach (var chunk in Boards.All.Select(b => $"{b.Id}={b.Name}").Chunk(4))
            SendLog("  boards: " + string.Join("  ", chunk));
        Log.Info($"   -> @boardobj map {_char.Map} facing {dir} tile ({fx},{fy}) obj={obj} match={match} board={bid}");
    }

    // ---- from Session.Social.cs -----------------------------------------------------------------------------------

    private uint _probeId = 1000;

    // Up to 7 whitespace-separated byte values from a command's ARGUMENT TAIL (see Server/Commands.cs);
    // missing/unparseable positions stay 0. Indexes from token 0 — handlers no longer see the command name.
    private static byte[] ParseBytes(string args)
    {
        var parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var app = new byte[7];
        for (int i = 0; i < parts.Length && i < 7; i++) byte.TryParse(parts[i], out app[i]);
        return app;
    }

    // Spawn one dummy just north of the player with the given 7 appearance bytes; its name is the
    // bytes so the screen is self-labeling. New id each call so repeats don't collide.
    private void LookOne(string text)
    {
        var app = ParseBytes(text);
        uint id = ++_probeId;
        ushort x = (ushort)Math.Clamp(_char.X, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        SendLook(id, x, y, dir: 2, app, renderKind: 1, $"{app[0]}-{app[1]}-{app[2]}", $"look-lab {id}");
        Log.Info($"   -> LOOK dummy id={id} @({x},{y}) app=[{string.Join(" ", app)}]");
    }

    // "@row i lo hi [body]": sweep appearance byte [i] from lo..hi across a west->east row of dummies, all
    // other bytes 0. One screenshot then maps that byte's entire id space. Optional 4th arg sets appearance
    // byte [0] (the BODY/sex) for the whole row — default 1 (female, the historically-swept base); pass 0 to
    // sweep the MALE body (its weapon/shield defaults differ from female — male frame 0 was never mapped).
    private void LookRow(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int idx = parts.Length > 0 && int.TryParse(parts[0], out var pi) ? Math.Clamp(pi, 0, 6) : 0;
        int lo = parts.Length > 1 && int.TryParse(parts[1], out var pl) ? pl : 0;
        int hi = parts.Length > 2 && int.TryParse(parts[2], out var ph) ? ph : lo + 7;
        int body = parts.Length > 3 && int.TryParse(parts[3], out var pb) ? pb : 1;
        ushort y = (ushort)Math.Clamp(_char.Y - 2, 0, _char.MapYs - 1);
        int col = 0;
        for (int v = lo; v <= hi && col < 12; v++, col++)
        {
            // Base = valid body (0)=body, normal form (1)=0, so sweeping [2..6] reads cleanly instead of
            // being blanked by the form/state byte. appearance[1] itself is the form table (0/4 normal,
            // 1 ghost, 3 mounted, 5 invisible-spell, most others = no sprite).
            var app = new byte[] { (byte)body, 0, 0, 0, 0, 0, 0 };
            app[idx] = (byte)v;
            uint id = ++_probeId;
            ushort x = (ushort)Math.Clamp(_char.X - 4 + col, 0, _char.MapXs - 1);
            SendLook(id, x, y, dir: 2, app, renderKind: 1, $"{idx}={v}", $"row byte[{idx}]={v}");
        }
        Log.Info($"   -> LOOK row: appearance[{idx}] sweep {lo}..{hi}");
    }

    // ---- from Session.Navigation.cs -------------------------------------------------------------------------------

    // "@cre <lookId> [hp] [color]": spawn ONE real monster (Monster.epf, via 0x07) on the tile in front
    // of you, so you can see it AND immediately melee it (combat is unchanged — it hits any Mob on the
    // tile). [color] is the 0x07 color byte we're trying to identify as a recolor/palette selector.
    private void CreatureOne(CommandArgs a)
    {
        int look = a.Int(0, 0);
        int hp = a.Int(1, 6);
        int color = a.Int(2, 0);
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SpawnMonster((ushort)look, x, y, $"c{look}", hp, dir: (byte)((_facing + 2) & 3), color: (byte)color);
    }
}
