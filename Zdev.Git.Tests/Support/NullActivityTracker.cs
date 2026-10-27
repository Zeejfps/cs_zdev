using GitBench.Features.Repos;

namespace GitBench.Tests;

internal sealed class NullActivityTracker : IRepoActivityTracker
{
    private sealed class Scope : IDisposable { public void Dispose() { } }
    public IDisposable Begin(string repoPath) => new Scope();
    public bool IsActive(string repoPath) => false;
}
