using System.Drawing.Drawing2D;
using System.Text;

namespace BgOverlay;

/// <summary>
/// BgOverlay --replay &lt;Power.log&gt; [--mmr 100|50|25|10|1]
/// Runs a saved log through the parser and prints every choice the overlay
/// would have rated, with the lobby tribes as known at that moment.
///
/// BgOverlay --shot &lt;file.png&gt; [--kind hero|trinket] [--size 1920x1080]
/// Draws the layout preview into an image, to look at it without the game.
/// </summary>
internal static class Cli
{
    public static int Run(string[] args)
    {
        Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;

        string? path = null, shot = null, size = "1920x1080";
        bool rating = false;
        var kind = ChoiceKind.LesserTrinket;
        bool history = false, peek = false;
        string? over = null;
        int mmr = 100;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--replay" && i + 1 < args.Length) path = args[++i];
            else if (args[i] == "--mmr" && i + 1 < args.Length) mmr = int.Parse(args[++i]);
            else if (args[i] == "--shot" && i + 1 < args.Length) shot = args[++i];
            else if (args[i] == "--rating") rating = true;
            else if (args[i] == "--over" && i + 1 < args.Length) over = args[++i];
            else if (args[i] == "--size" && i + 1 < args.Length) size = args[++i];
            else if (args[i] == "--kind" && i + 1 < args.Length)
            {
                string what = args[++i];
                history = what == "history";
                peek = what == "peek";
                kind = what == "hero" ? ChoiceKind.Hero : ChoiceKind.LesserTrinket;
            }
        }
        if (rating) return ShowRating();
        if (shot is not null) return Shot(shot, kind, size, history, peek, over);
        if (path is null)
        {
            Console.WriteLine("Использование: BgOverlay --replay <Power.log> [--mmr 100|50|25|10|1]");
            Console.WriteLine("               BgOverlay --shot <file.png> [--kind hero|trinket|history|peek] [--size 1920x1080]");
            Console.WriteLine("               BgOverlay --rating");
            return 2;
        }

        var stats = new StatsStore();
        stats.LoadAsync(mmr, force: false).GetAwaiter().GetResult();
        if (stats.Solo is null)
        {
            Console.WriteLine("Статистика не загрузилась: " + stats.Error);
            return 1;
        }
        Console.WriteLine($"Статистика: {stats.Solo.HeroStats.Count} героев (соло), {stats.Duo?.HeroStats.Count ?? 0} (дуэты), "
            + $"{stats.Trinkets?.TrinketStats.Count ?? 0} аксессуаров, срез топ-{mmr}%"
            + (stats.Error is null ? "" : $", из кэша: {stats.Error}"));

        var parser = new PowerLogParser();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        bool inChoice = false;
        int lastPrinted = -1, lastCombat = -1, game = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Contains("GameState.DebugPrintPower() - CREATE_GAME", StringComparison.Ordinal))
            {
                if (game > 0) PrintTribes(parser.Snapshot());
                Console.WriteLine($"\n=== Игра {++game} ===");
                lastPrinted = -1;
                lastCombat = -1;
            }

            bool choiceLine = line.Contains("GameState.DebugPrintEntityChoices()", StringComparison.Ordinal);
            inChoice = choiceLine;

            if (line.Contains("GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE", StringComparison.Ordinal))
            {
                PrintTribes(parser.Snapshot());
                PrintLobby(parser.Snapshot());
            }

            // Shop is over as TURN goes even: print the board that goes to combat.
            const string TurnTag = "TAG_CHANGE Entity=GameEntity tag=TURN value=";
            int t = line.IndexOf(TurnTag, StringComparison.Ordinal);
            if (t >= 0 && line.Contains("GameState.DebugPrintPower()", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(t + TurnTag.Length).Trim(), out int raw) && raw % 2 == 0)
                PrintTurn(parser.Snapshot(), stats);

            parser.Feed(line);
            foreach (var done in parser.TakeOutcomes()) PrintOutcome(done);

            // A choice is only on screen once the animation reaches its turn,
            // which is where the overlay starts rating it.
            if (line.Contains("PowerTaskList.DebugPrintPower()", StringComparison.Ordinal)
                && (line.Contains("Entity=GameEntity tag=TURN value=", StringComparison.Ordinal)
                 || line.Contains("Entity=GameEntity tag=BOARD_VISUAL_STATE value=", StringComparison.Ordinal)
                 || line.Contains("BLOCK_START BlockType=ATTACK", StringComparison.Ordinal)))
            {
                var shown = parser.Snapshot();
                if (shown.Choice is { } c && c.Id != lastPrinted)
                {
                    lastPrinted = c.Id;
                    Print(line, shown, stats);
                }
                if (shown.Combat is { } fight && fight.Id != lastCombat)
                {
                    lastCombat = fight.Id;
                    PrintCombat(shown);
                }
            }

            if (line.Contains("GameState.DebugPrintPower() - BLOCK_START BlockType=ATTACK", StringComparison.Ordinal))
            {
                var snap = parser.Snapshot();
                if (snap.Combat is { } c && c.Id != lastCombat)
                {
                    lastCombat = c.Id;
                    PrintCombat(snap);
                }
            }
        }
        if (game > 0 && parser.Snapshot().InBattlegrounds)
        {
            PrintTribes(parser.Snapshot());
            PrintLobby(parser.Snapshot());
            PrintSeen(parser.Snapshot());
        }
        return 0;
    }

    /// <summary>What the overlay can see of the rating right now.</summary>
    private static int ShowRating()
    {
        var hs = System.Diagnostics.Process.GetProcessesByName("Hearthstone").FirstOrDefault();
        if (hs is null) { Console.WriteLine("Hearthstone не запущен."); return 1; }
        using var reader = new RatingReader();
        reader.Poll(hs.Id);
        if (!reader.Known)
        {
            Console.WriteLine($"Рейтинг не читается (PID {hs.Id}): модуль gsunity не в игре "
                + "или ещё не успел прочитать кэш аккаунта.");
            return 1;
        }
        Console.WriteLine($"Рейтинг: соло {reader.Solo}, дуэты {reader.Duo} (PID {hs.Id}).");
        return 0;
    }

    /// <summary>The layout as the overlay draws it, on a stand-in for the game.</summary>
    private static int Shot(string file, ChoiceKind kind, string size, bool history, bool peek,
        string? over)
    {
        string[] wh = size.Split('x', 'X');
        if (wh.Length != 2 || !int.TryParse(wh[0], out int w) || !int.TryParse(wh[1], out int h))
        {
            Console.WriteLine("Размер задаётся как 1920x1080.");
            return 2;
        }
        OverlayModel model = peek ? OverlayForm.SamplePeek()
                           : history ? OverlayForm.SampleHistory()
                           : OverlayForm.Sample(kind);
        using var art = new CardArt();
        var wanted = (model.Peek?.Boards.SelectMany(b => b.Board) ?? Enumerable.Empty<BoardCard>())
            .Concat(model.History?.Rows.SelectMany(r => r.Board) ?? Enumerable.Empty<BoardCard>())
            .ToList();
        if (wanted.Count > 0)
        {
            // The tiles arrive over the network; a shot waits for them.
            foreach (BoardCard card in wanted)
            {
                art.Tile(card.CardId);
                art.Art(card.CardId);
            }
            foreach (PeekBoard board in model.Peek?.Boards ?? Enumerable.Empty<PeekBoard>())
                art.Art(board.HeroCard);
            foreach (HistoryRow past in model.History?.Rows ?? Enumerable.Empty<HistoryRow>())
                art.Art(past.HeroCard);
            Thread.Sleep(3000);
        }

        // A shot of the game itself makes a far better backdrop for checking
        // that the overlay lines up with what it is pointing at.
        using Image? shotOver = over is not null && File.Exists(over) ? Image.FromFile(over) : null;
        if (shotOver is not null) { w = shotOver.Width; h = shotOver.Height; }

        using var bmp = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            if (shotOver is not null) g.DrawImage(shotOver, 0, 0, w, h);
            else
            {
                using var back = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                    Color.FromArgb(44, 38, 56), Color.FromArgb(10, 9, 14), LinearGradientMode.Vertical);
                g.FillRectangle(back, 0, 0, w, h);
            }
            OverlayForm.Draw(g, new Size(w, h), model, art);
        }
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"Записал {file} ({w}x{h}).");
        return 0;
    }

    private static void Print(string line, GameSnapshot snap, StatsStore stats)
    {
        var c = snap.Choice!;
        string time = line.Length > 10 ? line.Substring(2, 8) : "";
        string what = c.Kind switch
        {
            ChoiceKind.Hero => "Выбор героя",
            ChoiceKind.LesserTrinket => "Малый аксессуар",
            _ => "Большой аксессуар",
        };
        Console.WriteLine($"[{time}] {what}{(snap.IsDuo ? " (дуэты)" : "")}; расы: "
            + (snap.Tribes.Count == 0 ? "ещё не видны"
               : string.Join(", ", snap.Tribes.Select(Tribes.Name)) + (snap.TribesConfident ? "" : " (предварительно)")));
        foreach (var o in Rating.Rate(c, snap, stats))
        {
            string place = o.AvgPlace is double p ? $"{p:0.00}" : " -- ";
            Console.WriteLine($"   {o.Tier}  {place}  {o.Option.Name} [{o.Option.CardId}]  {o.Detail}"
                + (o.TribeHint is null ? "" : " · " + o.TribeHint));
        }
    }

    private static void PrintTurn(GameSnapshot snap, StatsStore stats)
    {
        var s = Rating.Summarize(snap, stats);
        string hero = s is null ? "герой ?" : $"{s.Hero.Option.Name} [{s.Hero.Tier}]";
        string wr = s?.CombatWinrateThisTurn is double w ? $"{w:0}%" : "--";
        string avg = s?.AvgWarbandThisTurn is double a ? $"{a:0}" : "--";
        Console.WriteLine($"   ход {snap.Turn,2}: {hero}; армия {snap.BoardStats} ({snap.BoardMinions} сущ.), "
            + $"средняя {avg}; бои героя на ходу {wr}");
        // What this lobby's tribes do to that hero's average place.
        if (s is not null && s.Lobby.Any(t => t.Impact is not null))
            Console.WriteLine("           расы: " + string.Join(" · ", s.Lobby.Select(t =>
                $"{Tribes.Name(t.Tribe)} {(t.Impact is double v ? v.ToString("+0.00;-0.00;0.00") : "--")}")));
    }

    private static void PrintCombat(GameSnapshot snap)
    {
        if (snap.CombatWaiting || snap.Combat is null)
        {
            Console.WriteLine($"   бой: жду карты ({(snap.IsDuo ? "дуэты" : "соло")})");
            return;
        }
        var g = snap.Combat.Guess();
        Console.WriteLine($"   бой ход {snap.Turn}, удар {snap.Combat.Blow}: "
            + $"{g.Win!.Value * 100:0}% поб. / {g.Tie * 100:0}% ничья / {g.Lose * 100:0}% пор.  "
            + $"{g.MyStats} vs {g.TheirStats}  (мы {snap.Combat.Mine.Count} герой, они {snap.Combat.Theirs.Count})"
            + string.Concat(snap.Combat.Mine.Select(s => $" | {s.HeroName ?? s.HeroCardId} {s.Board.Count}сущ {s.Stats}")));
        if (snap.Combat.TeamGuess() is { } team)
            Console.WriteLine($"        командой: {team.Win * 100:0}% поб. / {team.Tie * 100:0}% ничья / "
                + $"{team.Lose * 100:0}% пор.  {team.MyStats} vs {team.TheirStats}"
                + $"  (доска союзника {snap.Combat.MyBench.Sum(s => s.Stats)} с хода {snap.Combat.BenchTurn})");
    }

    private static void PrintOutcome(GameOutcome o)
    {
        Console.WriteLine($"   ИТОГ: {o.Place} место, {o.HeroName} [{o.HeroCardId}], ход {o.Turn}, "
            + $"доска {o.BoardStats} ({o.Board.Count} сущ.), расы: "
            + string.Join(", ", o.Tribes.Select(Tribes.Name)) + $", время {o.At:hh\\:mm}");
        if (o.Board.Count > 0)
            Console.WriteLine("         " + string.Join(", ", o.Board.Select(m => $"{m.Name} {m.Attack}/{m.Health}")));
    }

    /// <summary>The lobby in the order the game's own leaderboard shows it.</summary>
    private static void PrintLobby(GameSnapshot snap)
    {
        if (snap.Lobby is not { Count: > 0 } lobby) return;
        Console.WriteLine("   Таблица игроков:");
        foreach (LobbyPlayer p in lobby)
            Console.WriteLine($"     {p.Place}. {p.Hero}{(p.Friendly ? " (свои)" : "")}"
                + $"{(p.Alive ? "" : " — выбыл")}"
                + (p.Board is { } b ? $"  доска с хода {b.Turn}: {b.Stats} ат.+зд." : "  доски не видели"));
    }

    /// <summary>The boards of the other players, as last seen on the field.</summary>
    private static void PrintSeen(GameSnapshot snap)
    {
        if (snap.Seen is not { Count: > 0 } seen) return;
        Console.WriteLine("   Доски соперников, как их видели в последний раз:");
        foreach (SeenBoard b in seen)
        {
            Console.WriteLine($"     ход {b.Turn,2}  {(b.Friendly ? "союзник" : "соперник")}  {b.HeroName} "
                + $"[{b.HeroCardId}] {b.Hp} зд., таверна {b.TechLevel}, {b.Stats} ат.+зд.");
            Console.WriteLine("        " + string.Join(", ", b.Board.Select(m => $"{m.Name} {m.Attack}/{m.Health}"
                + (m.Taunt ? " провок." : "") + (m.DivineShield ? " щит" : "")
                + (m.Poisonous ? " яд" : "") + (m.Reborn ? " возр." : "") + (m.Windfury ? " вихрь" : ""))));
        }
    }

    private static void PrintTribes(GameSnapshot snap)
    {
        if (snap.TribeCounts.Count == 0) return;
        Console.WriteLine("   Существа по расам: "
            + string.Join(" ", snap.TribeCounts.Select(t => $"{Tribes.Name(t.Tribe)}={t.Seen}"))
            + (snap.TribesConfident ? "  → уверенно" : "  → неуверенно"));
    }
}
