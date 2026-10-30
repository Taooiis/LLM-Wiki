using System.Security.Cryptography;
using System.Text;

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
    private static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLM_Wiki", "mcp.token");

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
        var kbs = new KbService(config);
        var prompts = new WorkflowPromptService(files);
        var policy = new McpAccessPolicy(config);
        var context = new McpServerContext { CurrentKb = kb };

        if (!kbs.ListKbs().Contains(kb, StringComparer.Ordinal))
        {
            Log($"知识库不存在：{kb}");
            return 3;
        }

        if (allowWrite && !AuthorizeWrite(tokenFile))
        {
            Log("写入档需要启动参数 --allow-write 且提供有效写令牌（环境变量 LLM_WIKI_MCP_TOKEN 或 --token-file）");
            return 4;
        }

        policy.Mode = allowWrite ? McpMode.ReadWrite : McpMode.ReadOnly;

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
        builder.Services.AddSingleton(prompts);
        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton<McpKnowledgeService>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<McpKnowledgeTools>()
            .WithPrompts<McpKnowledgePrompts>()
            .WithResources<McpKnowledgeResources>();

        Log(allowWrite ? "已启动（写入档）" : "已启动（只读）");
        await builder.Build().RunAsync();
        return 0;
    }

    /// <summary>写令牌必须与桌面端保存的令牌一致；只接受环境变量或本地密钥文件。</summary>
    private static bool AuthorizeWrite(string? tokenFile)
    {
        var provided = Environment.GetEnvironmentVariable("LLM_WIKI_MCP_TOKEN")?.Trim();
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            try { provided = File.ReadAllText(tokenFile).Trim(); }
            catch (Exception e)
            {
                Log("读取令牌文件失败：" + e.Message);
                return false;
            }
        }

        if (string.IsNullOrEmpty(provided)) return false;

        try
        {
            var stored = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
            var expected = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(stored));
            var actual = Encoding.UTF8.GetBytes(provided);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static void Log(string message) => Console.Error.WriteLine($"[llm-wiki-stdio] {message}");
}
