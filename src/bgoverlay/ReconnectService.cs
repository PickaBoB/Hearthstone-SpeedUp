using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BgOverlay;

internal sealed class ReconnectService : IDisposable
{
    private const string OutRule = "Hearthstone SpeedUp reconnect (out)";
    private const string InRule = "Hearthstone SpeedUp reconnect (in)";
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int AccessDenied = unchecked((int)0x80070005);

    private Task _pending = Task.CompletedTask;

    public bool Busy { get; private set; }

    public Task ReconnectAsync(int processId)
    {
        if (Busy) return _pending;
        Busy = true;
        _pending = Task.Run(() => Run(processId));
        return _pending;
    }

    private void Run(int processId)
    {
        dynamic? policy = null;
        bool added = false;
        try
        {
            string executable = GamePath(processId);
            var server = GameServer();
            policy = Create("HNetCfg.FwPolicy2");
            RemoveRule(policy, OutRule);
            RemoveRule(policy, InRule);
            AddRule(policy, OutRule, executable, 2, server);
            added = true;
            AddRule(policy, InRule, executable, 1, server);
            TcpReset.ResetProcessConnections(processId, server.Address, server.Port);
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            throw new UnauthorizedAccessException(
                "Для автореконнекта запустите Hearthstone SpeedUp от администратора.", ex);
        }
        finally
        {
            Exception? cleanup = null;
            if (policy is not null)
            {
                if (added)
                {
                    try { RemoveRule(policy, OutRule); }
                    catch (Exception ex) { cleanup = ex; }
                    try { RemoveRule(policy, InRule); }
                    catch (Exception ex) { cleanup ??= ex; }
                }
                Marshal.FinalReleaseComObject(policy);
            }
            Busy = false;
            if (cleanup is not null)
                throw new InvalidOperationException(
                    "Не удалось снять временную блокировку Hearthstone в брандмауэре Windows.", cleanup);
        }
    }

    public void Dispose()
    {
        try { _pending.GetAwaiter().GetResult(); }
        catch { }
    }

    private static string GamePath(int processId)
    {
        if (processId <= 0)
            throw new InvalidOperationException("Hearthstone ещё не найден.");
        using var game = Process.GetProcessById(processId);
        return game.MainModule?.FileName
            ?? throw new InvalidOperationException("Не удалось определить путь к Hearthstone.exe.");
    }

    private static (uint Address, uint Port, string Ip, string PortText) GameServer()
    {
        string? power = LogLocator.LatestPowerLog()
            ?? throw new InvalidOperationException("Не найден лог текущего запуска Hearthstone.");
        string path = Path.Combine(Path.GetDirectoryName(power)!, "GameNetLogger.log");
        string? endpoint = null;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is string line)
            {
                int marker = line.IndexOf("Network.GotoGameServe()", StringComparison.Ordinal);
                if (marker < 0) continue;
                int at = line.IndexOf("address=", marker, StringComparison.Ordinal);
                if (at < 0) continue;
                string value = line[(at + 8)..].TrimStart();
                if (value.Length > 0) endpoint = value.Split(' ', ',')[0];
            }
        }
        if (endpoint is null)
            throw new InvalidOperationException("Адрес игрового сервера ещё не появился в GameNetLogger.log.");

        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !System.Net.IPAddress.TryParse(endpoint[..colon], out var address)
            || !int.TryParse(endpoint[(colon + 1)..], out int port) || port is <= 0 or > 65535)
            throw new InvalidOperationException("Не удалось разобрать адрес игрового сервера.");
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            throw new InvalidOperationException("IPv6 игрового сервера пока не поддерживается.");
        uint networkPort = (uint)((port >> 8) | ((port & 0xff) << 8));
        return (BitConverter.ToUInt32(bytes, 0), networkPort, address.ToString(), port.ToString());
    }

    private static dynamic Create(string progId)
    {
        Type type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException("Служба брандмауэра Windows недоступна.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Не удалось открыть брандмауэр Windows.");
    }

    private static void AddRule(dynamic policy, string name, string executable, int direction,
        (uint Address, uint Port, string Ip, string PortText) server)
    {
        dynamic rule = Create("HNetCfg.FwRule");
        try
        {
            rule.Name = name;
            rule.Description = "Temporary Hearthstone reconnect; removed automatically after one second.";
            rule.ApplicationName = executable;
            rule.Direction = direction;
            rule.Action = 0;
            rule.Protocol = 6;
            rule.RemoteAddresses = server.Ip;
            rule.RemotePorts = server.PortText;
            rule.Enabled = true;
            rule.Profiles = int.MaxValue;
            policy.Rules.Add(rule);
        }
        finally
        {
            Marshal.FinalReleaseComObject(rule);
        }
    }

    private static void RemoveRule(dynamic policy, string name)
    {
        try { policy.Rules.Remove(name); }
        catch (COMException ex) when (ex.ErrorCode == FileNotFound) { }
    }

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || ex is COMException { ErrorCode: AccessDenied };
}
