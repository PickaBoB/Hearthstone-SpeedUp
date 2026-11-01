using System.Diagnostics;
using System.Text;

namespace BgOverlay;

/// <summary>
/// The Battlegrounds half of the window: shows what the log reader sees, owns
/// the stats and the overlay, and decides once per tick what the overlay
/// should draw.
/// </summary>
internal sealed class BattlegroundsPanel : UserControl
{
    private static readonly int[] Percentiles = { 100, 50, 25, 10, 1 };
    private static readonly Color Muted = Color.FromArgb(152, 158, 175);

    private readonly LogReader   _reader = new();
    private readonly StatsStore  _stats = new();
    private readonly History     _history = new();
    private readonly RatingReader _rating = new();
    private readonly LobbyNamesReader _lobbyNames = new();
    private readonly CardArt     _art = new();
    private readonly OverlayForm _overlay = new();
    private readonly SpeedStripForm _speedStrip = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 30 * 60 * 1000 };

    private readonly Label    _game = new();
    private readonly Label    _tribes = new();
    private readonly Label    _statsLabel = new();
    private readonly ComboBox _mmr = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _enabled = new() { Text = "Показывать оверлей поверх игры", AutoSize = true };
    private readonly CheckBox _speedOn = new() { Text = "Кнопки скорости в игре (справа снизу)", AutoSize = true };
    private readonly ComboBox _preview = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _heroY = new();
    private readonly NumericUpDown _trinketY = new();
    private readonly NumericUpDown _panelY = new();
    private readonly NumericUpDown _bannerY = new();
    private readonly CheckBox _panelOn = new() { Text = "Панель слева", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox _peekOn = new() { Text = "Доска по наведению", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox _turnOn = new() { Text = "Ход справа сверху", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly NumericUpDown _turnY = new();
    private readonly NumericUpDown _speedY = new();
    private readonly NumericUpDown _peekTop = new();
    private readonly NumericUpDown _peekSpan = new();
    private readonly NumericUpDown _peekWidth = new();
    private readonly TextBox  _details = new();

    private string _lastKey = "";
    private int _hoverRow = -1;
    private int _peekSlot = -1;
    private int _hsProcess;
    private int _ratingBefore;
    private bool _wasInGame;
    private OverlayModel? _model;
    private IntPtr _hsWindow;
    private long _nextWindowLookup;
    private bool _fillingMmr;
    private bool _statsStarted;
    private readonly string _configNote;

    /// <summary>The speed the main window has in force; the strip lights it.</summary>
    public double CurrentSpeed { get; set; } = 1.0;

    /// <summary>A missing-log warning, also shown on the main speed tab.</summary>
    public string OverlayNotice { get; private set; } = "";

    /// <summary>A speed button over the game was clicked.</summary>
    public event Action<double>? SpeedPicked;

    public BattlegroundsPanel()
    {
        Size = new Size(480, 600);
        Font = new Font("Segoe UI", 9.5f);

        _configNote = SafeEnsureLogging();
        _overlay.Art = _art;
        _speedStrip.SpeedPicked += v => SpeedPicked?.Invoke(v);
        BuildLayout();

        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        _refresh.Tick += async (_, _) => await LoadStats(force: false);
        _refresh.Start();
    }

    private static string SafeEnsureLogging()
    {
        try
        {
            return LogLocator.EnsurePowerLogging()
                ? "Включил запись Power.log в log.config — перезапустите Hearthstone."
                : "";
        }
        catch (Exception ex)
        {
            return "Не удалось проверить log.config: " + ex.Message;
        }
    }

    private void BuildLayout()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12),
        };
        Controls.Add(panel);

        _game.AutoSize = false;
        _game.Size = new Size(440, 40);
        _game.Font = new Font(Font, FontStyle.Bold);

        _tribes.AutoSize = false;
        _tribes.Size = new Size(440, 36);

        _statsLabel.AutoSize = false;
        _statsLabel.Size = new Size(450, 36);
        _statsLabel.ForeColor = SystemColors.GrayText;

        var statsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _mmr.Width = 300;
        FillMmrItems();
        _mmr.SelectedIndexChanged += async (_, _) =>
        {
            if (_fillingMmr || _mmr.SelectedIndex < 0) return;
            if (Percentiles[_mmr.SelectedIndex] == Settings.Current.MmrPercentile) return;
            Settings.Current.MmrPercentile = Percentiles[_mmr.SelectedIndex];
            Settings.Current.Save();
            await LoadStats(force: false);
        };
        var refresh = new Button { Text = "Обновить", AutoSize = true };
        refresh.Click += async (_, _) => await LoadStats(force: true);
        statsRow.Controls.AddRange(new Control[] { _mmr, refresh });

        _enabled.Checked = Settings.Current.OverlayEnabled;
        _enabled.CheckedChanged += (_, _) =>
        {
            Settings.Current.OverlayEnabled = _enabled.Checked;
            Settings.Current.Save();
        };

        _speedOn.Checked = Settings.Current.SpeedButtonsEnabled;
        _speedOn.CheckedChanged += (_, _) =>
        {
            Settings.Current.SpeedButtonsEnabled = _speedOn.Checked;
            Settings.Current.Save();
        };

        var layoutBox = new GroupBox { Text = "Положение меток (доля высоты окна игры, %)", Size = new Size(450, 240) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 7, Padding = new Padding(4) };
        SetupY(_heroY, Settings.Current.HeroBadgeY, v => Settings.Current.HeroBadgeY = v);
        SetupY(_trinketY, Settings.Current.TrinketBadgeY, v => Settings.Current.TrinketBadgeY = v);
        SetupY(_panelY, Settings.Current.PanelY, v => Settings.Current.PanelY = v);
        SetupY(_bannerY, Settings.Current.BannerY, v => Settings.Current.BannerY = v);
        _panelOn.Checked = Settings.Current.PanelEnabled;
        _panelOn.CheckedChanged += (_, _) =>
        {
            Settings.Current.PanelEnabled = _panelOn.Checked;
            Settings.Current.Save();
            _overlay.Invalidate();
        };
        SetupY(_peekTop, Settings.Current.PeekTop, v => Settings.Current.PeekTop = v);
        SetupY(_peekSpan, Settings.Current.PeekSpan, v => Settings.Current.PeekSpan = v);
        SetupY(_peekWidth, Settings.Current.PeekWidth, v => Settings.Current.PeekWidth = v);
        _peekOn.Checked = Settings.Current.PeekEnabled;
        _peekOn.CheckedChanged += (_, _) =>
        {
            Settings.Current.PeekEnabled = _peekOn.Checked;
            Settings.Current.Save();
            _lastKey = "";
        };
        SetupY(_turnY, Settings.Current.TurnY, v => Settings.Current.TurnY = v);
        _turnOn.Checked = Settings.Current.TurnEnabled;
        _turnOn.CheckedChanged += (_, _) =>
        {
            Settings.Current.TurnEnabled = _turnOn.Checked;
            Settings.Current.Save();
            _lastKey = "";
        };
        _preview.Items.AddRange(new object[] { "Пример: выкл", "Пример: герои", "Пример: аксессуары" });
        _preview.SelectedIndex = 0;
        _preview.Width = 150;
        _preview.SelectedIndexChanged += (_, _) => _lastKey = "";
        grid.Controls.Add(new Label { Text = "Герои", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        grid.Controls.Add(_heroY, 1, 0);
        grid.Controls.Add(new Label { Text = "Аксессуары", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        grid.Controls.Add(_trinketY, 3, 0);
        grid.Controls.Add(new Label { Text = "Шанс боя", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        grid.Controls.Add(_bannerY, 1, 1);
        grid.Controls.Add(_panelOn, 2, 1);
        grid.Controls.Add(_panelY, 3, 1);
        grid.Controls.Add(_peekOn, 0, 2);
        grid.SetColumnSpan(_peekOn, 4);
        grid.Controls.Add(new Label { Text = "Портреты: верх", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        grid.Controls.Add(_peekTop, 1, 3);
        grid.Controls.Add(new Label { Text = "высота полосы", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 3);
        grid.Controls.Add(_peekSpan, 3, 3);
        grid.Controls.Add(new Label { Text = "ширина полосы", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        grid.Controls.Add(_peekWidth, 1, 4);
        grid.Controls.Add(_preview, 2, 4);
        grid.SetColumnSpan(_preview, 2);
        grid.Controls.Add(_turnOn, 0, 5);
        grid.Controls.Add(_turnY, 1, 5);
        SetupY(_speedY, Settings.Current.SpeedButtonsY, v => Settings.Current.SpeedButtonsY = v);
        grid.Controls.Add(new Label { Text = "Кнопки скорости: низ", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        grid.Controls.Add(_speedY, 1, 6);
        layoutBox.Controls.Add(grid);

        _details.Multiline = true;
        _details.ReadOnly = true;
        _details.ScrollBars = ScrollBars.Vertical;
        _details.Size = new Size(450, 190);
        _details.Font = new Font("Consolas", 9.5f);

        var source = new LinkLabel { Text = "Данные: Firestone (firestoneapp.com)", AutoSize = true };
        source.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo("https://www.firestoneapp.com") { UseShellExecute = true });

        panel.Controls.AddRange(new Control[]
        {
            _game, _tribes, _statsLabel, statsRow, _enabled, _speedOn, layoutBox, _details, source,
        });
    }

    private void SetupY(NumericUpDown n, double value, Action<double> set)
    {
        n.DecimalPlaces = 1;
        n.Increment = 0.5m;
        n.Minimum = 0;
        n.Maximum = 90;
        n.Width = 70;
        n.Value = (decimal)Math.Round(value * 100, 1);
        n.ValueChanged += (_, _) =>
        {
            set((double)n.Value / 100);
            Settings.Current.Save();
            _overlay.Invalidate();
        };
    }

    private void FillMmrItems()
    {
        var known = _stats.Solo?.MmrPercentiles ?? new List<MmrPercentile>();
        _fillingMmr = true;
        try
        {
            _mmr.Items.Clear();
            foreach (int p in Percentiles)
            {
                int? mmr = known.FirstOrDefault(k => k.Percentile == p)?.Mmr;
                _mmr.Items.Add(p == 100
                    ? "Статистика: все игроки"
                    : $"Статистика: топ-{p}% игроков" + (mmr is > 0 ? $" (рейтинг от {mmr})" : ""));
            }
            _mmr.SelectedIndex = Math.Max(0, Array.IndexOf(Percentiles, Settings.Current.MmrPercentile));
        }
        finally { _fillingMmr = false; }
    }

    private async Task LoadStats(bool force)
    {
        await _stats.LoadAsync(Settings.Current.MmrPercentile, force);
        FillMmrItems();
        _lastKey = "";
    }

    // ---- tick --------------------------------------------------------

    private void Tick()
    {
        // The window opens on the speed tab, and a control sitting on a tab
        // nobody has opened never gets its Load event -- the page's handle is
        // built the first time it is shown. The overlay runs from the first
        // tick either way, so the statistics have to start from here too, or
        // everything it rates stays empty until that tab is opened once.
        if (!_statsStarted)
        {
            _statsStarted = true;
            _ = LoadStats(force: false);
        }

        IntPtr hs = FindGameWindow();
        _reader.Parser.ScreenActive(hs != IntPtr.Zero && !Native.IsIconic(hs));
        GameSnapshot snap = _reader.Parser.Snapshot();
        _rating.Poll(_hsProcess);
        _lobbyNames.Poll(_hsProcess);
        // The rating moves between games, so each game is bracketed by the
        // value before it started and the one that lands after it ends.
        if (snap.InBattlegrounds && !_wasInGame) _ratingBefore = _rating.Of(snap.IsDuo);
        _wasInGame = snap.InBattlegrounds;
        StoreOutcomes();
        if (!snap.InBattlegrounds && _rating.Known)
            _history.SettleRating(_rating.Of(_history.Games.Count > 0 && _history.Games[0].IsDuo),
                _history.Games.Count > 0 && _history.Games[0].IsDuo, TimeSpan.FromMinutes(20));

        OverlayNotice = LogNotice(hs);
        UpdateStatus(snap);
        bool up = hs != IntPtr.Zero && !Native.IsIconic(hs) && Native.IsWindowVisible(hs);
        Rectangle client = up ? ClientRect(hs) : Rectangle.Empty;
        bool front = false;
        if (up)
        {
            IntPtr own = TopLevelControl?.Handle ?? IntPtr.Zero;
            IntPtr fg = Native.GetForegroundWindow();
            front = fg == hs || (own != IntPtr.Zero && fg == own);
        }
        bool show = Settings.Current.OverlayEnabled && up && front;
        // The list of past games answers the pointer even though the overlay
        // itself never takes it: the cursor's place is all it needs.
        _hoverRow = show && !snap.InBattlegrounds ? HoverRow(client) : -1;
        _peekSlot = show && snap.InBattlegrounds ? HoverPortrait(client, snap) : -1;

        string key = Key(snap);
        if (key != _lastKey || (snap.InCombat && snap.CombatWaiting))
        {
            _lastKey = key;
            _model = BuildModel(snap);
            _details.Text = Describe(_model);
        }

        _overlay.Present(show && _model is not null ? client : Rectangle.Empty, show ? _model : null);
        // The speed buttons are not part of the overlay's model: they earn
        // their corner whenever the game is up, in and out of Battlegrounds.
        _speedStrip.Present(up && front && Settings.Current.SpeedButtonsEnabled ? client : Rectangle.Empty,
            CurrentSpeed);
    }

    private int HoverRow(Rectangle client)
    {
        int rows = Math.Min(SessionGames().Count, HistoryRows);
        if (rows == 0 || client.IsEmpty || !Native.GetCursorPos(out Native.POINT p)) return -1;
        return OverlayForm.HistoryHit(new Point(p.X - client.X, p.Y - client.Y), rows, client.Size);
    }

    /// <summary>Which portrait of the game's own leaderboard the pointer is on.</summary>
    private int HoverPortrait(Rectangle client, GameSnapshot snap)
    {
        if (client.IsEmpty || snap.Lobby is not { Count: > 0 } lobby) return -1;
        int slots = Slots(snap);
        if (slots == 0 || !Native.GetCursorPos(out Native.POINT p)) return -1;
        return OverlayForm.PeekHit(new Point(p.X - client.X, p.Y - client.Y), slots, client.Size);
    }

    /// <summary>
    /// How many rows the game's leaderboard has: eight players in solo, four
    /// teams in duos. Counting the places we happen to know would shift every
    /// row whenever one of them had not arrived yet.
    /// </summary>
    private static int Slots(GameSnapshot snap) => snap.IsDuo ? 4 : 8;

    /// <summary>Takes the games the parser has finished and keeps them.</summary>
    private void StoreOutcomes()
    {
        var done = _reader.Parser.TakeOutcomes();
        if (done.Count == 0) return;

        string path = _reader.CurrentPath ?? "";
        DateTime start = path.Length > 0 ? LogLocator.SessionStart(path) : DateTime.Now;
        string session = CurrentSession();
        foreach (GameOutcome o in done)
        {
            // Lines carry a time of day only; the log's own folder gives the date.
            DateTime at = start.Date + o.At;
            if (at < start) at = at.AddDays(1);
            _history.Add(new GameResult
            {
                Key = $"{session}#{o.GameIndex}",
                Finished = at,
                IsDuo = o.IsDuo,
                HeroCardId = o.HeroCardId,
                HeroName = o.HeroName,
                Place = o.Place,
                Turns = o.Turn,
                Tribes = o.Tribes.ToList(),
                BoardStats = o.BoardStats,
                RatingBefore = _ratingBefore,
                Rating = _rating.Of(o.IsDuo),
                Board = o.Board
                    .Select(m => new ResultMinion
                    {
                        CardId = m.CardId, Name = m.Name,
                        Attack = m.Attack, Health = m.Health, Marks = m.Marks,
                    })
                    .ToList(),
            });
        }
    }

    private string Key(GameSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(_preview.SelectedIndex).Append('|').Append(_stats.Version).Append('|')
          .Append(_history.Version).Append('|').Append(_hoverRow).Append('|')
          .Append(_rating.Solo).Append('/').Append(_rating.Duo).Append('|')
          .Append(_lobbyNames.Version).Append('|')
          .Append(CurrentSession()).Append('|')
          .Append(_art.Version).Append('|').Append(_peekSlot).Append('|')
          .Append(string.Join(",", (s.Seen ?? []).Select(b => $"{b.PlayerId}:{b.Turn}:{b.Stats}"))).Append('|')
          .Append(string.Join(",", (s.Lobby ?? []).Select(p =>
              $"{p.PlayerName}:{p.Tier}:{string.Join(".", (p.Tavern ?? []).Select(t => $"{t.Level}={t.Turn}"))}"))).Append('|')
          .Append(s.IsDuo).Append('|').Append(s.TribesConfident).Append('|')
          .Append(string.Join(",", s.Tribes)).Append('|');
        sb.Append(s.InBattlegrounds).Append('|').Append(s.MyHeroCardId).Append(s.MyHeroName).Append('|')
          .Append(s.Turn).Append(s.InCombat).Append('|').Append(s.BoardStats).Append('/').Append(s.BoardMinions).Append('|')
          .Append(s.CombatWaiting).Append('|').Append(s.Combat?.Key).Append('|')
          .Append(s.Combat?.HasExactGuess).Append('|')
          .Append(s.CombatEnemyTeamOut).Append('|').Append(s.CombatOverall?.Key).Append('|')
          .Append(s.CombatOverall?.HasExactGuess).Append('|')
          .Append(string.Join(",", (s.Buffs ?? []).Select(b => $"{b.Kind}:{b.Attack}/{b.Health}"))).Append('|')
          .Append(Settings.Current.PanelEnabled).Append(Settings.Current.TurnEnabled).Append('|');
        if (s.Choice is { } c)
        {
            sb.Append(c.Id).Append(c.Kind);
            foreach (var o in c.Options) sb.Append(',').Append(o.EntityId).Append(o.CardId);
        }
        sb.Append('|').Append(OverlayNotice);
        return sb.ToString();
    }

    private OverlayModel? BuildModel(GameSnapshot snap)
    {
        if (_preview.SelectedIndex > 0)
            return OverlayForm.Sample(_preview.SelectedIndex == 1 ? ChoiceKind.Hero : ChoiceKind.LesserTrinket);
        if (OverlayNotice.Length > 0)
            return new OverlayModel(null, [], "", Banner: new WinBanner(true,
                "Перезапустите Hearthstone", "Оверлею нужен Power.log текущего запуска игры",
                null, null, null));
        // Out of a game the screen is free: show how the last ones went.
        if (!snap.InBattlegrounds)
            return BuildHistory() is { } past ? new OverlayModel(null, [], "", null, null, past) : null;

        // The turn, for the corner plate: the game shows it nowhere itself.
        string turn = snap.Turn > 0 ? $"Ход {snap.Turn}" : "";
        var peekNow = BuildPeek(snap);
        // The hovered board takes the combat banner's place until the pointer leaves.
        WinBanner? banner = peekNow is null ? BuildBanner(snap) : null;
        // The hero pick has its own badges and no hero to describe yet.
        var panel = snap.Choice?.Kind == ChoiceKind.Hero ? null : BuildPanel(snap);
        var buffs = snap.InCombat ? null : snap.Buffs;
        if (snap.Choice is null) return new OverlayModel(null, [], "", panel, banner, null, peekNow, turn, buffs);

        var items = Rating.Rate(snap.Choice, snap, _stats);
        string header;
        if (snap.Choice.Kind == ChoiceKind.Hero)
        {
            header = snap.TribesConfident && !snap.IsDuo
                ? "С учётом рас: " + string.Join(", ", snap.Tribes.Select(Tribes.Name))
                : "Расы лобби ещё не видны — общий рейтинг героя";
        }
        else
        {
            header = (snap.Choice.Kind == ChoiceKind.LesserTrinket ? "Малый" : "Большой") + " аксессуар";
        }
        header += $" · {MmrCaption()}";
        return new OverlayModel(snap.Choice.Kind, items, header, panel, banner, null, peekNow, turn, buffs);
    }

    private static WinBanner? BuildBanner(GameSnapshot snap)
    {
        if (snap.Choice?.Kind == ChoiceKind.Hero) return null;
        if (!snap.InCombat) return null;
        if (snap.CombatWaiting || snap.Combat is null)
            return new WinBanner(true, "Бой начинается…", "ждём выход карт", null, null, null);

        // In duos the figure that counts the whole fight waits for the
        // second enemy board to step out: before that the fight's shape is
        // not all there. In solo the pairing is the fight, shown at once.
        if (snap.IsDuo && !snap.CombatEnemyTeamOut)
            return new WinBanner(true, "Бой идёт…", "ждём вторую доску соперника", null, null, null);

        var g = (snap.CombatOverall ?? snap.Combat).Guess();
        if (g.Waiting) return new WinBanner(true, "Считаю бой…", g.Status, null, null, null);
        // "≈" until the full simulator answers; bare figure once it has.
        string sub = $"{g.MyStats} против {g.TheirStats}" + (g.Exact ? "" : "  ·  ≈");
        return new WinBanner(false, $"{g.Win!.Value * 100:0}%", sub, g.Win, g.Tie, g.Lose);
    }

    private IReadOnlyList<PanelLine> BuildPanel(GameSnapshot snap)
    {
        var lines = new List<PanelLine>();
        HeroSummary? s = Rating.Summarize(snap, _stats);
        if (s is not null)
        {
            var h = s.Hero;
            lines.Add(PanelLine.Title(h.Option.Name, h.Tier, OverlayForm.TierColor(h.Tier),
                snap.MyHeroCardId ?? ""));
        }
        else if (snap.MyHeroCardId is { Length: > 0 } heroCard)
        {
            lines.Add(PanelLine.Title(snap.MyHeroName ?? heroCard, "?", OverlayForm.TierColor("?"), heroCard));
        }

        AddTribes(lines, snap);
        return lines;
    }

    private static void AddTribes(List<PanelLine> lines, GameSnapshot snap)
    {
        lines.Add(PanelLine.Section("Расы в лобби"));
        lines.Add(PanelLine.Note(snap.Tribes.Count > 0
            ? string.Join(", ", snap.Tribes.Select(Tribes.Name)) + (snap.TribesConfident ? "" : " (пока не все)")
            : "Пока не определены", Muted));

        lines.Add(PanelLine.Section("Забанены"));
        var banned = snap.TribesConfident ? Tribes.All.Except(snap.Tribes).Select(Tribes.Name).ToList() : [];
        lines.Add(PanelLine.Note(snap.TribesConfident
            ? string.Join(", ", banned)
            : "Пока не определены", Muted));
    }

    // A session's worth of games; the strip is narrow enough to carry them.
    private const int HistoryRows = 12;

    /// <summary>
    /// The board behind the leaderboard portrait under the pointer. In duos a
    /// place belongs to a team, so both of its boards come up at once.
    /// </summary>
    private BoardPeek? BuildPeek(GameSnapshot snap)
    {
        if (_peekSlot < 0 || snap.Lobby is not { Count: > 0 } lobby) return null;
        int place = _peekSlot + 1;

        var boards = lobby
            .Where(p => p.Place == place)
            .Select(p => new PeekBoard(
                p.HeroCard,
                !p.Alive ? Color.FromArgb(132, 138, 155)
                    : p.Friendly ? OverlayForm.Good : Color.White,
                p.Board is { } board
                    ? board.Board.Select(m => new BoardCard(m.CardId, m.Name, $"{m.Attack}/{m.Health}",
                        Marks(m), MarkInk(m))).ToList()
                    : Array.Empty<BoardCard>(),
                p.Tier,
                p.Tavern ?? Array.Empty<TavernTier>()))
            .ToList();
        return boards.Count == 0 ? null : new BoardPeek(boards);
    }

    private static string Marks(SeenMinion m)
    {
        var marks = new List<string>(3);
        if (m.Taunt) marks.Add("провок.");
        if (m.DivineShield) marks.Add("щит");
        if (m.Poisonous) marks.Add("яд");
        if (m.Reborn) marks.Add("возр.");
        if (m.Windfury) marks.Add("вихрь");
        return string.Join(" · ", marks);
    }

    /// <summary>The same colours, for a board kept as the words themselves.</summary>
    private static Color MarkInk(string marks) =>
        marks.Contains("яд") ? Color.FromArgb(140, 230, 140) :
        marks.Contains("щит") ? Color.FromArgb(255, 214, 120) :
                                               Color.FromArgb(178, 184, 200);

    private static Color MarkInk(SeenMinion m) =>
        m.Poisonous ? Color.FromArgb(140, 230, 140) :
        m.DivineShield ? Color.FromArgb(255, 214, 120) :
                         Color.FromArgb(178, 184, 200);

    /// <summary>
    /// The client's log folder for this launch of the game. The list is kept
    /// across launches, but only this one's games belong on screen.
    /// </summary>
    private string CurrentSession()
    {
        string? path = _reader.CurrentPath;
        return path is null ? "" : Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
    }

    /// <summary>The launch a kept game belongs to: its key is "session#number".</summary>
    private static string SessionOf(GameResult game)
    {
        int at = game.Key.LastIndexOf('#');
        return at > 0 ? game.Key.Substring(0, at) : game.Key;
    }

    /// <summary>The games of this launch of the client, newest first.</summary>
    private List<GameResult> SessionGames()
    {
        // Out of game there is no log to ask, so the newest launch stands in.
        string session = CurrentSession();
        if (session.Length == 0 && _history.Games.Count > 0) session = SessionOf(_history.Games[0]);
        return _history.Games.Where(g => SessionOf(g) == session).ToList();
    }

    private HistoryPanel? BuildHistory()
    {
        var played = SessionGames();
        var games = played.Take(HistoryRows).ToList();
        if (games.Count == 0) return null;

        var rows = games.Select(g => new HistoryRow(
            g.Place.ToString(),
            OverlayForm.PlaceColor(g.Place, g.IsDuo),
            g.HeroCardId,
            g.HeroName,
            (g.IsDuo ? "дуэты" : "соло")
                + (g.Tribes.Count > 0 ? " · " + string.Join(", ", g.Tribes.Select(Tribes.Name)) : ""),
            g.Finished.ToString("HH:mm"),
            Delta(g),
            g.Rating >= g.RatingBefore ? OverlayForm.Good : OverlayForm.Bad,
            $"{g.Place} место · ход {g.Turns} · {g.BoardStats} ат.+зд.",
            g.Board.Select(m => new BoardCard(m.CardId, m.Name, $"{m.Attack}/{m.Health}", m.Marks, MarkInk(m.Marks)))
                .ToList())).ToList();

        bool duo = games[0].IsDuo;
        double avg = played.Average(g => (double)g.Place);
        // The rating heads the strip; the day's swing closes it.
        string title = _rating.Of(duo) > 0 ? $"Рейтинг {_rating.Of(duo)}" : "Прошлые игры";
        return new HistoryPanel(title, $"ср. место {avg:0.0}", rows, _hoverRow, TodaysRating(duo));
    }

    /// <summary>
    /// Where the rating stood before the first game of today and where it
    /// stands now, when both ends of the day are known.
    /// </summary>
    private RatingToday? TodaysRating(bool duo)
    {
        var today = _history.Games
            .Where(g => g.IsDuo == duo && g.Finished.Date == DateTime.Today)
            .OrderBy(g => g.Finished)
            .ToList();
        if (today.Count == 0) return null;
        int before = today.Select(g => g.RatingBefore).FirstOrDefault(v => v > 0);
        int now = _rating.Of(duo) > 0 ? _rating.Of(duo) : today.Select(g => g.Rating).LastOrDefault(v => v > 0);
        return before > 0 && now > 0 ? new RatingToday(before, now, today.Count) : null;
    }

    /// <summary>How the rating moved over a game, when both ends are known.</summary>
    private static string Delta(GameResult g)
    {
        if (g.Rating <= 0 || g.RatingBefore <= 0 || g.Rating == g.RatingBefore) return "";
        int d = g.Rating - g.RatingBefore;
        return d > 0 ? $"+{d}" : d.ToString();
    }

    private string MmrCaption() => _stats.Percentile == 100 ? "все игроки" : $"топ-{_stats.Percentile}%";

    private static string Describe(OverlayModel? m)
    {
        if (m is null) return "Жду игру в Полях сражений.";
        var sb = new StringBuilder();
        if (m.Banner is { } b)
        {
            sb.AppendLine(b.Waiting ? b.Title : $"Шанс боя: {b.Title}");
            if (!b.Waiting && b.Tie is double tie && b.Lose is double lose)
                sb.AppendLine($"Ничья {tie * 100:0}% · поражение {lose * 100:0}%");
            if (b.Sub.Length > 0) sb.AppendLine(b.Sub);
            if (b.Team.Length > 0) sb.AppendLine(b.Team);
            sb.AppendLine();
        }
        if (m.History is { Rows.Count: > 0 } past)
        {
            sb.AppendLine($"{past.Title} — {past.Note}");
            if (past.Today is { } today)
            {
                int moved = today.Now - today.Before;
                sb.AppendLine($"Сегодня: {today.Before} → {today.Now} "
                    + $"({(moved > 0 ? "+" : "")}{moved} за {today.Games} игр)");
            }
            foreach (var r in past.Rows)
                sb.AppendLine($"  {r.Place} место  {r.Delta,5}  {r.Hero}  ({r.When})  {r.Tribes}");
            sb.AppendLine();
            sb.Append("Наведите курсор на игру в оверлее — покажет её доску.");
            return sb.ToString();
        }
        if (m.Panel is { Count: > 0 })
        {
            foreach (var l in m.Panel.Where(l => l.Style != PanelStyle.Rule)) sb.AppendLine(l.Flat);
            sb.AppendLine();
        }
        if (m.Kind is null)
        {
            sb.Append("Ранги появятся при выборе героя и аксессуаров.");
            return sb.ToString();
        }
        sb.AppendLine(m.Header);
        sb.AppendLine();
        foreach (var o in m.Items)
        {
            string place = o.AvgPlace is double p ? $"{p:0.00}" : " -- ";
            sb.Append($"{o.Tier}  {place}  {o.Option.Name}").AppendLine();
            sb.Append($"         {o.Detail}");
            if (o.TribeHint is not null) sb.Append($" · {o.TribeHint}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private string LogNotice(IntPtr hs)
    {
        if (hs == IntPtr.Zero) return "";
        string? path = _reader.CurrentPath;
        return path is null || !LogLocator.IsCurrentSession(path, _hsProcess)
            ? "Оверлей: перезапустите Hearthstone, чтобы включить Power.log."
            : "";
    }

    private void UpdateStatus(GameSnapshot snap)
    {
        string? path = _reader.CurrentPath;
        if (OverlayNotice.Length > 0)
            _game.Text = "Нет Power.log текущего запуска.\n"
                + (_configNote.Length > 0 ? _configNote : "Перезапустите Hearthstone.");
        else if (path is null)
            _game.Text = "Не нашёл Power.log. Запустите Hearthstone." + (_configNote.Length > 0 ? "\n" + _configNote : "");
        else if (_reader.CatchingUp)
            _game.Text = "Читаю лог текущего запуска игры…";
        else if (!snap.InBattlegrounds)
            _game.Text = "Жду игру в Полях сражений." + (_configNote.Length > 0 ? "\n" + _configNote : "");
        else
        {
            string mode = snap.IsDuo ? "Поля сражений, дуэты" : "Поля сражений";
            string phase = snap.Choice?.Kind switch
            {
                ChoiceKind.Hero => " · выбор героя",
                ChoiceKind.LesserTrinket => " · выбор малого аксессуара",
                ChoiceKind.GreaterTrinket => " · выбор большого аксессуара",
                _ => "",
            };
            _game.Text = mode + phase;
        }

        if (!snap.InBattlegrounds)
            _tribes.Text = "";
        else if (snap.TribeCounts.Count == 0)
            _tribes.Text = "Расы лобби: определятся по существам в таверне.";
        else
            _tribes.Text = "Расы лобби" + (snap.TribesConfident ? ": " : " (предварительно): ")
                + string.Join(", ", snap.Tribes.Select(Tribes.Name));

        if (_stats.Loading)
            _statsLabel.Text = "Загружаю статистику…";
        else if (_stats.Solo is null)
            _statsLabel.Text = "Статистики нет" + (_stats.Error is null ? "" : ": " + _stats.Error);
        else
            _statsLabel.Text = $"Статистика за текущий патч: {_stats.Solo.HeroStats.Count} героев, "
                + $"{_stats.Trinkets?.TrinketStats.Count ?? 0} аксессуаров, {_stats.Solo.DataPoints:N0} игр. "
                + $"Скачана {_stats.FetchedAt:dd.MM HH:mm}"
                + (_stats.Error is null ? "" : $" (обновить не удалось: {_stats.Error})");
    }

    private IntPtr FindGameWindow()
    {
        if (Environment.TickCount64 < _nextWindowLookup) return _hsWindow;
        _nextWindowLookup = Environment.TickCount64 + 1000;
        _hsWindow = IntPtr.Zero;
        _hsProcess = 0;
        foreach (Process p in Process.GetProcessesByName("Hearthstone"))
        {
            using (p)
                if (_hsWindow == IntPtr.Zero)
                {
                    _hsWindow = p.MainWindowHandle;
                    _hsProcess = p.Id;
                }
        }
        return _hsWindow;
    }

    private static Rectangle ClientRect(IntPtr hwnd)
    {
        if (!Native.GetClientRect(hwnd, out var rc)) return Rectangle.Empty;
        var origin = new Native.POINT();
        Native.ClientToScreen(hwnd, ref origin);
        return new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Stop();
            _refresh.Stop();
            _rating.Dispose();
            _lobbyNames.Dispose();
            _art.Dispose();
            _reader.Dispose();
            _overlay.Dispose();
            _speedStrip.Dispose();
        }
        base.Dispose(disposing);
    }
}
