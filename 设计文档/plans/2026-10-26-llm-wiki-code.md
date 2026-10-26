# LLM Wiki 桌面客户端 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `code/` 下按 `设计文档/01~04` 实现桌面知识库维护工具：.NET 10 WPF 壳 + WebView2 面板（移植 Demo 前端）、内置 C# 文件桥、docx/xlsx 上传转换、六类提示词、只读分享服务。

**Architecture:** 单进程 WPF 应用；宿主持 WebView2 控件加载 `wwwroot/` 面板（从 Demo `wiki-manager.html` 移植，fetch 换成桥通道）；C# 服务层实现文件桥（语义对齐 Demo `server.js` 的 API 形状）+ Office→MD 转换；可开关的 Kestrel 只读分享服务独立成面。

**Tech Stack:** .NET 10 (`net10.0-windows`)、WPF、Microsoft.Web.WebView2、DocumentFormat.OpenXml（docx/xlsx 转换）、ASP.NET Core Minimal API（只读分享）、xUnit。

**Spec:** `设计文档/README.md`、`01-总体设计.md`、`02-架构与数据设计.md`、`03-多设备只读访问.md`、`04-兼容与迁移.md`（本计划从规格出发，执行者需并行阅读）。Demo 代码参照：`C:\Users\Ari\Documents\LLM Wiki Demo\`（`server.js` 为 API 形状唯一权威，`wiki-manager.html` 为前端移植底稿）。

## Global Constraints

- 目标框架 `net10.0-windows`（.NET 10，本机 SDK 10.0.401）。.NET 11 仅在 GA 后升级，本计划不做。
- 数据层禁止数据库：一切内容以本地文件落盘（spec C5）。
- 面板永远不写 `wiki/` 层的现状内容；写范围 = `raw/**`、`purpose.md`、`schema.md`、`.changes.json`（spec 01 反目标；`index.md/log.md/overview.md` 仅骨架初始化时创建）。
- 源格式仅四类：`.md/.txt/.docx/.xlsx`（大小写不敏感）。office 上传必须成对：原件 → `raw/original/<原名>`，副本 → `raw/sources/<原名>.md`（spec 02 §6.1）。
- 路径防护照搬 Demo：库内文件拒绝 `..` 逃逸；存储路径拒绝单个 `\` 开头；库名正则 `^[a-zA-Z0-9_\-\u4e00-\u9fa5]+$`（`server.js:36-47`）。
- 请求体上限 20MB（spec D8）；转换失败不落盘（spec 02 §6.2）。
- `.changes.json` 条目形状与 Demo 一致外加 `hash` 字段：`{ file, action, time, hash }`，action ∈ 上传/更新/删除/编辑（spec 02 §7）。
- git：每个任务按步提交，commit 消息用 `feat:/test:/chore:` 前缀；在仓库根 `C:\Users\Ari\Documents\LLM Wiki` 提交（工作目录即此 repo）。
- 不做托盘（v1 决定，宿主关窗即退出，退出前有未保存逻辑由前端弹窗负责）；不做安装器，v1 交付 `dotnet publish` 产物目录。

## Review Focus

用户手动输入类/spec 未点名的输入，最可能刺到人：

1. **UNC 存储路径不可达时**（断网/共享关闭）：所有库操作应报错提示而非崩溃；删库必须拒绝（spec D7）。Task 3 测试覆盖。
2. **office 副本重名冲突**：上传 `报告.docx` 时 `raw/sources/报告.docx.md` 已被用户手工占用 → 拒绝上传并报错，不得覆盖（spec 02 §6.1 反"孤儿副本"精神）。Task 5 测试覆盖。
3. **GBK/UTF-8 BOM 混杂的 txt**：必须探测并以 UTF-8 写副本与预览，否则面板乱码（spec 02 §6.1；需 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`）。Task 5 测试覆盖。
4. **xlsx 大表/公式**：>200 行截断并在 md 表头注明"已截断，原件见 raw/original/"；公式单元格写缓存值（spec 02 §6.2）。Task 5 测试覆盖。
5. **只读服务挂写接口**：任何改 `wiki/`、`raw/`、配置的 HTTP 方法出现在分享服务上都算缺陷（spec 03 §4——路由白名单实现，非黑名单过滤）。Task 8 测试覆盖。

---

### Task 1: 解决方案骨架 + WPF 壳 + WebView2 空面板

**Files:**
- Create: `code/LlmWiki.sln`
- Create: `code/src/LlmWiki.App/LlmWiki.App.csproj`、`App.xaml(.cs)`、`MainWindow.xaml(.cs)`、`wwwroot/index.html`
- Create: `code/tests/LlmWiki.Tests/LlmWiki.Tests.csproj`（`net10.0-windows`，引用 App 项目）

**Interfaces:**
- Produces: App 项目常量 `AppRoot = AppContext.BaseDirectory`；`MainWindow` 内 `WebView BridgeWebView`；全局 `Services` 属性挂后续各服务（本任务先为空占位）。
- csproj 要点：`<UseWPF>true</UseWPF>`、NuGet `Microsoft.Web.WebView2`（最新稳定）、`<EnableWpfXamlDesignTime>`不需要。

- [ ] **Step 1: 用 dotnet CLI 建骨架**

```bash
cd C:\Users\Ari\Documents\LLM Wiki\code
dotnet new sln -n LlmWiki
dotnet new wpf -n LlmWiki.App -o src/LlmWiki.App -f net10.0
dotnet new xunit -n LlmWiki.Tests -o tests/LlmWiki.Tests -f net10.0
dotnet sln add src/LlmWiki.App tests/LlmWiki.Tests
dotnet add tests/LlmWiki.Tests reference src/LlmWiki.App
dotnet add src/LlmWiki.App package Microsoft.Web.WebView2
# csproj 手改：TargetFramework 改为 net10.0-windows（wpf/xunit 新模板默认是 net8/9，必须改）
```
Expected: `dotnet build` 成功（wwroot 尚未挂，先纯 WPF 窗口）。

- [ ] **Step 2: MainWindow 挂 WebView2 并加载本地 wwwroot/index.html**

`MainWindow` 用 `EnsureCoreWebView2Async`；`CoreWebView2.WebResourceRequested` 拦截 `https://app.local/*` 映射到磁盘 `AppRoot/wwwroot`，或直接 `SetVirtualHostNameToFolderMapping("app.local", folder, CoreWebView2HostResourceAccessKind.Allow)` 后 `webview.Source = new Uri("https://app.local/index.html")`（**用后者，更简单**）。`index.html` 先为占位页（标题 `LLM Wiki` + 一行状态文本）。

- [ ] **Step 3: 构建并手验**

Run: `dotnet build` 后 `dotnet run --project src/LlmWiki.App`
Expected: 窗口出现、占位页渲染无白屏、无异常输出。关闭窗口进程退出。

- [ ] **Step 4: Commit**

```bash
git add code
git commit -m "feat: solution skeleton, WPF shell with WebView2 placeholder panel"
```

### Task 2: PathGuard + ConfigService（存储路径机制）

**Files:**
- Create: `code/src/LlmWiki.App/Services/PathGuard.cs`、`Services/ConfigService.cs`
- Test: `code/tests/LlmWiki.Tests/PathGuardTests.cs`、`ConfigServiceTests.cs`

**Interfaces:**
- Consumes: 无（Demo `server.js:15-47` 为行为权威）。
- Produces:
  - `static class PathGuard { static string? ResolveWikisDir(string? v, string appRoot); static string? SafeJoin(string wikisDir, string kb, string? rel); static bool ValidKb(string? name); }`
  - `class ConfigService { string WikisDir { get; } bool SetWikisDir(string v, out string error); }`（配置文件：`AppRoot/config.json`，形状 `{"wikisDir": "..."}`，与 Demo 同键同义，支持 Demo 互导 spec 04 §1）

- [ ] **Step 1: 写 PathGuard 失败测试**（三组：ResolveWikisDir 的合法盘符/UNC/相对/空值回退/非法单反斜杠；SafeJoin 的 `..` 逃逸与越界；ValidKb 中文名通过、含空格拒绝）

- [ ] **Step 2: 跑测试确认失败** Run: `dotnet test` Expected: 新测试 FAIL（类型未定义）

- [ ] **Step 3: 实现 PathGuard 与 ConfigService**（resolve/safeJoin 逻辑逐行对照 `server.js:15-47` 移植为中国注释标注的 C# 版；新库写入前 `Directory.CreateDirectory(WikisDir)`）

- [ ] **Step 4: 跑测试确认通过** Run: `dotnet test` Expected: PASS

- [ ] **Step 5: Commit** `git commit -m "feat: path guards and wikis-dir config service"`

### Task 3: KbService（列表 / 建库骨架 / 删库 + 场景模板）

**Files:**
- Create: `code/src/LlmWiki.App/Services/KbService.cs`、`Services/LogWriter.cs`（被 KbService/FileBridge/UploadService 共用）
- Test: `code/tests/LlmWiki.Tests/KbServiceTests.cs`

**Interfaces:**
- Consumes: `PathGuard`, `ConfigService.WikisDir`。
- Produces:
  - `class KbService { IReadOnlyList<string> ListKbs(); string CreateKb(string name, string scene = "通用"); void DeleteKb(string name); (bool ok, string error) ValidateDelete(string name); }`
  - `static class LogWriter { void Append(string kb, string line); void AppendSource(string kb, string rel, string action, string? note = null); }`（`note == null` 时自动补"（需重新 Ingest）"，照 `server.js:72-81`）
  - `SceneTemplates.Get(scene)` 返回固化的 purpose/schema 文案（通用/研究/读书/业务），默认文案与 Demo `createSkeleton`（`server.js:49-69`）逐字一致，schema 段再加 spec 02 §8 的两句增量（`[[wikilink]]`、office 副本说明）。
- 删库语义（spec D7）：`ValidateDelete` 校验库名合法 + 目录存在于当前 WikisDir 下 + 路径可达；不可达（UNC 掉线）返回 error；`DeleteKb` 直接整库删除（含 `raw/original/`），调用方（前端）负责输入库名确认。

- [ ] **Step 1: 写失败测试**（建库产出全部骨架目录与 5 个默认文件且内容含库名与当天日期；scene=研究 时 purpose 首行含"研究"；删库后目录消失；UNC 模拟：WikisDir 指向不存在驱动器时 ValidateDelete 返回 ok=false）
- [ ] **Step 2: dotnet test → FAIL**
- [ ] **Step 3: 实现 KbService/LogWriter/SceneTemplates**（骨架目录九个 + `.changes.json` 初始化为 `[]`）
- [ ] **Step 4: dotnet test → PASS**
- [ ] **Step 5: Commit** `git commit -m "feat: knowledge base lifecycle with scene templates and log writer"`

### Task 4: FileBridge + HashUtil（树 / 读写 / 删除 / 变更指纹）

**Files:**
- Create: `code/src/LlmWiki.App/Services/FileBridge.cs`、`Services/HashUtil.cs`
- Test: `code/tests/LlmWiki.Tests/FileBridgeTests.cs`

**Interfaces:**
- Consumes: `PathGuard.SafeJoin`, `KbService.ListKbs`, `LogWriter`。
- Produces:
  - `class FileBridge { TreeItem[] Tree(string kb); string? ReadText(string kb, string rel, out string error); bool WriteText(string kb, string rel, string content, out string error); bool Delete(string kb, string rel, out string error); string[] Sources(string kb); Change[] Changes(string kb); void ClearChanges(string kb, string[] files); }`
  - `static class HashUtil { static string Sha256(string filePath); static string Sha256Text(string content); }`
  - `record TreeItem(string name, string type, TreeItem[] children)`、`record Change(string file, string action, string time, string hash)`
  - 语义：读写删除仅 md/txt；tree 跳过 `.` 开头与 `Office` 原件目录不筛（演示目录树完整性，original 文件照样入树）；source 范围动作同时写 `wiki/log.md` 与 `.changes.json`（带 `Sha256Text` 指纹）；`WriteText` 针对 `purpose.md`/`schema.md` 走"配置更新"日志路径（照 `server.js:209-222`）；`Delete` 对 office 路径返回 error（成对删除由 Task 5 的上传面负责）。

- [ ] **Step 1: 写失败测试**（树排序/隐藏过滤；safeJoin 后读写 md 成功且 log 有记录；`rel=..\evil.txt` 被拒；changes 记录含 64 位 hex hash；delete txt 后 log 与 changes 均记 `删除`）
- [ ] **Step 2: dotnet test → FAIL**
- [ ] **Step 3: 实现 FileBridge/HashUtil**
- [ ] **Step 4: dotnet test → PASS**
- [ ] **Step 5: Commit** `git commit -m "feat: file bridge with change fingerprinting"`

### Task 5: UploadService + OfficeConverter（四格式入库核心）

**Files:**
- Create: `code/src/LlmWiki.App/Services/UploadService.cs`、`Services/OfficeConverter.cs`
- Test: `code/tests/LlmWiki.Tests/UploadTests.cs`

**Interfaces:**
- Consumes: `LogWriter` 与变更存储（经 Task 4 的变更记录能力写入，避免服务互相依赖循环）、`PathGuard`、`HashUtil`、`OfficeConverter`。
- Produces:
  - `class UploadService { (bool ok, string error, string? sourceRel) Upload(string kb, string filename, byte[] content); (bool ok, string error) DeleteSource(string kb, string rel); }`
  - `static class OfficeConverter { static string? Convert(string filename, byte[] content, out string error, out byte[]? originalBytes); }`
  - 分派：`md/txt` → 直存 `raw/sources/<名>`（txt 走编码探测：UTF8-BOM/UTF8/GBK 系，写回统一 UTF-8；原件就是副本，不落 original）；`docx/xlsx` → `originalBytes=content`，原件落 `raw/original/<名>`，副本落 `raw/sources/<名>.md`；`size > 20MB` → error；副本目标已存在（用户手工占有）→ error，不覆盖（Review Focus 2）。
  - docx→md：标题段样式 → `#`（按 HeadingN 映射），加粗/斜体保留，列表 → `-`，表格 → md 表格；提取不到的元素（图片等）跳过并在副本头部追加 `<!-- 转换损失: ... -->` 不声不响（spec 02 §6.2）；转换异常 → 返回 error，**不写任何文件**。
  - xlsx→md：每工作表 `## <表名>`，md 表格，>200 行截断并在表头行下注 `> 已截断 N 行，原件见 raw/original/`；公式按缓存值输出。
  - 删除：md/txt 单删；office 成对删原件+副本且 log 记两行（spec 02 §6.1.4）；副本缺失仅剩原件时仍删除并告警 note。

- [ ] **Step 1: 写失败测试**（测试内用 DocumentFormat.OpenXml 程序化生成最小 docx/xlsx fixture：一份含 H1/H2、列表、2x3 表格的 docx；一份两个工作表、含 250 行数据与一个公式格的 xlsx。断言：docx 副本出现 `# 标题` 与 md 表格；xlsx 副本两个 `##`、截断提示、公式值为缓存数字；GBK txt 上传后读出为正字；重名拒绝；>20MB 拒绝；成对删除后两文件消失 + log 两行）
- [ ] **Step 2: dotnet test → FAIL**
- [ ] **Step 3: 实现 OfficeConverter/UploadService**（测试程序里 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 要在宿主 App.xaml.cs 启动也注册一次）
- [ ] **Step 4: dotnet test → PASS**
- [ ] **Step 5: Commit** `git commit -m "feat: four-format upload with docx/xlsx to markdown conversion"`

### Task 6: 桥通道 + 前端移植（面板可用）

**Files:**
- Create: `code/src/LlmWiki.App/Bridge/BridgeDispatcher.cs`、`wwwroot/bridge.js`
- Create（移植）: `wwwroot/index.html`（从 Demo `wiki-manager.html` 移植）
- Modify: `MainWindow.xaml.cs`（注册桥）

**Interfaces:**
- Consumes: Task 2-5 的全部服务。
- Produces:
  - C#：`class BridgeDispatcher { Task<string> HandleAsync(string name, string jsonArgs); }` 依 Demo 语义覆盖命令：`health/kbs-create/kbs-list/tree/file-read/file-write/upload/delete/sources/changes/clear-changes/settings-get/settings-set/drives/browse/places/net-pc/net-shared`（spec 02 §4 清单 +局域网选择器）。返回 JSON 字符串，形状照 `server.js` 对应路由（如 `file-read` 返回 `{"content": "..."} `，错误 `{"error": "..."}`）。
  - JS：`window.bridge.call(name, argsJson) -> Promise<obj>`（`bridge.js`：`WebView2 postMessage` + `WebMessageReceived`，按消息自带的 id 关联请求与 Promise resolve）。
- 移植策略：底稿逐段拷贝 Demo `wiki-manager.html`（编辑、上传、树、配置、路径浏览、提示词卡全部保留）；把面板里所有 `fetch('/api/…')` 调用点替换为 `bridge.call('命令名', args)`（调用集中在少量 helper 函数，改 helper 即可）；新建库弹窗加场景模板下拉（通用/研究/读书/业务）；docx/xlsx 仅提供上传与"预览转换副本"入口，不提供面板内编辑。

- [ ] **Step 1: 写 BridgeDispatcher 失败测试**（xUnit 对每个命令做参数校验/成功路径各一：`kbs-create` 非法名 error、`file-read` 越权 error、`settings-set` 单反斜杠 error）
- [ ] **Step 2: dotnet test → FAIL**
- [ ] **Step 3: 实现各命令服务路由 + bridge.js**
- [ ] **Step 4: dotnet test → PASS**
- [ ] **Step 5: 前端移植落到 wwwroot**（验收链：建库/删库、传 md、编辑保存、树浏览、存储路径改盘符与 UNC，全部手验）
- [ ] **Step 6: Commit** `git commit -m "feat: message bridge and ported management panel"`

### Task 7: PromptEngine（六类提示词 + 边界说明）

**Files:**
- Create: `code/src/LlmWiki.App/Services/PromptEngine.cs`
- Create: `code/src/LlmWiki.App/wwwroot/prompt-templates.js`（模板串，替代 Demo `prompts/*.md` 中需要面板填充的类型）
- Test: `code/tests/LlmWiki.Tests/PromptEngineTests.cs`

**Interfaces:**
- Consumes: `FileBridge.Sources/Changes`（增量 Ingest 时列全部源或带 hash 的待处理变更）、`KbService`/`ConfigService`（取绝对路径）。Demo `prompts/*.md` 与"边界说明固定格式"（`prompts/context.md`）为原文权威。
- Produces: `class PromptEngine { string Boundary(string kb); string Ingest(string kb, string[]? files); string IngestChanges(string kb, Change[] selected); string Query(string kb, string question); string Lint(string kb); string Compare(string kb, string topic); string Clean(string kb, string deletedFile); }`
- Clean 模板（新增，全文进 prompt-templates.js）：

```text
（先读 {{abs_root}}\AGENTS.md 与 wikis/{{kb}}/wiki/index.md）
知识库根目录（绝对）：{{abs_root}}\{{kb}}

源文档 {{file}} 已被删除（raw/sources/ 中副本与 raw/original/ 中原件均已移除），请按以下级联清理规则更新 wiki：
1. 删除 wiki/sources/ 下该源的摘要页（如存在）
2. 从 wiki/index.md 移除所有指到该源或该摘要页的条目
3. 从其余页面移除指向被删页面的失效 [[链接]]，行文需要时改为指向最近的替代页
4. 共享实体/概念页不要整页删除：仅把 frontmatter sources 数组里该源条目移除
5. 在 wiki/log.md 追加一行记录本次清理
```

- 边界说明按 Demo 五段格式生成，第 3 段补一句 office 说明、第 5 段补 clean 语义（spec 04 §2 的三处增量之一）。

- [ ] **Step 1: 写失败测试**（Ingest 单文件/多文件/留空列出全部源；增量含 hash 跳过句与"只处理以下文件"；Boundary 含绝对路径与 purpose/schema 摘要；Clean 含 `{{abs_root}}\{{kb}}` 展开后的真实路径）
- [ ] **Step 2: dotnet test → FAIL**
- [ ] **Step 3: 实现模板与填充**（AGENTS.md/使用说明随发布物打包进 `AppRoot/agents/`，生成提示词时引用）
- [ ] **Step 4: dotnet test → PASS**
- [ ] **Step 5: 前端工作流页接 6 张卡 + Clean 变更清单联动，手验复制内容**
- [ ] **Step 6: Commit** `git commit -m "feat: six prompt templates with clean cascade"`

### Task 8: 只读分享服务（Kestrel + share-ui）

**Files:**
- Create: `code/src/LlmWiki.App/Share/ShareService.cs`、`code/src/LlmWiki.App/wwwroot-share/index.html`（被进程内嵌为字符串或独立文件随程序集打包）
- Modify: `MainWindow`（分享开关接前端）

**Interfaces:**
- Consumes: `ConfigService.WikisDir`, `PathGuard`（同套防护）。
- Produces: `class ShareService { bool Running { get; } int Port { get; } void Start(int port, out string error); void Stop(); string LocalAddress(); string[] LanAddresses(); }`
- 路由（白名单，仅 GET）：`/` share-ui；`/api/kbs`（库名列表）；`/api/tree?kb=`；`/api/file?kb=&path=`（含原图/all office 副本预览文本与 md/txt 原文，附 `Content-Disposition` 下载分流 `/download`）；其它方法/路径一律 404/405（**白名单实现，spec 03 §4**）。share-ui 顶部横幅固定"只读视图 · 编辑请到主机"。
- 端口默认 8765，可改；停止/应用退出即释放。

- [ ] **Step 1: 写失败测试**（起服后（随机端口）：GET kbs=200、GET file 中 `..` 逃逸=400、POST 同路径=405、GET 提示词生成类不存在路由=404）
- [ ] **Step 2: dotnet test → FAIL**（测试用 `WebApplicationFactory` 或直接起 `IHost`）
- [ ] **Step 3: 实现 ShareService 与 share-ui（静态资源内嵌）**
- [ ] **Step 4: dotnet test → PASS**
- [ ] **Step 5: 手验手机/兄弟机浏览器访问 + 面板开关**
- [ ] **Step 6: Commit** `git commit -m "feat: read-only lan share service"`

### Task 9: 发布产物 + 使用说明

**Files:**
- Create: `code/publish.ps1`、`code/README.md`（新使用说明，替 Demo `使用说明.md` 的更新版）
- Modify: csproj（如需 `InvariantGlobalization=false` 等发布设置）

**Interfaces:**
- Produces: `code/publish.ps1` 脚本一行产出自包含可跑目录 `code/publish/LlmWiki/`（ RID `win-x64`，`self-contained`，含 ASP.NET Core + WindowsDesktop）；README 覆盖：装/跑、四格式说明、提示词链路、局域网共享与只读分享、常见问题（端口占用、GBK、WebView2 不可用检测提示——启动时 `CoreWebView2Environment.GetAvailableBrowserVersionString` 失败则 dialog 明示"请安装 WebView2 运行时"）。

- [ ] **Step 1: 写 publish.ps1 并执行** Run: `code\publish.ps1` Expected: `publish/LlmWiki/LlmWiki.exe` 可以从任意工作目录启动并建库
- [ ] **Step 2: 全功能手验一轮**（均可用默认路径快速建测试库）：建库/传 4 格式/编辑/提示词复制/分享开关
- [ ] **Step 3: 写 code/README.md**
- [ ] **Step 4: Commit** `git commit -m "chore: publish script, user guide, release packaging"`
