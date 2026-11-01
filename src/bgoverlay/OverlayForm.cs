using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace BgOverlay;

/// <summary>The session recap shown beside the game, in Deck Tracker's layout.</summary>
public sealed record LobbyPanel(IReadOnlyList<int> Available, IReadOnlyList<int> Banned,
    int RatingStart, int RatingCurrent, IReadOnlyList<HistoryRow> Games);

/// <summary>One finished game in the list the overlay shows between games.</summary>
public sealed record HistoryRow(string Place, Color PlaceInk, string HeroCard, string Hero, string Tribes,
    string When, string Delta, Color DeltaInk, string BoardNote, IReadOnlyList<BoardCard> Board);

/// <summary>Where the rating stood before today's first game, and where it stands now.</summary>
public sealed record RatingToday(int Before, int Now, int Games);

public sealed record HistoryPanel(string Title, string Note, IReadOnlyList<HistoryRow> Rows, int Hover,
    RatingToday? Today = null);

/// <summary>One minion on a board the overlay draws from its card art.</summary>
public sealed record BoardCard(string CardId, string Name, string Stats, string Marks, Color MarkInk);

/// <summary>One board under the pointer, laid out the way the game lays it.</summary>
public sealed record PeekBoard(string HeroCard, Color Ink,
    IReadOnlyList<BoardCard> Board, int Tier, IReadOnlyList<TavernTier> Tavern);

/// <summary>The boards and tavern progress to show over the play area.</summary>
public sealed record BoardPeek(IReadOnlyList<PeekBoard> Boards);

public sealed record WinBanner(bool Waiting, string Title, string Sub, double? Win, double? Tie, double? Lose,
    string Team = "", double? TeamWin = null);

/// <summary>Badges over a pending choice, a top winrate banner, and/or the side panel.</summary>
public sealed record OverlayModel(
    ChoiceKind? Kind,
    IReadOnlyList<RatedOption> Items,
    string Header,
    LobbyPanel? Panel = null,
    WinBanner? Banner = null,
    HistoryPanel? History = null,
    BoardPeek? Peek = null,
    string Turn = "",
    IReadOnlyList<BattlegroundsBuff>? Buffs = null);

/// <summary>
/// Borderless, click-through window laid exactly over the game's client area.
/// Positions are fractions of the client height: Hearthstone scales its UI
/// with the height and centres a fixed-aspect play area horizontally.
///
/// It is a layered window drawn through UpdateLayeredWindow, so every pixel
/// carries its own transparency: nothing needs a backing panel to hide a
/// colour key, and rounded art keeps its soft edges over the game.
/// </summary>
internal sealed class OverlayForm : Form
{
    private OverlayModel? _model;

    /// <summary>Card art, handed in by the panel that owns the overlay.</summary>
    public CardArt? Art { get; set; }

    private static readonly Color Ink      = Color.FromArgb(236, 238, 245);
    private static readonly Color Muted    = Color.FromArgb(152, 158, 175);
    private static readonly Color Dim      = Color.FromArgb(104, 110, 128);
    private static readonly Color Back     = Color.FromArgb(236, 12, 13, 19);
    private static readonly Color Band     = Color.FromArgb(240, 23, 25, 35);
    private static readonly Color Edge     = Color.FromArgb(46, 49, 62);
    /// <summary>The outline of a panel: brighter than a rule inside it, so the
    /// card has an edge of its own over whatever the game draws under it.</summary>
    private static readonly Color Frame    = Color.FromArgb(74, 80, 100);
    /// <summary>The grey plate the turn counter sits on.</summary>
    private static readonly Color Plate    = Color.FromArgb(230, 66, 69, 82);
    public  static readonly Color Good     = Color.FromArgb(120, 230, 140);
    public  static readonly Color Bad      = Color.FromArgb(240, 120, 100);

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        SetStyle(ControlStyles.Opaque, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                        | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void Present(Rectangle gameClient, OverlayModel? model)
    {
        if (model is null || gameClient.Width < 100 || gameClient.Height < 100)
        {
            if (Visible) Hide();
            _model = null;
            return;
        }
        bool moved = Bounds != gameClient;
        if (moved) Bounds = gameClient;
        if (moved || !Equals(model, _model))
        {
            _model = model;
            Render();
        }
        if (!Visible) Show();
    }

    /// <summary>Draws the whole overlay and hands it to the window as it is.</summary>
    private void Render()
    {
        if (_model is null || Width <= 0 || Height <= 0) return;
        using var picture = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(picture))
        {
            g.Clear(Color.Transparent);
            Draw(g, ClientSize, _model, Art);
        }
        Native.ShowLayered(Handle, picture);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) { }

    /// <summary>
    /// Everything the overlay shows, on any surface the size of the game's
    /// client area. Shared with the layout preview so both look alike.
    /// </summary>
    internal static void Draw(Graphics g, Size size, OverlayModel model) => Draw(g, size, model, null);

    internal static void Draw(Graphics g, Size size, OverlayModel model, CardArt? art)
    {
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float h = size.Height, w = size.Width;
        if (model.History is { Rows.Count: > 0 } history) DrawHistory(g, history, art, size, h);
        if (model.Peek is { Boards.Count: > 0 } peek) DrawPeek(g, peek, art, size, h);
        if (model.Banner is { } banner) DrawBanner(g, banner, w, h);
        if (model.Panel is { } panel && Settings.Current.PanelEnabled) DrawPanel(g, panel, art, size, h);
        if (model.Turn.Length > 0 && Settings.Current.TurnEnabled) DrawTurn(g, model.Turn, size, h);
        if (model.Buffs is { Count: > 0 } buffs) DrawBuffs(g, buffs, art, size, h);
        if (model.Kind is null) return;

        int n = model.Items.Count;
        double rowY = model.Kind == ChoiceKind.Hero ? Settings.Current.HeroBadgeY : Settings.Current.TrinketBadgeY;
        float top = (float)(rowY * h);

        using var headerFont = new Font("Segoe UI", 0.0125f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        DrawChip(g, model.Header, headerFont, Color.FromArgb(236, 14, 15, 22), Color.Gainsboro,
            w / 2, top - 0.024f * h, h);

        for (int i = 0; i < n; i++)
        {
            float cx = model.Kind == ChoiceKind.Hero
                ? w / 2 + 0.02f * h + (i - (n - 1) / 2f) * 0.315f * h
                : (w - 1.40f * h) / 2 + 0.20f * h + (i + 0.5f) * (1.01f * h / n);
            DrawBadge(g, model.Items[i], cx, top, h);
        }
    }

    /// <summary>
    /// The turn, in the top right corner of the game's own interface box,
    /// on a grey plate of its own. The game centres that box on a wider
    /// window, so the corner moves with it.
    /// </summary>
    private static void DrawTurn(Graphics g, string text, Size size, float h)
    {
        float playWidth = Math.Min(size.Width, h * 16f / 9f);
        using var font = new Font("Segoe UI", 0.0200f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var tight = new StringFormat(StringFormat.GenericTypographic);
        SizeF sz = g.MeasureString(text, font, int.MaxValue, tight);
        float padX = 0.011f * h, padY = 0.0055f * h;
        var box = new RectangleF(
            PlayLeft(size) + playWidth - 0.014f * h - (sz.Width + 2 * padX),
            (float)(Settings.Current.TurnY * h), sz.Width + 2 * padX, sz.Height + 2 * padY);

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath plate = Pill(box, 0.007f * h))
        {
            using var fill = new SolidBrush(Plate);
            using var rim = new Pen(Color.FromArgb(150, 168, 176, 196), Math.Max(1f, 0.0013f * h));
            g.FillPath(fill, plate);
            g.DrawPath(rim, plate);
        }
        g.SmoothingMode = was;

        using var ink = new SolidBrush(Ink);
        g.DrawString(text, font, ink, box.X + padX, box.Y + padY, tight);
    }

    /// <summary>Centered over the top of the play area, above the opponent.</summary>
    private static void DrawBanner(Graphics g, WinBanner b, float w, float h)
    {
        float bw = 0.40f * h, bh = (b.Waiting ? 0.086f : 0.065f) * h;
        var box = new RectangleF((w - bw) / 2, (float)(Settings.Current.BannerY * h), bw, bh);
        using var bg = new SolidBrush(Back);
        using var edge = new Pen(Edge, Math.Max(1f, 0.0012f * h));
        g.FillRectangle(bg, box);
        if (b.Waiting)
        {
            using var bar = new SolidBrush(Color.FromArgb(78, 84, 102));
            g.FillRectangle(bar, box.X, box.Y, box.Width, 0.005f * h);
        }
        else
        {
            Color[] accents = { Color.FromArgb(76, 217, 100), Color.FromArgb(240, 176, 64), Color.FromArgb(230, 80, 70) };
            float stripeW = box.Width / accents.Length;
            for (int i = 0; i < accents.Length; i++)
            {
                using var bar = new SolidBrush(accents[i]);
                g.FillRectangle(bar, box.X + i * stripeW, box.Y,
                    i == accents.Length - 1 ? box.Width - i * stripeW : stripeW, 0.005f * h);
            }
        }
        g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);

        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        if (b.Waiting)
        {
            using var waitFont = new Font("Segoe UI", 0.021f * h, FontStyle.Bold, GraphicsUnit.Pixel);
            using var subFont = new Font("Segoe UI", 0.0135f * h, FontStyle.Regular, GraphicsUnit.Pixel);
            using var waitInk = new SolidBrush(Muted);
            using var subInk = new SolidBrush(Dim);
            g.DrawString(b.Title, waitFont, waitInk,
                new RectangleF(box.X, box.Y + 0.016f * h, box.Width, 0.030f * h), center);
            g.DrawString(b.Sub, subFont, subInk,
                new RectangleF(box.X, box.Y + 0.048f * h, box.Width, 0.024f * h), center);
            return;
        }

        using var percentFont = new Font("Segoe UI", 0.030f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI", 0.0135f * h, FontStyle.Bold, GraphicsUnit.Pixel);

        string tie = b.Tie is double t ? $"{t * 100:0}%" : "—";
        string lose = b.Lose is double l ? $"{l * 100:0}%" : "—";
        (string Value, string Label, Color Ink)[] parts =
        {
            (b.Title, "Победа", Color.FromArgb(76, 217, 100)),
            (tie, "Ничья", Color.FromArgb(240, 176, 64)),
            (lose, "Поражение", Color.FromArgb(230, 80, 70)),
        };
        float partW = box.Width / parts.Length;
        for (int i = 0; i < parts.Length; i++)
        {
            float x = box.X + i * partW;
            using var ink = new SolidBrush(parts[i].Ink);
            g.DrawString(parts[i].Value, percentFont, ink,
                new RectangleF(x, box.Y + 0.007f * h, partW, 0.034f * h), center);
            g.DrawString(parts[i].Label, labelFont, ink,
                new RectangleF(x, box.Y + 0.040f * h, partW, 0.018f * h), center);
            if (i > 0)
                using (var separator = new Pen(Edge, Math.Max(1f, 0.0011f * h)))
                    g.DrawLine(separator, x, box.Y + 0.012f * h, x, box.Y + 0.056f * h);
        }
    }

    /// <summary>
    /// Where the side panel stands: against the left edge of the game's own
    /// interface box, and never over the leaderboard it sits beside — the
    /// strip of portraits starts at <see cref="Settings.PeekX"/>.
    /// </summary>
    internal static (float X, float W) PanelBox(Size size, float h)
    {
        float x = PlayLeft(size);
        float stop = PlayLeft(size) + (float)Settings.Current.PeekX * h - 0.014f * h;
        return (x, Math.Clamp(stop - x, 0.150f * h, 0.224f * h));
    }

    /// <summary>The session recap in Hearthstone Deck Tracker's three-section layout.</summary>
    private static void DrawPanel(Graphics g, LobbyPanel panel, CardArt? art, Size size, float h)
    {
        var (x, w) = PanelBox(size, h);
        float y = (float)(Settings.Current.PanelY * h);
        float sectionH = 0.026f * h, tribeH = 0.058f * h, emptyH = 0.060f * h;
        float mmrH = 0.052f * h, gameH = 0.032f * h, gameHeadH = 0.021f * h;
        float gamesH = panel.Games.Count > 0 ? gameHeadH + panel.Games.Count * gameH : emptyH;
        float total = sectionH + (panel.Available.Count > 0 ? tribeH : emptyH)
                    + (panel.Banned.Count > 0 ? tribeH : 0) + sectionH + mmrH + sectionH + gamesH;
        var body = new RectangleF(x, y, w, total);
        Color bodyInk = Color.FromArgb(222, 46, 50, 53);
        Color headInk = Color.FromArgb(230, 28, 32, 34);
        Color ruleInk = Color.FromArgb(74, 82, 86);

        using var headFont = new Font("Segoe UI", 0.0115f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", 0.0090f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var numberFont = new Font("Segoe UI", 0.0160f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var gameFont = new Font("Segoe UI", 0.0120f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var gameBold = new Font("Segoe UI", 0.0135f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var center = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
              FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        using var left = new StringFormat(StringFormat.GenericTypographic)
            { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap,
              Trimming = StringTrimming.EllipsisCharacter };

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(bodyInk)) g.FillRectangle(bg, body);

        float cy = y;
        void Header(string text)
        {
            using var bg = new SolidBrush(headInk);
            using var ink = new SolidBrush(Ink);
            g.FillRectangle(bg, x, cy, w, sectionH);
            g.DrawString(text, headFont, ink, new RectangleF(x, cy, w, sectionH), center);
            using var rule = new SolidBrush(ruleInk);
            g.FillRectangle(rule, x, cy + sectionH - 1, w, 1);
            cy += sectionH;
        }
        void Rule()
        {
            using var rule = new SolidBrush(ruleInk);
            g.FillRectangle(rule, x, cy, w, 1);
        }

        Header("Представлены");
        if (panel.Available.Count > 0)
        {
            DrawTribeRow(g, panel.Available, art, x, cy, w, h, banned: false);
            cy += tribeH;
        }
        else
        {
            using var ink = new SolidBrush(Muted);
            g.DrawString("Расы появятся в следующей игре", smallFont, ink,
                new RectangleF(x + 0.005f * h, cy, w - 0.010f * h, emptyH), center);
            cy += emptyH;
        }
        if (panel.Banned.Count > 0)
        {
            Rule();
            DrawTribeRow(g, panel.Banned, art, x, cy, w, h, banned: true);
            cy += tribeH;
        }

        Header("MMR");
        float half = w / 2;
        using (var labelInk = new SolidBrush(Muted))
        using (var numberInk = new SolidBrush(Ink))
        {
            g.DrawString("Начальный", smallFont, labelInk, new RectangleF(x, cy + 0.004f * h, half, 0.016f * h), center);
            g.DrawString("Текущий", smallFont, labelInk, new RectangleF(x + half, cy + 0.004f * h, half, 0.016f * h), center);
            g.DrawString(Mmr(panel.RatingStart), numberFont, numberInk,
                new RectangleF(x, cy + 0.020f * h, half, 0.028f * h), center);
            g.DrawString(Mmr(panel.RatingCurrent), numberFont, numberInk,
                new RectangleF(x + half, cy + 0.020f * h, half, 0.028f * h), center);
        }
        using (var divider = new SolidBrush(ruleInk)) g.FillRectangle(divider, x + half, cy, 1, mmrH);
        cy += mmrH;

        Header("Последние игры");
        if (panel.Games.Count == 0)
        {
            using var ink = new SolidBrush(Muted);
            g.DrawString("Ни одной игры не сыграно в этой сессии.\nПоследние 8 партий появятся здесь.",
                smallFont, ink, new RectangleF(x + 0.005f * h, cy, w - 0.010f * h, emptyH), center);
            cy += emptyH;
        }
        else
        {
            using var labels = new SolidBrush(Muted);
            g.DrawString("Герой", smallFont, labels, new RectangleF(x + 0.007f * h, cy, w * 0.48f, gameHeadH), left);
            g.DrawString("Место", smallFont, labels, new RectangleF(x + w * 0.52f, cy, w * 0.22f, gameHeadH), center);
            g.DrawString("MMR", smallFont, labels, new RectangleF(x + w * 0.74f, cy, w * 0.24f, gameHeadH), center);
            cy += gameHeadH;

            foreach (HistoryRow game in panel.Games)
            {
                using (var rowBg = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                    g.FillRectangle(rowBg, x, cy, w, gameH);
                Image? tile = art?.Tile(game.HeroCard);
                if (tile is not null)
                {
                    var row = new Rectangle((int)x, (int)cy, (int)w, (int)gameH);
                    float srcX = tile.Width * (34f / 256f);
                    float srcW = tile.Width - srcX;
                    float srcH = Math.Min(tile.Height, srcW * gameH / w);
                    float srcY = (tile.Height - srcH) / 2;
                    using var attributes = new ImageAttributes();
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.45f });
                    g.DrawImage(tile, row, srcX, srcY, srcW, srcH, GraphicsUnit.Pixel, attributes);
                    using var shade = new LinearGradientBrush(row,
                        Color.FromArgb(35, 0, 0, 0), Color.FromArgb(115, 0, 0, 0), 0f);
                    g.FillRectangle(shade, row);
                }
                using var heroInk = new SolidBrush(Ink);
                using var placeInk = new SolidBrush(game.PlaceInk);
                using var deltaInk = new SolidBrush(game.DeltaInk);
                g.DrawString(game.Hero, gameFont, heroInk,
                    new RectangleF(x + 0.007f * h, cy, w * 0.48f, gameH), left);
                g.DrawString(game.Place, gameBold, placeInk,
                    new RectangleF(x + w * 0.52f, cy, w * 0.22f, gameH), center);
                g.DrawString(game.Delta, gameBold, deltaInk,
                    new RectangleF(x + w * 0.74f, cy, w * 0.24f, gameH), center);
                cy += gameH;
            }
        }

        using (var edge = new Pen(ruleInk, Math.Max(1f, 0.0010f * h))) g.DrawRectangle(edge, body);
        g.SmoothingMode = was;
    }

    private static string Mmr(int value) => value > 0 ? value.ToString("N0") : "—";

    /// <summary>One row of circular tribe icons, greyed and crossed when banned.</summary>
    private static void DrawTribeRow(Graphics g, IReadOnlyList<int> tribes, CardArt? art,
        float x, float y, float w, float h, bool banned)
    {
        float cell = w / tribes.Count, icon = 0.035f * h;
        using var font = new Font("Segoe UI", 0.0080f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var center = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
              FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        for (int i = 0; i < tribes.Count; i++)
        {
            int tribe = tribes[i];
            float cx = x + i * cell + (cell - icon) / 2;
            var box = new RectangleF(cx, y + 0.006f * h, icon, icon);
            Image? picture = art?.Tribe(tribe);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(box);
                Region old = g.Clip;
                g.SetClip(path, CombineMode.Replace);
                if (picture is not null)
                {
                    if (banned)
                    {
                        using var attributes = new ImageAttributes();
                        attributes.SetColorMatrix(new ColorMatrix(new[]
                        {
                            new[] { 0.30f, 0.59f, 0.11f, 0, 0 },
                            new[] { 0.30f, 0.59f, 0.11f, 0, 0 },
                            new[] { 0.30f, 0.59f, 0.11f, 0, 0 },
                            new[] { 0f, 0f, 0f, 0.55f, 0 },
                            new[] { 0f, 0f, 0f, 0f, 1f },
                        }));
                        g.DrawImage(picture, new Rectangle((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height),
                            0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, attributes);
                    }
                    else g.DrawImage(picture, box, new RectangleF(0, 0, picture.Width, picture.Height),
                        GraphicsUnit.Pixel);
                }
                else
                {
                    using var empty = new SolidBrush(Color.FromArgb(45, 49, 55));
                    g.FillEllipse(empty, box);
                }
                g.Clip = old;
            }
            using (var rim = new Pen(banned ? Dim : Color.FromArgb(190, 198, 205), Math.Max(1f, 0.0016f * h)))
                g.DrawEllipse(rim, box);
            if (banned)
            {
                using var cross = new Pen(Color.FromArgb(220, 75, 75), Math.Max(1.5f, 0.0022f * h));
                g.DrawLine(cross, box.X + icon * 0.24f, box.Y + icon * 0.24f,
                    box.Right - icon * 0.24f, box.Bottom - icon * 0.24f);
                g.DrawLine(cross, box.Right - icon * 0.24f, box.Y + icon * 0.24f,
                    box.X + icon * 0.24f, box.Bottom - icon * 0.24f);
            }
            using var ink = new SolidBrush(banned ? Dim : Ink);
            g.DrawString(Tribes.ShortName(tribe), font, ink,
                new RectangleF(x + i * cell, y + 0.043f * h, cell, 0.014f * h), center);
        }
    }

    /// <summary>
    /// The strip of the game's own leaderboard, portrait by portrait: the
    /// overlay never draws there, it only notices the pointer.
    /// </summary>
    internal static int PeekHit(Point at, int slots, Size size)
    {
        if (slots <= 0 || !Settings.Current.PeekEnabled) return -1;
        float h = size.Height;
        var cur = Settings.Current;
        float x = PlayLeft(size) + (float)(cur.PeekX * h), w = (float)(cur.PeekWidth * h);
        float top = (float)(cur.PeekTop * h), step = Step(slots, h);
        if (at.X < x || at.X > x + w || at.Y < top) return -1;
        int i = (int)((at.Y - top) / step);
        // The gap between portraits belongs to neither of them.
        float within = at.Y - top - i * step;
        if (within > step * 0.92f) return -1;
        return i < slots ? i : -1;
    }

    /// <summary>
    /// The game lays its interface out in a 16:9 box; on a wider window that
    /// box is centred and everything measured from the left shifts with it.
    /// </summary>
    private static float PlayLeft(Size size) =>
        Math.Max(0f, (size.Width - size.Height * 16f / 9f) / 2f);

    /// <summary>Player-wide bonuses, above the coins in the bottom right.</summary>
    private static void DrawBuffs(Graphics g, IReadOnlyList<BattlegroundsBuff> buffs, CardArt? art,
        Size size, float h)
    {
        using var font = new Font("Segoe UI", 0.0155f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var tight = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        using var middle = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };

        float icon = 0.026f * h, boxH = 0.034f * h, pad = 0.006f * h, gap = 0.005f * h;
        var widths = buffs.Select(b => 2 * pad + icon + gap + g.MeasureString(b.Value, font, int.MaxValue, tight).Width)
            .ToList();
        int rows = (buffs.Count + 1) / 2;
        var columns = new float[Math.Min(2, buffs.Count)];
        for (int i = 0; i < buffs.Count; i++) columns[i % 2] = Math.Max(columns[i % 2], widths[i]);
        float total = columns.Sum() + (columns.Length - 1) * gap;
        float playWidth = Math.Min(size.Width, h * 16f / 9f);
        float left = PlayLeft(size) + playWidth - 0.025f * h - total - 100f;
        float top = 0.850f * h - rows * boxH - (rows - 1) * gap;

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < buffs.Count; i++)
        {
            BattlegroundsBuff buff = buffs[i];
            int column = i % 2;
            float x = left + (column == 1 ? columns[0] + gap : 0);
            float y = top + i / 2 * (boxH + gap);
            var box = new RectangleF(x, y, columns[column], boxH);
            using (GraphicsPath plate = Pill(box, 0.006f * h))
            {
                using var fill = new SolidBrush(Back);
                using var rim = new Pen(Frame, Math.Max(1f, 0.0012f * h));
                g.FillPath(fill, plate);
                g.DrawPath(rim, plate);
            }

            var face = new RectangleF(x + pad, y + (boxH - icon) / 2, icon, icon);
            DrawBuffIcon(g, buff.CardId, art, face, h);
            using var ink = new SolidBrush(Ink);
            g.DrawString(buff.Value, font, ink,
                new RectangleF(face.Right + gap, y, box.Width - icon - 2 * pad - gap, boxH), middle);
        }
        g.SmoothingMode = was;
    }

    private static void DrawBuffIcon(Graphics g, string cardId, CardArt? art, RectangleF box, float h)
    {
        Image? square = art?.Art(cardId);
        Image? pic = square ?? art?.Tile(cardId);
        using var path = new GraphicsPath();
        path.AddEllipse(box);
        Region old = g.Clip;
        g.SetClip(path, CombineMode.Replace);
        if (pic is not null)
        {
            RectangleF src = square is not null
                ? new RectangleF(pic.Width * 0.148f, pic.Height * 0.078f,
                                 pic.Width * 0.703f, pic.Height * 0.703f)
                : new RectangleF(pic.Width * 0.52f - pic.Height / 2f, 0, pic.Height, pic.Height);
            g.DrawImage(pic, box, src, GraphicsUnit.Pixel);
        }
        else
        {
            using var empty = new SolidBrush(Color.FromArgb(34, 36, 48));
            g.FillRectangle(empty, box);
        }
        g.Clip = old;
        using var rim = new Pen(Color.FromArgb(200, 174, 145, 91), Math.Max(1f, 0.0012f * h));
        g.DrawEllipse(rim, box);
    }

    /// <summary>One portrait's share of the strip.</summary>
    private static float Step(int slots, float h) =>
        (float)(Settings.Current.PeekSpan * h / Math.Max(slots, 1));

    /// <summary>
    /// The hovered player's compact strip: hero, minions in the same row,
    /// then a divider and all six tavern tiers. No player or board text.
    /// </summary>
    private static void DrawPeek(Graphics g, BoardPeek peek, CardArt? art, Size size, float h)
    {
        float pad = 0.006f * h, content = 0.052f * h;
        float portrait = 0.046f * h, oval = 0.044f * h, gap = 0.005f * h;
        float tierCell = 0.041f * h, rowH = 2 * pad + content;
        float y = (float)(Settings.Current.BannerY * h);

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        foreach (PeekBoard board in peek.Boards)
        {
            int count = board.Board.Count;
            float cardsWidth = count * oval + Math.Max(0, count - 1) * gap;
            float width = 2 * pad + portrait + (count > 0 ? gap + cardsWidth : 0)
                        + 0.014f * h + 6 * tierCell;
            width = Math.Min(width, size.Width - 0.020f * h);
            float x = (size.Width - width) / 2;
            using (GraphicsPath frame = Pill(new RectangleF(x, y, width, rowH), 0.005f * h))
            {
                using var fill = new SolidBrush(Back);
                using var rim = new Pen(Frame, Math.Max(1f, 0.0012f * h));
                g.FillPath(fill, frame);
                g.DrawPath(rim, frame);
            }

            float py = y + pad + (content - portrait) / 2;
            DrawFace(g, board.HeroCard, art, new RectangleF(x + pad, py, portrait, portrait), board.Ink, h);

            float bx = x + pad + portrait + (count > 0 ? gap : 0);
            float by = y + pad + (content - oval * 1.12f) / 2;
            foreach (BoardCard card in board.Board)
            {
                DrawMinion(g, card, art, new RectangleF(bx, by, oval, oval * 1.12f), h);
                bx += oval + gap;
            }

            float split = bx + 0.006f * h;
            using (var divider = new Pen(Edge, Math.Max(1f, 0.0012f * h)))
                g.DrawLine(divider, split, y + pad + 0.005f * h,
                    split, y + pad + content - 0.005f * h);

            float tiersX = split + 0.008f * h;
            for (int level = 1; level <= 6; level++)
            {
                TavernTier? reached = board.Tavern.FirstOrDefault(t => t.Level == level);
                bool unlocked = level <= board.Tier || reached is not null;
                DrawTavernTier(g, level, unlocked, reached?.Turn ?? 0,
                    new RectangleF(tiersX + (level - 1) * tierCell, y + pad, tierCell, content), h);
            }
            y += rowH + 0.006f * h;
        }

        g.SmoothingMode = was;
    }

    /// <summary>A tavern shield, grey until reached, with its upgrade turn.</summary>
    private static void DrawTavernTier(Graphics g, int level, bool unlocked, int turn, RectangleF cell, float h)
    {
        float w = 0.030f * h, sh = 0.036f * h;
        float x = cell.X + (cell.Width - w) / 2, y = cell.Y + 0.001f * h;
        PointF[] points =
        {
            new(x, y), new(x + w, y), new(x + w, y + sh * 0.56f),
            new(x + w * 0.50f, y + sh), new(x, y + sh * 0.56f),
        };
        using (var shield = new GraphicsPath())
        {
            shield.AddPolygon(points);
            using var fill = new SolidBrush(unlocked ? Color.FromArgb(112, 79, 39) : Color.FromArgb(39, 42, 50));
            using var rim = new Pen(unlocked ? Color.FromArgb(224, 178, 91) : Color.FromArgb(82, 87, 98),
                Math.Max(1f, 0.0012f * h));
            g.FillPath(fill, shield);
            g.DrawPath(rim, shield);
        }

        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tierFont = new Font("Segoe UI", 0.0150f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var tierInk = new SolidBrush(unlocked ? Ink : Dim);
        g.DrawString(level.ToString(), tierFont, tierInk, new RectangleF(x, y, w, sh * 0.76f), center);

        if (!unlocked) return;
        using var turnFont = new Font("Segoe UI", 0.0095f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var turnInk = new SolidBrush(Color.FromArgb(237, 196, 105));
        using var right = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(turn > 0 ? $"↗{turn}" : "↗?", turnFont, turnInk,
            new RectangleF(cell.X, cell.Y + 0.038f * h, cell.Width - 0.003f * h, 0.014f * h), right);
    }

    /// <summary>A round portrait of whatever card the id names.</summary>
    private static void DrawFace(Graphics g, string cardId, CardArt? art, RectangleF box, Color ring, float h)
    {
        using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillEllipse(shade, box.X - 0.002f * h, box.Y + 0.002f * h,
                box.Width + 0.004f * h, box.Height + 0.004f * h);

        Image? square = art?.Art(cardId);
        Image? pic = square ?? art?.Tile(cardId);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(box);
            Region old = g.Clip;
            InterpolationMode smooth = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SetClip(path, CombineMode.Replace);
            if (pic is not null)
            {
                RectangleF src = square is not null
                    ? new RectangleF(pic.Width * 0.148f, pic.Height * 0.040f,
                                     pic.Width * 0.703f, pic.Height * 0.703f)
                    : new RectangleF(pic.Width * 0.52f - pic.Height / 2f, 0, pic.Height, pic.Height);
                g.DrawImage(pic, box, src, GraphicsUnit.Pixel);
            }
            else
            {
                using var empty = new SolidBrush(Color.FromArgb(34, 36, 48));
                g.FillRectangle(empty, box);
            }
            g.InterpolationMode = smooth;
            g.Clip = old;
        }
        using var pen = new Pen(Color.FromArgb(200, ring), Math.Max(1.5f, 0.0026f * h));
        g.DrawEllipse(pen, box);
    }

    /// <summary>
    /// A rectangle with rounded corners. The corners stay well short of half
    /// the height: a full capsule would curve away under the last portrait.
    /// </summary>
    private static GraphicsPath Pill(RectangleF box, float radius)
    {
        float d = Math.Min(radius, Math.Min(box.Width, box.Height) / 2) * 2;
        var path = new GraphicsPath();
        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>One minion: its portrait, with attack and health on the corners.</summary>
    private static void DrawMinion(Graphics g, BoardCard card, CardArt? art, RectangleF box, float h)
    {
        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var face = new RectangleF(box.X, box.Y, box.Width, box.Width * 1.08f);
        using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillEllipse(shade, face.X - 0.0022f * h, face.Y + 0.0022f * h,
                face.Width + 0.0044f * h, face.Height + 0.0044f * h);

        Image? square = art?.Art(card.CardId);
        Image? pic = square ?? art?.Tile(card.CardId);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(face);
            Region old = g.Clip;
            InterpolationMode smooth = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SetClip(path, CombineMode.Replace);
            if (pic is not null)
            {
                // The square art has the card's whole picture with a blank
                // margin each side; the minion itself sits a little above
                // the middle. The tile, when that is all there is, is a wide
                // crop whose middle holds the same thing.
                RectangleF src = square is not null
                    ? new RectangleF(pic.Width * 0.148f, pic.Height * 0.078f,
                                     pic.Width * 0.703f, pic.Height * 0.703f)
                    : new RectangleF(pic.Width * 0.52f - pic.Height / 2f, 0, pic.Height, pic.Height);
                g.DrawImage(pic, face, src, GraphicsUnit.Pixel);
            }
            else
            {
                using var empty = new SolidBrush(Color.FromArgb(32, 34, 46));
                g.FillRectangle(empty, face);
            }
            g.InterpolationMode = smooth;
            g.Clip = old;
        }

        Color ring = card.Marks.Contains("щит") ? Color.FromArgb(255, 214, 120)
                   : card.Marks.Contains("провок") ? Color.FromArgb(150, 158, 178)
                   : Color.FromArgb(120, 98, 62);
        using (var pen = new Pen(ring, Math.Max(1.5f, card.Marks.Contains("провок") ? 0.0040f * h : 0.0022f * h)))
            g.DrawEllipse(pen, face);
        if (card.Marks.Contains("яд"))
            using (var poison = new SolidBrush(Color.FromArgb(120, 230, 130)))
                g.FillEllipse(poison, face.Right - 0.016f * h, face.Y + 0.002f * h, 0.013f * h, 0.013f * h);

        // Attack and health, bare on the portrait the way the board shows
        // them: an outline keeps them readable over whatever art is under.
        string[] parts = card.Stats.Split('/');
        string atkText = parts[0];
        string hpText = parts.Length > 1 ? parts[1] : "";
        float digits = Math.Max(atkText.Length, hpText.Length);
        float size = (digits >= 5 ? 0.0100f * h : digits >= 4 ? 0.0118f * h
                   : digits >= 3 ? 0.0145f * h : 0.0170f * h)
                   * Math.Min(1f, box.Width / (0.054f * h));
        using var numFont = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        using var left = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Near };
        using var rightEdge = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far };

        float line = size * 1.35f, numY = face.Bottom - line * 0.78f;
        // Kept within the portrait: at four digits a number that hangs out
        // would run into the minion beside it.
        var atkBox = new RectangleF(face.X - 0.002f * h, numY, box.Width * 0.58f, line);
        var hpBox = new RectangleF(face.Right - box.Width * 0.58f + 0.002f * h, numY, box.Width * 0.58f, line);
        Outlined(g, atkText, numFont, Color.FromArgb(118, 226, 118), atkBox, left, h);
        Outlined(g, hpText, numFont, Color.FromArgb(240, 84, 72), hpBox, rightEdge, h);

        g.SmoothingMode = was;
    }

    /// <summary>Text with a dark rim, for numbers that sit straight on art.</summary>
    private static void Outlined(Graphics g, string text, Font font, Color ink, RectangleF box,
        StringFormat format, float h)
    {
        if (text.Length == 0) return;
        float d = Math.Max(1f, 0.0016f * h);
        using (var rim = new SolidBrush(Color.FromArgb(230, 8, 8, 12)))
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        g.DrawString(text, font, rim,
                            new RectangleF(box.X + dx * d, box.Y + dy * d, box.Width, box.Height), format);
        using var brush = new SolidBrush(ink);
        g.DrawString(text, font, brush, box, format);
    }

    /// <summary>
    /// Where the list of finished games sits, and how big its parts are: a
    /// narrow strip, one portrait-high row per game.
    /// </summary>
    internal static (float X, float Y, float W, float Pad, float Head, float Row) HistoryBox(Size size)
    {
        float h = size.Height;
        return (PlayLeft(size) + 0.010f * h, (float)(Settings.Current.PanelY * h), 0.190f * h,
                0.009f * h, 0.025f * h, 0.032f * h);
    }

    /// <summary>Which row the pointer is over, or -1. Mirrors the drawing.</summary>
    internal static int HistoryHit(Point at, int rows, Size size)
    {
        var (x, y, w, pad, head, row) = HistoryBox(size);
        if (rows == 0 || at.X < x || at.X > x + w) return -1;
        float top = y + pad + head;
        if (at.Y < top) return -1;
        int i = (int)((at.Y - top) / row);
        return i < rows ? i : -1;
    }

    /// <summary>
    /// The games just played, one short row each: the hero as a portrait, the
    /// place it finished in and the rating it moved. The day's swing closes
    /// the strip, so where the rating stood before the first game stays in view.
    /// </summary>
    private static void DrawHistory(Graphics g, HistoryPanel panel, CardArt? art, Size size, float h)
    {
        var (x, y, w, pad, head, row) = HistoryBox(size);
        int n = panel.Rows.Count;
        float inner = w - 2 * pad, stripe = 0.0030f * h;
        float foot = panel.Today is null ? 0 : 0.037f * h;
        float total = pad + head + n * row + foot + pad;

        using var titleFont = new Font("Segoe UI", 0.0130f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var placeFont = new Font("Segoe UI", 0.0170f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var deltaFont = new Font("Segoe UI", 0.0150f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", 0.0110f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var flow  = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        using var right = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far };
        using var midLeft = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
        using var midRight = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Alignment = StringAlignment.Far,
              LineAlignment = StringAlignment.Center };

        using var bg = new SolidBrush(Back);
        using var band = new SolidBrush(Band);
        using var edge = new Pen(Frame, Math.Max(1f, 0.0013f * h));
        using var accent = new SolidBrush(Color.FromArgb(255, 196, 70));
        using var titleInk = new SolidBrush(Ink);
        using var noteInk = new SolidBrush(Muted);
        using var dimInk = new SolidBrush(Dim);
        float radius = 0.009f * h;
        SmoothingMode frameWas = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath frame = Pill(new RectangleF(x, y, w, total), radius))
        {
            g.FillPath(bg, frame);
            Region old = g.Clip;
            g.SetClip(new RectangleF(x, y, w, pad + head), CombineMode.Intersect);
            using (GraphicsPath top = Pill(new RectangleF(x, y, w, pad + head + 2 * radius), radius))
                g.FillPath(band, top);
            g.Clip = old;
            g.FillRectangle(accent, x, y + pad + head - 0.0022f * h, w, 0.0022f * h);
            g.DrawPath(edge, frame);
        }
        g.SmoothingMode = frameWas;

        float tx = x + pad + stripe;
        g.DrawString(panel.Title, titleFont, titleInk, new RectangleF(tx, y + pad * 0.6f, inner, head), flow);
        g.DrawString(panel.Note, smallFont, noteInk,
            new RectangleF(tx, y + pad * 0.9f, inner - stripe, head), right);

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float face = row - 0.009f * h;
        float cy = y + pad + head;
        for (int i = 0; i < n; i++)
        {
            HistoryRow r = panel.Rows[i];
            if (i == panel.Hover)
                using (var hot = new SolidBrush(Color.FromArgb(28, 30, 42)))
                    g.FillRectangle(hot, x + stripe, cy, w - stripe, row);

            // The ring takes the place's colour, so a row reads at a glance.
            var avatar = new RectangleF(tx, cy + (row - face) / 2, face, face);
            DrawFace(g, r.HeroCard, art, avatar, r.PlaceInk, h);

            using var placeInk = new SolidBrush(r.PlaceInk);
            using var deltaInk = new SolidBrush(r.DeltaInk);
            g.DrawString(r.Place, placeFont, placeInk,
                new RectangleF(avatar.Right + 0.008f * h, cy, 0.030f * h, row), midLeft);
            g.DrawString(r.Delta, deltaFont, deltaInk, new RectangleF(tx, cy, inner, row), midRight);
            cy += row;
        }
        g.SmoothingMode = was;

        if (panel.Today is { } today)
        {
            using var rule = new SolidBrush(Edge);
            g.FillRectangle(rule, tx, cy + 0.005f * h, inner, Math.Max(1f, 0.0011f * h));
            float fy = cy + 0.010f * h;
            int moved = today.Now - today.Before;
            using var swing = new SolidBrush(moved > 0 ? Good : moved < 0 ? Bad : Muted);
            g.DrawString("сегодня", smallFont, noteInk, new RectangleF(tx, fy + 0.002f * h, inner, 0.015f * h), flow);
            g.DrawString(moved > 0 ? $"+{moved}" : moved.ToString(), deltaFont, swing,
                new RectangleF(tx, fy, inner, 0.017f * h), right);
            g.DrawString($"{today.Before} → {today.Now}", smallFont, dimInk,
                new RectangleF(tx, fy + 0.017f * h, inner, 0.015f * h), flow);
        }

        if (panel.Hover >= 0 && panel.Hover < n)
            DrawBoardTip(g, panel.Rows[panel.Hover], art, x + w + 0.006f * h,
                y + pad + head + panel.Hover * row, h);
    }

    /// <summary>
    /// The board that game ended on, beside the row under the pointer: the
    /// minions as their own portraits, with the stats they finished with.
    /// </summary>
    private static void DrawBoardTip(Graphics g, HistoryRow r, CardArt? art, float x, float rowY, float h)
    {
        if (r.Board.Count == 0) return;
        int n = r.Board.Count;
        float oval = 0.052f * h, gap = 0.012f * h, pad = 0.013f * h, line = 0.018f * h;
        float head = line + 2 * 0.016f * h;

        using var heroFont = new Font("Segoe UI", 0.0145f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var noteFont = new Font("Segoe UI", 0.0120f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var flow = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Alignment = StringAlignment.Far };

        float cards = n * oval + (n - 1) * gap;
        float text = Math.Max(
            g.MeasureString(r.Hero + "    " + r.When, heroFont, int.MaxValue, flow).Width,
            Math.Max(g.MeasureString(r.BoardNote, noteFont, int.MaxValue, flow).Width,
                     g.MeasureString(r.Tribes, noteFont, int.MaxValue, flow).Width));
        float w = Math.Max(cards, text) + 2 * pad;
        float total = pad + head + oval * 1.12f + pad;
        float y = Math.Max(0.004f * h, Math.Min(rowY - line, h - total - 0.004f * h));

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath pill = Pill(new RectangleF(x, y, w, total), 0.014f * h))
        {
            using var bg = new SolidBrush(Back);
            using var edge = new Pen(Edge);
            g.FillPath(bg, pill);
            g.DrawPath(edge, pill);
        }

        float tx = x + pad, inner = w - 2 * pad;
        using var heroInk = new SolidBrush(Ink);
        using var noteInk = new SolidBrush(Muted);
        using var dimInk = new SolidBrush(Dim);
        g.DrawString(r.Hero, heroFont, heroInk, new RectangleF(tx, y + pad, inner, line), flow);
        g.DrawString(r.When, noteFont, dimInk, new RectangleF(tx, y + pad + 0.002f * h, inner, line), right);
        g.DrawString(r.BoardNote, noteFont, noteInk,
            new RectangleF(tx, y + pad + line, inner, 0.016f * h), flow);
        g.DrawString(r.Tribes, noteFont, dimInk,
            new RectangleF(tx, y + pad + line + 0.016f * h, inner, 0.016f * h), flow);

        float bx = tx, by = y + pad + head;
        foreach (BoardCard card in r.Board)
        {
            DrawMinion(g, card, art, new RectangleF(bx, by, oval, oval * 1.12f), h);
            bx += oval + gap;
        }
        g.SmoothingMode = was;
    }

    public static Color PlaceColor(int place, bool duo) => duo
        ? place <= 1 ? Color.FromArgb(255, 196, 0) : place == 2 ? Color.FromArgb(120, 220, 110)
                                                   : place == 3 ? Color.FromArgb(240, 180, 70) : Color.FromArgb(230, 90, 80)
        : place <= 1 ? Color.FromArgb(255, 196, 0) : place <= 2 ? Color.FromArgb(120, 220, 110)
                     : place <= 4 ? Color.FromArgb(200, 210, 110) : Color.FromArgb(230, 90, 80);

    /// <summary>The caption over a row of badges: as small as it reads.</summary>
    private static void DrawChip(Graphics g, string text, Font font, Color bg, Color fg, float cx, float y, float h)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var tight = new StringFormat(StringFormat.GenericTypographic);
        SizeF sz = g.MeasureString(text, font, int.MaxValue, tight);
        float padX = 0.007f * h, padY = 0.0035f * h;
        var r = new RectangleF(cx - sz.Width / 2 - padX, y, sz.Width + 2 * padX, sz.Height + 2 * padY);

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath pill = Pill(r, 0.005f * h))
        {
            using var fill = new SolidBrush(bg);
            using var rim = new Pen(Frame, Math.Max(1f, 0.0011f * h));
            g.FillPath(fill, pill);
            g.DrawPath(rim, pill);
        }
        g.SmoothingMode = was;

        using var ink = new SolidBrush(fg);
        g.DrawString(text, font, ink, r.X + padX, r.Y + padY, tight);
    }

    /// <summary>
    /// One option's rank: the tier letter, the place that option finishes at
    /// on average and what that number is made of. Kept small — the game
    /// shows the lobby's tribes right above this row.
    /// </summary>
    private static void DrawBadge(Graphics g, RatedOption o, float cx, float top, float h)
    {
        bool hinted = o.TribeHint is { Length: > 0 };
        float bw = 0.178f * h, bh = hinted ? 0.056f * h : 0.042f * h, tw = 0.040f * h;
        var box = new RectangleF(cx - bw / 2, top, bw, bh);
        Color tier = TierColor(o.Tier);
        float radius = 0.006f * h;

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath pill = Pill(box, radius))
        {
            using var bg = new SolidBrush(Back);
            g.FillPath(bg, pill);
            // The tier block takes the rounding of the side it sits on.
            Region old = g.Clip;
            g.SetClip(new RectangleF(box.X, box.Y, tw, bh), CombineMode.Intersect);
            using (var tierBg = new SolidBrush(tier))
            using (GraphicsPath left = Pill(new RectangleF(box.X, box.Y, tw + 2 * radius, bh), radius))
                g.FillPath(tierBg, left);
            g.Clip = old;
            using var border = new Pen(Color.FromArgb(215, tier), Math.Max(1f, 0.0016f * h));
            g.DrawPath(border, pill);
        }
        g.SmoothingMode = was;

        using var tierFont = new Font("Segoe UI", 0.029f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(o.Tier, tierFont, Brushes.Black, new RectangleF(box.X, box.Y, tw, bh), center);

        using var tight = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        using var big = new Font("Segoe UI", 0.0195f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var unitFont = new Font("Segoe UI", 0.0108f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 0.0112f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var inkBig = new SolidBrush(Ink);
        using var inkSmall = new SolidBrush(Muted);

        float x = box.X + tw + 0.007f * h, inner = bw - tw - 0.012f * h, y = box.Y + 0.0045f * h;
        if (o.AvgPlace is double p)
        {
            string place = $"{p:0.00}";
            SizeF num = g.MeasureString(place, big, int.MaxValue, tight);
            g.DrawString(place, big, inkBig, new RectangleF(x, y, inner, 0.021f * h), tight);
            using var unitInk = new SolidBrush(Dim);
            g.DrawString("место", unitFont, unitInk,
                new RectangleF(x + num.Width + 0.003f * h, y + 0.0085f * h, inner, 0.014f * h), tight);
        }
        else
        {
            using var noneFont = new Font("Segoe UI", 0.0150f * h, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("нет данных", noneFont, inkSmall,
                new RectangleF(x, y + 0.002f * h, inner, 0.019f * h), tight);
        }

        y += 0.0215f * h;
        g.DrawString(o.Detail, small, inkSmall, new RectangleF(x, y, inner, 0.014f * h), tight);
        if (hinted)
        {
            using var hintFont = new Font("Segoe UI", 0.0106f * h, FontStyle.Regular, GraphicsUnit.Pixel);
            using var hintBrush = new SolidBrush(Color.FromArgb(150, 220, 140));
            g.DrawString(o.TribeHint!, hintFont, hintBrush,
                new RectangleF(x, y + 0.0140f * h, inner, 0.014f * h), tight);
        }
    }

    /// <summary>Made-up state for the layout preview and for rendering a shot.</summary>
    internal static OverlayModel Sample(ChoiceKind kind)
    {
        string[] tiers = { "A", "B", "C", "E" };
        int n = kind == ChoiceKind.Hero ? 4 : 3;
        var items = Enumerable.Range(0, n).Select(i => new RatedOption(
            new ChoiceOption(i, "", $"Пример {i + 1}"), tiers[i], 3.6 + 0.3 * i,
            kind == ChoiceKind.Hero ? "топ-4 55% · 1-е 15%" : "берут в 20%",
            kind == ChoiceKind.Hero ? "любит: Звери, Драконы" : null)).ToList();
        var games = new[]
        {
            new HistoryRow("4", Bad, "TB_BaconShop_HERO_28", "Дорнозму Освобожденный", "", "16:08", "-24", Bad, "", []),
            new HistoryRow("1", Good, "BG31_HERO_005", "Воевода Саурфанг", "", "16:02", "+82", Good, "", []),
            new HistoryRow("4", Bad, "TB_BaconShop_HERO_28", "Пирамидон", "", "15:54", "+0", Muted, "", []),
        };
        var panel = new LobbyPanel(
            [15, 24, 20, 14, 18], [11, 17, 43, 92, 126, 23], 6082, 6082, games);
        var banner = new WinBanner(false, "30%", "420 против 310", 0.30, 0.60, 0.10,
            "с союзником 78%  ·  730 против 310", 0.78);
        return new OverlayModel(kind, items, "Пример расположения — подгоните высоту в окне", panel, banner,
            Turn: "Ход 8", Buffs:
            [
                new BattlegroundsBuff(BattlegroundsBuffKind.Undead, 12, 0),
                new BattlegroundsBuff(BattlegroundsBuffKind.TavernSpells, 4, 8),
                new BattlegroundsBuff(BattlegroundsBuffKind.Beetles, 14, 10),
                new BattlegroundsBuff(BattlegroundsBuffKind.BloodGems, 3, 5),
            ]);
    }

    /// <summary>Made-up past games, for looking at the list without playing.</summary>
    internal static OverlayModel SampleHistory()
    {
        (string Card, string Name, string Stats, string Marks)[] board =
        {
            ("BG36_366_G", "Мурчаль", "768/235", "щит"),
            ("BG26_149_G", "Калигуло", "4961/1238", "провок. · щит"),
            ("BGS_131", "Жуткий счетовод", "36/48", "щит · яд"),
            ("BG31_177", "Хитрый кобольд", "591/683", ""),
            ("BG36_853_G", "Механозавр", "2237/2404", "провок. · вихрь"),
            ("BG34_500", "Ледяной страж", "2576/2249", "щит"),
            ("BG_LOE_077", "Самоцвет", "177/226", ""),
        };
        var cards = board.Select(b => new BoardCard(b.Card, b.Name, b.Stats, b.Marks,
            b.Marks.Contains("яд") ? Good : b.Marks.Contains("щит") ? Color.FromArgb(255, 214, 120)
                                                                   : Muted)).ToList();
        string[] heroes = { "Вечная Токи", "Клонист Зерек", "Вечная Токи", "Клонист Зерек" };
        string[] heroCards = { "TB_BaconShop_HERO_28", "BG31_HERO_005", "TB_BaconShop_HERO_28", "BG31_HERO_005" };
        int[] places = { 1, 2, 4, 3 };
        string[] deltas = { "+92", "+0", "-58", "-12" };
        var rows = Enumerable.Range(0, 4).Select(i => new HistoryRow(
            places[i].ToString(), PlaceColor(places[i], duo: true), heroCards[i], heroes[i],
            "дуэты · Нежить, Механизмы, Аберрации, Драконы, Звери",
            $"2{1 + i}:0{i}", deltas[i], deltas[i] == "+0" ? Muted : deltas[i][0] == '+' ? Good : Bad,
            $"{places[i]} место · ход {14 - i} · {5000 - i * 900} ат.+зд.",
            i == 2 ? cards.Take(5).ToList() : cards)).ToList();
        var panel = new HistoryPanel("Рейтинг 8420", "ср. место 2,5", rows, 1, new RatingToday(8357, 8420, 4));
        return new OverlayModel(null, [], "", null, null, panel);
    }

    /// <summary>A board under a portrait, for looking at it without playing.</summary>
    internal static OverlayModel SamplePeek()
    {
        (string Card, string Name, string Stats, string Marks)[] board =
        {
            ("BG36_366_G", "Мурчаль", "768/235", "щит"),
            ("BG26_149_G", "Калигуло", "4961/1238", "провок. · щит"),
            ("BGS_131", "Жуткий счетовод", "36/48", "щит · яд"),
            ("BG31_177", "Хитрый кобольд", "591/683", ""),
            ("BG36_853_G", "Механозавр", "2237/2404", "провок. · вихрь"),
            ("BG34_500", "Ледяной страж", "2576/2249", "щит"),
            ("BG_LOE_077", "Самоцвет", "177/226", ""),
        };
        var cards = board.Select(b => new BoardCard(b.Card, b.Name, b.Stats, b.Marks,
            b.Marks.Contains("яд") ? Good : Muted)).ToList();
        var peek = new BoardPeek(
        [
            new PeekBoard("TB_BaconShop_HERO_28", Ink, cards, 6,
                [new(1, 1), new(2, 2), new(3, 4), new(4, 6), new(5, 9), new(6, 11)]),
            new PeekBoard("BG31_HERO_005", Ink, cards.Take(5).ToList(), 5,
                [new(1, 1), new(2, 2), new(3, 4), new(4, 7), new(5, 10)]),
        ]);
        return new OverlayModel(null, [], "", null, null, null, peek);
    }

    public static Color TierColor(string tier) => tier switch
    {
        "S" => Color.FromArgb(255, 196, 0),
        "A" => Color.FromArgb(76, 217, 100),
        "B" => Color.FromArgb(170, 220, 80),
        "C" => Color.FromArgb(240, 205, 70),
        "D" => Color.FromArgb(240, 140, 50),
        "E" => Color.FromArgb(230, 70, 60),
        _   => Color.FromArgb(140, 140, 140),
    };

    public static Color WinColor(double p) =>
        p >= 0.60 ? Color.FromArgb(76, 217, 100) :
        p >= 0.45 ? Color.FromArgb(220, 200, 70) :
                    Color.FromArgb(230, 80, 70);
}
