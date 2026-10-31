using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BgOverlay;

/// <summary>
/// The row of speed buttons over the bottom right corner of the game:
/// x1 · x2 · x3 · x8, the picked one lit. Unlike the main overlay this
/// window is NOT click-through — the whole point is that it takes the
/// click — but it is still a layered window, so the transparent pixels
/// around the pills let the game's own clicks through.
///
/// It is kept apart from OverlayForm rather than made a hole in its
/// click-through style: toggling WS_EX_TRANSPARENT under a moving cursor
/// races the click, and the panel and banner areas would swallow game
/// clicks whenever it stuck.
/// </summary>
internal sealed class SpeedStripForm : Form
{
    /// <summary>The same presets the main window offers.</summary>
    public static readonly double[] Presets = { 1, 2, 3, 8 };

    /// <summary>A button was clicked: the multiplier it stands for.</summary>
    public event Action<double>? SpeedPicked;

    private double _current = 1.0;
    private int _hover = -1;
    private RectangleF[] _boxes = Array.Empty<RectangleF>();

    private static readonly Color Ink   = Color.FromArgb(236, 238, 245);
    private static readonly Color Back  = Color.FromArgb(236, 12, 13, 19);
    private static readonly Color Band  = Color.FromArgb(240, 32, 34, 46);
    private static readonly Color Frame = Color.FromArgb(74, 80, 100);
    private static readonly Color On    = Color.FromArgb(255, 196, 70);

    public SpeedStripForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Opaque, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // No WS_EX_TRANSPARENT here: this one is meant to be clicked.
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                        | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>
    /// Lays the strip over the bottom right of the game's play area and
    /// redraws it when anything it shows has moved. An empty rect hides it.
    /// </summary>
    public void Present(Rectangle gameClient, double current)
    {
        if (gameClient.Width < 100 || gameClient.Height < 100)
        {
            if (Visible) Hide();
            _hover = -1;
            return;
        }

        float h = gameClient.Height;
        var size = StripSize(h);
        // The game centres a 16:9 play box on a wider window; hug its corner,
        // not the corner of empty side bands.
        float playWidth = Math.Min(gameClient.Width, h * 16f / 9f);
        float playLeft = Math.Max(0f, (gameClient.Width - playWidth) / 2f);
        // The bottom margin is a setting: the strip has to clear whatever the
        // game itself draws in that corner, and that differs a little per layout.
        float margin = Math.Max(0f, (float)Settings.Current.SpeedButtonsY * h);
        var at = new Point(
            gameClient.X + (int)MathF.Round(playLeft + playWidth - margin - size.Width),
            gameClient.Y + (int)MathF.Round(h - margin - size.Height));

        bool moved = Bounds != new Rectangle(at, size);
        if (moved) Bounds = new Rectangle(at, size);
        bool changed = moved || Math.Abs(current - _current) > 0.001;
        _current = current;
        if (changed) Render();
        if (!Visible) Show();
    }

    /// <summary>The strip's pixel size at a game window of this height.</summary>
    private static Size StripSize(float h)
    {
        float pad = 0.006f * h, gap = 0.005f * h, bh = 0.034f * h, bw = 0.048f * h;
        return new Size((int)MathF.Round(2 * pad + Presets.Length * bw + (Presets.Length - 1) * gap),
                        (int)MathF.Round(bh + 2 * pad));
    }

    /// <summary>The button boxes for a strip of this size. Mirrors the drawing.</summary>
    private static RectangleF[] Boxes(Size size)
    {
        float pad = 0.130f * size.Height;
        float bh = size.Height - 2 * pad;
        float bw = 1.41f * bh;
        float gap = (size.Width - 2 * pad - Presets.Length * bw) / (Presets.Length - 1);
        var boxes = new RectangleF[Presets.Length];
        float x = pad;
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = new RectangleF(x, pad, bw, bh);
            x += bw + gap;
        }
        return boxes;
    }

    private void Render()
    {
        if (Width <= 0 || Height <= 0) return;
        using var picture = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(picture))
        {
            g.Clear(Color.Transparent);
            Draw(g, ClientSize, _current, _hover, out _boxes);
        }
        Native.ShowLayered(Handle, picture);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    /// <summary>
    /// The strip on any surface of its size: one pill per preset, the
    /// current one lit, the hovered one lifted a shade.
    /// </summary>
    internal static void Draw(Graphics g, Size size, double current, int hover,
        out RectangleF[] boxes)
    {
        boxes = Boxes(size);
        float h = size.Height;
        float radius = 0.24f * boxes[0].Height;

        using var font = new Font("Segoe UI", 0.46f * boxes[0].Height, FontStyle.Bold, GraphicsUnit.Pixel);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var rim = new Pen(Frame, Math.Max(1f, 0.04f * h));

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        for (int i = 0; i < boxes.Length; i++)
        {
            bool on = Math.Abs(Presets[i] - current) < 0.001;
            using GraphicsPath pill = Pill(boxes[i], radius);
            using (var fill = new SolidBrush(on ? On : i == hover ? Band : Back))
                g.FillPath(fill, pill);
            g.DrawPath(rim, pill);
            using var ink = new SolidBrush(on ? Color.Black : Ink);
            g.DrawString($"x{Presets[i]:0}", font, ink, boxes[i], center);
        }
    }

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

    private int Hit(Point at)
    {
        for (int i = 0; i < _boxes.Length; i++)
            if (_boxes[i].Contains(at)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int i = Hit(e.Location);
        if (i < 0) return;
        // Light the button at once; the main window's answer lands on the
        // next tick and only confirms what is already drawn.
        _current = Presets[i];
        Render();
        SpeedPicked?.Invoke(Presets[i]);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = Hit(e.Location);
        if (i == _hover) return;
        _hover = i;
        Render();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        Render();
    }
}
