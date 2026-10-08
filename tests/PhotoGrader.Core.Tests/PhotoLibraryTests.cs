namespace PhotoGrader.Core.Tests;

public sealed class PhotoLibraryTests : IDisposable
{
    private readonly string _root;

    public PhotoLibraryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PhotoGraderLib", Guid.NewGuid().ToString("N"));
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
            // 清理失败不影响测试结论
        }
    }

    private string AddPhoto(string relativePath, int bodyBytes = 4096)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, TestPng.Create(bodyBytes));
        return full;
    }

    private void AddNonPng(string relativePath)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[64]);
    }

    [Fact]
    public void Scans_Png_Recursively_And_Ignores_Other_Files()
    {
        AddPhoto("a.png");
        AddPhoto(Path.Combine("sub", "b.png"));
        AddPhoto(Path.Combine("sub", "deep", "c.png"));
        AddNonPng("note.txt");

        PhotoLibrary library = PhotoLibrary.Load(_root);

        Assert.Equal(3, library.Entries.Count);
        Assert.Contains(library.Entries, e => e.RelativePath == "a.png");
        Assert.Contains(library.Entries, e => e.RelativePath == Path.Combine("sub", "b.png"));
        Assert.Contains(
            library.Entries,
            e => e.RelativePath == Path.Combine("sub", "deep", "c.png"));
    }

    [Fact]
    public void Folder_Name_Is_Exposed()
    {
        AddPhoto(Path.Combine("01-教室侧坐回头", "x.png"));

        PhotoLibrary library = PhotoLibrary.Load(_root);

        Assert.Equal("01-教室侧坐回头", library.Entries[0].FolderName);
        Assert.Equal("x.png", library.Entries[0].FileName);
    }

    [Fact]
    public void Second_Load_Serves_Everything_From_Index()
    {
        AddPhoto("a.png");
        AddPhoto("b.png");

        PhotoLibrary first = PhotoLibrary.Load(_root);
        Assert.Equal(0, first.LastReport!.FromIndex);
        Assert.Equal(2, first.LastReport.ReadFromDisk);

        PhotoLibrary second = PhotoLibrary.Load(_root);
        Assert.Equal(2, second.LastReport!.FromIndex);
        Assert.Equal(0, second.LastReport.ReadFromDisk);
    }

    [Fact]
    public void Modified_File_Is_Rescanned()
    {
        string path = AddPhoto("a.png");
        PhotoLibrary.Load(_root);

        Thread.Sleep(20);
        File.WriteAllBytes(path, TestPng.Create(bodyBytes: 8192));

        PhotoLibrary second = PhotoLibrary.Load(_root);

        Assert.Equal(0, second.LastReport!.FromIndex);
        Assert.Equal(1, second.LastReport.ReadFromDisk);
    }

    [Fact]
    public void Cache_Directory_Is_Skipped()
    {
        AddPhoto("a.png");
        AddPhoto(Path.Combine(PhotoLibrary.CacheFolderName, "thumb", "ghost.png"));

        PhotoLibrary library = PhotoLibrary.Load(_root);

        Assert.Single(library.Entries);
        Assert.Equal("a.png", library.Entries[0].RelativePath);
    }

    [Fact]
    public void Index_File_Lives_Under_Cache_Directory()
    {
        AddPhoto("a.png");
        PhotoLibrary library = PhotoLibrary.Load(_root);

        Assert.StartsWith(
            Path.Combine(_root, PhotoLibrary.CacheFolderName),
            library.IndexFilePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(library.IndexFilePath));
    }

    [Fact]
    public void Corrupted_Index_Falls_Back_To_Full_Scan()
    {
        AddPhoto("a.png");
        PhotoLibrary first = PhotoLibrary.Load(_root);

        File.WriteAllText(first.IndexFilePath, "这不是合法的索引内容");

        PhotoLibrary second = PhotoLibrary.Load(_root);

        Assert.Equal(0, second.LastReport!.FromIndex);
        Assert.Equal(1, second.LastReport.ReadFromDisk);
        Assert.Single(second.Entries);
    }

    [Fact]
    public void Deleted_Index_Does_Not_Break_Load()
    {
        AddPhoto("a.png");
        PhotoLibrary library = PhotoLibrary.Load(_root);
        File.Delete(library.IndexFilePath);

        PhotoLibrary reloaded = PhotoLibrary.Load(_root);

        Assert.Single(reloaded.Entries);
        Assert.False(reloaded.Entries[0].HasGrade);
    }

    [Fact]
    public void SetGrade_Refreshes_Fingerprint_So_Index_Stays_Valid()
    {
        AddPhoto("a.png");
        PhotoLibrary library = PhotoLibrary.Load(_root);
        PhotoEntry entry = library.Entries[0];

        library.SetGrade(entry, new GradeRecord { Rating = 5, Label = GradeLabel.Red });
        Assert.True(library.IsIndexDirty);

        library.SaveIndex();
        Assert.False(library.IsIndexDirty);

        PhotoLibrary reloaded = PhotoLibrary.Load(_root);
        Assert.Equal(1, reloaded.LastReport!.FromIndex);
        Assert.Equal(0, reloaded.LastReport.ReadFromDisk);
        Assert.Equal(5, reloaded.Entries[0].Rating);
        Assert.Equal(GradeLabel.Red, reloaded.Entries[0].Label);
    }

    [Fact]
    public void Graded_Count_Is_Reported()
    {
        AddPhoto("a.png");
        AddPhoto("b.png");

        PhotoLibrary library = PhotoLibrary.Load(_root);
        library.SetGrade(library.Entries[0], new GradeRecord { Rating = 3 });
        library.SaveIndex();

        PhotoLibrary reloaded = PhotoLibrary.Load(_root);

        Assert.Equal(1, reloaded.LastReport!.Graded);
        Assert.Equal(1, reloaded.Entries.Count(e => e.HasGrade));
    }

    [Fact]
    public void Find_Works_By_Relative_Path()
    {
        AddPhoto(Path.Combine("sub", "x.png"));
        PhotoLibrary library = PhotoLibrary.Load(_root);

        Assert.NotNull(library.Find(Path.Combine("sub", "x.png")));
        Assert.Null(library.Find("nope.png"));
    }

    [Fact]
    public void Missing_Root_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => PhotoLibrary.Load(Path.Combine(_root, "not-there")));
    }

    [Fact]
    public void UseIndex_False_Forces_Full_Read()
    {
        AddPhoto("a.png");
        PhotoLibrary.Load(_root);

        PhotoLibrary forced = PhotoLibrary.Load(_root, useIndex: false);

        Assert.Equal(0, forced.LastReport!.FromIndex);
        Assert.Equal(1, forced.LastReport.ReadFromDisk);
    }
}
