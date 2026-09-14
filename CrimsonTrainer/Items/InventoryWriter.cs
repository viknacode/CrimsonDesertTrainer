using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Items;

/// <summary>
/// Creates and rewrites inventory entries the way the game lays them out in patch 2.01.00
/// (0xC8 bytes per pool entry, every container copy written alike):
/// <code>
///   +00 instance id (int64, -1 = free)   +08 runtime index (uint16), +0A enhancement (byte)
///   +10 count (int64)                    +28 / +2C  1 / 1
///   +30 -1 while free, 0 when used       +38 per-build constant (copied from a live entry)
///   +40 0xFFFF                           +60 per-slot instance object (pre-allocated by the game — kept)
///   +68 5 / +6C 5                        +8C -1
///   +90 runtime index again (int64)      +98 creation time (unix seconds)
///   +A0 1 while free, 0 when used
/// </code>
/// The game keeps its own record of every item by <b>instance id</b>, so an entry that merely had
/// its index swapped (the old "put in slot") is not recognised as the new item — it cannot be
/// equipped and gets reverted. A fresh id in a range the game never allocates (500000+) fixes
/// that; free pool slots below the capacity are used first, exactly like the older trainers.
/// </summary>
internal sealed class InventoryWriter
{
    private const int Stride = InventoryScanner.EntryStride;
    private const long FreeId = -1;
    private const long IdBase = 500_000, IdLimit = 1_000_000;   // the game's own counter lives at 1,000,000+; legacy ids stop far below 500,000

    private readonly GameProcess _game;

    public InventoryWriter(GameProcess game) => _game = game;

    public sealed record Written(int Slot, long InstanceId, int Copies);

    /// <summary>Puts <paramref name="count"/> × item <paramref name="runtimeIndex"/> into the first free slot below the capacity; null when the inventory is full.</summary>
    public Written? Create(IReadOnlyList<InventoryContainer> copies, int runtimeIndex, long count)
    {
        var primary = copies[0];
        var pool = ReadPool(primary);
        int slot = -1;
        for (int i = 0; i < Math.Min(primary.Capacity, primary.PoolEntries); i++)
            if (BitConverter.ToInt64(pool, i * Stride) == FreeId) { slot = i; break; }
        if (slot < 0) return null;

        long id = NextId(pool);
        int constant = BuildConstant(pool);
        int written = 0;
        foreach (var copy in copies)
        {
            var address = copy.Entry(slot);
            var entry = _game.Read(address, Stride);
            if (BitConverter.ToInt64(entry, 0) != FreeId) continue;   // this copy already uses the slot: leave it alone
            Fill(entry, id, runtimeIndex, count, constant);
            _game.Write(address, entry);
            _game.WriteInt16(copy.Address + 0x12, (short)(copy.Used + 1));   // "used" in the header
            written++;
        }
        return written == 0 ? null : new Written(slot, id, written);
    }

    /// <summary>Rewrites an occupied slot as a brand-new item (fresh id) holding <paramref name="count"/> × <paramref name="runtimeIndex"/>.</summary>
    public Written? Replace(IReadOnlyList<InventoryContainer> copies, int slot, int runtimeIndex, long count)
    {
        var pool = ReadPool(copies[0]);
        long id = NextId(pool);
        int constant = BuildConstant(pool);
        int written = 0;
        foreach (var copy in copies)
        {
            var address = copy.Entry(slot);
            var entry = _game.Read(address, Stride);
            if (BitConverter.ToInt64(entry, 0) == FreeId) continue;   // emptied meanwhile
            Fill(entry, id, runtimeIndex, count, constant);
            _game.Write(address, entry);
            written++;
        }
        return written == 0 ? null : new Written(slot, id, written);
    }

    private byte[] ReadPool(InventoryContainer c) => _game.Read(c.Pool, Math.Clamp(c.PoolEntries, 1, 65536) * Stride);

    /// <summary>An id above every id already in the pool within our private range.</summary>
    private static long NextId(byte[] pool)
    {
        long max = IdBase;
        for (int o = 0; o + Stride <= pool.Length; o += Stride)
        {
            long id = BitConverter.ToInt64(pool, o);
            if (id >= IdBase && id < IdLimit && id > max) max = id;
        }
        return max + 1;
    }

    /// <summary>The per-build constant at +38, read from any occupied entry (0 if there is none).</summary>
    private static int BuildConstant(byte[] pool)
    {
        for (int o = 0; o + Stride <= pool.Length; o += Stride)
            if (BitConverter.ToInt64(pool, o) != FreeId) return BitConverter.ToInt32(pool, o + 0x38);
        return 0;
    }

    private static void Fill(byte[] e, long id, int runtimeIndex, long count, int constant)
    {
        long instance = BitConverter.ToInt64(e, 0x60);   // the slot's own per-instance object
        int instanceTag = BitConverter.ToInt32(e, 0x6C);
        Array.Clear(e);
        BitConverter.TryWriteBytes(e.AsSpan(0x00), id);
        BitConverter.TryWriteBytes(e.AsSpan(0x08), (ushort)runtimeIndex);
        BitConverter.TryWriteBytes(e.AsSpan(0x10), count);
        BitConverter.TryWriteBytes(e.AsSpan(0x28), 1);
        BitConverter.TryWriteBytes(e.AsSpan(0x2C), 1);
        BitConverter.TryWriteBytes(e.AsSpan(0x38), constant);
        BitConverter.TryWriteBytes(e.AsSpan(0x40), (ushort)0xFFFF);
        BitConverter.TryWriteBytes(e.AsSpan(0x60), instance);
        BitConverter.TryWriteBytes(e.AsSpan(0x68), 5);
        BitConverter.TryWriteBytes(e.AsSpan(0x6C), instanceTag == 0 ? 5 : instanceTag);
        BitConverter.TryWriteBytes(e.AsSpan(0x8C), -1);
        BitConverter.TryWriteBytes(e.AsSpan(0x90), (long)runtimeIndex);
        BitConverter.TryWriteBytes(e.AsSpan(0x98), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}
