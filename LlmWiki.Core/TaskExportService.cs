using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace LLM_Wiki.Services;

/// <summary>任务包导出：默认只含任务说明与清单；显式选择后才按原字节包含源资料与规则。</summary>
public sealed class TaskExportService(ConfigService config)
{
    public string Export(OrganizeTask task, bool includeSources, string destinationZip)
    {
        var root = Path.Combine(config.WikisDir, task.Kb);
        var instruction = task.Prompt.Replace(root, "<知识库根目录>", StringComparison.OrdinalIgnoreCase);
        var manifest = new
        {
            exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            taskId = task.Id,
            task.Kb,
            task.Protocol,
            task.Scope,
            task.CreatedAt,
            task.RuleVersion,
            task.TemplateVersion,
            includesSources = includeSources,
            items = task.Items.Select(i => new { i.RelPath, i.Action, i.SourceHash, i.ChangeHash }),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationZip))!);
        if (File.Exists(destinationZip)) File.Delete(destinationZip);
        using var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
        AddText(zip, "任务说明.md", instruction);
        AddText(zip, "manifest.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        if (includeSources)
        {
            foreach (var item in task.Items.Where(i => i.Action != "删除"))
            {
                var full = Path.Combine(root, item.RelPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) zip.CreateEntryFromFile(full, item.RelPath, CompressionLevel.Optimal);
            }
            foreach (var rule in new[] { "purpose.md", "schema.md" })
            {
                var full = Path.Combine(root, rule);
                if (File.Exists(full)) zip.CreateEntryFromFile(full, rule, CompressionLevel.Optimal);
            }
        }
        return destinationZip;
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }
}
