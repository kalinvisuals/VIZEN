using VIZEN.App.Infrastructure;
using VIZEN.App.Localization;
using VIZEN.App.Models;
using Windows.Devices.Enumeration;

namespace VIZEN.App.Services;

/// <summary>A camera device reported by Windows. Detection only — VIZEN does not open the camera.</summary>
public sealed class CameraDevice : ObservableObject
{
    public CameraDevice(string id, string name, Panel? panel, bool isEnabled)
    {
        Id = id;
        Name = name;
        Panel = panel;
        IsEnabled = isEnabled;
    }

    public string Id { get; }
    public string Name { get; }
    public Panel? Panel { get; }
    public bool IsEnabled { get; }

    public string LocationText => Panel switch
    {
        Windows.Devices.Enumeration.Panel.Front => Loc.T("Camera.Front"),
        Windows.Devices.Enumeration.Panel.Back => Loc.T("Camera.Back"),
        _ => Loc.T("Camera.External"),
    };

    /// <summary>
    /// Represents the camera as a capture source, so it works everywhere a window does (preview, regions,
    /// overlays, OBS outputs, profiles). "camera:&lt;id&gt;" stands in for the executable path as its identity.
    /// </summary>
    public WindowSource ToSource() => new()
    {
        Handle = IntPtr.Zero,
        CameraDeviceId = Id,
        Title = Name,
        ProcessName = Name,
        ProcessId = 0,
        ExecutablePath = "camera:" + Id,
        ClassName = LocationText,
        Category = SourceCategory.Cameras,
        State = WindowDisplayState.Normal,
    };
}

/// <summary>
/// Lists video capture devices (webcams, capture cards, virtual cameras) via Windows device enumeration.
/// Enumerating does not turn the camera on and needs no camera permission.
/// </summary>
public sealed class CameraDeviceService
{
    public async Task<IReadOnlyList<CameraDevice>> FindCamerasAsync()
    {
        if (DemoMode.IsOn)
        {
            return []; // the PC's camera names are not shown in documentation screenshots
        }

        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices
            .Select(d => new CameraDevice(d.Id, d.Name, d.EnclosureLocation?.Panel, d.IsEnabled))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
