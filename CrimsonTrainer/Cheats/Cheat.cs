using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Cheats;

public enum CheatSection
{
    Player,
    Inventory,
    World,
    Character,
}

/// <summary>
/// How a cheat is switched on: either it owns a whole script (<see cref="OwnInjection"/>) or it is
/// one feature of a script shared with other cheats (<see cref="SharedFeature"/>).
/// </summary>
internal interface IActivation
{
    bool IsOn { get; }
    bool IsResolved { get; }
    string? ResolveError { get; }
    IEnumerable<Injection> Injections { get; }

    void Resolve(byte[]? moduleImage);
    void Enable();
    void Disable();
    void DisableQuietly();
    void WriteVar(string name, long value);
    bool TryReadVar(string name, out long value);
}

/// <summary>The classic case: one script, enabled and disabled as a whole.</summary>
internal sealed class OwnInjection : IActivation
{
    private readonly Injection _injection;

    public OwnInjection(Injection injection) => _injection = injection;

    public bool IsOn => _injection.IsEnabled;
    public bool IsResolved => _injection.IsResolved;
    public string? ResolveError => _injection.ResolveError;
    public IEnumerable<Injection> Injections => new[] { _injection };

    public void Resolve(byte[]? moduleImage) => _injection.Resolve(moduleImage);
    public void Enable() => _injection.Enable();
    public void Disable() => _injection.Disable();
    public void DisableQuietly() => _injection.DisableQuietly();
    public void WriteVar(string name, long value) => _injection.WriteVar(name, value);
    public bool TryReadVar(string name, out long value) => _injection.TryReadVar(name, out value);
}

/// <summary>
/// A script used by several cheats at once (the 2.01.00 inventory-count hook). It is patched in
/// when the first feature turns on and removed when the last one turns off; each feature only
/// flips its own mode variable inside the cave.
/// </summary>
internal sealed class SharedInjection
{
    private readonly HashSet<string> _users = new();
    private byte[]? _resolvedWith;

    public SharedInjection(Injection injection) => Injection = injection;

    public Injection Injection { get; }

    public void Resolve(byte[]? moduleImage)
    {
        // Every feature asks for a resolve on attach; one scan per image is enough.
        if (moduleImage is not null && ReferenceEquals(_resolvedWith, moduleImage) && Injection.IsResolved) return;
        Injection.Resolve(moduleImage);
        _resolvedWith = moduleImage;
    }

    public void Acquire(string user)
    {
        if (!Injection.IsEnabled) Injection.Enable();
        _users.Add(user);
    }

    public void Release(string user)
    {
        _users.Remove(user);
        if (_users.Count == 0 && Injection.IsEnabled) Injection.Disable();
    }

    public void ReleaseQuietly(string user)
    {
        _users.Remove(user);
        if (_users.Count == 0) Injection.DisableQuietly();
    }
}

/// <summary>One feature of a <see cref="SharedInjection"/>: turning it on writes <c>modeVar = onValue</c>.</summary>
internal sealed class SharedFeature : IActivation
{
    private readonly SharedInjection _shared;
    private readonly string _id;
    private readonly string _modeVar;
    private readonly long _onValue;
    private readonly Injection? _extra;   // an additional private script enabled together with the feature

    public SharedFeature(SharedInjection shared, string id, string modeVar, long onValue, Injection? extra = null)
    {
        _shared = shared;
        _id = id;
        _modeVar = modeVar;
        _onValue = onValue;
        _extra = extra;
    }

    public bool IsOn { get; private set; }
    public bool IsResolved => _shared.Injection.IsResolved && (_extra?.IsResolved ?? true);
    public string? ResolveError => _shared.Injection.ResolveError ?? _extra?.ResolveError;
    public IEnumerable<Injection> Injections => _extra is null ? new[] { _shared.Injection } : new[] { _shared.Injection, _extra };

    public void Resolve(byte[]? moduleImage)
    {
        _shared.Resolve(moduleImage);
        _extra?.Resolve(moduleImage);
    }

    public void Enable()
    {
        if (IsOn) return;
        _shared.Acquire(_id);
        try
        {
            _extra?.Enable();
            _shared.Injection.WriteVar(_modeVar, _onValue);
        }
        catch
        {
            _extra?.DisableQuietly();
            _shared.Release(_id);
            throw;
        }
        IsOn = true;
    }

    public void Disable()
    {
        if (!IsOn) return;
        if (_shared.Injection.IsEnabled) _shared.Injection.WriteVar(_modeVar, 0);
        _extra?.Disable();
        _shared.Release(_id);
        IsOn = false;
    }

    public void DisableQuietly()
    {
        if (!IsOn) return;
        try { if (_shared.Injection.IsEnabled) _shared.Injection.WriteVar(_modeVar, 0); } catch { /* game gone */ }
        _extra?.DisableQuietly();
        _shared.ReleaseQuietly(_id);
        IsOn = false;
    }

    public void WriteVar(string name, long value)
    {
        if (_extra is not null && _extra.IsEnabled && _extra.TryReadVar(name, out _)) _extra.WriteVar(name, value);
        else _shared.Injection.WriteVar(name, value);
    }

    public bool TryReadVar(string name, out long value) =>
        (_extra is not null && _extra.TryReadVar(name, out value)) || _shared.Injection.TryReadVar(name, out value);
}

/// <summary>Base of everything the UI lists as a cheat: metadata + availability after the AOB scan.</summary>
internal abstract class Cheat
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required CheatSection Section { get; init; }
    public string? Description { get; init; }
    public string? Warning { get; init; }
    public string? HowTo { get; init; }
    public string? Credits { get; init; }

    public bool IsAvailable { get; protected set; }
    public string? UnavailableReason { get; protected set; }

    /// <summary>Runs the AOB scans. Called once per attach, on a worker thread.</summary>
    public abstract void Resolve(byte[]? moduleImage = null);

    /// <summary>Turns the cheat off, swallowing errors (shutdown / game exit).</summary>
    public abstract void DeactivateQuietly();

    /// <summary>Hooks whose addresses this cheat patches — used to explain conflicts.</summary>
    public abstract IEnumerable<Injection> Injections { get; }
}

/// <summary>An on/off cheat, optionally with variables the UI can edit while it is active.</summary>
internal sealed class ToggleCheat : Cheat
{
    private readonly IActivation _activation;
    private readonly Dictionary<string, long> _pendingVars = new();

    public ToggleCheat(Injection injection) : this(new OwnInjection(injection)) { }

    public ToggleCheat(IActivation activation) => _activation = activation;

    public bool IsOn => _activation.IsOn;
    public override IEnumerable<Injection> Injections => _activation.Injections;

    public override void Resolve(byte[]? moduleImage = null)
    {
        _activation.Resolve(moduleImage);
        IsAvailable = _activation.IsResolved;
        UnavailableReason = _activation.ResolveError;
    }

    public void SetOn(bool on)
    {
        if (on)
        {
            _activation.Enable();
            foreach (var (name, value) in _pendingVars) _activation.WriteVar(name, value);
        }
        else
        {
            _activation.Disable();
        }
    }

    /// <summary>Sets a cave variable now (if active) and remembers it for the next activation.</summary>
    public void SetVar(string name, long value)
    {
        _pendingVars[name] = value;
        if (_activation.IsOn) _activation.WriteVar(name, value);
    }

    public void SetVarSingle(string name, float value) => SetVar(name, BitConverter.SingleToUInt32Bits(value));

    public bool TryReadVar(string name, out long value) => _activation.TryReadVar(name, out value);

    public override void DeactivateQuietly() => _activation.DisableQuietly();
}

/// <summary>One option of a <see cref="ChoiceCheat"/>: what it activates and what to write into it.</summary>
internal sealed record ChoiceOption(string Key, string Label, IActivation? Activation, IReadOnlyDictionary<string, long>? Vars = null, string? Warning = null)
{
    public ChoiceOption(string key, string label, Injection? injection, IReadOnlyDictionary<string, long>? vars = null, string? warning = null)
        : this(key, label, injection is null ? null : new OwnInjection(injection), vars, warning) { }
}

/// <summary>
/// Mutually exclusive variants (v1 / v2, ×9 / ×99 / ×99999, durability 100 / no damage).
/// The first option is always "Off".
/// </summary>
internal sealed class ChoiceCheat : Cheat
{
    private readonly IReadOnlyList<ChoiceOption> _options;
    private ChoiceOption _selected;

    public ChoiceCheat(IReadOnlyList<ChoiceOption> options)
    {
        _options = options;
        _selected = options[0];
    }

    public IReadOnlyList<ChoiceOption> Options => _options;
    public ChoiceOption Selected => _selected;
    public bool IsOn => _selected.Activation is { IsOn: true };
    public override IEnumerable<Injection> Injections => _options.Select(o => o.Activation).OfType<IActivation>().SelectMany(a => a.Injections).Distinct();

    public override void Resolve(byte[]? moduleImage = null)
    {
        var errors = new List<string>();
        foreach (var activation in _options.Select(o => o.Activation).OfType<IActivation>())
        {
            activation.Resolve(moduleImage);
            if (!activation.IsResolved) errors.Add(activation.ResolveError ?? "not found");
        }
        IsAvailable = errors.Count == 0;
        UnavailableReason = errors.Count == 0 ? null : string.Join(" ", errors.Distinct());
    }

    public void Select(string key)
    {
        var option = _options.First(o => o.Key == key);
        if (option == _selected && (option.Activation is null || option.Activation.IsOn)) return;

        _selected.Activation?.Disable();
        _selected = _options[0];
        if (option.Activation is not null)
        {
            option.Activation.Enable();
            WriteVars(option);
        }
        _selected = option;
    }

    /// <summary>Advances to the next option (hotkey behaviour), wrapping back to Off.</summary>
    public string NextKey()
    {
        int index = _options.ToList().IndexOf(_selected);
        return _options[(index + 1) % _options.Count].Key;
    }

    private static void WriteVars(ChoiceOption option)
    {
        if (option.Vars is null || option.Activation is null) return;
        foreach (var (name, value) in option.Vars) option.Activation.WriteVar(name, value);
    }

    public override void DeactivateQuietly()
    {
        foreach (var activation in _options.Select(o => o.Activation).OfType<IActivation>()) activation.DisableQuietly();
        _selected = _options[0];
    }
}
