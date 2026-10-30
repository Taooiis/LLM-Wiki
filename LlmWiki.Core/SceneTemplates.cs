namespace LLM_Wiki.Services;

/// <summary>
/// 建库场景模板：只为 purpose.md / schema.md 提供预填文案差异（spec 02 §5）。
/// 默认（通用）文案与 Demo createSkeleton 逐字一致，office/链接两处为 spec 允许的增量。
/// </summary>
public static class SceneTemplates
{
    public static readonly string[] Scenes = ["通用", "研究", "读书", "业务"];

    /// <summary>返回 (purpose, schema)；scene 不认识回退通用。</summary>
    public static (string Purpose, string Schema) Get(string kb, string scene, string today) => scene switch
    {
        "研究" => (Research(kb, today), Schema(kb, true)),
        "读书" => (Reading(kb, today), Schema(kb, false)),
        "业务" => (Business(kb, today), Schema(kb, false)),
        _ => (Default(kb, today), Schema(kb, false)),
    };

    private static string Default(string kb, string today) => $"""
        # {kb} - 研究目标

        - 创建日期：{today}
        - 关键问题：（请在面板-配置页填写）
        - 研究范围：（请填写）
        """;

    private static string Research(string kb, string today) => $"""
        # {kb} - 研究目标

        - 创建日期：{today}
        - 场景：研究型知识库
        - 关键问题：（列出 3-5 个要回答的核心问题）
        - 研究范围：（按主题/时间/来源划定边界）
        - 演进中的论点：（随着资料增多持续修正的核心结论）
        """;

    private static string Reading(string kb, string today) => $"""
        # {kb} - 研究目标

        - 创建日期：{today}
        - 场景：读书型知识库
        - 关键问题：（这本书/这个作者想回答什么）
        - 研究范围：（章节、人物、主题线索）
        """;

    private static string Business(string kb, string today) => $"""
        # {kb} - 研究目标

        - 创建日期：{today}
        - 场景：业务型知识库
        - 关键问题：（业务决策需要回答什么）
        - 研究范围：（客户、项目、市场等边界）
        """;

    private static string Schema(string kb, bool isResearch) => $"""
        # {kb} - 结构规则

        ## 目录结构

        - purpose.md：目标、关键问题、研究范围
        - schema.md：本文件（结构规则）
        - raw/sources/：入库副本（.md/.txt；Word/Excel 上传时自动转换，原件在 raw/original/）；raw/assets/：图片
        - wiki/index.md：内容目录；wiki/log.md：操作历史；wiki/overview.md：全局摘要
        - wiki/entities/：人、组织、产品；wiki/concepts/：理论、方法、技术
        - wiki/sources/：源摘要；wiki/queries/：问答存档；wiki/synthesis/：跨源分析；wiki/comparisons/：并排对比

        ## 页规则

        - 实体页放 wiki/entities/，概念页放 wiki/concepts/
        - 每页至少链接 2-3 个相关页面，使用 [[页面名]] 的 wiki 链接
        - 每个 wiki 页面顶部建议加 YAML frontmatter，含 sources: [] 记录贡献本页的源文件（删除级联清理依赖它）
        - 所有断言必须有源引用，格式 [来源: 文件名]
        {(isResearch ? "\n- 研究型库：synthesis/ 页优先沉淀跨源论点，每个论点标注支持/矛盾它的来源\n" : "")}
        """;
}
