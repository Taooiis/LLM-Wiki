namespace LLM_Wiki.Services;

/// <summary>旧版批量输入解析工具：MCP Prompt 的文件参数仍兼容逗号/换行与 raw/sources/ 前缀。</summary>
public static class WorkflowPromptService
{
    public static List<string> ParseFiles(string input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : input
                .Split([',', '，', (char)10, (char)13], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeSourceName)
                .Where(f => f.Length > 0)
                .ToList();

    public static string NormalizeSourceName(string f)
    {
        var n = f.Trim().Replace((char)92, '/');
        const string prefix = "raw/sources/";
        return n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? n[prefix.Length..] : n;
    }
}
