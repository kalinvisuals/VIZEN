# VIZEN and games: anti-cheat safety

VIZEN shows windows over games. Anti-cheat systems (VAC, FACEIT, ESEA, Vanguard, EasyAntiCheat, BattlEye) look for
programs that change a game or read its memory, so VIZEN is built to stay entirely outside the game process. This
document states what VIZEN does and does not do, and is a **rule for all future development**: a change that breaks one
of the rules below must not be merged.

## Rules

1. **No injection.** VIZEN never loads code into another process: no DLL injection, no `CreateRemoteThread`, no
   `SetWindowsHookEx` hooks that load into other processes, no graphics hooks (OBS "Game Capture" style).
2. **No access to game memory.** No `ReadProcessMemory` / `WriteProcessMemory` / `VirtualAllocEx`. Other processes are
   opened only with `PROCESS_QUERY_LIMITED_INFORMATION`, to read the executable name shown in the source list.
3. **No drivers.** VIZEN installs no kernel driver and no service.
4. **No input into the game.** VIZEN does not send keys or mouse input to the game (`SendInput`, `keybd_event`,
   `mouse_event` are not used by the app). Overlays are click-through: clicks go to the game unchanged.
5. **No anti-cheat or DRM circumvention.** VIZEN does not detect, hide from, or work around anti-cheat or protected
   content. If Windows refuses to capture a window, VIZEN shows that it is unavailable.
6. **No game information is read or shown.** VIZEN only shows other windows (browser, call, video), never data taken
   from the game (positions, health, map). That is exactly what separates it from an "external overlay" cheat, which
   uses a similar transparent window but fills it with data read from the game's memory.

## What VIZEN uses, all public Windows APIs

| Feature | How | Touches the game? |
|---|---|---|
| Window capture | Windows Graphics Capture (the API used by OBS "Window Capture" and the Windows Snipping Tool) | No: Windows composes the window image |
| Overlays | Ordinary top-level windows: topmost, layered, click-through (`WS_EX_TRANSPARENT`) | No |
| Staying above borderless games | `SetWindowPos(HWND_TOPMOST, …, SWP_NOACTIVATE)` on VIZEN's own windows, on foreground changes and every 2 s | No: only VIZEN's windows move |
| Exclusive-fullscreen detection | `SHQueryUserNotificationState` (Windows reports a Direct3D exclusive-fullscreen app), plus noticing that a full-screen window minimized right after an overlay was raised | No: VIZEN then holds its overlays back and asks the user to switch the game to borderless |
| Noticing foreground changes and minimizes | `SetWinEventHook` with `WINEVENT_OUTOFCONTEXT` (events are delivered to VIZEN; nothing is loaded into other processes) | No |
| Smart Notes and Smart Draw | Ordinary topmost windows of VIZEN's own (Smart Draw: a transparent surface per monitor, click-through in pass-through with `WS_EX_TRANSPARENT`); drawings are vector data rendered by WPF | No: nothing is read from or sent to the game |
| Global hotkeys | `RegisterHotKey` (no keyboard hook). Some games (CS2) tell Windows not to deliver other apps' `RegisterHotKey` shortcuts while they are in front, so VIZEN also reads the state of its own shortcut keys every ~30 ms with `GetAsyncKeyState` (the method OBS Studio uses for its hotkeys). Only the keys of VIZEN's shortcuts are read (Ctrl, Shift, Alt, Win and the shortcut keys), nothing is recorded, hooked or sent; a press acts once | No: key state is read from Windows, not from the game, and the game still receives every key |
| Per-app volume and Smart Audio Mixer | Windows Core Audio: the list of audio sessions, each session's volume and mute (`ISimpleAudioVolume`, the same control as the Windows Volume Mixer), its level meter, and endpoint notifications when an output device changes. The master volume is never changed. The floating mixer is an ordinary topmost VIZEN window that does not take the focus | No: Windows applies the volume to the game's audio session; nothing is loaded into or read from the game |
| Source list | Window enumeration, window titles, executable names | No |
| Browser tabs (optional VIZEN Connector extension) | Only when the user presses Connect: a native messaging host manifest in the VIZEN data folder and three per-user registry keys (Chrome, Edge, Brave; no administrator rights). The browser starts VIZEN.App.exe as a small relay (no window) that talks to the running app over a named pipe only the current Windows user can open. The extension asks for `tabs`, `nativeMessaging` and `tabCapture` only (no access to page content, no content scripts, no host permissions) and sends tab titles and site names, never full addresses or incognito tabs. Nothing goes over the network. Tabs are switched, muted or moved to their own window only on the user's request; a tab is captured alone only after the user clicks "Mirror this tab live" in the extension (Chrome requires that click). Disconnect removes the manifest and the keys | No: the browser and its extension are ordinary programs; nothing touches the game |

## Exclusive fullscreen

In exclusive fullscreen the game owns the display; Windows minimizes it as soon as another window is placed above it.
Drawing over it would require injecting into the game, which rule 1 forbids. Since 0.4.3 VIZEN therefore does not raise
its overlays while a game is in exclusive fullscreen and shows a banner asking the user to switch the game to
**Fullscreen Windowed / Borderless**, where the overlays work.

## What cannot be promised

No third party can guarantee an anti-cheat decision: the rules of VAC and other systems are not public. What can be
said is that VIZEN does none of the things anti-cheat systems are known to ban for (modifying the game, reading its
memory, injecting code, automating input), and that windows over games are used every day by common software
(Discord, Steam, NVIDIA, MSI Afterburner), several of which do inject into games, which VIZEN does not. Users who want
no doubt at all on platforms with stricter anti-cheat (FACEIT, ESEA, Vanguard) can close VIZEN's overlays during
matches; no measurements exist for those platforms.

## Checking a change

Before merging, search the app code for the forbidden APIs (none may appear in `VIZEN.App`):

```
SetWindowsHookEx|ReadProcessMemory|WriteProcessMemory|VirtualAllocEx|CreateRemoteThread|SendInput|keybd_event|mouse_event
```

and check that every `OpenProcess` call asks only for `PROCESS_QUERY_LIMITED_INFORMATION`.
