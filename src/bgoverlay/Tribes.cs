namespace BgOverlay;

/// <summary>
/// Battlegrounds tribes by the Race ids Firestone's stats use
/// (hs-reference-data enums/race.ts). Power.log prints the same races by name.
/// </summary>
internal static class Tribes
{
    private static readonly Dictionary<string, int> ByLogName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UNDEAD"]     = 11,
        ["MURLOC"]     = 14,
        ["DEMON"]      = 15,
        ["MECHANICAL"] = 17,
        ["MECH"]       = 17,
        ["ELEMENTAL"]  = 18,
        ["BEAST"]      = 20,
        ["PET"]        = 20,
        ["PIRATE"]     = 23,
        ["DRAGON"]     = 24,
        ["QUILBOAR"]   = 43,
        ["NAGA"]       = 92,
        ["ABERRATION"] = 126,
    };

    private static readonly Dictionary<int, string> Names = new()
    {
        [11]  = "Нежить",
        [14]  = "Мурлоки",
        [15]  = "Демоны",
        [17]  = "Механизмы",
        [18]  = "Элементали",
        [20]  = "Звери",
        [23]  = "Пираты",
        [24]  = "Драконы",
        [43]  = "Свинобразы",
        [92]  = "Наги",
        [126] = "Аберрации",
    };

    private static readonly Dictionary<int, string> IconNames = new()
    {
        [11] = "undead", [14] = "murloc", [15] = "demon", [17] = "mech",
        [18] = "elemental", [20] = "pet", [23] = "pirate", [24] = "dragon",
        [43] = "quilboar", [92] = "naga", [126] = "aberration",
    };

    private static readonly Dictionary<int, string> ShortNames = new()
    {
        [11] = "Нежить", [14] = "Мурлок", [15] = "Демон", [17] = "Механизм",
        [18] = "Элементаль", [20] = "Зверь", [23] = "Пират", [24] = "Дракон",
        [43] = "Свинобраз", [92] = "Нага", [126] = "Аберрация",
    };

    /// <summary>Every tribe that can take part in a Battlegrounds lobby.</summary>
    public static IReadOnlyList<int> All { get; } = Names.Keys.ToArray();

    public static string? IconName(int id) => IconNames.TryGetValue(id, out var name) ? name : null;
    public static string ShortName(int id) => ShortNames.TryGetValue(id, out var name) ? name : Name(id);

    /// <summary>Race id for a CARDRACE value, or 0 for ALL/INVALID/unknown.</summary>
    public static int FromLog(string value)
    {
        if (ByLogName.TryGetValue(value, out int id)) return id;
        return int.TryParse(value, out id) && Names.ContainsKey(id) ? id : 0;
    }

    public static string Name(int id) => Names.TryGetValue(id, out var n) ? n : $"раса {id}";
}
