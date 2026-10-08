namespace PhotoGrader.Core;

/// <summary>旗标，对应 Lightroom 的留用 / 排除。</summary>
public enum GradeFlag
{
    /// <summary>排除（黑旗）。</summary>
    Rejected = -1,

    /// <summary>未标记。</summary>
    None = 0,

    /// <summary>留用（白旗）。</summary>
    Picked = 1,
}

/// <summary>彩色标签，对应 Lightroom 的五色彩旗。</summary>
public enum GradeLabel
{
    None = 0,
    Red = 1,
    Yellow = 2,
    Green = 3,
    Blue = 4,
    Purple = 5,
}

/// <summary>
/// 单张图片的评分记录。这是被写入图片文件本身的全部内容，不依赖任何外部存储。
/// </summary>
public sealed class GradeRecord
{
    /// <summary>当前载荷格式版本。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>星级。0 表示未评分，有效范围 0..5。</summary>
    public int Rating { get; set; }

    public GradeFlag Flag { get; set; }

    public GradeLabel Label { get; set; }

    /// <summary>最后修改时间（Unix 秒，UTC）。</summary>
    public long Timestamp { get; set; }

    /// <summary>写入时的文件路径，用于追溯图片来源。可为 null。</summary>
    public string? OriginPath { get; set; }

    /// <summary>三个评分维度是否都为默认值。</summary>
    public bool IsEmpty =>
        Rating == 0 && Flag == GradeFlag.None && Label == GradeLabel.None;

    public GradeRecord Clone() => new()
    {
        Version = Version,
        Rating = Rating,
        Flag = Flag,
        Label = Label,
        Timestamp = Timestamp,
        OriginPath = OriginPath,
    };

    /// <summary>只比较三个评分维度，忽略时间戳与路径。</summary>
    public bool SameGradeAs(GradeRecord other) =>
        Rating == other.Rating && Flag == other.Flag && Label == other.Label;

    /// <summary>把越界取值收敛到合法范围。</summary>
    public GradeRecord Normalized()
    {
        var copy = Clone();
        copy.Rating = Math.Clamp(copy.Rating, 0, 5);
        if (!Enum.IsDefined(copy.Flag)) copy.Flag = GradeFlag.None;
        if (!Enum.IsDefined(copy.Label)) copy.Label = GradeLabel.None;
        return copy;
    }

    public override string ToString() =>
        $"Rating={Rating} Flag={Flag} Label={Label}";
}

/// <summary>读取结果的状态。</summary>
public enum GradeStatus
{
    /// <summary>成功读到评分。</summary>
    Ok,

    /// <summary>PNG 文件有效，但尚未写入过评分。</summary>
    NoGrade,

    /// <summary>存在本软件的块，但校验失败（被截断或内容损坏）。</summary>
    Corrupted,

    /// <summary>文件过小，或不具备 PNG 签名。</summary>
    Unsupported,
}

/// <summary>图片像素尺寸。Unknown 表示未读取或读取失败。</summary>
public readonly record struct ImageDimension(int Width, int Height)
{
    public static readonly ImageDimension Unknown = new(0, 0);

    public bool IsKnown => Width > 0 && Height > 0;

    /// <summary>宽高比。尺寸未知时退回 16:9，保证布局不塌陷。</summary>
    public double Aspect => IsKnown ? (double)Width / Height : 16.0 / 9.0;
}

/// <summary>读取评分的结果。</summary>
/// <param name="Status">读取状态。</param>
/// <param name="Record">读到的评分记录，仅当 Status 为 Ok 时非空。</param>
/// <param name="BaseLength">不含本软件块的原始文件长度。</param>
/// <param name="Dimension">图片像素尺寸，随同一次读取取得。</param>
/// <param name="Detail">失败原因等补充信息。</param>
public readonly record struct GradeReadResult(
    GradeStatus Status,
    GradeRecord? Record,
    long BaseLength,
    ImageDimension Dimension,
    string? Detail)
{
    public bool HasGrade => Status == GradeStatus.Ok && Record is not null;
}

/// <summary>写入评分的结果。</summary>
/// <param name="Created">true 表示首次写入（文件增长 524 字节）；false 表示原地覆盖。</param>
/// <param name="BytesWritten">本次实际写入的字节数。</param>
/// <param name="Elapsed">写入耗时。</param>
public readonly record struct GradeWriteResult(
    bool Created,
    int BytesWritten,
    TimeSpan Elapsed);

/// <summary>元数据读写过程中出现的错误。</summary>
public sealed class GradeStoreException : Exception
{
    public GradeStoreException(string message) : base(message) { }

    public GradeStoreException(string message, Exception inner) : base(message, inner) { }
}
