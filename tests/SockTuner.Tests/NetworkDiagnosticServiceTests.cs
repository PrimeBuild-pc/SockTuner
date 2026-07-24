using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class NetworkDiagnosticServiceTests
{
    [Fact]
    public async Task RunAsync_PropagatesCallerCancellationWithoutSendingProbes()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new NetworkDiagnosticService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync("127.0.0.1", "127.0.0.1", 9, 3, cancellation.Token));
    }
}
