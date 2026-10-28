# LLM Wiki 桌面客户端 — 开发进度

> Status: **全部 Task 完成** | Branch: `feature/file-association`

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

## 2026-10-27 文件关联

**100/100 测试通过，0 错误 0 警告**

### 新增文件
- `Services/FileAssociation.cs` — 注册表 API 封装：`Register()`/`Unregister()`/`IsRegistered()`

### 改动文件
- `App.xaml.cs` — 命令行参数处理；管道协议扩展为 `show;path1;path2...` 传递文件路径到已运行实例
- `Bridge/BridgeDispatcher.cs` — 3 新命令 `file-assoc-get`/`file-assoc-register`/`file-assoc-unregister`
- `MainWindow.xaml.cs` — `PendingFiles` 属性 + `ImportFilesFromCommandLine()`（复用拖拽上传流程）+ `WaitForWebViewReady()`
- `wwwroot/bridge.js` — 2 新路由 `/api/file-assoc` + `/api/file-assoc/unregister`
- `wwwroot/index.html` — 设置面板加「文件关联」section + `toggleFileAssoc`/`loadFileAssoc` JS
- `BridgeDispatcherTests.cs` — 1 新测试

### 验收
- [x] `dotnet build` 0 错误 0 警告
- [x] `dotnet test` 100/100 通过
- [x] Push 到 `origin/feature/file-association`
- [ ] 手动验证：双击 `.md` 文件 → LLM Wiki 打开并导入；右键「发送到 LLM Wiki」

## 2026-10-27 mDNS 局域网自动发现

**113/113 测试通过，0 错误 0 警告**

### 新增文件
- `Services/MdnsPacket.cs` — mDNS 纯协议层（RFC 6762 最小子集，只做报文编解码，不碰网络）
  - `EncodeName` / `BuildResponse` / `FindARecordFor` / `SkipName` / `TryReadName` / `IsResponse`
  - 只认 A 记录 + 自己的域名，其它查询一律不理（不抢机器上 Bonjour 等服务的流量）
- `Services/MdnsResponder.cs` — UDP 5353 组播应答器
  - 只答 `llmwiki.local` 的 A 记录；询问方要单播就单播回，否则组播回
  - 别人抢同一个域名就自动改名 `llmwiki-2` / `llmwiki-3`…（否则手机会连到别人的机器）
  - 5353 被占时静默失效，不影响 IP / 二维码路径

### 改动文件
- `Share/ShareService.cs` — Start/Stop 联动 mDNS 生命周期；新增 `FriendlyAddress()`
- `Bridge/BridgeDispatcher.cs` — `ShareInfo()` 返回 `friendlyAddress`（同样带 PIN）
- `wwwroot/index.html` — 分享页把 `llmwiki.local` 友好地址置顶显示
- `tests/LlmWiki.Tests/MdnsPacketTests.cs` — 12 个协议单测 + 1 个真实组播网络测试

### 关键坑（实测）
- Windows 上 `IP_MULTICAST_LOOP=false` 会连**收到的**组播也一起收不到 → 必须保持默认开启；自己发出的应答会被 QR 位过滤、冲突检测会认出自己的 IP，不会自问自答
- Windows 同端口组播只投一个 socket → 测试客户端要绑临时端口 + QCLASS 置 `0x8001`（unicast-response 位）
- 局域网存在其它 mDNS 流量（实测有 Apple `_companion-link._tcp.local` 查询）→ 按 QR 位区分查询/应答，不受影响

### 验收
- [x] `dotnet build` 0 错误 0 警告
- [x] `dotnet test` 113/113 通过（含真实组播问答网络测试）
- [x] 实测：组播查询 → 收到 `flags=0x8400, QD=1, AN=1` 应答，A 记录 = 本机局域网 IP
- [ ] 手动验证：分享页出现 `http://llmwiki.local:8765/?pin=XXXX`；手机同网段输该地址能打开

## 2026-10-28 桥消息监听修复（阻塞性 bug）

**现象**：开启分享无反应、页面列表/状态不刷新（所有面板交互失效）

**根因**：`wwwroot/bridge.js` 把宿主应答监听注册在 `window` 上（`window.addEventListener('message')`），
而 WebView2 的 `PostWebMessageAsString` 应答只投递到 `chrome.webview` 的事件对象上（运行时升级后行为变化）。
请求发得出去、C# 也正常处理并回包，但 JS 永远收不到 → `api()` 的 Promise 永不 resolve → 全部静默挂起。

**修复**：改为 `window.chrome.webview.addEventListener('message', handler)`（移除时用同一 target），
与已有的 `fs-change` 推送通道写法保持一致。

**验证**（CDP 远程调试实测）：
- 修复前：`api('/api/settings')` 8 秒超时；C# 侧日志显示 `recv → handled → posted response` 全部正常
- 修复后：`api('/api/settings')` 正常返回；`startShare()` 后状态变「运行中 · 端口 8765」，
  地址列表出现 `http://llmwiki.local:8765/?pin=XXXX`

## 下一步
- 手动验证批次：手机端打开 mDNS 友好地址 + 文件关联双击/右键导入
- 后续候选：API 超时兜底 / 数据目录迁 %APPDATA% / 安装包 + 自动更新 / 系统通知
- 桌面化改造清单见 [DESKTOP-PLAN.md](DESKTOP-PLAN.md)