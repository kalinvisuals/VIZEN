using VIZEN.App.Capture;
using VIZEN.App.Models;
using VIZEN.App.ViewModels;

namespace VIZEN.Tests;

public class ReliabilityTests
{
    [Theory]
    [InlineData(unchecked((int)0x887A0005), true)]  // device removed
    [InlineData(unchecked((int)0x887A0007), true)]  // device reset
    [InlineData(unchecked((int)0x887A0006), true)]  // device hung
    [InlineData(unchecked((int)0x887A0027), false)] // wait timeout: transient
    [InlineData(unchecked((int)0x80070057), false)] // E_INVALIDARG
    public void ClassifiesDeviceLoss(int hresult, bool lost) => Assert.Equal(lost, CaptureDevice.IsDeviceLost(hresult));

    private static WindowSource Window(int handle, string exe, string title) => new()
    {
        Handle = handle,
        ProcessName = Path.GetFileNameWithoutExtension(exe),
        ProcessId = (uint)handle,
        ExecutablePath = exe,
        ClassName = "c",
        Title = title,
    };

    [Fact]
    public void ReopenedCallWindowIsFound()
    {
        var closed = Window(1, @"C:\zoom.exe", "Zoom Meeting");
        var windows = new[] { Window(2, @"C:\zoom.exe", "Zoom Workplace"), Window(3, @"C:\zoom.exe", "Zoom Meeting") };
        Assert.Equal((IntPtr)3, SourceReconnector.FindReplacement(closed, windows)!.Handle);
    }

    [Fact]
    public void SameAppWithUnrelatedTitleIsNotUsed()
    {
        // A different Chrome window must not silently replace a closed "Messenger call" window.
        var closed = Window(1, @"C:\chrome.exe", "Apel Messenger – Google Chrome");
        var windows = new[] { Window(2, @"C:\chrome.exe", "Gmail – Google Chrome") };
        Assert.Null(SourceReconnector.FindReplacement(closed, windows));
    }

    [Fact]
    public void TheSameClosedHandleIsNeverReused()
    {
        var closed = Window(5, @"C:\obs.exe", "Projector");
        Assert.Null(SourceReconnector.FindReplacement(closed, [closed]));
    }
}
