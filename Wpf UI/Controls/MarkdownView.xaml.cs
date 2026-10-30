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
    private readonly Dictionary<string, Paragraph> _headingMap = new(StringComparer.Ordinal);

    public MarkdownView()
    {
        InitializeComponent();
        Viewer.SizeChanged += (_, _) => ApplyReadingWidth();
        Render();
    }

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render()));

    public static readonly DependencyProperty SuppressFirstH1Property = DependencyProperty.Register(
        nameof(SuppressFirstH1), typeof(bool), typeof(MarkdownView),
        new PropertyMetadata(false, (d, _) => ((MarkdownView)d).Render()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>标题已由页面标题区显示时，正文不再重复同一个 H1。</summary>
    public bool SuppressFirstH1
    {
        get => (bool)GetValue(SuppressFirstH1Property);
        set => SetValue(SuppressFirstH1Property, value);
    }

    public void ScrollToHeading(string heading)
    {
        if (string.IsNullOrWhiteSpace(heading)) return;
        if (_headingMap.TryGetValue(heading.Trim(), out var paragraph)) paragraph.BringIntoView();
    }

    private void Render()
    {
        _headingMap.Clear();
        Viewer.Document = BuildDocument(Markdown ?? "", _headingMap, SuppressFirstH1);
        ApplyReadingWidth();
    }

    /// <summary>
    /// 01§3：正文优先 680—820 宽；阅读表面更宽时左右补白居中，窄容器时不产生横滚。
    /// </summary>
    private void ApplyReadingWidth()
    {
        if (Viewer.Document == null) return;
        var viewport = Viewer.ActualWidth;
        if (double.IsNaN(viewport) || viewport <= 0) return;
        var pad = Math.Max(0, (viewport - 820) / 2);
        Viewer.Document.PagePadding = new Thickness(pad, 0, pad, 0);
    }

    private static FlowDocument BuildDocument(string text, Dictionary<string, Paragraph> headingMap, bool suppressFirstH1)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 15,
            PagePadding = new Thickness(0),
            // 01§3：正文 14—15、行距 23—25
            LineHeight = 24,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var firstH1Suppressed = false;
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
                // 01§3：代码块等宽、12 内边距、可复制；局部横滚不折行
                doc.Blocks.Add(BuildMonoScrollableBlock(string.Join("\n", code), new Thickness(0, 6, 0, 12)));
                continue;
            }

            var heading = Regex.Match(trimmed, "^(#{1,6})\\s+(.*)$");
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                if (level == 1 && suppressFirstH1 && !firstH1Suppressed)
                {
                    firstH1Suppressed = true;
                    i++;
                    continue;
                }
                // 01§3：H2 字号 18、上 24 下 10；其余标题保持层级
                var p = level == 2
                    ? new Paragraph { Margin = new Thickness(0, 24, 0, 10) }
                    : new Paragraph { Margin = new Thickness(0, level <= 1 ? 14 : 10, 0, 6) };
                p.FontSize = level switch { 1 => 26, 2 => 22, 3 => 17, 4 => 15.5, _ => 14.5 };
                p.FontWeight = FontWeights.SemiBold;
                AddInlines(p, heading.Groups[2].Value);
                doc.Blocks.Add(p);
                var headingText = Regex.Replace(heading.Groups[2].Value, @"[*`\[\]]", "").Trim();
                if (headingText.Length > 0 && !headingMap.ContainsKey(headingText))
                    headingMap[headingText] = p;
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
                var raw = new List<string>();
                while (i < lines.Length && lines[i].Contains('|'))
                {
                    raw.Add(lines[i]);
                    i++;
                }
                var tableBlock = BuildTableBlock(raw);
                if (tableBlock != null) doc.Blocks.Add(tableBlock);
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
                        // 01§3：列表项距 6—8（相邻项上下各 3，间距 6）
                        var p = new Paragraph { Margin = new Thickness(0, 3, 0, 3) };
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

    /// <summary>
    /// 表格渲染（01§3）：窄表格保持原生 Table；宽表格（估算超宽或列数多）改为等宽原文视图，
    /// 外包横向 ScrollViewer 局部横滚、不折行，不让整页横滚。
    /// </summary>
    private static Block? BuildTableBlock(List<string> rawLines)
    {
        var rows = new List<string[]>();
        foreach (var line in rawLines)
        {
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (!Regex.IsMatch(rows.Count == 0 ? "" : line, @"^\|?\s*:?-{2,}"))
                rows.Add(cells);
        }
        if (rows.Count == 0) return null;

        var columns = rows.Max(r => r.Length);
        var widestRow = rows.Max(r => r.Sum(c => c.Length) + r.Length * 3);
        var estimatedWidth = widestRow * 7.2 + columns * 16;
        if (estimatedWidth > 760 || columns > 6)
            return BuildMonoScrollableBlock(string.Join("\n", rawLines), new Thickness(0, 6, 0, 12));

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
        return table;
    }

    /// <summary>
    /// 代码块/宽表格共用：等宽、12 内边距、可复制；内容不折行，超出宽度时局部横向滚动。
    /// 颜色走语义资源引用，主题切换随之更新（00§8）。
    /// </summary>
    private static Block BuildMonoScrollableBlock(string text, Thickness margin)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 12.5,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(12),
            IsTabStop = false,
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        var border = new Border { CornerRadius = new CornerRadius(6), Child = box };
        border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        border.BorderThickness = new Thickness(1);
        return new BlockUIContainer(border) { Margin = margin };
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
                        // 链接色随主题（00§8：不在代码写死主题色）。
                        hyperlink.Foreground = System.Windows.Application.Current?.TryFindResource("ItemSelectedBorderBrush") as System.Windows.Media.Brush
                            ?? new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
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
