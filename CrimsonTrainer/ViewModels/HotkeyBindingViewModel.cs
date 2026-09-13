using System.Windows.Input;
using CrimsonTrainer.Hotkeys;
using CrimsonTrainer.Infrastructure;

namespace CrimsonTrainer.ViewModels;

/// <summary>One assignable global hotkey ("Toggle", "Speed up", …) and the action it fires.</summary>
public sealed class HotkeyBindingViewModel : ObservableObject
{
    private Hotkey? _hotkey;
    private bool _isCapturing;
    private string? _error;

    public HotkeyBindingViewModel(string id, string label, Action action, Action<HotkeyBindingViewModel> beginCapture)
    {
        Id = id;
        Label = label;
        Action = action;
        CaptureCommand = new RelayCommand(() => beginCapture(this));
    }

    public string Id { get; }
    public string Label { get; }
    public Action Action { get; }
    public ICommand CaptureCommand { get; }

    /// <summary>RegisterHotKey id while registered.</summary>
    internal int? RegistrationId { get; set; }

    public Hotkey? Hotkey
    {
        get => _hotkey;
        set
        {
            if (Set(ref _hotkey, value)) Raise(nameof(Text));
        }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (Set(ref _isCapturing, value)) Raise(nameof(Text));
        }
    }

    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    public string Text => IsCapturing ? "Press keys…" : Hotkey?.ToString() ?? "Set key";
}
