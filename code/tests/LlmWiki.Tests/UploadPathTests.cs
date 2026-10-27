using System.IO;
using System.Text;
using LlmWiki.App.Services;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>
/// 桌面拖拽 / 原生文件对话框的上传入口：真实路径 → 直接读流，不经过 base64。
/// 覆盖：单文件、文件夹展开一层（过滤不支持格式）、路径不存在。
/// </summary>
public class UploadPathTests : IDisposable
{
    private readonly string _wikis = Path.Combine(Path.GetTempPath(), "llmwiki-path-" + Guid.NewGuid().ToString("N"));
    private readonly string _dropDir = Path.Combine(Path.GetTempPath(), "llmwiki-drop-" + Guid.NewGuid().ToString("N"));
    private readonly UploadService _svc;

    public UploadPathTests()
    {
        Directory.CreateDirectory(_wikis);
        Directory.CreateDirectory(_dropDir);
        new KbService(_wikis).CreateKb("测试库");
        _svc = new UploadService(_wikis);
    }

    public void Dispose()
    {
        try { Directory.Delete(_wikis, true); } catch { }
        try { Directory.Delete(_dropDir, true); } catch { }
    }

    [Fact]
    public void SinglePath_UploadsToSources()
    {
        var file = Path.Combine(_dropDir, "笔记.md");
        File.WriteAllText(file, "# 标题\n内容", Encoding.UTF8);

        var rows = _svc.UploadFromPaths("测试库", [file]);

        Assert.Single(rows);
        Assert.True(rows[0].Ok);
        Assert.Equal("raw/sources/笔记.md", rows[0].Rel);
        Assert.True(File.Exists(Path.Combine(_wikis, "测试库", "raw", "sources", "笔记.md")));
    }

    [Fact]
    public void DirectoryPath_TakesOneLevelAndSkipsUnsupported()
    {
        File.WriteAllText(Path.Combine(_dropDir, "a.md"), "a", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_dropDir, "b.txt"), "b", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_dropDir, "c.exe"), "x", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_dropDir, "nested"));
        File.WriteAllText(Path.Combine(_dropDir, "nested", "deep.md"), "d", Encoding.UTF8);

        var rows = _svc.UploadFromPaths("测试库", [_dropDir]);

        // 只取一层：a.md / b.txt 入库，c.exe 跳过，nested/deep.md 不递归
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Ok));
        Assert.DoesNotContain(rows, r => r.Name == "c.exe");
        Assert.DoesNotContain(rows, r => r.Name == "deep.md");
    }

    [Fact]
    public void MissingPath_ReportsErrorWithoutThrowing()
    {
        var rows = _svc.UploadFromPaths("测试库", [Path.Combine(_dropDir, "不存在.md")]);

        Assert.Single(rows);
        Assert.False(rows[0].Ok);
        Assert.Equal("文件不存在", rows[0].Error);
    }
}
