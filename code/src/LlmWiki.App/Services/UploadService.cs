using System.IO;
using System.Text;

namespace LlmWiki.App.Services;

/// <summary>
/// 上传面：四格式分派 + office 成对落盘（原件 raw/original/ + 副本 raw/sources/，spec 02 §6.1）。
/// 转换失败/重名冲突一律不落盘（不产生孤儿副本，Review Focus 2）。
/// </summary>
public class UploadService(string wikisDir)
{
    // 请求体与文件上限（spec D8，Demo readBody 同为 20MB）
    private const long MaxBytes = 20 * 1024 * 1024;

    // GB18030 兜底解码需要 CodePages 提供者（进 GBK 探测前必须可用）
    static UploadService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>上传入库。成功返回 rel（以 Agent 读到的 raw/sources/ 副本为准）。</summary>
    public (bool ok, string error, string? rel) Upload(string kb, string filename, byte[] content)
    {
        if (!PathGuard.ValidKb(kb)) return (false, "库非法", null);
        var name = Path.GetFileName(filename ?? "");
        if (!FileHelper.IsSourceFile(name)) return (false, $"不支持的格式：只收 md/txt/docx/xlsx（得到 {name}）", null);
        if (content.Length > MaxBytes) return (false, $"文件超过 20MB 上限（{content.Length / 1024 / 1024}MB）", null);

        try
        {
            var isOffice = name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

            if (!isOffice)
                return UploadPlainText(kb, name, content);

            return UploadOffice(kb, name, content);
        }
        catch (Exception e)
        {
            // 未预料的读写异常：回滚半成品，不留伪记录（spec 02 §6.2 收尾句）
            Rollback(kb, name, isOfficeGuess: name.EndsWith("x", StringComparison.OrdinalIgnoreCase));
            return (false, "入库失败：" + e.Message, null);
        }
    }

    // md/txt：直存 raw/sources/（txt 探测编码转 UTF-8；md 视为 UTF-8）
    private (bool ok, string error, string? rel) UploadPlainText(string kb, string name, byte[] content)
    {
        var text = FileHelper.DecodeText(content);
        var rel = $"raw/sources/{name}";
        var full = Path.Combine(wikisDir, kb, rel);
        var existed = File.Exists(full); // 覆盖=更新（Demo 语义）：写入前判断
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);

        LogWriter.AppendSource(wikisDir, kb, rel, existed ? "源文档更新" : "源文档上传");
        ChangeStore.Record(wikisDir, kb, rel, existed ? "更新" : "上传", HashUtil.Sha256Text(text));
        return (true, "", rel);
    }

    // docx/xlsx：原件 + Markdown 副本成对；重名拒绝（Review Focus 2）
    private (bool ok, string error, string? rel) UploadOffice(string kb, string name, byte[] content)
    {
        var ext = Path.GetExtension(name);                       // .docx / .xlsx
        var copyRel = $"raw/sources/{name}.md";
        var originalRel = $"raw/original/{name}";
        var copyFull = Path.Combine(wikisDir, kb, copyRel);
        var originalFull = Path.Combine(wikisDir, kb, originalRel);

        // 预检：副本目标被用户手工占用 → 拒绝，原件也不落
        if (File.Exists(copyFull))
            return (false, $"目标副本已存在（可能被手工占用）：{copyRel}，请改名后再传", null);

        // 转换先做（失败不落任何盘）
        var (md, error) = ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            ? OfficeConverter.ConvertDocx(name, content)
            : OfficeConverter.ConvertXlsx(name, content);
        if (md == null) return (false, error, null);

        Directory.CreateDirectory(Path.GetDirectoryName(originalFull)!);
        Directory.CreateDirectory(Path.GetDirectoryName(copyFull)!);
        File.WriteAllBytes(originalFull, content);          // 1) 原件入 raw/original/
        File.WriteAllText(copyFull, md);                    // 2) 副本入 raw/sources/
        LogWriter.AppendSource(wikisDir, kb, originalRel, "原件入库", "");
        LogWriter.AppendSource(wikisDir, kb, copyRel, "副本生成");
        ChangeStore.Record(wikisDir, kb, copyRel, "上传", HashUtil.Sha256Text(md));
        return (true, "", copyRel);
    }

    /// <summary>源删除：office 副本成对删原件；纯文本按 FileBridge 语义。</summary>
    public (bool ok, string error) DeleteSource(string kb, string rel)
    {
        var relSlash = rel.Replace('\\', '/');
        var fb = new FileBridge(wikisDir);

        // office 副本：<原文件名>.<ext>.md → 原件在 raw/original/
        var m = System.Text.RegularExpressions.Regex.Match(relSlash, @"^raw/sources/(.+)\.(docx|xlsx)\.md$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var originalRel = $"raw/original/{m.Groups[1].Value}.{m.Groups[2].Value}";
            var copyFull = Path.Combine(wikisDir, kb, relSlash);
            var originalFull = Path.Combine(wikisDir, kb, originalRel);

            if (File.Exists(copyFull)) File.Delete(copyFull);
            if (File.Exists(originalFull)) File.Delete(originalFull);
            LogWriter.AppendSource(wikisDir, kb, originalRel, "原件删除", "");
            LogWriter.AppendSource(wikisDir, kb, relSlash, "副本删除");
            ChangeStore.Record(wikisDir, kb, relSlash, "删除", "");
            return (true, "");
        }

        // 纯文本源删除走 FileBridge 同款语义
        if (!fb.Delete(kb, rel, out var error)) return (false, error);
        return (true, "");
    }

    // 失败回滚：把本次可能落的两类文件清掉（尽量精准，防孤儿）
    private void Rollback(string kb, string name, bool isOfficeGuess)
    {
        try
        {
            var copy = Path.Combine(wikisDir, kb, "raw", "sources", name + (isOfficeGuess ? ".md" : ""));
            if (File.Exists(copy)) File.Delete(copy);
            var original = Path.Combine(wikisDir, kb, "raw", "original", name);
            if (File.Exists(original)) File.Delete(original);
        }
        catch { /* 回滚也失败就留现场给用户，主流程已知 error */ }
    }
}
