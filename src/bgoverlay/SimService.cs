using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BgOverlay;

/// <summary>
/// The full combat simulator: a small Node service next to the app running
/// Firestone's battle simulator, the one that plays out what the cards do —
/// deathrattles, reborn, shields, avenge, trinkets — instead of just the
/// keywords. The overlay's own quick sim answers at once and this refines the
/// figures a blink later; when the service is not there (Node missing, sim
/// folder absent), the quick answer simply stays.
///
/// The service is found at sim\server.mjs next to the exe and is started with
/// sim\node.exe when one is bundled, else with whatever node is on PATH.
/// </summary>
internal static class SimService
{
    /// <summary>How often a failed start may be retried.</summary>
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(60);

    /// <summary>One request at a time; the simulator is single-threaded.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Boards already being counted, so a 250 ms redraw asks once.</summary>
    private static readonly ConcurrentDictionary<string, byte> InFlight = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static Process? _proc;
    private static int _port;
    private static long _nextTry;
    private static int _strikes;
    private static int _started;      // 1 once a start has been kicked off
    private static int _ready;        // 1 once the service said its port

    public static bool Ready => Volatile.Read(ref _ready) == 1;

    /// <summary>
    /// Counts the fight in the background and hands the odds to
    /// <paramref name="apply"/> when they arrive. Never throws, never blocks
    /// the caller. Requests for a board already in flight are dropped.
    /// </summary>
    public static void Ask(JsonObject battle, string key, Action<CombatSim.Odds> apply)
    {
        if (!InFlight.TryAdd(key, 0)) return;
        if (InFlight.Count > 64) InFlight.Clear();
        ThreadPool.QueueUserWorkItem(async _ =>
        {
            try
            {
                CombatSim.Odds? odds = await RunRemote(battle).ConfigureAwait(false);
                if (odds is { } o) apply(o);
            }
            catch { /* the quick sim's answer stays */ }
            finally { InFlight.TryRemove(key, out byte _); }
        });
    }

    private static async Task<CombatSim.Odds?> RunRemote(JsonObject battle)
    {
        if (!await EnsureUp().ConfigureAwait(false)) return null;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var resp = await Http.PostAsync($"http://127.0.0.1:{_port}/simulate",
                new StringContent(battle.ToJsonString(), System.Text.Encoding.UTF8, "application/json"))
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using JsonDocument doc = JsonDocument.Parse(await resp.Content
                .ReadAsStringAsync().ConfigureAwait(false));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("wonPercent", out JsonElement w)
                || !root.TryGetProperty("tiedPercent", out JsonElement t)
                || !root.TryGetProperty("lostPercent", out JsonElement l))
                return null;
            Interlocked.Exchange(ref _strikes, 0);
            return new CombatSim.Odds(w.GetDouble() / 100, t.GetDouble() / 100, l.GetDouble() / 100);
        }
        catch
        {
            // The service dying mid-game is normal enough (app closing, a
            // machine under load); a few misses in a row and it gets a
            // restart after the cooldown.
            if (Interlocked.Increment(ref _strikes) >= 3)
            {
                Volatile.Write(ref _ready, 0);
                KillChild();
                Volatile.Write(ref _nextTry, Environment.TickCount64 + (long)RestartCooldown.TotalMilliseconds);
            }
            return null;
        }
        finally { Gate.Release(); }
    }

    private static async Task<bool> EnsureUp()
    {
        if (Ready) return true;
        if (Environment.TickCount64 < Volatile.Read(ref _nextTry)) return false;
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return false;
        try
        {
            int port = await Start().ConfigureAwait(false);
            if (port <= 0) throw new InvalidOperationException("sim did not report a port");
            _port = port;
            Volatile.Write(ref _ready, 1);
            return true;
        }
        catch
        {
            KillChild();
            Volatile.Write(ref _nextTry, Environment.TickCount64 + (long)RestartCooldown.TotalMilliseconds);
            return false;
        }
        finally { Volatile.Write(ref _started, 0); }
    }

    private static async Task<int> Start()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "sim");
        string script = Path.Combine(dir, "server.mjs");
        if (!File.Exists(script)) return -1;
        string node = Path.Combine(dir, "node.exe");
        if (!File.Exists(node)) node = "node";   // fall back to a system Node.js

        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = node,
                Arguments = $"\"{script}\"",
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        if (!p.Start()) return -1;

        // The service announces its port with one line; everything else is
        // drained so a full pipe never stalls it.
        Task<string?> firstLine = p.StandardOutput.ReadLineAsync();
        _ = Task.Run(async () => { try { while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is not null) { } } catch { } });
        _ = Task.Run(async () => { try { while (await p.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } } catch { } });

        Task winner = await Task.WhenAny(firstLine, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
        if (!ReferenceEquals(winner, firstLine) || await firstLine.ConfigureAwait(false) is not { } line
            || !line.StartsWith("PORT ", StringComparison.Ordinal)
            || !int.TryParse(line.AsSpan(5), out int port))
        {
            try { p.Kill(); } catch { }
            return -1;
        }

        _proc = p;
        // The service also exits on its own when our stdin handle closes,
        // which a crash does for us; this covers the clean exit.
        p.Exited += (_, _) =>
        {
            Volatile.Write(ref _ready, 0);
            Volatile.Write(ref _nextTry, Environment.TickCount64 + (long)RestartCooldown.TotalMilliseconds);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillChild();
        return port;
    }

    private static void KillChild()
    {
        try { _proc?.Kill(entireProcessTree: true); } catch { }
        _proc = null;
    }
}
