using Zdev.Git;

namespace Zdev.Git;

public interface IGitWorktreeOperations
{
    IReadOnlyList<WorktreeInfo> ListWorktrees(Repo primary);
    WorktreeAddOutcome AddWorktree(Repo primary, WorktreeAddRequest request);
    WorktreeRemoveOutcome RemoveWorktree(Repo primary, string worktreePath, bool force);
    GitOutcome UnlockWorktree(Repo primary, string worktreePath);
    GitOutcome PruneWorktrees(Repo primary);
}
