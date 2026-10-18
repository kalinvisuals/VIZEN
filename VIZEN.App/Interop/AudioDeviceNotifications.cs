using System.Runtime.InteropServices;

namespace VIZEN.App.Interop;

/// <summary>
/// Windows audio endpoint notifications (IMMNotificationClient): an output device was added, removed, enabled,
/// disabled or became the default. Windows calls <paramref name="changed"/> on one of its own threads.
/// NAudio keeps its own definition of the interface internal, so the documented Core Audio interfaces are declared here.
/// </summary>
public sealed class AudioDeviceNotifications : IDisposable
{
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly Client _client;
    private bool _registered;

    private AudioDeviceNotifications(IMMDeviceEnumerator enumerator, Client client)
    {
        _enumerator = enumerator;
        _client = client;
    }

    /// <summary>Starts receiving notifications; null when Windows refuses (the caller then relies on its periodic scan).</summary>
    public static AudioDeviceNotifications? TryStart(Action changed)
    {
        try
        {
            if (Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid) is not { } type
                || Activator.CreateInstance(type) is not IMMDeviceEnumerator enumerator)
            {
                return null;
            }

            var notifications = new AudioDeviceNotifications(enumerator, new Client(changed));
            if (enumerator.RegisterEndpointNotificationCallback(notifications._client) != 0)
            {
                Marshal.ReleaseComObject(enumerator);
                return null;
            }

            notifications._registered = true;
            return notifications;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_client);
        }
        catch (COMException)
        {
            // shutting down
        }

        Marshal.ReleaseComObject(_enumerator);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Client(Action changed) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => Notify();
        public int OnDeviceAdded(string deviceId) => Notify();
        public int OnDeviceRemoved(string deviceId) => Notify();
        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId) => Notify();
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0; // frequent and irrelevant (names, formats)

        private int Notify()
        {
            changed();
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }
}
