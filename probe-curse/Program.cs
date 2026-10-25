using System.Text;
using PclLike.Core.Content;
using PclLike.Core.Mods;

// 只跑 CurseForge 整合包的「铺 overrides + 下文件」这一层。
//
// 为什么不走 GUI：走那条路要先建实例、装游戏本体、装 Forge（15 分钟起步），
// 调这一层不值当。这个探针直接调 CursePack.InstallAsync，把 instanceGameDir
// 指到一个已经存在的实例上就行；已经下好的文件会被跳过。
//
// 用法: probe-curse <pack.zip> <instanceGameDir>

// WinExe 没有控制台，Console.OutputEncoding 会抛 —— 直接接管标准输出流，
// 固定成 UTF-8，这样重定向出来的日志用 UTF-8 读就行（不用猜 GBK）。
try
{
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
    {
        AutoFlush = true,
    });
}
catch { /* 接不走就算了，后面照样写 */ }

if (args.Length < 2)
{
    Console.WriteLine("用法: probe-curse <pack.zip> <instanceGameDir>");
    return 2;
}

var zip = args[0];
var target = args[1];

Console.WriteLine($"包    : {zip}");
Console.WriteLine($"目标  : {target}");
Console.WriteLine();

if (!File.Exists(zip))
{
    Console.WriteLine("✗ 找不到包");
    return 2;
}

var plan = CursePack.ReadPlan(zip);

Console.WriteLine($"整合包: {plan.DisplayName} {plan.Version}");
Console.WriteLine($"游戏  : {plan.McVersion}    加载器: {plan.LoaderName} {plan.LoaderVersion}");
Console.WriteLine($"文件  : {plan.Files.Count} 个    overrides 目录: {plan.OverridesDir}");
Console.WriteLine();

Directory.CreateDirectory(target);

var downloader = new ModDownloader();

// 和 GUI 里那份 fetch 一致：清单只给 projectId/fileId，文件名要先问一次
async Task<string?> Fetch(int projectId, int fileId, string dir, CancellationToken ct)
{
    var name = await downloader.ResolveCurseFileNameAsync(projectId, fileId, ct).ConfigureAwait(false)
               ?? $"cf-{projectId}-{fileId}.jar";

    var dest = Path.Combine(dir, name);
    var info = new FileInfo(dest);
    if (info.Exists && info.Length > 0) return name;

    var candidate = new ModDownloadCandidate
    {
        Platform = "curseforge",
        ProjectId = projectId.ToString(),
        VersionId = fileId.ToString(),
        Name = $"curse-{projectId}",
        Version = fileId.ToString(),
        FileName = name,
        DownloadUrl = CursePack.DownloadUrl(projectId, fileId),
    };

    var ok = await downloader.DownloadAsync(candidate, dest, null, ct).ConfigureAwait(false);
    return ok ? name : null;
}

var sw = System.Diagnostics.Stopwatch.StartNew();

var result = await CursePack.InstallAsync(zip, plan, target, Fetch, Console.WriteLine);

sw.Stop();

Console.WriteLine();
Console.WriteLine($"结果  : {(result.Ok ? "✓ 成功" : "✗ 有问题")} —— {result.Message}");
Console.WriteLine($"用时  : {(int)sw.Elapsed.TotalSeconds} 秒");

// 落盘核对：数字对不上时以盘上的为准
var modsDir = Path.Combine(target, "mods");
var mods = Directory.Exists(modsDir) ? Directory.GetFiles(modsDir) : [];
var modMb = mods.Length == 0 ? 0 : Math.Round(mods.Sum(f => new FileInfo(f).Length) / 1048576.0, 1);

var overrides = 0;
var allFiles = Directory.Exists(target) ? Directory.GetFiles(target, "*", SearchOption.AllDirectories) : [];
foreach (var f in allFiles)
{
    if (f.StartsWith(modsDir, StringComparison.OrdinalIgnoreCase)) continue;
    overrides++;
}

Console.WriteLine();
Console.WriteLine($"盘上 mods      : {mods.Length} 个文件 / {modMb} MB（清单要 {plan.Files.Count} 个）");
Console.WriteLine($"盘上其它文件    : {overrides} 个（overrides 铺出来的）");

return result.Ok ? 0 : 1;