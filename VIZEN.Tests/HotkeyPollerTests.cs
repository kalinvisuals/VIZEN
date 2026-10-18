using System.Windows.Input;
using VIZEN.App.Models;

namespace VIZEN.Tests;

public class HotkeyPollerTests
{
    private const int Ctrl = 0x11, Shift = 0x10, Alt = 0x12, F7 = 0x76, F6 = 0x75;

    private readonly HashSet<int> _down = [];
    private DateTime _now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private readonly HotkeyPoller _poller;
    private static readonly (int, HotkeyGesture)[] Shortcuts =
    [
        (5, new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F7)),
        (6, new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F6)),
    ];

    public HotkeyPollerTests() => _poller = new HotkeyPoller(_down.Contains, () => _now);

    private List<int> Poll(int ms = 30)
    {
        _now += TimeSpan.FromMilliseconds(ms);
        return _poller.Poll(Shortcuts);
    }

    private void Press(params int[] keys) => _down.UnionWith(keys);

    private void ReleaseAll() => _down.Clear();

    [Fact]
    public void NothingHeldFiresNothing() => Assert.Empty(Poll());

    [Fact]
    public void PressFiresOnceWhileHeld()
    {
        Press(Ctrl, Shift, F7);
        Assert.Equal([5], Poll());
        Assert.Empty(Poll());
        Assert.Empty(Poll(1000)); // still held a second later: no repeat
    }

    [Fact]
    public void SecondPressAfterReleaseFiresAgain()
    {
        Press(Ctrl, Shift, F7);
        Assert.Equal([5], Poll());
        ReleaseAll();
        Assert.Empty(Poll(300));
        Press(Ctrl, Shift, F7);
        Assert.Equal([5], Poll());
    }

    [Fact]
    public void ModifiersMustMatchExactly()
    {
        Press(Ctrl, F7);                 // Shift missing
        Assert.Empty(Poll());
        Press(Shift, Alt);               // Alt extra
        Assert.Empty(Poll());
        _down.Remove(Alt);
        Assert.Equal([5], Poll());
    }

    [Fact]
    public void KeyWithoutModifiersIsIgnored()
    {
        Press(F7);                       // F7 alone is a game key, never VIZEN's shortcut
        Assert.Empty(Poll());
    }

    [Fact]
    public void RegisteredPressFirstIsNotRepeatedByPolling()
    {
        Press(Ctrl, Shift, F7);
        Assert.True(_poller.OnRegisteredPress(5)); // WM_HOTKEY arrived first (desktop, Chrome)
        Assert.Empty(Poll());
        Assert.Empty(Poll());
    }

    [Fact]
    public void PollingFirstIgnoresTheLateRegisteredPress()
    {
        Press(Ctrl, Shift, F7);
        Assert.Equal([5], Poll());
        _now += TimeSpan.FromMilliseconds(20);
        Assert.False(_poller.OnRegisteredPress(5)); // same press, reported by Windows a moment later
    }

    [Fact]
    public void QuickTapSeenOnlyByRegisterHotKeyStillCountsOnce()
    {
        Assert.True(_poller.OnRegisteredPress(5)); // pressed and released between two polls
        Assert.Empty(Poll());
        _now += TimeSpan.FromMilliseconds(400);
        Assert.True(_poller.OnRegisteredPress(5)); // a new press later is a new action
    }

    [Fact]
    public void TwoShortcutsAreIndependent()
    {
        Press(Ctrl, Shift, F7);
        Assert.Equal([5], Poll());
        _down.Remove(F7);
        Press(F6);
        Assert.Equal([6], Poll());
    }

    [Fact]
    public void PressedTwiceForClearConfirmationCountsTwice()
    {
        // Ctrl+Shift+F4 is pressed twice to clear: two real presses ~300 ms apart are two actions.
        Press(Ctrl, Shift, F7);
        Assert.Equal([5], Poll());
        _down.Remove(F7);
        Assert.Empty(Poll(150));
        Press(F7);
        Assert.Equal([5], Poll(150));
    }
}
