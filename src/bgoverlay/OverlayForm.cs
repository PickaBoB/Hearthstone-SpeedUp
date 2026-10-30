using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace BgOverlay;

public enum PanelStyle { Title, Pair, Note, Rule, Section, Tribe }

/// <summary>
/// One row of the side panel: the heading with the hero's face and tier chip,
/// a section caption, a label with its value and an optional meter, a tribe
/// with how much it moves the hero's average place, small print, a separator.
/// </summary>
public sealed record PanelLine(PanelStyle Style, string Text, string Value, Color Ink, double Bar = -1,
    string Card = "")
{
    public static PanelLine Title(string text, string chip, Color ink, string card = "") =>
        new(PanelStyle.Title, text, chip, ink, -1, card);
    public static PanelLine Pair(string label, string value, Color ink, double bar = -1) =>
        new(PanelStyle.Pair, label, value, ink, bar);
    public static PanelLine Note(string text, Color ink) => new(PanelStyle.Note, text, "", ink);
    public static PanelLine Rule() => new(PanelStyle.Rule, "", "", Color.Empty);
    /// <summary>A caption with a hairline out to the edge: what the rows under it are.</summary>
    public static PanelLine Section(string text) => new(PanelStyle.Section, text, "", Color.Empty);
    /// <summary>
    /// One tribe of the lobby. <paramref name="weight"/> is how far its row is
    /// filled, 0..1, scaled against the strongest tribe of this lobby.
    /// </summary>
    public static PanelLine Tribe(string name, string value, Color ink, double weight = -1) =>
        new(PanelStyle.Tribe, name, value, ink, weight);

    /// <summary>The row as one line of text, for the control window.</summary>
    public string Flat => Value.Length > 0 ? $"{Text}: {Value}" : Text;
}

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
public sealed record PeekBoard(string Hero, string HeroCard, string Note, Color Ink,
    IReadOnlyList<BoardCard> Board);

/// <summary>What to show beside the leaderboard portrait being hovered.</summary>
public sealed record BoardPeek(float AtY, IReadOnlyList<PeekBoard> Boards);

public sealed record WinBanner(bool Waiting, string Title, string Sub, double? Win, double? Tie, double? Lose,
    string Team = "", double? TeamWin = null);

/// <summary>Badges over a pending choice, a top winrate banner, and/or the side panel.</summary>
public sealed record OverlayModel(
    ChoiceKind? Kind,
    IReadOnlyList<RatedOption> Items,
    string Header,
    IReadOnlyList<PanelLine>? Panel = null,
    WinBanner? Banner = null,
    HistoryPanel? History = null,
    BoardPeek? Peek = null,
    string Turn = "");

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
    private static readonly Color Track    = Color.FromArgb(33, 35, 46);
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
        if (model.Panel is { Count: > 0 } panel && Settings.Current.PanelEnabled) DrawPanel(g, panel, art, size, h);
        if (model.Turn.Length > 0 && Settings.Current.TurnEnabled) DrawTurn(g, model.Turn, size, h);
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
        float bw = 0.40f * h, bh = (b.Team.Length > 0 ? 0.105f : 0.086f) * h;
        var box = new RectangleF((w - bw) / 2, (float)(Settings.Current.BannerY * h), bw, bh);
        Color accent = !b.Waiting && b.Win is double p ? WinColor(p) : Color.FromArgb(78, 84, 102);

        using var bg = new SolidBrush(Back);
        using var bar = new SolidBrush(accent);
        using var edge = new Pen(Edge, Math.Max(1f, 0.0012f * h));
        g.FillRectangle(bg, box);
        g.FillRectangle(bar, box.X, box.Y, box.Width, 0.005f * h);
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

        using var winFont = new Font("Segoe UI", 0.040f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var capFont = new Font("Segoe UI", 0.0145f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont2 = new Font("Segoe UI", 0.0135f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var winInk = new SolidBrush(accent);
        using var subInk2 = new SolidBrush(Muted);

        // The number carries the box; "победа" only says what it counts.
        using var tight = new StringFormat(StringFormat.GenericTypographic);
        SizeF num = g.MeasureString(b.Title, winFont, int.MaxValue, tight);
        SizeF cap = g.MeasureString("победа ", capFont, int.MaxValue, tight);
        float lead = 0.010f * h;
        float nx = box.X + (box.Width - num.Width - cap.Width - lead) / 2;
        g.DrawString(b.Title, winFont, winInk, new RectangleF(nx, box.Y + 0.008f * h, num.Width + 1, 0.046f * h), tight);
        g.DrawString("победа", capFont, subInk2,
            new RectangleF(nx + num.Width + lead, box.Y + 0.030f * h, cap.Width + 1, 0.020f * h), tight);
        g.DrawString(b.Sub, subFont2, subInk2,
            new RectangleF(box.X, box.Y + 0.054f * h, box.Width, 0.022f * h), center);

        // Duos: the same fight with the partner's board counted in.
        if (b.Team.Length > 0)
        {
            using var teamFont = new Font("Segoe UI", 0.0130f * h, FontStyle.Bold, GraphicsUnit.Pixel);
            using var teamInk = new SolidBrush(b.TeamWin is double tw ? WinColor(tw) : Muted);
            g.DrawString(b.Team, teamFont, teamInk,
                new RectangleF(box.X, box.Y + 0.076f * h, box.Width, 0.022f * h), center);
        }

        if (b.Win is double win && b.Tie is double tie)
        {
            float barY = box.Bottom - 0.0075f * h, barH = 0.0075f * h, x = box.X;
            float wWin = (float)(box.Width * win), wTie = (float)(box.Width * tie);
            using var winB = new SolidBrush(Color.FromArgb(76, 217, 100));
            using var tieB = new SolidBrush(Color.FromArgb(200, 190, 80));
            using var loseB = new SolidBrush(Color.FromArgb(230, 80, 70));
            g.FillRectangle(winB, x, barY, wWin, barH);
            g.FillRectangle(tieB, x + wWin, barY, wTie, barH);
            g.FillRectangle(loseB, x + wWin + wTie, barY, box.Width - wWin - wTie, barH);
        }
    }

    /// <summary>
    /// Where the side panel stands: against the left edge of the game's own
    /// interface box, and never over the leaderboard it sits beside — the
    /// strip of portraits starts at <see cref="Settings.PeekX"/>.
    /// </summary>
    internal static (float X, float W) PanelBox(Size size, float h)
    {
        float x = PlayLeft(size) + 0.010f * h;
        float stop = PlayLeft(size) + (float)Settings.Current.PeekX * h - 0.014f * h;
        return (x, Math.Clamp(stop - x, 0.150f * h, 0.224f * h));
    }

    /// <summary>
    /// The side panel: the hero with its tier, the board at this turn and
    /// what the lobby's tribes do to the hero's average place. One framed
    /// card, with a captioned section per question it answers.
    /// </summary>
    private static void DrawPanel(Graphics g, IReadOnlyList<PanelLine> lines, CardArt? art, Size size, float h)
    {
        var (x, w) = PanelBox(size, h);
        float pad = 0.011f * h, gap = 0.0050f * h;
        float y = (float)(Settings.Current.PanelY * h);
        float inner = w - 2 * pad;
        float titleH = 0.034f * h, pairH = 0.020f * h, tribeH = 0.0185f * h;
        float sectionH = 0.023f * h, sectionAir = 0.006f * h, barH = 0.0050f * h;
        float ruleH = Math.Max(1f, 0.0011f * h), radius = 0.009f * h;

        using var nameFont    = new Font("Segoe UI", 0.0170f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var chipFont    = new Font("Segoe UI", 0.0160f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sectionFont = new Font("Segoe UI", 0.0110f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelFont   = new Font("Segoe UI", 0.0140f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont   = new Font("Segoe UI", 0.0158f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var tribeFont   = new Font("Segoe UI", 0.0140f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueSmall  = new Font("Segoe UI", 0.0140f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var noteFont    = new Font("Segoe UI", 0.0126f * h, FontStyle.Regular, GraphicsUnit.Pixel);
        using var flow  = new StringFormat(StringFormat.GenericTypographic);
        using var clip  = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        using var midLeft = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter,
              LineAlignment = StringAlignment.Center };
        using var midRight = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Alignment = StringAlignment.Far,
              LineAlignment = StringAlignment.Center };
        using var middle = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        float RowHeight(PanelLine l) => l.Style switch
        {
            PanelStyle.Title   => titleH,
            PanelStyle.Section => sectionH,
            PanelStyle.Tribe   => tribeH,
            PanelStyle.Pair    => pairH + (l.Bar >= 0 ? barH + 0.0035f * h : 0),
            PanelStyle.Note    => g.MeasureString(l.Text, noteFont, new SizeF(inner, 1e4f), flow).Height + 0.0015f * h,
            _                  => ruleH + gap,
        };

        float total = 2 * pad - gap;
        foreach (var l in lines) total += RowHeight(l) + gap;

        bool titled = lines[0].Style == PanelStyle.Title;
        Color accent = titled ? lines[0].Ink : Color.FromArgb(255, 196, 70);
        float bandH = pad + titleH + gap;

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath frame = Pill(new RectangleF(x, y, w, total), radius))
        {
            using var bg = new SolidBrush(Back);
            g.FillPath(bg, frame);
            if (titled)
            {
                // The heading sits on its own band; the band's lower corners
                // are square, so only the panel's own outline is rounded.
                Region old = g.Clip;
                g.SetClip(new RectangleF(x, y, w, bandH), CombineMode.Intersect);
                using (var band = new SolidBrush(Band))
                using (GraphicsPath top = Pill(new RectangleF(x, y, w, bandH + 2 * radius), radius))
                    g.FillPath(band, top);
                g.Clip = old;
                using var line = new SolidBrush(accent);
                g.FillRectangle(line, x, y + bandH - 0.0022f * h, w, 0.0022f * h);
            }
            using var edge = new Pen(Frame, Math.Max(1f, 0.0013f * h));
            g.DrawPath(edge, frame);
        }

        using var labelInk = new SolidBrush(Muted);
        using var sectionInk = new SolidBrush(Dim);
        float tx = x + pad, cy = y + pad;
        foreach (var l in lines)
        {
            float rh = RowHeight(l);
            using var ink = new SolidBrush(l.Ink == Color.Empty ? Ink : l.Ink);
            switch (l.Style)
            {
                case PanelStyle.Title:
                    float face = l.Card.Length > 0 ? titleH : 0;
                    float cw = 0;
                    if (l.Value.Length > 0)
                    {
                        cw = Math.Max(g.MeasureString(l.Value, chipFont).Width + 0.004f * h, 0.026f * h);
                        var chip = new RectangleF(x + w - pad - cw, cy + (titleH - 0.026f * h) / 2, cw, 0.026f * h);
                        using (GraphicsPath box = Pill(chip, 0.005f * h))
                            g.FillPath(ink, box);
                        g.DrawString(l.Value, chipFont, Brushes.Black, chip, middle);
                    }
                    if (face > 0)
                        DrawFace(g, l.Card, art, new RectangleF(tx, cy, face, face), l.Ink, h);
                    using (var nameInk = new SolidBrush(Ink))
                        g.DrawString(l.Text, nameFont, nameInk,
                            new RectangleF(tx + face + (face > 0 ? 0.008f * h : 0), cy,
                                inner - face - cw - 0.012f * h, titleH), midLeft);
                    break;

                case PanelStyle.Section:
                    SizeF caption = g.MeasureString(l.Text, sectionFont, int.MaxValue, clip);
                    float top = cy + sectionAir;
                    g.DrawString(l.Text, sectionFont, sectionInk,
                        new RectangleF(tx, top, inner, sectionH - sectionAir), midLeft);
                    using (var rule = new SolidBrush(Edge))
                        g.FillRectangle(rule, tx + caption.Width + 0.006f * h,
                            MathF.Round(top + (sectionH - sectionAir) / 2),
                            Math.Max(0f, inner - caption.Width - 0.006f * h), ruleH);
                    break;

                case PanelStyle.Pair:
                    g.DrawString(l.Text, labelFont, labelInk, new RectangleF(tx, cy, inner, pairH), midLeft);
                    g.DrawString(l.Value, valueFont, ink, new RectangleF(tx, cy, inner, pairH), midRight);
                    if (l.Bar >= 0)
                    {
                        float by = cy + pairH + 0.0015f * h;
                        using (GraphicsPath track = Pill(new RectangleF(tx, by, inner, barH), barH / 2))
                        using (var trackInk = new SolidBrush(Track))
                            g.FillPath(trackInk, track);
                        float fill = (float)(inner * Math.Clamp(l.Bar, 0, 1));
                        if (fill > barH)
                            using (GraphicsPath bar = Pill(new RectangleF(tx, by, fill, barH), barH / 2))
                                g.FillPath(ink, bar);
                        // Where the average of this turn sits on the meter.
                        using var mark = new SolidBrush(Color.FromArgb(150, 156, 175));
                        g.FillRectangle(mark, tx + inner / 2, by - 0.0010f * h,
                            Math.Max(1f, 0.0013f * h), barH + 0.0020f * h);
                    }
                    break;

                case PanelStyle.Tribe:
                    // How much this tribe is worth, as how far its row fills.
                    if (l.Bar > 0)
                    {
                        float room = inner - 0.048f * h;      // the value keeps its own column
                        float width = Math.Max(0.012f * h, (float)(room * Math.Clamp(l.Bar, 0, 1)));
                        using (GraphicsPath heat = Pill(new RectangleF(tx, cy, width, tribeH - 0.0020f * h), 0.004f * h))
                        using (var heatInk = new SolidBrush(Color.FromArgb(52, l.Ink)))
                            g.FillPath(heatInk, heat);
                    }
                    using (var tribeInk = new SolidBrush(Ink))
                        g.DrawString(l.Text, tribeFont, tribeInk,
                            new RectangleF(tx + 0.005f * h, cy, inner - 0.055f * h, tribeH - 0.0020f * h), midLeft);
                    g.DrawString(l.Value, valueSmall, ink,
                        new RectangleF(tx, cy, inner - 0.005f * h, tribeH - 0.0020f * h), midRight);
                    break;

                case PanelStyle.Note:
                    g.DrawString(l.Text, noteFont, ink, new RectangleF(tx, cy, inner + 1, rh + 1), flow);
                    break;

                default:
                    using (var rule = new SolidBrush(Edge))
                        g.FillRectangle(rule, tx, MathF.Round(cy + gap / 2), inner, ruleH);
                    break;
            }
            cy += rh + gap;
        }
        g.SmoothingMode = was;
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

    /// <summary>One portrait's share of the strip.</summary>
    private static float Step(int slots, float h) =>
        (float)(Settings.Current.PeekSpan * h / Math.Max(slots, 1));

    /// <summary>The middle of a leaderboard portrait, for lining a peek up with it.</summary>
    internal static float PeekRowCenter(int slot, int slots, float h) =>
        (float)(Settings.Current.PeekTop * h) + (slot + 0.5f) * Step(slots, h);

    /// <summary>
    /// The board as it stood on the table: the minions as their own portraits
    /// in a row, attack and health on them, the way the game draws it. The
    /// backdrop is solid, so the rounded art never meets the colour key.
    /// </summary>
    /// <summary>The right edge of the leaderboard strip.</summary>
    internal static float PeekRight(float h) =>
        (float)((Settings.Current.PeekX + Settings.Current.PeekWidth) * h);

    private static void DrawPeek(Graphics g, BoardPeek peek, CardArt? art, Size size, float h)
    {
        // No panel behind any of it: the portraits and the text sit straight
        // on the game, the way its own interface would.
        float oval = 0.054f * h, gap = 0.015f * h, line = 0.019f * h;
        float portrait = 0.070f * h;          // the hero, as the leaderboard shows them
        float rowH = line + oval * 1.12f + 0.010f * h;
        float total = peek.Boards.Count * rowH;
        float x = PlayLeft(size) + PeekRight(h) + 0.012f * h;
        float y = Math.Max(0.010f * h, peek.AtY - total / 2);

        using var heroFont = new Font("Segoe UI", 0.0140f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var noteFont = new Font("Segoe UI", 0.0120f * h, FontStyle.Bold, GraphicsUnit.Pixel);
        using var left = new StringFormat(StringFormat.GenericTypographic)
            { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };

        SmoothingMode was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float cy = y;
        foreach (PeekBoard board in peek.Boards)
        {
            float bench = portrait + 0.012f * h;     // room for the hero's own face
            float width = bench + Math.Max(1, board.Board.Count) * oval
                        + Math.Max(0, board.Board.Count - 1) * gap;
            SizeF hero = g.MeasureString(board.Hero, heroFont, int.MaxValue, left);

            // A soft oval backing, enough to lift the row off the table
            // without the game disappearing behind it.
            float pad = 0.016f * h;
            SizeF note = g.MeasureString(board.Note, noteFont, int.MaxValue, left);
            float back = Math.Max(width, bench + hero.Width + note.Width + 0.010f * h);
            using (GraphicsPath pill = Pill(new RectangleF(x - pad, cy - 0.006f * h,
                                                           back + 2 * pad, rowH - 0.002f * h), 0.024f * h))
            {
                using var fill = new SolidBrush(Color.FromArgb(196, 4, 5, 9));
                using var rim = new Pen(Color.FromArgb(64, 190, 200, 225), Math.Max(1f, 0.0012f * h));
                g.FillPath(fill, pill);
                g.DrawPath(rim, pill);
            }
            // The hero's own face on the row, so a board is never read against
            // the wrong player: in duos the two of a team swap places on the
            // leaderboard depending on who fights next.
            float py = cy + (rowH - portrait) / 2 - 0.002f * h;
            DrawFace(g, board.HeroCard, art, new RectangleF(x, py, portrait, portrait), board.Ink, h);

            Outlined(g, board.Hero, heroFont, board.Ink,
                new RectangleF(x + bench, cy, 0.30f * h, line), left, h);
            Outlined(g, board.Note, noteFont, Muted,
                new RectangleF(x + bench + hero.Width + 0.008f * h, cy + 0.002f * h, 0.30f * h, line), left, h);

            float bx = x + bench;
            foreach (BoardCard card in board.Board)
            {
                DrawMinion(g, card, art, new RectangleF(bx, cy + line, oval, oval * 1.12f), h);
                bx += oval + gap;
            }
            cy += rowH;
        }

        g.SmoothingMode = was;
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
        float size = digits >= 5 ? 0.0100f * h : digits >= 4 ? 0.0118f * h
                   : digits >= 3 ? 0.0145f * h : 0.0170f * h;
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
        var panel = new List<PanelLine>
        {
            PanelLine.Title("Вечная Токи", "B", TierColor("B"), "TB_BaconShop_HERO_28"),
            PanelLine.Pair("Место с этими расами", "3,95", Ink),
            PanelLine.Note("топ-4 59% · 1-е 15%", Muted),
            PanelLine.Section("Ход 8 · таверна"),
            PanelLine.Pair("Сила доски", "260", Good, 0.6),
            PanelLine.Note("121% от средней (215) · 7 существ", Muted),
            PanelLine.Pair("Бои героя на ходу", "52%", Ink),
            PanelLine.Section("Расы лобби · сдвиг места"),
            PanelLine.Tribe("Звери", "−0,21", Good, 1.0),
            PanelLine.Tribe("Драконы", "−0,12", Good, 0.57),
            PanelLine.Tribe("Нежить", "±0,01", Muted),
            PanelLine.Tribe("Пираты", "+0,09", Bad, 0.43),
            PanelLine.Tribe("Демоны", "—", Dim),
            PanelLine.Note("Firestone · все игроки", Dim),
        };
        var banner = new WinBanner(false, "64%", "ничья 9% · поражение 27%  ·  420 против 310", 0.64, 0.09, 0.27,
            "с союзником 78%  ·  730 против 310", 0.78);
        return new OverlayModel(kind, items, "Пример расположения — подгоните высоту в окне", panel, banner,
            Turn: "Ход 8");
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
        string[] deltas = { "+92", "+41", "-58", "-12" };
        var rows = Enumerable.Range(0, 4).Select(i => new HistoryRow(
            places[i].ToString(), PlaceColor(places[i], duo: true), heroCards[i], heroes[i],
            "дуэты · Нежить, Механизмы, Аберрации, Драконы, Звери",
            $"2{1 + i}:0{i}", deltas[i], deltas[i][0] == '+' ? Good : Bad,
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
        var peek = new BoardPeek(0.42f * 1080f,
        [
            new PeekBoard("2. Вечная Токи  ★6", "TB_BaconShop_HERO_28",
                "ход 13 · 15054 ат.+зд. · 5 зд.", Ink, cards),
            new PeekBoard("2. Клонист Зерек  ★5", "BG31_HERO_005",
                "ход 12 · 12754 ат.+зд. · 13 зд.", Ink, cards.Take(5).ToList()),
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
