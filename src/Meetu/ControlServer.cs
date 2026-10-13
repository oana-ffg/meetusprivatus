using System.IO.Pipes;
using System.Text.Json;

namespace Meetu;

public static class ControlServer
{
    public static async Task RunAsync(StatePaths paths, CaptureSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(paths.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var clientTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                clientTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                // One fixed byte command prevents unbounded input or transcript-bearing control messages.
                var command = new byte[1];
                if (await pipe.ReadAsync(command, clientTimeout.Token) == 1)
                {
                    if (command[0] == 1) session.Stop();
                    else if (command[0] != 0) continue;
                    var reply = JsonSerializer.SerializeToUtf8Bytes(session.Status, JsonFiles.Options);
                    await pipe.WriteAsync(reply, clientTimeout.Token);
                    await pipe.FlushAsync(clientTimeout.Token);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (IOException) { /* A controller disconnect does not interrupt capture. */ }
            catch (OperationCanceledException) { /* A stalled controller loses its connection. */ }
        }
    }

    public static async Task<SessionStatus?> RequestAsync(StatePaths paths, bool stop = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(new byte[] { stop ? (byte)1 : (byte)0 }, timeout.Token);
            return await JsonSerializer.DeserializeAsync<SessionStatus>(pipe, JsonFiles.Options, timeout.Token);
        }
        catch (OperationCanceledException) { return null; }
        catch (IOException) { return null; }
        catch (JsonException) { return null; } // A worker can exit midway through its status reply.
    }
}
