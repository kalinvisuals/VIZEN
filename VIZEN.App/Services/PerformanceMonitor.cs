using System.Diagnostics;
using System.Windows.Threading;

namespace VIZEN.App.Services;

/// <summary>One second of resource usage. GPU values are null when Windows does not expose them.</summary>
public sealed record ResourceSnapshot(
    double VizenCpuPercent,
    double SystemCpuPercent,
    double WorkingSetMb,
    double PrivateMb,
    double? VizenGpuPercent,
    double? GpuTotalPercent,
    double? VizenGpuDedicatedMb = null,
    double? VizenGpuSharedMb = null)
{
    public static ResourceSnapshot Empty { get; } = new(0, 0, 0, 0, null, null);
}

/// <summary>
/// Samples VIZEN's CPU and memory and the GPU 3D-engine load (Windows "GPU Engine" counters, the same source as
/// Task Manager) once a second on a background thread, and publishes the result on the UI thread.
/// </summary>
public sealed class PerformanceMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly string _pidTag = $"pid_{Environment.ProcessId}_";
    private readonly Timer _timer;
    private PerformanceCounter? _systemCpu;
    private List<(PerformanceCounter Counter, bool Own, string Engine)> _gpuCounters = [];
    private DateTime _gpuRefreshedAt = DateTime.MinValue;
    private TimeSpan _lastCpu;
    private long _lastSample = Stopwatch.GetTimestamp();
    private bool _gpuUnavailable;
    private int _busy;

    public PerformanceMonitor()
    {
        _lastCpu = _process.TotalProcessorTime;
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// True while someone looks at the GPU figures (Performance page). The GPU counters walk every engine of every
    /// process — hundreds of instances, about 10 MB and a steady CPU cost — so they only run when shown.
    /// </summary>
    public bool Detailed { get; set; }

    /// <summary>The GPU engine counters do not exist on this PC (not just "not sampled yet").</summary>
    public bool GpuUnavailable => _gpuUnavailable;

    /// <summary>The GPU memory counters do not exist on this PC.</summary>
    public bool GpuMemoryUnavailable => _gpuMemoryUnavailable;

    public ResourceSnapshot Latest { get; private set; } = ResourceSnapshot.Empty;

    private readonly Queue<(DateTime At, ResourceSnapshot Snapshot)> _history = new();

    /// <summary>The last five minutes of samples (for the diagnostics export). UI thread.</summary>
    public IReadOnlyList<(DateTime At, ResourceSnapshot Snapshot)> History => [.. _history];

    /// <summary>Raised on the UI thread after each sample.</summary>
    public event EventHandler<ResourceSnapshot>? Sampled;

    private void Sample()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            _process.Refresh();
            var now = Stopwatch.GetTimestamp();
            var cpu = _process.TotalProcessorTime;
            var elapsedMs = (now - _lastSample) * 1000.0 / Stopwatch.Frequency;
            var vizenCpu = elapsedMs > 0 ? (cpu - _lastCpu).TotalMilliseconds / elapsedMs * 100 / Environment.ProcessorCount : 0;
            (_lastCpu, _lastSample) = (cpu, now);

            var systemCpu = 0.0;
            try
            {
                _systemCpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
                systemCpu = _systemCpu.NextValue();
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                systemCpu = 0;
            }

            double? ownGpu = null, totalGpu = null, dedicated = null, shared = null;
            if (Detailed)
            {
                (ownGpu, totalGpu) = SampleGpu();
                (dedicated, shared) = SampleGpuMemory();
            }
            else
            {
                ReleaseGpuCounters();
            }

            var snapshot = new ResourceSnapshot(Math.Clamp(vizenCpu, 0, 100), Math.Clamp(systemCpu, 0, 100),
                _process.WorkingSet64 / 1048576.0, _process.PrivateMemorySize64 / 1048576.0, ownGpu, totalGpu, dedicated, shared);
            _dispatcher.BeginInvoke(() =>
            {
                Latest = snapshot;
                _history.Enqueue((DateTime.Now, snapshot));
                while (_history.Count > 300)
                {
                    _history.Dequeue();
                }

                Sampled?.Invoke(this, snapshot);
            });
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void ReleaseGpuCounters()
    {
        if (_gpuCounters.Count == 0 && _gpuMemory is null)
        {
            return;
        }

        foreach (var (counter, _, _) in _gpuCounters)
        {
            counter.Dispose();
        }

        _gpuCounters = [];
        _gpuRefreshedAt = DateTime.MinValue;
        foreach (var (d, s) in _gpuMemory ?? [])
        {
            d.Dispose();
            s.Dispose();
        }

        _gpuMemory = null;
    }

    private (double? Own, double? Total) SampleGpu()
    {
        if (_gpuUnavailable)
        {
            return (null, null);
        }

        try
        {
            // Engine instances come and go with processes: refresh the list every few seconds.
            if (DateTime.UtcNow - _gpuRefreshedAt > TimeSpan.FromSeconds(5))
            {
                foreach (var (counter, _, _) in _gpuCounters)
                {
                    counter.Dispose();
                }

                var category = new PerformanceCounterCategory("GPU Engine");
                _gpuCounters = category.GetInstanceNames()
                    .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Select(n => (new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true), n.StartsWith(_pidTag, StringComparison.Ordinal), EngineOf(n)))
                    .ToList();
                foreach (var (counter, _, _) in _gpuCounters)
                {
                    counter.NextValue(); // first value is always 0
                }

                _gpuRefreshedAt = DateTime.UtcNow;
                return (Latest.VizenGpuPercent, Latest.GpuTotalPercent);
            }

            // Like Task Manager: add up all processes per 3D engine, report the busiest engine.
            var total = new Dictionary<string, double>();
            var own = new Dictionary<string, double>();
            foreach (var (counter, isOwn, engine) in _gpuCounters)
            {
                float value;
                try
                {
                    value = counter.NextValue();
                }
                catch (InvalidOperationException)
                {
                    continue; // the process ended
                }

                total[engine] = total.GetValueOrDefault(engine) + value;
                if (isOwn)
                {
                    own[engine] = own.GetValueOrDefault(engine) + value;
                }
            }

            return (Math.Min(100, own.Values.DefaultIfEmpty(0).Max()), Math.Min(100, total.Values.DefaultIfEmpty(0).Max()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _gpuUnavailable = true;
            return (null, null);
        }
    }

    private List<(PerformanceCounter Dedicated, PerformanceCounter Shared)>? _gpuMemory;
    private bool _gpuMemoryUnavailable;

    /// <summary>VIZEN's GPU memory ("GPU Process Memory" counters, as in Task Manager), in MB; null when not available.</summary>
    private (double? Dedicated, double? Shared) SampleGpuMemory()
    {
        if (_gpuMemoryUnavailable)
        {
            return (null, null);
        }

        try
        {
            _gpuMemory ??= new PerformanceCounterCategory("GPU Process Memory").GetInstanceNames()
                .Where(n => n.StartsWith(_pidTag, StringComparison.Ordinal))
                .Select(n => (new PerformanceCounter("GPU Process Memory", "Dedicated Usage", n, readOnly: true),
                              new PerformanceCounter("GPU Process Memory", "Shared Usage", n, readOnly: true)))
                .ToList();
            if (_gpuMemory.Count == 0)
            {
                _gpuMemory = null; // the process has no GPU allocations yet: look again next time
                return (null, null);
            }

            double dedicated = 0, shared = 0;
            foreach (var (d, s) in _gpuMemory)
            {
                dedicated += d.NextValue();
                shared += s.NextValue();
            }

            return (dedicated / 1048576.0, shared / 1048576.0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _gpuMemoryUnavailable = true;
            return (null, null);
        }
    }

    /// <summary>"pid_12_luid_0x..._phys_0_eng_3_engtype_3D" → "luid_0x..._phys_0_eng_3".</summary>
    internal static string EngineOf(string instance)
    {
        var start = instance.IndexOf("luid_", StringComparison.Ordinal);
        var end = instance.IndexOf("_engtype", StringComparison.Ordinal);
        return start >= 0 && end > start ? instance[start..end] : instance;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _systemCpu?.Dispose();
        foreach (var (counter, _, _) in _gpuCounters)
        {
            counter.Dispose();
        }
    }
}
