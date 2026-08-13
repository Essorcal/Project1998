"""Generate game-data/MobSpells.csv — every creature that throws a spell at you, from the RTK AI scripts.

RTK's spell scripts are caster-agnostic (`peck.cast(block, target)` takes a mob as readily as a player), so a
casting mob is just an AI hook that calls one. Three sources:

* `AI/normal_mobs/raven.lua`      — 1-in-5 peck (blind) on attack, if not already pecked.
* `AI/normal_mobs/buya_library_mob.lua` — 1-in-10 venom on attack.
* `AI/mob_ai_mythic.lua`          — the boss rotation, gated on `os.time() % 15 == 0` within 5 tiles, then a
  1-in-10 pick: vex/scourge, venom, blind, else the lightning line. WHICH spell each boss gets depends on its
  tier, which the boss's own table declares as the third argument to `mob_ai_mythic.move(mob, target, N)` —
  so the tiers are read out of the 72 boss tables rather than guessed.

Damage/duration numbers come from the spells' own SpellParams/spell_effects rows where we have them, and are
otherwise the conservative floor noted per row. They are the one part of this that is tuning rather than
extraction, which is why they live in a CSV you can edit without touching code.

It also writes game-data/MobBosses.csv, the rest of `mob_ai_mythic`: a mythic boss that takes a killing blow
may HEAL instead of dying (by tier: 500 / 5000 / 15000), it shrugs off paralysis on its own, and at full mana
it goes into Last Stand and regenerates while it runs. Same tier source as the spells — the boss's own table
declares it as the third argument to `mob_ai_mythic.on_attacked(mob, attacker, N)`.

Output columns:
  MobSpells.csv  MobKey,Name,Effect,Chance,EveryMs,Range,Amount,Stat,Category,DurationMs,Anim,Sound,Say
  MobBosses.csv  MobKey,HealAmount,HealChance,ParaBreakChance,LastStandMs,Anim,Sound
"""
import csv, re, glob
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "game-data" / "MobSpells.csv"
AI = ROOT / "RTK-Server" / "rtklua" / "Accepted" / "AI"

SUMMON = "** summons power **"     # RTK mob_ai_mythic's own line, spoken before every boss cast

# tier -> (curse spell, lightning spell, lightning damage). RTK: level 0 vex/ion, 1 scourge/call_lightning,
# 2+ scourge/thunder_touch. Damage rises with the tier the same way the boss's own HP pool does.
TIER = {
    0: ("Vex",     "Ion",            120),
    1: ("Scourge", "Call lightning", 260),
    2: ("Scourge", "Thunder touch",  480),
}

def mythic_tiers(hook: str = "move") -> dict[str, int]:
    """Every mythic boss key -> its tier, read from `mob_ai_mythic.<hook>(mob, …, N)` in its own table.
    The bosses pass a tier per hook and they are NOT always the same number (mythic_rat is 0/0/2 across
    on_attacked/move/attack), so the caller says which one it wants rather than assuming."""
    args = "mob, target" if hook == "move" else "mob, attacker"
    tiers = {}
    for f in glob.glob(str(AI / "bosses" / "mythic_bosses" / "*.lua")):
        text = Path(f).read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r"^(\w+)\s*=\s*\{", text, re.M):
            end = text.find("\n}", m.end())
            body = text[m.end(): end if end > 0 else len(text)]
            mm = re.search(r"mob_ai_mythic\.%s\(%s,\s*(\d+)\)" % (hook, args.replace(", ", r",\s*")), body)
            if mm:
                tiers[m.group(1)] = int(mm.group(1))
    return tiers

# tier -> heal on a would-be killing blow (RTK's healAmount ladder: 500 / 5000 / 15000).
BOSS_HEAL = {0: 500, 1: 5000, 2: 15000}

def valid_mobs() -> set[str]:
    with (ROOT / "game-data" / "mobs.csv").open(encoding="utf-8", errors="replace") as f:
        return {r["Identifier"] for r in csv.DictReader(f)}

def main():
    mobs = valid_mobs()
    rows = []

    def add(mob, name, effect, chance, every, rng, amount=0, stat="", cat="", dur=0, anim=0, sound=0, say=""):
        rows.append(dict(MobKey=mob, Name=name, Effect=effect, Chance=chance, EveryMs=every, Range=rng,
                         Amount=amount, Stat=stat, Category=cat, DurationMs=dur, Anim=anim, Sound=sound, Say=say))

    # ---- normal mobs -------------------------------------------------------------------------------
    # Peck: RTK sets target.blind for 5s. We have no player-blind state, so this occupies the `blinds`
    # slot and announces itself (see Session.MobSpells.cs) — cures clear it, nothing else changes.
    for raven in ("raven", "man_shik_raven"):
        if raven in mobs:
            add(raven, "Peck", "blind", chance=5, every=6000, rng=1, cat="blinds", dur=5000, anim=39)
    if "buya_library_mob" in mobs:
        add("buya_library_mob", "Venom", "poison", chance=10, every=12000, rng=1,
            amount=40, dur=20000, anim=6)   # 40 dps for 20s — the low end of the venom family

    # ---- mythic bosses -----------------------------------------------------------------------------
    # One row per branch of RTK's 1-in-10 pick, in its order: the roll walks them and takes the first hit,
    # which reproduces the same distribution (1/10 curse, 1/10 venom, 1/10 blind, the rest lightning).
    for mob, tier in sorted(mythic_tiers().items()):
        if mob not in mobs:
            continue
        curse, bolt, dmg = TIER[min(tier, 2)]
        add(mob, curse, "curse", chance=10, every=15000, rng=5,
            amount=5 + 5 * tier, stat="might", cat="disheartens", dur=30000, anim=10, say=SUMMON)
        add(mob, "Venom", "poison", chance=10, every=15000, rng=5,
            amount=100 * (tier + 1), dur=20000, anim=6, say=SUMMON)
        add(mob, "Blind", "blind", chance=10, every=15000, rng=5,
            cat="blinds", dur=8000, anim=39, say=SUMMON)
        add(mob, bolt, "damage", chance=1, every=15000, rng=5, amount=dmg, anim=4, say=SUMMON)

    with OUT.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["MobKey", "Name", "Effect", "Chance", "EveryMs", "Range",
                                          "Amount", "Stat", "Category", "DurationMs", "Anim", "Sound", "Say"])
        w.writeheader()
        w.writerows(rows)
    casters = len({r["MobKey"] for r in rows})
    print(f"wrote {len(rows)} spell rows for {casters} casting creatures -> {OUT}")

    # ---- MobBosses.csv -----------------------------------------------------------------------------
    # HealChance is RTK's `paraHeal` roll inverted: it rolls 1..2 (tier 0), 1..3 (1) or 1..4 (2) and takes
    # the hit normally on a 1, so the heal fires (N-1)/N of the time. ParaBreakChance is its flat 1-in-2
    # every few seconds while held. LastStandMs is ours: RTK's last_stand is a duration whose length lives
    # in a spell script we don't have, so 30s is a stated assumption, not an extracted number.
    boss_rows = []
    for mob, tier in sorted(mythic_tiers("on_attacked").items()):
        if mob not in mobs:
            continue
        t = min(tier, 2)
        boss_rows.append(dict(MobKey=mob, HealAmount=BOSS_HEAL[t], HealChance=t + 2,
                              ParaBreakChance=2, LastStandMs=30000, Anim=5, Sound=708))
    bosses_out = ROOT / "game-data" / "MobBosses.csv"
    with bosses_out.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["MobKey", "HealAmount", "HealChance", "ParaBreakChance",
                                          "LastStandMs", "Anim", "Sound"])
        w.writeheader()
        w.writerows(boss_rows)
    print(f"wrote {len(boss_rows)} boss rows -> {bosses_out}")

if __name__ == "__main__":
    main()
