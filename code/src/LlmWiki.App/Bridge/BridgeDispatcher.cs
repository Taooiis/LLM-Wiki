using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmWiki.App.Services;
using LlmWiki.App.Share;

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
    // ShareService 单例（维持服务器监听状态）
    private ShareService? _share;
    private ShareService Share => _share ??= new ShareService(config);

    // 原生系统文件夹选择器（MainWindow 注入；依赖 WPF 窗口，测试环境为 null）
    public Func<string?>? FolderPicker { get; set; }

    // 原生系统多选文件对话框（同上；返回真实路径，供桌面直接读流）
    public Func<string[]?>? FilePicker { get; set; }

    // 当前知识库的文件变更监听（MainWindow 注入；测试环境为 null）
    public FileWatcher? Watcher { get; set; }

    // 界面记忆（MainWindow 注入同一文件；未注入时按应用目录取）
    public UiStateStore? UiStore { get; set; }
    private UiStateStore Ui => UiStore ??= new UiStateStore(AppContext.BaseDirectory);

    /// <summary>只读分享服务是否在运行（关窗口时决定「退到托盘」还是「真的退出」）。</summary>
    public bool Sharing => _share?.Running == true;

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

                // ---------- 拖拽 / 原生文件对话框上传（真实路径，绕开 base64） ----------
                case "upload-path":
                    return UploadByPaths(S("kb"), SA("paths"));

                case "file-pick":
                    {
                        if (FilePicker == null) return Error("当前环境不支持原生文件选择");
                        var filePaths = FilePicker() ?? [];
                        if (filePaths.Length == 0)
                            return Ok(new JsonObject { ["ok"] = false, ["canceled"] = true });
                        return UploadByPaths(S("kb"), filePaths);
                    }

                // ---------- 界面记忆（上次知识库 / 上次 tab；窗口几何由 WPF 层自己存） ----------
                case "ui-state-get":
                    {
                        var st = Ui.Load();
                        return Ok(new JsonObject { ["lastKb"] = st.LastKb, ["lastTab"] = st.LastTab });
                    }

                case "ui-state-set":
                    {
                        var lastKb = S("lastKb");
                        var lastTab = S("lastTab");
                        Ui.Update(st =>
                        {
                            if (lastKb.Length > 0) st.LastKb = lastKb;
                            if (int.TryParse(lastTab, out var t)) st.LastTab = t;
                        });
                        return Ok(new JsonObject { ["ok"] = true });
                    }

                // ---------- 实时变更监听：前端切换库时改监听目标 ----------
                case "kb-watch":
                    {
                        var watchKb = S("kb");
                        Watcher?.Watch(string.IsNullOrWhiteSpace(watchKb)
                            ? null
                            : Path.Combine(config.WikisDir, watchKb));
                        return Ok(new JsonObject { ["ok"] = true, ["watching"] = Watcher?.CurrentRoot ?? "" });
                    }

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

                // ---------- 原生文件夹选择器（桌面应用走系统对话框；无宿主窗口时回退网页选择器） ----------
                case "folder-pick":
                    if (FolderPicker == null) return Ok(new JsonObject { ["ok"] = false });
                    var picked = FolderPicker();
                    return picked is null
                        ? Ok(new JsonObject { ["ok"] = false, ["canceled"] = true })
                        : Ok(new JsonObject { ["ok"] = true, ["path"] = picked });

                case "browse":
                    return SwitchBrowse(S("dir")).ToJsonString(J);

                case "places": return JsonNode.Parse(PlacesJson())!.ToJsonString(J);

                // ---------- 局域网选择器（server.js net view 的 C# 复刻） ----------
                case "net-pc": return NetPcs().ToJsonString(J);
                case "net-shares": return NetShares(S("pc")).ToJsonString(J);

                // ---------- 只读分享服务（Task 8） ----------
                case "share-status":
                    return Ok(ShareInfo());

                case "share-start":
                    var portStr = S("port");
                    int port = 0;
                    if (portStr.Length > 0 && !int.TryParse(portStr, out port))
                        return Error("端口号无效");
                    if (!Share.Running && !Share.Start(port, out var sErr))
                        return Error(sErr);
                    return Ok(ShareInfo());

                case "share-stop":
                    Share.Stop();
                    return Ok(new JsonObject { ["ok"] = true, ["running"] = false });

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

    /// <summary>
    /// 分享状态 + PIN + 二维码。
    /// 地址列表统一带上 PIN（对方点开即进，不用手输）；二维码取局域网地址（localhost 手机扫不到）。
    /// </summary>
    private JsonObject ShareInfo()
    {
        var running = Share.Running;
        var urls = running ? Share.LanAddresses().Select(Share.AccessUrl).ToArray() : [];

        // 优先非 localhost 的局域网地址；没有局域网地址时退回归环地址（本机浏览器预览也能用）
        var qrTarget = urls.FirstOrDefault(u => !u.Contains("localhost")) ?? urls.FirstOrDefault() ?? "";

        return new JsonObject
        {
            ["ok"] = true,
            ["running"] = running,
            ["port"] = running ? Share.Port : 0,
            ["pin"] = running ? Share.Pin : "",
            ["localAddress"] = running ? Share.LocalAddress() : "",
            ["lanAddresses"] = new JsonArray([.. urls.Select(u => (JsonNode?)u)]),
            ["qr"] = running ? QrSvg.Render(qrTarget) : "",
        };
    }

    /// <summary>批量上传真实路径（拖拽与系统文件对话框共用同一套结果形状）。</summary>
    private string UploadByPaths(string kb, string[] paths)
    {
        if (paths.Length == 0) return Error("没有收到文件");
        if (string.IsNullOrWhiteSpace(kb)) return Error("先选择知识库");

        var rows = Upload.UploadFromPaths(kb, paths);
        var arr = new JsonArray();
        foreach (var r in rows)
            arr.Add(new JsonObject
            {
                ["name"] = r.Name,
                ["ok"] = r.Ok,
                ["error"] = r.Error,
                ["rel"] = r.Rel,
            });
        return Ok(new JsonObject
        {
            ["results"] = arr,
            ["okCount"] = rows.Count(r => r.Ok),
            ["failCount"] = rows.Count(r => !r.Ok),
        });
    }

    private static string Ok(JsonObject o) => o.ToJsonString(J);
    // 分发器返回值是字符串；对象版给内部 JsonObject 返回型方法用
    private static string Error(string msg) => ErrObj(msg).ToJsonString(J);
    private static JsonObject ErrObj(string msg) => new() { ["error"] = msg };
}
