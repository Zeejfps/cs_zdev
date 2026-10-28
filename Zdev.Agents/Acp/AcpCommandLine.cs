using System.Text;
using System.Text.RegularExpressions;

namespace Zdev.Agents.Acp;

/// <summary>
/// A shell command line as the write guard reads it against the user's command entries. An entry is
/// a program and the words after it, such as <c>git push</c> or <c>dotnet test</c>, matched without
/// regard to case; the program may carry a folder and an <c>.exe</c> or <c>.cmd</c>, and git's global
/// options may come between <c>git</c> and the word after it.
/// </summary>
public static partial class AcpCommandLine
{
    /// <summary>Whether <paramref name="command"/> runs <paramref name="entry"/> anywhere in it, in
    /// any part of a chain or a script a shell is handed. For the entries to ask before.</summary>
    public static bool Runs(string command, string entry)
    {
        var words = Words(entry);
        if (words.Length == 0) return false;
        var program = Regex.Escape(words[0]);
        var options = words[0].Equals("git", StringComparison.OrdinalIgnoreCase) ? GitOptions : string.Empty;
        var rest = string.Concat(words.Skip(1).Select(w => @"\s+" + Regex.Escape(w)));
        return Regex.IsMatch(command, $@"(?<![\w.-]){program}(?:\.exe|\.cmd)?{options}{rest}(?![\w-])", RegexOptions.IgnoreCase);
    }

    private const string GitOptions = @"(?:\s+(?:-C|-c|--git-dir|--work-tree)\s+\S+|\s+-\S+)*";

    /// <summary>
    /// Whether every part of <paramref name="command"/> starts with one of <paramref name="entries"/>:
    /// each command of a chain joined by <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c> or <c>&amp;</c>, once a shell
    /// it is handed to (<c>bash -lc</c>, <c>pwsh -Command</c>, <c>cmd /c</c>) is set aside. A command
    /// that substitutes another's output, or writes its own to a file, is never one of them, nor is a
    /// command that sets a variable before its program.
    /// </summary>
    public static bool StartsEveryPart(string command, IReadOnlyList<string> entries)
    {
        if (Parts(Unwrap(command)) is not { Count: > 0 } parts) return false;
        var allowed = entries.Select(Words).Where(w => w.Length > 0).ToList();
        return parts.All(part => allowed.Any(entry => StartsWith(part, entry)));
    }

    private static bool StartsWith(IReadOnlyList<string> part, string[] entry)
    {
        if (!IsProgram(part[0], entry[0])) return false;
        var next = 1;
        if (entry[0].Equals("git", StringComparison.OrdinalIgnoreCase)) next = SkipGitOptions(part, next);
        for (var i = 1; i < entry.Length; i++, next++)
            if (next >= part.Count || !part[next].Equals(entry[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    /// <summary>Whether <paramref name="word"/> names <paramref name="program"/>, from any folder.</summary>
    private static bool IsProgram(string word, string program)
    {
        var name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..];
        foreach (var suffix in new[] { ".exe", ".cmd" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
        return name.Equals(program, StringComparison.OrdinalIgnoreCase);
    }

    private static int SkipGitOptions(IReadOnlyList<string> part, int next)
    {
        while (next < part.Count && part[next].StartsWith('-'))
            next += part[next] is "-C" or "-c" or "--git-dir" or "--work-tree" ? 2 : 1;
        return next;
    }

    /// <summary>The script a shell is handed, rather than the shell; the command itself otherwise.</summary>
    private static string Unwrap(string command)
    {
        var match = ShellWrapper().Match(command);
        if (!match.Success) return command;
        var script = command[match.Length..].Trim();
        if (script.Length >= 2 && script[0] is '"' or '\'' && script[^1] == script[0]) script = script[1..^1];
        return script;
    }

    [GeneratedRegex(@"^\s*(?:\S*[/\\])?(?:(?:ba|z)?sh(?:\.exe)?\s+-l?c|(?:pwsh|powershell)(?:\.exe)?(?:\s+-(?!c\b|command\b)\w+)*\s+-c(?:ommand)?|cmd(?:\.exe)?\s+/c)\s+",
        RegexOptions.IgnoreCase)]
    private static partial Regex ShellWrapper();

    /// <summary>The words of each command chained in <paramref name="script"/>, quotes taken off; null
    /// where one substitutes output, writes to a file or sets a variable first.</summary>
    private static List<List<string>>? Parts(string script)
    {
        var parts = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        char? quote = null;

        void EndWord()
        {
            if (inWord) words.Add(word.ToString());
            word.Clear();
            inWord = false;
        }

        bool EndPart()
        {
            EndWord();
            if (words.Count > 0)
            {
                if (words[0].Contains('=')) return false;
                parts.Add(words);
            }
            words = new List<string>();
            return true;
        }

        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            if (c == '`' || (c == '$' && i + 1 < script.Length && script[i + 1] == '(')) return null;
            if (quote is { } open)
            {
                if (c == open) quote = null;
                else word.Append(c);
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    inWord = true;
                    break;
                case '&' or '|' or ';' or '\n' or '\r':
                    if (!EndPart()) return null;
                    break;
                case '>':
                    if (i + 1 < script.Length && script[i + 1] == '(') return null;
                    if (!WritesNowhere(script, i + 1, out var end)) return null;
                    EndWord();
                    i = end - 1;
                    break;
                case '<' when i + 1 < script.Length && script[i + 1] == '(':
                    return null;
                default:
                    if (char.IsWhiteSpace(c)) EndWord();
                    else
                    {
                        word.Append(c);
                        inWord = true;
                    }
                    break;
            }
        }

        if (quote is not null) return null;
        return EndPart() ? parts : null;
    }

    /// <summary>Whether the redirection whose target starts at <paramref name="start"/> goes to another
    /// stream or to nowhere, rather than to a file; <paramref name="end"/> is just past its target.</summary>
    private static bool WritesNowhere(string script, int start, out int end)
    {
        var i = start;
        if (i < script.Length && script[i] == '>') i++;
        if (i < script.Length && script[i] == '&')
        {
            i++;
            while (i < script.Length && char.IsDigit(script[i])) i++;
            end = i;
            return true;
        }

        while (i < script.Length && script[i] == ' ') i++;
        var targetStart = i;
        while (i < script.Length && !char.IsWhiteSpace(script[i]) && script[i] is not ('&' or '|' or ';')) i++;
        end = i;
        var target = script[targetStart..i];
        return target.Equals("/dev/null", StringComparison.Ordinal) || target.Equals("nul", StringComparison.OrdinalIgnoreCase)
                                                                     || target.Equals("$null", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Words(string entry) => entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
