using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using VIZEN.App.Localization;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace VIZEN.App.Capture;

/// <summary>
/// Live capture of a camera (webcam, capture card, virtual camera) via Windows MediaCapture.
/// Opens the camera in shared mode first so it keeps working while a call app uses the same camera,
/// and falls back to exclusive mode for cameras that don't support sharing.
/// </summary>
public sealed class CameraCapture : LiveCapture
{
    private readonly string _deviceId;
    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _reader;

    internal CameraCapture(string deviceId, double maxFps)
        : base("camera:" + deviceId, maxFps)
    {
        _deviceId = deviceId;
    }

    internal async Task StartAsync()
    {
        try
        {
            _mediaCapture = await OpenAsync(MediaCaptureSharingMode.SharedReadOnly)
                ?? await OpenAsync(MediaCaptureSharingMode.ExclusiveControl);
            if (_mediaCapture is null)
            {
                Fail(Loc.T("Capture.CameraNoStream"));
                return;
            }

            _mediaCapture.Failed += OnCaptureFailed;

            var source = _mediaCapture.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(s => s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview ? 0 : 1)
                .FirstOrDefault();
            if (source is null)
            {
                Fail(Loc.T("Capture.CameraNoStream"));
                return;
            }

            _reader = await _mediaCapture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += OnFrameArrived;

            var status = await _reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                Fail(status == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                    ? Loc.T("Capture.CameraInUse")
                    : Loc.F("Capture.CameraStartFailed", status));
                ReleaseResources();
            }
        }
        catch (UnauthorizedAccessException)
        {
            Fail(Loc.T("Capture.CameraDenied"));
            ReleaseResources();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Fail(Loc.F("Capture.CameraStartFailed", ex.Message));
            ReleaseResources();
        }
    }

    /// <summary>Initializes MediaCapture in the given sharing mode; null if this mode isn't available.</summary>
    private async Task<MediaCapture?> OpenAsync(MediaCaptureSharingMode sharing)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = _deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = sharing,
            });

            if (capture.FrameSources.Count == 0)
            {
                capture.Dispose();
                return null;
            }

            return capture;
        }
        catch (Exception ex) when (sharing == MediaCaptureSharingMode.SharedReadOnly && ex is not UnauthorizedAccessException)
        {
            // Some cameras can't be shared; the caller retries in exclusive mode.
            capture.Dispose();
            return null;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        var receivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (IsDisposed || !TryBeginFrame())
        {
            // Still drain the frame so the reader keeps delivering the newest one.
            sender.TryAcquireLatestFrame()?.Dispose();
            return;
        }

        using var frameReference = sender.TryAcquireLatestFrame();
        var bitmap = frameReference?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
        {
            return;
        }

        var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? bitmap
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        try
        {
            var width = bgra.PixelWidth;
            var height = bgra.PixelHeight;
            WriteFrame(width, height, width * 4, (buffer, length) => bgra.CopyToBuffer(buffer.AsBuffer(0, length)),
                receivedAt);
        }
        finally
        {
            if (!ReferenceEquals(bgra, bitmap))
            {
                bgra.Dispose();
            }

            bitmap.Dispose();
        }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            ReleaseResources();
            Fail(Loc.F("Capture.CameraLost", args.Message));
        });
    }

    protected override void ReleaseResources()
    {
        var reader = _reader;
        var capture = _mediaCapture;
        _reader = null;
        _mediaCapture = null;

        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
        }

        if (capture is not null)
        {
            capture.Failed -= OnCaptureFailed;
        }

        if (reader is null && capture is null)
        {
            return;
        }

        // Stopping is asynchronous; turn the camera off in the background so the UI never waits on it.
        _ = Task.Run(async () =>
        {
            try
            {
                if (reader is not null)
                {
                    await reader.StopAsync();
                    reader.Dispose();
                }
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
            {
                // Already stopped.
            }
            finally
            {
                capture?.Dispose();
            }
        });
    }
}
