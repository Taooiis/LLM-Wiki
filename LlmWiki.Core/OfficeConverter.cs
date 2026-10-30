using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LLM_Wiki.Services;

/// <summary>
/// Office 内容提取：用于预览与 MCP read_source_content；不落盘、不生成资料 Markdown 副本。
/// 纯函数：任何异常都返回 error，不产生半成品文件；调用方负责成对落盘。
/// </summary>
public static class OfficeConverter
{
    // xlsx 大表截断阈值（spec 02 §6.2）
    private const int MaxRowsPerSheet = 200;

    /// <summary>docx → Markdown：标题/列表/加粗斜体/表格；不可转换元素记入"转换损失"注释。</summary>
    public static (string? md, string error) ConvertDocx(string filename, byte[] content)
    {
        try
        {
            using var ms = new MemoryStream(content);
            using var doc = WordprocessingDocument.Open(ms, false);
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw new InvalidDataException("docx 无正文");

            var losses = new List<string>();
            var sb = new StringBuilder();
            var lastWasBlock = true;

            foreach (var child in body.ChildElements)
            {
                switch (child)
                {
                    case W.Paragraph p: AppendParagraph(sb, p, losses, filename, ref lastWasBlock); break;
                    case W.Table t: AppendTable(sb, t, ref lastWasBlock); break;
                    default:
                        if (losses.Count == 0 || losses[^1] != child.LocalName)
                            losses.Add(child.LocalName);
                        break;
                }
            }

            var md = sb.ToString().TrimEnd() + "\n";
            if (losses.Count > 0)
                md = $"<!-- 转换损失（{filename}）：{string.Join("、", losses.Distinct())} -->\n" + md;
            return (md, "");
        }
        catch (Exception e)
        {
            return (null, "Word 转换失败：" + e.Message);
        }
    }

    /// <summary>段落：标题样式 → #；带 numPr → 列表项；加粗斜体内联</summary>
    private static void AppendParagraph(StringBuilder sb, W.Paragraph p, List<string> losses, string filename, ref bool lastWasBlock)
    {
        int heading = 0;
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style != null)
        {
            var m = Regex.Match(style, @"Heading(\d)$", RegexOptions.IgnoreCase);
            if (m.Success) heading = int.Parse(m.Groups[1].Value);
        }

        var text = RunsToMarkdown(p.ChildElements);
        if (heading > 0 && text.Trim().Length > 0)
        {
            sb.Append('\n').Append('#', Math.Min(heading, 6)).Append(' ').Append(text.Trim()).Append("\n\n");
            lastWasBlock = true;
            return;
        }

        bool isListItem = p.ParagraphProperties?.NumberingProperties != null;
        var line = isListItem ? "- " + text.Trim() : text.TrimEnd();

        if (line.Length > 0)
        {
            if (isListItem)
            {
                sb.Append(line).Append('\n');
            }
            else
            {
                if (!lastWasBlock) sb.Append('\n');
                sb.Append(line).Append("\n\n");
            }
        }
        else if (!lastWasBlock)
        {
            sb.Append('\n');
        }
        lastWasBlock = true;
    }

    /// <summary>一组 run → md（加粗 **…** / 斜体 *…*，同段落内整句聚合）</summary>
    private static string RunsToMarkdown(IEnumerable<OpenXmlElement> elements)
    {
        var sb = new StringBuilder();
        foreach (var el in elements)
        {
            if (el is W.Run r)
            {
                var text = r.InnerText;
                if (text.Length == 0) continue;
                var props = r.RunProperties;
                var bold = props?.Bold?.Val?.Value ?? props?.Bold != null;
                var italic = props?.Italic?.Val?.Value ?? props?.Italic != null;
                if (bold && italic) sb.Append("***").Append(text).Append("***");
                else if (bold) sb.Append("**").Append(text).Append("**");
                else if (italic) sb.Append('*').Append(text).Append('*');
                else sb.Append(text);
            }
            else if (el.LocalName == "hyperlink")
            {
                sb.Append(el.InnerText);
            }
            else if (el.LocalName is "proofErr" or "bookmarkStart" or "bookmarkEnd" or "proofErr")
            {
            }
            else if (el.LocalName is "t" or "br" or "tab" or "instrText" or "fldChar" or "noBreakHyphen")
            {
                sb.Append(el.InnerText);
            }
        }
        return sb.ToString();
    }

    /// <summary>表格 → md 表格（首行当表头）</summary>
    private static void AppendTable(StringBuilder sb, W.Table t, ref bool lastWasBlock)
    {
        var rows = t.Elements<W.TableRow>().ToList();
        if (rows.Count > 0)
        {
            sb.Append('\n');
            foreach (var (row, i) in rows.Select((r, i) => (r, i)))
            {
                var cells = row.Elements<W.TableCell>()
                    .Select(c => string.Join("<br>", c.Elements<W.Paragraph>().Select(p => RunsToMarkdown(p.ChildElements).Trim())))
                    .ToList();
                sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                if (i == 0) sb.AppendLine(string.Join("", cells.Select(_ => "| --- ")));
            }
        }
        lastWasBlock = true;
    }

    /// <summary>xlsx → Markdown：每 sheet 一个 ##；公式读缓存值；>200 行截断并指回原件（spec 02 §6.2）。</summary>
    public static (string? md, string error) ConvertXlsx(string filename, byte[] content)
    {
        try
        {
            using var ms = new MemoryStream(content);
            using var doc = SpreadsheetDocument.Open(ms, false);
            var workbookPart = doc.WorkbookPart ?? throw new InvalidDataException("xlsx 无工作簿");
            var sharedStrings = LoadSharedStrings(workbookPart);

            var sb = new StringBuilder();
            var sheets = workbookPart.Workbook?.Sheets?.Elements<S.Sheet>().ToList() ?? [];
            foreach (var sheet in sheets)
            {
                var name = sheet.Name?.Value ?? "Sheet";
                sb.Append("\n## ").Append(name).Append("\n\n");
                if (sheet.Id?.Value is not string id || workbookPart.GetPartById(id) is not WorksheetPart wsPart)
                    continue;

                var data = wsPart.Worksheet?.GetFirstChild<S.SheetData>();
                if (data == null) continue;

                var rows = data.Elements<S.Row>().ToList();
                var took = Math.Min(rows.Count, MaxRowsPerSheet);
                for (var i = 0; i < took; i++)
                {
                    var cells = RowCells(rows[i], sharedStrings);
                    sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                    if (i == 0)
                    {
                        var sep = cells.Count == 0 ? 1 : cells.Count;
                        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", sep))).Append('\n');
                    }
                }
                if (rows.Count > MaxRowsPerSheet)
                    sb.Append($"\n> 已截断 {rows.Count - MaxRowsPerSheet} 行，原件见 raw/sources/{filename}\n");
            }
            return (sb.ToString().TrimStart() + "\n", "");
        }
        catch (Exception e)
        {
            return (null, "Excel 转换失败：" + e.Message);
        }
    }


    /// <summary>pptx/pptm → 临时文字预览；按真实幻灯片顺序提取文字与备注，不执行宏、不渲染图形。</summary>
    public static (string? md, string error) ConvertPptx(string filename, byte[] content)
    {
        try
        {
            using var ms = new MemoryStream(content);
            using var doc = PresentationDocument.Open(ms, false);
            var presentationPart = doc.PresentationPart ?? throw new InvalidDataException("pptx 无演示文稿");
            var slideIds = presentationPart.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? [];
            var sb = new StringBuilder();
            sb.AppendLine("# PPT 内容预览（文字与备注，未覆盖图形/图表/图片）");
            sb.AppendLine();
            var index = 0;
            foreach (var slideId in slideIds)
            {
                index++;
                if (slideId.RelationshipId?.Value is not string relId
                    || presentationPart.GetPartById(relId) is not SlidePart slidePart)
                    continue;
                sb.AppendLine($"## 幻灯片 {index}");
                var texts = slidePart.Slide?.Descendants<A.Text>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
                sb.AppendLine(texts.Length == 0 ? "（本页没有可提取文字）" : string.Join("\n", texts.Select(t => "- " + t.Trim())));
                var notes = slidePart.NotesSlidePart?.NotesSlide?.Descendants<A.Text>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
                if (notes.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("### 备注");
                    sb.AppendLine(string.Join("\n", notes.Select(t => "- " + t.Trim())));
                }
                sb.AppendLine();
            }
            return (sb.ToString().TrimEnd() + "\n", "");
        }
        catch (Exception e)
        {
            return (null, "PPT 读取失败：" + e.Message);
        }
    }

    private static List<string> LoadSharedStrings(WorkbookPart wb)
    {
        var sst = wb.SharedStringTablePart?.SharedStringTable;
        if (sst == null) return [];
        return [.. sst.Elements<S.SharedStringItem>().Select(item => item.InnerText)];
    }

    /// <summary>一行 → 单元格文本清单（共享字符串/内联串/数值；公式有缓存值读缓存，spec 02 §6.2）</summary>
    private static List<string> RowCells(S.Row row, List<string> sharedStrings)
    {
        var result = new List<string>();
        foreach (var cell in row.Elements<S.Cell>())
        {
            string text;
            var kind = cell.DataType?.ToString();
            if (kind == "inlineStr")
                text = cell.InlineString?.InnerText ?? "";
            else if (cell.CellValue != null && cell.CellValue.InnerText.Length > 0)
            {
                text = cell.CellValue.InnerText;
                if (kind == "s"
                    && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)
                    && idx >= 0 && idx < sharedStrings.Count)
                    text = sharedStrings[idx];
            }
            else text = "";
            result.Add(text.Replace("|", "\\|").Replace("\n", "<br>"));
        }
        return result;
    }
}
