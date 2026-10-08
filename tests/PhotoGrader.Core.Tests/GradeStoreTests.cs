namespace PhotoGrader.Core.Tests;

public sealed class GradeStoreTests : IDisposable
{
    private readonly string _dir;

    public GradeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PhotoGraderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private string WriteFile(byte[] content, string name = "sample.png")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static GradeRecord Sample() => new()
    {
        Rating = 4,
        Flag = GradeFlag.Picked,
        Label = GradeLabel.Blue,
        Timestamp = 1784726400,
    };

    // ---------- 读取 ----------

    [Fact]
    public void Clean_Png_Reports_NoGrade()
    {
        string path = WriteFile(TestPng.Create());
        GradeReadResult result = GradeStore.Read(path);

        Assert.Equal(GradeStatus.NoGrade, result.Status);
        Assert.False(result.HasGrade);
    }

    [Fact]
    public void Non_Png_Is_Unsupported()
    {
        string path = WriteFile(new byte[4096]);
        Assert.Equal(GradeStatus.Unsupported, GradeStore.Read(path).Status);
    }

    [Fact]
    public void Too_Small_File_Is_Unsupported()
    {
        // 只有 20 字节，连 24 字节的文件头都不完整
        byte[] tiny = [.. TestPng.Signature, .. TestPng.Iend];
        string path = WriteFile(tiny);

        GradeReadResult result = GradeStore.Read(path);
        Assert.Equal(GradeStatus.Unsupported, result.Status);
        Assert.Contains("长度不足", result.Detail);
    }

    [Fact]
    public void File_Too_Small_For_Grade_Block_Is_Unsupported()
    {
        // 文件头完整，但装不下 536 字节的评分块
        byte[] small = TestPng.Create(bodyBytes: 40);
        string path = WriteFile(small);

        GradeReadResult result = GradeStore.Read(path);
        Assert.Equal(GradeStatus.Unsupported, result.Status);
        Assert.Contains("不足以容纳", result.Detail);
    }

    // ---------- 首次写入 ----------

    [Fact]
    public void First_Write_Grows_File_By_Chunk_Size()
    {
        byte[] original = TestPng.Create();
        string path = WriteFile(original);

        GradeWriteResult write = GradeStore.Write(path, Sample());

        Assert.True(write.Created);
        // 首次写入落盘 536 字节（524 的块 + 重写的 12 字节 IEND），文件净增长 524
        Assert.Equal(PngTailCodec.TailSize, write.BytesWritten);
        Assert.Equal(original.Length + PngTailCodec.ChunkSize, new FileInfo(path).Length);
    }

    [Fact]
    public void First_Write_Leaves_Image_Bytes_Untouched()
    {
        byte[] original = TestPng.Create(bodyBytes: 16384);
        string path = WriteFile(original);
        int imageBytes = TestPng.ImageBytesOf(original.Length);

        GradeStore.Write(path, Sample());

        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(
            original.AsSpan(0, imageBytes).ToArray(),
            after.AsSpan(0, imageBytes).ToArray());

        // 块之后必须紧跟 IEND
        Assert.Equal(
            TestPng.Iend,
            after.AsSpan(imageBytes + PngTailCodec.ChunkSize, PngTailCodec.IendSize).ToArray());
    }

    // ---------- 覆盖写入 ----------

    [Fact]
    public void Second_Write_Overwrites_In_Place()
    {
        string path = WriteFile(TestPng.Create());
        GradeStore.Write(path, Sample());
        long lengthAfterFirst = new FileInfo(path).Length;

        var updated = Sample();
        updated.Rating = 1;
        updated.Label = GradeLabel.Green;
        GradeWriteResult write = GradeStore.Write(path, updated);

        Assert.False(write.Created);
        Assert.Equal(PngTailCodec.ChunkSize, write.BytesWritten);
        Assert.Equal(lengthAfterFirst, new FileInfo(path).Length);

        GradeReadResult read = GradeStore.Read(path);
        Assert.True(read.HasGrade);
        Assert.Equal(1, read.Record!.Rating);
        Assert.Equal(GradeLabel.Green, read.Record.Label);
    }

    [Fact]
    public void Overwrite_Leaves_Image_Bytes_Untouched()
    {
        string path = WriteFile(TestPng.Create(bodyBytes: 16384));
        GradeStore.Write(path, Sample());

        byte[] before = File.ReadAllBytes(path);
        int imageRegion = before.Length - PngTailCodec.TailSize;

        GradeStore.Write(path, new GradeRecord { Rating = 2, Label = GradeLabel.Purple });

        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(
            before.AsSpan(0, imageRegion).ToArray(),
            after.AsSpan(0, imageRegion).ToArray());
    }

    [Fact]
    public void Repeated_Writes_Do_Not_Grow_File()
    {
        string path = WriteFile(TestPng.Create());
        long baseline = 0;

        for (int i = 0; i < 100; i++)
        {
            var record = Sample();
            record.Rating = i % 6;
            GradeStore.Write(path, record);

            long current = new FileInfo(path).Length;
            if (i == 0) baseline = current;
            else Assert.Equal(baseline, current);
        }

        Assert.Equal((100 - 1) % 6, GradeStore.Read(path).Record!.Rating);
    }

    // ---------- 损坏与拒绝 ----------

    [Fact]
    public void Corrupted_Crc_Is_Detected()
    {
        string path = WriteFile(TestPng.Create());
        GradeStore.Write(path, Sample());

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(-(PngTailCodec.IendSize + 4), SeekOrigin.End);
            stream.WriteByte(0xFF);
        }

        Assert.Equal(GradeStatus.Corrupted, GradeStore.Read(path).Status);
    }

    [Fact]
    public void Truncated_Tail_Is_Detected()
    {
        string path = WriteFile(TestPng.Create());
        GradeStore.Write(path, Sample());

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(stream.Length - 8);
        }

        Assert.Equal(GradeStatus.Corrupted, GradeStore.Read(path).Status);
    }

    [Fact]
    public void Write_Rejects_File_Without_Trailing_Iend()
    {
        byte[] broken = TestPng.Create();
        broken[^1] ^= 0xFF;
        string path = WriteFile(broken);

        Assert.Throws<GradeStoreException>(() => GradeStore.Write(path, Sample()));
    }

    [Fact]
    public void Write_Rejects_Non_Png()
    {
        string path = WriteFile(new byte[4096]);
        Assert.Throws<GradeStoreException>(() => GradeStore.Write(path, Sample()));
    }

    [Fact]
    public void Foreign_Ittx_Block_Is_Never_Overwritten()
    {
        string path = WriteFile(TestPng.Create());
        GradeStore.Write(path, Sample());

        // 篡改 keyword，模拟「位形相同但不属于本软件」的块
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(-(PngTailCodec.TailSize - 8), SeekOrigin.End);
            stream.WriteByte((byte)'X');
        }

        Assert.Throws<GradeStoreException>(() => GradeStore.Write(path, Sample()));
    }

    [Fact]
    public void Corrupted_Block_Falls_Back_To_NoScore_On_Read()
    {
        string path = WriteFile(TestPng.Create());
        GradeStore.Write(path, Sample());

        // 破坏整个载荷文本区，使 JSON 无法解析
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(-(PngTailCodec.TailSize - 8 - PngTailCodec.TextOffset), SeekOrigin.End);
            for (int i = 0; i < 32; i++) stream.WriteByte(0x00);
        }

        GradeReadResult result = GradeStore.Read(path);
        Assert.False(result.HasGrade);
        Assert.NotEqual(GradeStatus.Ok, result.Status);
    }

    // ---------- 边界 ----------

    [Fact]
    public void Special_Characters_In_Path_Are_Handled()
    {
        string path = WriteFile(TestPng.Create(), "图片 测试 (1).png");
        var record = new GradeRecord
        {
            Rating = 5,
            OriginPath = @"E:\Vibe Coding\PhotoGrader\中文 目录\a b.png",
        };

        GradeStore.Write(path, record);
        GradeReadResult read = GradeStore.Read(path);

        Assert.True(read.HasGrade);
        Assert.Equal(@"E:\Vibe Coding\PhotoGrader\中文 目录\a b.png", read.Record!.OriginPath);
    }

    [Fact]
    public void BaseLength_Reflects_Original_File_Size()
    {
        byte[] original = TestPng.Create(bodyBytes: 2048);
        string path = WriteFile(original);

        Assert.Equal(original.Length, GradeStore.Read(path).BaseLength);

        GradeStore.Write(path, Sample());

        Assert.Equal(original.Length, GradeStore.Read(path).BaseLength);
    }

    [Fact]
    public void Dimension_Is_Read_From_Ihdr()
    {
        string path = WriteFile(TestPng.CreateWithSize(1920, 1080));

        GradeReadResult result = GradeStore.Read(path);

        Assert.Equal(1920, result.Dimension.Width);
        Assert.Equal(1080, result.Dimension.Height);
        Assert.True(result.Dimension.IsKnown);
    }

    [Theory]
    [InlineData(900, 1200, 0.75)]
    [InlineData(1000, 1000, 1.0)]
    [InlineData(1680, 720, 2.3333)]
    [InlineData(2560, 1440, 1.7777)]
    public void Aspect_Reflects_Real_Ratio(int width, int height, double expected)
    {
        string path = WriteFile(TestPng.CreateWithSize(width, height));

        GradeReadResult result = GradeStore.Read(path);

        Assert.Equal(expected, result.Dimension.Aspect, 3);
    }

    [Fact]
    public void Unknown_Dimension_Falls_Back_To_Sixteen_By_Nine()
    {
        string path = WriteFile(TestPng.Create());

        GradeReadResult result = GradeStore.Read(path);

        Assert.False(result.Dimension.IsKnown);
        Assert.Equal(16.0 / 9.0, result.Dimension.Aspect, 3);
    }

    [Fact]
    public void Dimension_Survives_After_Writing_Grade()
    {
        string path = WriteFile(TestPng.CreateWithSize(1234, 567));
        GradeStore.Write(path, Sample());

        GradeReadResult result = GradeStore.Read(path);

        Assert.Equal(1234, result.Dimension.Width);
        Assert.Equal(567, result.Dimension.Height);
    }

    [Fact]
    public void TryRead_Returns_Null_For_Ungraded_File()
    {
        string path = WriteFile(TestPng.Create());
        Assert.Null(GradeStore.TryRead(path));
    }

    [Fact]
    public void TryRead_Returns_Record_After_Write()
    {
        string path = WriteFile(TestPng.Create());
        GradeStore.Write(path, Sample());

        Assert.Equal(4, GradeStore.TryRead(path)!.Rating);
    }

    [Fact]
    public void Overwrite_Is_Fast_For_Large_Files()
    {
        // 4 MB 体量下，原地覆盖应当与文件大小无关
        string path = WriteFile(TestPng.Create(bodyBytes: 4 * 1024 * 1024));
        GradeStore.Write(path, Sample());

        GradeWriteResult write = GradeStore.Write(path, Sample());

        Assert.False(write.Created);
        Assert.True(
            write.Elapsed.TotalMilliseconds < 300,
            $"原地覆盖耗时 {write.Elapsed.TotalMilliseconds:F1} ms，超出预期");
    }
}
