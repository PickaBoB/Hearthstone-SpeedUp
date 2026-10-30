using System.Runtime.InteropServices;

namespace BgOverlay;

internal static class Native
{
    public const int WS_EX_TOPMOST     = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_NOACTIVATE  = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int pid);
    public const int ATTACH_PARENT_PROCESS = -1;

    // ---- per-pixel alpha ------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    public const byte AC_SRC_OVER = 0;
    public const byte AC_SRC_ALPHA = 1;
    public const int  ULW_ALPHA = 2;

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")]  public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]  public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]  public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]  public static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr screenDc, IntPtr position,
        ref SIZE size, IntPtr memoryDc, ref POINT source, int colorKey, ref BLENDFUNCTION blend, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int  biSize;
        public int  biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int  biCompression, biSizeImage;
        public int  biXPelsPerMeter, biYPelsPerMeter;
        public int  biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, int usage,
        out IntPtr bits, IntPtr section, int offset);

    /// <summary>
    /// Puts a bitmap with an alpha channel on screen as the window itself:
    /// what is transparent in it stays transparent, edges blend with whatever
    /// is behind. A colour key could do neither.
    ///
    /// The pixels go through a DIB section of our own: an HBITMAP from GDI+
    /// can come back without the alpha channel, and the window would then be
    /// drawn as nothing at all.
    /// </summary>
    public static bool ShowLayered(IntPtr hWnd, Bitmap picture)
    {
        int w = picture.Width, h = picture.Height;
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,          // top-down, the way GDI+ hands the rows over
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,      // BI_RGB
        };

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bits = CreateDIBSection(screen, ref header, 0, out IntPtr pixels, IntPtr.Zero, 0);
        bool ok = false;

        if (bits != IntPtr.Zero && pixels != IntPtr.Zero)
        {
            // UpdateLayeredWindow wants the colours multiplied by their alpha.
            using (var premultiplied = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(premultiplied))
                {
                    g.Clear(Color.Transparent);
                    g.DrawImage(picture, 0, 0, w, h);
                }
                var area = new Rectangle(0, 0, w, h);
                System.Drawing.Imaging.BitmapData data = premultiplied.LockBits(area,
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    int bytes = Math.Abs(data.Stride) * h;
                    unsafe
                    {
                        Buffer.MemoryCopy((void*)data.Scan0, (void*)pixels, bytes, bytes);
                    }
                }
                finally { premultiplied.UnlockBits(data); }
            }

            IntPtr previous = SelectObject(memory, bits);
            var size = new SIZE { cx = w, cy = h };
            var origin = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            ok = UpdateLayeredWindow(hWnd, screen, IntPtr.Zero, ref size, memory, ref origin, 0, ref blend, ULW_ALPHA);
            SelectObject(memory, previous);
        }

        if (bits != IntPtr.Zero) DeleteObject(bits);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
        return ok;
    }
}
