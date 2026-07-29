-- Data-driven NPC dialog (the async cousin of spell_verbs.lua / item_verbs.lua). Each migrated NPC is a
-- COROUTINE: npcs.<Identifier> = function(ctx) ... end. The ctx methods below are thin `coroutine.yield`s —
-- the C# driver (Server/NpcScript.cs) awaits the matching NpcContext primitive and resumes the coroutine with
-- the reply, so a script reads as linear code even though every prompt suspends on the player's network reply:
--   local c = ctx:menu("Pick one", {"A","B"});  if c == 1 then ... end
--   local name = ctx:input("Your name?");        if name then ... end
-- Suspending ops (wait for the player): say / sayItem / sayLook / menu (returns 1-based pick, 0 = cancel) /
--   input (returns the typed string, or nil if cancelled).
-- Immediate ops (no wait): giveItem/takeItem/hasItem/countItem/itemName, awardExp/awardGold, stage/setStage,
--   reg/setReg, hasLegend/addLegend/removeLegend, warp, level/sex/nation/coins/spendGold, bubble/notify, gameDate.
-- To expose a new primitive: add a stub here AND a case in Server/NpcScript.cs Dispatch. Edit this file and run
-- !reload to see changes live -- no server restart. Any NPC with a script here takes precedence over its C#
-- ability; an NPC with no entry (or a broken file) uses the C# abilities unchanged.

function __make_ctx()
  local ctx = {}
  -- suspending (await the player's reply)
  function ctx:say(...)                 return coroutine.yield({op="say", pages={...}}) end
  function ctx:sayItem(item, ...)       return coroutine.yield({op="sayItem", item=item, pages={...}}) end
  function ctx:sayLook(look, color, ...) return coroutine.yield({op="sayLook", look=look, color=color, pages={...}}) end
  function ctx:menu(prompt, options)    return coroutine.yield({op="menu", prompt=prompt, options=options}) end
  function ctx:input(prompt)            return coroutine.yield({op="input", prompt=prompt}) end
  -- immediate
  function ctx:bubble(text)             return coroutine.yield({op="bubble", text=text}) end
  function ctx:notify(text)             return coroutine.yield({op="notify", text=text}) end
  function ctx:giveItem(key, n)         return coroutine.yield({op="giveItem", key=key, n=n}) end
  function ctx:takeItem(key, n)         return coroutine.yield({op="takeItem", key=key, n=n}) end
  function ctx:hasItem(key, n)          return coroutine.yield({op="hasItem", key=key, n=n}) end
  function ctx:countItem(key)           return coroutine.yield({op="countItem", key=key}) end
  function ctx:itemName(key)            return coroutine.yield({op="itemName", key=key}) end
  function ctx:awardExp(n)              return coroutine.yield({op="awardExp", n=n}) end
  function ctx:awardGold(n)             return coroutine.yield({op="awardGold", n=n}) end
  function ctx:stage(key)               return coroutine.yield({op="stage", key=key}) end
  function ctx:setStage(key, n)         return coroutine.yield({op="setStage", key=key, n=n}) end
  function ctx:reg(key)                 return coroutine.yield({op="reg", key=key}) end
  function ctx:setReg(key, n)           return coroutine.yield({op="setReg", key=key, n=n}) end
  function ctx:hasLegend(name)          return coroutine.yield({op="hasLegend", name=name}) end
  function ctx:addLegend(text, name, icon, color) return coroutine.yield({op="addLegend", text=text, name=name, icon=icon, color=color}) end
  function ctx:removeLegend(name)       return coroutine.yield({op="removeLegend", name=name}) end
  function ctx:warp(map, x, y)          return coroutine.yield({op="warp", map=map, x=x, y=y}) end
  function ctx:level()                  return coroutine.yield({op="level"}) end
  function ctx:sex()                    return coroutine.yield({op="sex"}) end
  function ctx:nation()                 return coroutine.yield({op="nation"}) end
  function ctx:coins()                  return coroutine.yield({op="coins"}) end
  function ctx:spendGold(n)             return coroutine.yield({op="spendGold", n=n}) end
  function ctx:gameDate()               return coroutine.yield({op="gameDate"}) end
  return ctx
end

npcs = {}

-- RTK: Koguryo (country 1) home = map 36 (7,6); otherwise Buya = map 351 (8,8).
local function warp_home(ctx)
  if ctx:nation() == 1 then ctx:warp(36, 7, 6) else ctx:warp(351, 8, 8) end
end

-- Chu Rua, the Dragon King's turtle (RTK tutorial/chu_rua.lua). Tutorial stage 7: he asks for a young_ginseng
-- (a scripted-tile pickup on Guol Tiger Pass, map 1116); bring it and he grants the aided_chu_rua legend + a
-- sea ring + experience, then warps you home. The Lost-Legend "mermaid song" branch isn't ported.
function npcs.ChuRuaNpc(ctx)
  if ctx:hasLegend("aided_chu_rua") then
    ctx:say("Thank you again for your help! I will return you home now.")
    warp_home(ctx)
    return
  end

  if ctx:hasItem("young_ginseng", 1) then
    ctx:say("Ginseng. What an odd looking root.", "The Dragon king shall live. Bless you, kind one.")
    ctx:awardExp(ctx:stage("tutorial_quest") == 7 and 600 or 400)   -- RTK: 400, +200 on the tutorial
    ctx:takeItem("young_ginseng", 1)
    ctx:giveItem("sea_ring", 1)
    ctx:addLegend("Aided Chu Rua (" .. ctx:gameDate() .. ")", "aided_chu_rua", 5, 128)
    ctx:sayItem("sea_ring", "Humbly, I offer one of the finest jewels from the sea.")
    ctx:say("Thank you again for your help! I will return you home now.")
    warp_home(ctx)
    return
  end

  ctx:say(
    "I have swum as hard as I could. Hey! hey you, honorable human. But a moment! I would that you would hear out an earnest request.",
    "The Lord, Dragon King, is dying as we speak, beneath the waves in his palace. The finest physician has come and declared that he must have an item we cannot procure from within the sea.",
    "I entreat you as a humble servant of the Dragon King, and the only servants who know of the land and the sea.",
    "Please, his highness's health depends upon a root of Young ginseng.")
  ctx:sayItem("sea_ring", "Give this to me, and this ring of the Mermaid Princess I would, in return, give to thee.")
  ctx:say(
    "I... I wish I could point you in the way of the ginseng, but I know not where it grows. There is an old verse,",
    "'Skip north, until rabbits nibbling grass you find, is a path to a king's health and harmony,'",
    "The ginseng lies north, in the Tiger Pass — mind the tiger. Please get young ginseng for his highness's sake!")
end
