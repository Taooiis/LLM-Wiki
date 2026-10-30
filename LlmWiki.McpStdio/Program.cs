using LLM_Wiki.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmWiki.McpStdio;

/// <summary>
/// stdio MCP 宿主（设计 08§7.2）：由 MCP 客户端拉起，复用 Core 能力。
/// stdout 只承载 JSON-RPC 协议，所有诊断信息走 stderr。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? kb = null;
        string? tokenFile = null;
        var allowWrite = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--kb":
                    kb = i + 1 < args.Length ? args[++i] : null;
                    break;
                case "--allow-write":
                    allowWrite = true;
                    break;
                case "--token-file":
                    tokenFile = i + 1 < args.Length ? args[++i] : null;
                    break;
                default:
                    Log($"未知参数：{args[i]}");
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(kb))
        {
            Log("缺少 --kb <知识库标识>");
            return 2;
        }

        var config = new ConfigService(AppContext.BaseDirectory);
        var files = new FileBridge(config);
        var changes = new ChangeService(config);
        var operations = new OperationStore(config);
        var trash = new TrashService(config, changes, operations);
        var reviews = new ReviewService(config);
        var sources = new SourceService(config, changes, trash, operations);
        var kbs = new KbService(config, reviews);
        var composer = new PromptComposer(config, sources, changes, reviews);
        var policy = new McpAccessPolicy(config) { AuthorizedKb = kb };
        var context = new McpServerContext { CurrentKb = kb };

        if (!kbs.ListKbs().Contains(kb, StringComparer.Ordinal))
        {
            Log($"知识库不存在：{kb}");
            return 3;
        }

        if (allowWrite)
        {
            // V2 首轮 stdio 固定只读；保留参数解析只用于给出明确迁移提示，不静默提升权限。
            Log("stdio 首轮只读：已忽略 --allow-write。需要写入请使用 HTTP MCP 的写入档并配置独立写令牌。");
        }
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            Log("stdio 首轮只读，不需要 --token-file；已忽略。");
        }

        policy.Mode = McpMode.ReadOnly;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(files);
        builder.Services.AddSingleton(kbs);
        builder.Services.AddSingleton(composer);
        builder.Services.AddSingleton(reviews);
        builder.Services.AddSingleton(sources);
        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton<McpKnowledgeService>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<McpKnowledgeTools>()
            .WithPrompts<McpKnowledgePrompts>()
            .WithResources<McpKnowledgeResources>();

        Log("已启动（只读）");
        await builder.Build().RunAsync();
        return 0;
    }

    private static void Log(string message) => Console.Error.WriteLine($"[llm-wiki-stdio] {message}");
}
