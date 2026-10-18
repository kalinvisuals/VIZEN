using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VIZEN.App.Localization;
using VIZEN.App.Models;

namespace VIZEN.App.Views;

/// <summary>
/// Click, then press a shortcut (e.g. Ctrl + Shift + 1). Needs at least one modifier. Backspace/Delete
/// clears it, Escape cancels. Writes the gesture text to <see cref="Gesture"/>.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).ShowGesture()));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        GotKeyboardFocus += (_, _) => Text = Loc.T("Hotkey.PressKeys");
        LostKeyboardFocus += (_, _) => ShowGesture();
    }

    public string? Gesture
    {
        get => (string?)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (modifiers == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.Escape:
                    MoveFocusAway();
                    return;
                case Key.Back or Key.Delete:
                    Gesture = string.Empty;
                    MoveFocusAway();
                    return;
                case Key.Tab:
                    e.Handled = false;
                    return;
            }
        }

        var gesture = new HotkeyGesture(modifiers, key);
        if (gesture.IsValid)
        {
            Gesture = gesture.ToString();
            MoveFocusAway();
        }
        else
        {
            // Show the modifiers held so far, waiting for the main key.
            Text = modifiers == ModifierKeys.None ? Loc.T("Hotkey.NeedsModifier") : new HotkeyGesture(modifiers, Key.None).ToString().Replace("None", "…");
        }
    }

    private void ShowGesture()
    {
        if (!IsKeyboardFocused)
        {
            Text = string.IsNullOrEmpty(Gesture) ? Loc.T("Hotkey.NotSet") : Gesture;
        }
    }

    private void MoveFocusAway()
    {
        Keyboard.ClearFocus();
        ShowGesture();
    }
}
