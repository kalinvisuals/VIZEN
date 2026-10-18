using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>A colour of the Smart Draw palette.</summary>
public sealed record DrawColor(string Hex, string NameKey)
{
    public string Name => Loc.T(NameKey);
}

/// <summary>
/// VIZEN Smart Draw: annotations drawn over the desktop, browsers and borderless games. Two explicit modes: draw mode
/// (the mouse draws) and pass-through (annotations stay visible, every click goes to the window underneath).
/// Disabled = no Smart Draw window exists. Drawings are vector data, saved to drawings.json only when the user asks.
/// </summary>
public sealed class SmartDrawViewModel : ObservableObject, IDisposable
{
    public const string FileName = "drawings.json";
    public static readonly double[] Thicknesses = [2, 4, 8, 14];
    public static readonly double[] FontSizes = [18, 28, 44];
    private static readonly TimeSpan ClearConfirmTime = TimeSpan.FromSeconds(5);

    /// <summary>Eraser diameter limits and default, in device-independent pixels.</summary>
    public const double MinEraserSize = 5, MaxEraserSize = 100, DefaultEraserSize = 24;

    private readonly ISmartDrawPresenter _presenter;
    private readonly IMonitorProvider _monitors;
    private readonly ActivityLog _activity;
    private DispatcherTimer? _confirmTimer;
    private bool _isEnabled;
    private bool _isDrawMode = true;
    private bool _annotationsVisible = true;
    private DrawTool _tool = DrawTool.Pen;
    private string _color = Palette[0].Hex;
    private double _thickness = 4;
    private double _fontSize = FontSizes[1];
    private ArrowHead _arrowHead = ArrowHead.Filled;
    private EraserMode _eraserMode = EraserMode.Precision;
    private double _eraserSize = DefaultEraserSize;
    private double _opacity = 1;
    private bool _isConfirmingClear;
    private string? _errorMessage;

    public SmartDrawViewModel(ISmartDrawPresenter presenter, IMonitorProvider monitors, ActivityLog activity)
    {
        _presenter = presenter;
        _monitors = monitors;
        _activity = activity;
        Document.Changed += (_, _) => RefreshDocument();

        ToggleEnabledCommand = new RelayCommand(ToggleEnabled);
        SetToolCommand = new RelayCommand<DrawTool>(t => Tool = t);
        SetColorCommand = new RelayCommand<string>(c => Color = c);
        NextThicknessCommand = new RelayCommand(() => Thickness = Next(Thicknesses, Thickness));
        NextFontSizeCommand = new RelayCommand(() => FontSize = Next(FontSizes, FontSize));
        ToggleArrowHeadCommand = new RelayCommand(() => ArrowHead = ArrowHead == ArrowHead.Filled ? ArrowHead.Open : ArrowHead.Filled);
        SetEraserModeCommand = new RelayCommand<EraserMode>(m => EraserMode = m);
        ToggleEraserModeCommand = new RelayCommand(() => EraserMode = EraserMode == EraserMode.Precision ? EraserMode.Object : EraserMode.Precision);
        UndoCommand = new RelayCommand(Document.Undo, () => Document.CanUndo);
        RedoCommand = new RelayCommand(Document.Redo, () => Document.CanRedo);
        ClearCommand = new RelayCommand(RequestClear, () => Document.Count > 0);
        ConfirmClearCommand = new RelayCommand(ConfirmClear);
        CancelClearCommand = new RelayCommand(() => IsConfirmingClear = false);
        ToggleModeCommand = new RelayCommand(ToggleMode);
        DrawModeCommand = new RelayCommand(() => SetDrawMode(true));
        PassThroughCommand = new RelayCommand(() => SetDrawMode(false));
        ToggleVisibilityCommand = new RelayCommand(ToggleVisibility);
        CloseCommand = new RelayCommand(() => IsEnabled = false);
        SaveCommand = new RelayCommand(Save, () => Document.Count > 0);
        RestoreCommand = new RelayCommand(Restore, () => HasSavedDrawing);
    }

    public static IReadOnlyList<DrawColor> Palette { get; } =
    [
        new("#FFFF354E", "Draw.Color.Red"),
        new("#FFFFB020", "Draw.Color.Amber"),
        new("#FFFFE94D", "Draw.Color.Yellow"),
        new("#FF34D399", "Draw.Color.Green"),
        new("#FF38BDF8", "Draw.Color.Blue"),
        new("#FFA78BFA", "Draw.Color.Violet"),
        new("#FFFFFFFF", "Draw.Color.White"),
        new("#FF111111", "Draw.Color.Black"),
    ];

    public DrawingDocument Document { get; } = new();

    // ----- State -----

    /// <summary>Smart Draw on: its windows exist. Off: they are closed (nothing runs); the drawing stays in memory.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            IsConfirmingClear = false;
            if (value)
            {
                _isDrawMode = true; // opening is meant to draw
                _annotationsVisible = true;
                _presenter.Open(this);
            }
            else
            {
                _presenter.Close();
            }

            OnPropertyChanged();
            RaiseModeChanged();
            OnPropertyChanged(nameof(AnnotationsVisible));
            _activity.Add("", value ? "Draw.Enabled" : "Draw.Disabled", value ? "Draw.EnabledDetail" : "Draw.DisabledDetail");
        }
    }

    /// <summary>Draw mode (the mouse draws) or pass-through (clicks go to the window underneath).</summary>
    public bool IsDrawMode => _isDrawMode;

    /// <summary>True when Smart Draw lets every click through: pass-through mode, hidden drawings, or off.</summary>
    public bool IsClickThrough => !_isEnabled || !_isDrawMode || !_annotationsVisible;

    public string ModeText => Loc.T(!_isEnabled ? "Draw.ModeOff" : _isDrawMode ? "Draw.ModeDraw" : "Draw.ModePassThrough");

    public bool AnnotationsVisible
    {
        get => _annotationsVisible;
        private set
        {
            if (SetProperty(ref _annotationsVisible, value))
            {
                RaiseModeChanged();
            }
        }
    }

    public DrawTool Tool
    {
        get => _tool;
        set
        {
            if (SetProperty(ref _tool, value))
            {
                OnPropertyChanged(nameof(ToolText));
                if (_isEnabled && !_isDrawMode)
                {
                    SetDrawMode(true); // picking a tool means drawing
                }
            }
        }
    }

    public string ToolText => Loc.T("Draw.Tool." + _tool);

    public string Color
    {
        get => _color;
        set => SetProperty(ref _color, value);
    }

    public double Thickness
    {
        get => _thickness;
        set => SetProperty(ref _thickness, Math.Clamp(value, 1, 40));
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, Math.Clamp(value, 8, 120));
    }

    public ArrowHead ArrowHead
    {
        get => _arrowHead;
        set
        {
            if (SetProperty(ref _arrowHead, value))
            {
                OnPropertyChanged(nameof(ArrowHeadText));
            }
        }
    }

    public string ArrowHeadText => Loc.T("Draw.Head." + _arrowHead);

    /// <summary>Precision (default): cuts freehand lines where it passes. Object: removes what it touches, whole.</summary>
    public EraserMode EraserMode
    {
        get => _eraserMode;
        set
        {
            if (SetProperty(ref _eraserMode, value))
            {
                OnPropertyChanged(nameof(EraserModeText));
            }
        }
    }

    public string EraserModeText => Loc.T("Draw.Eraser." + _eraserMode);

    /// <summary>Eraser diameter in device-independent pixels (5–100); the canvas shows a circle of exactly this size.</summary>
    public double EraserSize
    {
        get => _eraserSize;
        set
        {
            if (SetProperty(ref _eraserSize, Math.Clamp(Math.Round(value), MinEraserSize, MaxEraserSize)))
            {
                OnPropertyChanged(nameof(EraserSizeText));
            }
        }
    }

    public string EraserSizeText => Loc.F("Draw.EraserSizeValue", _eraserSize);

    /// <summary>Opacity of all drawings (0.3–1).</summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            if (SetProperty(ref _opacity, Math.Clamp(value, 0.3, 1)))
            {
                OnPropertyChanged(nameof(OpacityText));
            }
        }
    }

    public string OpacityText => $"{Math.Round(_opacity * 100)}%";

    public bool IsConfirmingClear
    {
        get => _isConfirmingClear;
        private set
        {
            if (SetProperty(ref _isConfirmingClear, value) && !value)
            {
                _confirmTimer?.Stop();
            }
        }
    }

    public int AnnotationCount => Document.Count;

    public string SummaryText => Loc.F("Draw.Summary", Document.Count, ModeText);

    public bool HasSavedDrawing => File.Exists(StorageLocation);

    public string StorageLocation => Path.Combine(JsonStore.DataDirectory, FileName);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ICommand ToggleEnabledCommand { get; }
    public ICommand SetToolCommand { get; }
    public ICommand SetColorCommand { get; }
    public ICommand NextThicknessCommand { get; }
    public ICommand NextFontSizeCommand { get; }
    public ICommand ToggleArrowHeadCommand { get; }
    public ICommand SetEraserModeCommand { get; }
    public ICommand ToggleEraserModeCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ConfirmClearCommand { get; }
    public ICommand CancelClearCommand { get; }
    public ICommand ToggleModeCommand { get; }
    public ICommand DrawModeCommand { get; }
    public ICommand PassThroughCommand { get; }
    public ICommand ToggleVisibilityCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RestoreCommand { get; }

    // ----- Actions (buttons, toolbar and hotkeys) -----

    public void ToggleEnabled() => IsEnabled = !IsEnabled;

    /// <summary>Hotkey / toolbar: draw mode ↔ pass-through. Turns Smart Draw on (in draw mode) when it is off.</summary>
    public void ToggleMode()
    {
        if (!_isEnabled)
        {
            IsEnabled = true;
            return;
        }

        SetDrawMode(!_isDrawMode);
    }

    public void SetDrawMode(bool draw)
    {
        if (!_isEnabled || _isDrawMode == draw)
        {
            return;
        }

        _isDrawMode = draw;
        if (draw && !_annotationsVisible)
        {
            _annotationsVisible = true; // drawing on hidden drawings would be confusing
            OnPropertyChanged(nameof(AnnotationsVisible));
        }

        RaiseModeChanged();
        _activity.Add(draw ? "" : "", draw ? "Draw.DrawMode" : "Draw.PassThrough", draw ? "Draw.DrawModeDetail" : "Draw.PassThroughDetail");
    }

    /// <summary>Shows or hides the drawings. Hidden drawings never take clicks.</summary>
    public void ToggleVisibility()
    {
        if (!_isEnabled)
        {
            return;
        }

        AnnotationsVisible = !_annotationsVisible;
        if (!_annotationsVisible && _isDrawMode)
        {
            _isDrawMode = false;
            RaiseModeChanged();
        }

        _activity.Add("", _annotationsVisible ? "Draw.Shown" : "Draw.Hidden", "Draw.CountDetail", Document.Count);
    }

    /// <summary>Adds a finished annotation from a canvas; says so when the drawing is full.</summary>
    public bool AddAnnotation(Annotation annotation)
    {
        if (Document.IsFull)
        {
            _activity.Add("", "Draw.Full", "Draw.FullDetail", DrawingDocument.MaxAnnotations);
            return false;
        }

        return Document.Add(annotation);
    }

    /// <summary>First call asks for confirmation (for a few seconds), the second one clears.</summary>
    public void RequestClear()
    {
        if (Document.Count == 0)
        {
            return;
        }

        if (IsConfirmingClear)
        {
            ConfirmClear();
            return;
        }

        IsConfirmingClear = true;
        if (Dispatcher.FromThread(Thread.CurrentThread) is not null)
        {
            _confirmTimer ??= new DispatcherTimer(ClearConfirmTime, DispatcherPriority.Background, (_, _) => IsConfirmingClear = false, Dispatcher.CurrentDispatcher);
            _confirmTimer.Stop();
            _confirmTimer.Start();
        }

        _activity.Add("", "Draw.ClearAsk", "Draw.ClearAskDetail", Document.Count);
    }

    public void ConfirmClear()
    {
        IsConfirmingClear = false;
        var count = Document.Count;
        if (count > 0)
        {
            Document.Clear();
            _activity.Add("", "Draw.Cleared", "Draw.ClearedDetail", count);
        }
    }

    /// <summary>Saves the drawing (vector data plus the monitors it was drawn on) to drawings.json.</summary>
    public void Save()
    {
        try
        {
            JsonStore.Save(FileName, new DrawingFile
            {
                SavedUtc = DateTime.UtcNow,
                Monitors = _monitors.Current().ToList(),
                Annotations = Document.Items.Select(a => a.Clone()).ToList(),
            });
            ErrorMessage = null;
            OnPropertyChanged(nameof(HasSavedDrawing));
            CommandManager.InvalidateRequerySuggested();
            _activity.Add("", "Draw.Saved", "Draw.SavedDetail", Document.Count, StorageLocation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.F("Draw.SaveFailed", ex.Message);
        }
    }

    /// <summary>Replaces the drawing with the saved one, mapped onto the monitors connected now.</summary>
    public void Restore()
    {
        var file = JsonStore.Load<DrawingFile>(FileName, out var error);
        if (error is not null || file is null)
        {
            ErrorMessage = error ?? Loc.T("Draw.NothingSaved");
            return;
        }

        var current = _monitors.Current();
        var saved = (file.Monitors ?? []).Where(m => m is not null).ToList();
        var mapped = new List<Annotation>();
        foreach (var a in (file.Annotations ?? []).Where(a => a is not null))
        {
            a.Points ??= [];
            a.Text ??= string.Empty;
            a.Monitor ??= string.Empty;
            a.Color ??= Palette[0].Hex;
            if (MonitorMapping.Resolve(a.Monitor, current) is { } target)
            {
                mapped.Add(MonitorMapping.Map(a, saved.FirstOrDefault(m => m.Key == a.Monitor), target));
            }
        }

        Document.Load(mapped);
        ErrorMessage = null;
        _activity.Add("", "Draw.Restored", "Draw.RestoredDetail", Document.Count);
    }

    public void Dispose()
    {
        _confirmTimer?.Stop();
        if (_presenter.IsOpen)
        {
            _presenter.Close();
        }
    }

    private void RaiseModeChanged()
    {
        OnPropertyChanged(nameof(IsDrawMode));
        OnPropertyChanged(nameof(IsClickThrough));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void RefreshDocument()
    {
        OnPropertyChanged(nameof(AnnotationCount));
        OnPropertyChanged(nameof(SummaryText));
        if (Document.Count == 0)
        {
            IsConfirmingClear = false;
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private static double Next(double[] values, double current) =>
        values.FirstOrDefault(v => v > current + 0.01, values[0]);

    private sealed class DrawingFile
    {
        public int Version { get; set; } = 1;
        public DateTime SavedUtc { get; set; }
        public List<MonitorInfo> Monitors { get; set; } = [];
        public List<Annotation> Annotations { get; set; } = [];
    }
}
