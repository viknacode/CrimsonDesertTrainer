using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Cheats;

/// <summary>
/// The character's level / EXP record, captured by the <see cref="TableScripts.LevelRecord"/> hook
/// whenever the game reads a character level (mul0's "getData": <c>cData+8</c> = level,
/// <c>cData+10</c> = EXP, both int32). The game keeps one record per character, so the last
/// captured one is whoever was read last — normally the player.
/// </summary>
internal sealed class LevelRecord
{
    private const int LevelField = 0x08, ExpField = 0x10;

    private readonly GameProcess _game;
    private readonly Injection _hook;

    public LevelRecord(GameProcess game, Injection hook)
    {
        _game = game;
        _hook = hook;
    }

    /// <summary>Record pointer captured by the hook, or 0 while nothing has been captured.</summary>
    public nint Record => _hook.TryReadVar("cData", out long p) ? (nint)p : 0;

    /// <summary>How many times the game read a level since the hook went in.</summary>
    public long Hits => _hook.TryReadVar("hits", out long n) ? n : 0;

    public (int Level, int Exp)? Read()
    {
        nint record = Record;
        if (record == 0) return null;
        var buf = new byte[0x14];
        if (!_game.TryRead(record, buf, buf.Length)) return null;
        int level = BitConverter.ToInt32(buf, LevelField), exp = BitConverter.ToInt32(buf, ExpField);
        return level is < 0 or > 10_000 ? null : (level, exp);
    }

    public bool WriteLevel(int level)
    {
        nint record = Record;
        if (record == 0) return false;
        _game.WriteInt32(record + LevelField, level);
        return true;
    }

    public bool WriteExp(int exp)
    {
        nint record = Record;
        if (record == 0) return false;
        _game.WriteInt32(record + ExpField, exp);
        return true;
    }
}
