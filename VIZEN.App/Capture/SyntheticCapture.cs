using System.Diagnostics;

namespace VIZEN.App.Capture;

/// <summary>
/// Benchmark source that produces frames itself at an exact rate (no Windows capture involved): a moving pattern
/// with a frame-number marker. It exercises VIZEN's own processing and display path only, so a good result here
/// says nothing about real window capture — the Performance page labels it accordingly.
/// </summary>
public sealed class SyntheticCapture : LiveCapture
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly Thread _thread;
    private volatile bool _stop;

    public SyntheticCapture(object key, int width, int height, int fps)
        : base(key, 0)
    {
        _width = width;
        _height = height;
        _fps = Math.Clamp(fps, 1, 240);
        HasFrameMarker = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "VIZEN synthetic source", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public override string PipelineDescription =>
        "Synthetic frames (no Windows capture) → triple buffer → WPF WriteableBitmap";

    private void Run()
    {
        var period = Stopwatch.Frequency / (double)_fps;
        var start = Stopwatch.GetTimestamp();
        var stride = _width * 4;
        for (long n = 0; !_stop; n++)
        {
            // Wait for the exact due time of frame n: sleep most of it, then spin the last millisecond.
            var due = start + (long)(n * period);
            while (!_stop)
            {
                var left = (due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                if (left <= 0)
                {
                    break;
                }

                if (left > 2)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.SpinWait(200);
                }
            }

            if (_stop || IsDisposed)
            {
                return;
            }

            if (!TryBeginFrame())
            {
                continue;
            }

            var frame = (uint)n;
            WriteFrame(_width, _height, stride, (buffer, _) => Draw(buffer, stride, frame), Stopwatch.GetTimestamp());
        }
    }

    private void Draw(byte[] buffer, int stride, uint frame)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, stride * _height));
        var background = 0xFF000000u | ((uint)(20 + frame % 40) << 16) | (24u << 8) | (uint)(40 + frame % 60);
        pixels.Fill(background);

        // A vertical bar moving 12 px per frame and a block circling the centre: stutter is easy to see.
        var barX = (int)(frame * 12 % (uint)_width);
        var barW = Math.Max(4, _width / 50);
        for (var y = 0; y < _height; y++)
        {
            pixels.Slice(y * _width + barX, Math.Min(barW, _width - barX)).Fill(0xFFFFFFFFu);
        }

        var size = _height / 7;
        var cx = (int)(_width / 2 + Math.Cos(frame / 20.0) * _width * 0.35) - size / 2;
        var cy = (int)(_height / 2 + Math.Sin(frame / 13.0) * _height * 0.30) - size / 2;
        for (var y = Math.Max(0, cy); y < Math.Min(_height, cy + size); y++)
        {
            var x0 = Math.Max(0, cx);
            var x1 = Math.Min(_width, cx + size);
            if (x1 > x0)
            {
                pixels.Slice(y * _width + x0, x1 - x0).Fill(0xFF4FC3F7u);
            }
        }

        FrameMarker.Encode(buffer, _width, _height, stride, frame, Math.Max(4, _height / 90));
    }

    protected override void ReleaseResources() => _stop = true;
}
