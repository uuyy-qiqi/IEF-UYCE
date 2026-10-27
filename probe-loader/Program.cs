// probe-loader —— 真的把一个加载器装进默认的游戏根，用来验收「安装包」那一栏。
//
// 用法：
//   probe-loader <kind> <mc版本> [加载器版本]
//   kind = fabric | quilt | forge | neoforge | optifine | legacyfabric
//
// 只做「装」，不做「启动」：启动那半在 probe-packlaunch 里验。
//
// 走的是跟界面完全同一条路（LoaderInstaller.InstallAsync），所以它过得去，
// 界面上点「安装这个」就过得去。

using System.Diagnostics;
using PclLike.Core.Minecraft;
using PclLike.Core.Minecraft.Loaders;

// 重定向到文件时默认走的是控制台的 ANSI 代码页（中文会乱码），显式钉成 UTF-8
Console.OutputEncoding = System.Text.Encoding.UTF8;

var kindArg = (args.Length > 0 ? args[0] : "optifine").ToLowerInvariant();
var mc = args.Length > 1 ? args[1] : "1.20.1";
var want = args.Length > 2 ? args[2] : null;

var kind = kindArg switch
{
    "fabric" => LoaderKind.Fabric,
    "quilt" => LoaderKind.Quilt,
    "forge" => LoaderKind.Forge,
    "neoforge" => LoaderKind.NeoForge,
    "optifine" => LoaderKind.OptiFine,
    "legacyfabric" => LoaderKind.LegacyFabric,
    _ => throw new ArgumentException($"认不出的 kind：{kindArg}"),
};

// 默认装到用户真正的游戏根 —— 验收要看到真的落盘，不是落在临时目录里自欺欺人
var root = args.FirstOrDefault(a => a.StartsWith("--root=", StringComparison.Ordinal))?[7..]
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".pcllike");

Console.WriteLine($"root = {root}");
Console.WriteLine($"目标 = {kind} / Minecraft {mc}" + (want is null ? "（不指定，取最新稳定版）" : $" / {want}"));
Console.WriteLine();

var installer = new LoaderInstaller(root);

// ── 1. 列版本 ──
var sw = Stopwatch.StartNew();
IReadOnlyList<LoaderVersion> list;
try
{
    list = await installer.ListAsync(kind, mc);
}
catch (Exception ex)
{
    Console.WriteLine($"✗ 列版本失败（{sw.Elapsed.TotalSeconds:0.#}s）：{ex.GetType().Name}: {ex.Message}");
    return 1;
}

Console.WriteLine($"✓ 列版本 {sw.Elapsed.TotalSeconds:0.#}s，共 {list.Count} 个");
if (list.Count == 0)
{
    Console.WriteLine("  这个 MC 版本没有可用版本，没什么可装的。");
    return 1;
}

var listOnly = args.Contains("--list-only");
foreach (var v in list.Take(listOnly ? 8 : 3))
{
    Console.WriteLine($"    · {v.DisplayName}  → {v.ResultVersionId}  稳定={v.IsStable}  稳定性已知={v.StabilityKnown}");
}
Console.WriteLine($"    其中被标记为稳定版：{list.Count(v => v.StabilityKnown && v.IsStable)} 个；" +
                  $"稳定性未知：{list.Count(v => !v.StabilityKnown)} 个");
if (listOnly) return 0;

var target = want is null
    ? list.FirstOrDefault(v => v.IsStable) ?? list[0]
    : list.FirstOrDefault(v => v.Version == want);

if (target is null)
{
    Console.WriteLine($"✗ 列表里没有版本 {want}");
    return 1;
}

Console.WriteLine();
Console.WriteLine($"选中：{target.DisplayName} → 版本 id {target.ResultVersionId}");
Console.WriteLine($"      installerUrl = {target.InstallerUrl}");
Console.WriteLine();

// ── 2. 真装 ──
sw.Restart();
try
{
    var r = await installer.InstallAsync(kind, mc, target.Version, null, line => Console.WriteLine("   " + line));

    Console.WriteLine();
    Console.WriteLine($"✓ 装完，{sw.Elapsed.TotalSeconds:0.#}s（{sw.Elapsed.TotalMinutes:0.#} 分钟）");
    Console.WriteLine($"  版本 id   = {r.VersionId}");
    Console.WriteLine($"  版本 JSON = {r.JsonPath}  存在={File.Exists(r.JsonPath)}");

    var vdir = Path.GetDirectoryName(r.JsonPath)!;
    foreach (var f in Directory.GetFiles(vdir))
        Console.WriteLine($"    · {Path.GetFileName(f),-44} {new FileInfo(f).Length / 1024.0 / 1024.0:0.#} MB");

    foreach (var w in r.Warnings) Console.WriteLine($"  ⚠ {w}");

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"✗ 装失败，{sw.Elapsed.TotalSeconds:0.#}s —— {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null)
        Console.WriteLine($"  内层：{ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    return 2;
}