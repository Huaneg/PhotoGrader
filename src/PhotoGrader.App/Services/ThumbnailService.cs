using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace PhotoGrader.App.Services;

/// <summary>
/// 缩略图生成与缓存。
///
/// 7485 张 4~5 MB 的 PNG 若全量解码约需 200 秒以上，因此必须三管齐下：
/// 解码时按目标宽度缩小（WIC 的 DecodePixelWidth，避免生成全分辨率位图）、
/// 结果落盘缓存、内存再做一层 LRU 控制。
///
/// 缓存同样属于可丢弃数据，删掉只会导致重新生成。
/// </summary>
public sealed class ThumbnailService
{
    private const int MemoryCacheCapacity = 512;

    private readonly string _diskCacheRoot;
    private readonly ConcurrentDictionary<string, byte[]> _memoryCache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _memoryOrder = new();
    private readonly SemaphoreSlim _decodeGate;

    public ThumbnailService(string diskCacheRoot)
    {
        _diskCacheRoot = diskCacheRoot;
        Directory.CreateDirectory(_diskCacheRoot);
        _decodeGate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
    }

    public int MemoryCacheCount => _memoryCache.Count;

    /// <summary>诊断信息回调，用于把生成失败的原因暴露出来。</summary>
    public Action<string>? OnDiagnostic { get; set; }

    /// <summary>取缩略图字节（JPEG）。失败返回 null。</summary>
    public byte[]? Get(string imagePath, string relativePath, int width)
    {
        width = Math.Clamp(width, 48, 1024);
        string key = BuildKey(relativePath, width);

        if (_memoryCache.TryGetValue(key, out byte[]? cached))
        {
            return cached;
        }

        string diskPath = Path.Combine(_diskCacheRoot, key + ".jpg");
        if (File.Exists(diskPath))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(diskPath);
                Remember(key, bytes);
                return bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 缓存读取失败则回退到重新生成
            }
        }

        byte[]? generated = Generate(imagePath, width);
        if (generated is null) return null;

        Remember(key, generated);

        try
        {
            File.WriteAllBytes(diskPath, generated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去不影响显示
        }

        return generated;
    }

    /// <summary>删除整个磁盘缓存。</summary>
    public void ClearDiskCache()
    {
        try
        {
            if (Directory.Exists(_diskCacheRoot)) Directory.Delete(_diskCacheRoot, recursive: true);
            Directory.CreateDirectory(_diskCacheRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _memoryCache.Clear();
    }

    public long DiskCacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(_diskCacheRoot)) return 0;
            return Directory.EnumerateFiles(_diskCacheRoot, "*.jpg")
                .Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private byte[]? Generate(string imagePath, int width)
    {
        _decodeGate.Wait();
        try
        {
            using FileStream stream = new(
                imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = width;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            OnDiagnostic?.Invoke(
                $"[thumb] 生成失败 {Path.GetFileName(imagePath)}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private void Remember(string key, byte[] bytes)
    {
        if (_memoryCache.TryAdd(key, bytes))
        {
            _memoryOrder.Enqueue(key);

            while (_memoryCache.Count > MemoryCacheCapacity && _memoryOrder.TryDequeue(out string? oldest))
            {
                _memoryCache.TryRemove(oldest, out _);
            }
        }
    }

    private static string BuildKey(string relativePath, int width)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(relativePath.ToLowerInvariant()));
        return $"{Convert.ToHexString(hash)[..16]}_{width}";
    }
}
