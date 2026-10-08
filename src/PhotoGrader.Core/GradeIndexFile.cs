using System.Text;

namespace PhotoGrader.Core;

/// <summary>索引中的一条记录：变更指纹、像素尺寸、MD5 与上次解析出的评分。</summary>
internal sealed record GradeIndexEntry(
    long ModifiedUtcTicks,
    long Size,
    int Width,
    int Height,
    string? Md5,
    GradeRecord? Grade)
{
    public bool HasGrade => Grade is not null;
}

/// <summary>
/// 索引文件的读写。
///
/// 索引只是缓存，**不是真相来源**。它记录每个文件的 mtime、大小与上次解析出的评分，
/// 让二次加载可以跳过绝大多数文件读取。删掉索引软件照常工作，只是首次加载慢一些。
/// 文件被外部改动后 mtime 会变化，对应条目自动失效并重新读取。
/// </summary>
internal static class GradeIndexFile
{
    private static readonly byte[] Magic = "PGIDX\x00\x01\x00\x00"u8.ToArray();

    private const int Version = 3;

    public static Dictionary<string, GradeIndexEntry> Load(string path)
    {
        var map = new Dictionary<string, GradeIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Magic)) return map;
            if (reader.ReadInt32() != Version) return map;

            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string relativePath = reader.ReadString();
                long ticks = reader.ReadInt64();
                long size = reader.ReadInt64();
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();

                bool hasMd5 = reader.ReadBoolean();
                string? md5 = null;
                if (hasMd5)
                {
                    byte[] hash = reader.ReadBytes(16);
                    if (hash.Length == 16)
                    {
                        md5 = Convert.ToHexString(hash).ToLowerInvariant();
                    }
                }

                bool hasGrade = reader.ReadBoolean();

                GradeRecord? grade = null;
                if (hasGrade)
                {
                    grade = new GradeRecord
                    {
                        Rating = reader.ReadByte(),
                        Flag = (GradeFlag)reader.ReadSByte(),
                        Label = (GradeLabel)reader.ReadByte(),
                    };
                }

                map[relativePath] = new GradeIndexEntry(ticks, size, width, height, md5, grade);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException
                                       or ArgumentException or OutOfMemoryException)
        {
            // 索引损坏不应影响主流程，直接丢弃后全量重扫
            map.Clear();
        }

        return map;
    }

    public static void Save(string path, IEnumerable<PhotoEntry> entries)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);

            string temp = path + ".tmp";
            List<PhotoEntry> list = entries as List<PhotoEntry> ?? [.. entries];

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(list.Count);

                foreach (PhotoEntry entry in list)
                {
                    writer.Write(entry.RelativePath);
                    writer.Write(entry.ModifiedUtcTicks);
                    writer.Write(entry.Size);
                    writer.Write(entry.Width);
                    writer.Write(entry.Height);

                    byte[]? hash = FileHasher.TryParseHex(entry.Md5);
                    writer.Write(hash is not null);
                    if (hash is not null) writer.Write(hash);

                    writer.Write(entry.HasGrade);

                    if (entry.Grade is { } grade)
                    {
                        writer.Write((byte)Math.Clamp(grade.Rating, 0, 5));
                        writer.Write((sbyte)grade.Flag);
                        writer.Write((byte)grade.Label);
                    }
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 索引写不进去（只读目录、磁盘满等）不影响正常使用
        }
    }
}
