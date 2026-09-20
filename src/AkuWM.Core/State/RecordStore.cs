using System.IO.MemoryMappedFiles;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Core.State;

/// <param name="Handle">The window, as a number.</param>
/// <param name="Process">Who owns it, checked on recovery because Windows reuses handles.</param>
/// <param name="Title">What it was called, for the log line.</param>
/// <param name="At">Seconds since the epoch.</param>
/// <param name="Frame">Where it was, for the records that carry geometry.</param>
public readonly record struct StoredWindow(
    long Handle,
    string Process,
    string Title,
    long At,
    Rect Frame = default,
    bool Maximized = false,
    bool Minimized = false,
    bool Topmost = false);

/// <summary>
/// Fixed-size window records in a memory-mapped file.
/// </summary>
/// <remarks>
/// <para>
/// These records exist to survive the process dying, not the machine dying --
/// if the machine goes, every window goes with it and the record is moot. That
/// is exactly the threat model a memory-mapped file answers: the dirty pages
/// belong to the operating system, not to us, so a killed, crashed or frozen
/// process leaves them intact and the lazy writer persists them. Writing a
/// record is a few stores to memory: no serialisation, no rename, no syscall.
/// </para>
/// <para>
/// Measured on the desk before this existed: one JSON rewrite per record,
/// <strong>0.576 ms</strong>, paid once per window hidden and once per window
/// shown. A workspace switch of eight is sixteen of them -- 9.2 ms inside a
/// 5 ms budget, the largest single cost in the program.
/// </para>
/// <para>
/// One writer (the wm thread) and readers in other processes. A slot's state
/// word is written last, behind a barrier, so a reader never sees a half-built
/// slot as live.
/// </para>
/// </remarks>
public sealed class RecordStore : IDisposable
{
    private const int Magic = 0x414B5701; // AKW1
    private const int HeaderBytes = 16;
    private const int SlotBytes = 256;
    private const int ProcessChars = 32;
    private const int TitleChars = 64;
    private const int DefaultCapacity = 256;

    private const int StateOffset = 0;
    private const int HandleOffset = 8;
    private const int AtOffset = 16;
    private const int RectOffset = 24;
    private const int FlagsOffset = 40;
    private const int ProcessOffset = 48;
    private const int TitleOffset = ProcessOffset + (ProcessChars * 2);

    private readonly string _file;
    private readonly int _capacity;
    private readonly Dictionary<long, int> _slots = [];
    private readonly object _gate = new();

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private bool _broken;

    public RecordStore(string file, int capacity = DefaultCapacity)
    {
        _file = file;
        _capacity = capacity;
        Open();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _slots.Count;
            }
        }
    }

    public bool Contains(long handle)
    {
        lock (_gate)
        {
            return _slots.ContainsKey(handle);
        }
    }

    public List<StoredWindow> All()
    {
        var all = new List<StoredWindow>(_slots.Count);

        lock (_gate)
        {
            if (_view is null)
            {
                return all;
            }

            foreach (int slot in _slots.Values)
            {
                all.Add(Read(slot));
            }
        }

        return all;
    }

    /// <summary>Writes one record. Reuses the window's slot when it has one.</summary>
    public void Put(in StoredWindow record)
    {
        lock (_gate)
        {
            if (_view is null)
            {
                return;
            }

            if (!_slots.TryGetValue(record.Handle, out int slot))
            {
                slot = FreeSlot();
                if (slot < 0)
                {
                    Log.Error($"the record store is full ({_capacity}); {record.Process} was not written down");
                    return;
                }

                _slots[record.Handle] = slot;
            }

            Write(slot, record);
        }
    }

    public bool Remove(long handle)
    {
        lock (_gate)
        {
            if (_view is null || !_slots.Remove(handle, out int slot))
            {
                return false;
            }

            _view.Write(Offset(slot) + StateOffset, 0);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_view is null)
            {
                return;
            }

            foreach (int slot in _slots.Values)
            {
                _view.Write(Offset(slot) + StateOffset, 0);
            }

            _slots.Clear();
        }
    }

    /// <summary>Pushes the pages to disk. Only needed before a deliberate exit.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _view?.Flush();
        }
    }

    private void Open()
    {
        long length = HeaderBytes + ((long)_capacity * SlotBytes);

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_file));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            bool fresh = !File.Exists(_file) || new FileInfo(_file).Length != length;

            // Shared, because the point of these records is that a SECOND
            // process reads them: `akuwm rescue` while the daemon is alive, or
            // the next daemon after it died. The default is exclusive, and it
            // made rescue open nothing and report nothing hidden.
            var stream = new FileStream(
                _file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

            if (stream.Length != length)
            {
                stream.SetLength(length);
            }

            _map = MemoryMappedFile.CreateFromFile(
                stream, null, length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            _view = _map.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);

            if (fresh || _view.ReadInt32(0) != Magic)
            {
                for (int slot = 0; slot < _capacity; slot++)
                {
                    _view.Write(Offset(slot) + StateOffset, 0);
                }

                _view.Write(0, Magic);
                _view.Write(4, 1);
                _view.Write(8, _capacity);
                return;
            }

            for (int slot = 0; slot < _capacity; slot++)
            {
                if (_view.ReadInt32(Offset(slot) + StateOffset) == 1)
                {
                    _slots[_view.ReadInt64(Offset(slot) + HandleOffset)] = slot;
                }
            }
        }
        catch (Exception ex)
        {
            // A store that cannot be opened must never be the reason AkuWM
            // refuses to start: its only job is to help.
            _broken = true;
            Log.Error($"the record store {_file} could not be opened: {ex.Message}");
            _view = null;
            _map = null;
        }
    }

    private int FreeSlot()
    {
        for (int slot = 0; slot < _capacity; slot++)
        {
            if (_view!.ReadInt32(Offset(slot) + StateOffset) == 0)
            {
                return slot;
            }
        }

        return -1;
    }

    private static long Offset(int slot) => HeaderBytes + ((long)slot * SlotBytes);

    private void Write(int slot, in StoredWindow record)
    {
        long at = Offset(slot);

        _view!.Write(at + HandleOffset, record.Handle);
        _view.Write(at + AtOffset, record.At);
        _view.Write(at + RectOffset, record.Frame.X);
        _view.Write(at + RectOffset + 4, record.Frame.Y);
        _view.Write(at + RectOffset + 8, record.Frame.Width);
        _view.Write(at + RectOffset + 12, record.Frame.Height);
        _view.Write(at + FlagsOffset, (byte)(
            (record.Maximized ? 1 : 0) | (record.Minimized ? 2 : 0) | (record.Topmost ? 4 : 0)));

        WriteText(at + ProcessOffset, record.Process, ProcessChars);
        WriteText(at + TitleOffset, record.Title, TitleChars);

        // Last, and behind a barrier: a reader in another process must never
        // take a half-built slot for a live one.
        Thread.MemoryBarrier();
        _view.Write(at + StateOffset, 1);
    }

    private StoredWindow Read(int slot)
    {
        long at = Offset(slot);
        byte flags = _view!.ReadByte(at + FlagsOffset);

        return new StoredWindow(
            _view.ReadInt64(at + HandleOffset),
            ReadText(at + ProcessOffset, ProcessChars),
            ReadText(at + TitleOffset, TitleChars),
            _view.ReadInt64(at + AtOffset),
            new Rect(
                _view.ReadInt32(at + RectOffset),
                _view.ReadInt32(at + RectOffset + 4),
                _view.ReadInt32(at + RectOffset + 8),
                _view.ReadInt32(at + RectOffset + 12)),
            (flags & 1) != 0,
            (flags & 2) != 0,
            (flags & 4) != 0);
    }

    private void WriteText(long at, string text, int max)
    {
        int length = Math.Min(text.Length, max - 1);
        _view!.Write(at, (char)length);

        for (int i = 0; i < length; i++)
        {
            _view.Write(at + 2 + (i * 2), text[i]);
        }
    }

    private string ReadText(long at, int max)
    {
        int length = Math.Min(_view!.ReadChar(at), max - 1);
        if (length <= 0)
        {
            return string.Empty;
        }

        return string.Create(length, (_view, at), static (span, state) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = state._view.ReadChar(state.at + 2 + (i * 2));
            }
        });
    }

    /// <summary>True when the store could not be opened and is doing nothing.</summary>
    public bool Broken => _broken;

    public void Dispose()
    {
        lock (_gate)
        {
            _view?.Flush();
            _view?.Dispose();
            _map?.Dispose();
            _view = null;
            _map = null;
        }
    }
}
