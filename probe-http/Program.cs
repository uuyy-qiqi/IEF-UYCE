using PclLike.Core.Mods;
using PclLike.Core.Storage;

// 验证文件名修复：用 Guid 命名的临时文件下载，装进去必须仍是真实文件名
var root = Path.Combine(Path.GetTempPath(), "pclike-name");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(root);

var instDir = Path.Combine(root, "instances", "n1");
System.IO.Directory.CreateDirectory(instDir);
var inst = new GameInstance
{
    Id = "n1", Name = "命名测试", VersionId = "1.20.1", Loader = "fabric",
    Isolated = true, Directory = instDir, LauncherRoot = root,
};
System.IO.Directory.CreateDirectory(inst.GameDir);

var dl = new ModDownloader();
var mgr = new ModManager(inst);

Console.WriteLine("=== 模拟 GUI 安装流程：Guid 临时名 → 装进 mods ===");
foreach (var (slug, mc, loader) in new[]
{
    ("sodium", "1.20.1", "fabric"),
    ("iris",   "1.20.1", "fabric"),
    ("modmenu","1.20.1", "fabric"),
})
{
    var cand = await dl.FindModrinthByIdAsync(slug, mc, loader);
    if (cand is null) { Console.WriteLine($"  {slug}: 解析失败"); continue; }

    // GUI 用的就是这个命名方式
    var tmp = Path.Combine(Path.GetTempPath(), "pcllike-dl", $"{Guid.NewGuid():N}.jar");
    Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);

    if (!await dl.DownloadAsync(cand, tmp)) { Console.WriteLine($"  {slug}: 下载失败"); continue; }

    var landed = mgr.InstallFromFile(tmp, preferredFileName: cand.FileName);
    Console.WriteLine($"  下载名: {Path.GetFileName(tmp)}");
    Console.WriteLine($"  落盘名: {Path.GetFileName(landed)}");
    Console.WriteLine($"  期望名: {cand.FileName}");
    Console.WriteLine($"  正确  : {Path.GetFileName(landed) == cand.FileName}");
    Console.WriteLine();

    try { File.Delete(tmp); } catch { }
}

Console.WriteLine("=== mods 目录实际内容 ===");
foreach (var f in System.IO.Directory.GetFiles(mgr.ModsDirectory))
    Console.WriteLine($"  {Path.GetFileName(f)}");

Console.WriteLine("\n=== 扫描回读（名字与元数据是否都对上）===");
foreach (var m in mgr.Scan().Mods)
    Console.WriteLine($"  {m.Info?.Name,-14} {m.Info?.Version,-16} 文件={m.FileName}");

Console.WriteLine("\n=== 边界：带路径穿越的 preferredFileName 必须被剥干净 ===");
var evil = Path.Combine(Path.GetTempPath(), "pclike-dl", $"{Guid.NewGuid():N}.jar");
var cand2 = await dl.FindModrinthByIdAsync("sodium", "1.20.1", "fabric");
if (cand2 is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(evil)!);
    if (await dl.DownloadAsync(cand2, evil))
    {
        try
        {
            var r = mgr.InstallFromFile(evil, preferredFileName: @"..\..\evil-escape.jar");
            Console.WriteLine($"  落盘: {r}");
            var inside = Path.GetFullPath(r).StartsWith(Path.GetFullPath(mgr.ModsDirectory));
            Console.WriteLine($"  仍在 mods 目录内: {inside}  ← 必须为 True");
        }
        catch (Exception ex) { Console.WriteLine($"  异常: {ex.GetType().Name}: {ex.Message}"); }
        try { File.Delete(evil); } catch { }
    }
}
dl.Dispose();