using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SockTuner.Services;

public enum UpdateChannel
{
    Stable,
    Preview
}

public sealed record AppRelease(
    string Version,
    string Name,
    string Notes,
    Uri ArchiveUri,
    Uri ChecksumUri);

public sealed record PreparedUpdate(AppRelease Release, string ExecutablePath);

public sealed class AppUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/PrimeBuild-pc/SockTuner/releases?per_page=20";
    private const long MaximumArchiveBytes = 300 * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    public static string CurrentVersion =>
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
         ?? "0.0.0").Split('+')[0];

    public static bool AutomaticInstallationAvailable =>
        Environment.ProcessPath is { } path && AuthenticodeTrust.IsTrusted(path);

    public static bool InstallationNeedsElevation(string executablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))
            ?? throw new InvalidOperationException("The installation directory is unavailable.");
        var probe = Path.Combine(directory, $".socktuner-update-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public async Task<AppRelease?> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        var json = await Client.GetStringAsync(new Uri(ReleasesApi), cancellationToken);
        try
        {
            return SelectRelease(json, channel, CurrentVersion);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                           or KeyNotFoundException or UriFormatException)
        {
            throw new InvalidDataException("GitHub returned an invalid release response.", exception);
        }
    }

    public async Task<PreparedUpdate> PrepareAsync(AppRelease release, string currentExecutable, CancellationToken cancellationToken)
    {
        if (!IsExpectedReleaseUri(release.ArchiveUri) || !IsExpectedReleaseUri(release.ChecksumUri))
        {
            throw new InvalidDataException("The release assets do not belong to the SockTuner GitHub repository.");
        }

        var archive = await DownloadAsync(release.ArchiveUri, MaximumArchiveBytes, cancellationToken);
        var checksumText = await Client.GetStringAsync(release.ChecksumUri, cancellationToken);
        var expectedHash = ParseChecksum(checksumText, Path.GetFileName(release.ArchiveUri.LocalPath));
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(archive));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
        {
            throw new InvalidDataException("The downloaded update does not match its SHA-256 checksum.");
        }

        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrimeBuild", "SockTuner", "Updates", release.Version);
        Directory.CreateDirectory(updateDirectory);
        var executable = Path.Combine(updateDirectory, "SockTuner.exe");
        using (var stream = new MemoryStream(archive, writable: false))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var entry = zip.Entries.SingleOrDefault(item =>
                string.Equals(item.FullName, "SockTuner.exe", StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length <= 0 || entry.Length > MaximumArchiveBytes)
            {
                throw new InvalidDataException("The release archive does not contain a valid SockTuner.exe.");
            }

            await using var source = entry.Open();
            await using var destination = new FileStream(executable, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken);
        }

        if (!AuthenticodeTrust.IsTrusted(currentExecutable) || !AuthenticodeTrust.IsTrusted(executable))
        {
            File.Delete(executable);
            throw new InvalidDataException("Automatic installation requires both the installed app and the update to have trusted Authenticode signatures.");
        }

        return new PreparedUpdate(release, executable);
    }

    internal static AppRelease? SelectRelease(string json, UpdateChannel channel, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Where(item => !item.GetProperty("draft").GetBoolean())
            .Where(item => channel == UpdateChannel.Preview || !item.GetProperty("prerelease").GetBoolean())
            .Select(ParseRelease)
            .Where(release => release is not null && CompareVersions(release.Version, currentVersion) > 0)
            .OrderByDescending(release => release!.Version, SemanticVersionComparer.Instance)
            .FirstOrDefault();
    }

    internal static int CompareVersions(string left, string right) =>
        SemanticVersionComparer.Instance.Compare(left, right);

    internal static string ParseChecksum(string text, string archiveName)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && parts[0].Length == 64
                && parts[0].All(Uri.IsHexDigit)
                && string.Equals(parts[^1].TrimStart('*'), archiveName, StringComparison.Ordinal))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new InvalidDataException("The release checksum file is invalid.");
    }

    private static AppRelease? ParseRelease(JsonElement item)
    {
        var tag = item.GetProperty("tag_name").GetString() ?? string.Empty;
        var version = tag.TrimStart('v', 'V');
        if (!SemanticVersionComparer.IsValid(version)) return null;
        var archiveName = $"SockTuner-{version}-win-x64.zip";
        var assets = item.GetProperty("assets").EnumerateArray().ToArray();
        var archive = Asset(assets, archiveName);
        var checksum = Asset(assets, archiveName + ".sha256");
        if (archive is null || checksum is null) return null;

        return new AppRelease(
            version,
            item.GetProperty("name").GetString() ?? tag,
            item.GetProperty("body").GetString() ?? string.Empty,
            archive,
            checksum);
    }

    private static Uri? Asset(IEnumerable<JsonElement> assets, string name)
    {
        var asset = assets.FirstOrDefault(item =>
            string.Equals(item.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        var value = asset.ValueKind == JsonValueKind.Undefined
            ? null
            : asset.GetProperty("browser_download_url").GetString();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static bool IsExpectedReleaseUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/PrimeBuild-pc/SockTuner/releases/download/", StringComparison.Ordinal);

    private static async Task<byte[]> DownloadAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("The update archive is unexpectedly large.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (destination.Length + read > maximumBytes)
                throw new InvalidDataException("The update archive is unexpectedly large.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return destination.ToArray();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SockTuner-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed class SemanticVersionComparer : IComparer<string>
    {
        internal static readonly SemanticVersionComparer Instance = new();
        internal static bool IsValid(string value) => TryParse(value, out _);

        public int Compare(string? x, string? y)
        {
            if (!TryParse(x, out var left) || !TryParse(y, out var right)) return string.CompareOrdinal(x, y);
            var comparison = left.Major.CompareTo(right.Major);
            if (comparison == 0) comparison = left.Minor.CompareTo(right.Minor);
            if (comparison == 0) comparison = left.Patch.CompareTo(right.Patch);
            if (comparison != 0) return comparison;
            if (left.PreRelease.Length == 0) return right.PreRelease.Length == 0 ? 0 : 1;
            if (right.PreRelease.Length == 0) return -1;

            for (var index = 0; index < Math.Max(left.PreRelease.Length, right.PreRelease.Length); index++)
            {
                if (index == left.PreRelease.Length) return -1;
                if (index == right.PreRelease.Length) return 1;
                var leftPart = left.PreRelease[index];
                var rightPart = right.PreRelease[index];
                var leftNumeric = int.TryParse(leftPart, out var leftNumber);
                var rightNumeric = int.TryParse(rightPart, out var rightNumber);
                comparison = leftNumeric && rightNumeric
                    ? leftNumber.CompareTo(rightNumber)
                    : leftNumeric ? -1
                    : rightNumeric ? 1
                    : string.Compare(leftPart, rightPart, StringComparison.Ordinal);
                if (comparison != 0) return comparison;
            }

            return 0;
        }

        private static bool TryParse(string? value, out (int Major, int Minor, int Patch, string[] PreRelease) version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var coreAndPre = value.Trim().TrimStart('v', 'V').Split('+', 2)[0].Split('-', 2);
            var core = coreAndPre[0].Split('.');
            if (core.Length != 3 || !core.All(part => int.TryParse(part, out _))) return false;
            version = (
                int.Parse(core[0]), int.Parse(core[1]), int.Parse(core[2]),
                coreAndPre.Length == 1 ? [] : coreAndPre[1].Split('.'));
            return version.PreRelease.All(part => part.Length > 0);
        }
    }
}

internal static class AuthenticodeTrust
{
    private static readonly Guid VerifyAction = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static bool IsTrusted(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        var fileInfo = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData(filePointer);
            return WinVerifyTrust(new nint(-1), VerifyAction, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal WinTrustFileInfo(string path)
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = nint.Zero;
            KnownSubject = nint.Zero;
        }

        private uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] private string FilePath;
        private nint FileHandle;
        private nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal WinTrustData(nint fileInfo)
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = nint.Zero;
            SipClientData = nint.Zero;
            UIChoice = 2;
            RevocationChecks = 1;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = nint.Zero;
            UrlReference = nint.Zero;
            ProviderFlags = 0x40;
            UIContext = 0;
            SignatureSettings = nint.Zero;
        }

        private uint Size;
        private nint PolicyCallbackData;
        private nint SipClientData;
        private uint UIChoice;
        private uint RevocationChecks;
        private uint UnionChoice;
        private nint FileInfo;
        private uint StateAction;
        private nint StateData;
        private nint UrlReference;
        private uint ProviderFlags;
        private uint UIContext;
        private nint SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(nint window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);
}

internal static class UpdateInstaller
{
    internal const string Argument = "--apply-update";

    internal static (bool Success, string? Error) Apply(int parentProcessId, string targetPath)
    {
        try
        {
            var source = Environment.ProcessPath ?? throw new InvalidOperationException("The updater path is unavailable.");
            var target = Path.GetFullPath(targetPath);
            if (!string.Equals(Path.GetFileName(target), "SockTuner.exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(target)
                || !AuthenticodeTrust.IsTrusted(source)
                || !AuthenticodeTrust.IsTrusted(target))
            {
                throw new InvalidDataException("The update target or Authenticode signature is invalid.");
            }

            try
            {
                using var parent = Process.GetProcessById(parentProcessId);
                if (!parent.WaitForExit(30_000)) throw new TimeoutException("SockTuner did not close in time.");
            }
            catch (ArgumentException)
            {
                // The parent already exited.
            }

            var staging = target + ".new";
            var backup = target + ".old";
            File.Copy(source, staging, true);
            File.Move(target, backup, true);
            try
            {
                File.Move(staging, target, true);
            }
            catch
            {
                File.Move(backup, target, true);
                throw;
            }

            try
            {
                File.Delete(backup);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The update is already committed; a stale backup is safer than reporting a false failure.
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! });
            return (true, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                           or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return (false, exception.Message);
        }
    }
}
