using System.Text;
using QRCoder;

namespace LlmWiki.App.Share;

/// <summary>
/// 二维码 SVG 生成：给面板展示「手机扫码即可打开只读页」。
/// 用 QRCoder 只取模块矩阵，自己拼 SVG —— 不依赖 System.Drawing，也不用生成 PNG 再转 base64。
/// </summary>
public static class QrSvg
{
    /// <param name="text">要编码的内容（带 PIN 的完整访问地址）</param>
    /// <param name="size">显示边长（px）；viewBox 用模块数，缩放不失真</param>
    public static string Render(string text, int size = 220)
    {
        if (string.IsNullOrEmpty(text)) return "";

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var n = matrix.Count;

        // 四周留 4 模块静区：没有静区时很多手机摄像头识别率明显下降
        const int quiet = 4;
        var total = n + quiet * 2;

        // 每个黑模块画一个 1x1 方块，路径连写比逐个 <rect> 小很多
        var path = new StringBuilder();
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (matrix[y][x]) path.Append($"M{x + quiet} {y + quiet}h1v1h-1z");
            }
        }

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {total} {total}" width="{size}" height="{size}" shape-rendering="crispEdges" role="img" aria-label="扫码访问只读页">
            <rect width="{total}" height="{total}" fill="#ffffff"/>
            <path fill="#000000" d="{path}"/>
            </svg>
            """;
    }
}
