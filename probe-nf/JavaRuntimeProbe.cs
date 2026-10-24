using PclLike.Core.Minecraft.Launch;

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string GameRoot = @"C:\Users\yuwei\AppData\Roaming\.pcllike";

Console.WriteLine("══════ Java 运行时自动下载测试 ══════");
Console.WriteLine();

var mgr = new JavaRuntimeManager(GameRoot);
Console.WriteLine($"平台标识: {JavaRuntimeManager.Platform}");
Console.WriteLine($"运行时根: {mgr.RuntimeRoot}");
Console.WriteLine();

Console.WriteLine("▸ 已安装的运行时：");
var installed = mgr.ListInstalled().ToList();
if (installed.Count == 0) Console.WriteLine("    （无）");
foreach (var (major, path, marker) in installed)
{
    Console.WriteLine($"    Java {major,-3} {marker?.FullVersion ?? "(无标记)",-22} {path}");
}
Console.WriteLine();

// 21 本机没有 —— 正好验证下载链路
const int Target = 21;
Console.WriteLine($"▸ 目标 Java {Target}（本机应该没有，走下载）");
Console.WriteLine($"    IsInstalled({Target}) = {mgr.IsInstalled(Target)}");
Console.WriteLine();

var sw = System.Diagnostics.Stopwatch.StartNew();
long lastPct = -1;

try
{
    var exe = await mgr.EnsureAsync(Target, new Progress<(string Stage, long Done, long Total)>(p =>
    {
        if (p.Total <= 0) return;
        var pct = p.Done * 100 / p.Total;
        // 只在整数百分比变化时打印，避免刷屏
        if (pct == lastPct) return;
        lastPct = pct;
        Console.WriteLine($"      {p.Stage}");
    }));

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine($"  ✓ 完成，用时 {sw.Elapsed.TotalSeconds:F1} 秒");
    Console.WriteLine($"    java.exe → {exe}");
    Console.WriteLine();

    // 探测下载下来的东西是不是真能跑
    var info = JavaLocator.Probe(exe);
    Console.WriteLine("▸ 探测下载下来的运行时：");
    Console.WriteLine(info is null
        ? "    ✗ 探测失败"
        : $"    ✓ Java {info.MajorVersion} 64bit={info.Is64Bit}\n      vendor={info.Vendor}\n      raw={info.RawVersion}");
}
catch (Exception ex)
{
    sw.Stop();
    Console.WriteLine();
    Console.WriteLine($"  ✗ 失败（{sw.Elapsed.TotalSeconds:F1} 秒）");
    Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("▸ 再次列出已安装：");
foreach (var (major, path, marker) in mgr.ListInstalled())
{
    Console.WriteLine($"    Java {major,-3} {marker?.FullVersion ?? "(无标记)",-22} {path}");
}

Console.WriteLine();
Console.WriteLine("══════ 结束 ══════");