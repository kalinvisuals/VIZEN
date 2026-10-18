using System.Windows.Input;
using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Services;

namespace VIZEN.App.ViewModels;

/// <summary>An output device choice for "listen on".</summary>
public sealed class ListenDeviceOption : ObservableObject
{
    private readonly AppAudioViewModel _owner;

    public ListenDeviceOption(AudioOutputDevice device, AppAudioViewModel owner)
    {
        Device = device;
        _owner = owner;
    }

    public AudioOutputDevice Device { get; }

    public string Name => Device.Name;

    public bool IsSelected
    {
        get => _owner.SelectedDeviceId == Device.Id;
        set
        {
            if (value)
            {
                _owner.SelectedDeviceId = Device.Id;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>Sound controls for one source's app (volume, mute, level meter, listen), shown on cards and in Sources.</summary>
public sealed class AppAudioViewModel : ObservableObject
{
    private readonly AppAudio _audio;
    private readonly ActivityLog _activity;
    private string? _selectedDeviceId;
    private string? _listenError;
    private bool _busy;

    public AppAudioViewModel(AppAudio audio, IReadOnlyList<AudioOutputDevice> devices, ActivityLog activity)
    {
        _audio = audio;
        _activity = activity;
        Devices = devices.Select(d => new ListenDeviceOption(d, this)).ToList();
        _selectedDeviceId = Devices.LastOrDefault()?.Device.Id;
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted, () => HasAudio);
        audio.Changed += OnAudioChanged;
    }

    public string AppName => _audio.AppName;

    /// <summary>Unique radio-button group for this control instance.</summary>
    public string GroupId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The app has an audio session (it has played or is playing sound since it started).</summary>
    public bool HasAudio => _audio.HasSessions;

    public string StatusText => !HasAudio
        ? Loc.T("Audio.NoSession")
        : _audio.IsPlayingSound ? Loc.T("Audio.Playing") : Loc.T("Audio.Silent");

    public double Peak => _audio.Peak;

    public double VolumePercent
    {
        get => Math.Round(_audio.Volume * 100);
        set
        {
            _audio.Volume = (float)(Math.Clamp(value, 0, 100) / 100);
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
        }
    }

    public string VolumeText => $"{VolumePercent:0}%";

    public bool Muted
    {
        get => _audio.Muted;
        set
        {
            if (value == _audio.Muted || !HasAudio)
            {
                return;
            }

            _audio.Muted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MuteGlyph));
            _activity.Add(value ? "" : "", value ? "Audio.MutedOn" : "Audio.MutedOff", "Audio.MutedDetail", AppName);
        }
    }

    public string MuteGlyph => Muted ? "" : "";

    public static bool IsListenSupported => AppAudioService.IsListenSupported;

    public IReadOnlyList<ListenDeviceOption> Devices { get; }

    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set
        {
            if (_selectedDeviceId == value)
            {
                return;
            }

            _selectedDeviceId = value;
            foreach (var device in Devices)
            {
                device.Refresh();
            }

            if (IsListening)
            {
                _ = SetListeningAsync(true); // move to the new device
            }
        }
    }

    /// <summary>Plays just this app's sound on the selected device as well.</summary>
    public bool IsListening
    {
        get => _audio.IsListening;
        set => _ = SetListeningAsync(value);
    }

    public string? ListenStatus => _listenError ?? (_audio.IsListening ? Loc.F("Audio.ListeningOn", _audio.ListeningDeviceName) : null);

    public ICommand ToggleMuteCommand { get; }

    private async Task SetListeningAsync(bool listen)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (listen)
            {
                var device = Devices.FirstOrDefault(d => d.Device.Id == _selectedDeviceId)?.Device;
                var error = await _audio.StartListeningAsync(device?.Id);
                _listenError = error is null ? null : Loc.F("Audio.ListenFailed", error);
                _activity.Add("", error is null ? "Audio.ListenOn" : "Audio.ListenFailedTitle",
                    error is null ? "Audio.ListenOnDetail" : ActivityLog.RawDetail,
                    error is null ? [AppName, device?.Name ?? Loc.T("Audio.DefaultDevice")] : [_listenError]);
            }
            else if (_audio.IsListening)
            {
                _audio.StopListening();
                _listenError = null;
                _activity.Add("", "Audio.ListenOff", "Audio.ListenOffDetail", AppName);
            }
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(ListenStatus));
        }
    }

    private void OnAudioChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Peak));
        OnPropertyChanged(nameof(HasAudio));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(VolumePercent));
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(Muted));
        OnPropertyChanged(nameof(MuteGlyph));
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(ListenStatus));
    }

    /// <summary>Stops listening to the app (when the card or page that owns this goes away).</summary>
    public void Detach() => _audio.Changed -= OnAudioChanged;
}
