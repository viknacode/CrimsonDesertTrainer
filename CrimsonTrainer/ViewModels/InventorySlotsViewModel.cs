using System.ComponentModel;
using System.Windows.Input;
using CrimsonTrainer.Cheats;
using CrimsonTrainer.Infrastructure;
using CrimsonTrainer.Items;
using CrimsonTrainer.Memory;

namespace CrimsonTrainer.ViewModels;

/// <summary>
/// "Inventory slots": finds the main inventory containers and rewrites their capacity
/// (the "239 / 240" in the inventory header). Keep re-applies the number every second so it
/// survives whatever the game recalculates.
/// </summary>
public sealed class InventorySlotsViewModel : ObservableObject
{
    private readonly ITrainerHost _host;
    private readonly InventorySlots _slots = new();
    private GameProcess? _game;
    private bool _isScanning, _keep;
    private double _progress;
    private string _status = "Waiting for the game";
    private string _currentText = "—";
    private int _lastCapacity = -1, _lastUsed = -1;
    private IReadOnlyList<InventoryContainer> _all = Array.Empty<InventoryContainer>();

    internal InventorySlotsViewModel(ITrainerHost host)
    {
        _host = host;
        Slots = new ValueFieldViewModel("inventory.slots", "Slot capacity", InventorySlots.DefaultSlots.ToString(), ApplyText,
            hint: $"{InventorySlots.MinSlots} – {InventorySlots.MaxSlots}. Each container holds 1460 entries, so {InventorySlots.MaxSlots} is the ceiling; other trainers use {InventorySlots.DefaultSlots}.");
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => CanScan);
        RestoreCommand = new RelayCommand(() => _ = RestoreAsync(), () => IsFound);
        Bindings = new[]
        {
            new HotkeyBindingViewModel("inventory.slots_apply", "Apply slot capacity", () => Slots.Apply(), host.BeginCapture),
            new HotkeyBindingViewModel("inventory.slots_keep", "Keep slot capacity", () => Keep = !Keep, host.BeginCapture),
        };
    }

    public ValueFieldViewModel Slots { get; }
    public ICommand ScanCommand { get; }
    public ICommand RestoreCommand { get; }
    public IReadOnlyList<HotkeyBindingViewModel> Bindings { get; }

    /// <summary>Every container the last scan found (all types, both copies); the inventory grid feeds on it.</summary>
    public IReadOnlyList<InventoryContainer> AllContainers => _all;
    public event Action? Scanned;

    public bool IsFound => _slots.IsFound && _game is not null;
    public bool CanScan => _game is not null && !IsScanning;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            Raise(nameof(CanScan));
            RelayCommand.Requery();
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

    /// <summary>"used / capacity" as the game currently has it.</summary>
    public string CurrentText
    {
        get => _currentText;
        private set => Set(ref _currentText, value);
    }

    /// <summary>Re-applies the capacity every second while on.</summary>
    public bool Keep
    {
        get => _keep;
        set
        {
            if (!Set(ref _keep, value)) return;
            if (value)
            {
                if (!IsFound) { _keep = false; Raise(nameof(Keep)); _host.Log("Inventory slots: press Find first.", LogLevel.Warning); return; }
                Slots.Apply();
                _host.Log($"Keep slot capacity → ON ({Slots.Text})", LogLevel.Success);
            }
            else _host.Log("Keep slot capacity → OFF (the number stays until the game changes it).", LogLevel.Info);
        }
    }

    internal void Bind(GameProcess? game)
    {
        _game = game;
        _slots.Forget();
        _all = Array.Empty<InventoryContainer>();
        Scanned?.Invoke();
        _keep = false;
        Raise(nameof(Keep));
        _lastCapacity = _lastUsed = -1;
        CurrentText = "—";
        Progress = 0;
        Status = game is null ? "Waiting for the game" : "Not found yet — load into the world, then press Find";
        Raise(nameof(IsFound));
        Raise(nameof(CanScan));
        RelayCommand.Requery();
        if (game is not null) _ = ScanAsync();
    }

    internal async Task ScanAsync()
    {
        var game = _game;
        if (game is null || IsScanning) return;
        IsScanning = true;
        Status = "Looking for the inventory…";
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        try
        {
            var started = DateTime.Now;
            var all = await Task.Run(() => InventoryScanner.ScanAll(game, p => dispatcher.InvokeAsync(() => Progress = p)));
            if (!ReferenceEquals(_game, game)) return;
            _all = all;
            bool found = _slots.Use(all);
            Scanned?.Invoke();
            double secs = (DateTime.Now - started).TotalSeconds;
            if (found)
            {
                var c = _slots.Containers[0];
                Status = $"{_slots.Containers.Count} container{(_slots.Containers.Count == 1 ? "" : "s")} found in {secs:0.0} s";
                _host.Log($"Inventory slots: {_slots.Containers.Count} main-inventory container(s) at {string.Join(", ", _slots.Containers.Select(x => $"0x{(long)x.Address:X}"))} — {c.Used} / {c.Capacity} slots (base {c.Capacity - c.Bonus} + bonus {c.Bonus}).", LogLevel.Success);
                Tick();
            }
            else
            {
                Status = "Inventory not found — load into the world with at least one item, then press Find";
                _host.Log("Inventory slots: no main-inventory container found in memory (is the game fully loaded?).", LogLevel.Warning);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Status = "Scan failed";
            _host.Log($"Inventory slots scan failed: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            IsScanning = false;
            Raise(nameof(IsFound));
            RelayCommand.Requery();
        }
    }

    private string? ApplyText(string text)
    {
        var game = _game;
        if (game is null) return "Attach to the game first.";
        if (!IsFound) return "Press Find first.";
        if (!ValueFieldViewModel.TryParseLong(text, out long value, InventorySlots.MinSlots, InventorySlots.MaxSlots))
            return $"Enter a whole number between {InventorySlots.MinSlots} and {InventorySlots.MaxSlots:N0}.";
        try
        {
            int written = _slots.Apply(game, (int)value);
            _host.Log(written == 0
                ? $"Slot capacity already {value:N0}."
                : $"Slot capacity → {value:N0} ({written} container{(written == 1 ? "" : "s")} written). Close and reopen the inventory to see it.", LogLevel.Success);
            _lastCapacity = -1;
            Tick();
            return null;
        }
        catch (Win32Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task RestoreAsync()
    {
        var game = _game;
        if (game is null || !IsFound) return;
        _keep = false;
        Raise(nameof(Keep));
        await _host.RunAsync(() => _slots.Restore(game), "Slot capacity restored to the game's own value.", LogLevel.Info);
        _lastCapacity = -1;
        Tick();
    }

    /// <summary>Once a second: refresh the readout and, with Keep on, put the capacity back.</summary>
    internal void Tick()
    {
        var game = _game;
        if (game is null || !_slots.IsFound || IsScanning) return;
        var c = _slots.ReadPrimary(game);
        if (c is null || c.Type != InventorySlots.MainInventoryType)
        {
            CurrentText = "lost — press Find";
            return;
        }
        if (c.Capacity != _lastCapacity || c.Used != _lastUsed)
        {
            _lastCapacity = c.Capacity;
            _lastUsed = c.Used;
            CurrentText = $"{c.Used:N0} / {c.Capacity:N0}";
        }
        if (_keep && ValueFieldViewModel.TryParseLong(Slots.Text, out long wanted, InventorySlots.MinSlots, InventorySlots.MaxSlots) && c.Capacity != wanted)
        {
            try { _slots.Apply(game, (int)wanted); }
            catch (Win32Exception ex) { _host.Log($"Keep slot capacity: {ex.Message}", LogLevel.Error); _keep = false; Raise(nameof(Keep)); }
        }
    }
}
