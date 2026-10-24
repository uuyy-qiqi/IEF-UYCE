using PclLike.Core.Minecraft;
using PclLike.Core.Minecraft.Loaders;
using PclLike.Core.Storage;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 整合包"真装之后能不能真启动"。
//
// 前一个探针（probe-pack）只证明文件落地了；这里回答更难的那半：
//   把包装进实例之后，本机还要有包要求的 Minecraft + 加载器，实例的 VersionId
//   也得改过去 —— 少了这一步，装完的包就是一堆躺在 mods/ 里没人加载的 jar。
// 所以这个探针会真的去装 fabric-loader、真的把实例指过去、真的把游戏拉起来，
// 再看 latest.log 里 Fabric 有没有把这些 Mod 装上。

const string GameRoot = @"C:\Users\yuwei\AppData\Roaming\.pcllike";
const string JavaPath = @"C:\Users\yuwei\.jdks\jdk-17.0.20.1+1\bin\java.exe";

var instName = "整合包测试";
var mcVersion = "1.20.1";
var loaderVersion = "0.14.23";
var skipInstall = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--instance": instName = args[++i]; break;
        case "--mc": mcVersion = args[++i]; break;
        case "--loader": loaderVersion = args[++i]; break;
        case "--fast": skipInstall = true; break;
    }
}

var store = new InstanceStore(GameRoot);
var inst = store.LoadAll().FirstOrDefault(i => i.Name == instName);

if (inst is null)
{
    Console.WriteLine($"✗ 没有叫「{instName}」的实例 —— 先跑 probe-pack --go");
    return 1;
}

Console.WriteLine("══════ 整合包实例：真启动验证 ══════");
Console.WriteLine($"实例   : {inst.Name}（当前 VersionId={inst.VersionId}）");
Console.WriteLine($"gameDir: {inst.GameDir}");
Console.WriteLine();

// ─── A. 包里装进来了什么 ───
var modsDir = Path.Combine(inst.GameDir, "mods");
var mods = Directory.Exists(modsDir) ? Directory.GetFiles(modsDir, "*.jar") : [];
var modsMb = mods.Sum(f => new FileInfo(f).Length) / 1024.0 / 1024.0;

var configDir = Path.Combine(inst.GameDir, "config");
var cfg = Directory.Exists(configDir)
    ? Directory.GetFiles(configDir, "*", SearchOption.AllDirectories).Length : 0;

Console.WriteLine("── A. 实例里现在有什么 ──");
Console.WriteLine($"    mods/   : {mods.Length} 个 jar，{modsMb:0.#} MB");
Console.WriteLine($"    config/ : {cfg} 个文件（来自 overrides）");
foreach (var m in mods.Take(6)) Console.WriteLine($"      {Path.GetFileName(m)}");
if (mods.Length > 6) Console.WriteLine($"      …还有 {mods.Length - 6} 个");
Console.WriteLine();

// ─── B. 包要求的加载器装了吗 ───
var wanted = $"fabric-loader-{loaderVersion}-{mcVersion}";
var wantedJson = Path.Combine(GameRoot, "versions", wanted, wanted + ".json");

Console.WriteLine("── B. 安装包要求的加载器 ──");
Console.WriteLine($"    需要：{wanted}");

if (File.Exists(wantedJson))
{
    Console.WriteLine("    已经装过了，跳过");
}
else
{
    using var li = new LoaderInstaller(GameRoot);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var res = await li.InstallAsync(
        LoaderKind.Fabric,
        mcVersion,
        loaderVersion,
        progress: new Progress<LoaderInstaller.Progress>(p =>
        {
            if (p.Total > 0 && p.Done == p.Total) Console.WriteLine($"      {p.Stage} 完成");
        }),
        onLog: l => Console.WriteLine("      " + l));

    Console.WriteLine($"    → {res.VersionId}（{sw.Elapsed.TotalSeconds:0.#} 秒）");
    Console.WriteLine($"      {res.JsonPath}");

    if (res.Warnings.Count > 0)
        foreach (var w in res.Warnings) Console.WriteLine($"      ⚠ {w}");

    wanted = res.VersionId;
    wantedJson = res.JsonPath;
}

if (!File.Exists(wantedJson))
{
    Console.WriteLine("    ✗ 加载器没装上，没法启动");
    return 2;
}
Console.WriteLine();

// ─── C. 把实例指到这个版本 ───
Console.WriteLine("── C. 把实例的版本改成包要求的那个 ──");
inst.VersionId = wanted;
store.Save(inst);
var reread = store.Load(inst.Id);
Console.WriteLine($"    实例 VersionId：{inst.VersionId} → 回读 {reread?.VersionId}");
Console.WriteLine();

// ─── D. 真启动 ───
Console.WriteLine("── D. 真启动（90 秒预算，界面出现在日志里就算成功）──");

using var svc = new LaunchService(GameRoot);

var opts = new LaunchService.LaunchOptions
{
    VersionId = wanted,
    Username = "PackTest",
    Uuid = "00000000000000000000000000000000",
    AccessToken = "0",
    MaxMemoryMb = 4096,
    MinMemoryMb = 512,
    JavaPath = JavaPath,

    // 默认走完整流程（包含"安装／补全文件"）。
    //
    // 这一步不是可省略的装饰：加载器版本目录里原本没有游戏 jar（原版 jar 在
    // versions/1.20.1 下），补全步骤负责把它放到加载器版本目录里、并核对
    // assets / natives。跳过它就会看到 Fabric 报
    // "Class path entries reference missing files: .../<loaderId>.jar"。
    SkipInstall = skipInstall,
    Instance = inst,
};

var lines = new List<string>();

try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

    var outcome = await svc.LaunchAsync(
        opts,
        progress: null,
        onLog: line =>
        {
            lines.Add(line);
            if (line.Contains("Fabric") || line.Contains("Loading") || line.Contains("mods")
                || line.Contains("Exception") || line.Contains("error"))
                Console.WriteLine("    " + line);
        },
        ct: cts.Token);

    Console.WriteLine($"    ExitCode={outcome.ExitCode} Crashed={outcome.Crashed} {outcome.Reason}");
    Console.WriteLine($"    日志：{outcome.LogPath}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("    ✓ 跑满 90 秒没退出 → 判定启动成功");
    svc.KillGame();
}

// ─── E. 日志筛查 ───
Console.WriteLine();
Console.WriteLine("── E. 日志筛查 ──");

// 游戏自己的日志才是权威：它写在实例目录里
var gameLog = Path.Combine(inst.GameDir, "logs", "latest.log");
Console.WriteLine($"    游戏日志：{gameLog}（存在={File.Exists(gameLog)}）");

var gameLines = new List<string>();
if (File.Exists(gameLog))
{
    try
    {
        using var fs = new FileStream(gameLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } l) gameLines.Add(l);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    读日志失败：{ex.Message}");
    }
}

Console.WriteLine($"    共 {gameLines.Count} 行");

var bad = new[]
{
    "Incompatible mod set",
    "Mod resolution failed",
    "A fatal error",
    "Failed to start game",
    "Unsupported class file",
    "NoSuchMethodError",
};
Console.WriteLine("    不该出现的：");
foreach (var pat in bad)
{
    var hit = gameLines.FirstOrDefault(l => l.Contains(pat, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(hit is null ? $"      ✓ 未出现「{pat}」" : $"      ✗ 命中「{pat}」：{hit.Trim()[..Math.Min(120, hit.Trim().Length)]}");
}

var good = new[]
{
    "Loading Minecraft 1.20.1",
    "Fabric Loader",
    "Loading 5",
    "Setting user",
    "Render thread",
};
Console.WriteLine("    该出现的：");
foreach (var pat in good)
{
    var hit = gameLines.FirstOrDefault(l => l.Contains(pat, StringComparison.OrdinalIgnoreCase))
           ?? lines.FirstOrDefault(l => l.Contains(pat, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(hit is null ? $"      · 未见「{pat}」" : $"      ✓ {hit.Trim()[..Math.Min(120, hit.Trim().Length)]}");
}

// Fabric 自己的加载条数（"Loading N mods"）
var loading = gameLines.FirstOrDefault(l => l.Contains("mods", StringComparison.OrdinalIgnoreCase)
                                        && l.Contains("Loading", StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"    Fabric 加载声明：{loading?.Trim() ?? "（没找到）"}");

Console.WriteLine();
Console.WriteLine("══════ 结束 ══════");
return 0;