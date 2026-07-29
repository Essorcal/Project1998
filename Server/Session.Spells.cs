using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Protocol.Tk495;
using Shared;

namespace Server;

public sealed partial class Session
{

    // ===== spells / skills ======================================================================
    // Spellbook wire = RTK 7.x clif_sendmagic, opcode 0x17: slot(u8=idx+1) type(u8) [name u8len+txt]
    // [question u8len+txt]. This is the same "no-op in the main world dispatcher (remap 0x2a), handled by
    // the client's SECONDARY dispatcher" pattern already proven for the item opcodes (0x0F/0x10/0x37/0x38):
    // 0x17 add-spell resolves to 0x2a in remap[0x17-3], exactly like 0x0F/0x10 which work live. The client
    // sorts type 1/2 into the Spell book and type 5 into the Skill book (one 0x17 packet, keyed on type).
    // Casting comes back client->server as 0x0F (clif_parsemagic) -> HandleCast. The 906 spell definitions
    // (name/class/level/type/prompt) come from the RTK Spells table (Content.Spells) — real NexusTK data.

    // The client's spellbook array size is unconfirmed for 4.95; RTK 7.x uses 52 (MAX_SPELLS). Cap
    // conservatively so an over-long teach can't overrun the client array; raise via NEXUS_SPELLBOOK_CAP
    // once a live test confirms the real limit.
    private static readonly int SpellBookCap =
        int.TryParse(Environment.GetEnvironmentVariable("NEXUS_SPELLBOOK_CAP"), out var c) && c > 0 ? c : 52;

    // Re-send every learned spell/skill on world entry (the client's book starts empty each login). Slot =
    // list index, matching the 0x0F cast "pos" the client sends back.
    private void RefreshSpells()
    {
        for (int i = 0; i < _char.Spells.Count; i++)
        {
            var sp = Content.SpellById(_char.Spells[i]);
            if (sp is not null) SendAddSpell(i, sp);
        }
    }

    // 0x17 add-spell-to-book: slot(u8=idx+1) type(u8) [name u8len+txt] [question u8len+txt].
    private void SendAddSpell(int slot, SpellDef sp)
    {
        var d = new List<byte> { (byte)(slot + 1), sp.Type };
        var nm = Ascii(sp.Name);     d.Add((byte)nm.Length); d.AddRange(nm);
        var q  = Ascii(sp.Question); d.Add((byte)q.Length);  d.AddRange(q);
        SendMap(0x17, _gameInc++, d.ToArray(), $"addspell(0x17) slot={slot} '{sp.Name}' t{sp.Type}");
    }

    // "!spells" — learn EVERY spell/skill of your class up to your level and populate the book. Class comes
    // from the profile class line (set with !class); level from !lvl. Skips ones already known.
    private void TeachClassSpells()
    {
        int path = Content.PathIdForClass(_char.ClassName);
        if (path < 0)
        {
            SendLog($"'{_char.ClassName}' isn't a known class — set one first, e.g.  !class Mage  (Warrior / Rogue / Mage / Poet / Peasant).");
            return;
        }
        var all = Content.SpellsForClass(path, _char.Level, _char.Alignment);
        if (all.Count == 0) { SendLog($"No spells found for {Content.PathName(path)} ({Character.AlignmentName(_char.Alignment)}) at level {_char.Level}."); return; }

        int learned = 0; bool capped = false;
        foreach (var sp in all)
        {
            if (_char.Spells.Contains(sp.Id)) continue;
            if (_char.Spells.Count >= SpellBookCap) { capped = true; break; }
            _char.Spells.Add(sp.Id);
            SendAddSpell(_char.Spells.Count - 1, sp);
            learned++;
        }
        if (_enteredWorld) _store.Save(_char);
        int spells = all.Count(s => s.IsSpell), skills = all.Count(s => s.IsSkill);
        SendLog($"Learned {learned} new {Content.PathName(path)} ({Character.AlignmentName(_char.Alignment)}) ability(ies) — " +
                $"book now {_char.Spells.Count} (class has {all.Count} ≤ lvl {_char.Level}: {spells} spell / {skills} skill)." +
                (capped ? $"  Hit the {SpellBookCap}-slot cap (raise NEXUS_SPELLBOOK_CAP)." : ""));
        Log.Info($"   -> !spells: {Content.PathName(path)}({path}) align {_char.Alignment} lvl {_char.Level} -> +{learned}, total {_char.Spells.Count}{(capped ? " (CAPPED)" : "")}");
    }

    // "!align <Unaligned|Kwisin|Mingken|Ohaeng | 0-3>" — set the sub-alignment !spells teaches. A character
    // learns only universal spells + this alignment's set, never the other sub-alignments' parallel spells
    // (which share display names and showed up as duplicates). Non-destructive: run !forgetspells + !spells
    // to relearn a clean single-alignment book after changing it.
    private void SetAlignment(string text)
    {
        string a = text.Length > "!align".Length ? text["!align".Length..].Trim() : "";
        if (a.Length == 0)
        {
            SendLog($"alignment is {Character.AlignmentName(_char.Alignment)} ({_char.Alignment}). usage: !align <Unaligned|Kwisin|Mingken|Ohaeng | 0-3>");
            return;
        }
        int val = int.TryParse(a, out var n) && n >= 0 && n < Character.Alignments.Length
            ? n
            : Array.FindIndex(Character.Alignments, s => string.Equals(s, a, StringComparison.OrdinalIgnoreCase));
        if (val < 0) { SendLog($"unknown alignment \"{a}\" — use Unaligned / Kwisin / Mingken / Ohaeng (or 0-3)."); return; }
        _char.Alignment = (byte)val;
        if (_enteredWorld) _store.Save(_char);
        SendLog($"Alignment set to {Character.AlignmentName(_char.Alignment)}. Run  !forgetspells  then  !spells  to relearn a clean {Character.AlignmentName(_char.Alignment)} set.");
        Log.Info($"   -> ALIGN set to {_char.Alignment} ({Character.AlignmentName(_char.Alignment)})");
    }

    // "!learnspell <name|id>" — learn a single spell/skill by fuzzy name or id (any class; handy for testing).
    private void LearnSpellCmd(string text)
    {
        string q = text.Length > "!learnspell".Length ? text["!learnspell".Length..].Trim() : "";
        if (q.Length == 0) { SendLog("usage: !learnspell <name or id>   (or  !spells  to learn all for your class)"); return; }
        var sp = Content.FindSpell(q);
        if (sp is null) { SendLog($"no spell matches \"{q}\"."); return; }
        if (_char.Spells.Contains(sp.Id)) { SendLog($"You already know {sp.Name}."); return; }
        if (_char.Spells.Count >= SpellBookCap) { SendLog($"Spellbook full ({SpellBookCap})."); return; }
        _char.Spells.Add(sp.Id);
        SendAddSpell(_char.Spells.Count - 1, sp);
        if (_enteredWorld) _store.Save(_char);
        SendLog($"Learned {sp.Name} ({(sp.IsSkill ? "skill" : "spell")}, {Content.PathName(sp.PathId)}).");
    }

    // "!forgetspells" — clear the whole book (0x18 remove per slot, then empty the list).
    private void ForgetSpells()
    {
        int n = _char.Spells.Count;
        for (int slot = n - 1; slot >= 0; slot--)
            SendMap(0x18, _gameInc++, new byte[] { (byte)(slot + 1) }, $"removespell(0x18) slot={slot}");
        _char.Spells.Clear();
        if (_enteredWorld) _store.Save(_char);
        SendLog($"Forgot all {n} spell(s).");
    }

    // 0x0F cast (RTK clif_parsemagic): body[0]=book slot+1; then per the learned spell's type: type 1 -> a
    // typed answer string, type 2 -> target entity id (u32BE), type 5 -> nothing. We play the cast animation
    // (0x1A type 6 = magic) for us + peers, spend a little mana, and apply a GENERIC effect: targeted (type 2)
    // spells damage the target world mob with a magic-power hit (reusing the world damage/exp path, so a spell
    // kill rewards exp like a melee kill); self/prompt spells just animate. Per-spell bespoke effects (heals,
    // buffs, teleports, summons) are a follow-up — RTK implements those as ~900 Lua scripts.
    private void HandleCast(byte[] dec)
    {
        if (_char.Hp == 0) { SendMiniText("Spirits cannot cast spells."); return; }
        if (dec.Length < 1) return;
        int slot = dec[0] - 1;
        if (slot < 0 || slot >= _char.Spells.Count)
        { Log.Info($"   ?? cast slot {slot} out of range ({_char.Spells.Count} known)"); return; }
        var sp = Content.SpellById(_char.Spells[slot]);
        if (sp is null) return;

        // Per spell type, the body carries different args after the slot byte. Type 1 ("Which …?") = a typed
        // answer string — e.g. Gateway's N/E/S/W. RTK's clif_parsemagic (clif.c:8857) strcpy's it straight from
        // the byte after the slot, so it is a NUL-terminated ASCII string (NOT length-prefixed). Type 2 ("Which
        // target?") = a u32BE entity id; if absent the client cast with just the slot (log: `0f 14 00`), so we
        // fall back to the faced tile.
        string? answer = null;
        if (sp.Type == 1 && dec.Length >= 2)
        {
            int end = 1;
            while (end < dec.Length && dec[end] != 0) end++;
            answer = Encoding.ASCII.GetString(dec, 1, end - 1);
        }
        uint? targetId = sp.Type != 1 && dec.Length >= 5
            ? (uint)((dec[1] << 24) | (dec[2] << 16) | (dec[3] << 8) | dec[4]) : null;
        Log.Info($"   -> CAST '{sp.Name}' slot {slot} type {sp.Type} base '{Content.BaseKey(sp)}'" +
                 $"{(answer is null ? "" : $" answer '{answer}'")} by {_char.Name}");

        if (!ApplyCast(sp, targetId, answer)) return;   // couldn't cast (no mana / too weak) — a message was already sent

        // The cast's magic animation (0x1A type 6). Sound is NOT carried here — the client picks an action's sound
        // from a fixed type->sound table (magic/type 6 has none), so the 4th byte is ignored. The spell's sound is
        // sent separately over 0x19 by BroadcastFx (RTK clif_playsound), which the static RE shows IS wired to the
        // audio player. param stays 0.
        SendAction(_char.Id, type: 6, time: 8, param: 0);                                  // cast anim (magic)
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 6, 8, 0), except: this);   // peers see us cast
        SendStats();                                                                        // push HP/MP to the HUD
    }

    // Apply a spell's effect. Returns false if the cast couldn't happen (a message was already sent). The engine
    // is DATA-DRIVEN: Content.FxFor(sp) supplies the archetype + real RTK formulas (extracted from the Lua by
    // re/extract_spell_formulas.py). We dispatch on the archetype — Damage/Heal evaluate the actual per-spell
    // formula, Buff applies a timed stat mod, Debuff freezes a mob, ManaBattery trades HP↔MP, Cure clears our
    // debuffs; Utility/Summon/Teleport/Dialog degrade to "spend mana + acknowledge". A spell with no export row
    // falls back to the keyword classifier (ApplyCastGeneric).
    private bool ApplyCast(SpellDef sp, uint? targetId, string? answer = null)
    {
        // Data-driven Lua verb path (data/game-data/SpellParams.csv + spell_verbs.lua): if this spell has a
        // params row naming a loaded Lua verb, run it and we're done. STRICTLY ADDITIVE — any spell without a
        // row (the other ~600) falls straight through to the C# dispatch below, unchanged. A Lua error falls
        // through too, so a broken verb can never take a spell offline. Both files hot-reload via !reload.
        if (Content.SpellParams.TryGetValue(sp.Key, out var prow))
        {
            var verb = prow.GetValueOrDefault("verb", "");
            if (SpellScript.HasVerb(verb) && SpellScript.Run(verb, new SpellContext(this, sp, targetId, answer), prow))
                return true;
        }

        // Gateway (common/gateway.lua): a teleport with its own bespoke logic — warp to a gate of the caster's
        // kingdom picked by the N/E/S/W answer. Intercept before the fx dispatch (a teleport has no damage/heal
        // archetype and would otherwise degrade to CastMisc's "spend mana + acknowledge" no-op).
        if (Content.BaseKey(sp) == "gateway") return CastGateway(sp, answer);

        // Return (common/return.lua): another peasant-commons teleport with no damage/heal archetype --
        // same interception reason as Gateway above, or it would degrade to CastMisc's no-op.
        if (sp.Key.Equals("return_spell", StringComparison.OrdinalIgnoreCase)) return CastReturn();

        // Propose (common/propose.lua): a skill-type spell (SplType 5 — no native typed-answer/target wire
        // arg at all) whose real interaction is entirely a scripted dialog (RTK inputSeq/menuSeq), same class
        // of primitive as an NPC's. Intercept before the fx dispatch — it has no export row and would
        // otherwise silently no-op via CastMisc.
        if (sp.Key.Equals("propose", StringComparison.OrdinalIgnoreCase)) return CastPropose(sp);

        // set_trap dispatcher (RTK rogue/set_trap.lua, SplQuestion "What trap? >"): re-runs the SAME level
        // gate + mana cost as casting the specific set_X_trap spell directly (see Content.TrapSpellFor),
        // keyed off the typed answer. Costs no mana itself — CastTrap below spends the real per-kind amount.
        if (Content.IsTrapDispatcher(sp))
        {
            var trapKey = Content.TrapKeyForAnswer(answer ?? "");
            var trapSpell = trapKey is null ? null : Content.SpellByKey(trapKey);
            var trapInfo = trapSpell is null ? null : Content.TrapSpellFor(trapSpell);
            if (trapInfo is null)
            {
                SendMiniText("Select: Dart trap, Snare trap, Repeating dart, Flash trap, Spear trap, Poison trap, Death trap, Sleep trap");
                return false;
            }
            if (_char.Level < trapInfo.Value.Level) { SendMiniText($"You must be level {trapInfo.Value.Level} to set that trap."); return false; }
            return CastTrap(trapSpell!, trapInfo.Value.Kind, Content.FxFor(trapSpell!), trapInfo.Value.Mana);
        }
        if (Content.TrapSpellFor(sp) is (Content.TrapKind directKind, int _, int directMana))
            return CastTrap(sp, directKind, Content.FxFor(sp), directMana);
        if (Content.IsBladestormTrap(sp)) return CastBladestormTrap(sp);
        if (Content.PetSpellFor(sp) is (string petMobKey, int _, int petMana, int petCooldown))
            return CastPetSummon(sp, petMobKey, petMana, petCooldown);

        var fx = Content.FxFor(sp);
        string arch = fx?.Archetype ?? "";

        // Mana-battery family (Invoke / Spirit's Power / …) always runs the verbatim RTK formula, whether the
        // export tagged it ManaBattery or we recognise its base identifier (belt-and-suspenders for export gaps).
        if (arch == "ManaBattery" || Content.BaseKey(sp) is "invoke" or "spirits_power" or "life_force" or "gather_magic")
            return CastManaBattery(sp);

        if (fx is null) return ApplyCastGeneric(sp, targetId);   // no export row — keyword classifier fallback

        // Cooldown (RTK "aether"), if this spell has one and it's still ticking.
        if (fx.Aether > 0 && OnCooldown(sp.Key, out int wait))
        { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }

        int mana = fx.Mana > 0 ? fx.Mana : 5;   // real per-spell cost; 5 only if the export had none

        // Backstab/Flank (Warrior-only skills — RTK Spells.csv has both at SplPthId=1, no other class learns
        // either; user: "Flank and Backstab come from the warrior spells that provide those abilities. I
        // think only warriors ever get them"): a boolean combat STANCE (RTK player.backstab/player.flank),
        // not a numeric stat — CastBuff's generic BuffStat/BuffAmt loop can't express it (this spell's export
        // row has neither set), so it silently no-opped before. RTK tags every alignment variant of both
        // (backstab_warrior/back_battle_warrior/back_attack_warrior/back_damage_warrior, and the flank
        // equivalents) with CureCat "backstabs"/"flanks" — a clean, data-driven way to catch all 4 aliases of
        // each without hardcoding 8 identifiers.
        if (fx.CureCat == "backstabs") return CastStance(sp, fx, mana, isBackstab: true);
        if (fx.CureCat == "flanks")    return CastStance(sp, fx, mana, isBackstab: false);

        // Rage tiers (Wolf's/Tiger's/Dragon's Fury, Baekho's Rage — Warrior AND Rogue; user: "they get rage
        // spells until 99 I think") and Rogue's Invisible/Spirit's Form/Life's Cloak/Glass Form (sneak-attack
        // multiplier — user: "rogue invis also has a damage multiplier"): same class of gap as Backstab/Flank
        // above — neither has a numeric BuffStat/BuffAmt the generic CastBuff loop can express, so both
        // silently no-opped (spent mana, printed a message, did nothing) before this pass.
        if (Content.RageAmountFor(sp) is int rageAmt) return CastRage(sp, fx, mana, rageAmt);
        if (Content.IsStealthSpell(sp)) return CastStealth(sp, fx, mana);
        if (Content.EnchantFor(sp) is (double enchantAmt, int enchantMana)) return CastEnchant(sp, fx, enchantAmt, enchantMana);

        // The rest of this pass (self-sacrifice strikes, mana steal/gift, cleanse, revive, short leap): none
        // of these have a numeric BuffStat/BuffAmt or a damage amountExpr the generic archetypes can express
        // either (their CSV rows are all bare "Utility"), and each manages its own real RTK mana cost/cooldown
        // internally rather than trusting the generic `mana`/`fx.Aether` values above (which are blank for
        // all of them in the export — see each method's own hardcoded RTK constant).
        if (Content.SacrificeFamilyFor(sp) is Content.SacrificeFamily fam) return CastSacrificeStrike(sp, fam);
        if (Content.IsManaStealSpell(sp)) return CastManaSteal(sp, targetId);
        if (Content.IsManaGiftSpell(sp)) return CastManaGift(sp, targetId);
        if (Content.IsCleanseSpell(sp)) return CastCleanse(sp, targetId);
        if (Content.IsReviveSpell(sp)) return CastRevive(sp, targetId);
        if (Content.IsLeapSpell(sp)) return CastLeap(sp);
        if (Content.IsAmbushSpell(sp)) return CastAmbush(sp);
        if (Content.IsSpotTrapsSpell(sp)) return CastSpotTraps(sp, fx, mana);
        if (Content.IsGroundLootSpell(sp)) return CastGroundLoot(sp, fx, mana);
        if (Content.IsDivinationSpell(sp)) return CastDivination(sp, fx, targetId, Content.IsDivinationSpySpell(sp));

        // Morph family (see Content.MorphSpells/MorphDispatchSpells): question-dispatched ones (feral_rogue,
        // gangrel_rogue/mage, rodent_rogue, beast_rogue/mage, druids_rodent, wilderness_guise) resolve their
        // look from the typed answer; the rest are fixed alignment reskins.
        if (Content.MorphDispatchFor(sp) is (Dictionary<string, ushort> morphAnswers, int mdMana, int mdDur))
        {
            if (!morphAnswers.TryGetValue((answer ?? "").Trim().ToLowerInvariant(), out var mLook))
            { SendMiniText("Become what?"); return false; }
            return CastMorph(sp, fx, mLook, 0, mdMana, mdDur);
        }
        if (Content.MorphFor(sp) is (ushort morphLook, ushort morphLookF, int morphMana, int morphDur))
            return CastMorph(sp, fx, morphLook, morphLookF, morphMana, morphDur);

        // Archetype dispatch. Each archetype first tries its data-driven Lua verb (`arch_<name>` in
        // spell_verbs.lua), so the whole archetype's behaviour is scriptable + hot-reloadable; if the verb is
        // absent it falls back to the C# handler unchanged. Migrating one archetype at a time (Damage done —
        // see docs/Modding.md); the others still dispatch straight to C#.
        bool ok = arch switch
        {
            "Damage" => CastArch("arch_damage", sp, fx, targetId, mana) ?? CastDamage(sp, fx, targetId, mana),
            "Heal"   => CastHeal(sp, fx, mana),
            "Buff"   => CastBuff(sp, fx, mana),
            "Debuff" => CastDebuff(sp, fx, targetId, mana),
            "Cure"   => CastCure(sp, fx, mana),
            _        => CastMisc(sp, mana),   // Utility / Summon / Teleport / Dialog — graceful
        };
        if (ok && fx.Aether > 0) SetCooldown(sp.Key, fx.Aether);
        return ok;
    }

    // Archetype Lua hook: if spell_verbs.lua defines `verb` (e.g. arch_damage), evaluate this spell's real
    // formula (spell_effects.csv amountExpr — no target term exists in any formula, so SpellVars(null) matches
    // what the C# archetype computed) and run the verb with the amount + mana pre-supplied. Returns the verb's
    // success bool, or null if the verb isn't loaded so the caller falls back to the C# CastX handler. A verb
    // that errors mid-run returns false (not null) so we don't double-apply via the fallback.
    private bool? CastArch(string verb, SpellDef sp, SpellFx fx, uint? targetId, int mana)
    {
        if (!SpellScript.HasVerb(verb)) return null;
        double amount = Math.Round(Formula.Eval(fx.AmountExpr, SpellVars(null)));
        return SpellScript.RunArch(verb, new SpellContext(this, sp, targetId, null, amount, mana));
    }

    // ---- Lua spell-verb primitives (called by SpellContext; see Server/SpellScript.cs) --------------------
    // Thin internal wrappers over the SAME plumbing the C# CastX methods use, so the Lua route can't drift into
    // a second combat/heal implementation. Effective (base+gear+buff) stats, matching CastDamage/CastHeal.
    internal int  LuaLevel => _char.Level;
    internal int  LuaWill  => _char.Will + Totals().will;
    internal int  LuaGrace => _char.Grace + Totals().grace;
    internal int  LuaMight => EffMight;
    internal uint LuaHp    => _char.Hp;
    internal uint LuaMaxHp => EffMaxHp;
    internal uint LuaMp    => _char.Mp;
    internal bool LuaHasTarget(uint? targetId) => ResolveCastTarget(targetId) is not null;

    internal bool LuaSpendMana(int amt, SpellDef sp)
    {
        if (amt < 0) amt = 0;
        if (_char.Mp < (uint)amt) { SendMiniText($"Not enough mana to cast {sp.Name}."); return false; }
        _char.Mp -= (uint)amt;
        SendStats();
        return true;
    }

    internal bool LuaDamageTarget(int amt, SpellDef sp, uint? targetId)
    {
        var mob = ResolveCastTarget(targetId);
        if (mob is null) { SendMiniText($"{sp.Name} finds no target."); return false; }
        if (sp.CanFail && RollDeflect(mob)) { SendMiniText("The magic has been deflected."); return true; }
        if (amt < 1) amt = 1;
        var fx = Content.FxFor(sp);
        if (_world.TryDamage(_char.Map, mob, amt, out bool died, _char.Id))
        {
            if (fx is not null) BroadcastFx(mob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
            ShowDamageResult(mob.Id, mob, died);
            if (died)
            {
                uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
                AwardExp(reward, killExp: true);
                SendMessage($"Your {sp.Name} destroys {mob.Name}! (+{reward} exp)");
            }
            else SendMessage($"Your {sp.Name} hits {mob.Name} for {amt}.");
            Log.Info($"      (lua) {sp.Name} -> mob {mob.Id} '{mob.Name}' for {amt} (died={died})");
        }
        return true;
    }

    // Full magic-attack sequence for the archetype Lua path (arch_damage) — a faithful port of CastDamage's body:
    // mana check FIRST, then resolve target, then the deflect roll (which spends NO mana, RTK-correct), then debit
    // mana, then apply. Takes a pre-evaluated amount (the engine already ran the spell_effects.csv formula) so the
    // verb stays pure logic. Returns false only when the cast can't happen (no mana / no target); a deflect still
    // returns true (the cast "happened", just resisted). Byte-identical to CastDamage so the Lua route can't drift.
    internal bool LuaMagicDamage(int amt, int mana, SpellDef sp, uint? targetId)
    {
        if (mana < 0) mana = 0;
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        var mob = ResolveCastTarget(targetId);
        if (mob is null) { SendMiniText($"{sp.Name} finds no target."); return false; }
        if (sp.CanFail && RollDeflect(mob)) { SendMiniText("The magic has been deflected."); return true; }
        if (amt < 1) amt = 1;
        _char.Mp -= (uint)mana;
        var fx = Content.FxFor(sp);
        if (_world.TryDamage(_char.Map, mob, amt, out bool died, _char.Id))
        {
            if (fx is not null) BroadcastFx(mob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
            ShowDamageResult(mob.Id, mob, died);
            if (died)
            {
                uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
                AwardExp(reward, killExp: true);
                SendMessage($"Your {sp.Name} destroys {mob.Name}! (+{reward} exp)");
            }
            else SendMessage($"Your {sp.Name} hits {mob.Name} for {amt}.");
            Log.Info($"      (lua-arch) {sp.Name} -> mob {mob.Id} '{mob.Name}' for {amt} (died={died})");
        }
        return true;
    }

    internal void LuaHeal(int amt, SpellDef sp)
    {
        if (amt <= 0) { SendMiniText($"You cast {sp.Name}."); return; }
        uint before = _char.Hp;
        _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)amt);
        uint gain = _char.Hp - before;
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText(gain > 0 ? $"{sp.Name} restores {gain} HP." : $"You cast {sp.Name} (already at full HP).");
        SendStats();
    }

    internal void LuaRestoreMana(int amt)
    {
        if (amt <= 0) return;
        _char.Mp = Math.Min(EffMaxMp, _char.Mp + (uint)amt);
        SendStats();
    }

    internal void LuaSay(string msg)     => SendMiniText(msg);
    internal void LuaMessage(string msg) => SendMessage(msg);

    // Apply one timed stat buff (might/hit/dam/hp/mp/…) for durationMs, folded live into Totals() -> HUD/melee.
    // Re-casting the SAME spell refreshes rather than stacks (matches C# CastBuff / RTK removeDuras-then-set).
    // Buffs flow through BuffTotals() (never cached), so no equip-cache invalidation is needed. Shares the exact
    // ActiveBuff plumbing the C# archetype uses.
    internal void LuaBuff(string stat, int amount, int durationMs, SpellDef sp)
    {
        if (string.IsNullOrEmpty(stat) || amount == 0 || durationMs <= 0) return;
        _buffs.RemoveAll(b => b.Key == sp.Key);   // refresh, don't stack
        _buffs.Add(new ActiveBuff { Stat = stat, Amount = amount, Expires = Environment.TickCount64 + durationMs, Key = sp.Key, Name = sp.Name });
        SendStats();
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
    }

    // The stat variables an RTK spell formula reads (player.level, player.will, target.baseHealth, …). Effective
    // (base + gear + buff) values, so a buffed caster hits harder. enchant/rage/invis reflect the real armed
    // multiplier now (EffEnchant/EffRage/Stealthed) in case any Damage-archetype formula ever reads them; fury
    // has no separate tracked state (player.rage covers every fury tier) so it stays a no-op 1.
    private Dictionary<string, double> SpellVars(Mob? target)
    {
        var t = Totals();
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["player.level"]      = _char.Level,
            ["player.will"]       = _char.Will + t.will,
            ["player.grace"]      = _char.Grace + t.grace,
            ["player.might"]      = EffMight,
            ["player.magic"]      = _char.Mp,
            ["player.maxMagic"]   = EffMaxMp,
            ["player.health"]     = _char.Hp,
            ["player.maxHealth"]  = EffMaxHp,
            ["player.enchant"]    = EffEnchant, ["player.rage"] = EffRage, ["player.fury"] = 1, ["player.invis"] = Stealthed ? 9 : 1,
            ["target.health"]     = target?.Hp ?? 0,
            ["target.baseHealth"] = target?.MaxHp ?? 0,
        };
    }

    // RTK magic-deflect roll (clif_parsemagic, clif.c:8910-8934): resist = target.Protection + the target's
    // Will advantage over the caster (in 10-point steps), then exponential decay (0.9^prot) turns that into a
    // fail chance. Only spells flagged SplCanFail roll this at all. `target.Protection` now comes from the
    // merged-in CTK `MobProtection` column (Content.cs) — previously always 0 for lack of a source column.
    // No mana is spent on a deflected cast (RTK returns before the Lua "cast" script — which is where mana is
    // actually debited — ever runs).
    private bool RollDeflect(Mob target)
    {
        int casterWill = _char.Will + Totals().will;
        int willDiff = Math.Max(0, target.Will - casterWill);
        int prot = Math.Max(0, target.Protection + (int)(willDiff / 10.0 + 0.5));
        int failChance = (int)(100 - Math.Pow(0.9, prot) * 100 + 0.5);
        return Random.Shared.Next(100) < failChance;
    }

    // Damage: evaluate the spell's real RTK damage formula, spend mana, apply to the faced/targeted mob.
    private bool CastDamage(SpellDef sp, SpellFx fx, uint? targetId, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        var mob = ResolveCastTarget(targetId);
        if (mob is null) { SendMiniText($"{sp.Name} finds no target."); return false; }
        if (sp.CanFail && RollDeflect(mob)) { SendMiniText("The magic has been deflected."); return true; }

        int power = Math.Max(1, (int)Math.Round(Formula.Eval(fx.AmountExpr, SpellVars(mob))));
        _char.Mp -= (uint)mana;
        if (_world.TryDamage(_char.Map, mob, power, out bool died, _char.Id))
        {
            BroadcastFx(mob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // graphic + sound
            ShowDamageResult(mob.Id, mob, died);   // 0x13: over-head HP bar (empty bar + delayed despawn on death)
            if (died)
            {
                uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
                AwardExp(reward, killExp: true);
                SendMessage($"Your {sp.Name} destroys {mob.Name}! (+{reward} exp)");
            }
            else SendMessage($"Your {sp.Name} hits {mob.Name} for {power}.");
            Log.Info($"      {sp.Name} -> mob {mob.Id} '{mob.Name}' for {power} (died={died})");
        }
        return true;
    }

    // Heal: evaluate the spell's real heal amount and restore the caster's HP (RTK heals a target; solo, that's
    // us). A pure heal at full HP still "works" (mana spent) — matches casting a heal you don't strictly need.
    private bool CastHeal(SpellDef sp, SpellFx fx, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        int amount = Math.Max(0, (int)Math.Round(Formula.Eval(fx.AmountExpr, SpellVars(null))));
        _char.Mp -= (uint)mana;
        uint before = _char.Hp;
        _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)amount);
        uint gain = _char.Hp - before;
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // heal sparkle + sound
        SendMiniText(gain > 0 ? $"{sp.Name} restores {gain} HP." : $"You cast {sp.Name} (already at full HP).");
        return true;
    }

    // Buff: apply the spell's timed stat modifier(s) (might/hit/dam/…) for its RTK duration, folded live into the
    // HUD/melee via Totals(). Re-casting refreshes rather than stacks (matches RTK removeDuras-then-setDuration).
    // Buffs whose stat delta the export couldn't pin down still "cast" (mana + duration marker) so they're not
    // silently dead.
    private bool CastBuff(SpellDef sp, SpellFx fx, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;

        int durMs = fx.DurationMs > 0 ? fx.DurationMs : 60000;
        long expires = Environment.TickCount64 + durMs;
        _buffs.RemoveAll(b => b.Key == sp.Key);   // refresh, don't stack

        var stats = fx.BuffStat.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var amts  = fx.BuffAmt.Split('|', StringSplitOptions.RemoveEmptyEntries);
        int applied = 0;
        for (int i = 0; i < stats.Length && i < amts.Length; i++)
            if (int.TryParse(amts[i].Split('.')[0], out var amt) && amt != 0)
            { _buffs.Add(new ActiveBuff { Stat = stats[i], Amount = amt, Expires = expires, Key = sp.Key, Name = sp.Name }); applied++; }

        SendStats();   // reflect the boosted caps/attributes on the HUD immediately
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // buff aura + sound
        SendMiniText(applied > 0
            ? $"You cast {sp.Name} — you feel its power ({durMs / 1000}s)."
            : $"You cast {sp.Name}.");
        return true;
    }

    // Backstab/Flank stance timers (RTK player.backstab/player.flank, set true by these Warrior skills'
    // "recast" hook and cleared by "uncast" when the duration lapses — we just track expiry directly, same
    // pattern as Mob.FrozenUntil). Read by Session.PlayerSwingDamage via the properties below to apply RTK
    // swingDamage.lua's backstab/flank positional 2x (Combat.IsBackstabAngle/IsFlankAngle) on top of the
    // base same-facing positional bonus every swing already gets.
    private long _backstabUntil, _flankUntil;
    private bool BackstabStance => Environment.TickCount64 < _backstabUntil;
    private bool FlankStance    => Environment.TickCount64 < _flankUntil;

    // Backstab/Flank (RTK Spells.csv SplPthId=1 — Warrior-only, both at type 5/self-cast): a timed STANCE,
    // not a stat buff or an attack — casting it just arms the positional bonus for the buff's duration (RTK
    // 625s / ~10.4min per the Lua's `setDuration(..., 625000)`). Refreshing while already active just
    // extends it (RTK's own recast semantics), matching CastBuff's "refresh, don't stack" behavior.
    private bool CastStance(SpellDef sp, SpellFx fx, int mana, bool isBackstab)
    {
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        int durMs = fx.DurationMs > 0 ? fx.DurationMs : 60000;
        long expires = Environment.TickCount64 + durMs;
        if (isBackstab) _backstabUntil = expires; else _flankUntil = expires;
        SendStats();
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText($"You cast {sp.Name} — you'll strike harder from the {(isBackstab ? "back" : "side")} for {durMs / 1000}s.");
        Log.Info($"      {sp.Name} -> {(isBackstab ? "backstab" : "flank")} stance armed for {durMs}ms");
        return true;
    }

    // Rage-tier timer (RTK player.rage — Wolf's/Tiger's/Dragon's Fury, Baekho's Rage): swingDamage.lua
    // multiplies the ENTIRE player swing by max(player.rage,1), so this is the single biggest melee
    // multiplier a Warrior/Rogue can stack (up to 5x at the level-99 tier). Expires back to the RTK
    // baseline of 1 (not 0) automatically once _rageUntil lapses — see EffRage.
    private long _rageUntil;
    private int  _rageAmount = 1;
    private int  EffRage => Environment.TickCount64 < _rageUntil ? _rageAmount : 1;

    // Real RTK rejects re-casting ANY fury while one is already active (`player:checkIfCast(lesserFuries) or
    // player.rage > 1`) — you can't skip straight to a stronger tier by overwriting; you wait the current one
    // out. Content.RageAmountFor's level gate (via SpellLevelOverrides) already keeps a low-level character
    // off the higher tiers; this just blocks stacking/re-triggering once any tier is up.
    private bool CastRage(SpellDef sp, SpellFx fx, int mana, int rageAmount)
    {
        if (EffRage > 1) { SendMiniText("You are already benefiting from a fury."); return false; }
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        int durMs = fx.DurationMs > 0 ? fx.DurationMs : 60000;
        _rageUntil = Environment.TickCount64 + durMs;
        _rageAmount = rageAmount;
        SendStats();
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText($"You cast {sp.Name} — your attacks hit {rageAmount}x harder for {durMs / 1000}s!");
        Log.Info($"      {sp.Name} -> rage x{rageAmount} armed for {durMs}ms");
        return true;
    }

    // Stealth timer (RTK player.state==2 — Rogue Invisible/Spirit's Form/Life's Cloak/Glass Form): a flat 9x
    // damage multiplier on the swing that follows (swingDamage.lua: `if player.state==2 then invisible=9
    // end`), meant as a one-shot sneak-attack burst — landing that hit strips the stealth immediately
    // (RTK `block:removeDuras(invis)` after a nonzero hit), so Session.PlayerSwingDamage clears _stealthUntil
    // itself once it applies the multiplier. ONLY the damage multiplier is ported here — real RTK's
    // PC_INVIS also hides the player's sprite from other clients (clif.c), which isn't touched by this pass.
    private long _stealthUntil;
    private bool Stealthed => Environment.TickCount64 < _stealthUntil;

    private bool CastStealth(SpellDef sp, SpellFx fx, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        int durMs = fx.DurationMs > 0 ? fx.DurationMs : 60000;
        _stealthUntil = Environment.TickCount64 + durMs;
        SendStats();
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText($"You cast {sp.Name} — your next strike will land with brutal force ({durMs / 1000}s).");
        Log.Info($"      {sp.Name} -> stealth (9x next hit) armed for {durMs}ms");
        return true;
    }

    // Enchant-tier timer (RTK player.enchant — see Content.EnchantFor): unlike rage, swingDamage.lua
    // multiplies ONLY the raw weapon-swing term (s/2) by this, not the whole swing (Session.PlayerSwingDamage).
    // Expires back to the RTK baseline of 1 (not 0) once _enchantUntil lapses, same shape as EffRage.
    private long _enchantUntil;
    private double _enchantAmount = 1;
    private double EffEnchant => Environment.TickCount64 < _enchantUntil ? _enchantAmount : 1;

    // Real RTK rejects casting ANY enchant while one (including itself) is already active — the shared
    // "enchants" checkIfCast group in spellTables.lua — rather than letting a stronger tier overwrite a
    // weaker one. Mana is the per-spell hardcoded amount from Content.EnchantFor, not the generic dispatch
    // `mana` (unreliable in the CSV export for these Type-5 skills — tigers_fortitude_rogue is genuinely free).
    private bool CastEnchant(SpellDef sp, SpellFx fx, double amount, int mana)
    {
        if (EffEnchant > 1) { SendMiniText("This spell is already active."); return false; }
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        int durMs = fx.DurationMs > 0 ? fx.DurationMs : 60000;
        _enchantUntil = Environment.TickCount64 + durMs;
        _enchantAmount = amount;
        SendStats();
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText($"Your weapon shines with holy light — enchanted for {durMs / 1000}s.");
        Log.Info($"      {sp.Name} -> enchant x{amount} armed for {durMs}ms");
        return true;
    }

    // RTK "morphs" duration group (see Content.MorphSpells/MorphDispatchFor) — purely cosmetic disguise.
    // CORRECTED 2026-07-26: an earlier pass concluded self-view was client-engine blocked, based on 0x33's
    // handler (0x44fef0) always forcing the player sprite archive (0x4f2a84) via 0x463380. That's real, but
    // it only proves 0x33 can't draw a monster — it does NOT apply to self. Fresh disassembly of the shared
    // entity factory (0x44d7d0) shows a THIRD path: when a packet's entity id equals the client's own self
    // id (checked via `cmp edi,[[ebx+0x40c]+0x108]`), the factory never touches the peer/monster ctor OR
    // 0x33's hardcoded-archive draw call at all — it reroutes to 0x461e30, which writes the look descriptor
    // straight into the self entity's OWN struct fields (self+0x178/0x17c/0x180), the identical fields a
    // real monster entity carries. That path is reachable the exact same way the peer workaround already
    // reaches it: send a real 0x07 Monster.epf creature-spawn, just addressed to the caster's own id instead
    // of a peer's. So Session.ShowPlayer — the single choke point every peer re-sync path already funnels
    // through — now updates the CASTER's own view too (World.Broadcast with no `except`, including a
    // self-call). The caster's own id never enters World's mob list, so click/party/trade resolution
    // (HandleClickInfo checks MobById before PlayerById) is unaffected either way.
    private ushort _morphLook;     // 0 = not morphed; else the Monster.tbl index peers see us as (0x8000|this)
    private byte   _morphColor;
    private long   _morphUntil;
    private string _morphKey = "";
    public bool IsMorphed => _morphLook != 0 && Environment.TickCount64 < _morphUntil;
    /// <summary>Read by World.Tick (outside the lock) to know when to fire the revert broadcast.</summary>
    public bool IsMorphExpired => _morphLook != 0 && Environment.TickCount64 >= _morphUntil;

    // Real RTK lets EVERY morph identifier keep its OWN independent duration timer (hasDuration(OWN_NAME)),
    // so casting a second morph while a different one is active leaves both ticking — whichever lapses LAST
    // wins the visual, a genuine RTK quirk with no gameplay purpose. Simplified to one consistent slot:
    // casting a DIFFERENT morph replaces the old one outright; re-casting the SAME one un-morphs (toggle),
    // matching every morph script's own `if hasDuration(OWN_NAME) then removeDuras(morphs); return`.
    private bool CastMorph(SpellDef sp, SpellFx? fx, ushort look, ushort lookFemale, int mana, int durationMs)
    {
        if (IsMorphed && string.Equals(_morphKey, sp.Key, StringComparison.OrdinalIgnoreCase)) { RevertMorph(); return true; }
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        _buffs.RemoveAll(b => b.Key == _morphKey);   // drop the OLD morph's timer entry (if switching morphs)
        _morphLook = (lookFemale != 0 && _char.Sex == 1) ? lookFemale : look;
        _morphColor = 0;   // RTK ties this to disguiseColor = player.armorColor, a per-item dye this server doesn't track
        _morphUntil = Environment.TickCount64 + durationMs;
        _morphKey = sp.Key;
        _buffs.Add(new ActiveBuff { Stat = "", Amount = 0, Expires = _morphUntil, Key = sp.Key, Name = sp.Name });
        SendStats();
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        // Force-clear the old entity (incl. its nameplate) on PEERS before re-showing — see RefreshAppearance
        // doc comment. Deliberately NOT sent to ourselves: unlike a peer's copy of us, our own client's self
        // entity is a persistent singleton the 0x33 self-id path updates in place (never destroys), and an
        // explicit 0x0E despawn of our own id risks tearing down that same object instead (untested, and not
        // needed — self's view was already correct before this fix).
        _world.Broadcast(_char.Map, p => p.DespawnEntity(_char.Id), except: this);
        _world.Broadcast(_char.Map, p => p.ShowPlayer(this), except: this);
        ShowPlayer(this);   // + our own view too, via the same 0x07 self-id path — see doc comment above
        SendMiniText($"You feel your body twist and change! ({durationMs / 1000}s)");
        Log.Info($"      {sp.Name} -> morph look={_morphLook} for {durationMs}ms (self + peer visible via 0x07, see Content.MorphSpells)");
        return true;
    }

    /// <summary>Clears morph state and re-broadcasts our real human look to everyone, including ourselves.
    /// Called from the toggle-off cast above, and from World.Tick (via IsMorphExpired) when the duration
    /// lapses on its own.</summary>
    public void RevertMorph()
    {
        if (_morphLook == 0) return;
        _buffs.RemoveAll(b => b.Key == _morphKey);
        _morphLook = 0; _morphColor = 0; _morphUntil = 0; _morphKey = "";
        _world.Broadcast(_char.Map, p => p.DespawnEntity(_char.Id), except: this);   // force-clear the morphed entity (incl. its nameplate) on peers before restoring the real look
        _world.Broadcast(_char.Map, p => p.ShowPlayer(this), except: this);
        ShowPlayer(this);   // restore our own view too (same 0x07-self-id path we used to morph)
    }

    // The rogue/warrior "self-sacrifice strike" family (Lethal Strike/Afterlife's Embrace/Ming-Ken's
    // Judgement/Calculating Blow; Desperate Attack/The Void's Measure/Beastly Frenzy/Tilting the Balance;
    // Berserk/No Fear/Tiger's Pounce/Wind's Blast; Whirlwind/Death's Angel/Nature's Own/Bladedance). Ported
    // verbatim from RTK rogue/lethal_strike.lua, rogue/desperate_attack.lua, warrior/berserk.lua,
    // warrior/whirlwind.lua (+ rogue/backflow.lua, warrior/overflow.lua):
    //   - damage is computed from the CASTER's OWN pre-cast HP/MP, not target stats — a facing-tile physical
    //     attack (not a targeted cast) that hits whatever mob is directly in front of the caster
    //   - the raw damage is armor-netted the same way melee is (Combat.ApplyArmor) to find any OVERKILL
    //   - Rogue pair (Lethal Strike/Desperate Attack): overkill "backflows" — up to half returns to the
    //     caster as HP and MP, each capped at half whatever HP/MP the caster had before casting
    //   - Warrior pair (Berserk/Whirlwind): overkill "overflows" instead — splashes recursively onto up to
    //     4 adjacent-tile mobs rather than refunding the caster
    //   - landing the hit ALWAYS costs the caster a big chunk of their own HP, regardless of overkill
    //   - Baekho's Rage specifically (rage tier 5, not any lesser Fury) adds a further 1.5x to Berserk/Whirlwind
    //   - Whirlwind's damage factor AND post-hit HP cost differ by the caster's OWN alignment stat (RTK reads
    //     player.alignment directly, not which of the 4 aliases was actually cast — Unaligned/Kwisin get the
    //     milder tier, Ming-Ken/Ohaeng the harsher one)
    // PC targets are skipped entirely — no PvP damage path exists yet (same precedent as CastDebuff below).
    private bool CastSacrificeStrike(SpellDef sp, Content.SacrificeFamily fam)
    {
        var (mana, aetherMs) = fam switch
        {
            Content.SacrificeFamily.LethalStrike    => (120, 23000),
            Content.SacrificeFamily.DesperateAttack => (60, 11000),
            Content.SacrificeFamily.Berserk         => (60, 12000),
            Content.SacrificeFamily.Whirlwind       => (120, _char.Alignment == 0 ? 30000 : 25000),
            _ => (60, 60000),
        };
        if (OnCooldown(sp.Key, out int wait)) { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }

        uint preHealth = _char.Hp, preMagic = _char.Mp;
        bool baekho = _rageAmount == 5 && EffRage > 1;   // Baekho's Rage specifically, not any lesser Fury tier

        int damage = fam switch
        {
            Content.SacrificeFamily.LethalStrike    => (int)Math.Ceiling(preHealth / 2.0) + (int)Math.Ceiling(preMagic * 2.5),
            Content.SacrificeFamily.DesperateAttack => (int)(preHealth + preMagic),
            Content.SacrificeFamily.Berserk         => (int)Math.Ceiling(preHealth * 0.75),
            Content.SacrificeFamily.Whirlwind       => (int)Math.Ceiling(preHealth * (_char.Alignment >= 2 ? 1.525 : 1.75)),
            _ => 0,
        };
        if ((fam is Content.SacrificeFamily.Berserk or Content.SacrificeFamily.Whirlwind) && baekho)
            damage = (int)Math.Ceiling(damage * 1.5);

        _char.Mp -= (uint)mana;
        SetCooldown(sp.Key, aetherMs);

        var (fx, fy) = FrontTile();
        var mob = _world.MobAt(_char.Map, fx, fy);
        bool landed = false;
        if (mob is not null && mob.Alive)
        {
            int netDamage = Combat.ApplyArmor(damage, mob.Ac, floor: -95);
            int overkill = netDamage - mob.Hp;
            _world.TryDamage(_char.Map, mob, netDamage, out bool died, _char.Id);
            BroadcastFx(mob.Id, SacrificeAnim(fam), SacrificeSound(fam));
            ShowDamageResult(mob.Id, mob, died);
            landed = true;
            if (died) { uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp); AwardExp(reward, killExp: true); SendMessage($"Your {sp.Name} destroys {mob.Name}! (+{reward} exp)"); }
            else SendMessage($"Your {sp.Name} hits {mob.Name} for {netDamage}.");

            if (overkill > 0)
            {
                if (fam is Content.SacrificeFamily.LethalStrike or Content.SacrificeFamily.DesperateAttack)
                    ApplyBackflow(overkill, preHealth, preMagic);
                else
                    ApplyOverflow(overkill, fx, fy, fam);
            }
        }
        else SendMiniText($"{sp.Name} finds no target.");

        if (landed)
        {
            _char.Hp = fam switch
            {
                Content.SacrificeFamily.LethalStrike    => (uint)Math.Ceiling(_char.Hp / 2.0),
                Content.SacrificeFamily.DesperateAttack => (uint)Math.Ceiling(_char.Hp / 2.0),
                Content.SacrificeFamily.Berserk         => (uint)Math.Ceiling(_char.Hp / 3.0),
                Content.SacrificeFamily.Whirlwind       => _char.Alignment >= 2 ? (uint)Math.Ceiling(_char.Hp * 0.10) : 10,
                _ => _char.Hp,
            };
            if (fam == Content.SacrificeFamily.DesperateAttack) _char.Mp = 0;
        }
        SendStats();
        Log.Info($"      {sp.Name} -> sacrifice strike ({fam}) landed={landed} hp->{_char.Hp}/{EffMaxHp}");
        return true;
    }

    // RTK's spellFX local (201/120/119/125 for lethal_strike/desperate_attack/berserk/whirlwind) indexes the
    // SAME shared pcalign->(anim,sound) ladder Content.ZapEffect already carries (rows 119-127/200-204).
    private static (int anim, int sound) SacrificeFx(Content.SacrificeFamily fam) => fam switch
    {
        Content.SacrificeFamily.LethalStrike    => Content.ZapEffect(201, 2),
        Content.SacrificeFamily.DesperateAttack => Content.ZapEffect(120, 2),
        Content.SacrificeFamily.Berserk         => Content.ZapEffect(119, 1),
        Content.SacrificeFamily.Whirlwind       => Content.ZapEffect(125, 1),
        _ => (-1, -1),
    };
    private static int SacrificeAnim(Content.SacrificeFamily fam) => SacrificeFx(fam).anim;
    private static int SacrificeSound(Content.SacrificeFamily fam) => SacrificeFx(fam).sound;

    // RTK rogue/backflow.lua: half the OVERKILL (post-armor damage beyond what was needed to kill) refunds
    // to the caster as HP and as MP, each capped at half of whatever HP/MP the caster had BEFORE this cast.
    private void ApplyBackflow(int overkill, uint preHealth, uint preMagic)
    {
        if (overkill < 1) return;
        int refund = (int)Math.Ceiling(overkill / 2.0);
        int hpCap = (int)Math.Ceiling(preHealth / 2.0);
        int mpCap = (int)Math.Ceiling(preMagic / 2.0);
        _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)Math.Min(refund, hpCap));
        _char.Mp = Math.Min(EffMaxMp, _char.Mp + (uint)Math.Min(refund, mpCap));
    }

    // RTK warrior/overflow.lua: overkill splashes onto up to 4 adjacent-tile mobs (N/S/E/W of the point),
    // evenly split; each further net-armored hit's own overkill recursively re-splashes outward — a rare
    // but real chain-kill cleave. Only mobs are valid splash targets in RTK's own Lua (no PC branch here).
    private void ApplyOverflow(int baseDamage, int srcX, int srcY, Content.SacrificeFamily fam)
    {
        if (baseDamage < 1) return;
        int total = (int)Math.Ceiling(baseDamage * 1.05);
        var offsets = new (int dx, int dy)[] { (0, 1), (0, -1), (1, 0), (-1, 0) };
        var targets = new List<(Mob mob, int x, int y)>();
        foreach (var (dx, dy) in offsets)
        {
            var m = _world.MobAt(_char.Map, srcX + dx, srcY + dy);
            if (m is not null && m.Alive) targets.Add((m, srcX + dx, srcY + dy));
        }
        if (targets.Count == 0) return;
        int share = (int)Math.Ceiling((double)total / targets.Count);
        foreach (var (mob, x, y) in targets)
        {
            int net = Combat.ApplyArmor(share, mob.Ac, floor: -95);
            int overkill = net - mob.Hp;
            _world.TryDamage(_char.Map, mob, net, out bool died, _char.Id);
            BroadcastFx(mob.Id, SacrificeAnim(fam), SacrificeSound(fam));
            ShowDamageResult(mob.Id, mob, died);
            if (died) AwardExp((uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp), killExp: true);
            if (overkill > 0) ApplyOverflow(overkill, x, y, fam);
        }
    }

    // Resolve a spell's PLAYER target: an explicit targetId (client-supplied entity id, same convention as
    // ResolveCastTarget's mob lookup) or else whoever's standing on the tile directly faced.
    private Session? ResolvePcCastTarget(uint? targetId)
    {
        if (targetId is uint id && id != 0)
        {
            var byId = _world.PlayerById(id);
            if (byId is not null) return byId;
        }
        var (fx, fy) = FrontTile();
        return _world.PeerAt(_char.Map, fx, fy);
    }

    // RTK poet/inspiration.lua family (Draw Energy/Harness Power/Combine Focus/Inspiration — 4 reskins, one
    // mechanic): drains a GROUP MEMBER's entire current mana into the caster's own pool (capped at the
    // caster's max). No separate cast cost — the "cost" IS taking the target's mana. Target must be in the
    // caster's own party and not a ghost (RTK: "That cannot save them now").
    private bool CastManaSteal(SpellDef sp, uint? targetId)
    {
        var target = ResolvePcCastTarget(targetId);
        if (target is null) { SendMiniText($"{sp.Name} finds no target."); return false; }
        if (target.IsDead) { SendMiniText("That cannot save them now."); return false; }
        if (_party is null || !ReferenceEquals(target._party, _party)) { SendMiniText("They must be in your group."); return false; }

        uint mana = target._char.Mp;
        target._char.Mp = 0;
        target.SendStats();
        target.SendMiniText($"{Snapshot().Name} casts {sp.Name} on you.");

        _char.Mp = Math.Min(EffMaxMp, _char.Mp + mana);
        BroadcastFx(_char.Id, 6, 22);   // player:sendAction(6,20) + playSound(22) — no explicit Effect.tbl id in the Lua, action-only
        SendStats();
        SendMiniText($"You cast {sp.Name}.");
        Log.Info($"      {sp.Name} -> stole {mana} mana from '{target._char.Name}'");
        return true;
    }

    // RTK poet/inspire.lua family (Inspire/Share Energy/Bestow Power/Release Focus — 4 reskins): tops off
    // ANY other player's mana using the caster's own — no group requirement, but requires the caster hold at
    // least 30 mana to attempt it at all, then gives up to (target's missing mana), capped by whatever the
    // caster actually has (draining the caster to 0 rather than failing outright if they're short).
    private bool CastManaGift(SpellDef sp, uint? targetId)
    {
        const int magicCost = 30;
        var target = ResolvePcCastTarget(targetId);
        if (target is null) { SendMiniText($"{sp.Name} finds no target."); return false; }
        if (ReferenceEquals(target, this)) { SendMiniText("It doesn't work."); return false; }
        if (target.IsDead) { SendMiniText("That cannot save them now."); return false; }
        if (_char.Mp < magicCost) { SendMiniText("Not enough mana."); return false; }

        uint missing = target.EffMaxMp - target._char.Mp;
        uint give = Math.Min(_char.Mp, missing);
        _char.Mp -= give;
        target._char.Mp = Math.Min(target.EffMaxMp, target._char.Mp + give);

        target.SendStats();
        target.SendMiniText($"{Snapshot().Name} casts {sp.Name} on you.");
        SendStats();
        BroadcastFx(_char.Id, 6, 22);
        SendMiniText($"You cast {sp.Name}.");
        Log.Info($"      {sp.Name} -> gave {give} mana to '{target._char.Name}'");
        return true;
    }

    // RTK's `player:flushDuration()` — strips every active timed spell effect in one shot, buffs and
    // debuffs alike. We don't yet track true player-targeted debuffs (only mob-targeted freezes), so this
    // clears every timed mechanic that exists PC-side: stat buffs, rage tiers, the stealth burst, and the
    // Warrior Backstab/Flank stances.
    private void FlushDurations()
    {
        _buffs.Clear();
        _rageUntil = 0;
        _stealthUntil = 0;
        _backstabUntil = 0;
        _flankUntil = 0;
    }

    // RTK poet/dispell.lua family (Dispell/Remove Magic/Return Natural/Restore Balance — 4 reskins): a
    // chance-based full buff/debuff wipe on a targeted player (self-castable too). Success chance is RTK's
    // literal formula: target's effective armor (clamped to [-60,70]) minus a will-scaled protection term,
    // folded into (120+armor)/2, floored at 10%. Fixed 200 mana; no cooldown in the Lua. Player Protection
    // isn't modeled yet (only mobs carry it — see MobProtection), so the term defaults to 0 for a PC target.
    private bool CastCleanse(SpellDef sp, uint? targetId)
    {
        const int cost = 200;
        if (_char.Mp < cost) { SendMiniText("You do not have enough mana."); return false; }
        var target = ResolvePcCastTarget(targetId);
        if (target is null) { SendMiniText($"{sp.Name} finds no target."); return false; }

        int targetArmor = Math.Clamp(target._char.Ac - target.Totals().armor, -60, 70);
        int prot = (int)Math.Floor(((target._char.Will + target.Totals().will) - (_char.Will + Totals().will)) / 10.0);
        int armor = targetArmor - prot;
        int successRate = Math.Max(10, (int)Math.Ceiling((120 + armor) / 2.0));

        _char.Mp -= cost;
        SendStats();
        if (Random.Shared.Next(1, 101) > successRate) { SendMiniText("Something went wrong."); return true; }

        target.FlushDurations();
        target.SendStats();
        if (!ReferenceEquals(target, this)) target.SendMiniText($"{Snapshot().Name} casts {sp.Name} on you.");
        BroadcastFx(_char.Id, 6, 34);
        SendMiniText($"You cast {sp.Name}.");
        Log.Info($"      {sp.Name} -> cleansed '{target._char.Name}' (rate {successRate}%)");
        return true;
    }

    // RTK poet/resurrect.lua family (Resurrect/Return Spirit/Ming-Ken Blessing/Death Undone — 4 reskins):
    // revives a dead/ghost player to full health in place. Fixed 3000 mana, 8s cooldown. RTK also blocks
    // reviving a currently-hostile PvP target (player:canPK) — not modeled since this server has no PvP
    // combat/hostility-flag system yet, so that guard is simply absent rather than faked.
    private bool CastRevive(SpellDef sp, uint? targetId)
    {
        const int cost = 3000;
        if (OnCooldown(sp.Key, out int wait)) { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }
        if (_char.Mp < cost) { SendMiniText("Your will is too weak."); return false; }
        var target = ResolvePcCastTarget(targetId);
        if (target is null) { SendMiniText($"{sp.Name} finds no target."); return false; }
        if (!target.IsDead) { SendMiniText($"{sp.Name} has no effect on the living."); return true; }

        _char.Mp -= cost;
        SetCooldown(sp.Key, 8000);
        target.ReviveAt(target._char.Map, target._char.X, target._char.Y, $"{Snapshot().Name} cast {sp.Name} on you.");
        BroadcastFx(_char.Id, 6, 20);
        SendMiniText($"You cast {sp.Name}.");
        SendStats();
        Log.Info($"      {sp.Name} -> revived '{target._char.Name}'");
        return true;
    }

    // RTK rogue/race.lua family (Race/Spiritual Jump/Leap of Faith/Transport — 4 independently-authored
    // copies of the same mechanic): jump up to 3 tiles in the faced direction, stopping at the last passable
    // tile (same collision test normal movement uses — BlockedMove folds in both the ground pass flag and
    // SObj.tbl directional object-walls). 1 mana, 80s cooldown.
    private bool CastLeap(SpellDef sp)
    {
        const int cost = 1;
        if (OnCooldown(sp.Key, out int wait)) { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }
        if (_char.Mp < cost) { SendMiniText("You do not have enough mana."); return false; }
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        if (md is null) { SendMiniText("It doesn't work here."); return false; }

        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        int dist = 0;
        for (int step = 1; step <= 3; step++)
        {
            int nx = _char.X + dx * step, ny = _char.Y + dy * step;
            if (nx < 0 || ny < 0 || nx >= _char.MapXs || ny >= _char.MapYs || md.BlockedMove(nx, ny, _facing)) break;
            dist = step;
        }
        if (dist == 0) { SendMiniText("There's nowhere to go."); return false; }

        _char.Mp -= (uint)cost;
        SetCooldown(sp.Key, 80000);
        ushort nx2 = (ushort)(_char.X + dx * dist), ny2 = (ushort)(_char.Y + dy * dist);
        string mapName = Content.Maps.TryGetValue(_char.Map, out var mi) ? mi.Name : "";
        EnterMap(_char.Map, _char.MapXs, _char.MapYs, nx2, ny2, mapName);   // re-anchor viewport/redraw at the new spot (same convention as !warp)
        SendStats();
        SendMiniText($"You cast {sp.Name}.");
        Log.Info($"      {sp.Name} -> leapt {dist} tile(s) dir {_facing}");
        return true;
    }

    // RTK rogue/ambush.lua (+ displacement/waylay/reflect reskins, see Content.IsAmbushSpell): "Leap over
    // your enemy to face their back while attacking." No mana cost in the Lua — the caster teleports to the
    // tile directly behind the faced target (relative to the TARGET's own facing, not the caster's) and
    // re-faces to match it, which lines up exactly with Combat.IsBehindTarget's existing unconditional
    // positional-backstab bonus (attacker/target facing the same way, attacker on the target's blind side)
    // — so the follow-up swing below gets that x2 "for free", same as a real sneak-up. Only ever targets a
    // world mob (RTK's own getTargetFacing prioritizes BL_MOB first; PC/NPC targets aren't meaningful here
    // — this server has no PvP melee path yet). RTK paces reuse with player.ambushTimer (attackSpeed-
    // derived, not modeled) — a short fixed cooldown substitutes so it can't be chain-spammed.
    private bool CastAmbush(SpellDef sp)
    {
        if (OnCooldown(sp.Key, out int wait)) { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }
        var (fx, fy) = FrontTile();
        var mob = _world.MobAt(_char.Map, fx, fy);
        if (mob is null) { SendMiniText($"{sp.Name} finds no target."); return false; }

        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        byte behindDir = (byte)((mob.Dir + 2) & 3);
        var (bx, by) = StepDir(mob.X, mob.Y, behindDir);
        bool blocked = bx < 0 || by < 0 || bx >= _char.MapXs || by >= _char.MapYs ||
                       (md?.BlockedMove(bx, by, behindDir) ?? false) || _world.PeerAt(_char.Map, bx, by) is not null;
        if (blocked) { SendMiniText($"{sp.Name} finds no opening."); return false; }

        SetCooldown(sp.Key, 3000);
        _facing = mob.Dir;
        string mapName2 = Content.Maps.TryGetValue(_char.Map, out var mi2) ? mi2.Name : "";
        EnterMap(_char.Map, _char.MapXs, _char.MapYs, (ushort)bx, (ushort)by, mapName2);   // re-anchor viewport at the target's back
        SendAction(_char.Id, type: 1, time: 8, param: 0);
        _world.Broadcast(_char.Map, p => p.ActionOver(_char.Id, 1, 8, 0), except: this);

        var (dmg, crit) = PlayerSwingDamage(mob);
        if (_world.TryDamage(_char.Map, mob, dmg, out bool died, _char.Id))
        {
            var weapon = _char.Equipment.FirstOrDefault(e => e.Slot == 1);
            if (weapon is not null) DeductDura(weapon);
            ShowDamageResult(mob.Id, mob, died, crit ? (byte)0xFF : HitCritByte, (byte)Math.Clamp(_hitSfx, 0, 255));
            Log.Info($"      {sp.Name} -> leapt behind '{mob.Name}' and struck for {dmg}{(crit ? " (CRIT)" : "")}");
            if (died)
            {
                uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
                AwardExp(reward, killExp: true);
                SendMessage($"You defeated {mob.Name}. (+{reward} exp)");
                TallyKill(mob);
            }
        }
        return true;
    }

    // One step from (x,y) in direction dir (0=N/1=E/2=S/3=W — same convention as FrontTile/_facing/Mob.Dir).
    private static (int x, int y) StepDir(int x, int y, int dir) => (dir & 3) switch
    {
        0 => (x, y - 1),
        1 => (x + 1, y),
        2 => (x, y + 1),
        _ => (x - 1, y),
    };

    // RTK rogue/filch.lua family (see Content.IsGroundLootSpell): mana is spent and the "I'll take that"
    // bark plays regardless of what's on the tile (RTK does both unconditionally, before ever looking at
    // the floor) — only the actual grab is conditional on the tile being empty of other players.
    private bool CastGroundLoot(SpellDef sp, SpellFx fx, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMiniText("Your will is too weak."); return false; }
        _char.Mp -= (uint)mana;
        SendStats();
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText("I'll take that.");

        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        int tx = _char.X + dx, ty = _char.Y + dy;
        if (tx < 0 || ty < 0 || tx >= _char.MapXs || ty >= _char.MapYs) return true;

        if (_world.PeerAt(_char.Map, tx, ty) is not null) return true;   // someone's standing on it — hands off

        var gi = _world.PickUp(_char.Map, tx, ty);
        if (gi is null) return true;
        if (gi.ItemId < 0) { _char.Coins += (uint)gi.Amount; SendStats(); MarkDirty(); return true; }   // coins -> purse
        var def = Content.ItemById(gi.ItemId);
        if (def is null) return true;
        if (!GiveItem(def, gi.Amount, gi.Dura, gi.CustomName))
            // pack full — put it straight back rather than losing it (same recovery as HandlePickup)
            _world.DropItem(_char.Map, new GroundItem { Id = _world.AllocateItemId(), ItemId = gi.ItemId,
                X = (ushort)tx, Y = (ushort)ty, Amount = gi.Amount, Dura = gi.Dura, Graphic = gi.Graphic, CustomName = gi.CustomName });
        Log.Info($"      {sp.Name} -> grabbed item {gi.ItemId} from ({tx},{ty})");
        return true;
    }

    // RTK warrior/watchful_eye.lua family + dog/spot_traps.lua (see Content.IsSpotTrapsSpell): reveals every
    // hidden rogue-trap NPC (dart/snare/repeating/flash/spear/poison/death/sleep) within 15 tiles (RTK
    // seeSpotTraps: distanceSquare(player, npc, 15)) by drawing item 99 ("wooden sword" — RTK's own marker,
    // its Lua comment calls it a "steel dagger" but the actual dropped id is 99 either way) on each trap's
    // tile — via ShowGroundItem directly (not World.DropItem), so only the caster's own client ever sees it,
    // matching RTK's addTrapSpotters/getTrapSpotters per-player visibility tagging. No removal call exists
    // yet (RTK's own removeSpotTraps is a separate GM-style command) — same "stays until you leave/re-enter
    // the map" behaviour the Lua describes ("will remain on screen for as long as you want").
    private bool CastSpotTraps(SpellDef sp, SpellFx fx, int mana)
    {
        if (fx.Aether > 0 && OnCooldown(sp.Key, out int wait)) { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }

        _char.Mp -= (uint)mana;
        SendStats();
        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SetCooldown(sp.Key, fx.Aether > 0 ? fx.Aether : 25000);   // RTK setAether(key, 25000) for the warrior family — missing from the export, spot_traps' own row already has a real aether

        var traps = _world.TrapsNear(_char.Map, _char.X, _char.Y, 15);
        ushort markerIcon = Content.ItemById(99)?.Icon ?? 0;
        foreach (var t in traps)
            ShowGroundItem(new GroundItem { Id = _world.AllocateItemId(), ItemId = 99, X = t.X, Y = t.Y, Graphic = markerIcon });

        SendMiniText(traps.Length > 0 ? $"You sense {traps.Length} hidden trap{(traps.Length == 1 ? "" : "s")} nearby." : "You sense nothing nearby.");
        Log.Info($"      {sp.Name} -> revealed {traps.Length} trap(s) near ({_char.X},{_char.Y})");
        return true;
    }

    // RTK rogue/judge.lua + spy.lua (see Content.IsDivinationSpell): a text popup showing another player's
    // class/name/level/title/might/will/grace — the spy variant appends their full inventory. 30 mana flat,
    // no cooldown in the Lua. Sent to the CASTER (this session), not the target — it's an inspect, not a
    // debuff, so the target isn't notified.
    private bool CastDivination(SpellDef sp, SpellFx fx, uint? targetId, bool showInventory)
    {
        const int mana = 30;
        if (_char.Mp < mana) { SendMiniText("You do not have enough mana."); return false; }
        var target = ResolvePcCastTarget(targetId);
        if (target is null) { SendMiniText($"{sp.Name} finds no target."); return false; }

        // Judge family: target must be STRICTLY lower level. Spy family: equal level is also allowed.
        // (`target.level >= player.level` fails vs `target.level > player.level` fails — a real distinction
        // in the Lua, not a typo.)
        bool allowed = showInventory ? target._char.Level <= _char.Level : target._char.Level < _char.Level;
        if (!allowed) { SendMiniText("Target player must be lower level than you for you to use this spell."); return false; }

        _char.Mp -= mana;
        SendStats();

        var tc = target._char;
        var text = new System.Text.StringBuilder();
        text.Append(tc.ClassName).Append(' ').Append(tc.Name).Append("     Level ").Append(tc.Level).Append('\n');
        text.Append(tc.Title ?? "").Append('\n');
        text.Append("Might: ").Append(target.EffMight)
            .Append(" Will: ").Append(tc.Will + target.Totals().will)
            .Append(" Grace: ").Append(tc.Grace + target.Totals().grace).Append('\n');
        if (showInventory)
        {
            text.Append("Items: ");
            foreach (var it in tc.Inventory)
            {
                var def = Content.ItemById(it.ItemId);
                if (def is not null) text.Append(def.Name).Append(' ');
            }
        }

        BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendScriptMessageP(tc.Id, text.ToString(), DialogPortrait.None, prev: false, next: false);
        Log.Info($"      {sp.Name} -> divined '{tc.Name}' (inventory={showInventory})");
        return true;
    }

    // RTK rogue/set_X_trap.lua family (see Content.TrapSpellFor/IsTrapDispatcher): places a hidden hazard
    // on the caster's OWN current tile (RTK: player.x/player.y at cast time, never the faced tile) that
    // fires once a mob steps onto it (World.Tick's movement pass). Bark + sound play unconditionally, same
    // as every other trap spell in the source.
    private bool CastTrap(SpellDef sp, Content.TrapKind kind, SpellFx? fx, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        _world.PlaceTrap(_char.Map, _char.X, _char.Y, Content.TrapWireKind(kind), _char.Id);
        SendStats();
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText("You set a trap!");   // RTK dart_trap.lua: sendMinitext, exact wording
        Log.Info($"      {sp.Name} -> placed {kind} trap at ({_char.X},{_char.Y})");
        return true;
    }

    // RTK rogue/bladestorm_trap.lua (see Content.IsBladestormTrap): places a visible step-triggered decoy on
    // the caster's OWN tile that detonates a facing-cone AoE the instant ANYTHING steps onto it — a mob
    // triggers it from World.Tick's movement loop (TriggerTrapLocked's "bladestorm" case); a player triggers
    // it from HandleWalk via World.CheckPlayerTrapTrigger. Real RTK also drains the owner 5000 mana/tick via
    // the decoy's own heartbeat for up to 21s — NOT ported (the exact drain/early-deletion formula isn't in
    // the captured Lua), so this is the flat 1520 upfront cost only; flagged, not silently dropped.
    private bool CastBladestormTrap(SpellDef sp)
    {
        const int mana = 1520, cooldownMs = 125000, lifetimeMs = 21000;
        if (OnCooldown(sp.Key, out int wait)) { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        SetCooldown(sp.Key, cooldownMs);
        _world.PlaceTrap(_char.Map, _char.X, _char.Y, "bladestorm", _char.Id, Environment.TickCount64 + lifetimeMs);
        SendStats();
        var fx = Content.FxFor(sp);
        if (fx is not null) BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));
        SendMiniText("You set a devastating trap!");
        Log.Info($"      {sp.Name} -> bladestorm trap placed at ({_char.X},{_char.Y}), expires in {lifetimeMs}ms");
        return true;
    }

    /// <summary>Called by World.CheckPlayerTrapTrigger when WE step on our own (or anyone's) bladestorm decoy.
    /// RTK's damage is ONE number — floor(health*0.5) + calculateDamage(35000 netted against our own armor,
    /// Combat.ApplyArmor mirrors calculateDamage's identical deduction formula) — applied both to us and to
    /// every mob the facing cone catches. Capped here to leave at least 1 HP on OURSELVES only: a trap
    /// tripped mid-walk has no death-flow of its own to hook (unlike a real melee/spell kill), same
    /// "self-cost, never actually lethal" precedent as CastSacrificeStrike. Returns the UNCAPPED value so the
    /// cone's mob targets take the real RTK number, not our clamped one.</summary>
    public int ApplyBladestormSelfDamage()
    {
        int effectiveAc = _char.Ac - Totals().armor;
        int raw = (int)(_char.Hp * 0.5) + Combat.ApplyArmor(35000, effectiveAc, floor: -80);
        int applied = Math.Min(raw, (int)_char.Hp - 1);
        if (applied > 0) _char.Hp -= (uint)applied;
        SendStats();
        SendMiniText("AIEE~! A trap goes off right beneath you!");
        return raw;
    }

    // RTK Poet "Call of the Wild" pet family (see Content.PetSpellFor/PetCapFor): spawns a real, correctly
    // statted shared-world Mob owned by the caster (SummonWorldMob — same helper !summon uses), one tile
    // ahead if that tile is free, else on the caster's own tile (RTK cotw_SpawnSetThreat's exact fallback).
    // Expires 300s later (World.Tick), uncapped duration extension on recast — just another pet, subject to
    // the same live-pet cap. Combat-assist/threat-transfer isn't ported (see Mob.OwnerId's doc).
    private bool CastPetSummon(SpellDef sp, string mobKey, int mana, int cooldownMs)
    {
        var def = Content.MobByKey(mobKey);
        if (def is null) { SendMiniText("Something went wrong."); return false; }
        if (cooldownMs > 0 && OnCooldown(sp.Key, out int wait)) { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }
        if (mana > 0 && _char.Mp < (uint)mana) { SendMiniText("Not enough mana."); return false; }

        int cap = Content.PetCapFor(_char.Level);
        if (_world.PetCountFor(_char.Map, _char.Id) >= cap) { SendMiniText("You cannot summon any more creatures right now."); return false; }

        int dx = _facing switch { 1 => 1, 3 => -1, _ => 0 };
        int dy = _facing switch { 0 => -1, 2 => 1, _ => 0 };
        int fx2 = _char.X + dx, fy2 = _char.Y + dy;
        var md = MapData.For(_char.Map, _char.MapXs, _char.MapYs);
        bool frontFree = fx2 >= 0 && fy2 >= 0 && fx2 < _char.MapXs && fy2 < _char.MapYs
                          && (md is null || !md.BlockedMove(fx2, fy2, _facing))
                          && _world.MobAt(_char.Map, fx2, fy2) is null
                          && _world.PeerAt(_char.Map, fx2, fy2) is null;
        ushort sx = frontFree ? (ushort)fx2 : _char.X, sy = frontFree ? (ushort)fy2 : _char.Y;

        if (mana > 0) _char.Mp -= (uint)mana;
        if (cooldownMs > 0) SetCooldown(sp.Key, cooldownMs);
        SendStats();

        var mob = SummonWorldMob(def.Look, sx, sy, def.Name, def.Hp, dir: (byte)((_facing + 2) & 3), color: def.Color,
                                  exp: def.Exp, moveTime: def.MoveTime, key: def.Key, def: def);
        mob.OwnerId = _char.Id;
        mob.PetExpiresAt = Environment.TickCount64 + 300_000;
        SendMiniText($"You summon a {mob.Name}.");
        Log.Info($"      {sp.Name} -> summoned pet '{mob.Name}' ({mob.Id}) for player {_char.Id} at ({sx},{sy})");
        return true;
    }

    // Debuff: paralyze/sleep the targeted mob — freeze its wandering for the RTK duration, subject to the spell's
    // hit chance. PC targets are immune here (no PvP). Damage-less crowd control.
    private bool CastDebuff(SpellDef sp, SpellFx fx, uint? targetId, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        var mob = ResolveCastTarget(targetId);
        if (mob is null) { SendMiniText($"{sp.Name} finds no target."); return false; }
        if (sp.CanFail && RollDeflect(mob)) { SendMiniText("The magic has been deflected."); return true; }

        double chance = string.IsNullOrEmpty(fx.Chance) ? 100 : Formula.Eval(fx.Chance, SpellVars(mob));
        _char.Mp -= (uint)mana;
        if (chance < 100 && Random.Shared.Next(100) >= chance) { SendMiniText($"{sp.Name} fails to take hold."); return true; }

        int durMs = fx.DurationMs > 0 ? fx.DurationMs : 20000;
        mob.FrozenUntil = Environment.TickCount64 + durMs;
        BroadcastFx(mob.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // debuff graphic + sound
        SendMiniText($"Your {sp.Name} holds {mob.Name} for {durMs / 1000}s.");
        Log.Info($"      {sp.Name} -> mob {mob.Id} '{mob.Name}' frozen {durMs}ms ({fx.Debuff})");
        return true;
    }

    // Cure: RTK removes a category of durations from the target. We clear the caster's own active debuffs/buffs
    // (we don't yet carry negative status), so functionally it's a "dispel my timers" + mana spend.
    private bool CastCure(SpellDef sp, SpellFx fx, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMiniText("You do not have enough mana."); return false; }
        _char.Mp -= (uint)mana;
        SendMiniText($"You cast {sp.Name}.");
        return true;
    }

    // Utility / Summon / Teleport / Dialog: no faithful model yet — spend the real mana and acknowledge so the
    // cast isn't a silent no-op. These get bespoke cases later as they're wanted.
    private bool CastMisc(SpellDef sp, int mana)
    {
        if (_char.Mp < (uint)mana) { SendMiniText($"Not enough mana to cast {sp.Name}."); return false; }
        _char.Mp -= (uint)mana;
        SendMiniText($"You cast {sp.Name}.");
        return true;
    }

    // Gateway destinations are data-driven (data/game-data/GatewayGates.csv -> Content.GatewayRegions): region
    // -> the kingdom's city map + the four gate spawn boxes. Casting Gateway warps you to a RANDOM tile inside
    // the box for the gate you answered (N/E/S/W), on the region's city map. Coords are 1:1 with RTK
    // (gateway.lua); only the four playable kingdoms (regions 0-3) have gates. Hot-reloads via !reload.

    // Gateway: teleport to a gate of the caster's kingdom. The N/E/S/W answer to the spell's question picks the
    // gate; the region (Content.RegionOf) picks the city. Faithful to gateway.lua incl. its guards (dead can't
    // cast, warp-locked maps say "It doesn't work here", non-kingdom maps "Cannot find any gates!") and the
    // per-gate random landing spread. No mana cost — RTK's gateway only calls canCast (a state check), not a
    // mana debit. On success we re-run the full map-entry sequence via EnterMap so the client redraws.
    // A virtual "npc" purely for the propose/marriage dialog headers (never spawned or looked up). Distinct
    // sentinel from F1 (0xFFFFFFFF) / subpath-chat (0xFFFFFFFE) / Trade (0xFFFFFFFD) / WorldMap (0xFFFFFFFC).
    private static readonly Mob MarriageVirtualNpc = new(0xFFFFFFFB, 0, 0, 0, "Cupid", 1);

    // Propose (RTK Spells/common/propose.lua): a skill-type spell (SplType 5 — no native typed-answer/target
    // wire arg) whose real interaction is entirely a scripted dialog (RTK inputSeq/menuSeq), the same class
    // of primitive an NPC uses. CastPropose just validates + kicks the async flow off; RunProposeAsync does
    // the actual asking, and PromptProposal (below) runs on the TARGET's own session once found.
    private bool CastPropose(SpellDef sp)
    {
        if (HasLegend("engaged") || HasLegend("married"))
        {
            ForgetOneSpell(sp.Id);   // RTK: a stray cast of a spell you shouldn't have anymore just cleans it up
            SendMiniText("You are already committed to someone else!");
            return false;
        }
        _ = RunProposeAsync(sp);
        return true;   // the cast anim plays regardless; the real outcome resolves async below
    }

    private async Task RunProposeAsync(SpellDef sp)
    {
        string? name = await DlgInput(MarriageVirtualNpc, "What is the name of your beloved?");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.Equals(name, _char.Name, StringComparison.OrdinalIgnoreCase))
        { SendMiniText("You can't marry yourself."); return; }

        var target = _world.FindPlayer(name);
        if (target is null) { SendMiniText("Player is not valid or not online."); return; }
        // RTK checks the beloved is physically nearby (getObjectsInArea); same-map is our practical proxy
        // for "nearby", matching the gate Trade already uses for its own "must be present" check.
        if (target.CharMap != CharMap)
        { SendMiniText("Your beloved must be near you when you make this commitment."); return; }
        if (target.HasLegend("engaged") || target.HasLegend("married"))
        { SendMiniText($"{target.Snapshot().Name} is already committed to someone else!"); return; }
        if (target.CountItem("engagement_ring") < 1)
        { SendMiniText("You have not given them an engagement ring yet."); return; }

        ForgetOneSpell(sp.Id);
        _ = target.PromptProposal(this);
    }

    /// <summary>Runs on the PROPOSEE's own session (called cross-session — same pattern as Trade/Party):
    /// shows the accept/decline prompt and, on accept, engages both parties.</summary>
    internal async Task PromptProposal(Session proposer)
    {
        int choice = await DlgMenu(MarriageVirtualNpc,
            $"{proposer.Snapshot().Name} proposes marriage. Do you accept?",
            new[] { "Yes! I am madly in love.", "I must decline." });

        string me = Snapshot().Name, them = proposer.Snapshot().Name;
        if (choice == 1)
        {
            AddLegend($"Engaged to {them} ({Character.GameDate})", "engaged", 6, 1);
            proposer.AddLegend($"Engaged to {me} ({Character.GameDate})", "engaged", 6, 1);
            TakeItem("engagement_ring", 1);
            long timer = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 259200;   // RTK: 3-day cool-down
            SetEngaged(them, isProposee: true, timer);
            proposer.SetEngaged(me, isProposee: false, timer);
            proposer.SendMiniText($"{me} accepts!");
            SendMiniText($"You are now engaged to {them}!");
        }
        else
        {
            proposer.SendMiniText($"{me} regretably must decline.");
            SendMiniText("What but love can last forever?");
        }
    }

    /// <summary>Runs the wedding ceremony against this player's current fiancé (RTK
    /// <c>ChapelNpc.marriageprompt</c>): asks the FIANCÉ (the original proposer) for their "I do", then
    /// marries both on accept. Returns the message the Chapel should show, or null if the outcome was
    /// already messaged directly (a decline).</summary>
    internal async Task<string?> RunMarriageCeremony()
    {
        var fiance = _world.FindPlayer(_char.Fiance);
        if (fiance is null) return "Both parties must be present for the ceremony to commence";
        if (fiance.HasLegend("married")) return "The person is already married.";

        string me = _char.Name, them = fiance.Snapshot().Name;
        int choice = await fiance.DlgMenu(MarriageVirtualNpc,
            $"Do you, {them} take {me} as your partner?",
            new[] { "I do. (you will lose much xp if you divorce)", "I don't." });

        if (choice != 1)
        {
            SendMiniText($"{them} regretably must decline.");
            fiance.SendMiniText("What but love can last forever?");
            return null;
        }

        string date = Character.GameDate;
        AddLegend($"Married to {them} ({date})", "married", 6, 1);
        fiance.AddLegend($"Married to {me} ({date})", "married", 6, 1);
        RemoveLegend("engaged"); fiance.RemoveLegend("engaged");
        ClearEngagement(); fiance.ClearEngagement();
        SetSpouse(them); fiance.SetSpouse(me);
        GiveRewardItem("love", 1); fiance.GiveRewardItem("love", 1);
        SendMiniText("I now pronounce you (married)");
        fiance.SendMiniText("I now pronounce you (married)");
        return "Congratulations! You are both now married.";
    }

    private bool CastGateway(SpellDef sp, string? answer)
    {
        if (_char.Hp == 0) { SendMiniText("Spirits cannot use Gateway."); return false; }
        if (!Content.WarpOut(_char.Map)) { SendMiniText("It doesn't work here."); return false; }

        int region = Content.RegionOf(_char.Map);
        if (!Content.GatewayRegions.TryGetValue(region, out var r) || !Content.Maps.TryGetValue(r.Map, out var map))
        { SendMiniText("Cannot find any gates!"); return false; }

        // RTK keys on the answer's first letter (string.sub(q,1,1)). Take the first ASCII letter so a stray
        // framing byte or leading space can't swallow the direction.
        char dir = (answer ?? "").ToLowerInvariant().FirstOrDefault(char.IsLetter);
        if (!r.Gates.TryGetValue(dir, out var box))
        { SendMiniText("Which gate? Answer North, East, South or West."); return false; }

        ushort x = (ushort)Random.Shared.Next(box.Xlo, box.Xhi + 1);
        ushort y = (ushort)Random.Shared.Next(box.Ylo, box.Yhi + 1);
        string gate = dir switch { 'n' => "North", 'e' => "East", 'w' => "West", 's' => "South", _ => "" };

        EnterMap(map.Id, map.Xs, map.Ys, x, y, map.Name);
        SendSound(708, _char.Id);   // confirmed live 2026-07-27; self-only, teleport isn't visible to peers anyway
        SendMiniText($"You have arrived at {gate} Gate of {r.City}.");
        Log.Info($"      Gateway -> region {region} {r.City} {gate} gate: map {map.Id} ({x},{y})");
        return true;
    }

    // Return (common/return.lua): warps home to the same destination as the yellow_scroll/qui_hyang item's
    // "warphome" effect (ReturnToInn -- see ApplyItemEffect's "warphome" case). RTK's script costs 30 mana
    // and checks warpOut before warping ("That does not work here" verbatim); its handful of hardcoded
    // per-map "Fizzle." checks (arena/instance ids like 3010/3011/3034-39/3042/666) aren't ported --
    // Content.WarpOut already carries the real RTK per-map warp-out flag those would largely duplicate, and
    // this server never loads those unrenderable instance maps in the first place (see Content.Warps' doc).
    private bool CastReturn()
    {
        const uint cost = 30;
        if (_char.Mp < cost) { SendMiniText("You do not have enough mana."); return false; }
        if (!Content.WarpOut(_char.Map)) { SendMiniText("That does not work here."); return false; }

        _char.Mp -= cost;
        ReturnToInn();
        SendStats();
        return true;
    }

    // RTK Player.returnToInn (player.lua:4607): "home" for Return / yellow_scroll / qui_hyang is a RANDOM
    // tavern in your nation (each has a bed to wake up in), NOT the nation's home-city interior — that's
    // CharacterFactory.HomeCityFor, which stays the fresh-character spawn + Silver-Thread revive point (it
    // used to double as the return target, which is why Return dumped you at Jadespear). Country->tavern
    // lists + the (4,5)/(4,6) arrival tiles are verbatim from RTK; nations without their own tavern set
    // (Neutral/Shilla/Jinhan/Paekjae/Kaya) fall back to Kugnae's, matching RTK's own `country > 3 -> Ginger`.
    // Tavern return tiles are data-driven (data/game-data/Inns.csv -> Content.Inns), grouped Kugnae/Buya/
    // Nagnang; the nation->group choice (incl. RTK's country>3 -> Kugnae default) stays here. Hot-reloads via
    // !reload.
    private void ReturnToInn()
    {
        string group = _char.Nation switch
        {
            2 => "Buya",
            3 => "Nagnang",
            _ => "Kugnae",     // Kugnae + any nation without its own tavern set (RTK's country>3 default)
        };
        var inns = Content.Inns.GetValueOrDefault(group);
        if (inns is { Count: > 0 })
        {
            var pick = inns[Random.Shared.Next(inns.Count)];
            if (Content.TryMap(pick.Map, out var hm)) { EnterMap(hm.Id, hm.Xs, hm.Ys, pick.X, pick.Y, hm.Name); return; }
        }

        // Safety net: if a nation's tavern map isn't loaded, fall back to the home city so the warp never
        // silently no-ops (all six 4.95 tavern maps are present, so this only guards future data drift).
        var (fm, fx, fy) = CharacterFactory.HomeCityFor(_char.Nation);
        if (Content.TryMap(fm, out var fmap)) EnterMap(fmap.Id, fmap.Xs, fmap.Ys, fx, fy, fmap.Name);
    }

    // Fallback for spells with NO export row: the old keyword classifier over the name (Damage/Heal/Buff/Utility)
    // with the shared magic-damage base formula. Kept so an unmatched identifier still does something sensible.
    private bool ApplyCastGeneric(SpellDef sp, uint? targetId)
    {
        const uint cost = 5;
        if (_char.Mp < cost) { SendMiniText($"Not enough mana to cast {sp.Name}."); return false; }
        var eq = Totals();
        int power = Math.Max(1, 1 + (_char.Will + eq.will) * 4 + (_char.Grace + eq.grace) * 3);
        switch (Content.EffectOf(sp))
        {
            case Content.SpellEffect.Heal:
            {
                _char.Mp -= cost;
                uint before = _char.Hp;
                _char.Hp = Math.Min(EffMaxHp, _char.Hp + (uint)power);
                uint gain = _char.Hp - before;
                BroadcastFx(_char.Id, 5, 4);   // generic unaligned heal graphic + sound
                SendMiniText(gain > 0 ? $"{sp.Name} restores {gain} HP." : $"You cast {sp.Name} (already at full HP).");
                return true;
            }
            case Content.SpellEffect.Damage:
            {
                var mob = ResolveCastTarget(targetId);
                if (mob is null) { SendMiniText($"{sp.Name} finds no target."); return false; }
                _char.Mp -= cost;
                if (_world.TryDamage(_char.Map, mob, power, out bool died, _char.Id))
                {
                    BroadcastFx(mob.Id, 4, 56);   // generic unaligned zap graphic + sound
                    ShowDamageResult(mob.Id, mob, died);   // 0x13: over-head HP bar (empty bar + delayed despawn on death)
                    if (died)
                    {
                        uint reward = (uint)(mob.Exp > 0 ? mob.Exp : mob.MaxHp);
                        AwardExp(reward, killExp: true);
                        SendMessage($"Your {sp.Name} destroys {mob.Name}! (+{reward} exp)");
                    }
                    else SendMessage($"Your {sp.Name} hits {mob.Name} for {power}.");
                }
                return true;
            }
            case Content.SpellEffect.Buff:
                _char.Mp -= cost;
                SendMiniText($"You invoke {sp.Name} — you feel its power.");
                return true;
            default:
                _char.Mp -= cost;
                SendMiniText($"You cast {sp.Name}.");
                return true;
        }
    }

    // Per-spell cooldowns ("aether"), keyed by spell identifier -> earliest next-cast tick (ms). Mirrors RTK's
    // player:setAether. Lightweight and session-local (resets on relog, like most timers here).
    private readonly Dictionary<string, long> _aether = new();
    private bool OnCooldown(string key, out int secondsLeft)
    {
        secondsLeft = 0;
        if (_aether.TryGetValue(key, out var until) && Environment.TickCount64 < until)
        { secondsLeft = (int)Math.Ceiling((until - Environment.TickCount64) / 1000.0); return true; }
        return false;
    }
    private void SetCooldown(string key, int ms) => _aether[key] = Environment.TickCount64 + ms;

    // Mana-battery family (Invoke / Spirit's Power / Life Force / Gather Magic). Ported verbatim from RTK
    // rtklua/Accepted/Spells/{mage,poet}/invoke.lua: needs ≥30 current mana; HP cost = 40% of MAX mana, with
    // HP floored at 100 (never below); then refill mana to FULL. 22s aether (cooldown).
    private bool CastManaBattery(SpellDef sp)
    {
        const uint MinMana = 30;
        if (OnCooldown(sp.Key, out int wait)) { SendMiniText($"{sp.Name} isn't ready yet ({wait}s)."); return false; }
        if (_char.Mp < MinMana) { SendMiniText("Not enough mana."); return false; }

        uint healthCost = (uint)(EffMaxMp * 0.4);
        uint before = _char.Hp;
        _char.Hp = (long)_char.Hp - healthCost < 100 ? 100u : _char.Hp - healthCost;   // RTK: floor at 100
        _char.Mp = EffMaxMp;                                                            // refill to full
        SetCooldown(sp.Key, 22000);

        uint lost = before > _char.Hp ? before - _char.Hp : 0;
        if (Content.FxFor(sp) is { } fx)
            BroadcastFx(_char.Id, Content.EffectAnim(fx, sp.PathId), Content.EffectSound(fx, sp.PathId));   // Invoke graphic + sound
        SendMiniText($"You cast {sp.Name}.");
        Log.Info($"      {sp.Name}: -{lost} HP (cost {healthCost}, floor 100), MP -> full {_char.Mp}/{EffMaxMp}");
        return true;
    }

    // The world mob a cast should affect: an explicit target id if the client sent one, else the mob on the
    // tile the caster faces (like melee). World mobs only — session-local debug dummies aren't spell targets.
    private Mob? ResolveCastTarget(uint? targetId)
    {
        if (targetId is uint id && id != 0)
        {
            var byId = _world.MobById(_char.Map, id);
            if (byId is not null) return byId;
        }
        var (fx, fy) = FrontTile();
        return _world.MobAt(_char.Map, fx, fy);
    }

}
