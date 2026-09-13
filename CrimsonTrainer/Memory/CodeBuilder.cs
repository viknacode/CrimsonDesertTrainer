namespace CrimsonTrainer.Memory;

/// <summary>
/// Tiny x64 byte emitter — just enough to build the code caves and jumps used by the
/// cheats. Instruction bytes are written out explicitly so they can be checked against
/// the disassembly in the Cheat Engine table.
/// </summary>
internal sealed class CodeBuilder
{
    private readonly List<byte> _code = new();
    private readonly Dictionary<string, int> _labels = new();
    private readonly List<(int Position, string Label)> _rel8Fixups = new();

    public int Position => _code.Count;

    public CodeBuilder Emit(params byte[] bytes)
    {
        _code.AddRange(bytes);
        return this;
    }

    public CodeBuilder Label(string name)
    {
        _labels[name] = _code.Count;
        return this;
    }

    /// <summary>jle rel8 (7E xx); the label is resolved in <see cref="Build"/>.</summary>
    public CodeBuilder JleShort(string label)
    {
        _code.Add(0x7E);
        _rel8Fixups.Add((_code.Count, label));
        _code.Add(0);
        return this;
    }

    /// <summary>
    /// jmp qword ptr [rip+0] ; dq target — a 14-byte absolute jump that reaches any
    /// address. This is what CE emits for "jmp newmem" when newmem is more than 2 GB away.
    /// </summary>
    public CodeBuilder JmpAbsolute(nint target)
    {
        Emit(0xFF, 0x25, 0x00, 0x00, 0x00, 0x00);
        _code.AddRange(BitConverter.GetBytes((long)target));
        return this;
    }

    public byte[] Build()
    {
        foreach (var (position, label) in _rel8Fixups)
        {
            if (!_labels.TryGetValue(label, out int target))
                throw new InvalidOperationException($"Undefined label '{label}'.");
            int rel = target - (position + 1);
            if (rel is < sbyte.MinValue or > sbyte.MaxValue)
                throw new InvalidOperationException($"Label '{label}' is out of rel8 range ({rel}).");
            _code[position] = unchecked((byte)(sbyte)rel);
        }
        return _code.ToArray();
    }

    /// <summary>E9 rel32 — 5-byte relative jump. <paramref name="to"/> must be within ±2 GB of <paramref name="from"/>.</summary>
    public static byte[] JmpRelative(nint from, nint to)
    {
        long rel = (long)to - ((long)from + 5);
        if (rel is < int.MinValue or > int.MaxValue)
            throw new InvalidOperationException($"Jump from 0x{(long)from:X} to 0x{(long)to:X} is out of rel32 range.");
        var bytes = new byte[5];
        bytes[0] = 0xE9;
        BitConverter.TryWriteBytes(bytes.AsSpan(1), (int)rel);
        return bytes;
    }

    public static byte[] AbsoluteJump(nint target) => new CodeBuilder().JmpAbsolute(target).Build();
}
