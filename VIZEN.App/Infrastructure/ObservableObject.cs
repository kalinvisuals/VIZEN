using System.ComponentModel;
using System.Runtime.CompilerServices;
using VIZEN.App.Localization;

namespace VIZEN.App.Infrastructure;

/// <summary>
/// Minimal INotifyPropertyChanged base for view models and observable models.
/// Objects that are bound to the UI are refreshed automatically when the UI language changes,
/// because many of their properties return localized text.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    private PropertyChangedEventHandler? _propertyChanged;
    private int _localizationSubscribed;

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
            _propertyChanged += value;

            // Subscribe lazily: only objects someone listens to need refreshing. This also keeps
            // construction thread-agnostic (models may be created on background threads).
            if (Interlocked.Exchange(ref _localizationSubscribed, 1) == 0)
            {
                Loc.Subscribe(this);
            }
        }
        remove => _propertyChanged -= value;
    }

    /// <summary>Tells bindings that every property may have changed.</summary>
    internal void RaiseAllPropertiesChanged() => _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
