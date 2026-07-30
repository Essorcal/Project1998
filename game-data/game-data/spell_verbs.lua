-- Spell verbs: the LOGIC half of the data-driven spell system (the "verb"; SpellParams.csv is the "row").
-- Each verb is a function(ctx, row):
--   ctx = the SpellContext facade -> engine primitives (ctx:spendMana / ctx:damage / ctx:heal / ...) plus
--         read-only caster stats (ctx.will, ctx.level, ctx.might, ctx.hp, ctx.hasTarget, ...).
--   row = this spell's SpellParams.csv row, numbers already parsed (row.coeff, row.mana, row.amount, ...);
--         empty CSV cells are nil, so `row.base or 0` gives a safe default.
-- Bind a verb to a spell by adding a SpellParams.csv row whose `verb` column names it.
-- Edit THIS file or the CSV and run !reload to see changes live -- no server restart.

verbs = {}

-- =========================================================================================================
-- ARCHETYPE verbs (arch_*): the DEFAULT behaviour for a whole spell archetype, run for every spell of that
-- archetype (see Session.ApplyCast). The engine has already evaluated the spell's real formula and mana cost
-- from spell_effects.csv, so these read ctx.amount / ctx.mana rather than per-spell row params. A missing
-- arch_* verb makes that archetype fall back to its C# handler, so migration is one archetype at a time.
-- =========================================================================================================

-- Damage archetype: the standard direct magic attack. ctx:magicDamage does the whole faithful sequence
-- (mana check -> resolve target -> deflect roll, no mana on a deflect -> spend mana -> apply + XP). Returns
-- false if the cast couldn't happen (no mana / no target) so the cast animation is suppressed, matching C#.
function verbs.arch_damage(ctx, row)
  return ctx:magicDamage(ctx.amount, ctx.mana)
end

-- Heal archetype: restore the caster's own HP by the engine-evaluated amount (spell_effects.csv formula).
-- Mana is checked+spent first (return false on short mana so the cast animation is suppressed, matching C#);
-- ctx:heal caps at max HP and plays the sparkle/message.
function verbs.arch_heal(ctx, row)
  if not ctx:spendMana(ctx.mana) then return false end
  ctx:heal(ctx.amount)
  return true
end

-- Split a '|'-separated CSV field into a list ("might|hit" -> {"might","hit"}; "" -> {}).
local function split_bar(s)
  local out = {}
  if s == nil or s == "" then return out end
  for part in string.gmatch(s, "([^|]+)") do out[#out + 1] = part end
  return out
end

-- Buff archetype: a timed SELF buff (the caster is the target). Spend mana, refresh (don't stack), then apply
-- each stat|amount pair from the export row for the spell's duration, play the aura fx once, and show the live
-- target-flavor line to yourself (e.g. Might -> "Your muscles develop." then the central "You cast Might.").
function verbs.arch_buff(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:clearBuff()
  local dur = ctx.durationMs > 0 and ctx.durationMs or 60000
  local stats, amts = split_bar(ctx.buffStat), split_bar(ctx.buffAmt)
  for i = 1, #stats do
    local amt = math.floor(tonumber(amts[i]) or 0)      -- "3" or "3.0" -> 3; missing/0 -> skipped by addBuff
    if amt ~= 0 then ctx:addBuff(stats[i], amt, dur) end
  end
  ctx:fxSelf()
  ctx:flavorSelf()
  return true
end

-- TargetBuff archetype: a beneficial timed buff cast ON a target (another player, yourself, or a mob/NPC/pet).
-- Mana is CHECKED up front but DEBITED only once the cast commits, so a no-target abort spends nothing. The
-- verb owns the routing: deduction (a damage-reduction multiplier) is player-only; a plain stat buff applies to
-- a player (with their flavor line) or a mob. buffAmt is a fraction for deduction, an integer otherwise.
function verbs.arch_targetbuff(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  if ctx.targetKind == "none" then ctx:say(ctx.spellName .. " finds no target."); return false end
  local dur = ctx.durationMs > 0 and ctx.durationMs or 300000
  if ctx.buffStat == "deduction" then
    if ctx.targetKind ~= "player" then ctx:say(ctx.spellName .. " has no effect on that."); return false end
    ctx:debitMana(ctx.mana)
    ctx:deductionTarget(tonumber(ctx.buffAmt) or 1.0, dur)
    return true
  end
  ctx:debitMana(ctx.mana)
  ctx:buffTarget(ctx.buffStat, math.floor(tonumber(ctx.buffAmt) or 0), dur)
  return true
end

-- Debuff archetype: a hostile crowd-control freeze. Check mana, require a target, roll the magic-deflect (no
-- mana on a deflect), debit, roll the take-hold chance, then freeze the mob for the duration.
function verbs.arch_debuff(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  if not ctx.hasTarget then ctx:say(ctx.spellName .. " finds no target."); return false end
  if ctx:deflected() then ctx:say("The magic has been deflected."); return true end
  ctx:debitMana(ctx.mana)
  local ch = ctx.chance
  if ch < 100 and not ctx:roll(ch) then ctx:say(ctx.spellName .. " fails to take hold."); return true end
  ctx:freezeTarget(ctx.durationMs > 0 and ctx.durationMs or 20000)
  return true
end

-- Cure archetype: RTK removes a whole CATEGORY of durations from the target (the cure's CureCat, e.g. a
-- 'curses' cure like Atone clears pestilence; a 'venoms' cure clears poisons). Now that curses are real
-- categorized statuses (see the `curse` verb), this actually dispels them from the caster, not just spends mana.
function verbs.arch_cure(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:cureCategory(ctx.cureCat)
  return true
end

-- =========================================================================================================
-- PER-SPELL verbs: bound to one spell by a SpellParams.csv row whose `verb` column names it (these take
-- precedence over the archetype path). Their numbers come from the row (row.coeff, row.base, ...).
-- =========================================================================================================

-- Direct magic damage to the current target: base + Will*coeff, costing `mana`.
-- (Target resolution, deflect, the HP-bar packet, death + XP are all handled inside ctx:damage.)
function verbs.magic_damage(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return false end   -- declined (no mana) -> no "You cast X."
  ctx:damage((row.base or 0) + ctx.will * (row.coeff or 1.0))
  return true
end

-- =========================================================================================================
-- Baekho's Cunning (RTK Spells/Subpaths/Baekho/baekhos_cunning.lua) — the showcase for COMPOSED verbs: a
-- single spell that is simultaneously a rage MULTIPLIER, a damage-REDUCTION (deduction), and positional
-- STANCE grants, plus a stateful tier machine. It fits NO single archetype; it just composes primitives.
-- Recast to climb Cunning 1->6, each tier stronger and far pricier; each cast supersedes the lesser furies
-- (ctx:rage overwrites the single rage slot). The tier TABLE is an RTK constant so it lives here in the verb.
-- CLASSIC (4.95-era) Cunning chart, from the Rogue Tutor Melalye "Baekho's Cunning" board post in the scraped
-- archive (canonical, > RTK per user). Per tier: mana, duration (DECREASES each tier), damage multiplier (rage),
-- deduction (incoming-damage mult), and cumulative special grants (Cun2 = Backstab, Cun3 adds Flank). ded goes
-- 0/15/30/55/85% reduction -> mult 1.00/0.85/0.70/0.45/0.15; at Cun4-5 this EXCEEDS Sanctuary's 50%, so casting
-- Sanctuary DOWNGRADES a high-Cunning rogue (Sanctuary overrides Cunning's slot; see ApplyCunningDeduction). The
-- 6th cunning (~262k mana) is "no better than the 5th" in the archive, so 5 is the real cap.
local CUNNING = {
  [1] = { mana = 3000,   dur = 938000, rage = 4, ded = 1.00, back = false, flank = false },  --  0% reduction
  [2] = { mana = 4200,   dur = 788000, rage = 5, ded = 0.85, back = true,  flank = false },  -- 15%, +Backstab
  [3] = { mana = 15634,  dur = 638000, rage = 6, ded = 0.70, back = true,  flank = true  },  -- 30%, +Flank
  [4] = { mana = 46658,  dur = 488000, rage = 7, ded = 0.45, back = true,  flank = true  },  -- 55%
  [5] = { mana = 117667, dur = 338000, rage = 8, ded = 0.15, back = true,  flank = true  },  -- 85% (max)
}
local CUNNING_MAX    = 5
local CUNNING_AETHER = 150000   -- ~2.5 min cooldown between tier-ups (RTK/archive setAether)

function verbs.baekhos_cunning(ctx, row)
  if ctx:onCooldown("baekhos_cunning") then
    ctx:say("Baekho's power is not yet ready to grow.")
    return false
  end
  -- Only continue the ladder if the stance is still active; if it lapsed, hasDuration is false -> start fresh.
  local active = ctx:hasDuration("baekhos_cunning")
  local tier = active and ctx:reg("baekhos_cunning") or 0
  if tier < 0 or tier > CUNNING_MAX then tier = 0 end
  if active and tier >= CUNNING_MAX then
    ctx:say("You have reached your max potential.")
    return false
  end

  local nt = tier + 1
  local c = CUNNING[nt]
  if not ctx:spendMana(c.mana) then return false end        -- huge, tier-scaled cost

  ctx:setReg("baekhos_cunning", nt)
  ctx:setDuration("baekhos_cunning", c.dur)                  -- per-tier window (shrinks as the tier climbs)
  ctx:rage(c.rage, c.dur)                                    -- whole-swing xN (supersedes lesser furies)
  ctx:deduction(c.ded, c.dur)                                -- take less damage (own slot; Sanc overrides it)
  ctx:stance("backstab", c.back, c.dur)                      -- free positional crits
  ctx:stance("flank",    c.flank, c.dur)
  ctx:setCooldown("baekhos_cunning", CUNNING_AETHER)
  ctx:fx(35, 705)                                            -- RTK sendAnimation(35) / playSound(705)
  ctx:say("[Cunning "..nt.."] Baekho sharpens your instincts (x"..c.rage.." damage).")
  return true
end

-- Restore the caster's own HP: flat `amount` plus optional Will scaling (`willcoeff`), costing `mana`.
function verbs.heal(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return false end   -- declined (no mana) -> no "You cast X."
  ctx:heal((row.amount or 0) + ctx.will * (row.willcoeff or 0))
  return true
end

-- Restore the caster's own mana by a flat `amount` (no mana cost, obviously).
function verbs.restore_mana(ctx, row)
  ctx:restoreMana(row.amount or 0)
  return true
end

-- Timed self-buff: spend `mana`, then raise `stat` by `amount` for `duration` ms (default 60s). `stat` is one
-- of the Totals() keys: might/will/grace/hp/mp/armor/hit/dam. Re-casting the same spell refreshes, not stacks.
function verbs.buff(ctx, row)
  if not ctx:spendMana(row.mana or 0) then return false end   -- declined (no mana) -> no "You cast X."
  ctx:buff(row.stat, row.amount or 0, row.duration or 60000)
  return true
end

-- =========================================================================================================
-- TIER-2 COMBAT STANCES (rage / enchant / stealth / backstab / flank): each just ARMS a timed melee modifier
-- on the caster, so they share the CastArch-style dispatch (Session.CastStanceArch): the C# classifier
-- (Content.RageAmountFor / EnchantFor / IsStealthSpell / fx.CureCat) still identifies the spell and pre-computes
-- the RTK numbers into ctx.amount / ctx.mana / ctx.durationMs; the LOGIC (guard, debit, arm, fx) lives here.
-- No SpellParams row needed — these run for every spell the classifier recognises, falling back to the C#
-- CastRage / CastEnchant / CastStealth / CastStance handler if the verb is absent (migrate-one-at-a-time).
-- =========================================================================================================

-- Rage tier: a whole-swing damage MULTIPLIER (Wolf's/Tiger's/Dragon's Fury, Baekho's Rage). RTK blocks casting
-- ANY fury while one is already active — you wait it out, no overwrite to a stronger tier. amount = the tier's xN.
function verbs.stance_rage(ctx, row)
  if ctx.rageActive then ctx:say("You are already benefiting from a fury."); return false end
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:rage(math.floor(ctx.amount), ctx.durationMs > 0 and ctx.durationMs or 60000)
  ctx:fxSelf()
  return true
end

-- Enchant tier: multiplies ONLY the raw weapon-swing term (not the whole swing). Also blocked while one is active.
function verbs.stance_enchant(ctx, row)
  if ctx.enchantActive then ctx:say("This spell is already active."); return false end
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:armEnchant(ctx.amount, ctx.durationMs > 0 and ctx.durationMs or 60000)
  ctx:fxSelf()
  return true
end

-- Stealth (Rogue Invisible/Spirit's Form/…): arms a one-shot 9x sneak-attack burst; landing the next swing
-- strips it (handled engine-side in PlayerSwingDamage). Here we just spend mana and arm the timer.
function verbs.stance_stealth(ctx, row)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:armStealth(ctx.durationMs > 0 and ctx.durationMs or 60000)
  ctx:fxSelf()
  return true
end

-- Backstab / Flank (Warrior positional stances): arm the free positional-crit angle for the duration.
local function arm_positional(ctx, which)
  if not ctx:enoughMana(ctx.mana) then return false end
  ctx:debitMana(ctx.mana)
  ctx:stance(which, true, ctx.durationMs > 0 and ctx.durationMs or 60000)
  ctx:fxSelf()
  return true
end
function verbs.stance_backstab(ctx, row) return arm_positional(ctx, "backstab") end
function verbs.stance_flank(ctx, row)    return arm_positional(ctx, "flank")    end

-- Venom (RTK mage/venom.lua & kin): a MOB-only damage-over-time poison — ticks MaxHp*1% every 1.5s for a random
-- window, capped so it can never land the killing blow (RTK while_cast_1500). Reuses the SAME poison engine the
-- Rogue poison-dart trap already drives (World.PoisonMob). row.amount = per-tick cap, row.base = random lower
-- bound (ms); blocked if the target is already venomed (checkIfCast(venoms)) or isn't a mob ("It doesn't work").
function verbs.venom(ctx, row)
  local mana = row.mana or 60
  if not ctx:enoughMana(mana) then return false end
  if not ctx:applyVenom(row.amount or 1000, row.base or 1500, 30000) then return false end
  ctx:debitMana(mana)
  return true
end

-- =========================================================================================================
-- TIER-3 UTILITY / TARGET verbs (mana transfer, cleanse, revive, leap, mana battery). Classified in C#
-- (Content.Is*Spell), so they run against an EMPTY row — RTK constants are the `row.x or <default>` fallbacks
-- (add a SpellParams row later to tune). The target.* primitives all act on the PC resolved by ctx:pcTarget().
-- =========================================================================================================

-- Mana Steal (RTK poet inspiration): drain a GROUP member's entire mana into your own pool (capped at your max).
-- No cast cost — the "cost" is taking their mana. Target must be in your party and not a ghost.
function verbs.mana_steal(ctx, row)
  if not ctx:pcTarget() then return false end
  if ctx.targetIsDead then ctx:say("That cannot save them now."); return false end
  if not ctx.targetInGroup then ctx:say("They must be in your group."); return false end
  ctx:setMana(ctx.mp + ctx.targetMana)     -- setMana clamps to the caster's max
  ctx:setTargetMana(0)
  ctx:tellTarget()
  ctx:fx(6, 22)
  return true
end

-- Mana Gift (RTK poet inspire): top off ANOTHER player's mana from your own (drains you). Needs >= 30 mana to
-- attempt; gives up to their missing mana, capped by whatever you actually have.
function verbs.mana_gift(ctx, row)
  local cost = row.mana or 30
  if not ctx:pcTarget() then return false end
  if ctx.targetIsSelf then ctx:say("It doesn't work."); return false end
  if ctx.targetIsDead then ctx:say("That cannot save them now."); return false end
  if ctx.mp < cost then ctx:say("Not enough mana."); return false end
  local give = math.min(ctx.mp, ctx.targetMaxMana - ctx.targetMana)
  ctx:setMana(ctx.mp - give)
  ctx:setTargetMana(ctx.targetMana + give)
  ctx:tellTarget()
  ctx:fx(6, 22)
  return true
end

-- Cleanse (RTK poet dispell): chance-based FULL buff/debuff wipe on a targeted player (self-castable). Success =
-- (120 + clamp(targetAC,-60,70) - floor((targetWill-casterWill)/10)) / 2, floored at 10%. 200 mana, no cooldown.
function verbs.cleanse(ctx, row)
  local cost = row.mana or 200
  if ctx.mp < cost then ctx:say("You do not have enough mana."); return false end
  if not ctx:pcTarget() then return false end
  local armor = math.max(-60, math.min(70, ctx.targetArmor))
  local prot  = math.floor((ctx.targetWill - ctx.will) / 10)
  local rate  = math.max(10, math.ceil((120 + armor - prot) / 2))
  ctx:setMana(ctx.mp - cost)
  if not ctx:roll(rate) then ctx:say("Something went wrong."); return true end
  ctx:flushTarget()
  if not ctx.targetIsSelf then ctx:tellTarget() end
  ctx:fx(6, 34)
  return true
end

-- Revive (RTK poet resurrect): bring a dead/ghost player back to full health in place. 3000 mana, 8s cooldown.
function verbs.revive(ctx, row)
  local cost = row.mana or 3000
  if ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.mp < cost then ctx:say("Your will is too weak."); return false end
  if not ctx:pcTarget() then return false end
  if not ctx.targetIsDead then ctx:say(ctx.spellName .. " has no effect on the living."); return true end
  ctx:setMana(ctx.mp - cost)
  ctx:setCooldown(ctx.spellKey, 8000)
  ctx:reviveTarget()
  ctx:fx(6, 20)
  return true
end

-- Leap (RTK rogue race): jump up to 3 tiles in the faced direction, stopping at the last passable tile. 1 mana,
-- 80s cooldown. ctx:leap does the collision walk + the actual move, returning tiles moved (0 = nowhere to go).
function verbs.leap(ctx, row)
  local cost = row.mana or 1
  if ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.mp < cost then ctx:say("You do not have enough mana."); return false end
  if ctx:leap(row.amount or 3) == 0 then ctx:say("There's nowhere to go."); return false end
  ctx:setMana(ctx.mp - cost)
  ctx:setCooldown(ctx.spellKey, 80000)
  return true
end

-- Mana Battery (RTK invoke): trade HP for a full mana refill — costs 40% of max mana as HP (floored at 100 HP),
-- refills mana to full. Needs >= 30 mana to invoke; 22s cooldown.
function verbs.mana_battery(ctx, row)
  local minMana = row.mana or 30
  if ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if ctx.mp < minMana then ctx:say("Not enough mana."); return false end
  ctx:setHp(math.max(100, ctx.hp - math.floor(ctx.maxMp * 0.4)))
  ctx:setMana(ctx.maxMp)
  ctx:setCooldown(ctx.spellKey, 22000)
  ctx:fxSelf()
  return true
end

-- =========================================================================================================
-- CATEGORIZED STATUS layer (curses + wards). RTK's checkIfCast() cross-guards live in spellTables.lua: a cast is
-- refused if the target already carries a status in its category's BLOCK list. These relationships are RTK
-- CONSTANTS (not per-spell data), so they live here keyed by the casting status's category:
--   curses/minorcurses : own family + protections  (a protection makes you immune to curses)
--   disheartens        : own + bolsters + protections   (bolster blocks dishearten and vice-versa)
--   bolsters           : own + disheartens
--   hardarmors         : own only (independent of bolster — you CAN stack harden + bolster)
--   protections        : NONE — RTK hoche/immunity/... have no checkIfCast guard, so re-cast just refreshes
-- minorcurses collapses into curses inside ctx:hasStatus (see Session.CatFamily), so listing "curses" catches it.
local BLOCKS = {
  curses      = { "curses", "protections" },
  minorcurses = { "curses", "protections" },
  disheartens = { "disheartens", "bolsters", "protections" },
  bolsters    = { "bolsters", "disheartens" },
  hardarmors  = { "hardarmors" },
  protections = {},
}
-- Return the FIRST blocking category present on the target (via `hasFn`), or nil. Unknown categories default to
-- own-category exclusivity. `hasFn` is the caller's status check (curses check the curse target; wards their own).
local function blockedBy(hasFn, category)
  local list = BLOCKS[category]
  if list == nil then list = { category } end
  for _, cat in ipairs(list) do
    if hasFn(cat) then return cat end
  end
  return nil
end
local function blockMsg(cat)
  if cat == "protections" then return "The target is already protected." end
  return "Another spell of this type is in effect."
end

-- Curse (RTK Spells/*/pestilence.lua & kin): apply a MUTUALLY-EXCLUSIVE categorized status to a curse target.
-- Blocked if the target already carries a status in this category's BLOCK list (checkIfCast) — which is what
-- makes self-pestilence a real defense (occupy your own 'curses' slot with a mild curse; row.amount is the stat
-- effect, e.g. armor -5 -> raises effective AC -> take MORE damage) AND what makes a protection curse-immune.
-- Row: mana, category (curses/disheartens/...), stat, amount, duration. Removed later by a Cure of that category.
function verbs.curse(ctx, row)
  local mana = row.mana or 0
  if not ctx:enoughMana(mana) then return false end
  if not ctx:canCurse() then return false end                                    -- PvP-legal PC (incl self) or a mob
  local by = blockedBy(function(c) return ctx:hasStatus(c) end, row.category)
  if by then ctx:say(blockMsg(by)); return false end
  ctx:debitMana(mana)
  ctx:applyCurse(row.category, row.stat or "", math.floor(tonumber(row.amount) or 0), row.duration or 200000)
  return true
end

-- Ward (RTK bolster/harden_armor/hoche & kin): the BENEFICIAL twin of curse — a mutually-exclusive categorized
-- status cast on yourself or an ally (never PvP-gated; a bolster raises AC via positive `amount` in our inverted
-- convention). Protections (hoche family) carry NO stat (amount 0) — they exist only to occupy the 'protections'
-- slot so curses bounce off. Same _buffs storage + category exclusivity as curse, so a warded ally shows up when
-- an enemy's curse checks hasStatus. category -> self vs ally target and any cooldown are RTK constants (below).
local WARD_SELF     = { protections = true }        -- hoche/immunity/... are self-cast (no target arg)
local WARD_COOLDOWN = { protections = 180000 }      -- RTK setAether("hoche_warrior", 180000)
function verbs.ward(ctx, row)
  local mana = row.mana or 0
  local cd = WARD_COOLDOWN[row.category]
  if cd and ctx:onCooldown(ctx.spellKey) then ctx:say(ctx.spellName .. " isn't ready yet."); return false end
  if not ctx:enoughMana(mana) then return false end
  if not ctx:wardTarget(WARD_SELF[row.category] and "self" or "ally") then return false end
  local by = blockedBy(function(c) return ctx:wardHasStatus(c) end, row.category)
  if by then ctx:say(blockMsg(by)); return false end
  ctx:debitMana(mana)
  ctx:applyWard(row.category, row.stat or "", math.floor(tonumber(row.amount) or 0), row.duration or 60000)
  if cd then ctx:setCooldown(ctx.spellKey, cd) end
  return true
end
