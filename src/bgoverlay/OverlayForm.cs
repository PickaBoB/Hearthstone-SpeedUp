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
    IReadOnlyList<BoardCard> Board, int Tier, IReadOnlyList<TavernTier> Tavern, int LastTurn = 0);

/// <summary>The boards and tavern progress to show over the play area.</summary>
public sealed record BoardPeek(IReadOnlyList<PeekBoard> Boards, int CurrentTurn = 0);

/// <summary>Badges over a pending choice and/or the side panel.</summary>
public sealed record OverlayModel(
    ChoiceKind? Kind,
    IReadOnlyList<RatedOption> Items,
    string Header,
    LobbyPanel? Panel = null,
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
        if (model.Panel is { } panel && Settings.Current.PanelEnabled) DrawPanel(g, panel, art, size, h);
        if (model.Turn.Length > 0 && Settings.Current.TurnEnabled) DrawTurn(g, model.Turn, size, h);
        if (model.Buffs is { Count: > 0 } buffs) DrawBuffs(g, buffs, art, size, h);
        if (model.Kind is null) return;

        int n = model.Items.Count;
        double rowY = model.Kind == ChoiceKind.Hero ? Settings.Current.HeroBadgeY : Settings.Current.TrinketBadgeY;
        float top = (float)(rowY * h);

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
            float u = w / 240f;
            float heroW = w * 1.7f / 3.7f, placeW = w / 3.7f;
            float placeX = x + heroW, mmrX = placeX + placeW;
            using var labels = new SolidBrush(Muted);
            g.DrawString("Герой", smallFont, labels, new RectangleF(x + 8 * u, cy, heroW - 8 * u, gameHeadH), left);
            g.DrawString("Место", smallFont, labels, new RectangleF(placeX, cy, placeW, gameHeadH), center);
            g.DrawString("MMR", smallFont, labels, new RectangleF(mmrX, cy, placeW, gameHeadH), center);
            cy += gameHeadH;

            foreach (HistoryRow game in panel.Games)
            {
                var row = new RectangleF(x, cy, w, gameH);
                using (var rowBg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    g.FillRectangle(rowBg, row);
                Image? tile = art?.Tile(game.HeroCard);
                if (tile is not null)
                {
                    float imageX = x - 35 * u, imageW = 145 * u;
                    float srcX = tile.Width * (34f / 256f), srcW = tile.Width - srcX;
                    float srcH = Math.Min(tile.Height, srcW * 34f / 145f);
                    float srcY = (tile.Height - srcH) / 2;
                    const int slices = 24;
                    Region oldClip = g.Clip;
                    g.SetClip(row, CombineMode.Replace);
                    using var attributes = new ImageAttributes();
                    for (int i = 0; i < slices; i++)
                    {
                        float alpha = 170f / 255f * (1f - (i + 0.5f) / slices);
                        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                        g.DrawImage(tile,
                            Rectangle.Round(new RectangleF(imageX + i * imageW / slices, cy,
                                imageW / slices + 0.5f, gameH)),
                            srcX + i * srcW / slices, srcY, srcW / slices, srcH, GraphicsUnit.Pixel, attributes);
                    }
                    g.Clip = oldClip;
                }
                using var heroInk = new SolidBrush(Ink);
                using var placeInk = new SolidBrush(game.PlaceInk);
                using var deltaInk = new SolidBrush(game.DeltaInk);
                g.DrawString(game.Hero, gameFont, heroInk,
                    new RectangleF(x + 8 * u, cy, heroW - 8 * u, gameH), left);
                g.DrawString(game.Place, gameBold, placeInk,
                    new RectangleF(placeX, cy, placeW, gameH), center);
                g.DrawString(game.Delta, gameBold, deltaInk,
                    new RectangleF(mmrX, cy, placeW, gameH), center);
                if (game.Place is "1" or "1-е" && art?.Icon("bgs-crown") is { } crown)
                    g.DrawImage(crown, new RectangleF(placeX + (placeW - 22 * u) / 2 - 8 * u,
                        cy + (gameH - 18 * u) / 2 - 7 * u, 16 * u, 16 * u));
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
    /// The hovered player's last seen board and tavern upgrades, using Deck
    /// Tracker's panel geometry, colours and original game interface images.
    /// </summary>
    private static void DrawPeek(Graphics g, BoardPeek peek, CardArt? art, Size size, float h)
    {
        float boardW = 0.685f * h, tiersW = 0.231f * h, panelH = 0.139f * h;
        float cardH = panelH, cardW = cardH * 300f / 350f, cardStep = 0.0926f * h;
        float captionH = 0.025f * h;
        float y = (float)(Settings.Current.BoardY * h);

        using var captionFont = new Font("Segoe UI", 0.0148f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var center = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        using var left = new StringFormat(StringFormat.GenericTypographic)
        {
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        SmoothingMode was = g.SmoothingMode;
        InterpolationMode interpolation = g.InterpolationMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        foreach (PeekBoard board in peek.Boards)
        {
            float width = Math.Min(boardW + tiersW, size.Width - 0.020f * h);
            float x = (size.Width - width) / 2;
            var frame = new RectangleF(x, y, width, panelH);
            using (var fill = new LinearGradientBrush(frame,
                       Color.FromArgb(229, 32, 36, 39), Color.FromArgb(229, 35, 39, 42), 90f))
                g.FillRectangle(fill, frame);
            using (var rim = new Pen(Color.FromArgb(229, 64, 67, 69), Math.Max(1f, 0.0010f * h)))
            {
                g.DrawRectangle(rim, frame.X, frame.Y, frame.Width, frame.Height);
                g.DrawLine(rim, x + boardW, y, x + boardW, y + panelH);
            }

            int count = board.Board.Count;
            float cardsWidth = count > 0 ? cardW + (count - 1) * cardStep : 0;
            float bx = x + (boardW - cardsWidth) / 2, by = y;
            foreach (BoardCard card in board.Board)
            {
                DrawMinion(g, card, art, new RectangleF(bx, by, cardW, cardH), h);
                bx += cardStep;
            }

            if (count == 0)
            {
                using var emptyInk = new SolidBrush(Ink);
                g.DrawString("Доска ещё не была видна", captionFont, emptyInk,
                    new RectangleF(x, y, boardW, panelH), center);
            }
            else
            {
                int ago = Math.Max(0, peek.CurrentTurn - board.LastTurn);
                string caption = ago == 0 ? "Текущая доска" : $"{ago} {TurnWord(ago)} назад";
                using var captionInk = new SolidBrush(Ink);
                g.DrawString(caption, captionFont, captionInk,
                    new RectangleF(x, y + panelH + 0.0046f * h, boardW, captionH), left);
            }

            float cellW = 0.0694f * h, cellH = 0.0556f * h;
            float tiersX = x + boardW + 0.006f * h, tiersY = y + 0.0093f * h;
            for (int level = 1; level <= 6; level++)
            {
                TavernTier? reached = board.Tavern.FirstOrDefault(t => t.Level == level);
                int column = (level - 1) % 3, row = (level - 1) / 3;
                DrawTavernTier(g, art, level, reached?.Turn ?? 0, reached?.Triples ?? 0,
                    new RectangleF(tiersX + column * cellW, tiersY + row * (cellH + 0.0046f * h),
                        cellW - 0.0046f * h, cellH), h);
            }
            y += panelH + captionH + 0.006f * h;
        }

        g.InterpolationMode = interpolation;
        g.SmoothingMode = was;
    }

    private static string TurnWord(int turn)
    {
        int lastTwo = turn % 100, last = turn % 10;
        if (lastTwo is >= 11 and <= 14 || last == 0 || last >= 5) return "ходов";
        return last == 1 ? "ход" : "хода";
    }

    /// <summary>One Deck Tracker tavern cell: game shield, triples and upgrade turn.</summary>
    private static void DrawTavernTier(Graphics g, CardArt? art, int level, int turn, int triples,
        RectangleF cell, float h)
    {
        using (GraphicsPath background = Pill(cell, 0.0028f * h))
        using (var fill = new SolidBrush(turn > 0 ? Color.FromArgb(55, 57, 60) : Color.FromArgb(40, 43, 46)))
            g.FillPath(fill, background);

        float tierX = cell.X + 0.0028f * h + (turn > 0 ? 0 : 0.0111f * h);
        float tierY = cell.Y + (turn > 0 ? 0.0056f : 0.0102f) * h;
        Image? tier = art?.Icon($"tier-{level}");
        if (tier is not null)
        {
            var box = new RectangleF(tierX, tierY, 0.0222f * h, 0.0222f * h);
            if (turn > 0) g.DrawImage(tier, box);
            else
            {
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.5f });
                g.DrawImage(tier, new Rectangle((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height),
                    0, 0, tier.Width, tier.Height, GraphicsUnit.Pixel, attributes);
            }
        }
        if (turn <= 0) return;

        Image? triple = art?.Icon("triple");
        if (triple is not null)
        {
            var box = new RectangleF(cell.X + 0.0287f * h, cell.Y + 0.0056f * h,
                0.0315f * h, 0.0315f * h);
            g.DrawImage(triple, box);
            if (triples == 0)
            {
                Image? shade = art?.Icon("triple-black");
                if (shade is not null)
                {
                    using var attributes = new ImageAttributes();
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.2f });
                    g.DrawImage(shade, new Rectangle((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height),
                        0, 0, shade.Width, shade.Height, GraphicsUnit.Pixel, attributes);
                }
            }
        }

        using var qtyFont = new Font("Segoe UI", 0.0139f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var turnFont = new Font("Segoe UI", 0.0093f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.White);
        using var center = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(triples.ToString(), qtyFont, ink,
            new RectangleF(cell.X + 0.0370f * h, cell.Y + 0.0083f * h, 0.0185f * h, 0.0222f * h), center);
        g.DrawString($"{turn} {TurnWord(turn)}", turnFont, ink,
            new RectangleF(cell.X, cell.Y + 0.0390f * h, cell.Width, 0.0140f * h), center);
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

    /// <summary>One minion with the game's own frame, stat plate and keyword overlays.</summary>
    private static void DrawMinion(Graphics g, BoardCard card, CardArt? art, RectangleF box, float h)
    {
        SmoothingMode was = g.SmoothingMode;
        InterpolationMode interpolation = g.InterpolationMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float sx = box.Width / 300f, sy = box.Height / 350f;

        void Layer(string name)
        {
            Image? image = art?.Icon(name);
            if (image is not null) g.DrawImage(image, box);
        }

        if (card.Marks.Contains("провок")) Layer("Minion/taunt");

        // Deck Tracker's 300x350 frame wraps a 256x256 canvas at (-24,-36).
        // The portrait is the ellipse centred at 128,128 with radii 87,120.
        var face = new RectangleF(box.X + 65f * sx, box.Y + 44f * sy, 174f * sx, 240f * sy);
        var canvas = new RectangleF(box.X + 24f * sx, box.Y + 36f * sy, 256f * sx, 256f * sy);
        Image? square = art?.Art(card.CardId);
        Image? pic = square ?? art?.Tile(card.CardId);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(face);
            Region old = g.Clip;
            g.SetClip(path, CombineMode.Replace);
            if (pic is not null)
            {
                RectangleF src = square is not null
                    ? new RectangleF(pic.Width * 0.148f, pic.Height * 0.078f,
                                     pic.Width * 0.703f, pic.Height * 0.703f)
                    : new RectangleF(pic.Width * 0.52f - pic.Height / 2f, 0, pic.Height, pic.Height);
                g.DrawImage(pic, canvas, src, GraphicsUnit.Pixel);
            }
            else
            {
                using var empty = new SolidBrush(Color.FromArgb(32, 34, 36));
                g.FillEllipse(empty, face);
            }
            g.Clip = old;
        }

        if (art?.Icon("Minion/border") is { } border) g.DrawImage(border, box);
        else
        {
            using var fallback = new Pen(Color.FromArgb(122, 119, 112), Math.Max(1.5f, 0.0022f * h));
            g.DrawEllipse(fallback, face);
        }
        if (card.Marks.Contains("возр")) Layer("Minion/reborn");
        if (card.Marks.Contains("яд")) Layer("Minion/poisonous");
        Layer("Minion/stats");
        if (card.Marks.Contains("щит") && art?.Icon("Minion/divine-shield") is { } shield)
            g.DrawImage(shield, new RectangleF(box.X - 12f * sx, box.Y + 12f * sy, 325f * sx, 311f * sy));

        string[] parts = card.Stats.Split('/');
        string atkText = parts[0];
        string hpText = parts.Length > 1 ? parts[1] : "";
        float digits = Math.Max(atkText.Length, hpText.Length);
        float size = digits >= 5 ? 0.0100f * h : digits >= 4 ? 0.0118f * h
                   : digits >= 3 ? 0.0145f * h : 0.0170f * h;
        using var numFont = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        using var center = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        var atkBox = new RectangleF(box.X + 53f * sx, box.Y + 206f * sy, 75f * sx, 75f * sy);
        var hpBox = new RectangleF(box.X + 175f * sx, box.Y + 206f * sy, 75f * sx, 75f * sy);
        Outlined(g, atkText, numFont, Good, atkBox, center, h);
        Outlined(g, hpText, numFont, Good, hpBox, center, h);

        g.InterpolationMode = interpolation;
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

    /// <summary>One option in Deck Tracker's choice layout: tier above two stat columns.</summary>
    private static void DrawBadge(Graphics g, RatedOption o, float cx, float top, float h)
    {
        float bw = 0.200f * h, bh = 0.040f * h, tw = 0.047f * h, th = 0.039f * h;
        float gap = Math.Max(1f, 0.0015f * h);
        float columnW = (bw - gap) / 2;
        float statsY = top + 0.039f * h;
        var tierBox = new RectangleF(cx - tw / 2, top, tw, th);
        var placeBox = new RectangleF(cx - bw / 2, statsY, columnW, bh);
        var pickBox = new RectangleF(placeBox.Right + gap, statsY, columnW, bh);
        Color tier = TierColor(o.Tier);

        using (var tierBg = new SolidBrush(tier)) g.FillRectangle(tierBg, tierBox);

        using var tierFont = new Font("Segoe UI", 0.028f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var tierLabel = new RectangleF(tierBox.X, tierBox.Y, tierBox.Width, statsY - top);
        g.DrawString(o.Tier, tierFont, Brushes.Black, tierLabel, center);

        using var tight = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        using var labelFont = new Font("Segoe UI", 0.0120f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Segoe UI", 0.0190f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labels = new SolidBrush(Muted);
        using var placeInk = new SolidBrush(Color.FromArgb(104, 196, 113));
        using var pickInk = new SolidBrush(Ink);
        using var bg = new SolidBrush(Back);
        float pad = 0.005f * h, labelH = 0.014f * h, valueY = statsY + 0.016f * h;

        void Column(RectangleF box, string label, string value, Brush valueBrush)
        {
            SizeF valueSize = g.MeasureString(value, valueFont, int.MaxValue, tight);
            float valueW = Math.Min(box.Width, valueSize.Width + 2 * pad);
            g.FillRectangle(bg, box.X, box.Y, box.Width, valueY - box.Y);
            g.FillRectangle(bg, box.X, valueY, valueW, box.Bottom - valueY);

            var labelRect = new RectangleF(box.X + pad, box.Y + 0.002f * h, box.Width - 2 * pad, labelH);
            var valueRect = new RectangleF(box.X + pad, valueY, box.Width - 2 * pad, 0.027f * h);
            g.DrawString(label, labelFont, labels, labelRect, tight);
            g.DrawString(value, valueFont, valueBrush, valueRect, tight);
        }

        Column(placeBox, "Avg Placement", o.AvgPlace is double p ? $"{p:0.00}" : "—", placeInk);
        Column(pickBox, "Частота выбора", o.Detail.Length > 0 ? o.Detail : "—", pickInk);
    }

    /// <summary>Made-up state for the layout preview and for rendering a shot.</summary>
    internal static OverlayModel Sample(ChoiceKind kind)
    {
        string[] tiers = { "A", "A", "B", "S" };
        int n = 4;
        var items = Enumerable.Range(0, n).Select(i => new RatedOption(
            new ChoiceOption(i, "", $"Пример {i + 1}"), tiers[i], 4.01 + 0.12 * i,
            $"{16.1 + 11.4 * i:0.0}%",
            kind == ChoiceKind.Hero ? "любит: Звери, Драконы" : null)).ToList();
        var games = new[]
        {
            new HistoryRow("4-е", Bad, "TB_BaconShop_HERO_28", "Дорнозму Освобожденный", "", "16:08", "-24", Bad, "", []),
            new HistoryRow("1-е", Good, "BG31_HERO_005", "Воевода Саурфанг", "", "16:02", "+82", Good, "", []),
            new HistoryRow("4-е", Bad, "TB_BaconShop_HERO_28", "Пирамидон", "", "15:54", "+0", Muted, "", []),
        };
        var panel = new LobbyPanel(
            [15, 24, 20, 14, 18], [], 6082, 6082, games);
        return new OverlayModel(kind, items, "Пример расположения — подгоните высоту в окне", panel,
            Turn: "Ход 8", Buffs:
            [
                new BattlegroundsBuff(BattlegroundsBuffKind.Undead, 12, 0),
                new BattlegroundsBuff(BattlegroundsBuffKind.TavernSpells, 4, 8),
                new BattlegroundsBuff(BattlegroundsBuffKind.Beetles, 14, 10),
                new BattlegroundsBuff(BattlegroundsBuffKind.BloodGems, 3, 5),
                new BattlegroundsBuff(BattlegroundsBuffKind.Nomi, 7, 7),
                new BattlegroundsBuff(BattlegroundsBuffKind.Volumizers, 5, 9),
                new BattlegroundsBuff(BattlegroundsBuffKind.GoldNextTurn, 4, 0),
                new BattlegroundsBuff(BattlegroundsBuffKind.FreeRerolls, 3, 0),
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
        return new OverlayModel(null, [], "", History: panel);
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
                [new(1, 1, 0), new(2, 2, 0), new(3, 4, 1), new(4, 6, 2), new(5, 9, 1), new(6, 11, 3)], 7),
            new PeekBoard("BG31_HERO_005", Ink, cards.Take(5).ToList(), 5,
                [new(1, 1, 0), new(2, 2, 1), new(3, 4, 1), new(4, 7, 2), new(5, 10, 1)], 6),
        ], 8);
        return new OverlayModel(null, [], "", Peek: peek);
    }

    public static Color TierColor(string tier) => tier switch
    {
        "S" => Color.FromArgb(52, 152, 203),
        "A" => Color.FromArgb(70, 156, 82),
        "B" => Color.FromArgb(91, 166, 77),
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
