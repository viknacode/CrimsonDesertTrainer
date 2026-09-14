using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CrimsonTrainer.Infrastructure;
using CrimsonTrainer.Items;
using CrimsonTrainer.Memory;

namespace CrimsonTrainer.ViewModels;

/// <summary>One item that can fill a slot of the selected set.</summary>
public sealed class SetPieceOptionViewModel : ObservableObject
{
    private readonly SetPieceViewModel _owner;
    private SpawnItem? _item;
    private bool _isSelected;

    internal SetPieceOptionViewModel(SetPieceViewModel owner, ArmorPieceOption option)
    {
        _owner = owner;
        Option = option;
    }

    public ArmorPieceOption Option { get; }
    public string Name => Option.Name;
    public string ToolTipText => $"{Option.Name}\nkey {Option.Key} · {Option.Internal}";

    /// <summary>The runtime-table row for this item; null while the table is not loaded or the key is gone.</summary>
    public SpawnItem? Item
    {
        get => _item;
        internal set
        {
            if (Set(ref _item, value)) Raise(nameof(IsReady));
        }
    }

    public bool IsReady => _item is not null;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            if (value) _owner.OnOptionSelected(this);
        }
    }

    internal void SetSelectedSilently(bool value)
    {
        _isSelected = value;
        Raise(nameof(IsSelected));
    }
}

/// <summary>One slot of the selected set: which item fills it, whether it is included, and whether the game can spawn it.</summary>
public sealed class SetPieceViewModel : ObservableObject
{
    private readonly Action _changed;
    private SetPieceOptionViewModel _selected;
    private bool _include = true;
    private string _statusText = "";

    internal SetPieceViewModel(ArmorPiece piece, Action changed)
    {
        Piece = piece;
        _changed = changed;
        Options = piece.Options.Select(o => new SetPieceOptionViewModel(this, o)).ToList();
        _selected = Options[0];
        _selected.SetSelectedSilently(true);
    }

    public ArmorPiece Piece { get; }
    public IReadOnlyList<SetPieceOptionViewModel> Options { get; }
    public bool HasVariants => Options.Count > 1;
    public string SlotLabel => Piece.SlotLabel;

    /// <summary>The variant that will be spawned.</summary>
    public SetPieceOptionViewModel Selected => _selected;
    public string Name => _selected.Name;
    public SpawnItem? Item => _selected.Item;
    public bool IsReady => _selected.IsReady;

    /// <summary>Unticked pieces are skipped by Spawn (e.g. one that the character cannot equip).</summary>
    public bool Include
    {
        get => _include;
        set
        {
            if (Set(ref _include, value)) _changed();
        }
    }

    public string StatusText
    {
        get => _statusText;
        internal set => Set(ref _statusText, value);
    }

    internal void OnOptionSelected(SetPieceOptionViewModel option)
    {
        if (ReferenceEquals(option, _selected)) return;
        _selected.SetSelectedSilently(false);
        _selected = option;
        Raise(nameof(Selected));
        Raise(nameof(Name));
        Raise(nameof(Item));
        Raise(nameof(IsReady));
        _changed();
    }

    internal void RaiseResolved()
    {
        Raise(nameof(Item));
        Raise(nameof(IsReady));
    }
}

public enum SetCharacterFilter
{
    All,
    Kliff,
    Damiane,
}

/// <summary>A card in the set gallery.</summary>
public sealed class ArmorSetViewModel : ObservableObject
{
    private static readonly Dictionary<string, BitmapImage> ImageCache = new();

    internal ArmorSetViewModel(ArmorSet set, Action changed)
    {
        Set = set;
        Pieces = set.Pieces.Select(p => new SetPieceViewModel(p, changed)).ToList();
    }

    public ArmorSet Set { get; }
    public IReadOnlyList<SetPieceViewModel> Pieces { get; }

    public string Name => Set.Name;
    public string? Alias => Set.Alias;
    public bool HasAlias => !string.IsNullOrEmpty(Set.Alias);
    public string Character => Set.Character;
    public string ArmorClass => Set.ArmorClass;
    public string Resistance => Set.Resistance;
    public string AbyssGear => Set.AbyssGear;
    public string Source => Set.Source;
    public int PieceCount => Pieces.Count;
    public string PiecesText => Pieces.Count == 0 ? "no pieces in the item list" : $"{Pieces.Count} piece{(Pieces.Count == 1 ? "" : "s")}";
    public string ToolTipText => $"{Name}{(HasAlias ? $" ({Alias})" : "")}\n{ArmorClass} · {Resistance}\n{Source} · {Character}\n{PiecesText}";

    /// <summary>Catalog picture, embedded in the exe (Resources/sets), decoded once per set.</summary>
    public ImageSource? Image
    {
        get
        {
            if (ImageCache.TryGetValue(Set.Image, out var cached)) return cached;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri($"pack://application:,,,/Resources/sets/{Set.Image}", UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                ImageCache[Set.Image] = image;
                return image;
            }
            catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException)
            {
                return null;
            }
        }
    }

    public bool Matches(string query, bool damianeOnly, bool kliffOnly)
    {
        if (damianeOnly && !Set.IsDamiane) return false;
        if (kliffOnly && Set.IsDamiane) return false;
        if (query.Length == 0) return true;
        return Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (Alias?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
               || ArmorClass.Contains(query, StringComparison.OrdinalIgnoreCase)
               || Resistance.Contains(query, StringComparison.OrdinalIgnoreCase)
               || Source.Contains(query, StringComparison.OrdinalIgnoreCase)
               || Pieces.Any(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The armor-set gallery: pick a set from the vulkk.com catalog and every piece of it is written
/// into your main inventory. Slots cannot be created from outside the game, so each piece takes
/// over the entry of something you already carry — the most recently picked-up items, shown
/// beforehand so nothing valuable goes by surprise.
/// </summary>
public sealed class SetsViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);

    private readonly ITrainerHost _host;
    private readonly SpawnerViewModel _spawner;
    private readonly InventorySlotsViewModel _scan;
    private readonly List<ArmorSetViewModel> _all;
    private GameProcess? _game;
    private ArmorSetViewModel? _selected;
    private string _searchText = "";
    private SetCharacterFilter _characterFilter = SetCharacterFilter.All;
    private string _planText = "";
    private string _spawnHint = "";
    private bool _isActive;
    private int _tableVersion = -1;
    private DateTime _lastPlan = DateTime.MinValue;
    private List<(InventoryEntry Entry, SpawnItem? Item)> _targets = new();

    internal SetsViewModel(ITrainerHost host, ArmorSetCatalog catalog, SpawnerViewModel spawner, InventorySlotsViewModel scan)
    {
        _host = host;
        _spawner = spawner;
        _scan = scan;
        _all = catalog.Sets.Select(s => new ArmorSetViewModel(s, OnPiecesChanged)).ToList();
        SourceUrl = catalog.Source;
        SpawnCommand = new RelayCommand(() => _ = SpawnAsync(), () => CanSpawn);
        scan.Scanned += () => Replan(force: true);
        spawner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpawnerViewModel.IsTableLoaded)) Replan(force: true);
        };
        ApplyFilter();
    }

    public ObservableCollection<ArmorSetViewModel> Sets { get; } = new();
    public ICommand SpawnCommand { get; }
    public string SourceUrl { get; }
    public int TotalCount => _all.Count;
    public string CountText => Sets.Count == _all.Count ? $"{_all.Count} sets" : $"{Sets.Count} of {_all.Count} sets";

    /// <summary>The Sets section is on screen — keep the replacement preview current.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (Set(ref _isActive, value) && value) Replan(force: true);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyFilter();
        }
    }

    public SetCharacterFilter CharacterFilter
    {
        get => _characterFilter;
        set
        {
            if (Set(ref _characterFilter, value)) ApplyFilter();
        }
    }

    public ArmorSetViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedTitle));
            ResolvePieces();
            Replan(force: true);
            RelayCommand.Requery();
        }
    }

    public bool HasSelection => _selected is not null;
    public string SelectedTitle => _selected is null ? "Pick a set above" : _selected.HasAlias ? $"{_selected.Name} · {_selected.Alias}" : _selected.Name;

    /// <summary>What will be overwritten by the next Spawn, in the order the pieces are written.</summary>
    public string PlanText
    {
        get => _planText;
        private set => Set(ref _planText, value);
    }

    /// <summary>Why Spawn is disabled, or what it will do.</summary>
    public string SpawnHint
    {
        get => _spawnHint;
        private set => Set(ref _spawnHint, value);
    }

    public bool CanSpawn => _game is not null && _selected is not null && ReadyPieces(_selected).Count > 0 && _targets.Count >= ReadyPieces(_selected).Count;

    internal void Bind(GameProcess? game)
    {
        _game = game;
        _targets.Clear();
        ResolvePieces();
        Replan(force: true);
        RelayCommand.Requery();
    }

    /// <summary>Once a second from the session timer.</summary>
    internal void Tick()
    {
        if (!_isActive || _game is null) return;
        if (_tableVersion != _spawner.TableVersion) ResolvePieces();
        if (DateTime.Now - _lastPlan >= RefreshEvery) Replan(force: false);
    }

    private void ApplyFilter()
    {
        string q = _searchText.Trim();
        bool damiane = _characterFilter == SetCharacterFilter.Damiane, kliff = _characterFilter == SetCharacterFilter.Kliff;
        var keep = _selected;
        Sets.Clear();
        foreach (var s in _all)
            if (s.Matches(q, damiane, kliff)) Sets.Add(s);
        Raise(nameof(CountText));
        if (keep is not null && !Sets.Contains(keep)) Selected = null;
    }

    private static List<SetPieceViewModel> ReadyPieces(ArmorSetViewModel set) => set.Pieces.Where(p => p.Include && p.IsReady).ToList();

    /// <summary>A piece was ticked / unticked or its variant changed.</summary>
    private void OnPiecesChanged()
    {
        UpdatePieceStatus();
        Replan(force: true);
    }

    /// <summary>Maps every variant of every piece of the selected set to a row of the game's item table.</summary>
    private void ResolvePieces()
    {
        _tableVersion = _spawner.TableVersion;
        var set = _selected;
        if (set is null) return;
        foreach (var piece in set.Pieces)
        {
            foreach (var option in piece.Options)
                option.Item = _game is null || !_spawner.IsTableLoaded ? null : _spawner.LookupKey(option.Option.Key);
            piece.RaiseResolved();
        }
        UpdatePieceStatus();
    }

    private void UpdatePieceStatus()
    {
        var set = _selected;
        if (set is null) return;
        foreach (var piece in set.Pieces)
            piece.StatusText = _game is null ? "waiting for the game"
                : !_spawner.IsTableLoaded ? "item table not loaded yet"
                : !piece.Include ? "skipped"
                : piece.Item is null ? "not in this game version"
                : $"runtime #{piece.Item.Index}";
    }

    /// <summary>Picks the entries that the pieces will overwrite and describes them.</summary>
    private void Replan(bool force)
    {
        _lastPlan = DateTime.Now;
        var game = _game;
        var set = _selected;
        var containers = _scan.AllContainers.Where(c => c.Type == 1).OrderBy(c => (long)c.Address).ToList();

        if (set is null) { _targets = new(); PlanText = ""; SpawnHint = ""; RelayCommand.Requery(); return; }
        var ready = ReadyPieces(set);
        if (game is null) { Fail("Waiting for the game."); return; }
        if (set.Pieces.Count == 0) { Fail("This set has no pieces in the trainer's item list, so there is nothing to spawn."); return; }
        if (!_spawner.IsTableLoaded) { Fail("The item table has not been read from the game yet (Items section → Reload table)."); return; }
        if (ready.Count == 0) { Fail("None of the pieces exist in this game version's item table."); return; }
        if (containers.Count == 0) { Fail("Main inventory not found yet — load into the world, then press Find in the Inventory section."); return; }

        List<InventoryEntry> entries;
        try { entries = InventoryScanner.ReadEntries(game, containers[0]); }
        catch (Win32Exception) { Fail("Could not read the inventory."); return; }

        var pieceIndices = ready.Select(p => p.Item!.Index).ToHashSet();
        var candidates = entries
            .Where(e => !pieceIndices.Contains(e.RuntimeIndex))                  // never overwrite a piece of this set
            .OrderByDescending(e => e.Created).ThenByDescending(e => e.Slot)     // most recently obtained first
            .Take(ready.Count)
            .Select(e => (Entry: e, Item: _spawner.Lookup(e.RuntimeIndex)))
            .ToList();

        bool changed = force || candidates.Count != _targets.Count || candidates.Zip(_targets).Any(z => z.First.Entry.Slot != z.Second.Entry.Slot || z.First.Entry.RuntimeIndex != z.Second.Entry.RuntimeIndex || z.First.Entry.Count != z.Second.Entry.Count);
        _targets = candidates;
        if (!changed) return;

        if (candidates.Count < ready.Count)
        {
            PlanText = "";
            Fail($"The set needs {ready.Count} items to replace, but the inventory only has {candidates.Count} that can be used. Pick up some junk first.");
            return;
        }

        var lines = new List<string>();
        for (int i = 0; i < ready.Count; i++)
        {
            var (entry, item) = candidates[i];
            string what = item?.Name ?? $"Unknown #{entry.RuntimeIndex}";
            string count = entry.Count == 1 ? "" : $" ×{entry.Count:N0}";
            lines.Add($"#{entry.Slot} {what}{count} → {ready[i].Name}");
        }
        PlanText = string.Join("\n", lines);
        int missing = set.Pieces.Count - ready.Count;
        SpawnHint = $"Replaces the {ready.Count} most recently picked-up items with the set" + (missing > 0 ? $" ({missing} piece{(missing == 1 ? "" : "s")} not in this game version, skipped)." : ".")
                    + " Close and reopen the inventory afterwards to see them.";
        RelayCommand.Requery();

        void Fail(string why)
        {
            _targets = new();
            PlanText = "";
            SpawnHint = why;
            RelayCommand.Requery();
        }
    }

    private async Task SpawnAsync()
    {
        var game = _game;
        var set = _selected;
        if (game is null || set is null) return;
        Replan(force: true);   // fresh targets: the inventory may have changed since the preview
        var ready = ReadyPieces(set);
        var targets = _targets;
        if (ready.Count == 0 || targets.Count < ready.Count) { _host.Log($"Spawn set: {SpawnHint}", LogLevel.Error); return; }
        var copies = _scan.AllContainers.Where(c => c.Type == 1).OrderBy(c => (long)c.Address).ToList();

        var written = new List<string>();
        bool ok = await _host.RunAsync(() =>
        {
            for (int i = 0; i < ready.Count; i++)
            {
                var entry = targets[i].Entry;
                var item = ready[i].Item!;
                bool any = false;
                foreach (var copy in copies)
                {
                    var address = copy.Entry(entry.Slot);
                    if (game.ReadInt64(address) == -1) continue;   // the slot emptied meanwhile
                    game.WriteInt64(address + 0x08, item.Index);
                    game.WriteInt64(address + 0x10, 1);
                    any = true;
                }
                if (any) written.Add($"{ready[i].Name} (#{item.Index}) in slot #{entry.Slot}");
            }
        }, null);
        if (!ok) return;
        _host.Log(written.Count == 0
                ? $"{set.Name}: nothing written — the target slots emptied before the write."
                : $"{set.Name}: {string.Join(", ", written)}. Close and reopen the inventory to see the set.",
            written.Count == 0 ? LogLevel.Warning : LogLevel.Success);
        Replan(force: true);
    }
}
