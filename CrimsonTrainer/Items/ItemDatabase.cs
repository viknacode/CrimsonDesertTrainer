using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrimsonTrainer.Items;

public sealed record ItemInfo(
    [property: JsonPropertyName("itemKey")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("internalName")] string InternalName,
    [property: JsonPropertyName("maxStack")] long MaxStack,
    [property: JsonPropertyName("derived")] bool Derived = false)
{
    /// <summary>The display name is only derived from the internal name (item added after the name list was made).</summary>
    public bool IsDerived => Derived;

    public string IdText => Id.ToString();
    public string StackText => MaxStack >= 1_000_000_000 ? "∞" : MaxStack.ToString("N0");
}

/// <summary>The item list shipped with the old trainer (item_names.json), embedded in the exe.</summary>
public sealed class ItemDatabase
{
    private sealed record Root([property: JsonPropertyName("items")] List<ItemInfo> Items);

    public IReadOnlyList<ItemInfo> Items { get; }
    private readonly Dictionary<long, ItemInfo> _byId;

    private ItemDatabase(List<ItemInfo> items)
    {
        Items = items;
        _byId = new Dictionary<long, ItemInfo>();
        foreach (var item in items) _byId.TryAdd(item.Id, item);
    }

    public static ItemDatabase LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("item_names.json")
                           ?? throw new FileNotFoundException("Embedded item_names.json is missing.");
        var root = JsonSerializer.Deserialize<Root>(stream) ?? throw new InvalidDataException("item_names.json is empty.");
        return new ItemDatabase(root.Items);
    }

    public ItemInfo? Find(long id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Name / internal-name / id search. An exact id match and "starts with" name matches come first.
    /// </summary>
    public List<ItemInfo> Search(string query, int limit = 300)
    {
        query = query.Trim();
        if (query.Length == 0) return Items.Take(limit).ToList();

        bool numeric = long.TryParse(query, out long id);
        var starts = new List<ItemInfo>();
        var contains = new List<ItemInfo>();

        foreach (var item in Items)
        {
            if (numeric && item.Id == id)
            {
                starts.Insert(0, item);
                continue;
            }
            if (item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                starts.Add(item);
            else if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || item.InternalName.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || (numeric && item.IdText.StartsWith(query, StringComparison.Ordinal)))
                contains.Add(item);
            if (starts.Count + contains.Count >= limit * 2) break;
        }

        return starts.Concat(contains).Take(limit).ToList();
    }
}
