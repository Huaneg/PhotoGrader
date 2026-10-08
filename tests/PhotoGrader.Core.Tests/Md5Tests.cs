namespace PhotoGrader.Core.Tests;

public sealed class Md5Tests : IDisposable
{
    private readonly string _root;

    public Md5Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PhotoGraderMd5", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string AddFile(string name, byte[] content)
    {
        string full = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    // ---------- 哈希本身 ----------

    [Fact]
    public void Empty_File_Matches_Known_Md5()
    {
        string path = AddFile("empty.bin", []);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", FileHasher.TryComputeMd5(path));
    }

    [Fact]
    public void Identical_Content_Yields_Identical_Hash()
    {
        byte[] content = TestPng.Create(2048);

        string first = AddFile("a.png", content);
        string second = AddFile(Path.Combine("sub", "b.png"), content);

        Assert.Equal(FileHasher.TryComputeMd5(first), FileHasher.TryComputeMd5(second));
    }

    [Fact]
    public void Different_Content_Yields_Different_Hash()
    {
        string first = AddFile("a.png", TestPng.Create(2048));
        string second = AddFile("b.png", TestPng.Create(2049));

        Assert.NotEqual(FileHasher.TryComputeMd5(first), FileHasher.TryComputeMd5(second));
    }

    [Fact]
    public void Hash_Is_Lowercase_Hex_Of_32_Chars()
    {
        string path = AddFile("a.png", TestPng.Create());
        string? md5 = FileHasher.TryComputeMd5(path);

        Assert.NotNull(md5);
        Assert.Equal(32, md5!.Length);
        Assert.Matches("^[0-9a-f]{32}$", md5);
    }

    [Fact]
    public void Missing_File_Returns_Null()
    {
        Assert.Null(FileHasher.TryComputeMd5(Path.Combine(_root, "nope.png")));
    }

    [Fact]
    public void Hash_Ignores_FileName_And_Path()
    {
        byte[] content = TestPng.Create(4096);

        string a = AddFile("同名.png", content);
        string b = AddFile(Path.Combine("完全不同的目录", "另一个名字.png"), content);

        Assert.Equal(FileHasher.TryComputeMd5(a), FileHasher.TryComputeMd5(b));
    }

    [Fact]
    public void Large_File_Is_Streamed_Correctly()
    {
        // 跨越内部缓冲区（1MB）验证流式读取
        byte[] content = new byte[3 * 1024 * 1024 + 777];
        new Random(7).NextBytes(content);

        string path = AddFile("big.bin", content);
        string? md5 = FileHasher.TryComputeMd5(path);

        Assert.NotNull(md5);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.MD5.HashData(content)).ToLowerInvariant(),
            md5);
    }

    [Fact]
    public void Hex_Parsing_Roundtrips()
    {
        string path = AddFile("a.png", TestPng.Create());
        string md5 = FileHasher.TryComputeMd5(path)!;

        byte[]? bytes = FileHasher.TryParseHex(md5);

        Assert.NotNull(bytes);
        Assert.Equal(16, bytes!.Length);
        Assert.Equal(md5, Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Fact]
    public void Hex_Parsing_Rejects_Invalid_Input()
    {
        Assert.Null(FileHasher.TryParseHex(null));
        Assert.Null(FileHasher.TryParseHex(""));
        Assert.Null(FileHasher.TryParseHex("abc"));
        Assert.Null(FileHasher.TryParseHex("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"));
    }

    // ---------- 图库集成 ----------

    [Fact]
    public void Duplicates_Are_Found_Within_Library()
    {
        byte[] content = TestPng.Create(3000);
        string original = AddFile("a.png", content);
        AddFile(Path.Combine("子目录", "a 副本.png"), content);
        AddFile("other.png", TestPng.Create(3001));

        PhotoLibrary library = PhotoLibrary.Load(_root);

        foreach (PhotoEntry entry in library.Entries)
        {
            library.SetMd5(entry, FileHasher.TryComputeMd5(entry.FullPath));
        }

        PhotoEntry target = library.Find("a.png")!;
        IReadOnlyList<PhotoEntry> duplicates = library.FindDuplicates(target);

        Assert.Single(duplicates);
        Assert.Equal("a 副本.png", duplicates[0].FileName);
        Assert.True(library.IndexOf(duplicates[0]) >= 0);
    }

    [Fact]
    public void Unique_File_Has_No_Duplicates()
    {
        AddFile("a.png", TestPng.Create(3000));
        AddFile("b.png", TestPng.Create(3001));

        PhotoLibrary library = PhotoLibrary.Load(_root);

        foreach (PhotoEntry entry in library.Entries)
        {
            library.SetMd5(entry, FileHasher.TryComputeMd5(entry.FullPath));
        }

        Assert.Empty(library.FindDuplicates(library.Find("a.png")!));
    }

    [Fact]
    public void Duplicate_Groups_Are_Reported()
    {
        byte[] shared = TestPng.Create(2500);
        AddFile("x1.png", shared);
        AddFile("x2.png", shared);
        AddFile("x3.png", shared);
        AddFile("y1.png", TestPng.Create(2600));
        AddFile("y2.png", TestPng.Create(2600));

        PhotoLibrary library = PhotoLibrary.Load(_root);

        foreach (PhotoEntry entry in library.Entries)
        {
            library.SetMd5(entry, FileHasher.TryComputeMd5(entry.FullPath));
        }

        IReadOnlyList<IReadOnlyList<PhotoEntry>> groups = library.FindAllDuplicateGroups();

        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
    }

    [Fact]
    public void Md5_Survives_Index_Roundtrip()
    {
        byte[] content = TestPng.Create(3000);
        AddFile("a.png", content);
        AddFile(Path.Combine("sub", "b.png"), content);

        PhotoLibrary library = PhotoLibrary.Load(_root);
        string expected = FileHasher.TryComputeMd5(library.Find("a.png")!.FullPath)!;

        foreach (PhotoEntry entry in library.Entries)
        {
            library.SetMd5(entry, FileHasher.TryComputeMd5(entry.FullPath));
        }

        library.SaveIndex();

        PhotoLibrary reloaded = PhotoLibrary.Load(_root);
        Assert.Equal(2, reloaded.Md5KnownCount);
        Assert.Equal(expected, reloaded.Find("a.png")!.Md5);
    }

    [Fact]
    public void Changed_File_Invalidates_Cached_Md5()
    {
        string path = AddFile("a.png", TestPng.Create(3000));

        PhotoLibrary library = PhotoLibrary.Load(_root);
        library.SetMd5(library.Find("a.png")!, FileHasher.TryComputeMd5(path));
        library.SaveIndex();

        Thread.Sleep(20);
        File.WriteAllBytes(path, TestPng.Create(4000));

        PhotoLibrary reloaded = PhotoLibrary.Load(_root);

        // mtime 与大小都变了，缓存不再匹配，应重算
        Assert.Equal(0, reloaded.LastReport!.FromIndex);
        Assert.Equal(0, reloaded.Md5KnownCount);
    }

    [Fact]
    public void Md5_Queue_Prioritizes_Colliding_Sizes()
    {
        AddFile("unique.png", TestPng.Create(1111));
        AddFile("dup-a.png", TestPng.Create(2222));
        AddFile("dup-b.png", TestPng.Create(2222));

        PhotoLibrary library = PhotoLibrary.Load(_root);
        IReadOnlyList<PhotoEntry> queue = library.BuildMd5Queue();

        Assert.Equal(3, queue.Count);
        // 大小相同的两张应排在最前（注意文件实际大小 = 签名 8 + 内容 + IEND 12）
        Assert.Equal(queue[0].Size, queue[1].Size);
        Assert.NotEqual(queue[1].Size, queue[2].Size);
        Assert.Contains("unique.png", queue[2].FileName);
    }

    [Fact]
    public void Md5_Queue_Skips_Already_Hashed_Entries()
    {
        AddFile("a.png", TestPng.Create(3000));
        AddFile("b.png", TestPng.Create(3001));

        PhotoLibrary library = PhotoLibrary.Load(_root);
        library.SetMd5(library.Find("a.png")!, "0123456789abcdef0123456789abcdef");

        IReadOnlyList<PhotoEntry> queue = library.BuildMd5Queue();

        Assert.Single(queue);
        Assert.Equal("b.png", queue[0].FileName);
    }

    // ---------- 归档重复文件 ----------

    private static PhotoLibrary LoadWithHashes(string root)
    {
        PhotoLibrary library = PhotoLibrary.Load(root);

        foreach (PhotoEntry entry in library.Entries)
        {
            library.SetMd5(entry, FileHasher.TryComputeMd5(entry.FullPath));
        }

        return library;
    }

    [Fact]
    public void Archive_Moves_Duplicates_And_Marks_Them()
    {
        byte[] content = TestPng.Create(3000);
        AddFile("原图.png", content);
        string copy = AddFile(Path.Combine("子目录", "副本.png"), content);

        PhotoLibrary library = LoadWithHashes(_root);
        PhotoEntry source = library.Find("原图.png")!;
        IReadOnlyList<PhotoEntry> duplicates = library.FindDuplicates(source);

        IReadOnlyList<MoveOutcome> outcomes = library.ArchiveDuplicates(duplicates);

        Assert.Single(outcomes);
        Assert.True(outcomes[0].Moved);
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(Path.Combine(
            _root, PhotoLibrary.DuplicatesFolderName, "副本.png")));
        Assert.True(duplicates[0].Archived);
    }

    [Fact]
    public void Archived_Files_Are_No_Longer_Reported_As_Duplicates()
    {
        byte[] content = TestPng.Create(3000);
        AddFile("原图.png", content);
        AddFile(Path.Combine("子目录", "副本.png"), content);

        PhotoLibrary library = LoadWithHashes(_root);
        PhotoEntry source = library.Find("原图.png")!;

        Assert.Single(library.FindDuplicates(source));

        library.ArchiveDuplicates(library.FindDuplicates(source));

        Assert.Empty(library.FindDuplicates(source));
        Assert.Empty(library.FindAllDuplicateGroups());
    }

    [Fact]
    public void Archive_Folder_Is_Excluded_From_Scanning()
    {
        AddFile("原图.png", TestPng.Create(3000));
        AddFile(
            Path.Combine(PhotoLibrary.DuplicatesFolderName, "旧副本.png"),
            TestPng.Create(3000));

        PhotoLibrary library = PhotoLibrary.Load(_root);

        Assert.Single(library.Entries);
        Assert.Equal("原图.png", library.Entries[0].FileName);
    }

    [Fact]
    public void Archive_Keeps_Original_Untouched()
    {
        byte[] content = TestPng.Create(3000);
        string original = AddFile("原图.png", content);
        AddFile(Path.Combine("子目录", "副本.png"), content);

        PhotoLibrary library = LoadWithHashes(_root);
        PhotoEntry source = library.Find("原图.png")!;

        library.ArchiveDuplicates(library.FindDuplicates(source));

        Assert.True(File.Exists(original));
        Assert.Equal(content, File.ReadAllBytes(original));
        Assert.False(source.Archived);
    }

    [Fact]
    public void Archive_Handles_Name_Collision_In_Target()
    {
        byte[] content = TestPng.Create(3000);
        AddFile("原图.png", content);
        AddFile(Path.Combine("子目录", "副本.png"), content);

        // 归档目录里已经有同名文件
        AddFile(
            Path.Combine(PhotoLibrary.DuplicatesFolderName, "副本.png"),
            TestPng.Create(999));

        PhotoLibrary library = LoadWithHashes(_root);
        PhotoEntry source = library.Find("原图.png")!;

        IReadOnlyList<MoveOutcome> outcomes = library.ArchiveDuplicates(
            library.FindDuplicates(source));

        Assert.True(outcomes[0].Moved);
        Assert.True(outcomes[0].Renamed);
        Assert.True(File.Exists(Path.Combine(
            _root, PhotoLibrary.DuplicatesFolderName, "副本 (2).png")));
    }
}
