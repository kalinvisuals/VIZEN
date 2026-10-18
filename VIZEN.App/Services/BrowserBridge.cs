using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Threading;
using VIZEN.App.Models;

namespace VIZEN.App.Services;

/// <summary>
/// The VIZEN side of the browser extension: a local named pipe (only the current Windows user may connect) that the
/// native host (VIZEN.App.exe started by Chrome / Edge, see <see cref="BrowserNativeHost"/>) connects to. Receives the
/// tab lists and sends the user's commands (switch to a tab, mute, move to its own window). Nothing goes over the
/// network. Reading runs on background tasks; the tab list changes on the UI thread.
/// </summary>
public sealed class BrowserBridgeService : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Dictionary<string, Connection> _connections = [];
    private readonly object _gate = new();
    private bool _started;

    public BrowserTabStore Store { get; } = new();

    /// <summary>Tabs or connected browsers changed (UI thread).</summary>
    public event EventHandler? Changed;

    /// <summary>A command failed in the browser (UI thread): command, error text.</summary>
    public event EventHandler<(string Command, string Error)>? CommandFailed;

    public IReadOnlyList<string> ConnectedBrowsers
    {
        get
        {
            lock (_gate)
            {
                return _connections.Keys.ToList();
            }
        }
    }

    /// <summary>The pipe name for this Windows user (the host computes the same name).</summary>
    public static string PipeName => "VIZEN.Browser." + WindowsIdentity.GetCurrent().User?.Value + DemoMode.PipeSuffix;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Task.Run(() => AcceptLoopAsync(_cancel.Token));
    }

    public Task ActivateAsync(BrowserTab tab) => SendAsync(tab.Browser, new BrowserMessage { Type = "activate", TabId = tab.Id });

    public Task SetMutedAsync(BrowserTab tab, bool muted) => SendAsync(tab.Browser, new BrowserMessage { Type = "mute", TabId = tab.Id, Muted = muted });

    public Task PopOutAsync(BrowserTab tab) => SendAsync(tab.Browser, new BrowserMessage { Type = "popout", TabId = tab.Id });

    private async Task SendAsync(string browser, BrowserMessage message)
    {
        Connection? connection;
        lock (_gate)
        {
            _connections.TryGetValue(browser, out connection);
        }

        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.SendAsync(BrowserTabs.Serialize(message), _cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The browser went away; the read loop cleans up.
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancel).ConfigureAwait(false);
                var connection = new Connection(pipe);
                _ = Task.Run(() => ReadLoopAsync(connection, cancel), cancel);
                pipe = null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(500, cancel).ConfigureAwait(false);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    /// <summary>Only the current user (and nobody else on this PC) can open the pipe.</summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task ReadLoopAsync(Connection connection, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var frame = await BrowserTabs.ReadFrameAsync(connection.Pipe, cancel).ConfigureAwait(false);
                if (frame is null || BrowserTabs.Deserialize(frame) is not { } message)
                {
                    if (frame is null)
                    {
                        break;
                    }

                    continue;
                }

                var browser = BrowserTabs.KnownBrowsers.Contains(message.Browser) ? message.Browser! : "chrome";
                if (connection.Browser != browser)
                {
                    lock (_gate)
                    {
                        if (connection.Browser is { } previous && _connections.GetValueOrDefault(previous) == connection)
                        {
                            _connections.Remove(previous);
                        }

                        connection.Browser = browser;
                        _connections[browser] = connection;
                    }

                    _ = _dispatcher.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));
                }

                _ = _dispatcher.BeginInvoke(() => OnMessage(message));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            // Browser closed, extension reloaded or VIZEN exiting.
        }
        finally
        {
            string? gone = null;
            lock (_gate)
            {
                if (connection.Browser is { } browser && _connections.GetValueOrDefault(browser) == connection)
                {
                    _connections.Remove(browser);
                    gone = browser;
                }
            }

            connection.Dispose();
            if (gone is not null && !cancel.IsCancellationRequested)
            {
                _ = _dispatcher.BeginInvoke(() =>
                {
                    Store.Remove(gone);
                    Changed?.Invoke(this, EventArgs.Empty);
                });
            }
        }
    }

    private void OnMessage(BrowserMessage message)
    {
        switch (message.Type)
        {
            case "tabs" when Store.Apply(message):
                Changed?.Invoke(this, EventArgs.Empty);
                break;
            case "result" when message.Ok == false:
                CommandFailed?.Invoke(this, (message.Command ?? string.Empty, message.Error ?? string.Empty));
                break;
        }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        lock (_gate)
        {
            foreach (var connection in _connections.Values)
            {
                connection.Dispose();
            }

            _connections.Clear();
        }
    }

    private sealed class Connection(NamedPipeServerStream pipe) : IDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public NamedPipeServerStream Pipe { get; } = pipe;

        public string? Browser { get; set; }

        public async Task SendAsync(byte[] payload, CancellationToken cancel)
        {
            await _write.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                await BrowserTabs.WriteFrameAsync(Pipe, payload, cancel).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }

        public void Dispose() => Pipe.Dispose();
    }
}

/// <summary>
/// VIZEN.App.exe started by Chrome / Edge / Brave as the extension's native messaging host (the browser passes the
/// extension's origin as the first argument). No window and no WPF: it relays framed JSON between the browser
/// (stdin / stdout) and the running VIZEN app (named pipe), waiting quietly while VIZEN is not running, and exits
/// when the browser closes the connection.
/// </summary>
public static class BrowserNativeHost
{
    public static bool IsHostLaunch(string[] args) =>
        args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

    public static int Run()
    {
        using var cancel = new CancellationTokenSource();
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var relay = new Relay(stdout, cancel.Token);
        var pipeTask = Task.Run(() => relay.PipeLoopAsync());
        try
        {
            while (true)
            {
                var frame = BrowserTabs.ReadFrameAsync(stdin, cancel.Token).GetAwaiter().GetResult();
                if (frame is null)
                {
                    break; // the browser closed the connection
                }

                relay.FromBrowser(frame);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // broken connection
        }

        cancel.Cancel();
        try
        {
            pipeTask.Wait(1000);
        }
        catch (AggregateException)
        {
            // shutting down
        }

        return 0;
    }

    private sealed class Relay(Stream stdout, CancellationToken cancel)
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _stdoutWrite = new(1, 1);
        private NamedPipeClientStream? _pipe;
        private byte[]? _hello;
        private byte[]? _lastTabs;

        /// <summary>A message from the extension: forwarded to VIZEN, the hello and latest tab list kept for a (re)connect.</summary>
        public void FromBrowser(byte[] frame)
        {
            var type = BrowserTabs.Deserialize(frame)?.Type;
            NamedPipeClientStream? pipe;
            lock (_gate)
            {
                if (type == "hello")
                {
                    _hello = frame;
                }
                else if (type == "tabs")
                {
                    _lastTabs = frame;
                }

                pipe = _pipe;
            }

            if (pipe is null)
            {
                return;
            }

            try
            {
                BrowserTabs.WriteFrameAsync(pipe, frame, cancel).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // VIZEN closed: the pipe loop reconnects
            }
        }

        public async Task PipeLoopAsync()
        {
            while (!cancel.IsCancellationRequested)
            {
                var pipe = new NamedPipeClientStream(".", BrowserBridgeService.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(2000, cancel).ConfigureAwait(false);
                    byte[]? hello, tabs;
                    lock (_gate)
                    {
                        _pipe = pipe;
                        (hello, tabs) = (_hello, _lastTabs);
                    }

                    if (hello is not null)
                    {
                        await BrowserTabs.WriteFrameAsync(pipe, hello, cancel).ConfigureAwait(false);
                    }

                    if (tabs is not null)
                    {
                        await BrowserTabs.WriteFrameAsync(pipe, tabs, cancel).ConfigureAwait(false);
                    }

                    while (await BrowserTabs.ReadFrameAsync(pipe, cancel).ConfigureAwait(false) is { } command)
                    {
                        await _stdoutWrite.WaitAsync(cancel).ConfigureAwait(false);
                        try
                        {
                            await BrowserTabs.WriteFrameAsync(stdout, command, cancel).ConfigureAwait(false);
                        }
                        finally
                        {
                            _stdoutWrite.Release();
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
                {
                    // VIZEN not running (or closed): try again shortly
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_pipe == pipe)
                        {
                            _pipe = null;
                        }
                    }

                    pipe.Dispose();
                }

                try
                {
                    await Task.Delay(2000, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
