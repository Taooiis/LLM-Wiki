namespace LLM_Wiki.Services;

using System.Text;

/// <summary>文件扩展名与文本解码工具：四类源格式 + 纯文本判定集中一处（spec C 约束统一口径）。</summary>
public static class FileHelper
{
    static FileHelper()
    {
        // GB18030 兜底解码/编码依赖代码页提供程序；net10 共享框架自带该程序集。
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { }
    }

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
    /// txt 解码：BOM → 严格 UTF-8 → GB18030 兜底（spec 02 §6.1）。
    /// 已知极限：GBK 高位双字节可能"恰好"是合法 UTF-8，此场景按 UTF-8 处理（与 Demo 行为一致）。
    /// </summary>
    public static string DecodeText(byte[] bytes) => DecodeTextWithInfo(bytes).Content;

    /// <summary>
    /// 带格式信息的解码：同时给出检测到的编码/BOM 与换行风格，供保存时原样写回（02§5）。
    /// 编码无法判定时回退无 BOM UTF-8。
    /// </summary>
    public static DecodedText DecodeTextWithInfo(byte[] bytes)
    {
        TextFileEncoding encoding;
        string content;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = TextFileEncoding.Utf8Bom;
            content = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = TextFileEncoding.Utf16Le;
            content = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = TextFileEncoding.Utf16Be;
            content = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else
        {
            try
            {
                content = new UTF8Encoding(false, true).GetString(bytes);
                encoding = TextFileEncoding.Utf8NoBom;
            }
            catch (DecoderFallbackException)
            {
                encoding = TextFileEncoding.Gb18030;
                content = Encoding.GetEncoding("GB18030").GetString(bytes);
            }
        }
        return new DecodedText(content, encoding, DetectNewLine(content));
    }

    /// <summary>按检测到的编码编码文本；调用方需先用 ApplyNewLine 归一换行。</summary>
    public static byte[] EncodeText(string content, TextFileEncoding encoding) => encoding switch
    {
        TextFileEncoding.Utf8Bom => new UTF8Encoding(true).GetBytes(content),
        TextFileEncoding.Utf16Le => Encoding.Unicode.GetBytes(content),
        TextFileEncoding.Utf16Be => Encoding.BigEndianUnicode.GetBytes(content),
        TextFileEncoding.Gb18030 => Encoding.GetEncoding("GB18030").GetBytes(content),
        _ => new UTF8Encoding(false).GetBytes(content),
    };

    /// <summary>
    /// 检测主导换行风格：CRLF 与孤立 LF 计数，CRLF 占优返回 "\r\n"，仅 LF 返回 "\n"，
    /// 无换行（无法判定）返回空串，保存时保持原样不归一。
    /// </summary>
    public static string DetectNewLine(string content)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            if (i > 0 && content[i - 1] == '\r') crlf++;
            else lf++;
        }
        if (crlf == 0 && lf == 0) return "";
        return crlf >= lf ? "\r\n" : "\n";
    }

    /// <summary>把文本换行归一为检测到的风格；newLine 为空表示无法判定，保持原样。</summary>
    public static string ApplyNewLine(string content, string newLine)
    {
        if (string.IsNullOrEmpty(newLine) || !content.Contains('\n')) return content;
        if (newLine != "\r\n")
            return content.Contains("\r\n") ? content.Replace("\r\n", "\n") : content;

        var builder = new StringBuilder(content.Length + 16);
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (ch == '\n' && (i == 0 || content[i - 1] != '\r')) builder.Append("\r\n");
            else builder.Append(ch);
        }
        return builder.ToString();
    }
}

/// <summary>文本文件编码/BOM 口径：保存时按读取检测到的值原样写回（02§5）。</summary>
public enum TextFileEncoding
{
    Utf8NoBom,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
    Gb18030,
}

/// <summary>解码结果：正文 + 检测到的编码 + 主导换行（空串表示无换行、无法判定）。</summary>
public readonly record struct DecodedText(string Content, TextFileEncoding Encoding, string NewLine);
