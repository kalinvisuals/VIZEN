using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Forms = System.Windows.Forms;

namespace VIZEN.App.Services;

/// <summary>Spec-based expectation for this PC. An estimate: real results depend on the game, sources and drivers.</summary>
public enum PerformanceTier
{
    BelowMinimum,

    /// <summary>One or two 720p30 overlays.</summary>
    Minimum,

    /// <summary>1080p60 with several sources.</summary>
    Recommended,

    /// <summary>Many 1080p60 sources next to a demanding game, high-refresh displays.</summary>
    Professional,
}

public sealed record GpuInfo(string Name, double DedicatedMemoryGb, double SharedMemoryGb, bool IsLikelyIntegrated);

public sealed record DisplayInfo(string Name, int Width, int Height, int RefreshHz, bool IsPrimary);

/// <summary>What VIZEN detected about this PC. Every value comes from Windows; none is guessed.</summary>
public sealed record SystemInfo(
    string CpuName,
    int LogicalProcessors,
    double MemoryGb,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<DisplayInfo> Displays,
    string WindowsVersion,
    int WindowsBuild,
    bool WindowCaptureSupported,
    bool BorderlessCaptureSupported,
    bool AppAudioSupported,
    string? WebView2Version,
    bool OnBattery)
{
    public bool HasHardwareGpu => Gpus.Count > 0;

    public double MaxDedicatedVideoGb => Gpus.Count == 0 ? 0 : Gpus.Max(g => g.DedicatedMemoryGb);

    public bool HasDedicatedGpu => Gpus.Any(g => !g.IsLikelyIntegrated);

    public int MaxRefreshHz => Displays.Count == 0 ? 0 : Displays.Max(d => d.RefreshHz);
}

/// <summary>
/// Detects the hardware and Windows features VIZEN relies on and turns them into a tier estimate and
/// recommendations. Detection reads Windows (registry, DXGI, display settings) and never sends anything anywhere.
/// </summary>
public static class SystemCompatibility
{
    public static SystemInfo Detect(bool windowCaptureSupported)
    {
        var build = Environment.OSVersion.Version.Build;
        return new SystemInfo(
            CpuName: CpuName(),
            LogicalProcessors: Environment.ProcessorCount,
            MemoryGb: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0,
            Gpus: Gpus(),
            Displays: Displays(),
            WindowsVersion: WindowsVersion(build),
            WindowsBuild: build,
            WindowCaptureSupported: windowCaptureSupported,
            BorderlessCaptureSupported: OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348),
            AppAudioSupported: AppAudioService.IsListenSupported,
            WebView2Version: WebView2Version(),
            OnBattery: Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline);
    }

    /// <summary>
    /// Spec thresholds (memory slightly under the nominal size, because Windows reserves some):
    /// Minimum 4 threads, 8 GB, a hardware GPU; Recommended 8 threads, 16 GB;
    /// Professional 12 threads, 32 GB and a GPU with at least 6 GB of its own memory.
    /// </summary>
    internal static PerformanceTier EstimateTier(int logicalProcessors, double memoryGb, bool hasHardwareGpu,
        double dedicatedVideoGb, bool windowCaptureSupported)
    {
        if (!windowCaptureSupported || !hasHardwareGpu || logicalProcessors < 4 || memoryGb < 7.5)
        {
            return PerformanceTier.BelowMinimum;
        }

        if (logicalProcessors >= 12 && memoryGb >= 31 && dedicatedVideoGb >= 6)
        {
            return PerformanceTier.Professional;
        }

        return logicalProcessors >= 8 && memoryGb >= 15 ? PerformanceTier.Recommended : PerformanceTier.Minimum;
    }

    public static PerformanceTier EstimateTier(SystemInfo info) =>
        EstimateTier(info.LogicalProcessors, info.MemoryGb, info.HasHardwareGpu, info.MaxDedicatedVideoGb, info.WindowCaptureSupported);

    /// <summary>Localization keys (with arguments) of the advice that applies to this PC.</summary>
    internal static IReadOnlyList<(string Key, object[] Args)> Recommendations(SystemInfo info)
    {
        var list = new List<(string, object[])>();
        if (!info.WindowCaptureSupported)
        {
            list.Add(("Rec.NoCapture", []));
        }

        if (!info.HasHardwareGpu)
        {
            list.Add(("Rec.NoGpu", []));
        }
        else if (!info.HasDedicatedGpu)
        {
            list.Add(("Rec.IntegratedGpu", []));
        }

        if (info.MemoryGb < 15)
        {
            list.Add(("Rec.LowMemory", []));
        }

        if (info.LogicalProcessors < 8)
        {
            list.Add(("Rec.FewThreads", []));
        }

        if (info.OnBattery)
        {
            list.Add(("Rec.OnBattery", []));
        }

        if (info.MaxRefreshHz > 60)
        {
            list.Add(("Rec.HighRefresh", [info.MaxRefreshHz]));
        }

        if (!info.AppAudioSupported)
        {
            list.Add(("Rec.NoAppAudio", []));
        }

        if (info.WebView2Version is null)
        {
            list.Add(("Rec.NoWebView2", []));
        }

        list.Add(("Rec.AutoPerformance", []));
        return list;
    }

    private static string CpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() is { Length: > 0 } name ? name : "?";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return "?";
        }
    }

    private static string WindowsVersion(int build)
    {
        string? display = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
        }

        // ProductName still says "Windows 10" on Windows 11; the build number tells them apart.
        var name = build >= 22000 ? "Windows 11" : "Windows 10";
        return display is null ? $"{name} (build {build})" : $"{name} {display} (build {build})";
    }

    private static string? WebView2Version()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or DllNotFoundException)
        {
            return null;
        }
    }

    private static List<DisplayInfo> Displays()
    {
        var list = new List<DisplayInfo>();
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            var hz = EnumDisplaySettings(screen.DeviceName, EnumCurrentSettings, ref mode) ? mode.dmDisplayFrequency : 0;
            list.Add(new DisplayInfo(screen.DeviceName.TrimStart('\\', '.'), screen.Bounds.Width, screen.Bounds.Height,
                hz, screen.Primary));
        }

        return list;
    }

    private static unsafe List<GpuInfo> Gpus()
    {
        var list = new List<GpuInfo>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == IntPtr.Zero)
        {
            return list;
        }

        try
        {
            var vtable = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)vtable[12];
            for (uint i = 0; i < 16; i++)
            {
                if (enumAdapters1(factory, i, out var adapter) < 0 || adapter == IntPtr.Zero)
                {
                    break; // DXGI_ERROR_NOT_FOUND: no more adapters
                }

                try
                {
                    var adapterTable = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)adapterTable[10];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) < 0 || (desc.Flags & 2) != 0 || desc.VendorId == 0x1414)
                    {
                        continue; // software rasterizer (Microsoft Basic Render Driver)
                    }

                    var name = new string(desc.Description).TrimEnd('\0').Trim();
                    var dedicated = desc.DedicatedVideoMemory / 1073741824.0;
                    var shared = desc.SharedSystemMemory / 1073741824.0;
                    if (list.Any(g => g.Name == name && Math.Abs(g.DedicatedMemoryGb - dedicated) < 0.01))
                    {
                        continue; // the same adapter listed once per output on some systems
                    }

                    // Integrated GPUs have little or no memory of their own and borrow system memory.
                    list.Add(new GpuInfo(name, dedicated, shared, dedicated < 1));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return list;
    }

    private const int EnumCurrentSettings = -1;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
