using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CrimsonTrainer.Items;

/// <summary>
/// Item icons. The game keeps them inside its encrypted asset packs, so they come from the
/// community database at crimsondb.gg instead: an embedded index maps English item names to
/// the site's icon paths, and each icon is downloaded once as a 64×64 PNG (the site's image
/// proxy converts from WebP) and kept in <c>%LocalAppData%\CrimsonTrainer\icons</c>.
/// Everything is best-effort: no network, no icon — the tiles fall back to a letter.
/// </summary>
public sealed class IconCache
{
    private const string PngUrl = "https://crimsondb.gg/_ipx/f_png&s_64x64/images/items/{0}.webp";
    private const int MaxParallel = 6;

    private static readonly HttpClient Http = CreateClient();

    private readonly Dictionary<string, string> _pathByName;       // exact name → "category/hash"
    private readonly Dictionary<string, string> _pathByLooseName;  // letters+digits only → path
    private readonly ConcurrentDictionary<string, ImageSource?> _loaded = new();
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(MaxParallel, MaxParallel);
    private readonly string _folder;
    private int _failures;

    private IconCache(Dictionary<string, string> index)
    {
        _pathByName = index;
        _pathByLooseName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in index) _pathByLooseName.TryAdd(Loose(name), path);
        _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrimsonTrainer", "icons");
    }

    /// <summary>How many icon names the index knows.</summary>
    public int Count => _pathByName.Count;

    /// <summary>Downloads that failed this session (network gone, site down…).</summary>
    public int Failures => _failures;

    public static IconCache LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon_index.json");
        var index = stream is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        return new IconCache(index);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CrimsonTrainer/2.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/png,image/*;q=0.8");
        return client;
    }

    /// <summary>The icon path for an item name, if the index knows the name.</summary>
    public string? PathFor(string name) =>
        _pathByName.TryGetValue(name, out var p) || _pathByLooseName.TryGetValue(Loose(name), out p) ? p : null;

    /// <summary>Already in memory? (Never touches disk or network.)</summary>
    public ImageSource? Peek(string name) =>
        PathFor(name) is { } path && _loaded.TryGetValue(path, out var img) ? img : null;

    /// <summary>
    /// Gets the icon, loading it from disk or the web on a worker thread. Returns null when
    /// the name is unknown or the download failed. The result is frozen, so any thread may use it.
    /// </summary>
    public Task<ImageSource?> GetAsync(string name)
    {
        var path = PathFor(name);
        if (path is null) return Task.FromResult<ImageSource?>(null);
        if (_loaded.TryGetValue(path, out var cached)) return Task.FromResult(cached);
        return _inFlight.GetOrAdd(path, p => LoadAsync(p).ContinueWith(t =>
        {
            _inFlight.TryRemove(p, out _);
            var result = t.IsCompletedSuccessfully ? t.Result : null;
            _loaded[p] = result;
            return result;
        }));
    }

    private async Task<ImageSource?> LoadAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            string file = Path.Combine(_folder, path.Replace('/', '_') + ".png");
            byte[]? bytes = null;
            if (File.Exists(file))
            {
                try { bytes = await File.ReadAllBytesAsync(file); }
                catch (IOException) { bytes = null; }
            }
            if (bytes is null || bytes.Length == 0)
            {
                if (_failures >= 20) return null;   // the site is unreachable: stop hammering it
                try
                {
                    bytes = await Http.GetByteArrayAsync(string.Format(PngUrl, path));
                    Directory.CreateDirectory(_folder);
                    await File.WriteAllBytesAsync(file, bytes);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    Interlocked.Increment(ref _failures);
                    return null;
                }
            }
            return Decode(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static string Loose(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
