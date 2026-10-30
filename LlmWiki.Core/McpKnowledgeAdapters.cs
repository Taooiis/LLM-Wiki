using System.ComponentModel;

using ModelContextProtocol.Server;

namespace LLM_Wiki.Services;

/// <summary>MCP 只读工具（设计 08§5.3）。返回 JSON 文本；错误以 CODE: 消息抛出。</summary>
[McpServerToolType]
public sealed class McpKnowledgeTools(McpKnowledgeService service)
{
    [McpServerTool(Name = "list_knowledge_bases", ReadOnly = true), Description("列出当前应用可见的知识库；不返回本机路径。")]
    public string ListKnowledgeBases() => service.ListKnowledgeBases();

    [McpServerTool(Name = "list_tree", ReadOnly = true), Description("列出指定知识库某个目录下的条目（名称、类型、相对路径、是否只读）。relative_dir 省略时列库根。")]
    public string ListTree(
        [Description("知识库标识，来自 list_knowledge_bases")] string kb,
        [Description("相对目录路径，例如 wiki/concepts；省略为库根")] string? relative_dir = null)
        => service.ListTree(kb, relative_dir);

    [McpServerTool(Name = "read_file", ReadOnly = true), Description("分段读取知识库内 md/txt/json 文本文件；返回内容、原字节版本、总字节、nextCursor 与 complete。")]
    public string ReadFile(
        [Description("知识库标识")] string kb,
        [Description("相对知识库根的文件路径，例如 wiki/index.md")] string relative_path,
        [Description("可选：首次读取返回的 version；续读时必须一致")] string? version = null,
        [Description("可选：上一段返回的 nextCursor")] string? cursor = null)
        => service.ReadFile(kb, relative_path, version, cursor);

    [McpServerTool(Name = "read_source_content", ReadOnly = true), Description("读取原格式资料（md/txt/docx/xlsx）的临时内容；不落盘、不生成 Markdown 副本，失败或截断会明确返回。")]
    public string ReadSourceContent(
        [Description("知识库标识")] string kb,
        [Description("raw/sources/ 下的相对路径，例如 raw/sources/需求.docx")] string relative_path,
        [Description("可选：首次读取返回的 sourceVersion；续读时必须一致")] string? version = null,
        [Description("可选：上一段返回的 nextCursor")] string? cursor = null)
        => service.ReadSourceContent(kb, relative_path, version, cursor);

    [McpServerTool(Name = "search_content", ReadOnly = true), Description("在 wiki/ 与 raw/sources/ 的文本文件中搜索关键词；返回文件相对路径与不超过 200 字符的片段。")]
    public string SearchContent(
        [Description("知识库标识")] string kb,
        [Description("搜索关键词")] string query,
        [Description("最多返回条数，默认 20，最大 50")] int limit = 20)
        => service.SearchContent(kb, query, limit);

    [McpServerTool(Name = "get_pending_changes", ReadOnly = true), Description("读取 .changes.json 的待处理变更列表；不会清除或修改待处理状态。")]
    public string GetPendingChanges(
        [Description("知识库标识")] string kb)
        => service.GetPendingChanges(kb);

    [McpServerTool(Name = "write_wiki_file"), Description("写入 wiki/ 下的 md/txt 文件。默认档位不可用。覆盖必须带 expected_version；新建必须 expectedAbsent=true，二者互斥。人工修订保护返回 MANUAL_REVIEW_REQUIRED。")]
    public string WriteWikiFile(
        [Description("知识库标识")] string kb,
        [Description("以 wiki/ 开头的相对路径，例如 wiki/synthesis/总结.md")] string relative_path,
        [Description("完整文件内容")] string content,
        [Description("覆盖已有文件时必填：读取返回的 version")] string? expected_version = null,
        [Description("新建文件时必填：显式声明目标不存在")] bool expected_absent = false)
        => service.WriteWikiFile(kb, relative_path, content, expected_version, expected_absent);
}

/// <summary>MCP Prompts（设计 08§5.2）：七张工作流卡共用 WorkflowPromptService。</summary>
[McpServerPromptType]
public sealed class McpKnowledgePrompts(McpKnowledgeService service)
{
    [McpServerPrompt(Name = "boundary"), Description("知识库接入说明：目标、结构规则、目录约定与文件清单。")]
    public string Boundary() => service.BuildBoundaryPrompt();

    [McpServerPrompt(Name = "batch_ingest"), Description("批量 Ingest 提示词；files 为空表示全部源文档。")]
    public string BatchIngest(
        [Description("可选：逗号分隔的源文档相对路径；空=全部")] string? files = null)
        => service.BuildBatchIngestPrompt(files);

    [McpServerPrompt(Name = "incremental_ingest"), Description("增量 Ingest 提示词：仅处理当前 .changes.json 中的待处理变更。")]
    public string IncrementalIngest() => service.BuildIncrementalIngestPrompt();

    [McpServerPrompt(Name = "query"), Description("基于知识库内容回答问题的提示词。")]
    public string Query([Description("要回答的问题")] string question)
        => service.BuildQueryPrompt(question);

    [McpServerPrompt(Name = "lint"), Description("知识库质量检查提示词：格式、链接、引用与结构。")]
    public string Lint() => service.BuildLintPrompt();

    [McpServerPrompt(Name = "compare"), Description("源文档对比提示词。")]
    public string Compare([Description("对比主题或文档")] string topic)
        => service.BuildComparePrompt(topic);

    [McpServerPrompt(Name = "clean"), Description("级联清理提示词：仅生成清理计划，不执行删除。")]
    public string Clean() => service.BuildCleanPrompt();
}

/// <summary>MCP Resources（设计 08§5.1）：逻辑 URI，不暴露本机绝对路径。</summary>
[McpServerResourceType]
public sealed class McpKnowledgeResources(McpKnowledgeService service)
{
    [McpServerResource(UriTemplate = "wiki://{kb}/purpose.md", Name = "purpose", MimeType = "text/markdown"), Description("知识库目的 purpose.md")]
    public string Purpose(string kb) => service.ReadResourceFile(kb, "purpose.md");

    [McpServerResource(UriTemplate = "wiki://{kb}/schema.md", Name = "schema", MimeType = "text/markdown"), Description("知识库结构约定 schema.md")]
    public string Schema(string kb) => service.ReadResourceFile(kb, "schema.md");

    [McpServerResource(UriTemplate = "wiki://{kb}/index", Name = "index", MimeType = "text/markdown"), Description("知识库目录索引")]
    public string Index(string kb) => service.BuildIndex(kb);

    [McpServerResource(UriTemplate = "wiki://{kb}/{+relative_path}", Name = "file", MimeType = "text/markdown"), Description("知识库内单个文件的逻辑地址")]
    public string File(string kb, string relative_path) => service.ReadResourceFile(kb, relative_path);
}
