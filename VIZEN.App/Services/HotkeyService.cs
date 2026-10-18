using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;

namespace VIZEN.App.Services;

public enum HotkeyAction
{
    ToggleClickThrough,
    ToggleVisibility,
    ToggleNotes,
    NotesEditMode,
    ToggleSmartDraw,
    DrawMode,
    ToggleAnnotations,
    ClearAnnotations,
    ToggleMixer,
}

public enum HotkeyStatus
{
    Off,
    Active,
    Conflict,
}

/// <summary>A system-wide shortcut and whether Windows accepted its registration.</summary>
public sealed class HotkeyBinding : ObservableObject
{
    private readonly string _nameKey;
    private HotkeyStatus _status = HotkeyStatus.Off;
    private HotkeyGesture _gesture;

    public HotkeyBinding(int id, HotkeyAction action, string nameKey, HotkeyGesture defaultGesture)
    {
        Id = id;
        Action = action;
        _nameKey = nameKey;
        DefaultGesture = defaultGesture;
        _gesture = defaultGesture;
    }

    public int Id { get; }
    public HotkeyAction Action { get; }
    public string Name => Loc.T(_nameKey);
    public HotkeyGesture DefaultGesture { get; }

    public HotkeyGesture Current
    {
        get => _gesture;
        internal set
        {
            if (SetProperty(ref _gesture, value))
            {
                OnPropertyChanged(nameof(Gesture));
                OnPropertyChanged(nameof(IsDefault));
            }
        }
    }

    public string Gesture => _gesture.ToString();

    public bool IsDefault => _gesture == DefaultGesture;

    public HotkeyStatus Status
    {
        get => _status;
        internal set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsRegistered));
                OnPropertyChanged(nameof(IsConflict));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsRegistered => Status == HotkeyStatus.Active;

    public bool IsConflict => Status == HotkeyStatus.Conflict;

    public string StatusText => Loc.T($"Hotkey.Status.{Status}");
}

/// <summary>
/// Registers global hotkeys with RegisterHotKey on a message-only window, without any keyboard hook. Some games
/// (CS2) keep RegisterHotKey shortcuts from other apps while they are in front, so the same shortcuts are also
/// noticed by reading the state of their keys every ~30 ms (<see cref="HotkeyPoller"/>); each press acts once.
/// Besides the built-in actions, other features (e.g. media schedules) can register extra shortcuts. UI thread only.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _window;
    private readonly Dictionary<string, Extra> _extras = [];
    private readonly HotkeyPoller _poller = new(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);
    private readonly DispatcherTimer _pollTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
    private int _nextExtraId = 100;
    private bool _enabled;

    public HotkeyService()
    {
        _window = new HwndSource(new HwndSourceParameters("VIZEN.Hotkeys") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _pollTimer.Tick += (_, _) => Poll();

        Bindings =
        [
            new HotkeyBinding(1, HotkeyAction.ToggleClickThrough, "Hotkey.ToggleClickThrough",
                new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F9)),
            new HotkeyBinding(2, HotkeyAction.ToggleVisibility, "Hotkey.ToggleVisibility",
                new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F10)),
            new HotkeyBinding(3, HotkeyAction.ToggleNotes, "Hotkey.ToggleNotes",
                new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F11)),
            new HotkeyBinding(4, HotkeyAction.NotesEditMode, "Hotkey.NotesEditMode",
                new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F8)),
            new HotkeyBinding(5, HotkeyAction.ToggleSmartDraw, "Hotkey.ToggleSmartDraw",
                new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F7)),
            new HotkeyBinding(6, HotkeyAction.DrawMode, "Hotkey.DrawMode",
                new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F6)),
            new HotkeyBinding(7, HotkeyAction.ToggleAnnotations, "Hotkey.ToggleAnnotations",
                new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F5)),
            new HotkeyBinding(8, HotkeyAction.ClearAnnotations, "Hotkey.ClearAnnotations",
                new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F4)),
            new HotkeyBinding(9, HotkeyAction.ToggleMixer, "Hotkey.ToggleMixer",
                new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift, Key.F3)),
        ];
    }

    public IReadOnlyList<HotkeyBinding> Bindings { get; }

    public bool IsEnabled => _enabled;

    /// <summary>Raised on the UI thread when a built-in hotkey is pressed.</summary>
    public event EventHandler<HotkeyAction>? Pressed;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        foreach (var binding in Bindings)
        {
            binding.Status = Register(binding.Id, binding.Current);
        }

        foreach (var extra in _extras.Values)
        {
            extra.Status = Register(extra.Id, extra.Gesture);
        }

        _pollTimer.IsEnabled = enabled;
    }

    /// <summary>Changes a built-in shortcut. Returns the new status (Conflict if Windows or VIZEN already uses it).</summary>
    public HotkeyStatus ChangeGesture(HotkeyBinding binding, HotkeyGesture gesture)
    {
        if (!gesture.IsValid)
        {
            return HotkeyStatus.Conflict;
        }

        binding.Current = gesture;
        binding.Status = IsTakenByOther(binding.Id, gesture) ? Fail(binding.Id) : Register(binding.Id, gesture);
        return binding.Status;
    }

    /// <summary>
    /// Registers an extra shortcut under <paramref name="key"/> (replacing any previous one with that key).
    /// Returns Off while hotkeys are disabled, Conflict when the gesture is taken.
    /// </summary>
    public HotkeyStatus RegisterExtra(string key, HotkeyGesture gesture, Action callback)
    {
        UnregisterExtra(key);
        if (!gesture.IsValid)
        {
            return HotkeyStatus.Conflict;
        }

        var extra = new Extra(_nextExtraId++, gesture, callback);
        _extras[key] = extra;
        extra.Status = IsTakenByOther(extra.Id, gesture) ? Fail(extra.Id) : Register(extra.Id, gesture);
        return extra.Status;
    }

    public void UnregisterExtra(string key)
    {
        if (_extras.Remove(key, out var extra))
        {
            UnregisterHotKey(_window.Handle, extra.Id);
        }
    }

    public HotkeyStatus StatusOf(string key) => _extras.TryGetValue(key, out var extra) ? extra.Status : HotkeyStatus.Off;

    private bool IsTakenByOther(int id, HotkeyGesture gesture) =>
        Bindings.Any(b => b.Id != id && b.Current == gesture)
        || _extras.Values.Any(e => e.Id != id && e.Gesture == gesture);

    private HotkeyStatus Fail(int id)
    {
        UnregisterHotKey(_window.Handle, id);
        return _enabled ? HotkeyStatus.Conflict : HotkeyStatus.Off;
    }

    private HotkeyStatus Register(int id, HotkeyGesture gesture)
    {
        UnregisterHotKey(_window.Handle, id);
        if (!_enabled)
        {
            return HotkeyStatus.Off;
        }

        return RegisterHotKey(_window.Handle, id, gesture.Win32Modifiers | MOD_NOREPEAT, gesture.VirtualKey)
            ? HotkeyStatus.Active
            : HotkeyStatus.Conflict;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            handled = true;
            if (_poller.OnRegisteredPress(id))
            {
                Fire(id);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>The shortcuts Windows accepted (a Conflict one belongs to another app, so it is not watched either).</summary>
    private IEnumerable<(int Id, HotkeyGesture Gesture)> ActiveShortcuts() =>
        Bindings.Where(b => b.Status == HotkeyStatus.Active).Select(b => (b.Id, b.Current))
            .Concat(_extras.Values.Where(e => e.Status == HotkeyStatus.Active).Select(e => (e.Id, e.Gesture)));

    private void Poll()
    {
        foreach (var id in _poller.Poll(ActiveShortcuts()))
        {
            Fire(id);
        }
    }

    private void Fire(int id)
    {
        if (Bindings.FirstOrDefault(b => b.Id == id) is { } binding)
        {
            Pressed?.Invoke(this, binding.Action);
        }
        else if (_extras.Values.FirstOrDefault(e => e.Id == id) is { } extra)
        {
            extra.Callback();
        }
    }

    public void Dispose()
    {
        SetEnabled(false);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private sealed class Extra(int id, HotkeyGesture gesture, Action callback)
    {
        public int Id { get; } = id;
        public HotkeyGesture Gesture { get; } = gesture;
        public Action Callback { get; } = callback;
        public HotkeyStatus Status { get; set; }
    }
}
