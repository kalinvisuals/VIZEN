using System.Windows;
using System.Windows.Media;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>Aspect-ratio choice for a region; <see cref="Ratio"/> null = free-form.</summary>
public sealed record AspectOption(string Key, double? Ratio)
{
    public string Label => Ratio is null ? Loc.T("Editor.AspectFree") : Key;
}

/// <summary>One crop region in the region editor.</summary>
public sealed class CropRegionViewModel : ObservableObject
{
    private string _name;
    private Rect _bounds;
    private double? _aspect;
    private ImageSource? _frame;
    private int _sourceWidth;
    private int _sourceHeight;

    public CropRegionViewModel(Guid id, string name, Rect bounds, double? aspect)
    {
        Id = id;
        _name = name;
        _bounds = RegionMath.ClampInside(bounds);
        _aspect = aspect;
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, string.IsNullOrWhiteSpace(value) ? _name : value.Trim());
    }

    /// <summary>Normalized rectangle within the source window.</summary>
    public Rect Bounds
    {
        get => _bounds;
        set
        {
            if (SetProperty(ref _bounds, RegionMath.ClampInside(value)))
            {
                OnPropertyChanged(nameof(PixelSizeText));
            }
        }
    }

    /// <summary>Locked pixel aspect ratio (width / height) or null.</summary>
    public double? Aspect
    {
        get => _aspect;
        set => SetProperty(ref _aspect, value);
    }

    /// <summary>Live source frame, for the region's thumbnail (an ImageBrush with Viewbox = Bounds).</summary>
    public ImageSource? Frame
    {
        get => _frame;
        private set => SetProperty(ref _frame, value);
    }

    public string PixelSizeText => _sourceWidth > 0
        ? Loc.F("Editor.PixelSize", (int)Math.Round(Bounds.Width * _sourceWidth), (int)Math.Round(Bounds.Height * _sourceHeight))
        : "—";

    internal void SetSource(ImageSource? frame, int width, int height)
    {
        Frame = frame;
        if (_sourceWidth != width || _sourceHeight != height)
        {
            _sourceWidth = width;
            _sourceHeight = height;
            OnPropertyChanged(nameof(PixelSizeText));
        }
    }

    public SavedRegion ToSaved() => new()
    {
        Id = Id,
        Name = Name,
        X = Bounds.X,
        Y = Bounds.Y,
        Width = Bounds.Width,
        Height = Bounds.Height,
        Aspect = Aspect,
    };

    public static CropRegionViewModel FromSaved(SavedRegion saved) =>
        new(saved.Id, saved.Name, new Rect(saved.X, saved.Y, saved.Width, saved.Height), saved.Aspect);
}
