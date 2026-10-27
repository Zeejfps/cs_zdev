using System.Text;
using Zdev.Git;

namespace Zdev.Git.Tests;

/// <summary>
/// A real repository parked mid-merge (or mid-rebase) with one conflict of every shape the conflict
/// tools have to describe.
/// </summary>
/// <remarks>
/// Built with the git binary rather than faked, because the whole question these tests ask is what
/// the index looks like after git left it half-merged: which of stages 1/2/3 exist per path, what a
/// delete/modify leaves behind, and what a path git would have to quote comes back as. A fake index
/// would answer whatever the implementation assumed.
/// </remarks>
internal sealed class ConflictedRepo : IDisposable
{
    // Long enough that any per-side cap worth having has to cut it short.
    public const int LongFileLines = 1200;

    private readonly TempDir _dir;

    private ConflictedRepo(TempDir dir, string displayName)
    {
        _dir = dir;
        Repo = new Repo(Guid.NewGuid(), dir.Path, displayName);
    }

    public string Path => _dir.Path;

    public Repo Repo { get; }

    /// <summary>
    /// A merge of <c>feature</c> into <c>main</c> that stopped on every kind of conflict at once:
    /// modify/modify, add/add, modify/delete, a binary file, a very long file, and a path whose name
    /// git would C-quote. <c>quiet.txt</c> is present and not conflicted.
    /// </summary>
    public static ConflictedRepo Merging()
    {
        var it = new ConflictedRepo(new TempDir("gitbench-conflict-merge-"), "merging");

        it.Init();
        it.Write("a.txt", "one\ntwo\nthree\n");
        it.Write("gone.txt", "still here\n");
        it.Write("naïve name.txt", "base\n");
        it.Write("quiet.txt", "neither side touched this\n");
        it.Write("long.txt", LongFile("base"));
        it.WriteBytes("logo.bin", new byte[] { 0x89, 0x00, 0x01, 0x02 });
        it.Git("add", ".");
        it.Commit("seed the tree");

        it.Git("checkout", "-q", "-b", "feature");
        it.Write("a.txt", "one\nfrom feature\nthree\n");
        it.Git("rm", "-q", "gone.txt");
        it.Write("naïve name.txt", "feature\n");
        it.Write("fresh.txt", "feature's idea\n");
        it.Write("long.txt", LongFile("feature"));
        it.WriteBytes("logo.bin", new byte[] { 0x89, 0x00, 0x01, 0x03 });
        it.Git("add", "-A");
        it.Commit("what feature did");

        it.Git("checkout", "-q", "main");
        it.Write("a.txt", "one\nfrom main\nthree\n");
        it.Write("gone.txt", "still here, and edited\n");
        it.Write("naïve name.txt", "main\n");
        it.Write("fresh.txt", "main's idea\n");
        it.Write("long.txt", LongFile("main"));
        it.WriteBytes("logo.bin", new byte[] { 0x89, 0x00, 0x01, 0x04 });
        it.Git("add", "-A");
        it.Commit("what main did");

        it.Expect(false, "merge", "--no-edit", "feature");
        return it;
    }

    /// <summary>A rebase of <c>feature</c> onto <c>main</c> stopped on one modify/modify conflict —
    /// the operation whose ours/theirs are the inverse of what a reader expects.</summary>
    public static ConflictedRepo Rebasing()
    {
        var it = new ConflictedRepo(new TempDir("gitbench-conflict-rebase-"), "rebasing");

        it.Init();
        it.Write("a.txt", "one\ntwo\nthree\n");
        it.Git("add", ".");
        it.Commit("seed the tree");

        it.Git("checkout", "-q", "-b", "feature");
        it.Write("a.txt", "one\nfrom feature\nthree\n");
        it.Git("add", "-A");
        it.Commit("what feature did");

        it.Git("checkout", "-q", "main");
        it.Write("a.txt", "one\nfrom main\nthree\n");
        it.Git("add", "-A");
        it.Commit("what main did");

        it.Git("checkout", "-q", "feature");
        it.Expect(false, "rebase", "main");
        return it;
    }

    public void Dispose() => _dir.Dispose();

    public void Write(string path, string text) =>
        File.WriteAllText(System.IO.Path.Combine(Path, path), text, new UTF8Encoding(false));

    public void WriteBytes(string path, byte[] bytes) =>
        File.WriteAllBytes(System.IO.Path.Combine(Path, path), bytes);

    public string Read(string path) => File.ReadAllText(System.IO.Path.Combine(Path, path));

    public bool Exists(string path) => File.Exists(System.IO.Path.Combine(Path, path));

    public string Git(params string[] args) => Expect(true, args);

    /// <summary>The unmerged index as git itself prints it, for a test that wants git's own answer
    /// rather than the service's.</summary>
    public string UnmergedIndex() => Git("ls-files", "-u");

    public string Commit(string message) => Git("commit", "-m", message);

    private void Init()
    {
        TestGit.Init(Path);
        // The listing has to survive a path git would otherwise C-quote; turning quoting off in the
        // repository's own config would hide exactly the bug worth catching.
        Git("config", "core.quotePath", "true");
        // Every side here is written and asserted with LF. On a machine whose global config sets
        // core.autocrlf=true, git checks the ours/theirs blob back out as CRLF and the sides stop
        // matching the bytes the fixture committed — a property of the machine, not of the code.
        Git("config", "core.autocrlf", "false");
    }

    private static string LongFile(string marker) =>
        string.Concat(Enumerable.Range(1, LongFileLines).Select(n => $"{marker} line {n}\n"));

    private string Expect(bool success, params string[] args)
    {
        var run = TestGit.Try(Path, args);
        if (success && run.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {run.Stderr}");
        if (!success && run.ExitCode == 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} was expected to conflict but succeeded.");
        return run.Stdout;
    }
}
