using System.Diagnostics;
using System.Security.Cryptography;
using PhotoGrader.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string SourceDir = @"D:\GPT Image";
const int SampleCount = 8;
const int ChunkSize = 524;
const int RepeatCount = 10;

byte[] iend =
[
    0x00, 0x00, 0x00, 0x00,
    (byte)'I', (byte)'E', (byte)'N', (byte)'D',
    0xAE, 0x42, 0x60, 0x82,
];

Console.WriteLine("=== PhotoGrader 真实 PNG 端到端验证 ===");
Console.WriteLine();

if (!Directory.Exists(SourceDir))
{
    Console.WriteLine($"源目录不存在：{SourceDir}");
    return 1;
}

List<string> all = [.. Directory.EnumerateFiles(SourceDir, "*.png", SearchOption.AllDirectories)];
Console.WriteLine($"[扫描] {SourceDir} 下共 {all.Count} 个 PNG");
Console.WriteLine();

string workDir = Path.Combine(AppContext.BaseDirectory, "_verify_tmp");
if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
Directory.CreateDirectory(workDir);

var random = new Random(20260921);
List<string> samples = [.. all.OrderBy(_ => random.Next()).Take(SampleCount)];

int failures = 0;
var rows = new List<string>();

for (int index = 0; index < samples.Count; index++)
{
    string source = samples[index];
    string work = Path.Combine(workDir, $"{index:D2}_{Path.GetFileName(source)}");
    File.Copy(source, work, overwrite: true);

    byte[] original = File.ReadAllBytes(work);
    int imageRegion = original.Length - iend.Length;
    string hashBefore = Sha256(original.AsSpan(0, imageRegion));

    var record = new GradeRecord
    {
        Rating = 5,
        Flag = GradeFlag.Picked,
        Label = GradeLabel.Red,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        OriginPath = source,
    };

    GradeWriteResult write = GradeStore.Write(work, record);

    byte[] after = File.ReadAllBytes(work);
    string hashAfter = Sha256(after.AsSpan(0, imageRegion));

    bool touchOk = hashBefore == hashAfter;
    bool lengthOk = after.Length == original.Length + ChunkSize;
    bool iendOk = after.AsSpan(imageRegion + ChunkSize, iend.Length).SequenceEqual(iend);

    GradeReadResult read = GradeStore.Read(work);
    bool readOk = read.HasGrade
        && read.Record!.Rating == 5
        && read.Record.Flag == GradeFlag.Picked
        && read.Record.Label == GradeLabel.Red;

    long baselineLength = after.Length;
    bool idempotent = true;
    for (int i = 0; i < RepeatCount; i++)
    {
        GradeStore.Write(work, new GradeRecord
        {
            Rating = i % 6,
            Label = (GradeLabel)(i % 6),
        });

        if (new FileInfo(work).Length != baselineLength)
        {
            idempotent = false;
            break;
        }
    }

    // 幂等测试后恢复为 5 星红标，便于外部工具核验
    GradeStore.Write(work, record);

    bool pass = touchOk && lengthOk && iendOk && readOk && idempotent;
    if (!pass) failures++;

    string sizeMb = (original.Length / 1024.0 / 1024.0).ToString("F2");
    rows.Add(
        $"{Path.GetFileName(source),-36} {sizeMb,6} MB  "
        + $"像素={Mark(touchOk)} 长度={Mark(lengthOk)} IEND={Mark(iendOk)} "
        + $"读回={Mark(readOk)} 幂等={Mark(idempotent)}  "
        + $"{write.Elapsed.TotalMilliseconds,6:F2} ms");
}

Console.WriteLine("[单文件验证]");
foreach (string row in rows) Console.WriteLine("  " + row);
Console.WriteLine();

Console.WriteLine("[性能：扫描全部文件的评分]");

Stopwatch sw = Stopwatch.StartNew();
int gradedSerial = 0;
foreach (string file in all)
{
    if (GradeStore.TryRead(file) is not null) gradedSerial++;
}
sw.Stop();
Console.WriteLine($"  串行扫描 {all.Count} 个：{sw.ElapsedMilliseconds,6} ms   已评分 {gradedSerial} 个");

sw.Restart();
int gradedParallel = 0;
Parallel.ForEach(
    all,
    file =>
    {
        if (GradeStore.TryRead(file) is not null) Interlocked.Increment(ref gradedParallel);
    });
sw.Stop();
Console.WriteLine($"  并行扫描 {all.Count} 个：{sw.ElapsedMilliseconds,6} ms   已评分 {gradedParallel} 个");
Console.WriteLine();

Console.WriteLine("[图库加载]");

Stopwatch libraryWatch = Stopwatch.StartNew();
PhotoLibrary first = PhotoLibrary.Load(SourceDir);
libraryWatch.Stop();
Console.WriteLine($"  首次加载 {libraryWatch.ElapsedMilliseconds,6} ms   {first.LastReport}");

libraryWatch.Restart();
PhotoLibrary second = PhotoLibrary.Load(SourceDir);
libraryWatch.Stop();
Console.WriteLine($"  二次加载 {libraryWatch.ElapsedMilliseconds,6} ms   {second.LastReport}");
Console.WriteLine($"  索引文件：{first.IndexFilePath}");
Console.WriteLine();

Console.WriteLine($"[结论] 失败 {failures} 项 / 共 {samples.Count} 个样本");
Console.WriteLine($"[产物] 验证样本保留于 {workDir}");

return failures == 0 ? 0 : 1;

static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

static string Mark(bool ok) => ok ? "OK" : "FAIL";
