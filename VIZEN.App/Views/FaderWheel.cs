using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VIZEN.App.Views;

/// <summary>
/// Mouse wheel over a mixer fader: one notch = one SmallChange (5 %). Works without keyboard focus, so it also works
/// in the floating mixer while a game keeps the focus.
/// </summary>
public static class FaderWheel
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(FaderWheel), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
        {
            return;
        }

        slider.PreviewMouseWheel -= OnWheel;
        if ((bool)e.NewValue)
        {
            slider.PreviewMouseWheel += OnWheel;
        }
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var slider = (Slider)sender;
        slider.Value = Math.Clamp(slider.Value + Math.Sign(e.Delta) * slider.SmallChange, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }
}

/// <summary>
/// For a ScrollViewer that only scrolls sideways (inside a page that scrolls up and down): the mouse wheel goes on to
/// the page instead of stopping at it, so the page never "gets stuck" while the pointer passes over it.
/// </summary>
public static class WheelPassthrough
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WheelPassthrough), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnWheel;
        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnWheel;
        }
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || System.Windows.Media.VisualTreeHelper.GetParent((DependencyObject)sender) is not UIElement parent)
        {
            return;
        }

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }
}
