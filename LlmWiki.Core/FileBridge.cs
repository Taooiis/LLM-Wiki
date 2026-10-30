using System.IO;

namespace LLM_Wiki.Services;

/// <summary>分享目录树节点；RelPath 相对库根。</summary>
public record TreeItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("children")] TreeItem[]? Children,
    [property: System.Text.Json.Serialization.JsonPropertyName("rel")] string? RelPath = null);

/// <summary>
/// 文本文件桥：只负责 md/txt 的安全读取与原子写入（MCP 写入工具使用）。
/// 资料导入/删除/扫描/变更登记分别由 SourceService、TrashService、ScanService、ChangeService 负责。
/// </summary>
public class FileBridge(ConfigService config)
{
    public string KbRoot(string kb) => Path.GetFullPath(Path.Combine(config.WikisDir, kb));

    public string? ReadText(string kb, string rel, out string error)
    {
        error = "";
        var path = PathGuard.SafeJoin(config.WikisDir, kb, rel);
        if (path == null || !FileHelper.IsTextFile(Path.GetFileName(path)))
        {
            error = "只允许读写 md/txt";
            return null;
        }
        if (!File.Exists(path))
        {
            error = "文件不存在";
            return null;
        }
        try { return FileHelper.DecodeText(File.ReadAllBytes(path)); }
        catch (Exception e)
        {
            error = "读取失败：" + e.Message;
            return null;
        }
    }

    public bool WriteTextAtomic(string kb, string rel, string content, out string error)
    {
        error = "";
        var path = PathGuard.SafeJoin(config.WikisDir, kb, rel);
        if (path == null || !FileHelper.IsTextFile(Path.GetFileName(path)))
        {
            error = "只允许读写 md/txt";
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
            return true;
        }
        catch (Exception e)
        {
            error = "写入失败：" + e.Message;
            return false;
        }
    }
}
