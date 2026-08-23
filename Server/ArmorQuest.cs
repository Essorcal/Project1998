using Shared;

namespace Server;

/// <summary>
/// The Star / Moon / Sun armor chains — twelve quests, three per path, the level-66-and-up spine of the
/// 4.95 endgame. Each is run entirely by speech at your own path's guildmaster: <b>say "star"</b>, do what
/// he asks, come back and <b>say "star"</b> again. Every source is emphatic that this is the whole
/// interface ("Be sure to say the armor type every time you complete a step"), which is why none of these
/// add a click-menu entry — see <see cref="ArmorQuestAbility"/>.
///
/// <para><b>The shape of a chain.</b> Star wants <i>Blessed by the Stars</i> (see
/// <see cref="BlessedByTheStars"/>) and pays a bonded Star piece plus the legend
/// "<c>mastered_the_stars</c>". Moon consumes a Star piece and pays "<c>understood_the_moon</c>". Sun
/// consumes a Moon piece and pays "<c>survived_the_sun</c>". The consumed piece need not be your own bonded
/// one — every page says so in the same words, "whether it's bonded to someone else, your bonded one, or
/// even a nonbonded one" — which is exactly why unbonded Star and Moon armor had a resale market. Moon
/// unlocks that path's Wind quest alongside Sun; Wind itself is out of scope here (a separate giver, the
/// Scribe atop Scribe's Mountain in Vale).</para>
///
/// <para><b>Sources, and how they were weighed.</b> Three witnesses, none of them the game:
/// <list type="number">
/// <item><b>tswolf.com/quests/armor/{warrior,mage,poet,rogue}.shtml</b> (Wayback, Jan–Jun 2001) — the
/// era-closest walkthrough, and the one that fixes the STEP STRUCTURE: which asks exist and in what order.
/// Its per-page prose is loose, and it visibly copy-pastes boilerplate between its four pages (the same
/// "Dog Karma" line and the same Wind paragraph appear on all four), so its numbers are the weakest part
/// of it.</item>
/// <item><b>nexusatlas.com/quests/{warrior,mage,poet,rogue}armor.php</b> (5.x era) — structured, itemising
/// every sacrificed item and every stat/karma point per step. Confirmed to omit NO step that tswolf has;
/// where the two differ it is always a value, never a missing ask.</item>
/// <item><b>The official-board tutor walkthroughs</b> — eyewitness, but only ONE of them is independent:
/// the Rogue guide (Ssjxrouge, ed. Melalye) has its own prose and details found nowhere else (it names
/// Rogue Maro, and it is the second witness for the white-amber method). The Warrior guides are not a
/// cross-check — Veggs's opens "Information borrowed from www.nexusatlas.com" and SoulHunter's is
/// near-identical to Veggs's.</item>
/// </list>
/// <b>RTK-Server's Lua is deliberately NOT a source for requirements here.</b> It is a hobby fan-server
/// reconstruction; per the project's standing rule the archive outranks it, and anything RTK asks for that
/// neither period page mentions has been dropped (see the divergence list in
/// <c>docs/common/Armor-Quests.md</c>). What RTK IS used for is <b>prose</b>: the guildmasters' lines
/// survive nowhere else, and RTK's strings are ported dialogue rather than invented. Its own signature
/// double-letter corruption is corrected on the way in ("anotheer" → "another", "otheer" → "other"), the
/// same call made for the Sute tale.</para>
///
/// <para><b>Level and karma gates live in <c>game-data/ArmorQuests.csv</c></b>, not here — that is the one
/// field the sources genuinely fight over (Rogue Sun is Bear on Atlas and Tiger on the tutor board, and
/// unresolved), so it is editable + @reload-able rather than compiled in. The values below are the
/// fallback if the file is missing.</para>
/// </summary>
public static class ArmorQuest
{
    // ---- legends ---------------------------------------------------------------------------------
    /// <summary>The Star chain's prerequisite — see <see cref="BlessedByTheStars"/>.</summary>
    public const string BlessedLegend = "blessed_by_the_stars";
    public const string StarLegend = "mastered_the_stars";
    public const string MoonLegend = "understood_the_moon";
    public const string SunLegend  = "survived_the_sun";

    /// <summary>Icon/colour for the three chain legends. RTK's values, and the only witness for them —
    /// neither period page records a glyph index. Cosmetic either way.</summary>
    public const byte LegendIcon = 5, LegendColor = 128;

    // ---- registry keys other systems write, that these chains read -------------------------------
    /// <summary>Completed mentorships (<see cref="Session.RunMentorAsync"/>). Poet Moon wants three.</summary>
    public const string MentoredReg = "mentored";
    /// <summary>Carnage victories, recorded by a GM with <c>@carnage</c>. Warrior Sun wants two.</summary>
    public const string CarnageWinsReg = "carnage_wins";
    /// <summary>How far along the Poet Sun four-totem sequence the player is (0..4), advanced only by
    /// worshipping the right totem in the right order — see <see cref="TotemWorshipAbility"/>.</summary>
    public const string TotemStepReg = "sun_armor_totem";

    /// <summary>Crafting skill-point registry keys, one per manufacturing skill, and the point total each
    /// needs to read as <b>Adept</b> (RTK <c>crafting.skillPointsPerLevel</c>, rank 4 of 11).
    ///
    /// <para><b>Nothing writes these yet, and Poet Sun step 5 is therefore a hard stop.</b> That is
    /// deliberate and was chosen over skipping the step: both period pages carry it ("become Adept in a
    /// Manufacturing skill", "Adept, or higher, in a Refining Skill"), so it belongs in the chain, and a
    /// gate wired to a real check is one line from working the day manufacturing crafting is ported.
    /// Gathering is modelled (Session.Harvest.cs) but feeds none of these three: tailoring, smithing and
    /// carpentry take their points from crafting items, and there are no recipes here.</para></summary>
    public static readonly (string Skill, string Reg, int Adept)[] ManufactureSkills =
    {
        ("Tailoring",   "craft_tailoring",   3910),
        ("Smithing",    "craft_metalworking", 2040),
        ("Carpentry",   "craft_woodworking",  2250),
    };

    // ---- who runs the quests ---------------------------------------------------------------------
    /// <summary>NpcId → base path, for the eight guildmasters who run these chains: the four in Kugnae and
    /// the four in Buya. Atlas is explicit and identical on all four pages — <i>"Say 'Star' to Kugnae or
    /// Buya &lt;Path&gt; Guildmaster"</i> — and the Rogue tutor names Maro by name.
    ///
    /// <para>This is a DELIBERATE narrowing. The ability is composed onto the four <c>*TrainerNpc</c>
    /// identifiers, and eighteen NPCs share those four keys: the eight guildmasters, the twelve alignment
    /// trainers in the path halls, and the three Nagnang masters (Sword/Wand/Staff/Dagger). RTK lets any of
    /// them run the chain; no period source mentions anywhere but the two capitals.</para></summary>
    public static readonly IReadOnlyDictionary<int, int> GuildMasters = new Dictionary<int, int>
    {
        [36] = 1, [41] = 1,   // Tabaek (Kugnae 12)  · Yabaek   (Buya 366)   — Warrior
        [37] = 2, [42] = 2,   // Maro   (Kugnae 16)  · Maso     (Buya 368)   — Rogue
        [35] = 3, [39] = 3,   // Haedu  (Kugnae 14)  · Eldritch (Buya 367)   — Mage
        [38] = 4, [40] = 4,   // Jinsun (Kugnae 18)  · Song     (Buya 369)   — Poet
    };

    /// <summary>The level + karma tier a chain demands, from <c>ArmorQuests.csv</c>, falling back to the
    /// shipped values if the row is missing so a deleted file cannot open a gate.</summary>
    public static (int Level, string Karma) GateFor(int path, string tier)
    {
        if (Content.ArmorQuestGates.TryGetValue((path, tier), out var g)) return g;
        int level = tier switch { "star" => 66, "moon" => 76, _ => 86 };
        string karma = tier switch
        {
            "star" => "Rabbit",
            "moon" => path is 1 or 2 ? "Dog" : "Ox",
            _      => path is 1 or 3 ? "Tiger" : "Bear",
        };
        return (level, karma);
    }

    private static Dictionary<(int, string), ArmorChain>? _chains;

    /// <summary>Every chain, keyed by (base path, tier). Built on first use, NOT in a field initializer —
    /// <see cref="BuildChains"/> reads shared prose and mob lists declared further down this class, and
    /// static field initializers run in declaration order, so an eager one would bake nulls into half the
    /// steps.</summary>
    public static IReadOnlyDictionary<(int Path, string Tier), ArmorChain> Chains => _chains ??= BuildChains();

    // =========================================================================================
    // The chains themselves. Each step is one "say"; the runner (ArmorQuestAbility.Run) speaks Ask,
    // tests the requirement, and either takes the price and advances or speaks Unmet and stops.
    // =========================================================================================
    private static Dictionary<(int, string), ArmorChain> BuildChains()
    {
        var all = new List<ArmorChain>();

        // ---------------------------------------------------------------------------------------
        // WARRIOR — Scale Mail / Mail Dress
        // ---------------------------------------------------------------------------------------
        all.Add(new ArmorChain(1, "star", "star_scale_mail", "star_mail_dress")
        {
            Intro = new[]
            {
                "Every man and woman is a star.",
                "You wish to twinkle?",
                "Everyone does. Yet many have failed.",
            },
            Steps = new[]
            {
                // 18 monkeys of your Mythic Monkey tier. Any tier's count satisfies it — you can only reach
                // the cave you qualify for, so testing all three is the same rule stated without a lookup.
                new ArmorStep
                {
                    Ask = new[] { "Among the failures are the evil monkeys. Even the most agile ones lack light. Slay 18 of the fastest monkeys, then return." },
                    Kills = Groups(One("spry_monkey", 18), One("agile_monkey", 18), One("fast_monkey", 18)),
                    Unmet = "I see no monkey blood on your hands. Slay 18 of them and return.",
                },
                new ArmorStep
                {
                    Ask = new[] { "You have the speed of a star, but have you its strength? Bring me two titanium gloves." },
                    Items = new[] { ("titanium_glove", 2) },
                    Unmet = "You are missing the gloves. Please return when you have them.",
                },
                new ArmorStep
                {
                    Ask = new[] { "To truly shine with the light of the stars, you must also bring the sword that glows with the star's light." },
                    Items = new[] { ("electra", 1) },
                    Unmet = "Please come back when you have an electra.",
                },
                Final(might: 1, karma: 1),
            },
        });

        all.Add(new ArmorChain(1, "moon", "moon_scale_mail", "moon_mail_dress")
        {
            Intro = MoonIntro("You follow the path of Valor. Prove yours."),
            Steps = new[]
            {
                new ArmorStep
                {
                    Ask = new[] { "A foul beast has stolen light from the moon to serve his vanity. Slay the glowing pig-man to free the moon's power." },
                    Kills = Groups(One("boar_champion", 1), One("pig_champion", 1), One("pig_avenger", 1)),
                    Unmet = "The glowing pig-man still hoards the moon's light. Go.",
                },
                new ArmorStep
                {
                    Ask = new[] { "The most insane mutts were corrupted by the moon's power. Slay thirty of them to realize the moon's strength." },
                    Kills = Groups(One("mad_dog", 30), One("crazed_mongrel", 30), One("frothing_mutt", 30)),
                    Unmet = "Thirty of them, and not one fewer. Return when it is done.",
                },
                new ArmorStep
                {
                    Ask = new[] { "The full moon drips upon the earth, seeping into the ground. The grim ogres try to harness this power. Kill twenty of them and bring me as many ambers." },
                    Kills = Groups(One("grim_ogre", 20)),
                    Items = new[] { ("amber", 20) },
                    Unmet = "I see no blood of the ogres on your blade, or I see no ambers. Return when you have both.",
                },
                new ArmorStep
                {
                    Ask = new[] { "The moon's power is not harnessed so easily! Bring the following all at once: one titanium glove, and three electras." },
                    Items = new[] { ("titanium_glove", 1), ("electra", 3) },
                    Unmet = "Please return when you have all the required items.",
                    Might = 2, Grace = 1,
                },
                Final(might: 0, karma: 2),
            },
        });

        all.Add(new ArmorChain(1, "sun", "sun_scale_mail", "sun_mail_dress")
        {
            Intro = SunIntro,
            Steps = new[]
            {
                // Carnage. Atlas + the (Atlas-derived) Warrior tutor only; tswolf's period page has no
                // trace of it. Kept because it is not an RTK-only ask, and satisfiable because a carnage
                // win is an event outcome a GM records — which is how carnage actually ran. See @carnage.
                new ArmorStep
                {
                    Ask = new[] { "Prove your combat expertise. Win at least two Carnages." },
                    Extra = c => c.Reg(CarnageWinsReg) >= 2,
                    Unmet = "You are missing the two required carnage victories that you need.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Far to the north is a land the sun only grazes. Master that land. Slay 60 ogres of ice and 60 ogres of frost then return." },
                    Kills = Groups(All(("ice_ogre", 60), ("frost_ogre", 60))),
                    Unmet = "The northern land is not yet mastered. Sixty of each, then return.",
                },
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "You must now bring me several things to complete your armor. Read carefully and do not return unless you have ALL of them.",
                        "Now bring to me 20 of the purest ambers so that we may capture the light of the sun.",
                        "Such fragile hands you have. Mere flesh and bone is not enough. Bring two titanium gloves.",
                        "And to cut your armor from the sun? I will need four electras.",
                        "Only with impurities can true purity be reached. You must also bring to me two corrupted blades.",
                    },
                    Items = new[] { ("white_amber", 20), ("titanium_glove", 2), ("electra", 4), ("corrupted_blade", 2) },
                    KeepBack = new[] { ("titanium_glove", 2), ("electra", 4), ("corrupted_blade", 2) },
                    Unmet = "Return to me when you have all the required items.",
                },
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "Did you think it was so easy? Your work is still very far from complete. Your pride is too strong. Your greed is too strong also, I see.",
                        "Humble yourself. Slay 200 rabbits, and nothing else.",
                    },
                    Kills = Groups(One("rabbit", 200)), Pure = true,
                    Unmet = "Return when you have slain 200 rabbits, and only rabbits.",
                },
                new ArmorStep
                {
                    Ask = new[] { "I heard your mind complaining at that tedious task. Perhaps you are not yet humble enough. Collect 14 gold acorns while you kill 200 squirrels." },
                    Kills = Groups(One("squirrel", 200)), Pure = true,
                    Items = new[] { ("gold_acorn", 14) },
                    Unmet = "Return when you have slain all 200 squirrels and hold all 14 gold acorns.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Prove your superiority over the monkeys. Slay their two leaders." },
                    Kills = Groups(All(("mythic_monkey", 1), ("monkey_mauler", 1)),
                                   All(("divine_monkey", 1), ("monkey_basher", 1)),
                                   All(("spirit_monkey", 1), ("monkey_avenger", 1))),
                    Unmet = "Both leaders, warrior. Not one.",
                },
                Final(might: 3, grace: 2, will: 2, karma: 3, gold: 20000,
                      goldLine: "Now bring me 20,000 coins of gold which I will melt and use to forge your armor. Bring also your unequipped moon mail."),
            },
        });

        // ---------------------------------------------------------------------------------------
        // ROGUE — Waistcoat / Blouse
        // ---------------------------------------------------------------------------------------
        all.Add(new ArmorChain(2, "star", "star_waistcoat", "star_blouse")
        {
            Intro = new[]
            {
                "Every man and woman is a star.",
                "You wish to twinkle?",
                "Everyone does. Yet many have failed.",
            },
            Steps = new[]
            {
                new ArmorStep
                {
                    Ask = new[] { "Among the failures are the ogres. Even the most agile ones lack light. Slay 2 of the Slime Ogres or 2 of the Muck Ogres, then return." },
                    Kills = Groups(One("slime_ogre", 2), One("muck_ogre", 2)),
                    Unmet = "I want the blood fresh on your blade. Two of them, then return.",
                },
                new ArmorStep
                {
                    Ask = new[] { "You have proven your strength, but what of your grace? Bring me two of the silent bands." },
                    Items = new[] { ("whisper_bracelet", 2) },
                    Unmet = "You are missing the bracelets. Please return when you have them.",
                },
                new ArmorStep
                {
                    Ask = new[] { "To truly shine with the light of the stars, you must also bring the sword that glows with the star's light." },
                    Items = new[] { ("steelthorn", 1) },
                    Unmet = "Please come back when you have a Steelthorn.",
                },
                Final(grace: 1, karma: 1),
            },
        });

        all.Add(new ArmorChain(2, "moon", "moon_waistcoat", "moon_blouse")
        {
            Intro = MoonIntro("You follow the path of Riches. Prove your worth."),
            Steps = new[]
            {
                new ArmorStep
                {
                    Ask = new[] { "Dogs in town teach secrets to many. But most animals are not so pure. Slay the Dog that defiles the beautiful rose by carrying it in his mouth." },
                    Kills = Groups(One("dog_assassin", 1), One("dog_cutthroat", 1), One("dog_avenger", 1)),
                    Unmet = "The rose is still in his mouth. Go.",
                },
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "You follow the path of Riches. Prove your worth. Bring to me all of the following AT THE SAME TIME.",
                        "The full moon drips upon the earth, seeping into the ground. Bring fifty spheres of sweet amber.",
                        "The dark moon drips still deeper into the earth. Bring ten of this darker amber.",
                        "Only the stealthiest can don this garment. Bring two whisper bracelets to quiet your hands.",
                        "Bring me two steelthorns to cut the moon's material.",
                        "The moon favors the lucky. Bring also one of the lucky coins, and fifteen thousand coins of gold.",
                    },
                    Items = new[] { ("amber", 50), ("dark_amber", 10), ("whisper_bracelet", 2), ("steelthorn", 2), ("lucky_coin", 1) },
                    Gold = 15000,
                    Unmet = "Please return when you have all the required items.",
                    Done = "You have proven your material worth.",
                },
                // The axe is REBONDED, not consumed — "he will bond it to you… it CAN BE BONDED TO SOMEONE
                // ELSE. It will simply be rebonded to you." Handled by Rebond, not Items.
                new ArmorStep
                {
                    Ask = new[] { "Soon, my impatient rogue friend. Now display to me your White Moon Axe. I will bond it to your soul, so that no other may wield it." },
                    Rebond = "white_moon_axe",
                    Unmet = "Please return when you have the axe.",
                },
                Final(grace: 2, karma: 2),
            },
        });

        all.Add(new ArmorChain(2, "sun", "sun_waistcoat", "sun_blouse")
        {
            Intro = SunIntro,
            Steps = new[]
            {
                new ArmorStep
                {
                    Ask = new[] { "I do not envy you, rogue. For to prove your worthiness, you must slay a dozen ice panthers and the dreaded Citelam! If you survive, return so that we may continue." },
                    Kills = Groups(All(("ice_panther", 12), ("ogre_citelam", 1))),
                    Unmet = "Twelve panthers and Citelam. Return when both are done.",
                },
                // "Slay the two leaders of the rats, WITHOUT killing any other creatures in the rats cave."
                // Forbid (not Pure): the ask names the cave, not the world, so a kill elsewhere is allowed.
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "You have proven your melee prowess. But there is much more to being a rogue, is there not?",
                        "Prove now your skill and stealth. Slay the two leaders of the rats, WITHOUT killing any other creatures in the rats cave.",
                    },
                    Kills = Groups(All(("mythic_rat", 1), ("mighty_mouse", 1)),
                                   All(("divine_rat", 1), ("rat_lord", 1)),
                                   All(("spirit_rat", 1), ("rat_avenger", 1))),
                    Forbid = RatRabble,
                    Unmet = "Both leaders, and nothing else in that cave.",
                    Spoiled = "You slew a beast you should not have touched. Try again.",
                },
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "Ah, but it is easy to defeat the clumsy rats with stealth! Now for a real challenge.",
                        "Slay the two leaders of the rabbits, WITHOUT killing any other creatures in that cave.",
                    },
                    Kills = Groups(All(("mythic_hare", 1), ("hare_witch", 1)),
                                   All(("divine_rabbit", 1), ("rabbit_witch", 1)),
                                   All(("spirit_rabbit", 1), ("rabbit_avenger", 1))),
                    Forbid = RabbitRabble,
                    Unmet = "Both leaders, and nothing else in that cave.",
                    Spoiled = "You slew a beast you should not have touched. Try again.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Your stealth is impressive! But your Path is that of Riches, not stealth. Bring me 50,000 gold coins, eight steelthorns, five whisper bracelets, six corrupted rings and twenty gold acorns." },
                    Items = new[] { ("steelthorn", 8), ("whisper_bracelet", 5), ("corrupted_ring", 6), ("gold_acorn", 20) },
                    KeepBack = new[] { ("steelthorn", 4), ("whisper_bracelet", 2), ("corrupted_ring", 3) },
                    Gold = 50000,
                    Unmet = "Return when you have all the required items.",
                    Done = "You have done well. I'll let ya keep most of the goods.",
                },
                Final(might: 2, grace: 3, will: 2, karma: 3),
            },
        });

        // ---------------------------------------------------------------------------------------
        // MAGE — Garb / Dress
        // ---------------------------------------------------------------------------------------
        all.Add(new ArmorChain(3, "star", "star_garb", "star_dress")
        {
            Intro = new[]
            {
                "Every man and woman is a star.",
                "You wish to twinkle?",
                "Everyone does. Yet many have failed.",
            },
            Steps = new[]
            {
                new ArmorStep
                {
                    Ask = new[] { "Among the failures are the Skeleton Mage and Skeleton Warrior. Slay both of them, then return." },
                    Kills = Groups(All(("skeleton_mage", 1), ("skeleton_warrior", 1))),
                    Unmet = "Both of them, mage. Return when it is done.",
                },
                new ArmorStep
                {
                    Ask = new[] { "You have the speed of a star, but have you its strength? Bring me two holy rings." },
                    Items = new[] { ("holy_ring", 2) },
                    Unmet = "You are missing the rings. Please return when you have them.",
                },
                new ArmorStep
                {
                    Ask = new[] { "To truly shine with the light of the stars, you must also bring the staff that glows with the star's light." },
                    Items = new[] { ("star_staff", 1) },
                    Unmet = "Please come back when you have a Star-staff.",
                },
                Final(will: 1, karma: 1),
            },
        });

        all.Add(new ArmorChain(3, "moon", "moon_garb", "moon_dress")
        {
            Intro = MoonIntro("You follow the path of Magic. Prove yours."),
            Steps = new[]
            {
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "A foul beast has stolen light from the moon to serve his vanity. Slay the monster with the shortest name in all the lands to free the moon's power.",
                        "Please return to me when you have completed this task.",
                    },
                    Kills = Groups(One("li", 1)),
                    Unmet = "The shortest name in all the lands. You have not yet found him.",
                },
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "A foul beast has stolen light from the moon to serve his vanity. Slay the slowest creature in all the lands to free the moon's power.",
                        "Please return to me when you have completed this task.",
                    },
                    Kills = Groups(One("white_wolf", 1)),
                    Unmet = "The slowest creature in all the lands still breathes.",
                },
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "For this next task, I will require that you bring me a complete key set.",
                        "I will need: Key to Earth, Key to Fire, Key to Heaven, Key to Mountain, Key to Wind, Key to Pond, Key to Thunder, Key to Water, and Sute's Key.",
                    },
                    Items = new[]
                    {
                        ("key_to_earth", 1), ("key_to_fire", 1), ("key_to_heaven", 1), ("key_to_mountain", 1),
                        ("key_to_wind", 1), ("key_to_pond", 1), ("key_to_thunder", 1), ("key_to_water", 1),
                        ("sutes_key", 1),
                    },
                    Unmet = "Please return when you have all the keys.",
                },
                new ArmorStep
                {
                    Ask = new[] { "For this next task, I will require that you bring me 2 Star-staves and a holy ring." },
                    Items = new[] { ("star_staff", 2), ("holy_ring", 1) },
                    Unmet = "Please return to me when you have the star-staves and a holy ring.",
                },
                Final(will: 2, karma: 2),
            },
        });

        all.Add(new ArmorChain(3, "sun", "sun_garb", "sun_dress")
        {
            Intro = SunIntro,
            Steps = new[]
            {
                new ArmorStep
                {
                    Ask = new[] { "I do not envy you, mage. For to prove your worthiness, you must slay forty Fluffs and forty Thumps — or, if the deepest warren is still beyond you, sixty Mad hares and sixty Giant rabbits." },
                    Kills = Groups(All(("fluff", 40), ("thump", 40)),
                                   All(("mad_hare", 60), ("giant_rabbit", 60))),
                    Unmet = "Not yet. And take care — the Hops wear the Thumps' colours.",
                },
                // "Three items with Star in the name (excluding armor)." The period pool is four —
                // Star powder, Stardrop, Star-staff, Star burst — but Star burst is carpenter-made and has
                // no row in the 4.95 item registry, so the pool that exists here is exactly three.
                new ArmorStep
                {
                    Ask = new[] { "Next I will require three items with \"Star\" in the name." },
                    AnyItems = (new[] { "star_powder", "stardrop", "star_staff", "star_burst" }, 3),
                    Unmet = "I need three items with the word \"Star\" in the name.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Please kill a creature with the word \"Slow\" in its name." },
                    Kills = Groups(One("skeleton_warrior", 1), One("wild_horse", 1), One("wild_rooster", 1)),
                    Unmet = "You have not slain a creature with the word \"Slow\" in its name.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Next I will require 20 White Ambers, 4 Holy Rings, 5 Star-staves, and 2 Corrupted staves." },
                    Items = new[] { ("white_amber", 20), ("holy_ring", 4), ("star_staff", 5), ("corrupted_staff", 2) },
                    Unmet = "Return when you have all the required items.",
                },
                new ArmorStep
                {
                    Ask = new[] { "The hard part is over. What is left is merely tedious. Kill the Massive Scorpion in the Kugnae Spider cave." },
                    Kills = Groups(One("massive_scorpion", 1)),
                    Unmet = "You have not slain the Massive Scorpion.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Please kill 200 rabbits, and nothing else, then return to me." },
                    Kills = Groups(One("rabbit", 200)), Pure = true,
                    Unmet = "You have not slain 200 rabbits, and only rabbits.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Please get me 14 gold acorns while you kill 200 squirrels, and then return to me." },
                    Kills = Groups(One("squirrel", 200)), Pure = true,
                    Items = new[] { ("gold_acorn", 14) },
                    Unmet = "Return when you have slain all 200 squirrels and hold all 14 gold acorns.",
                },
                Final(might: 2, grace: 2, will: 3, karma: 3),
            },
        });

        // ---------------------------------------------------------------------------------------
        // POET — Robes / Gown
        // ---------------------------------------------------------------------------------------
        all.Add(new ArmorChain(4, "star", "star_robes", "star_gown")
        {
            Intro = new[]
            {
                "Every man and woman is a star.",
                "You wish to twinkle?",
                "Everyone does. Yet many have failed.",
            },
            Steps = new[]
            {
                new ArmorStep
                {
                    Ask = new[] { "One that has failed has turned to trickery and deceit. Find him. He's grown another tail for each of his generations of deceit. Slay him once for each tail, then return." },
                    Kills = Groups(One("nine_tailed_fox", 9)),
                    Unmet = "Once for each tail. Nine, and not one fewer.",
                },
                // tswolf and Atlas both put the gloves and the lance in ONE ask; only RTK splits them.
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "Your hands do not show any power. Receive the power of the Sen Gloves — bring me two.",
                        "And to twinkle brightly, you must present that lance that twinkles most brightly.",
                    },
                    Items = new[] { ("sen_glove", 2), ("titanium_lance", 1) },
                    Unmet = "You are missing the gloves or the lance. Please return when you have them.",
                },
                Final(will: 1, karma: 1),
            },
        });

        all.Add(new ArmorChain(4, "moon", "moon_robes", "moon_gown")
        {
            Intro = MoonIntro("You follow the path of Love. Prove your devotion through sacrifice."),
            Steps = new[]
            {
                new ArmorStep
                {
                    Ask = new[] { "Do you understand the feeling of true companionship? Has another touched your soul?" },
                    Extra = c => c.SpouseName.Length > 0,
                    Unmet = "Please return to me when you have made a commitment.",
                    Done = "I see that you have found your true companion. That is good.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Not all victories stem from combat. Bring to me 50 roses to offer in the name of love." },
                    Items = new[] { ("rose", 50) },
                    Unmet = "Please return to me when you have the 50 roses.",
                },
                new ArmorStep
                {
                    Ask = new[] { "For this next task, I will ask that you show me another example of commitment, mentoring 3 others." },
                    Extra = c => c.Reg(MentoredReg) >= 3,
                    Unmet = "Return to me when you have mentored at least 3 others.",
                },
                Final(will: 2, karma: 2),
            },
        });

        all.Add(new ArmorChain(4, "sun", "sun_robes", "sun_gown")
        {
            Intro = SunIntro,
            Steps = new[]
            {
                // tswolf and the tutor guide both make these two separate asks; only Atlas folds them into
                // one line of its own step 1. Two says, matching the two independent accounts.
                new ArmorStep
                {
                    Ask = new[] { "I do not envy you, poet. For to prove your worthiness, you must slay the Massive Scorpion of the Kugnae Spider cave." },
                    Kills = Groups(One("massive_scorpion", 1)),
                    Unmet = "You have not slain the Massive Scorpion.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Now Sute, in his lair beneath Buya. He has troubled these lands long enough." },
                    Kills = Groups(One("sute", 1)),
                    Unmet = "Sute still stirs beneath Buya.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Next I would like you to bring to me: 10 crafted white ambers, 1 purified water, and 6 sen gloves." },
                    Items = new[] { ("crafted_white_amber", 10), ("purified_water", 1), ("sen_glove", 6) },
                    Unmet = "Please return when you have all the items.",
                },
                new ArmorStep
                {
                    Ask = new[]
                    {
                        "Next I would like you to show your devotion to the four totem animals.",
                        "First I would like you to worship Chung ryong, then Baekho, then Ju Jak, and finally Hyun moo.",
                        "You do not need to return to me after you worship each totem, only when you have worshipped all four totems.",
                    },
                    Extra = c => c.Reg(TotemStepReg) >= 4,
                    Unmet = "You have yet to worship all four totems, please return to me when you have done so.",
                    OnPay = c => c.SetReg(TotemStepReg, 0),
                },
                // The hard stop. See ManufactureSkills: the gate is real, and nothing fills it yet.
                new ArmorStep
                {
                    Ask = new[] { "Next I would like to see your devotion to the crafts. You will need to be the level of Adept or higher in either Tailoring, Smithing, or Carpentry." },
                    Extra = c => ManufactureSkills.Any(s => c.Reg(s.Reg) >= s.Adept),
                    Unmet = "Return to me when you have achieved Adept status in Tailoring, Smithing, or Carpentry.",
                    Done = "You have shown your devotion to the crafts.",
                },
                new ArmorStep
                {
                    Ask = new[] { "Next I will require 2 Titanium Lances." },
                    Items = new[] { ("titanium_lance", 2) },
                    Unmet = "Return when you have 2 titanium lances.",
                },
                Final(karma: 5),
            },
        });

        return all.ToDictionary(c => (c.Path, c.Tier));
    }

    // ---- shared prose ----------------------------------------------------------------------------
    private static string[] MoonIntro(string pathLine) => new[]
    {
        "You have returned for guidance from the moon?",
        "Very well, but the sacrifices shall be much greater!",
        pathLine,
    };

    private static readonly string[] SunIntro =
    {
        "The sun is the mightiest and fiercest of all.",
        "Only the very best and most true can master it.",
    };

    /// <summary>The closing step every chain ends on: hand over the previous tier's garment (Star has none
    /// to hand over, so its Wear is the only price), pay the stats and karma, receive the bonded piece.</summary>
    private static ArmorStep Final(int might = 0, int grace = 0, int will = 0, int karma = 0,
                                   uint gold = 0, string? goldLine = null) => new()
    {
        Ask = goldLine is null ? Array.Empty<string>() : new[] { goldLine },
        Gold = gold,
        Might = might, Grace = grace, Will = will, Karma = karma,
        Unmet = "Please return when you have all the required items.",
        IsFinal = true,
    };

    // Kill-requirement sugar: Groups(...) = satisfy ANY group; All(...) / One(...) build a group.
    private static (string Mob, int Count)[][] Groups(params (string, int)[][] groups) => groups;
    private static (string Mob, int Count)[] All(params (string, int)[] reqs) => reqs;
    private static (string Mob, int Count)[] One(string mob, int count) => new[] { (mob, count) };

    /// <summary>The ordinary residents of the Mythic Rat cave — killing any of them spoils the Rogue Sun
    /// "both leaders and NOTHING else" step. All three tiers are listed because the step never asks which
    /// cave you are in.</summary>
    private static readonly string[] RatRabble =
    {
        "mythic_mouse", "vile_rat", "blood_rat", "rat_sentry",
        "divine_mouse", "mud_rat", "hunter_rat", "lava_rat", "rat_guardian",
        "spirit_mouse", "earth_rat", "fire_rat", "beady_eyed_stalker", "rat_defender",
    };

    /// <summary>Likewise for the Mythic Rabbit cave. Note <c>hop</c> is here: it is a normal resident, and
    /// the one that ruins Mage Sun's count as well ("Hops look exactly like Thumps").</summary>
    private static readonly string[] RabbitRabble =
    {
        "golden_hare", "mad_rabbit", "giant_hare", "rabbit_sentry",
        "golden_rabbit", "mad_hare", "giant_rabbit", "rabbit_guardian",
        "hop", "thump", "fluff", "rabbit_defender",
    };
}

/// <summary>
/// <b>Blessed by the Stars</b> — the one-off rite that unlocks the whole Star/Moon/Sun line: carry a White
/// amber into the <b>Mythic Nexus</b> (map 41) and <b>drop it in the middle circle</b>.
///
/// <para>Two independent witnesses for the method, which is why it is built the way it is rather than as an
/// NPC turn-in: nexusatlas <c>quests/blessed.php</c> — <i>"Simply obtain a White Amber and drop it in the
/// middle circle area of Mythic Nexus"</i> — and the official-board Rogue tutor walkthrough, which is the
/// only tutor guide with its own independent reporting. The same Atlas page carries the <b>level 60</b>
/// requirement, which no other source records and which RTK's own item script does not implement.</para>
///
/// <para>Atlas also notes an alternative opening (<i>say "Stars" to Ironheart, the Tutor in Kugnae</i>) and
/// says of it, in the same breath, <i>"The 'Stars' part can be skipped if you wish"</i> — it is a signpost
/// to the amber, not a second way to earn the mark. Only the amber is wired.</para>
///
/// <para>The drop is intercepted, so the amber is consumed rather than left lying in the Nexus — the same
/// shape as the harvesting hook on the same handler (RTK sets <c>player.fakeDrop = 1</c> to the same end).
/// Dropping a second amber once you hold the mark does nothing special and drops normally.</para>
/// </summary>
public static class BlessedByTheStars
{
    public const ushort NexusMap = 41;
    /// <summary>The middle circle, in the 60x60 Nexus. RTK's rectangle, and the only numeric witness for
    /// "the middle circle area" — it does sit dead centre, which is the corroboration available.</summary>
    public const int X0 = 28, X1 = 32, Y0 = 30, Y1 = 35;

    public const int    MinLevel = 60;              // nexusatlas quests/blessed.php, "Level Required : 60"
    public const string Offering = "white_amber";

    /// <summary>RTK's two effects, played together: the cloud and the swirl.</summary>
    public const int CloudEffect = 18, SwirlEffect = 11;
    public const byte LegendIcon = 3, LegendColor = 128;

    public static bool AtAltar(int map, int x, int y) =>
        map == NexusMap && x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
}

/// <summary>One "say" of an armor chain: what the guildmaster asks for, how to tell whether it is done, and
/// what it costs. See <see cref="ArmorQuestAbility.Run"/> for the order these are evaluated in.</summary>
public sealed class ArmorStep
{
    /// <summary>The ask, spoken every time the player says the word while on this step.</summary>
    public required string[] Ask { get; init; }

    /// <summary>Kill requirement as alternative groups: satisfy ANY one group, and within a group every
    /// (mob, count) must hold. Counts are DELTAS taken since the step opened, so kills banked before the
    /// guildmaster asked do not pay for it.</summary>
    public (string Mob, int Count)[][] Kills { get; init; } = Array.Empty<(string, int)[]>();

    /// <summary>Mobs that must not be killed at all while this step is open ("WITHOUT killing any other
    /// creatures in that cave"). A single one resets the step's kill snapshot and the player starts over.</summary>
    public string[] Forbid { get; init; } = Array.Empty<string>();

    /// <summary>Stricter than <see cref="Forbid"/>: EVERY kill made while this step is open must be one of
    /// the required mobs ("kill 200 Rabbits and nothing else"). Cheap to satisfy and brutal to break — which
    /// is what every source warns about.</summary>
    public bool Pure { get; init; }

    /// <summary>Tribute, taken under the 100%-worn-or-carried rule (<see cref="Session.TakeReady"/>).</summary>
    public (string Key, int Count)[] Items { get; init; } = Array.Empty<(string, int)>();

    /// <summary>"A portion of these items will be returned to you" (Warrior Sun) / "Some items returned"
    /// (Rogue Sun): he requires the full <see cref="Items"/> count but only KEEPS a random 1..Max of these
    /// keys, and the rest stays in the bag. Both period pages state that a portion comes back; only RTK
    /// records how much, so the Max values are its ranges and nothing else in the step is.</summary>
    public (string Key, int Max)[] KeepBack { get; init; } = Array.Empty<(string, int)>();

    /// <summary>Coins taken.</summary>
    public uint Gold { get; init; }

    /// <summary>An item that is BONDED rather than consumed (Rogue Moon's White Moon Axe).</summary>
    public string? Rebond { get; init; }

    /// <summary>Pick any <c>Count</c> distinct keys from a pool (Mage Sun's "three items with Star in the
    /// name"). Keys absent from the item registry simply cannot be offered.</summary>
    public (string[] Pool, int Count)? AnyItems { get; init; }

    /// <summary>Anything else the step gates on — marriage, mentorships, totems, carnage, craft rank.</summary>
    public Func<NpcContext, bool>? Extra { get; init; }

    /// <summary>Stat/karma price paid on success.</summary>
    public int Might { get; init; }
    public int Grace { get; init; }
    public int Will  { get; init; }
    public int Karma { get; init; }

    /// <summary>Said when the requirement is not met.</summary>
    public required string Unmet { get; init; }

    /// <summary>Said when a <see cref="Forbid"/> mob was killed, instead of <see cref="Unmet"/>.</summary>
    public string Spoiled { get; init; } = "You slew a beast you should not have touched. Try again.";

    /// <summary>Said on success. The stock line is RTK's, and it is what every step in every chain says.</summary>
    public string Done { get; init; } = "You have done well.";

    /// <summary>Extra bookkeeping on success (clearing the totem counter, say).</summary>
    public Action<NpcContext>? OnPay { get; init; }

    /// <summary>The closing step: also takes the previous tier's garment, grants the bonded piece and the
    /// legend, and resets the stage. Exactly one step per chain has this.</summary>
    public bool IsFinal { get; init; }

    /// <summary>Does this step take anything the player would want warning about first?</summary>
    public bool TakesSomething => Items.Length > 0 || Gold > 0 || AnyItems is not null || Rebond is not null || IsFinal;
}

/// <summary>One tier of one path's chain. See <see cref="ArmorQuest"/>.</summary>
public sealed class ArmorChain
{
    public ArmorChain(int path, string tier, string maleArmor, string femaleArmor)
    { Path = path; Tier = tier; MaleArmor = maleArmor; FemaleArmor = femaleArmor; }

    public int Path { get; }
    /// <summary>"star" / "moon" / "sun" — also the word the player says, and the stage key's prefix.</summary>
    public string Tier { get; }
    public string MaleArmor { get; }
    public string FemaleArmor { get; }

    /// <summary>Spoken once, when the chain is first opened.</summary>
    public string[] Intro { get; init; } = Array.Empty<string>();
    public required ArmorStep[] Steps { get; init; }

    /// <summary>RTK's own stage key names, kept because they are also the names the RTK data uses — a
    /// character imported from that shape keeps its progress.</summary>
    public string StageKey => Tier + "_armor";

    public string ArmorFor(int sex) => sex == 1 ? FemaleArmor : MaleArmor;

    /// <summary>The garment this tier consumes (the previous tier's), or null for Star.</summary>
    public string? PreviousArmorFor(int sex) => Tier switch
    {
        "moon" => ArmorQuest.Chains[(Path, "star")].ArmorFor(sex),
        "sun"  => ArmorQuest.Chains[(Path, "moon")].ArmorFor(sex),
        _      => null,
    };

    public string Legend => Tier switch
    {
        "star" => ArmorQuest.StarLegend,
        "moon" => ArmorQuest.MoonLegend,
        _      => ArmorQuest.SunLegend,
    };

    public string LegendText => Tier switch
    {
        "star" => "Mastered the stars",
        "moon" => "Understood the moon",
        _      => "Survived the sun",
    };
}
