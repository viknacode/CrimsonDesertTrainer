using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using CrimsonTrainer.Cheats;
using CrimsonTrainer.Infrastructure;
using CrimsonTrainer.Settings;

namespace CrimsonTrainer.ViewModels;

public sealed class WaypointViewModel : ObservableObject
{
    private string _name;

    internal WaypointViewModel(string name, Vector3F position)
    {
        _name = name;
        Position = position;
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public Vector3F Position { get; }
    public string PositionText => Position.ToString();

    internal WaypointSetting ToSetting() => new() { Name = Name, X = Position.X, Y = Position.Y, Z = Position.Z };
}

/// <summary>
/// Teleport: live position readout, go-to-coordinates, saved waypoints and the nudge hotkeys
/// used to prove which position copy the game obeys. Everything needs Player tracking on.
/// </summary>
public sealed class TeleportViewModel : ObservableObject
{
    private readonly ITrainerHost _host;
    private readonly ToggleCheatViewModel _tracking;
    private readonly AppSettings _settings;
    private PlayerPosition? _position;
    private Vector3F? _last;
    private string _positionText = "—";
    private string _targetX = "", _targetY = "", _targetZ = "";
    private string _newWaypointName = "";
    private WaypointViewModel? _selectedWaypoint;
    private int _tick;
    private bool _isActive;

    internal TeleportViewModel(ITrainerHost host, ToggleCheatViewModel tracking, AppSettings settings)
    {
        _host = host;
        _tracking = tracking;
        _settings = settings;
        foreach (var w in settings.Waypoints) Waypoints.Add(new WaypointViewModel(w.Name, new Vector3F(w.X, w.Y, w.Z)));

        GoCommand = new RelayCommand(() => _ = GoAsync(), () => IsReady);
        UseCurrentCommand = new RelayCommand(UseCurrent, () => _last is not null);
        SaveWaypointCommand = new RelayCommand(SaveWaypoint, () => _last is not null);
        GoToWaypointCommand = new RelayCommand(() => _ = GoToWaypointAsync(), () => IsReady && _selectedWaypoint is not null);
        DeleteWaypointCommand = new RelayCommand(DeleteWaypoint, () => _selectedWaypoint is not null);

        Bindings = new[]
        {
            new HotkeyBindingViewModel("teleport.nudge_x", "Nudge +5 on X", () => _ = NudgeAsync(new Vector3F(5, 0, 0)), host.BeginCapture),
            new HotkeyBindingViewModel("teleport.nudge_y", "Nudge +5 on Y", () => _ = NudgeAsync(new Vector3F(0, 5, 0)), host.BeginCapture),
            new HotkeyBindingViewModel("teleport.nudge_z", "Nudge +5 on Z", () => _ = NudgeAsync(new Vector3F(0, 0, 5)), host.BeginCapture),
            new HotkeyBindingViewModel("teleport.go", "Teleport to coordinates", () => _ = GoAsync(), host.BeginCapture),
            new HotkeyBindingViewModel("teleport.waypoint", "Teleport to selected waypoint", () => _ = GoToWaypointAsync(), host.BeginCapture),
            new HotkeyBindingViewModel("teleport.save", "Save position as waypoint", SaveWaypoint, host.BeginCapture),
        };
        tracking.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ToggleCheatViewModel.IsOn) or nameof(ToggleCheatViewModel.IsAvailable)) { Raise(nameof(IsReady)); Raise(nameof(CanTrack)); RelayCommand.Requery(); }
        };
    }

    public ObservableCollection<WaypointViewModel> Waypoints { get; } = new();
    public IReadOnlyList<HotkeyBindingViewModel> Bindings { get; }
    public ICommand GoCommand { get; }
    public ICommand UseCurrentCommand { get; }
    public ICommand SaveWaypointCommand { get; }
    public ICommand GoToWaypointCommand { get; }
    public ICommand DeleteWaypointCommand { get; }

    /// <summary>The Teleport section is on screen: make sure Player tracking is on so the position resolves.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (Set(ref _isActive, value) && value && _position is not null && !_tracking.IsOn && _tracking.IsAvailable)
            {
                _host.Log("Teleport: turning Player tracking on (the position hangs off the player pointer).", LogLevel.Info);
                _ = _tracking.EnsureOnAsync();
            }
        }
    }

    public bool CanTrack => _tracking.IsAvailable;
    public bool IsReady => _position is not null && _tracking.IsOn;

    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    public string TargetX { get => _targetX; set => Set(ref _targetX, value); }
    public string TargetY { get => _targetY; set => Set(ref _targetY, value); }
    public string TargetZ { get => _targetZ; set => Set(ref _targetZ, value); }

    public string NewWaypointName
    {
        get => _newWaypointName;
        set => Set(ref _newWaypointName, value);
    }

    public WaypointViewModel? SelectedWaypoint
    {
        get => _selectedWaypoint;
        set
        {
            if (Set(ref _selectedWaypoint, value)) RelayCommand.Requery();
        }
    }

    internal void Bind(PlayerPosition? position)
    {
        _position = position;
        _last = null;
        PositionText = "—";
        Raise(nameof(IsReady));
        Raise(nameof(CanTrack));
        RelayCommand.Requery();
    }

    /// <summary>~5×/s from the session timer: refresh the readout while tracking is on.</summary>
    internal void Tick()
    {
        if (_position is null) return;
        if (!_tracking.IsOn)
        {
            if (_last is not null) { _last = null; RelayCommand.Requery(); }
            PositionText = _tracking.IsAvailable ? "Player tracking is off — turn it on (Player section)" : "Player tracking not available in this game version";
            return;
        }
        if (++_tick % 2 != 0) return;   // 2-3 reads per second are plenty
        Vector3F? now;
        try { now = _position.Read(); }
        catch (Exception) { now = null; }
        bool had = _last is not null;
        _last = now;
        PositionText = now?.ToString() ?? "waiting for the player pointer — move a little in the game (it pauses while unfocused)";
        if (had != (now is not null)) RelayCommand.Requery();
    }

    private async Task<bool> EnsureTrackingAsync()
    {
        if (_tracking.IsOn) return true;
        if (!_tracking.IsAvailable) { _host.Log("Teleport needs Player tracking, which is not available in this game version.", LogLevel.Error); return false; }
        return await _tracking.EnsureOnAsync();
    }

    private async Task NudgeAsync(Vector3F delta)
    {
        if (!await EnsureTrackingAsync() || _position is null) return;
        var now = _position.Read();
        if (now is null) { _host.Log("Nudge: position not readable yet — move a little so the player pointer is captured.", LogLevel.Warning); return; }
        var target = new Vector3F(now.Value.X + delta.X, now.Value.Y + delta.Y, now.Value.Z + delta.Z);
        int written = 0;
        await _host.RunAsync(() => written = _position.Write(target), null);
        _host.Log($"Nudge {delta}: {now} → {target} ({written} copies written, {_position.OtherCopies} of them outside the actor). Did the character move?", LogLevel.Info);
    }

    private async Task TeleportAsync(Vector3F target, string what)
    {
        if (!await EnsureTrackingAsync() || _position is null) return;
        var from = _position.Read();
        int written = 0;
        bool ok = await _host.RunAsync(() => written = _position.Write(target), null);
        if (ok) _host.Log(written == 0 ? "Teleport: the position could not be resolved — move a little and try again." : $"Teleport → {what} {target} (from {from?.ToString() ?? "?"}).", written == 0 ? LogLevel.Warning : LogLevel.Success);
    }

    private static bool TryParse(string s, out float v) =>
        float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && float.IsFinite(v);

    private async Task GoAsync()
    {
        if (!TryParse(TargetX, out float x) || !TryParse(TargetY, out float y) || !TryParse(TargetZ, out float z))
        {
            _host.Log("Teleport: enter X, Y and Z (numbers, e.g. -6491.5).", LogLevel.Error);
            return;
        }
        await TeleportAsync(new Vector3F(x, y, z), "coordinates");
    }

    private void UseCurrent()
    {
        if (_last is not { } p) return;
        TargetX = p.X.ToString("0.##", CultureInfo.InvariantCulture);
        TargetY = p.Y.ToString("0.##", CultureInfo.InvariantCulture);
        TargetZ = p.Z.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void SaveWaypoint()
    {
        if (_last is not { } p) { _host.Log("Save waypoint: no position yet (turn Player tracking on and move a little).", LogLevel.Warning); return; }
        string name = string.IsNullOrWhiteSpace(NewWaypointName) ? $"Waypoint {Waypoints.Count + 1}" : NewWaypointName.Trim();
        var wp = new WaypointViewModel(name, p);
        Waypoints.Add(wp);
        SelectedWaypoint = wp;
        NewWaypointName = "";
        Persist();
        _host.Log($"Waypoint \"{name}\" saved at {p}.", LogLevel.Success);
    }

    private void DeleteWaypoint()
    {
        if (_selectedWaypoint is null) return;
        var wp = _selectedWaypoint;
        Waypoints.Remove(wp);
        SelectedWaypoint = null;
        Persist();
        _host.Log($"Waypoint \"{wp.Name}\" removed.", LogLevel.Info);
    }

    private async Task GoToWaypointAsync()
    {
        if (_selectedWaypoint is null) { _host.Log("Teleport: select a waypoint first.", LogLevel.Warning); return; }
        await TeleportAsync(_selectedWaypoint.Position, $"\"{_selectedWaypoint.Name}\"");
    }

    private void Persist()
    {
        _settings.Waypoints = Waypoints.Select(w => w.ToSetting()).ToList();
        _host.SaveSettings();
    }
}
