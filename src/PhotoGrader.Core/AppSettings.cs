using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoGrader.Core;

/// <summary>
/// 应用级设置。与图片评分不同，这些是「这台机器上这个用户」的偏好，
/// 因此不写进图片，而是单独存一份 JSON。
/// </summary>
public sealed class AppSettings
{
    /// <summary>上次使用的图库根目录。为空表示尚未选择过，由程序决定默认值。</summary>
    public string? LibraryRoot { get; set; }
}

/// <summary>
/// 设置的读写。刻意做成容错：文件不存在、内容损坏、无写入权限都只当作「没有设置」，
/// 绝不让配置问题把程序拦在启动阶段。
/// </summary>
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 落盘用 camelCase（符合 JSON 习惯），读取容忍大小写，
        // 这样手工改配置文件时写成 libraryRoot / LibraryRoot 都能认。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>默认存放位置：%APPDATA%\PhotoGrader\settings.json。</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PhotoGrader",
        "settings.json");

    /// <summary>读取设置。任何失败都返回一个空的设置对象。</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or NotSupportedException
                                      or ArgumentException)
        {
            return new AppSettings();
        }
    }

    /// <summary>保存设置。返回是否写入成功（失败不抛异常）。</summary>
    public static bool Save(string path, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
