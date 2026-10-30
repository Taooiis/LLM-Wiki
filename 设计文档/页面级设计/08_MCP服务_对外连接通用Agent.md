# 08｜MCP 服务：对外连接通用 Agent

## 1. 页面目标与定位

本功能把当前知识库以 MCP（Model Context Protocol）服务的形式暴露给通用 Agent（如 Claude Desktop、Claude Code、Cursor 等支持 MCP 的客户端）。Agent 不再需要用户手动复制 04 工作流生成的提示词，而是通过标准协议自行读取知识库、执行检查或写入 `wiki/`。

定位约束：

- 本功能是 05「分享」的延伸，不是新的业务模块。知识库业务规则、文件落盘规则、Ingest 规则全部复用现有服务。
- 默认只读。写入能力必须由用户显式开启，并使用独立令牌。
- MCP 服务只暴露“知识库内容与受控写入”，不暴露配置修改、路径切换、服务启停等管理能力。
- 不把本功能描述为“Agent 已完成 Ingest/Lint”。Agent 的执行结果以 Agent 返回的内容和文件变更为准。

## 2. 与现有设计的关系

| 现有页面/能力 | 在 MCP 中的对应 | 说明 |
|---|---|---|
| 03 目录浏览 | Tool `list_tree`、Resource | 复用目录树服务，过滤系统文件规则保持一致 |
| 02 源文档 / 03 文件预览 | Tool `read_file` | 只读文件内容，编码与大小限制沿用现有预览规则 |
| 02 待处理变更 | Tool `get_pending_changes` | 读取 `.changes.json` 的待处理项，不暴露其内部结构以外的信息 |
| 04 七张工作流卡 | Prompts | 七类提示词作为 MCP Prompt 暴露，参数与卡片输入一致 |
| 05 局域网分享 | HTTP 传输宿主 | 复用同一宿主进程和端口配置，但认证方式独立 |
| 06 存储路径 | 只读上下文 | 仅展示当前知识库标识，不允许通过 MCP 切换路径 |

## 3. 架构分层

```text
┌──────────────────────────────────────────────────────────┐
│ WPF 桌面端（05 / 08 页面 UI）                             │
├──────────────────────────────────────────────────────────┤
│ LlmWiki.Core（业务与安全边界，唯一实现）                    │
│  KnowledgeBaseService / FileService / ChangeService       │
│  WorkflowPromptService / McpAccessPolicy                  │
├───────────────────────────┬──────────────────────────────┤
│ HTTP MCP 宿主              │ stdio MCP 可执行文件（只读）   │
│ 嵌入桌面进程，Kestrel       │ 独立 exe，供本机客户端拉起     │
│ MapMcp，随分享开关启停      │ 默认只读，不与桌面端共享写入    │
└───────────────────────────┴──────────────────────────────┘
```

要求：

- 所有能力只在 `LlmWiki.Core` 中实现一次。HTTP 与 stdio 只做协议适配，不得各自拼路径、各自读写文件。
- `McpAccessPolicy` 集中判断“某个工具在当前档位下是否允许”，两种传输共用。
- SDK 使用官方 `ModelContextProtocol.AspNetCore` 2.x 与 `ModelContextProtocol`（stdio）。版本在项目文件中锁定，不使用浮动版本号。
- 运行环境为 .NET 10。

## 4. 权限档位

| 档位 | 默认 | 可用能力 | 开启方式 |
|---|---|---|---|
| 关闭 | 是 | 无，MCP 端点不响应 | 默认状态 |
| 只读 | 否 | Resources、Prompts、只读 Tools | 在 08 页面开启 MCP 服务 |
| 写入 | 否 | 只读能力 + `write_wiki_file` | 在只读基础上单独勾选“允许 Agent 写入 wiki/”，二次确认 |

规则：

- 写入档位必须二次确认，并提示“Agent 将可以修改 `wiki/` 下的文件，修改会立即落盘”。
- 写入档位使用独立令牌（写令牌）。只读令牌无法调用任何写入工具，即使客户端伪造工具名。
- 切换到只读或关闭时，立即使进行中的写入请求失败，并返回明确错误码。
- 档位变化写入配置并在状态栏显示当前档位。

## 5. 暴露能力清单

### 5.1 Resources（只读）

URI 使用逻辑地址，不暴露本机绝对路径。

| URI 模板 | 内容 | 说明 |
|---|---|---|
| `wiki://{kb}/purpose.md` | 知识库目的 | 与 06 的 purpose.md 为同一文件 |
| `wiki://{kb}/schema.md` | 知识库结构约定 | 与 06 的 schema.md 为同一文件 |
| `wiki://{kb}/index` | 目录索引 | 由 `list_tree` 生成，不超过设定的条目上限 |
| `wiki://{kb}/{relative_path}` | 单个文件内容 | 路径规则同 5.3 的 `read_file` |

`{kb}` 为知识库标识（即目录名）。

### 5.2 Prompts（对应 04 的七张卡）

| Prompt 名称 | 参数 | 对应卡片 |
|---|---|---|
| `boundary` | 无 | ① 边界说明 |
| `batch_ingest` | `files`（可选，逗号分隔相对路径，空=全部源文档） | ② 批量 Ingest |
| `incremental_ingest` | 无 | ③ 增量 Ingest，基于当前 `.changes.json` |
| `query` | `question`（必填） | ④ Query |
| `lint` | 无 | ⑤ Lint |
| `compare` | `topic`（必填） | ⑥ Compare |
| `clean` | 无 | ⑦ Clean，仅生成清理计划的指令 |

Prompt 的生成逻辑直接调用 04 的 `WorkflowPromptService`，文案与 04 中的卡片保持一致。`batch_ingest` 的路径前缀归一化规则同样适用。

### 5.3 Tools（只读，只读档及以上可用）

| Tool | 参数 | 返回 | 说明 |
|---|---|---|---|
| `list_knowledge_bases` | 无 | 知识库标识、名称、是否为当前选中库 | 不返回本机路径 |
| `list_tree` | `kb`、`relative_dir`（可选，默认根） | 条目名、类型、相对路径、是否只读 | 过滤 `.changes.json` 等内部文件，与 03 规则一致 |
| `read_file` | `kb`、`relative_path` | 文本内容、编码、是否截断 | 仅允许白名单类型（md、txt、json），单文件大小上限沿用预览上限 |
| `search_content` | `kb`、`query`、`limit`（默认 20，最大 50） | 匹配文件的相对路径、片段（每个不超过 200 字符） | 只扫描 `wiki/` 与 `raw/sources/` 文本文件；大库需异步或限流 |
| `get_pending_changes` | `kb` | 变更列表：相对路径、类型（新增/修改/删除）、时间 | 只读，不清除待处理状态 |

明确不提供：

- 不提供 `MarkChangesProcessed`。标记已处理仍由用户在 02 源文档页确认，避免 Agent 在未执行完成时清除变更。
- 不提供任何删除工具。Clean 只通过 Prompt 生成计划，删除动作由用户在应用内或本机手动确认。
- 不提供路径切换、配置读写（`purpose.md`、`schema.md` 的写入只能走 5.4 的写入工具且限定 `wiki/`，不允许写这两个文件）。

### 5.4 Tools（写入，仅写入档可用）

| Tool | 参数 | 返回 | 说明 |
|---|---|---|---|
| `write_wiki_file` | `kb`、`relative_path`（必须以 `wiki/` 开头）、`content`、`expected_version`（可选） | 新版本号、写入时间、是否登记为变更 | 写回原相对路径；内容大小上限与编辑器一致 |

写入约束：

- `relative_path` 规范化后必须位于 `kb/wiki/` 之下。
- 禁止写入：`raw/sources/`、`raw/original/`、`raw/assets/`、`.changes.json`、`purpose.md`、`schema.md`、`wiki/log.md`（日志仅追加，不由 Agent 改写）。
- 可选参数 `expected_version` 用于乐观并发：若文件已被用户修改，返回冲突错误，不覆盖。
- 写入通过现有保存服务完成，遵循现有的变更登记规则。Wiki 文件修改不登记为源文档变更，除非原规则明确要求。
- 写入失败时不产生部分写入（先写临时文件，成功后替换）。

## 6. 路径与输入安全

所有接收路径的工具（`list_tree`、`read_file`、`write_wiki_file`、Resources）共用同一套校验：

1. 拒绝绝对路径、盘符、UNC 路径。
2. 拒绝包含 `..` 段、空段或控制字符的路径。
3. 统一转换为知识库根目录下的相对路径后，再次确认解析结果仍在根目录内（防符号链接与大小写绕过）。
4. 符号链接和 junction 不跟随；遇到即视为不可访问。
5. `kb` 必须是 `list_knowledge_bases` 返回的标识，不接受任意路径。
6. 所有字符串参数设长度上限（路径 260 字符，内容按编辑器上限）。

这些校验放在 `McpAccessPolicy` 与 `FileService` 中，不依赖客户端传参的可信度。

## 7. 认证与网络

### 7.1 HTTP 传输

- 默认只监听 `127.0.0.1`。开启“允许局域网访问”后才监听 `0.0.0.0`，并在界面明确提示。
- 所有请求必须携带 `Authorization: Bearer <token>`。
- 校验 `Origin` 头：仅允许无 Origin 的请求（原生客户端）或在白名单内的来源，防止浏览器跨站调用。
- 令牌由系统生成，长度不少于 32 字节随机数，使用 DPAPI 加密保存，不写入普通配置明文。
- 令牌可“重新生成”。重新生成后旧令牌立即失效，界面需提示会断开现有客户端。
- 令牌不写入日志、错误详情或调试输出。

### 7.2 stdio 传输

- 作为独立 exe 由客户端拉起，不需要网络。
- 默认只读。写入需要启动参数 `--allow-write` 且必须同时提供写令牌（环境变量或本地密钥文件）。
- stdout 仅用于 MCP 协议，所有日志输出到 stderr 或日志文件，避免破坏协议流。

### 7.3 并发与单实例

- 桌面端 HTTP 服务与 stdio 进程可能同时访问同一知识库。写入以文件级乐观并发（`expected_version`）和原子替换保证一致性。
- stdio 进程启动时检测桌面端是否正在编辑同一文件；若用户正在编辑，写入返回“文件正在编辑中”，不覆盖。

## 8. 错误模型

所有工具错误使用统一结构，便于 Agent 理解与重试：

| 错误码 | 含义 | Agent 应对 |
|---|---|---|
| `KB_NOT_FOUND` | 知识库标识不存在 | 先调用 `list_knowledge_bases` |
| `PATH_NOT_ALLOWED` | 路径越界或不在允许范围 | 修正路径，不要重试同一路径 |
| `READ_ONLY_MODE` | 当前档位不允许写入 | 告知用户开启写入档位 |
| `WRITE_NOT_ALLOWED_PATH` | 路径在写入禁区内 | 改为写入 `wiki/` |
| `VERSION_CONFLICT` | 文件已被修改 | 重新读取后再写 |
| `FILE_TOO_LARGE` | 超过大小上限 | 分段或改用搜索 |
| `SERVICE_UNAVAILABLE` | 知识库正在切换或服务启动中 | 稍后重试 |

错误信息不包含本机绝对路径、令牌或其他知识库的内容。

## 9. 桌面端 UI 变化

在 05「分享」页新增一张「Agent 接入（MCP）」卡片，位于局域网分享卡片之下：

- 标题与一句话说明：“让支持 MCP 的通用 Agent 读取当前知识库。”
- 状态徽标：关闭 / 只读 / 写入，与真实服务状态一致。
- 档位选择：只读（默认）与写入（需二次确认）。
- 端点地址：本机地址默认显示；开启局域网后逐条列出地址。
- 令牌：默认隐藏，点击显示；提供“复制”“重新生成”。
- 客户端配置：提供可复制的 JSON 片段（HTTP 与 stdio 两种格式），格式示例如下，具体字段以所用客户端文档为准。
- 已暴露能力：固定显示工具与 Prompt 清单，写入档时额外标出 `write_wiki_file`。
- 最近调用：仅显示调用时间、工具名、结果状态，不显示参数内容和文件正文。
- 连接数：仅在真实可得时显示。

空状态与异常：

- 未选择知识库：开关禁用，显示黄色提示“请先在首页选择或创建知识库”。
- 端口被占用或启动失败：显示错误摘要，提供重试；开关回到关闭。
- 未检测到局域网地址：沿用 05 的提示文案。

## 10. ViewModel 建议

- `McpIsRunning`、`McpIsBusy`、`McpMode`（Off / ReadOnly / ReadWrite）、`McpEndpoint`、`McpLanEndpoints`、`McpTokenMasked`、`McpTokenVisible`、`McpClientConfigJson`、`McpExposedTools`、`McpRecentCalls`、`McpLastError`、`McpAllowLan`、`McpCanStart`
- 命令：`ToggleMcp`、`SetMcpMode`、`ToggleLanAccess`、`CopyEndpoint`、`CopyToken`、`RegenerateToken`、`CopyClientConfig`、`RefreshMcpStatus`
- 状态必须来自服务层回报，不得仅在开关 setter 中置为运行中。
- `OnNavigatedToAsync()` 时调用 `RefreshMcpStatus()`。

## 11. 实施阶段

1. **共享核心**：抽出 `LlmWiki.Core` 中的 `McpAccessPolicy` 与路径校验，并补单元测试。这一阶段不改 UI。
2. **只读 HTTP**：Kestrel 宿主 + `MapMcp`，实现 Resources、Prompts、只读 Tools，接入 05 的启停。
3. **stdio 只读**：独立 exe，复用 Core，验证 stdout 不被日志污染。
4. **认证与网络**：Bearer 令牌、Origin 校验、DPAPI 保存、局域网开关。
5. **写入档**：`write_wiki_file`，乐观并发，二次确认，写入禁区测试。
6. **UI 与文档**：08 卡片、客户端配置生成、最近调用记录；更新帮助指南。

每个阶段完成后执行 `dotnet build` 与对应测试。

## 12. 验收清单

- [ ] 默认关闭，端点不响应任何请求。
- [ ] 只读档下调用 `write_wiki_file` 返回 `READ_ONLY_MODE`，且文件未被修改。
- [ ] 写入档下只能写 `wiki/` 下的文件；写 `raw/`、`.changes.json`、`purpose.md`、`schema.md`、`wiki/log.md` 均被拒绝。
- [ ] `..`、绝对路径、UNC 路径、符号链接越界均被拒绝，并有自动化测试覆盖。
- [ ] 无 Bearer 令牌或 Origin 不合法时返回 401/403。
- [ ] 默认只监听 `127.0.0.1`；开启局域网后才监听其他地址。
- [ ] 重新生成令牌后旧令牌立即失效。
- [ ] 令牌、文件正文、本机绝对路径不出现在日志和错误信息中。
- [ ] 写入冲突返回 `VERSION_CONFLICT`，不覆盖用户修改。
- [ ] 写入失败不产生部分文件。
- [ ] stdio 模式下 stdout 只含协议消息。
- [ ] 停止服务后，已建立的连接被关闭，旧地址不可访问。
- [ ] 切换知识库或存储路径后，MCP 仅暴露新的当前库范围。
- [ ] 04 的七张卡与 MCP Prompts 文案一致，参数行为一致。
- [ ] 不提供任何删除或标记已处理的工具。

## 13. 待确认项

1. 写入档是否需要在每次应用启动后重新确认，还是持久保存档位。
2. `search_content` 采用实时扫描还是建立索引；知识库超过多少文件时需要索引。
3. 多知识库同时开放时，是否允许一个令牌跨库访问，还是按库发放令牌。
4. 局域网访问是否需要支持 HTTPS；若需要，证书由谁管理。
5. 写入是否需要记录审计日志（时间、工具、路径、字节数），以及日志保留期限。
6. 客户端配置 JSON 的具体字段，需按目标客户端（Claude Desktop、Claude Code、Cursor 等）的当前文档核对。
7. `.changes.json` 的内部结构与 Ingest 规则以《设计文档》02-03 为准，本文档未包含这两份内容，接入前需对照。

---
