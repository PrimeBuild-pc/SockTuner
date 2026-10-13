using System.IO;
using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Views;

namespace SockTuner.Tests;

public sealed class ElevatedChangeTests
{
    private static readonly ElevatedWorkerRequest Request = ElevatedChange.Request(WorkerOperationKind.Apply,
        [ElevatedChange.Operation("x", null, StoredSettingValue.Missing, new StoredSettingValue(true, "1"), ChangeSource.Manual)]);

    // The worker closing its pipe without answering surfaces as InvalidDataException; before the
    // shared helper, the tuning plan let it escape an async void handler and the app closed.
    [Fact]
    public async Task WorkerThatDiesWithoutAnswering_BecomesAStatusNotAnException()
    {
        string? status = null;
        var logged = new List<string>();

        var response = await ElevatedChange.RunAsync(
            (_, _) => throw new InvalidDataException("The elevated worker closed without responding."),
            Request, "test", value => status = value, message => $"failed: {message}",
            (key, _) => { logged.Add(key); return null; });

        Assert.Null(response);
        Assert.Equal("failed: The elevated worker closed without responding.", status);
        Assert.Equal(["test_failed"], logged);
    }

    [Fact]
    public async Task DeclinedElevation_ShowsItsOwnMessage()
    {
        string? status = null;

        var response = await ElevatedChange.RunAsync(
            (_, _) => throw new ElevatedWorkerDeclinedException(new Exception()),
            Request, "test", value => status = value, message => $"failed: {message}", (_, _) => null);

        Assert.Null(response);
        Assert.Equal("The elevation prompt was dismissed, so nothing was changed.", status);
    }
}
