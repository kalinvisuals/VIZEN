using System.Globalization;
using System.Text;
using System.Windows.Media;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;

namespace VIZEN.App.Models;

public enum WindowDisplayState
{
    Normal,
    Minimized,
    Maximized,
}

/// <summary>
/// A visible top-level window. Identity (handle, process) is fixed; title, bounds, state and category
/// are refreshed in place by <see cref="UpdateFrom"/> so bound UI and selection survive auto-refresh.
/// </summary>
public sealed class WindowSource : ObservableObject
{
    public required IntPtr Handle { get; init; }
    public required string ProcessName { get; init; }
    public required uint ProcessId { get; init; }
    public string? ExecutablePath { get; init; }
    public required string ClassName { get; init; }

    /// <summary>True when the window belongs to a web browser (its category then comes from the active tab title).</summary>
    public bool IsBrowser { get; init; }

    /// <summary>Windows device id when this source is a camera (webcam, capture card, virtual camera); null for windows.</summary>
    public string? CameraDeviceId { get; init; }

    public bool IsCamera => CameraDeviceId is not null;

    /// <summary>Frame rate of a synthetic benchmark source (Performance page); null for real windows and cameras.</summary>
    public int? SyntheticFps { get; init; }

    public bool IsSynthetic => SyntheticFps is not null;

    public string Title { get; internal set; } = string.Empty;
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public WindowDisplayState State { get; internal set; }
    public uint Dpi { get; internal set; }
    public SourceCategory Category { get; internal set; }

    /// <summary>The window's own icon (from WM_GETICON / class icon). Null when the window exposes none.</summary>
    public ImageSource? Icon { get; internal set; }

    /// <summary>Position in the desktop Z-order at the last refresh (0 = topmost).</summary>
    public int ZOrder { get; internal set; }

    public string HandleText => IsCamera ? "—" : $"0x{Handle.ToInt64():X8}";

    public string Initial => string.IsNullOrEmpty(ProcessName)
        ? "?"
        : ProcessName[..1].ToUpper(CultureInfo.CurrentCulture);

    public string SizeText => IsCamera ? (Width > 0 ? $"{Width} × {Height}" : Loc.T("Source.CameraSizePending")) : State == WindowDisplayState.Minimized ? Loc.T("State.Minimized") : $"{Width} × {Height}";

    public string PositionText => IsCamera ? "—" : State == WindowDisplayState.Minimized ? "—" : $"{X}, {Y}";

    public string StateText => Loc.T($"State.{State}");

    public string DpiText => IsCamera ? "—" : Dpi == 0 ? Loc.T("Common.Unknown") : $"{Dpi * 100 / 96}%  ({Dpi} DPI)";

    public string ExecutablePathText => ExecutablePath ?? Loc.T("Source.PathUnavailable");

    public string ProcessSummary => IsCamera ? Loc.T("Source.CameraSummary") : Loc.F("Source.ProcessSummary", ProcessName, ProcessId);

    public string CategoryText => Loc.T($"Category.{Category}");

    public string CategoryGlyph => SourceClassifier.Glyph(Category);

    /// <summary>A browser window categorised as Calls/Media from its active tab — the category can change with the tab.</summary>
    public bool IsCategoryFromTabTitle => IsBrowser && Category != SourceCategory.Browsers;

    /// <summary>Copies the mutable state of a fresher snapshot of the same window. Returns true if anything changed.</summary>
    internal bool UpdateFrom(WindowSource fresh)
    {
        var changed = Title != fresh.Title || X != fresh.X || Y != fresh.Y || Width != fresh.Width
            || Height != fresh.Height || State != fresh.State || Dpi != fresh.Dpi || Category != fresh.Category
            || !ReferenceEquals(Icon, fresh.Icon);

        ZOrder = fresh.ZOrder;
        if (!changed)
        {
            return false;
        }

        Title = fresh.Title;
        X = fresh.X;
        Y = fresh.Y;
        Width = fresh.Width;
        Height = fresh.Height;
        State = fresh.State;
        Dpi = fresh.Dpi;
        Category = fresh.Category;
        Icon = fresh.Icon;
        RaiseAllPropertiesChanged();
        return true;
    }

    public string ToDetailsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Loc.T("Details.Title")}: {Title}");
        sb.AppendLine($"{Loc.T("Details.Process")}: {ProcessName} (PID {ProcessId})");
        sb.AppendLine($"{Loc.T("Details.Category")}: {CategoryText}");
        sb.AppendLine($"{Loc.T("Details.Executable")}: {ExecutablePathText}");
        sb.AppendLine($"{Loc.T("Details.Handle")}: {HandleText}");
        sb.AppendLine($"{Loc.T("Details.Class")}: {ClassName}");
        sb.AppendLine($"{Loc.T("Details.Position")}: {PositionText}");
        sb.AppendLine($"{Loc.T("Details.Size")}: {SizeText}");
        sb.AppendLine($"{Loc.T("Details.State")}: {StateText}");
        sb.Append($"{Loc.T("Details.Dpi")}: {DpiText}");
        return sb.ToString();
    }
}
