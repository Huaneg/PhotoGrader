namespace PhotoGrader.Core;

/// <summary>
/// PNG / zlib 规范的 CRC-32（多项式 0xEDB88320，反射实现，初值与终值均取反）。
/// 自行实现以避免引入 System.IO.Hashing 包，保持 Core 库零外部依赖。
/// </summary>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    /// <summary>计算单段数据的 CRC-32。</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>计算两段数据的 CRC-32，等价于拼接后计算，但无需实际分配内存。</summary>
    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = 0; i < first.Length; i++)
        {
            c = Table[(c ^ first[i]) & 0xFF] ^ (c >> 8);
        }
        for (int i = 0; i < second.Length; i++)
        {
            c = Table[(c ^ second[i]) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }
}
