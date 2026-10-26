using LlmWiki.App.Services;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>文件桥：目录树/读写/删除/源列表/变更清单（对照 server.js:82-124,180-248；spec 02 §4）。</summary>
public class FileBridgeTests : IDisposable
{
    private readonly string _wikis = Path.Combine(Path.GetTempPath(), "llmwiki-fb-" + Guid.NewGuid().ToString("N"));
    private readonly FileBridge _fb;

    public FileBridgeTests()
    {
        Directory.CreateDirectory(_wikis);
        var kb = new KbService(_wikis).CreateKb("测试库");
        _fb = new FileBridge(_wikis);
    }

    public void Dispose() { try { Directory.Delete(_wikis, true); } catch { } }

    // ---------- 树 ----------

    [Fact]
    public void Tree_SortsDirsFirstAndSkipsDotFiles()
    {
        File.WriteAllText(Path.Combine(_wikis, "测试库", "raw", "sources", "b.md"), "二");
        File.WriteAllText(Path.Combine(_wikis, "测试库", "raw", "sources", "a.md"), "一");
        var tree = _fb.Tree("测试库");
        var raw = Assert.Single(tree, n => n.Name == "raw").Children!;
        // 目录在前，目录按名排序（assets/original/sources）
        Assert.Equal(["assets", "original", "sources"], raw.Where(t => t.Type == "dir").Select(t => t.Name));
        var sources = Assert.Single(raw, n => n.Name == "sources").Children!;
        Assert.Equal(["a.md", "b.md"], sources.Select(t => t.Name));
        // 点号文件不进树
        Assert.DoesNotContain(tree.SelectMany(t => Flatten(t)), t => t.Name.StartsWith('.'));
    }

    [Fact]
    public void Tree_IncludesOriginalOfficeFiles()
    {
        File.WriteAllBytes(Path.Combine(_wikis, "测试库", "raw", "original", "报告.docx"), [1, 2, 3]);
        var tree = _fb.Tree("测试库");
        var original = Assert.Single(Assert.Single(tree, t => t.Name == "raw").Children!, t => t.Name == "original").Children!;
        Assert.Contains(original, t => t.Name == "报告.docx" && t.Type == "file");
    }

    // ---------- 读写 ----------

    [Fact]
    public void WriteRead_RoundTripsMdFile_AndLogsSourceChange()
    {
        var rel = @"raw\sources\心得.md";
        Assert.True(_fb.WriteText("测试库", rel, "# 心得", out var err));
        Assert.Equal("", err);
        Assert.Equal("# 心得", _fb.ReadText("测试库", rel, out _));

        var log = File.ReadAllText(Path.Combine(_wikis, "测试库", "wiki", "log.md"));
        Assert.Contains("源文档编辑保存：" + rel.Replace('\\', '/'), log);
        // 变更清单带 64 位十六进制 sha256 指纹（spec 02 §7）
        var changes = _fb.Changes("测试库");
        var c = Assert.Single(changes);
        Assert.Equal(@"raw\sources\心得.md".Replace('\\', '/'), c.File.Replace('\\', '/'));
        Assert.Equal(64, c.Hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", c.Hash);
    }

    [Fact]
    public void Write_PurposeOrSchema_UsesConfigUpdateLog()
    {
        _fb.WriteText("测试库", "purpose.md", "# 新目标", out _);
        var log = File.ReadAllText(Path.Combine(_wikis, "测试库", "wiki", "log.md"));
        Assert.Contains("配置更新：purpose.md", log);
        Assert.Empty(_fb.Changes("测试库")); // 配置不进待处理变更
    }

    [Fact]
    public void WriteText_RejectsEscapeAndForeignExtension()
    {
        Assert.False(_fb.WriteText("测试库", @"..\evil.md", "x", out var e1));
        Assert.False(string.IsNullOrEmpty(e1));
        Assert.False(_fb.WriteText("测试库", @"wiki\Entities\bad.docx", "x", out var e2));
        Assert.False(string.IsNullOrEmpty(e2));
    }

    // ---------- 删除 ----------

    [Fact]
    public void Delete_TextFile_LogsAndRecords()
    {
        _fb.WriteText("测试库", @"raw\sources\旧.md", "内容", out _);
        Assert.True(_fb.Delete("测试库", @"raw\sources\旧.md", out _));
        Assert.False(File.Exists(Path.Combine(_wikis, "测试库", "raw", "sources", "旧.md")));
        Assert.Equal("删除", _fb.Changes("测试库").Single(c => c.Action == "删除").Action);

        // office 原件不允许走这里（成对删除归上传面，spec 02 §4）
        Assert.False(_fb.Delete("测试库", @"raw\original\报告.docx", out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    // ---------- 变更清单 ----------

    [Fact]
    public void ClearChanges_RemovesSelectedOrAll()
    {
        _fb.WriteText("测试库", @"raw\sources\a.md", "1", out _);
        _fb.WriteText("测试库", @"raw\sources\b.md", "2", out _);
        Assert.Equal(2, _fb.Changes("测试库").Count);

        _fb.ClearChanges("测试库", ["raw/sources/a.md"]);
        Assert.Single(_fb.Changes("测试库"), c => c.File == "raw/sources/b.md");

        _fb.ClearChanges("测试库", []);
        Assert.Empty(_fb.Changes("测试库"));
    }

    // ---------- 工具 ----------

    [Fact]
    public void HashUtil_StableSha256()
    {
        Assert.Equal(HashUtil.Sha256Text("abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Assert.Equal(64, HashUtil.Sha256Text("").Length);
    }

    private static IEnumerable<TreeItem> Flatten(TreeItem t)
    {
        yield return t;
        foreach (var c in t.Children ?? []) foreach (var f in Flatten(c)) yield return f;
    }
}
