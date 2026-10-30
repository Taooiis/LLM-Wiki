using System.IO;

namespace LLM_Wiki.Services;

/// <summary>原格式资料的临时预览：文本、图片或 Office 内容提取；不落盘、不改源字节。</summary>
public sealed class SourcePreviewService(ConfigService config)
{
    public SourcePreview Get(string kb, string rel)
    {
        var normalized = ChangeService.Normalize(rel);
        if (!normalized.StartsWith("raw/sources/", StringComparison.OrdinalIgnoreCase))
            return SourcePreview.Unavailable("只允许预览 raw/sources/ 下的资料。");
        var full = Path.GetFullPath(Path.Combine(config.WikisDir, kb, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(full)) return SourcePreview.Unavailable("资料不存在。");

        var name = Path.GetFileName(full);
        var format = SourceFormatRegistry.Find(name);
        if (format == null) return SourcePreview.Unavailable("此格式不在当前支持列表。");

        if (SourceFormatRegistry.IsTextReadable(name))
        {
            try
            {
                return new SourcePreview(false, true, false, FileHelper.DecodeText(File.ReadAllBytes(full)), null, "", "");
            }
            catch (Exception e)
            {
                return SourcePreview.Unavailable("文本读取失败：" + e.Message);
            }
        }

        if (format.Category == SourceCategory.Image)
        {
            var imageExt = Path.GetExtension(name).ToLowerInvariant();
            var decodable = imageExt is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";
            return decodable
                ? new SourcePreview(true, false, false, "", full, "图片预览", "图片内容不代表 OCR 已完成。")
                : SourcePreview.Unavailable("当前 WPF 解码器不能可靠预览该图片格式；请打开原文件。");
        }

        if (format.Extension is ".pdf")
            return ExtractPdf(full);

        try
        {
            var bytes = File.ReadAllBytes(full);
            var (text, error) = format.Extension switch
            {
                ".docx" or ".docm" => OfficeConverter.ConvertDocx(name, bytes),
                ".xlsx" or ".xlsm" => OfficeConverter.ConvertXlsx(name, bytes),
                ".pptx" or ".pptm" => OfficeConverter.ConvertPptx(name, bytes),
                _ => (null, "此格式暂无页内读取器，请打开原文件。")
            };
            return text == null
                ? SourcePreview.Unavailable(error)
                : new SourcePreview(false, false, true, text, null,
                    "内容预览（不代表完整版式）",
                    "图片、批注、图表或其他未提取结构可能未覆盖；源文件保持原样。");
        }
        catch (Exception e)
        {
            return SourcePreview.Unavailable("读取失败：" + e.Message);
        }
    }

    private static SourcePreview ExtractPdf(string fullPath)
    {
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(fullPath);
            var total = document.NumberOfPages;
            var text = new System.Text.StringBuilder();
            var withText = 0;
            var scanned = 0;
            var limit = Math.Min(total, 50);
            for (var pageNumber = 1; pageNumber <= limit; pageNumber++)
            {
                var pageText = document.GetPage(pageNumber).Text?.Trim() ?? "";
                if (pageText.Length == 0)
                {
                    scanned++;
                    continue;
                }
                withText++;
                text.AppendLine($"— 第 {pageNumber} 页 —");
                text.AppendLine(pageText);
                text.AppendLine();
            }

            if (withText == 0)
                return SourcePreview.Unavailable("此 PDF 未提取到文字，可能为扫描内容，需要视觉读取或 OCR；请打开原文件查看版面。");

            var warning = $"仅提取前 {limit} 页文字，不代表完整版式；";
            if (scanned > 0) warning += $"其中 {scanned} 页无可提取文字（可能为扫描页）；";
            if (total > limit) warning += $"文档共 {total} 页；";
            warning += "图表、批注与图片未覆盖。";
            return new SourcePreview(false, false, true, text.ToString(), null,
                $"PDF 文字内容预览（{limit}/{total} 页）", warning);
        }
        catch (Exception e)
        {
            return SourcePreview.Unavailable("PDF 已加密、损坏或无法解析：" + e.Message + "；原件仍保持原样，可打开或导出。");
        }
    }
}

public sealed record SourcePreview(
    bool IsImage,
    bool IsText,
    bool IsExtractedText,
    string Text,
    string? ImagePath,
    string Title,
    string Warning)
{
    public static SourcePreview Unavailable(string message)
        => new(false, false, false, "", null, "", message);

    public bool Available => IsImage || IsText || IsExtractedText;
}
