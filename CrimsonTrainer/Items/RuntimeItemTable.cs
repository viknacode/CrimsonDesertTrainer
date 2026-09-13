using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Items;

/// <summary>
/// The game's runtime item table (patch 2.01.00): a contiguous array of 8-byte rows
/// { itemKey (int32), dataOffset (int32) } whose position is the <b>runtime index</b> — the number
/// inventory slots store to say which item they hold (the old trainer's CSV called it RuntimeIdx).
/// Its first three rows (Stub Arrow 2200, Arrow 50001, Quiver 1002557) have stayed at indices
/// 0–2 across patches and are used to verify a candidate.
/// </summary>
public sealed class RuntimeItemTable
{
    private static readonly AobPattern Head = AobPattern.Parse("98 08 00 00 00 00 00 00 51 C3 00 00 ?? ?? 00 00 3D 4C 0F 00");

    /// <summary>
    /// Patch 2.01.00: a static pointer in .data leads to the table manager —
    /// <c>manager = [CrimsonDesert.exe+6C2E2E8]</c>, <c>[manager+08]</c> = row count, <c>[manager+28]</c> = rows.
    /// </summary>
    private const int ManagerPointerRva = 0x6C2E2E8;

    private readonly Dictionary<int, long> _keyByIndex;
    private readonly Dictionary<long, int> _indexByKey;

    private RuntimeItemTable(nint address, List<long> keys)
    {
        Address = address;
        _keyByIndex = new Dictionary<int, long>(keys.Count);
        _indexByKey = new Dictionary<long, int>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            _keyByIndex[i] = keys[i];
            _indexByKey.TryAdd(keys[i], i);
        }
    }

    public nint Address { get; }
    public int Count => _keyByIndex.Count;
    public IEnumerable<KeyValuePair<int, long>> Rows => _keyByIndex;

    public long? KeyOf(int runtimeIndex) => _keyByIndex.TryGetValue(runtimeIndex, out var key) ? key : null;
    public int? IndexOf(long itemKey) => _indexByKey.TryGetValue(itemKey, out var index) ? index : null;

    /// <summary>
    /// Finds the table: first through the static pointer (instant, verified against the first
    /// rows), then by scanning private memory. Null when it is not in memory (game still loading).
    /// </summary>
    internal static RuntimeItemTable? Locate(GameProcess game, Action<double>? progress = null)
    {
        if (LocateStatic(game) is { } viaPointer) return viaPointer;

        var matches = Head.ScanAllMemory(game, maxResults: 4, (done, total) => progress?.Invoke(total == 0 ? 1 : (double)done / total), privateOnly: true);
        foreach (var match in matches)
        {
            var keys = ReadRows(game, match, null);
            if (keys.Count >= 1000) return new RuntimeItemTable(match, keys);
        }
        return null;
    }

    private static RuntimeItemTable? LocateStatic(GameProcess game)
    {
        if (!game.TryReadPointer(game.ModuleBase + ManagerPointerRva, out nint manager) || manager == 0) return null;
        if (!game.TryReadPointer(manager + 0x28, out nint rows) || rows == 0) return null;
        if (!IsHead(game, rows)) return null;
        int? count = game.TryReadInt32(manager + 0x08, out int n) && n is > 0 and < 100_000 ? n : null;
        var keys = ReadRows(game, rows, count);
        return keys.Count >= 1000 ? new RuntimeItemTable(rows, keys) : null;
    }

    /// <summary>2200 (Stub Arrow), 50001 (Arrow), 1002557 (Quiver) — indices 0..2 in every known build.</summary>
    private static bool IsHead(GameProcess game, nint rows)
    {
        var head = new byte[20];
        return game.TryRead(rows, head, head.Length)
               && BitConverter.ToInt32(head, 0) == 2200
               && BitConverter.ToInt32(head, 8) == 50001
               && BitConverter.ToInt32(head, 16) == 1002557;
    }

    /// <summary>
    /// Reads rows: exactly <paramref name="count"/> when the manager says how many, otherwise
    /// until the data-offset column stops increasing (the end of the table).
    /// </summary>
    private static List<long> ReadRows(GameProcess game, nint address, int? count)
    {
        const int Chunk = 1024;
        var keys = new List<long>();
        var buf = new byte[Chunk * 8];
        int prevOffset = -1;
        int limit = count ?? 32 * Chunk;
        for (int page = 0; page * Chunk < limit; page++)
        {
            if (!game.TryRead(address + page * Chunk * 8, buf, buf.Length)) break;
            for (int i = 0; i < Chunk && keys.Count < limit; i++)
            {
                int key = BitConverter.ToInt32(buf, i * 8);
                int offset = BitConverter.ToInt32(buf, i * 8 + 4);
                if (key <= 0 || (count is null && offset < prevOffset)) return keys;
                prevOffset = offset;
                keys.Add(key);
            }
        }
        return keys;
    }
}
