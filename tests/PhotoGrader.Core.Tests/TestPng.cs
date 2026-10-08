namespace PhotoGrader.Core.Tests;

/// <summary>
/// 构造测试用的合成 PNG。
///
/// 本项目的读取逻辑只依赖「文件头 8 字节签名」与「文件尾的 IEND 块」，
/// 因此测试文件只需满足这两点，无需真实的压缩像素数据即可覆盖全部读写分支。
/// 真实 PNG 的兼容性由集成测试单独验证。
/// </summary>
internal static class TestPng
{
    public static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static readonly byte[] Iend =
    [
        0x00, 0x00, 0x00, 0x00,
        (byte)'I', (byte)'E', (byte)'N', (byte)'D',
        0xAE, 0x42, 0x60, 0x82,
    ];

    /// <summary>生成「签名 + 伪数据体 + IEND」的合成 PNG。</summary>
    public static byte[] Create(int bodyBytes = 8192)
    {
        var buffer = new byte[Signature.Length + bodyBytes + Iend.Length];
        Signature.CopyTo(buffer, 0);

        for (int i = 0; i < bodyBytes; i++)
        {
            buffer[Signature.Length + i] = (byte)((i * 31 + 7) % 251);
        }

        Iend.CopyTo(buffer, Signature.Length + bodyBytes);
        return buffer;
    }

    /// <summary>图片数据区长度（文件名总长扣除末尾 IEND）。</summary>
    public static int ImageBytesOf(int totalLength) => totalLength - Iend.Length;

    /// <summary>生成带真实 IHDR 的合成 PNG，供尺寸解析测试使用。</summary>
    public static byte[] CreateWithSize(int width, int height, int bodyBytes = 4096)
    {
        var buffer = new List<byte>();

        buffer.AddRange(Signature);
        buffer.AddRange([0, 0, 0, 13]);
        buffer.AddRange("IHDR"u8.ToArray());
        buffer.AddRange(BitConverter.GetBytes(width).Reverse());
        buffer.AddRange(BitConverter.GetBytes(height).Reverse());
        buffer.AddRange([8, 6, 0, 0, 0]);
        buffer.AddRange([0, 0, 0, 0]);

        for (int i = 0; i < bodyBytes; i++) buffer.Add((byte)(i % 251));
        buffer.AddRange(Iend);

        return [.. buffer];
    }
}
