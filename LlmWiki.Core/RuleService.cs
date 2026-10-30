using System.IO;
using System.Text;

namespace LLM_Wiki.Services;

/// <summary>purpose.md / schema.md 规则文件的读写；保持 Markdown 为真实来源，不做结构化副本。</summary>
public sealed class RuleService(ConfigService config)
{
    public static readonly string[] RuleFiles = ["purpose.md", "schema.md"];

    public bool TryRead(string kb, string fileName, out string content, out string hash, out bool exists, out string error)
    {
        content = "";
        hash = "";
        error = "";
        exists = false;
        if (fileName is not ("purpose.md" or "schema.md"))
        {
            error = "只允许读取规则文件";
            return false;
        }
        var full = Path.Combine(config.WikisDir, kb, fileName);
        if (!File.Exists(full)) return true;
        try
        {
            content = FileHelper.DecodeText(File.ReadAllBytes(full));
            hash = HashUtil.Sha256(full);
            exists = true;
            return true;
        }
        catch (Exception e)
        {
            error = "读取失败：" + e.Message;
            return false;
        }
    }

    public RuleSaveResult Save(string kb, string fileName, string content, string? expectedHash)
    {
        if (fileName is not ("purpose.md" or "schema.md"))
            return new RuleSaveResult(false, "", "只允许保存规则文件");
        var full = Path.Combine(config.WikisDir, kb, fileName);
        var exists = File.Exists(full);
        var currentHash = exists ? HashUtil.Sha256(full) : "";
        if (!string.IsNullOrEmpty(expectedHash)
            && !string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return new RuleSaveResult(false, currentHash, "文件已被其他程序修改，请重新读取后再保存");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var tmp = full + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, full, overwrite: true);
            var hash = HashUtil.Sha256(full);
            LogWriter.Append(config.WikisDir, kb, $"- {LogWriter.Now} 配置更新：{fileName}");
            return new RuleSaveResult(true, hash, "");
        }
        catch (Exception e)
        {
            return new RuleSaveResult(false, currentHash, "保存失败：" + e.Message);
        }
    }

    public (string Purpose, string Schema) DefaultTemplates(string kb)
        => SceneTemplates.Get(kb, "通用", DateTime.Today.ToString("yyyy-MM-dd"));
}

public sealed record RuleSaveResult(bool Success, string Hash, string Message);
