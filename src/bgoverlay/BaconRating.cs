using System.IO.MemoryMappedFiles;

namespace BgOverlay;

/// <summary>
/// The Battlegrounds rating. Hearthstone writes it to no log, so it comes
/// from the game's own account cache, read inside the process by the
/// speed-up module (gsunity) and put in a small shared section. Without that
/// module in the game there is nothing to read, and the overlay simply shows
/// no rating.
/// </summary>
internal sealed class RatingReader : IDisposable
{
    private const uint Magic = 0x54524742u;   // "BGRT"
    private const int  Size = 20;

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private int  _pid;
    private long _nextTry;

    public int  Solo { get; private set; }
    public int  Duo { get; private set; }
    public bool Known => Solo > 0 || Duo > 0;

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
        }
        catch { Close(); }
    }

    private void Close()
    {
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
        _pid = 0;
    }

    public void Dispose() => Close();
}
