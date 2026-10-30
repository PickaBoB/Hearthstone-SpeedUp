using System.Text.Json;

namespace BgOverlay;

public sealed class ResultMinion
{
    /// <summary>Empty in games kept before the list drew card art.</summary>
    public string CardId { get; set; } = "";
    public string Name { get; set; } = "";
    public int    Attack { get; set; }
    public int    Health { get; set; }
    public string Marks { get; set; } = "";
}

/// <summary>One finished game, as the overlay lists it afterwards.</summary>
public sealed class GameResult
{
    /// <summary>Log file and the game's number in it: a replay rewrites, never duplicates.</summary>
    public string    Key { get; set; } = "";
    public DateTime  Finished { get; set; }
    public bool      IsDuo { get; set; }
    public string    HeroCardId { get; set; } = "";
    public string    HeroName { get; set; } = "";
    public int       Place { get; set; }
    public int       Turns { get; set; }
    public List<int> Tribes { get; set; } = new();
    public int       BoardStats { get; set; }
    /// <summary>Rating before and after the game; 0 when it could not be read.</summary>
    public int       RatingBefore { get; set; }
    public int       Rating { get; set; }
    public List<ResultMinion> Board { get; set; } = new();
}

/// <summary>
/// The games this machine has played, kept next to the settings so the list
/// survives a restart of the overlay or of the game.
/// </summary>
internal sealed class History
{
    private const int Keep = 40;
    private static string FilePath => Path.Combine(Settings.Dir, "history.json");

    private readonly List<GameResult> _games = new();

    public IReadOnlyList<GameResult> Games => _games;
    /// <summary>Bumped on every change, so the overlay knows to redraw.</summary>
    public int Version { get; private set; }

    public History() => Load();

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var games = JsonSerializer.Deserialize<List<GameResult>>(File.ReadAllText(FilePath));
            if (games is not null) _games.AddRange(games);
            Sort();
        }
        catch { /* a broken file just means an empty list */ }
    }

    public void Add(GameResult game)
    {
        int at = _games.FindIndex(g => g.Key == game.Key);
        if (at >= 0)
        {
            if (Same(_games[at], game)) return;
            _games[at] = game;
        }
        else _games.Add(game);

        Sort();
        if (_games.Count > Keep) _games.RemoveRange(Keep, _games.Count - Keep);
        Version++;
        Save();
    }

    private static bool Same(GameResult a, GameResult b) =>
        a.Place == b.Place && a.Turns == b.Turns && a.HeroCardId == b.HeroCardId
        && a.BoardStats == b.BoardStats && a.Board.Count == b.Board.Count;

    /// <summary>
    /// The rating lands a little after the game does, so the newest one keeps
    /// taking the current value until the next game starts.
    /// </summary>
    public void SettleRating(int rating, bool duo, TimeSpan within)
    {
        if (rating <= 0 || _games.Count == 0) return;
        GameResult last = _games[0];
        if (last.IsDuo != duo || last.Rating == rating) return;
        if (DateTime.Now - last.Finished > within) return;
        last.Rating = rating;
        Version++;
        Save();
    }

    private void Sort() => _games.Sort((a, b) => b.Finished.CompareTo(a.Finished));

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_games, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* the list is a convenience */ }
    }
}
