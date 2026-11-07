namespace BgOverlay;

/// <summary>
/// Card art for the boards the overlay shows. HearthstoneJSON serves two
/// shapes per card: the wide tile a deck list uses, and the square art a
/// portrait wants. Each is fetched once and kept on disk next to the
/// settings, so a lobby seen twice costs nothing. Art missing upstream (some
/// tokens) simply never appears and the card falls back to its name.
/// </summary>
internal sealed class CardArt : IDisposable
{
    private const string Tiles = "https://art.hearthstonejson.com/v1/tiles/";
    private const string Squares = "https://art.hearthstonejson.com/v1/256x/";
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Image?> _tiles = new();
    private readonly Dictionary<int, Image?> _tribes = new();
    private readonly Dictionary<string, Image?> _icons = new();
    private readonly Dictionary<string, DateTime> _failed = new();
    private readonly HashSet<string> _fetching = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Bumped whenever a new tile has landed, so the overlay redraws.</summary>
    public int Version { get; private set; }

    private static string Dir => Path.Combine(Settings.Dir, "cards");

    /// <summary>The wide tile, for a card in a list.</summary>
    public Image? Tile(string cardId) => Get(cardId, square: false);

    /// <summary>The square art, for a minion's portrait on a board.</summary>
    public Image? Art(string cardId) => Get(cardId, square: true);

    /// <summary>An embedded game interface image used by Deck Tracker.</summary>
    public Image? Icon(string name)
    {
        if (_icons.TryGetValue(name, out Image? cached)) return cached;
        string resource = "GameSpeed.BattlegroundsIcons." + name.Replace('/', '.') + ".png";
        using var stream = typeof(CardArt).Assembly.GetManifestResourceStream(resource);
        Image? picture = null;
        if (stream is not null)
        {
            using var raw = new Bitmap(stream);
            picture = new Bitmap(raw);
        }
        return _icons[name] = picture;
    }

    /// <summary>The embedded Hearthstone Deck Tracker tribe icon.</summary>
    public Image? Tribe(int tribe)
    {
        if (_tribes.TryGetValue(tribe, out Image? cached)) return cached;
        string? name = Tribes.IconName(tribe);
        using var stream = name is null ? null : typeof(CardArt).Assembly.GetManifestResourceStream(
            $"GameSpeed.TribeIcons.{name}.jpg");
        Image? picture = null;
        if (stream is not null)
        {
            using var raw = new Bitmap(stream);
            picture = new Bitmap(raw);
        }
        return _tribes[tribe] = picture;
    }

    /// <summary>
    /// The picture, or null while it is on its way. Call from the UI thread:
    /// the download runs in the background and only leaves a file.
    /// </summary>
    private Image? Get(string cardId, bool square)
    {
        if (string.IsNullOrEmpty(cardId)) return null;
        string key = square ? cardId + "@art" : cardId;
        if (_tiles.TryGetValue(key, out Image? picture)) return picture;

        string path = Path.Combine(Dir, cardId + (square ? ".jpg" : ".png"));
        if (File.Exists(path))
        {
            try
            {
                // Copied into memory so the file is not held open.
                using var raw = new Bitmap(path);
                picture = new Bitmap(raw);
            }
            catch { picture = null; }
            _tiles[key] = picture;
            return picture;
        }

        Fetch(key, (square ? Squares : Tiles) + cardId + (square ? ".jpg" : ".png"), path);
        return null;
    }

    private void Fetch(string key, string url, string path)
    {
        lock (_failed)
        {
            if (_fetching.Contains(key)) return;
            if (_failed.TryGetValue(key, out DateTime when) && DateTime.UtcNow - when < Retry) return;
            _fetching.Add(key);
        }

        _ = Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                byte[] picture = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                Directory.CreateDirectory(Dir);
                await File.WriteAllBytesAsync(path + ".part", picture).ConfigureAwait(false);
                File.Move(path + ".part", path, overwrite: true);
                ok = true;
            }
            catch { /* no tile for this card, or no network */ }
            lock (_failed)
            {
                _fetching.Remove(key);
                if (ok) Version++;
                else _failed[key] = DateTime.UtcNow;
            }
        });
    }

    public void Dispose()
    {
        foreach (Image? tile in _tiles.Values) tile?.Dispose();
        foreach (Image? tribe in _tribes.Values) tribe?.Dispose();
        foreach (Image? icon in _icons.Values) icon?.Dispose();
        _tiles.Clear();
        _tribes.Clear();
        _icons.Clear();
        _http.Dispose();
    }
}
