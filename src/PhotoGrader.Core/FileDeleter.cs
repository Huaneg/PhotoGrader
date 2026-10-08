using System.Runtime.InteropServices;

namespace PhotoGrader.Core;

/// <summary>
/// 文件删除。
///
/// 一律走系统回收站而不是永久删除 —— 图库里的误操作代价太高，
/// 进回收站至少还能捞回来。
///
/// 实现说明：不使用 Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile，
/// 它在 WPF 里会因等待一个不可见的系统对话框而挂死（实测）。
/// 这里直接 P/Invoke SHFileOperation，并显式关闭所有 UI 反馈。
/// </summary>
public static class FileDeleter
{
    private const uint FoDelete = 0x0003;
    private const uint FofAllowUndo = 0x0040;      // 移入回收站
    private const uint FofNoConfirmation = 0x0010; // 不弹确认
    private const uint FofSilent = 0x0004;         // 不显示进度
    private const uint FofNoErrorUi = 0x0400;      // 不显示错误框

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public string? From;
        public string? To;
        public ushort Flags;
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    /// <summary>把文件移入回收站。返回是否成功以及失败原因。</summary>
    public static (bool Ok, string? Error) MoveToRecycleBin(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return (false, "路径为空");
        if (!File.Exists(filePath)) return (false, $"文件不存在：{filePath}");

        // 诊断：确认传给 API 的路径与文件状态
        System.Diagnostics.Debug.WriteLine($"[delete] 路径=[{filePath}] 存在={File.Exists(filePath)}");

        var op = new ShFileOpStruct
        {
            Hwnd = IntPtr.Zero,
            Func = FoDelete,
            From = filePath + '\0',
            To = null,
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
            ProgressTitle = string.Empty,
        };

        int code = SHFileOperation(ref op);

        // 注意：SHFileOperation 在部分系统上即使成功也返回非零码（实测返回 2），
        // 因此不能以返回码判定成败 —— 以文件是否真的消失为准。
        bool gone = !File.Exists(filePath);

        if (gone && !op.AnyOperationsAborted) return (true, null);

        if (op.AnyOperationsAborted) return (false, "操作被中止");

        return gone
            ? (true, null)
            : (false, $"SHFileOperation 返回 0x{code:X}，文件仍在原地");
    }
}
