namespace PhotoGrader.Core.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _root;

    public AppSettingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PhotoGraderSettings", Guid.NewGuid().ToString("N"));
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

    private string SettingsFile => Path.Combine(_root, "settings.json");

    [Fact]
    public void Load_Returns_Empty_When_File_Missing()
    {
        AppSettings settings = AppSettingsStore.Load(SettingsFile);

        Assert.Null(settings.LibraryRoot);
    }

    [Fact]
    public void Save_Then_Load_Round_Trips_LibraryRoot()
    {
        bool saved = AppSettingsStore.Save(
            SettingsFile, new AppSettings { LibraryRoot = @"C:\Photos\图库" });

        Assert.True(saved);

        AppSettings loaded = AppSettingsStore.Load(SettingsFile);
        Assert.Equal(@"C:\Photos\图库", loaded.LibraryRoot);
    }

    [Fact]
    public void Save_Creates_Missing_Directory()
    {
        string nested = Path.Combine(_root, "a", "b", "settings.json");

        Assert.True(AppSettingsStore.Save(nested, new AppSettings { LibraryRoot = @"D:\x" }));
        Assert.True(File.Exists(nested));
    }

    /// <summary>配置文件损坏时不能把程序拦在启动阶段，退回空设置即可。</summary>
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public void Load_Returns_Empty_When_Content_Unusable(string content)
    {
        File.WriteAllText(SettingsFile, content);

        AppSettings settings = AppSettingsStore.Load(SettingsFile);

        Assert.Null(settings.LibraryRoot);
    }

    /// <summary>多出来的未知字段不应导致读取失败（为将来加设置项留余地）。</summary>
    [Fact]
    public void Load_Ignores_Unknown_Fields()
    {
        File.WriteAllText(
            SettingsFile,
            """{ "libraryRoot": "E:\\Pics", "somethingFuture": 42 }""");

        AppSettings settings = AppSettingsStore.Load(SettingsFile);

        Assert.Equal(@"E:\Pics", settings.LibraryRoot);
    }

    /// <summary>
    /// 默认位置必须在 AppData 下 —— 单文件分发时 exe 可能放在没有写权限的目录，
    /// 配置文件不能挨着 exe 放，否则保存会静默失败。
    /// </summary>
    [Fact]
    public void DefaultPath_Lives_Under_ApplicationData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(appData, AppSettingsStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("PhotoGrader", "settings.json"),
            AppSettingsStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
    }
}
