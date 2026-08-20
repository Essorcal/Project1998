-- Per-creature AI hooks. See Server/MobScript.cs.
--
-- This file is the ESCAPE HATCH, not the AI. A creature's ordinary behaviour -- wander, chase, swing, threat,
-- idle chatter, spellcasting, boss survival -- is C# and data (MobChatter.csv, MobSpells.csv, MobBosses.csv,
-- MobSpawnRules.csv). A mob only belongs here when RTK's script does something none of those can express.
--
-- Four hooks, all EVENTS: on_attacked, on_healed, after_death, on_spawn. There is deliberately no `move` or
-- `attack` hook: those fire for every mob on every 600ms heartbeat, and the survey of RTK's 265 mob tables
-- found nothing in them that isn't data. Keeping Lua off the tick is what lets these hooks run outside the
-- world lock, where they are free to speak, heal, vanish and touch a player's quest registry.
--
-- ctx: key, name, hp, maxHp, x, y, alive, gameDate, hasStatus(cat), say(line[, channel]), heal(n), vanish(),
--      actorName, actorTell(line), actorQuest(key, value), actorQuestStage(key),
--      actorHasLegend(mark), actorAddLegend(text, mark, icon, colour)

mobs = {}

-- The yin/yang/void mice (RTK AI/normal_mobs/yin_yang_void_mouse.lua). Zapping one WHILE IT IS CURSED is the
-- thing being recorded: the mouse squeaks and the registry flag `zapped_<mob>` goes on the attacker, which
-- some later step reads. Not data — the flag's name is derived from the creature and the trigger is a status
-- check on the victim at the moment of the hit.
local function squeak(ctx)
  if not ctx:hasStatus("curses") then return end
  ctx:actorQuest("zapped_" .. ctx.key, 1)
  ctx:say("Squeek!")
end

mobs.yin_mouse   = { on_attacked = squeak }
mobs.yang_mouse  = { on_attacked = squeak }
mobs.void_mouse  = { on_attacked = squeak }

-- Leviathans remember. You take the old one's talisman on the promise not to harm his kind; KILL one while
-- the quest is open and you are branded "Sworn enemy of the Leviathans". Once you have actually freed them
-- ("leviathan_freed") the door closes -- a freed-kin legend outranks the grudge. Clearing the brand costs a
-- million coins at the Ancient Leviathan (see Server/LeviathanQuest.cs).
--
-- ON THE KILL, NOT THE HIT: RTK hangs this off on_attacked, so in RTK a single stray swing brands you --
-- but the in-game instructions are explicit that it is killing one that counts ("do not kill any
-- Leviathans"), and a million-coin penalty for one mis-click is not the quest anyone played. The player-facing
-- text wins over RTK's script here. after_death is the hook, so the actor is the killer.
-- The Ice Beast greets the world every time it reforms (RTK Mobs/ice_beast.lua on_spawn). Channel 0
-- attributes the line to it, so this shows as "Ice Beast: Ho, ho! ...". Its chase taunts are data
-- (MobChatter.csv); the lava self-destruct that makes this greeting recur is engine (World.StepMobTo, keyed
-- IceBeastKey). The rest of the questline: Blood + the Nameless Hermit (npc_dialog.lua) and the lava/shoes
-- gate (Session.Navigation.cs, TryIceBeastLava).
mobs.ice_beast = {
  on_spawn = function(ctx) ctx:say("Ho, ho! It is good to be back!") end
}

mobs.leviathan = {
  after_death = function(ctx)
    if ctx:actorHasLegend("leviathan_sworn_enemy") or ctx:actorHasLegend("leviathan_freed") then return end
    if ctx:actorQuestStage("leviathan") == 0 then return end
    ctx:actorAddLegend("Sworn enemy of the Leviathans (" .. ctx.gameDate .. ")", "leviathan_sworn_enemy", 7, 4)
    ctx:actorTell("You promised to not attack us! You are now a sworn enemy of the Leviathan.")
  end
}
