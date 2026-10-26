using LlmWiki.App.Services;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>库生命周期：建库骨架/场景模板/删库校验（spec 02 §5、D7；骨架默认文案对照 server.js:49-69）。</summary>
public class KbServiceTests : IDisposable
{
    private readonly string _wikis = Path.Combine(Path.GetTempPath(), "llmwiki-kb-" + Guid.NewGuid().ToString("N"));
    private readonly KbService _svc;

    public KbServiceTests()
    {
        Directory.CreateDirectory(_wikis);
        _svc = new KbService(_wikis);
    }

    public void Dispose() { try { Directory.Delete(_wikis, true); } catch { } }

    [Fact]
    public void CreateKb_MakesSkeletonDirsFilesAndChangesFile()
    {
        var root = _svc.CreateKb("测试库");
        Assert.True(Directory.Exists(root));
        foreach (var d in new[] { "raw/sources", "raw/assets", "raw/original",
                 "wiki/entities", "wiki/concepts", "wiki/sources", "wiki/queries", "wiki/synthesis", "wiki/comparisons" })
            Assert.True(Directory.Exists(Path.Combine(root, d)), d);
        foreach (var f in new[] { "purpose.md", "schema.md", "wiki/index.md", "wiki/log.md", "wiki/overview.md" })
            Assert.True(File.Exists(Path.Combine(root, f)), f);

        // .changes.json 初始化为空数组（Demo 点号文件，目录树跳过）
        Assert.Equal("[]", File.ReadAllText(Path.Combine(root, ".changes.json")));
        // 默认文案带库名与当天日期
        Assert.Contains("测试库", File.ReadAllText(Path.Combine(root, "purpose.md")));
        Assert.Contains(DateTime.Today.ToString("yyyy-MM-dd"), File.ReadAllText(Path.Combine(root, "wiki/log.md")));
    }

    [Fact]
    public void CreateKb_InvalidName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _svc.CreateKb("带空格 库"));
        Assert.Throws<ArgumentException>(() => _svc.CreateKb("a/b"));
    }

    [Fact]
    public void CreateKb_SceneResearch_PurposePrefilled()
    {
        var purpose = File.ReadAllText(Path.Combine(_svc.CreateKb("研究库"), "purpose.md"));
        // 首个标题行体现场景
        Assert.StartsWith("# 研究库", purpose);
        Assert.Contains("研究", purpose);
    }

    [Fact]
    public void Schema_DefaultCopy_IncludesOfficeCopyAndWikilinkRules()
    {
        var schema = File.ReadAllText(Path.Combine(_svc.CreateKb("基础库"), "schema.md"));
        // spec 04 §2 增量：office 副本 + 原件目录说明
        Assert.Contains("raw/original", schema);
        Assert.Contains("自动转换", schema);
        // spec 02 §8 增量：wikilink / frontmatter sources 规则
        Assert.Contains("[[", schema);
        Assert.Contains("sources:", schema);
    }

    [Fact]
    public void DeleteKb_RemovesWholeDirectory()
    {
        _svc.CreateKb("将删库");
        _svc.DeleteKb("将删库");
        Assert.False(Directory.Exists(Path.Combine(_wikis, "将删库")));
    }

    [Fact]
    public void ValidateDelete_RejectsInvalidNameAndMissingAndUnreachable()
    {
        Assert.False(_svc.ValidateDelete("不存在库").ok);
        _svc.CreateKb("可用库");
        Assert.True(_svc.ValidateDelete("可用库").ok);

        // UNC 不可达：存储路径指向不存在驱动器时必须拒绝删库（spec Review Focus 1）
        var bad = new KbService("Q:\\no-such-drive\\wikis");
        var r = bad.ValidateDelete("孤库");
        Assert.False(r.ok);
        Assert.False(string.IsNullOrEmpty(r.error));
    }

    [Fact]
    public void LogWriter_AppendsSource_AutoAddsReingestNote()
    {
        _svc.CreateKb("日志库");
        LogWriter.AppendSource(_wikis, "日志库", "raw/sources/a.md", "源文档上传");
        var log = File.ReadAllText(Path.Combine(_wikis, "日志库", "wiki", "log.md"));
        Assert.Contains("源文档上传：raw/sources/a.md（需重新 Ingest）", log);

        LogWriter.AppendSource(_wikis, "日志库", "purpose.md", "配置更新", "");
        log = File.ReadAllText(Path.Combine(_wikis, "日志库", "wiki", "log.md"));
        // note 为空串则不加提示语（配置更新不需要重跑 Ingest）
        Assert.Contains("配置更新：purpose.md", log);
        Assert.DoesNotContain("配置更新：purpose.md（需重新 Ingest）", log);
    }

    [Fact]
    public void ListKbs_ReturnsCreatedSorted()
    {
        _svc.CreateKb("乙库");
        _svc.CreateKb("甲库");
        Assert.Equal(new[] { "乙库", "甲库" }.OrderBy(x => x, StringComparer.Ordinal), _svc.ListKbs());
    }
}
