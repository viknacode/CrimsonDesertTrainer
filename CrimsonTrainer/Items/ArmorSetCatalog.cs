using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrimsonTrainer.Items;

/// <summary>One piece of an armor set: the equipment slot it fills and the item key that fills it.</summary>
public sealed record ArmorPiece(
    [property: JsonPropertyName("slot")] string Slot,
    [property: JsonPropertyName("key")] long Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("variants")] int Variants)
{
    public string SlotLabel => Slot switch
    {
        "head" => "Head",
        "body" => "Body",
        "hands" => "Hands",
        "feet" => "Feet",
        "back" => "Cloak",
        _ => Slot,
    };
}

/// <summary>
/// An armor set from the vulkk.com catalog (name, class, resistance, where it drops, picture)
/// with its pieces resolved against item_names.json — one item per equipment slot.
/// </summary>
public sealed record ArmorSet(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("character")] string Character,
    [property: JsonPropertyName("armorClass")] string ArmorClass,
    [property: JsonPropertyName("resistance")] string Resistance,
    [property: JsonPropertyName("abyssGear")] string AbyssGear,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("pieces")] List<ArmorPiece> Pieces)
{
    public bool IsDamiane => Character.StartsWith("Damiane", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The set catalog shipped with the trainer (armor_sets.json, built from the vulkk.com catalog).</summary>
public sealed class ArmorSetCatalog
{
    private sealed record Root(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("sets")] List<ArmorSet> Sets);

    public string Source { get; }
    public IReadOnlyList<ArmorSet> Sets { get; }

    private ArmorSetCatalog(string source, List<ArmorSet> sets)
    {
        Source = source;
        Sets = sets;
    }

    public static ArmorSetCatalog LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("armor_sets.json")
                           ?? throw new FileNotFoundException("Embedded armor_sets.json is missing.");
        var root = JsonSerializer.Deserialize<Root>(stream) ?? throw new InvalidDataException("armor_sets.json is empty.");
        return new ArmorSetCatalog(root.Source, root.Sets);
    }
}
