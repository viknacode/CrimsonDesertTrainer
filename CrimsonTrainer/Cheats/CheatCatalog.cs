using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Cheats;

/// <summary>Everything the trainer offers for one attached game process.</summary>
internal sealed class CheatCatalog
{
    public const string PlayerPointersId = "player_pointers";
    public const string GodmodeId = "godmode";
    public const string MaxStatsId = "max_stats";
    public const string MaxContributionId = "max_contribution";
    public const string MaxTrustPeopleId = "max_trust_people";
    public const string MaxTrustHorseId = "max_trust_horse";
    public const string NoDecreaseId = "no_decrease";
    public const string GainMultiplierId = "gain_multiplier";
    public const string CopperId = "copper";
    public const string StackModifierId = "stack_modifier";
    public const string ItemSwapperId = "item_swapper";
    public const string TimeScaleId = "timescale";
    public const string HorseCaptureId = "horse_capture";
    public const string ArcheryId = "archery";
    public const string DurabilityId = "durability";
    public const string EasyParryId = "easy_parry";

    private const string Rebased = "AOB re-located for patch 2.01.00 by scanning the live game (the table's original no longer exists).";
    private const string Unverified = " The new site could not be verified in-game yet — if it does nothing or misbehaves, turn it off and report it.";

    public IReadOnlyList<Cheat> Cheats { get; }
    public PlayerStats PlayerStats { get; }
    public PlayerPosition PlayerPosition { get; }
    public IReadOnlyList<BodyScaleTarget> BodyScales { get; }

    public ToggleCheat Toggle(string id) => (ToggleCheat)Cheats.First(c => c.Id == id);
    public ChoiceCheat Choice(string id) => (ChoiceCheat)Cheats.First(c => c.Id == id);

    public CheatCatalog(GameProcess game)
    {
        var playerPointers = TableScripts.PlayerPointers(game);

        // One hook shared by four inventory features (see TableScripts.InventoryCount).
        var inventory = new SharedInjection(TableScripts.InventoryCount(game));
        SharedFeature Feature(string id, string modeVar, long value, Injection? extra = null) => new(inventory, id, modeVar, value, extra);

        Cheats = new Cheat[]
        {
            // ---------------- Player ----------------
            new ToggleCheat(playerPointers)
            {
                Id = PlayerPointersId, Name = "Player tracking", Section = CheatSection.Player,
                Description = "Captures the player pointers the live stats below need. Values may take a few seconds to populate after enabling.",
                Credits = "Tuuuup!",
            },
            new ToggleCheat(TableScripts.MaxContribution(game))
            {
                Id = MaxContributionId, Name = "Max contribution", Section = CheatSection.Player,
                Description = "Overrides the contribution total with 99,999. Also maxes skill levels (upgrade the skill twice per cycle). " + Rebased,
                HowTo = "Enable → steal something → disable → gain any contribution → level up. Repeat as needed.",
            },
            new ToggleCheat(TableScripts.MaxTrustPeople(game))
            {
                Id = MaxTrustPeopleId, Name = "Max trust — people & pets", Section = CheatSection.Player,
                Description = "Writes 100 trust into the record the game saves when a common / unnamed person or pet gains trust. " + Rebased + Unverified + " Tip from the table: for named people, turn on Items don't decrease and just keep gifting.",
                HowTo = "Greet, gift or pet the target twice (the first action creates the record, the second one updates it through the hooked path).",
            },
            new ToggleCheat(TableScripts.MaxTrustHorse(game))
            {
                Id = MaxTrustHorseId, Name = "Max trust — horse", Section = CheatSection.Player,
                Description = "Writes the 350 trust cap directly into the horse's trust field.",
                HowTo = "Perform any trust-gaining action with the horse.",
            },

            // ---------------- Inventory ----------------
            new ChoiceCheat(new[]
            {
                new ChoiceOption("off", "Off", (IActivation?)null),
                new ChoiceOption("v1", "v1 · All items", Feature("nodec1", "noDecMode", 1),
                    Warning: "v1 also skips the decrement for equipment — gear can be duplicated on use."),
                new ChoiceOption("v2", "v2 · Stackables", Feature("nodec2", "noDecMode", 2)),
            })
            {
                Id = NoDecreaseId, Name = "Items don't decrease", Section = CheatSection.Inventory,
                Description = "Skips the inventory count write when a stack goes down, so consumables and materials never run out. v2 only skips while the count is above 1, so equipment behaves normally. Pickups still add normally.",
            },
            new ChoiceCheat(new[]
            {
                new ChoiceOption("off", "Off", (IActivation?)null),
                new ChoiceOption("x9", "×9", Feature("gain9", "gainAmount", 9)),
                new ChoiceOption("x99", "×99", Feature("gain99", "gainAmount", 99)),
                new ChoiceOption("x99999", "×99999", Feature("gain99999", "gainAmount", 99999),
                    Warning: "×99999 applies to ALL gains, including quest items. Use with extreme caution."),
            })
            {
                Id = GainMultiplierId, Name = "Gain multiplier (pickup / buy / sell / earn)", Section = CheatSection.Inventory,
                Description = "Whenever a stack goes up, it goes up by the selected amount instead. " + Rebased + " Combinable with the other inventory cheats.",
                HowTo = "You need at least one of the item in your inventory already.",
            },
            new ToggleCheat(TableScripts.Copper99999(game))
            {
                Id = CopperId, Name = "Gain 99,999 copper on sell", Section = CheatSection.Inventory,
                Description = "Writes 99,999 into the copper balance on every sale — a direct write, independent of the multiplier. " + Rebased,
                HowTo = "Sell any item.",
            },
            new ToggleCheat(Feature("lock", "lockMode", 1))
            {
                Id = StackModifierId, Name = "Inventory stack lock", Section = CheatSection.Inventory,
                Description = "When a stack goes down it is rewritten to the locked amount instead (0 = keep whatever it was). " + Rebased,
                HowTo = "Set the amount, then use or drop any stackable item — its count becomes the locked amount.",
                Credits = "Austin / MPElite",
            },
            new ToggleCheat(Feature("swap", "swapMode", 1, TableScripts.HoverReader(game)))
            {
                Id = ItemSwapperId, Name = "Item swapper / spawner", Section = CheatSection.Inventory,
                Description = "Hook used by the spawner: watches every inventory count change (use / drop / pickup), remembers the entry that changed as the spawn target and, if a swap target is armed, rewrites any stack that goes down with that item ×1. " + Rebased,
                HowTo = "Turn it on, use or drop 1 unit of the stack you want to replace, then Spawn below. Turn it off when done.",
                Warning = "With a swap target armed (Swap on next use / drop), EVERY stack that goes down is swapped — leave the target at 0 unless you mean it.",
                Credits = "Austin / MPElite",
            },

            // ---------------- World ----------------
            new ToggleCheat(TableScripts.TimeScale(game))
            {
                Id = TimeScaleId, Name = "Time scale", Section = CheatSection.World,
                Description = "Overrides the game speed every tick. 1.0 = normal, 0.5 = half, 2.0 = double. " + Rebased,
                Credits = "supex0 / MPElite",
            },
            new ToggleCheat(TableScripts.InstantHorseCapture(game))
            {
                Id = HorseCaptureId, Name = "Instant horse capture", Section = CheatSection.World,
                Description = "Writes the capture threshold as the current progress every time the capture gauge advances, completing the attempt instantly. " + Rebased + Unverified,
                HowTo = "Enable before starting the capture.",
            },
            new ToggleCheat(TableScripts.Archery(game))
            {
                Id = ArcheryId, Name = "Wild West archery / shooting", Section = CheatSection.World,
                Description = "Sets the target score to 1 and your score to 0 every time the minigame checks them — the first hit wins. " + Rebased + Unverified,
            },
            new ChoiceCheat(new[]
            {
                new ChoiceOption("off", "Off", (IActivation?)null),
                new ChoiceOption("full", "Set to 100", new OwnInjection(TableScripts.Durability100(game))),
                new ChoiceOption("nodamage", "No damage", new OwnInjection(TableScripts.DurabilityNoDamage(game))),
            })
            {
                Id = DurabilityId, Name = "Equipment durability", Section = CheatSection.World,
                Description = "\"Set to 100\" rewrites the durability to 100 on every update (use the equipment once → save → reload). \"No damage\" skips the durability write entirely. " + Rebased,
                Warning = "The 2.01.00 location is the only \"store durability, then jns\" site in the binary, but it could not be verified in-game. If equipment behaves oddly, turn it off.",
                Credits = "bobdandy / MPElite",
            },
            new ToggleCheat(TableScripts.EasyParry(game))
            {
                Id = EasyParryId, Name = "Always perfect parry", Section = CheatSection.World,
                Description = "Makes the parry validation function return success immediately.",
                Warning = "Marked BROKEN in table v21 — kept for completeness, expect it not to work.",
                Credits = "supex0 / MPElite",
            },
        };

        PlayerStats = new PlayerStats(game, playerPointers);
        PlayerPosition = new PlayerPosition(game, playerPointers);
        BodyScales = new[] { BodyScaleTarget.Kliff(), BodyScaleTarget.Damiane() };
    }

    public void ResolveAll(byte[]? moduleImage = null)
    {
        foreach (var cheat in Cheats) cheat.Resolve(moduleImage);
    }

    public void DeactivateAll()
    {
        foreach (var cheat in Cheats) cheat.DeactivateQuietly();
    }
}
