namespace SockTuner.Tests;

/// <summary>
/// Marks a test that reads real Windows state. These stay skipped by default so CI and normal
/// development runs remain deterministic and host-independent; set
/// <c>SOCKTUNER_LIVE_INVENTORY=1</c> to run them. Read-only inventory only — never a mutation.
/// </summary>
public sealed class LiveWindowsFactAttribute : FactAttribute
{
    public LiveWindowsFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SOCKTUNER_LIVE_INVENTORY"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set SOCKTUNER_LIVE_INVENTORY=1 to run read-only live Windows inventory checks.";
        }
        else if (!OperatingSystem.IsWindows())
        {
            Skip = "Live inventory checks require Windows.";
        }
    }
}
