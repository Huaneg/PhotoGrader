namespace PhotoGrader.Core;

/// <summary>图库中的一张图片。</summary>
public sealed class PhotoEntry
{
    /// <summary>绝对路径。</summary>
    public required string FullPath { get; init; }

    /// <summary>相对于图库根目录的路径，用作索引键与前端标识。</summary>
    public required string RelativePath { get; init; }

    /// <summary>文件字节数。写入评分后会刷新。</summary>
    public required long Size { get; set; }

    /// <summary>最后写入时间（UTC ticks）。用于判断索引条目是否失效，写入评分后刷新。</summary>
    public required long ModifiedUtcTicks { get; set; }

    /// <summary>像素尺寸，随同文件头一次读出，供界面还原原始比例。</summary>
    public ImageDimension Dimension { get; set; } = ImageDimension.Unknown;

    /// <summary>文件内容的 MD5（小写十六进制）。null 表示尚未计算。</summary>
    public string? Md5 { get; set; }

    /// <summary>
    /// 已作为重复文件移出图库（剪切到归档目录）。
    /// 本次会话内不再参与重复检测，也不出现在图库墙上。
    /// </summary>
    public bool Archived { get; set; }

    /// <summary>已删除（移入系统回收站）。</summary>
    public bool Deleted { get; set; }

    /// <summary>已经离开图库：被归档或被删除。</summary>
    public bool IsGone => Archived || Deleted;

    /// <summary>MD5 是否已知。</summary>
    public bool HasMd5 => !string.IsNullOrEmpty(Md5);

    /// <summary>MD5 的短显示形式，用于界面展示。</summary>
    public string Md5Short => Md5 is { Length: 32 } ? Md5[..8] : "——";

    /// <summary>评分。null 表示尚未评分。</summary>
    public GradeRecord? Grade { get; set; }

    public int Width => Dimension.Width;

    public int Height => Dimension.Height;

    /// <summary>宽高比。尺寸未知时退回 16:9，避免布局塌陷。</summary>
    public double Aspect => Dimension.Aspect;

    public string FileName => Path.GetFileName(FullPath);

    /// <summary>
    /// 所属目录：相对图库根的完整路径，统一用 / 分隔；直接放在根目录时为空串。
    ///
    /// 注意这里必须保留完整层级而不是只取最后一段：否则
    /// `A/第二级` 与 `B/第二级` 会被并成同一个条目，
    /// 且点父目录时看不到子目录里的图片。
    /// </summary>
    public string FolderName
    {
        get
        {
            string? dir = Path.GetDirectoryName(RelativePath);
            return string.IsNullOrEmpty(dir)
                ? string.Empty
                : dir.Replace('\\', '/');
        }
    }

    public bool HasGrade => Grade is not null;

    public int Rating => Grade?.Rating ?? 0;

    public GradeFlag Flag => Grade?.Flag ?? GradeFlag.None;

    public GradeLabel Label => Grade?.Label ?? GradeLabel.None;

    public override string ToString() => $"{RelativePath} [{Rating}★ {Label} {Flag}]";
}

/// <summary>一次图库加载的统计。</summary>
/// <param name="TotalFiles">扫描到的图片总数。</param>
/// <param name="FromIndex">直接复用索引、未打开文件的条目数。</param>
/// <param name="ReadFromDisk">实际读取了文件尾部的条目数。</param>
/// <param name="Graded">已评分的条目数。</param>
/// <param name="Unreadable">读取失败或结构异常的文件数。</param>
/// <param name="Elapsed">总耗时。</param>
public sealed record LibraryLoadReport(
    int TotalFiles,
    int FromIndex,
    int ReadFromDisk,
    int Graded,
    int Unreadable,
    TimeSpan Elapsed)
{
    public override string ToString() =>
        $"共 {TotalFiles} 张｜索引命中 {FromIndex}｜实读 {ReadFromDisk}｜"
        + $"已评分 {Graded}｜异常 {Unreadable}｜{Elapsed.TotalMilliseconds:F0} ms";
}
