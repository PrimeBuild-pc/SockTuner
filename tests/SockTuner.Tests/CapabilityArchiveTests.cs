using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class CapabilityArchiveTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void GeneratedEntriesKeepDriverConstraintsUsableAndUnique()
    {
        foreach (var path in Directory.GetFiles(ArchivePath(), "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.Equal(Path.GetFileNameWithoutExtension(path), root.GetProperty("archiveKey").GetString());
            var isVirtual = root.GetProperty("isVirtual").GetBoolean();
            var ndisProperties = root.GetProperty("ndisProperties");
            AssertUniqueKeywords(ndisProperties);
            if (!isVirtual)
                Assert.All(ndisProperties.EnumerateArray(), item =>
                    Assert.True(NicKeywordCatalog.IsCharacterised(item.GetProperty("keyword").GetString()!)));

            var capabilities = root.GetProperty("capabilities")
                .Deserialize<AdapterSettingCapability[]>(JsonOptions) ?? [];
            Assert.Equal(capabilities.Length, capabilities.Select(item => item.Keyword).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            var adapterId = NormalizeId(root.GetProperty("adapter").GetProperty("id").GetString());
            Assert.All(capabilities, capability => Assert.Equal(adapterId, NormalizeId(capability.AdapterId.ToString())));
            foreach (var capability in capabilities)
            {
                if (!isVirtual)
                    Assert.True(NicKeywordCatalog.IsCharacterised(capability.Keyword), $"Uncharacterised physical-adapter keyword: {capability.Keyword}");
                var profile = NicKeywordCatalog.For(capability.Keyword);
                Assert.Equal(profile.Areas, capability.Areas);
                Assert.Equal(profile.Risk, capability.Risk);
                Assert.Equal(profile.TradeOff, capability.TradeOff);
                Assert.Equal(profile.Rejected, capability.Rejected);

                if (capability.Rejected)
                {
                    Assert.Throws<InvalidOperationException>(() => capability.Validate(capability.CurrentValue));
                    continue;
                }

                if (!string.Equals(capability.CurrentValue, "[redacted]", StringComparison.Ordinal))
                    capability.Validate(capability.CurrentValue);
                if (!string.IsNullOrEmpty(capability.DefaultValue)) capability.Validate(capability.DefaultValue);
            }
        }
    }

    [Fact]
    public void RawReportsAreProbeSnapshotsWithPersonalNetworkFieldsRedacted()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(ArchivePath(), "reports"), "socktuner-probe-*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.True(root.GetProperty("schemaVersion").GetInt32() >= 11);
            Assert.True(root.GetProperty("probe").GetBoolean());
            Assert.True(root.GetProperty("redacted").GetBoolean());

            var snapshot = root.GetProperty("snapshot");
            Assert.Equal("[redacted]", snapshot.GetProperty("system").GetProperty("machineName").GetString());
            foreach (var adapter in snapshot.GetProperty("adapters").EnumerateArray())
            {
                var mac = adapter.GetProperty("macAddress").GetString();
                Assert.True(mac == "[redacted]" || mac?.EndsWith("-00-00-00", StringComparison.Ordinal) == true);
                AssertRedactedAddresses(adapter.GetProperty("addresses"));
                AssertRedactedAddresses(adapter.GetProperty("gateways"));
                AssertRedactedAddresses(adapter.GetProperty("dnsServers"));
                Assert.All(
                    adapter.GetProperty("ndisProperties").EnumerateArray().Where(item =>
                        item.GetProperty("keyword").GetString()?.Equals("NetworkAddress", StringComparison.OrdinalIgnoreCase) == true),
                    item => Assert.Equal("[redacted]", item.GetProperty("currentValue").GetString()));
            }

            foreach (var route in snapshot.GetProperty("routes").EnumerateArray())
            {
                Assert.EndsWith(" destination redacted]", route.GetProperty("destination").GetString(), StringComparison.Ordinal);
                Assert.EndsWith(" next hop redacted]", route.GetProperty("nextHop").GetString(), StringComparison.Ordinal);
            }
        }
    }

    private static void AssertUniqueKeywords(JsonElement items)
    {
        var keywords = items.EnumerateArray().Select(item => item.GetProperty("keyword").GetString()).ToArray();
        Assert.Equal(keywords.Length, keywords.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static void AssertRedactedAddresses(JsonElement addresses) => Assert.All(
        addresses.EnumerateArray(),
        address => Assert.EndsWith(" address redacted]", address.GetString(), StringComparison.Ordinal));

    private static string NormalizeId(string? value) => value?.Trim('{', '}').ToUpperInvariant() ?? string.Empty;

    /// <summary>
    /// A profile drives a vendor keyword only on the driver's own wording for the target value. Every driver the
    /// community has reported must agree with the curated list, so a disagreement is caught here rather than on
    /// someone's adapter.
    /// </summary>
    [Fact]
    public void CuratedVendorTargetsAgreeWithEveryArchivedDriver()
    {
        var targets = SockTuner.Services.Remediation.UseCaseProfiles.All.SelectMany(profile => profile.Nic)
            .Where(target => target.DisplayMustMatch is not null).DistinctBy(target => target.Keyword).ToArray();
        var checkedPairs = 0;
        foreach (var path in Directory.GetFiles(ArchivePath(), "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("isVirtual").GetBoolean()) continue;
            foreach (var target in targets)
            {
                var labels = root.GetProperty("capabilities").EnumerateArray()
                    .Where(item => string.Equals(item.GetProperty("keyword").GetString(), target.Keyword, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(item => item.GetProperty("choices").EnumerateArray())
                    .Where(choice => choice.GetProperty("registryValue").GetString() == target.Value)
                    .Select(choice => choice.GetProperty("displayValue").GetString() ?? string.Empty)
                    .ToArray();
                foreach (var label in labels)
                {
                    checkedPairs++;
                    Assert.True(System.Text.RegularExpressions.Regex.IsMatch(label.Trim(), target.DisplayMustMatch!,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                        $"{Path.GetFileName(path)}: {target.Keyword}={target.Value} is labelled \"{label}\"");
                }
            }
        }

        Assert.True(checkedPairs > 10, "The archive should exercise the curated list.");
    }

    private static string ArchivePath() => Path.Combine(RepositoryRoot(), "alpha-tester-output");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SockTuner.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SockTuner.sln not found above the test output.");
    }
}
