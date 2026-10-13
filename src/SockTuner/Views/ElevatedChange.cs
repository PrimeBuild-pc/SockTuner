using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Views;

/// <summary>
/// The one way a view hands a change to the elevated worker. Every failure comes back as a status
/// line rather than an exception: these run from async void handlers, where an escaping exception
/// ends the app mid-change and leaves the user unsure whether anything was written.
/// </summary>
internal static class ElevatedChange
{
    public static ElevatedWorkerRequest Request(WorkerOperationKind operation, IEnumerable<WorkerSettingOperation> changes) =>
        new(ElevatedWorker.SchemaVersion, Guid.NewGuid(), operation, changes.ToArray());

    public static WorkerSettingOperation Operation(
        string settingId, string? targetId, StoredSettingValue expected, StoredSettingValue desired, ChangeSource source) =>
        new(settingId,
            targetId,
            new WorkerStoredValue(expected.Exists, expected.Value),
            new WorkerStoredValue(desired.Exists, desired.Value),
            source);

    /// <summary>Runs <paramref name="request"/>; null when it was declined or failed, with the reason already shown.</summary>
    /// <remarks><paramref name="execute"/> is <see cref="ElevatedWorkerClient.ExecuteAsync"/>; <paramref name="log"/>
    /// defaults to <see cref="AppLog.Write"/>. Tests replace both.</remarks>
    public static async Task<ElevatedWorkerResponse?> RunAsync(
        Func<ElevatedWorkerRequest, CancellationToken, Task<ElevatedWorkerResponse>> execute,
        ElevatedWorkerRequest request,
        string logKey,
        Action<string> setStatus,
        Func<string, string> describeFailure,
        Func<string, string, string?>? log = null)
    {
        log ??= AppLog.Write;
        try
        {
            var response = await execute(request, CancellationToken.None);
            log(logKey, $"Success={response.Success}; Changes={request.Changes.Count}; Status={response.Status}");
            return response;
        }
        catch (ElevatedWorkerDeclinedException exception)
        {
            setStatus(exception.Message);
        }
        catch (Exception exception)
        {
            // Includes a worker that died without answering (InvalidDataException), which is not an IOException.
            setStatus(describeFailure(exception.Message));
            log($"{logKey}_failed", exception.Message);
        }

        return null;
    }
}
