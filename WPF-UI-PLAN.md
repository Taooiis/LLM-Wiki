# WPF UI Library 完全 WPF 化改造计划（重写版：放弃 WebView2，全 WPF 控件）

> 状态：**计划阶段**（详细计划已写入本文件，等待批准后进入 implementation plan / 实现）
> 来源：WPF-UI-PLAN.md 旧版（WebView2 保留）→ 本次重写为"完全 WPF 化 + 放弃 WebView2"
> 分支：feature/file-association（当前基线）

## 一、背景与目标

1. **现状**：原 LLM Wiki 桌面客户端（仓库 `code/`）宿主为 C# WPF + `Microsoft.Web.WebView2` 渲染 Web 前端（`wwwroot/index.html` + `bridge.js`），实现知识库浏览/预览/编辑/上传/分享/提示词/文件关联等功能。
2. **迁移方向**：改用 **WPF UI Library（lepoco，NuGet `WPF-UI` 4.2.0 + `WPF-UI.DependencyInjection` 4.2.0）** 的原生控件，将全部界面从 WebView2 迁移为 WPF UI 控件 + WPF 原生控件（RichTextBox 等）。
3. **目标**：宿主应用 `Wpf UI/` 项目**彻底移除 WebView2**；C# 内核服务（KbService / Bridge / Share / Kestrel / mDNS / Upload / PromptEngine / FileAssociation 等）**不动**，只换 UI 呈现层。

## 二、已核实基线（2026-10-28 调研）

### 2.1 `Wpf UI/` 模板状态（迁移目标项目）

| 项 | 状态 |
|---|---|
| csproj | `net10.0-windows` / `WinExe` / `UseWPF` / `Nullable+ImplicitUsings`；包：`WPF-UI 4.2.0`、`WPF-UI.DependencyInjection 4.2.0`、`Microsoft.Extensions.Hosting 10.0.1`、`CommunityToolkit.Mvvm 8.4.0`；**无任何 WebView2 包引用** |
| App.xaml | `ThemesDictionary Theme="Light"` + `ControlsDictionary` |
| MainWindow.xaml | `FluentWindow`（`ExtendsContentIntoTitleBar=True`、`WindowBackdropType=Mica`、`WindowCornerPreference=Round`）+ Grid + `ui:NavigationView`（BreadcrumbBar + FooterMenuItemsSource + FrameMargin + IsBackButtonVisible + IsPaneToggleVisible + MenuItemsSource + PaneDisplayMode=LeftFluent + Header）+ ContentOverlay(`SnackbarPresenter`) + ContentPresenter(`RootContentDialog`) + `ui:TitleBar`（CloseWindowByDoubleClickOnIcon） |
| MainWindowViewModel | MenuItems：`NavigationViewItem{Content="Home",Icon=SymbolIcon Home24,TargetPageType=DashboardPage}` / `Data→DataPage`；FooterMenuItems：`Settings→Settings24` |
| 页面骨架 | DashboardPage=Counter 模板页；DataPage=`VirtualizingItemsControl` + `ui:Button`(80x80) 画 DataColor；SettingsPage=`RadioButton` Theme + AppVersion |
| MainWindow.xaml.cs | `INavigationWindow` 实现 + `SystemThemeWatcher.Watch(this)` + `SetPageService` + `navigationService.SetNavigationControl(RootNavigation)` |
| App.xaml.cs | Wpf.Ui DI 全套（AddNavigationViewPageProvider / AddHostedService / ITaskBarService / INavigationService / INavigationWindow + 各 Page） |

### 2.2 原项目 WebView2 使用点（`code/`，迁移来源）

| 文件 | 事实 |
|---|---|
| `code/src/LlmWiki.App/LlmWiki.App.csproj` | `<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4314.35" />` |
| `code/src/LlmWiki.App/MainWindow.xaml` | `xmlns:Wpf=...WebView2.Wpf` + `<Wpf:WebView2 x:Name="BridgeWebView">` 右侧内容区；左侧知识库面板（SectionLabel + ListBox + TreeView）；Menu/ToolBar（SearchBox+搜索） |
| `code/src/LlmWiki.App/MainWindow.xaml.cs` | `InitWebViewAsync` → `EnsureCoreWebView2Async` → AddScriptToExecuteOnDocumentCreated → WebMessageReceived→BridgeDispatcher → PostWebMessageAsString；剪贴板；DocumentTitleChanged；拖拽上传 |
| `wwwroot/bridge.js` | 前端桥接（/api/kbs /api/tree /api/file /api/save /api/share /api/file-assoc…） |
| `wwwroot-share/index.html` | 分享页（只读 UI，mobile browser 访问 Kestrel 静态文件） |

## 三、功能迁移清单（WebView2 前端功能 → WPF UI 原生控件映射）

| # | 原功能 | 迁移后宿主控件 | 备注 |
|---|---|---|---|
| 1 | 主框架外壳导航 | `FluentWindow` + `NavigationView(PaneDisplayMode=LeftFluent)` + `TitleBar` + `BreadcrumbBar` + `SnackbarPresenter` + `ContentPresenter` | 模板已就位；菜单项类型 `NavigationViewItem` 已验证可绑定 |
| 2 | 左侧知识库列表 | `NavigationView` Pane 内 `ListView` / ComboBox | 当前模板未放内容区；迁移时决定 Pane 放什么 |
| 3 | 目录树 | WPF `TreeView`（Fluent 样式）放 Pane | 原 `TreeView` + ItemTemplate 迁移 |
| 4 | 内容浏览/预览 Markdown | WPF UI Page + Markdown 渲染控件（RichTextBox/Document / 第三方库） | **关键阻塞项**：WPF UI 4.2.0 无内置 Markdown 控件 |
| 5 | 内容编辑 | WPF `TextBox` / `Document` + `ui:Button` | 保存走 C# `BridgeDispatcher`（C# 侧保留） |
| 6 | 上传 | WPF `ui:Button` + `OpenFileDialog` + Drag&Drop | C# `UploadService`/`OfficeConverter` 保留 |
| 7 | 搜索 | WPF `TextBox` + `ui:Button`（或 `ui:TextBox`） | 原 SearchBox+OnSearch |
| 8 | 分享 | WPF `ui:DialogHost` + Button；Kestrel+mDNS+静态分享页**保留**（mobile browser 用，不走 WPF） | 主机端分享只读 UI 走 WPF UI |
| 9 | 提示词窗口 | WPF UI `FluentWindow` + `DialogHost`/`ContentDialog` | 原 `PromptWindow.cs` |
| 10 | 设置窗口 | WPF UI `FluentWindow` + `DialogHost` + SettingsPage | 原 `SettingsWindow.xaml` |
| 11 | 文件关联 | WPF UI DialogHost + `Microsoft.Win32.Registry`（C# `FileAssociation.cs` 保留） | 原 bridge `file-assoc-*` |
| 12 | 托盘/任务栏 | WPF UI `TaskBarService` / 托盘控件 | 模板已注册 ITaskBarService |
| 13 | 启动闪屏 | WPF UI `BootSplash`?（模板无）→ WPF `Border`/`LoadingControl` | 原 BootSplash 在 WebView2 前 |

## 四、分阶段实施计划

| Phase | 内容 | 预估 |
|---|---|---|
| **P0** | 基线验证：WPF UI 4.2.0 + net10.0-windows 模板外壳能启动（Mica/NavigationView/主题） | 0.5d |
| **P1** | 主框架接线：MainWindow 用 `NavigationView` 承载内容区 Frame；左侧导航 Pane（TreeView+知识库列表）；工具栏 ToolBar+TextBox+Button；状态栏/Snackbar | 1d |
| **P2** | Dashboard 知识库功能：列表/树/预览/编辑/搜索/上传控件替换 WebView2 前端 | 2~3d |
| **P3** | Markdown 渲染控件选型与实现 | 1~2d |
| **P4** | PromptWindow/SettingsWindow/分享/文件关联/托盘迁移 | 1~2d |
| **P5** | 验收：移除 WebView2 全部引用；构建 0 错 0 错；内核测试 113/113 不回归 | 0.5d |

**串行总工期**：约 **6~10 天**（按当前仓库规模与阶段切分估算）。

## 五、关键设计决策（待你拍板）

1. **Markdown 渲染**：WPF UI 4.2.0 无内置 Markdown 控件。
   - 选项 A：第三方库（如 Markdig + 自写 WPF 渲染控件 / MarkdownControl）。
   - 选项 B：手写 RichTextBox/Document 渲染器。
   - **已定（2026-10-28）：选项 A，第三方库**（用户明确确认）。落地时再定具体库。
2. **视觉**：**放弃旧 Web 前端样式**，全部改用 WPF UI Library 官方 Fluent 主题样式（由 WPF-UI `ThemesDictionary` / `ControlsDictionary` + `Fluent*` 控件提供），不做旧样式迁移。
3. **优先级**：先实现功能完整性，样式随 WPF UI 控件原生样式呈现，不在前期做视觉抄袭。
4. **内容区承载**：`NavigationView.Content`（Frame）vs 自建 ContentControl。
5. **分享页**：主机端 WPF UI 实现分享只读视图；Kestrel 静态文件（`wwwroot-share`）给手机浏览器保留——与"放弃 WebView2"不冲突。
6. **WebView2 桥接**：C# `BridgeDispatcher`/`BridgeProtocol` 保留（供 WPF 控件直调 + Kestrel/浏览器）；`bridge.js` 随前端移除。

## 六、风险

| 风险 | 缓解 |
|---|---|
| WPF UI 4.2.0 + .NET 10 兼容性 | P0 验证，不通过则回退方案 |
| Markdown 控件缺失 | P3 前必须定方案 |
| NavigationView MenuItemsSource 对象绑定 | 模板已验证可行 |
| 8192 色块 VirtualizingItemsControl | 模板已验证可行 |

## 七、验收标准

- [ ] 全仓库 UI 层（`Wpf UI/`）**无** WebView2 引用（csproj / XAML / 代码）。
- [ ] `Wpf UI/` 构建 **0 错误 0 警告**。
- [ ] C# 内核单测 **113/113 通过**（不回归）。
- [ ] 原 WebView2 前端功能清单（12 项）被逐项映射到 WPF UI 控件。

## 八、待你确认（HARD GATE）

1. 是否**批准本详细计划**——批准后我才进入 `superpowers:writing-plans` 写 Implementation Plan，再开始实现。
2. ~~Markdown 渲染方案~~：**✅ 已定：选项 A（第三方库）**（2026-10-28 你确认）。
3. ~~Phase 顺序~~：**✅ 已定：P0~P5 串行方案**（2026-10-28 你确认采用该结构）。

> 2026-10-28 补充：你明确"先别开始实现，先做计划"，实现阶段**尚未启动**，上述 Phase 均待最终批准后执行。

---

**下一步行动**：你确认计划无误后，我说"批准"，我就去写 Implementation Plan；你有调整意见就直接改这份计划。
