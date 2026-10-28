namespace Zdev.Agents.Acp;

/// <summary>What the write guard answers a shell command that neither of the rules' lists names.</summary>
public enum AcpCommandFallback
{
    Allow,
    Ask,
}

/// <summary>
/// The answers the user gave the write guard ahead of time. A shell command matching an entry of
/// <see cref="AskBefore"/> anywhere in it is put to the user; one whose every part starts with an
/// entry of <see cref="AlwaysAllow"/> is allowed; any other gets <see cref="OtherCommands"/>. Where
/// <see cref="AllowsWorkspaceEdits"/>, a file edit or move whose every path is inside the session's
/// folder and matches none of <see cref="ProtectedFiles"/> is allowed; any other is put to the user.
/// The tools of the MCP servers named in <see cref="AllowedServers"/> are allowed.
/// </summary>
public sealed record AcpApprovalRules(
    IReadOnlyList<string> AskBefore,
    IReadOnlyList<string> AlwaysAllow,
    AcpCommandFallback OtherCommands,
    bool AllowsWorkspaceEdits,
    IReadOnlyList<string> ProtectedFiles,
    IReadOnlyList<string> AllowedServers)
{
    /// <summary>The guard as it stands with no rules of the user's: shell commands allowed but a push,
    /// every edit put to the user, and no MCP server's tools allowed but the app's own.</summary>
    public static readonly AcpApprovalRules AskingForEdits = new(["git push"], [], AcpCommandFallback.Allow, false, [], []);

    /// <summary>Whether the tools of <paramref name="server"/> are allowed, its name in any case.</summary>
    public bool AllowsServer(string server) => AllowedServers.Any(s => string.Equals(s, server, StringComparison.OrdinalIgnoreCase));

    public bool Equals(AcpApprovalRules? other) =>
        other is not null
        && AskBefore.SequenceEqual(other.AskBefore)
        && AlwaysAllow.SequenceEqual(other.AlwaysAllow)
        && OtherCommands == other.OtherCommands
        && AllowsWorkspaceEdits == other.AllowsWorkspaceEdits
        && ProtectedFiles.SequenceEqual(other.ProtectedFiles)
        && AllowedServers.SequenceEqual(other.AllowedServers);

    public override int GetHashCode() =>
        HashCode.Combine(AskBefore.Count, AlwaysAllow.Count, OtherCommands, AllowsWorkspaceEdits, ProtectedFiles.Count, AllowedServers.Count);
}
