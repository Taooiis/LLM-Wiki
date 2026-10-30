using System.Collections.ObjectModel;
using System.Linq;

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
        private readonly WorkflowPromptService _prompts;
        private readonly ToastService _toastService;

        public WorkflowViewModel(
            MainWindowViewModel mainViewModel,
            FileBridge fileBridge,
            WorkflowPromptService prompts,
            ToastService toastService)
        {
            _mainViewModel = mainViewModel;
            _fileBridge = fileBridge;
            _prompts = prompts;
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
                _prompts.BuildBoundary(kb, kbPath, kbPath)));

            // 批量 Ingest：输入移入卡内（设计文档 04§2），输入变化即时重生成预览
            MainCards.Add(new PromptCard(
                i++, "Ingest（批量）",
                "将指定源文档批量整理入 Wiki；留空=全部源文档。",
                _prompts.BuildIngest(kbPath, false),
                showInput: true,
                inputPlaceholder: "文件名，逗号分隔；留空=全部源文档",
                regen: input => _prompts.BuildIngest(kbPath, false, WorkflowPromptService.ParseFiles(input))));

            MainCards.Add(new PromptCard(
                i++, "Ingest（增量）",
                "仅处理 .changes.json 中标记的待处理变更文件。",
                _prompts.BuildIngest(kbPath, true),
                isIncremental: true));

            MainCards.Add(new PromptCard(
                i++, "Query",
                "基于知识库内容回答问题，要求带来源引用。",
                _prompts.BuildQuery(kbPath, ""),
                showInput: true,
                inputPlaceholder: "输入你的问题，如：星尘X9的售价和续航？",
                requireInput: true,
                regen: input => _prompts.BuildQuery(kbPath, input)));

            MainCards.Add(new PromptCard(
                i++, "Lint",
                "检查知识库文档的格式、链接有效性、引用规范。",
                _prompts.BuildLint(kbPath)));

            MainCards.Add(new PromptCard(
                i++, "Compare",
                "对比多个源文档的差异与共同点。",
                _prompts.BuildCompare(kbPath, ""),
                showInput: true,
                inputPlaceholder: "输入对比主题，如：Cursor vs Copilot",
                requireInput: true,
                regen: input => _prompts.BuildCompare(kbPath, input)));

            CleanCard = new PromptCard(
                i, "Clean（级联清理）",
                "删除已废弃源文档的摘要页、index 条目与失效 [[wikilinks]]。",
                _prompts.BuildClean(kbPath));

            RefreshPendingCount();
        }

        /// <summary>按卡片标题重新生成提示词（生成器与复制共用，保证内容一致）。</summary>
        private string Regenerate(PromptCard card) => card.Title switch
        {
            "边界说明" => _prompts.BuildBoundary(SelectedKb, KbPath, KbPath),
            "Ingest（批量）" => _prompts.BuildIngest(KbPath, false, WorkflowPromptService.ParseFiles(card.Input)),
            "Ingest（增量）" => _prompts.BuildIngest(KbPath, true),
            "Query" => _prompts.BuildQuery(KbPath, card.Input),
            "Lint" => _prompts.BuildLint(KbPath),
            "Compare" => _prompts.BuildCompare(KbPath, card.Input),
            "Clean（级联清理）" => _prompts.BuildClean(KbPath),
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
                .Select(c => WorkflowPromptService.NormalizeSourceName(c.File))
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
