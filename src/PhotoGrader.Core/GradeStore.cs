using System.Buffers.Binary;
using System.Diagnostics;

namespace PhotoGrader.Core;

/// <summary>
/// 评分读写入口。所有评分数据都写入图片文件自身，不使用数据库或 sidecar 文件。
///
/// 读写策略：
/// 读取只取文件末尾 536 字节，与图片大小无关；
/// 写入时若块已存在则原地覆盖 524 字节（不触碰图片数据，IEND 也不用动），
/// 首次写入才在 IEND 之前插入块并使文件增长 524 字节。
/// </summary>
public static class GradeStore
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>头部读取长度：签名 8 + IHDR 长度 4 + 类型 4 + 宽 4 + 高 4。</summary>
    private const int HeaderSize = 24;

    /// <summary>读取指定图片的评分。</summary>
    public static GradeReadResult Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        long length = stream.Length;

        if (length < HeaderSize)
        {
            return new GradeReadResult(
                GradeStatus.Unsupported, null, length, ImageDimension.Unknown, "文件长度不足");
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);

        if (!header[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return new GradeReadResult(
                GradeStatus.Unsupported, null, length, ImageDimension.Unknown, "不是 PNG 文件");
        }

        ImageDimension dimension = ReadDimension(header);

        if (length < PngTailCodec.TailSize)
        {
            return new GradeReadResult(
                GradeStatus.Unsupported, null, length, dimension,
                $"文件仅 {length} 字节，不足以容纳 {PngTailCodec.TailSize} 字节的评分块");
        }

        Span<byte> tail = stackalloc byte[PngTailCodec.TailSize];
        stream.Seek(-PngTailCodec.TailSize, SeekOrigin.End);
        stream.ReadExactly(tail);

        if (PngTailCodec.TryParse(tail, out GradeRecord? record, out string? reason))
        {
            return new GradeReadResult(
                GradeStatus.Ok, record, length - PngTailCodec.ChunkSize, dimension, null);
        }

        bool endsWithIend = PngTailCodec.EndsWithIend(tail);
        bool looksLikeOurs = PngTailCodec.LooksLikeOurBlock(tail);

        if (endsWithIend && !looksLikeOurs)
        {
            // 干净的 PNG，从未写过评分
            return new GradeReadResult(GradeStatus.NoGrade, null, length, dimension, null);
        }

        return new GradeReadResult(
            GradeStatus.Corrupted, null, length - PngTailCodec.ChunkSize, dimension, reason);
    }

    /// <summary>从文件头取出 IHDR 中的像素尺寸。</summary>
    private static ImageDimension ReadDimension(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize) return ImageDimension.Unknown;
        if (!header.Slice(12, 4).SequenceEqual("IHDR"u8)) return ImageDimension.Unknown;

        int width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));

        return width > 0 && height > 0
            ? new ImageDimension(width, height)
            : ImageDimension.Unknown;
    }

    /// <summary>
    /// 写入评分。写入完成后会回读校验，确认落盘数据可解析且与期望一致。
    /// </summary>
    public static GradeWriteResult Write(string filePath, GradeRecord record)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(record);

        Stopwatch stopwatch = Stopwatch.StartNew();
        byte[] tail = PngTailCodec.Build(record);
        bool created;
        int bytesWritten;

        using (var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            long length = stream.Length;
            if (length < PngTailCodec.IendSize + PngSignature.Length)
            {
                throw new GradeStoreException($"文件过小，不是有效 PNG：{filePath}");
            }

            Span<byte> signature = stackalloc byte[8];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(PngSignature))
            {
                throw new GradeStoreException($"不是 PNG 文件，当前版本仅支持 PNG：{filePath}");
            }

            bool overwritable = false;
            if (length >= PngTailCodec.TailSize)
            {
                Span<byte> probe = stackalloc byte[PngTailCodec.TailSize];
                stream.Seek(-PngTailCodec.TailSize, SeekOrigin.End);
                stream.ReadExactly(probe);
                overwritable = PngTailCodec.IsOverwritable(probe);

                // 位形匹配但不属于本软件时，宁可报错也不覆盖别人的块
                if (!overwritable && PngTailCodec.LooksLikeOurBlock(probe))
                {
                    throw new GradeStoreException(
                        $"文件尾部存在非本软件写入的 iTXt 块，已拒绝覆盖：{filePath}");
                }
            }

            if (overwritable)
            {
                // 原地覆盖：只写 524 字节，IEND 保持在原位不动
                stream.Seek(-PngTailCodec.TailSize, SeekOrigin.End);
                stream.Write(tail, 0, PngTailCodec.ChunkSize);
                created = false;
                bytesWritten = PngTailCodec.ChunkSize;
            }
            else
            {
                // 首次写入：确认末尾是 IEND，再在其前方插入块
                Span<byte> lastChunk = stackalloc byte[PngTailCodec.IendSize];
                stream.Seek(-PngTailCodec.IendSize, SeekOrigin.End);
                stream.ReadExactly(lastChunk);
                if (!lastChunk.SequenceEqual(PngTailCodec.IendBytes))
                {
                    throw new GradeStoreException($"PNG 结尾不是 IEND，已拒绝写入：{filePath}");
                }

                stream.Seek(-PngTailCodec.IendSize, SeekOrigin.End);
                stream.Write(tail, 0, PngTailCodec.TailSize);
                created = true;
                bytesWritten = PngTailCodec.TailSize;
            }

            // 把数据真正刷到磁盘，避免掉电丢失
            stream.Flush(flushToDisk: true);
        }

        stopwatch.Stop();

        GradeReadResult check = Read(filePath);
        if (!check.HasGrade || !check.Record!.SameGradeAs(record))
        {
            throw new GradeStoreException(
                $"写入后回读校验失败：{filePath}（状态 {check.Status}，{check.Detail ?? "数值不一致"}）");
        }

        return new GradeWriteResult(created, bytesWritten, stopwatch.Elapsed);
    }

    /// <summary>只读取评分维度，读不到或出错时返回 null。</summary>
    public static GradeRecord? TryRead(string filePath)
    {
        try
        {
            GradeReadResult result = Read(filePath);
            return result.HasGrade ? result.Record : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
