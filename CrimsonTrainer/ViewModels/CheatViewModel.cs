using System.Collections.ObjectModel;
using CrimsonTrainer.Cheats;
using CrimsonTrainer.Infrastructure;

namespace CrimsonTrainer.ViewModels;

/// <summary>What cheat view-models need from the session: serialized game operations, logging, hotkey capture.</summary>
internal interface ITrainerHost
{
    bool IsAttached { get; }

    /// <summary>Runs a game operation on a worker thread, one at a time. Errors are logged; returns success.</summary>
    Task<bool> RunAsync(Action operation, string? successMessage = null, LogLevel successLevel = LogLevel.Success);

    void Log(string message, LogLevel level);
    void BeginCapture(HotkeyBindingViewModel binding);
    void SaveSettings();
}

/// <summary>Base for a listed cheat: metadata, availability, hotkeys and editable fields.</summary>
public abstract class CheatViewModel : ObservableObject
{
    private bool _isAvailable;
    private string? _unavailableReason;
    private bool _isBusy;

    private protected CheatViewModel(ITrainerHost host, Cheat cheat)
    {
        Host = host;
        Id = cheat.Id;
        Name = cheat.Name;
        Section = cheat.Section;
        Description = cheat.Description;
        Warning = cheat.Warning;
        HowTo = cheat.HowTo;
        Credits = cheat.Credits;
        UnavailableReason = "Waiting for the game";
    }

    private protected ITrainerHost Host { get; }

    public string Id { get; }
    public string Name { get; }
    public CheatSection Section { get; }
    public string? Description { get; }
    public string? Warning { get; }
    public string? HowTo { get; }
    public string? Credits { get; }

    public ObservableCollection<HotkeyBindingViewModel> Bindings { get; } = new();
    public ObservableCollection<ValueFieldViewModel> Fields { get; } = new();
    public bool HasFields => Fields.Count > 0;

    /// <summary>The pattern was found in the attached game.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        protected set
        {
            if (Set(ref _isAvailable, value)) Raise(nameof(CanInteract));
        }
    }

    public string? UnavailableReason
    {
        get => _unavailableReason;
        protected set => Set(ref _unavailableReason, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (Set(ref _isBusy, value)) Raise(nameof(CanInteract));
        }
    }

    public bool CanInteract => IsAvailable && !IsBusy;

    /// <summary>True while the cheat is patched into the game (drives the status chip).</summary>
    public abstract bool IsActive { get; }

    /// <summary>Points the view-model at the cheat instance of a newly attached (or detached) process.</summary>
    internal abstract void Bind(Cheat cheat);

    /// <summary>Re-reads on/off state from the model (after game exit or shutdown).</summary>
    internal abstract void Refresh();

    /// <summary>Fires the primary hotkey action.</summary>
    internal abstract void Trigger();

    private protected void SetAvailability(Cheat cheat)
    {
        IsAvailable = cheat.IsAvailable;
        UnavailableReason = cheat.IsAvailable ? null : cheat.UnavailableReason ?? "Not found in this game version";
        foreach (var field in Fields) field.IsEnabled = true; // fields stay editable; values are applied when possible
    }

    internal HotkeyBindingViewModel AddBinding(string key, string label, Action action)
    {
        var binding = new HotkeyBindingViewModel($"{Id}.{key}", label, action, Host.BeginCapture);
        Bindings.Add(binding);
        return binding;
    }

    internal ValueFieldViewModel AddField(string key, string label, string initial, Func<string, string?> apply, string? hint = null)
    {
        var field = new ValueFieldViewModel($"{Id}.{key}", label, initial, apply, hint, _ => Host.SaveSettings());
        Fields.Add(field);
        Raise(nameof(HasFields));
        return field;
    }
}

/// <summary>An on/off cheat rendered as a switch.</summary>
public sealed class ToggleCheatViewModel : CheatViewModel
{
    private ToggleCheat _cheat;
    private bool _isOn;

    internal ToggleCheatViewModel(ITrainerHost host, ToggleCheat cheat) : base(host, cheat)
    {
        _cheat = cheat;
        AddBinding("toggle", "Toggle", Trigger);
    }

    internal ToggleCheat Cheat => _cheat;
    public override bool IsActive => IsOn;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            Raise();
            Raise(nameof(IsActive));
            _ = ApplyAsync(value);
        }
    }

    private async Task ApplyAsync(bool on)
    {
        if (!IsAvailable)
        {
            SetOnSilently(false);
            return;
        }

        IsBusy = true;
        try
        {
            var cheat = _cheat;
            bool ok = await Host.RunAsync(() => cheat.SetOn(on), $"{Name} → {(on ? "ON" : "OFF")}", on ? LogLevel.Success : LogLevel.Info);
            if (!ok) SetOnSilently(cheat.IsOn);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetOnSilently(bool on)
    {
        if (_isOn == on) return;
        _isOn = on;
        Raise(nameof(IsOn));
        Raise(nameof(IsActive));
    }

    /// <summary>Turns the cheat on when another feature needs it (e.g. live stats need player tracking).</summary>
    internal async Task<bool> EnsureOnAsync()
    {
        if (IsOn) return true;
        if (!IsAvailable) return false;
        IsBusy = true;
        try
        {
            var cheat = _cheat;
            bool ok = await Host.RunAsync(() => cheat.SetOn(true), $"{Name} → ON (required)", LogLevel.Info);
            SetOnSilently(cheat.IsOn);
            return ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal override void Bind(Cheat cheat)
    {
        _cheat = (ToggleCheat)cheat;
        SetAvailability(cheat);
        SetOnSilently(_cheat.IsOn);
        foreach (var field in Fields) field.ApplyQuietly();
    }

    internal override void Refresh() => SetOnSilently(_cheat.IsOn);

    internal override void Trigger()
    {
        if (CanInteract) IsOn = !IsOn;
    }
}

public sealed class ChoiceOptionViewModel : ObservableObject
{
    private readonly ChoiceCheatViewModel _owner;
    private bool _isSelected;

    internal ChoiceOptionViewModel(ChoiceCheatViewModel owner, ChoiceOption option)
    {
        _owner = owner;
        Key = option.Key;
        Label = option.Label;
        Warning = option.Warning;
    }

    public string Key { get; }
    public string Label { get; }
    public string? Warning { get; }
    public bool IsOff => Key == "off";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise();
            if (value) _owner.OnOptionChosen(this);
        }
    }

    internal void SetSelectedSilently(bool selected)
    {
        if (_isSelected == selected) return;
        _isSelected = selected;
        Raise(nameof(IsSelected));
    }
}

/// <summary>A cheat with mutually exclusive variants, rendered as a segmented control.</summary>
public sealed class ChoiceCheatViewModel : CheatViewModel
{
    private ChoiceCheat _cheat;

    internal ChoiceCheatViewModel(ITrainerHost host, ChoiceCheat cheat) : base(host, cheat)
    {
        _cheat = cheat;
        Options = cheat.Options.Select(o => new ChoiceOptionViewModel(this, o)).ToList();
        Options[0].SetSelectedSilently(true);
        AddBinding("cycle", "Next mode", Trigger);
    }

    public IReadOnlyList<ChoiceOptionViewModel> Options { get; }
    public ChoiceOptionViewModel Selected => Options.First(o => o.IsSelected);
    public string? SelectedWarning => Selected.Warning;
    public override bool IsActive => !Selected.IsOff;

    internal void OnOptionChosen(ChoiceOptionViewModel option)
    {
        foreach (var other in Options)
            if (!ReferenceEquals(other, option)) other.SetSelectedSilently(false);
        RaiseSelection();
        _ = ApplyAsync(option.Key);
    }

    private async Task ApplyAsync(string key)
    {
        if (!IsAvailable)
        {
            SelectSilently("off");
            return;
        }

        IsBusy = true;
        try
        {
            var cheat = _cheat;
            var label = Options.First(o => o.Key == key).Label;
            bool ok = await Host.RunAsync(() => cheat.Select(key), $"{Name} → {label}", key == "off" ? LogLevel.Info : LogLevel.Success);
            if (!ok) SelectSilently(cheat.Selected.Key);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SelectSilently(string key)
    {
        foreach (var option in Options) option.SetSelectedSilently(option.Key == key);
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        Raise(nameof(Selected));
        Raise(nameof(SelectedWarning));
        Raise(nameof(IsActive));
    }

    internal override void Bind(Cheat cheat)
    {
        _cheat = (ChoiceCheat)cheat;
        SetAvailability(cheat);
        SelectSilently(_cheat.Selected.Key);
    }

    internal override void Refresh() => SelectSilently(_cheat.Selected.Key);

    internal override void Trigger()
    {
        if (!CanInteract) return;
        int index = Options.ToList().IndexOf(Selected);
        Options[(index + 1) % Options.Count].IsSelected = true;
    }
}
