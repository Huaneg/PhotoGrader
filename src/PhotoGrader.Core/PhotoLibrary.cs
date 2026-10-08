using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace PhotoGrader.Core;

/// <summary>
/// 图库：扫描一个根目录下的全部图片，并加载它们的评分。
///
/// 加载流程分两段：先遍历目录拿到每个文件的 mtime 与大小（不打开文件），
/// 再与索引比对——只有新增或被改动过的文件才真正读取尾部 536 字节。
/// 这样二次加载可以跳过绝大多数 I/O。
/// </summary>
public sealed class PhotoLibrary
{
    /// <summary>缓存目录名。存放索引与缩略图，扫描时自动跳过。</summary>
    public const string CacheFolderName = ".pgcache";

    /// <summary>
    /// 重复文件归档目录名。被移出的重复文件放在这里，
    /// 并且该目录不参与图库扫描 —— 否则归档的文件会被再次当成重复检测出来。
    /// </summary>
    public const string DuplicatesFolderName = "_duplicates_MD5";

    public const string IndexFileName = "index.bin";

    private readonly List<PhotoEntry> _entries = [];

    private PhotoLibrary(string rootPath)
    {
        RootPath = rootPath;
        CacheDirectory = Path.Combine(rootPath, CacheFolderName);
        IndexFilePath = Path.Combine(CacheDirectory, IndexFileName);
    }

    public string RootPath { get; }

    public string CacheDirectory { get; }

    public string IndexFilePath { get; }

    public IReadOnlyList<PhotoEntry> Entries => _entries;

    public LibraryLoadReport? LastReport { get; private set; }

    /// <summary>加载图库。useIndex 为 false 时强制全量读取。</summary>
    public static PhotoLibrary Load(string rootPath, bool useIndex = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        string fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"图库根目录不存在：{fullRoot}");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        var library = new PhotoLibrary(fullRoot);

        Dictionary<string, GradeIndexEntry> index = useIndex
            ? GradeIndexFile.Load(library.IndexFilePath)
            : new Dictionary<string, GradeIndexEntry>(StringComparer.OrdinalIgnoreCase);

        List<FileCandidate> candidates = CollectFiles(fullRoot);

        var entries = new ConcurrentBag<PhotoEntry>();
        int fromIndex = 0;
        int readFromDisk = 0;
        int unreadable = 0;

        Parallel.ForEach(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount * 2),
            },
            candidate =>
            {
                GradeRecord? grade;
                ImageDimension dimension = ImageDimension.Unknown;
                string? md5 = null;

                bool indexHit = index.TryGetValue(candidate.RelativePath, out GradeIndexEntry? cached)
                    && cached!.Size == candidate.Size
                    && cached.ModifiedUtcTicks == candidate.Ticks;

                if (indexHit)
                {
                    grade = cached!.Grade;
                    dimension = new ImageDimension(cached.Width, cached.Height);
                    md5 = cached.Md5;
                    Interlocked.Increment(ref fromIndex);
                }
                else
                {
                    GradeReadResult result;
                    try
                    {
                        result = GradeStore.Read(candidate.FullPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        result = new GradeReadResult(
                            GradeStatus.Unsupported, null, 0, ImageDimension.Unknown, ex.Message);
                    }

                    grade = result.HasGrade ? result.Record : null;
                    dimension = result.Dimension;

                    if (result.Status is GradeStatus.Corrupted or GradeStatus.Unsupported)
                    {
                        Interlocked.Increment(ref unreadable);
                    }

                    Interlocked.Increment(ref readFromDisk);
                }

                entries.Add(new PhotoEntry
                {
                    FullPath = candidate.FullPath,
                    RelativePath = candidate.RelativePath,
                    Size = candidate.Size,
                    ModifiedUtcTicks = candidate.Ticks,
                    Dimension = dimension,
                    Md5 = md5,
                    Grade = grade,
                });
            });

        library._entries.AddRange(entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase));
        stopwatch.Stop();

        library.LastReport = new LibraryLoadReport(
            library._entries.Count,
            fromIndex,
            readFromDisk,
            library._entries.Count(e => e.HasGrade),
            unreadable,
            stopwatch.Elapsed);

        if (useIndex)
        {
            GradeIndexFile.Save(library.IndexFilePath, library._entries);
        }

        return library;
    }

    // ---------- MD5 与重复检测 ----------

    private Dictionary<string, List<PhotoEntry>>? _md5Index;

    /// <summary>
    /// 生成 MD5 计算队列。先算「文件大小存在重复」的条目 —— 不同大小的文件
    /// 不可能内容相同，优先处理这些能让用户更快看到重复结果。
    /// </summary>
    public IReadOnlyList<PhotoEntry> BuildMd5Queue()
    {
        var sizeCounts = new Dictionary<long, int>();
        foreach (PhotoEntry entry in _entries)
        {
            sizeCounts[entry.Size] = sizeCounts.GetValueOrDefault(entry.Size) + 1;
        }

        return
        [
            .. _entries
                .Where(e => !e.HasMd5)
                .OrderByDescending(e => sizeCounts.GetValueOrDefault(e.Size, 0) > 1)
                .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>记录某个文件的 MD5，并使重复索引失效。</summary>
    public void SetMd5(PhotoEntry entry, string? md5)
    {
        entry.Md5 = md5;
        _md5Index = null;
        IsIndexDirty = true;
    }

    /// <summary>找出与指定文件内容完全相同（MD5 一致）的其他文件。</summary>
    public IReadOnlyList<PhotoEntry> FindDuplicates(PhotoEntry entry)
    {
        if (!entry.HasMd5) return [];

        _md5Index ??= BuildMd5Index();

        if (!_md5Index.TryGetValue(entry.Md5!, out List<PhotoEntry>? group)) return [];

        return [.. group.Where(e => !ReferenceEquals(e, entry) && !e.IsGone)];
    }

    /// <summary>把文件移入系统回收站，成功后标记为已删除。</summary>
    public IReadOnlyList<(PhotoEntry Entry, bool Ok, string? Error)> DeleteToRecycleBin(
        IReadOnlyList<PhotoEntry> entries)
    {
        var results = new List<(PhotoEntry, bool, string?)>(entries.Count);

        foreach (PhotoEntry entry in entries)
        {
            (bool ok, string? error) = FileDeleter.MoveToRecycleBin(entry.FullPath);
            results.Add((entry, ok, error));

            if (ok)
            {
                entry.Deleted = true;
                _md5Index = null;
                IsIndexDirty = true;
            }
        }

        return results;
    }

    /// <summary>
    /// 把重复文件归档：移动到 &lt;图库根&gt;/_duplicates_MD5，并标记为已归档。
    /// 归档后它们不再参与重复检测，图库墙也不再显示。
    /// </summary>
    public IReadOnlyList<MoveOutcome> ArchiveDuplicates(IReadOnlyList<PhotoEntry> entries)
    {
        string targetDirectory = Path.Combine(RootPath, DuplicatesFolderName);
        var results = new List<MoveOutcome>(entries.Count);

        foreach (PhotoEntry entry in entries)
        {
            MoveOutcome outcome = FileMover.Move(entry.FullPath, targetDirectory);
            results.Add(outcome);

            if (outcome.Moved)
            {
                entry.Archived = true;
                _md5Index = null;
                IsIndexDirty = true;
            }
        }

        return results;
    }

    /// <summary>所有成员多于一个的重复组，按组内数量降序。</summary>
    public IReadOnlyList<IReadOnlyList<PhotoEntry>> FindAllDuplicateGroups()
    {
        _md5Index ??= BuildMd5Index();

        return
        [
            .. _md5Index.Values
                .Where(group => group.Count > 1)
                .OrderByDescending(group => group.Count)
                .Select(group => (IReadOnlyList<PhotoEntry>)group),
        ];
    }

    /// <summary>已算出 MD5 的条目数。</summary>
    public int Md5KnownCount => _entries.Count(e => e.HasMd5);

    /// <summary>条目在列表中的位置，找不到返回 -1。</summary>
    public int IndexOf(PhotoEntry entry) => _entries.IndexOf(entry);

    private Dictionary<string, List<PhotoEntry>> BuildMd5Index()
    {
        var map = new Dictionary<string, List<PhotoEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (PhotoEntry entry in _entries)
        {
            // 已离开图库的条目（被归档或删除）不再参与任何重复统计
            if (!entry.HasMd5 || entry.IsGone) continue;

            if (!map.TryGetValue(entry.Md5!, out List<PhotoEntry>? group))
            {
                group = [];
                map[entry.Md5!] = group;
            }

            group.Add(entry);
        }

        return map;
    }

    /// <summary>把当前内存中的评分状态写回索引。</summary>
    public void SaveIndex()
    {
        GradeIndexFile.Save(IndexFilePath, _entries);
        IsIndexDirty = false;
    }

    /// <summary>索引是否有未落盘的变更。</summary>
    public bool IsIndexDirty { get; private set; }

    /// <summary>
    /// 给一张图片写入评分，并刷新它在索引中的变更指纹。
    /// 写入会改变文件的 mtime，若不刷新，下次加载会白白重读这些文件。
    /// </summary>
    public GradeWriteResult SetGrade(PhotoEntry entry, GradeRecord record)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(record);

        GradeWriteResult result = GradeStore.Write(entry.FullPath, record);
        entry.Grade = record;
        RefreshFingerprint(entry);
        IsIndexDirty = true;
        return result;
    }

    /// <summary>重新读取文件的大小与 mtime，保持索引有效。</summary>
    public static void RefreshFingerprint(PhotoEntry entry)
    {
        try
        {
            var info = new FileInfo(entry.FullPath);
            if (!info.Exists) return;

            entry.Size = info.Length;
            entry.ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 取不到指纹时保持原值，下次加载会重新读取该文件
        }
    }

    /// <summary>按相对路径取条目（容忍正/反斜杠混用，供 URL 寻址使用）。</summary>
    public PhotoEntry? Find(string relativePath)
    {
        string wanted = relativePath.Replace('\\', '/');

        return _entries.FirstOrDefault(
            e => string.Equals(
                e.RelativePath.Replace('\\', '/'),
                wanted,
                StringComparison.OrdinalIgnoreCase));
    }

    private static List<FileCandidate> CollectFiles(string root)
    {
        var results = new List<FileCandidate>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
            MatchType = MatchType.Simple,
            ReturnSpecialDirectories = false,
        };

        // FileSystemEnumerable 在枚举时直接带出长度与修改时间，
        // 不必再对每个文件额外做一次 stat —— 这是二次加载的主要开销来源。
        var enumerable = new FileSystemEnumerable<FileCandidate>(
            root,
            (ref FileSystemEntry entry) =>
            {
                string fullPath = entry.ToFullPath();
                return new FileCandidate(
                    fullPath,
                    Path.GetRelativePath(root, fullPath),
                    entry.Length,
                    entry.LastWriteTimeUtc.UtcTicks);
            },
            options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                !entry.IsDirectory
                && entry.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase),
            ShouldRecursePredicate = static (ref FileSystemEntry entry) =>
                !entry.FileName.Equals(CacheFolderName, StringComparison.OrdinalIgnoreCase)
                && !entry.FileName.Equals(DuplicatesFolderName, StringComparison.OrdinalIgnoreCase),
        };

        foreach (FileCandidate candidate in enumerable)
        {
            results.Add(candidate);
        }

        return results;
    }

    private readonly record struct FileCandidate(
        string FullPath,
        string RelativePath,
        long Size,
        long Ticks);
}
