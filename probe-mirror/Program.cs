using System.Diagnostics;
using System.Text.Json;
using PclLike.Core.Net;
using PclLike.Core.Storage;

// 下载源（BMCLAPI）验证：先证明地址改对了，再证明速度真的快了。
//
// 为什么用 20 秒上限而不是"下完为止"：官方源今天在这条线路上大约 45 KB/s，
// 1.20.1 的客户端 jar 有 23.9 MB —— 下完要九分钟，没法当回归测试用。
// 所以两边都只跑 20 秒，看这 20 秒里到了多少字节。

Console.OutputEncoding = System.Text.Encoding.UTF8;

var root = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".pcllike");
var versionJson = Path.Combine(root, "versions", "1.20.1", "1.20.1.json");

if (!File.Exists(versionJson))
{
    Console.WriteLine($"找不到 {versionJson}，先装一次 1.20.1 再跑这个探针。");
    return 1;
}

using var doc = JsonDocument.Parse(File.ReadAllText(versionJson));
var client = doc.RootElement.GetProperty("downloads").GetProperty("client");
var officialUrl = client.GetProperty("url").GetString()!;
var sha1 = client.TryGetProperty("sha1", out var s) ? s.GetString() : null;
var size = client.TryGetProperty("size", out var z) ? z.GetInt64() : 0;

Console.WriteLine("── 1. 地址改写（不需要网络就能验）──");
MirrorSource.Current = MirrorKind.Bmclapi;

var samples = new[]
{
    "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
    "https://piston-meta.mojang.com/v1/packages/aaaa/1.20.1.json",
    officialUrl,
    "https://resources.download.minecraft.net/ab/abcdef1234567890abcdef1234567890abcdef12",
    "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar",
    "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.15.0/fabric-loader-0.15.0.jar",
};

foreach (var u in samples)
{
    var rewritten = MirrorSource.Rewrite(u);
    // 客户端 jar 那种 /objects/<sha1>/client.jar 靠 sha1 反查不出版本 id，
    // 只能由调用方带着 id 走进 ClientJarUrl —— 这里模拟一下调用方。
    if (u == officialUrl) rewritten = MirrorSource.ClientJarUrl(u, "1.20.1");

    var changed = rewritten != u ? "改写" : "原样";
    Console.WriteLine($"  [{changed}] {u}");
    if (changed == "改写") Console.WriteLine($"           -> {rewritten}");
}

Console.WriteLine();
Console.WriteLine($"── 2. 速度对比（各跑 20 秒，目标 {size / 1024.0 / 1024.0:0.0} MB）──");

var tmpRoot = Path.Combine(Path.GetTempPath(), "pclike-mirror-probe");
Directory.CreateDirectory(tmpRoot);

foreach (var (kind, label) in new (MirrorKind, string)[] { (MirrorKind.Official, "官方"), (MirrorKind.Bmclapi, "BMCLAPI") })
{
    MirrorSource.Current = kind;

    var url = kind == MirrorKind.Official ? officialUrl : MirrorSource.ClientJarUrl(officialUrl, "1.20.1");
    var dest = Path.Combine(tmpRoot, $"{kind}.jar");
    if (File.Exists(dest)) File.Delete(dest);

    using var downloader = new Downloader(maxConcurrency: 4, maxRetries: 1);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

    var sw = Stopwatch.StartNew();
    long got = 0;
    string? err = null;

    try
    {
        await downloader.DownloadOneAsync(new Downloader.DownloadTask(url, dest, null, 0), cts.Token);
    }
    catch (OperationCanceledException)
    {
        // 到点就停 —— 这是预期的，不是失败
    }
    catch (Exception ex)
    {
        err = ex.Message;
    }

    sw.Stop();
    var tmp = dest + ".tmp";
    if (File.Exists(tmp)) got = new FileInfo(tmp).Length;
    else if (File.Exists(dest)) got = new FileInfo(dest).Length;

    var kbs = got / 1024.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
    Console.WriteLine($"  {label,-9} 20 秒拿到 {got / 1024.0 / 1024.0:0.00} MB  ≈ {kbs:0} KB/s" +
                      (err is null ? "" : $"   （错误：{err}）"));
}

Console.WriteLine();
Console.WriteLine("（结论：同样 20 秒，谁拿到的字节多就是谁快。）");

// ── 3. 小文件对比：真正决定"装原版要等多久"的是几千个材质小文件 ──
// 单个大文件两边都能跑满带宽，测不出差别；小文件拼的是每请求的延迟与并发，
// 这才是资源服务器在国内慢的真正原因。
Console.WriteLine();
Console.WriteLine("── 3. 材质小文件对比（30 个，并发 8）──");

var indexDir = Path.Combine(root, "assets", "indexes");
var indexPath = Directory.Exists(indexDir)
    ? Directory.GetFiles(indexDir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
    : null;

if (indexPath is null)
{
    Console.WriteLine($"  跳过：{indexDir} 下没有索引 json");
    return 0;
}

Console.WriteLine($"  用索引 {Path.GetFileName(indexPath)}");

using var indexDoc = JsonDocument.Parse(File.ReadAllText(indexPath));
var objects = indexDoc.RootElement.GetProperty("objects").EnumerateObject().Take(30).ToList();
Console.WriteLine($"  取 {objects.Count} 个对象（每个几十 KB 到几百 KB）");

foreach (var (kind, label) in new (MirrorKind, string)[] { (MirrorKind.Official, "官方"), (MirrorKind.Bmclapi, "BMCLAPI") })
{
    MirrorSource.Current = kind;

    var dir = Path.Combine(tmpRoot, $"assets-{kind}");
    Directory.CreateDirectory(dir);

    var tasks = objects.Select(o =>
    {
        var hash = o.Value.GetProperty("hash").GetString()!;
        var sub = hash[..2];
        var url = $"https://resources.download.minecraft.net/{sub}/{hash}";
        return new Downloader.DownloadTask(url, Path.Combine(dir, hash), hash);
    }).ToList();

    using var downloader = new Downloader(maxConcurrency: 8, maxRetries: 1);
    var sw = Stopwatch.StartNew();
    var results = await downloader.DownloadAllAsync(tasks);
    sw.Stop();

    var ok = results.Count(r => r.Ok);
    var mb = results.Where(r => r.Ok).Sum(r => r.Bytes) / 1024.0 / 1024.0;
    Console.WriteLine($"  {label,-9} {ok}/{tasks.Count} 成功，{mb:0.00} MB，用时 {sw.Elapsed.TotalSeconds:0.0} 秒" +
                      $"  ≈ {mb * 1024 / Math.Max(sw.Elapsed.TotalSeconds, 0.1):0} KB/s");
}

Console.WriteLine();
Console.WriteLine("（这才是装原版时你能感受到的那个快慢。）");
return 0;