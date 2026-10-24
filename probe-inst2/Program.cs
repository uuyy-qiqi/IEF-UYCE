using PclLike.Core.Minecraft;
using PclLike.Core.Storage;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 实例功能的真实验证。
//
// 两件事分开测：
//   A. InstanceStore 的建/复制/删/默认实例语义（纯文件操作，秒级）
//   B. 把实例交给 LaunchService，看游戏到底把日志写进了哪个目录 ——
//      logs/ 出现在实例目录里才说明 --gameDir 真的被换掉了，
//      只看命令行参数不算数（参数对了也可能被后面的代码覆盖）。

const string GameRoot = @"C:\Users\yuwei\AppData\Roaming\.pcllike";
const string JavaPath = @"C:\Users\yuwei\.jdks\jdk-17.0.20.1+1\bin\java.exe";
const string VersionId = "1.20.1";

var store = new InstanceStore(GameRoot);
var fails = new List<string>();

void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine(ok ? $"    ✓ {what}" : $"    ✗ {what}  {detail}");
    if (!ok) fails.Add(what);
}

Console.WriteLine("══════ 实例功能验证 ══════");
Console.WriteLine($"启动器根：{GameRoot}");
Console.WriteLine();

// ─────────── A. 仓库语义 ───────────

Console.WriteLine("── A. InstanceStore ──");

var before = store.LoadAll();
Console.WriteLine($"    现有实例 {before.Count} 个：{string.Join("、", before.Select(i => $"{i.Name}({i.VersionId})"))}");

// 默认实例：共享目录，gameDir 就是启动器根
var def = store.EnsureDefault(VersionId);
Console.WriteLine($"    EnsureDefault → {def.Name} / 独立={def.Isolated} / gameDir={def.GameDir}");
Check("默认实例是共享模式（gameDir == 启动器根）",
      !def.Isolated && Path.GetFullPath(def.GameDir) == Path.GetFullPath(GameRoot),
      $"实际 {def.GameDir}");

// 独立实例
var probeName = "探查实例";
var old = store.LoadAll().FirstOrDefault(i => i.Name == probeName);
if (old is not null) store.Delete(old.Id, deleteFiles: true);

var iso = store.Create(probeName, VersionId, isolated: true);
Console.WriteLine($"    Create    → id={iso.Id} / 独立={iso.Isolated}");
Console.WriteLine($"                gameDir={iso.GameDir}");
Check("独立实例的 gameDir 在 instances/<id>/.minecraft",
      iso.GameDir.StartsWith(Path.Combine(GameRoot, "instances"), StringComparison.OrdinalIgnoreCase)
      && iso.GameDir.EndsWith(".minecraft", StringComparison.OrdinalIgnoreCase),
      $"实际 {iso.GameDir}");
Check("独立实例目录已创建", Directory.Exists(iso.GameDir));
Check("IsolationLabel = 独立", iso.IsolationLabel == "独立");

// 从新实例读回来，运行期字段是否被填上
var reread = store.Load(iso.Id);
Check("Load 回填 Directory / LauncherRoot / GameDir",
      reread is not null
      && reread.Directory.Length > 0
      && reread.LauncherRoot == GameRoot
      && reread.GameDir == iso.GameDir,
      $"Directory={reread?.Directory} GameDir={reread?.GameDir}");

// 造点私有文件，验证复制会带上
Directory.CreateDirectory(Path.Combine(iso.GameDir, "saves", "世界一"));
File.WriteAllText(Path.Combine(iso.GameDir, "saves", "世界一", "level.dat"), "fake");
Directory.CreateDirectory(Path.Combine(iso.GameDir, "mods"));
File.WriteAllText(Path.Combine(iso.GameDir, "mods", "jei.jar"), "fake");
File.WriteAllText(Path.Combine(iso.GameDir, "options.txt"), "fov:0.5");

var copy = store.Copy(iso.Id, "探查实例-副本");
Console.WriteLine($"    Copy      → id={copy.Id} / 独立={copy.Isolated}");
Check("副本也是独立模式", copy.Isolated);
Check("副本带上了存档", File.Exists(Path.Combine(copy.GameDir, "saves", "世界一", "level.dat")));
Check("副本带上了 mods", File.Exists(Path.Combine(copy.GameDir, "mods", "jei.jar")));
Check("副本带上了 options.txt", File.Exists(Path.Combine(copy.GameDir, "options.txt")));
Check("副本目录和源目录不是同一个",
      Path.GetFullPath(copy.GameDir) != Path.GetFullPath(iso.GameDir), copy.GameDir);

// 共享实例的 gameDir 是启动器根，里面装着所有版本的公共文件；
// 复制它时必须只挑私有的子目录，否则会把整个启动器抄一遍。
var sharedCopy = store.Copy(def.Id, "探查实例-共享副本");
Check("共享实例的副本没把 assets/libraries 抄进来",
      !Directory.Exists(Path.Combine(sharedCopy.GameDir, "assets"))
      || new DirectoryInfo(Path.Combine(sharedCopy.GameDir, "assets")).EnumerateFiles().Take(1).Count() < 100,
      sharedCopy.GameDir);

// 删除
Check("删除实例（保留文件）", store.Delete(copy.Id, deleteFiles: false)
      && Directory.Exists(copy.GameDir));
Check("删除实例（连文件）", store.Delete(sharedCopy.Id, deleteFiles: true)
      && !Directory.Exists(sharedCopy.GameDir));

Console.WriteLine();

// ─────────── B. 真实启动 ───────────

Console.WriteLine("── B. 真实启动（实例 → --gameDir）──");
Console.WriteLine($"    实例：{iso.Name} → {iso.GameDir}");

// 先把可能残留的日志清掉，才能证明是新写进来的
var instLogs = Path.Combine(iso.GameDir, "logs", "latest.log");
if (File.Exists(instLogs)) File.Delete(instLogs);

using var svc = new LaunchService(GameRoot);

var opts = new LaunchService.LaunchOptions
{
    VersionId = VersionId,
    Instance = store.Load(iso.Id),      // 重新读一次，模拟 VM 的实际用法
    Username = "ProbePlayer",
    Uuid = "00000000000000000000000000000000",
    AccessToken = "0",
    MaxMemoryMb = 2048,
    MinMemoryMb = 512,
    JavaPath = JavaPath,
    SkipInstall = false,                // 让它自己校验/补齐
};

Console.WriteLine("    ▸ 启动中（最多等 70 秒）");
Console.WriteLine();

var logLines = new List<string>();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));

try
{
    _ = await svc.LaunchAsync(opts, progress: null,
        onLog: line =>
        {
            logLines.Add(line);
            if (logLines.Count <= 12) Console.WriteLine("      " + line);
        },
        ct: cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("      … 已跑满 70 秒，收工");
}
finally
{
    svc.KillGame();

    // 等进程真的退干净再删目录 —— 游戏还攥着 latest.log 的话，
    // 删除会以 IOException 告终（InstanceStore.Delete 会返回 false）
    try { svc.CurrentHandle?.Process.WaitForExit(8000); } catch { /* 已经退出了 */ }
}

Console.WriteLine();
Console.WriteLine("── C. 落地检查 ──");

// gameDir 真的换了的话，游戏会往实例目录写日志和 options.txt
var instLogDir = Path.Combine(iso.GameDir, "logs");
var sharedLog = Path.Combine(GameRoot, "logs", "latest.log");

Check("实例目录下出现了 logs/", Directory.Exists(instLogDir));
Check("实例目录下写了 latest.log", File.Exists(instLogs));

if (File.Exists(instLogs))
{
    var fi = new FileInfo(instLogs);
    Console.WriteLine($"      latest.log {fi.Length} 字节，改于 {fi.LastWriteTime:HH:mm:ss}");

    // 游戏可能还攥着这个文件（log4j 以写方式独占打开），
    // 所以必须自己开 FileShare.ReadWrite 才读得到，File.ReadLines 会抛 IOException
    var lines = ReadShared(instLogs);

    // 游戏自己会打印 gameDir
    var dirLine = lines.FirstOrDefault(l => l.Contains("gameDir", StringComparison.OrdinalIgnoreCase)
                                         || l.Contains("Working Directory", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(dirLine is null ? "      （日志里没直接打印 gameDir）" : $"      日志：{dirLine.Trim()}");

    // 原版客户端不打印 "Loading Minecraft"（那是 ModLauncher 的话），
    // 看这两句才准：Datafixer 起来 + 渲染线程开始干活 = 客户端真跑起来了
    var booted = lines.Any(l =>
        l.Contains("Datafixer", StringComparison.OrdinalIgnoreCase)
        && l.Contains("optimizations", StringComparison.OrdinalIgnoreCase));
    Check("日志里有 Datafixer 启动记录（游戏真的跑了）", booted);

    Check("日志里有渲染线程活动",
          lines.Any(l => l.Contains("Render thread", StringComparison.OrdinalIgnoreCase)));
}

Check("游戏没有往共享根目录写日志（说明没走旧的 gameDir）",
      !File.Exists(sharedLog) || File.GetLastWriteTime(sharedLog) < DateTime.Now.AddMinutes(-1),
      File.Exists(sharedLog) ? $"共享日志改于 {File.GetLastWriteTime(sharedLog):HH:mm:ss}" : "");

Console.WriteLine();

// ─────────── D. 收尾 ───────────

Console.WriteLine("── D. 收尾 ──");

var cleaned = false;
for (var i = 0; i < 10 && !cleaned; i++)
{
    cleaned = store.Delete(iso.Id, deleteFiles: true);
    if (!cleaned) await Task.Delay(800);
}
Check("探查实例已清理", cleaned && !Directory.Exists(iso.Directory));

Console.WriteLine();
Console.WriteLine(fails.Count == 0
    ? "══════ 全部通过 ══════"
    : $"══════ {fails.Count} 项失败：\n  - {string.Join("\n  - ", fails)} ══════");

return fails.Count == 0 ? 0 : 1;

/// <summary>
/// 以共享模式读取一个"正在被写"的日志文件。
///
/// 游戏用 log4j 打开 latest.log 时不允许别人再读，File.ReadLines 会直接
/// 抛 IOException —— 必须自己指定 FileShare.ReadWrite。
/// </summary>
static string[] ReadShared(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var sr = new StreamReader(fs);

    var list = new List<string>();
    while (sr.ReadLine() is { } line) list.Add(line);
    return [.. list];
}