using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgOverlay;

internal sealed class Settings
{
    /// <summary>Stats bucket: games of the top N% of players (100 = everyone).</summary>
    public int     MmrPercentile  { get; set; } = 100;
    public bool    OverlayEnabled { get; set; } = true;
    /// <summary>Badge row heights, as a share of the game window's height.</summary>
    public double  HeroBadgeY     { get; set; } = 0.20;
    public double  TrinketBadgeY  { get; set; } = 0.20;
    /// <summary>Height of the hovered-board strip over the play area.</summary>
    [JsonPropertyName("BannerY")]
    public double  BoardY         { get; set; } = 0.018;
    public bool    PanelEnabled   { get; set; } = true;

    /// <summary>
    /// The game's own leaderboard down the left edge, in fractions of the
    /// client height: where the portraits start, how far apart they sit and
    /// how wide the strip is. Hovering one of them shows that board.
    /// </summary>
    public bool    PeekEnabled    { get; set; } = true;
    /// <summary>Measured on a 1920x1080 client: the portraits sit at x 272…337,
    /// the first row starts at y 165 and the rows are 188 apart.</summary>
    public double  PeekX          { get; set; } = 0.248;
    public double  PeekWidth      { get; set; } = 0.070;
    public double  PeekTop        { get; set; } = 0.152;
    /// <summary>How tall the whole strip is: the step follows from how many
    /// portraits the mode puts in it — eight in solo, four teams in duos.</summary>
    public double  PeekSpan       { get; set; } = 0.700;
    public double  PanelY         { get; set; } = 0.12;
    /// <summary>The turn counter in the top right corner of the play area.</summary>
    public bool    TurnEnabled    { get; set; } = true;
    public double  TurnY          { get; set; } = 0.015;
    /// <summary>The clickable speed buttons in the bottom right corner.</summary>
    public bool    SpeedButtonsEnabled { get; set; } = true;
    /// <summary>How far the speed buttons sit from the bottom edge, as a share
    /// of the game window's height — raised so the strip clears the game's
    /// own buttons in that corner.</summary>
    public double  SpeedButtonsY  { get; set; } = 0.10;
    /// <summary>
    /// The hotkey of each speed preset, as Win32 modifier flags
    /// (MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN) and a virtual-key code.
    /// Defaults: Ctrl+Alt+1..4.
    /// </summary>
    public int[]   SpeedHotkeyMods { get; set; } = { 0x0003, 0x0003, 0x0003, 0x0003 };
    public int[]   SpeedHotkeyKeys { get; set; } = { 0x31, 0x32, 0x33, 0x34 };
    public string? InstallDir     { get; set; }

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BgOverlay");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Current { get; } = Load();

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* a broken file just means defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience */ }
    }
}
