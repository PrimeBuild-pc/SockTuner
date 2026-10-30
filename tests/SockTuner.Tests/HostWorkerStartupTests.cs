using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SockTuner.Tests;

public sealed class HostWorkerStartupTests
{
    [Fact]
    public async Task WpfWorkerCanReadAndRejectARequestWithoutBlockingItsDispatcher()
    {
        var name = $"SockTuner-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SockTuner.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--host-action");
        start.ArgumentList.Add(name);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            // Invalid duration is rejected before any OS capture or write is attempted.
            var request = JsonSerializer.Serialize(new { RequestId = Guid.NewGuid(), Kind = "CaptureDpcIsr", DurationSeconds = 0 });
            await writer.WriteLineAsync(request.AsMemory(), timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(2, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
