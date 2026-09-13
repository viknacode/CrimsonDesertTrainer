using System.Globalization;
using System.Windows.Input;
using CrimsonTrainer.Infrastructure;

namespace CrimsonTrainer.ViewModels;

/// <summary>A labelled text box with an Apply action (stack amount, time scale, max health…).</summary>
public sealed class ValueFieldViewModel : ObservableObject
{
    private readonly Func<string, string?> _apply;   // returns an error message, or null on success
    private readonly Action<ValueFieldViewModel>? _onApplied;
    private string _text;
    private string? _error;
    private bool _isEnabled = true;

    public ValueFieldViewModel(string id, string label, string initialText, Func<string, string?> apply, string? hint = null, Action<ValueFieldViewModel>? onApplied = null)
    {
        Id = id;
        Label = label;
        Hint = hint;
        _text = initialText;
        _apply = apply;
        _onApplied = onApplied;
        ApplyCommand = new RelayCommand(Apply);
    }

    public string Id { get; }
    public string Label { get; }
    public string? Hint { get; }
    public ICommand ApplyCommand { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value)) Error = null;
        }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    public void Apply()
    {
        Error = _apply(Text);
        if (Error is null) _onApplied?.Invoke(this);
    }

    /// <summary>Re-applies the current text without reporting errors (used when re-attaching).</summary>
    public void ApplyQuietly()
    {
        try { _apply(Text); }
        catch (Exception) { /* the game may be gone; the next explicit Apply will report */ }
    }

    // ---- parsing helpers for callers ----

    public static bool TryParseLong(string text, out long value, long min = long.MinValue, long max = long.MaxValue)
    {
        text = text.Replace(",", "").Replace(".", "").Replace(" ", "");
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }

    public static bool TryParseFloat(string text, out float value)
    {
        text = text.Trim().Replace(',', '.');
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
    }

    public static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
