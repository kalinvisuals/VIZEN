using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using VIZEN.App.Infrastructure;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>One on-screen notification describing what an action just did.</summary>
public sealed class ToastViewModel : ObservableObject
{
    public ToastViewModel(ActivityEntry entry, Action<ToastViewModel> dismiss)
    {
        Entry = entry;
        DismissCommand = new RelayCommand(() => dismiss(this));
    }

    public ActivityEntry Entry { get; }

    public ICommand DismissCommand { get; }
}

/// <summary>
/// Shows the latest activity-log entry as a single toast, long enough to read it, so each button press is confirmed. A
/// new entry replaces the toast on screen instead of stacking on top of it (stacked toasts covered the controls being
/// changed); every entry stays in the activity history on the Dashboard.
/// </summary>
public sealed class ToastHostViewModel : ObservableObject
{
    // Reading time: a base plus ~15 characters per second (slow reading), within these bounds.
    private static readonly TimeSpan MinLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AfterHover = TimeSpan.FromSeconds(2);
    private readonly DispatcherTimer _timer = new();

    public ToastHostViewModel(ActivityLog activity)
    {
        activity.EntryAdded += (_, entry) => Show(entry);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Toasts.Clear();
        };
    }

    /// <summary>At most one toast.</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>How long a toast with this text stays on screen.</summary>
    public static TimeSpan LifetimeFor(string title, string detail)
    {
        var seconds = 2 + (title.Length + detail.Length) / 15.0;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, MinLifetime.TotalSeconds, MaxLifetime.TotalSeconds));
    }

    /// <summary>The pointer is over the toast: it stays while the user reads it, and goes shortly after the pointer leaves.</summary>
    public void SetHovered(bool hovered)
    {
        if (Toasts.Count == 0)
        {
            return;
        }

        _timer.Stop();
        if (!hovered)
        {
            _timer.Interval = AfterHover;
            _timer.Start();
        }
    }

    private void Show(ActivityEntry entry)
    {
        Toasts.Clear();
        Toasts.Add(new ToastViewModel(entry, Dismiss));

        // Each new entry gets its full reading time.
        _timer.Stop();
        _timer.Interval = LifetimeFor(entry.Title, entry.Detail);
        _timer.Start();
    }

    private void Dismiss(ToastViewModel toast)
    {
        _timer.Stop();
        Toasts.Remove(toast);
    }
}
