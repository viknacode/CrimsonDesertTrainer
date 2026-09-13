using CrimsonTrainer.Items;
using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Cheats;

/// <summary>
/// Rewrites the slot capacity of the player's main inventory containers (both copies the
/// game keeps). See <see cref="InventoryContainer"/> for the layout.
/// </summary>
internal sealed class InventorySlots
{
    public const short MainInventoryType = 1;
    public const int MinSlots = 50;
    public const int DefaultSlots = 1000;

    /// <summary>The pool is 1,460 entries; stay under it so the game never has to grow it.</summary>
    public const int MaxSlots = 1400;

    private readonly List<InventoryContainer> _containers = new();
    private readonly Dictionary<nint, (short Capacity, short Bonus, short BonusA)> _originals = new();

    public IReadOnlyList<InventoryContainer> Containers => _containers;
    public bool IsFound => _containers.Count > 0;

    public void Forget()
    {
        _containers.Clear();
        _originals.Clear();
    }

    /// <summary>Keeps the main-inventory containers out of a scan result (remembering their original values).</summary>
    public bool Use(IEnumerable<InventoryContainer> all)
    {
        Forget();
        foreach (var c in all.Where(c => c.Type == MainInventoryType))
        {
            _containers.Add(c);
            _originals[c.Address] = (c.Capacity, c.Bonus, c.BonusA);
        }
        return IsFound;
    }

    public bool Scan(GameProcess game, Action<double>? progress = null) => Use(InventoryScanner.ScanAll(game, progress));

    /// <summary>Fresh header values for the first container (the one the display follows).</summary>
    public InventoryContainer? ReadPrimary(GameProcess game) =>
        _containers.Count == 0 ? null : InventoryContainer.Read(game, _containers[0].Address);

    /// <summary>
    /// Sets the slot capacity of every container found, moving the bonus fields by the same
    /// amount so a recalculation (base + bonus) lands on the same number.
    /// </summary>
    public int Apply(GameProcess game, int slots)
    {
        int written = 0;
        foreach (var c in _containers)
        {
            var now = InventoryContainer.Read(game, c.Address);
            if (now is null || now.Type != MainInventoryType || now.Pool != c.Pool) continue;   // container moved: leave it alone
            int delta = slots - now.Capacity;
            if (delta == 0) continue;
            WriteHeader(game, c.Address, (short)slots, (short)(now.Bonus + delta), (short)(now.BonusA + delta));
            written++;
        }
        return written;
    }

    /// <summary>Puts the values read at scan time back.</summary>
    public int Restore(GameProcess game)
    {
        int written = 0;
        foreach (var c in _containers)
        {
            if (!_originals.TryGetValue(c.Address, out var o)) continue;
            var now = InventoryContainer.Read(game, c.Address);
            if (now is null || now.Type != MainInventoryType || now.Pool != c.Pool) continue;
            WriteHeader(game, c.Address, o.Capacity, o.Bonus, o.BonusA);
            written++;
        }
        return written;
    }

    private static void WriteHeader(GameProcess game, nint address, short capacity, short bonus, short bonusA)
    {
        var bytes = new byte[6];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), capacity);
        BitConverter.TryWriteBytes(bytes.AsSpan(2), bonus);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), bonusA);
        game.Write(address + 0x14, bytes);
    }
}
