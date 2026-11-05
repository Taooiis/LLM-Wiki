using System.IO;

namespace LLM_Wiki.Services;

/// <summary>
/// 任务指令组合门面:所有入口（复制、本机启动、MCP Prompt）共用。
/// 模板按任务协议分派给 ITaskComposer 策略;本类只负责公共字段装配与协议路由。
/// 每次生成都自动携带库身份、规则入口与版本、源文件清单、范围、边界和结果报告要求,
/// 不依赖外部 AI 记住上一次对话。
/// 新增任务类型 = 新增一个 ITaskComposer 组合器 + 在构造函数的组合器数组里加一行。
/// </summary>
public sealed class PromptComposer
{
    public const string TemplateVersion = "v2-2026-10-30";

    private readonly ConfigService _config;
    private readonly SourceService _sources;
    private readonly ChangeService _changes;
    private readonly ReviewService _reviews;
    // 协议 → 组合器;未注册协议回退到检查模板(与旧 switch default 行为一致)
    private readonly IReadOnlyDictionary<string, ITaskComposer> _composers;
    private readonly ITaskComposer _fallback;

    public PromptComposer(ConfigService config, SourceService sources, ChangeService changes, ReviewService reviews)
    {
        _config = config;
        _sources = sources;
        _changes = changes;
        _reviews = reviews;
        var composers = new ITaskComposer[]
        {
            new UpdateTaskComposer(config, sources, changes, reviews),
            new AskTaskComposer(config, sources, changes, reviews),
            new CheckTaskComposer(config, sources, changes, reviews),
        };
        _composers = composers
            .SelectMany(c => c.Protocols.Select(p => (Protocol: p, Composer: c)))
            .ToDictionary(x => x.Protocol, x => x.Composer, StringComparer.Ordinal);
        _fallback = composers[^1];
    }

    /// <summary>按任务已设好的 Protocol 渲染指令;持久任务路径(TaskService)使用。</summary>
    public string ComposeFor(OrganizeTask task, string transport)
        => ComposerFor(task.Protocol).Compose(task, transport);

    /// <summary>兼容入口:渲染更新知识指令(按 task.Protocol 路由)。</summary>
    public string ComposeUpdate(OrganizeTask task, string transport) => ComposeFor(task, transport);

    /// <summary>兼容入口:渲染提问指令。</summary>
    public string ComposeAsk(OrganizeTask task, string transport) => ComposeFor(task, transport);

    /// <summary>兼容入口:渲染检查指令。</summary>
    public string ComposeCheck(OrganizeTask task, string transport) => ComposeFor(task, transport);

    /// <summary>批量任务可只处理指定资料；空清单回退到全部资料。</summary>
    public string ComposeBatch(string kb, IReadOnlyList<string> relPaths, string transport)
    {
        if (relPaths.Count == 0) return ComposeProtocol(kb, TaskProtocol.BatchIngest, "", transport);
        var status = _reviews.GetRuleStatus(kb);
        var task = NewMcpTask(kb, TaskProtocol.BatchIngest, "", transport);
        task.Kind = TaskKind.Update;
        task.Scope = TaskScope.Selected;
        task.Items = [.. relPaths.Select(rel =>
        {
            var normalized = ChangeService.Normalize(rel);
            var change = _changes.Get(kb, normalized);
            return new TaskItem
            {
                RelPath = normalized,
                Action = change?.Action ?? "所选资料",
                SourceHash = _sources.Hash(kb, normalized),
                ChangeHash = change?.Hash ?? "",
            };
        })];
        return ComposeFor(task, transport);
    }

    /// <summary>MCP Prompt 等无持久任务入口使用:公共字段由门面装配,差异字段由组合器装配。</summary>
    public string ComposeProtocol(string kb, string protocol, string input, string transport)
    {
        var composer = ComposerFor(protocol);
        var task = NewMcpTask(kb, protocol, input, transport);
        composer.Fill(task);
        return composer.Compose(task, transport);
    }

    /// <summary>高级入口单独查看/复制库说明；内容固定长度（不含文件清单），可放进外部 AI 的自定义指令。</summary>
    public string ComposeBoundary(string kb, string? localPath, string transport = "file")
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 知识库说明：" + kb);
        sb.AppendLine();
        sb.AppendLine("## 身份与访问");
        sb.AppendLine("- 访问方式：" + (transport == "mcp" ? "MCP 连接（逻辑资源）" : "文件直接访问"));
        if (transport != "mcp" && !string.IsNullOrWhiteSpace(localPath))
            sb.AppendLine("- 库根目录：" + localPath);
        else
            sb.AppendLine("- 逻辑资源：wiki://" + kb + "/purpose.md、wiki://" + kb + "/schema.md、wiki://" + kb + "/index");
        sb.AppendLine();
        sb.AppendLine("## 目录约定");
        sb.AppendLine("- raw/sources/：原始资料，保持源格式（md/txt/docx/xlsx），只读，不转 Markdown。");
        sb.AppendLine("- wiki/：知识页；entities 实体、concepts 概念、sources 资料摘要、queries 问题分析、synthesis 综合分析、comparisons 对比。");
        sb.AppendLine("- wiki/index.md：全部知识页的链接与摘要总目录，找页面先读它。");
        sb.AppendLine("- purpose.md 与 schema.md：目标与组织规则，执行任务前必须读取。");
        sb.AppendLine("- .changes.json、.llmwiki/：应用内部状态，不读取、不修改、不声称可用于完成任务。");
        sb.AppendLine();
        sb.AppendLine("## 边界");
        sb.AppendLine("- 只读写本库；不跨库、不使用旧路径写入。");
        sb.AppendLine("- 不把 docx/xlsx 转成 Markdown 落盘；读取失败或内容截断必须报告。");
        sb.AppendLine("- 人工修订过的知识页需要保留人工内容；覆盖前列出拟修改项。");
        sb.AppendLine("- 不自行清除待整理记录；任务是否完成由用户核对后确认。");
        return sb.ToString();
    }

    private ITaskComposer ComposerFor(string protocol)
        => _composers.TryGetValue(protocol, out var composer) ? composer : _fallback;

    private OrganizeTask NewMcpTask(string kb, string protocol, string input, string transport)
    {
        var status = _reviews.GetRuleStatus(kb);
        return new OrganizeTask
        {
            Id = "mcp-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"),
            Kb = kb,
            Protocol = protocol,
            Transport = transport,
            Input = input ?? "",
            TemplateVersion = TemplateVersion,
            RuleVersion = status.CurrentVersion,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
    }
}
