using System.Globalization;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LlmWiki.App.Services;
using Xunit;
using Xunit.Abstractions;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace LlmWiki.Tests;

/// <summary>四格式上传 + Office→Markdown 转换（spec 02 §6、20MB 上限 D8、重名拒绝、GBK 探测、成对删除）。</summary>
public class UploadTests : IDisposable
{
    private readonly string _wikis = Path.Combine(Path.GetTempPath(), "llmwiki-up-" + Guid.NewGuid().ToString("N"));
    private readonly UploadService _svc;
    private readonly ITestOutputHelper _out;

    public UploadTests(ITestOutputHelper output)
    {
        Directory.CreateDirectory(_wikis);
        new KbService(_wikis).CreateKb("测试库");
        _svc = new UploadService(_wikis);
        _out = output;
    }

    public void Dispose() { try { Directory.Delete(_wikis, true); } catch { } }

    // ---------- fixture 生成（程序化构造最小合法 docx/xlsx） ----------

    /// <summary>含 Heading1/2、列表、加粗、斜体与 2x3 表格的 docx。</summary>
    private static byte[] MakeDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var body = new Body(
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new Run(new Text("标题一"))),
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }),
                    new Run(new Text("标题二"))),
                new Paragraph(
                    new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
                    new Run(new Text("项一"))),
                new Paragraph(
                    new Run(new RunProperties(new Bold()), new Text("加粗")),
                    new Run(new Text("正文"))),
                new Paragraph(
                    new Run(new RunProperties(new Italic()), new Text("斜体句"))),
                new Table(
                    new TableRow(
                        new TableCell(new Paragraph(new Run(new Text("甲")))),
                        new TableCell(new Paragraph(new Run(new Text("乙"))))),
                    new TableRow(
                        new TableCell(new Paragraph(new Run(new Text("一")))),
                        new TableCell(new Paragraph(new Run(new Text("二")))))));
            doc.AddMainDocumentPart().Document = new Document(body);
            doc.Save();
        }
        return ms.ToArray();
    }

    /// <summary>两个工作表（"表甲" 3 行含公式缓存；"表乙" 250 行触发截断）。</summary>
    private static byte[] MakeXlsx()
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            var sheetA = wb.AddNewPart<WorksheetPart>();
            var sheetB = wb.AddNewPart<WorksheetPart>();

            var rowsA = new S.SheetData(
                InnerRow(1, NumCell("A1", 1), StrCell("B1", "一件")),
                InnerRow(2, NumCell("A2", 2), StrCell("B2", "二件")),
                InnerRow(3, FormulaCell("A3", "A1+A2", "3")));
            sheetA.Worksheet = new S.Worksheet(rowsA);

            var rowsB = new S.SheetData();
            for (uint i = 1; i <= 250; i++)
            {
                rowsB.Append(InnerRow(i, NumCell("A" + i, i)));
            }
            sheetB.Worksheet = new S.Worksheet(rowsB);

            wb.Workbook = new S.Workbook(new S.Sheets(
                new S.Sheet { Name = "表甲", SheetId = 1, Id = wb.GetIdOfPart(sheetA) },
                new S.Sheet { Name = "表乙", SheetId = 2, Id = wb.GetIdOfPart(sheetB) }));
            doc.Save();
        }
        return ms.ToArray();
    }

    private static S.Row InnerRow(uint idx, params S.Cell[] cells)
    {
        var r = new S.Row();
        foreach (var c in cells) r.Append(c);
        return r;
    }

    private static S.Cell NumCell(string @ref, double v) =>
        new() { CellReference = @ref, DataType = new EnumValue<S.CellValues>(S.CellValues.Number), CellValue = new S.CellValue(v.ToString(CultureInfo.InvariantCulture)) };

    private static S.Cell StrCell(string @ref, string v) =>
        new() { CellReference = @ref, DataType = new EnumValue<S.CellValues>(S.CellValues.InlineString), InlineString = new S.InlineString(new S.Text(v)) };

    private static S.Cell FormulaCell(string @ref, string formula, string cached) =>
        new() { CellReference = @ref, CellFormula = new S.CellFormula(formula), CellValue = new S.CellValue(cached) };

    // ---------- md / docx / xlsx / txt 上传 ----------

    [Fact]
    public void Upload_Md_GoesStraightToSources()
    {
        var (ok, err, rel) = _svc.Upload("测试库", "笔记.md", Encoding.UTF8.GetBytes("# 笔记"));
        Assert.True(ok, err);
        Assert.Equal("raw/sources/笔记.md", rel);
        Assert.Equal("# 笔记", File.ReadAllText(Path.Combine(_wikis, "测试库", "raw", "sources", "笔记.md")));
        // 骨架本就含 raw/original 目录；断言的是没有原件文件
        Assert.Empty(Directory.GetFiles(Path.Combine(_wikis, "测试库", "raw", "original")));
    }

    [Fact]
    public void Upload_Docx_PairsOriginalAndMarkdownCopy()
    {
        var (ok, err, rel) = _svc.Upload("测试库", "调研报告.docx", MakeDocx());
        Assert.True(ok, err);
        Assert.Equal("raw/sources/调研报告.docx.md", rel);

        // 原件落 raw/original/，副本落 raw/sources/
        Assert.True(File.Exists(Path.Combine(_wikis, "测试库", "raw", "original", "调研报告.docx")));
        var md = File.ReadAllText(Path.Combine(_wikis, "测试库", "raw", "sources", "调研报告.docx.md"));

        // 副本内容：标题层级 / 列表 / 加粗 / 表格（spec 02 §6.2）
        Assert.Contains("# 标题一", md);
        Assert.Contains("## 标题二", md);
        Assert.Contains("- 项一", md);
        Assert.Contains("**加粗**", md);
        Assert.Contains("| 甲 | 乙 |", md);
        Assert.Contains("| 一 | 二 |", md);

        // log 两行事实：原件入库 + 副本生成（spec 02 §6.1.4）
        var log = File.ReadAllText(Path.Combine(_wikis, "测试库", "wiki", "log.md"));
        Assert.Contains("原件入库：raw/original/调研报告.docx", log);
        Assert.Contains("副本生成：raw/sources/调研报告.docx.md", log);

        // 待处理变更以副本文件名为准，带指纹
        var c = Assert.Single(ChangeStore.Load(_wikis, "测试库"));
        Assert.Equal("raw/sources/调研报告.docx.md", c.File);
        Assert.Equal(64, c.Hash.Length);
    }

    [Fact]
    public void Upload_Xlsx_MultiSheetFormulaCachedAndTruncation()
    {
        var (ok, err, _) = _svc.Upload("测试库", "数据表.xlsx", MakeXlsx());
        Assert.True(ok, err);
        var md = File.ReadAllText(Path.Combine(_wikis, "测试库", "raw", "sources", "数据表.xlsx.md"));

        // 每工作表一个二级标题
        Assert.Contains("## 表甲", md);
        Assert.Contains("## 表乙", md);
        // 公式取缓存值 3（spec 02 §6.2）
        Assert.Matches(@"\|\s*3\s*\|", md);
        // >200 行截断并注明原件位置
        Assert.Contains("已截断", md);
        Assert.Contains("raw/original", md);
    }

    [Fact]
    public void Upload_Txt_GbkDetectedAndStoredAsUtf8()
    {
        // 构造"非法 UTF8 且合法 GB18030"的字节，严格 UTF8 校验必失败 → 走 GB 解码
        var gbkText = "中文锟斤拷";
        var gbkBytes = Encoding.GetEncoding("GB18030").GetBytes(gbkText);
        // 校验这批字节确实非法 UTF8（保证测试本身有效）
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(gbkBytes));

        var (ok, err, _) = _svc.Upload("测试库", "gbk.txt", gbkBytes);
        Assert.True(ok, err);
        var stored = File.ReadAllText(Path.Combine(_wikis, "测试库", "raw", "sources", "gbk.txt"));
        Assert.Equal(gbkText, stored); // 读回 = 原文，已统一 UTF-8
    }

    // ---------- 防护语义 ----------

    [Fact]
    public void Upload_RejectsOversizeInvalidExtAndCopyRenameConflict()
    {
        // >20MB（spec D8）
        var big = new byte[20 * 1024 * 1024 + 1];
        Assert.False(_svc.Upload("测试库", "大文件.docx", big).ok);

        // 非四格式
        var (ok, err, _) = _svc.Upload("测试库", "说明.pdf", new byte[] { 1 });
        Assert.False(ok);
        Assert.Contains("格式", err);

        // office 副本重名（用户手工占用 raw/sources/调研报告.docx.md）→ 拒绝不覆盖（Review Focus 2）
        File.WriteAllText(Path.Combine(_wikis, "测试库", "raw", "sources", "调研报告.docx.md"), "手工占用");
        var (ok2, err2, _) = _svc.Upload("测试库", "调研报告.docx", MakeDocx());
        Assert.False(ok2);
        Assert.False(string.IsNullOrEmpty(err2));
        Assert.Equal("手工占用", File.ReadAllText(Path.Combine(_wikis, "测试库", "raw", "sources", "调研报告.docx.md")));
        // 原件也必须没落盘（spec：转换失败/冲突不半入库）
        Assert.False(File.Exists(Path.Combine(_wikis, "测试库", "raw", "original", "调研报告.docx")));
    }

    // ---------- 成对删除 ----------

    [Fact]
    public void DeleteSource_OfficeRemovesPairAndLogsTwice()
    {
        _svc.Upload("测试库", "调研报告.docx", MakeDocx());
        var (ok, err) = _svc.DeleteSource("测试库", "raw/sources/调研报告.docx.md");
        Assert.True(ok, err);
        Assert.False(File.Exists(Path.Combine(_wikis, "测试库", "raw", "original", "调研报告.docx")));
        Assert.False(File.Exists(Path.Combine(_wikis, "测试库", "raw", "sources", "调研报告.docx.md")));
        var log = File.ReadAllText(Path.Combine(_wikis, "测试库", "wiki", "log.md"));
        Assert.Contains("原件删除：raw/original/调研报告.docx", log);
        Assert.Contains("副本删除：raw/sources/调研报告.docx.md", log);
        Assert.Equal("删除", ChangeStore.Load(_wikis, "测试库").Single().Action);
    }

    [Fact]
    public void DeleteSource_PlainText_GoesThroughBridgeSemantics()
    {
        _svc.Upload("测试库", "旧笔记.txt", Encoding.UTF8.GetBytes("内容"));
        var (ok, err) = _svc.DeleteSource("测试库", "raw/sources/旧笔记.txt");
        Assert.True(ok, err);
        Assert.False(File.Exists(Path.Combine(_wikis, "测试库", "raw", "sources", "旧笔记.txt")));
    }
}
