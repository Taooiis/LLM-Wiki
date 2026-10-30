# WPF 版走通完整流程 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 WPF 版按 Demo（HTML 版）的完整流程走通：新建库 → 选库 → 源文档增删改 → 待处理变更 → 生成/复制提示词 → 浏览编辑 → 配置生效 → 分享可用。

**Architecture:** 断点修复为主，不改整体结构。核心数据链是 `MainWindowViewModel.CurrentKb`（全工程共享的当前库状态），修通它之后各页面已有的 `OnKbChanged` 监听自动激活。变更→提示词的跨页通信从"集合元素变更（不通知）"改为"字符串属性赋值（自动通知）"。

**Tech Stack:** WPF (.NET 10) + WPF-UI 4.3.0 + CommunityToolkit.Mvvm 8.4.2 + Microsoft.Extensions.Hosting DI

**Spec:** `C:\Users\Ari\Documents\LLM Wiki Demo\使用说明.md`（Demo 完整行为定义）；断点证据来自探索报告（每任务内注明文件:行号）。

## Global Constraints

- 不引入新 NuGet 包；沿用现有 DI 单例注册模式（`App.xaml.cs` ConfigureServices）
- 所有用户可见文案用中文，与 Demo 一致
- 每个任务结束 `dotnet build "Wpf UI\LLM Wiki.csproj"` 0 错误才算完成
- 不改 `App.xaml` 全局样式资源（上一轮已定稿）
- 文件路径统一用 `/` 分隔存入 ChangeStore（现状），显示时原样

## Review Focus

- 非法库名（空格、点、空串、已重名）：预期 toast 报错，不崩溃
- 文件名含中文/空格：预期完整读写，路径拼接不错位
- 空库状态（0 个知识库 / 0 个源文档）：预期页面显示空态提示，不抛 NRE
- 存储路径改为 UNC（`\\pc\share`）后：预期所有页面读写跟随新路径
- 编辑器关闭时未保存：预期弹确认，不静默丢稿

---

### Task 1: CurrentKb 贯通（根因断链）

**Files:**
- Modify: `Wpf UI/ViewModels/Pages/DashboardViewModel.cs`（构造注入 + SelectedKb setter）
- Modify: `Wpf UI/App.xaml.cs`（若 DashboardViewModel 注册方式不支持注入则调整）

**Interfaces:**
- Consumes: `MainWindowViewModel.CurrentKb`（已存在，`MainWindowViewModel.cs:80`）
- Produces: Dashboard 选库 → `MainWindowViewModel.CurrentKb` 更新 → Source/TreeBrowser/Workflow/Config 各页已有监听激活

- [ ] **Step 1:** 读 `DashboardViewModel.cs` 构造函数与 SelectedKb 定义，确认 `MainWindowViewModel` 在 DI 中为单例且 `DashboardViewModel` 注册为 `AddSingleton<DashboardViewModel>()`
- [ ] **Step 2:** `DashboardViewModel` 构造函数追加 `MainWindowViewModel mainViewModel` 参数并保存为字段 `_main`
- [ ] **Step 3:** 在 `SelectedKb` 赋值处（partial `OnSelectedKbChanged` 或 setter）：`_main.CurrentKb = value`；建库成功后自动选中：`SelectedKb = 新库名`
- [ ] **Step 4:** Dashboard 工具栏下加一行库路径显示（Demo `#kbPath` 对齐）：`Text="{Binding ViewModel.CurrentKbPath}"`，`CurrentKbPath` 为 DashboardViewModel 新增属性，选库/建库后更新为 `_fileBridge.KbRoot(库名)` 完整路径
- [ ] **Step 5:** 构建 + 手动验证：① 未选库时切到源文档/目录浏览/工作流页不崩溃（空态文案）；② Dashboard 选一个库 → 源文档页不再停留"请先选择知识库"；③ 库路径行显示完整本地路径
- [ ] **Step 6:** Commit: `fix: Dashboard 选中库写入 MainWindowViewModel.CurrentKb 打通全页联动`

### Task 2: 源文档页列出真实源文档 + 删除入口 + 新建走编辑器

**Files:**
- Modify: `Wpf UI/ViewModels/Pages/SourceViewModel.cs:76-83`（LoadFiles）、`:114-131`（CreateFile）
- Modify: `Wpf UI/Views/Pages/SourcePage.xaml`（待处理变更区加删除按钮）
- Modify: `Wpf UI/Views/Pages/SourcePage.xaml.cs:53,71`（编辑保存路径）

**Interfaces:**
- Consumes: `FileBridge.Sources(kb)`（已存在，`FileBridge.cs:133-139`，现零调用）、`FileBridge.Delete(kb, relPath)`（若不存在则在本任务补一个薄方法）、`EditorDialog.Mode.New`（已实现，无入口）
- Produces: 源文档列表项为携带相对路径的结构（如 `record SourceFileItem(string Name, string RelPath)`），编辑/删除按 `RelPath` 定位文件，显示用 `Name`

- [ ] **Step 1:** `LoadFiles` 改用 `_fileBridge.Sources(kb)`，只留文件名显示（去掉目录名）
- [ ] **Step 2:** `SourcePage.xaml` 工具栏加"删除"按钮 `Click="OnDeleteClick"`；code-behind 弹 `ConfirmationDialog` 确认后执行 `ViewModel.DeleteFileCommand`（参考 `DashboardPage.xaml.cs:30-43` 既有模式）
- [ ] **Step 3:** `CreateFile` 改为：校验文件名后弹 `EditorDialog(Mode.New)`，`Saved` 事件里以输入文件名写盘（复用 `OnEditorSaved`，注意用传入的新文件名而非旧 `SelectedFile`）；新建成功后 `LoadFiles` 刷新
- [ ] **Step 4:** `SourcePage.xaml.cs:53,71` 编辑保存路径修复：仅当选中文件属于 `raw/sources/` 时写 `raw/sources/{name}`，否则写相对原位置（给选中项记录相对路径）
- [ ] **Step 5:** 构建 + 手动验证：选库 → 源文档页列出 `raw/sources` 文档 → 新建弹编辑器 → 保存出现在列表 → 删除有确认
- [ ] **Step 6:** Commit: `fix: 源文档页接入 Sources 列表、删除入口与新建编辑器`

### Task 3: 工作流页绑定修复 + Query/Compare 输入 + 双前缀

**Files:**
- Modify: `Wpf UI/Views/Pages/WorkflowPage.xaml:89`（复制命令绑定）、Ingest 卡加"生成"按钮、Query 卡加问题输入框、Compare 卡加主题输入框
- Modify: `Wpf UI/ViewModels/Pages/WorkflowViewModel.cs:60,209,336-344`（文件名归一化 + Query/Compare 参数）

**Interfaces:**
- Consumes: `CopyPromptCommand`（`WorkflowViewModel.cs:313-331`，绑定路径写错）、`GenerateIngestCommand`（`:333`，零绑定）、`BuildQueryPrompt(question)`、`BuildComparePrompt(topic)`
- Produces: 复制按钮可用；提示词文件路径不再出现 `raw/sources/raw/sources/`

- [ ] **Step 1:** `WorkflowPage.xaml:89` 绑定改为 `{Binding ViewModel.CopyPromptCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}`（Page 的 `ViewModel` 属性持有 VM）
- [ ] **Step 2:** Ingest 卡（IngestFiles 所在行）加 `ui:Button Content="生成提示词" Command="{Binding ViewModel.GenerateIngestCommand}"`
- [ ] **Step 3:** Query 卡加 `ui:TextBox` 绑定新属性 `ViewModel.QueryQuestion`；`BuildQueryPrompt` 改为带 `question` 参数，`CopyPromptCommand` 对 Query 卡用该值生成；Compare 卡同理加 `CompareTopic` 属性
- [ ] **Step 4:** `WorkflowViewModel` 加私有静态方法 `NormalizeFiles(IEnumerable<string>)`：去掉 `raw/sources/` 前缀再拼提示词；`GenerateIngest`（`:336-344`）拆分后调用
- [ ] **Step 5:** 构建 + 手动验证：工作流页选库后卡片出现 → 点"复制"剪贴板有内容 → Ingest 填 `01-xxx.md` 生成提示词无双前缀 → Query 填问题生成含问题的提示词
- [ ] **Step 6:** Commit: `fix: 工作流页复制绑定、生成按钮、Query/Compare 输入与路径归一化`

### Task 4: 待处理变更 → Ingest 闭环

**Files:**
- Modify: `Wpf UI/ViewModels/Windows/MainWindowViewModel.cs:83-84`（PendingIngestFiles 集合改字符串）
- Modify: `Wpf UI/ViewModels/Pages/SourceViewModel.cs:172-188`（生成选中 Ingest）
- Modify: `Wpf UI/ViewModels/Pages/WorkflowViewModel.cs:35-38`（监听新类型）
- Modify: `Wpf UI/ViewModels/Pages/WorkflowViewModel.cs:116-119`（Clean 卡接真实已删文件，可选）

**Interfaces:**
- Consumes: `ChangeItem`（`SourceViewModel.cs:291-294`，`File` 为含 `raw/sources/` 的相对路径）
- Produces: `MainWindowViewModel.PendingIngestFiles` 改为 `[ObservableProperty] string`（逗号分隔，已归一化文件名）；赋值即触发 `WorkflowViewModel.OnPendingIngestFilesChanged`

- [ ] **Step 1:** `MainWindowViewModel`：`PendingIngestFiles` 从 `ObservableCollection<string>` 改为 `[ObservableProperty] private string _pendingIngestFiles = "";`
- [ ] **Step 2:** `SourceViewModel` 生成选中 Ingest：取勾选项的 `File`，经 `NormalizeFiles` 归一化后逗号拼接，赋 `PendingIngestFiles`，再导航到 Workflow（沿用现有导航调用）
- [ ] **Step 3:** `WorkflowViewModel.OnPendingIngestFilesChanged(string value)`：`IngestFiles = value` + 立即 `GenerateIngest()`；空值跳过
- [ ] **Step 4:** 构建 + 手动验证：源文档页编辑保存 → 待处理变更出现条目 → 勾选 → 点"生成选中 Ingest" → 自动跳工作流页且 Ingest 卡已带文件名并生成提示词 → 执行完回源文档页"标记已处理"列表清空
- [ ] **Step 5:** Commit: `fix: 待处理变更直达 Ingest 提示词生成`

### Task 5: 目录树读写用相对路径（数据安全）

**Files:**
- Modify: `Wpf UI/Views/Pages/TreeBrowserPage.xaml.cs:29-32`（传相对路径）
- Modify: `Wpf UI/ViewModels/Pages/TreeBrowserViewModel.cs:71-74,92-93`（读写入参）
- Modify: 树节点模型（`FileBridge.Tree` 的项类型）加 `RelativePath`

**Interfaces:**
- Consumes: `FileBridge.Tree(kb)` 节点结构
- Produces: 树节点携带 `RelativePath`（如 `wiki/index.md`）；`SelectFile(string relativePath)` 读该路径、保存写回同路径

- [ ] **Step 1:** 找到 `FileBridge.Tree` 返回的节点类型，加 `RelativePath` 属性，构建树时按层级拼出
- [ ] **Step 2:** `TreeBrowserPage.xaml.cs:31` 改传 `item.RelativePath`
- [ ] **Step 3:** `TreeBrowserViewModel` 读（`:71-74`）与保存（`:92-93`）均使用该相对路径
- [ ] **Step 4:** 构建 + 手动验证：目录浏览 → 展开 `wiki/` → 点 `index.md` 能读到内容 → 改一个字保存 → 重开确认写回原文件（且库根目录没有多出同名文件）
- [ ] **Step 5:** Commit: `fix: 目录树按相对路径读写，子目录文件不再写错位置`

### Task 6: 存储路径全局生效 + 恢复默认按钮

**Files:**
- Modify: `Wpf UI/App.xaml.cs:71-73`（FileBridge/KbService 构造接 ConfigService）
- Modify: `Wpf UI/Services/FileBridge.cs`、`Services/KbService.cs`（根目录来自 ConfigService）
- Modify: `Wpf UI/ViewModels/Pages/WorkflowViewModel.cs:74`、`SourceViewModel.cs:285-288`（删本地 GetKbRoot 硬编码）
- Modify: `Wpf UI/Views/Pages/ConfigPage.xaml`（加"恢复默认"按钮）

**Interfaces:**
- Consumes: `ConfigService.WikisDir`（已存在，`ConfigViewModel.SaveWikisDir` 已写它）
- Produces: 全工程库根路径单一来源 = ConfigService；`FileBridge` 暴露 `string KbRoot(string kb)` 供 Workflow/Source 复用

- [ ] **Step 1:** `FileBridge`、`KbService` 构造函数加 `ConfigService` 参数，内部根目录属性改为 `=> _config.WikisDir`
- [ ] **Step 2:** `App.xaml.cs:71-73` 注册处传入 ConfigService 实例
- [ ] **Step 3:** `WorkflowViewModel.cs:74`、`SourceViewModel.cs:285-288` 删除 `Path.Combine(AppContext.BaseDirectory, "wikis", kb)`，改调 `_fileBridge.KbRoot(kb)`
- [ ] **Step 4:** `ConfigPage.xaml` 存储路径卡加 `ui:Button Content="恢复默认" Command="{Binding ViewModel.ResetWikisDirCommand}"`
- [ ] **Step 5:** 构建 + 手动验证：配置页改路径到 `D:\test-wikis`（先手动建目录）→ 回 Dashboard 能看到/新建库落在新目录 → 改为 UNC 路径（如 `\\localhost\共享名`，或跳过若无共享）各页可读 → 点"恢复默认"回到程序目录 `wikis/`
- [ ] **Step 6:** Commit: `fix: 存储路径改动全局生效并支持恢复默认`

### Task 7: 健壮性（建库崩溃 + 分享开关 + 勾选丢失）

**Files:**
- Modify: `Wpf UI/ViewModels/Pages/DashboardViewModel.cs:151-160`（CreateKb try/catch）
- Modify: `Wpf UI/Views/Pages/SharePage.xaml:41-46`（ToggleSwitch 绑定）
- Modify: `Wpf UI/ViewModels/Pages/ShareViewModel.cs`（ToggleShareCommand）
- Modify: `Wpf UI/ViewModels/Pages/SourceViewModel.cs:291-294`（ChangeItem 改可通知类）
- Modify: `Wpf UI/App.xaml.cs:108-111`（全局异常 handler 记日志）

**Interfaces:**
- Consumes: `ToastService`（已存在）、`ShareService.Start()/Stop()`（已存在）
- Produces: `ShareViewModel.ToggleShareCommand`；`ChangeItem` 实现 `INotifyPropertyChanged`（record → class 或加 `[ObservableProperty]`）

- [ ] **Step 1:** `CreateKb` 包 try/catch（`ArgumentException`/`IOException`），失败走 `ToastService` 显示"库名不能含空格或特殊符号"类文案
- [ ] **Step 2:** `ShareViewModel` 加 `ToggleShareCommand`：`IsRunning` 为真则 Stop 否则 Start，结束后刷新地址与 IsRunning；`SharePage.xaml:41-46` 改为 `IsChecked="{Binding ViewModel.IsRunning, Mode=OneWay}"` + `Command="{Binding ViewModel.ToggleShareCommand}"`
- [ ] **Step 3:** `ChangeItem` 的 `IsSelected` 改为可通知（class + `ObservableObject` 或手写 INPC）
- [ ] **Step 4:** `App.xaml.cs` 全局异常 handler：`e.Handled = true` 前先 `ToastService`/日志记录异常（避免再吞成静默）
- [ ] **Step 5:** 构建 + 手动验证：新建库名输入 `a b` 点新建 → toast 报错不崩溃；分享页开关可开可关且状态一致；勾选变更后滚动列表再滚回，勾选仍在
- [ ] **Step 6:** Commit: `fix: 建库异常提示、分享开关绑定与变更勾选持久`

### Task 8: schema.md 编辑器

**Files:**
- Modify: `Wpf UI/Views/Pages/ConfigPage.xaml`（purpose 卡后加 schema 卡）

**Interfaces:**
- Consumes: `ConfigViewModel.SchemaContent`、`SaveSchemaCommand`（均已实现，零绑定）
- Produces: schema 编辑卡（与 purpose 卡同构）

- [ ] **Step 1:** 复制 purpose 卡结构改为 schema：标题 `schema.md`、说明"结构规则（默认即可，一般不动）"、`ui:TextBox` 绑 `ViewModel.SchemaContent`、保存按钮绑 `ViewModel.SaveSchemaCommand`
- [ ] **Step 2:** 确认 `ConfigViewModel` 加载时已填充 `SchemaContent`（若只加载了 purpose 则补读 `schema.md`）
- [ ] **Step 3:** 构建 + 手动验证：配置页 schema 可编辑保存，重进内容保持
- [ ] **Step 4:** Commit: `feat: 配置页补 schema.md 编辑器`

### Task 9: Dashboard 修整（死按钮、搜索、预览保存）

**Files:**
- Modify: `Wpf UI/Views/Pages/DashboardPage.xaml:75-81`（KB 编辑按钮）、`:50,130`（两个搜索框）、`:151-162`（PreviewBox）
- Modify: `Wpf UI/ViewModels/Pages/DashboardViewModel.cs:115-120`（UploadFileCommand）、`:122`（SearchKbCommand）、`:137`（SaveFileCommand）

**Interfaces:**
- Consumes: 既有 `ObservableCollection` 属性
- Produces: KB 搜索与文件搜索各自独立过滤；预览改动有"保存"落盘

- [ ] **Step 1:** 决策落地：KB 列表"编辑"按钮改为重命名会话成本高 → 移除该按钮（Demo 也无重命名），保留列表点击选中
- [ ] **Step 2:** 搜索改为本地过滤：`DashboardViewModel` 拆 `KbSearchText`/`FileSearchText` 两属性，`Kbs`/`Files` 改为过滤后的视图集合（`CollectionViewSource` 或手动过滤刷新）；XAML 两个搜索框分别改绑
- [ ] **Step 3:** 预览区加"保存"按钮绑 `SaveFileCommand`（已有实现则接通；实现缺失则写当前 `MarkdownText` 回选中文件）
- [ ] **Step 4:** `UploadFileCommand` 占位实现替换为真实 OpenFileDialog 上传（`SourceViewModel.cs:207-283` 已有可复用逻辑，搬或调用）；Dashboard 工具栏若无需上传则移除占位命令与相关 UI
- [ ] **Step 5:** 构建 + 手动验证：KB 搜索输入能过滤列表；文件搜索同理；预览改字点保存后重进内容保持
- [ ] **Step 6:** Commit: `fix: Dashboard 搜索过滤、预览保存与死按钮清理`

### Task 10: 编辑器细节（路径显示 + 保存后 Ingest 提醒）

**Files:**
- Modify: `Wpf UI/Views/Dialogs/EditorDialog.xaml.cs`（FilePathText 赋值）
- Modify: `Wpf UI/Views/Pages/SourcePage.xaml.cs:80-84`（保存后 toast 提醒）

**Interfaces:**
- Consumes: `EditorDialog` 打开时传入的路径、`ToastService`
- Produces: 弹窗顶部显示文件路径；保存后 toast 提示"已保存，去工作流页重新 Ingest 该文档"

- [ ] **Step 1:** `EditorDialog` 初始化（Exist 模式）时 `FilePathText.Text = 完整相对路径`
- [ ] **Step 2:** `SourcePage.OnEditorSaved` 的 toast 文案追加"去工作流页重新 Ingest 该文档"（Demo wiki-manager.html:365-366 行为对齐）
- [ ] **Step 3:** 构建 + 手动验证：编辑文档弹窗顶部显示路径；保存后 toast 含 Ingest 提醒
- [ ] **Step 4:** Commit: `polish: 编辑器路径显示与保存后 Ingest 提醒`

---

## 执行顺序与依赖

- Task 1 是其余所有页面的前置（CurrentKb）；Task 2-4 依赖 Task 1；Task 5-10 相互独立，可乱序
- 建议顺序：1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10
- 每任务后跑一次手动验证并 commit；全部完成后按 `验证清单.txt` 走一遍五题流程
