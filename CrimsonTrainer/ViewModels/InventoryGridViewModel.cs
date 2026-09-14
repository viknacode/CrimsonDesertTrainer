using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using CrimsonTrainer.Infrastructure;
using CrimsonTrainer.Items;
using CrimsonTrainer.Memory;

namespace CrimsonTrainer.ViewModels;

/// <summary>One container type as a selectable tab: both copies the game keeps are written together.</summary>
public sealed class ContainerChoiceViewModel : ObservableObject
{
    private readonly Action<ContainerChoiceViewModel> _select;
    private bool _isSelected;

    internal ContainerChoiceViewModel(short type, IReadOnlyList<InventoryContainer> copies, Action<ContainerChoiceViewModel> select)
    {
        Type = type;
        Copies = copies;
        _select = select;
    }

    public short Type { get; }
    public IReadOnlyList<InventoryContainer> Copies { get; }
    public InventoryContainer Primary => Copies[0];
    public string Label => Type == 1 ? $"Main items · {Primary.Used}" : $"Container {Type} · {Primary.Used}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            if (value) _select(this);
        }
    }
}

/// <summary>A tile in the inventory grid.</summary>
public sealed class SlotViewModel : ObservableObject
{
    private static readonly Dictionary<string, Brush> CategoryBrushes = new()
    {
        ["Equipment"] = Freeze("#F97316"),
        ["Consumable"] = Freeze("#3DD68C"),
        ["Material"] = Freeze("#5B9CF6"),
        ["Ammo"] = Freeze("#A78BFA"),
        ["Currency"] = Freeze("#F5A524"),
        ["Quest"] = Freeze("#F472B6"),
        ["Misc"] = Freeze("#8B93A3"),
        ["New"] = Freeze("#5A6170"),
    };

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    internal SlotViewModel(InventoryEntry entry, SpawnItem? item)
    {
        Entry = entry;
        Item = item;
        if (item is not null)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SpawnItem.Icon)) { Raise(nameof(Icon)); Raise(nameof(HasIcon)); }
            };
        Name = item?.Name ?? $"Unknown #{entry.RuntimeIndex}";
        Category = item?.Category ?? "New";
        CategoryBrush = CategoryBrushes.TryGetValue(Category, out var b) ? b : CategoryBrushes["Misc"];
        Initial = Name.Length > 0 && char.IsLetterOrDigit(Name[0]) ? Name[..1].ToUpperInvariant() : "?";
    }

    public InventoryEntry Entry { get; }
    public SpawnItem? Item { get; }
    public int Slot => Entry.Slot;
    public string Name { get; }
    public string Category { get; }
    public Brush CategoryBrush { get; }
    public string Initial { get; }
    public long Count => Entry.Count;

    /// <summary>The item's icon (from the picker item, loaded lazily); null falls back to the letter.</summary>
    public ImageSource? Icon => Item?.Icon;
    public bool HasIcon => Item?.HasIcon == true;

    public string SlotText => $"#{Entry.Slot}";
    public string CountText => Entry.Count == 1 ? "" : $"×{Entry.Count:N0}";
    public string ToolTipText => $"{Name}\nslot {Entry.Slot} · runtime #{Entry.RuntimeIndex} · {Category}\ncount {Entry.Count:N0}";
    public string SummaryText => $"Slot #{Entry.Slot} · {Name} ×{Entry.Count:N0}";

    /// <summary>Same slot, same item, same count — the tile does not need rebuilding.</summary>
    public bool SameAs(InventoryEntry e, SpawnItem? item) =>
        e.Slot == Entry.Slot && e.RuntimeIndex == Entry.RuntimeIndex && e.Count == Entry.Count && e.InstanceId == Entry.InstanceId && ReferenceEquals(item, Item);

    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Category.Equals(query, StringComparison.OrdinalIgnoreCase)
        || Entry.RuntimeIndex.ToString() == query
        || SlotText == query;
}

/// <summary>
/// The inventory as a grid of slots read straight from the game's containers. A tile is the
/// write target: replace what is in it with any item from the table, or just change the count.
/// </summary>
public sealed class InventoryGridViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);

    private readonly ITrainerHost _host;
    private readonly SpawnerViewModel _spawner;
    private readonly InventorySlotsViewModel _scan;
    private GameProcess? _game;
    private InventoryWriter? _writer;
    private ContainerChoiceViewModel? _selectedContainer;
    private SlotViewModel? _selectedSlot;
    private string _status = "Waiting for the game";
    private string _filter = "";
    private List<SlotViewModel> _all = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private int _namesVersion = -1;
    private bool _isActive;

    internal InventoryGridViewModel(ITrainerHost host, SpawnerViewModel spawner, InventorySlotsViewModel scan)
    {
        _host = host;
        _spawner = spawner;
        _scan = scan;
        scan.Scanned += OnScanned;
        RefreshCommand = new RelayCommand(() => Refresh(force: true), () => _game is not null && _selectedContainer is not null);
        PutCommand = new RelayCommand(() => _ = PutAsync(), () => CanWrite && _spawner.Selected is not null);
        AddCommand = new RelayCommand(() => _ = AddAsync(), () => CanAdd);
        SetCountCommand = new RelayCommand(() => _ = SetCountAsync(), () => CanWrite);
        MaxCountCommand = new RelayCommand(SetMaxCount, () => _spawner.Selected is not null || _selectedSlot is not null);
        spawner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpawnerViewModel.Selected)) { Raise(nameof(PutText)); Raise(nameof(AddText)); Raise(nameof(CanAdd)); RelayCommand.Requery(); }
        };
    }

    public ObservableCollection<ContainerChoiceViewModel> Containers { get; } = new();

    /// <summary>The tiles on screen (the current container, filtered).</summary>
    public ObservableCollection<SlotViewModel> Slots { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand PutCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand SetCountCommand { get; }
    public ICommand MaxCountCommand { get; }

    /// <summary>The Items section is on screen — refresh the grid every couple of seconds.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (Set(ref _isActive, value) && value) Refresh(force: true);
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Filters the tiles by item name, category, runtime index or "#slot".</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) { ApplyFilter(); UpdateStatus(); }
        }
    }

    public ContainerChoiceViewModel? SelectedContainer
    {
        get => _selectedContainer;
        private set
        {
            if (!Set(ref _selectedContainer, value)) return;
            SelectedSlot = null;
            Refresh(force: true);
            RelayCommand.Requery();
        }
    }

    public SlotViewModel? SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            int? before = _selectedSlot?.Slot;
            if (!Set(ref _selectedSlot, value)) return;
            if (value is not null && value.Slot != before) _spawner.CountText = value.Count.ToString();   // a newly clicked slot: the box starts from what it holds
            Raise(nameof(HasSelectedSlot));
            Raise(nameof(SelectedSlotText));
            Raise(nameof(PutText));
            RelayCommand.Requery();
        }
    }

    public bool HasSelectedSlot => _selectedSlot is not null;
    public string SelectedSlotText => _selectedSlot?.SummaryText ?? "Click a slot in the grid above";
    public string PutText => _selectedSlot is null ? "Put in slot" : _spawner.Selected is null ? $"Put in slot #{_selectedSlot.Slot}" : $"Put {_spawner.Selected.Name} in slot #{_selectedSlot.Slot}";
    public bool CanWrite => _game is not null && _selectedContainer is not null && _selectedSlot is not null;

    /// <summary>A picked item can go into a free slot of the current container.</summary>
    public bool CanAdd => _game is not null && _selectedContainer is not null && _spawner.Selected is not null;
    public string AddText => _spawner.Selected is null ? "Add to inventory" : $"Add {_spawner.Selected.Name} to inventory";

    internal void Bind(GameProcess? game)
    {
        _game = game;
        _writer = game is null ? null : new InventoryWriter(game);
        Containers.Clear();
        _all.Clear();
        Slots.Clear();
        _selectedContainer = null;
        Raise(nameof(SelectedContainer));
        SelectedSlot = null;
        Status = game is null ? "Waiting for the game" : "Looking for the inventory…";
        RelayCommand.Requery();
    }

    private void OnScanned()
    {
        var all = _scan.AllContainers;
        Containers.Clear();
        _selectedContainer = null;
        Raise(nameof(SelectedContainer));
        SelectedSlot = null;
        foreach (var group in all.GroupBy(c => c.Type).OrderBy(g => g.Key == 1 ? -1 : g.Key))
            Containers.Add(new ContainerChoiceViewModel(group.Key, group.OrderBy(c => (long)c.Address).ToList(), c => SelectedContainer = c));
        if (Containers.Count == 0)
        {
            Status = _game is null ? "Waiting for the game" : "No inventory found yet — load into the world and press Find in the Inventory section";
            _all.Clear();
            Slots.Clear();
            return;
        }
        Containers[0].IsSelected = true;   // selects and refreshes
    }

    /// <summary>Once a second from the session timer.</summary>
    internal void Tick()
    {
        if (!_isActive || _game is null || _selectedContainer is null) return;
        if (DateTime.Now - _lastRefresh >= RefreshEvery) Refresh(force: false);
    }

    private void Refresh(bool force)
    {
        var game = _game;
        var container = _selectedContainer;
        if (game is null || container is null) { _all.Clear(); Slots.Clear(); return; }
        _lastRefresh = DateTime.Now;

        List<InventoryEntry> entries;
        try
        {
            entries = InventoryScanner.ReadEntries(game, container.Primary);
        }
        catch (Win32Exception)
        {
            Status = "Could not read the inventory";
            return;
        }

        bool namesChanged = _namesVersion != _spawner.TableVersion;
        _namesVersion = _spawner.TableVersion;
        bool changed = force || namesChanged || entries.Count != _all.Count;
        if (!changed)
            for (int i = 0; i < entries.Count; i++)
                if (!_all[i].SameAs(entries[i], _spawner.Lookup(entries[i].RuntimeIndex))) { changed = true; break; }

        if (changed)
        {
            int keepSlot = _selectedSlot?.Slot ?? -1;
            _all = entries.Select(e => new SlotViewModel(e, _spawner.Lookup(e.RuntimeIndex))).ToList();
            ApplyFilter();
            SelectedSlot = keepSlot < 0 ? null : Slots.FirstOrDefault(s => s.Slot == keepSlot);
        }
        UpdateStatus();
    }

    private void ApplyFilter()
    {
        string q = _filter.Trim();
        var keep = _selectedSlot;
        Slots.Clear();
        foreach (var s in _all)
            if (s.Matches(q)) Slots.Add(s);
        SelectedSlot = keep is not null && Slots.Contains(keep) ? keep : null;
    }

    private void UpdateStatus()
    {
        var game = _game;
        var container = _selectedContainer;
        if (game is null || container is null) return;
        var header = InventoryContainer.Read(game, container.Primary.Address);
        int unnamed = _all.Count(s => s.Item is null);
        string shown = Slots.Count != _all.Count ? $" ({Slots.Count} shown)" : "";
        string capacity = header is null ? "" : $" · {header.Used} / {header.Capacity} slots";
        string names = unnamed > 0 ? $" · {unnamed} without a name yet" : "";
        string copies = container.Copies.Count > 1 ? "" : " · only one copy found";
        Status = $"{_all.Count} items{shown}{capacity}{names}{copies}";
    }

    /// <summary>Count box → the max stack of the picked item (or of the slot's item), 999 when unknown or unlimited.</summary>
    private void SetMaxCount()
    {
        var item = _spawner.Selected ?? _selectedSlot?.Item;
        long max = item is null || item.MaxStack <= 0 || item.MaxStack >= 1_000_000_000 ? 999 : Math.Min(item.MaxStack, 999_999);
        _spawner.CountText = max.ToString();
    }

    /// <summary>Replaces whatever is in the selected slot with the item picked in the spawner list (rebuilt as a fresh item).</summary>
    private async Task PutAsync()
    {
        var game = _game; var writer = _writer; var container = _selectedContainer; var slot = _selectedSlot; var item = _spawner.Selected;
        if (game is null || writer is null || container is null || slot is null || item is null) return;
        if (!ValueFieldViewModel.TryParseLong(_spawner.CountText, out long count, 1, 999_999))
        {
            _host.Log("Put: enter a count between 1 and 999,999.", LogLevel.Error);
            return;
        }
        InventoryWriter.Written? written = null;
        bool ok = await _host.RunAsync(() => written = writer.Replace(container.Copies, slot.Slot, item.Index, count), null);
        if (!ok) return;
        _host.Log(written is null
                ? $"Slot #{slot.Slot} emptied before the write — nothing changed."
                : $"Slot #{slot.Slot}: {slot.Name} → {count:N0} × {item.Name} (#{item.Index}, new item id {written.InstanceId}). Close and reopen the inventory to see it.",
            written is null ? LogLevel.Warning : LogLevel.Success);
        Refresh(force: true);
    }

    /// <summary>Puts the picked item into the first free slot of the current container (no item is sacrificed).</summary>
    private async Task AddAsync()
    {
        var game = _game; var writer = _writer; var container = _selectedContainer; var item = _spawner.Selected;
        if (game is null || writer is null || container is null || item is null) return;
        if (!ValueFieldViewModel.TryParseLong(_spawner.CountText, out long count, 1, 999_999))
        {
            _host.Log("Add: enter a count between 1 and 999,999.", LogLevel.Error);
            return;
        }
        InventoryWriter.Written? written = null;
        bool ok = await _host.RunAsync(() => written = writer.Create(container.Copies, item.Index, count), null);
        if (!ok) return;
        _host.Log(written is null
                ? "Add: no free slot below the container's capacity — drop something or raise the slot capacity (Inventory section)."
                : $"Added {count:N0} × {item.Name} (#{item.Index}) in slot #{written.Slot} (item id {written.InstanceId}). Close and reopen the inventory to see it.",
            written is null ? LogLevel.Warning : LogLevel.Success);
        Refresh(force: true);
    }

    /// <summary>Writes only the count of the selected slot.</summary>
    private async Task SetCountAsync()
    {
        var game = _game; var container = _selectedContainer; var slot = _selectedSlot;
        if (game is null || container is null || slot is null) return;
        if (!ValueFieldViewModel.TryParseLong(_spawner.CountText, out long count, 1, 999_999))
        {
            _host.Log("Set count: enter a count between 1 and 999,999.", LogLevel.Error);
            return;
        }
        bool ok = await _host.RunAsync(() =>
        {
            foreach (var copy in container.Copies)
            {
                var entry = copy.Entry(slot.Slot);
                if (game.ReadInt64(entry) == -1) continue;
                game.WriteInt64(entry + 0x10, count);
            }
        }, $"Slot #{slot.Slot}: {slot.Name} count {slot.Count:N0} → {count:N0}. Close and reopen the inventory to see it.");
        if (ok) Refresh(force: true);
    }
}
