using System.IO;
using LlmWiki.App.Services;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>
/// 桌面实时变更监听：Agent 在外部改文件后，宿主把变更推给前端。
/// 覆盖：写入触发一次（防抖合并）、无效目录不监听、Office 临时文件忽略。
/// </summary>
public class FileWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmwiki-watch-" + Guid.NewGuid().ToString("N"));

    public FileWatcherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task WriteFile_RaisesChangedWithRoot()
    {
        using var watcher = new FileWatcher();
        var hits = 0;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += root =>
        {
            Interlocked.Increment(ref hits);
            tcs.TrySetResult(root);
        };

        watcher.Watch(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.md"), "x");

        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Same(tcs.Task, done);
        Assert.Equal(_dir, await tcs.Task);
        Assert.True(Volatile.Read(ref hits) >= 1);
    }

    [Fact]
    public void WatchMissingDirectory_LeavesRootNull()
    {
        using var watcher = new FileWatcher();
        watcher.Watch(Path.Combine(_dir, "not-exists"));
        Assert.Null(watcher.CurrentRoot);
    }

    [Fact]
    public async Task OfficeTempFile_IsIgnored()
    {
        using var watcher = new FileWatcher();
        var hits = 0;
        watcher.Changed += _ => Interlocked.Increment(ref hits);

        watcher.Watch(_dir);
        // Office 保存时生成的锁文件，不应触发面板刷新
        File.WriteAllText(Path.Combine(_dir, "~$报告.docx"), "lock");

        // 防抖窗口 600ms；等 1.2s 确认没有回调
        await Task.Delay(1200);
        Assert.Equal(0, Volatile.Read(ref hits));
    }
}
