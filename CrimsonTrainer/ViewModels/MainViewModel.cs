using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CrimsonTrainer.Cheats;
using CrimsonTrainer.Hotkeys;
using CrimsonTrainer.Infrastructure;
using CrimsonTrainer.Items;
using CrimsonTrainer.Memory;
using CrimsonTrainer.Settings;

namespace CrimsonTrainer.ViewModels;

public enum ConnectionState
{
    Waiting,
    Scanning,
    Attached,
    Error,
}

public sealed record SectionViewModel(string Key, string Title, string Glyph, string Subtitle);

/// <summary>
/// The trainer session: finds the game, scans every script, binds the cheat view-models to
/// the live process, owns hotkeys, settings and the activity log.
/// </summary>
public sealed class MainViewModel : ObservableObject, ITrainerHost, IDisposable
{
    private const string ProcessName = "CrimsonDesert";
    private const int MaxLogEntries = 300;

    /// <summary>Optional "--pid N" from the command line: only attach to that process.</summary>
    private static readonly int? PidFilter = ParsePidArgument();

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _fast;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppSettings _settings;
    private readonly CheatCatalog _detached;
    private readonly ValueFieldViewModel _timeScaleField;

    private GameProcess? _game;
    private CheatCatalog? _catalog;
    private HotkeyManager? _hotkeys;
    private HotkeyBindingViewModel? _capturing;
    private bool _attaching;
    private bool _disposed;

    private ConnectionState _state;
    private string _statusText = "Waiting for game";
    private SectionViewModel _selectedSection;
    private LogEntry? _lastLog;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = AppSettings.Load();
        var db = ItemDatabase.LoadEmbedded();
        Icons = IconCache.LoadEmbedded();

        // Metadata-only catalog so every cheat is listed (disabled) before the game is attached.
        _detached = new CheatCatalog(GameProcess.Detached);
        Cheats = _detached.Cheats.Select<Cheat, CheatViewModel>(c => c switch
        {
            ToggleCheat t => new ToggleCheatViewModel(this, t),
            ChoiceCheat ch => new ChoiceCheatViewModel(this, ch),
            _ => throw new InvalidOperationException($"Unknown cheat type {c.GetType().Name}"),
        }).ToList();

        PlayerCheats = Cheats.Where(c => c.Section == CheatSection.Player && c.Id != CheatCatalog.PlayerPointersId).ToList();
        InventoryCheats = Cheats.Where(c => c.Section == CheatSection.Inventory && c.Id != CheatCatalog.ItemSwapperId).ToList();
        WorldCheats = Cheats.Where(c => c.Section == CheatSection.World).ToList();

        // ---- cheat-specific fields and extra hotkeys ----
        var timeScale = Toggle(CheatCatalog.TimeScaleId);
        _timeScaleField = timeScale.AddField("scale", "Game speed", "1", text =>
        {
            if (!ValueFieldViewModel.TryParseFloat(text, out float value) || value < 0.05f || value > 20f) return "Enter a number between 0.05 and 20.";
            return Guard(() => timeScale.Cheat.SetVarSingle("TimeScaleFloat", value));
        }, hint: "1 = normal · 0.5 = half · 2 = double. Applies while the cheat is on.");
        timeScale.AddBinding("faster", "Faster (+0.5)", () => NudgeTimeScale(+0.5f));
        timeScale.AddBinding("slower", "Slower (−0.5)", () => NudgeTimeScale(-0.5f));

        var stack = Toggle(CheatCatalog.StackModifierId);
        stack.AddField("stack", "Locked stack amount", "0", text =>
        {
            if (!ValueFieldViewModel.TryParseLong(text, out long value, 0, int.MaxValue)) return "Enter a whole number (0 disables the lock).";
            return Guard(() => stack.Cheat.SetVar("lockAmount", value));
        }, hint: "0 = keep the count it had; any other value = the stack becomes exactly this.");

        var swapper = Toggle(CheatCatalog.ItemSwapperId);
        var swapField = swapper.AddField("swapId", "Swap target (runtime #)", "0", text =>
        {
            if (!ValueFieldViewModel.TryParseLong(text, out long value, 0, int.MaxValue)) return "Enter a runtime item number (pick one from the list).";
            return Guard(() => swapper.Cheat.SetVar("swapId", value));
        });

        Player = new PlayerPanelViewModel(this, Toggle(CheatCatalog.PlayerPointersId), _settings);
        Spawner = new SpawnerViewModel(this, db, Icons, swapper, swapField, dispatcher);
        InventorySlots = new InventorySlotsViewModel(this);
        InventoryGrid = new InventoryGridViewModel(this, Spawner, InventorySlots);
        Teleport = new TeleportViewModel(this, Toggle(CheatCatalog.PlayerPointersId), _settings);
        InventorySlots.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InventorySlotsViewModel.Keep)) { Raise(nameof(ActiveCount)); Raise(nameof(ActiveText)); }
        };
        BodyScales = _detached.BodyScales.Select(t => new BodyScaleViewModel(this, t)).ToList();

        RestoreFieldTexts();
        swapField.Text = "0";   // never restore a stale swap target: a wrong runtime index would corrupt a slot

        Sections = new[]
        {
            new SectionViewModel("player", "Player", "", "Stats, godmode, trust, contribution"),
            new SectionViewModel("inventory", "Inventory", "", "Infinite items, multipliers, stack lock"),
            new SectionViewModel("items", "Items", "", "Spawn any item into your inventory"),
            new SectionViewModel("teleport", "Teleport", "", "Coordinates, waypoints, map marker"),
            new SectionViewModel("world", "World", "", "Time scale, horses, minigames, durability"),
            new SectionViewModel("character", "Character", "", "Body & head scale"),
            new SectionViewModel("activity", "Activity", "", "Everything the trainer did"),
            new SectionViewModel("credits", "Credits", "", "About the trainer, author and links"),
        };
        _selectedSection = Sections[0];

        AddLog("Waiting for CrimsonDesert.exe — launch the game and load into the world.", LogLevel.Info);

        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Poll(), dispatcher);
        _fast = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FastTick(), dispatcher);
        Poll();
        _poll.Start();
        _fast.Start();
    }

    // ---------------------------------------------------------------- collections

    public IReadOnlyList<CheatViewModel> Cheats { get; }
    public IReadOnlyList<CheatViewModel> PlayerCheats { get; }
    public IReadOnlyList<CheatViewModel> InventoryCheats { get; }
    public IReadOnlyList<CheatViewModel> WorldCheats { get; }
    public PlayerPanelViewModel Player { get; }
    public SpawnerViewModel Spawner { get; }
    public InventorySlotsViewModel InventorySlots { get; }
    public InventoryGridViewModel InventoryGrid { get; }
    public TeleportViewModel Teleport { get; }
    public IconCache Icons { get; }
    public IReadOnlyList<BodyScaleViewModel> BodyScales { get; }
    public IReadOnlyList<SectionViewModel> Sections { get; }
    public ObservableCollection<LogEntry> Log { get; } = new();

    private ToggleCheatViewModel Toggle(string id) => (ToggleCheatViewModel)Cheats.First(c => c.Id == id);

    /// <summary>The "Current Player Pointers" hook — shown at the top of the Player section.</summary>
    public ToggleCheatViewModel PlayerTracking => Toggle(CheatCatalog.PlayerPointersId);

    /// <summary>The swapper / hover hook — shown on the Items screen next to the spawner.</summary>
    public ToggleCheatViewModel ItemSwapper => Toggle(CheatCatalog.ItemSwapperId);

    public System.Windows.Input.ICommand ReloadItemTableCommand => _reloadItemTable ??= new RelayCommand(() => Spawner.ReloadTable(), () => IsAttached && !Spawner.IsScanning);
    private RelayCommand? _reloadItemTable;

    // ---------------------------------------------------------------- credits

    public const string Author = "ViknaCode";
    public const string GitHubUrl = "https://github.com/viknacode/CrimsonDesertTrainer";
    public string VersionText => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "2.0.0");

    public System.Windows.Input.ICommand OpenGitHubCommand => _openGitHub ??= new RelayCommand(OpenGitHub);
    private RelayCommand? _openGitHub;

    public System.Windows.Input.ICommand CopyGitHubLinkCommand => _copyGitHubLink ??= new RelayCommand(CopyGitHubLink);
    private RelayCommand? _copyGitHubLink;

    private void OpenGitHub()
    {
        try
        {
            // UseShellExecute hands the URL to the default browser.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(GitHubUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AddLog($"Could not open the browser: {ex.Message}", LogLevel.Error);
        }
    }

    private void CopyGitHubLink()
    {
        try
        {
            System.Windows.Clipboard.SetText(GitHubUrl);
            AddLog("GitHub link copied to the clipboard.", LogLevel.Info);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            AddLog($"Could not copy the link: {ex.Message}", LogLevel.Error);
        }
    }

    private IEnumerable<HotkeyBindingViewModel> AllBindings =>
        Cheats.SelectMany(c => c.Bindings).Concat(Player.KeepBindings).Concat(InventorySlots.Bindings).Concat(Teleport.Bindings);

    private IEnumerable<ValueFieldViewModel> PersistedFields =>
        Cheats.SelectMany(c => c.Fields).Concat(Player.Fields).Append(InventorySlots.Slots);

    // ---------------------------------------------------------------- state

    public SectionViewModel SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!Set(ref _selectedSection, value)) return;
            InventoryGrid.IsActive = value.Key == "items";
            Teleport.IsActive = value.Key == "teleport";
        }
    }

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsWaiting));
            Raise(nameof(IsAttached));
            Raise(nameof(HasError));
        }
    }

    public bool IsWaiting => State is ConnectionState.Waiting or ConnectionState.Scanning;
    public bool IsAttached => State == ConnectionState.Attached;
    public bool HasError => State == ConnectionState.Error;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public LogEntry? LastLog
    {
        get => _lastLog;
        private set => Set(ref _lastLog, value);
    }

    public int ActiveCount => Cheats.Count(c => c.IsActive) + (Player.KeepHealthFull ? 1 : 0) + (Player.KeepStaminaFull ? 1 : 0) + (Player.KeepSpiritFull ? 1 : 0)
                              + (Player.GodmodeOn ? 1 : 0) + (Player.MaxCombatOn ? 1 : 0) + (InventorySlots.Keep ? 1 : 0);
    public string ActiveText => ActiveCount == 1 ? "1 cheat active" : $"{ActiveCount} cheats active";

    public bool IsCapturing => _capturing is not null;

    // ---------------------------------------------------------------- ITrainerHost

    public async Task<bool> RunAsync(Action operation, string? successMessage = null, LogLevel successLevel = LogLevel.Success)
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Run(operation);
            if (successMessage is not null) AddLog(successMessage, successLevel);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            AddLog(ex.Message, LogLevel.Error);
            return false;
        }
        finally
        {
            _gate.Release();
            Raise(nameof(ActiveCount));
            Raise(nameof(ActiveText));
        }
    }

    void ITrainerHost.Log(string message, LogLevel level) => AddLog(message, level);

    public void SaveSettings()
    {
        _settings.Values = PersistedFields.ToDictionary(f => f.Id, f => f.Text);
        _settings.Hotkeys = AllBindings.Where(b => b.Hotkey is not null).ToDictionary(b => b.Id, b => b.Hotkey!.Value.Serialize());
        _settings.Save();
    }

    private void RestoreFieldTexts()
    {
        foreach (var field in PersistedFields)
            if (_settings.Values.TryGetValue(field.Id, out var text)) field.Text = text;
    }

    private string? Guard(Action write)
    {
        try
        {
            write();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return ex.Message;
        }
    }

    private void NudgeTimeScale(float delta)
    {
        if (!ValueFieldViewModel.TryParseFloat(_timeScaleField.Text, out float current)) current = 1f;
        float next = MathF.Round(Math.Clamp(current + delta, 0.1f, 20f), 2);
        _timeScaleField.Text = ValueFieldViewModel.Format(next);
        _timeScaleField.Apply();
        var cheat = Toggle(CheatCatalog.TimeScaleId);
        if (!cheat.IsOn) _ = cheat.EnsureOnAsync();
        if (_timeScaleField.Error is null) AddLog($"Time scale → {_timeScaleField.Text}", LogLevel.Info);
    }

    // ---------------------------------------------------------------- hotkeys

    /// <summary>Called by the window once its HWND exists.</summary>
    internal void AttachHotkeys(HotkeyManager manager)
    {
        _hotkeys = manager;
        foreach (var binding in AllBindings)
        {
            if (_settings.Hotkeys.TryGetValue(binding.Id, out var text) && Hotkey.TryParse(text, out var hotkey))
            {
                binding.Hotkey = hotkey;
                Register(binding);
            }
        }
    }

    public void BeginCapture(HotkeyBindingViewModel binding)
    {
        if (_capturing is not null) _capturing.IsCapturing = false;
        _capturing = binding;
        binding.IsCapturing = true;
        Raise(nameof(IsCapturing));
    }

    public void CancelCapture()
    {
        if (_capturing is null) return;
        _capturing.IsCapturing = false;
        _capturing = null;
        Raise(nameof(IsCapturing));
    }

    /// <summary>Finishes a capture; null clears the binding.</summary>
    public void CompleteCapture(Hotkey? hotkey)
    {
        var binding = _capturing;
        if (binding is null) return;
        binding.IsCapturing = false;
        _capturing = null;
        Raise(nameof(IsCapturing));

        Unregister(binding);
        binding.Hotkey = hotkey;
        binding.Error = null;
        if (hotkey is not null) Register(binding);
        SaveSettings();
    }

    private void Register(HotkeyBindingViewModel binding)
    {
        if (_hotkeys is null || binding.Hotkey is null) return;
        binding.RegistrationId = _hotkeys.Register(binding.Hotkey.Value, () => _dispatcher.InvokeAsync(binding.Action), out var error);
        binding.Error = error;
        if (error is not null) AddLog($"Hotkey {binding.Hotkey} for \"{binding.Label}\": {error}", LogLevel.Warning);
    }

    private void Unregister(HotkeyBindingViewModel binding)
    {
        if (_hotkeys is null || binding.RegistrationId is null) return;
        _hotkeys.Unregister(binding.RegistrationId.Value);
        binding.RegistrationId = null;
    }

    // ---------------------------------------------------------------- session

    private void Poll()
    {
        if (_disposed) return;

        if (_game is { HasExited: true })
        {
            OnGameExited();
            return;
        }

        if (_game is null && !_attaching)
            _ = AttachAsync();
        else if (IsAttached)
        {
            InventorySlots.Tick();
            InventoryGrid.Tick();
        }
    }

    private void FastTick()
    {
        if (_disposed || !IsAttached) return;
        Player.Tick();
        Spawner.Tick();
        Teleport.Tick();
    }

    private async Task AttachAsync()
    {
        _attaching = true;
        try
        {
            GameProcess? game;
            try
            {
                game = await Task.Run(() => GameProcess.TryAttach(ProcessName, PidFilter));
            }
            catch (Win32Exception ex)
            {
                _poll.Stop();
                State = ConnectionState.Error;
                StatusText = "Access denied";
                AddLog($"{ex.Message} (Win32 error {ex.NativeErrorCode})", LogLevel.Error);
                AddLog("Close the trainer and run it again as Administrator.", LogLevel.Warning);
                return;
            }
            if (game is null) return;

            _game = game;
            State = ConnectionState.Scanning;
            StatusText = "Scanning memory";
            AddLog($"Attached to {game.ModuleName} (PID {game.Process.Id}) — base 0x{(long)game.ModuleBase:X}, size 0x{game.ModuleSize:X}.", LogLevel.Info);

            CheatCatalog catalog;
            try
            {
                catalog = await Task.Run(() =>
                {
                    var c = new CheatCatalog(game);
                    c.ResolveAll(game.ReadModuleImage());
                    return c;
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                State = ConnectionState.Error;
                StatusText = "Scan failed";
                AddLog(ex.Message, LogLevel.Error);
                return;
            }

            _catalog = catalog;
            BindCatalog(catalog, game);
            Spawner.Bind(game);
            _ = Spawner.LoadTableAsync(game);

            State = ConnectionState.Attached;
            StatusText = $"Attached · PID {game.Process.Id}";

            var missing = Cheats.Where(c => !c.IsAvailable).Select(c => c.Name).ToList();
            int ready = Cheats.Count - missing.Count;
            AddLog($"{ready} of {Cheats.Count} scripts found their injection point.", LogLevel.Success);
            if (missing.Count > 0)
                AddLog($"Not found in this game version (probably changed by a patch): {string.Join(", ", missing)}.", LogLevel.Warning);
        }
        finally
        {
            _attaching = false;
        }
    }

    private void BindCatalog(CheatCatalog catalog, GameProcess? game)
    {
        foreach (var vm in Cheats)
            vm.Bind(catalog.Cheats.First(c => c.Id == vm.Id));
        Player.Bind(game is null ? null : catalog.PlayerStats);
        Teleport.Bind(game is null ? null : catalog.PlayerPosition);
        for (int i = 0; i < BodyScales.Count; i++)
            BodyScales[i].Bind(game);
        InventorySlots.Bind(game);
        InventoryGrid.Bind(game);
        Raise(nameof(ActiveCount));
        Raise(nameof(ActiveText));
    }

    private void OnGameExited()
    {
        _catalog?.DeactivateAll();   // process is gone: only resets state
        _catalog = null;
        _game?.Dispose();
        _game = null;

        BindCatalog(_detached, null);
        Spawner.Bind(null);
        Player.KeepHealthFull = Player.KeepStaminaFull = Player.KeepSpiritFull = false;
        Player.GodmodeOn = Player.MaxCombatOn = false;
        State = ConnectionState.Waiting;
        StatusText = "Waiting for game";
        AddLog("Game process exited — all cheats reset. Waiting for it to start again.", LogLevel.Warning);
    }

    private static int? ParsePidArgument()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--pid", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int pid))
                return pid;
        return null;
    }

    private void AddLog(string message, LogLevel level)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => AddLog(message, level));
            return;
        }

        var entry = new LogEntry(DateTime.Now, message, level);
        Log.Add(entry);
        while (Log.Count > MaxLogEntries) Log.RemoveAt(0);
        LastLog = entry;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _fast.Stop();
        SaveSettings();

        Player.Shutdown();           // puts the original maximums back (needs the pointer hook, so before DeactivateAll)
        _catalog?.DeactivateAll();   // restores every patched byte while the game is still running
        _catalog = null;
        _hotkeys?.Dispose();
        _game?.Dispose();
        _game = null;
    }
}
