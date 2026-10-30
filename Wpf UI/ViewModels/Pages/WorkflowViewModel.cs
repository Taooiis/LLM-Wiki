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
    /// <summary>工作流页：七类提示词卡（设计文档 04）。1-6 双列网格，Clean 横跨；输入与预览都在卡内完成。</summary>
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

            // 页面 VM 懒解析：CurrentKb 首值可能早于本构造，订阅后拉一次现值
            OnKbChanged(_mainViewModel.CurrentKb);
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

        /// <summary>顶部「当前库」展示文案：未选库时明确写「未选择知识库」，避免冒号后空白。</summary>
        public string KbPathText => string.IsNullOrWhiteSpace(KbPath) ? "未选择知识库" : KbPath;

        partial void OnKbPathChanged(string value) => OnPropertyChanged(nameof(KbPathText));

        /// <summary>当前库待处理变更数（顶部状态行与增量卡状态的真实来源）。</summary>
        [ObservableProperty]
        private int _pendingCount;

        /// <summary>主卡片（1-6，双列网格）。</summary>
        [ObservableProperty]
        private ObservableCollection<PromptCard> _mainCards = new();

        /// <summary>Clean 卡（横跨双列，设计文档 04§2）。</summary>
        [ObservableProperty]
        private PromptCard? _cleanCard;

        /// <summary>源文档页"生成选中 Ingest"跳转：文件清单写入批量卡输入并立即生成。</summary>
        private void ProcessPendingIngest()
        {
            var files = _mainViewModel.PendingIngestFiles;
            if (string.IsNullOrWhiteSpace(files))
            {
                return;
            }

            var card = MainCards.FirstOrDefault(c => c.Title == "Ingest（批量）");
            if (card != null)
            {
                card.Input = files;
            }
            GenerateCard("Ingest（批量）");
        }

        private void OnKbChanged(string kb)
        {
            SelectedKb = kb;
            if (string.IsNullOrWhiteSpace(kb))
            {
                KbPath = string.Empty;
                PendingCount = 0;
                MainCards.Clear();
                CleanCard = null;
                return;
            }

            KbPath = _fileBridge.KbRoot(kb);
            BuildPromptCards();
        }

        /// <summary>刷新当前库待处理变更数并同步增量卡状态（设计文档 04§4.3）。</summary>
        public void RefreshPendingCount()
        {
            PendingCount = string.IsNullOrWhiteSpace(SelectedKb)
                ? 0
                : _fileBridge.Changes(SelectedKb).Count;

            var incremental = MainCards.FirstOrDefault(c => c.IsIncremental);
            if (incremental != null)
            {
                incremental.CanCopy = PendingCount > 0;
                incremental.StatusText = PendingCount > 0
                    ? $"当前待处理变更：{PendingCount} 项"
                    : "暂无待处理变更";
            }
        }

        private void BuildPromptCards()
        {
            MainCards.Clear();
            CleanCard = null;
            if (string.IsNullOrWhiteSpace(KbPath))
            {
                return;
            }

            var kbPath = KbPath;
            var kb = SelectedKb;
            var i = 1;

            MainCards.Add(new PromptCard(
                i++, "边界说明",
                "定义 Agent 对本知识库的读写边界与行为规范。粘贴进 Agent 配置（全局 AGENTS.md 等）后可直接读取本库。",
                BuildBoundaryPrompt(kbPath, kb)));

            // 批量 Ingest：输入移入卡内（设计文档 04§2），输入变化即时重生成预览
            MainCards.Add(new PromptCard(
                i++, "Ingest（批量）",
                "将指定源文档批量整理入 Wiki；留空=全部源文档。",
                BuildIngestPrompt(kbPath, false, null),
                showInput: true,
                inputPlaceholder: "文件名，逗号分隔；留空=全部源文档",
                regen: input => BuildIngestPrompt(kbPath, false, ParseFiles(input))));

            MainCards.Add(new PromptCard(
                i++, "Ingest（增量）",
                "仅处理 .changes.json 中标记的待处理变更文件。",
                BuildIngestPrompt(kbPath, true, null),
                isIncremental: true));

            MainCards.Add(new PromptCard(
                i++, "Query",
                "基于知识库内容回答问题，要求带来源引用。",
                BuildQueryPrompt(kbPath, ""),
                showInput: true,
                inputPlaceholder: "输入你的问题，如：星尘X9的售价和续航？",
                requireInput: true,
                regen: input => BuildQueryPrompt(kbPath, input)));

            MainCards.Add(new PromptCard(
                i++, "Lint",
                "检查知识库文档的格式、链接有效性、引用规范。",
                BuildLintPrompt(kbPath)));

            MainCards.Add(new PromptCard(
                i++, "Compare",
                "对比多个源文档的差异与共同点。",
                BuildComparePrompt(kbPath, ""),
                showInput: true,
                inputPlaceholder: "输入对比主题，如：Cursor vs Copilot",
                requireInput: true,
                regen: input => BuildComparePrompt(kbPath, input)));

            CleanCard = new PromptCard(
                i, "Clean（级联清理）",
                "删除已废弃源文档的摘要页、index 条目与失效 [[wikilinks]]。",
                BuildCleanPrompt(kbPath));

            RefreshPendingCount();
        }

        /// <summary>解析批量 Ingest 输入：逗号分隔 + 前缀归一化（避免 raw/sources/ 双前缀）。</summary>
        private static List<string> ParseFiles(string input)
            => string.IsNullOrWhiteSpace(input)
                ? new List<string>()
                : input
                    .Split(new[] { ',', '，', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeSourceName)
                    .Where(f => f.Length > 0)
                    .ToList();

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

        private static string BuildQueryPrompt(string kbPath, string question)
        {
            var q = string.IsNullOrWhiteSpace(question) ? "（待填写：请在卡片输入框输入问题后复制）" : question.Trim();
            var sb = new StringBuilder();
            sb.AppendLine("# Query（" + kbPath + "）");
            sb.AppendLine();
            sb.AppendLine("你是一个基于知识库的问答 Agent，目标路径：" + kbPath);
            sb.AppendLine();
            sb.AppendLine("## 我的问题");
            sb.AppendLine(q);
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

        private static string BuildComparePrompt(string kbPath, string topic)
        {
            var t = string.IsNullOrWhiteSpace(topic) ? "（待填写：请在卡片输入框输入对比主题后复制）" : topic.Trim();
            var sb = new StringBuilder();
            sb.AppendLine("# Compare（" + kbPath + "）");
            sb.AppendLine();
            sb.AppendLine("你是一个知识库对比 Agent，目标路径：" + kbPath);
            sb.AppendLine();
            sb.AppendLine("## 对比主题");
            sb.AppendLine(t);
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

        /// <summary>按卡片标题重新生成提示词（生成器与复制共用，保证内容一致）。</summary>
        private string Regenerate(PromptCard card) => card.Title switch
        {
            "边界说明" => BuildBoundaryPrompt(KbPath, SelectedKb),
            "Ingest（批量）" => BuildIngestPrompt(KbPath, false, ParseFiles(card.Input)),
            "Ingest（增量）" => BuildIngestPrompt(KbPath, true, null),
            "Query" => BuildQueryPrompt(KbPath, card.Input),
            "Lint" => BuildLintPrompt(KbPath),
            "Compare" => BuildComparePrompt(KbPath, card.Input),
            "Clean（级联清理）" => BuildCleanPrompt(KbPath),
            _ => card.PromptContent
        };

        /// <summary>卡内"生成提示词"按钮：按当前输入重建预览（设计文档 04§3）。</summary>
        [RelayCommand]
        private void GenerateCard(string cardName)
        {
            var card = FindCard(cardName);
            if (card == null)
            {
                return;
            }

            if (card.Title == "Ingest（增量）")
            {
                RefreshPendingCount();
            }

            card.PromptContent = Regenerate(card);
        }

        [RelayCommand]
        private void CopyPrompt(string cardName)
        {
            var card = FindCard(cardName);
            if (card == null)
            {
                return;
            }

            if (!card.CanCopy)
            {
                // 增量卡 0 待处理 / Query、Compare 未输入：禁用态下不该到达，兜底提示（不虚假复制）
                _toastService.ShowWarning(card.StatusText.Length > 0 ? card.StatusText : "请先完成卡片输入");
                return;
            }

            // 复制前按当前输入重新生成，保证剪贴板内容与输入一致（设计文档 04§5）
            var content = Regenerate(card);
            if (content != card.PromptContent)
            {
                card.PromptContent = content;
            }

            try
            {
                System.Windows.Clipboard.SetText(content);
                _toastService.ShowSuccess($"已复制：{card.Title}");
            }
            catch
            {
                _toastService.ShowError("复制剪贴板失败");
            }
        }

        private PromptCard? FindCard(string cardName)
            => MainCards.FirstOrDefault(c => c.Title == cardName)
               ?? (CleanCard != null && CleanCard.Title == cardName ? CleanCard : null);

        /// <summary>把当前库 .changes.json 的待处理文件名导入批量卡输入（设计文档 04§2）。</summary>
        [RelayCommand]
        private void ImportChanges()
        {
            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                _toastService.ShowWarning("请先选择知识库");
                return;
            }

            var names = _fileBridge.Changes(SelectedKb)
                .Select(c => NormalizeSourceName(c.File))
                .Where(n => n.Length > 0)
                .Distinct()
                .ToList();
            if (names.Count == 0)
            {
                _toastService.ShowWarning("当前没有待处理变更");
                return;
            }

            var card = MainCards.FirstOrDefault(c => c.Title == "Ingest（批量）");
            if (card == null)
            {
                return;
            }

            card.Input = string.Join(", ", names);
            GenerateCard(card.Title);
            _toastService.ShowSuccess($"已导入 {names.Count} 个待处理文件");
        }

        /// <summary>刷新工作流：重建卡片（重新读取目录/purpose/变更），保留输入不做硬性要求。</summary>
        [RelayCommand]
        private void RefreshWorkflow() => OnKbChanged(SelectedKb);

        /// <summary>归一化源文件名：变更记录存的是含 raw/sources/ 前缀的相对路径，提示词统一用纯文件名，避免双前缀。</summary>
        private static string NormalizeSourceName(string f)
        {
            var n = f.Trim().Replace('\\', '/');
            const string prefix = "raw/sources/";
            return n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? n[prefix.Length..] : n;
        }
    }

    /// <summary>
    /// 工作流提示词卡（设计文档 04§3 固定结构：编号+标题+说明+输入+生成/复制+可折叠预览）。
    /// 带输入的卡在输入变化时即时重生成预览（不自动复制/执行）。
    /// </summary>
    public partial class PromptCard : ObservableObject
    {
        public int Index { get; }
        public string Title { get; }
        public string Description { get; }

        [ObservableProperty]
        private string _promptContent;

        /// <summary>是否显示参数输入框（批量 Ingest / Query / Compare）。</summary>
        public bool ShowInput { get; }

        /// <summary>输入框占位文案。</summary>
        public string InputPlaceholder { get; }

        /// <summary>是否增量 Ingest 卡（显示待处理数，0 时禁用复制）。</summary>
        public bool IsIncremental { get; }

        /// <summary>需要用户输入后才能生成/复制（Query/Compare，设计文档 04§4.4、§4.6）。</summary>
        public bool RequireInput { get; }

        [ObservableProperty]
        private string _input = "";

        /// <summary>复制/生成是否可用。</summary>
        [ObservableProperty]
        private bool _canCopy = true;

        /// <summary>卡片附加状态文案（如"当前待处理变更：N 项"）。</summary>
        [ObservableProperty]
        private string _statusText = "";

        /// <summary>预览折叠区标题（含字数，随内容更新）。</summary>
        public string ContentSummary => $"提示词预览（{PromptContent.Length} 字）";

        private readonly Func<string, string>? _regen;

        public PromptCard(int index, string title, string description, string promptContent,
            bool showInput = false, string inputPlaceholder = "",
            bool requireInput = false, bool isIncremental = false,
            Func<string, string>? regen = null)
        {
            Index = index;
            Title = title;
            Description = description;
            _promptContent = promptContent;
            ShowInput = showInput;
            InputPlaceholder = inputPlaceholder;
            RequireInput = requireInput;
            IsIncremental = isIncremental;
            _regen = regen;
            UpdateCanCopy();
        }

        partial void OnInputChanged(string value)
        {
            // 输入变化即时重生成预览（设计文档 04§5：不自动复制、不自动执行）
            if (_regen != null)
            {
                PromptContent = _regen(value);
            }
            UpdateCanCopy();
        }

        partial void OnPromptContentChanged(string value)
            => OnPropertyChanged(nameof(ContentSummary));

        private void UpdateCanCopy()
        {
            if (RequireInput)
            {
                CanCopy = !string.IsNullOrWhiteSpace(Input);
                StatusText = CanCopy ? "" : "请先输入";
            }
        }
    }
}
