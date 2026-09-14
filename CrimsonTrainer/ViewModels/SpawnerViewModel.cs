using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using CrimsonTrainer.Infrastructure;
using CrimsonTrainer.Items;
using CrimsonTrainer.Memory;

namespace CrimsonTrainer.ViewModels;

/// <summary>
/// One spawnable item: a row of the game's runtime item table, named from item_names.json when
/// possible. The icon is fetched the first time something binds to it.
/// </summary>
public sealed class SpawnItem : ObservableObject
{
    private readonly IconCache? _icons;
    private System.Windows.Media.ImageSource? _icon;
    private bool _iconRequested;

    internal SpawnItem(int index, long key, string name, string category, long maxStack, bool known, IconCache? icons)
    {
        Index = index; Key = key; Name = name; Category = category; MaxStack = maxStack; Known = known; _icons = icons;
    }

    public int Index { get; }
    public long Key { get; }
    public string Name { get; }
    public string Category { get; }
    public long MaxStack { get; }
    public bool Known { get; }
    public string IndexText => $"#{Index}";
    public string KeyText => Key.ToString();
    public string StackText => MaxStack >= 1_000_000_000 ? "∞" : MaxStack.ToString("N0");

    /// <summary>Icon from the community database; null until loaded (and stays null when unknown / offline).</summary>
    public System.Windows.Media.ImageSource? Icon
    {
        get
        {
            if (!_iconRequested) { _iconRequested = true; _ = LoadIconAsync(); }
            return _icon;
        }
    }

    public bool HasIcon => _icon is not null;

    private async Task LoadIconAsync()
    {
        if (_icons is null || !Known) return;
        var image = await _icons.GetAsync(Name);
        if (image is null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply(image);
        else await dispatcher.InvokeAsync(() => Apply(image));
    }

    private void Apply(System.Windows.Media.ImageSource image)
    {
        _icon = image;
        Raise(nameof(Icon));
        Raise(nameof(HasIcon));
    }
}

/// <summary>
/// Item spawner. Inventory slots reference items by <b>runtime index</b> (position in the game's
/// item table), so the list is built from the table read out of the running game and the
/// index — not the item key — is what gets written. Two ways to spawn:
/// direct write into the hovered slot, or the on-use/drop swap of the inventory hook.
/// </summary>
public sealed class SpawnerViewModel : ObservableObject
{
    private readonly ITrainerHost _host;
    private readonly ItemDatabase _db;
    private readonly IconCache _icons;
    private readonly ToggleCheatViewModel _swapper;
    private readonly ValueFieldViewModel _swapField;
    private readonly DispatcherTimer _debounce;
    private GameProcess? _game;
    private RuntimeItemTable? _table;
    private List<SpawnItem> _items = new();
    private Dictionary<int, SpawnItem> _byIndex = new();
    private Dictionary<long, SpawnItem> _byKey = new();
    private string _searchText = "";
    private SpawnItem? _selected;
    private string _countText = "1";
    private string _hoveredText = "—";
    private string _tableStatus = "Waiting for the game";
    private bool _isScanning;
    private long _lastHoveredIndex = -1, _lastHoveredCount = -1, _hoveredSlot;

    internal SpawnerViewModel(ITrainerHost host, ItemDatabase db, IconCache icons, ToggleCheatViewModel swapper, ValueFieldViewModel swapField, Dispatcher dispatcher)
    {
        _host = host;
        _db = db;
        _icons = icons;
        _swapper = swapper;
        _swapField = swapField;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => RunSearch(), dispatcher);
        SpawnNowCommand = new RelayCommand(() => _ = SpawnNowAsync(), () => CanSpawnNow);
        UseSelectedCommand = new RelayCommand(() => _ = UseSelectedAsync(), () => Selected is not null && IsTableLoaded);
        swapper.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToggleCheatViewModel.IsOn)) { Raise(nameof(SwapperOn)); RelayCommand.Requery(); }
        };
        RunSearch();
    }

    public ObservableCollection<SpawnItem> Results { get; } = new();
    public ICommand SpawnNowCommand { get; }
    public ICommand UseSelectedCommand { get; }

    public bool IsTableLoaded => _table is not null;

    /// <summary>Bumped every time the item table is (re)loaded, so other views know to re-resolve names.</summary>
    public int TableVersion { get; private set; }

    /// <summary>The item at a runtime index, when the table is loaded and knows it.</summary>
    public SpawnItem? Lookup(int runtimeIndex) => _byIndex.TryGetValue(runtimeIndex, out var item) ? item : null;

    /// <summary>The item with a given item key (the number in item_names.json), when the table knows it.</summary>
    public SpawnItem? LookupKey(long key) => _byKey.TryGetValue(key, out var item) ? item : null;
    public bool SwapperOn => _swapper.IsOn;
    public bool HasHoveredSlot => _hoveredSlot != 0;
    public bool CanSpawnNow => Selected is not null && IsTableLoaded && SwapperOn && HasHoveredSlot && _game is not null;

    public string TableStatus
    {
        get => _tableStatus;
        private set => Set(ref _tableStatus, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            _debounce.Stop();
            _debounce.Start();
        }
    }

    public SpawnItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            Raise(nameof(CanSpawnNow));
            RelayCommand.Requery();
        }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>How many to put in the slot when spawning directly.</summary>
    public string CountText
    {
        get => _countText;
        set => Set(ref _countText, value);
    }

    /// <summary>What the game reports for the slot under the cursor while the swapper is on.</summary>
    public string HoveredText
    {
        get => _hoveredText;
        private set => Set(ref _hoveredText, value);
    }

    public string ResultsText => Results.Count == 0 ? "No items match" : $"{Results.Count:N0} shown of {_items.Count:N0}";

    // ---------------------------------------------------------------- binding

    internal void Bind(GameProcess? game)
    {
        _game = game;
        _table = null;
        _items = new List<SpawnItem>();
        _byIndex = new Dictionary<int, SpawnItem>();
        _byKey = new Dictionary<long, SpawnItem>();
        TableVersion++;
        _hoveredSlot = 0;
        _lastHoveredIndex = -1;
        HoveredText = "—";
        TableStatus = game is null ? "Waiting for the game" : "Item table not loaded yet";
        RaiseTable();
        RunSearch();
    }

    /// <summary>Reads the runtime item table out of the game (full-memory scan, a few seconds).</summary>
    internal async Task LoadTableAsync(GameProcess game)
    {
        if (IsScanning) return;
        IsScanning = true;
        TableStatus = "Reading the item table from the game…";
        try
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            var started = DateTime.Now;
            bool finished = false;   // late progress posts must not overwrite the final status
            var table = await Task.Run(() => RuntimeItemTable.Locate(game, p => dispatcher.InvokeAsync(() => { if (!finished) TableStatus = $"Reading the item table from the game… {p:P0}"; })));
            finished = true;
            _host.Log($"Item table scan took {(DateTime.Now - started).TotalSeconds:0.0} s.", LogLevel.Info);
            if (!ReferenceEquals(_game, game)) return; // the game went away meanwhile
            _table = table;
            if (table is null)
            {
                TableStatus = "Item table not found — load into the world, then press Reload";
                _host.Log("Runtime item table not found in memory (is the game fully loaded?).", LogLevel.Warning);
            }
            else
            {
                _items = table.Rows.Select(row =>
                {
                    var info = _db.Find(row.Value);
                    return info is null
                        ? new SpawnItem(row.Key, row.Value, $"Unknown item (key {row.Value})", "New", 1, false, null)
                        : new SpawnItem(row.Key, row.Value, info.Name, info.Category, info.MaxStack, true, _icons);
                }).ToList();
                _byIndex = _items.ToDictionary(i => i.Index);
                _byKey = new Dictionary<long, SpawnItem>();
                foreach (var item in _items) _byKey.TryAdd(item.Key, item);
                TableVersion++;
                int named = _items.Count(i => i.Known);
                TableStatus = $"{_items.Count:N0} items read from the game ({named:N0} with names, {_items.Count - named:N0} new / unnamed)";
                _host.Log($"Runtime item table found at 0x{(long)table.Address:X}: {_items.Count:N0} items.", LogLevel.Success);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            TableStatus = "Item table scan failed";
            _host.Log($"Item table scan failed: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            IsScanning = false;
            RaiseTable();
            RunSearch();
        }
    }

    public void ReloadTable()
    {
        if (_game is not null) _ = LoadTableAsync(_game);
    }

    private void RaiseTable()
    {
        Raise(nameof(IsTableLoaded));
        Raise(nameof(CanSpawnNow));
        RelayCommand.Requery();
    }

    // ---------------------------------------------------------------- search

    private void RunSearch()
    {
        _debounce.Stop();
        Results.Clear();
        foreach (var item in Search(_searchText)) Results.Add(item);
        Raise(nameof(ResultsText));
    }

    private IEnumerable<SpawnItem> Search(string query, int limit = 300)
    {
        query = query.Trim();
        if (query.Length == 0) return _items.Where(i => i.Known).Take(limit);

        bool numeric = long.TryParse(query, out long number);
        var starts = new List<SpawnItem>();
        var contains = new List<SpawnItem>();
        foreach (var item in _items)
        {
            if (numeric && (item.Index == number || item.Key == number)) { starts.Insert(0, item); continue; }
            if (item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) starts.Add(item);
            else if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) contains.Add(item);
            if (starts.Count + contains.Count >= limit * 2) break;
        }
        return starts.Concat(contains).Take(limit);
    }

    // ---------------------------------------------------------------- spawning

    /// <summary>Writes the selected item straight into the last inventory entry the game touched.</summary>
    private async Task SpawnNowAsync()
    {
        var item = Selected;
        var game = _game;
        long slot = _hoveredSlot;
        if (item is null || game is null || slot == 0) return;
        if (!ValueFieldViewModel.TryParseLong(CountText, out long count, 1, 999_999))
        {
            _host.Log("Spawn: enter a count between 1 and 999,999.", LogLevel.Error);
            return;
        }
        bool ok = await _host.RunAsync(() =>
        {
            game.WriteInt64((nint)slot + 0x08, item.Index);
            game.WriteInt64((nint)slot + 0x10, count);
        }, $"Wrote {count:N0} × {item.Name} (#{item.Index}) into entry 0x{slot:X} — close and reopen the inventory to see it.");
        if (ok) _lastHoveredIndex = -1; // force the readout to refresh
    }

    /// <summary>Arms the on-use/drop swap with the selected item's runtime index.</summary>
    private async Task UseSelectedAsync()
    {
        var item = Selected;
        if (item is null) return;
        _swapField.Text = item.Index.ToString();
        if (!_swapper.IsOn && _swapper.IsAvailable && !await _swapper.EnsureOnAsync()) return;
        _swapField.Apply();
        if (_swapField.Error is null)
            _host.Log($"Swap target → {item.Name} (#{item.Index}). Use or drop any stack and it becomes this item ×1.", LogLevel.Success);
    }

    private long _lastHits = -1;

    /// <summary>
    /// Polls the inventory hook's capture: the entry the game touched last (use / drop / pickup),
    /// what it held and how the count changed. That entry is the spawn target.
    /// </summary>
    internal void Tick()
    {
        if (!_swapper.IsOn)
        {
            if (_hoveredSlot != 0 || _lastHoveredIndex != -1 || _lastHits != -1)
            {
                _hoveredSlot = 0; _lastHoveredIndex = -1; _lastHits = -1;
                HoveredText = "—";
                Raise(nameof(HasHoveredSlot)); Raise(nameof(CanSpawnNow)); RelayCommand.Requery();
            }
            return;
        }
        if (!_swapper.Cheat.TryReadVar("hits", out long hits) || hits == _lastHits) return;
        if (!_swapper.Cheat.TryReadVar("lastEntry", out long slot) || slot == 0) return;
        _swapper.Cheat.TryReadVar("lastField8", out long index);
        _swapper.Cheat.TryReadVar("lastOld", out long oldCount);
        _swapper.Cheat.TryReadVar("lastNew", out long newCount);
        bool first = _lastHits == -1;
        _lastHits = hits;

        bool slotChanged = slot != _hoveredSlot;
        _hoveredSlot = slot;
        if (slotChanged) { Raise(nameof(HasHoveredSlot)); Raise(nameof(CanSpawnNow)); RelayCommand.Requery(); }
        _lastHoveredIndex = index;
        _lastHoveredCount = newCount;

        var known = _items.FirstOrDefault(i => i.Index == index);
        string name = known?.Name ?? $"#{index}";
        HoveredText = $"{name} (#{index})  {oldCount:N0} → {newCount:N0}   entry 0x{slot:X}";

        // Diagnostics: the first capture (or an entry whose +8 is not a valid item) goes to the log.
        if (first || known is null)
        {
            try
            {
                var head = _game?.Read((nint)slot, 0x40);
                if (head is not null)
                    _host.Log($"Inventory entry 0x{slot:X} captured (+8 = {index}, count {oldCount:N0} → {newCount:N0}): " +
                              string.Join(" ", Enumerable.Range(0, 8).Select(i => BitConverter.ToInt64(head, i * 8).ToString("X"))), known is null ? LogLevel.Warning : LogLevel.Info);
            }
            catch (Exception) { /* entry may already be gone */ }
        }
    }
}
