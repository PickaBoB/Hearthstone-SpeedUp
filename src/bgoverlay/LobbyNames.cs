using System.IO.MemoryMappedFiles;
using System.Text;

namespace BgOverlay;

/// <summary>Display names of everyone in the Battlegrounds lobby, read by gsunity.</summary>
internal sealed class LobbyNamesReader : IDisposable
{
    private const uint Magic = 0x4d4e4742u;   // "BGNM"
    private const int MaxPlayers = 16;
    private const int NameChars = 64;
    private const int EntrySize = 4 + NameChars * 2;
    private const int EntriesOffset = 12;
    private const int EntriesSize = MaxPlayers * EntrySize;
    private const int Size = EntriesOffset + EntriesSize;

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private Dictionary<int, string> _names = new();
    private int _pid;
    private long _nextTry;

    public int Version { get; private set; }

    public string? Of(int playerId) => _names.TryGetValue(playerId, out string? name) ? name : null;

    public void Poll(int pid)
    {
        if (pid == 0) { Close(); return; }
        if (pid != _pid) { Close(); _pid = pid; }

        if (_view is null)
        {
            if (Environment.TickCount64 < _nextTry) return;
            _nextTry = Environment.TickCount64 + 3000;
            try
            {
                _file = MemoryMappedFile.OpenExisting($"Local\\BgOverlayLobbyNames_{pid}", MemoryMappedFileRights.Read);
                _view = _file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.Read);
            }
            catch { Close(); return; }
        }

        try
        {
            if (_view!.ReadUInt32(0) != Magic || _view.ReadUInt32(4) != 1) return;
            var raw = new byte[EntriesSize];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint before = _view.ReadUInt32(8);
                if ((before & 1) != 0) continue;
                _view.ReadArray(EntriesOffset, raw, 0, raw.Length);
                uint after = _view.ReadUInt32(8);
                if (before != after || (after & 1) != 0) continue;
                Apply(ReadNames(raw));
                return;
            }
        }
        catch { Close(); }
    }

    private static Dictionary<int, string> ReadNames(byte[] raw)
    {
        var names = new Dictionary<int, string>();
        for (int i = 0; i < MaxPlayers; i++)
        {
            int at = i * EntrySize;
            int playerId = BitConverter.ToInt32(raw, at);
            if (playerId <= 0) continue;
            string name = Encoding.Unicode.GetString(raw, at + 4, NameChars * 2);
            int end = name.IndexOf('\0');
            if (end >= 0) name = name.Substring(0, end);
            if (name.Length > 0) names[playerId] = name;
        }
        return names;
    }

    private void Apply(Dictionary<int, string> names)
    {
        if (_names.Count == names.Count && _names.All(p => names.TryGetValue(p.Key, out string? n) && n == p.Value))
            return;
        _names = names;
        Version++;
    }

    private void Close()
    {
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
        _pid = 0;
        _nextTry = 0;
        if (_names.Count > 0)
        {
            _names = new Dictionary<int, string>();
            Version++;
        }
    }

    public void Dispose() => Close();
}
