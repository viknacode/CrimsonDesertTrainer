using CrimsonTrainer.Memory;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace CrimsonTrainer.Cheats;

/// <summary>
/// One code-injection point: "aobscanmodule(Name,...)" + "Name: jmp freejumpmem+Slot / nop…".
/// </summary>
internal sealed class HookSite
{
    public required string Name { get; init; }
    public required AobPattern Pattern { get; init; }

    /// <summary>Injection address = pattern match + Offset (e.g. "MaxTrust+0E").</summary>
    public int Offset { get; init; }

    /// <summary>Bytes overwritten at the injection point (5-byte jmp + NOP padding).</summary>
    public required int Length { get; init; }

    /// <summary>Trampoline slot: freejumpmem+Slot (each slot is 16 bytes).</summary>
    public required int Slot { get; init; }

    /// <summary>Cave label the trampoline jumps to.</summary>
    public required string Entry { get; init; }

    /// <summary>
    /// Bytes that must be at the injection point (used when the point is reached by an
    /// <see cref="Offset"/> from a pattern elsewhere in the function, so a layout change is caught).
    /// </summary>
    public AobPattern? Expect { get; init; }

    public nint Address { get; internal set; }
    public byte[] Original { get; internal set; } = Array.Empty<byte>();
    public nint Return => Address + Length;
}

/// <summary>A range that is simply NOP-ed out while the cheat is active ("nop 12").</summary>
internal sealed class NopSite
{
    public required string Name { get; init; }
    public required AobPattern Pattern { get; init; }
    public int Offset { get; init; }
    public required int Length { get; init; }

    public nint Address { get; internal set; }
    public byte[] Original { get; internal set; } = Array.Empty<byte>();
}

/// <summary>
/// Wraps an Iced <see cref="Assembler"/> with the few conveniences the table scripts need:
/// named entry labels, named data variables and absolute jumps back into the game.
/// </summary>
internal sealed class CaveBuilder
{
    private readonly Dictionary<string, Label> _entries = new();
    private readonly Dictionary<string, Label> _vars = new();
    private readonly List<(string Name, Label Label, ulong Initial)> _varOrder = new();
    private readonly List<(Label Label, ulong Target)> _pointers = new();

    public Assembler Asm { get; } = new(64);

    /// <summary>Marks "name:" — the point a trampoline slot jumps to.</summary>
    public void Entry(string name)
    {
        var label = Asm.CreateLabel(name);
        Asm.Label(ref label);
        _entries[name] = label;
    }

    /// <summary>A qword variable stored inside the cave (CE "name: dq 0"). Usable as [Var("x")].</summary>
    public Label Var(string name, ulong initial = 0)
    {
        if (_vars.TryGetValue(name, out var existing)) return existing;
        var label = Asm.CreateLabel(name);
        _vars[name] = label;
        _varOrder.Add((name, label, initial));
        return label;
    }

    /// <summary>jmp to an absolute address anywhere in the process (jmp qword ptr [rip+ptr]).</summary>
    public void JmpAbs(ulong target)
    {
        var ptr = Asm.CreateLabel();
        Asm.jmp(__qword_ptr[ptr]);
        _pointers.Add((ptr, target));
    }

    /// <summary>"jmp return" for the given hook.</summary>
    public void Return(HookSite hook) => JmpAbs((ulong)hook.Return);

    /// <summary>Emits the pointer table and the data variables after the code.</summary>
    internal void Finish()
    {
        foreach (var (label, target) in _pointers)
        {
            var l = label;
            Asm.Label(ref l);
            Asm.dq(target);
        }
        foreach (var (name, label, initial) in _varOrder)
        {
            var l = label;
            Asm.Label(ref l);
            Asm.dq(initial);
            _vars[name] = l; // Label is a struct: keep the copy that carries the instruction index
        }
    }

    internal IReadOnlyDictionary<string, Label> Entries => _entries;
    internal IReadOnlyDictionary<string, Label> Vars => _vars;
}

/// <summary>
/// A complete auto-assembler script: one allocated cave, any number of hook sites that
/// jump into it (through freejumpmem slots) and optional NOP patches. Mirrors the
/// [ENABLE]/[DISABLE] halves of the Cheat Engine scripts.
/// </summary>
internal sealed class Injection
{
    private const int FreeJumpMemOffset = 0x500;   // define(freejumpmem,$process+500)
    private const int FreeJumpMemSize = 0x1000;    // fullaccess(freejumpmem,$1000)
    private const int CaveSize = 0x1000;

    private readonly GameProcess _game;
    private nint _cave;
    private Dictionary<string, nint> _varAddresses = new();

    public string Name { get; }
    public IReadOnlyList<HookSite> Hooks { get; }
    public IReadOnlyList<NopSite> Nops { get; }
    public Action<CaveBuilder, Injection> Body { get; }

    public bool IsResolved { get; private set; }
    public string? ResolveError { get; private set; }
    public bool IsEnabled { get; private set; }

    public Injection(GameProcess game, string name, IReadOnlyList<HookSite> hooks, Action<CaveBuilder, Injection> body, IReadOnlyList<NopSite>? nops = null)
    {
        _game = game;
        Name = name;
        Hooks = hooks;
        Nops = nops ?? Array.Empty<NopSite>();
        Body = body;
    }

    public HookSite Hook(string name) => Hooks.First(h => h.Name == name);

    /// <summary>Runs every aobscan of the script. Safe to call again after a failure.</summary>
    public void Resolve(byte[]? moduleImage = null)
    {
        IsResolved = false;
        ResolveError = null;
        try
        {
            foreach (var hook in Hooks)
            {
                hook.Address = Locate(hook.Pattern, hook.Offset, hook.Name, moduleImage);
                hook.Original = _game.Read(hook.Address, hook.Length);
                if (hook.Expect is { } expect && !expect.MatchesAt(hook.Original, 0))
                    throw new InvalidOperationException($"{hook.Name}: the code at pattern+{hook.Offset:X} is not the expected instruction in this game version.");
            }
            foreach (var nop in Nops)
            {
                nop.Address = Locate(nop.Pattern, nop.Offset, nop.Name, moduleImage);
                nop.Original = _game.Read(nop.Address, nop.Length);
            }
            IsResolved = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ResolveError = ex.Message;
        }
    }

    private nint Locate(AobPattern pattern, int offset, string name, byte[]? moduleImage)
    {
        var matches = moduleImage is null ? pattern.ScanModule(_game) : pattern.ScanBytes(moduleImage, _game.ModuleBase, 1);
        if (matches.Count == 0)
            throw new InvalidOperationException($"{name}: pattern not found in this game version.");
        return matches[0] + offset;
    }

    /// <summary>
    /// True when some other hook (this trainer's or Cheat Engine's) already sits on one of
    /// our injection points.
    /// </summary>
    public bool HasConflict(out string site)
    {
        foreach (var hook in Hooks)
        {
            if (!_game.Read(hook.Address, hook.Length).AsSpan().SequenceEqual(hook.Original))
            {
                site = hook.Name;
                return true;
            }
        }
        foreach (var nop in Nops)
        {
            if (!_game.Read(nop.Address, nop.Length).AsSpan().SequenceEqual(nop.Original))
            {
                site = nop.Name;
                return true;
            }
        }
        site = "";
        return false;
    }

    public void Enable()
    {
        if (IsEnabled) return;
        if (!IsResolved) throw new InvalidOperationException(ResolveError ?? $"{Name}: not resolved.");
        if (HasConflict(out var site))
            throw new InvalidOperationException($"{Name}: '{site}' is already hooked by another cheat (or the Cheat Engine table). Disable it first.");

        nint freeJumpMem = _game.ModuleBase + FreeJumpMemOffset;

        // alloc(newmem,...) — only scripts with hooks need a cave; NOP-only patches do not.
        _cave = Hooks.Count > 0 ? _game.Allocate(CaveSize) : 0;
        try
        {
            var builder = new CaveBuilder();
            Body(builder, this);
            builder.Finish();

            var writer = new ByteListCodeWriter();
            var result = builder.Asm.Assemble(writer, (ulong)_cave, BlockEncoderOptions.ReturnNewInstructionOffsets);
            if (writer.Bytes.Count > CaveSize)
                throw new InvalidOperationException($"{Name}: cave code is larger than {CaveSize} bytes.");

            _varAddresses = builder.Vars.ToDictionary(kv => kv.Key, kv => (nint)result.GetLabelRIP(kv.Value));
            if (writer.Bytes.Count > 0) _game.Write(_cave, writer.Bytes.ToArray());

            // fullaccess(freejumpmem,$1000) + "freejumpmem+XX: jmp entry"
            if (Hooks.Count > 0) _game.MakeExecutableWritable(freeJumpMem, FreeJumpMemSize);
            var patches = new List<(nint Address, byte[] Bytes)>();
            foreach (var hook in Hooks)
            {
                if (!builder.Entries.TryGetValue(hook.Entry, out var entry))
                    throw new InvalidOperationException($"{Name}: cave has no entry label '{hook.Entry}'.");
                nint slot = freeJumpMem + hook.Slot;
                _game.Write(slot, CodeBuilder.AbsoluteJump((nint)result.GetLabelRIP(entry)));

                // "Hook: jmp freejumpmem+XX" followed by NOPs for the rest of the stolen bytes.
                var bytes = new byte[hook.Length];
                CodeBuilder.JmpRelative(hook.Address, slot).CopyTo(bytes, 0);
                for (int i = 5; i < bytes.Length; i++) bytes[i] = 0x90;
                patches.Add((hook.Address, bytes));
            }
            foreach (var nop in Nops)
                patches.Add((nop.Address, Enumerable.Repeat((byte)0x90, nop.Length).ToArray()));

            using (_game.SuspendThreads())
                foreach (var (address, bytes) in patches)
                    _game.Write(address, bytes);
        }
        catch
        {
            _game.Free(_cave);
            _cave = 0;
            throw;
        }

        IsEnabled = true;
    }

    public void Disable()
    {
        if (!IsEnabled) return;

        using (_game.SuspendThreads())
        {
            foreach (var hook in Hooks) _game.Write(hook.Address, hook.Original);
            foreach (var nop in Nops) _game.Write(nop.Address, nop.Original);
        }

        // A thread that was already inside the cave still has to execute "jmp return".
        Thread.Sleep(100);
        _game.Free(_cave);
        _cave = 0;
        _varAddresses.Clear();
        IsEnabled = false;
    }

    /// <summary>Best-effort disable for shutdown / game exit.</summary>
    public void DisableQuietly()
    {
        try
        {
            if (!_game.HasExited) Disable();
            else IsEnabled = false;
        }
        catch
        {
            IsEnabled = false;
        }
    }

    // ---- cave variables ("registersymbol" values the table exposes as addresses) ----

    private nint VarAddress(string name)
    {
        if (!IsEnabled || !_varAddresses.TryGetValue(name, out var address))
            throw new InvalidOperationException($"{Name}: variable '{name}' is not available while the cheat is off.");
        return address;
    }

    public long ReadVar(string name) => _game.ReadInt64(VarAddress(name));
    public float ReadVarSingle(string name) => _game.ReadSingle(VarAddress(name));
    public void WriteVar(string name, long value) => _game.WriteInt64(VarAddress(name), value);
    public void WriteVarSingle(string name, float value) => _game.WriteSingle(VarAddress(name), value);

    public bool TryReadVar(string name, out long value)
    {
        value = 0;
        if (!IsEnabled || !_varAddresses.TryGetValue(name, out var address)) return false;
        var buf = new byte[8];
        if (!_game.TryRead(address, buf, 8)) return false;
        value = BitConverter.ToInt64(buf);
        return true;
    }

    private sealed class ByteListCodeWriter : CodeWriter
    {
        public List<byte> Bytes { get; } = new();
        public override void WriteByte(byte value) => Bytes.Add(value);
    }
}
