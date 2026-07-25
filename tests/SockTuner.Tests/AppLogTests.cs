using SockTuner.Persistence;

namespace SockTuner.Tests;

public sealed class AppLogTests
{
    [Fact]
    public void RotateIfNeeded_MovesOnlyLogsAtTheLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var current = Path.Combine(directory, "current.jsonl");
            var previous = Path.Combine(directory, "previous.jsonl");
            File.WriteAllText(current, "1234");

            AppLog.RotateIfNeeded(current, previous, 5, 1);
            Assert.True(File.Exists(current));

            AppLog.RotateIfNeeded(current, previous, 5, 2);
            Assert.False(File.Exists(current));
            Assert.Equal("1234", File.ReadAllText(previous));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TrimToMaximum_KeepsNewestCompleteLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "old-line\nnew-1\nnew-2\n");

            AppLog.TrimToMaximum(path, 13);

            Assert.Equal("new-1\nnew-2\n", File.ReadAllText(path).Replace("\r\n", "\n"));
            Assert.True(new FileInfo(path).Length <= 13);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_CombinesPreviousAndCurrentHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var previous = Path.Combine(directory, "previous.jsonl");
            var current = Path.Combine(directory, "current.jsonl");
            var exported = Path.Combine(directory, "exported.jsonl");
            File.WriteAllText(previous, "old\n");
            File.WriteAllText(current, "new\n");

            AppLog.Export(current, previous, exported);

            Assert.Equal("old\nnew\n", File.ReadAllText(exported).Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
