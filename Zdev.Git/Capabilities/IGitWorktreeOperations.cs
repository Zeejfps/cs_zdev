using Zdev.Git;

namespace Zdev.Git;

public interface IGitWorktreeOperations
{
    Fetched<IReadOnlyList<WorktreeInfo>> ListWorktrees(Repo primary);
    WorktreeAddOutcome AddWorktree(Repo primary, WorktreeAddRequest request);
    WorktreeRemoveOutcome RemoveWorktree(Repo primary, string worktreePath, bool force);
    WorktreeResetOutcome ResetWorktree(Repo primary, WorktreeResetRequest request);
    GitOutcome UnlockWorktree(Repo primary, string worktreePath);
    GitOutcome PruneWorktrees(Repo primary);
}
