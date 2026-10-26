using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmWiki.App.Services;

namespace LlmWiki.App.Bridge;

/// <summary>
/// JS↔C# 命令分发（Task 6）：语义逐条对齐 Demo server.js 路由表（spec 02 §4）。
/// 入参/出参形状与 /api 一致，前端移植时只换传输不换形状。
/// 服务按请求从 config.WikisDir 现取：改存储路径后立即生效（不绑定启动时旧路径）。
/// </summary>
public class BridgeDispatcher(ConfigService config)
{
    private static readonly JsonSerializerOptions J = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // 当前存储根下的服务（每次请求现取，避免路径切换后指向旧目录）
    private KbService Kb => new(config.WikisDir);
    private FileBridge Fb => new(config.WikisDir);
    private UploadService Upload => new(config.WikisDir);
    private PromptEngine Prompts => new(config, Fb);

    public async Task<string> HandleAsync(string name, string jsonArgs)
    {
        JsonNode args;
        try { args = string.IsNullOrEmpty(jsonArgs) ? new JsonObject() : JsonNode.Parse(jsonArgs) ?? new JsonObject(); }
        catch { return Error("参数不是合法 JSON"); }

        string S(string key) => args[key]?.ToString() ?? "";
        string[] SA(string key)
        {
            if (args[key] is JsonArray a) return [.. a.Select(n => n?.ToString() ?? "")];
            return [];
        }

        try
        {
            switch (name)
            {
                // ---------- 健康 / 设置 ----------
                case "health": return Ok(new JsonObject { ["ok"] = true });

                case "settings-get": return Ok(new JsonObject { ["wikisDir"] = config.WikisDir });
                case "settings-set":
                    if (config.SetWikisDir(S("wikisDir"), out var err))
                        return Ok(new JsonObject { ["ok"] = true, ["wikisDir"] = config.WikisDir });
                    return Error(err);

                // ---------- 知识库 ----------
                case "kbs-list":
                    if (!Directory.Exists(config.WikisDir)) Directory.CreateDirectory(config.WikisDir);
                    return Ok(new JsonObject { ["kbs"] = new JsonArray([.. Kb.ListKbs().Select(n => (JsonNode?)n)]), ["root"] = config.WikisDir });

                case "kbs-create":
                    try
                    {
                        var p = Kb.CreateKb(S("name"), string.IsNullOrWhiteSpace(S("scene")) ? "通用" : S("scene"));
                        return Ok(new JsonObject { ["ok"] = true, ["path"] = p });
                    }
                    catch (ArgumentException e) { return Error(e.Message); }

                case "kbs-delete":
                    var (dok, derror) = Kb.ValidateDelete(S("name"));
                    if (!dok) return Error("拒绝删除：" + derror);
                    Kb.DeleteKb(S("name"));
                    return Ok(new JsonObject { ["ok"] = true });

                // ---------- 目录与文件 ----------
                case "tree": return Ok(new JsonObject { ["tree"] = JsonObjectifyTree(Fb.Tree(S("kb"))) });

                case "file-read":
                    var content = Fb.ReadText(S("kb"), S("path"), out var readErr);
                    return readErr.Length == 0 ? Ok(new JsonObject { ["content"] = content }) : Error(readErr);

                case "file-write":
                    return Fb.WriteText(S("kb"), S("path"), S("content"), out var wErr)
                        ? Ok(new JsonObject { ["ok"] = true })
                        : Error(wErr);

                case "delete":
                    // raw/sources 下经上传面（office 成对删除）；其他走 FileBridge（仅 md/txt）
                    var delRel = S("path").Replace('\\', '/');
                    if (delRel.StartsWith("raw/sources/"))
                        return Upload.DeleteSource(S("kb"), delRel).ok ? Ok(new JsonObject { ["ok"] = true }) : Error("删除失败");
                    return Fb.Delete(S("kb"), delRel, out var dErr) ? Ok(new JsonObject { ["ok"] = true }) : Error(dErr);

                // ---------- 源与变更 ----------
                case "sources": return Ok(new JsonObject { ["files"] = new JsonArray([.. Fb.Sources(S("kb")).Select(n => (JsonNode?)n)]) });

                case "changes":
                    var list = new JsonArray();
                    foreach (var c in Fb.Changes(S("kb")))
                        list.Add(new JsonObject { ["file"] = c.File, ["action"] = c.Action, ["time"] = c.Time, ["hash"] = c.Hash });
                    return Ok(new JsonObject { ["changes"] = list });

                case "changes-clear":
                    Fb.ClearChanges(S("kb"), SA("files"));
                    return Ok(new JsonObject { ["ok"] = true });

                // ---------- 上传（base64 传输，office 由 UploadService 分派） ----------
                case "upload":
                    try
                    {
                        var bytes = Convert.FromBase64String(S("b64"));
                        var (ok, uerr, _) = Upload.Upload(S("kb"), S("filename"), bytes);
                        return ok ? Ok(new JsonObject { ["ok"] = true }) : Error(uerr);
                    }
                    catch (FormatException) { return Error("文件内容不是合法 base64"); }

                // ---------- 提示词生成（Task 7：六类 + 边界说明） ----------
                case "prompt":
                    var pe = Prompts;
                    var kbName = S("kb");
                    switch (S("kind"))
                    {
                        case "boundary":
                            return Ok(new JsonObject { ["prompt"] = pe.Boundary(kbName) });
                        case "ingest":
                            // files 省略或空数组 = 列出本库全部源文档
                            var files = SA("files");
                            return Ok(new JsonObject { ["prompt"] = pe.Ingest(kbName, files.Length == 0 ? null : files) });
                        case "ingest-changes":
                            // 只传选中的变更文件名；hash 由后端从当前变更清单取（权威值）
                            var selected = Fb.Changes(kbName).Where(c => SA("files").Contains(c.File)).ToArray();
                            return Ok(new JsonObject { ["prompt"] = pe.IngestChanges(kbName, selected) });
                        case "query":
                            return Ok(new JsonObject { ["prompt"] = pe.Query(kbName, S("question")) });
                        case "lint":
                            return Ok(new JsonObject { ["prompt"] = pe.Lint(kbName) });
                        case "compare":
                            return Ok(new JsonObject { ["prompt"] = pe.Compare(kbName, S("topic")) });
                        case "clean":
                            return Ok(new JsonObject { ["prompt"] = pe.Clean(kbName, S("file")) });
                        default:
                            return Error("未知提示词类型：" + S("kind"));
                    }

                // ---------- 路径选择器 ----------
                case "drives":
                    List<string> drives = [.. Enumerable.Range('A', 26).Select(i => i + ":\\")
                        .Where(d => { try { return Directory.Exists(d); } catch { return false; } })];
                    return Ok(new JsonObject { ["drives"] = new JsonArray([.. drives.Select(n => (JsonNode?)n)]) });

                case "browse":
                    return SwitchBrowse(S("dir")).ToJsonString(J);

                case "places": return JsonNode.Parse(PlacesJson())!.ToJsonString(J);

                // ---------- 局域网选择器（server.js net view 的 C# 复刻） ----------
                case "net-pc": return NetPcs().ToJsonString(J);
                case "net-shares": return NetShares(S("pc")).ToJsonString(J);

                default: return Error("未知命令：" + name);
            }
        }
        catch (Exception e)
        {
            return Error(e.Message);
        }
    }

    // 目录浏览（server.js 同款：只列子目录）
    private static JsonObject SwitchBrowse(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir))
            return ErrObj("请选择有效的文件夹");
        var full = Path.GetFullPath(dir);
        if (!Directory.Exists(full)) return ErrObj("文件夹不存在");
        try
        {
            var dirs = Directory.GetDirectories(full)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return new JsonObject { ["dir"] = full, ["dirs"] = new JsonArray([.. dirs.Select(n => (JsonNode?)n)]) };
        }
        catch { return ErrObj("无权限访问该文件夹"); }
    }

    // 快捷位置：桌面 / 我的文档 / 本程序所在盘（server.js 同款）
    private static string PlacesJson()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appRoot = AppContext.BaseDirectory;
        var list = new JsonArray();
        foreach (var (name, dir) in new (string, string)[] { ("桌面", Path.Combine(home, "Desktop")), ("我的文档", Path.Combine(home, "Documents")), ("本程序目录", appRoot) })
            if (Directory.Exists(dir)) list.Add(new JsonObject { ["name"] = name, ["path"] = dir });
        return new JsonObject { ["places"] = list }.ToJsonString(J);
    }

    // net view 解析局域网电脑（失败回空数组 + 错误提示）
    private static JsonObject NetPcs()
    {
        var pcs = new List<string>();
        string? error = null;
        try
        {
            var outp = Run("net view");
            pcs = System.Text.RegularExpressions.Regex.Matches(outp, @"\\\\([A-Za-z0-9_\-.]+)")
                .Select(m => m.Groups[1].Value).Distinct().ToList();
        }
        catch (Exception e) { error = "没找到局域网电脑：" + e.Message; }

        var o = new JsonObject { ["pcs"] = new JsonArray([.. pcs.Select(n => (JsonNode?)n)]) };
        if (error != null) o["error"] = error;
        return o;
    }

    // net view \\pc 解析磁盘共享（过滤 C$/ADMIN$/IPC$ 管理共享）
    private static JsonObject NetShares(string pc)
    {
        var shares = new List<string>();
        string? error = null;
        if (!string.IsNullOrEmpty(pc) && System.Text.RegularExpressions.Regex.IsMatch(pc, "^[A-Za-z0-9_\\-.]{1,64}$"))
        {
            try
            {
                var outp = Run("net view", $"\\\\{pc}");
                foreach (var line in outp.Split('\n'))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\S+)\s+磁盘\s*");
                    if (m.Success && !m.Groups[1].Value.EndsWith('$')) shares.Add(m.Groups[1].Value);
                }
            }
            catch (Exception e) { error = e.Message; }
        }
        else { error = "无效的主机名"; }

        var o = new JsonObject { ["pc"] = pc, ["shares"] = new JsonArray([.. shares.Select(n => (JsonNode?)n)]) };
        if (error != null) o["error"] = error;
        return o;
    }

    // net view 输出经 cmd 转码为 UTF-8 后读取（避免乱码不可匹配）
    private static string Run(string cmdline, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c chcp 65001>nul & {cmdline} {string.Join(' ', args)}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var std = p.StandardOutput.ReadToEndAsync().Result + p.StandardError.ReadToEndAsync().Result;
        p.WaitForExit(20000);
        return std;
    }

    // TreeItem → JsonNode（children 可空数组显式序列化）
    private static JsonNode? JsonObjectifyTree(TreeItem[] tree) => JsonNode.Parse(JsonSerializer.Serialize(tree, J));

    private static string Ok(JsonObject o) => o.ToJsonString(J);
    // 分发器返回值是字符串；对象版给内部 JsonObject 返回型方法用
    private static string Error(string msg) => ErrObj(msg).ToJsonString(J);
    private static JsonObject ErrObj(string msg) => new() { ["error"] = msg };
}
