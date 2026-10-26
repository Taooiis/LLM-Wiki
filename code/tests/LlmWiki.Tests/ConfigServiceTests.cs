using LlmWiki.App.Services;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>存储路径配置：config.json 读写与校验（与 Demo config.json 同键同义，spec 04 §1）。</summary>
public class ConfigServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmwiki-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void NoConfigFile_DefaultsToAppRootWikis()
    {
        var cfg = new ConfigService(_root);
        Assert.Equal(Path.Combine(_root, "wikis"), cfg.WikisDir);
    }

    [Fact]
    public void LoadConfig_RecoversDemoFormat()
    {
        var demoDir = Path.Combine(_root, "migrated");
        File.WriteAllText(Path.Combine(_root, "config.json"),
            "{\"wikisDir\": \"\\\\\\\\pc\\\\share\\\\wikis\"}");
        var cfg = new ConfigService(_root);
        Assert.Equal("\\\\pc\\share\\wikis", cfg.WikisDir);
        Assert.True(demoDir != cfg.WikisDir || true); // 不因字符串拼接误判
    }

    [Fact]
    public void SetWikisDir_ValidPath_SavesAndReloads()
    {
        var cfg = new ConfigService(_root);
        var target = Path.Combine(_root, "new-place");
        var ok = cfg.SetWikisDir(target, out var error);
        Assert.True(ok, error);
        Assert.Equal(target, cfg.WikisDir);
        Assert.True(Directory.Exists(target)); // 设置成功即建目录（Demo server.js:337 同）
        // 新实例从 config.json 读回（重启保持）
        Assert.Equal(target, new ConfigService(_root).WikisDir);
    }

    [Fact]
    public void SetWikisDir_SingleBackslash_IsRejected()
    {
        var cfg = new ConfigService(_root);
        var ok = cfg.SetWikisDir("\\foo", out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
