using System.IO;
using System.Text;

namespace LlmWiki.App.Services;

/// <summary>
/// 提示词引擎（spec 02 §7 工作流页、§8 Clean 卡）。
/// 模板原文对照 Demo prompts/*.md；只做 spec 04 §2 允许的三处增量：
///   1) Ingest 加 office 副本与原文件对应说明；
///   2) 边界说明第 3 段加 office 目录说明；
///   3) 边界说明第 5 段加 Clean 级联清理语义。
/// 所有占位符在返回前必须全部展开（测试断言无 {{...}} 残留）。
/// </summary>
public class PromptEngine(ConfigService config, FileBridge fb)
{
    // 存储根目录（绝对路径，wikis/ 所在）
    private string AbsRoot => config.WikisDir;

    // 程序根目录（AGENTS.md / 使用说明随发布物打包进 AppRoot/agents/）
    private static string AppRoot => AppContext.BaseDirectory;

    /// <summary>知识库绝对路径：{存储根}\{库名}。</summary>
    private string KbPath(string kb) => Path.Combine(AbsRoot, kb);

    // ---------- 1. 边界说明（先接入 Agent，只做一次） ----------

    /// <summary>边界说明：五段结构 + 增量 2/3（office 说明、Clean 语义）。</summary>
    public string Boundary(string kb)
    {
        var purpose = ReadSafe(kb, "purpose.md");
        var schema = ReadSafe(kb, "schema.md");
        var index = ReadSafe(kb, "wiki/index.md");
        var files = Flatten(fb.Tree(kb));

        var sb = new StringBuilder();
        sb.AppendLine($"# 知识库接入说明（粘贴进 Agent 配置后可直接读取本库）");
        sb.AppendLine();
        sb.AppendLine($"知识库：{kb}");
        sb.AppendLine($"本地绝对路径：{KbPath(kb)}");
        sb.AppendLine();
        sb.AppendLine("## 先读这三个文件");
        sb.AppendLine($"1. purpose.md（目标/范围）：\n{Cut(purpose, 800)}");
        sb.AppendLine($"2. schema.md（结构规则）：\n{Cut(schema, 800)}");
        sb.AppendLine($"3. wiki/index.md（内容目录，先读它找页面）：\n{Cut(index, 1200)}");
        sb.AppendLine();
        sb.AppendLine("## 目录约定");
        sb.AppendLine("- raw/sources/：源文档（.md/.txt 只读）");
        sb.AppendLine("- raw/sources/ 下的 xxx.docx.md / xxx.xlsx.md 是从 raw/original/ 下同名原件自动转换出的副本；raw/original/ 存 office 原件（只读）");
        sb.AppendLine("- 引用 office 来源时写原文件名（如 [来源: 调研报告.docx]），不要写 .docx.md 副本名");
        sb.AppendLine("- wiki/entities/、concepts/、sources/、queries/、synthesis/、comparisons/：知识页");
        sb.AppendLine("- wiki/log.md：变更历史；源文档被改后必须重新 Ingest 对应文档");
        sb.AppendLine("- 页内用 [[页面名]] 互链；断言格式 [来源: 文件名]");
        sb.AppendLine();
        sb.AppendLine("## 文件清单");
        sb.AppendLine(FlatFiles(files));
        sb.AppendLine();
        sb.AppendLine("## 回答要求");
        sb.AppendLine("引用具体的 wiki 页面；重要结论写回 synthesis/comparison 新页并更新 index.md 和 log.md。");
        sb.AppendLine("源文档被删除后，用 Clean 提示词卡做级联清理（删摘要页、清 index 条目与失效 [[链接]]，共享实体/概念页只摘除该源条目，不整页删）。");
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------- 2. Ingest（批量 / 留空=全部） ----------

    /// <summary>Ingest 提示词：files 为空或 null 时自动列出本库全部源文档。</summary>
    public string Ingest(string kb, string[]? files)
    {
        // 文件名可只给 basename（面板行为），统一补成库内相对路径
        var list = (files is { Length: > 0 })
            ? files.Select(NormalizeRel).ToArray()
            : fb.Sources(kb).Select(f => "raw/sources/" + f).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine(Head(kb, readPurpose: true));
        sb.AppendLine($"请按 Ingest 工作流批量处理 {list.Length} 篇文档（逐篇执行，不要跳过）：");
        for (int i = 0; i < list.Length; i++)
            sb.AppendLine($"{i + 1}. 读取 wikis/{kb}/{list[i]} 全文，提取实体/概念，在 entities/ 或 concepts/ 建页或更新");
        sb.AppendLine($"全部完成后统一更新 wikis/{kb}/wiki/index.md 和 log.md，有矛盾在 log 标记");
        sb.AppendLine();
        sb.AppendLine(OfficeNote());
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------- 3. 增量 Ingest（只处理选中变更） ----------

    /// <summary>增量 Ingest 提示词：只处理选中文件；带 SHA256 指纹供 Agent 跳过假变更（spec 02 §7）。</summary>
    public string IngestChanges(string kb, Change[] selected)
    {
        var ups = selected.Where(c => c.Action != "删除").ToArray();
        var dels = selected.Where(c => c.Action == "删除").ToArray();

        var sb = new StringBuilder();
        sb.AppendLine(Head(kb, readPurpose: true));
        sb.AppendLine("请按 Ingest 工作流只处理以下文件（其余源文档一律不动）：");
        for (int i = 0; i < ups.Length; i++)
            sb.AppendLine($"{i + 1}. 读取 wikis/{kb}/{ups[i].File} 全文，提取实体/概念，在 entities/ 或 concepts/ 建页或更新");
        if (ups.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("各文件上次处理的 sha256 指纹（hash 与上次 Ingest 一致的文件可跳过，实际内容未变就跳过该文件）：");
            foreach (var c in ups) sb.AppendLine($"- {c.File}：{c.Hash}");
        }
        if (dels.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"另有 {dels.Length} 个源文档已被删除（{string.Join("、", dels.Select(d => d.File))}）：");
            sb.AppendLine("一并清理 wiki 中引用它们的内容并更新索引；逐条级联清理请用 Clean 提示词卡。");
        }
        sb.AppendLine();
        sb.AppendLine(OfficeNote());
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------- 4. Query ----------

    public string Query(string kb, string question)
    {
        var q = string.IsNullOrWhiteSpace(question) ? "（你的问题）" : question.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"（先读 {AgentsFile()} 和 wikis/{kb}/wiki/index.md）");
        sb.AppendLine($"知识库根目录（绝对）：{KbPath(kb)}");
        sb.AppendLine("以下 wikis/ 开头的路径均相对该根目录；若工作目录不在本程序目录，请换成绝对路径。");
        sb.AppendLine();
        sb.AppendLine($"请按 Query 工作流回答：{q}");
        sb.AppendLine($"1. 先读 wikis/{kb}/wiki/index.md 找相关页");
        sb.AppendLine($"2. 读相关实体/概念页综合回答");
        sb.AppendLine($"3. 引用具体 wiki 页面；重要答案新建 synthesis 页");
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------- 5. Lint ----------

    public string Lint(string kb)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"知识库根目录（绝对）：{KbPath(kb)}");
        sb.AppendLine("以下 wikis/ 开头的路径均相对该根目录；若工作目录不在本程序目录，请换成绝对路径。");
        sb.AppendLine();
        sb.AppendLine("请按 Lint 工作流做健康检查：");
        sb.AppendLine($"检查 wikis/{kb}/wiki/ 下矛盾、陈旧主张、孤立页、数据缺口，并建议新调研问题");
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------- 6. Compare ----------

    public string Compare(string kb, string topic)
    {
        var t = string.IsNullOrWhiteSpace(topic) ? "A vs B" : topic.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"知识库根目录（绝对）：{KbPath(kb)}");
        sb.AppendLine("以下 wikis/ 开头的路径均相对该根目录；若工作目录不在本程序目录，请换成绝对路径。");
        sb.AppendLine();
        sb.AppendLine($"请创建对比页比较：{t}");
        sb.AppendLine($"读 wikis/{kb}/wiki/ 相关页，在 wiki/comparisons/ 新建并排对比页并更新 index.md");
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------- 7. Clean（级联清理，spec 02 §8） ----------

    /// <summary>Clean 提示词：源文档已删除后，清理摘要页、index 条目、失效链接、共享页源条目。</summary>
    public string Clean(string kb, string deletedFile)
    {
        var f = string.IsNullOrWhiteSpace(deletedFile) ? "（源文档）" : deletedFile.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"（先读 {AgentsFile()} 与 wikis/{kb}/wiki/index.md）");
        sb.AppendLine($"知识库根目录（绝对）：{KbPath(kb)}");
        sb.AppendLine();
        sb.AppendLine($"源文档 {f} 已被删除（raw/sources/ 中副本与 raw/original/ 中原件均已移除），请按以下级联清理规则更新 wiki：");
        sb.AppendLine("1. 删除 wiki/sources/ 下该源的摘要页（如存在）");
        sb.AppendLine("2. 从 wiki/index.md 移除所有指到该源或该摘要页的条目");
        sb.AppendLine("3. 从其余页面移除指向被删页面的失效 [[链接]]，行文需要时改为指向最近的替代页");
        sb.AppendLine("4. 共享实体/概念页不要整页删除：仅把 frontmatter sources 数组里该源条目移除");
        sb.AppendLine("5. 在 wiki/log.md 追加一行记录本次清理");
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------- 公共片段 ----------

    // 头部：读约定 + 绝对根路径（Demo prompts/*.md 同款措辞）
    private string Head(string kb, bool readPurpose)
    {
        var sb = new StringBuilder();
        sb.AppendLine(readPurpose
            ? $"（先读 {AgentsFile()} 和 wikis/{kb}/purpose.md、schema.md）"
            : $"（先读 {AgentsFile()}）");
        sb.AppendLine($"知识库根目录（绝对）：{KbPath(kb)}");
        sb.AppendLine("以下 wikis/ 开头的路径均相对该根目录；若工作目录不在本程序目录，请换成绝对路径。");
        return sb.ToString().TrimEnd();
    }

    // AGENTS.md 随发布物打包在程序目录 agents/ 下（计划 Task 7 Step 3）
    private static string AgentsFile() => Path.Combine(AppRoot, "agents", "AGENTS.md");

    // 文件名归一：basename → raw/sources/<name>；已是库内路径则原样保留
    private static string NormalizeRel(string f)
    {
        var s = f.Trim().Replace('\\', '/');
        if (s.StartsWith("raw/", StringComparison.OrdinalIgnoreCase)) return s;
        return "raw/sources/" + s;
    }

    // office 副本与原件对应说明（spec 04 §2 增量 3）
    private static string OfficeNote() =>
        "（说明：raw/sources/ 下的 xxx.docx.md / xxx.xlsx.md 是从 raw/original/ 下同名原件自动转换出的副本；" +
        "引用来源时写原文件名，如 [来源: 调研报告.docx]）";

    // 读文件（不存在时给占位，不抛错）
    private string ReadSafe(string kb, string rel)
    {
        var text = fb.ReadText(kb, rel, out var err);
        return text ?? $"（读取失败：{err}）";
    }

    // 截断（Demo 行为：超长加"…（已截断）"）
    private static string Cut(string s, int n) =>
        s.Length > n ? s[..n] + "\n…（已截断）" : s;

    // 目录树 → 扁平路径清单（最多 120 项，与 Demo 一致）
    private static string FlatFiles(List<(string path, string type)> files)
    {
        if (files.Count == 0) return "（未含文件清单：按上面的目录约定自行浏览 wiki/ 即可）";
        var cap = Math.Min(files.Count, 120);
        var lines = files.Take(cap).Select(f => f.path).ToList();
        var text = $"当前文件清单（{files.Count} 项，最多列 120，大改后请重新生成覆盖）\n{string.Join("\n", lines)}";
        if (files.Count > cap) text += $"\n…（其余 {files.Count - cap} 项已省略）";
        return text;
    }

    // 目录树 → (路径, 类型) 列表
    private static List<(string path, string type)> Flatten(TreeItem[] nodes)
    {
        var list = new List<(string, string)>();
        void Walk(TreeItem[] items, string prefix)
        {
            foreach (var n in items)
            {
                if (n.Type == "dir") Walk(n.Children ?? [], prefix + n.Name + "/");
                else list.Add((prefix + n.Name, n.Type));
            }
        }
        Walk(nodes, "");
        return list;
    }
}
