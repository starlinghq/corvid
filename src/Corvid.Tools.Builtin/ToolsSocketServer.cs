using System.Net.Sockets;
using Corvid.Tools.CodeMode;

namespace Corvid.Tools.Builtin;

/// <summary>
/// A7: one run's tools socket: a Unix stream socket, listening before the script starts, each connection served by the run's
/// <see cref="CodeModeSession"/> (which checks the token and enforces the limits across connections). At most
/// <c>maxConnections</c> are served at once; more are closed unanswered. <see cref="StopAsync"/> stops accepting, cancels
/// every connection (a tool call still running is cancelled and has its result) and waits for them all.
/// </summary>
internal sealed class ToolsSocketServer : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CodeModeSession _session;
    private readonly int _maxConnections;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private int _open;
    private Task _accepting = Task.CompletedTask;
    private int _stopped;

    private ToolsSocketServer(Socket listener, CodeModeSession session, int maxConnections)
    {
        _listener = listener;
        _session = session;
        _maxConnections = maxConnections;
    }

    /// <summary>Connections accepted over the run (served or closed for being over the limit).</summary>
    public int Accepted { get; private set; }

    public static ToolsSocketServer Start(string socketPath, CodeModeSession session, int maxConnections)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            // The directory is already 0700; the socket is the owner's alone as well.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(maxConnections);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        var server = new ToolsSocketServer(listener, session, maxConnections);
        server._accepting = server.AcceptAsync();
        return server;
    }

    private async Task AcceptAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            lock (_connections)
            {
                Accepted++;
                if (_open >= _maxConnections)
                {
                    client.Dispose();
                    continue;
                }
                _open++;
                // Finished connections are dropped (a failed one is kept for StopAsync to report), so a script that
                // connects and closes in a loop holds nothing.
                _connections.RemoveAll(task => task.IsCompletedSuccessfully);
                _connections.Add(ServeAsync(client, token));
            }
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken token)
    {
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: true);
            await _session.ServeAsync(stream, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The run ended or the script closed its side mid-line: nothing to answer.
        }
        catch (Exception exception)
        {
            // A7 review (L3): anything else (a tool path that threw) ends this connection only, never the run; the run's
            // result reports it.
            lock (_connections)
                _failures.Add($"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            lock (_connections)
                _open--;
        }
    }

    private readonly List<string> _failures = [];

    /// <summary>A7 review (L3): connections that ended on an unexpected error, each as its exception's type and message.</summary>
    public IReadOnlyList<string> Failures
    {
        get
        {
            lock (_connections)
                return [.. _failures];
        }
    }

    /// <summary>
    /// Stops the server and waits for every connection it served; safe to call more than once. Never throws (A7 review L3):
    /// it runs in the code tool's <c>finally</c>, where an exception would lose the run's output.
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Dispose();
            await _accepting.ConfigureAwait(false);
            Task[] connections;
            lock (_connections)
                connections = [.. _connections];
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_connections)
                _failures.Add($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
