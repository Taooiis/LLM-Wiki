using LlmWiki.App.Services;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>路径防护：存储目录解析 / 库内安全拼接 / 库名校验（对照 server.js:15-47）。</summary>
public class PathGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmwiki-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ---------- ResolveWikisDir ----------

    [Fact]
    public void ResolveWikisDir_NullOrEmpty_FallsBackToAppRootWikis()
    {
        var r = PathGuard.ResolveWikisDir(null, _root);
        Assert.Equal(Path.Combine(_root, "wikis"), r);
        Assert.Equal(Path.Combine(_root, "wikis"), PathGuard.ResolveWikisDir(" ", _root));
    }

    [Fact]
    public void ResolveWikisDir_RelativePath_IsRelativeToAppRoot()
    {
        var r = PathGuard.ResolveWikisDir("我的库", _root);
        Assert.Equal(Path.Combine(_root, "我的库"), r);
    }

    [Fact]
    public void ResolveWikisDir_AbsolutePath_NormalizesAndWins()
    {
        var dir = Path.Combine(_root, "abs", "wikis");
        var r = PathGuard.ResolveWikisDir(dir, _root);
        Assert.Equal(dir, r);
    }

    [Fact]
    public void ResolveWikisDir_SingleBackslashPrefix_IsIllegal()
    {
        // 单个反斜杠开头 = 盘符相对路径，解析不可控，判非法（Demo 同规则）
        Assert.Null(PathGuard.ResolveWikisDir("\\foo", _root));
        // UNC（双反斜杠）合法
        Assert.Equal("\\\\pc\\share\\wikis", PathGuard.ResolveWikisDir("\\\\pc\\share\\wikis", _root));
    }

    // ---------- SafeJoin ----------

    [Fact]
    public void SafeJoin_InsideKb_ReturnsCombined()
    {
        var wikis = Path.Combine(_root, "wikis");
        var p = PathGuard.SafeJoin(wikis, "测试库", @"raw\sources\a.md");
        Assert.Equal(Path.Combine(wikis, "测试库", "raw", "sources", "a.md"), p);
    }

    [Fact]
    public void SafeJoin_ParentEscape_IsRejected()
    {
        var wikis = Path.Combine(_root, "wikis");
        Assert.Null(PathGuard.SafeJoin(wikis, "kb", @"..\evil.txt"));
        Assert.Null(PathGuard.SafeJoin(wikis, "kb", @"raw\..\..\evil.txt"));
    }

    [Fact]
    public void SafeJoin_AbsoluteRel_IsRejected()
    {
        var wikis = Path.Combine(_root, "wikis");
        Assert.Null(PathGuard.SafeJoin(wikis, "kb", @"C:\Windows\evil.txt"));
    }

    // ---------- ValidKb ----------

    [Theory]
    [InlineData("我的读书库", true)]
    [InlineData("kb_01", true)]
    [InlineData("带空格 库", false)]
    [InlineData("a/b", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void ValidKb_FollowsDemoRule(string? name, bool expected)
    {
        Assert.Equal(expected, PathGuard.ValidKb(name));
    }
}
