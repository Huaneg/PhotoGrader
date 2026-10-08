using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoGrader.Core;

/// <summary>
/// PNG 尾部 iTXt 块的编码与解码。
///
/// 文件末尾 536 字节的布局：
/// <code>
///   [ 0 ..  4)   data 长度 = 512（大端）
///   [ 4 ..  8)   块类型 "iTXt"
///   [ 8 ..520)   data：keyword + 压缩标志 + 压缩方法 + 语言标签 + 翻译 keyword + 文本
///   [520..524)   CRC-32（覆盖块类型 + data）
///   [524..536)   IEND 块
/// </code>
///
/// 之所以把 data 定长为 512 字节，是为了让块的位置恒等于「文件长度 - 536」，
/// 读取与覆盖都能一次定位，不必扫描整个文件。
/// </summary>
internal static class PngTailCodec
{
    /// <summary>iTXt 块使用的自定义 keyword。规范允许自行发明 keyword 用于其他用途。</summary>
    public const string Keyword = "PhotoGrader";

    /// <summary>iTXt 块 data 字段的固定字节数。</summary>
    public const int PayloadSize = 512;

    /// <summary>完整 iTXt 块字节数：长度 4 + 类型 4 + 数据 512 + CRC 4。</summary>
    public const int ChunkSize = 4 + 4 + PayloadSize + 4;

    /// <summary>IEND 块的固定字节数。</summary>
    public const int IendSize = 12;

    /// <summary>尾部区总字节数：iTXt 块 + IEND。</summary>
    public const int TailSize = ChunkSize + IendSize;

    /// <summary>data 中文本区的起始偏移。</summary>
    public const int TextOffset = 16;

    /// <summary>文本区可容纳的字节数。</summary>
    public const int TextCapacity = PayloadSize - TextOffset;

    private static readonly byte[] ChunkType = "iTXt"u8.ToArray();

    private static readonly byte[] KeywordField = BuildKeywordField();

    private static readonly byte[] Iend =
    [
        0x00, 0x00, 0x00, 0x00,
        (byte)'I', (byte)'E', (byte)'N', (byte)'D',
        0xAE, 0x42, 0x60, 0x82,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static byte[] BuildKeywordField()
    {
        byte[] name = Encoding.ASCII.GetBytes(Keyword);
        var buffer = new byte[name.Length + 1];
        name.CopyTo(buffer, 0);
        buffer[^1] = 0;
        return buffer;
    }

    /// <summary>IEND 块字节，首次写入时需要在块之后重新落盘。</summary>
    public static ReadOnlySpan<byte> IendBytes => Iend;

    /// <summary>
    /// 判断尾部区是否具备本软件块的外形（长度字段与块类型匹配）。
    /// 不校验 CRC 与 keyword，因此即使内容损坏也能识别出「这里曾经有个块」。
    /// </summary>
    public static bool LooksLikeOurBlock(ReadOnlySpan<byte> tail)
    {
        if (tail.Length < ChunkSize) return false;

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(tail[..4]);
        return declaredLength == PayloadSize
            && tail.Slice(4, 4).SequenceEqual(ChunkType);
    }

    /// <summary>判断尾部区是否是一个可以安全覆盖的、确实属于本软件的块。</summary>
    public static bool IsOverwritable(ReadOnlySpan<byte> tail) =>
        LooksLikeOurBlock(tail)
        && tail.Slice(8, KeywordField.Length).SequenceEqual(KeywordField);

    /// <summary>判断尾部区的 IEND 是否位于预期位置。</summary>
    public static bool EndsWithIend(ReadOnlySpan<byte> tail) =>
        tail.Length >= TailSize
        && tail.Slice(ChunkSize, IendSize).SequenceEqual(Iend);

    /// <summary>
    /// 解析尾部区。成功返回 true 并给出评分记录；失败返回 false 并给出原因。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> tail, out GradeRecord? record, out string? reason)
    {
        record = null;
        reason = null;

        if (tail.Length < TailSize)
        {
            reason = "尾部区长度不足";
            return false;
        }

        if (!EndsWithIend(tail))
        {
            reason = "段落末尾不是 IEND";
            return false;
        }

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(tail[..4]);
        if (declaredLength != PayloadSize)
        {
            reason = $"iTXt 长度字段为 {declaredLength}，期望 {PayloadSize}";
            return false;
        }

        if (!tail.Slice(4, 4).SequenceEqual(ChunkType))
        {
            reason = "未找到 iTXt 块";
            return false;
        }

        ReadOnlySpan<byte> data = tail.Slice(8, PayloadSize);
        uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(tail.Slice(8 + PayloadSize, 4));
        uint actualCrc = Crc32.Compute(ChunkType, data);
        if (storedCrc != actualCrc)
        {
            reason = "CRC 校验失败";
            return false;
        }

        if (!data[..KeywordField.Length].SequenceEqual(KeywordField))
        {
            reason = "keyword 不匹配";
            return false;
        }

        ReadOnlySpan<byte> text = data[TextOffset..];
        int end = text.Length;
        while (end > 0 && (text[end - 1] == (byte)' ' || text[end - 1] == 0x00))
        {
            end--;
        }

        if (end == 0)
        {
            reason = "载荷文本为空";
            return false;
        }

        try
        {
            PayloadDto? dto = JsonSerializer.Deserialize<PayloadDto>(text[..end], JsonOptions);
            if (dto is null)
            {
                reason = "载荷反序列化结果为空";
                return false;
            }

            record = dto.ToRecord();
            return true;
        }
        catch (JsonException ex)
        {
            reason = "载荷 JSON 解析失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>构造完整的尾部区（iTXt 块 + IEND），共 536 字节。</summary>
    public static byte[] Build(GradeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        GradeRecord normalized = record.Normalized();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(PayloadDto.FromRecord(normalized), JsonOptions);

        if (json.Length > TextCapacity)
        {
            throw new GradeStoreException(
                $"评分载荷超出容量：{json.Length} 字节，上限 {TextCapacity} 字节。");
        }

        var tail = new byte[TailSize];

        BinaryPrimitives.WriteUInt32BigEndian(tail.AsSpan(0, 4), PayloadSize);
        ChunkType.CopyTo(tail, 4);

        Span<byte> data = tail.AsSpan(8, PayloadSize);
        KeywordField.CopyTo(data);

        int cursor = KeywordField.Length;
        data[cursor++] = 0x00;  // 压缩标志：不压缩
        data[cursor++] = 0x00;  // 压缩方法：仅 0 合法
        data[cursor++] = 0x00;  // 语言标签：空
        data[cursor++] = 0x00;  // 翻译后的 keyword：空

        if (cursor != TextOffset)
        {
            throw new GradeStoreException($"iTXt 头部布局错误：光标停在 {cursor}，期望 {TextOffset}。");
        }

        json.CopyTo(data[TextOffset..]);
        for (int i = TextOffset + json.Length; i < PayloadSize; i++)
        {
            data[i] = (byte)' ';
        }

        Iend.CopyTo(tail, ChunkSize);

        uint crc = Crc32.Compute(ChunkType, data);
        BinaryPrimitives.WriteUInt32BigEndian(tail.AsSpan(8 + PayloadSize, 4), crc);

        return tail;
    }

    /// <summary>载荷的线上表示。字段名刻意取短，以节省固定 512 字节的空间。</summary>
    private sealed class PayloadDto
    {
        [JsonPropertyName("v")] public int Version { get; set; } = GradeRecord.CurrentVersion;

        [JsonPropertyName("r")] public int Rating { get; set; }

        [JsonPropertyName("f")] public int Flag { get; set; }

        [JsonPropertyName("l")] public int Label { get; set; }

        [JsonPropertyName("t")] public long Timestamp { get; set; }

        [JsonPropertyName("p")] public string? OriginPath { get; set; }

        public static PayloadDto FromRecord(GradeRecord r) => new()
        {
            Version = r.Version,
            Rating = r.Rating,
            Flag = (int)r.Flag,
            Label = (int)r.Label,
            Timestamp = r.Timestamp,
            OriginPath = r.OriginPath,
        };

        public GradeRecord ToRecord() => new()
        {
            Version = Version,
            Rating = Rating,
            Flag = (GradeFlag)Flag,
            Label = (GradeLabel)Label,
            Timestamp = Timestamp,
            OriginPath = OriginPath,
        };
    }
}
