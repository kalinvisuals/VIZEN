using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VIZEN.App.Localization;
using VIZEN.App.Media;
using VIZEN.App.Models;
using VIZEN.App.ViewModels;

namespace VIZEN.App.Views;

/// <summary>
/// Renders one media file (image, animated GIF or video) with the layer's crop and rotation, and plays it.
/// Used by a layer's own window and by compositions; each instance decodes/plays independently.
/// </summary>
public partial class MediaContentView : UserControl
{
    private MediaLayerViewModel? _layer;
    private GifAnimation? _animation;
    private GifPlayer? _gif;
    private bool _reportsToLayer;
    private bool _isPlaying;

    public MediaContentView()
    {
        InitializeComponent();
        ContentVideo.MediaOpened += OnVideoOpened;
        ContentVideo.MediaEnded += OnVideoEnded;
        ContentVideo.MediaFailed += OnVideoFailed;
    }

    /// <summary>A non-looping GIF or video reached its end.</summary>
    public event EventHandler? Ended;

    /// <summary>The video was opened and its natural size is known.</summary>
    public event EventHandler? VideoOpened;

    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// Opens the layer's file. <paramref name="reportToLayer"/>: this is the layer's own view, so size, duration,
    /// errors and play state are reported to the layer (compositions only display). Throws nothing: returns
    /// the error message, or null on success.
    /// </summary>
    public async Task<string?> LoadAsync(MediaLayerViewModel layer, bool reportToLayer)
    {
        _layer = layer;
        _reportsToLayer = reportToLayer;
        DataContext = layer;
        layer.PropertyChanged += OnLayerPropertyChanged;
        try
        {
            switch (layer.Kind)
            {
                case MediaKind.Image:
                    var image = MediaFormats.LoadImage(layer.FilePath);
                    ContentImage.Source = image;
                    SetNaturalSize(image.PixelWidth, image.PixelHeight, null);
                    break;

                case MediaKind.Gif:
                    _animation = await GifAnimation.LoadAsync(layer.FilePath);
                    _gif = new GifPlayer(ContentImage);
                    _gif.Ended += (_, _) => OnEnded();
                    ContentImage.Source = _animation.Frames[0];
                    SetNaturalSize(_animation.PixelWidth, _animation.PixelHeight, _animation.TotalDuration);
                    break;

                case MediaKind.Video:
                    if (!System.IO.File.Exists(layer.FilePath))
                    {
                        throw new MediaLoadException(Loc.F("Media.FileMissing", layer.FilePath));
                    }

                    ContentImage.Visibility = Visibility.Collapsed;
                    ContentVideo.Visibility = Visibility.Visible;
                    ContentVideo.Source = new Uri(layer.FilePath, UriKind.Absolute);
                    break;
            }

            if (reportToLayer)
            {
                layer.ClearError();
            }

            return null;
        }
        catch (MediaLoadException ex)
        {
            if (reportToLayer)
            {
                layer.ReportError(ex.Message);
            }

            return ex.Message;
        }
    }

    public void Play(bool fromStart)
    {
        if (_layer is null)
        {
            return;
        }

        switch (_layer.Kind)
        {
            case MediaKind.Gif when _animation is not null && _gif is not null:
                if (fromStart)
                {
                    _gif.Play(_animation, _layer.Loop);
                }
                else
                {
                    _gif.Resume(_layer.Loop);
                }

                SetPlaying(_animation.Frames.Count > 1);
                break;

            case MediaKind.Video:
                if (fromStart)
                {
                    ContentVideo.Position = TimeSpan.Zero;
                }

                ContentVideo.Play();
                SetPlaying(true);
                break;
        }
    }

    public void Pause()
    {
        _gif?.Stop();
        if (_layer?.Kind == MediaKind.Video)
        {
            ContentVideo.Pause();
        }

        SetPlaying(false);
    }

    /// <summary>Stops and rewinds to the first frame.</summary>
    public void Stop()
    {
        Pause();
        if (_layer?.Kind == MediaKind.Video)
        {
            ContentVideo.Position = TimeSpan.Zero;
        }
        else if (_animation is not null && _gif is not null)
        {
            _gif.Play(_animation, _layer?.Loop ?? true);
            _gif.Stop();
        }
    }

    /// <summary>Releases the decoder/player. Call when the hosting window closes.</summary>
    public void Unload()
    {
        _gif?.Stop();
        ContentVideo.Stop();
        ContentVideo.Close();
        if (_layer is not null)
        {
            _layer.PropertyChanged -= OnLayerPropertyChanged;
            if (_reportsToLayer)
            {
                _layer.ReportPlaying(false);
            }
        }
    }

    private void OnLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MediaLayerViewModel.Crop):
                UpdateCropperSize();
                break;
            case nameof(MediaLayerViewModel.Loop):
                _gif?.SetLoop(_layer!.Loop);
                break;
        }
    }

    private void OnVideoOpened(object? sender, RoutedEventArgs e)
    {
        var duration = ContentVideo.NaturalDuration.HasTimeSpan ? ContentVideo.NaturalDuration.TimeSpan : (TimeSpan?)null;
        SetNaturalSize(ContentVideo.NaturalVideoWidth, ContentVideo.NaturalVideoHeight, duration);
        VideoOpened?.Invoke(this, EventArgs.Empty);
    }

    private void OnVideoEnded(object? sender, RoutedEventArgs e)
    {
        if (_layer?.Loop == true)
        {
            ContentVideo.Position = TimeSpan.Zero;
            ContentVideo.Play();
        }
        else
        {
            OnEnded();
        }
    }

    private void OnVideoFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (_layer is not null && _reportsToLayer)
        {
            _layer.ReportError(Loc.F("Media.VideoFailed", System.IO.Path.GetFileName(_layer.FilePath), e.ErrorException?.Message ?? string.Empty));
        }

        SetPlaying(false);
    }

    private void OnEnded()
    {
        SetPlaying(false);
        if (_reportsToLayer)
        {
            _layer?.ReportEnded();
        }

        Ended?.Invoke(this, EventArgs.Empty);
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        if (_reportsToLayer)
        {
            _layer?.ReportPlaying(playing);
        }
    }

    private int _naturalWidth;
    private int _naturalHeight;

    private void SetNaturalSize(int width, int height, TimeSpan? duration)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _naturalWidth = width;
        _naturalHeight = height;
        if (_reportsToLayer)
        {
            _layer?.ReportOpened(width, height, duration);
        }

        UpdateCropperSize();
    }

    private void UpdateCropperSize()
    {
        if (_naturalWidth <= 0 || _layer is null)
        {
            return;
        }

        Cropper.Width = Math.Max(1, _naturalWidth * _layer.Crop.Width);
        Cropper.Height = Math.Max(1, _naturalHeight * _layer.Crop.Height);
    }
}
