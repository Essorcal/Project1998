using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ---- mob / combat lab ----
    // The 4.95 creature GRAPHIC id-space is unknown, so we discover it live (look-lab style) via 0x16.
    //   "@mob <hi> <lo> [hp]"   spawn ONE creature on the tile in front of you (gfx = hi*256+lo) so you
    //                           can see it and immediately whack it.
    //   "@mobrow <lo> <hi> [step]"  spawn a W->E row sweeping graphic id lo..hi (step defaults to 1).
    //                           The gfx id is a FRAME index into the monster archive (client adds
    //                           0x4000, category "I"), and Monster.tbl's "Starting" column lists each
    //                           monster's idle frame — the first ~19 monsters start at 0,20,40,...,360.
    //                           So "@mobrow 0 360 20" shows one idle sprite per monster 0..18.
    //   "@spawn [hi] [lo]"      drop a little pack of critters around you at one graphic id.
    //   "@kill"                 despawn every mob.

    /// <summary>Every integer in a command's ARGUMENT TAIL, in order; non-numeric tokens are skipped.
    /// Starts at token 0: handlers are handed the arguments alone (see Server/Commands.cs), never the
    /// whole message. It used to start at 1 to step over the command name, which after the move to the
    /// command table silently ate the FIRST argument of every numeric command — "@stats 50000 50000 130"
    /// parsed as (50000, 130).</summary>
    private static int[] ParseInts(string args)
    {
        var parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var vals = new List<int>();
        for (int i = 0; i < parts.Length; i++) if (int.TryParse(parts[i], out var v)) vals.Add(v);
        return vals.ToArray();
    }

    // "@cre <lookId> [hp] [color]": spawn ONE real monster (Monster.epf, via 0x07) on the tile in front
    // of you, so you can see it AND immediately melee it (combat is unchanged — it hits any Mob on the
    // tile). [color] is the 0x07 color byte we're trying to identify as a recolor/palette selector.
    private void CreatureOne(string text)
    {
        var a = ParseInts(text);
        int look = a.Length > 0 ? a[0] : 0;
        int hp = a.Length > 1 ? a[1] : 6;
        int color = a.Length > 2 ? a[2] : 0;
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SpawnMonster((ushort)look, x, y, $"c{look}", hp, dir: (byte)((_facing + 2) & 3), color: (byte)color);
    }

    // ===== MVP: spawn a rabbit, watch it wander, kill it =====================================
    // The whole lifecycle end-to-end, kept deliberately hardcoded (one rabbit, look 21, 6 HP, random
    // wander near its spawn) before generalizing into a real mob/AI/spawn system. It mirrors how the RTK
    // map-server drives a mob: the server owns the entity + HP, ticks its AI on a timer, streams walk
    // steps (0x0C) to the client, and despawns it (0x0E) on death. Combat is the EXISTING melee path —
    // face the rabbit and press space; HandleAttack finds it on the front tile and deals damage.
    private const ushort RabbitLook = 21;   // Monster.tbl look id — validated shape-match: rabbit = 21

    // "@rabbit": drop a single wandering rabbit into the SHARED world on the tile in front of you.
    // Everyone on the map sees it, everyone fights the SAME one, and World.Tick drives its wander — no
    // per-session task anymore (that only moved the rabbit on the spawner's screen).
    private void SpawnRabbit()
    {
        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        byte dir = (byte)((_facing + 2) & 3);   // face the player on arrival
        // Real registry entry (mobs.csv id 1, key "rabbit"): look 21 color 3, 10hp, 5xp. This used to
        // hardcode color 0, which for look 21 renders like the "Hare" family (id 116+, same look, color
        // 37+) instead of the actual Rabbit — reported live 2026-07-26.
        var def = Content.FindMob("rabbit");
        if (def is not null) SummonWorldMob(def.Look, x, y, def.Name, hp: def.Hp, dir: dir, color: def.Color, exp: def.Exp, moveTime: def.MoveTime, key: def.Key, def: def);
        else SummonWorldMob(RabbitLook, x, y, "Rabbit", hp: 6, dir: dir, color: 0, exp: 5, moveTime: 3000);   // registry missing -> old fallback
        SendLog("A rabbit appears. Face it and press space to attack.");
    }

    // Register a mob in the SHARED world (drawn via 0x07 = Monster.epf) and broadcast the spawn to every
    // player on the map. World.Tick then wanders it (leashed to its spawn tile); combat resolves against
    // the world's authoritative HP in HandleAttack. This is the gameplay-mob path (@rabbit / @summon);
    // the debug lab (@cre/@mob/@crow/look-lab) still uses the session-local SpawnMonster/SpawnMob.
    // `def`, when given, is the real registry entry — its full combat stat block (MinDam/MaxDam/Ac/Grace/
    // Hit/IsBoss/Protection/Will/Aggressive) rides along, exactly like World.Materialize's real spawns.
    // Without it (the `@rabbit` no-registry fallback), a summon defaults to a harmless vanilla mob (1-1
    // damage, 0 AC) rather than silently under-tuned — previously EVERY debug/GM summon (@rabbit/@summon/
    // the ridden-horse re-spawn) dropped these fields entirely, so testing a fix like this one via @summon
    // would never have shown the real numbers.
    private Mob SummonWorldMob(ushort look, ushort x, ushort y, string name, int hp, byte dir, byte color,
                               int exp = 0, int moveTime = 2500, string key = "", MobDef? def = null)
    {
        var mob = new Mob(_world.AllocateMobId(), look, x, y, name, hp)
        {
            Key = key,   // MobDef identifier (for quest kill-matching); empty for keyless debug summons
            Dir = dir, Color = color, Exp = exp, HomeX = x, HomeY = y, Wander = true,
            MoveTime = moveTime, MoveTimer = Random.Shared.Next(moveTime),
            Level = def?.Level ?? 0, Will = def?.Will ?? 0, Aggressive = def?.Aggressive ?? false, Flees = def?.Flees ?? false,
            MinDam = def?.MinDam ?? 1, MaxDam = def?.MaxDam ?? 1, Hit = def?.Hit ?? 0,
            IsBoss = def?.IsBoss ?? false, Protection = def?.Protection ?? 0, Ac = def?.Ac ?? 0, Grace = def?.Grace ?? 0,
        };
        _world.AddMob(_char.Map, mob);   // broadcasts the 0x07 spawn to every player on the map (incl. us)
        Log.Info($"   -> world spawn mob {mob.Id} '{name}' look={look} c{color} @({x},{y}) hp={hp} dmg={mob.MinDam}-{mob.MaxDam} on map {_char.Map}");
        return mob;
    }

    // ===== navigation: warp + map/mob listing + data-driven summon ==========================

    // ---- Mythic Nexus zodiac cave entrances ----
    // RTK gates each of the 12 zodiac caves behind a level/vitals check (Scripts/mythicCaveReqCheck.lua) and
    // an easy/dangerous/deadly tier picker (NPCs/mythic/mythic_cave_selector.lua). With the picker menu off
    // (the default — it's GM/Config-only), RTK auto-warps to the DEEPEST tier the player qualifies for, so we
    // reproduce that: tier 1 -> base map, tier 2 -> base+3000, tier 3 -> base+4000.
    //
    // The entrance tiles, destinations, and per-tier requirements are DATA-DRIVEN from
    // game-data/MythicCaves.csv (Content.MythicCaves / Content.MythicCaveTiles), editable + hot-reloadable
    // via @reload. The requirement numbers are archival — cross-referenced against 4 tutor posts (see the CSV
    // Sources column + Sources.csv tutor-caves-*); the tile/destination geometry is RTK routing.

    // Plural form for the mythic-cave denial line ("Mythic Oxen dwell here"). Every zodiac animal takes a
    // plain "s" except Ox, whose plural is irregular.
    private static string PluralAnimal(string animal) => animal == "Ox" ? "oxen" : animal.ToLowerInvariant() + "s";

    // Deepest tier (1..3) the player unlocks for this cave, or a negative "how close" code when locked out:
    // 0 = within 3 levels, -1 = within 4-7, -2 = 8+ levels short. Mirrors mythicCaveReqCheck.lua exactly.
    private int MythicCaveTier(Content.MythicCaveDef cave)
    {
        for (int i = 2; i >= 0; i--)   // check tier 3 -> 1, return the first satisfied
        {
            var r = cave.Tiers[i];
            if (_char.Level >= r.Level && (_char.MaxHp >= r.Vita || _char.MaxMp >= r.Mana))
                return i + 1;
        }
        int levelsUntil = cave.Tiers[0].Level - _char.Level;
        if (levelsUntil >= 8) return -2;
        if (levelsUntil >= 4) return -1;
        return 0;
    }

    // Handle a step onto a zodiac entrance tile: warp into the deepest unlocked cave tier, or refuse (snap back
    // + flavour line) when under-levelled. Returns false if (current map, x, y) isn't a configured entrance.
    private bool TryMythicCaveEntrance(ushort x, ushort y)
    {
        if (!Content.MythicCaveTiles.TryGetValue((_char.Map, x, y), out var cave)) return false;
        int tier = MythicCaveTier(cave);
        if (tier < 1)
        {
            SendXy();   // cancel the client's step prediction / unblock the next step — the entrance holds them out
            SendMiniText(tier switch   // status box (RTK clif_sendminitext), not the login message box
            {
                -2 => $"That would be unwise. Mythic {PluralAnimal(cave.Animal)} dwell here.",
                0  => "You almost understand the secrets of this entrance.",
                _  => "You are not yet ready to enter here.",
            });
            Log.Info($"   -> MYTHIC {cave.Animal} entrance REFUSED (tier {tier}, level {_char.Level})");
            return true;
        }

        ushort destMap = (ushort)(cave.DestMap + (tier == 3 ? 4000 : tier == 2 ? 3000 : 0));
        if (!Content.TryMap(destMap, out var dm)) { destMap = cave.DestMap; Content.TryMap(destMap, out dm); }
        if (dm is null) { SendXy(); return true; }   // map data missing — don't strand the player
        Log.Info($"   -> MYTHIC {cave.Animal} cave {tier} -> map {destMap} '{dm.Name}' ({cave.DestX},{cave.DestY}) [level {_char.Level}]");
        EnterMap(dm.Id, dm.Xs, dm.Ys, cave.DestX, cave.DestY, dm.Name);
        return true;
    }

    // Class path-hall interior warps (onScriptedTilesPathHalls.lua). Each Kugnae/Buya path hall (Warrior/Rogue/
    // Mage/Poet, both cities) has two scripted-tile doorways that are NOT in the SQL warp table: the SOUTH edge
    // (x 1-2, y 23) into that class's guild hall — class-gated to members of that base class (RTK also lets a
    // Tutor in, a staff role we don't model) — and the NORTH edge (x 8-9, y 1) into the player's alignment
    // sanctum (Unaligned/Kwisin/Mingken/Ohaeng, indexed by Character.Alignment 0-3). Only the map-exit warp is
    // in Warps.csv, so before this the leader-room and hall doors did nothing (or read as solid). The hall/
    // sanctum geometry is data-driven (game-data/PathHalls.csv -> Content.PathHalls); hot-reloads via @reload.
    private bool TryPathHallWarp(ushort x, ushort y)
    {
        if (!Content.PathHalls.TryGetValue(_char.Map, out var hall)) return false;

        // South doorway -> class guild hall (members of that base class only).
        if ((x == 1 || x == 2) && y == 23)
        {
            if (CharClassId != hall.BaseClass)
            {
                // RTK onScriptedTilesPathHalls.lua: player:sendMinitext(str) — the status box, not chat.
                SendMiniText("You are not the right class to enter here.");
                SendXy();   // refuse: hold at the from-tile (RTK bumps 2 tiles north — same net effect)
                return true;
            }
            return WarpHall(hall.GuildMap, (ushort)(x + 6), 3);
        }

        // North doorway -> the player's alignment sanctum (the path-leader room).
        if ((x == 8 || x == 9) && y == 1)
        {
            byte a = _char.Alignment <= 3 ? _char.Alignment : (byte)0;
            return WarpHall(hall.Sanctum[a], (ushort)(x - 3), 18);
        }
        return false;
    }

    // PvP arena doors (onScriptedTilesArena.lua -> arenaPVPCheckAndWarp.lua). Tower Arena is a hub: five side
    // doors, each opening into one level-banded PvP arena. NONE of them are SQL warps — only the return leg is
    // — so before this every door in the room was dead. Geometry + bands are data-driven
    // (game-data/ArenaDoors.csv -> Content.ArenaDoorTiles) and hot-reload via @reload.
    //
    // RTK's own rejection is a 2-tile shove based on facing; we hold at the from-tile with SendXy() like the
    // mythic-cave and path-hall refusals, which is the same net effect on a 4.95 client (self-walk is local,
    // so the step never commits) without needing the facing. The two denial lines are RTK's verbatim, and
    // deliberately NOT the engine's map-req cascade in TryWarpGate — the arena script has its own wording.
    // The "be careful, you may be slain..." entry warning isn't sent here: every arena map is MapPvP=1, so
    // EnterMap's own PvP-crossing warning already fires (same string).
    private bool TryArenaDoor(ushort x, ushort y)
    {
        if (!Content.ArenaDoorTiles.TryGetValue((_char.Map, x, y), out var door)) return false;

        bool low  = _char.Level < door.MinLevel || (door.Unmarked && CharMark != 0);
        // RTK's arena check ORs the two vital caps (the engine's map-req check ANDs them — this is the script's,
        // so it stays OR): being over EITHER cap keeps you out of the capped band.
        bool high = (door.MaxLevel > 0 && _char.Level > door.MaxLevel)
                 || (door.MaxVita > 0 && (long)_char.MaxHp > door.MaxVita)
                 || (door.MaxMana > 0 && (long)_char.MaxMp > door.MaxMana);

        if (low || high)
        {
            SendXy();   // cancel the client's step prediction — the door holds them out
            SendMiniText(low ? "Nightmarish visions of your own death repel you."
                             : "Your honor forbids you from entering.");
            Log.Info($"   -> ARENA '{door.Label}' door REFUSED ({(low ? "under" : "over")}-qualified: level {_char.Level}, vita {_char.MaxHp}, mana {_char.MaxMp})");
            return true;
        }

        if (!Content.TryMap(door.DestMap, out var dm) || dm is null) { SendXy(); return true; }   // dest unrenderable -> don't strand
        ushort dx = door.DestX2 > door.DestX ? (ushort)Random.Shared.Next(door.DestX, door.DestX2 + 1) : door.DestX;
        Log.Info($"   -> ARENA '{door.Label}' -> map {door.DestMap} '{dm.Name}' ({dx},{door.DestY}) [level {_char.Level}]");
        EnterMap(dm.Id, dm.Xs, dm.Ys, dx, door.DestY, dm.Name);
        return true;
    }

    private bool WarpHall(ushort destMap, ushort dx, ushort dy)
    {
        if (!Content.TryMap(destMap, out var dm)) { SendXy(); return true; }   // dest not renderable -> don't strand
        Log.Info($"   -> PATHHALL map {_char.Map} -> {destMap} '{dm.Name}' ({dx},{dy})");
        EnterMap(dm.Id, dm.Xs, dm.Ys, dx, dy, dm.Name);
        return true;
    }

    // ---- After-step scripted tiles (fire once the step has completed, i.e. standing on the new tile) ----
    // RTK runs these from onScriptedTile on every walk. We only port the two that are self-contained AND live
    // entirely on maps the 4.95 client can render: mythic-cave fall-rooms and bush/tree foraging.
    private void OnScriptedTileStep()
    {
        TryForage();                         // adjacent apple tree / rose bush -> small chance of an item
        TryGinseng();                        // Guol Tiger Pass ginseng rocks -> young_ginseng (Chu Rua quest)
        TryLeviathanRelease();               // Blight pen: talisman + a penned captive -> free it
        if (TryLeviathanHermitDoor()) return;// the Hermit's hut door: in if you freed one, shoved back if not (warps)
        if (TryMythicFallRoom()) return;     // mythic cave trap floor -> drop to a lower sub-room (warps)
        TryWorldMapTravel();                 // town edge tile -> inter-continent travel picker
    }

    // ---- Leviathan quest tiles (onScriptedTilesQuest.lua; see Server/LeviathanQuest.cs) -----------
    // Blight pen: stand on the tile below one of the four penned captives holding the talisman and the spell
    // breaks. The captive is DESPAWNED rather than killed — no exp, no loot, and its spawn point refills
    // normally, so the next player finds a captive to free. (RTK removes 9,999,999 health from a mob with a
    // million HP, which is the same thing said in the engine's only vocabulary.)
    //
    // Gated on the legend, not the stage: the legend is only handed out when you report back to Dae-Whan, and
    // it is what stops a player farming the pen. The "you do not have the talisman" line is deliberately
    // moved to AFTER the captive check — RTK tests the item first, so walking that row without a talisman
    // (the normal case for anyone who has finished the quest) spams the status box on every step.
    private void TryLeviathanRelease()
    {
        if (_char.Map != LeviathanQuest.PenMap || _char.Y != LeviathanQuest.PenPlayerY) return;
        if (!LeviathanQuest.PenX.Contains(_char.X)) return;
        if (HasLegend(LeviathanQuest.LegendFreed) || HasLegend(LeviathanQuest.LegendEnemy)) return;

        var captive = _world.MobAt(_char.Map, _char.X, LeviathanQuest.PenCaptiveY);
        if (captive is null || captive.Key != LeviathanQuest.CaptiveMob) return;

        if (!TakeItem(LeviathanQuest.Talisman, 1)) { Notify("You do not have the talisman."); return; }

        Notify("You cast Release leviathan.");
        NpcBubble(captive, "Thank you puny one.");   // NpcBubble prefixes the speaker's own name
        _world.DespawnMob(_char.Map, captive);
        SetQuestStage(LeviathanQuest.Key, LeviathanQuest.StageFreed);
        Log.Info($"   -> LEVIATHAN freed at ({_char.X},{LeviathanQuest.PenCaptiveY}) by {_char.Name}");
    }

    // The Hermit's hut door. Freed his kindred and it lets you in; otherwise it shoves you four tiles south
    // with a "Go AWAY!". True when it moved the player (either way), so the remaining step hooks are skipped.
    private bool TryLeviathanHermitDoor()
    {
        if (_char.Map != LeviathanQuest.DoorMap || _char.Y != LeviathanQuest.DoorY) return false;
        if (!LeviathanQuest.DoorX.Contains(_char.X)) return false;

        if (!HasLegend(LeviathanQuest.LegendFreed))
        {
            Warp(LeviathanQuest.DoorMap, (ushort)_char.X, LeviathanQuest.DoorPushToY);
            Notify("Go AWAY!");
            return true;
        }
        return Warp(LeviathanQuest.HutMap, LeviathanQuest.HutX, LeviathanQuest.HutY);
    }

    // ---- Newbie area, quest 3: the coordinate lesson (npc_dialog.lua TutorialNpc1) ------------------
    // The Deep Forest tutor's task is "walk from here to 0021 0020", and 21,20 is a warp tile (Warps.csv
    // 4714 (21,20) -> 4715 (3,2)) — so the moment the lesson is passed is the moment the player steps onto
    // it and is carried on, not the moment his dialog closes. Paying the exp at the end of his speech (which
    // is where it used to be) rewarded clicking through pages; this rewards actually finding the tile.
    //
    // Called from the WARP branch of HandleWalk rather than OnScriptedTileStep, because a warp returns
    // before the after-step hooks ever run — the player never "stands on" 21,20.
    //
    // Once only, via its own registry flag: the stage can't be used as the marker because stage 5 is also
    // what TutorialNpc2 gates his magic lesson on, and bumping it here would skip that.
    private const string NewbCoordsLearned = "newbie_coords_learned";

    private void TryNewbieCoordinateLesson(ushort mapId, ushort x, ushort y)
    {
        if (mapId != 4714 || x != 21 || y != 20) return;
        if (QuestStage("newbie_area_quest") < 5) return;         // hasn't been set the task yet
        if (QuestCounter(NewbCoordsLearned) != 0) return;        // already paid
        SetQuestStage(NewbCoordsLearned, 1);
        AwardExp(50);                                            // NEWB_STAGE_EXP, same as every other beat
    }

    // ---- Inter-continent travel ("world map" screen) ----
    // RTK triggers this from onScriptedTile on EVERY step (onScriptedTilesMap.lua checks the current
    // map's title + x/y against hardcoded edge coordinates), then opens a destination picker via
    // clif_mapselect (sendWorldMap.lua) — a full-screen "click a location on a map graphic" UI, NOT an
    // NPC/ferry menu. The real click-a-destination flow applies NO level/quest/req gate at all: pc_warp
    // doesn't validate the (map,x,y) the client echoes back, so every listed destination is always usable
    // (RTK gates only one entry, Mount Baekdu, by simply omitting it from the list pre-quest).
    //
    // SendWorldMap's body was recovered by statically disassembling THIS project's own 4.95 client (not
    // guessed, and NOT trusting RTK 7.x, whose clif_mapselect has a different shape): opcode 0x2e's receive
    // handler is 0x450580 (verified via the real two-level dispatch table at 0x44bc80/0x44bbd4:
    // sel = idx[opcode-3], jmp jumptab[sel]; opcode 0x2e -> sel 22 -> stub 0x44bac4 -> call 0x450580).
    // The 0x450580 parser reads, in order, straight off the packet body (payload = bytes AFTER the opcode):
    //   bgNameLen(u8)  <- payload[0] IS the length; there is NO leading "kind" byte
    //   bgName[bgNameLen]
    //   destCount(u8)
    //   one still-unexplained byte
    //   per-destination:  x0(u16BE) y0(u16BE)  name(u8 len + bytes)  mapId(u32BE)  x1(u16BE) y1(u16BE)
    // (each entry is exactly 2 u16 + name + 4 u16; the client reads mapId as two of those u16 slots.)
    // The background is "field10" = "Map of the Kingdom" (the overview world-map art in Inter.dat, one of
    // field10..field18 = the whole-kingdom + per-region maps; NATION_E is only a 20KB flag icon, too small
    // to be a 640x480 background -- that's why it rendered black). Confirmed by rendering the candidate EPFs
    // to a grayscale contact sheet and reading their baked-in title banners. An earlier version of
    // this code sent a spurious leading kind=0 byte, which the client read as bgNameLen=0 -> empty name ->
    // a "%s.epf" path builder produced "." -> catlookup2(".") -> and every later field was shifted one byte,
    // so destCount/offsets became garbage and the handler eventually made a bogus huge allocation and threw.
    // That was OUR one-byte framing error, not a client bug (the client is retail-shipped and works). The
    // client's click/ESC reply is LIVE-CONFIRMED (opcode 0x3F, body mapId(u32BE) x(u16BE) y(u16BE) 00 --
    // RTK's case 0x3F map-change); HandleWorldMapSelect below decodes it and either warps to the clicked
    // destination or, for ESC/unrecognized coords, back to the origin. Of RTK's nine destinations, only
    // Mount Baekdu is omitted outright: its map 4259 has no renderable map data here (game-data/map_index.csv).
    // Hamgyong Nam-Do IS carried, but not to RTK's target: RTK warps it to map 99 ("North Hamgyong Valley"),
    // which has no map data, so it goes to map 114 -- the map literally NAMED "Hamgyong Nam-Do" -- landing on
    // (13,1), just inside the map's north gate. Its return trigger is 114's north edge, y=0 x∈12..15, so the
    // arrival tile sits directly below it. Those four tiles are ALSO Warps.csv 283-286 (114 -> map 99), but that warp
    // never fires here: the warp branch in HandleWalk is gated on Content.TryMap(dest.m), and 99 has no map
    // data, so the step completes normally and the after-step hook below gets the tile. Nagnang IS carried at
    // RTK's own numbers: trigger "Nagnang Gathering" (2520) y=5, x∈7..9 — the top row of that map's walkable
    // corridor, with no competing Warps.csv row — landing back on (8,8). Hausson (1025) is renderable too and
    // could be added the same way; it simply isn't listed yet.
    // X,Y = landing tile on the destination map. Destinations + their field10 dot pixels are data-driven
    // (game-data/WorldMapDests.csv -> Content.WorldDests, order-significant); the trigger tiles that open
    // the screen live in Content.WorldMapTriggers (WorldMapTriggers.csv). Both hot-reload via @reload.
    //
    // DOT PIXELS: DotX/DotY is the CENTRE of the label button, not its top-left. Proven in the client at
    // 0x423600, which the world-map draw loop (0x423500) calls once per entry:
    //     w = textWidth(name) + 0xc ; h = fontHeight * 2
    //     left = x0 - w/2 ; top = y0 - h/2 ; right = left + w ; bottom = top + h
    // So DO NOT scale RTK's 7.x x0/y0 into this space -- those numbers are pixels in a DIFFERENT background
    // image (RTK's "WMkru"), and no scale factor makes them land correctly; that is what put every button in
    // the wrong place. Pick coordinates straight off the real 640x480 artwork instead:
    //     python re/worldmap_plot.py --grid
    // renders field10.epf out of the client's own Inter.dat and draws each button with the exact geometry
    // above, flagging any that fall on the wooden frame or under the "Map of the Kingdom" banner. Iterate
    // there with --move/--add, then bake the numbers into WorldMapDests.csv. ("@wmpos <i> <x> <y>" still
    // works for a live in-client nudge, but the plot tool is the faster loop.)

    // Ephemeral live-tuning overrides for the world-map dot pixels, set by "@wmpos <i> <x> <y>" (index into
    // Content.WorldDests). Not persisted — you eyeball a dot live, then bake the final number into
    // WorldMapDests.csv and @reload. Empty = every dot uses its CSV DotX/DotY.
    private static readonly Dictionary<int, (int X, int Y)> WorldDotOverride = new();

    // True while a world-map screen we sent is (as far as we know) still open on the client, so a stray
    // 0x3F that happens to coincide with a real destination can't be mistaken for a real click.
    private bool _worldMapPending;
    // Where the player was standing when the world map opened. Opening the map makes the client "leave the
    // world" (full-screen modal); pressing ESC sends a 0x3F carrying these origin coords, and we warp back
    // here to restore the view (RTK exits the same way -- see HandleWorldMapSelect).
    private ushort _worldMapReturnMap, _worldMapReturnX, _worldMapReturnY;

    // Fires the native full-screen world-map screen at the real trigger tiles (re-enabled 2026-07-26 after
    // the one-byte framing bug was found and fixed -- see SendWorldMap). Falls back to nothing if bgName
    // resolution fails client-side; if a fresh crash ever recurs, revert this to RunWorldMapMenuAsync().
    private void TryWorldMapTravel()
    {
        if (!Content.WorldMapTriggers.TryGetValue(_char.Map, out var trig) || !trig.Hits(_char.X, _char.Y)) return;
        SendWorldMap("field10");
    }

    // The earlier "crashes regardless of content / client memory-lifetime bug" conclusion was WRONG: the
    // crash was a one-byte framing error in the packet BELOW (a spurious leading kind=0 byte that the client
    // read as bgNameLen=0, misaligning every field -- see the class comment above SendWorldMap). Once that
    // byte is removed and a real background name is used (field10 = "Map of the Kingdom"), the packet parses
    // correctly. The retail client is not buggy. "@wmtest <name>" tries alternate background graphics.
    private void SendWorldMap(string bgName)
    {
        var dests = Content.WorldDests;
        // Origin = where the player opened the map. Captured up-front because it's both the ESC/cancel
        // landing AND the entry-0 override below.
        ushort originMap = _char.Map, originX = _char.X, originY = _char.Y;

        // ESC-CANCEL FIX (2026-07-29, live-proven): the 4.95 client's ESC (exit without choosing) sends
        // back the FIRST destination in the list we send -- there is NO cancel opcode and NO origin echo.
        // (Proof: with Kugnae first, ESC's 0x3F body was byte-identical to the Kugnae dot, 1011/18/14,
        // regardless of where the map was opened -- so it ALWAYS warped to Kugnae. The old code comment
        // claiming ESC "carries the origin" was wrong.) Every trigger map IS one of these destination maps,
        // so we put the player's CURRENT continent first with its landing tile overridden to the exact
        // origin tile: ESC then round-trips to origin (which matches no real WorldDests row, so
        // HandleWorldMapSelect's cancel branch restores the player in place), while every other dot travels
        // as before. Dot PIXELS are unchanged (each dot keeps its own DotX/DotY) -- only wire order shifts.
        var order = new List<int>(dests.Count);
        for (int i = 0; i < dests.Count; i++) if (dests[i].Map == originMap) order.Add(i);
        for (int i = 0; i < dests.Count; i++) if (dests[i].Map != originMap) order.Add(i);
        if (order.Count == 0 || dests[order[0]].Map != originMap)
            Log.Info($"   -> WORLDMAP WARN: opened on map {originMap} with no matching destination row; ESC-cancel will not work (add a WorldMapDests row for this map)");

        var d = new List<byte>();       // NO leading kind byte: payload[0] IS the bgName length (see comment)
        AddLenStr(d, bgName);
        d.Add((byte)dests.Count);
        d.Add(0);                        // unexplained byte after the count -- see class-comment note above
        foreach (int i in order)
        {
            var dest = dests[i];
            // Dot position is field10's own pixel coordinate (WorldMapDests.csv DotX/DotY), unless a live
            // "@wmpos" tweak is overriding it this session -- placed directly on the displayed map, not scaled
            // from RTK. Clamp defensively to the 640x480 art.
            var (dotX, dotY) = WorldDotOverride.TryGetValue(i, out var ov) ? ov : (dest.DotX, dest.DotY);
            int sx = Math.Clamp(dotX, 0, 639);
            int sy = Math.Clamp(dotY, 0, 479);
            // The current-continent entry (position 0) lands on the EXACT origin tile, so an ESC that
            // selects it returns the player precisely where they stood -- not the continent's default tile.
            bool isOrigin = dest.Map == originMap;
            ushort landX = isOrigin ? originX : dest.X;
            ushort landY = isOrigin ? originY : dest.Y;
            d.AddRange(Be((ushort)sx));   // x0 (field10 pixel)
            d.AddRange(Be((ushort)sy));   // y0 (field10 pixel)
            AddLenStr(d, dest.Name);
            d.AddRange(Be32(dest.Map));
            d.AddRange(Be(landX));
            d.AddRange(Be(landY));
        }
        _worldMapPending = true;
        _worldMapReturnMap = originMap;
        _worldMapReturnX   = originX;
        _worldMapReturnY   = originY;
        SendMap(0x2e, _gameInc++, d.ToArray(), $"worldmap(0x2e) bg='{bgName}' {dests.Count} dests (origin map {originMap} first)");
    }

    // Parses the client's world-map click / ESC reply. Body: mapId(u32BE) x(u16BE) y(u16BE) 00 -- RTK's
    // case 0x3F map-change (clif.c:11619, pc_warp with the client-supplied map/x/y). There is NO separate
    // cancel opcode: opening the map makes the client "leave the world", and BOTH a destination click and
    // ESC send this same 0x3F. ESC does NOT echo the origin (old comment was wrong -- see SendWorldMap's
    // ESC-CANCEL FIX); it echoes the FIRST list entry, which we make the player's current continent landing
    // on the origin tile. So: if the reply is the origin tile, treat it as ESC/cancel and restore in place;
    // else warp to the matching known destination; else (unrecognized) also fall back to restoring origin,
    // so the player can never be stranded on the map screen or mis-warped to arbitrary client-chosen coords.
    private void HandleWorldMapSelect(byte[] dec)
    {
        if (!_worldMapPending) return;
        _worldMapPending = false;
        if (dec.Length < 8) return;
        uint   map = (uint)((dec[0] << 24) | (dec[1] << 16) | (dec[2] << 8) | dec[3]);
        ushort x   = (ushort)((dec[4] << 8) | dec[5]);
        ushort y   = (ushort)((dec[6] << 8) | dec[7]);
        // ESC / clicked own location: the reply is the origin tile (entry 0). Restore in place -- must
        // still EnterMap to rebuild the view the modal world-map screen tore down.
        if (map == _worldMapReturnMap && x == _worldMapReturnX && y == _worldMapReturnY)
        {
            if (Content.TryMap(_worldMapReturnMap, out var sm))
            {
                Log.Info($"   -> WORLDMAP (esc/cancel) stay at {_worldMapReturnMap} '{sm.Name}' ({_worldMapReturnX},{_worldMapReturnY})");
                EnterMap(sm.Id, sm.Xs, sm.Ys, _worldMapReturnX, _worldMapReturnY, sm.Name);
            }
            return;
        }
        foreach (var dest in Content.WorldDests)
        {
            if (dest.Map != map || dest.X != x || dest.Y != y) continue;
            if (!Content.TryMap(dest.Map, out var dm)) return;
            Log.Info($"   -> WORLDMAP (native) {_char.Map} -> {dest.Map} '{dm.Name}' ({dest.X},{dest.Y})");
            EnterMap(dm.Id, dm.Xs, dm.Ys, dest.X, dest.Y, dm.Name);
            return;
        }
        // Not a known destination -> treat as ESC/cancel: restore the player to their origin.
        if (Content.TryMap(_worldMapReturnMap, out var om))
        {
            Log.Info($"   -> WORLDMAP (esc/cancel) back to {_worldMapReturnMap} '{om.Name}' ({_worldMapReturnX},{_worldMapReturnY}) [reply map={map} ({x},{y})]");
            EnterMap(om.Id, om.Xs, om.Ys, _worldMapReturnX, _worldMapReturnY, om.Name);
        }
    }

    // "@travel" — chat-command fallback using the already-proven async dialog primitives, so travel keeps
    // working end-to-end even before the native screen's click-reply format (above) is confirmed live.
    private static readonly Mob WorldMapVirtualNpc = new(0xFFFFFFFC, 0, 0, 0, "WorldMap", 1);

    private async Task RunWorldMapMenuAsync()
    {
        // The menu await can suspend for as long as the player takes to answer, during which something
        // else entirely could move them (a GM @warp, death+revive, another dialog, disconnect). Re-verify
        // they're still on the same map when the reply comes back -- same "don't trust state from before
        // the await" discipline as the trade flow re-validating live inventory at finalize.
        ushort startMap = _char.Map;
        int choice = await DlgMenu(WorldMapVirtualNpc, "Where would you like to travel?",
            Content.WorldDests.Select(d => d.Name).ToList());
        if (choice < 1 || choice > Content.WorldDests.Count) return;
        if (_char.Map != startMap) return;   // moved on since we opened the menu
        var d = Content.WorldDests[choice - 1];
        if (!Content.TryMap(d.Map, out var dm)) return;   // dest not renderable here -- silently ignore
        Log.Info($"   -> WORLDMAP (menu) {_char.Map} -> {d.Map} '{dm.Name}' ({d.X},{d.Y})");
        EnterMap(dm.Id, dm.Xs, dm.Ys, d.X, d.Y, dm.Name);
    }

    // Chu Rua's young ginseng (onScriptedTilesQuest.lua, "Guol Tiger Pass" = map 1116): the rocks at x 5-6,
    // y 2-4 hold one young_ginseng. The tiger guards them until you distract him (say "rabbit" -> Forest, which
    // sets chu_rua_tiger_gone); until then it's "too dangerous". (RTK warps to a tiger-free copy, map 1117, but
    // that map isn't renderable here, so we gate on the flag instead and keep you on 1116.)
    private void TryGinseng()
    {
        if (_char.Map != 1116) return;
        if (!((_char.X == 5 || _char.X == 6) && _char.Y >= 2 && _char.Y <= 4)) return;
        if (CountItem("young_ginseng") > 0) return;

        var def = Content.ItemByKey("young_ginseng");
        if (def is null) return;

        // BOTH outcomes are a dialog pop-up carrying the ginseng's own icon, not a minitext: that is what RTK
        // sends (dialogSeq against the item portrait) and what the screenshots on both walkthroughs show
        // (tswolf grabthatdamginseng.gif, nexusatlas churuaginseng.gif / chuaruastrange.gif). Single page,
        // fire-and-forget with the player's own entity id — exactly as the PvP-entry warning in EnterMap does
        // it — because there is no NPC here to hang the dialog on and nothing needs to await the dismissal.
        // (A stray 0x3A with no pending awaiter is a no-op; see HandleNpcDialog.)
        var icon = DialogPortrait.Item(IconOf(def), _ver == ClientVersion.V533 ? def.IconColor : (byte)0);

        if (_char.Quests.GetValueOrDefault("chu_rua_tiger_gone") != 1)
        {
            SendScriptMessageP(_char.Id, "You see a strange root in the rocks here. But with the tiger nearby, " +
                                         "it is too dangerous to try to climb up to it.",
                               icon, prev: false, next: false);
            return;
        }

        if (!GiveItem(def, 1)) return;
        SendScriptMessageP(_char.Id, "Snuggled between the rocks is a young root of ginseng. Was this what Chu Rua meant?",
                           icon, prev: false, next: false);
    }

    // Mythic cave "fall rooms": inside a zodiac cave, every step has a 1/500 chance to drop through the floor
    // to a fixed landing tile in a lower sub-room (onScriptedTilesMythicFallRooms.lua). The source->landing
    // map is data-driven (game-data/FallRooms.csv -> Content.FallRooms, already tier-expanded); hot-reloads
    // via @reload.
    private const int FallRate = 500;

    private bool TryMythicFallRoom()
    {
        if (!Content.FallRooms.TryGetValue(_char.Map, out var f)) return false;
        if (Random.Shared.Next(FallRate) != 0) return false;
        if (!Content.TryMap(f.Map, out var dm)) return false;   // dest not renderable -> no fall (don't strand)
        Log.Info($"   -> FALL through map {_char.Map} -> {f.Map} '{dm.Name}' ({f.X},{f.Y})");
        EnterMap(dm.Id, dm.Xs, dm.Ys, f.X, f.Y, dm.Name);
        return true;
    }

    // Bush/tree foraging (onScriptedTilesBushTree.lua): standing next to an apple tree (object ids 860-864)
    // or a rose bush (876-889), each step has a 1/50 chance to pick an apple / rose. Objects are read from the
    // map's OWN object layer (same ids RTK's checkProximityObjects uses), scanned in the 3x3 around the player.
    private const int ForageRate = 50;
    private void TryForage()
    {
        var map = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (map is null) return;

        string? item = null;
        for (int dy = -1; dy <= 1 && item is null; dy++)
        for (int dx = -1; dx <= 1 && item is null; dx++)
        {
            int tx = _char.X + dx, ty = _char.Y + dy;
            if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) continue;
            ushort o = map.Obj(tx, ty);
            if (o >= 860 && o <= 864) item = "apple";
            else if (o >= 876 && o <= 889) item = "rose";
        }
        if (item is null) return;
        if (Random.Shared.Next(ForageRate) != 0) return;

        var def = Content.FindItem(item);
        if (def is null || !GiveItem(def)) return;
        SendMiniText(item == "apple" ? "You found an apple." : "You find a beautiful rose!");   // RTK onScriptedTilesBushTree.lua: sendMinitext, exact wording
        Log.Info($"   -> FORAGE {item} on map {_char.Map} @({_char.X},{_char.Y})");
    }

    // Move the player to another map (or a far tile) and redraw. On 4.95 the client loads its OWN local
    // Maps\TK<id>.map from the 0x15 mapId, so a warp is just: update tracked position, then re-send the
    // entry trio — 0x15 (map) + 0x04 (coords + camera) + 0x33 (our sprite). The world object (0x02) and
    // our entity id (0x05) are already established this session, so those are NOT resent.
    private void EnterMap(ushort mapId, ushort xs, ushort ys, ushort x, ushort y, string mapName)
    {
        // Warn on crossing INTO a PvP realm (RTK MapPvP flag — Content.IsPvpMap) from a non-PvP one, e.g.
        // stepping through an arena door into Sire Pit/Yusa Pit. Skipped when already in a PvP map (tier
        // warps within the same arena chain shouldn't re-nag every hop).
        // DISABLED per request — the pop-up on arena entry was unwanted. Flip PvpEntryWarning to re-enable
        // the whole thing (the message block below is kept intact on purpose).
        const bool PvpEntryWarning = false;
        bool warnPvp = PvpEntryWarning && Content.IsPvpMap(mapId) && !Content.IsPvpMap(_char.Map);

        // Leave the OLD map in the shared world (despawn us for the players we're leaving behind), and
        // clear our session-local debug dummies (the client drops all foreign entities on a map change).
        _world.LeaveMap(this, _char.Map);
        _mobs.Clear();
        ResetStreamCoverage();   // terrain streamed for the old map says nothing about the new one
        _dlgReply = null;    // orphan any NPC prompt awaiting a reply — its NPC is on the old map
        _worldMapPending = false;   // any open world-map screen is meaningless once we've already warped
        ForgetShownMobs();   // new map -> the client wiped every foreign entity; re-stream from scratch

        _char.Map = mapId;
        _char.MapXs = xs;
        _char.MapYs = ys;
        _char.X = (ushort)Math.Clamp((int)x, 0, xs - 1);
        _char.Y = (ushort)Math.Clamp((int)y, 0, ys - 1);
        MarkDirty();   // map + position, same reasoning as HandleWalk

        SendMapInfo(mapId, xs, ys, mapName, 232, _gameInc++);   // 0x15 (light arg ignored; uses LightValue)
        SendXy();                                                // 0x04 coords + camera anchor
        SendSelfLook();                                          // 0x33 draw self on the new map
        PrimeViewport("warp");                                   // 0x06 fill the window before the client asks
        PlayMapMusic(mapId);                                     // 0x19 swap to the new map's track (if different)
        SendWeather(_world.GetWeather(mapId));                   // 0x1F whatever the new map's weather already is

        // Join the NEW map: draw the players + mobs already there for us, and broadcast us to them.
        var (peers, mobs) = _world.EnterMap(this, mapId);
        foreach (var p in peers) ShowPlayer(p);
        SyncMobs(mobs);   // stream the in-view mobs of the new map
        SyncGroundItems(_world.ItemsOn(mapId));   // in-view floor items of the new map (0x07, viewport-gated)
        SyncMapDoors(mapId);
        if (warnPvp)
        {
            SendScriptMessageP(_char.Id,
                "Be careful, you may be slain by another player within this realm and items on the floor " +
                "can be destroyed by bombs!", DialogPortrait.None, prev: false, next: false);
        }
        Log.Info($"   -> ENTER map {mapId} '{mapName}' {xs}x{ys} @({_char.X},{_char.Y}) — {peers.Length} player(s), {mobs.Length} mob(s) here");
    }

    // Bring the arriving client's object layer in line with the server's. The 4.95 client draws its own local
    // .map file for everything except the narrow 0x06 cell-patch mechanism (the same one door toggles use), so
    // ANY server-side object change is invisible until we replay it — and self-walk is client-local
    // ([[nexustk-495-selfwalk-turn]]), so a door the client still believes is shut keeps refusing the step no
    // matter what the server thinks. Three things need replaying, and MapData.PatchRuns covers all of them at
    // once because every one of them goes through SetObj:
    //   * doors that START open (Content.DoorDefaultOpen, applied in MapData.Load — e.g. the city gates),
    //   * doors another player has toggled since the map was first loaded (previously invisible to later
    //     arrivals, who saw the file state and then got a first 'o' that appeared to do nothing),
    //   * ForceOpen tiles (Doors.cs), which have no real "open" sprite and are simply cleared to object 0.
    private void SyncMapDoors(ushort mapId)
    {
        var md = MapData.For(mapId, _char.MapXs, _char.MapYs);
        if (md is null) return;
        // ForceOpen tiles used to be stamped on here, per session, which mutated shared map state from a
        // session path — they are an AUTHORED override now and applied once in MapData.Load.
        foreach (var (x, y, objs) in md.PatchRuns()) PatchObjRow(x, y, objs);
    }

    // "@warp <map name or id> [x y]": jump to another map by fuzzy name or numeric id, optional coords.
    // Trailing "x y" integers are the destination tile; the rest is the map query. Defaults to map centre.
    private void Warp(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1) { SendLog($"usage: {Prefix}warp <map name or id> [x y]"); return; }

        // Trailing "x y" only counts as coordinates when something is left over to name the map with —
        // "@warp 12 5" is map 12 at whatever's left, not a nameless map at (12,5).
        int? cx = null, cy = null, end = parts.Length;
        if (parts.Length >= 3 && int.TryParse(parts[^1], out var py) && int.TryParse(parts[^2], out var px))
        { cx = px; cy = py; end = parts.Length - 2; }

        string query = string.Join(' ', parts[0..end.Value]);
        var map = Content.FindMap(query);
        if (map is null) { SendLog($"no map matches \"{query}\" — try  @maps {query}"); return; }

        ushort x = (ushort)(cx ?? map.Xs / 2);
        ushort y = (ushort)(cy ?? map.Ys / 2);
        EnterMap(map.Id, map.Xs, map.Ys, x, y, map.Name);
        SendLog($"Warped to {map.Name} (map {map.Id}, {map.Xs}x{map.Ys}) at ({_char.X},{_char.Y}).");
    }

    // "@go <x> <y>": jump to a tile on the map you are ALREADY on — the short, tester-safe half of @warp.
    // Anything that isn't two in-bounds integers (missing argument, a word, a coordinate off the edge of this
    // map) lands you on (0,0) rather than refusing: the command always moves you somewhere, and the reply
    // says which of the two happened.
    private void GoCmd(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int gx = 0, gy = 0;
        bool ok = parts.Length >= 2 && int.TryParse(parts[0], out gx) && int.TryParse(parts[1], out gy)
                  && gx >= 0 && gx < _char.MapXs && gy >= 0 && gy < _char.MapYs;
        if (!ok) { gx = 0; gy = 0; }

        // Same map, so the live dims are already right; the registry only supplies the 0x15 name string.
        // EnterMap is the ONLY proven way to relocate the self entity on 4.95 — a bare 0x04 is a one-tile
        // snap-back, not a teleport — which is why the Rogue leap spells jump the same way, and it is
        // same-map-safe: the World leave/enter pair just re-registers us where we already were.
        bool named = Content.TryMap(_char.Map, out var md);
        string name = named ? md.Name : "Nexus";   // HandleRefresh's fallback: 0x15 needs SOME name string
        string where = named ? $"{name} (map {_char.Map})" : $"map {_char.Map}";
        EnterMap(_char.Map, _char.MapXs, _char.MapYs, (ushort)gx, (ushort)gy, name);

        SendLog(ok
            ? $"Moved to ({_char.X},{_char.Y}) on {where}."
            : $"usage: {Prefix}go <x> <y>  —  0..{_char.MapXs - 1} / 0..{_char.MapYs - 1} on {where}; sent you to (0,0).");
    }

    // "@maps [filter]": list maps, fuzzy-ranked by name (blank = alphabetical). Capped so we don't flood.
    private void ListMaps(string text)
    {
        string q = text.Trim();
        var found = Content.SearchMaps(q, 15);
        if (found.Count == 0) { SendLog(q.Length == 0 ? "no maps loaded (run re/build_map_index.py)" : $"no maps match \"{q}\""); return; }
        SendLog($"maps{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count} of {Content.Maps.Count}):");
        foreach (var m in found) SendLog($"  {m.Id}: {m.Name} ({m.Xs}x{m.Ys})");
    }

    // "@mobs [filter]": list summonable mobs, fuzzy-ranked by name.
    private void ListMobs(string text)
    {
        string q = text.Trim();
        var found = Content.SearchMobs(q, 15);
        if (found.Count == 0) { SendLog(q.Length == 0 ? "no mobs loaded (check game-data/mobs.csv)" : $"no mobs match \"{q}\""); return; }
        SendLog($"mobs{(q.Length > 0 ? $" ~ \"{q}\"" : "")} ({found.Count} of {Content.Mobs.Count}):");
        foreach (var m in found) SendLog($"  {m.Name} — look {m.Look} c{m.Color}, {m.Hp}hp, {m.Exp}xp   (@summon {m.Name})");
    }

    // "@summon <mob name or id>": spawn a real, named creature from the registry on the tile in front of
    // you — correct look + palette colour + HP + exp, all data-driven. Same 0x07 spawn + melee-kill loop
    // as @rabbit, but any of the 700+ mobs by name. (No wander AI yet — that generalizes next.)
    private void Summon(string text)
    {
        string q = text.Trim();
        if (q.Length == 0) { SendLog("usage: @summon <mob name or id>   (browse with  @mobs <name>)"); return; }
        var mob = Content.FindMob(q);
        if (mob is null) { SendLog($"no mob matches \"{q}\" — try  @mobs {q}"); return; }

        var (fx, fy) = FrontTile();
        ushort x = (ushort)Math.Clamp(fx, 0, _char.MapXs - 1);
        ushort y = (ushort)Math.Clamp(fy, 0, _char.MapYs - 1);
        SummonWorldMob(mob.Look, x, y, mob.Name, mob.Hp, dir: (byte)((_facing + 2) & 3), color: mob.Color, exp: mob.Exp, moveTime: mob.MoveTime, key: mob.Key, def: mob);
        SendLog($"Summoned {mob.Name} into the world (look {mob.Look} c{mob.Color}, {mob.Hp}hp, dmg {mob.MinDam}-{mob.MaxDam}).");
    }

    // @reload — hot-reload all file-backed game content (mob stats, items, warps, shop stock, spells, spawns,
    // NPC placements + on/off toggles, crafting-skill toggles, map metadata, mob drops, map BGM, and the Lua
    // verb/dialog scripts) WITHOUT restarting the server, so content fixes ship live. Re-reads the CSVs +
    // Lua, clears the map-terrain cache, and fully rebuilds the world population (World.RebuildPopulation) so
    // ADDED/REMOVED/REPOSITIONED spawns and NPCs take effect — editing AreaSpawns.csv or an NPC's tile no
    // longer needs a restart. The terrain cache for maps that currently have players is pre-warmed OUTSIDE the
    // world lock first, so the .map re-reads don't stall the world under the lock. A load error keeps the OLD
    // content. (Everything file-backed is reloadable now — no compile-time content tables remain that a
    // restart would be needed for.)
    //
    // The work itself lives in World.ReloadFromDisk, because a content deploy has no GM logged in to type
    // this — the CI content lane drops a run/reload_now sentinel and the world picks it up (see
    // RestartSchedule.Loop). This method is now just the chat-facing half: run it, report it to the GM.
    private void ReloadContent()
    {
        var (ok, report) = _world.ReloadFromDisk();
        SendLog(ok ? $"Reloaded: {report}" : $"@reload FAILED: {report}  (previous content kept)");
        Log.Info($"   -> @reload by '{_char.Name}': {report}");
    }

    // @restart [minutes] [reason] | @restart cancel | @restart  (status)
    //
    // The in-game half of RestartSchedule; the other trigger is the run/restart_at file a deploy writes.
    // Note this is deliberately NOT an immediate kill — there is no "@restart now" shorthand, because the
    // whole point of the ladder is that players get told. A GM who genuinely wants it down this second can
    // say "@restart 0", which still announces, still flushes every player, and still takes the grace period.
    private void RestartCmd(string args)
    {
        var sched = _world.Restarts;
        args = args.Trim();

        if (args.Length == 0)
        {
            long left = sched.RemainingMs;
            SendLog(left < 0
                ? $"No restart scheduled.  ({Prefix}restart <minutes> [reason])"
                : $"Restart in {left / 60000}m{left / 1000 % 60:00}s.  ({Prefix}restart cancel to call it off)");
            return;
        }

        if (args.Equals("cancel", StringComparison.OrdinalIgnoreCase)
            || args.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            SendLog(sched.Cancel() ? "Restart cancelled." : "Nothing to cancel.");
            return;
        }

        // "<minutes> [reason]" — the tail after the number is free text, so "@restart 30 deploying 1.2" works.
        var parts = args.Split(' ', 2);
        if (!double.TryParse(parts[0], out double minutes) || minutes < 0 || minutes > 24 * 60)
        {
            SendLog($"Usage: {Prefix}restart <minutes 0-1440> [reason] | {Prefix}restart cancel");
            return;
        }
        string reason = parts.Length > 1 ? parts[1].Trim() : "";

        sched.Schedule(minutes, reason);
        Log.Info($"   -> {Prefix}restart by '{_char.Name}': {minutes} min ({(reason.Length == 0 ? "no reason" : reason)})");
    }

}
