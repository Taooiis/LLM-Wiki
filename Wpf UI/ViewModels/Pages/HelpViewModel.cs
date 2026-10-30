using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages;

/// <summary>使用帮助：离线主题列表 + 本机/复制两种执行说明 + 就地跳转与返回原任务。</summary>
public partial class HelpViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public HelpViewModel(MainWindowViewModel main)
    {
        _main = main;
        Topics = BuildTopics();
        FilteredTopics = CollectionViewSource.GetDefaultView(Topics);
        FilteredTopics.Filter = o => o is HelpTopic topic && Matches(topic, SearchKeyword);
        SelectedTopic = Topics.FirstOrDefault();
    }

    public ObservableCollection<HelpTopic> Topics { get; }
    public ICollectionView FilteredTopics { get; }

    [ObservableProperty] private HelpTopic? _selectedTopic;
    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private int _readMode;
    [ObservableProperty] private bool _hasReturnContext;
    [ObservableProperty] private string _returnLabel = "";

    public int FilteredCount => FilteredTopics.Cast<object>().Count();
    public bool HasSearchResult => FilteredCount > 0;
    public bool ShowModeTabs => SelectedTopic?.HasExecutionModes == true;
    /// <summary>快速上手的就地提示条（07）：本机方式激活时提示“也可手动复制”，点击切换到复制方式。</summary>
    public bool ShowCopyHint => ShowModeTabs && ReadMode == 0;
    public string ModeHint => ReadMode switch
    {
        0 => "本机执行：连接与分享 → AI 工具，检测并实际执行验证通过后，整理页直接开始，无需再粘贴。",
        _ => "手动复制：整理页生成完整任务后复制，到能访问该目录的外部 AI 中执行；返回后用“我已在外部执行，核对结果”进入核对。",
    };

    partial void OnSearchKeywordChanged(string value)
    {
        FilteredTopics.Refresh();
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(HasSearchResult));
        if (!Matches(SelectedTopic, value)) SelectedTopic = FilteredTopics.Cast<HelpTopic>().FirstOrDefault();
    }

    partial void OnSelectedTopicChanged(HelpTopic? value)
    {
        ReadMode = 0;
        OnPropertyChanged(nameof(ShowModeTabs));
        OnPropertyChanged(nameof(ShowCopyHint));
        OnPropertyChanged(nameof(ModeHint));
    }

    partial void OnReadModeChanged(int value)
    {
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(ShowCopyHint));
    }

    [RelayCommand]
    private void ClearSearch() => SearchKeyword = "";

    /// <summary>就地提示条动作（07§2）：切到“手动复制”方式，不跳页。</summary>
    [RelayCommand]
    private void ShowCopyMode() => ReadMode = 1;

    public void RefreshFromNavigation()
    {
        if (!string.IsNullOrWhiteSpace(_main.HelpTopicId) &&
            Topics.FirstOrDefault(t => t.Id == _main.HelpTopicId) is { } topic)
        {
            SelectedTopic = topic;
        }
        HasReturnContext = !string.IsNullOrWhiteSpace(_main.HelpReturnPageKey);
        ReturnLabel = HasReturnContext ? "返回" + PageLabel(_main.HelpReturnPageKey) : "";
        _main.ClearHelpRequest();
    }

    [RelayCommand]
    public async Task ReturnAsync()
    {
        var key = _main.HelpReturnPageKey;
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!await _main.NavigateWithGuardAsync(key)) return;
        _main.ClearHelpContext();
        OnPropertyChanged(nameof(HasReturnContext));
        ReturnLabel = "";
    }

    public async Task NavigateAsync(string pageKey)
    {
        await _main.NavigateWithGuardAsync(pageKey);
    }

    public void MarkVisited() => _main.RememberPage("help");

    private static bool Matches(HelpTopic? topic, string keyword)
    {
        if (topic == null) return false;
        var query = (keyword ?? "").Trim();
        if (query.Length == 0) return true;
        return topic.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || topic.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase)
               || topic.Summary.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string PageLabel(string key) => key switch
    {
        "knowledge" => "知识页",
        "sources" => "资料页",
        "organize" => "整理页",
        "connections" => "连接与分享",
        "kbsettings" => "知识库设置",
        "settings" => "应用设置",
        _ => "原页面",
    };

    private static ObservableCollection<HelpTopic> BuildTopics() =>
    [
        new("quick-start", "快速上手",
            "开始 建库 添加资料 整理 核对 第一次",
            "从创建知识库到确认整理结果：资料保持原格式，外部 AI 生成知识，你核对完成。",
            [
                new("你需要知道的边界", [
                    "应用不内置模型、聊天或 API Key；整理由外部 AI 工具通过文件访问或 MCP 执行。",
                    "知识必须先“整理”才会出现；保存资料成功不代表知识已更新。",
                ]),
            ],
            true,
            [
                "顶栏库选择器 → 新建知识库，填写名称；模板只影响目标与规则的默认文字。",
                "进入“资料”，点“添加文件”，按类型选择文本、Word/PDF、表格、演示、JSON 或图片；单文件上限 20 MiB，原格式保存。",
                "进入“连接与分享 → AI 工具”，选择原生 codex.exe，检测安装与登录，并完成“验证实际执行”。",
                "进入“整理”，选择范围，点“开始整理”；应用会把完整任务自动传给工具，不需要再粘贴。",
                "外部程序结束后回到“整理”，查看本次知识变化与逐项版本状态。",
                "勾选你已核对的资料，点“确认完成这 n 项”；未完成项可继续保留或结束任务。",
            ],
            [
                "顶栏库选择器 → 新建知识库，填写名称。",
                "进入“资料”，添加要整理的原始文件；支持文本、Word/PDF、表格、演示、JSON 与常见图片，保持原格式。",
                "进入“整理”，选择范围，点“复制整理指令”。库身份、规则入口、范围和边界已经包含在任务里。",
                "到能访问该目录的外部 AI 工具中粘贴执行。复制成功只表示已复制，不代表已执行或完成。",
                "回到“整理”，点“我已在外部执行，核对结果”或“查看本次变化”。",
                "勾选已核对项并一次确认；剩余项继续保留，不会被自动清除。",
            ],
            [("资料页", "sources"), ("整理页", "organize"), ("连接与分享", "connections")]),

        new("sources", "日常资料管理",
            "上传 添加 预览 编辑 保存 PDF Word Excel PPT 图片 回收站 删除",
            "添加保留原字节；预览、编辑与 AI 读取能力分开判断。移入回收站可恢复，永久清空需要明确确认。",
            [
                new("添加与覆盖", [
                    "支持 .md .markdown .txt .log、Word（.docx/.docm/.doc/.rtf/.odt/.wps）、表格（.xlsx/.xlsm/.xls/.csv/.tsv/.ods/.et）、演示（.pptx/.pptm/.ppt/.odp/.dps）、PDF、结构化数据（.json/.jsonl/.xml/.yaml/.yml/.toml/.ini）和常见图片。",
                    "同名上传会先询问覆盖、跳过或取消；覆盖前旧版本保留到回收站，无法保留时拒绝覆盖。",
                    "单个文件上限 20 MiB；多选逐文件提交，失败项单独说明，不影响其他文件。",
                ]),
                new("预览与编辑", [
                    "文本与结构化数据可在 1 MiB 内按原文编辑；保存失败保留草稿和原因。",
                    "DOCX/XLSX/PPTX 提供内容文字预览；PDF 提供文字页预览，扫描页会提示需要 OCR 或视觉读取；图片按解码器静态预览。",
                    "其他旧版 Office 或无法解析的文件显示原件卡，用“打开原件”交给系统程序。预览失败不影响文件已入库。",
                ]),
                new("回收站", [
                    "“移入回收站”可撤销；也可以在“更多 → 回收站”恢复或永久清理。",
                    "恢复遇同名文件不会覆盖，会要求另存新名称；资料恢复不会自动恢复已清理的知识。",
                    "回收站保留到手动清理；永久清空同时删除库内回收内容，无法通过本应用恢复。",
                ]),
            ],
            false, null, null,
            [("去资料页", "sources")]),

        new("organize", "整理与结果核对",
            "任务 复制 本机 确认 部分完成 待核对 零变化",
            "任务快照冻结范围与版本；复制或进程结束都不自动确认。逐项核对一次提交，剩余项继续待整理。",
            [
                new("准备任务", [
                    "更新知识支持待整理变更、所选资料、全部资料三种范围；默认不勾选。",
                    "提问分析的问题或主题必填；检查维护的清理任务先生成计划，不直接删除。",
                    "规则更新会使旧指令过期，但已执行的旧任务仍可核对；提醒不能被部分任务清除。",
                ]),
                new("核对与确认", [
                    "“确认完成”表示你已核对结果，不证明知识质量合格；只移出仍匹配的记录。",
                    "确认时会重新比较源版本：有新变化或已删除的项不会被清除，会逐项返回原因。",
                    "零知识变化也可以填写理由后确认；其他方式已完整处理的项，在同一面板一次提交。",
                    "确认成功后刷新或重启不会回流；新版本仍然保留待整理。",
                ]),
                new("部分完成与结束", [
                    "只确认完成项；剩余项可继续核对，或“结束本任务，剩余继续待整理”。",
                    "误确认可在任务详情“重新列为待整理当前版本”，不会回滚知识。",
                    "本机运行、失败或取消不自动确认；取消不会立即杀进程，强制结束需明确确认并核对归属。",
                ]),
            ],
            false, null, null,
            [("去整理页", "organize"), ("去连接与分享", "connections")]),

        new("local-agent", "本机 AI 工具",
            "Codex CLI 本机 自动执行 检测 登录 验证 Ready 权限",
            "保存路径不等于可用；安装、登录、实际执行与格式能力分别验证。模型和账户仍由外部工具管理。",
            [
                new("配置与验证", [
                    "工具类型为内置 Codex CLI 适配器；只选择真实原生 codex.exe，不填任意 shell 模板。",
                    "检测安装与登录：执行 --version、exec --help 和 login status；不发送整理任务。",
                    "验证实际执行：在隔离临时目录运行一次非交互测试，会使用外部工具已有账号和额度。",
                    "只有安装、登录、实际执行都通过，才显示本机 Ready；保存路径本身不构成 Ready。",
                ]),
                new("权限与格式", [
                    "只读权限可做分析与检查；写回知识需明确开启“当前库目录写操作”，这是文件系统权限，不能强制限于 wiki/。",
                    "TXT/MD 通过不等于 Word/Excel/PDF/PPT/图片通过；格式能力分开记录，读取器或工具版本变化后需重验。",
                    "程序路径、库路径和中文内容通过参数数组与 stdin 传递；不需要操作外部窗口或粘贴。",
                ]),
                new("仍需粘贴时", [
                    "当前是“手动复制”入口，或本机适配尚未 Ready。到“连接与分享 → AI 工具”查看检测分项，不要相信仅保存路径。",
                    "也可继续使用复制指令兜底，任务内容完全相同。",
                ]),
            ],
            false, null, null,
            [("去连接与分享", "connections")]),

        new("connections", "连接与分享",
            "浏览 分享 PIN 局域网 MCP 只读 写入 令牌 二维码",
            "浏览分享与 MCP 是两种独立访问方式，各自启停、绑定单库；切换桌面当前库不会改变服务范围。",
            [
                new("浏览分享", [
                    "启动后显示真实本机与局域网地址、4 位访问码；其他设备可浏览、预览和下载原件，不能上传、编辑或删除。",
                    "更换访问码会使旧码和已认证浏览会话失效；更换分享库需先停止并更换范围。",
                    "看不到局域网地址时，先确认服务已运行、设备同网；不要为此关闭全部防火墙。",
                ]),
                new("MCP 连接", [
                    "MCP 默认关闭、默认只读、绑定单库；开启连接不等于发送任务，也不自动确认源变更。",
                    "写入需显式授权并使用独立写入令牌，只允许修改本库 wiki/ 下的文本，禁止资料、规则、日志和删除；重启应用后恢复只读。",
                    "局域网监听默认关闭，开放仅限可信网络，仍需令牌；不会连带开启写入。",
                    "stdio 由外部客户端启动，退出桌面应用不一定停止该进程；授权库通过 --kb 参数指定。",
                ]),
                new("客户端配置", [
                    "HTTP 与 stdio 配置片段使用真实地址和令牌；复制后需按目标客户端格式调整并实际验证。",
                    "本页显示“最近受控调用”只来自真实请求记录，不伪造连接数或设备。",
                ]),
            ],
            false, null, null,
            [("去连接与分享", "connections")]),

        new("storage", "存储与备份",
            "根目录 路径 UNC 切换 主题 配置 备份",
            "根目录只决定读取位置，不搬迁数据；主题切换不重建业务内容。共享目录由 Windows 管理权限。",
            [
                new("更换根目录", [
                    "输入新路径后先异步校验；未变化时“应用路径”禁用，合法变化才可应用。",
                    "应用前会处理所有未保存草稿，并提示将停止的浏览分享/HTTP MCP 服务；确认只提交一次。",
                    "失败时原路径继续生效；已停止的服务保持关闭，可手动重开。",
                    "只更换读取位置，不搬迁、合并或删除原目录；回旧根即可找回原库，不要新建同名空库。",
                ]),
                new("主题与偏好", [
                    "深色/浅色立即生效并持久化；切换会刷新已有弹框、通知、菜单与各格式预览。",
                    "主题已应用但偏好保存失败时会显示页内提醒，不会假称已保存。",
                ]),
                new("备份与恢复", [
                    "直接复制整个知识库目录即可备份资料、Wiki 与内部任务记录；回收站也位于库内。",
                    "正常离开有未保存保护；崩溃未保存草稿不保证恢复，任务快照会跨重启保留。",
                ]),
            ],
            false, null, null,
            [("去应用设置", "settings"), ("去知识库设置", "kbsettings")]),

        new("feedback", "认识弹框与通知",
            "提示 通知 弹框 未保存 Esc 撤销 永久删除 关闭",
            "蓝色/绿色短消息是结果；带操作入口的页内提醒持续存在；关闭提示不等于解决业务问题。",
            [
                new("反馈层级", [
                    "短消息（右下角）：已保存、已复制、回收撤销等短暂结果，自动消失，可关闭。",
                    "页内提醒：保存了但记录未完成、规则更新、任务待核对等尚未解决的业务状态，持续保留直到真正处理。",
                    "决策框：未保存离开、覆盖、版本冲突、权限或范围变化、永久删除等，需要你明确选择。",
                ]),
                new("键盘与安全", [
                    "未保存框按 Esc 或关闭表示“留在原处”，不会丢草稿；默认焦点在留在原处。",
                    "永久删除必须明确点击危险按钮并输入库名，输入框按 Enter 不会触发删除。",
                    "移入回收站不弹确认，可撤销；撤销过期后仍可从回收站恢复，不等于永久删除。",
                    "关闭一条提示只关闭提示，不会撤销已落盘的保存、任务确认或回收条目。",
                ]),
            ]),

        new("faq", "常见问题",
            "问题 上传 不变 待核对 仍需粘贴 PDF 预览 记录失败 误删 路径 手机 规则",
            "每个问题都给操作出口，不把失败解释成“稍后再试”。",
            [
                new("常见问题", [
                    "上传后知识不变：去整理，复制或本机执行；保存资料成功不等于 AI 已完成。",
                    "任务一直待核对：主动进入核对；零变化可填理由确认；部分完成可结束任务保留剩余。",
                    "本机仍需粘贴：当前是复制模式或适配未通过；到 AI 工具查看安装/登录/实际执行分项。",
                    "PDF/Word/表格/PPT 无法预览：打开原文件；已保存状态不受预览失败影响。扫描 PDF 或图片需单独验证 OCR/视觉读取。",
                    "文件已保存但变更记录失败：重试登记，不重复上传；补记成功后移除提醒。",
                    "误删资料：在资料页“更多 → 回收站”恢复；同名另存恢复，已清理的知识需重新整理。",
                    "切换路径后内容不见：新根不搬迁数据；回旧根，不要新建同名空库。",
                    "手机无法访问：检查服务是否运行、设备是否同网、使用显示的真实局域网地址与访问码；不要为此关闭全部防火墙。",
                    "规则保存后旧知识不变：按新规则核对后，在规则提醒中显式确认；不会自动重写知识。",
                ]),
            ]),
    ];
}

public sealed record HelpSection(string Heading, IReadOnlyList<string> Lines);
public sealed record HelpLink(string Label, string PageKey)
{
    public static implicit operator HelpLink((string Label, string PageKey) pair) => new(pair.Label, pair.PageKey);
}

public sealed class HelpTopic(
    string id,
    string title,
    string keywords,
    string summary,
    IReadOnlyList<HelpSection> sections,
    bool hasExecutionModes = false,
    IReadOnlyList<string>? localSteps = null,
    IReadOnlyList<string>? copySteps = null,
    IReadOnlyList<HelpLink>? links = null)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Keywords { get; } = keywords;
    public string Summary { get; } = summary;
    public IReadOnlyList<HelpSection> Sections { get; } = sections;
    public bool HasExecutionModes { get; } = hasExecutionModes;
    public IReadOnlyList<string> LocalSteps { get; } = localSteps ?? [];
    public IReadOnlyList<string> CopySteps { get; } = copySteps ?? [];
    public IReadOnlyList<HelpLink> Links { get; } = links ?? [];
}
