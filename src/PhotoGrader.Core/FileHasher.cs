using System.Security.Cryptography;

namespace PhotoGrader.Core;

/// <summary>
/// 文件内容哈希。
///
/// 用于找出内容完全相同的图片：MD5 一致即字节级完全相同，
/// 与文件名、所在目录、修改时间都无关。
/// </summary>
public static class FileHasher
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>计算文件的 MD5，返回小写十六进制字符串。失败返回 null。</summary>
    public static string? TryComputeMd5(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan);

            byte[] hash = MD5.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>把十六进制字符串还原成 16 字节。失败返回 null。</summary>
    public static byte[]? TryParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 32) return null;

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
