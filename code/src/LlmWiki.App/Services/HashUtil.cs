using System.IO;
using System.Security.Cryptography;

namespace LlmWiki.App.Services;

/// <summary>SHA256 指纹（spec 02 §7：.changes.json 带 hash，供 Agent 跳过假变更）。</summary>
public static class HashUtil
{
    public static string Sha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Hex(SHA256.HashData(stream));
    }

    public static string Sha256Text(string content) =>
        Hex(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
