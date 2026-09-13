using System.Windows.Input;
using CrimsonTrainer.Cheats;
using CrimsonTrainer.Infrastructure;
using CrimsonTrainer.Memory;

namespace CrimsonTrainer.ViewModels;

public sealed record ScalePreset(string Label, float Value);

/// <summary>Body / head scale for one character (full-memory scan + float writes).</summary>
public sealed class BodyScaleViewModel : ObservableObject
{
    private readonly ITrainerHost _host;
    private readonly BodyScaleTarget _target;
    private GameProcess? _game;
    private bool _isScanning;
    private double _progress;
    private string _status = "Not scanned";

    internal BodyScaleViewModel(ITrainerHost host, BodyScaleTarget target)
    {
        _host = host;
        _target = target;
        Name = target.Name;
        BodyPresets = Presets(target.BodyDefault);
        HeadPresets = Presets(target.HeadDefault);

        Body = new ValueFieldViewModel($"scale.{Name}.body", "Body scale", ValueFieldViewModel.Format(target.BodyDefault),
            text => WriteScale(text, isBody: true));
        Head = new ValueFieldViewModel($"scale.{Name}.head", "Head scale", ValueFieldViewModel.Format(target.HeadDefault),
            text => WriteScale(text, isBody: false));

        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => _game is not null && !IsScanning);
        ResetCommand = new RelayCommand(Reset, () => IsFound);
        ApplyBodyPresetCommand = new RelayCommand(p => ApplyPreset(Body, p));
        ApplyHeadPresetCommand = new RelayCommand(p => ApplyPreset(Head, p));
    }

    public string Name { get; }
    public ValueFieldViewModel Body { get; }
    public ValueFieldViewModel Head { get; }
    public IReadOnlyList<ScalePreset> BodyPresets { get; }
    public IReadOnlyList<ScalePreset> HeadPresets { get; }
    public ICommand ScanCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ApplyBodyPresetCommand { get; }
    public ICommand ApplyHeadPresetCommand { get; }

    public bool IsFound => _target.IsFound && _game is not null;
    public bool CanScan => _game is not null && !IsScanning;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value)) Raise(nameof(CanScan));
        }
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    internal void Bind(GameProcess? game)
    {
        _game = game;
        _target.Forget();
        Status = game is null ? "Waiting for the game" : "Not scanned — load into the world first";
        Progress = 0;
        Raise(nameof(IsFound));
        Raise(nameof(CanScan));
    }

    private async Task ScanAsync()
    {
        var game = _game;
        if (game is null || IsScanning) return;

        IsScanning = true;
        Status = "Scanning all process memory…";
        Progress = 0;
        try
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            bool found = await Task.Run(() => _target.Scan(game, p => dispatcher.InvokeAsync(() => Progress = p)));
            if (found)
            {
                var (body, head) = _target.Read(game);
                Body.Text = ValueFieldViewModel.Format(body);
                Head.Text = ValueFieldViewModel.Format(head);
                Status = $"Found at 0x{(long)_target.Address:X}";
                _host.Log($"{Name} scale block found at 0x{(long)_target.Address:X}.", LogLevel.Success);
            }
            else
            {
                Status = "Not found — make sure the character is loaded, then retry";
                _host.Log($"{Name} scale pattern not found in memory.", LogLevel.Warning);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "Scan failed";
            _host.Log($"{Name} scan failed: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            IsScanning = false;
            Progress = 1;
            Raise(nameof(IsFound));
            RelayCommand.Requery();
        }
    }

    private string? WriteScale(string text, bool isBody)
    {
        if (!ValueFieldViewModel.TryParseFloat(text, out float value) || value <= 0 || value > 10) return "Enter a number between 0.1 and 10.";
        if (_game is null || !_target.IsFound) return "Scan first.";
        try
        {
            if (isBody) _target.WriteBody(_game, value); else _target.WriteHead(_game, value);
            _host.Log($"{Name} {(isBody ? "body" : "head")} scale → {ValueFieldViewModel.Format(value)}", LogLevel.Success);
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.Message;
        }
    }

    private void Reset()
    {
        Body.Text = ValueFieldViewModel.Format(_target.BodyDefault);
        Head.Text = ValueFieldViewModel.Format(_target.HeadDefault);
        Body.Apply();
        Head.Apply();
    }

    private static void ApplyPreset(ValueFieldViewModel field, object? parameter)
    {
        if (parameter is not ScalePreset preset) return;
        field.Text = ValueFieldViewModel.Format(preset.Value);
        field.Apply();
    }

    private static IReadOnlyList<ScalePreset> Presets(float defaultValue) =>
        new[] { new ScalePreset("Default", defaultValue) }
            .Concat(BodyScaleTarget.Presets.Select(p => new ScalePreset(p.Label, p.Value)))
            .ToList();
}
