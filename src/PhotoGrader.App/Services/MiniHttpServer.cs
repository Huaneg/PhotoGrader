using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhotoGrader.App.Services;

/// <summary>
/// 极简回环 HTTP 服务，向前端提供静态文件与缩略图。
///
/// 之所以不用 WebView2 的 WebResourceRequested 拦截：实测表明该事件对
/// SetVirtualHostNameToFolderMapping 映射出来的请求完全不触发（即使过滤器设为 "*"），
/// 因此改为自开一个监听 127.0.0.1 随机端口的服务。这样既可靠，也方便
/// 用普通浏览器直接打开调试。
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _staticRoot;
    // 缩略图按「相对路径 + 尺寸」寻址：index 会随文件列表增删而偏移，
    // 而 URL 又带长缓存，按 index 寻址会让浏览器返回错位的旧图。
    private readonly Func<string, int, byte[]?> _thumbnailProvider;

    /// <summary>诊断输出（如找不到缩略图时的路径）。</summary>
    public Action<string>? OnDiagnostic { get; set; }
    private readonly Func<string, byte[]?> _previewProvider;
    private readonly CancellationTokenSource _cts = new();

    public MiniHttpServer(
        string staticRoot,
        Func<string, int, byte[]?> thumbnailProvider,
        Func<string, byte[]?>? previewProvider = null)
    {
        _staticRoot = staticRoot;
        _thumbnailProvider = thumbnailProvider;
        _previewProvider = previewProvider ?? (_ => null);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;

                using NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 15000;

                string? requestLine = await ReadLineAsync(stream, _cts.Token);
                if (string.IsNullOrWhiteSpace(requestLine)) return;

                await DrainHeadersAsync(stream, _cts.Token);

                string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return;

                await RouteAsync(stream, parts[1]);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
                                       or OperationCanceledException)
        {
            // 客户端提前断开等，忽略
        }
    }

    private async Task RouteAsync(NetworkStream stream, string target)
    {
        int questionMark = target.IndexOf('?');
        string path = questionMark >= 0 ? target[..questionMark] : target;
        string query = questionMark >= 0 ? target[(questionMark + 1)..] : string.Empty;

        path = Uri.UnescapeDataString(path);

        if (path.StartsWith("/thumb/", StringComparison.Ordinal))
        {
            await ServeThumbnailAsync(stream, path, query);
            return;
        }

        if (path.StartsWith("/full/", StringComparison.Ordinal))
        {
            await ServeFullImageAsync(stream, path);
            return;
        }

        await ServeStaticAsync(stream, path);
    }

    private async Task ServeFullImageAsync(NetworkStream stream, string path)
    {
        // /full/<URL编码的相对路径>?s=<文件大小>
        // 改为按相对路径寻址：index 会随文件列表增删而偏移，
        // 显示中的大图可能因此指向另一张（与缩略图曾经的错位问题同源）。
        string tail = path["/full/".Length..];
        if (string.IsNullOrEmpty(tail))
        {
            await WriteTextAsync(stream, 400, "Bad Request", "无效的图片路径");
            return;
        }

        byte[]? bytes = _previewProvider(tail);
        if (bytes is null)
        {
            OnDiagnostic?.Invoke($"[full] 未找到 relative=[{tail}]");
            await WriteTextAsync(stream, 404, "Not Found", "图片不可用");
            return;
        }

        await WriteResponseAsync(
            stream, 200, "OK", "image/png", bytes, "Cache-Control: no-cache\r\n");
    }

    private async Task ServeThumbnailAsync(NetworkStream stream, string path, string query)
    {
        // /thumb/<URL编码的相对路径>?w=<宽度>
        string relative = path["/thumb/".Length..];
        if (string.IsNullOrEmpty(relative))
        {
            await WriteTextAsync(stream, 400, "Bad Request", "缺少缩略图路径");
            return;
        }

        int width = 220;
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            if (pair.AsSpan(0, equals).SequenceEqual("w") && int.TryParse(pair.AsSpan(equals + 1), out int w))
            {
                width = Math.Clamp(w, 48, 1024);
            }
        }

        byte[]? bytes = _thumbnailProvider(relative, width);
        if (bytes is null)
        {
            OnDiagnostic?.Invoke($"[thumb] 未找到 relative=[{relative}]");
            await WriteTextAsync(stream, 404, "Not Found", "缩略图不可用");
            return;
        }

        await WriteResponseAsync(
            stream, 200, "OK", "image/jpeg", bytes, "Cache-Control: max-age=604800\r\n");
    }

    private async Task ServeStaticAsync(NetworkStream stream, string path)
    {
        if (path is "/" or "") path = "/index.html";

        string relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(_staticRoot, relative));

        // 防止路径穿越
        if (!full.StartsWith(_staticRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            await WriteTextAsync(stream, 404, "Not Found", "文件不存在");
            return;
        }

        string contentType = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream",
        };

        // 字体是内容恒定的大文件（MiSansVF.woff2 有十几 MB），
        // 用 no-cache 会导致每次启动都重新拉一遍，首屏明显变慢。
        // 其余资源保持 no-cache，便于开发期改完刷新即生效。
        string cache = contentType.StartsWith("font/", StringComparison.Ordinal)
            ? "Cache-Control: max-age=604800, immutable\r\n"
            : "Cache-Control: no-cache\r\n";

        byte[] body = await File.ReadAllBytesAsync(full);
        await WriteResponseAsync(stream, 200, "OK", contentType, body, cache);
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken token)
    {
        var builder = new StringBuilder();
        var single = new byte[1];

        while (builder.Length < 8192)
        {
            int read = await stream.ReadAsync(single, token);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();

            char c = (char)single[0];
            if (c == '\n') return builder.ToString().TrimEnd('\r');
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static async Task DrainHeadersAsync(NetworkStream stream, CancellationToken token)
    {
        var builder = new StringBuilder();
        var single = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(single, token);
            if (read == 0) return;

            char c = (char)single[0];
            if (c == '\n')
            {
                if (builder.Length == 0) return;
                builder.Clear();
            }
            else if (c != '\r')
            {
                builder.Append(c);
            }
        }
    }

    private static async Task WriteTextAsync(
        NetworkStream stream, int status, string reason, string message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message);
        await WriteResponseAsync(stream, status, reason, "text/plain; charset=utf-8", body);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string reason,
        string contentType,
        byte[] body,
        string extraHeaders = "")
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append(extraHeaders);
        head.Append("Connection: close\r\n\r\n");

        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());

        await stream.WriteAsync(headBytes);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
