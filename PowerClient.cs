using System.IO.Pipes;

namespace VirtualScreenViewer;

/// <summary>
/// The viewer's connection to <see cref="PowerService"/>. The connection stays open while the
/// viewer runs: when it closes (exit, crash or kill), the service switches the device off.
/// </summary>
internal sealed class PowerClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public Task OnAsync() => SendAsync("ON");
    public Task OffAsync() => SendAsync("OFF");

    private async Task SendAsync(string command)
    {
        await _gate.WaitAsync();
        try
        {
            if (_pipe is not { IsConnected: true })
            {
                Close();
                var pipe = new NamedPipeClientStream(".", PowerService.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(3000); }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    pipe.Dispose();
                    throw new InvalidOperationException("The Virtual Screen service isn't running. Run install.ps1 to install it, "
                        + "or start \"Barton IT Virtual Screen\" in Services.");
                }
                _pipe = pipe;
                _reader = new StreamReader(pipe);
                _writer = new StreamWriter(pipe) { AutoFlush = true };
            }
            await _writer!.WriteLineAsync(command);
            var reply = await _reader!.ReadLineAsync() ?? throw new InvalidOperationException("The Virtual Screen service closed the connection.");
            if (!reply.StartsWith("OK", StringComparison.Ordinal))
                throw new InvalidOperationException(reply.StartsWith("ERR ", StringComparison.Ordinal) ? reply[4..] : reply);
        }
        catch (IOException ex)
        {
            Close();
            throw new InvalidOperationException($"Lost the connection to the Virtual Screen service ({ex.Message}).");
        }
        finally { _gate.Release(); }
    }

    private void Close()
    {
        _reader?.Dispose(); _writer = null; _reader = null;
        _pipe?.Dispose(); _pipe = null;
    }

    public void Dispose() => Close();
}
