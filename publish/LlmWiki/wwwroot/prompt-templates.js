/**
 * LLM Wiki 提示词模板（原文模板，替代 Demo 的 prompts/*.md）
 *
 * 用途：
 *   1. 面板工作流页「查看模板原文」按钮直接展示这份模板，便于手工复制/存档；
 *   2. 实际生成提示词由 C# 侧 Services/PromptEngine.cs 完成（绝对路径、哈希指纹、
 *      变更清单等都要用后端权威数据填充，前端不重复实现填充逻辑）。
 *
 * 占位符说明：
 *   {{abs_root}} 存储根目录（绝对路径，面板顶部显示）
 *   {{kb}}       知识库名
 *   {{file}}     源文档文件名（或 raw/sources/ 下的相对路径）
 *   {{question}} 用户问题
 *   {{topic}}    对比主题
 *
 * 模板与 PromptEngine.cs 一一对应；PromptEngineTests 中的
 * PromptTemplatesFile_MatchesEnginePhrases 用例守住两份文案不漂移。
 */
window.PROMPT_TEMPLATES = {
  boundary: `# 知识库接入说明（粘贴进 Agent 配置后可直接读取本库）

知识库：{{kb}}
本地绝对路径：{{abs_root}}\{{kb}}

## 先读这三个文件
1. purpose.md（目标/范围）：
2. schema.md（结构规则）：
3. wiki/index.md（内容目录，先读它找页面）：

## 目录约定
- raw/sources/：源文档（.md/.txt 只读）
- raw/sources/ 下的 xxx.docx.md / xxx.xlsx.md 是从 raw/original/ 下同名原件自动转换出的副本；raw/original/ 存 office 原件（只读）
- 引用 office 来源时写原文件名（如 [来源: 调研报告.docx]），不要写 .docx.md 副本名
- wiki/entities/、concepts/、sources/、queries/、synthesis/、comparisons/：知识页
- wiki/log.md：变更历史；源文档被改后必须重新 Ingest 对应文档
- 页内用 [[页面名]] 互链；断言格式 [来源: 文件名]

## 文件清单
当前文件清单（最多列 120 项，大改后请重新生成覆盖）

## 回答要求
引用具体的 wiki 页面；重要结论写回 synthesis/comparison 新页并更新 index.md 和 log.md。
源文档被删除后，用 Clean 提示词卡做级联清理（删摘要页、清 index 条目与失效 [[链接]]，共享实体/概念页只摘除该源条目，不整页删）。`,

  ingest: `（先读 AGENTS.md 和 wikis/{{kb}}/purpose.md、schema.md）
知识库根目录（绝对）：{{abs_root}}\{{kb}}
以下 wikis/ 开头的路径均相对该根目录；若工作目录不在本程序目录，请换成绝对路径。

请按 Ingest 工作流批量处理 N 篇文档（逐篇执行，不要跳过）：
1. 读取 wikis/{{kb}}/raw/sources/{{file}} 全文，提取实体/概念，在 entities/ 或 concepts/ 建页或更新
全部完成后统一更新 wikis/{{kb}}/wiki/index.md 和 log.md，有矛盾在 log 标记

（说明：raw/sources/ 下的 xxx.docx.md / xxx.xlsx.md 是从 raw/original/ 下同名原件自动转换出的副本；引用来源时写原文件名，如 [来源: 调研报告.docx]）`,

  ingestChanges: `（先读 AGENTS.md 和 wikis/{{kb}}/purpose.md、schema.md）
知识库根目录（绝对）：{{abs_root}}\{{kb}}
以下 wikis/ 开头的路径均相对该根目录；若工作目录不在本程序目录，请换成绝对路径。

请按 Ingest 工作流只处理以下文件（其余源文档一律不动）：
1. 读取 wikis/{{kb}}/raw/sources/{{file}} 全文，提取实体/概念，在 entities/ 或 concepts/ 建页或更新

各文件上次处理的 sha256 指纹（hash 与上次 Ingest 一致的文件可跳过，实际内容未变就跳过该文件）：
- {{file}}：{{hash}}

（说明：raw/sources/ 下的 xxx.docx.md / xxx.xlsx.md 是从 raw/original/ 下同名原件自动转换出的副本；引用来源时写原文件名，如 [来源: 调研报告.docx]）`,

  query: `（先读 AGENTS.md 和 wikis/{{kb}}/wiki/index.md）
知识库根目录（绝对）：{{abs_root}}\{{kb}}
以下 wikis/ 开头的路径均相对该根目录；若工作目录不在本程序目录，请换成绝对路径。

请按 Query 工作流回答：{{question}}
1. 先读 wikis/{{kb}}/wiki/index.md 找相关页
2. 读相关实体/概念页综合回答
3. 引用具体 wiki 页面；重要答案新建 synthesis 页`,

  lint: `知识库根目录（绝对）：{{abs_root}}\{{kb}}
以下 wikis/ 开头的路径均相对该根目录；若工作目录不在本程序目录，请换成绝对路径。

请按 Lint 工作流做健康检查：
检查 wikis/{{kb}}/wiki/ 下矛盾、陈旧主张、孤立页、数据缺口，并建议新调研问题`,

  compare: `知识库根目录（绝对）：{{abs_root}}\{{kb}}
以下 wikis/ 开头的路径均相对该根目录；若工作目录不在本程序目录，请换成绝对路径。

请创建对比页比较：{{topic}}
读 wikis/{{kb}}/wiki/ 相关页，在 wiki/comparisons/ 新建并排对比页并更新 index.md`,

  clean: `（先读 AGENTS.md 与 wikis/{{kb}}/wiki/index.md）
知识库根目录（绝对）：{{abs_root}}\{{kb}}

源文档 {{file}} 已被删除（raw/sources/ 中副本与 raw/original/ 中原件均已移除），请按以下级联清理规则更新 wiki：
1. 删除 wiki/sources/ 下该源的摘要页（如存在）
2. 从 wiki/index.md 移除所有指到该源或该摘要页的条目
3. 从其余页面移除指向被删页面的失效 [[链接]]，行文需要时改为指向最近的替代页
4. 共享实体/概念页不要整页删除：仅把 frontmatter sources 数组里该源条目移除
5. 在 wiki/log.md 追加一行记录本次清理`,
};
