# Surface brief: 管理面板 + 只读分享页

> 注：impeccable 引擎无网络不可用，本 brief 按 skill 降级规则手工撰写；方向由用户通过结构化问题指定（pinned direction beats the roll）。

## Scope & mode
- Surface: `code/src/LlmWiki.App/wwwroot/index.html`（管理面板）+ `wwwroot-share/index.html`（只读分享页）
- Mode: **Operate**（单机维护知识库的工具；扫码率、一致性、原生预期优先）
- Build path: **code-led**（Operate 模式规则；无图像生成）

## Direction contract
**THESIS**: 一台安静的本地工作台：所有维护动作在一臂之内，导航常驻、内容为王；拒绝"表单堆在页面顶部"的设置页长相。

**OWN-WORLD**: 冷灰白系统（ground #F7F7F9，白色卡片，发丝线 #E4E4EA），单一靛蓝强调 #3E63DD 只做主操作与当前态；系统字体栈（Segoe UI / 微软雅黑）靠字重与字号拉层级；路径与时间戳用等宽数字；签名 = 工作流页的深色代码面板输出区（提示词是产品的事实产出物，长得像代码块）。

**STORY**: 用户三秒内知道：左边选页面、左下选库、右边干活；提示词生成 → 复制 → 粘贴给 Agent 的链路一眼可见。

**FIRST VIEWPORT**: 左栏 224px（品牌 14px/650 + 6 个带图标导航项 + 底部知识库选择器与可折叠"新建/存储路径"）+ 工作区页头（17px/650 标题 + 13px 说明 + 页级主操作按钮）+ 白卡片内容区；源文档页首屏 = 页头 + 工具行 + 文档表格卡。

**FORM**: 用户钉选"现代工作台 + 左侧边栏"；候选对决中"Windows 11 原生 Fluent"败于"现代工作台"（一致性跨平台更强、信息密度更高），其系统圆角与克制借为 discipline；被否的 Coral 纸面世界整体废弃。

**SIGNATURE INTERACTION**: 侧栏当前项的强调态瞬时切换 + 提示词输出深色面板内"一键复制"后按钮变成功态（绿色"已复制"），无其他装饰动效。

**FINISH**: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance.

## Constraints
- 全部 id/onclick/JS 处理器不变；bridge.js、prompt-templates.js、C# 不动
- 分享页单文件（Kestrel 字符串直出），CSS 内联；测试锚点字符串"只读视图""编辑请到主机"保留
- 离线可用：系统字体栈，无 CDN 字体
