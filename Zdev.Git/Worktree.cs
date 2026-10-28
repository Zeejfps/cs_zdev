namespace Zdev.Git;

public sealed record WorktreeInfo(
    string Path,
    string? HeadSha,
    string? Branch,
    bool IsDetached,
    bool IsBare,
    bool IsLocked,
    string? LockReason,
    bool IsPrunable,
    string? PrunableReason);

public sealed record WorktreeAddRequest(
    string Path,
    string StartPoint,
    string? NewBranchName,
    bool Force,
    bool InitSubmodules,
    bool RecurseSubmodules);

// Puts an existing worktree back to a clean checkout of StartPoint, as if it had just been added
// there: on the new branch NewBranchName, or detached when that is null. Whatever was in it is
// thrown away, ignored files and an operation in progress included. UpdateSubmodules also checks
// out the submodules StartPoint records, nested ones too, initializing those not set up yet.
public sealed record WorktreeResetRequest(
    string Path,
    string StartPoint,
    string? NewBranchName,
    bool UpdateSubmodules);


