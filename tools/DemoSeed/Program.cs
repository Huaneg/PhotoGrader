using PhotoGrader.Core;

// 给 _uidemo 演示图库批量注入「各种评分状态」，
// 目的是让 UI 截图能同时展示：星级、彩色标签、旗标、灰化的排除图。
//
// 用法：DemoSeed <图库根目录>

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length < 1)
{
    Console.WriteLine("用法：DemoSeed <图库根目录>");
    return 1;
}

string root = args[0];
if (!Directory.Exists(root))
{
    Console.WriteLine($"目录不存在：{root}");
    return 1;
}

// 每个目录一套预设，精心搭配以覆盖界面上的所有状态组合。
// 元组含义：(星级, 彩色标签, 旗标)
var plan = new Dictionary<string, (int Rating, GradeLabel Label, GradeFlag Flag)[]>
{
    // 角色设定：以「留用 + 高星」为主，展示红色旗标
    ["01-角色设定"] =
    [
        (5, GradeLabel.Red,    GradeFlag.Picked),
        (5, GradeLabel.Red,    GradeFlag.Picked),
        (4, GradeLabel.Yellow, GradeFlag.Picked),
        (4, GradeLabel.None,   GradeFlag.None),
        (3, GradeLabel.Green,  GradeFlag.None),
        (2, GradeLabel.None,   GradeFlag.Rejected),
    ],

    // 场景概念：混合，含未评分
    ["02-场景概念"] =
    [
        (5, GradeLabel.Blue,   GradeFlag.Picked),
        (4, GradeLabel.Purple, GradeFlag.Picked),
        (3, GradeLabel.None,   GradeFlag.None),
        (2, GradeLabel.None,   GradeFlag.Rejected),
        (0, GradeLabel.None,   GradeFlag.None),
        (0, GradeLabel.None,   GradeFlag.None),
    ],

    // 产品渲染：以「排除」为主，展示灰化效果
    ["03-产品渲染"] =
    [
        (0, GradeLabel.None, GradeFlag.Rejected),
        (1, GradeLabel.None, GradeFlag.Rejected),
        (2, GradeLabel.None, GradeFlag.Rejected),
        (5, GradeLabel.Green, GradeFlag.Picked),
        (4, GradeLabel.Green, GradeFlag.Picked),
        (3, GradeLabel.None,  GradeFlag.None),
    ],

    // 情绪氛围：五色标签齐活
    ["04-情绪氛围"] =    [
        (5, GradeLabel.Red,    GradeFlag.Picked),
        (4, GradeLabel.Yellow, GradeFlag.Picked),
        (4, GradeLabel.Green,  GradeFlag.Picked),
        (3, GradeLabel.Blue,   GradeFlag.None),
        (3, GradeLabel.Purple, GradeFlag.None),
        (1, GradeLabel.None,   GradeFlag.Rejected),
    ],

    // 产品渲染 / 配件特写：嵌套子目录，验证树状缩进
    ["03-产品渲染/配件特写"] =
    [
        (4, GradeLabel.Blue,   GradeFlag.Picked),
        (3, GradeLabel.None,   GradeFlag.None),
        (2, GradeLabel.None,   GradeFlag.Rejected),
    ],

    // 情绪氛围 / 夜景专辑：嵌套子目录
    ["04-情绪氛围/夜景专辑"] =
    [
        (5, GradeLabel.Purple, GradeFlag.Picked),
        (4, GradeLabel.None,   GradeFlag.None),
    ],

    // 版式测试：各种宽高比，配不同的星级，方便看底部星星排布
    ["05-版式测试"] =
    [
        (5, GradeLabel.Red,   GradeFlag.Picked),
        (4, GradeLabel.None,  GradeFlag.None),
        (3, GradeLabel.None,  GradeFlag.None),
        (2, GradeLabel.None,  GradeFlag.None),
        (1, GradeLabel.None,  GradeFlag.None),
        (0, GradeLabel.None,  GradeFlag.None),
    ],

    // 重复样本：4 张内容相同，全部标记黄色，演示重复角标 + 标签
    ["06-重复样本"] =
    [
        (3, GradeLabel.Yellow, GradeFlag.None),
        (3, GradeLabel.Yellow, GradeFlag.None),
        (3, GradeLabel.Yellow, GradeFlag.None),
        (3, GradeLabel.Yellow, GradeFlag.None),
    ],
};

int ok = 0;
int failed = 0;

foreach ((string folder, var grades) in plan)
{
    string dir = Path.Combine(root, folder);
    if (!Directory.Exists(dir))
    {
        Console.WriteLine($"[跳过] 目录不存在：{dir}");
        continue;
    }

    string[] files = [.. Directory.EnumerateFiles(dir, "*.png").OrderBy(f => f, StringComparer.Ordinal)];

    for (int i = 0; i < files.Length; i++)
    {
        (int rating, GradeLabel label, GradeFlag flag) = grades[i % grades.Length];

        var record = new GradeRecord
        {
            Rating = rating,
            Label = label,
            Flag = flag,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            OriginPath = null,
        };

        try
        {
            GradeWriteResult result = GradeStore.Write(files[i], record);
            string marker = flag switch
            {
                GradeFlag.Picked => "留用",
                GradeFlag.Rejected => "排除",
                _ => "----",
            };
            Console.WriteLine(
                $"  {Path.GetFileName(files[i]),-22} "
                + $"{new string('★', rating)}{new string('☆', 5 - rating)}  "
                + $"{label,-6} {marker}  "
                + $"{(result.Created ? "新建" : "覆盖")} {result.Elapsed.TotalMilliseconds,5:F1}ms");
            ok++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [失败] {files[i]} —— {ex.Message}");
            failed++;
        }
    }

    Console.WriteLine();
}

Console.WriteLine($"完成：成功 {ok}，失败 {failed}");
return failed == 0 ? 0 : 1;
