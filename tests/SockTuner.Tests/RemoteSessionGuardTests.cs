using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// The guard that protects the way back in, rather than the setting. Deterministic: the remote
/// session check is substituted, nothing here reads the host.
/// </summary>
public sealed class RemoteSessionGuardTests : IDisposable
{
    private readonly Func<bool> _original = RemoteSessionGuard.IsRemoteSession;

    public void Dispose() => RemoteSessionGuard.IsRemoteSession = _original;

    private static void Session(bool remote) => RemoteSessionGuard.IsRemoteSession = () => remote;

    private static PlannedChange Change(string restartRequirement) => new(
        new SettingDefinition(
            "test.setting", "Test", "Test", SettingScope.AdapterInterface,
            EvidenceLevel.Documented, ChangeRisk.Low, restartRequirement,
            "Description", "Trade-off", @"SYSTEM\Test", "Value", RegistryValueKind.DWord, 0, 1,
            EvidenceNote: "Test fixture."),
        new SettingAddress("test.setting", null, @"SYSTEM\Test", "Value", RegistryValueKind.DWord),
        new StoredSettingValue(true, "0"),
        new StoredSettingValue(true, "1"));

    [Fact]
    public void ALocalSessionIsNotWarnedAboutALinkDrop()
    {
        // On the console a link drop is an inconvenience. The warning would be noise, and noise is
        // how a warning that matters stops being read.
        Session(remote: false);

        Assert.Null(RemoteSessionGuard.WarningFor([Change(RemoteSessionGuard.AdapterRestart)]));
    }

    [Fact]
    public void ARemoteSessionIsWarnedBeforeAnAdapterRestart()
    {
        Session(remote: true);

        var warning = RemoteSessionGuard.WarningFor([Change(RemoteSessionGuard.AdapterRestart)]);

        Assert.NotNull(warning);
        Assert.Contains("remote session", warning, StringComparison.Ordinal);
        // It must not claim to know which adapter carries the session, because it does not.
        Assert.Contains("does not know which adapter", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanThatDropsNoLinkIsNotWarnedAboutEvenRemotely()
    {
        Session(remote: true);

        Assert.Null(RemoteSessionGuard.WarningFor([Change("None"), Change("Service restart")]));
    }

    [Fact]
    public void ARebootRequirementDoesNotEndTheSessionAtApplyTime()
    {
        // The value is written now and read at boot. The session survives the apply, which is the
        // only thing this guard is about.
        Session(remote: true);

        Assert.False(RemoteSessionGuard.Disrupts(Change("System reboot").Definition));
        Assert.Null(RemoteSessionGuard.WarningFor([Change("System reboot")]));
    }

    [Fact]
    public void AnUnrecognisedRestartRequirementIsTreatedAsDisruptive()
    {
        // The safe default. A requirement nobody classified must not silently become harmless.
        Assert.True(RemoteSessionGuard.Disrupts(Change("Something new nobody classified").Definition));
    }

    [Fact]
    public void EveryRestartRequirementTheAppProducesIsClassified()
    {
        // Fails when a new restart requirement appears anywhere, so it has to be classified
        // deliberately instead of falling through to the disruptive default.
        var produced = SettingCatalog.All.Select(definition => definition.RestartRequirement)
            .Append(new DnsServerSpecification().RestartRequirement)
            .Append(new InterruptAffinitySpecification(8, new HashSet<string>()).RestartRequirement)
            .Distinct(StringComparer.Ordinal);

        foreach (var requirement in produced)
        {
            Assert.True(
                RemoteSessionGuard.RestartRequirements.ContainsKey(requirement),
                $"Restart requirement '{requirement}' is not classified in RemoteSessionGuard.");
        }
    }
}
