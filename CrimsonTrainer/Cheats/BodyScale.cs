using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Cheats;

/// <summary>
/// "Character Body/Head Scale" from the table: a full-process AOB scan (CE Lua AOBScan) for a
/// pair of float constants, then direct float writes. Once a value is changed the pattern no
/// longer matches, so the address is kept for the life of the process.
/// </summary>
internal sealed class BodyScaleTarget
{
    public required string Name { get; init; }
    public required AobPattern Pattern { get; init; }
    public required int BodyOffset { get; init; }
    public required int HeadOffset { get; init; }
    public required float BodyDefault { get; init; }
    public required float HeadDefault { get; init; }

    public nint Address { get; private set; }
    public bool IsFound => Address != 0;

    public static readonly (float Value, string Label)[] Presets =
    {
        (0.5f, "Small"), (0.75f, "Slim"), (1.0f, "Normal"), (1.25f, "Large"),
        (1.5f, "XL"), (2.0f, "Giant"), (3.0f, "Absurd"),
    };

    /// <summary>Scans every readable region; returns false when the pattern is not in memory.</summary>
    public bool Scan(GameProcess game, Action<double>? progress = null)
    {
        var matches = Pattern.ScanAllMemory(game, maxResults: 1, (done, total) => progress?.Invoke(total == 0 ? 1 : (double)done / total));
        Address = matches.Count > 0 ? matches[0] : 0;
        return IsFound;
    }

    public void Forget() => Address = 0;

    public (float Body, float Head) Read(GameProcess game) =>
        (game.ReadSingle(Address + BodyOffset), game.ReadSingle(Address + HeadOffset));

    public void WriteBody(GameProcess game, float value) => game.WriteSingle(Address + BodyOffset, value);
    public void WriteHead(GameProcess game, float value) => game.WriteSingle(Address + HeadOffset, value);

    public static BodyScaleTarget Kliff() => new()
    {
        Name = "Kliff",
        Pattern = AobPattern.Parse("42 77 4A 83 3F 1F 85 6B 3F FF"),
        BodyOffset = 1, HeadOffset = 5,
        BodyDefault = 1.025709987f, HeadDefault = 0.9200000167f,
    };

    public static BodyScaleTarget Damiane() => new()
    {
        Name = "Damiane",
        Pattern = AobPattern.Parse("D7 A3 70 3F 5C 8F 82 3F FF"),
        BodyOffset = 0, HeadOffset = 4,
        BodyDefault = 0.9399999976f, HeadDefault = 1.019999981f,
    };
}
