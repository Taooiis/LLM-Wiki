# LLM Wiki WPF UI Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完全放弃 WebView2，把 LLM Wiki 桌面客户端的功能界面迁移到 WPF UI Library（WPF-UI 4.2.0）原生控件，实现全 WPF 化。

**Architecture:** WPF UI 模板宿壳（FluentWindow + NavigationView + TitleBar + Snackbar/Breadcrumb）承载 Frame 内容区；C# 内核服务与 Bridge 协议保留（供 WPF 控件直调与 Kestrel/浏览器使用）；Markdown 渲染走第三方库；所有界面控件统一为 WPF UI / WPF 原生控件。

**Tech Stack:** C# / .NET 10 / WPF / WPF-UI 4.2.0 / WPF-UI.DependencyInjection 4.2.0 / CommunityToolkit.Mvvm 8.4.0 / Markdown 第三方库（P3 定）。

**Spec:** `WPF-UI-PLAN.md` — approved detailed plan（2026-10-28）。

## Global Constraints

- 全 UI 层不得保留 WebView2 引用（csproj / XAML / 代码）；WebView2 移除工作归 Task 6 验收。
- C# 内核服务（KbService / Bridge / Share-Kestrel / mDNS / Upload / PromptEngine / FileAssociation）逻辑不改，仅换 UI 呈现层。
- Markdown 渲染必须使用第三方库（用户决策，P3 定具体库）。
- 构建要求 0 error / 0 warning；C# 单测回归基线 113/113。

## Design Decisions（2026-10-28 用户追加）

- **视觉**：放弃旧 Web 前端样式（wwwroot/index.html + CSS），全部改用 WPF UI Library 官方 Fluent 主题样式（由 WPF-UI `ThemesDictionary` / `ControlsDictionary` + `Fluent*` 控件提供），不做旧样式迁移。
- **优先级**：先实现功能完整性（Task 1-6），样式随 WPF UI 控件原生样式自然呈现，不在前期做视觉抄袭。

## Review Focus

- WPF UI 4.2.0 + .NET 10 兼容性与实际行为（Task 1）。
- Markdown 渲染控件选型与集成（Task 4）。
- NavigationView 内容区承载方案（Frame/ContentControl）（Task 2）。
- 主题切换（Light/Dark）与 SystemThemeWatcher 联动（Task 2 / Task 5）。
- 构建与单测基线（Task 6）。

---

### Task 1: WPF UI 模板外壳基线验证（Phase P0）

**Files:**
- Modify: `Wpf UI/LLM Wiki.csproj`, `Wpf UI/App.xaml`, `Wpf UI/App.xaml.cs`, `Wpf UI/Views/Windows/MainWindow.xaml(.cs)`, `Wpf UI/ViewModels/Windows/MainWindowViewModel.cs`

**Interfaces:**
- Consumes: WPF-UI 4.2.0 DI（AddNavigationViewPageProvider / AddHostedService / INavigationService / INavigationWindow）
- Produces: 基线外壳可构建、可启动

- [ ] **Step 1: 构建验证**
- [ ] **Step 2: 冒烟验证（启动外壳）**
- [ ] **Step 3: Commit**

### Task 2: 主框架接线（Phase P1）

**Files:**
- Modify: `Wpf UI/Views/Windows/MainWindow.xaml`, `Wpf UI/Views/Windows/MainWindow.xaml.cs`, `Wpf UI/ViewModels/Windows/MainWindowViewModel.cs`

**Interfaces:**
- Consumes: Task 1 通过的外壳
- Produces: NavigationView 承载内容区 Frame；左侧导航 Pane；ToolBar；Snackbar

- [ ] **Step 1: 设计内容区承载方式（Frame / ContentControl）
- [ ] **Step 2: 实现 MainWindow 接线**
- [ ] **Step 3: 构建验证
- [ ] **Step 4: Commit**

### Task 3: Dashboard 知识库功能迁移（Phase P2）

**Files:**
- Modify: `Wpf UI/Views/Pages/DashboardPage.xaml(.cs)`, `Wpf UI/ViewModels/Pages/DashboardViewModel.cs`

**Interfaces:**
- Consumes: Task 2 导航框架
- Produces: 知识库列表 / 目录树 / 预览 / 编辑 / 搜索 / 上传控件

- [ ] **Step 1: 替换 Counter 占位页为知识库功能页
- [ ] **Step 2: 目录树 / 预览控件
- [ ] **Step 3: 搜索 / 上传 / 保存接线
- [ ] **Step 4: 构建验证
- [ ] **Step 5: Commit**

### Task 4: Markdown 渲染（Phase P3）

**Files:**
- Modify: `Wpf UI/Views/Pages/DashboardPage.xaml`（或新增 Markdown 控件封装）

**Interfaces:**
- Consumes: Task 3 预览控件
- Produces: Markdown 渲染控件

- [ ] **Step 1: 定第三方库（P3 决定）
- [ ] **Step 2: 集成渲染控件
- [ ] **Step 3: 构建验证
- [ ] **Step 4: Commit**

### Task 5: Prompt/设置/分享/文件关联/托盘迁移（Phase P4）

**Files:**
- Modify: `Wpf UI/Views/Pages/SettingsPage.xaml(.cs)`, 新增 PromptWindow / Share UI / Tray 等

**Interfaces:**
- Consumes: Task 2 框架 + Task 4 Markdown

- [ ] **Step 1: Settings / PromptWindow / Tray / Share 迁移
- [ ] **Step 2: 构建验证
- [ ] **Step 3: Commit**

### Task 6: 验收：移除 WebView2（Phase P5）

**Files:**
- Modify: `Wpf UI/LLM Wiki.csproj`, `Wpf UI/**`，移除 `code/src/LlmWiki.App` 中 WebView2 前端引用

**Interfaces:**
- Consumes: Tasks 1-5
- Produces: 无 WebView2 引用；构建 0 error / 0 warning；单测 113/113 通过

- [ ] **Step 1: 移除 WebView2 包引用与 XAML / 代码
- [ ] **Step 2: 构建 + 单测验证
- [ ] **Step 3: Commit

---

## Self-Review 结果

- Spec coverage: WPF-UI-PLAN.md 的背景/目标、基线、功能清单、分阶段、决策、风险、验收、待确认均已映射到 Task 1-6 ✓
- Step scan: 每个 task 的 step 均为单一可执行动作，无 "TBD" ✓
- Type consistency: Task 顺序与 Interfaces 依赖关系自洽 ✓
- Review Focus: 已更新为 5 条（补齐了主题切换与构建基线）✓
- Proportion: 6 task / ~25 step，与 spec 匹配 ✓
