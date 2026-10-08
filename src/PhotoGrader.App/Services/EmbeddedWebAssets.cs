using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace PhotoGrader.App.Services;

/// <summary>
/// 内嵌在程序集里的前端资源（wwwroot 下的文件）。
///
/// 为什么需要它：单文件发布时磁盘上没有 wwwroot 目录，
/// 静态文件服务必须有地方取 index.html / app.js / style.css / 字体。
///
/// 命名规则：MSBuild 默认用「根命名空间 + 带点的相对路径」作资源名，
/// 例如 wwwroot/index.html → PhotoGrader.App.wwwroot.index.html。
/// 因此在 <c>.wwwroot.</c> 之后的部分，就是把相对路径的 '/' 换成 '.' 的结果。
/// </summary>
internal static class EmbeddedWebAssets
{
    private const string Marker = ".wwwroot.";

    private static readonly Assembly Host = typeof(EmbeddedWebAssets).Assembly;

    /// <summary>资源名（带点） → 程序集内完整资源名。</summary>
    private static readonly Dictionary<string, string> Index = BuildIndex();

    /// <summary>资源内容缓存。字体有 8 MB 级，每次请求都读一遍太浪费。</summary>
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    /// <summary>按相对路径取资源内容；找不到返回 null。</summary>
    public static byte[]? TryGet(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;

        string key = relativePath.Replace('\\', '/').TrimStart('/').Replace('/', '.');
        if (!Index.TryGetValue(key, out string? resourceName)) return null;

        return Cache.GetOrAdd(resourceName, ReadAll);
    }

    /// <summary>内嵌资源的总数，供日志诊断用。</summary>
    public static int Count => Index.Count;

    private static byte[] ReadAll(string resourceName)
    {
        using Stream? stream = Host.GetManifestResourceStream(resourceName);
        if (stream is null) return [];

        using var memory = new MemoryStream(capacity: (int)Math.Min(stream.Length, int.MaxValue));
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static Dictionary<string, string> BuildIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in Host.GetManifestResourceNames())
        {
            int at = name.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0) continue;

            map[name[(at + Marker.Length)..]] = name;
        }

        return map;
    }
}
