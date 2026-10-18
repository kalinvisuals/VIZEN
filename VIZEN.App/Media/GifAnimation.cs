using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VIZEN.App.Localization;

namespace VIZEN.App.Media;

/// <summary>
/// An animated GIF decoded into full, composed frames (WPF's Image control only shows the first frame).
/// Frames honour each frame's offset, delay and disposal method; transparency is preserved.
/// </summary>
public sealed class GifAnimation
{
    /// <summary>Refuse GIFs whose decoded frames would need more memory than this.</summary>
    public const long MaxDecodedBytes = 768L * 1024 * 1024;

    // Browsers treat 0–1 hundredths of a second as "as fast as possible" and show such frames for 100 ms.
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(100);

    private GifAnimation(IReadOnlyList<BitmapSource> frames, IReadOnlyList<TimeSpan> delays, int width, int height)
    {
        Frames = frames;
        Delays = delays;
        PixelWidth = width;
        PixelHeight = height;
        TotalDuration = TimeSpan.FromTicks(delays.Sum(d => d.Ticks));
    }

    public IReadOnlyList<BitmapSource> Frames { get; }
    public IReadOnlyList<TimeSpan> Delays { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public TimeSpan TotalDuration { get; }

    /// <summary>Decodes on a background STA thread (rendering needs one); frames are frozen and usable anywhere.</summary>
    public static Task<GifAnimation> LoadAsync(string path)
    {
        var completion = new TaskCompletionSource<GifAnimation>();
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Load(path));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "VIZEN GIF decoder",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>Decodes on the calling thread, which must be STA. Throws <see cref="MediaLoadException"/>.</summary>
    public static GifAnimation Load(string path)
    {
        GifBitmapDecoder decoder;
        try
        {
            using var stream = File.OpenRead(path);
            decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new MediaLoadException(Loc.F("Media.FileMissing", path), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileFormatException
            or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            throw new MediaLoadException(Loc.F("Media.ReadFailed", Path.GetFileName(path), ex.Message), ex);
        }

        if (decoder.Frames.Count == 0)
        {
            throw new MediaLoadException(Loc.F("Media.ReadFailed", Path.GetFileName(path), "0 frames"));
        }

        var first = decoder.Frames[0];
        var width = Query(decoder.Metadata, "/logscrdesc/Width", first.PixelWidth);
        var height = Query(decoder.Metadata, "/logscrdesc/Height", first.PixelHeight);
        if (width <= 0 || height <= 0)
        {
            (width, height) = (first.PixelWidth, first.PixelHeight);
        }

        if ((long)width * height * 4 * decoder.Frames.Count > MaxDecodedBytes)
        {
            throw new MediaLoadException(Loc.F("Media.GifTooLarge", Path.GetFileName(path), decoder.Frames.Count, width, height));
        }

        var full = new Rect(0, 0, width, height);
        var frames = new List<BitmapSource>(decoder.Frames.Count);
        var delays = new List<TimeSpan>(decoder.Frames.Count);
        BitmapSource? canvas = null; // what the next frame is drawn over (null = transparent)

        foreach (var frame in decoder.Frames)
        {
            var metadata = frame.Metadata as BitmapMetadata;
            var frameRect = new Rect(
                Query(metadata, "/imgdesc/Left", 0), Query(metadata, "/imgdesc/Top", 0),
                frame.PixelWidth, frame.PixelHeight);
            var delay = Query(metadata, "/grctlext/Delay", 10);
            var disposal = Query(metadata, "/grctlext/Disposal", 0);

            var background = canvas;
            var composed = Render(width, height, dc =>
            {
                if (background is not null)
                {
                    dc.DrawImage(background, full);
                }

                dc.DrawImage(frame, frameRect);
            });
            frames.Add(composed);
            delays.Add(delay <= 1 ? DefaultDelay : TimeSpan.FromMilliseconds(delay * 10));

            canvas = disposal switch
            {
                // Restore to background: clear this frame's area.
                2 => Render(width, height, dc =>
                {
                    dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(frameRect)));
                    dc.DrawImage(composed, full);
                    dc.Pop();
                }),

                // Restore to previous: the next frame is drawn over what was there before this one.
                3 => background,

                // Unspecified / do not dispose: keep the composed frame.
                _ => composed,
            };
        }

        return new GifAnimation(frames, delays, width, height);
    }

    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            draw(dc);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static int Query(BitmapMetadata? metadata, string query, int fallback)
    {
        try
        {
            return metadata?.GetQuery(query) is { } value ? Convert.ToInt32(value) : fallback;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException
            or FormatException or InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            return fallback;
        }
    }
}

/// <summary>Plays a <see cref="GifAnimation"/> in an <see cref="Image"/> using each frame's own delay.</summary>
public sealed class GifPlayer
{
    private readonly Image _image;
    private readonly DispatcherTimer _timer;
    private GifAnimation? _animation;
    private int _index;
    private bool _loop;

    public GifPlayer(Image image)
    {
        _image = image;
        _timer = new DispatcherTimer(DispatcherPriority.Render, image.Dispatcher);
        _timer.Tick += OnTick;
    }

    /// <summary>Raised when a non-looping animation reaches its last frame.</summary>
    public event EventHandler? Ended;

    public bool IsPlaying => _timer.IsEnabled;

    public void Play(GifAnimation animation, bool loop)
    {
        _animation = animation;
        _loop = loop;
        _index = 0;
        ShowCurrent();
    }

    /// <summary>Continues from the current frame.</summary>
    public void Resume(bool loop)
    {
        _loop = loop;
        if (_animation is { Frames.Count: > 1 })
        {
            _timer.Start();
        }
    }

    /// <summary>Changes looping without restarting.</summary>
    public void SetLoop(bool loop) => _loop = loop;

    public void Stop() => _timer.Stop();

    private void ShowCurrent()
    {
        if (_animation is null)
        {
            return;
        }

        _image.Source = _animation.Frames[_index];
        _timer.Interval = _animation.Delays[_index];
        if (_animation.Frames.Count > 1)
        {
            _timer.Start();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_animation is null)
        {
            _timer.Stop();
            return;
        }

        if (_index + 1 >= _animation.Frames.Count)
        {
            if (!_loop)
            {
                _timer.Stop();
                Ended?.Invoke(this, EventArgs.Empty);
                return;
            }

            _index = 0;
        }
        else
        {
            _index++;
        }

        ShowCurrent();
    }
}
