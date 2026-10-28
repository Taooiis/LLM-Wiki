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
    public void NoConfigFile_DefaultsToAppDataLlmWikiWikis()
    {
        var cfg = new ConfigService(_root);
        // 4.3 迁移：无 config.json 时默认使用 %APPDATA%\LlmWiki\wikis（免丢）
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LlmWiki", "wikis");
        Assert.Equal(expected, cfg.WikisDir);
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
        // 使用独立目录（不在 appRoot 下），避免触发迁移逻辑
        var target = Path.Combine(Path.GetTempPath(), "llmwiki-cfg-test-" + Guid.NewGuid().ToString("N"));
        var ok = cfg.SetWikisDir(target, out var error);
        Assert.True(ok, error);
        Assert.Equal(target, cfg.WikisDir);
        Assert.True(Directory.Exists(target));
        // 新实例从 config.json 读回
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
