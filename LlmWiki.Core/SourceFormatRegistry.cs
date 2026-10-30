using System.IO;

namespace LLM_Wiki.Services;

public enum SourceCategory
{
    Text,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Data,
    Image,
}

public sealed record SourceFormat(string Extension, SourceCategory Category, bool EditAsText, bool InAppPreview, string Label);

/// <summary>
/// 资料格式注册表：上传白名单、筛选分组、文本编辑/预览能力、AI 读取范围统一从这里取。
/// 支持添加不等于支持页内预览、原位编辑或 AI 读取，三者分别判断。
/// </summary>
public static class SourceFormatRegistry
{
    private static readonly SourceFormat[] Formats =
    [
        new(".md", SourceCategory.Text, true, true, "Markdown"),
        new(".markdown", SourceCategory.Text, true, true, "Markdown"),
        new(".txt", SourceCategory.Text, true, true, "文本"),
        new(".log", SourceCategory.Text, true, true, "文本"),

        new(".docx", SourceCategory.Document, false, true, "Word"),
        new(".docm", SourceCategory.Document, false, true, "Word"),
        new(".doc", SourceCategory.Document, false, false, "Word（旧版）"),
        new(".rtf", SourceCategory.Document, false, false, "RTF"),
        new(".odt", SourceCategory.Document, false, false, "ODT"),
        new(".wps", SourceCategory.Document, false, false, "WPS"),

        new(".xlsx", SourceCategory.Spreadsheet, false, true, "Excel"),
        new(".xlsm", SourceCategory.Spreadsheet, false, true, "Excel"),
        new(".xls", SourceCategory.Spreadsheet, false, false, "Excel（旧版）"),
        new(".csv", SourceCategory.Spreadsheet, true, true, "CSV"),
        new(".tsv", SourceCategory.Spreadsheet, true, true, "TSV"),
        new(".ods", SourceCategory.Spreadsheet, false, false, "ODS"),
        new(".et", SourceCategory.Spreadsheet, false, false, "WPS 表格"),

        new(".pptx", SourceCategory.Presentation, false, true, "PowerPoint"),
        new(".pptm", SourceCategory.Presentation, false, true, "PowerPoint"),
        new(".ppt", SourceCategory.Presentation, false, false, "PowerPoint（旧版）"),
        new(".odp", SourceCategory.Presentation, false, false, "ODP"),
        new(".dps", SourceCategory.Presentation, false, false, "WPS 演示"),

        new(".pdf", SourceCategory.Pdf, false, false, "PDF"),

        new(".json", SourceCategory.Data, true, true, "JSON"),
        new(".jsonl", SourceCategory.Data, true, true, "JSONL"),
        new(".xml", SourceCategory.Data, true, true, "XML"),
        new(".yaml", SourceCategory.Data, true, true, "YAML"),
        new(".yml", SourceCategory.Data, true, true, "YAML"),
        new(".toml", SourceCategory.Data, true, true, "TOML"),
        new(".ini", SourceCategory.Data, true, true, "INI"),

        new(".png", SourceCategory.Image, false, true, "图片"),
        new(".jpg", SourceCategory.Image, false, true, "图片"),
        new(".jpeg", SourceCategory.Image, false, true, "图片"),
        new(".webp", SourceCategory.Image, false, false, "图片"),
        new(".bmp", SourceCategory.Image, false, true, "图片"),
        new(".gif", SourceCategory.Image, false, true, "图片（静态帧）"),
        new(".tif", SourceCategory.Image, false, false, "TIFF"),
        new(".tiff", SourceCategory.Image, false, false, "TIFF"),
        new(".svg", SourceCategory.Image, false, false, "SVG"),
    ];

    private static readonly Dictionary<string, SourceFormat> ByExtension =
        Formats.ToDictionary(f => f.Extension, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SourceFormat> All => Formats;

    public static readonly string[] FilterOptions = ["全部", "文档", "表格", "演示", "PDF", "文本", "数据", "图片"];

    public static SourceFormat? Find(string fileName)
        => ByExtension.GetValueOrDefault(Path.GetExtension(fileName).ToLowerInvariant());

    public static bool IsSupported(string fileName) => Find(fileName) != null;

    public static SourceCategory? CategoryOf(string fileName) => Find(fileName)?.Category;

    public static string CategoryLabel(string fileName)
    {
        var format = Find(fileName);
        return format == null ? "其他" : CategoryLabel(format.Category);
    }

    public static string CategoryLabel(SourceCategory category) => category switch
    {
        SourceCategory.Text => "文本",
        SourceCategory.Document => "文档",
        SourceCategory.Spreadsheet => "表格",
        SourceCategory.Presentation => "演示",
        SourceCategory.Pdf => "PDF",
        SourceCategory.Data => "数据",
        SourceCategory.Image => "图片",
        _ => "其他",
    };

    public static string TypeLabel(string fileName)
    {
        var format = Find(fileName);
        return format == null ? "文件" : format.Label;
    }

    public static string ExtensionLabel(string fileName)
        => Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();

    /// <summary>可作为文本读取/预览的类型。</summary>
    public static bool IsTextReadable(string fileName)
        => Find(fileName) is { EditAsText: true };

    /// <summary>允许在应用内按原文编辑的类型；实际保存还会校验 1 MiB 上限与编码。</summary>
    public static bool IsTextEditable(string fileName)
        => Find(fileName) is { EditAsText: true };

    public static bool CanPreviewInApp(string fileName)
        => Find(fileName) is { InAppPreview: true };

    public static string FileDialogFilter()
    {
        var groups = Formats
            .GroupBy(f => f.Category)
            .Select(g => $"{CategoryLabel(g.Key)} ({string.Join(";", g.Select(f => "*" + f.Extension))})|{string.Join(";", g.Select(f => "*" + f.Extension))}");
        return "所有支持的资料|" + string.Join(";", Formats.Select(f => "*" + f.Extension)) + "|" + string.Join("|", groups);
    }

    public static bool ShouldIgnore(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.StartsWith('.') || name.StartsWith("~$")) return true;
        if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase)) return true;
        return !IsSupported(name);
    }
}
