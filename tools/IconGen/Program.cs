using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// PhotoGrader 图标生成器
//
// 用几何绘制而不是位图缩放：小尺寸（16px）需要更简化的造型才看得清，
// 因此按尺寸分档控制细节。输出多尺寸 ICO（内嵌 PNG，Vista+ 支持）。
//
// 用法：dotnet run -- <输出路径>

const float Design = 256f;   // 设计基准尺寸，其余尺寸按比例缩放

string outputPath = args.Length > 0 ? args[0] : "PhotoGrader.ico";
int[] sizes = [16, 24, 32, 48, 64, 128, 256];

var frames = new List<(int Size, byte[] Png)>();

foreach (int size in sizes)
{
    using Bitmap bitmap = Render(size);
    using var buffer = new MemoryStream();
    bitmap.Save(buffer, ImageFormat.Png);
    frames.Add((size, buffer.ToArray()));
}

WriteIco(outputPath, frames);
Console.WriteLine($"已生成 {outputPath} — {frames.Count} 个尺寸：{string.Join(", ", sizes)}");

// 附带导出预览：256 原图 + 小尺寸整数倍放大对比，便于肉眼检查可辨识度
string previewPath = Path.ChangeExtension(outputPath, null) + "-preview.png";
WritePreview(previewPath);
Console.WriteLine($"预览图：{previewPath}");

// ---------------------------------------------------------------- 绘制

static Bitmap Render(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    bitmap.SetResolution(96, 96);

    using Graphics g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

    float s = size / Design;   // 缩放系数

    // 细节分级：小于 32px 时省掉照片内部的景物，只留轮廓 + 星
    bool detailed = size >= 32;

    // ---- 1. 圆角底板（蓝色渐变）
    using (GraphicsPath plate = RoundedRect(
        new RectangleF(6 * s, 6 * s, 244 * s, 244 * s), 56 * s))
    using (var brush = new LinearGradientBrush(
        new PointF(0, 6 * s),
        new PointF(0, 250 * s),
        Color.FromArgb(0x46, 0x9A, 0xE8),
        Color.FromArgb(0x15, 0x55, 0x99)))
    {
        g.FillPath(brush, plate);
    }

    // ---- 2. 白色照片卡片
    var photo = new RectangleF(52 * s, 54 * s, 152 * s, 124 * s);
    using (GraphicsPath card = RoundedRect(photo, 16 * s))
    {
        using (var white = new SolidBrush(Color.White))
        {
            g.FillPath(white, card);
        }

        if (detailed)
        {
            // 照片内部：天空、太阳、山峦 —— 用裁剪保证不出框
            GraphicsState state = g.Save();
            g.SetClip(card);

            var sky = new RectangleF(photo.X, photo.Y, photo.Width, photo.Height * 0.62f);
            using (var skyBrush = new LinearGradientBrush(
                sky, Color.FromArgb(0xBF, 0xE2, 0xFB), Color.FromArgb(0xE8, 0xF4, 0xFD), 90f))
            {
                g.FillRectangle(skyBrush, sky);
            }

            // 太阳
            using (var sun = new SolidBrush(Color.FromArgb(0xF6, 0xB3, 0x4A)))
            {
                g.FillEllipse(sun, photo.X + photo.Width * 0.62f, photo.Y + photo.Height * 0.14f,
                    photo.Width * 0.17f, photo.Width * 0.17f);
            }

            // 远山（浅）与近山（深）
            using (var far = new SolidBrush(Color.FromArgb(0x9A, 0xC4, 0xE8)))
            {
                g.FillPolygon(far, [
                    new PointF(photo.X,                    photo.Y + photo.Height),
                    new PointF(photo.X,                    photo.Y + photo.Height * 0.62f),
                    new PointF(photo.X + photo.Width*0.34f, photo.Y + photo.Height * 0.34f),
                    new PointF(photo.X + photo.Width*0.62f, photo.Y + photo.Height * 0.62f),
                    new PointF(photo.X + photo.Width,       photo.Y + photo.Height * 0.44f),
                    new PointF(photo.X + photo.Width,       photo.Y + photo.Height),
                ]);
            }

            using (var near = new SolidBrush(Color.FromArgb(0x3F, 0x7A, 0xB8)))
            {
                g.FillPolygon(near, [
                    new PointF(photo.X,                    photo.Y + photo.Height),
                    new PointF(photo.X + photo.Width*0.40f, photo.Y + photo.Height * 0.58f),
                    new PointF(photo.X + photo.Width*0.74f, photo.Y + photo.Height * 0.86f),
                    new PointF(photo.X + photo.Width,       photo.Y + photo.Height * 0.68f),
                    new PointF(photo.X + photo.Width,       photo.Y + photo.Height),
                ]);
            }

            g.Restore(state);
        }
    }

    // ---- 3. 右下角金色星标（评分）
    // 底板右下角是半径 56 的圆角，圆心 (194,194)。
    // 星标右下尖角到该圆心的距离必须 < 56，否则会压出圆弧外，小尺寸下显得悬空。
    float starCx = 180 * s;
    float starCy = 176 * s;
    float outer = 52 * s;
    float inner = outer * 0.44f;

    using (GraphicsPath star = StarPath(starCx, starCy, outer, inner))
    {
        // 白色描边让星标在浅色照片上也跳得出来
        using (var pen = new Pen(Color.White, Math.Max(1f, 12 * s)))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPath(pen, star);
        }

        using var glow = new LinearGradientBrush(
            new PointF(starCx - outer, starCy - outer),
            new PointF(starCx + outer, starCy + outer),
            Color.FromArgb(0xFF, 0xD4, 0x6B),
            Color.FromArgb(0xE8, 0x92, 0x18));
        g.FillPath(glow, star);
    }

    return bitmap;
}

static GraphicsPath RoundedRect(RectangleF rect, float radius)
{
    var path = new GraphicsPath();
    float d = radius * 2;

    path.AddArc(rect.X, rect.Y, d, d, 180, 90);
    path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
    path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
    path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
    path.CloseFigure();

    return path;
}

static GraphicsPath StarPath(float cx, float cy, float outer, float inner)
{
    var points = new PointF[10];

    for (int i = 0; i < 10; i++)
    {
        double angle = -Math.PI / 2 + i * Math.PI / 5;
        float r = i % 2 == 0 ? outer : inner;
        points[i] = new PointF(
            cx + (float)(Math.Cos(angle) * r),
            cy + (float)(Math.Sin(angle) * r));
    }

    var path = new GraphicsPath();
    path.AddPolygon(points);
    path.CloseFigure();

    return path;
}

// ---------------------------------------------------------------- 预览图

/// <summary>把各尺寸按整数倍放大后排成一行，左侧单独放 256 原图。</summary>
static void WritePreview(string path)
{
    int[] previewSizes = [16, 24, 32, 48, 256];
    int[] zooms = [8, 6, 5, 4, 1];

    var tiles = new List<Bitmap>();
    for (int i = 0; i < previewSizes.Length; i++)
    {
        int size = previewSizes[i];
        int zoom = zooms[i];

        using Bitmap raw = Render(size);
        var scaled = new Bitmap(size * zoom, size * zoom, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(raw, 0, 0, scaled.Width, scaled.Height);
        }

        tiles.Add(scaled);
    }

    int gap = 20;
    int height = tiles.Max(t => t.Height) + gap * 2;
    int width = tiles.Sum(t => t.Width) + gap * (tiles.Count + 1);

    using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    using (Graphics g = Graphics.FromImage(canvas))
    {
        g.Clear(Color.FromArgb(0xF2, 0xF2, 0xF0));

        int x = gap;
        foreach (Bitmap tile in tiles)
        {
            g.DrawImage(tile, x, (height - tile.Height) / 2);
            x += tile.Width + gap;
        }
    }

    canvas.Save(path, ImageFormat.Png);

    foreach (Bitmap tile in tiles) tile.Dispose();
}

// ---------------------------------------------------------------- ICO 封装

static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
{
    using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var writer = new BinaryWriter(file);

    writer.Write((ushort)0);              // 保留位
    writer.Write((ushort)1);              // 类型：图标
    writer.Write((ushort)frames.Count);   // 图像数量

    int offset = 6 + frames.Count * 16;

    foreach ((int size, byte[] png) in frames)
    {
        // 256 在这个字段里用 0 表示
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);            // 调色板数量
        writer.Write((byte)0);            // 保留位
        writer.Write((ushort)1);          // 色彩平面
        writer.Write((ushort)32);         // 每像素位数
        writer.Write(png.Length);
        writer.Write(offset);

        offset += png.Length;
    }

    foreach ((_, byte[] png) in frames)
    {
        writer.Write(png);
    }
}
