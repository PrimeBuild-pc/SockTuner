using System.IO;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>
/// Captures the kernel's own ISR and DPC trace events for a bounded window and attributes each one
/// to the driver file that owns it — the same measurement LatencyMon reports, taken with the
/// in-process <c>TraceEvent</c> library rather than shelling out to <c>wpr.exe</c>/<c>xperf</c>.
/// </summary>
/// <remarks>
/// Runs only inside the elevated capture process (<see cref="DpcIsrCaptureHost"/>): starting a
/// kernel trace session needs Administrator rights, and the NT Kernel Logger is a system-wide
/// singleton — if another tool already has one running, this fails outright rather than retrying.
/// </remarks>
internal static class EtwDpcIsrCapture
{
    internal static DpcIsrCaptureResult Capture(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], duration, null);
        }

        if (TraceEventSession.IsElevated() != true)
        {
            return new([], duration, "Administrator rights are required to capture driver interrupt activity.");
        }

        var etlPath = Path.Combine(Path.GetTempPath(), $"socktuner-dpcisr-{Guid.NewGuid():N}.etl");
        try
        {
            using (var session = new TraceEventSession(
                       KernelTraceEventParser.KernelSessionName, etlPath, TraceEventSessionOptions.Create))
            {
                session.StopOnDispose = true;
                // ImageLoad is what lets the offline pass below resolve a routine address back to
                // the driver file that owns it; without it every sample would read "(unresolved)".
                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.Interrupt
                    | KernelTraceEventParser.Keywords.DeferedProcedureCalls
                    | KernelTraceEventParser.Keywords.ImageLoad);

                cancellationToken.WaitHandle.WaitOne(duration);
            }

            return Aggregate(etlPath, duration);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException
            or IOException or System.Runtime.InteropServices.COMException)
        {
            return new([], duration, $"Could not capture driver interrupt activity: {exception.Message}");
        }
        finally
        {
            TryDelete(etlPath);
            TryDelete(Path.ChangeExtension(etlPath, ".etlx"));
        }
    }

    private static DpcIsrCaptureResult Aggregate(string etlPath, TimeSpan duration)
    {
        using var traceLog = TraceLog.OpenOrConvert(etlPath);
        var totals = new Dictionary<string, (double IsrMs, double DpcMs, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var isr in traceLog.Events.ByEventType<ISRTraceData>())
        {
            Accumulate(totals, ResolveModule(traceLog, isr.RoutineCodeAddressIndex()), isr.ElapsedTimeMSec, isDpc: false);
        }

        foreach (var dpc in traceLog.Events.ByEventType<DPCTraceData>())
        {
            Accumulate(totals, ResolveModule(traceLog, dpc.RoutineCodeAddressIndex()), dpc.ElapsedTimeMSec, isDpc: true);
        }

        var drivers = totals
            .Select(entry => new DpcIsrDriverSample(entry.Key, entry.Value.IsrMs, entry.Value.DpcMs, entry.Value.Count))
            .ToArray();
        return new(drivers, duration, null);
    }

    private static void Accumulate(
        Dictionary<string, (double IsrMs, double DpcMs, int Count)> totals, string module, double elapsedMs, bool isDpc)
    {
        var current = totals.GetValueOrDefault(module);
        totals[module] = isDpc
            ? (current.IsrMs, current.DpcMs + elapsedMs, current.Count + 1)
            : (current.IsrMs + elapsedMs, current.DpcMs, current.Count + 1);
    }

    private static string ResolveModule(TraceLog traceLog, CodeAddressIndex index)
    {
        if (index == CodeAddressIndex.Invalid)
        {
            return "(unresolved)";
        }

        var moduleName = traceLog.CodeAddresses[index]?.ModuleName;
        return string.IsNullOrEmpty(moduleName) ? "(unresolved)" : moduleName;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
