using System.IO.MemoryMappedFiles;

namespace BgOverlay;

/// <summary>
/// Battlegrounds rating and lobby races. Hearthstone writes neither to its
/// log, so gsunity reads them from the client and publishes a shared section.
/// </summary>
internal sealed class RatingReader : IDisposable
{
    private const uint Magic = 0x54524742u;   // "BGRT"
    private const int MaxRaces = 8;
    private const int Size = 24 + MaxRaces * 4;

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private int[] _races = Array.Empty<int>();
    private int  _pid;
    private long _nextTry;

    public int  Solo { get; private set; }
    public int  Duo { get; private set; }
    public bool Known => Solo > 0 || Duo > 0;
    public IReadOnlyList<int> Races => _races;
    public int RacesVersion { get; private set; }

    public int Of(bool duo) => duo ? Duo : Solo;

    public void Poll(int pid)
    {
        if (pid == 0) { Close(); return; }
        if (pid != _pid) { Close(); _pid = pid; }

        if (_view is null)
        {
            // The module creates the section once it has read the cache.
            if (Environment.TickCount64 < _nextTry) return;
            _nextTry = Environment.TickCount64 + 3000;
            try
            {
                _file = MemoryMappedFile.OpenExisting($"Local\\BgOverlayRating_{pid}", MemoryMappedFileRights.Read);
                _view = _file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.Read);
            }
            catch { Close(); return; }
        }

        try
        {
            if (_view!.ReadUInt32(0) != Magic) return;
            Solo = _view.ReadInt32(8);
            Duo  = _view.ReadInt32(12);
            uint version = _view.ReadUInt32(4);
            int count = version >= 2 ? _view.ReadInt32(20) : 0;
            if (count < 0 || count > MaxRaces) return;
            var races = new int[count];
            for (int i = 0; i < count; i++) races[i] = _view.ReadInt32(24 + i * 4);
            ApplyRaces(races);
        }
        catch { Close(); }
    }

    private void ApplyRaces(int[] races)
    {
        if (_races.SequenceEqual(races)) return;
        _races = races;
        RacesVersion++;
    }

    private void Close()
    {
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
        _pid = 0;
        ApplyRaces(Array.Empty<int>());
    }

    public void Dispose() => Close();
}
