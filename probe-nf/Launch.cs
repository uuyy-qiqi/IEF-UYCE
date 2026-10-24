using PclLike.Core.Minecraft;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 用产品代码路径真实启动 NeoForge 1.20.1。
// 目的：验证 processor 链产出的 slim/srg/patched jar 是否真的让 FML 通过，
// 即不再报 `Invalid paths argument, contained no existing paths`。

const string GameRoot = @"C:\Users\yuwei\AppData\Roaming\.pcllike";
const string JavaPath = @"C:\Users\yuwei\.jdks\jdk-17.0.20.1+1\bin\java.exe";
const string VersionId = "neoforge-47.1.106";

Console.WriteLine("══════ NeoForge 1.20.1 真实启动 ══════");

using var svc = new LaunchService(GameRoot);

var opts = new LaunchService.LaunchOptions
{
    VersionId = VersionId,
    Username = "ProbePlayer",
    Uuid = "00000000000000000000000000000000",
    AccessToken = "0",
    MaxMemoryMb = 4096,
    MinMemoryMb = 512,
    JavaPath = JavaPath,
    SkipInstall = true,   // 版本已装好，直接启
};

Console.WriteLine("  ▸ 启动中（最多等 75 秒，能看到 Forge 加载完成即算成功）");
Console.WriteLine();

var logLines = new List<string>();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));

try
{
    var outcome = await svc.LaunchAsync(
        opts,
        progress: null,
        onLog: line =>
        {
            logLines.Add(line);
            Console.WriteLine("    " + line);
        },
        ct: cts.Token);

    Console.WriteLine();
    Console.WriteLine("  ── 结果 ──");
    Console.WriteLine($"    ExitCode={outcome.ExitCode}  Crashed={outcome.Crashed}");
    if (outcome.Reason is not null) Console.WriteLine($"    Reason={outcome.Reason}");
    Console.WriteLine($"    LogPath={outcome.LogPath}");
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("  ✓ 已跑满 75 秒未崩溃 → 判定启动成功");
    svc.KillGame();
}

Console.WriteLine();
Console.WriteLine("  ── 关键日志筛查 ──");
var bad = new[]
{
    "Invalid paths argument",
    "InaccessibleObjectException",
    "Caused by",
    "A fatal error",
    "Failed to locate library",
};
foreach (var pat in bad)
{
    var hit = logLines.FirstOrDefault(l => l.Contains(pat, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(hit is null ? $"      ✓ 未出现 \"{pat}\"" : $"      ✗ 命中 \"{pat}\"：{hit.Trim()}");
}

var good = new[]
{
    "Loading Minecraft",
    "BootstrapLauncher",
    "ModLauncher",
    "Forge",
};
Console.WriteLine("  ── 启动成功标志 ──");
foreach (var pat in good)
{
    var hit = logLines.FirstOrDefault(l => l.Contains(pat, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine(hit is null ? $"      · 未见 \"{pat}\"" : $"      ✓ 见 \"{pat}\"");
}

Console.WriteLine();
Console.WriteLine("══════ 结束 ══════");