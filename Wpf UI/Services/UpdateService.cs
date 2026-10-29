using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

using Wpf.Ui;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Services;

/// <summary>
/// 自动更新检查服务（spec 04 §5）。
/// 检查 GitHub Releases 页面是否有新版本，toast 提示并提供下载链接。
/// 无更新时静默；检查失败时也不打扰（仅 log）。
/// </summary>
public class UpdateService
{
    private readonly ToastService _toastService;
    // GitHub Releases JSON 接口（默认仓库为 nashsu/llm_wiki 镜像）
    private readonly string _releasesUrl;
    private readonly string _currentVersion;
    private readonly string _downloadUrlBase;

    public UpdateService(ToastService toastService, string? releasesUrl = null, string? downloadUrlBase = null)
    {
        _toastService = toastService;
        _releasesUrl = releasesUrl ?? "https://api.github.com/repos/nashsu/llm_wiki/releases/latest";
        _currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        _downloadUrlBase = downloadUrlBase ?? "https://github.com/nashsu/llm_wiki/releases/latest";
    }

    /// <summary>
    /// 异步检查更新。失败时静默不提示。
    /// </summary>
    public async Task CheckAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LLM-Wiki-Desktop");
            client.Timeout = TimeSpan.FromSeconds(5);

            var json = await client.GetStringAsync(_releasesUrl);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release == null) return;

            var latest = release.TagName.TrimStart('v');
            if (IsNewerVersion(latest, _currentVersion))
            {
                ShowUpdateToast(release);
            }
        }
        catch
        {
            // 网络异常/解析失败：静默不打扰
        }
    }

    /// <summary>
    /// 比较版本号：a > b 返回 true。
    /// </summary>
    private static bool IsNewerVersion(string latest, string current)
    {
        try
        {
            var l = new Version(latest);
            var c = new Version(current);
            return l > c;
        }
        catch
        {
            // 无法解析为 Version，使用字符串比较
            return string.CompareOrdinal(latest, current) > 0;
        }
    }

    private void ShowUpdateToast(GitHubRelease release)
    {
        var url = release.HtmlUrl ?? _downloadUrlBase;
        _ = Task.Run(async () =>
        {
            // 延迟一点显示，避免应用启动时刷叠toast
            await Task.Delay(1000);
            // 在 UI 线程上执行点击打开浏览器
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _toastService.Show("发现新版本", $"v{release.TagName} 已发布，点击下载", ControlAppearance.Info, TimeSpan.FromSeconds(10));
            });
        });
    }

    /// <summary>
    /// 打开下载页面。
    /// </summary>
    public void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _downloadUrlBase, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toastService.ShowError("打开下载页面失败：" + ex.Message);
        }
    }

    // GitHub Releases JSON 模型（仅需要的字段）
    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }
}
