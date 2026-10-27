using GitBench.Infrastructure;

namespace GitBench.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        DirectoryTree.Delete(Path);
    }
}
