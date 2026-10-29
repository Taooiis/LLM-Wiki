using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;
using Wpf.Ui.Abstractions;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class WorkflowViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _mainViewModel;
        private readonly FileBridge _fileBridge;
        private readonly ToastService _toastService;

        public WorkflowViewModel(MainWindowViewModel mainViewModel, FileBridge fileBridge, ToastService toastService)
        {
            _mainViewModel = mainViewModel;
            _fileBridge = fileBridge;
            _toastService = toastService;
            _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
        }

        private void MainViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb))
            {
                OnKbChanged(_mainViewModel.CurrentKb);
            }
            if (e.PropertyName == nameof(MainWindowViewModel.PendingIngestFiles))
            {
                ProcessPendingIngest();
            }
        }

        [ObservableProperty]
        private string _selectedKb = "";

        [ObservableProperty]
        private string _kbPath = "";

        [ObservableProperty]
        private string _ingestFiles = "";

        [ObservableProperty]
        private ObservableCollection<PromptCard> _promptCards = new();

        private void ProcessPendingIngest()
        {
            if (_mainViewModel.PendingIngestFiles == null || _mainViewModel.PendingIngestFiles.Count == 0)
            {
                return;
            }

            IngestFiles = string.Join(",", _mainViewModel.PendingIngestFiles);
            _mainViewModel.PendingIngestFiles.Clear();
            GenerateIngestCommand.Execute(null);
        }

        private void OnKbChanged(string kb)
        {
            SelectedKb = kb;
            if (string.IsNullOrWhiteSpace(kb))
            {
                KbPath = string.Empty;
                return;
            }

            KbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wikis", kb));
            BuildPromptCards();
        }

        private void BuildPromptCards()
        {
            PromptCards.Clear();
            if (string.IsNullOrWhiteSpace(KbPath))
            {
                return;
            }

            var kbPath = KbPath;

            PromptCards.Add(new PromptCard(
                "边界说明",
                "定义 Agent 对本知识库的读写边界与行为规范。",
                BuildBoundaryPrompt(kbPath, SelectedKb)));

            PromptCards.Add(new PromptCard(
                "Ingest（批量）",
                "将知识库内所有源文档纳入索引，生成全量 Ingest 提示词。",
                BuildIngestPrompt(kbPath, false, null)));

            PromptCards.Add(new PromptCard(
                "Ingest（增量）",
                "仅处理 .changes.json 中标记的待处理变更文件。",
                BuildIngestPrompt(kbPath, true, null)));            PromptCards.Add(new PromptCard(
                "Query",
                "基于知识库内容回答问题，要求带来源引用。",
                BuildQueryPrompt(kbPath)));

            PromptCards.Add(new PromptCard(
                "Lint",
                "检查知识库文档的格式、链接有效性、引用规范。",
                BuildLintPrompt(kbPath)));

            PromptCards.Add(new PromptCard(
                "Compare",
                "对比多个源文档的差异与共同点。",
                BuildComparePrompt(kbPath)));

            PromptCards.Add(new PromptCard(
                "Clean（级联清理）",
                "删除已废弃源文档的摘要页、index 条目与失效 [[wikilinks]]。",
                BuildCleanPrompt(kbPath)));
        }

        private string BuildBoundaryPrompt(string kbPath, string kb)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 知识库接入说明（粘贴进 Agent 配置后可直接读取本库）");
            sb.AppendLine();
            sb.AppendLine("知识库：" + kb);
            sb.AppendLine("本地绝对路径：" + kbPath);
            sb.AppendLine();
            sb.AppendLine("## 先读这三个文件");

            // 读取 purpose.md
            var purposeError = "";
            var purposeContent = _fileBridge.ReadText(kb, "purpose.md", out purposeError) ?? "";
            sb.AppendLine("1. purpose.md（目标/范围）：");
            sb.AppendLine(Cut(purposeContent, 800));
            sb.AppendLine();

            // 读取 schema.md
            var schemaError = "";
            var schemaContent = _fileBridge.ReadText(kb, "schema.md", out schemaError) ?? "";
            sb.AppendLine("2. schema.md（结构规则）：");
            sb.AppendLine(Cut(schemaContent, 800));
            sb.AppendLine();

            // 读取 wiki/index.md
            var indexError = "";
            var indexContent = _fileBridge.ReadText(kb, "wiki/index.md", out indexError) ?? "";
            sb.AppendLine("3. wiki/index.md（内容目录，先读它找页面）：");
            sb.AppendLine(Cut(indexContent, 1200));
            sb.AppendLine();

            sb.AppendLine("## 目录约定");
            sb.AppendLine("- raw/sources/：源文档（.md/.txt，只读）");
            sb.AppendLine("- wiki/entities/、concepts/、sources/、queries/、synthesis/、comparisons/：知识页");
            sb.AppendLine("- wiki/log.md：变更历史；源文档被改后必须重新 Ingest 对应文档");
            sb.AppendLine("- 页内用 [[页面名]] 互链；断言格式 [来源: 文件名]");
            sb.AppendLine();

            // 读取目录树（前 120 项）
            var treeError = "";
            var treeItems = _fileBridge.Tree(kb);
            var files = new List<string>();
            FlattenTree(treeItems, "", files, 120);
            sb.AppendLine("## 文件清单（" + files.Count + " 项，最多列 120，大改后请重新生成覆盖）");
            sb.AppendLine(string.Join("\n", files));
            sb.AppendLine();
            sb.AppendLine("## 回答要求");
            sb.AppendLine("引用具体的 wiki 页面；重要结论写回 synthesis/comparison 新页并更新 index.md 和 log.md。");
            return sb.ToString();
        }

        private static string Cut(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > n ? s.Substring(0, n) + "\n…（已截断）" : s;
        }

        private static void FlattenTree(TreeItem[] nodes, string pre, List<string> outList, int cap)
        {
            foreach (var node in nodes)
            {
                if (outList.Count >= cap) return;
                if (node.Type == "dir")
                {
                    outList.Add(pre + node.Name + "/");
                    FlattenTree(node.Children ?? Array.Empty<TreeItem>(), pre + node.Name + "/", outList, cap);
                }
                else
                {
                    outList.Add(pre + node.Name);
                }
            }
        }

        private static string BuildIngestPrompt(string kbPath, bool incremental, List<string>? files = null)
        {
            var suffix = incremental ? "（增量）" : "（批量）";
            var sb = new StringBuilder();
            sb.AppendLine("# Ingest" + suffix + "（" + kbPath + "）");
            sb.AppendLine();
            sb.AppendLine("你是一个知识库 Ingest Agent，目标路径：" + kbPath);
            sb.AppendLine();
            sb.AppendLine("## 任务");
            if (files != null && files.Count > 0)
            {
                sb.AppendLine("- 仅处理以下指定文件：");
                foreach (var f in files)
                {
                    sb.AppendLine("  - raw/sources/" + f);
                }
            }
            else if (incremental)
            {
                sb.AppendLine("- 仅处理 .changes.json 中记录的待处理变更文件（文件/动作/哈希）");
            }
            else
            {
                sb.AppendLine("- 扫描 raw/sources/ 下全部 .md/.txt 入库副本");
            }
            sb.AppendLine("- 对每个文件：读取内容 → 生成摘要 → 写入 wiki/sources/ → 更新 wiki/index.md → 记录 wiki/log.md");
            sb.AppendLine("- 如文件内容未变化（SHA256 与上次一致），跳过该文件");
            sb.AppendLine();
            sb.AppendLine("## 输出要求");
            sb.AppendLine("- 为每个处理的文件生成一条 wiki/sources/<文件名>.md 摘要");
            sb.AppendLine("- 更新 wiki/index.md 的文件列表");
            sb.AppendLine("- 在 wiki/log.md 追加操作记录：" + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " Ingest：<文件>");
            sb.AppendLine("- 所有路径基于：" + kbPath);
            return sb.ToString();
        }

        private static string BuildQueryPrompt(string kbPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Query（" + kbPath + "）");
            sb.AppendLine();
            sb.AppendLine("你是一个基于知识库的问答 Agent，目标路径：" + kbPath);
            sb.AppendLine();
            sb.AppendLine("## 任务");
            sb.AppendLine("- 根据用户问题，在 wiki/sources/、wiki/entities/、wiki/concepts/ 中检索相关内容");
            sb.AppendLine("- 基于检索到的证据回答问题");
            sb.AppendLine("- 每个结论必须标注来源：[来源: 文件名]");
            sb.AppendLine();
            sb.AppendLine("## 约束");
            sb.AppendLine("- 仅使用本知识库内的文件，不得编造内容");
            sb.AppendLine("- 找不到相关内容时，诚实说明“知识库中未找到相关信息”");
            sb.AppendLine("- 所有路径基于：" + kbPath);
            return sb.ToString();
        }

        private static string BuildLintPrompt(string kbPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Lint（" + kbPath + "）");
            sb.AppendLine();
            sb.AppendLine("你是一个知识库质量检查 Agent，目标路径：" + kbPath);
            sb.AppendLine();
            sb.AppendLine("## 检查项");
            sb.AppendLine("- 格式：每个 wiki 页面顶部是否有 YAML frontmatter（含 sources: []）");
            sb.AppendLine("- 链接：[[页面名]] 格式是否正确，目标页面是否存在");
            sb.AppendLine("- 引用：所有断言是否有 [来源: 文件名] 格式的来源标注");
            sb.AppendLine("- 结构：实体页是否在 wiki/entities/，概念页是否在 wiki/concepts/");
            sb.AppendLine();
            sb.AppendLine("## 输出");
            sb.AppendLine("- 列出所有发现的问题，按严重程度分类");
            sb.AppendLine("- 给出可直接执行的修复建议");
            sb.AppendLine("- 所有路径基于：" + kbPath);
            return sb.ToString();
        }

        private static string BuildComparePrompt(string kbPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Compare（" + kbPath + "）");
            sb.AppendLine();
            sb.AppendLine("你是一个知识库对比 Agent，目标路径：" + kbPath);
            sb.AppendLine();
            sb.AppendLine("## 任务");
            sb.AppendLine("- 用户指定两个或多个源文档");
            sb.AppendLine("- 对比它们的差异与共同点");
            sb.AppendLine("- 生成对比报告，写入 wiki/comparisons/ 下");
            sb.AppendLine();
            sb.AppendLine("## 输出格式");
            sb.AppendLine("- ## 对比报告：<文件A> vs <文件B>");
            sb.AppendLine("- ### 差异点");
            sb.AppendLine("- ### 共同点");
            sb.AppendLine("- ### 结论建议");
            sb.AppendLine("- 所有路径基于：" + kbPath);
            return sb.ToString();
        }

        private static string BuildCleanPrompt(string kbPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Clean（级联清理）（" + kbPath + "）");
            sb.AppendLine();
            sb.AppendLine("你是一个知识库清理 Agent，目标路径：" + kbPath);
            sb.AppendLine();
            sb.AppendLine("## 任务");
            sb.AppendLine("- 用户指定已删除或即将删除的源文档");
            sb.AppendLine("- 清理该文档在 wiki/ 层的所有引用：");
            sb.AppendLine("  1. 删除 wiki/sources/ 下对应的摘要页");
            sb.AppendLine("  2. 从 wiki/index.md 中移除该文档条目");
            sb.AppendLine("  3. 清理所有页面中失效的 [[该文档名]] 链接");
            sb.AppendLine("  4. 共享实体页仅摘除该源条目，不删除整页");
            sb.AppendLine();
            sb.AppendLine("## 约束");
            sb.AppendLine("- 不删除任何 wiki/ 页面本身，仅移除条目与链接");
            sb.AppendLine("- 清理后记录 wiki/log.md：" + System.DateTime.Now.ToString("yyyy-MM-dd") + " Clean：<文件>");
            sb.AppendLine("- 所有路径基于：" + kbPath);
            return sb.ToString();
        }

        [RelayCommand]
        private void CopyPrompt(string cardName)
        {
            var card = PromptCards.FirstOrDefault(c => c.Title == cardName);
            if (card == null)
            {
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(card.PromptContent);
                _toastService.ShowSuccess($"已复制：{card.Title}");
            }
            catch
            {
                _toastService.ShowError("复制剪贴板失败");
            }
        }

        [RelayCommand]
        private void GenerateIngest()
        {
            List<string>? files = null;
            if (!string.IsNullOrWhiteSpace(IngestFiles))
            {
                files = IngestFiles
                    .Split(new[] { ',', '，', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .Where(f => f.Length > 0)
                    .ToList();
            }

            // 判断是增量还是批量：如果有指定文件，按批量处理指定文件；否则按批量全部
            var card = PromptCards.FirstOrDefault(c => c.Title == "Ingest（批量）");
            if (card == null)
            {
                return;
            }

            var prompt = BuildIngestPrompt(KbPath, false, files);
            var idx = PromptCards.IndexOf(card);
            PromptCards[idx] = new PromptCard(card.Title, card.Description, prompt);
        }
    }

    public record PromptCard(string Title, string Description, string PromptContent);
}
