using System.IO;
using System.Text;
using System.Text.Json;

namespace SockTuner.Persistence;

public static class AppLog
{
    private const long MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumMessageCharacters = 32 * 1024;
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrimeBuild",
        "SockTuner",
        "Logs");

    public static string CurrentPath => Path.Combine(LogDirectory, "SockTuner.jsonl");
    private static string PreviousPath => Path.Combine(LogDirectory, "SockTuner.previous.jsonl");

    public static string? Write(string eventName, string message)
    {
        try
        {
            var boundedMessage = message.Length <= MaximumMessageCharacters
                ? message
                : message[..MaximumMessageCharacters] + "…[truncated]";
            var line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                eventName,
                message = boundedMessage
            }) + Environment.NewLine;

            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(CurrentPath, PreviousPath, MaximumBytes, Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(CurrentPath, line);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    public static void Export(string destination)
    {
        lock (Sync)
        {
            Export(CurrentPath, PreviousPath, destination);
        }
    }

    internal static void Export(string current, string previous, string destination)
    {
        var sources = new[] { previous, current }.Where(File.Exists).ToArray();
        if (sources.Length == 0)
        {
            throw new FileNotFoundException("No SockTuner log is available yet.", current);
        }

        var destinationPath = Path.GetFullPath(destination);
        if (sources.Any(source => string.Equals(Path.GetFullPath(source), destinationPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("Choose an export path outside SockTuner's active log files.");
        }

        using var output = File.Create(destination);
        foreach (var source in sources)
        {
            using var input = File.OpenRead(source);
            input.CopyTo(output);
        }
    }

    internal static void RotateIfNeeded(string current, string previous, long maximumBytes, long pendingBytes)
    {
        var file = new FileInfo(current);
        if (!file.Exists || file.Length + pendingBytes <= maximumBytes)
        {
            return;
        }

        File.Move(current, previous, true);
    }
}
