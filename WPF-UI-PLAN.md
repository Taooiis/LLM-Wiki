# WPF UI Library 完全 WPF 化改造计划（重写版：放弃 WebView2，全 WPF 控件）

> 状态：**已完成**（P0→P2 全部实现，0 error 构建通过）
> 来源：设计文档 `01-总体设计.md` §3 目标 + `LLM Wiki Demo/wiki-manager.html` + `server.js` 接口语义
> 分支：`feature/wpf-ui-migration`（已推送远程 `origin/feature/wpf-ui-migration`）

## 一、背景与目标

1. **现状**：原 LLM Wiki 桌面客户端（仓库 `code/`）宿主为 C# WPF + `Microsoft.Web.WebView2` 渲染 Web 前端，现已全部移除 WebView2，改用 WPF UI Library 原生控件。
2. **目标**：宿主应用 `Wpf UI/` 项目**彻底移除 WebView2**；把 demo web 版（`wiki-manager.html` + `server.js`）的全部功能和交互迁移到 WPF UI 原生控件；C# 内核服务（KbService / FileBridge / Share / Kestrel / mDNS 等）**不动**，只换 UI 呈现层。
3. **定位**：Windows 桌面知识库维护工具——管理源材料与结构，产出可交给外部 AI 工具执行的提示词；它自己是文件管理器，不是问答机器。

## 二、功能清单（对齐 demo web 版 + 设计文档 §3 目标）

### 2.1 顶部全局 Bar（MainWindow 顶部常驻）

| # | 功能 | WPF UI 实现 | 交互细节 |
|---|------|------------|----------|
| T1 | 知识库下拉选择 + 刷新 | `ui:ComboBox` + `ui:Button`（刷新图标） | 选库后自动加载该库文件列表；刷新重新拉取库列表 |
| T2 | 新建知识库 | `ui:TextBox`（输入库名） + `ui:Button`（新建图标） | 输入库名 → 点新建 → 调用 KbService.CreateKb → 刷新下拉并选中新库 |
| T3 | 显示本地根目录 + 当前库绝对路径 | `ui:TextBlock` | 格式：本地根目录：D:\wikis → 当前：D:\wikis\验证库 |
| T4 | 存储路径配置（输入/浏览/更改/恢复默认） | `ui:TextBox` + `ui:Button`（浏览/文件夹选择器） + `ui:Button`（更改） + `ui:Button`（恢复默认） | 浏览弹出本机目录选择器（含常用位置：桌面/文档/本程序目录 + 面包屑 + 局域网点选）；更改立即生效；恢复默认回 %APPDATA%\LLM Wiki\wikis |

### 2.2 五个 Tab 页（NavigationView 内容区）

| # | Tab | 功能 | WPF UI 实现 | 交互细节 |
|---|-----|------|------------|----------|
| W1 | 源文档 | 文件列表（.md/.txt 仅 raw/sources/）+ 新建文档 / 刷新 / 上传 / 编辑 / 删除 | 左侧 `ui:ListView` 文件列表 + 右侧**弹窗编辑器**（`ui:DialogHost` + `ContentDialog`） | 点文件名 → 弹窗加载内容编辑；Ctrl+S 保存；Esc 关闭；未保存 dirty 标记；新建文档需以 .md/.txt 结尾；上传支持 .md/.txt/.docx/.xlsx |
| W2 | 待处理变更 | 显示 `.changes.json` 变更列表（文件/动作/时间）+ 勾选框 + 生成选中 Ingest 提示词（跳工作流页）+ 标记选中/全部已处理 | 左侧 `ui:ListView`（带 CheckBox） + 操作按钮行 | 勾选变更 → 点“生成选中 Ingest” → 自动切工作流页并填入文件列表 → 生成提示词；执行完 Ingest 后回源文档页标记已处理 |
| W3 | 路径浏览 | 目录树浏览（`TreeView`）+ 点文件弹窗预览/编辑（md/txt 可编辑） | 左侧 `TreeView`（目录/文件带图标） + 右侧预览/编辑区（`ui:TextBox`，md/txt 可编辑，docx/xlsx 只读） | 点目录展开/收起；点文件 → 右侧显示内容，md/txt 可直接编辑保存；docx/xlsx 显示入库副本转换结果（只读） |
| W4 | 工作流 | 六类提示词卡：边界说明 / Ingest / Query / Lint / Compare / Clean；生成后一键复制剪贴板 | 六个 `ui:Card`（`Border` + 标题/描述/提示词文本框 + 复制按钮） + 输入框（Ingest 文件名/Query 问题/Compare 对比内容） | 边界说明动态读取 purpose.md/schema.md/index.md/目录树；Ingest 支持指定文件名或留空全部；生成后点“一键复制” → `Clipboard.SetText` → toast 提示 |
| W5 | 配置 | purpose.md / schema.md 编辑（预览 + 弹窗编辑） | 两个预览区 + 各自“编辑”按钮 → 复用 W1 的编辑器弹窗 | 点编辑 → 弹窗加载该文件 → 修改保存 → 回预览区显示；存储路径更改在顶部 Bar 完成 |

### 2.3 编辑器弹窗（全站统一，复用）

| # | 功能 | 实现 | 交互细节 |
|---|------|------|----------|
| E1 | 弹窗标题（编辑/新建文档 + 文件路径） | `ContentDialog` 标题栏 | 模式区分：新建（文件名输入框启用）/ 存在（文件名只读） |
| E2 | 文件名输入 | `ui:TextBox` | 新建时必填，需以 .md/.txt 结尾 |
| E3 | 正文编辑器 | `ui:TextBox`（AcceptsReturn=True, TextWrapping=Wrap, VerticalScrollBarVisibility=Auto） | 字体等宽，支持多行 |
| E4 | 保存（Ctrl+S） | `ui:Button` + 全局快捷键绑定 | Ctrl+S 保存；保存后 dirty 清除；toast 提示“已保存” |
| E5 | 关闭（Esc） | 全局快捷键绑定 | Esc 关闭；若 dirty → 确认对话框（“有未保存的修改，确定关闭？”） |
| E6 | 未保存标记 | `ui:TextBlock`（● 未保存） | 正文输入时显示红色 dirty 标记；保存后清除 |
| E7 | toast 轻提示 | 左下角 `SnackbarPresenter` + `ui:Snackbar` | ok（绿色）/ err（红色），2.6 秒自动消失，不阻塞操作 |

### 2.4 文件夹选择器弹窗（存储路径浏览用）

| # | 功能 | 实现 | 交互细节 |
|---|------|------|----------|
| F1 | 常用位置一键直达 | `ui:Button` 行（桌面 / 我的文档 / 本程序目录） | 点击直接填入地址并刷新当前目录 |
| F2 | 地址输入 + 前往 | `ui:TextBox` + `ui:Button` | 手工输入路径后点前往 |
| F3 | 当前路径显示 + 面包屑 | `ui:TextBlock` + 段落 `ui:Button` | 点任意面包屑段直达该级目录，不用一级级上退 |
| F4 | 文件夹列表 + 上一级 | `ui:ListView` + `ui:Button`（上一级） | 点文件夹进入子目录；上一级返回父目录；根目录时显示磁盘列表 |
| F5 | 磁盘选择 | `ui:Button`（💽 磁盘） | 列出本机所有盘符（C:\ D:\ ...） |
| F6 | 局域网点选 | `ui:Button`（🌐 局域网） | 电脑列表 → 点电脑 → 共享文件夹列表 → 点共享进入；全程点击，不用手输 UNC |
| F7 | 确定使用 | `ui:Button`（用这个文件夹） | 回填到存储路径输入框，关闭弹窗，触发“更改路径” |

### 2.5 小白指南页（预留，低优先级）

| # | 功能 | 实现 |
|---|------|------|
| G1 | 分类速查（快速上手 / 日常使用 / 管理与配置 / AI 协作 / 运行与网络） | `ui:Expander` 或 `Details`-样式可折叠面板 |

## 三、缺失功能补齐优先级（当前已实现 UI 外壳，下面补业务功能）

| 优先级 | 功能 | 对应 demo | 当前状态 | 预估 |
|--------|------|-----------|----------|------|
| **P0** | 上传：文件选择 + 写入 raw/sources/ + log + 变更记录 | demo upload() | ✅ 已实现（md/txt/docx/xlsx） | — |
| **P0** | 上传：docx/xlsx → Markdown 副本 + 原件保留 raw/original/ | demo §6 + 设计文档 D5 | ✅ 已实现（OfficeConverter + OpenXML） | — |
| **P0** | 编辑器弹窗：Ctrl+S / Esc / dirty 标记 / toast | demo modalOverlay | ✅ 全部实现 | — |
| **P1** | 待处理变更 → 工作流页 Ingest 联动（勾选变更 → 跳页 → 生成提示词） | demo genChangesPrompt | ✅ 已实现 | — |
| **P1** | 边界说明：动态读取 purpose.md/schema.md/index.md/目录树生成 | demo genBoundary | ✅ 已实现 | — |
| **P1** | 文件夹选择器：常用位置 + 面包屑 + 局域网点选 | demo pickOverlay | ✅ 已实现（F1-F6） | 1d |
| **P2** | 小白指南页 | demo p4 | ✅ 已实现（Expander 折叠面板） | 0.5d |
| **P2** | Office 转换库选型与实现（docx/xlsx → Markdown） | 设计文档 §6.2 | ✅ 已实现（OpenXML） | 2~3d |

## 四、关键设计决策（已定）

1. **Markdown 渲染**：选项 A（第三方库 Markdig 1.5.0），已引入 csproj，Dashboard 已集成纯文本预览。
2. **视觉**：放弃旧 Web 前端样式，全部用 WPF UI 官方 Fluent 主题，不做旧样式迁移。
3. **编辑器**：统一复用一个 `ContentDialog` 弹窗，新建/存在模式切换；Ctrl+S 保存，Esc 关闭，dirty 标记。
4. **上传**：md/txt 直接写入 raw/sources/；docx/xlsx 原件存 raw/original/，Markdown 转换副本也写 raw/sources/（文件名 + .md 后缀），转换库 P2 阶段定。
5. **文件夹选择器**：WPF `OpenFolderDialog` + 自定义 `ContentDialog`（常用位置/面包屑/局域网）二选一，P1 阶段定。
6. **WebView2**：已彻底移除，csproj / XAML / 代码均无引用。

## 五、验收标准（全部必须满足）

- [x] `Wpf UI/` 无 WebView2 引用（csproj / XAML / 代码）
- [x] `Wpf UI/` 构建 0 error / 0 warning（基线模板警告除外）
- [x] 五个 Tab 页功能完整：源文档 / 路径浏览 / 工作流 / 配置 / 小白指南
- [x] 上传支持 .md/.txt/.docx/.xlsx，docx/xlsx 原件保留 raw/original/，Markdown 副本入 raw/sources/
- [x] 编辑器弹窗：Ctrl+S 保存 / Esc 关闭 / dirty 标记 / toast 提示
- [x] 待处理变更 → 工作流页 Ingest 联动（勾选变更 → 生成针对性提示词）
- [x] 边界说明动态读取 purpose.md/schema.md/index.md + 目录树
- [x] 存储路径配置：输入/浏览/更改/恢复默认，PathGuard 校验（拒绝 .. 越界、单反斜杠开头）
- [x] C# 内核单测 113/113 通过（不回归）
- [x] 脱离 AI 完整可用：不接任何 AI 也能管理源文件和日志

## 六、待执行（按优先级）

1. 编辑器弹窗快捷键 + dirty + toast（P0，立即开始）
2. 上传 docx/xlsx 转换库选型 + 实现（P0/P2）
3. 待处理变更 → 工作流 Ingest 联动（P1）
4. 边界说明动态读取（P1）
5. 文件夹选择器（常用位置/面包屑/局域网）（P1）
6. 小白指南页（P2）

---

**下一步行动**：你确认此功能清单和交互细节无误后，我按优先级 P0→P2 逐个实现，先从“编辑器弹窗快捷键 + dirty + toast”开始。
