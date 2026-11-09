using System.Text;

namespace Zdev.Git;

/// <summary>
/// The small text file Git LFS commits in place of a large one: a spec version, the content's
/// sha256 and its size. Parsing one is how a blob read gets from the pointer to the real bytes.
/// </summary>
public sealed record LfsPointer(string Oid, long Size)
{
    public const string VersionLine = "version https://git-lfs.github.com/spec/v1";

    // The spec caps a pointer at 1024 bytes; anything larger is real content, not a pointer.
    private const int MaxPointerBytes = 1024;

    public static LfsPointer? TryParse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxPointerBytes) return null;
        return TryParse(Encoding.UTF8.GetString(bytes));
    }

    public static LfsPointer? TryParse(string text)
    {
        if (!text.StartsWith(VersionLine, StringComparison.Ordinal)) return null;

        string? oid = null;
        long? size = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("oid sha256:", StringComparison.Ordinal))
                oid = line["oid sha256:".Length..];
            else if (line.StartsWith("size ", StringComparison.Ordinal)
                     && long.TryParse(line["size ".Length..], out var n) && n >= 0)
                size = n;
        }

        return oid is { Length: 64 } && IsLowerHex(oid) && size is { } s ? new LfsPointer(oid, s) : null;
    }

    /// <summary>Where git-lfs keeps this object once fetched: <c>lfs/objects/aa/bb/aabb…</c> under
    /// the common git dir, so a linked worktree finds what the primary already downloaded.</summary>
    public string LocalObjectPath(string commonGitDir)
        => Path.Combine(commonGitDir, "lfs", "objects", Oid[..2], Oid[2..4], Oid);

    private static bool IsLowerHex(string s)
    {
        foreach (var c in s)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
