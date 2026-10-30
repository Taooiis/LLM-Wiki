# 14｜WPF-UI 4.3.0 组件与页面实现规范

> 本章明确每个页面使用的控件、公共组件和实现边界。WPF-UI 指 lepoco 的 Wpf.Ui，不是 WinUI/UWP；禁止混用它们的同名 API。控件名称已对照 [官方 4.3.0 源码目录](https://github.com/lepoco/wpfui/tree/4.3.0/src/Wpf.Ui/Controls) 核对，包版本见 [WPF-UI 4.3.0](https://www.nuget.org/packages/WPF-UI/4.3.0)。在线 API 文档可能跟随版本更新，编译依据是项目锁定的 4.3.0 程序集。

## 1. 组件选用优先级与包约束

顺序固定：**项目已有具体业务组件 → 项目已有公共封装 → WPF-UI 4.3.0 控件 → 必要的 WPF 原生控件/布局 → 最小新增封装**。下面的选型是缺少现有封装时的基准，不能据此替换已经成熟的同类业务组件。

桌面 UI 使用 WPF-UI 4.3.0 和项目已有依赖；相关 WPF-UI 扩展包按实际使用并锁定兼容版本，不为凑表安装所有包。不引入 HandyControl、MahApps.Metro、MaterialDesignThemes 等第二套 UI 体系，不引入 WebView2。MVVM 与 DI 复用现有项目框架。

下文 `ui:` 指 Wpf.Ui.Controls；`wpf:` 指 System.Windows.Controls。Grid、Border、DockPanel、GridSplitter 等布局可直接使用 WPF 原生类型，并统一样式资源。

## 2. 主框架与通用控件表

| 界面对象 | 首选控件或封装 | 必须实现的行为 |
|---|---|---|
| 主窗口 | ui:FluentWindow | 窗口尺寸、主题、正常退出保护、本机任务关闭协调 |
| 标题栏 | ui:TitleBar | 拖动区与按钮区分离，系统最小化/最大化/关闭 |
| 左侧导航 | ui:NavigationView + NavigationViewItem | 当前项、折叠持久化、底部设置入口；离开保护通过后才真正跳页 |
| 页面承载 | 现有导航服务 + Page/Frame | 避免额外再建一套导航栈；库上下文变化取消旧请求 |
| 库切换器 | 项目 KnowledgeBasePicker；缺失则 ui:DropDownButton + 列表面板 | 可搜索库名、当前库、建库；不能直接用未保护的 SelectedItem 切库 |
| 全局文件名查找 | ui:AutoSuggestBox + 分组结果模板 | Ctrl+K、键盘选中、空态、打开保护；可自定义结果模板而非声称内置分组业务 |
| 图标 | ui:SymbolIcon | Fluent 一致图标，Tooltip 与 AutomationProperties.Name |
| 普通/主操作按钮 | ui:Button，更多操作用 DropDownButton/ContextMenu | 使用命令与 CanExecute；处理中防重复，无未实现的空按钮 |
| 成功/短消息 | ui:Snackbar + SnackbarPresenter | 主窗口统一宿主；“撤销”动作需项目封装接命令，不猜测库自带业务能力 |
| 页面长期提醒 | ui:InfoBar | 规则变化、待补记、连接只读、执行异常，不依赖短 Toast |
| 确认/未保存对话框 | ui:ContentDialog + 全局对话框协调器 | 同时只出现一个；正常确认不弹框，危险操作按规范确认 |
| 状态条 | ui:StatusBar 或现有封装 | 当前库、目录状态、运行任务返回入口、分享绑定库 |

NavigationView 的职责参考 [官方导航说明](https://wpfui.lepo.co/documentation/navigation-view.html)；对话框与通知承载参考 [ContentDialog](https://wpfui.lepo.co/api/Wpf.Ui.Controls.ContentDialog.html)、[SnackbarPresenter](https://wpfui.lepo.co/api/Wpf.Ui.Controls.SnackbarPresenter.html)。这些是基础控件，库业务和权限仍由项目服务负责。

## 3. 按页面落地

| 页面/区域 | 基础控件 | 项目层复用/新增组件 | 注意事项 |
|---|---|---|---|
| 知识：目录 | ui:TreeView + TextBox；布局用 Grid/GridSplitter | KnowledgeTree | 中文分类别名、展开恢复、来源定位，不改磁盘目录 |
| 知识：正文 | 既有 Markdown 预览；缺失时 wpf:FlowDocumentScrollViewer 承载转换后的 FlowDocument | DocumentWorkspace + MarkdownPreview | 解析器复用项目现有实现，控件本身不提供 Markdown 解析 |
| 资料：文件列表 | ui:ListView；需要多列时 ui:DataGrid | SourceList + FileTypeBadge | 复选与行预览分离，筛选外选择提示，按源文件计数 |
| 资料：文本编辑 | 既有编辑器封装；缺失时 ui:TextBox 多行起步 | DocumentWorkspace + DocumentToolbar | Ctrl+S、脏状态、外部冲突、保存后继续编辑；不强制引入新编辑库 |
| 资料：非文本 | ui:Card/Border、SymbolIcon、TextBlock、Button | SourceFilePreview | 无预览器时原格式信息卡与系统打开，不用 RichTextBox 读取 docx/xlsx 二进制 |
| 资料：回收站 | ui:DataGrid + TextBox + Buttons | SourceRecycleBin | 恢复、另存名称、导出、危险永久清理；放在资料页内部，不新导航 |
| 整理：任务切换 | ui:TabControl + wpf:TabItem + ComboBox/RadioButton | TaskComposer | 更新/分析/维护共用上下文；选项不能混淆入口与访问方式 |
| 整理：待整理清单 | ui:DataGrid + CheckBox | PendingChangeList | 版本快照、选择数、冲突项、规则提醒独立展示 |
| 整理：指令预览 | ui:Expander + 只读 TextBox | TaskInstructionPreview | 保持换行、可选复制，预览不自动执行，不暴露令牌 |
| 整理：运行中 | ui:ProgressRing/ProgressBar + InfoBar + Expander | AgentRunPanel | 无真实百分比用不确定进度；取消只作用本次执行 |
| 整理：结果核对 | ui:DataGrid + Expander + Buttons | TaskResultReview | 文件变化与资料完成分开；选中后一次提交，无重复确认框 |
| 连接与分享 | ui:TabControl、CardExpander、ToggleSwitch、NumberBox、PasswordBox | BrowserSharePanel + AgentToolSettings + McpConnectionPanel | 浏览开关、MCP 开关、本机配置独立；令牌默认掩码、不绑定日志 |
| 知识库设置 | ui:TabControl、TextBox、Button、InfoBar | KnowledgeBaseSettings + RuleEditor | 两文件独立保存状态，危险删库输入库名 |
| 应用设置 | ui:CardExpander、RadioButton、TextBox、Button | StorageSettings + AppearanceSettings | 路径校验不阻塞 UI；浏览调用系统选择器 |
| 使用帮助 | ui:Expander、HyperlinkButton、TextBlock | HelpTopicPanel | 中文步骤、实际按钮名、就地返回原任务 |

以上项目层组件名是职责名称，实施时先映射现有类名；已有同功能封装直接复用。ui:DataGrid、TreeView 等仍需项目配置模板与行为，不能当作现成业务组件。

## 4. 文档工作区与状态绑定

DocumentWorkspace 输入明确的库身份、相对路径、文档角色和允许动作；不只靠扩展名决定可写。编辑缓冲、原内容和文件版本独立管理，不在页面 View 里直接写文件。

工具栏提供文件名、路径、状态、编辑/保存/完成编辑；CanSave 由 Dirty、权限、版本和 Busy 综合决定。预览/编辑切换复用同区域，禁止为主要编辑任务弹小窗口。Office 原格式文件的编辑动作是打开关联程序，不假装应用内保存。

内容区独立滚动；左右栏宽度允许拖动。大列表使用现有虚拟化方案，避免外层 ScrollViewer 破坏虚拟化。高亮和文件加载异步执行；查询结果与当前上下文版本不匹配则丢弃。

## 5. 本机 Agent 输出与交互限制

AgentRunPanel 用只读文本/虚拟化行列表展示适配器事件，不嵌入终端模拟器，不执行 ANSI/脚本，不新增聊天输入框。UI 默认保留最近 5,000 行且最多 1 MiB 展示文本，任一上限达到就丢弃最早显示内容并标注“较早输出已省略”；任务状态和确认记录不能随显示截断而丢失。

没有真实事件时显示“运行中，等待外部工具响应”，不虚构正在读哪个文件。需要外部审批时只有适配器明确报告才显示对应入口。工具选择与程序路径属于结构化配置，不提供任意脚本编辑框。

运行中、等待取消、待核对、异常结束都在页面持久呈现，Snackbar 只报告短结果。取消、登录、提升授权、退出运行中应用的交互按第 13 章，不用 MessageBox 堆叠。

## 6. 系统能力不伪装成 WPF-UI 控件

- 文件多选、导出、目录选择、程序路径选择使用系统选择器或已有项目封装，复用明确路径校验。
- 原格式文件打开通过系统关联程序；缺少关联时提示选择程序或导出，不能影响文件已保存状态。
- Markdown、Word、Excel 解析不由 WPF-UI 负责，按数据/读取服务实现；不增加转换落盘。
- QR 码、剪贴板、进程管理、凭据保护、文件监测、哈希和回收站都是服务职责，控件只绑定状态与命令。
- 不引入 Office 嵌入控件/COM 自动化作为默认预览前提；缺少现有预览时使用原格式信息卡。

## 7. 主题资源与样式

| 资源 | 深色 | 浅色 |
|---|---|---|
| AppBackground | #202020 | #F3F3F3 |
| NavigationBackground | #1C1C1C | #EEEEEE |
| LayerBackground | #242424 | #F8F8F8 |
| CardBackground | #2B2B2B | #FFFFFF |
| ControlBackground | #1F1F1F | #FFFFFF |
| Stroke | #454545 | #D6D6D6 |
| TextPrimary | #F5F5F5 | #1A1A1A |
| TextSecondary | #C5C5C5 | #5C5C5C |
| AccentStrong | #0078D4 | #0078D4 |
| Success | #6CCB5F | #0F7B0F |
| Warning | #FCE100 | #9A6700 |
| Danger | #FF6B6B | #C42B1C |

语义 Token 是项目资源键，不冒充 WPF-UI 内置键；由资源适配层映射到锁定版本支持的画刷与样式。使用动态资源，不每页硬编码。主按钮文字、悬停、禁用、焦点和选中背景分别验对比度；蓝色正文链接与蓝色实心按钮不要共用一个文字色。

禁止紫色渐变、发光、第二套主题体系。保留 WPF-UI 原生交互与键盘焦点，不为还原概念图重写全部控件模板。

## 8. 开发前控件盘点与交付

阶段 0 必须形成“已有控件 → 本章职责 → 复用/补充项”清单，确认包为 4.3.0，并在真实项目编译最小主窗、导航、对话框、Snackbar 和所用列表控件。相关属性、事件、模板以程序集和官方对应源码为准。

逐页交付实际命令绑定、深浅主题、键盘操作、空/错/忙/只读状态与截图。WPF-UI 控件存在不代表按钮已接业务；没有源码与运行环境时只能完成设计检查，不能勾选构建通过。
