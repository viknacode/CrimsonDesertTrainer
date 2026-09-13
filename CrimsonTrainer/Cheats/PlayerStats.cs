using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Cheats;

public enum StatKind
{
    Health,
    Stamina,
    Spirit,
}

/// <summary>Raw values as stored by the game (fixed point: HUD value × 1000).</summary>
public sealed record PlayerSnapshot(
    long Health, long HealthMax, long HealthRegen,
    long Stamina, long StaminaMax, long StaminaRegen,
    long Spirit, long SpiritMax, long SpiritRegen,
    long Attack, long Defense);

/// <summary>
/// The "Player Stat Block" pointer chain from the table, rooted at the actor pointer that
/// the "Current Player Pointers" hook captures:
/// <code>[[[[cplayer+68]+20]+18]+58]</code>
/// (the .CT lists the offsets bottom-up).
///
/// Verified on patch 2.01.00 against the in-game character sheet: the block is an array of
/// 0x90-byte entries { id (4), pad, current (8), rate (8), max (8), … }, every value being the
/// HUD number × 1000. Health is the first entry (id 0). Stamina ("Vigor", id 22) and Spirit
/// ("Espírito", id 23) moved since the table was made (+510/+5A0 → +6C0/+750), so they are
/// located by id, with those offsets as fallback.
/// </summary>
internal sealed class PlayerStats
{
    public const long Scale = 1000;

    private const int EntryStride = 0x90;
    private const int BlockSize = 0x900;
    private const int CurrentField = 0x08, RegenField = 0x10, MaxField = 0x18;

    private const int HealthEntry = 0x000;
    private const int StaminaId = 22, StaminaEntryFallback = 0x6C0;
    private const int SpiritId = 23, SpiritEntryFallback = 0x750;

    public const int Attack = 0x0, Defense = 0x8;

    private static readonly int[] StatChain = { 0x20, 0x18, 0x58 };
    private static readonly int[] CombatChain = { 0x20, 0x18, 0x38 };

    private readonly GameProcess _game;
    private readonly Injection _pointers;

    public PlayerStats(GameProcess game, Injection playerPointers)
    {
        _game = game;
        _pointers = playerPointers;
    }

    /// <summary>Actor pointer captured by the hook, or 0 while nothing has been captured yet.</summary>
    public nint Actor => _pointers.TryReadVar("cplayer", out long actor) ? (nint)actor : 0;

    private nint StatBlock(nint actor) => _game.FollowChain(actor + 0x68, StatChain, 0);
    private nint CombatBlock(nint actor) => _game.FollowChain(actor + 0x68, CombatChain, 0);

    public PlayerSnapshot? Read()
    {
        nint actor = Actor;
        if (actor == 0) return null;
        nint stats = StatBlock(actor);
        nint combat = CombatBlock(actor);
        if (stats == 0 || combat == 0) return null;

        var block = new byte[BlockSize];
        var fight = new byte[0x10];
        if (!_game.TryRead(stats, block, block.Length) || !_game.TryRead(combat, fight, fight.Length)) return null;

        int health = HealthEntry;
        int stamina = FindEntry(block, StaminaId, StaminaEntryFallback);
        int spirit = FindEntry(block, SpiritId, SpiritEntryFallback);

        long L(int entry, int field) => BitConverter.ToInt64(block, entry + field);
        if (!IsValue(L(health, MaxField)) || !IsValue(L(stamina, MaxField)) || !IsValue(L(spirit, MaxField))) return null;

        return new PlayerSnapshot(
            L(health, CurrentField), L(health, MaxField), L(health, RegenField),
            L(stamina, CurrentField), L(stamina, MaxField), L(stamina, RegenField),
            L(spirit, CurrentField), L(spirit, MaxField), L(spirit, RegenField),
            BitConverter.ToInt64(fight, Attack), BitConverter.ToInt64(fight, Defense));
    }

    /// <summary>Writes the current or maximum value of a stat; false when the chain is not readable right now.</summary>
    public bool WriteStat(StatKind kind, bool max, long rawValue)
    {
        nint actor = Actor;
        if (actor == 0) return false;
        nint stats = StatBlock(actor);
        if (stats == 0) return false;

        var block = new byte[BlockSize];
        if (!_game.TryRead(stats, block, block.Length)) return false;
        int entry = kind switch
        {
            StatKind.Health => HealthEntry,
            StatKind.Stamina => FindEntry(block, StaminaId, StaminaEntryFallback),
            _ => FindEntry(block, SpiritId, SpiritEntryFallback),
        };
        _game.WriteInt64(stats + entry + (max ? MaxField : CurrentField), rawValue);
        return true;
    }

    public const int CombatCount = 42;

    public bool WriteCombat(int field, long rawValue)
    {
        nint actor = Actor;
        if (actor == 0) return false;
        nint combat = CombatBlock(actor);
        if (combat == 0) return false;
        _game.WriteInt64(combat + field, rawValue);
        return true;
    }

    /// <summary>The whole combat attribute array (42 × int64, HUD × 1000); null while unreadable.</summary>
    public long[]? ReadCombatArray()
    {
        nint actor = Actor;
        if (actor == 0) return null;
        nint combat = CombatBlock(actor);
        if (combat == 0) return null;
        var buf = new byte[CombatCount * 8];
        if (!_game.TryRead(combat, buf, buf.Length)) return null;
        var values = new long[CombatCount];
        for (int i = 0; i < CombatCount; i++) values[i] = BitConverter.ToInt64(buf, i * 8);
        return values;
    }

    /// <summary>Entry whose id matches, searched at the 0x90 stride; the table-era offset if none does.</summary>
    private static int FindEntry(byte[] block, int id, int fallback)
    {
        for (int entry = EntryStride; entry + MaxField + 8 <= block.Length; entry += EntryStride)
            if (BitConverter.ToInt32(block, entry) == id) return entry;
        return fallback;
    }

    private static bool IsValue(long v) => v is > 0 and < 100_000_000_000;
}
