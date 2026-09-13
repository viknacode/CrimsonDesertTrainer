using System.Collections.ObjectModel;
using System.Globalization;
using CrimsonTrainer.Cheats;
using CrimsonTrainer.Infrastructure;
using CrimsonTrainer.Settings;

namespace CrimsonTrainer.ViewModels;

/// <summary>One slot of the 42-entry combat attribute array (live value + editor).</summary>
public sealed class CombatEntryViewModel : ObservableObject
{
    private readonly Action<CombatEntryViewModel> _onIncludeChanged;
    private string _liveText = "—";
    private bool _isChanged, _includeInMax;
    private int _changedTicks;

    internal CombatEntryViewModel(int index, string? name, ValueFieldViewModel field, Action<CombatEntryViewModel> onIncludeChanged)
    {
        Index = index;
        Name = name ?? $"attribute {index}";
        Field = field;
        _onIncludeChanged = onIncludeChanged;
    }

    public int Index { get; }
    public string Name { get; }
    public string IndexText => $"[{Index}]";
    public ValueFieldViewModel Field { get; }

    /// <summary>Always maxed (Attack / Defense) — the checkbox is shown checked and locked.</summary>
    public bool IsCoreStat => Index is 0 or 1;

    public string LiveText
    {
        get => _liveText;
        internal set => Set(ref _liveText, value);
    }

    /// <summary>True for a few seconds after the live value moved — equip gear and watch this light up.</summary>
    public bool IsChanged
    {
        get => _isChanged;
        private set => Set(ref _isChanged, value);
    }

    /// <summary>Part of "Max Attack &amp; Defense": kept at 999,999 while that toggle is on.</summary>
    public bool IncludeInMax
    {
        get => _includeInMax || IsCoreStat;
        set
        {
            if (IsCoreStat || !Set(ref _includeInMax, value)) return;
            _onIncludeChanged(this);
        }
    }

    internal void SetIncludeSilently(bool value)
    {
        _includeInMax = value;
        Raise(nameof(IncludeInMax));
    }

    /// <summary>Called on every refresh; keeps the marker up for <paramref name="holdRefreshes"/> refreshes after a change.</summary>
    internal void Update(long raw, bool changed, int holdRefreshes)
    {
        LiveText = PlayerPanelViewModel.HudText(raw);
        if (changed) _changedTicks = holdRefreshes;
        else if (_changedTicks > 0) _changedTicks--;
        IsChanged = _changedTicks > 0;
    }
}

/// <summary>
/// Live stats from the "Player Stat Block" pointer chain plus everything built on top of it:
/// editable maximums, "keep full", a pointer-based Godmode, max Attack &amp; Defense and the
/// combat attribute editor. Everything here needs Player tracking on.
/// Values are shown and typed in HUD units; the game stores them × 1000.
/// </summary>
public sealed class PlayerPanelViewModel : ObservableObject
{
    private const long GodValue = 999_999 * PlayerStats.Scale;   // HUD 999,999 — the table used 999,999,999 raw

    private static readonly string?[] CombatNames = new string?[PlayerStats.CombatCount];

    static PlayerPanelViewModel()
    {
        CombatNames[0] = "Attack";
        CombatNames[1] = "Defense";
    }

    private const int ChangedHoldRefreshes = 10;   // refreshes happen every 3 ticks (~0.6 s) → marker stays ~6 s

    private readonly ITrainerHost _host;
    private readonly ToggleCheatViewModel _tracking;
    private readonly AppSettings _settings;
    private PlayerStats? _stats;
    private PlayerSnapshot? _snapshot;
    private long[]? _lastCombatValues;
    private bool _keepHealth, _keepStamina, _keepSpirit;
    private bool _godmode, _maxCombat, _showAttributes;
    private (long Health, long Stamina, long Spirit)? _godmodeOriginals;
    private readonly Dictionary<int, long> _combatOriginals = new();   // attribute index → value before maxing
    private int _tick;

    internal PlayerPanelViewModel(ITrainerHost host, ToggleCheatViewModel tracking, AppSettings settings)
    {
        _host = host;
        _tracking = tracking;
        _settings = settings;
        tracking.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ToggleCheatViewModel.IsOn) or nameof(ToggleCheatViewModel.IsAvailable))
            {
                Raise(nameof(IsTracking));
                Raise(nameof(CanTrack));
                if (!tracking.IsOn) ClearSnapshot();
            }
        };

        Fields = new ObservableCollection<ValueFieldViewModel>
        {
            Field("max_health", "Max Health", StatKind.Health),
            Field("max_stamina", "Max Stamina", StatKind.Stamina),
            Field("max_spirit", "Max Spirit", StatKind.Spirit),
        };

        CombatEntries = new ObservableCollection<CombatEntryViewModel>(
            Enumerable.Range(0, PlayerStats.CombatCount).Select(i => new CombatEntryViewModel(i, CombatNames[i], CombatField(i), OnIncludeInMaxChanged)));
        foreach (int index in settings.MaxedAttributes)
            if (index >= 0 && index < CombatEntries.Count) CombatEntries[index].SetIncludeSilently(true);

        KeepBindings = new ObservableCollection<HotkeyBindingViewModel>
        {
            new("player.keep_health", "Keep Health full", () => KeepHealthFull = !KeepHealthFull, host.BeginCapture),
            new("player.keep_stamina", "Keep Stamina full", () => KeepStaminaFull = !KeepStaminaFull, host.BeginCapture),
            new("player.keep_spirit", "Keep Spirit full", () => KeepSpiritFull = !KeepSpiritFull, host.BeginCapture),
            new("player.godmode", "Godmode", () => GodmodeOn = !GodmodeOn, host.BeginCapture),
            new("player.max_combat", "Max Attack, Defense & selected", () => MaxCombatOn = !MaxCombatOn, host.BeginCapture),
        };
    }

    public ObservableCollection<ValueFieldViewModel> Fields { get; }
    public ObservableCollection<CombatEntryViewModel> CombatEntries { get; }
    public ObservableCollection<HotkeyBindingViewModel> KeepBindings { get; }

    public bool IsTracking => _tracking.IsOn;
    public bool CanTrack => _tracking.IsAvailable;
    public bool HasSnapshot => _snapshot is not null;

    public string HealthText => Pair(_snapshot?.Health, _snapshot?.HealthMax);
    public string StaminaText => Pair(_snapshot?.Stamina, _snapshot?.StaminaMax);
    public string SpiritText => Pair(_snapshot?.Spirit, _snapshot?.SpiritMax);
    public string AttackText => _snapshot is null ? "—" : Hud(_snapshot.Attack);
    public string DefenseText => _snapshot is null ? "—" : Hud(_snapshot.Defense);
    public double HealthRatio => Ratio(_snapshot?.Health, _snapshot?.HealthMax);
    public double StaminaRatio => Ratio(_snapshot?.Stamina, _snapshot?.StaminaMax);
    public double SpiritRatio => Ratio(_snapshot?.Spirit, _snapshot?.SpiritMax);

    /// <summary>Raw numbers, for people comparing with Cheat Engine.</summary>
    public string RawText => _snapshot is null
        ? "Values are read from the game as HUD × 1000."
        : $"raw: HP {_snapshot.Health:N0} / {_snapshot.HealthMax:N0} · stamina {_snapshot.Stamina:N0} / {_snapshot.StaminaMax:N0} · spirit {_snapshot.Spirit:N0} / {_snapshot.SpiritMax:N0}";

    public bool KeepHealthFull
    {
        get => _keepHealth;
        set { if (Set(ref _keepHealth, value) && value) _ = _tracking.EnsureOnAsync(); }
    }

    public bool KeepStaminaFull
    {
        get => _keepStamina;
        set { if (Set(ref _keepStamina, value) && value) _ = _tracking.EnsureOnAsync(); }
    }

    public bool KeepSpiritFull
    {
        get => _keepSpirit;
        set { if (Set(ref _keepSpirit, value) && value) _ = _tracking.EnsureOnAsync(); }
    }

    /// <summary>
    /// Pointer-based replacement for the table's Godmode script (whose hook no longer exists):
    /// raises the three maximums to 999,999 and keeps them full; the original maximums come
    /// back when it is turned off.
    /// </summary>
    public bool GodmodeOn
    {
        get => _godmode;
        set
        {
            if (!Set(ref _godmode, value)) return;
            if (value) _ = _tracking.EnsureOnAsync();   // applied on the next tick with a valid snapshot
            else RestoreGodmode();
        }
    }

    /// <summary>
    /// Pointer-based replacement for the table's "Max Resistance Stats + Attack &amp; Defense" hook
    /// (its increment instruction no longer exists): Attack, Defense and every attribute ticked in
    /// the editor are held at 999,999; the originals come back when it is turned off.
    /// </summary>
    public bool MaxCombatOn
    {
        get => _maxCombat;
        set
        {
            if (!Set(ref _maxCombat, value)) return;
            if (value) _ = _tracking.EnsureOnAsync();
            else RestoreCombat();
        }
    }

    public bool ShowAttributes
    {
        get => _showAttributes;
        set => Set(ref _showAttributes, value);
    }

    /// <summary>Attribute indices currently covered by <see cref="MaxCombatOn"/> (Attack, Defense and the ticked ones).</summary>
    public IEnumerable<int> MaxedIndices => CombatEntries.Where(e => e.IncludeInMax).Select(e => e.Index);

    internal void Bind(PlayerStats? stats)
    {
        _stats = stats;
        _godmodeOriginals = null;
        _combatOriginals.Clear();
        _lastCombatValues = null;
        if (stats is null)
        {
            _godmode = _maxCombat = false;
            Raise(nameof(GodmodeOn));
            Raise(nameof(MaxCombatOn));
        }
        ClearSnapshot();
    }

    /// <summary>Puts the game's own maximums back before the trainer closes.</summary>
    internal void Shutdown()
    {
        try
        {
            if (_godmode) RestoreGodmode();
            if (_maxCombat) RestoreCombat();
        }
        catch (Exception)
        {
            // The game may already be gone.
        }
    }

    /// <summary>Called ~5×/s from the session timer while attached.</summary>
    internal void Tick()
    {
        if (_stats is null || !_tracking.IsOn) return;

        PlayerSnapshot? snapshot;
        try { snapshot = _stats.Read(); }
        catch (Exception) { snapshot = null; }

        _snapshot = snapshot;
        RaiseSnapshot();
        if (snapshot is null) return;

        try
        {
            if (_godmode) ApplyGodmode(snapshot);
            if (_maxCombat) ApplyMaxCombat();

            bool fillHealth = _keepHealth || _godmode, fillStamina = _keepStamina || _godmode, fillSpirit = _keepSpirit || _godmode;
            if (fillHealth && snapshot.Health < snapshot.HealthMax) _stats.WriteStat(StatKind.Health, false, snapshot.HealthMax);
            if (fillStamina && snapshot.Stamina < snapshot.StaminaMax) _stats.WriteStat(StatKind.Stamina, false, snapshot.StaminaMax);
            if (fillSpirit && snapshot.Spirit < snapshot.SpiritMax) _stats.WriteStat(StatKind.Spirit, false, snapshot.SpiritMax);

            if (_showAttributes && ++_tick % 3 == 0) RefreshCombatEntries();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            // Transient: the game is probably shutting down; the session poll will notice.
        }
    }

    private void ApplyGodmode(PlayerSnapshot snapshot)
    {
        if (_stats is null) return;
        if (_godmodeOriginals is null)
        {
            _godmodeOriginals = (snapshot.HealthMax, snapshot.StaminaMax, snapshot.SpiritMax);
            _host.Log($"Godmode → ON (maximums were {Hud(snapshot.HealthMax)} / {Hud(snapshot.StaminaMax)} / {Hud(snapshot.SpiritMax)})", LogLevel.Success);
        }
        if (snapshot.HealthMax != GodValue) _stats.WriteStat(StatKind.Health, true, GodValue);
        if (snapshot.StaminaMax != GodValue) _stats.WriteStat(StatKind.Stamina, true, GodValue);
        if (snapshot.SpiritMax != GodValue) _stats.WriteStat(StatKind.Spirit, true, GodValue);
    }

    private void RestoreGodmode()
    {
        if (_stats is null || _godmodeOriginals is null) return;
        var (health, stamina, spirit) = _godmodeOriginals.Value;
        _godmodeOriginals = null;
        bool ok = _stats.WriteStat(StatKind.Health, true, health) && _stats.WriteStat(StatKind.Health, false, health)
               && _stats.WriteStat(StatKind.Stamina, true, stamina) && _stats.WriteStat(StatKind.Stamina, false, stamina)
               && _stats.WriteStat(StatKind.Spirit, true, spirit) && _stats.WriteStat(StatKind.Spirit, false, spirit);
        _host.Log(ok ? $"Godmode → OFF, maximums restored ({Hud(health)} / {Hud(stamina)} / {Hud(spirit)})" : "Godmode → OFF, but the maximums could not be restored (player not readable).",
            ok ? LogLevel.Info : LogLevel.Warning);
    }

    private void ApplyMaxCombat()
    {
        if (_stats is null) return;
        var values = _stats.ReadCombatArray();
        if (values is null) return;

        bool first = _combatOriginals.Count == 0;
        var added = new List<string>();
        foreach (int index in MaxedIndices)
        {
            if (!_combatOriginals.ContainsKey(index))
            {
                _combatOriginals[index] = values[index];
                if (!first) added.Add($"{EntryLabel(index)} (was {Hud(values[index])})");
            }
            if (values[index] != GodValue) _stats.WriteCombat(index * 8, GodValue);
        }
        if (first)
            _host.Log($"Max Attack & Defense → ON: {string.Join(", ", _combatOriginals.Select(kv => $"{EntryLabel(kv.Key)} was {Hud(kv.Value)}"))}", LogLevel.Success);
        else if (added.Count > 0)
            _host.Log($"Max Attack & Defense now also covers {string.Join(", ", added)}", LogLevel.Success);
    }

    private void RestoreCombat()
    {
        if (_stats is null || _combatOriginals.Count == 0) return;
        bool ok = true;
        var restored = new List<string>();
        foreach (var (index, value) in _combatOriginals)
        {
            ok &= _stats.WriteCombat(index * 8, value);
            restored.Add($"{EntryLabel(index)} {Hud(value)}");
        }
        _combatOriginals.Clear();
        _host.Log(ok ? $"Max Attack & Defense → OFF, restored {string.Join(", ", restored)}" : "Max Attack & Defense → OFF, but the values could not be restored.",
            ok ? LogLevel.Info : LogLevel.Warning);
    }

    /// <summary>An attribute was ticked or unticked in the editor: persist it and, if maxing is on, apply or undo right away.</summary>
    private void OnIncludeInMaxChanged(CombatEntryViewModel entry)
    {
        _settings.MaxedAttributes = CombatEntries.Where(e => e.IncludeInMax && !e.IsCoreStat).Select(e => e.Index).ToList();
        _host.SaveSettings();
        if (!_maxCombat || _stats is null) return;

        if (entry.IncludeInMax)
        {
            if (_snapshot is not null) ApplyMaxCombat();
        }
        else if (_combatOriginals.Remove(entry.Index, out long original))
        {
            bool ok = _stats.WriteCombat(entry.Index * 8, original);
            _host.Log(ok ? $"{EntryLabel(entry.Index)} restored to {Hud(original)}" : $"{EntryLabel(entry.Index)} could not be restored.", ok ? LogLevel.Info : LogLevel.Warning);
        }
    }

    private static string EntryLabel(int index) => CombatNames[index] is { } name ? name : $"[{index}]";

    private void RefreshCombatEntries()
    {
        var values = _stats?.ReadCombatArray();
        if (values is null) return;
        var previous = _lastCombatValues;
        for (int i = 0; i < values.Length && i < CombatEntries.Count; i++)
            CombatEntries[i].Update(values[i], previous is not null && previous[i] != values[i], ChangedHoldRefreshes);
        _lastCombatValues = values;
    }

    private ValueFieldViewModel Field(string key, string label, StatKind kind) =>
        new($"player.{key}", label, "", text =>
        {
            if (!TryParseHud(text, out long raw)) return "Enter the value as shown in the HUD (e.g. 750).";
            if (_stats is null || !_tracking.IsOn) return "Turn on Player tracking first.";
            if (!_stats.WriteStat(kind, true, raw) || !_stats.WriteStat(kind, false, raw)) return "Player not found yet — move around for a moment.";
            _host.Log($"{label} → {Hud(raw)} (raw {raw:N0})", LogLevel.Success);
            return null;
        }, hint: "HUD units — sets the maximum and refills to it.", onApplied: _ => _host.SaveSettings());

    private ValueFieldViewModel CombatField(int index) =>
        new($"player.combat{index}", CombatNames[index] ?? $"[{index}]", "", text =>
        {
            if (!ValueFieldViewModel.TryParseFloat(text, out float hud) || hud < -99_999_999 || hud > 99_999_999) return "Enter a number (HUD units, negatives allowed).";
            long raw = (long)Math.Round(hud * PlayerStats.Scale);
            if (_stats is null || !_tracking.IsOn) return "Turn on Player tracking first.";
            if (!_stats.WriteCombat(index * 8, raw)) return "Player not found yet — move around for a moment.";
            _host.Log($"Combat attribute [{index}] → {Hud(raw)} (raw {raw:N0})", LogLevel.Success);
            return null;
        });

    /// <summary>"750" or "12.5" in HUD units → raw fixed-point value.</summary>
    private static bool TryParseHud(string text, out long raw)
    {
        raw = 0;
        if (!ValueFieldViewModel.TryParseFloat(text, out float hud) || hud <= 0 || hud > 99_999_999) return false;
        raw = (long)Math.Round(hud * PlayerStats.Scale);
        return true;
    }

    private static string Hud(long raw) => HudText(raw);

    internal static string HudText(long raw)
    {
        double v = (double)raw / PlayerStats.Scale;
        return v == Math.Floor(v) ? v.ToString("N0", CultureInfo.InvariantCulture) : v.ToString("N1", CultureInfo.InvariantCulture);
    }

    private void ClearSnapshot()
    {
        _snapshot = null;
        RaiseSnapshot();
    }

    private void RaiseSnapshot()
    {
        Raise(nameof(HasSnapshot));
        Raise(nameof(HealthText)); Raise(nameof(StaminaText)); Raise(nameof(SpiritText));
        Raise(nameof(AttackText)); Raise(nameof(DefenseText)); Raise(nameof(RawText));
        Raise(nameof(HealthRatio)); Raise(nameof(StaminaRatio)); Raise(nameof(SpiritRatio));
    }

    private static string Pair(long? current, long? max) => current is null ? "—" : $"{Hud(current.Value)} / {Hud(max!.Value)}";
    private static double Ratio(long? current, long? max) => current is null || max is null or <= 0 ? 0 : Math.Clamp((double)current / max.Value, 0, 1);
}
