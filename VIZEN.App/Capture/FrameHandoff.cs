namespace VIZEN.App.Capture;

/// <summary>One CPU copy of a BGRA frame.</summary>
public sealed class FrameSlot
{
    public byte[] Data { get; private set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public int Stride { get; set; }

    /// <summary>QPC ticks when VIZEN received the frame from Windows (0 = unknown).</summary>
    public long SourceTime { get; set; }

    /// <summary>QPC ticks when the frame was handed to the UI thread.</summary>
    public long QueuedAt { get; set; }

    public void Ensure(int length)
    {
        if (Data.Length < length)
        {
            Data = new byte[length];
        }
    }
}

/// <summary>
/// Triple buffer between one producer (capture thread) and one consumer (UI thread). The producer fills
/// <see cref="WriteSlot"/> without any lock and publishes it with a pointer swap; the consumer takes the newest
/// published frame with another swap and reads it without a lock. Neither side ever waits for the other's copy,
/// and only the newest frame is kept (an unshown frame is replaced, never queued).
/// </summary>
public sealed class FrameHandoff
{
    private readonly object _gate = new();
    private FrameSlot _write = new();
    private FrameSlot _ready = new();
    private FrameSlot _present = new();
    private bool _pending;

    /// <summary>Producer only: the slot to fill before calling <see cref="Publish"/>.</summary>
    public FrameSlot WriteSlot => _write;

    /// <summary>Producer: makes the filled write slot the newest frame. Returns true if it replaced an unshown frame.</summary>
    public bool Publish(long now)
    {
        lock (_gate)
        {
            (_write, _ready) = (_ready, _write);
            _ready.QueuedAt = now;
            var replaced = _pending;
            _pending = true;
            return replaced;
        }
    }

    /// <summary>
    /// Producer: drops the CPU buffers when no one needs CPU frames any more (they are allocated again on the next
    /// frame). A slot the consumer is still reading stays alive until it is done with it.
    /// </summary>
    public void Trim()
    {
        lock (_gate)
        {
            _write = new FrameSlot();
            _present = new FrameSlot();
            if (!_pending)
            {
                _ready = new FrameSlot();
            }
        }
    }

    /// <summary>Consumer: the newest published frame, or null when nothing new arrived since the last call.</summary>
    public FrameSlot? TakeLatest()
    {
        lock (_gate)
        {
            if (!_pending)
            {
                return null;
            }

            (_ready, _present) = (_present, _ready);
            _pending = false;
            return _present;
        }
    }
}
