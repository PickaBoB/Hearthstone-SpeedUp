using System.Text;

namespace BgOverlay;

/// <summary>
/// Follows the newest Power.log on a background thread and feeds every line
/// to the parser. Attaching to a log replays it from the top, which brings the
/// state of a game already in progress back.
/// </summary>
internal sealed class LogReader : IDisposable
{
    public PowerLogParser Parser { get; } = new();
    public string? CurrentPath => Volatile.Read(ref _path);
    /// <summary>True while catching up on the existing part of a log.</summary>
    public bool CatchingUp => Volatile.Read(ref _catchingUp);

    private readonly Thread _thread;
    private volatile bool _stop;
    private string? _path;
    private bool _catchingUp;

    public LogReader()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Power.log reader" };
        _thread.Start();
    }

    private void Run()
    {
        StreamReader? reader = null;
        var buf = new char[1 << 16];
        var line = new StringBuilder();
        long nextLocate = 0;

        while (!_stop)
        {
            if (Environment.TickCount64 >= nextLocate)
            {
                nextLocate = Environment.TickCount64 + 2000;
                string? latest = SafeLocate();
                if (latest is not null && latest != _path)
                {
                    reader?.Dispose();
                    reader = Open(latest);
                    Volatile.Write(ref _path, reader is null ? null : latest);
                    Parser.Reset();
                    line.Clear();
                    Volatile.Write(ref _catchingUp, true);
                }
            }

            if (reader is null) { Thread.Sleep(500); continue; }

            int n;
            try { n = reader.Read(buf, 0, buf.Length); }
            catch (IOException) { reader.Dispose(); reader = null; _path = null; continue; }

            if (n == 0)
            {
                Volatile.Write(ref _catchingUp, false);
                Thread.Sleep(100);
                continue;
            }

            for (int i = 0; i < n; i++)
            {
                char c = buf[i];
                if (c == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    Parser.Feed(line.ToString());
                    line.Clear();
                }
                else line.Append(c);
            }
        }
        reader?.Dispose();
    }

    private static string? SafeLocate()
    {
        try { return LogLocator.LatestPowerLog(); }
        catch { return null; }
    }

    private static StreamReader? Open(string path)
    {
        try
        {
            // The game keeps the file open for writing.
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(fs, Encoding.UTF8);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
    }
}
