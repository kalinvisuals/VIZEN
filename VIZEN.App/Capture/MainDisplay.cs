using System.Runtime.InteropServices;

namespace VIZEN.App.Capture;

/// <summary>
/// Display facts used by the diagnostics and the test pattern. (WPF's own composition rate is deliberately not
/// measured: subscribing to CompositionTarget.Rendering would make every window redraw at the screen's refresh rate.)
/// </summary>
public static class MainDisplay
{
    /// <summary>Refresh rate of the main display in Hz (0 when Windows does not say).</summary>
    public static double DisplayHz { get; } = ReadDisplayHz();

    private static double ReadDisplayHz()
    {
        try
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            return EnumDisplaySettings(null, -1, ref mode) ? mode.dmDisplayFrequency : 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
}
