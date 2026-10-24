using PclLike.Core.Storage;

namespace PclLike.Core.Diagnostics;

/// <summary>扫描出来的垃圾清单。只看不删，先给用户看清楚。</summary>
public sealed record JunkScan(IReadOnlyList<string> Paths, long Bytes)
{
    public int Count => Paths.Count;

    public string SizeText => ByteText.Format(Bytes);

    public bool Any => Paths.Count > 0;
}

/// <summary>清理结果。</summary>
public sealed record JunkCleanResult(int Deleted, int Failed, long Bytes)
{
    public string SizeText => ByteText.Format(Bytes);
}

/// <summary>
/// 清理启动器自己生成的垃圾。
///
/// 划的边界很重要 —— 只删这几类，而且每一条都能说清为什么是垃圾：
///   1. `logs/*.log.gz` 与除了最新一份以外的 `logs/*.log`
///      —— Minecraft 每次启动都会轮转日志，老日志除了占地方没有别的用途；
///      留着 `latest.log` 是因为用户/我们要排障时会去读它。
///   2. `crash-reports/crash-*.txt` 且超过保留天数的
///      —— 崩溃报告是排障证据，刚崩完那一份必须留着，所以按时间保留最近几天。
///   3. `.part` / `.tmp` 残留
///      —— 下载中断留下的半截文件，正常流程会自己清，异常退出的会攒下来。
///
/// **不碰**：存档、Mod、资源包、光影、配置文件、版本文件。删错了用户是找不回来的。
/// </summary>
public static class JunkCleaner
{
    /// 崩溃报告默认保留 7 天 —— 比"只留最近一份"宽松，因为用户可能是隔几天
    /// 才想起来去查"前几天那次为什么崩"。
    public const int DefaultCrashKeepDays = 7;

    public static JunkScan Scan(string gameDir, int crashKeepDays = DefaultCrashKeepDays)
    {
        var paths = new List<string>();
        long bytes = 0;

        void Add(string file)
        {
            try
            {
                bytes += new FileInfo(file).Length;
                paths.Add(file);
            }
            catch { }
        }

        // 1. 日志
        var logs = Path.Combine(gameDir, "logs");
        try
        {
            if (Directory.Exists(logs))
            {
                foreach (var f in Directory.GetFiles(logs, "*.log.gz")) Add(f);

                foreach (var f in Directory.GetFiles(logs, "*.log"))
                {
                    // latest.log 留着：排障要用它
                    if (string.Equals(Path.GetFileName(f), "latest.log",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    Add(f);
                }
            }
        }
        catch { }

        // 2. 过期崩溃报告
        var crashes = Path.Combine(gameDir, "crash-reports");
        try
        {
            if (Directory.Exists(crashes))
            {
                var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, crashKeepDays));
                foreach (var f in Directory.GetFiles(crashes, "crash-*.txt"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < cutoff) Add(f);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 3. 下载残留。只扫这几个目录，不在整个游戏目录上递归 ——
        //    大整合包有几万个文件，全盘递归会让界面卡住。
        foreach (var dir in new[]
                 {
                     gameDir,
                     Path.Combine(gameDir, "mods"),
                     Path.Combine(gameDir, "resourcepacks"),
                     Path.Combine(gameDir, "shaderpacks"),
                     Path.Combine(gameDir, "saves"),
                 })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var pat in new[] { "*.part", "*.tmp" })
                    foreach (var f in Directory.GetFiles(dir, pat)) Add(f);
            }
            catch { }
        }

        return new JunkScan(paths, bytes);
    }

    /// <summary>给已经拿到实例的调用方用的便利重载。</summary>
    public static JunkScan Scan(GameInstance instance, int crashKeepDays = DefaultCrashKeepDays)
        => Scan(instance.GameDir, crashKeepDays);

    public static JunkCleanResult Clean(JunkScan scan)
    {
        var deleted = 0;
        var failed = 0;
        long bytes = 0;

        foreach (var p in scan.Paths)
        {
            try
            {
                var len = new FileInfo(p).Length;
                File.Delete(p);
                deleted++;
                bytes += len;
            }
            catch
            {
                // 单个文件删不掉（被占用/权限）不该中断整轮清理
                failed++;
            }
        }

        return new JunkCleanResult(deleted, failed, bytes);
    }
}