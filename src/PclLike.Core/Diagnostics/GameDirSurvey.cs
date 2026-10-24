using PclLike.Core.Storage;

namespace PclLike.Core.Diagnostics;

/// <summary>
/// 实例里一个子目录的占用。
///
/// 为什么要分开统计而不是只给一个总数：用户看到"游戏目录 1.8 GB"没有任何行动力，
/// 但看到"其中 saves 1.2 GB"就知道该去删存档了。启动器能给的最有用的东西是
/// **把体积归因到用户能动手的地方**。
/// </summary>
public sealed record DirUsage(string Label, string Path, long Bytes, int Files)
{
    public bool Exists { get; init; }

    public string SizeText => ByteText.Format(Bytes);

    /// <summary>目录不存在时不要显示 "0 B"，那会让人以为目录是空的（其实是还没有）。</summary>
    public string DisplayText => Exists ? $"{SizeText} · {Files} 个文件" : "尚未生成";
}

/// <summary>一条崩溃报告。</summary>
public sealed record CrashReport(string Path, string FileName, DateTimeOffset? Time)
{
    public string TimeText => Time is null
        ? "—"
        : Time.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}

/// <summary>游戏目录体检结果。全是只读统计，不修改任何东西。</summary>
public sealed record GameDirSurvey
{
    public required string GameDir { get; init; }
    public required int WorldCount { get; init; }
    public required int ScreenshotCount { get; init; }
    public required IReadOnlyList<CrashReport> CrashReports { get; init; }
    public required IReadOnlyList<DirUsage> Usages { get; init; }
    public required long TotalBytes { get; init; }

    /// <summary>崩溃报告按时间倒序，所以第一条就是最近那次。</summary>
    public CrashReport? LatestCrash => CrashReports.Count > 0 ? CrashReports[0] : null;

    public string WorldText => WorldCount > 0 ? $"{WorldCount} 个存档" : "还没有存档";

    public string ScreenshotText => ScreenshotCount > 0 ? $"{ScreenshotCount} 张截图" : "还没有截图";

    public string CrashText => LatestCrash is null
        ? "没有崩溃报告"
        : $"最近一次：{LatestCrash.TimeText}";

    public string TotalText => ByteText.Format(TotalBytes);

    /// <summary>给界面用的一行摘要，例如 "1.8 GB · 12 个存档 · 3 张截图"。</summary>
    public string SummaryText => $"{TotalText} · {WorldText} · {ScreenshotText}";
}

/// <summary>
/// 把字节数格式化成人看的样子。
///
/// 1024 进制（KB/MB/GB）而不是 1000 进制：Windows 资源管理器和 Minecraft 自己
/// 都是这么显示的，用 1000 进制会和用户在别处看到的数字对不上。
/// </summary>
public static class ByteText
{
    public static string Format(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";

        double v = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var i = -1;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        // 小于 10 的保留一位小数（1.8 GB 比 2 GB 有用），大于 10 就不要小数了
        return v < 10 ? $"{v:0.#} {units[i]}" : $"{v:0} {units[i]}";
    }
}

/// <summary>
/// 给"百宝箱"用的游戏目录体检。
///
/// 全程只读、不抛异常 —— 用户点一下"看看占了多少"，不该因为某个子目录权限不足
/// 就弹一个红框。拿不到的目录按"不存在"处理。
/// </summary>
public static class GameDirSurveyor
{
    /// <summary>
    /// 主入口收的是**目录字符串**而不是 GameInstance。
    ///
    /// 因为"看看占了多少"这件事在"还没选实例"的时候也该能用（那就看共享根目录），
    /// 而为了扫一个目录去造一个假实例，会让调用方以为自己在改实例状态。
    /// </summary>
    public static GameDirSurvey Take(string gameDir)
    {
        var usages = new List<DirUsage>
        {
            Measure("存档 saves", Path.Combine(gameDir, "saves")),
            Measure("Mod", Path.Combine(gameDir, "mods")),
            Measure("资源包", Path.Combine(gameDir, "resourcepacks")),
            Measure("光影包", Path.Combine(gameDir, "shaderpacks")),
            Measure("截图", Path.Combine(gameDir, "screenshots")),
            Measure("日志", Path.Combine(gameDir, "logs")),
            Measure("崩溃报告", Path.Combine(gameDir, "crash-reports")),
        };

        long total = 0;
        try
        {
            if (Directory.Exists(gameDir)) total = DirBytes(gameDir);
        }
        catch { }

        return new GameDirSurvey
        {
            GameDir = gameDir,
            WorldCount = CountWorlds(Path.Combine(gameDir, "saves")),
            ScreenshotCount = CountFiles(Path.Combine(gameDir, "screenshots"), "*.png"),
            CrashReports = ListCrashes(Path.Combine(gameDir, "crash-reports")),
            Usages = usages,
            TotalBytes = total,
        };
    }

    /// <summary>给已经拿到实例的调用方用的便利重载。</summary>
    public static GameDirSurvey Take(GameInstance instance) => Take(instance.GameDir);

    private static DirUsage Measure(string label, string path)
    {
        var exists = false;
        long bytes = 0;
        var files = 0;

        try
        {
            if (Directory.Exists(path))
            {
                exists = true;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        bytes += new FileInfo(f).Length;
                        files++;
                    }
                    catch { }
                }
            }
        }
        catch { }

        return new DirUsage(label, path, bytes, files) { Exists = exists };
    }

    private static long DirBytes(string path)
    {
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; }
            catch { }
        }
        return total;
    }

    /// <summary>
    /// 数世界：只认"目录里还有 level.dat"的目录。
    ///
    /// 光看目录数量会把 mods 生成的杂项目录（比如 `Backup`、`world_backup_*`）
    /// 也算成存档，用户会发现数字对不上。
    /// </summary>
    private static int CountWorlds(string savesDir)
    {
        var n = 0;
        try
        {
            if (!Directory.Exists(savesDir)) return 0;
            foreach (var d in Directory.EnumerateDirectories(savesDir))
            {
                try
                {
                    if (File.Exists(Path.Combine(d, "level.dat"))) n++;
                }
                catch { }
            }
        }
        catch { }
        return n;
    }

    private static int CountFiles(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Length : 0;
        }
        catch { return 0; }
    }

    private static IReadOnlyList<CrashReport> ListCrashes(string dir)
    {
        var list = new List<CrashReport>();
        try
        {
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "crash-*.txt"))
            {
                DateTimeOffset? t = null;
                try { t = File.GetLastWriteTimeUtc(f); }
                catch { }
                list.Add(new CrashReport(f, Path.GetFileName(f), t));
            }
        }
        catch { }

        return list.OrderByDescending(c => c.Time ?? DateTimeOffset.MinValue).ToList();
    }
}