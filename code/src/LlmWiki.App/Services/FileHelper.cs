namespace LlmWiki.App.Services;

/// <summary>文件扩展名工具：四类源格式 + 纯文本判定集中一处（spec C 约束统一口径）。</summary>
public static class FileHelper
{
    // Agent 可直接读的入库文本格式
    public static bool IsTextFile(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    // 可上传的源格式四类（spec 01 §3）
    public static bool IsSourceFile(string name) =>
        IsTextFile(name) ||
        name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
}
