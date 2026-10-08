using System.Security.Cryptography;

namespace PhotoGrader.Core;

/// <summary>单次移动的结果。</summary>
/// <param name="Source">源路径。</param>
/// <param name="Destination">目标路径，失败时为 null。</param>
/// <param name="Moved">是否成功移动。</param>
/// <param name="Renamed">是否因同名冲突而自动改名。</param>
/// <param name="Error">失败原因。</param>
public readonly record struct MoveOutcome(
    string Source,
    string? Destination,
    bool Moved,
    bool Renamed,
    string? Error);

/// <summary>
/// 把图片移动到指定目录，用于「归类汇总」。
///
/// 同盘用 File.Move（原子、瞬时）；跨盘走「复制 → 校验哈希 → 删源」，
/// 校验失败就回滚，绝不在源文件未确认落地时删除它。
/// 目标目录已有同名文件时自动改名为「名字 (2).png」，不覆盖任何既有文件。
/// </summary>
public static class FileMover
{
    public static MoveOutcome Move(string sourcePath, string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);

        try
        {
            if (!File.Exists(sourcePath))
            {
                return new MoveOutcome(sourcePath, null, false, false, "源文件不存在");
            }

            Directory.CreateDirectory(targetDirectory);

            string target = ResolveTargetPath(sourcePath, targetDirectory);
            bool renamed = !string.Equals(
                Path.GetFileName(sourcePath), Path.GetFileName(target), StringComparison.Ordinal);

            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath)) ?? string.Empty;
            string targetRoot = Path.GetPathRoot(Path.GetFullPath(target)) ?? string.Empty;

            if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(sourcePath, target);
            }
            else
            {
                File.Copy(sourcePath, target, overwrite: false);

                if (!SameContent(sourcePath, target))
                {
                    TryDelete(target);
                    return new MoveOutcome(sourcePath, null, false, renamed, "跨盘复制后校验不一致，已回滚");
                }

                File.Delete(sourcePath);
            }

            return new MoveOutcome(sourcePath, target, true, renamed, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return new MoveOutcome(sourcePath, null, false, false, ex.Message);
        }
    }

    /// <summary>从列表推导出目标目录下的可用文件名，避免覆盖。</summary>
    public static string ResolveTargetPath(string sourcePath, string targetDirectory)
    {
        string name = Path.GetFileName(sourcePath);
        string candidate = Path.Combine(targetDirectory, name);
        if (!File.Exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);

        for (int index = 2; index < 100000; index++)
        {
            candidate = Path.Combine(targetDirectory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        throw new IOException($"无法为 {name} 生成不冲突的文件名");
    }

    private static bool SameContent(string first, string second)
    {
        var left = new FileInfo(first);
        var right = new FileInfo(second);

        if (left.Length != right.Length) return false;
        if (left.Length == 0) return true;

        using FileStream leftStream = File.OpenRead(first);
        using FileStream rightStream = File.OpenRead(second);

        byte[] leftHash = SHA256.HashData(leftStream);
        byte[] rightHash = SHA256.HashData(rightStream);

        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
