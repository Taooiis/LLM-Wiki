using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using DocList = System.Windows.Documents.List;

namespace LLM_Wiki.Controls;

/// <summary>
/// 轻量 Markdown 阅读器：标题、段落、列表、引用、代码块、表格、粗斜体/行内代码/链接。
/// 不做 HTML 解析，内容始终作为文本进入 FlowDocument，避免脚本注入。
/// </summary>
public partial class MarkdownView : UserControl
{
    public MarkdownView()
    {
        InitializeComponent();
        Viewer.Document = BuildDocument("");
    }

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Render() => Viewer.Document = BuildDocument(Markdown ?? "");

    private static FlowDocument BuildDocument(string text)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 14,
            PagePadding = new Thickness(0),
            LineHeight = 22,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                i++;
                var code = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;
                var p = new Paragraph(new Run(string.Join("\n", code)))
                {
                    FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    FontSize = 12.5,
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 6, 0, 12),
                };
                doc.Blocks.Add(p);
                continue;
            }

            var heading = Regex.Match(trimmed, "^(#{1,6})\\s+(.*)$");
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var p = new Paragraph { Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 6) };
                p.FontSize = level switch { 1 => 26, 2 => 20, 3 => 17, 4 => 15.5, _ => 14.5 };
                p.FontWeight = FontWeights.SemiBold;
                AddInlines(p, heading.Groups[2].Value);
                doc.Blocks.Add(p);
                i++;
                continue;
            }

            if (trimmed.Trim() is "---" or "***" or "___")
            {
                doc.Blocks.Add(new Paragraph
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 8, 0, 8),
                });
                i++;
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                var p = new Paragraph
                {
                    Margin = new Thickness(12, 2, 0, 8),
                    FontStyle = FontStyles.Italic,
                };
                AddInlines(p, trimmed.TrimStart('>', ' '));
                doc.Blocks.Add(p);
                i++;
                continue;
            }

            if (IsTableStart(lines, i))
            {
                i = AppendTable(doc, lines, i);
                continue;
            }

            if (Regex.IsMatch(trimmed, @"^\s*([-*+]|\d+\.)\s+"))
            {
                var ordered = Regex.IsMatch(trimmed, @"^\s*\d+\.\s+");
                var list = new DocList { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(18, 2, 0, 8) };
                while (i < lines.Length)
                {
                    var item = lines[i].TrimEnd();
                    var match = Regex.Match(item, @"^\s*([-*+]|\d+\.)\s+(.*)$");
                    if (match.Success)
                    {
                        var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                        AddInlines(p, match.Groups[2].Value);
                        list.ListItems.Add(new ListItem(p));
                        i++;
                        continue;
                    }
                    if (item.Trim().Length > 0 && lines[i].StartsWith("  ", StringComparison.Ordinal) && list.ListItems.Count > 0)
                    {
                        var lastPara = (Paragraph)list.ListItems.Cast<ListItem>().Last().Blocks.FirstBlock!;
                        lastPara.Inlines.Add(new LineBreak());
                        AddInlines(lastPara, item.Trim());
                        i++;
                        continue;
                    }
                    break;
                }
                doc.Blocks.Add(list);
                continue;
            }

            // 普通段落：收集到空行/块级元素之前
            var buffer = new List<string> { trimmed };
            i++;
            while (i < lines.Length)
            {
                var next = lines[i];
                if (next.Trim().Length == 0
                    || next.StartsWith("#", StringComparison.Ordinal)
                    || next.StartsWith(">", StringComparison.Ordinal)
                    || next.StartsWith("```", StringComparison.Ordinal)
                    || Regex.IsMatch(next, @"^\s*([-*+]|\d+\.)\s+")
                    || IsTableStart(lines, i))
                    break;
                buffer.Add(next.Trim());
                i++;
            }
            var para = new Paragraph { Margin = new Thickness(0, 2, 0, 10) };
            AddInlines(para, string.Join(" ", buffer));
            doc.Blocks.Add(para);
        }
        return doc;
    }

    private static bool IsTableStart(string[] lines, int index)
    {
        if (index + 1 >= lines.Length) return false;
        var line = lines[index].Trim();
        var next = lines[index + 1].Trim();
        return line.Contains('|') && Regex.IsMatch(next, @"^\|?\s*:?-{2,}");
    }

    private static int AppendTable(FlowDocument doc, string[] lines, int start)
    {
        var rows = new List<string[]>();
        var i = start;
        while (i < lines.Length && lines[i].Contains('|'))
        {
            var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (!Regex.IsMatch(rows.Count == 0 ? "" : lines[i], @"^\|?\s*:?-{2,}"))
                rows.Add(cells);
            i++;
        }
        if (rows.Count == 0) return i;

        var columns = rows.Max(r => r.Length);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 12) };
        for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0) row.FontWeight = FontWeights.SemiBold;
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] : "";
                var p = new Paragraph { Margin = new Thickness(0) };
                AddInlines(p, text);
                var cell = new TableCell(p)
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                };
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        doc.Blocks.Add(table);
        return i;
    }

    private static readonly Regex InlineRegex = new(
        @"(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`|\[[^\]]+\]\([^)]+\))",
        RegexOptions.Compiled);

    private static void AddInlines(Paragraph paragraph, string text)
    {
        var index = 0;
        foreach (Match match in InlineRegex.Matches(text))
        {
            if (match.Index > index) paragraph.Inlines.Add(new Run(text[index..match.Index]));
            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal))
                paragraph.Inlines.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith('*') && token.EndsWith('*'))
                paragraph.Inlines.Add(new Italic(new Run(token[1..^1])));
            else if (token.StartsWith('`') && token.EndsWith('`'))
                paragraph.Inlines.Add(new Run(token[1..^1]) { FontFamily = new FontFamily("Cascadia Code, Consolas") });
            else if (token.StartsWith('['))
            {
                var link = Regex.Match(token, @"^\[([^\]]+)\]\(([^)]+)\)$");
                if (link.Success)
                {
                    var hyperlink = new Hyperlink(new Run(link.Groups[1].Value));
                    if (Uri.TryCreate(link.Groups[2].Value, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.Click += (_, _) => OpenUri(uri);
                    }
                    else
                    {
                        hyperlink.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
                    }
                    paragraph.Inlines.Add(hyperlink);
                }
                else paragraph.Inlines.Add(new Run(token));
            }
            else paragraph.Inlines.Add(new Run(token));
            index = match.Index + match.Length;
        }
        if (index < text.Length) paragraph.Inlines.Add(new Run(text[index..]));
    }

    private static void OpenUri(Uri uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
    }
}
