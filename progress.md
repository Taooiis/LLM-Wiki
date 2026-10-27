# LLM Wiki 桌面客户端 — 开发进度

> Status: **全部 9 个 Task 完成** | Branch: `feature/desktop-client`

## 已完成

| Task | 名称 | 测试 | 提交 |
|------|------|------|------|
| T1 | 方案骨架 + WPF 壳 + WebView2 | — | 21a47ac |
| T2 | PathGuard + ConfigService | 9/9 ✅ | 735146b |
| T3 | KbService + SceneTemplates + LogWriter | 5/5 ✅ | 735146b |
| T4 | FileBridge + HashUtil | 8/8 ✅ | 147ee63 |
| T5 | UploadService + OfficeConverter | 7/7 ✅ | 735146b |
| T6 | 桥通道 + 前端移植 | 19/19 ✅ | 21a47ac |
| T7 | PromptEngine + 桥协议修复 | 25/25 ✅ | 7159741 |
| T8 | 只读分享服务 (ShareService + UI) | 11/11 ✅ | 0b37dc5 |
| T9 | 发布产物 + 使用说明 | — | 61ca411 |

**当前总计：80/80 测试通过，0 错误 0 警告**

## Task 8 — 只读分享服务 (spec 03)

- `Share/ShareService.cs`：Kestrel 白名单服务，仅 GET 路由
  - 白名单：`/` `/api/kbs` `/api/tree` `/api/file` `/download`
  - 写方法 → 405，越权路径 → 400，未知路由 → 404
  - `Start(port)/Stop()/LocalAddress()/LanAddresses()`
- `BridgeDispatcher` 新增 `share-start`/`share-status`/`share-stop` 命令
- `bridge.js` 新增 `/api/share` + `/api/share/stop` 路由
- `wwwroot-share/index.html`：最小只读 UI（目录树 + md 渲染 + 链接跳转）
- `index.html` 新增 **分享** tab + `startShare`/`stopShare`/`loadShare`

### 验收清单
- [x] GET /api/kbs = 200，列库
- [x] GET / = share-ui HTML，含"只读视图 · 编辑请到主机"
- [x] GET /api/tree = 200，目录树
- [x] GET /api/file = 200，md 内容预览
- [x] GET /api/file with `..` = 400，路径越权拒绝
- [x] POST/PUT/DELETE = 405，方法拦截
- [x] GET /api/prompt = 404，提示词路由不存在
- [x] 发布产物运行验证（从 publish/LlmWiki 启动正常）

## 状态
- 全部 9 个 Task 完成 ✅
- 83/83 测试通过（新增：分享跟随存储路径、分享页 wiki/ 路径、原生选路径桥命令）
- `dotnet publish` 自包含输出验证可用

## 2026-10-27 实现修复 + UI 重设计
- **分享未生效根因**：`ShareService` 构造时固化 `config.WikisDir`（含注入的 FileBridge/KbService 实例），改存储路径后局域网仍服务旧目录 → 改为每请求现取；`Stop()` 补 `StopAsync` 优雅释放端口；分享页目录树改为带全路径（wiki/ 页面可点）
- **原生选路径**：桌面应用不再走网页面包屑选择器——`BridgeDispatcher.FolderPicker` + WPF `OpenFolderDialog` 系统对话框（可浏览网络共享），无宿主时前端自动回退旧选择器
- **UI**：管理面板与分享页整体重设计（现代工作台 + 左侧边栏 + slate 皮肤，见 `design.md`），全部接线与测试锚点保留

## 下一步
- Task 9：发布产物 + 使用说明 (`publish.ps1` + `code/README.md`)
