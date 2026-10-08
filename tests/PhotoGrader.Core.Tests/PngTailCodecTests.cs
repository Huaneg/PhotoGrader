namespace PhotoGrader.Core.Tests;

public class Crc32Tests
{
    [Fact]
    public void Iend_Chunk_Crc_Matches_Png_Spec()
    {
        // PNG 规范中 IEND 块的 CRC 是固定值 0xAE426082
        Assert.Equal(0xAE426082u, Crc32.Compute("IEND"u8));
    }

    [Fact]
    public void Empty_Input_Yields_Zero()
    {
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Split_Computation_Matches_Single_Pass()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Equal(Crc32.Compute(data), Crc32.Compute(data.AsSpan(0, 3), data.AsSpan(3)));
    }

    [Fact]
    public void Different_Data_Yields_Different_Crc()
    {
        Assert.NotEqual(Crc32.Compute("abc"u8), Crc32.Compute("abd"u8));
    }
}

public class PngTailCodecTests
{
    private static GradeRecord Sample() => new()
    {
        Rating = 4,
        Flag = GradeFlag.Picked,
        Label = GradeLabel.Red,
        Timestamp = 1784726400,
        OriginPath = @"D:\GPT Image\01-教室侧坐回头\task-abc123.png",
    };

    [Fact]
    public void Size_Constants_Are_Consistent()
    {
        Assert.Equal(512, PngTailCodec.PayloadSize);
        Assert.Equal(524, PngTailCodec.ChunkSize);
        Assert.Equal(536, PngTailCodec.TailSize);
        Assert.Equal(496, PngTailCodec.TextCapacity);
    }

    [Fact]
    public void Build_Produces_Expected_Size()
    {
        Assert.Equal(536, PngTailCodec.Build(Sample()).Length);
    }

    [Fact]
    public void RoundTrip_Preserves_Every_Field()
    {
        GradeRecord original = Sample();
        byte[] tail = PngTailCodec.Build(original);

        Assert.True(PngTailCodec.TryParse(tail, out GradeRecord? parsed, out string? reason), reason);
        Assert.NotNull(parsed);
        Assert.Equal(original.Rating, parsed!.Rating);
        Assert.Equal(original.Flag, parsed.Flag);
        Assert.Equal(original.Label, parsed.Label);
        Assert.Equal(original.Timestamp, parsed.Timestamp);
        Assert.Equal(original.OriginPath, parsed.OriginPath);
    }

    [Theory]
    [InlineData(0, GradeFlag.None, GradeLabel.None)]
    [InlineData(5, GradeFlag.Rejected, GradeLabel.Purple)]
    [InlineData(1, GradeFlag.Picked, GradeLabel.Blue)]
    [InlineData(3, GradeFlag.None, GradeLabel.Green)]
    [InlineData(2, GradeFlag.Rejected, GradeLabel.Yellow)]
    public void RoundTrip_Covers_All_Enum_Combinations(int rating, GradeFlag flag, GradeLabel label)
    {
        var original = new GradeRecord { Rating = rating, Flag = flag, Label = label };
        byte[] tail = PngTailCodec.Build(original);

        Assert.True(PngTailCodec.TryParse(tail, out GradeRecord? parsed, out string? reason), reason);
        Assert.Equal(rating, parsed!.Rating);
        Assert.Equal(flag, parsed.Flag);
        Assert.Equal(label, parsed.Label);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(9, 5)]
    [InlineData(int.MaxValue, 5)]
    [InlineData(int.MinValue, 0)]
    public void Out_Of_Range_Rating_Is_Clamped(int input, int expected)
    {
        byte[] tail = PngTailCodec.Build(new GradeRecord { Rating = input });

        Assert.True(PngTailCodec.TryParse(tail, out GradeRecord? parsed, out _));
        Assert.Equal(expected, parsed!.Rating);
    }

    [Fact]
    public void Undefined_Enum_Values_Fall_Back_To_None()
    {
        byte[] tail = PngTailCodec.Build(new GradeRecord
        {
            Rating = 3,
            Flag = (GradeFlag)77,
            Label = (GradeLabel)99,
        });

        Assert.True(PngTailCodec.TryParse(tail, out GradeRecord? parsed, out _));
        Assert.Equal(GradeFlag.None, parsed!.Flag);
        Assert.Equal(GradeLabel.None, parsed.Label);
    }

    [Fact]
    public void Null_OriginPath_Is_Omitted_From_Payload()
    {
        byte[] tail = PngTailCodec.Build(new GradeRecord { Rating = 3 });

        Assert.True(PngTailCodec.TryParse(tail, out GradeRecord? parsed, out _));
        Assert.Null(parsed!.OriginPath);
    }

    [Fact]
    public void Broken_Crc_Fails_Parsing()
    {
        byte[] tail = PngTailCodec.Build(Sample());
        tail[8 + PngTailCodec.PayloadSize] ^= 0xFF;

        Assert.False(PngTailCodec.TryParse(tail, out _, out string? reason));
        Assert.Contains("CRC", reason);
    }

    [Fact]
    public void Broken_Keyword_Keeps_Shape_But_Blocks_Overwrite()
    {
        byte[] tail = PngTailCodec.Build(Sample());
        tail[8] = (byte)'X';

        Assert.True(PngTailCodec.LooksLikeOurBlock(tail));
        Assert.False(PngTailCodec.IsOverwritable(tail));
    }

    [Fact]
    public void Missing_Iend_Fails_Parsing()
    {
        byte[] tail = PngTailCodec.Build(Sample());
        tail[^1] ^= 0xFF;

        Assert.False(PngTailCodec.TryParse(tail, out _, out string? reason));
        Assert.Contains("IEND", reason);
    }

    [Fact]
    public void Wrong_Declared_Length_Fails_Parsing()
    {
        byte[] tail = PngTailCodec.Build(Sample());
        tail[3] = 0x10;

        Assert.False(PngTailCodec.TryParse(tail, out _, out string? reason));
        Assert.Contains("长度", reason);
    }

    [Fact]
    public void Oversized_Payload_Throws()
    {
        var huge = new GradeRecord { Rating = 5, OriginPath = new string('x', 5000) };

        Assert.Throws<GradeStoreException>(() => PngTailCodec.Build(huge));
    }

    [Fact]
    public void Payload_Is_Deterministic()
    {
        byte[] first = PngTailCodec.Build(Sample());
        byte[] second = PngTailCodec.Build(Sample());

        Assert.Equal(first, second);
    }

    [Fact]
    public void Maximal_Path_Still_Fits()
    {
        // 文本区 496 字节，扣除 JSON 结构开销后仍能容纳约 440 字节的路径
        var record = new GradeRecord
        {
            Rating = 5,
            Label = GradeLabel.Purple,
            Flag = GradeFlag.Picked,
            Timestamp = long.MaxValue,
            OriginPath = new string('a', 250) + @"\file.png",
        };

        byte[] tail = PngTailCodec.Build(record);
        Assert.True(PngTailCodec.TryParse(tail, out GradeRecord? parsed, out _));
        Assert.Equal(record.OriginPath, parsed!.OriginPath);
    }

    [Fact]
    public void Non_Ascii_Path_Consumes_Multi_Byte_Utf8()
    {
        // 中文路径按 UTF-8 每字 3 字节计，需确认编码正确往返
        var record = new GradeRecord
        {
            Rating = 3,
            OriginPath = @"D:\GPT Image\13-吕津会战\子目录\图片.png",
        };

        byte[] tail = PngTailCodec.Build(record);
        Assert.True(PngTailCodec.TryParse(tail, out GradeRecord? parsed, out _));
        Assert.Equal(record.OriginPath, parsed!.OriginPath);
    }
}
