using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Zdev.Git.Tests;

// A file Git LFS tracks is committed as a pointer; a blob read for a picture has to come back with
// the picture, or the diff pane shows the pointer's hash instead of the image.
public sealed class LfsPointerTests : IDisposable
{
    // Bigger than any pointer, so a cap between the two tells the pointer read from the object read.
    private static readonly byte[] Content = [0x89, 0x50, 0x4E, 0x47, .. Enumerable.Range(0, 2000).Select(i => (byte)i)];
    private const int Cap = 4096;

    private readonly TempGitRepo _dir = TempGitRepo.Init();
    private readonly GitService _git = new(new RepoActivityTracker());
    private readonly Repo _repo;

    public LfsPointerTests() => _repo = new Repo(Guid.NewGuid(), _dir.Path, "lfs");

    public void Dispose()
    {
        _git.Dispose();
        _dir.Dispose();
    }

    private static string Oid => Convert.ToHexStringLower(SHA256.HashData(Content));

    private static string PointerText =>
        $"{LfsPointer.VersionLine}\noid sha256:{Oid}\nsize {Content.Length}\n";

    private void StoreObjectLocally()
    {
        var path = new LfsPointer(Oid, Content.Length).LocalObjectPath(Path.Combine(_dir.Path, ".git"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Content);
    }

    [Fact]
    public void TryParse_ReadsOidAndSize()
    {
        var pointer = LfsPointer.TryParse(Encoding.UTF8.GetBytes(PointerText));

        Assert.Equal(new LfsPointer(Oid, Content.Length), pointer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world\n")]
    [InlineData("version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 3\n")]
    [InlineData("version https://git-lfs.github.com/spec/v1\nsize 3\n")]
    public void TryParse_RejectsWhatIsNotAPointer(string text)
        => Assert.Null(LfsPointer.TryParse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void GetFileBytes_OfACommittedPointer_ReturnsTheLocalObject()
    {
        StoreObjectLocally();
        var sha = _dir.Commit("logo.png", PointerText, "add logo");

        var bytes = _git.GetFileBytes(_repo, "logo.png", DiffSide.Commit, oldSide: false, Cap, sha);

        Assert.Equal(Content, bytes);
    }

    [Fact]
    public void GetFileBytes_OfAWorkingTreePointer_ReturnsTheLocalObject()
    {
        StoreObjectLocally();
        _dir.Write("logo.png", PointerText);

        var bytes = _git.GetFileBytes(_repo, "logo.png", DiffSide.WorkingTree, oldSide: false, Cap);

        Assert.Equal(Content, bytes);
    }

    // No object and no lfs filter to fetch it: nothing, never the pointer text dressed up as content.
    [Fact]
    public void GetFileBytes_OfAPointerWhoseObjectIsMissing_IsNull()
    {
        var sha = _dir.Commit("logo.png", PointerText, "add logo");

        Assert.Null(_git.GetFileBytes(_repo, "logo.png", DiffSide.Commit, oldSide: false, Cap, sha));
    }

    [Fact]
    public void GetFileBytes_OfAPointerLargerThanTheCap_IsNull()
    {
        StoreObjectLocally();
        var sha = _dir.Commit("logo.png", PointerText, "add logo");

        Assert.Null(_git.GetFileBytes(_repo, "logo.png", DiffSide.Commit, oldSide: false, 1024, sha));
    }
}
