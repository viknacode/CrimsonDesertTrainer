using System.Runtime.InteropServices;
using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Items;

/// <summary>
/// Header of one item container (patch 2.01.00), a 0x30-byte object:
/// <code>
///   +00  entry pool (pointer)            +08  pool entries (int32)   +0C  pool entries again
///   +10  type (int16, 1 = main inventory) +12  used slots            +14  slot capacity  ← "239 / 240"
///   +16  bonus slots (int16)              +18  bonus part A          +1A  bonus part B
/// </code>
/// capacity = base (50 for type 1) + bonus, bonus = A + B. Every pool holds 1,460 entries of
/// 0xC8 bytes, so the capacity can be raised far above 240 without the game reallocating.
/// The game keeps two copies of every container in sync (the one the item code writes and
/// the one the UI reads), so a scan normally finds two per type.
/// </summary>
public sealed record InventoryContainer(nint Address, nint Pool, int PoolEntries, short Type, short Used, short Capacity, short Bonus, short BonusA, short BonusB)
{
    public const int Size = 0x30;

    internal static InventoryContainer? Read(GameProcess game, nint address)
    {
        var b = new byte[Size];
        if (!game.TryRead(address, b, b.Length)) return null;
        return new InventoryContainer(address, (nint)BitConverter.ToInt64(b, 0), BitConverter.ToInt32(b, 8),
            BitConverter.ToInt16(b, 0x10), BitConverter.ToInt16(b, 0x12), BitConverter.ToInt16(b, 0x14),
            BitConverter.ToInt16(b, 0x16), BitConverter.ToInt16(b, 0x18), BitConverter.ToInt16(b, 0x1A));
    }

    /// <summary>Address of pool entry <paramref name="slot"/>.</summary>
    public nint Entry(int slot) => Pool + slot * InventoryScanner.EntryStride;
}

/// <summary>
/// One occupied pool entry (0xC8 bytes): <c>+00</c> instance id (-1 when free), <c>+08</c> runtime
/// item index (low dword), <c>+10</c> count, <c>+60</c> per-instance data, <c>+90</c> creation time
/// (<see cref="Created"/>, larger = obtained more recently).
/// </summary>
public sealed record InventoryEntry(int Slot, nint Address, long InstanceId, int RuntimeIndex, long Count, long Created = 0);

/// <summary>Finds every item container the player has and reads what is in them.</summary>
internal static class InventoryScanner
{
    public const int EntryStride = 0xC8;

    private const long PoolEntriesMin = 256, PoolEntriesMax = 65536;
    private const long HeapStart = 0x4_0000_0000;   // the game's own heap; memory below it is mostly GPU-shared and very slow to read

    /// <summary>
    /// Scans the game heap for container headers whose pool already holds items (templates and
    /// NPC containers sit empty). Usually well under two seconds; the slow low-address memory
    /// is only scanned when nothing was found above it.
    /// </summary>
    public static List<InventoryContainer> ScanAll(GameProcess game, Action<double>? progress = null)
    {
        var found = new List<InventoryContainer>();
        var regions = game.EnumerateReadableRegions(privateOnly: true, 1L << 32);
        var fast = regions.Where(r => (long)r.Start >= HeapStart).ToList();
        var slow = regions.Where(r => (long)r.Start < HeapStart).ToList();

        ScanRegions(game, fast, found, progress, 0, 0.3);
        if (found.Count == 0) ScanRegions(game, slow, found, progress, 0.3, 1);
        progress?.Invoke(1);
        return found;
    }

    private static void ScanRegions(GameProcess game, List<(nint Start, long Size)> regions, List<InventoryContainer> found, Action<double>? progress, double from, double to)
    {
        const int Chunk = 1 << 24, Overlap = InventoryContainer.Size;
        long total = Math.Max(1, regions.Sum(r => r.Size)), done = 0;
        var buf = new byte[Chunk + Overlap];
        foreach (var (start, size) in regions)
        {
            for (long offset = 0; offset < size; offset += Chunk)
            {
                int len = (int)Math.Min(buf.Length, size - offset);
                if (!game.TryRead(start + (nint)offset, buf, len)) break;
                ScanChunk(game, buf, len, (long)start + offset, found);
            }
            done += size;
            progress?.Invoke(from + (to - from) * done / total);
        }
    }

    private static void ScanChunk(GameProcess game, byte[] buf, int len, long baseAddress, List<InventoryContainer> found)
    {
        var longs = MemoryMarshal.Cast<byte, long>(buf.AsSpan(0, len & ~7));
        for (int i = 1; i + 3 < longs.Length; i++)
        {
            long q = longs[i];
            long lo = q & 0xFFFF_FFFF, hi = q >>> 32;
            if (lo != hi || lo < PoolEntriesMin || lo > PoolEntriesMax) continue;
            long pool = longs[i - 1];
            if (pool < 0x10000 || pool > 0x7FFF_FFFF_FFFF || (pool & 0xFFF) != 0) continue;

            int off = (i + 1) * 8;
            short type = BitConverter.ToInt16(buf, off), used = BitConverter.ToInt16(buf, off + 2), capacity = BitConverter.ToInt16(buf, off + 4);
            if (type < 0 || type > 63 || used <= 0 || capacity <= 0) continue;

            nint address = (nint)(baseAddress + (i - 1) * 8);
            if (found.Any(c => c.Address == address) || !LooksLikeEntryPool(game, (nint)pool)) continue;
            if (InventoryContainer.Read(game, address) is { } container) found.Add(container);
        }
    }

    /// <summary>The first pool entry is either the empty marker (-1 id) or a live item (tag 5|5 at +68).</summary>
    private static bool LooksLikeEntryPool(GameProcess game, nint pool)
    {
        var e = new byte[EntryStride];
        if (!game.TryRead(pool, e, e.Length)) return false;
        long id = BitConverter.ToInt64(e, 0), tag = BitConverter.ToInt64(e, 0x68), marker = BitConverter.ToInt64(e, 0x30);
        return (id == -1 && marker == -1) || tag == 0x5_0000_0005;
    }

    /// <summary>Every occupied entry of a container's pool, in slot order.</summary>
    public static List<InventoryEntry> ReadEntries(GameProcess game, InventoryContainer container)
    {
        var entries = new List<InventoryEntry>();
        int n = Math.Clamp(container.PoolEntries, 0, (int)PoolEntriesMax);
        var buf = new byte[n * EntryStride];
        if (n == 0 || !game.TryRead(container.Pool, buf, buf.Length)) return entries;
        for (int i = 0; i < n; i++)
        {
            int o = i * EntryStride;
            long id = BitConverter.ToInt64(buf, o);
            if (id == -1) continue;
            long count = BitConverter.ToInt64(buf, o + 0x10);
            int index = (int)(BitConverter.ToInt64(buf, o + 8) & 0xFFFF_FFFF);
            if (count < 0 || index < 0) continue;
            entries.Add(new InventoryEntry(i, container.Entry(i), id, index, count, BitConverter.ToInt64(buf, o + 0x90)));
        }
        return entries;
    }
}
