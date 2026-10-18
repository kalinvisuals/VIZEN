using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VIZEN.App.Capture;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;

namespace VIZEN.App.ViewModels;

/// <summary>One layer of an open or closed composition, with its live content when the composition is open.</summary>
public sealed class CompositionItemViewModel : ObservableObject
{
    private readonly CompositionViewModel _owner;
    private CaptureLease? _lease;
    private string? _missingText;

    public CompositionItemViewModel(CompositionItem item, CompositionViewModel owner)
    {
        Item = item;
        _owner = owner;
        MoveForwardCommand = new RelayCommand(() => owner.MoveItem(this, -1));
        MoveBackwardCommand = new RelayCommand(() => owner.MoveItem(this, 1));
        RemoveCommand = new RelayCommand(() => owner.RemoveItem(this));
        FillCanvasCommand = new RelayCommand(() => SetBounds(new Rect(0, 0, 1, 1)));
        FitAspectCommand = new RelayCommand(FitAspect);
    }

    public CompositionItem Item { get; }

    public bool IsCapture => Item.Kind == CompositionItemKind.Capture;
    public bool IsMedia => Item.Kind == CompositionItemKind.Media;

    public MediaLayerViewModel? MediaLayer => Item.MediaLayerId is { } id ? _owner.Library.Find(id) : null;

    /// <summary>Live capture while the composition is open (null when the source window is not found).</summary>
    public LiveCapture? Capture => _lease?.Capture;

    public Rect Viewbox => RegionMath.IsFull(Item.Region) ? RegionMath.Full : RegionMath.ClampInside(Item.Region);

    public string Name => IsMedia
        ? MediaLayer?.Name ?? Loc.T("Schedule.MissingLayer")
        : Item.RegionName is { } region ? $"{region} · {Item.ProcessName}" : $"{Item.ProcessName} · {Item.WindowTitle}";

    public string KindGlyph => IsMedia ? MediaLayer?.KindGlyph ?? "" : "";

    /// <summary>Why nothing is shown (source not open / layer removed), or null.</summary>
    public string? MissingText
    {
        get => _missingText;
        private set => SetProperty(ref _missingText, value);
    }

    public double Opacity
    {
        get => Item.Opacity;
        set
        {
            Item.Opacity = Math.Clamp(value, 0.05, 1);
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpacityText));
            _owner.Changed();
        }
    }

    public string OpacityText => $"{Math.Round(Opacity * 100)}%";

    public bool FollowLayerVisibility
    {
        get => Item.FollowLayerVisibility;
        set
        {
            Item.FollowLayerVisibility = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsContentVisible));
            _owner.Changed();
        }
    }

    /// <summary>Media items that follow their layer are only visible while the layer is shown (by hand or schedule).</summary>
    public bool IsContentVisible => !IsMedia || !Item.FollowLayerVisibility || MediaLayer?.IsShown == true;

    // Canvas pixel bounds, for the composition window.
    public double PixelX => Item.X * _owner.CanvasWidth;
    public double PixelY => Item.Y * _owner.CanvasHeight;
    public double PixelWidth => Item.Width * _owner.CanvasWidth;
    public double PixelHeight => Item.Height * _owner.CanvasHeight;

    /// <summary>Front items get the highest z-index.</summary>
    public int ZIndex => _owner.Items.Count - _owner.Items.IndexOf(this);

    public string BoundsText => Loc.F("Composition.ItemBounds", (int)PixelX, (int)PixelY, (int)PixelWidth, (int)PixelHeight);

    public ICommand MoveForwardCommand { get; }
    public ICommand MoveBackwardCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand FillCanvasCommand { get; }
    public ICommand FitAspectCommand { get; }

    /// <summary>Moves/resizes by a delta in canvas pixels (from the edit handles in the window).</summary>
    public void MoveBy(double dx, double dy) =>
        SetBounds(new Rect(Item.X + dx / _owner.CanvasWidth, Item.Y + dy / _owner.CanvasHeight, Item.Width, Item.Height));

    public void ResizeBy(double dx, double dy) =>
        SetBounds(new Rect(Item.X, Item.Y, Item.Width + dx / _owner.CanvasWidth, Item.Height + dy / _owner.CanvasHeight));

    public void SetBounds(Rect normalized)
    {
        Item.Bounds = normalized;
        RaiseBoundsChanged();
        _owner.Changed();
    }

    internal void RaiseBoundsChanged()
    {
        OnPropertyChanged(nameof(PixelX));
        OnPropertyChanged(nameof(PixelY));
        OnPropertyChanged(nameof(PixelWidth));
        OnPropertyChanged(nameof(PixelHeight));
        OnPropertyChanged(nameof(BoundsText));
    }

    internal void RaiseZIndexChanged() => OnPropertyChanged(nameof(ZIndex));

    internal void Attach(CaptureLease? lease, string? missingText)
    {
        _lease = lease;
        MissingText = missingText;
        OnPropertyChanged(nameof(Capture));
    }

    internal void Detach()
    {
        _lease?.Dispose();
        _lease = null;
        OnPropertyChanged(nameof(Capture));
    }

    internal void OnLayerChanged(string? propertyName)
    {
        if (propertyName is nameof(MediaLayerViewModel.IsShown) or null or "")
        {
            OnPropertyChanged(nameof(IsContentVisible));
        }

        if (propertyName is nameof(MediaLayerViewModel.Name) or null or "")
        {
            OnPropertyChanged(nameof(Name));
        }
    }

    /// <summary>Keeps the width and sets the height so the item has its content's shape (no empty bands).</summary>
    private void FitAspect()
    {
        double? aspect = IsMedia
            ? MediaLayer?.ContentAspect
            : Capture is { FrameWidth: > 0, FrameHeight: > 0 } c ? c.FrameWidth * Viewbox.Width / (c.FrameHeight * Viewbox.Height) : null;
        if (aspect is not { } a || a <= 0)
        {
            return;
        }

        var heightPixels = PixelWidth / a;
        SetBounds(new Rect(Item.X, Item.Y, Item.Width, heightPixels / _owner.CanvasHeight));
    }
}

/// <summary>
/// A composition: a canvas (e.g. 1920×1080) with captures and media stacked on it, shown in one clean window
/// titled "VIZEN Output · Compoziție · name" for OBS. Edit mode shows handles to move and resize items.
/// </summary>
public sealed class CompositionViewModel : ObservableObject
{
    private static readonly Brush GreenScreen = Freeze(new SolidColorBrush(Color.FromRgb(0, 255, 0)));

    private readonly Action _save;
    private bool _isOpen;
    private bool _isEditing;
    private bool _isConfirmingDelete;
    private int _pixelWidth;
    private int _pixelHeight;

    public CompositionViewModel(Composition composition, MediaLibraryViewModel library, Action save)
    {
        Composition = composition;
        Library = library;
        _save = save;
        foreach (var item in composition.Items)
        {
            Items.Add(new CompositionItemViewModel(item, this));
        }

        CanvasPresetCommand = new RelayCommand<string>(SetCanvasPreset);
        ToggleEditCommand = new RelayCommand(() => IsEditing = !IsEditing, () => IsOpen);
        WindowSizeCommand = new RelayCommand<string>(RequestWindowSize, _ => IsOpen);
        AddMediaCommand = new RelayCommand<MediaLayerViewModel>(AddMedia);
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);

        foreach (var layer in library.Items)
        {
            layer.PropertyChanged += OnLayerPropertyChanged;
        }

        library.Items.CollectionChanged += OnLibraryChanged;
    }

    public Composition Composition { get; }
    public MediaLibraryViewModel Library { get; }
    public Guid Id => Composition.Id;

    /// <summary>Front first.</summary>
    public ObservableCollection<CompositionItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public IReadOnlyList<MediaLayerViewModel> AvailableMedia => Library.Items.ToList();

    public string Name
    {
        get => Composition.Name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Composition.Name = value.Trim();
                OnPropertyChanged(nameof(WindowTitle));
                Changed();
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Exact window title to pick in OBS.</summary>
    public string WindowTitle => $"VIZEN Output · {Loc.T("Composition.TitleWord")} · {Name}";

    public int CanvasWidth => Composition.CanvasWidth;
    public int CanvasHeight => Composition.CanvasHeight;
    public string CanvasText => Loc.F("Editor.PixelSize", CanvasWidth, CanvasHeight);

    public Brush BackgroundBrush => Composition.Background == "green" ? GreenScreen : Brushes.Black;

    public bool IsGreenBackground
    {
        get => Composition.Background == "green";
        set => SetBackground(value, "green");
    }

    public bool IsBlackBackground
    {
        get => Composition.Background != "green";
        set => SetBackground(value, "black");
    }

    public bool AutoOpen
    {
        get => Composition.AutoOpen;
        set
        {
            Composition.AutoOpen = value;
            OnPropertyChanged();
            Changed();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        internal set
        {
            if (SetProperty(ref _isOpen, value))
            {
                if (!value)
                {
                    IsEditing = false;
                }

                OnPropertyChanged(nameof(StatusText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Shows move/resize handles in the window. Turn off before going live (OBS would capture them).</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (SetProperty(ref _isEditing, value))
            {
                EditingChanged?.Invoke(this, value);
            }
        }
    }

    public string StatusText => IsOpen
        ? Loc.F("Composition.StatusOpen", _pixelWidth > 0 ? Loc.F("Editor.PixelSize", _pixelWidth, _pixelHeight) : "—")
        : Loc.T("Composition.StatusClosed");

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set => SetProperty(ref _isConfirmingDelete, value);
    }

    // Set by the manager.
    public ICommand? OpenCommand { get; internal set; }
    public ICommand? CloseCommand { get; internal set; }
    public ICommand? ConfirmDeleteCommand { get; internal set; }
    public ICommand? AddOutputCommand { get; internal set; }

    public ICommand CanvasPresetCommand { get; }
    public ICommand ToggleEditCommand { get; }
    public ICommand WindowSizeCommand { get; }
    public ICommand AddMediaCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    /// <summary>Asks the window to set its client size in physical pixels.</summary>
    public event EventHandler<OutputSizeRequest>? SizeRequested;

    public event EventHandler<OutputSizeRequest>? Resized;

    public event EventHandler<bool>? EditingChanged;

    /// <summary>Raised for edits that the manager logs (item added/removed/moved in the stack).</summary>
    public event EventHandler<string>? ItemsEdited;

    internal void ReportPixelSize(int width, int height)
    {
        _pixelWidth = width;
        _pixelHeight = height;
        OnPropertyChanged(nameof(StatusText));
    }

    internal void ReportWindowBounds(Rect bounds)
    {
        (Composition.Left, Composition.Top, Composition.WindowWidth, Composition.WindowHeight) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        Changed();
    }

    /// <summary>Adds a capture item copied from an open OBS output (whole window or region).</summary>
    internal CompositionItemViewModel AddCapture(WindowSource source, Rect region, string? regionName)
    {
        var offset = (Items.Count % 6) * 0.04;
        var aspect = source.Width > 0 && source.Height > 0 ? source.Width * region.Width / (source.Height * region.Height) : 16.0 / 9;
        var width = 0.4;
        var height = Math.Min(0.9, width * CanvasWidth / aspect / CanvasHeight);
        var item = new CompositionItem
        {
            Kind = CompositionItemKind.Capture,
            ProcessName = source.ProcessName,
            ExecutablePath = source.ExecutablePath,
            WindowTitle = source.Title,
            ClassName = source.ClassName,
            RegionX = region.X,
            RegionY = region.Y,
            RegionWidth = region.Width,
            RegionHeight = region.Height,
            RegionName = regionName,
            X = 0.05 + offset,
            Y = 0.05 + offset,
            Width = width,
            Height = height,
        };
        return Insert(item);
    }

    private void AddMedia(MediaLayerViewModel layer)
    {
        var aspect = layer.ContentAspect ?? 16.0 / 9;
        var width = layer.IsVideo ? 0.4 : 0.2;
        var item = new CompositionItem
        {
            Kind = CompositionItemKind.Media,
            MediaLayerId = layer.Id,
            FollowLayerVisibility = false,
            X = 0.95 - width,
            Y = 0.05,
            Width = width,
            Height = Math.Min(0.9, width * CanvasWidth / aspect / CanvasHeight),
        };
        Insert(item);
    }

    private CompositionItemViewModel Insert(CompositionItem item)
    {
        Composition.Items.Insert(0, item);
        var vm = new CompositionItemViewModel(item, this);
        Items.Insert(0, vm);
        AfterStackChanged();
        ItemAdded?.Invoke(this, vm);
        ItemsEdited?.Invoke(this, Loc.F("Composition.ItemAdded", vm.Name));
        return vm;
    }

    /// <summary>Raised when an item is added, so an open window can attach its capture.</summary>
    public event EventHandler<CompositionItemViewModel>? ItemAdded;

    public event EventHandler<CompositionItemViewModel>? ItemRemoved;

    internal void MoveItem(CompositionItemViewModel item, int direction)
    {
        var index = Items.IndexOf(item);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
        Composition.Items.RemoveAt(index);
        Composition.Items.Insert(target, item.Item);
        AfterStackChanged();
        ItemsEdited?.Invoke(this, Loc.F("Composition.ItemMoved", item.Name, Items.Count - target, Items.Count));
    }

    internal void RemoveItem(CompositionItemViewModel item)
    {
        item.Detach();
        Items.Remove(item);
        Composition.Items.Remove(item.Item);
        AfterStackChanged();
        ItemRemoved?.Invoke(this, item);
        ItemsEdited?.Invoke(this, Loc.F("Composition.ItemRemoved", item.Name));
    }

    internal void Changed() => _save();

    private void AfterStackChanged()
    {
        foreach (var item in Items)
        {
            item.RaiseZIndexChanged();
        }

        OnPropertyChanged(nameof(HasItems));
        Changed();
    }

    private void SetCanvasPreset(string preset)
    {
        (int w, int h)? size = preset switch
        {
            "1080p" => (1920, 1080),
            "720p" => (1280, 720),
            "vertical" => (1080, 1920),
            "square" => (1080, 1080),
            _ => null,
        };
        if (size is not { } s || (s.w == CanvasWidth && s.h == CanvasHeight))
        {
            return;
        }

        (Composition.CanvasWidth, Composition.CanvasHeight) = s;
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(CanvasText));
        foreach (var item in Items)
        {
            item.RaiseBoundsChanged();
        }

        ItemsEdited?.Invoke(this, Loc.F("Composition.CanvasChanged", CanvasText));
        Changed();
    }

    private void RequestWindowSize(string preset)
    {
        Size? target = preset switch
        {
            "canvas" => new Size(CanvasWidth, CanvasHeight),
            "half" => new Size(CanvasWidth / 2.0, CanvasHeight / 2.0),
            _ => null,
        };
        if (target is { } size)
        {
            var request = new OutputSizeRequest(size);
            SizeRequested?.Invoke(this, request);
            Resized?.Invoke(this, request);
        }
    }

    private void SetBackground(bool selected, string value)
    {
        if (!selected || Composition.Background == value)
        {
            return;
        }

        Composition.Background = value;
        OnPropertyChanged(nameof(BackgroundBrush));
        OnPropertyChanged(nameof(IsGreenBackground));
        OnPropertyChanged(nameof(IsBlackBackground));
        Changed();
    }

    private void OnLibraryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var layer in e.NewItems?.OfType<MediaLayerViewModel>() ?? [])
        {
            layer.PropertyChanged += OnLayerPropertyChanged;
        }

        foreach (var layer in e.OldItems?.OfType<MediaLayerViewModel>() ?? [])
        {
            layer.PropertyChanged -= OnLayerPropertyChanged;
        }

        OnPropertyChanged(nameof(AvailableMedia));
        foreach (var item in Items)
        {
            item.OnLayerChanged(null);
        }
    }

    /// <summary>Stops listening to the media library (when the composition is deleted).</summary>
    internal void Detach()
    {
        Library.Items.CollectionChanged -= OnLibraryChanged;
        foreach (var layer in Library.Items)
        {
            layer.PropertyChanged -= OnLayerPropertyChanged;
        }
    }

    private void OnLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MediaLayerViewModel layer)
        {
            return;
        }

        foreach (var item in Items.Where(i => i.Item.MediaLayerId == layer.Id))
        {
            item.OnLayerChanged(e.PropertyName);
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
