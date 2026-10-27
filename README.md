# LLM Wiki 桌面客户端

> **AI 链路不内置 LLM**：面板生成提示词 → 人复制给外部 Agent → Agent 改文件。

C# (.NET 10) + WPF + WebView2 桌面客户端。支持 `.md` / `.txt` / `.docx` / `.xlsx` 四格式。

---

## 安装 & 运行

### 方式 1：直接运行发布版（推荐）

```powershell
cd code\publish\LlmWiki
.\LlmWiki.exe
```

无需安装 .NET SDK，已内附运行时。

### 方式 2：源码构建

```powershell
cd code
dotnet build
dotnet run --project src/LlmWiki.App
```

> **WebView2 运行时**：首次启动如弹窗提示，请安装 [Microsoft Edge WebView2](https://aka.ms/webview2)。
> 检测命令：`dotnet run` 后如窗口闪退，先查 `WebView2.Runtime` 是否安装。

---

## 四格式说明

| 格式 | 上传方式 | 存储位置 | AI 引用格式 |
|------|---------|---------|-------------|
| `.md` / `.txt` | 直接入库 | `raw/sources/<name>` | `[来源: 文件名]` |
| `.docx` | 成对转换 | `raw/original/<name>` + `raw/sources/<name>.md` | 仍写 `[来源: 原文件名.docx]` |
| `.xlsx` | 成对转换 | `raw/original/<name>` + `raw/sources/<name>.md` | 同上 |

- **引用约定**：`[来源:]` 中的"文件名"永远是原件名（如 `调研报告.docx`），不是 `.docx.md` 副本。
- **大小上限**：20MB。转换失败不落盘。

---

## 提示词链路

1. 进入 **工作流页**，点击生成**边界说明** → 复制 → 粘贴进 Agent 配置（如 `AGENTS.md`）。
2. 生成 **Ingest / Query / Lint / Compare** 提示词 → 复制 → 粘贴给外部 Agent 执行。
3. **Clean** 卡片：删除源文档后，生成级联清理提示词（删摘要页、清 index、清失效链接）。

> 提示词含知识库**绝对路径**，在哪台机器生成就用哪台机器的路径。

---

## 局域网共享 & 只读浏览

### 主方案：多设备共享目录
- 把知识库目录设为 Windows 共享；其它电脑配置存储路径为 `\\主机IP\wikis` 即可。
- 编辑约定：不要同时改同一个文件，改完源的负责重新 Ingest。

### 辅助方案：只读网页
- 在面板**分享页**打开服务 → 端口默认 8765（可改） → 扫描二维码或访问 `http://主机IP:8765/`。
- 仅 GET：浏览目录树、预览 md/txt/docx(xlsx→md)、下载原件。
- 禁止：上传、编辑、删除、改配置、生成提示词。

> **安全**：仅在可信局域网使用，不得暴露公网。

---

## 常见问题

| 问题 | 解决 |
|------|------|
| 端口 8765 占用 | 在分享页改端口后重启分享服务 |
| GBK txt 显示乱码 | 自动检测编码，统一转存为 UTF-8 |
| 删除知识库 | 输入库名二次确认；含 `raw/original/` 原件一并删除，不可恢复 |
| 分享页二维码没生成 | 手动访问 `http://主机IP:端口/` |
| WebView2 无法访问网络 | 右键 "以管理员身份运行" 绕过 Windows 网络限制 |

---

## 项目结构

```
code/
├── src/LlmWiki.App/
│   ├── Services/        # PathGuard, ConfigService, KbService, FileBridge, PromptEngine
│   ├── Bridge/          # BridgeDispatcher + BridgeProtocol (JS↔C#)
│   ├── Share/           # ShareService (Kestrel 只读服务)
│   ├── wwwroot/         # 管理面板 (WPF WebView2 加载)
│   ├── wwwroot-share/   # 只读分享页面
│   ├── App.xaml / MainWindow.xaml
│   └── LlmWiki.App.csproj
├── tests/LlmWiki.Tests/ # xUnit 全部测试
└── publish.ps1          # 一键发布脚本
```
