using Zdev.Git;
using Zdev.Common;
using Xunit;

namespace Zdev.Git.Tests;

// Resetting a worktree that is kept for reuse, against a real repo with a submodule: whatever the
// last user left in it goes, and it ends up as a fresh `git worktree add` would make it, its
// submodule set up from the clone it already has.
public sealed class WorktreeResetTests : IDisposable
{
    // See WorktreeAddTests: lets the submodule be cloned from a folder.
    private static readonly bool FileTransportAllowed = AllowFileTransport();

    private static bool AllowFileTransport()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
        return true;
    }

    private readonly string _root;
    private readonly string _repoPath;
    private readonly string _subPath;
    private readonly string _worktree;
    private readonly GitService _git;
    private readonly Repo _repo;

    public WorktreeResetTests()
    {
        Assert.True(FileTransportAllowed);
        _root = Path.Combine(Path.GetTempPath(), "gitbench-wtreset-" + Guid.NewGuid().ToString("N"));
        _repoPath = Path.Combine(_root, "primary");
        _subPath = Path.Combine(_root, "sub");

        Directory.CreateDirectory(_subPath);
        TestGit.Init(_subPath);
        File.WriteAllText(Path.Combine(_subPath, "sub.txt"), "submodule");
        Git(_subPath, "add", ".");
        Git(_subPath, "commit", "-m", "sub");

        Directory.CreateDirectory(_repoPath);
        TestGit.Init(_repoPath);
        File.WriteAllText(Path.Combine(_repoPath, "file.txt"), "hello");
        File.WriteAllText(Path.Combine(_repoPath, ".gitignore"), "ignored.txt\n");
        Git(_repoPath, "add", ".");
        Git(_repoPath, "commit", "-m", "init");
        Git(_repoPath, "-c", "protocol.file.allow=always", "submodule", "add", "../sub", "sub");
        Git(_repoPath, "commit", "-m", "add submodule");

        _git = new GitService(new NullActivityTracker());
        _repo = new Repo(Guid.NewGuid(), _repoPath, "primary");
        _worktree = Path.Combine(_root, "kept");
        Assert.Equal(WorktreeAddOutcome.Ok, _git.AddWorktree(_repo, new WorktreeAddRequest(
            _worktree, "HEAD", "first", Force: false, InitSubmodules: true, RecurseSubmodules: true)));
    }

    public void Dispose()
    {
        DirectoryTree.Delete(_root);
    }

    [Fact]
    public void PutsTheWorktreeOnANewBranchAtTheStartPoint()
    {
        Git(_repoPath, "branch", "base", "HEAD~1");

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "refs/heads/base", "second", UpdateSubmodules: true));

        Assert.Equal(WorktreeResetOutcome.Ok, outcome);
        Assert.Equal("second", Git(_worktree, "branch", "--show-current").Trim());
        Assert.Equal(Git(_repoPath, "rev-parse", "base").Trim(), Git(_worktree, "rev-parse", "HEAD").Trim());
        // The commit before the submodule was added: its folder, untracked there, is cleaned away.
        Assert.False(Directory.Exists(Path.Combine(_worktree, "sub")));
    }

    [Fact]
    public void ThrowsAwayChangesUntrackedAndIgnoredFilesInItAndItsSubmodules()
    {
        File.WriteAllText(Path.Combine(_worktree, "file.txt"), "changed");
        File.WriteAllText(Path.Combine(_worktree, "untracked.txt"), "new");
        File.WriteAllText(Path.Combine(_worktree, "ignored.txt"), "build output");
        File.WriteAllText(Path.Combine(_worktree, "sub", "sub.txt"), "changed in the submodule");
        File.WriteAllText(Path.Combine(_worktree, "sub", "untracked.txt"), "new in the submodule");

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "HEAD", "second", UpdateSubmodules: true));

        Assert.Equal(WorktreeResetOutcome.Ok, outcome);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_worktree, "file.txt")));
        Assert.False(File.Exists(Path.Combine(_worktree, "untracked.txt")));
        Assert.False(File.Exists(Path.Combine(_worktree, "ignored.txt")));
        Assert.Equal("submodule", File.ReadAllText(Path.Combine(_worktree, "sub", "sub.txt")));
        Assert.False(File.Exists(Path.Combine(_worktree, "sub", "untracked.txt")));
    }

    [Fact]
    public void SetsUpTheSubmodulesFromTheCloneTheWorktreeAlreadyHas()
    {
        // With the submodule's own repository gone, only the worktree's clone of it can set it up.
        Git(_repoPath, "branch", "base", "HEAD~1");
        Assert.Equal(WorktreeResetOutcome.Ok, _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "refs/heads/base", null, UpdateSubmodules: true)));
        DirectoryTree.Delete(_subPath);

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "refs/heads/main", "second", UpdateSubmodules: true));

        Assert.Equal(WorktreeResetOutcome.Ok, outcome);
        Assert.Equal("submodule", File.ReadAllText(Path.Combine(_worktree, "sub", "sub.txt")));
    }

    [Fact]
    public void DetachesWhenNoBranchIsAskedFor()
    {
        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "HEAD", null, UpdateSubmodules: false));

        Assert.Equal(WorktreeResetOutcome.Ok, outcome);
        Assert.Equal("", Git(_worktree, "branch", "--show-current").Trim());
        Git(_repoPath, "branch", "-D", "first");
    }

    [Fact]
    public void GivesUpAMergeInProgress()
    {
        Git(_worktree, "checkout", "-q", "-b", "other");
        File.WriteAllText(Path.Combine(_worktree, "file.txt"), "other");
        Git(_worktree, "commit", "-q", "-am", "other");
        Git(_worktree, "checkout", "-q", "first");
        File.WriteAllText(Path.Combine(_worktree, "file.txt"), "first");
        Git(_worktree, "commit", "-q", "-am", "first");
        Assert.NotEqual(0, TestGit.Try(_worktree, "merge", "other").ExitCode);
        Assert.Equal(RepoOperationState.Merge, _git.GetOperationState(_repo with { Path = _worktree }));

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "HEAD", "second", UpdateSubmodules: true));

        Assert.Equal(WorktreeResetOutcome.Ok, outcome);
        Assert.Equal(RepoOperationState.None, _git.GetOperationState(_repo with { Path = _worktree }));
        Assert.Equal("", Git(_worktree, "status", "--porcelain").Trim());
    }

    [Fact]
    public void ABranchThatExistsAlreadyIsRefusedAndTheWorktreeIsLeftAsItWas()
    {
        File.WriteAllText(Path.Combine(_worktree, "untracked.txt"), "kept");
        Git(_repoPath, "branch", "taken");

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "HEAD", "taken", UpdateSubmodules: true));

        Assert.IsType<WorktreeResetOutcome.Failed>(outcome);
        Assert.Equal("first", Git(_worktree, "branch", "--show-current").Trim());
        Assert.True(File.Exists(Path.Combine(_worktree, "untracked.txt")));
    }

    [Fact]
    public void ABranchThatExistsAlreadyIsRefusedBeforeAMergeInProgressIsGivenUp()
    {
        File.WriteAllText(Path.Combine(_worktree, "untracked.txt"), "kept");
        var head = Git(_worktree, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(GitDir(_worktree), "MERGE_HEAD"), head + "\n");
        Git(_repoPath, "branch", "taken");

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "HEAD", "taken", UpdateSubmodules: true));

        Assert.IsType<WorktreeResetOutcome.Failed>(outcome);
        Assert.Equal(RepoOperationState.Merge, _git.GetOperationState(_repo with { Path = _worktree }));
        Assert.True(File.Exists(Path.Combine(_worktree, "untracked.txt")));
    }

    [Fact]
    public void RefusesTheMainCheckoutAndAnotherRepositoryAndLeavesThemAlone()
    {
        File.WriteAllText(Path.Combine(_repoPath, "untracked.txt"), "mine");
        var other = Path.Combine(_root, "other");
        TestGit.Init(other);
        Git(other, "commit", "--allow-empty", "-m", "other");
        File.WriteAllText(Path.Combine(other, "untracked.txt"), "theirs");

        var main = _git.ResetWorktree(_repo, new WorktreeResetRequest(_repoPath, "HEAD", null, UpdateSubmodules: false));
        var foreign = _git.ResetWorktree(_repo, new WorktreeResetRequest(other, "HEAD", null, UpdateSubmodules: false));

        Assert.IsType<WorktreeResetOutcome.Failed>(main);
        Assert.IsType<WorktreeResetOutcome.Failed>(foreign);
        Assert.True(File.Exists(Path.Combine(_repoPath, "untracked.txt")));
        Assert.True(File.Exists(Path.Combine(other, "untracked.txt")));
    }

    [Fact]
    public void GivesUpWhatASubmoduleIsInTheMiddleOf()
    {
        var sub = Path.Combine(_worktree, "sub");
        var subHead = Git(sub, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(GitDir(sub), "MERGE_HEAD"), subHead + "\n");
        Directory.CreateDirectory(Path.Combine(GitDir(sub), "rebase-merge"));
        Assert.NotEqual(RepoOperationState.None, _git.GetOperationState(_repo with { Path = sub }));

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "HEAD", "second", UpdateSubmodules: true));

        Assert.Equal(WorktreeResetOutcome.Ok, outcome);
        Assert.Equal(RepoOperationState.None, _git.GetOperationState(_repo with { Path = sub }));
    }

    [Fact]
    public void ChecksOutTheRecordedSubmoduleCommitWhateverUpdateStrategyIsConfigured()
    {
        File.WriteAllText(Path.Combine(_subPath, "sub.txt"), "later");
        Git(_subPath, "commit", "-am", "later");
        Git(Path.Combine(_repoPath, "sub"), "pull", "-q", "origin", "main");
        Git(_repoPath, "commit", "-am", "bump sub");
        Git(_repoPath, "config", "submodule.sub.update", "none");

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "refs/heads/main", "second", UpdateSubmodules: true));

        Assert.Equal(WorktreeResetOutcome.Ok, outcome);
        Assert.Equal("later", File.ReadAllText(Path.Combine(_worktree, "sub", "sub.txt")));
    }

    [Fact]
    public void PointsAKeptSubmoduleAtTheUrlItsSuperprojectNowGives()
    {
        var moved = Path.Combine(_root, "moved-sub");
        Directory.Move(_subPath, moved);
        Git(_repoPath, "config", "submodule.sub.url", moved);

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "HEAD", "second", UpdateSubmodules: true));

        Assert.Equal(WorktreeResetOutcome.Ok, outcome);
        Assert.Equal(moved, Git(Path.Combine(_worktree, "sub"), "remote", "get-url", "origin").Trim());
    }

    [Fact]
    public void AHookFailingAfterTheCheckoutIsAWarningNotAFailure()
    {
        var hooks = Path.Combine(_repoPath, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "post-checkout"), "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(hooks, "post-checkout"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var outcome = _git.ResetWorktree(_repo, new WorktreeResetRequest(_worktree, "HEAD", "second", UpdateSubmodules: true));

        var reset = Assert.IsType<WorktreeResetOutcome.Reset>(outcome);
        Assert.NotNull(reset.Warning);
        Assert.Equal("second", Git(_worktree, "branch", "--show-current").Trim());
    }

    private static string GitDir(string tree)
    {
        var dir = Git(tree, "rev-parse", "--git-dir").Trim();
        return Path.IsPathRooted(dir) ? dir : Path.GetFullPath(Path.Combine(tree, dir));
    }

    private static string Git(string cwd, params string[] args) => TestGit.Run(cwd, args);
}
