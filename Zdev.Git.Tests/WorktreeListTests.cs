using Zdev.Git;
using Xunit;

namespace Zdev.Git.Tests;

// A listing git can't complete is reported rather than returned empty: an empty list reads as "no
// worktrees", and RemoveWorktree would take a worktree it can't find for one git already let go of.
public sealed class WorktreeListTests : IDisposable
{
    private readonly TempDir _dir = new("zdev-wtlist-");
    private readonly GitService _git = new(new NullActivityTracker());

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_repository_git_cannot_read_fails_to_list_instead_of_listing_nothing()
    {
        // A .git file passes the cheap "is this a repository" check but isn't a gitlink git accepts.
        var path = Path.Combine(_dir.Path, "broken");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".git"), "not a gitlink\n");
        var repo = new Repo(Guid.NewGuid(), path, "broken");

        var listing = _git.ListWorktrees(repo);

        var failed = Assert.IsType<Fetched<IReadOnlyList<WorktreeInfo>>.Failed>(listing);
        Assert.Contains("gitfile", failed.Message);
    }

    [Fact]
    public void A_folder_that_is_not_a_repository_fails_to_list()
    {
        var repo = new Repo(Guid.NewGuid(), _dir.Path, "plain");

        Assert.IsType<Fetched<IReadOnlyList<WorktreeInfo>>.Failed>(_git.ListWorktrees(repo));
    }
}
