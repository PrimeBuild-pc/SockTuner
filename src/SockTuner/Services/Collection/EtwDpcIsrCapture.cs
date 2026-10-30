using System.Diagnostics;
using System.IO;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>Bounded, elevated ETW capture with ICMP samples collected only while the session is active.</summary>
internal static class EtwDpcIsrCapture
{
    internal static DpcIsrCaptureResult Capture(TimeSpan duration, string target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || TraceEventSession.IsElevated() != true)
        {
            return new([], duration, "Administrator rights are required to capture driver interrupt activity.");
        }

        var etlPath = Path.Combine(Path.GetTempPath(), $"socktuner-dpcisr-{Guid.NewGuid():N}.etl");
        try
        {
            var profile = new DiagnosticProfile("dpcisr", "Concurrent driver check", 4,
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));
            var baseline = NetworkDiagnosticService.ProbeAsync("Driver check baseline", target,
                profile with { SampleCount = 12 }, cancellationToken).GetAwaiter().GetResult();
            var windows = new List<DpcIsrWindow>();
            using (var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName, etlPath,
                       TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate))
            {
                session.StopOnDispose = true;
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.Interrupt
                    | KernelTraceEventParser.Keywords.DeferedProcedureCalls
                    | KernelTraceEventParser.Keywords.ImageLoad);

                for (var index = 0; index < (int)duration.TotalSeconds; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var started = DateTimeOffset.UtcNow;
                    var timer = Stopwatch.StartNew();
                    var latency = NetworkDiagnosticService.ProbeAsync(
                        "Concurrent driver check", target, profile, cancellationToken).GetAwaiter().GetResult();
                    var remaining = TimeSpan.FromSeconds(1) - timer.Elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        cancellationToken.WaitHandle.WaitOne(remaining);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    windows.Add(new(started, DateTimeOffset.UtcNow, [], latency));
                }
            }

            using var traceLog = TraceLog.OpenOrConvert(etlPath);
            var kernelModules = traceLog.Processes.Where(process => process.ProcessID is 0 or 4)
                .Select(process => process.LoadedModules).ToArray();
            var events = traceLog.Events.Where(item => item is ISRTraceData or DPCTraceData)
                .Select(item => item switch
                {
                    ISRTraceData isr => (new DateTimeOffset(isr.TimeStamp.ToUniversalTime()),
                        new DpcIsrDriverSample(ResolveModule(kernelModules, isr.Routine, isr.TimeStampRelativeMSec), isr.ElapsedTimeMSec, 0, 1)),
                    DPCTraceData dpc => (new DateTimeOffset(dpc.TimeStamp.ToUniversalTime()),
                        new DpcIsrDriverSample(ResolveModule(kernelModules, dpc.Routine, dpc.TimeStampRelativeMSec), 0, dpc.ElapsedTimeMSec, 1)),
                    _ => throw new InvalidOperationException()
                });
            return AggregateWindows(events, windows) with { Baseline = baseline };
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException
            or IOException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception
            or DllNotFoundException or BadImageFormatException)
        {
            return new([], duration, $"Could not capture driver interrupt activity: {exception.Message}");
        }
        finally
        {
            TryDelete(etlPath);
            TryDelete(Path.ChangeExtension(etlPath, ".etlx"));
        }
    }

    internal static DpcIsrCaptureResult AggregateWindows(
        IEnumerable<(DateTimeOffset Timestamp, DpcIsrDriverSample Sample)> events,
        IReadOnlyList<DpcIsrWindow> windows)
    {
        var totals = windows.Select(_ => new Dictionary<string, DpcIsrDriverSample>(StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (var (timestamp, sample) in events)
        {
            for (var index = 0; index < windows.Count; index++)
            {
                if (timestamp < windows[index].StartedAt || timestamp >= windows[index].EndedAt) continue;
                var previous = totals[index].GetValueOrDefault(sample.DriverName);
                totals[index][sample.DriverName] = new(sample.DriverName,
                    (previous?.ElapsedIsrMs ?? 0) + sample.ElapsedIsrMs,
                    (previous?.ElapsedDpcMs ?? 0) + sample.ElapsedDpcMs,
                    (previous?.EventCount ?? 0) + sample.EventCount);
                break;
            }
        }

        var measured = windows.Select((window, index) => window with { Drivers = totals[index].Values.ToArray() }).ToArray();
        var ranking = measured.SelectMany(window => window.Drivers)
            .GroupBy(sample => sample.DriverName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DpcIsrDriverSample(group.Key, group.Sum(item => item.ElapsedIsrMs),
                group.Sum(item => item.ElapsedDpcMs), group.Sum(item => item.EventCount))).ToArray();
        return new(ranking, TimeSpan.FromTicks(measured.Sum(window => (window.EndedAt - window.StartedAt).Ticks)), null, measured);
    }

    private static string ResolveModule(IReadOnlyList<TraceLoadedModules> kernelModules, ulong routine, double timeMs)
    {
        // TraceEvent 3.1.21 does not index ISR/DPC code addresses; resolve the actual image range.
        foreach (var modules in kernelModules)
        {
            var path = modules.GetModuleContainingAddress(routine, timeMs)?.FilePath;
            if (!string.IsNullOrEmpty(path)) return Path.GetFileName(path);
        }
        return "(unresolved)";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
