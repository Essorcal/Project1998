-- Data-driven NPC dialog (the async cousin of spell_verbs.lua / item_verbs.lua). Each migrated NPC is a
-- COROUTINE: npcs.<Identifier> = function(ctx) ... end. The ctx methods below are thin `coroutine.yield`s —
-- the C# driver (Server/NpcScript.cs) awaits the matching NpcContext primitive and resumes the coroutine with
-- the reply, so a script reads as linear code even though every prompt suspends on the player's network reply:
--   local c = ctx:menu("Pick one", {"A","B"});  if c == 1 then ... end
--   local name = ctx:input("Your name?");        if name then ... end
-- Suspending ops (wait for the player): say / sayItem / sayLook / menu (returns 1-based pick, 0 = cancel) /
--   input (returns the typed string, or nil if cancelled).
-- Immediate ops (no wait): giveItem/takeItem/hasItem/countItem/itemName/learnSpell, awardExp/awardGold,
--   stage/setStage, reg/setReg, hasLegend/addLegend/removeLegend, warp, level/sex/nation/coins/spendGold,
--   killCount/mounted, eraHas, bubble/notify, gameDate.
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
  function ctx:learnSpell(key)          return coroutine.yield({op="learnSpell", key=key}) end
  function ctx:eraHas(feature)          return coroutine.yield({op="eraHas", feature=feature}) end
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
  function ctx:killCount(key)           return coroutine.yield({op="killCount", key=key}) end
  function ctx:mounted()                return coroutine.yield({op="mounted"}) end
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

-- Haguru, the tutors' missing youngest brother (RTK NPCs/arctic/haguru.lua). He stands on Du Mountain, the
-- first turning west off the Northern Pass, with a pack of mountain wolves above him. Kill 3 and he sets
-- helped_haguru, which clears TutorialQuest stage 11.
--
-- Faithful to the Lua including its ONE deliberate looseness: RTK commented out the `registry
-- ["tutorial_quest"] == 11` half of the gate and ships the kill-count check alone, so the flag can be earned
-- before the tutor ever asks for it. Kept that way — a player who cleared wolves here last week should not be
-- sent back to do it again.
--
-- ERA GATE: the QUEST is dated 2001-03-18, but Haguru is not. TSWolf's release post that day calls him
-- "the old guy named Haguru that was stranded on the mountain in early 4.0" -- he had been standing here
-- for months with nothing to give. So before that date he stays where he is and still answers, he just
-- has no wolves to send you after, and helped_haguru is never set. (His pre-quest lines are RECONSTRUCTED
-- -- no archive records what a stranded Haguru actually said -- and are written to give away nothing the
-- quest later reveals, in particular not his name, which is the quest's own payoff.)
function npcs.HaguruNpc(ctx)
  if not ctx:eraHas("tutor_du_mountain_quest") then
    ctx:say(
      "Away with you. This mountain is no place to go wandering, and I have troubles enough of my own.",
      "My road down washed out in the thaw and the wolves have the high paths. So here I sit.",
      "No, I want nothing from you. Go back the way you came, before the cold finds you too.")
    return
  end

  if ctx:killCount("mountain_wolf") >= 3 then
    ctx:setReg("helped_haguru", 1)
    ctx:say(
      "You were great! Thank you so much! Now I can save my friends up there, once I get them out of hiding.",
      "My name is Haguru, I am the brother of the tutors of the great city.",
      "Why do you look so surprised? Don't tell me he sent you to look for me! You can go back and tell him that I am fine, and not to worry about me.",
      "Oh, and thanks again for all your help here today.")
    return
  end

  ctx:say(
    "You just came from my town I see. Did you come to help me with the dark forces?",
    "Of course you are! What other reason would you have to be in such a place.",
    "Well, you can help me if you want, but it could be dangerous.",
    "A few levels up this mountain you will find a pack of wolves. They have trapped my hunting party up there.",
    "If you can kill a few of them then I can start saving the people. Be very careful, they do a lot of damage to you.")
end

-- =====================================================================================================
-- THE NEWBIE TUTORIAL AREA (maps 4711 Welcome -> 4712 Open Field -> 4713 Forest Path -> 4714 Deep Forest
-- -> 4715 Country Farm + 4716 Mignok's Home -> 4717 City Limits -> 4718 Angel's Blessing).
--
-- Added ~2000-10-06 and era-gated on `newbie_tutorial_area`; before that date the tutor taught these
-- beats himself (Server/NoviceQuest.cs, retired the same day) and new characters spawned at his feet.
-- The tutor is NEVER gated -- all three eras end up at him, the area just changes what he still has to
-- teach. See Server/Era.cs and docs/Era-Gating.md.
--
-- The dialog below is TRANSCRIBED, not reconstructed: every line is read off the screenshots archived on
-- tswolf.com/newb/quest.shtml, quest2.shtml and quiz.shtml (Wayback 2001-06-27 / 2001-07-23), which
-- captured each dialog page as a GIF. Where a page failed to archive the gap is marked inline rather
-- than papered over. This supersedes NoviceQuest.cs's rewritten one-tutor wording, which existed only
-- because the area itself wasn't implemented -- here the four speakers really are four NPCs in four
-- rooms, so their "continue down this path" framing is literally true again.
--
-- One counter drives the whole area; each NPC owns a slice and nudges you back if you're behind it.
local NEWB = "newbie_area_quest"
local NEWB_RABBITS, NEWB_SQUIRRELS = 10, 10       -- tswolf: "Slay ten of them", "kill ten Squirrels"
local NEWB_STAGE_EXP = 50                          -- junior to every TutorialQuest stage, as NoviceQuest is
local NEWB_MIGNOK_TOLD = "newbie_mignok_told"      -- set once Mignok has named his price (see MignokNpc)

-- Kills are compared against a snapshot taken when the task is GIVEN, so a player who wandered off and
-- cleared the field before being asked doesn't skip the quest (same pattern as NoviceQuest/MinorQuest).
--
-- The count is never shown back to the player. None of the archived dialog does that -- the NPC just
-- restates the task -- and a running "3 of 10" tally reads as a modern quest-log UI the 4.95 client has
-- no equivalent of.
local function newb_killed(ctx, mob, snapkey)
  return ctx:killCount(mob) - ctx:reg(snapkey)
end

-- Quest 1 -- the traveling weapon smith, 4712 Open Field. Gives the Wooden saber and the 10 rabbits.
function npcs.WoodlandSmithNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage == 0 then
    ctx:say(
      "Welcome young one. What are you doing so far from town? 'Tis a good thing that this area is relatively safe to travel.",
      "Within this kingdom lay many a beast that would kill you with nothing but a glance. In these fields you will find little that would kill you fast, but that is not to say they can't kill you.")
    ctx:giveItem("wooden_saber", 1)
    ctx:sayItem("wooden_saber",
      "I am a traveling weapon smith. I can teach you the basics of combat, and weapons. Here is a Wooden saber, I carve them from the branches I find in the woods nearby.")
    ctx:say(
      "To see what you're carrying press 'i'. To use a weapon or item in your inventory press 'u', then the letter of the weapon.",
      "To attack using your saber press the <space> bar. Wield your new Wooden saber now, then look for rabbits around here. Slay ten of them and return to me.")
    ctx:setReg("newbie_rabbit_snapshot", ctx:killCount("rabbit"))
    ctx:setStage(NEWB, 1)
    return
  end

  if stage == 1 then
    if newb_killed(ctx, "rabbit", "newbie_rabbit_snapshot") < NEWB_RABBITS then
      ctx:say("The rabbits are still out there, young one. Slay ten of them and return to me.")
      return
    end
    ctx:awardExp(NEWB_STAGE_EXP)
    ctx:setStage(NEWB, 2)
    ctx:say(
      "Congratulations! Your first hunt is a success! When you get to the town make sure to seek out a blacksmith to keep your weapons in top condition, as you use them they can grow dull and may break.",
      "Now continue down this path, and catch up with my brother who I am sure will tell you a little of his trade - Armor!")
    return
  end

  ctx:say("Continue down this path, and catch up with my brother who I am sure will tell you a little of his trade - Armor!")
end

-- Quest 2 -- the smith's brother the armorer, 4713 Forest Path. Gives the Spring garb and the 10 squirrels.
function npcs.WoodlandArmorerNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 2 then
    ctx:say("Back down the path there is my brother, a weapon smith. Speak with him before you come to me — you'll want something to swing before you want something to wear.")
    return
  end

  if stage == 2 then
    ctx:say("Whoa there mighty fighter. Where are you off to in such a hurry? I see you have already armed yourself well for a hunt, but your still in your rags.")
    ctx:giveItem("spring_garb", 1)
    ctx:sayItem("spring_garb",
      "Take this, like all armor in this kingdom it is tailored to fit your gender. It is Spring in quality, and represents the spring of your adventure.")
    ctx:say(
      "As with weapons, you can press 'i' to see it in your inventory. Then type 'u' and the letter next to the armor to wear it.",
      "Armor helps protect you from attacks, and reduces the damage you take. If you look to the bottom right of your screen you will see a red bar, that is your vitality, your health.",
      "Watch it carefully as you hunt, for should it fall to low your body will become but a spirit, and some mana can drop to the floor or even break from the extra damage of death!",
      "This part of the woods is filled with Squirrels. Unlike the rabbits from before these have far sharper teeth. Your new armor will help protect you from their bite.",
      "Go now, kill ten Squirrels. As you kill them you will see that they drop Acorns. Stand above the acorns and press ',' to pick them up.")
    ctx:setReg("newbie_squirrel_snapshot", ctx:killCount("squirrel"))
    ctx:setStage(NEWB, 3)
    return
  end

  if stage == 3 then
    if newb_killed(ctx, "squirrel", "newbie_squirrel_snapshot") < NEWB_SQUIRRELS then
      ctx:say("Ten Squirrels, and mind their teeth.")
      return
    end
    ctx:awardExp(NEWB_STAGE_EXP)
    ctx:setStage(NEWB, 4)
    ctx:say(
      "Well, that was fast! You are well on your way to being a truly mighty fighter. Remember to keep your Armor well maintained like your weapons.",
      "As you grow stronger, and gain more insight, you will be able to use better armor and weapons. These can be gained from creatures you kill or bought from players and shops.",
      "Continue now on your journey, and I hope you fair well on your adventure.")
    return
  end

  ctx:say("Continue now on your journey, and I hope you fair well on your adventure.")
end

-- Quest 3 -- the coordinate lesson, 4714 Deep Forest. He explains the coordinate readout and sends you to
-- walk to 21,20, where the map's own warp (Warps.csv 4714 (21,20) -> 4715 (3,2)) carries you on.
--
-- GAP: tswolf's quest3congrats/quest3congrats2 ("Congratulations! You have learned to find your way
-- around. If you look at the bottom right of your screen, the numbers will now read 0021 0020." / "Using
-- this system, and the information in the help ((Press F1)) under 'Finding a place', you can find your
-- way around the cities and towns.") fired ON the 21,20 tile, not from an NPC -- the guide says "a text
-- box will pop up". That needs the scripted-tile trigger system, so those two pages are deliberately NOT
-- spoken by anyone here rather than being relocated onto a speaker who couldn't have said them.
function npcs.TutorialNpc1(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 4 then
    ctx:say("Hello there young one. You seem like your in a hurry. But where are you going to in such a rush? Do you even know?",
            "Go back along the path and finish what my brothers were teaching you. They will not keep you long.")
    return
  end

  if stage == 4 then
    ctx:say(
      "Hello there young one. You seem like your in a hurry. But where are you going to in such a rush? Do you even know?",
      "Look to the bottom right of your screen. Those two numbers are where you stand — the first across, the second down. Every place in the kingdoms is named that way.",
      "Learn to read them and you need never be lost again. Walk from here to 0021 0020, and I will know you have understood.")
    ctx:awardExp(NEWB_STAGE_EXP)
    ctx:setStage(NEWB, 5)
    return
  end

  ctx:say("Walk to 0021 0020, and the way onward will open for you.")
end

-- Quest 4 -- the magic teacher, 4715 Country Farm. Points you at his sister Mignok in the hut to the
-- north east (Warps.csv 4715 (17,4) -> 4716), then explains the spell list when you come back.
function npcs.TutorialNpc2(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 5 then
    ctx:say("Peace, young one. There are others along the path behind you with more to teach before I can be of use.")
    return
  end

  if stage == 5 then
    ctx:say(
      "Welcome child. You seem to be learning much, I can sense your mind expanding in leaps and bounds. But are you ready for the greatest test of mental power?",
      "Magic, and its mastery, is the greatest challenge of the mind. Are you ready to face the challenge? Depending on which path you follow later in your life ((level 5)) you will learn different secrets.",
      "In the hut beside me is my sister, a novice magic teacher. If you go in I am sure she will teach you a secret. She may ask for items in exchange for teaching you.",
      "Return to me after she has taught you the spell.")
    ctx:setStage(NEWB, 6)
    return
  end

  if stage == 6 then
    ctx:say("Return to me after she has taught you the spell. She is in the hut beside me.")
    return
  end

  if stage == 7 then
    ctx:awardExp(NEWB_STAGE_EXP)
    ctx:setStage(NEWB, 8)
    ctx:say(
      "Ah, you look eager to use your new spell. To see the list of spells you have press the '+' key on the keypad on the right of your keyboard.",
      "When you click on your Path's tutor, they will have a button that says 'Learn Secret' and it is from there that you will be able to see what spells can be learned.",
      "Try your spells now, and when you're ready you should continue with your travels.")
    return
  end

  ctx:say("Try your spells now, and when you're ready you should continue with your travels.")
end

-- Mignok, 4716 Mignok's Home -- teaches Soothe for 5 acorns and 5 rabbit meats.
function npcs.MignokNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 6 then
    ctx:say("Greetings there young one. My brother stands outside — speak with him first, and he will send you to me when you are ready.")
    return
  end

  if stage > 6 then
    ctx:say("Go well, and use the spell kindly.")
    return
  end

  -- Naming the price IS the quest, so it has to be said before it can be met: a player who wandered in
  -- already carrying 5 acorns and 5 rabbit meats (both drop in the rooms before this one) used to skip
  -- straight to the reward and never hear a word of it. Told once, then hand them over on the next click.
  if ctx:reg(NEWB_MIGNOK_TOLD) ~= 0 and ctx:hasItem("acorn", 5) and ctx:hasItem("rabbit_meat", 5) then
    if not ctx:learnSpell("soothe") then
      ctx:say("Your mind cannot hold any more secrets right now.")
      return
    end
    ctx:takeItem("acorn", 5)
    ctx:takeItem("rabbit_meat", 5)
    ctx:awardExp(NEWB_STAGE_EXP)
    ctx:setStage(NEWB, 7)
    ctx:say("Thank you for the items! Now here is your spell, Soothe. Go back and speak with my brother outside as to how to use it.")
    return
  end

  ctx:setReg(NEWB_MIGNOK_TOLD, 1)
  ctx:say(
    "Greetings there young one. So you wish to learn the ways of magic do you? All classes learn various spells. Some are common to all, while others are unique to the path they choose.",
    "Every spell will usually have a price associated with it. For instance, I will be happy to teach you the spell \"Soothe\" - a healing spell - but you will have to bring me 5 acorns and 5 rabbit meats.")
end

-- Quest 5 -- the Woodland Guard, 4717 City Limits. A short lecture on the law, then a yes/no quiz; pass it
-- and he hands over the Novice sword and lets you through.
--
-- The sword is HIS, not the Angel's: quizcongrats2 is the guard saying "here's a sword that will hit a
-- little harder than that stick your carrying" (the wooden saber). quiz.shtml's closing line "She gives
-- you a Novice Sword" contradicts its own screenshots, so the screenshots win.
--
-- All FIVE questions, in the archive's own quiz1..quiz5 order. Per question the archive may hold: quizN
-- (the question), quizNa (the two options, correct one highlighted yellow), quizNb (the guard's reply to a
-- correct answer). quiz1, quiz1a, quiz2, quiz2b and quiz4b have ZERO snapshots in the Wayback CDX index for
-- newb/questpics/ (which lists 50 files) and 404 at every timestamp -- permanently lost, not un-fetched.
--
-- Correct answers were read by MEASURING the highlight colour in quizNa.gif (rgb 239,223,143 vs the
-- 16,16,16 of the unselected row), not by eye: No, No, No, YES, No.
--
-- Each line below is tagged with where it came from: a bare quizN reference means it is transcribed off that
-- screenshot word for word, and anything marked RECONSTRUCTED is ours, written to fill a hole the archive
-- can no longer fill, and deletable without breaking the quiz.
local NEWB_QUIZ = {
  -- Q1 -- question RECONSTRUCTED. quiz1/quiz1a are gone, but quiz1b (the reply) survives word for word and
  -- fixes the subject and the answer beyond doubt, and the guard's own lecture already lists "no profanity".
  { q = "Are you allowed to use bad language in Nexus?",             answer = 2,   -- No. (RECONSTRUCTED)
    reply = "Correct! Bad language is not allowed in Nexus, and will land you in jail if used." },  -- quiz1b
  -- Q2 -- question and reply from brian; the archive kept only quiz2a, whose highlight is the second option,
  -- which agrees with the answer being No. Phrased to match the attested siblings' cadence.
  { q = "Are you allowed to harass other players?",                  answer = 2,   -- No. (quiz2a)
    reply = "Correct! Respect other players and treat them in the same manner you would like to be treated." },
  { q = "Are you allowed to steal other players items?",             answer = 2,   -- No.  quiz3/quiz3a
    reply = "Correct! Treat other people's items in the same way you would like them to respect yours." },  -- quiz3b
  -- Q4 -- reply RECONSTRUCTED (quiz4b is gone); the question and the Yes are both transcribed. Every other
  -- question answers back, so without this one the quiz silently advances mid-run.
  { q = "Is there a justice system within Nexus?",                   answer = 1,   -- Yes. quiz4/quiz4a
    reply = "Correct! Judges and justice officials uphold the law here, and they will act on it." },  -- RECONSTRUCTED
  { q = "Can you use new, or secret, characters to commit crimes?",  answer = 2,   -- No.  quiz5/quiz5a
    reply = "Correct! People using a new character to hide their true identity can be traced." },
}

function npcs.WoodlandGuardNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 8 then
    ctx:say("Who goes there? Show yourself for identification! Ahh.. you are not the person I am looking for. Sorry for the abrupt greeting, I am a city guard.",
            "You are not finished with your training yet. Go back and see it done, then I will let you by.")
    return
  end

  if stage > 8 then
    ctx:say("On your way, and keep on the right side of the law.")
    -- Edge case: passed the quiz but wound up back on this side of the gate anyway (relog, death, a warp
    -- that didn't take). He already agreed to let them through, so he does it again rather than leaving
    -- them stranded -- the gate is scenery and this is the only way in. Stage 9 only: once the Angel has
    -- finished with them (10) they belong in their home city, not back in there.
    if stage == 9 then ctx:warp(4718, 9, 18) end
    return
  end

  -- quest501, [quest502 -- from brian; the one page of the lecture the capture missed, and it lands exactly
  -- between "I am a city guard" and "If your caught"], quest503..quest506. The misspellings in the
  -- transcribed pages ("If your caught", "more sever punishments") are the original's, not typos -- leave them.
  ctx:say(
    "Who goes there? Show yourself for identification! Ahh.. you are not the person I am looking for. Sorry for the abrupt greeting, I am a city guard.",
    "I work for the justice system of Nexus. In our lands we have many rules that people must follow. Breaking the laws here can have severe consequences.",
    "If your caught, and convicted of a crime Judges and justice officials have the ability to jail your character, and if you continue there is more sever punishments!",
    "There are simple rules here, such as no profanity, no stealing, and no harassment. Most of this information is available on the 'Law' boards.",
    "All boards contain information that is important. Be sure to look over 'Law', and 'Guide', and read 'Dream Weaver' regularly as it keeps you updated on important information and events.",
    "Before I let you continue, I want to make sure you have a basic understanding of the laws.")

  for _, item in ipairs(NEWB_QUIZ) do
    local pick = ctx:menu(item.q, {"Yes.", "No."})
    if pick ~= item.answer then
      -- OURS: the archive doesn't record the failure branch. Re-asking from the top is the gentlest
      -- reading of a gate whose whole purpose is to make sure the answer is known.
      ctx:say("That is not the law here. Read the 'Law' board, and speak to me again when you are sure.")
      return
    end
    if item.reply then ctx:say(item.reply) end
  end

  ctx:giveItem("novice_sword", 1)
  ctx:awardExp(NEWB_STAGE_EXP)
  ctx:setStage(NEWB, 9)
  ctx:say("You have done well. I can now permit you to enter the city, go on your way, and keep on the right side of the law.")
  ctx:sayItem("novice_sword",
    "To help you defend against the lawless and some of those tougher critters, here's a sword that will hit a little harder than that stick your carrying.")
  -- He says he's letting you through, so he does. The gate itself (4717 17..20,18) is scenery in a solid
  -- wall band and stays shut -- passing it is HIS act, not a tile you walk over, which is also the only
  -- reason 4718 is reachable at all (there is no walk-in warp into Angel's Blessing).
  ctx:warp(4718, 9, 18)
end

-- Quest 6 -- the Woodland Angel, 4718 Angel's Blessing. Closes the area and hands the player to the town
-- tutor, which is the seam the whole era model turns on: the area never replaced the tutor, it fed him.
-- Saying "Finish" is what ends it (npcs_say.WoodlandAngelNpc below).
--
-- GAP: lastq02 and lastq08 did not survive the Wayback capture. lastq08 sat between the "talk out loud"
-- page and the farewell, so it was almost certainly the whisper key -- but "almost certainly" is not a
-- transcript, so it is left out.
function npcs.WoodlandAngelNpc(ctx)
  local stage = ctx:stage(NEWB)

  if stage < 9 then
    ctx:say("Not yet, child. The guard at the city limits has not finished with you.")
    return
  end

  if stage > 9 then
    ctx:say("Good luck, I now leave you in the hands of the tutor from the town you have picked as home. Farewell.")
    return
  end

  ctx:say(
    "There you are at last. This marks the end of your training. The great tutors of the cities will now continue your training.",
    "You can get more help from players, and also the built in help system. To access the help press 'F1' and look under the topic that applies to you.",
    "Before you invest too much time into your character, make sure that you have selected a good name for the setting of Ancient Korea.",
    "Some groups, such as Clans and Subpaths will not accept people with names that reflect modern life or current trends.",
    "This may harm your chances of joining important parts of the Nexus community and block you from a full and diverse adventure in Nexus.",
    "The last thing I have to teach you is how to talk, and whisper to people. Pressing ' allows you to talk out loud, to all the people around you.",
    "When you are ready to leave, say \"Finish\".")
end

-- Speech-trigger handlers: npcs_say.<Identifier> = function(ctx, speech). `speech` is already lowercased +
-- trimmed. Return true to CONSUME the speech (stops dispatch); return false/nothing to let other NPCs or the
-- C# say-handlers try it. Same ctx/coroutine model as the click handlers above.
npcs_say = {}

-- Saying "Finish" to the Woodland Angel ends the newbie area and delivers the player to their home city's
-- tutor -- the hand-off the tutor chain then picks up (TutorialQuest stage 0).
function npcs_say.WoodlandAngelNpc(ctx, speech)
  if speech ~= "finish" then return false end
  if ctx:stage(NEWB) < 9 then
    ctx:say("Not yet, child. The guard at the city limits has not finished with you.")
    return true
  end
  ctx:awardExp(NEWB_STAGE_EXP)
  ctx:setStage(NEWB, 10)
  ctx:say("Good luck, I now leave you in the hands of the tutor from the town you have picked as home. Farewell.")
  warp_home(ctx)
  return true
end

-- The talking rabbit of Guol Valley (chu_rua_rabbit.lua) — hints at the ginseng quest.
function npcs_say.ChuRuaRabbitNpc(ctx, speech)
  if speech == "hello" then
    ctx:say("Hmmm..", "What is it you want?")
    return true
  elseif speech == "tiger" then
    ctx:bubble("Fool was I to go north for ginseng. He almost ate me!")
    return true
  elseif speech == "ginseng" then
    ctx:say(
      "What a bitter root! It's as bad tasting as the mountains in which it grows.",
      "Some trickster cousin told me I should go up the left path and have some of the delicious root.",
      "Fool was I to go into the awful mountains. I followed this stream up to those horrid mountain's foot, and hopped up a dangerous path.")
    return true
  end
  return false
end

-- The Ancient dolmen of Guol Divide (chu_rua_rock.lua) — say "hello" for the tiger hint.
function npcs_say.ChuRuaRockNpc(ctx, speech)
  if speech ~= "hello" then return false end
  ctx:say(
    "O, it must be good to have feet.",
    "You've been to the sea I'll bet from the smell of you.",
    "That is where I have lived for so long until now; by the sea.",
    "Thank you for spending a moment with this old soul. Be careful of the tiger to the north.",
    "He only thinks of food, though you might distract him if you allude to one of the rabbits that tricked him")
  return true
end

-- The tiger guarding the ginseng (chu_rua_tiger.lua). Say "rabbit", pick Forest, and he leaves (sets the
-- chu_rua_tiger_gone flag so TryGinseng lets you take the root on map 1116).
function npcs_say.ChuRuaTigerNpc(ctx, speech)
  if speech == "hello" then
    ctx:bubble("Hello, Dinner!")
    return true
  elseif speech == "ginseng" then
    ctx:bubble("I'd rather eat you!")
    return true
  elseif speech == "rabbit" then
    ctx:say("What? Rabbit? Was it that foul hopping furre that trapped me in a pit?")
    local choice = ctx:menu("I'd love to rend his neck. Where did you see him?",
      {"Warrior's Guild", "Forest", "Town", "Mage's Guild"})
    if choice == 2 then   -- Forest = correct
      ctx:say("Mmm. Well then I guess I'll return him a favor with grinning teeth.")
      ctx:notify("The tiger leaves to the south.")
      ctx:setReg("chu_rua_tiger_gone", 1)
    elseif choice == 4 then
      ctx:say("What, did someone pull him out of a hat?",
              "So far? Oh well, I guess I'll have a snack beforehand... and you look tasty!")
    else
      ctx:say("So far? Oh well, I guess I'll have a snack beforehand... and you look tasty!")
    end
    return true
  end
  return false
end

-- Blood, the Sonhi trickster in Blood's Home off KaMing's Encampment (RTK NPCs/kaming/blood.lua, the
-- `onSayClick` "ice beast" branch). TutorialQuest stage 12 wants his Frost sabre.
--
-- The flow, verbatim from the Lua: say "ice beast" -> he pitches the sabre and asks 100 gold up front
-- (paid_gold_for_frost_sabre) -> you kill the Ice Beast in Northeast Koguryo, off Dae Shore, for its Ice
-- heart -> bring it back and he forges the sabre, pays 2,300 exp and grants the defeated_ice_beast legend.
-- Level 7 minimum, as RTK has it.
--
-- Only the "ice beast" branch is ported. His shop, spell-teaching and the whole blood-oath ritual are a
-- different system (and the oath belongs with marriage, which lives in C#); the "seal" branch belongs to the
-- wind-armor chain, which isn't here. An unported keyword just falls through to `return false`, so he stays
-- silent about them rather than half-answering.
function npcs_say.BloodNpc(ctx, speech)
  if speech ~= "ice beast" then return false end

  if ctx:level() < 7 then return true end   -- RTK returns silently rather than explaining

  if ctx:hasLegend("defeated_ice_beast") then
    ctx:say("\"I hope you have found your Frost sabre useful!\"")
    return true
  end

  if ctx:reg("paid_gold_for_frost_sabre") == 1 then
    if ctx:hasItem("ice_heart", 1) then
      ctx:say("The Sonhi looks surprised. \"I do not know how one as wimpy as you could defeat an Ice beast, but somehow you have triumphed.\"",
              "\"As promised, I will forge you a Frost sabre.\"")
      ctx:takeItem("ice_heart", 1)
      ctx:giveItem("frost_sabre", 1)
      ctx:awardExp(2300)
      ctx:addLegend("Defeated the Ice beast (" .. ctx:gameDate() .. ")", "defeated_ice_beast", 5, 128)
      ctx:setReg("paid_gold_for_frost_sabre", 0)
      ctx:sayItem("frost_sabre", "\"Wield it well.\"")
      return true
    end

    ctx:say("The Sonhi seems to be suppressing laughter. \"Bring me an Ice heart and I will make you a Frost sabre.\"")
    return true
  end

  ctx:say("The Sonhi grins. \"Ice Beast, eh? You must be after a Frost sabre.\"",
          "\"You have heard of the Frost sabre, haven't you? No? Then let me tell you what you are missing.\"")
  ctx:sayItem("frost_sabre",
          "\"Though only a modest weapon, the Frost sabre has many great powers.\"",
          "\"When you die, it will not leave your side. When it is worn, it can be repaired with ease. In combat, sometimes it chills your foe, making them easier to hit.\"",
          "\"Perhaps most impressive is that only YOU will be able to wield your Frost sabre if it is crafted for you.\nUnfortunately, very, very few know how to make one.\"")
  ctx:say("\"I know what you are thinking. Yes, I know how to craft a Frost sabre. I can see in your eyes that you are eager for one aren't you?\"")

  local choice = ctx:menu("\"For a mere 100 gold, I will forge you one if you bring me the item needed to make one. Will you pay?\"",
    {"Yes, I want a Frost sabre.", "No, I'll keep my money."})

  if choice == 1 then
    if not ctx:spendGold(100) then
      ctx:say("Come back when you have more gold.")
      return true
    end
    ctx:setReg("paid_gold_for_frost_sabre", 1)
    ctx:say("\"To forge it, I will need the Ice heart of a mighty and wicked Ice Beast.\"",
            "\"Where do you find an Ice Beast? I wouldn't know. We Sonhi aren't from this area. Perhaps one who has dwelt here a long time would know.\"",
            "As you are leaving, you hear the Sonhi captain chuckle to himself, \"Even if the fool finds an Ice beast, they'll surely die. An easy 100 gold, heh, heh.\"")
  elseif choice == 2 then
    ctx:say("\"As you wish... I suppose I will make a Frost sabre for another then.\"")
  end
  return true
end
