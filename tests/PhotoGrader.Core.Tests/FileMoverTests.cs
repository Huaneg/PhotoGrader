namespace PhotoGrader.Core.Tests;

public sealed class FileMoverTests : IDisposable
{
    private readonly string _root;

    public FileMoverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PhotoGraderMove", Guid.NewGuid().ToString("N"));
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

    private string AddFile(string relativePath, int bodyBytes = 4096)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, TestPng.Create(bodyBytes));
        return full;
    }

    [Fact]
    public void Moves_File_Into_Target_Directory()
    {
        string source = AddFile("a.png");
        string target = Path.Combine(_root, "sub");

        MoveOutcome outcome = FileMover.Move(source, target);

        Assert.True(outcome.Moved);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(target, "a.png")));
    }

    [Fact]
    public void Content_Is_Preserved_After_Move()
    {
        byte[] content = TestPng.Create(8192);
        string source = AddFile("a.png");
        File.WriteAllBytes(source, content);
        string target = Path.Combine(_root, "sub");

        FileMover.Move(source, target);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(target, "a.png")));
    }

    [Fact]
    public void Creates_Target_Directory_When_Missing()
    {
        string source = AddFile("a.png");
        string target = Path.Combine(_root, "deep", "nested");

        MoveOutcome outcome = FileMover.Move(source, target);

        Assert.True(outcome.Moved);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Renames_On_Name_Conflict()
    {
        string source = AddFile("a.png");
        string target = Path.Combine(_root, "sub");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "a.png"), TestPng.Create(512));

        MoveOutcome outcome = FileMover.Move(source, target);

        Assert.True(outcome.Moved);
        Assert.True(outcome.Renamed);
        Assert.True(File.Exists(Path.Combine(target, "a (2).png")));
    }

    [Fact]
    public void Existing_File_Is_Never_Overwritten()
    {
        string source = AddFile("a.png", 4096);
        string target = Path.Combine(_root, "sub");
        Directory.CreateDirectory(target);

        byte[] existing = TestPng.Create(512);
        File.WriteAllBytes(Path.Combine(target, "a.png"), existing);

        FileMover.Move(source, target);

        Assert.Equal(existing, File.ReadAllBytes(Path.Combine(target, "a.png")));
    }

    [Fact]
    public void Sequential_Conflicts_Produce_Incremented_Names()
    {
        string target = Path.Combine(_root, "sub");

        for (int i = 0; i < 4; i++)
        {
            string source = AddFile(Path.Combine($"src{i}", "same.png"));
            MoveOutcome outcome = FileMover.Move(source, target);
            Assert.True(outcome.Moved, outcome.Error);
        }

        Assert.True(File.Exists(Path.Combine(target, "same.png")));
        Assert.True(File.Exists(Path.Combine(target, "same (2).png")));
        Assert.True(File.Exists(Path.Combine(target, "same (3).png")));
        Assert.True(File.Exists(Path.Combine(target, "same (4).png")));
    }

    [Fact]
    public void Missing_Source_Reports_Error_Without_Throwing()
    {
        MoveOutcome outcome = FileMover.Move(
            Path.Combine(_root, "missing.png"), Path.Combine(_root, "sub"));

        Assert.False(outcome.Moved);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void ResolveTargetPath_Picks_Free_Name()
    {
        string target = Path.Combine(_root, "sub");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "x.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(target, "x (2).png"), [1, 2, 3]);

        string resolved = FileMover.ResolveTargetPath(Path.Combine(_root, "x.png"), target);

        Assert.Equal(Path.Combine(target, "x (3).png"), resolved);
    }

    [Fact]
    public void Move_Into_Same_Directory_Renames_Instead_Of_Failing()
    {
        string source = AddFile("a.png");
        string sameDirectory = _root;

        MoveOutcome outcome = FileMover.Move(source, sameDirectory);

        Assert.True(outcome.Moved);
        Assert.True(outcome.Renamed);
        Assert.True(File.Exists(Path.Combine(_root, "a (2).png")));
    }

    [Fact]
    public void Non_Ascii_Names_Are_Handled()
    {
        string source = AddFile(Path.Combine("源目录", "图片 一.png"));
        string target = Path.Combine(_root, "目标 目录");

        MoveOutcome outcome = FileMover.Move(source, target);

        Assert.True(outcome.Moved, outcome.Error);
        Assert.True(File.Exists(Path.Combine(target, "图片 一.png")));
    }
}
