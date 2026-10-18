using System.Diagnostics;
using VIZEN.App.Capture;
using VIZEN.App.Services;

namespace VIZEN.Tests;

public class PerformanceTests
{
    private static readonly long Second = Stopwatch.Frequency;

    // ---- Health classification ----

    [Fact]
    public void Classify_SmoothSixtyFps_IsLive()
    {
        Assert.Equal(CaptureHealth.Live, FrameStats.Classify(60, 0, 0, 60, 16, true, false, false, 0));
    }

    [Fact]
    public void Classify_ThirtyFpsVideo_IsSourceLimitedNotVizen()
    {
        Assert.Equal(CaptureHealth.SourceLimited, FrameStats.Classify(30, 0, 0, 30, 33, true, false, false, 0));
        Assert.Equal(CaptureHealth.SourceLimited, FrameStats.Classify(24, 0, 0, 24, 40, true, false, false, 0));
    }

    [Fact]
    public void Classify_DropsWithoutEvidence_IsUnknown()
    {
        // Frames are lost but nothing points to a stage: say so instead of guessing.
        Assert.Equal(CaptureHealth.Unknown, FrameStats.Classify(60, 0, 12, 52.8, 16, true, false, false, 0));
    }

    [Fact]
    public void Classify_BusyUiThread_IsRenderLimited()
    {
        Assert.Equal(CaptureHealth.RenderLimited, FrameStats.Classify(60, 0, 12, 52.8, 16, true, false, false, 0,
            captureBusyFraction: 0.2, uiDelayMs: 20));
        Assert.Equal(CaptureHealth.RenderLimited, FrameStats.Classify(60, 0, 12, 52.8, 16, true, false, false, 0,
            presentBusyFraction: 0.7));
    }

    [Fact]
    public void Classify_SaturatedCaptureThread_BlamesVizenNotTheSource()
    {
        // Only 23 fps arrive, but VIZEN's capture thread is busy 90% of the time: Windows is being held back.
        Assert.Equal(CaptureHealth.CaptureLimited, FrameStats.Classify(23, 0, 0, 23, 40, true, false, false, 0,
            captureBusyFraction: 0.9, gpuWaitFraction: 0.6));
        Assert.Equal(CaptureHealth.ProcessingLimited, FrameStats.Classify(23, 0, 0, 23, 40, true, false, false, 0,
            captureBusyFraction: 0.9, gpuWaitFraction: 0.1));
    }

    [Fact]
    public void Classify_LowRateWithIdleCapture_IsSourceLimited_ButMixedEvidenceIsUnknown()
    {
        Assert.Equal(CaptureHealth.SourceLimited, FrameStats.Classify(24, 0, 0, 24, 40, true, false, false, 0, captureBusyFraction: 0.1));
        Assert.Equal(CaptureHealth.Unknown, FrameStats.Classify(24, 0, 0, 24, 40, true, false, false, 0, captureBusyFraction: 0.6));
    }

    [Fact]
    public void IsVizenLimited_CoversOnlyVizenStages()
    {
        Assert.True(CaptureStats.IsVizenLimited(CaptureHealth.CaptureLimited));
        Assert.True(CaptureStats.IsVizenLimited(CaptureHealth.RenderLimited));
        Assert.False(CaptureStats.IsVizenLimited(CaptureHealth.SourceLimited));
        Assert.False(CaptureStats.IsVizenLimited(CaptureHealth.Unknown));
    }

    // ---- Frame handoff (triple buffer), marker, percentiles ----

    [Fact]
    public void FrameHandoff_KeepsOnlyTheNewestFrame_AndNeverHandsTheWriterTheReadersSlot()
    {
        var handoff = new FrameHandoff();
        var first = handoff.WriteSlot;
        first.Width = 1;
        Assert.False(handoff.Publish(10));
        var second = handoff.WriteSlot;
        Assert.NotSame(first, second);
        second.Width = 2;
        Assert.True(handoff.Publish(20)); // replaced the unshown first frame

        var shown = handoff.TakeLatest();
        Assert.NotNull(shown);
        Assert.Equal(2, shown!.Width);
        Assert.Null(handoff.TakeLatest()); // nothing new
        Assert.NotSame(shown, handoff.WriteSlot); // the writer never gets the slot being displayed
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(123_456u)]
    [InlineData(uint.MaxValue)]
    public void FrameMarker_RoundTrips(uint index)
    {
        const int width = 400, height = 40, stride = width * 4;
        var buffer = new byte[stride * height];
        FrameMarker.Encode(buffer, width, height, stride, index, 8);
        Assert.Equal(index, FrameMarker.Decode(buffer, width, height, stride));
    }

    [Fact]
    public void FrameMarker_AbsentOnOrdinaryContent()
    {
        var buffer = new byte[400 * 4 * 40];
        Assert.Null(FrameMarker.Decode(buffer, 400, 40, 400 * 4));
    }

    [Fact]
    public void Roll_CountsSkippedAndRepeatedFramesFromMarkers()
    {
        var stats = new FrameStats();
        var start = Stopwatch.GetTimestamp();
        foreach (var index in new uint[] { 10, 11, 13, 13, 14 })
        {
            stats.OnMarker(index);
        }

        var s = stats.Roll(start + Second, true, false, false, 0, 10, 10);
        Assert.Equal(1, s.SkippedFrames);   // 12 never shown
        Assert.Equal(1, s.RepeatedFrames);  // 13 shown twice
    }

    [Fact]
    public void Percentile_UsesNearestRank()
    {
        var values = new List<double> { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        Assert.Equal(50, FrameStats.Percentile(values, 0.50));
        Assert.Equal(100, FrameStats.Percentile(values, 0.95));
        Assert.Equal(0, FrameStats.Percentile([], 0.5));
    }

    [Fact]
    public void Classify_SmallDrops_StayLive()
    {
        Assert.Equal(CaptureHealth.Live, FrameStats.Classify(60, 0, 3, 58.2, 16, true, false, false, 0));
    }

    [Fact]
    public void Classify_NoFramesForSeconds_IsNoNewFrames()
    {
        Assert.Equal(CaptureHealth.NoNewFrames, FrameStats.Classify(0, 0, 0, 0, 2500, true, false, false, 0));
    }

    [Fact]
    public void Classify_BriefPause_IsNotYetStalled()
    {
        Assert.NotEqual(CaptureHealth.NoNewFrames, FrameStats.Classify(0, 0, 0, 0, 800, true, false, false, 0));
    }

    [Fact]
    public void Classify_StateFlagsWinOverRates()
    {
        Assert.Equal(CaptureHealth.Ended, FrameStats.Classify(60, 0, 50, 30, 0, true, true, true, 0));
        Assert.Equal(CaptureHealth.Paused, FrameStats.Classify(0, 0, 0, 0, 9000, true, true, false, 0));
        Assert.Equal(CaptureHealth.Starting, FrameStats.Classify(0, 0, 0, 0, 0, false, false, false, 0));
    }

    [Fact]
    public void Classify_IntentionalCap_IsCapped()
    {
        Assert.Equal(CaptureHealth.Capped, FrameStats.Classify(60, 36, 0, 24, 16, true, false, false, 24));
    }

    // ---- Measurement window ----

    [Fact]
    public void Roll_CountsSourceShownDroppedAndLatency()
    {
        var stats = new FrameStats();
        var start = Stopwatch.GetTimestamp();
        var frame = Second / 60;
        for (var i = 0; i < 60; i++)
        {
            var at = start + i * frame;
            stats.OnArrived(at);
            stats.OnCopied(Second / 2000); // 0.5 ms
            if (i % 20 != 0)
            {
                // 57 of 60 shown, each 4 ms after it was received; 1 ms to display.
                stats.OnPresented(at + Second * 4 / 1000, at, Second / 1000);
            }
        }

        var s = stats.Roll(start + Second, isLive: true, isMinimized: false, hasEnded: false, maxFps: 0, 1920, 1080);

        Assert.InRange(s.SourceFps, 59, 61);
        Assert.InRange(s.PresentedFps, 56, 58);
        Assert.Equal(5, s.DroppedPercent, 1);
        Assert.NotNull(s.LatencyMs);
        Assert.Equal(4, s.LatencyMs!.Value, 1);
        Assert.Equal(0.5, s.CopyMs, 2);
        Assert.Equal(1, s.PresentMs, 2);
        Assert.Equal(1920, s.Width);
    }

    [Fact]
    public void Roll_ResetsBetweenWindows()
    {
        var stats = new FrameStats();
        var start = Stopwatch.GetTimestamp();
        stats.OnArrived(start);
        stats.OnCopied(1);
        stats.OnPresented(start + 10, start, 1);
        stats.Roll(start + Second, true, false, false, 0, 100, 100);

        var next = stats.Roll(start + 2 * Second, true, false, false, 0, 100, 100);
        Assert.Equal(0, next.SourceFps);
        Assert.Equal(0, next.PresentedFps);
        Assert.Null(next.LatencyMs);
    }

    [Fact]
    public void Roll_NeverReportsMoreShownThanDeliveredAsDrops()
    {
        // A presented count above copies (should not happen) must not produce negative drops.
        var stats = new FrameStats();
        var start = Stopwatch.GetTimestamp();
        stats.OnPresented(start + 1, 0, 1);
        var s = stats.Roll(start + Second, true, false, false, 0, 10, 10);
        Assert.Equal(0, s.DroppedPercent);
        Assert.Null(s.LatencyMs); // unknown source time is never turned into a number
    }

    // ---- Auto Performance diagnosis ----

    private static CaptureStats Limited(double copyMs = 0.5, double presentMs = 1) =>
        CaptureStats.Empty with { SourceFps = 60, PresentedFps = 50, DroppedPercent = 16, CopyMs = copyMs, PresentMs = presentMs, Health = CaptureHealth.Unknown };

    [Fact]
    public void Diagnose_PicksTheMeasuredBottleneck()
    {
        var idle = new ResourceSnapshot(2, 20, 300, 200, 1, 30);
        Assert.Equal("Perf.Cause.Gpu", AutoPerformanceService.Diagnose(Limited(), idle with { GpuTotalPercent = 97 }));
        Assert.Equal("Perf.Cause.Cpu", AutoPerformanceService.Diagnose(Limited(), idle with { SystemCpuPercent = 95 }));
        Assert.Equal("Perf.Cause.Copy", AutoPerformanceService.Diagnose(Limited(copyMs: 12), idle));
        Assert.Equal("Perf.Cause.Present", AutoPerformanceService.Diagnose(Limited(presentMs: 11), idle));
        Assert.Equal("Perf.Cause.Busy", AutoPerformanceService.Diagnose(Limited(), idle));
    }

    [Fact]
    public void Diagnose_UnknownGpu_DoesNotBlameGpu()
    {
        var noGpuCounters = new ResourceSnapshot(2, 20, 300, 200, null, null);
        Assert.Equal("Perf.Cause.Busy", AutoPerformanceService.Diagnose(Limited(), noGpuCounters));
    }

    // ---- Hardware tiers ----

    [Theory]
    [InlineData(8, 15.7, true, 0.1, true, PerformanceTier.Recommended)]   // 4C/8T, 16 GB, integrated GPU
    [InlineData(4, 7.8, true, 0.1, true, PerformanceTier.Minimum)]        // 4 threads, 8 GB, integrated
    [InlineData(16, 31.8, true, 8, true, PerformanceTier.Professional)]   // 8C/16T, 32 GB, 8 GB GPU
    [InlineData(16, 31.8, true, 4, true, PerformanceTier.Recommended)]    // strong CPU, small GPU
    [InlineData(2, 16, true, 2, true, PerformanceTier.BelowMinimum)]      // too few threads
    [InlineData(8, 4, true, 2, true, PerformanceTier.BelowMinimum)]       // too little memory
    [InlineData(8, 16, false, 0, true, PerformanceTier.BelowMinimum)]     // software rendering only
    [InlineData(16, 64, true, 16, false, PerformanceTier.BelowMinimum)]   // capture not supported
    public void EstimateTier_FollowsDocumentedThresholds(int threads, double memoryGb, bool gpu, double vramGb, bool capture, PerformanceTier expected)
    {
        Assert.Equal(expected, SystemCompatibility.EstimateTier(threads, memoryGb, gpu, vramGb, capture));
    }

    [Fact]
    public void Recommendations_MatchTheDetectedSystem()
    {
        var laptop = new SystemInfo("CPU", 8, 15.7, [new GpuInfo("Integrated GPU", 0.125, 7.8, true)],
            [new DisplayInfo("DISPLAY1", 1920, 1080, 144, true)], "Windows 11", 26100, true, true, true, "140.0", OnBattery: true);
        var keys = SystemCompatibility.Recommendations(laptop).Select(r => r.Key).ToList();

        Assert.Contains("Rec.IntegratedGpu", keys);
        Assert.Contains("Rec.OnBattery", keys);
        Assert.Contains("Rec.HighRefresh", keys);
        Assert.DoesNotContain("Rec.LowMemory", keys);
        Assert.DoesNotContain("Rec.NoWebView2", keys);
        Assert.Equal(144, SystemCompatibility.Recommendations(laptop).First(r => r.Key == "Rec.HighRefresh").Args[0]);
    }

    [Fact]
    public void EngineOf_GroupsProcessesByEngine()
    {
        Assert.Equal("luid_0x00000000_0x0000F1D2_phys_0_eng_0",
            PerformanceMonitor.EngineOf("pid_1234_luid_0x00000000_0x0000F1D2_phys_0_eng_0_engtype_3D"));
        Assert.Equal(PerformanceMonitor.EngineOf("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D"),
            PerformanceMonitor.EngineOf("pid_99_luid_0x0_0x1_phys_0_eng_0_engtype_3D"));
    }

    [Fact]
    public void SystemDetection_ReadsThisPc()
    {
        var info = SystemCompatibility.Detect(windowCaptureSupported: true);
        Assert.True(info.LogicalProcessors >= 1);
        Assert.True(info.MemoryGb > 1);
        Assert.NotEqual("?", info.CpuName);
        Assert.True(info.WindowsBuild >= 17763);
    }

    // ---- Leases and CPU copies ----

    [Fact]
    public void Lease_NativeRequestOnSourceWithoutNativePresentation_StillGetsCpuFrames()
    {
        // A synthetic (or camera) capture cannot present natively: asking for it must not starve the WPF image.
        using var service = new CaptureService();
        var source = new VIZEN.App.Models.WindowSource
        {
            Handle = IntPtr.Zero,
            ProcessId = (uint)Environment.ProcessId,
            ProcessName = "VIZEN",
            ClassName = "synthetic-test",
            SyntheticFps = 30,
            Width = 320,
            Height = 180,
        };
        using var lease = service.Acquire(source, presentsNatively: true);
        Assert.False(lease.UsesNativePresenter);
        Assert.Equal(0, lease.Capture.CpuFrameMaxFps);

        // A consumer that does present natively (and is the only one) stops the CPU copies.
        lease.SetNativePresenter(true);
        Assert.Equal(-1, lease.Capture.CpuFrameMaxFps);
    }
}
