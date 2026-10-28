# Design: LLM Wiki 桌面客户端

The locked system. Every design pass reads this first and pages share it. Change it on purpose; the file is the rule.

## System
- Genre: 现代工作台（Linear / Notion 一档的 Operate 工具界面）
- Shape: 左侧 Pane + 工作区 Frame；管理面板由 WPF `NavigationView` 导航切换（六页按计划 P1-P2 逐步接线；左侧 Pane 默认展开，展开宽度 224px，折叠策略按计划 P1 实现）；分享页为横幅 + 树/文并排。
- Theme: 冷灰白系统 + 单一靛蓝强调（用户通过结构化问题钉选；Coral 纸面世界已于 2026-10-27 整体废弃）。控件视觉以 **WPF UI Library 官方 Fluent 主题样式** 为准，优先功能完整性，前期不做视觉抄袭；冷灰白 + 靛蓝由 Fluent 主题及设计 token 参考呈现，不做旧 Web 样式迁移。
- Mode: Operate —— 扫读性、一致性、原生预期优先；签名元素 = 工作流页的深色代码面板提示词输出区

## Tokens
`Wpf UI/` 主题资源字典（ThemesDictionary / ControlsDictionary）与设计 token 为**参考值集**（计划以控件原生 Fluent 样式呈现为主，不强制把 Web 端 token 全量映射为 WPF 资源；全 WPF 化后 WebView2 tokens.css 随前端移除；分享页仍为独立静态网页，内联同一套值）。前期保留 token 仅供后期视觉对齐参考，不强制落地。

```css
:root {
  --bg: #F6F8FB; --surface: #FFFFFF; --surface-2: #F1F4F9; --surface-3: #E9EDF4;
  --border: #E3E8EF; --border-strong: #CBD5E4; --border-hover: #AEBDD2;
  --text-1: #16233A; --text-2: #3E4C63; --text-3: #64748B; --text-faint: #94A3B8; /* 仅禁用 */
  --accent: #2E6BE6; --accent-hover: #245BD0; --accent-subtle: #EAF0FE; --accent-text: #2455C4;
  --danger: #D92D20; --success: #12805C;
  --code-bg: #16181D; --code-text: #DEE1E7; --code-muted: #8B8F9A;
  --font-ui: "Segoe UI Variable Text", "Segoe UI", -apple-system, "Microsoft YaHei UI", "PingFang SC", system-ui, sans-serif;
  --font-mono: "Cascadia Code", "Consolas", ui-monospace, monospace;
  --r-sm: 7px; --r-md: 9px; --r-lg: 14px; --sidebar-w: 224px;
}
```

## Rules（前期视觉以 WPF UI 控件原生 Fluent 样式呈现为主；以下规则为后期视觉优化参考，非 P0-P1 强制）
- **强调色纪律**：靛蓝只出现在主操作按钮、当前导航项、焦点环与复制成功语义；绝不作大面积底色或标题色。主按钮带纵向渐变与品牌色投影。
- **层级**：17px/650 页标题 → 14px/600 卡片标题 → 13px 正文 → 12px 辅助 → 11px 弱说明；字重拉层级，不用颜色。
- **密度**：控件 32px、表格行 40px 档、卡片内边距 16/20、间距走 4px 尺度（--sp-*）。
- **质感**：卡片 14px 圆角 + 双层柔和阴影（悬停加深、边框加强）；侧栏微渐变底、当前项带 3px 左指示条；品牌块与主按钮用同族渐变；Chromium 滚动条主题化。
- **字体**：系统栈（离线可用）；路径/时间戳/文件名用 `--font-mono` + tabular-nums；占位符用 `--text-3`（对比度达标）。
- **图标**：手绘 16px / stroke 1.5 / currentColor 内联 SVG，一库里。
- **深色代码面板**（签名）：`--code-*` 令牌；一键复制后按钮变 `--success` 绿"已复制"2 秒。
- **动效**：仅 transform/opacity 微动（按压、120ms）；reduced-motion 全瞬时。
- **新增页面**：复用 WPF 主题资源 + token；分享页改 token 时两处同步（文件头有注释提示）。

## Exports
`Wpf UI/` 主题资源字典（ThemesDictionary / ControlsDictionary + 设计 token 参考）；方向契约存档于 `.impeccable/surfaces/panel.md`。全 WPF 化后主题资源随 WPF 控件主题迁移，不再强制"唯一事实源"。
