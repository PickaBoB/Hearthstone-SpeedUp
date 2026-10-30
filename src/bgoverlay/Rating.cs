using System.Text.RegularExpressions;

namespace BgOverlay;

public sealed record RatedOption(
    ChoiceOption Option,
    string Tier,            // S..E, "?" without data
    double? AvgPlace,
    string Detail,
    string? TribeHint);

/// <summary>
/// One tribe of this lobby and what it does to the hero's average place:
/// below zero the hero finishes higher with that tribe in the game, above
/// zero lower. Null when the hero's data has nothing to say about it.
/// </summary>
public sealed record LobbyTribe(int Tribe, double? Impact);

public sealed record HeroSummary(RatedOption Hero, double? CombatWinrateThisTurn, double? AvgWarbandThisTurn,
    IReadOnlyList<LobbyTribe> Lobby, bool LobbyCounted);

/// <summary>
/// Tiers the way Firestone builds its tier lists: by average final place,
/// cut at the mean and multiples of the standard deviation across the whole
/// pool, so a tier means the same thing in every lobby.
/// </summary>
internal static class Rating
{
    private const int MinDataPoints = 30;
    private static readonly Regex Skin = new(@"^(.*)_SKIN_.*$", RegexOptions.Compiled);

    public static IReadOnlyList<RatedOption> Rate(PendingChoice choice, GameSnapshot game, StatsStore stats)
    {
        return choice.Kind == ChoiceKind.Hero
            ? RateHeroes(choice.Options, game, stats)
            : RateTrinkets(choice.Options, stats);
    }

    public static string NormalizeHero(string cardId)
    {
        Match m = Skin.Match(cardId);
        string id = m.Success ? m.Groups[1].Value : cardId;
        return id == "TB_BaconShop_HERO_59t" ? "TB_BaconShop_HERO_59" : id;
    }

    /// <summary>The tribes that move this hero's average place up the most.</summary>
    public static IEnumerable<(int Tribe, double Impact)> BestTribes(HeroStat h) =>
        UsableTribeStats(h)
            .Where(t => t.ImpactAveragePosition <= -0.03)
            .OrderBy(t => t.ImpactAveragePosition)
            .Select(t => (t.Tribe, t.ImpactAveragePosition));

    private static IEnumerable<TribeStat> UsableTribeStats(HeroStat h) =>
        h.TribeStats.Where(t => t.DataPoints > h.DataPoints / 20
                             && t.DataPointsOnMissingTribe > t.DataPoints / 20
                             && double.IsFinite(t.ImpactAveragePosition));

    private static IReadOnlyList<RatedOption> RateHeroes(IReadOnlyList<ChoiceOption> options, GameSnapshot game, StatsStore stats)
    {
        HeroStatsFile? file = game.IsDuo ? stats.Duo : stats.Solo;
        if (file is null) return options.Select(NoData).ToList();

        // Firestone only applies the lobby correction in solo; the duo
        // per-tribe samples are too thin.
        IReadOnlyList<int>? tribes = !game.IsDuo && game.TribesConfident ? game.Tribes : null;

        double? Score(HeroStat h)
        {
            if (h.DataPoints < MinDataPoints || !double.IsFinite(h.AveragePosition)) return null;
            if (tribes is null) return h.AveragePosition;
            var relevant = UsableTribeStats(h).Where(t => tribes.Contains(t.Tribe)).ToList();
            return relevant.Count == 0 ? null : h.AveragePosition + relevant.Sum(t => t.ImpactAveragePosition);
        }

        var byId = file.HeroStats.ToDictionary(h => h.HeroCardId);
        var cuts = Cuts(file.HeroStats.Select(Score).OfType<double>().ToList());

        return options.Select(o =>
        {
            if (!byId.TryGetValue(NormalizeHero(o.CardId), out var h) || Score(h) is not double score)
                return NoData(o);
            // Duo teams finish 1st to 4th, so the top half is the top two.
            int half = game.IsDuo ? 2 : 4;
            var places = h.PlacementDistribution.Where(p => double.IsFinite(p.Percentage)).ToList();
            double top1 = places.Where(p => p.Rank == 1).Sum(p => p.Percentage);
            double topHalf = places.Where(p => p.Rank <= half).Sum(p => p.Percentage);
            string detail = $"топ-{half} {topHalf:0}% · 1-е {top1:0}%";
            string? hint = string.Join(", ", BestTribes(h).Take(2).Select(t => Tribes.Name(t.Tribe)));
            return new RatedOption(o, cuts(score), score, detail, hint.Length > 0 ? "любит: " + hint : null);
        }).ToList();
    }

    /// <summary>How the picked hero does overall and at this turn.</summary>
    public static HeroSummary? Summarize(GameSnapshot game, StatsStore stats)
    {
        if (game.MyHeroCardId is null) return null;
        HeroStatsFile? file = game.IsDuo ? stats.Duo : stats.Solo;
        var heroId = NormalizeHero(game.MyHeroCardId);
        var option = new ChoiceOption(0, game.MyHeroCardId, game.MyHeroName ?? game.MyHeroCardId);
        RatedOption rated = RateHeroes(new[] { option }, game, stats)[0];
        HeroStat? h = file?.HeroStats.FirstOrDefault(s => s.HeroCardId == heroId);

        double? combat = h?.CombatWinrate.FirstOrDefault(c => c.Turn == game.Turn)?.Winrate;
        double? warband = h?.WarbandStats.FirstOrDefault(w => w.Turn == game.Turn)?.AverageStats;

        // Both modes carry per-tribe samples, so both can be shown; the
        // correction itself is Firestone's, and Firestone works it into the
        // average place in solo only, where the samples are three times as
        // deep. In duos the numbers stand on their own.
        bool known = game.TribesConfident && h is not null && game.Tribes.Count > 0;
        var usable = known ? UsableTribeStats(h!).ToList() : new List<TribeStat>();
        var lobby = game.Tribes
            .Select(t => new LobbyTribe(t, usable.FirstOrDefault(s => s.Tribe == t)?.ImpactAveragePosition))
            .OrderBy(t => t.Impact ?? double.MaxValue)
            .ToList();

        return new HeroSummary(rated,
            combat is double c && double.IsFinite(c) ? c : null,
            warband is double w && double.IsFinite(w) ? w : null,
            lobby, !game.IsDuo && known && lobby.Any(t => t.Impact is not null));
    }

    private static IReadOnlyList<RatedOption> RateTrinkets(IReadOnlyList<ChoiceOption> options, StatsStore stats)
    {
        TrinketStatsFile? file = stats.Trinkets;
        if (file is null) return options.Select(NoData).ToList();
        int bucket = stats.Percentile;

        double? Score(TrinketStat t)
        {
            var at = t.AveragePlacementAtMmr.FirstOrDefault(p => p.Mmr == bucket);
            if (at?.Placement is double placed && at.DataPoints >= MinDataPoints && double.IsFinite(placed)) return placed;
            return t.DataPoints >= MinDataPoints && t.AveragePlacement is double all && double.IsFinite(all) ? all : null;
        }

        var byId = file.TrinketStats.GroupBy(t => t.TrinketCardId).ToDictionary(g => g.Key, g => g.First());
        var cuts = Cuts(file.TrinketStats.Select(Score).OfType<double>().ToList());

        return options.Select(o =>
        {
            if (!byId.TryGetValue(o.CardId, out var t) || Score(t) is not double score)
                return NoData(o);
            double? pick = t.PickRateAtMmr.FirstOrDefault(p => p.Mmr == bucket)?.PickRate ?? t.PickRate;
            string detail = pick is double pr && double.IsFinite(pr) ? $"берут в {pr * 100:0}%" : "";
            return new RatedOption(o, cuts(score), score, detail, null);
        }).ToList();
    }

    private static Func<double, string> Cuts(IReadOnlyList<double> pool)
    {
        if (pool.Count == 0) return _ => "?";
        double mean = pool.Average();
        double sd = Math.Sqrt(pool.Sum(x => (x - mean) * (x - mean)) / pool.Count);
        return x =>
            x < mean - 3 * sd   ? "S" :
            x < mean - 1.5 * sd ? "A" :
            x < mean            ? "B" :
            x < mean + sd       ? "C" :
            x < mean + 2 * sd   ? "D" : "E";
    }

    private static RatedOption NoData(ChoiceOption o) => new(o, "?", null, "мало данных", null);
}
