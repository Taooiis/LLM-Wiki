# LLM Wiki 桌面客户端

<div align="center">

[![Release](https://img.shields.io/github/v/release/nashsu/llm_wiki?style=for-the-badge&color=brightgreen)](https://github.com/nashsu/llm_wiki/releases)
[![License](https://img.shields.io/github/license/nashsu/llm_wiki?style=for-the-badge)](LICENSE)
[![License](https://img.shields.io/github/languages/top/nashsu/llm_wiki?style=for-the-badge)]

</div>

> **AI 链路不内置 LLM**：面板生成提示词 → 人复制给外部 Agent → Agent 改文件。

![界面示意图](docs/screenshot-main.png)

---

## 功能特性

| 功能 | 说明 |
|------|------|
| **知识库管理** | 多库切换、新建/删除知识库，支持 UNC 网络路径 |
| **文件上传** | `.md`/`.txt`/`.docx`/`.xlsx` 四格式，Office 文件自动转 Markdown |
| **文档编辑** | 内置编辑器，支持 Ctrl+S 保存，Esc 关闭，dirty 标记，toast 提示 |
| **变更跟踪** | 自动记录 `raw/sources/` 变更，待处理变更列表勾选 |
| **批量操作** | 选中变更 → 一键生成 Ingest 提示词 → 自动切到工作流页 |
| **局域网共享** | 只读网页浏览，支持 mDNS (`llmwiki.local:端口`) |
| **自动更新** | 启动检查 GitHub Releases，新版本 toast 提示 |

---

## 安装 & 运行

### 方式 1：直接运行发布版（推荐）

```powershell
# 下载最新 Release 包后解压
cd LlmWiki
.\LlmWiki.exe
```

✅ 已内附 .NET 10 Runtime，零依赖

### 方式 2：源码构建

```powershell
git clone https://github.com/nashsu/llm_wiki.git
cd llm_wiki/Wpf UI
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained
```

> **WPF UI**：界面控件全部由 [WPF UI Library 4.2.0](https://github.com/lepo-co/wpf-ui) 提供；如启动异常请检查版本兼容性。

---

## 四格式说明

| 格式 | 上传方式 | 存储位置 | AI 引用格式 |
|------|---------|---------|-------------|
| `.md` / `.txt` | 直接入库 | `raw/sources/<name>` | `[来源: 文件名]` |
| `.docx` | 成对转换 | `raw/original/<name>` + `raw/sources/<name>.md` | `[来源: 原文件名.docx]` |
| `.xlsx` | 成对转换 | `raw/original/<name>` + `raw/sources/<name>.md` | `[来源: 原文件名.xlsx]` |

- **引用约定**：`[来源:]` 中的"文件名"永远是原件名
- **大小上限**：20MB，转换失败不落盘

---

## 提示词链路

1. 进入**工作流页** → 生成**边界说明** → 复制 → 粘贴进 Agent 配置
2. 生成**Ingest / Query / Lint / Compare** 提示词 → 复制 → 粘贴给 Agent 执行
3. **Clean** 卡片：生成级联清理提示词（删摘要页、清 index、清失效链接）

> 提示词含知识库**绝对路径**，在哪台机器生成就用哪台机器的路径。

---

## 局域网共享 & 只读浏览

### 主方案：多设备共享目录
- 把知识库目录设为 Windows 共享；其它电脑配置存储路径为 `\\主机IP\wikis` 即可

### 辅助方案：只读网页
- 在**分享页**打开服务 → 扫描二维码或访问 `http://主机IP:8765/`
- 支持目录树浏览、md/txt 预览、docx/xlsx 下载
- 仅 GET：上传、编辑、删除、改配置、生成提示词均禁止

> **安全**：仅在可信局域网使用，不得暴露公网。

---

## FAQ

| 问题 | 解决 |
|------|------|
| 端口 8765 占用 | 修改分享页端口后重启分享服务 |
| GBK txt 显示乱码 | 自动检测编码，统一转存为 UTF-8 |
| 删除知识库 | 输入库名二次确认；含 `raw/original/` 原件一并删除 |
| 分享页二维码没生成 | 手动访问 `http://主机IP:端口/` |
| WPF 界面网络/上传失败 | 右键 exe 以管理员身份运行 |

---

## 项目结构

```
Wpf UI/
├── Views/Pages/        # 6 个 Tab 页面
├── ViewModels/Pages/   # CommunityToolkit.Mvvm ViewModels
├── Services/           # 服务层：KbService, FileBridge, Share, Upload, ...
├── Views/Dialogs/      # 弹窗：EditorDialog, FolderPickerDialog, ConfirmationDialog
├── install/            # Inno Setup 安装脚本
├── publish.ps1          # 一键发布脚本
└── LLM Wiki.csproj      # WPF-UI 4.2.0 + .NET 10

code/
├── src/LlmWiki.App/    # 旧宿主内核层
├── tests/LlmWiki.Tests/ # xUnit 单元测试
└── publish.ps1         # 旧版发布脚本
```

---

## License

MIT License - 详见 [LICENSE](LICENSE)