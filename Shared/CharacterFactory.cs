namespace Shared;

/// <summary>
/// Character creation helpers shared by the login server (account creation / world placement) and the
/// game server (world entry re-derives appearance and places never-before-seen accounts). Kept in Shared
/// so the two processes cannot drift: the appearance decode and home-city rules live in exactly one place.
/// </summary>
public static class CharacterFactory
{
    /// <summary>
    /// The character a <c>0x04</c> CreateAppearance writes, before it is placed in the world. The name is
    /// <paramref name="checkedName"/>, the one the <c>0x02</c> name check carried, and <paramref name="body"/>
    /// is appearance only: the whole body becomes <see cref="Character.CreationBlob"/>, face first.
    ///
    /// <para><c>0x04</c> carries no name. The login server used to read <c>body[0]</c> as a name length, but
    /// on 4.95 that byte is the FACE (docs/4.x/Protocol.md §9), so a face of 1 to 4 turned 1 to 4 appearance
    /// bytes into the "name" and the name gate refused a name the player never typed. No source shows the
    /// 5.33 client sending a name there either, so both clients take the name from <c>0x02</c>.</para>
    ///
    /// <para>This does not gate the name. The caller must run its name gate on the returned character's
    /// <see cref="Character.Name"/> before anything is written.</para>
    /// </summary>
    public static Character FromCreate(string checkedName, byte[] body)
    {
        var c = new Character { SchemaVersion = Character.CurrentSchemaVersion };
        c.Name = checkedName;      // stored with the player's chosen CASING; logins match case-insensitively
        c.CreationBlob = body;     // keep the raw body for future re-decoding if the mapping changes
        ApplyAppearance(c);        // decode face/sex/nation/totem/hair: creation and the legacy import only
        return c;
    }

    // Map the raw 0x04 creation body onto Character fields.
    //
    // Layout confirmed against the REAL RTK char-server source (RTK-Server/rtk/src/char/logif.c
    // logif_parse_newchar, which is authoritative — its call `char_db_newchar(name, pass, totem=B39,
    // sex=B37%2, country=B38, face=B36, hair=B40, faceColor=B42, hairColor=B41)` shows the field ORDER
    // right after the (there, fixed-width) name+pass block is: face, sex, nation, totem, hair, then
    // hair/face color. Our 4.95 blob is a 5-byte tail in that same relative order, just without the two
    // color bytes: [0]=face [1]=sex [2]=nation [3]=totem [4]=hair. This was previously mis-read as
    // "[2]=near-constant misc, [3]/[4]=nation/totem" — that guess never had a sample where nation was
    // deliberately varied; re-decoding under the corrected order lines up perfectly (e.g. "newbie"/
    // "newbieb": 29/3d 00 02 00 00 -> nation=2 Buya, totem=0 JuJak — exactly the picks reported live).
    //   [0]=face  [1]=SEX(0=male,1=female)  [2]=NATION(Character.Nations index)  [3]=TOTEM(0-4)  [4]=hair
    //
    // Render caveat (still true): the 0x33 appearance bytes are a DIFFERENT id space than these creation
    // bytes — appearance[2] (face) uses creation byte[0] directly (proven: faceone=00/facetwo=23/
    // facethree=34 gave three distinct correct faces), but hair has no slot in the 4.95 type-0 render
    // form, so Character.Hair is persisted (creation byte[4]) without being drawn anywhere yet.
    //
    // Applied once in a character's life and never at a login: by FromCreate, and by CharacterStore's import of
    // a legacy per-file record.
    //
    // Not at a login. Each of these is changed in play without the blob changing: the nation by the town criers,
    // Rotah and @nation; the face and the sex by the rogue-guild shaman's paid Change Face and Change Gender; the
    // totem by the shrines' worship and @totem. Applying the blob at every login, as the arrival used to, undid
    // every one of those changes at the next login (PR #325 re-check 3, F7, and its fix round 1). The hair is
    // changed by nothing and drawn by neither client, and is treated the same way.
    //
    // At the legacy import. Creation decoded only face and sex in 521e49b; nation, totem and hair from 85d423b
    // (2026-07-25). Older records hold the compiled-in values, and 521e49b's own comments record an earlier
    // reading of the layout (the face at byte 2, the gender at byte 3), so for those records only the login gave
    // them their picks. They were files in the per-file store, which 07878cf replaced with SQLite the same day;
    // its import still brings in, at every start, any file whose name the database lacks. No such file holds a
    // value changed in play: the first path that changes any of these (the shaman, 5a0b626, 2026-07-27) came
    // after the last write to that store.
    public static void ApplyAppearance(Character c)
    {
        var b = c.CreationBlob;
        if (b is null || b.Length < 2) return;
        c.Sex  = b[1];   // gender: 0=male, 1=female
        c.Face = b[0];   // -> render appearance[2]
        if (b.Length > 2 && b[2] < Character.Nations.Length) c.Nation = b[2];
        // Totem crest, valid range 0..3 (JuJak/Baekho/HyunMoo/ChungRyong). A byte of 4 ("none") keeps the
        // compiled-in 4, which the arrival then clamps into range (the 5.33 pane-wipe guard).
        if (b.Length > 3 && b[3] <= 3) c.Totem = b[3];
        if (b.Length > 4) c.Hair = b[4];   // persisted; no 4.95 render slot yet
    }

    /// <summary>
    /// At a login, put the creation pick back over a saved face or totem that no client can use, exactly as the
    /// login's old re-derivation did for it. Every usable saved value is kept: that is the whole point of no
    /// longer applying <see cref="ApplyAppearance"/> at a login.
    ///
    /// <list type="bullet">
    /// <item><b>A face of <paramref name="faceCount"/> or more.</b> The 4.95 client has heads 0 to 89 only. The
    /// rogue-guild shaman's first version (5a0b626 to 339526b, 2026-07-27 to 2026-08-07) sold RTK's faces 200 to 216,
    /// and its browse wrote each candidate into the character, so a player who left mid-browse took one into the
    /// logout's save. The old login replaced either with the creation face, and so does this. The shaman sells 0 to 89 now, and no
    /// other path writes a face, so a usable saved face is never one the player did not choose.</item>
    /// <item><b>A totem past 3 whose creation byte is 0 to 3.</b> The old login applied that byte. A saved 4 is the
    /// compiled-in "none" of a record created before 85d423b, or a value @totem accepted before 744dfd1 clamped it
    /// (2026-08-17 to 2026-08-22). A totem past 3 with no usable creation byte is left to the arrival's clamp, as
    /// before.</item>
    /// </list>
    /// </summary>
    public static void RestoreUnusableFromCreation(Character c, int faceCount)
    {
        var b = c.CreationBlob;
        if (b is null || b.Length < 2) return;   // the old login did nothing for these either
        if (c.Face >= faceCount) c.Face = b[0];
        if (c.Totem > 3 && b.Length > 3 && b[3] <= 3) c.Totem = b[3];
    }

    // A character's home city — INSIDE the nation's home (RTK Warps.csv door-arrival tiles, not GmWarp's
    // outdoor GM-teleport spot): Buya-aligned characters (Nation==2) start/revive just inside Jadespear's
    // Home (map 351); every other nation just inside Ironheart's Home (map 36). Both are 12x12 (valid
    // tiles 0..11) with an entirely open PASSABLE floor (verified against the real TK351.map/TK36.map
    // pass data — no solid tiles at all), but the OBJECT layer still draws walls/furniture that are only
    // collision-free, not invisible — so a tile can be "walkable" and still look like you're in a wall.
    //
    // Jadespear's tile went through two bad picks before landing on (3,6):
    //   (7,12): the raw Warps.csv door-arrival Y — one past map 351's last valid row (11). The 4.95
    //     client's self-placement check (0x424310) silently bails on an out-of-bounds tile: the
    //     game-world object gets created but the self entity is never placed, so the screen stays black
    //     and movement keys do nothing (GUI still works — it doesn't depend on the world entity).
    //   (7,11): in-bounds, but that row is the bottom wall/threshold strip in TK351.map's object layer
    //     (object ids 636-643) — visually "in a wall" even though it's collision-free. Confirmed clear via
    //     the real map's object grid: (3,6) sits in the empty interior, away from every wall/furniture id.
    //
    // Shared by a fresh character's starting spawn (PlaceNewCharacter) and a defeated character's revive
    // point so both stay in lock-step.
    public static (ushort map, ushort x, ushort y) HomeCityFor(byte nation) =>
        nation == 2 ? ((ushort)351, (ushort)3, (ushort)6) : ((ushort)36, (ushort)5, (ushort)10);

    // Where a BRAND NEW character opens their eyes — which is NOT always the home city, and is the one
    // place the two differ from each other.
    //
    // From 2000-10-06 the game put a small tutorial AREA in front of the kingdoms ("You used to just spawn
    // in next to Jadespear or Ironheart then they added a small area before you entered your starting
    // nation"), so a new character starts in Welcome (map 4711) and only reaches their nation's tutor at
    // the end of it, when the Woodland Angel warps them there. Before that date there was no area and the
    // tutor's home WAS the spawn. See Shared/EraCalendar.cs and docs/common/Era-Gating.md.
    //
    // Nation-independent on purpose: the area sits BEFORE you enter your kingdom, so both nations walk the
    // same rooms. The nation split re-appears at the far end, in the Angel's warp home.
    //
    // Deliberately NOT folded into HomeCityFor: that is also the revive point (Silver Thread, GM fallback),
    // and a defeated veteran must not wake up in the newbie area.
    //
    // Welcome is 16x16 with — verified against TK4711.map — no solid tiles at all. (3,5) is the arrival
    // spot, up and to the left; the way onward is the four-tile doorway along the bottom edge at
    // (8..11, 15), which Warps.csv carries into Open Field (4712).
    public static (ushort map, ushort x, ushort y, ushort xs, ushort ys) StartFor(byte nation)
    {
        if (EraCalendar.Has(EraCalendar.NewbieArea)) return (4711, 3, 5, 16, 16);
        var (m, x, y) = HomeCityFor(nation);
        return (m, x, y, 12, 12);   // both home interiors (36, 351) are 12x12
    }

    // Place a BRAND NEW character (never persisted before) at their starting point instead of Character's
    // compiled-in fallback. MUST run after ApplyAppearance has decoded the real Nation pick (creation
    // byte[2]) or every character would route by the compiled-in default instead of the picked nation.
    //
    // Also rolls starting Vita/Mana here (RTK player.lua Player.reset: baseHealth = random(45,55),
    // baseMagic = random(32,36) — Might/Grace/Will=3/3/3 and baseArmor=99 are already Character's compiled-in
    // defaults, fixed values not rolls, so they don't need re-applying here).
    public static void PlaceNewCharacter(Character c)
    {
        var (map, x, y, xs, ys) = StartFor(c.Nation);
        c.Map = map; c.X = x; c.Y = y;
        c.MapXs = xs; c.MapYs = ys;

        c.MaxHp = c.Hp = (uint)Random.Shared.Next(45, 56);   // inclusive both ends, matches math.random(45,55)
        c.MaxMp = c.Mp = (uint)Random.Shared.Next(32, 37);   // matches math.random(32,36)
    }
}
