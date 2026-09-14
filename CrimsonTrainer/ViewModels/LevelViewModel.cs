using System.Collections.ObjectModel;
using CrimsonTrainer.Cheats;
using CrimsonTrainer.Infrastructure;

namespace CrimsonTrainer.ViewModels;

/// <summary>
/// Level / EXP editor on top of the <see cref="LevelRecord"/> hook: shows the captured numbers
/// live and writes what is typed into the fields. Needs the "Level &amp; EXP editor" hook on.
/// </summary>
public sealed class LevelViewModel : ObservableObject
{
    private readonly ITrainerHost _host;
    private readonly ToggleCheatViewModel _hook;
    private LevelRecord? _record;
    private string _statusText = "Turn the hook on, then open the character screen.";
    private string _levelText = "—", _expText = "—";
    private long _lastHits = -1;

    internal LevelViewModel(ITrainerHost host, ToggleCheatViewModel hook)
    {
        _host = host;
        _hook = hook;
        hook.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ToggleCheatViewModel.IsOn) or nameof(ToggleCheatViewModel.IsAvailable))
            {
                Raise(nameof(IsOn));
                if (!hook.IsOn) Clear();
            }
        };
        Fields = new ObservableCollection<ValueFieldViewModel>
        {
            new("player.level", "Level", "", text => Apply(text, "Level", v => _record!.WriteLevel(v)), hint: "1 – 999. Takes effect after the game re-reads the record (reopen the screen)."),
            new("player.exp", "EXP", "", text => Apply(text, "EXP", v => _record!.WriteExp(v)), hint: "Raw EXP value of the current level."),
        };
    }

    public ToggleCheatViewModel Hook => _hook;
    public ObservableCollection<ValueFieldViewModel> Fields { get; }
    public bool IsOn => _hook.IsOn;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string LevelText
    {
        get => _levelText;
        private set => Set(ref _levelText, value);
    }

    public string ExpText
    {
        get => _expText;
        private set => Set(ref _expText, value);
    }

    internal void Bind(LevelRecord? record)
    {
        _record = record;
        Clear();
    }

    /// <summary>~5×/s from the session timer.</summary>
    internal void Tick()
    {
        if (_record is null || !_hook.IsOn) return;
        var values = _record.Read();
        long hits = _record.Hits;
        if (values is null)
        {
            StatusText = hits == 0 ? "Waiting for the game to read a level — open the character / status screen." : "Record captured but not readable right now.";
            LevelText = ExpText = "—";
            return;
        }
        var (level, exp) = values.Value;
        LevelText = level.ToString("N0");
        ExpText = exp.ToString("N0");
        StatusText = $"Record at 0x{(long)_record.Record:X} · read {hits:N0}× by the game";
        if (hits != _lastHits)
        {
            _lastHits = hits;
            if (string.IsNullOrEmpty(Fields[0].Text)) Fields[0].Text = level.ToString();
            if (string.IsNullOrEmpty(Fields[1].Text)) Fields[1].Text = exp.ToString();
        }
    }

    private string? Apply(string text, string what, Func<int, bool> write)
    {
        if (!ValueFieldViewModel.TryParseLong(text, out long value, 0, int.MaxValue)) return "Enter a whole number.";
        if (_record is null || !_hook.IsOn) return "Turn the Level & EXP hook on first.";
        if (_record.Record == 0) return "Nothing captured yet — open the character screen so the game reads your level.";
        if (!write((int)value)) return "Could not write the record.";
        _host.Log($"{what} → {value:N0}. Reopen the character screen to see it.", LogLevel.Success);
        return null;
    }

    private void Clear()
    {
        _lastHits = -1;
        LevelText = ExpText = "—";
        StatusText = "Turn the hook on, then open the character screen.";
        foreach (var f in Fields) f.Text = "";
    }
}
