using System.IO;
using LlmWiki.App.Services;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>六类提示词 + 边界说明（spec 02 §7 工作流页、§8 Clean 卡；模板原文对照 Demo prompts/*.md）。</summary>
public class PromptEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmwiki-pe-" + Guid.NewGuid().ToString("N"));
    private readonly string _wikis;
    private readonly ConfigService _cfg;
    private readonly FileBridge _fb;
    private readonly PromptEngine _pe;

    public PromptEngineTests()
    {
        _wikis = Path.Combine(_root, "wikis");
        Directory.CreateDirectory(_wikis);
        _cfg = new ConfigService(_root);
        _cfg.SetWikisDir(_wikis, out _);
        new KbService(_wikis).CreateKb("测试库");
        _fb = new FileBridge(_wikis);
        _pe = new PromptEngine(_cfg, _fb);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ---------- Ingest ----------

    [Fact]
    public void Ingest_SingleFile_ListsThatFileOnly()
    {
        var p = _pe.Ingest("测试库", ["a.md"]);
        Assert.Contains("wikis/测试库/raw/sources/a.md", p);
        Assert.DoesNotContain("b.md", p);
        // 头部含绝对根路径与读约定
        Assert.Contains(_wikis + "\\测试库", p);
        Assert.Contains("AGENTS.md", p);
        // office 副本与原文件对应说明（spec 04 §2 增量 3）
        Assert.Contains(".docx.md", p);
    }

    [Fact]
    public void Ingest_MultipleFiles_NumbersEach()
    {
        var p = _pe.Ingest("测试库", ["a.md", "b.md"]);
        Assert.Contains("1. 读取", p);
        Assert.Contains("2. 读取", p);
        Assert.Contains("a.md", p);
        Assert.Contains("b.md", p);
    }

    [Fact]
    public void Ingest_NullFiles_ListsAllSources()
    {
        _fb.WriteText("测试库", "raw/sources/one.md", "x", out _);
        _fb.WriteText("测试库", "raw/sources/two.txt", "y", out _);
        var p = _pe.Ingest("测试库", null);
        Assert.Contains("one.md", p);
        Assert.Contains("two.txt", p);
        Assert.Contains("2 篇文档", p);
    }

    [Fact]
    public void Ingest_EmptyArray_ListsAllSources()
    {
        _fb.WriteText("测试库", "raw/sources/only.md", "x", out _);
        var p = _pe.Ingest("测试库", []);
        Assert.Contains("only.md", p);
    }

    // ---------- 增量 Ingest ----------

    [Fact]
    public void IngestChanges_ContainsHashSkipSentenceAndSelectedFilesOnly()
    {
        _fb.WriteText("测试库", "raw/sources/keep.md", "x", out _);
        _fb.WriteText("测试库", "raw/sources/drop.md", "y", out _);
        var changes = _fb.Changes("测试库");
        var selected = new[] { changes.Single(c => c.File.EndsWith("keep.md")) };

        var p = _pe.IngestChanges("测试库", selected);
        Assert.Contains("keep.md", p);
        Assert.DoesNotContain("drop.md", p);
        // hash 跳过句（spec 02 §7 SHA256 指纹）
        Assert.Contains("hash", p);
        Assert.Contains("跳过", p);
        // 只处理以下文件的限定句
        Assert.Contains("只处理以下文件", p);
        // 列出真实 hash 值
        Assert.Contains(selected[0].Hash, p);
    }

    [Fact]
    public void IngestChanges_MixedDeleted_NotesDeletedFilesSeparately()
    {
        _fb.WriteText("测试库", "raw/sources/gone.md", "x", out _);
        _fb.Delete("测试库", "raw/sources/gone.md", out _);
        var changes = _fb.Changes("测试库");
        var p = _pe.IngestChanges("测试库", changes.ToArray());
        Assert.Contains("gone.md", p);
        Assert.Contains("删除", p);
    }

    // ---------- Boundary ----------

    [Fact]
    public void Boundary_ContainsAbsolutePathAndPurposeSchemaSummary()
    {
        var p = _pe.Boundary("测试库");
        // 绝对路径
        Assert.Contains(_wikis + "\\测试库", p);
        // purpose / schema 摘要（内容被截断进提示词）
        Assert.Contains("purpose.md", p);
        Assert.Contains("schema.md", p);
        Assert.Contains("测试库 - 研究目标", p);   // purpose 首行内容
        Assert.Contains("结构规则", p);             // schema 首行内容
        // 五段结构标记
        Assert.Contains("## 先读这三个文件", p);
        Assert.Contains("## 目录约定", p);
        Assert.Contains("## 回答要求", p);
        // 增量：第 3 段 office 说明 + 第 5 段 clean 语义（spec 04 §2）
        Assert.Contains("raw/original/", p);
        Assert.Contains("Clean", p);
    }

    // ---------- Query / Lint / Compare ----------

    [Fact]
    public void Query_ContainsQuestionAndPath()
    {
        var p = _pe.Query("测试库", "RAG和LLM Wiki的区别");
        Assert.Contains("RAG和LLM Wiki的区别", p);
        Assert.Contains(_wikis + "\\测试库", p);
        Assert.Contains("Query 工作流", p);
    }

    [Fact]
    public void Lint_ContainsHealthCheckInstruction()
    {
        var p = _pe.Lint("测试库");
        Assert.Contains("Lint 工作流", p);
        Assert.Contains(_wikis + "\\测试库", p);
    }

    [Fact]
    public void Compare_ContainsTopicAndComparisonsDir()
    {
        var p = _pe.Compare("测试库", "Cursor vs Copilot");
        Assert.Contains("Cursor vs Copilot", p);
        Assert.Contains("comparisons", p);
    }

    // ---------- Clean ----------

    [Fact]
    public void Clean_ExpandsAbsRootAndKb_AndHasCascadeRules()
    {
        var p = _pe.Clean("测试库", "raw/sources/报告.docx.md");
        // 真实路径已展开（无模板占位符残留）
        Assert.Contains(_wikis + "\\测试库", p);
        Assert.DoesNotContain("{{abs_root}}", p);
        Assert.DoesNotContain("{{kb}}", p);
        // 文件名与级联清理规则
        Assert.Contains("报告.docx.md", p);
        Assert.Contains("wiki/sources/", p);
        Assert.Contains("wiki/index.md", p);
        Assert.Contains("[[", p);
        Assert.Contains("frontmatter", p);
        Assert.Contains("wiki/log.md", p);
    }

    [Fact]
    public void AllPrompts_ContainAbsoluteRootPath_NoPlaceholdersLeft()
    {
        var prompts = new[]
        {
            _pe.Ingest("测试库", ["a.md"]),
            _pe.IngestChanges("测试库", []),
            _pe.Query("测试库", "q"),
            _pe.Lint("测试库"),
            _pe.Compare("测试库", "t"),
            _pe.Clean("测试库", "f.md"),
            _pe.Boundary("测试库"),
        };
        foreach (var p in prompts)
        {
            Assert.DoesNotContain("{{abs_root}}", p);
            Assert.DoesNotContain("{{kb}}", p);
            Assert.DoesNotContain("{{file}}", p);
            Assert.DoesNotContain("{{question}}", p);
            Assert.DoesNotContain("{{topic}}", p);
        }
    }

    // ---------- 模板文件与引擎文案不漂移 ----------

    [Fact]
    public void PromptTemplatesFile_MatchesEnginePhrases()
    {
        // wwwroot 随构建复制到输出目录；测试直接读输出副本核对
        var file = Path.Combine(AppContext.BaseDirectory, "wwwroot", "prompt-templates.js");
        Assert.True(File.Exists(file), "prompt-templates.js 未随构建复制到输出目录：" + file);
        var js = File.ReadAllText(file);

        // 两份文案必须都含这些特征句，否则说明模板漂移了
        foreach (var phrase in new[]
        {
            "请按 Ingest 工作流批量处理",
            "请按 Ingest 工作流只处理以下文件",
            "请按 Query 工作流回答",
            "请按 Lint 工作流做健康检查",
            "请创建对比页比较",
            "级联清理规则",
            "raw/original/",
            "hash",
        })
        {
            Assert.Contains(phrase, js);
        }
    }
}
