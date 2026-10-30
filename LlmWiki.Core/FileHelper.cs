namespace LLM_Wiki.Services;

using System.Text;

/// <summary>文件扩展名与文本解码工具：四类源格式 + 纯文本判定集中一处（spec C 约束统一口径）。</summary>
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

    /// <summary>
    /// txt 解码：BOM → 严格 UTF-8 → GB18030 兜底（spec 02 §6.1；调用方需先注册 CodePagesEncodingProvider）。
    /// 已知极限：GBK 高位双字节可能"恰好"是合法 UTF-8，此场景按 UTF-8 处理（与 Demo 行为一致）。
    /// </summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding("GB18030").GetString(bytes); }
    }
}
