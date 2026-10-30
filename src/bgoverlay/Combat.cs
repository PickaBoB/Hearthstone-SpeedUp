using System.Text.Json.Nodes;

namespace BgOverlay;

public sealed record SimEnchant(string CardId, int Origin, int Data1, int Data2, int Timing);

public sealed record SimMinion(int EntityId, string CardId, int Attack, int Health, int MaxHealth,
    bool Taunt, bool DivineShield, bool Poisonous, bool Venomous, bool Reborn, bool Windfury, bool Stealth,
    int Data1, int Data2, IReadOnlyList<SimEnchant> Enchantments,
    bool MegaWindfury = false, bool CantAttack = false);

public sealed record SimCard(int EntityId, string CardId, bool Used, int Data1, int Data2);

/// <summary>One hero and the board it brought into a fight.</summary>
public sealed record SimSide(int HeroEntityId, string HeroCardId, string? HeroName, int Hp, int TavernTier,
    IReadOnlyList<SimCard> HeroPowers, IReadOnlyList<SimCard> Trinkets, IReadOnlyList<SimMinion> Board)
{
    public int Stats => Board.Sum(m => m.Attack + m.Health);
}

/// <summary>
/// The boards of one combat. In Duos each team can field a second hero once
/// the first one's board is wiped; such boards are listed in order of entry.
/// </summary>
public sealed record CombatGuess(
    bool Waiting,
    string Status,
    double? Win,
    double? Tie,
    double? Lose,
    int MyStats,
    int TheirStats)
{
    public static CombatGuess Hidden { get; } = new(false, "", null, null, null, 0, 0);
    public static CombatGuess Pending(string why) => new(true, why, null, null, null, 0, 0);
}

public sealed record CombatInfo(int Id, int Turn, bool IsDuo, IReadOnlyList<SimSide> Mine, IReadOnlyList<SimSide> Theirs)
{
    /// <summary>How many blows had been struck when these boards were read.</summary>
    public int Blow { get; init; }

    /// <summary>Duos: the partner's board, still waiting to step in.</summary>
    public IReadOnlyList<SimSide> MyBench { get; init; } = Array.Empty<SimSide>();
    /// <summary>The turn that board was last seen fighting.</summary>
    public int BenchTurn { get; init; }

    /// <summary>What the boards hold, which is what the count depends on.</summary>
    private string Content =>
        $"{Id}:{Mine.Count}:{Theirs.Count}:{Mine.Sum(s => s.Board.Count)}:{Theirs.Sum(s => s.Board.Count)}"
        + $":{Mine.Sum(s => s.Stats)}:{Theirs.Sum(s => s.Stats)}:{MyBench.Sum(s => s.Stats)}";

    public string Key => $"{Content}:{Blow}";

    /// <summary>How many times the fight is played out for one reading.</summary>
    private const int Runs = 4000;

    private CombatGuess? _guess;
    private CombatGuess? _team;

    public CombatGuess Guess() => _guess ??= Reckon(Mine, Theirs);

    /// <summary>
    /// The fight counted with the partner's board as well — what our team
    /// brings if this board is wiped and theirs steps in. The opponents'
    /// second board is not in it: the client is never told what it holds.
    /// </summary>
    public CombatGuess? TeamGuess()
    {
        if (MyBench.Count == 0) return null;
        CombatGuess g = _team ??= Reckon(Mine.Concat(MyBench).ToList(), Theirs);
        return g.Waiting ? null : g;
    }

    /// <summary>
    /// Plays the fight out a few thousand times and reports how it went. The
    /// boards are read anew after every blow, so a board that has just been
    /// wiped reads as the loss it is rather than as a chance.
    /// </summary>
    private CombatGuess Reckon(IReadOnlyList<SimSide> us, IReadOnlyList<SimSide> them)
    {
        if (us.Count == 0 || them.Count == 0) return CombatGuess.Pending("Жду доски…");
        int mine = us.Sum(s => s.Board.Count), theirs = them.Sum(s => s.Board.Count);
        // Both sides bare is the fight not read yet, not a draw: the boards
        // are only ever read while a blow is being struck.
        if (mine == 0 && theirs == 0) return CombatGuess.Pending("Жду карты…");

        // The same boards always read the same: the figures are not to dance
        // under the player while nothing on the field moves.
        CombatSim.Odds o = CombatSim.Run(us, them, Runs, Content.GetHashCode());
        return new CombatGuess(false, "", o.Win, o.Tie, o.Lose,
            us.Sum(s => s.Stats), them.Sum(s => s.Stats));
    }

    /// <summary>The simulator's BgsBattleInfo.</summary>
    public JsonObject ToBattleInfo(int simulations, int maxMs)
    {
        var o = new JsonObject
        {
            ["playerBoard"] = Board(Mine[0], true),
            ["opponentBoard"] = Board(Theirs[0], false),
            ["options"] = new JsonObject
            {
                ["numberOfSimulations"] = simulations,
                ["maxAcceptableDuration"] = maxMs,
                ["skipInfoLogs"] = true,
                ["hideMaxSimulationDurationWarning"] = true,
                ["includeOutcomeSamples"] = false,
            },
            ["gameState"] = new JsonObject { ["currentTurn"] = Turn },
        };
        if (Mine.Count > 1) o["playerTeammateBoard"] = Board(Mine[1], true);
        if (Theirs.Count > 1) o["opponentTeammateBoard"] = Board(Theirs[1], false);
        return o;
    }

    private static JsonObject Board(SimSide s, bool friendly)
    {
        var heroPowers = new JsonArray();
        foreach (var hp in s.HeroPowers)
            heroPowers.Add(new JsonObject
            {
                ["cardId"] = hp.CardId, ["entityId"] = hp.EntityId, ["used"] = hp.Used,
                ["info"] = hp.Data1, ["info2"] = hp.Data2, ["info3"] = 0, ["info4"] = 0, ["info5"] = 0, ["info6"] = 0,
            });
        var trinkets = new JsonArray();
        foreach (var t in s.Trinkets)
            trinkets.Add(new JsonObject
            {
                ["cardId"] = t.CardId, ["entityId"] = t.EntityId,
                ["scriptDataNum1"] = t.Data1, ["scriptDataNum2"] = t.Data2,
            });
        var board = new JsonArray();
        foreach (var m in s.Board)
        {
            var ench = new JsonArray();
            foreach (var e in m.Enchantments)
                ench.Add(new JsonObject
                {
                    ["cardId"] = e.CardId, ["originEntityId"] = e.Origin,
                    ["tagScriptDataNum1"] = e.Data1, ["tagScriptDataNum2"] = e.Data2, ["timing"] = e.Timing,
                });
            board.Add(new JsonObject
            {
                ["entityId"] = m.EntityId, ["cardId"] = m.CardId,
                ["attack"] = m.Attack, ["health"] = m.Health, ["maxHealth"] = m.MaxHealth,
                ["taunt"] = m.Taunt, ["divineShield"] = m.DivineShield, ["poisonous"] = m.Poisonous,
                ["venomous"] = m.Venomous, ["reborn"] = m.Reborn, ["windfury"] = m.Windfury,
                ["stealth"] = m.Stealth, ["scriptDataNum1"] = m.Data1, ["scriptDataNum2"] = m.Data2,
                ["enchantments"] = ench, ["friendly"] = friendly,
            });
        }
        return new JsonObject
        {
            ["player"] = new JsonObject
            {
                ["cardId"] = s.HeroCardId, ["entityId"] = s.HeroEntityId, ["hpLeft"] = s.Hp,
                ["tavernTier"] = s.TavernTier, ["heroPowers"] = heroPowers, ["questEntities"] = new JsonArray(),
                ["trinkets"] = trinkets, ["friendly"] = friendly, ["startOfCombatDone"] = true,
                ["globalInfo"] = new JsonObject(),
            },
            ["board"] = board,
        };
    }
}
