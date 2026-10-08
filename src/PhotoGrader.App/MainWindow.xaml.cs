using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using PhotoGrader.App.Services;
using PhotoGrader.Core;

namespace PhotoGrader.App;

public partial class MainWindow : Window
{
    private const string DefaultLibraryRoot = @"D:\GPT Image";

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "photograder.log");

    private readonly string _wwwRoot;
    private string _libraryRoot;
    private ThumbnailService _thumbnails;
    private MiniHttpServer? _server;
    private PhotoLibrary? _library;

    public MainWindow()
    {
        InitializeComponent();

        _libraryRoot = ReadArgument("--root") ?? DefaultLibraryRoot;
        _wwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _thumbnails = CreateThumbnailsFor(_libraryRoot);
        _thumbnails.OnDiagnostic = Log;

        Loaded += OnLoaded;
    }

    /// <summary>读取形如 --name value 的命令行参数。args 为 null 表示未提供。</summary>
    private static string? ReadArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ThumbnailService CreateThumbnailsFor(string libraryRoot) =>
        new(Path.Combine(libraryRoot, PhotoLibrary.CacheFolderName, "thumbs"));

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Log("[init] OnLoaded 开始");

            // WebView2 的运行时数据放 LocalAppData，避免污染 exe 所在目录
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoGrader",
                "WebView2");

            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateAsync(null, userDataFolder);

            await Web.EnsureCoreWebView2Async(environment);
            Log("[init] WebView2 控件就绪");

            Web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // 前端与缩略图统一由本地回环服务提供，页面与资源同源，避免混合内容限制
            _server = new MiniHttpServer(_wwwRoot, ProvideThumbnail, ProvidePreview);
            _server.OnDiagnostic = Log;
            _server.Start();
            Log($"[init] 本地服务已启动 {_server.BaseUrl}");

            Web.CoreWebView2.Navigate(_server.BaseUrl);
            Log("[init] 已发起导航");
        }
        catch (Exception ex)
        {
            Log($"[init] 初始化失败：{ex}");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_library?.IsIndexDirty == true)
        {
            _library.SaveIndex();
        }

        _server?.Dispose();
        base.OnClosing(e);
    }

    /// <summary>
    /// 自检用：带 --autograde N 启动时，给前 N 张写入示例评分，用于端到端验证写入链路。
    /// </summary>
    private async void TryAutoGrade()
    {
        if (!int.TryParse(ReadArgument("--autograde"), out int count) || count <= 0) return;

        await Task.Delay(2500);
        PhotoLibrary? library = _library;
        if (library is null) return;

        int done = 0;
        for (int i = 0; i < Math.Min(count, library.Entries.Count); i++)
        {
            PhotoEntry entry = library.Entries[i];
            var record = new GradeRecord
            {
                Rating = i % 6,
                Flag = (GradeFlag)((i % 3) switch { 0 => 1, 1 => -1, _ => 0 }),
                Label = (GradeLabel)(i % 6),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            try
            {
                library.SetGrade(entry, record);
                done++;
            }
            catch (Exception ex)
                when (ex is GradeStoreException or IOException or UnauthorizedAccessException)
            {
                Log($"[autograde] {entry.FileName} 失败：{ex.Message}");
            }
        }

        library.SaveIndex();
        Log($"[autograde] 已写入 {done} 张");
        SendLibrary(library);
    }

    /// <summary>
    /// 调试用：带 --capture &lt;路径&gt; 启动时，等界面稳定后把渲染结果保存为 PNG。
    /// 走 WebView2 自身的截图接口，不受窗口遮挡影响。
    /// </summary>
    private async void TryCaptureOnStart()
    {
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--capture");
        if (at < 0 || at + 1 >= args.Length) return;

        string target = args[at + 1];

        try
        {
            await Task.Delay(4000);
            Log("[capture] 延迟结束，准备截图");

            // 调试：截图前先打开放大查看，用于验证该视图
            if (int.TryParse(ReadArgument("--lightbox"), out int lightboxIndex))
            {
                await Web.CoreWebView2.ExecuteScriptAsync(
                    $"window.__openLightbox && window.__openLightbox({lightboxIndex})");
                await Task.Delay(1500);
            }

            // 调试：可多次传入 --eval，按顺序执行（每次间隔 900ms），
            // 便于分步验证「点击 → 等待异步结果 → 再断言」这类交互
            string[] allArgs = Environment.GetCommandLineArgs();
            var scripts = new List<string>();
            for (int i = 0; i < allArgs.Length - 1; i++)
            {
                if (allArgs[i] == "--eval") scripts.Add(allArgs[i + 1]);
            }

            foreach (string one in scripts)
            {
                string evaluated = await Web.CoreWebView2.ExecuteScriptAsync(one);
                Log($"[eval] {evaluated}");
                await Task.Delay(2600);
            }

            using var raw = new MemoryStream();
            await Web.CoreWebView2.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png, raw);
            raw.Position = 0;

            BitmapSource frame = BitmapFrame.Create(
                raw, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            // 调试：--region x,y,w,h,scale 用于把局部放大后导出，便于核对细节
            string? regionArg = ReadArgument("--region");
            if (!string.IsNullOrEmpty(regionArg))
            {
                string[] parts = regionArg.Split(',');
                if (parts.Length == 5
                    && int.TryParse(parts[0], out int rx)
                    && int.TryParse(parts[1], out int ry)
                    && int.TryParse(parts[2], out int rw)
                    && int.TryParse(parts[3], out int rh)
                    && double.TryParse(parts[4], out double rs))
                {
                    frame = CropAndScale(frame, rx, ry, rw, rh, rs);
                    Log($"[capture] 已裁剪 {rx},{ry} {rw}x{rh} ×{rs}");
                }
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));

            using var output = new FileStream(target, FileMode.Create, FileAccess.Write);
            encoder.Save(output);

            Log($"[capture] 已保存 {target}");
        }
        catch (Exception ex)
        {
            Log($"[capture] 失败：{ex}");
        }
    }

    /// <summary>裁剪并放大位图，用于导出界面局部做细节核对。</summary>
    private static BitmapSource CropAndScale(
        BitmapSource source, int x, int y, int width, int height, double scale)
    {
        int left = Math.Clamp(x, 0, Math.Max(0, source.PixelWidth - 1));
        int top = Math.Clamp(y, 0, Math.Max(0, source.PixelHeight - 1));
        int w = Math.Clamp(width, 1, source.PixelWidth - left);
        int h = Math.Clamp(height, 1, source.PixelHeight - top);

        var cropped = new CroppedBitmap(source, new Int32Rect(left, top, w, h));
        return new TransformedBitmap(cropped, new ScaleTransform(scale, scale));
    }

    // ---------- MD5 后台计算 ----------

    private CancellationTokenSource? _md5Cts;

    /// <summary>
    /// 在后台逐步补齐所有文件的 MD5。7485 张 4~5MB 的图约 35GB，
    /// 首次需要数十秒，因此限并发并放到低优先级，避免影响界面响应。
    /// </summary>
    private void StartMd5BackgroundScan(PhotoLibrary library)
    {
        _md5Cts?.Cancel();
        var cts = new CancellationTokenSource();
        _md5Cts = cts;

        IReadOnlyList<PhotoEntry> queue = library.BuildMd5Queue();
        Log($"[md5] 待计算 {queue.Count} 个 / 共 {library.Entries.Count} 个");

        if (queue.Count == 0)
        {
            // 全部命中缓存，直接汇报结果（这条分支同样要发重复摘要）
            SendMd5Progress(library.Md5KnownCount, library.Entries.Count);
            SendDuplicateSummary();
            return;
        }

        int total = library.Entries.Count;
        int parallelism = Math.Max(1, Environment.ProcessorCount / 2);

        _ = Task.Run(
            () =>
            {
                int done = 0;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                Parallel.ForEach(
                    queue,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = cts.Token,
                    },
                    entry =>
                    {
                        if (cts.IsCancellationRequested) return;

                        library.SetMd5(entry, FileHasher.TryComputeMd5(entry.FullPath));

                        int current = Interlocked.Increment(ref done);
                        if (current % 300 == 0)
                        {
                            Dispatch(() => SendMd5Progress(library.Md5KnownCount, total));
                        }
                    });

                stopwatch.Stop();
                Log($"[md5] 后台计算完成 {done} 个，用时 {stopwatch.Elapsed.TotalSeconds:F1} s");

                Dispatch(() =>
                {
                    library.SaveIndex();
                    SendMd5Progress(library.Md5KnownCount, total);
                    SendDuplicateSummary();
                });
            },
            cts.Token);
    }

    private void HandleNeedMd5(JsonElement payload)
    {
        PhotoLibrary? library = _library;
        if (library is null) return;
        if (!payload.TryGetProperty("index", out JsonElement indexElement)) return;

        int index = indexElement.GetInt32();
        PhotoEntry? entry = EntryAt(index);
        if (entry is null) return;

        if (entry.HasMd5)
        {
            SendMd5(index, entry);
            return;
        }

        // 用户正在看这张图，优先把它算出来，不等后台队列
        _ = Task.Run(() =>
        {
            library.SetMd5(entry, FileHasher.TryComputeMd5(entry.FullPath));

            Dispatch(() =>
            {
                SendMd5(index, entry);
                library.SaveIndex();
            });
        });
    }

    private void SendMd5(int index, PhotoEntry entry)
    {
        PhotoLibrary? library = _library;
        IReadOnlyList<PhotoEntry> duplicates = library?.FindDuplicates(entry) ?? [];

        PostToWeb(new
        {
            type = "md5",
            index,
            md5 = entry.Md5,
            shortHash = entry.Md5Short,
            ok = entry.HasMd5,
            duplicates = duplicates
                .Select(dup => new
                {
                    i = library!.IndexOf(dup),
                    n = dup.FileName,
                    d = dup.FolderName,
                    p = dup.RelativePath,
                })
                .ToArray(),
        });
    }

    private void SendMd5Progress(int done, int total) =>
        PostToWeb(new { type = "md5Progress", done, total });

    /// <summary>
    /// 把「哪些文件存在重复」的整体情况推给界面，用于在图库墙上打角标。
    /// 只含有重复的条目，数量很小。
    /// </summary>
    private void SendDuplicateSummary()
    {
        PhotoLibrary? library = _library;
        if (library is null) return;

        var groupSize = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (PhotoEntry entry in library.Entries)
        {
            if (!entry.HasMd5 || entry.IsGone) continue;
            groupSize[entry.Md5!] = groupSize.GetValueOrDefault(entry.Md5!) + 1;
        }

        var flagged = new List<object>();
        for (int index = 0; index < library.Entries.Count; index++)
        {
            PhotoEntry entry = library.Entries[index];
            if (!entry.HasMd5 || entry.IsGone) continue;
            if (groupSize.GetValueOrDefault(entry.Md5!) <= 1) continue;

            flagged.Add(new { i = index, c = groupSize[entry.Md5!] });
        }

        PostToWeb(new { type = "duplicates", items = flagged.ToArray() });
    }

    /// <summary>
    /// 把重复文件剪切到 _duplicates_MD5。
    /// scope 为 "self" 时移走当前这张，为 "duplicates" 时移走其余的重复文件
    /// —— 让用户自行决定保留哪一份。归档后立即刷新索引与界面。
    /// </summary>
    private async void HandleArchive(JsonElement payload)
    {
        PhotoLibrary? library = _library;
        if (library is null) return;
        if (!payload.TryGetProperty("index", out JsonElement indexElement)) return;
        if (!payload.TryGetProperty("scope", out JsonElement scopeElement)) return;

        int index = indexElement.GetInt32();
        PhotoEntry? source = EntryAt(index);
        if (source is null) return;

        bool archiveSelf = string.Equals(
            scopeElement.GetString(), "self", StringComparison.OrdinalIgnoreCase);

        List<PhotoEntry> targets = archiveSelf
            ? [source]
            : [.. library.FindDuplicates(source)];

        if (targets.Count == 0) return;

        SendStatus(archiveSelf
            ? "正在移出当前这张 …"
            : $"正在移出 {targets.Count} 个重复文件 …");

        IReadOnlyList<MoveOutcome> outcomes = await Task.Run(
            () => library.ArchiveDuplicates(targets));

        int moved = outcomes.Count(o => o.Moved);
        int failed = outcomes.Count - moved;

        foreach (MoveOutcome outcome in outcomes.Where(o => !o.Moved))
        {
            Log($"[archive] {Path.GetFileName(outcome.Source)}：{outcome.Error}");
        }

        library.SaveIndex();

        SendStatus(
            $"已移出 {moved} 张到 {PhotoLibrary.DuplicatesFolderName}"
            + (failed > 0 ? $"，{failed} 张失败" : string.Empty));

        // 先在归档后的新索引里解析「当前这张」的位置：如果它自己也被移走了，
        // 就取原位置上的后继（视觉上的下一张），供界面顺势接续浏览。
        PhotoEntry? next = null;
        bool sourceGone = source.IsGone;

        if (!archiveSelf)
        {
            // 移走的是重复件，当前这张还在原地，位置不变
            next = source;
        }
        else
        {
            // 移走的是当前这张：原位置落到的那个条目就是后继（列表顺序不变）
            IReadOnlyList<PhotoEntry> entries = library.Entries;
            if (index < entries.Count)
            {
                PhotoEntry candidate = entries[index];
                if (!candidate.IsGone) next = candidate;
            }
        }

        // 回传被移走条目在图库列表中的位置，供界面移除对应卡片
        var archivedIndices = new List<int>();
        foreach (PhotoEntry target in targets.Where(t => t.Archived))
        {
            int position = library.IndexOf(target);
            if (position >= 0) archivedIndices.Add(position);
        }

        // 关键：必须先把「跳转到哪一张」告诉界面，再补发该张的 MD5 详情。
        // 否则跳转之前的 MD5 响应会因为 lightboxIndex 不匹配被丢掉，
        // 落到新图上时既没有发起请求、也没有收到结果 → 提示条一直是空的。
        PostToWeb(new
        {
            type = "archived",
            self = archiveSelf,
            sourceIndex = index,
            nextIndex = next is null ? -1 : library.IndexOf(next),
            sourceGone,
            indices = archivedIndices.ToArray(),
        });

        SendDuplicateSummary();

        // 归档后重复组已变化，重发详情。注意这里必须按「跳转后的那一张」发，
        // 传 source 会导致界面拿到的是刚被移走那张的信息。
        if (next is not null)
        {
            SendMd5(library.IndexOf(next), next);
        }
    }

    private void Dispatch(Action action)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.InvokeAsync(action);
    }

    /// <summary>
    /// 删除文件 —— 走系统回收站，可从回收站还原，不是永久删除。
    /// </summary>
    private async void HandleDelete(JsonElement payload)
    {
        try
        {
            PhotoLibrary? library = _library;
            if (library is null) return;
            if (!payload.TryGetProperty("index", out JsonElement indexElement)) return;

            int index = indexElement.GetInt32();
            PhotoEntry? entry = EntryAt(index);
            if (entry is null) return;

            SendStatus($"正在删除 {entry.FileName} …");

            // 注意：不能放进 Task.Run —— SHFileOperation 在 MTA 线程池线程上
            // 会挂死或返回不可靠的结果，直接在 UI 线程同步执行即可。
            var targets = new List<PhotoEntry> { entry };
            IReadOnlyList<(PhotoEntry Entry, bool Ok, string? Error)> results =
                library.DeleteToRecycleBin(targets);

            var (target, ok, error) = results[0];

            if (!ok)
            {
                Log($"[delete] {target.FileName}：{error}");
                SendStatus($"删除失败：{error}");
                return;
            }

            try
            {
                library.SaveIndex();
            }
            catch (Exception ex)
            {
                // 索引写失败不应吞掉删除成功的反馈
                Log($"[delete] 索引保存失败：{ex.Message}");
            }

            SendStatus($"已移入回收站：{target.FileName}");

            PostToWeb(new
            {
                type = "deleted",
                index,
                closeLightbox = true,
            });

            SendDuplicateSummary();
        }
        catch (Exception ex)
        {
            Log($"[delete] 异常：{ex}");
            SendStatus($"删除失败：{ex.Message}");
        }
    }

    /// <summary>把图片的完整路径复制到剪贴板。</summary>
    private void HandleCopyPath(JsonElement payload)
    {
        if (!payload.TryGetProperty("index", out JsonElement indexElement)) return;

        PhotoEntry? entry = EntryAt(indexElement.GetInt32());
        if (entry is null) return;

        try
        {
            Clipboard.SetText(entry.FullPath);

            // 回读确认：剪贴板可能被其它进程抢占，光看 SetText 不报错不算数
            string readBack = Clipboard.GetText();
            bool ok = string.Equals(readBack, entry.FullPath, StringComparison.Ordinal);

            Log($"[copyPath] 写入长度={entry.FullPath.Length} 回读一致={ok}");
            SendStatus(ok
                ? $"已复制路径：{entry.FileName}"
                : "复制路径失败，剪贴板被占用");
        }
        catch (Exception ex)
        {
            Log($"[copyPath] 失败：{ex.Message}");
            SendStatus("复制路径失败，剪贴板被占用");
        }
    }

    /// <summary>在资源管理器中打开所在文件夹并选中该文件。</summary>
    private void HandleRevealInExplorer(JsonElement payload)
    {
        if (!payload.TryGetProperty("index", out JsonElement indexElement)) return;

        PhotoEntry? entry = EntryAt(indexElement.GetInt32());
        if (entry is null) return;

        if (!File.Exists(entry.FullPath))
        {
            SendStatus($"文件已不存在：{entry.FileName}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                // /select 需要完整路径；参数整体加引号以容纳空格
                Arguments = $"/select,\"{entry.FullPath}\"",
                UseShellExecute = true,
            });

            SendStatus($"已在资源管理器中定位：{entry.FileName}");
            Log($"[reveal] 已启动 explorer /select 路径={entry.FullPath}");
        }
        catch (Exception ex)
        {
            Log($"[reveal] 失败：{ex.Message}");
            SendStatus($"打开文件夹失败：{ex.Message}");
        }
    }

    /// <summary>缩略图提供者：由本地服务在后台线程调用，按相对路径寻址。</summary>
    private byte[]? ProvideThumbnail(string relativePath, int width)
    {
        PhotoLibrary? library = _library;
        PhotoEntry? entry = library?.Find(relativePath);
        return entry is null ? null : _thumbnails.Get(entry.FullPath, entry.RelativePath, width);
    }

    /// <summary>原图提供者：供放大查看使用。按相对路径寻址，直接读文件不做转码。</summary>
    private byte[]? ProvidePreview(string relativePath)
    {
        PhotoLibrary? library = _library;
        PhotoEntry? entry = library?.Find(relativePath);
        if (entry is null) return null;

        try
        {
            return File.ReadAllBytes(entry.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"[preview] 读取失败 {entry.FileName}：{ex.Message}");
            return null;
        }
    }

    private PhotoEntry? EntryAt(int index)
    {
        PhotoLibrary? library = _library;
        if (library is null) return null;

        return index >= 0 && index < library.Entries.Count ? library.Entries[index] : null;
    }

    // ---------- 与前端通信 ----------

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("cmd", out JsonElement cmdElement)) return;

            switch (cmdElement.GetString())
            {
                case "ready":
                case "refresh":
                    _ = LoadLibraryAsync(_libraryRoot);
                    break;

                case "setGrade":
                    HandleSetGrade(root);
                    break;

                case "moveFiles":
                    HandleMoveFiles(root);
                    break;

                case "needMd5":
                    HandleNeedMd5(root);
                    break;

                case "archive":
                    HandleArchive(root);
                    break;

                case "delete":
                    HandleDelete(root);
                    break;

                case "copyPath":
                    HandleCopyPath(root);
                    break;

                case "revealInExplorer":
                    HandleRevealInExplorer(root);
                    break;

                case "clearThumbCache":
                    _thumbnails.ClearDiskCache();
                    SendStatus("缩略图缓存已清空");
                    break;

                case "switchRoot":
                    HandleSwitchRoot();
                    break;
            }
        }
    }

    private async Task LoadLibraryAsync(string root)
    {
        Log($"[lib] 开始加载 {root}");
        SendStatus($"正在加载 {root} …");

        try
        {
            _thumbnails = CreateThumbnailsFor(root);
            _thumbnails.OnDiagnostic = Log;

            PhotoLibrary library = await Task.Run(() => PhotoLibrary.Load(root));
            Log($"[lib] 加载完成，共 {library.Entries.Count} 张");

            _library = library;
            SendLibrary(library);
            Log("[lib] 已下发到界面");

            StartMd5BackgroundScan(library);
            TryAutoGrade();
            TryCaptureOnStart();
        }
        catch (Exception ex)
        {
            Log($"[lib] 加载失败：{ex}");
            SendStatus($"加载失败：{ex.Message}");
        }
    }

    private void HandleSwitchRoot()
    {
        string? picked = PickFolder(_libraryRoot);
        if (picked is null)
        {
            SendStatus("已取消切换图库");
            return;
        }

        if (string.Equals(picked, _libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            SendStatus("已停留在当前图库");
            return;
        }

        Log($"[lib] 切换图库 {_libraryRoot} → {picked}");
        _libraryRoot = picked;

        // 图库切换后旧索引全部失效，前端需要整体重置
        PostToWeb(new { type = "rootChanged", path = _libraryRoot });
        _ = LoadLibraryAsync(_libraryRoot);
    }

    /// <summary>弹出文件夹选择框；用户取消时返回 null。</summary>
    private string? PickFolder(string initial)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择照片图库根目录",
            Multiselect = false,
        };

        if (Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void HandleSetGrade(JsonElement payload)
    {
        if (_library is null) return;
        if (!payload.TryGetProperty("index", out JsonElement indexElement)) return;

        int index = indexElement.GetInt32();
        PhotoEntry? entry = EntryAt(index);
        if (entry is null) return;

        GradeRecord record = entry.Grade?.Clone() ?? new GradeRecord();

        if (payload.TryGetProperty("rating", out JsonElement ratingElement))
        {
            record.Rating = ratingElement.GetInt32();
        }

        if (payload.TryGetProperty("flag", out JsonElement flagElement))
        {
            record.Flag = (GradeFlag)flagElement.GetInt32();
        }

        if (payload.TryGetProperty("label", out JsonElement labelElement))
        {
            record.Label = (GradeLabel)labelElement.GetInt32();
        }

        record.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            GradeWriteResult result = _library.SetGrade(entry, record);
            _library.SaveIndex();

            SendGradeUpdated(index, entry);
            SendStatus(
                $"{entry.FileName} → {record.Rating}★  {result.Elapsed.TotalMilliseconds:F1} ms"
                + (result.Created ? "（首次写入）" : string.Empty));
        }
        catch (Exception ex) when (ex is GradeStoreException or IOException or UnauthorizedAccessException)
        {
            SendStatus($"写入失败：{ex.Message}");
        }
    }

    /// <summary>把选中的图片移动到目标文件夹（归类汇总）。</summary>
    private async void HandleMoveFiles(JsonElement payload)
    {
        PhotoLibrary? library = _library;
        if (library is null) return;

        if (!payload.TryGetProperty("indices", out JsonElement indicesElement)
            || indicesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        if (!payload.TryGetProperty("folder", out JsonElement folderElement)) return;

        string folder = folderElement.GetString() ?? string.Empty;
        if (folder.Length == 0) return;

        var sources = new List<string>();
        foreach (JsonElement item in indicesElement.EnumerateArray())
        {
            PhotoEntry? entry = EntryAt(item.GetInt32());
            if (entry is not null) sources.Add(entry.FullPath);
        }

        if (sources.Count == 0) return;

        string targetDirectory = Path.Combine(library.RootPath, folder);
        SendStatus($"正在移动 {sources.Count} 张 → {folder} …");

        (int moved, int renamed, List<string> failed) = await Task.Run(() =>
        {
            int movedCount = 0;
            int renamedCount = 0;
            var failures = new List<string>();

            foreach (string source in sources)
            {
                MoveOutcome outcome = FileMover.Move(source, targetDirectory);
                if (outcome.Moved)
                {
                    movedCount++;
                    if (outcome.Renamed) renamedCount++;
                }
                else
                {
                    failures.Add($"{Path.GetFileName(source)}：{outcome.Error}");
                }
            }

            return (movedCount, renamedCount, failures);
        });

        foreach (string failure in failed.Take(5))
        {
            Log($"[move] {failure}");
        }

        SendStatus(
            $"已移动 {moved} 张到 {folder}"
            + (renamed > 0 ? $"（{renamed} 张因重名自动改名）" : string.Empty)
            + (failed.Count > 0 ? $"，{failed.Count} 张失败" : string.Empty));

        // 文件路径已变化，重新加载图库
        await LoadLibraryAsync(library.RootPath);
    }

    private void SendLibrary(PhotoLibrary library)
    {
        PostToWeb(new
        {
            type = "library",
            root = library.RootPath,
            report = new
            {
                total = library.Entries.Count,
                graded = library.Entries.Count(e => e.HasGrade),
                fromIndex = library.LastReport?.FromIndex ?? 0,
                readFromDisk = library.LastReport?.ReadFromDisk ?? 0,
                unreadable = library.LastReport?.Unreadable ?? 0,
                elapsedMs = library.LastReport?.Elapsed.TotalMilliseconds ?? 0,
            },
            items = library.Entries.Select((entry, index) => new
            {
                i = index,
                n = entry.FileName,
                d = entry.FolderName,
                p = entry.RelativePath,
                r = entry.Rating,
                f = (int)entry.Flag,
                l = (int)entry.Label,
                s = entry.Size,
                // 时间下发 Unix 毫秒：ticks 是 6.4e17，超出 JS 安全整数范围（9e15），
                // 直接用数字会丢精度导致排序错乱
                t = (entry.ModifiedUtcTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond,
                w = entry.Width,
                h = entry.Height,
                m = entry.Md5,
            }).ToArray(),
        });
    }

    private void SendGradeUpdated(int index, PhotoEntry entry)
    {
        PostToWeb(new
        {
            type = "gradeUpdated",
            index,
            rating = entry.Rating,
            flag = (int)entry.Flag,
            label = (int)entry.Label,
        });
    }

    private void SendStatus(string text) => PostToWeb(new { type = "status", text });

    private void PostToWeb(object payload)
    {
        CoreWebView2? core = Web.CoreWebView2;
        if (core is null) return;

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 窗口正在关闭
        }
    }
}
