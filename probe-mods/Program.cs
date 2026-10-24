using System.Text;
using PclLike.Core.Mods;
using PclLike.Core.Storage;

// Mod 管理链路的端到端实测：
//   建实例 → 装真实 Fabric Mod → 扫描/解析 → 依赖分析 → 自动补齐 → 启停/删除
//
// 用真实网络下载，所以会联网。测完不清空，方便手动看效果。

Console.OutputEncoding = Encoding.UTF8;

var root = Path.Combine(Path.GetTempPath(), "pcllike-modtest");
Console.WriteLine($"测试根目录: {root}");

var store = new InstanceStore(root);
var instance = store.Load("modtest");
if (instance is null)
{
    instance = store.Create("modtest", "1.21.1");
    instance.Loader = "fabric";
    instance.LoaderVersion = "0.16.9";
    store.Save(instance);
}
else
{
    instance.Loader = "fabric";
    instance.LoaderVersion = "0.16.9";
    store.Save(instance);
}

Console.WriteLine($"实例: {instance.Id}  MC {instance.VersionId}  {instance.Loader}");
Console.WriteLine($"GameDir: {instance.GameDir}");

var manager = new ModManager(instance);
Console.WriteLine($"mods 目录: {manager.ModsDirectory}");

// ── 1. 空目录扫描 ──
Console.WriteLine("\n[1] 空目录扫描");
var empty = manager.Scan();
Console.WriteLine($"    目录存在: {empty.DirectoryExists}  文件数: {empty.Mods.Count}");

// ── 2. 下载一个真实 Mod（sodium，无依赖，容易验证） ──
Console.WriteLine("\n[2] 下载 sodium (Fabric 1.21.1)");
using var dl = new ModDownloader();

var sodium = await dl.FindModrinthAsync("sodium", "1.21.1", "fabric");
if (sodium is null)
{
    Console.WriteLine("    ✗ 查不到 sodium —— 网络问题或 API 变化");
    return 1;
}

Console.WriteLine($"    ✓ {sodium.Name} {sodium.Version}");
Console.WriteLine($"      文件: {sodium.FileName}  {sodium.SizeBytes / 1024}KB");
Console.WriteLine($"      加载器: {string.Join(",", sodium.Loaders)}");
Console.WriteLine($"      MC: {string.Join(",", sodium.GameVersions.Take(5))}");
Console.WriteLine($"      sha1: {sodium.Sha1?[..12] ?? "(无)"}");

var target = Path.Combine(manager.ModsDirectory, sodium.FileName);
var ok = await dl.DownloadAsync(sodium, target,
    new Progress<DownloadProgress>(p => Console.WriteLine($"      进度: {p.Text}")));

Console.WriteLine($"    下载结果: {(ok ? "成功" : "失败")}");

// 校验 sha1 是否匹配（DownloadAsync 内部已校验，这里是二次确认）
if (ok && sodium.Sha1 is not null)
{
    using var sha = System.Security.Cryptography.SHA1.Create();
    await using var fs = File.OpenRead(target);
    var actual = Convert.ToHexString(await sha.ComputeHashAsync(fs)).ToLowerInvariant();
    Console.WriteLine($"    sha1 二次校验: {(actual == sodium.Sha1 ? "匹配 ✓" : "不匹配 ✗")}");
}

// ── 3. 带依赖的 Mod：装一个明确依赖别的库的 ──
Console.WriteLine("\n[3] 下载 sodium-extra（依赖 sodium + fabric-api）");
var extra = await dl.FindModrinthAsync("sodium-extra", "1.21.1", "fabric");

if (extra is null)
{
    Console.WriteLine("    ✗ 查不到 sodium-extra");
}
else
{
    Console.WriteLine($"    ✓ {extra.Name} {extra.Version}  文件: {extra.FileName}");
    Console.WriteLine($"      声明的必需依赖: {extra.DependencyIds.Count} 个");

    var t2 = Path.Combine(manager.ModsDirectory, extra.FileName);
    var ok2 = await dl.DownloadAsync(extra, t2, null);
    Console.WriteLine($"    下载结果: {(ok2 ? "成功" : "失败")}");
}

// ── 4. 扫描 + 元数据解析 ──
Console.WriteLine("\n[4] 扫描并解析元数据");
var scan = manager.Scan();
Console.WriteLine($"    目录存在: {scan.DirectoryExists}  发现 {scan.Mods.Count} 个 Mod");

foreach (var m in scan.Mods)
{
    Console.WriteLine($"    ── {m.DisplayName}");
    Console.WriteLine($"       文件: {m.FileName}  ({m.SizeBytes / 1024}KB)  启用={m.Enabled}");

    if (m.Info is null)
    {
        Console.WriteLine($"       ✗ 解析失败: {m.ParseError}");
        continue;
    }

    Console.WriteLine($"       id={m.Info.Id}  version={m.Info.Version}");
    Console.WriteLine($"       名称={m.Info.Name}");
    Console.WriteLine($"       加载器=[{string.Join(",", m.Info.Loaders)}]");
    Console.WriteLine($"       MC版本=[{string.Join(",", m.Info.McVersions.Take(4))}]");
    Console.WriteLine($"       依赖数={m.Info.Dependencies.Count}");

    foreach (var (k, v) in m.Info.Dependencies.Take(6))
        Console.WriteLine($"         · {k} {v}");
}

// ── 5. 依赖分析 ──
Console.WriteLine("\n[5] 依赖分析");
var issues = ModDependencyAnalyzer.Analyze(scan.Mods);
Console.WriteLine($"    发现 {issues.Count} 个问题");

foreach (var i in issues.Take(10))
    Console.WriteLine($"    [{i.Level}] {i.SourceName}: {i.Message}");

// ── 6. 游戏兼容性检查 ──
Console.WriteLine("\n[6] 兼容性检查（目标 MC 1.21.1 / fabric）");
var compat = ModDependencyAnalyzer.CheckGameCompatibility(scan.Mods, "1.21.1", "fabric");
Console.WriteLine($"    发现 {compat.Count} 个问题");
foreach (var i in compat.Take(6))
    Console.WriteLine($"    [{i.Level}] {i.SourceName}: {i.Message}");

// 故意用错的版本，验证能报出来
Console.WriteLine("\n[6b] 故意用 MC 1.19.2 检查（应该报不兼容）");
var compat2 = ModDependencyAnalyzer.CheckGameCompatibility(scan.Mods, "1.19.2", "fabric");
Console.WriteLine($"    发现 {compat2.Count} 个问题");
foreach (var i in compat2.Take(4))
    Console.WriteLine($"    [{i.Level}] {i.SourceName}: {i.Message}");

// ── 7. 版本范围匹配单元验证 ──
Console.WriteLine("\n[7] 版本范围匹配");

var cases = new (string Ver, string Range, bool Expect)[]
{
    ("1.21.1", "[1.20,1.22)", true),
    ("1.21.1", "[1.22,)", false),
    ("1.21.1", "(,1.21]", false),
    ("1.21",   "[1.21,)", true),
    ("1.20.1", "[1.20.1]", true),
    ("1.19.2", ">=1.19 <1.20", true),
    ("1.20.1", ">=1.19 <1.20", false),
    ("1.21.1", "~1.21", true),
    ("1.21.1", "~1.20", false),
    ("1.21.1", "^1.21", true),
    ("2.0.0",  "^1.21", false),
    ("1.20.1", "1.20", true),        // 前缀匹配
    ("1.20.1", "1.20.1", true),
    ("1.20.1", "1.20.2", false),
    ("1.20.1", "*", true),
    ("abc",    ">=1.0", true),       // 解析不了 → 放行
};

var pass = 0;
foreach (var (ver, range, expect) in cases)
{
    var got = VersionRange.Matches(ver, range, out var why);
    var mark = got == expect ? "✓" : "✗";
    if (got == expect) pass++;
    Console.WriteLine($"    {mark} {ver,-8} {range,-16} => {got} (期望 {expect}) {(why.Length > 0 ? "[" + why + "]" : "")}");
}
Console.WriteLine($"    {pass}/{cases.Length} 通过");

// ── 8. 启停 ──
Console.WriteLine("\n[8] 启用/禁用");
scan = manager.Scan();
if (scan.Mods.Count > 0)
{
    var first = scan.Mods[0];
    Console.WriteLine($"    目标: {first.DisplayName}  当前启用={first.Enabled}");

    var disabled = manager.Disable(first);
    Console.WriteLine($"    Disable() => {disabled}");

    var rescan = manager.Scan();
    var after = rescan.Mods.First(m => m.Info?.Id == first.Info?.Id);
    Console.WriteLine($"    重扫后 启用={after.Enabled}  文件名={after.FileName}");

    var enabled = manager.Enable(after);
    Console.WriteLine($"    Enable() => {enabled}");

    rescan = manager.Scan();
    var final = rescan.Mods.First(m => m.Info?.Id == first.Info?.Id);
    Console.WriteLine($"    恢复后 启用={final.Enabled}  文件名={final.FileName}");
}
else Console.WriteLine("    没有 Mod 可测");

// ── 9. 重复安装检测 ──
Console.WriteLine("\n[9] 重复安装检测");
if (scan.Mods.Count > 0)
{
    var src = scan.Mods[0].Path;
    // 让源文件是启用态版本
    if (src.EndsWith(".disabled")) src = src[..^".disabled".Length];

    if (File.Exists(src))
    {
        try
        {
            manager.InstallFromFile(src);
            Console.WriteLine("    ✗ 应该抛「已装过」但没有");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"    ✓ 正确拦截: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ? 抛了别的异常: {ex.GetType().Name}: {ex.Message}");
        }
    }
    else Console.WriteLine($"    源文件不存在: {src}");
}

// ── 10. 重名安装加序号 ──
Console.WriteLine("\n[10] 同名不同内容 → 加序号");
var tempJar = Path.Combine(Path.GetTempPath(), "fake-mod-for-test.jar");
File.WriteAllBytes(tempJar, "not a real jar"u8.ToArray());

try
{
    var p1 = manager.InstallFromFile(tempJar);
    Console.WriteLine($"    第一次: {Path.GetFileName(p1)}");

    // 改内容再装，触发重名分支
    File.WriteAllBytes(tempJar, "different content"u8.ToArray());
    var p2 = manager.InstallFromFile(tempJar);
    Console.WriteLine($"    第二次: {Path.GetFileName(p2)}");

    // 清理测试垃圾
    File.Delete(p1); File.Delete(p2); File.Delete(tempJar);
    Console.WriteLine("    已清理测试文件");
}
catch (Exception ex)
{
    Console.WriteLine($"    ✗ {ex.GetType().Name}: {ex.Message}");
    if (File.Exists(tempJar)) File.Delete(tempJar);
}

// ── 11. 自动补齐依赖 ──
Console.WriteLine("\n[11] 自动补齐依赖（联网）");
var resolver = new ModDependencyResolver(manager, new ModDownloader());
var report = await resolver.ResolveAsync("1.21.1", "fabric",
    new Progress<string>(s => Console.WriteLine($"    · {s}")));

Console.WriteLine($"    总结: {report.Summary}");
if (report.Installed.Count > 0) Console.WriteLine($"    新装: {string.Join(", ", report.Installed)}");
if (report.Transitive.Count > 0) Console.WriteLine($"    传递依赖: {string.Join(", ", report.Transitive)}");
if (report.NotFound.Count > 0) Console.WriteLine($"    找不到: {string.Join(", ", report.NotFound)}");
if (report.Failed.Count > 0) Console.WriteLine($"    失败: {string.Join(", ", report.Failed)}");

// ── 12. 最终状态 ──
Console.WriteLine("\n[12] 最终 mods 目录");
var finalScan = manager.Scan();
Console.WriteLine($"    共 {finalScan.Mods.Count} 个文件");
foreach (var m in finalScan.Mods)
    Console.WriteLine($"    {(m.Enabled ? "[✓]" : "[ ]")} {m.DisplayName}  {m.Info?.Id ?? "?"}  {m.Info?.Version ?? ""}");

return pass == cases.Length ? 0 : 1;