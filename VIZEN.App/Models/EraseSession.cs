using System.Windows;

namespace VIZEN.App.Models;

/// <summary>
/// A per-frame callback source (WPF: <c>CompositionTarget.Rendering</c>, raised once per WPF rendering pass; that is
/// usually, not always, once per display refresh). Abstracted so tests can drive frames at any rate.
/// </summary>
public interface IFrameClock
{
    void Subscribe(EventHandler handler);
    void Unsubscribe(EventHandler handler);
}

/// <summary>
/// Frame-synchronized processing of one eraser drag. Mouse positions are queued as they arrive (a gaming mouse may
/// send hundreds per second) and the whole path since the last frame is erased once per rendering pass, segment by
/// segment, so no movement is skipped and nothing is drawn more often than the screen can show. The frame callback
/// is subscribed only while positions are waiting and is removed as soon as the queue is empty or the drag ends.
/// </summary>
public sealed class EraseSession : IDisposable
{
    private readonly EraseGesture _gesture;
    private readonly IFrameClock _clock;
    private readonly List<Point> _pending = new(64);
    private readonly EventHandler _onFrame;
    private Point _last;
    private bool _subscribed;
    private bool _finished;

    public EraseSession(EraseGesture gesture, Point start, double radius, IFrameClock clock)
    {
        _gesture = gesture;
        _clock = clock;
        _last = start;
        Radius = radius;
        _onFrame = (_, _) => ProcessPending();
    }

    /// <summary>Erases under the circle where the button was pressed (a click without moving still erases).</summary>
    public void Start()
    {
        _gesture.Apply(_last, _last, Radius);
        Publish();
    }

    /// <summary>Eraser radius in pixels.</summary>
    public double Radius { get; }

    public EraseGesture Gesture => _gesture;

    /// <summary>True while the frame callback is subscribed (tests and diagnostics).</summary>
    public bool IsSubscribed => _subscribed;

    /// <summary>Rendering passes in which queued positions were processed.</summary>
    public int FramesProcessed { get; private set; }

    /// <summary>Mouse positions received and processed.</summary>
    public int PositionsProcessed { get; private set; }

    /// <summary>Raised (UI thread) with what changed in a frame, for a localized redraw of the preview.</summary>
    public event Action<EraseFrame>? FrameReady;

    /// <summary>Queues a mouse position (pixels); it is erased on the next rendering pass.</summary>
    public void MoveTo(Point p)
    {
        if (_finished)
        {
            return;
        }

        if (_pending.Count == 0 && p == _last)
        {
            return;
        }

        _pending.Add(p);
        if (!_subscribed)
        {
            _clock.Subscribe(_onFrame);
            _subscribed = true;
        }
    }

    /// <summary>
    /// Ends the drag: erases what is still queued, stops the frame callback and, when <paramref name="commit"/> is
    /// true, applies the drag to the document as one undo step. Safe to call more than once.
    /// </summary>
    public bool Finish(DrawingDocument document, bool commit)
    {
        if (_finished)
        {
            return false;
        }

        ProcessPending();
        Unsubscribe();
        _finished = true;
        return commit && _gesture.Commit(document);
    }

    public void Dispose()
    {
        Unsubscribe();
        _finished = true;
    }

    private void ProcessPending()
    {
        if (_pending.Count == 0)
        {
            Unsubscribe(); // the mouse is still: no callback until it moves again
            return;
        }

        foreach (var p in _pending)
        {
            _gesture.Apply(_last, p, Radius);
            _last = p;
        }

        PositionsProcessed += _pending.Count;
        _pending.Clear();
        FramesProcessed++;
        Publish();
    }

    private void Publish()
    {
        var frame = _gesture.TakeFrame();
        if (!frame.IsEmpty)
        {
            FrameReady?.Invoke(frame);
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            _clock.Unsubscribe(_onFrame);
            _subscribed = false;
        }
    }
}

/// <summary>Coordinates of the drawing surfaces: DIPs on the window ↔ physical pixels on the monitor.</summary>
public static class DrawCoordinates
{
    /// <summary>A position in window DIPs → monitor pixels (the window starts at the work area, <paramref name="workOffset"/> pixels into the monitor).</summary>
    public static Point ToMonitor(Point dip, double dpiScale, Vector workOffset) => new(dip.X * dpiScale + workOffset.X, dip.Y * dpiScale + workOffset.Y);

    /// <summary>Eraser radius in monitor pixels for a diameter in DIPs: the circle drawn in DIPs covers exactly this.</summary>
    public static double EraserRadius(double diameterDip, double dpiScale) => diameterDip / 2 * dpiScale;
}
