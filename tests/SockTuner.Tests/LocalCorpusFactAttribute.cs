using System.IO;

namespace SockTuner.Tests;

/// <summary>
/// Marks a test that reads the private research corpus under <c>research/</c>. That directory is
/// deliberately gitignored — it holds real measurements from real connections — so these tests
/// skip themselves on CI and on a clean clone rather than failing there. Everything they cover is
/// also covered deterministically against the fixtures in <c>tests/SockTuner.Tests/Fixtures</c>;
/// these exist to check that the committed fixtures still describe the real files.
/// </summary>
public sealed class LocalCorpusFactAttribute : FactAttribute
{
    public LocalCorpusFactAttribute(string relativePath)
    {
        var path = TestPaths.InRepository(relativePath);
        if (!File.Exists(path))
        {
            Skip = $"Not present in this checkout: {relativePath} (the research corpus is local-only).";
        }
    }
}

/// <summary>Locates files relative to the repository root, wherever the test binary was built.</summary>
public static class TestPaths
{
    public static string InRepository(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SockTuner.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
