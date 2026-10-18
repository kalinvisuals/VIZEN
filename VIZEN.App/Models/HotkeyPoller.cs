using System.Windows.Input;

namespace VIZEN.App.Models;

/// <summary>
/// Second way of noticing VIZEN's shortcuts, for games that keep RegisterHotKey shortcuts from reaching other apps
/// while they are in front (CS2 does). Every ~30 ms the current state of the shortcut keys
/// is read (GetAsyncKeyState, the method OBS Studio uses for its hotkeys): nothing is hooked, injected or sent, and
/// only the keys of VIZEN's own shortcuts are looked at. A shortcut fires once when its key goes down with exactly
/// its modifiers held; if RegisterHotKey already reported the same press it is not fired twice.
/// </summary>
public sealed class HotkeyPoller
{
    /// <summary>A press reported by one way is not reported again by the other within this time.</summary>
    public static readonly TimeSpan SamePressWindow = TimeSpan.FromMilliseconds(250);

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly Func<int, bool> _isDown;
    private readonly Func<DateTime> _clock;
    private readonly HashSet<int> _held = [];
    private readonly Dictionary<int, DateTime> _lastFired = [];

    /// <param name="isDown">Whether a virtual key is down now (GetAsyncKeyState in the app, a fake in tests).</param>
    public HotkeyPoller(Func<int, bool> isDown, Func<DateTime>? clock = null)
    {
        _isDown = isDown;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Reads the keys once and returns the ids of the shortcuts pressed since the last call (not yet reported by
    /// RegisterHotKey). Cheap when nothing is held: four modifier reads, then it stops.
    /// </summary>
    public List<int> Poll(IEnumerable<(int Id, HotkeyGesture Gesture)> shortcuts)
    {
        var fired = new List<int>();
        var held = ModifierKeys.None;
        if (_isDown(VK_CONTROL)) held |= ModifierKeys.Control;
        if (_isDown(VK_SHIFT)) held |= ModifierKeys.Shift;
        if (_isDown(VK_MENU)) held |= ModifierKeys.Alt;
        if (_isDown(VK_LWIN) || _isDown(VK_RWIN)) held |= ModifierKeys.Windows;

        foreach (var (id, gesture) in shortcuts)
        {
            var down = held != ModifierKeys.None && held == gesture.Modifiers && _isDown((int)gesture.VirtualKey);
            if (!down)
            {
                _held.Remove(id); // released: the next press counts again
                continue;
            }

            if (_held.Add(id) && !FiredRecently(id))
            {
                _lastFired[id] = _clock();
                fired.Add(id);
            }
        }

        return fired;
    }

    /// <summary>
    /// RegisterHotKey reported a press. False when polling already reported this press (the caller then ignores it).
    /// </summary>
    public bool OnRegisteredPress(int id)
    {
        if (FiredRecently(id))
        {
            return false;
        }

        _lastFired[id] = _clock();
        _held.Add(id); // the poll that sees this key still down must not report it again
        return true;
    }

    private bool FiredRecently(int id) => _lastFired.TryGetValue(id, out var at) && _clock() - at < SamePressWindow;
}
