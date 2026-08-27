using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SockTuner.Services;

public sealed class ElevatedWorkerDeclinedException : Exception
{
    public ElevatedWorkerDeclinedException(Exception inner)
        : base("The elevation prompt was dismissed, so nothing was changed.", inner) { }
}

/// <summary>
/// Launches the same signed executable in elevated worker mode and exchanges one typed request
/// over a private named pipe.
/// </summary>
/// <remarks>
/// A pipe is used rather than standard input/output because elevation requires
/// <c>UseShellExecute = true</c> while stream redirection requires it to be false — the two
/// cannot be combined. The pipe carries the identical JSON envelope, so the worker's protocol
/// and validation are unchanged by the transport.
/// </remarks>
public sealed class ElevatedWorkerClient
{
    internal const string WorkerArgument = "--elevated-worker";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task<ElevatedWorkerResponse> ExecuteAsync(
        ElevatedWorkerRequest request,
        CancellationToken cancellationToken)
    {
        var pipeName = $"SockTuner-{Guid.NewGuid():N}";

        // The default pipe ACL grants the creating user and administrators access, which is
        // exactly the trust boundary needed: the worker runs as the same user, elevated, and
        // anyone already holding administrator rights does not need this pipe to change settings.
        using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        using var process = Launch(pipeName);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(ConnectTimeout);

        try
        {
            await pipe.WaitForConnectionAsync(connectTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The elevated worker did not connect.");
        }

        using var completionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        completionTimeout.CancelAfter(CompletionTimeout);

        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Options).AsMemory(), completionTimeout.Token);

        var payload = await reader.ReadLineAsync(completionTimeout.Token)
            ?? throw new InvalidDataException("The elevated worker closed without responding.");
        await process.WaitForExitAsync(completionTimeout.Token);

        return JsonSerializer.Deserialize<ElevatedWorkerResponse>(payload, Options)
            ?? throw new InvalidDataException("The elevated worker returned an unreadable response.");
    }

    private static Process Launch(string pipeName)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The SockTuner executable path is unavailable.");
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        startInfo.ArgumentList.Add(WorkerArgument);
        startInfo.ArgumentList.Add(pipeName);

        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("The elevated worker could not be started.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt.
            throw new ElevatedWorkerDeclinedException(exception);
        }
    }
}

/// <summary>Worker-side entry point: connects back to the launching process and serves one request.</summary>
internal static class ElevatedWorkerHost
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<int> RunAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var exitCode = await ElevatedWorker.RunAsync(reader, writer, cancellationToken);
            pipe.WaitForPipeDrain();
            return exitCode;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException
                                          or UnauthorizedAccessException or OperationCanceledException)
        {
            return 2;
        }
    }
}
