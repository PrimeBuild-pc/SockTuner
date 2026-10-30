using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;
using SockTuner.Services.Collection;

namespace SockTuner.Services;

/// <summary>
/// One bounded, elevated host action — a capture or a one-shot system change — none of which fit the
/// typed setting-change envelope <see cref="ElevatedWorkerClient"/> carries, because none of them
/// have a before/after value the transaction engine's snapshot-and-verify model can check.
/// </summary>
public enum HostActionKind
{
    Unknown,

    /// <summary>Capture DPC/ISR driver activity for <see cref="HostActionRequest.DurationSeconds"/> seconds.</summary>
    CaptureDpcIsr,

    /// <summary>
    /// Remove one driver-store package (<see cref="HostActionRequest.Target"/>, a published name
    /// like <c>oem19.inf</c>) via <c>pnputil /delete-driver</c>. Never passes <c>/uninstall</c> or <c>/force</c>;
    /// eligibility is re-read in this elevated worker and Windows also refuses in-use packages.
    /// </summary>
    DeleteDriverPackage,

    /// <summary>
    /// Disable one device (<see cref="HostActionRequest.Target"/>, a PnP instance ID) via
    /// <c>pnputil /disable-device</c>. Never passes <c>/force</c>, so pnputil itself refuses a device
    /// it considers critical to booting — the guard is Microsoft's own, not re-derived here.
    /// </summary>
    DisableDevice
}

public sealed record HostActionRequest(Guid RequestId, HostActionKind Kind, int DurationSeconds = 0, string? Target = null);
public sealed record HostActionResponse(Guid RequestId, bool Success, string? Error,
    IReadOnlyList<DpcIsrDriverSample> Drivers, DpcIsrCaptureResult? Capture = null);

/// <summary>
/// Launches the same signed executable to run one <see cref="HostActionKind"/> and exchanges a
/// single request/response pair over a private named pipe — the same transport shape as
/// <see cref="ElevatedWorkerClient"/>, kept separate because every action here is a bounded capture
/// or a one-shot pnputil call, not a typed setting change with a snapshot to roll back.
/// </summary>
public sealed class HostActionClient
{
    internal const string WorkerArgument = "--host-action";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public async Task<HostActionResponse> ExecuteAsync(HostActionRequest request, CancellationToken cancellationToken)
    {
        var pipeName = $"SockTuner-{Guid.NewGuid():N}";
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
            throw new TimeoutException("The elevated host-action process did not connect.");
        }

        // A capture runs for the requested duration on the worker side, plus headroom for process
        // start-up; a pnputil action is near-instant but gets the same generous ceiling.
        using var completionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        completionTimeout.CancelAfter(TimeSpan.FromSeconds(request.DurationSeconds + 60));

        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Options).AsMemory(), completionTimeout.Token);

        var payload = await reader.ReadLineAsync(completionTimeout.Token)
            ?? throw new InvalidDataException("The elevated host-action process closed without responding.");

        // Deliberately not awaiting the worker process's own exit here: the response above is
        // already the authoritative result, and a capture the watchdog gave up on can leave that
        // process's thread blocked in a native call for a while after it has already answered —
        // waiting for it to fully exit would re-introduce the same hang this client is meant to avoid.

        var response = JsonSerializer.Deserialize<HostActionResponse>(payload, Options)
            ?? throw new InvalidDataException("The elevated host-action process returned an unreadable response.");
        if (response.RequestId != request.RequestId)
            throw new InvalidDataException("The host-action response does not match the request.");
        return response;
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
                ?? throw new InvalidOperationException("The elevated host-action process could not be started.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt.
            throw new ElevatedWorkerDeclinedException(exception);
        }
    }
}

/// <summary>Worker-side entry point: connects back, runs the one requested action, reports the result, exits.</summary>
internal static class HostActionHost
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static async Task<int> RunAsync(string pipeName, CancellationToken cancellationToken)
    {
        // An unconditional backstop, independent of every try/catch path below: this process must
        // never run forever no matter which branch of the logic below did or did not run to
        // completion — observed necessary in practice when the pipe connect or a native call blocks
        // with no cooperative way to cancel it.
        _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => Environment.Exit(4));

        HostActionRequest? request = null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync(cancellationToken)
                ?? throw new InvalidDataException("Host-action request is empty.");
            request = JsonSerializer.Deserialize<HostActionRequest>(line, Options)
                ?? throw new InvalidDataException("Host-action request is empty.");

            var (success, error, capture) = await ExecuteAsync(request, cancellationToken);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new HostActionResponse(request.RequestId, success, error, capture?.Drivers ?? [], capture), Options));
            pipe.WaitForPipeDrain();

            // A capture the watchdog gave up on leaves its thread blocked inside a native call that
            // may never return. The response is already sent and flushed above, so there is nothing
            // left for this short-lived, single-purpose process to do — exit immediately rather than
            // leave that thread (and this whole process) stuck around indefinitely.
            Environment.Exit(success ? 0 : 3);
            return success ? 0 : 3;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException
            or UnauthorizedAccessException or OperationCanceledException or JsonException or InvalidDataException)
        {
            // Same reasoning as the success path above: a watchdog-abandoned capture thread may
            // still be blocked even when the pipe itself failed (e.g. the caller's own completion
            // timeout gave up first), so this exits the same way rather than relying solely on the
            // three-minute backstop above.
            Environment.Exit(2);
            return 2;
        }
    }

    private static async Task<(bool Success, string? Error, DpcIsrCaptureResult? Capture)> ExecuteAsync(
        HostActionRequest request, CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case HostActionKind.CaptureDpcIsr:
                if (request.DurationSeconds is <= 0 or > 120)
                {
                    throw new InvalidDataException("DPC/ISR capture duration must be between 1 and 120 seconds.");
                }

                if (!System.Net.IPAddress.TryParse(request.Target, out _))
                    throw new InvalidDataException("A resolved IP address is required for the concurrent latency check.");
                return await CaptureWithWatchdogAsync(TimeSpan.FromSeconds(request.DurationSeconds), request.Target!, cancellationToken);

            case HostActionKind.DeleteDriverPackage:
                if (string.IsNullOrWhiteSpace(request.Target))
                {
                    throw new InvalidDataException("A driver package name is required.");
                }

                var inventory = WindowsDriverStoreInventory.Read();
                if (inventory.Error is not null || !DriverStoreDuplicateFinder.FindOutdatedDuplicates(
                        inventory.Packages, inventory.InUsePublishedNames).Any(duplicate =>
                        string.Equals(duplicate.Outdated.PublishedName, request.Target, StringComparison.OrdinalIgnoreCase)))
                    return (false, "Cleanup refused: the package is in use, no longer an older duplicate, or its bindings could not be verified.", null);

                var deleteExit = await RunPnputilAsync(["/delete-driver", request.Target], cancellationToken);
                return (deleteExit.ExitCode == 0, deleteExit.ExitCode == 0 ? null : deleteExit.Output, null);

            case HostActionKind.DisableDevice:
                if (string.IsNullOrWhiteSpace(request.Target))
                {
                    throw new InvalidDataException("A device instance ID is required.");
                }

                var device = InterruptAffinityInventory.Read(onlyInteresting: false).Devices.FirstOrDefault(item =>
                    string.Equals(item.InstanceId, request.Target, StringComparison.OrdinalIgnoreCase));
                if (!Remediation.DpcIsrGuidance.CanDisableDevice(device))
                    return (false, "Device disable refused: only verified ancillary device classes are allowed.", null);

                var disableExit = await RunPnputilAsync(["/disable-device", request.Target], cancellationToken);
                return (disableExit.ExitCode == 0, disableExit.ExitCode == 0 ? null : disableExit.Output, null);

            default:
                throw new InvalidDataException($"Unknown host action: {request.Kind}.");
        }
    }

    /// <summary>
    /// Starting a kernel ETW session can block inside the native call itself — observed hanging for
    /// minutes on a real machine, almost certainly because another tool already owns the NT Kernel
    /// Logger singleton — with no cooperative way to cancel it. Racing it against a hard deadline
    /// lets this worker process respond and exit even when the capture thread never returns; that
    /// thread is a background thread-pool thread, so it does not keep the process alive once this
    /// method moves on without it.
    /// </summary>
    private static async Task<(bool Success, string? Error, DpcIsrCaptureResult? Capture)> CaptureWithWatchdogAsync(
        TimeSpan duration, string target, CancellationToken cancellationToken)
    {
        var captureTask = Task.Run(() => EtwDpcIsrCapture.Capture(duration, target, cancellationToken), cancellationToken);
        var watchdog = duration + TimeSpan.FromSeconds(30);
        var winner = await Task.WhenAny(captureTask, Task.Delay(watchdog, cancellationToken));
        if (!ReferenceEquals(winner, captureTask))
        {
            return (false, "The ETW capture did not respond in time — another tool may already be using the "
                + "system's kernel trace session.", null);
        }

        var result = await captureTask;
        return (result.Error is null, result.Error, result);
    }

    private static async Task<(int ExitCode, string Output)> RunPnputilAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("pnputil.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("pnputil could not be started.");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
    }
}
