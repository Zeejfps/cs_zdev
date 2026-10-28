using System.Text;
using System.Text.RegularExpressions;

namespace Zdev.Agents.Acp;

/// <summary>
/// The files a tool call names, as the write guard reads them against the session's folder and the
/// user's protected files. A protected-file entry is a glob over the path inside the folder, with
/// <c>/</c> between folders: <c>*</c> and <c>?</c> stay within a name, <c>**</c> crosses folders.
/// Like a <c>.gitignore</c> line, an entry with no <c>/</c> in it, a trailing one aside, matches a
/// name at any depth, and any other only from the folder's top; an entry naming a folder covers
/// everything in it. Case is ignored, so an entry can't be got round on a file system that ignores it.
/// </summary>
public static class AcpWorkspaceFiles
{
    /// <summary>Where <paramref name="path"/>, absolute or relative to <paramref name="workspace"/>,
    /// is inside it, with <c>/</c> between folders; null when it is the folder itself or outside it.</summary>
    public static string? Inside(string workspace, string path)
    {
        string root, full;
        try
        {
            root = Path.GetFullPath(workspace);
            full = Path.GetFullPath(path, root);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || Path.IsPathRooted(relative)) return null;
        relative = relative.Replace('\\', '/');
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ? null : relative;
    }

    /// <summary>Whether <paramref name="relative"/>, a path inside the folder, matches <paramref name="entry"/>.</summary>
    public static bool Matches(string relative, string entry)
    {
        var glob = entry.Trim().Replace('\\', '/').TrimEnd('/');
        if (glob.Length == 0) return false;
        var anchored = glob.StartsWith('/') || glob.Contains('/');
        glob = glob.TrimStart('/');
        var pattern = (anchored ? "^" : "^(?:.*/)?") + GlobBody(glob) + "(?:/.*)?$";
        return Regex.IsMatch(relative, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string GlobBody(string glob)
    {
        var body = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var crossesFolder = i + 2 < glob.Length && glob[i + 2] == '/';
                body.Append(crossesFolder ? "(?:.*/)?" : ".*");
                i += crossesFolder ? 2 : 1;
            }
            else if (c == '*') body.Append("[^/]*");
            else if (c == '?') body.Append("[^/]");
            else body.Append(Regex.Escape(c.ToString()));
        }

        return body.ToString();
    }
}
