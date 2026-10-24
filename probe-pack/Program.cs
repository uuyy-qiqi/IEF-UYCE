using PclLike.Core.Content;
using PclLike.Core.Mods;
using PclLike.Core.Storage;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 整合包"真装"验证。
//
// 为什么要探针而不是看界面：界面上只能看到一句"成功 N / 失败 M"。
// 这里要回答的是更硬的问题 ——
//   A. ReadPlan 读出来的清单跟包里的 modrinth.index.json 是否一致
//   B. InstallAsync 之后，清单里每一个 path 是否真的躺在了实例目录里
//      （不是"没报错"，而是逐个文件去 File.Exists）
//   C. overrides / client-overrides 是否真的铺开、且 client-overrides 覆盖了同名文件
//   D. 这个包要求的 Minecraft + 加载器，本机 già装了没有 ——
//      没装的话"装完了"这个包照样打不开，那是这个流程真正的缺口

const string GameRoot = @"C:\Users\yuwei\AppData\Roaming\.pcllike";

var url = "";
var local = "";
var go = false;
var keep = false;
var instName = "整合包测试";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--go": go = true; break;
        case "--keep": keep = true; break;
        case "--url": url = args[++i]; break;
        case "--file": local = args[++i]; break;
        case "--instance": instName = args[++i]; break;
    }
}

if (url.Length == 0 && local.Length == 0)
{
    Console.WriteLine("用法：probe-pack --url <mrpack 地址> [--go] [--instance 名字]");
    Console.WriteLine("      probe-pack --file <本地 mrpack> [--go]");
    return 1;
}

var fails = new List<string>();

void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine(ok ? $"    ✓ {what}" : $"    ✗ {what}  {detail}");
    if (!ok) fails.Add(what);
}

// ─────────── 取包 ───────────
var cache = @"C:\Users\yuwei\Documents\pcl2-like\.cache";
Directory.CreateDirectory(cache);

var downloader = new ModDownloader();

var path = local;
if (path.Length == 0)
{
    path = Path.Combine(cache, "pack-" + Guid.NewGuid().ToString("N")[..8] + ".mrpack");

    Console.WriteLine("── 下载整合包 ──");
    Console.WriteLine($"    {url}");

    // 必须走启动器自己的下载器，不能图省事用裸 HttpClient：
    // 本机到 cdn.modrinth.com 的 DNS 常常只回一个死节点，裸 HttpClient 直接
    // "连接尝试失败"（实测四个候选包全挂），而这个下载器带候选轮换与跨轮重解析。
    var packCand = new ModDownloadCandidate
    {
        Platform = "modrinth",
        ProjectId = "",
        VersionId = "",
        Name = Path.GetFileName(url),
        Version = "",
        FileName = Path.GetFileName(Uri.UnescapeDataString(new Uri(url).AbsolutePath)),
        DownloadUrl = url,
        SizeBytes = 0,
        Sha1 = null,
    };

    var swDl = System.Diagnostics.Stopwatch.StartNew();

    var got = await downloader.DownloadAsync(
        packCand, path,
        new Progress<DownloadProgress>(p => Console.WriteLine($"      {p.Text}")),
        CancellationToken.None);

    if (!got || !File.Exists(path))
    {
        Console.WriteLine("    ✗ 下载失败（下载器重试也没成功）");
        return 2;
    }

    var bytes = new FileInfo(path).Length;
    Console.WriteLine($"    {bytes / 1024.0 / 1024.0:0.##} MB，{swDl.Elapsed.TotalSeconds:0.#} 秒");
}

Console.WriteLine();
Console.WriteLine("── A. ReadPlan（只读清单，不下任何东西）──");

var plan = ModPackInstaller.ReadPlan(path);

if (plan is null)
{
    Console.WriteLine("    ✗ 读不出清单 —— 这个文件不是合法 .mrpack");
    return 3;
}

var totalBytes = plan.Files.Sum(f => f.SizeBytes);
Console.WriteLine($"    名字      : {plan.Name}");
Console.WriteLine($"    Minecraft : {plan.McVersion}");
Console.WriteLine($"    加载器    : {plan.LoaderName ?? "（无，纯原版）"} {plan.LoaderVersion}");
Console.WriteLine($"    文件      : {plan.Files.Count} 个，合计 {totalBytes / 1024.0 / 1024.0:0.#} MB");
Console.WriteLine($"    带覆盖文件: {(plan.HasOverrides ? "是" : "否")}");

Console.WriteLine("    最大的 5 个：");
foreach (var f in plan.Files.OrderByDescending(f => f.SizeBytes).Take(5))
    Console.WriteLine($"      {f.SizeBytes / 1024.0 / 1024.0,7:0.0} MB  {f.Path}");

Check("清单有名字", plan.Name.Length > 0);
Check("清单有 Minecraft 版本", plan.McVersion.Length > 0);
Check("清单里有文件", plan.Files.Count > 0);
Check("每个文件都有下载地址", plan.Files.All(f => f.Url.Length > 0),
      $"缺地址 {plan.Files.Count(f => f.Url.Length == 0)} 个");
Check("每个文件的 sha1 都给了", plan.Files.All(f => f.Sha1 is not null),
      $"缺 sha1 {plan.Files.Count(f => f.Sha1 is null)} 个");

if (!go)
{
    Console.WriteLine();
    Console.WriteLine("（只读模式。加 --go 才会真的装。）");
    return fails.Count == 0 ? 0 : 1;
}

// ─────────── 建独立实例 ───────────
Console.WriteLine();
Console.WriteLine("── B. 建一个干净的独立实例 ──");

var store = new InstanceStore(GameRoot);

var existing = store.LoadAll().FirstOrDefault(i => i.Name == instName);
if (existing is not null && !keep)
{
    Console.WriteLine($"    先删掉上次留下的「{instName}」");
    store.Delete(existing.Id, deleteFiles: true);
}
else if (existing is not null)
{
    Console.WriteLine($"    --keep：复用已有的「{instName}」，不删");
}

var inst = existing is not null && keep
    ? existing
    : store.Create(instName, plan.McVersion, isolated: true);
Console.WriteLine($"    id      : {inst.Id}");
Console.WriteLine($"    gameDir : {inst.GameDir}");
Check("实例目录已创建", Directory.Exists(inst.GameDir));

// ─────────── 真装 ───────────
Console.WriteLine();
Console.WriteLine("── C. InstallAsync（真的下载 + 铺 overrides）──");

var sw = System.Diagnostics.Stopwatch.StartNew();

var result = await ModPackInstaller.InstallAsync(
    path,
    inst,
    Fetch,
    new Progress<string>(s =>
    {
        // 只打关键节点，不然几百行刷屏
        if (s.StartsWith("整合包") || s.StartsWith("已覆盖") || s.StartsWith("成功"))
            Console.WriteLine($"    {s}");
    }));

Console.WriteLine($"    耗时 {sw.Elapsed.TotalSeconds:0.#} 秒");
Console.WriteLine($"    结果：{(result.Ok ? "成功" : "失败")} —— {result.Message}");

async Task<bool> Fetch(ModPackFile file, string destPath, CancellationToken ct)
{
    var cand = new ModDownloadCandidate
    {
        Platform = "modrinth",
        ProjectId = "",
        VersionId = "",
        Name = file.FileName,
        Version = "",
        FileName = file.FileName,
        DownloadUrl = file.Url,
        SizeBytes = file.SizeBytes,
        Sha1 = file.Sha1,
    };

    return await downloader.DownloadAsync(cand, destPath, null, ct);
}

// ─────────── 逐个文件核对 ───────────
Console.WriteLine();
Console.WriteLine("── D. 落盘核对（不靠「没报错」，一个一个 File.Exists）──");

var missing = new List<string>();
foreach (var f in plan.Files)
{
    var dest = ContentInstaller.ResolveInside(inst.GameDir, f.Path);
    if (dest is null || !File.Exists(dest)) missing.Add(f.Path);
}

var landed = plan.Files.Count - missing.Count;
Console.WriteLine($"    清单文件落盘：{landed} / {plan.Files.Count}");
Check("清单里每个文件都落地了", missing.Count == 0,
      missing.Count > 0 ? $"缺 {missing.Count} 个，例如 {string.Join("、", missing.Take(3))}" : "");

Console.WriteLine("    落盘的目录：");
foreach (var dir in plan.Files
             .Select(f => f.Path.Contains('/') ? f.Path[..f.Path.IndexOf('/')] : "(根)")
             .Distinct())
{
    var full = Path.Combine(inst.GameDir, dir == "(根)" ? "" : dir);
    if (!Directory.Exists(full)) continue;
    var files = Directory.GetFiles(full, "*", SearchOption.AllDirectories);
    Console.WriteLine($"      {dir,-14} {files.Length,4} 个文件，{files.Sum(p => new FileInfo(p).Length) / 1024.0 / 1024.0,7:0.0} MB");
}

// overrides 落盘了吗（拿 config/ 当代表）
var configDir = Path.Combine(inst.GameDir, "config");
var overrideFiles = Directory.Exists(configDir)
    ? Directory.GetFiles(configDir, "*", SearchOption.AllDirectories).Length
    : 0;
Console.WriteLine($"    overrides 里的 config/ 文件：{overrideFiles} 个");
if (plan.HasOverrides) Check("包里有 overrides 且铺开了", overrideFiles > 0);

// ─────────── 缺的那一环：版本与加载器 ───────────
Console.WriteLine();
Console.WriteLine("── E. 这个包要求的版本 / 加载器，本机装了吗 ──");

var versionsDir = Path.Combine(GameRoot, "versions");
var installed = Directory.Exists(versionsDir)
    ? Directory.GetDirectories(versionsDir).Select(Path.GetFileName).ToList()
    : [];

string wanted;
if (plan.LoaderName is null or "vanilla")
    wanted = plan.McVersion;
else if (plan.LoaderName == "fabric")
    wanted = $"fabric-loader-{plan.LoaderVersion}-{plan.McVersion}";
else if (plan.LoaderName == "quilt")
    wanted = $"quilt-loader-{plan.LoaderVersion}-{plan.McVersion}";
else if (plan.LoaderName == "neoforge")
    wanted = $"neoforge-{plan.LoaderVersion}";
else
    wanted = $"{plan.McVersion}-{plan.LoaderName}-{plan.LoaderVersion}";

var hasIt = installed.Any(v => v.Equals(wanted, StringComparison.OrdinalIgnoreCase));
var hasMc = installed.Any(v => v.Equals(plan.McVersion, StringComparison.OrdinalIgnoreCase));

Console.WriteLine($"    需要 : {wanted}");
Console.WriteLine($"    已装的版本：{string.Join("、", installed)}");
Console.WriteLine($"    原版 {plan.McVersion}：{(hasMc ? "有" : "没有")}");
Console.WriteLine($"    目标版本：{(hasIt ? "有" : "没有")}");

if (!hasIt)
{
    Console.WriteLine();
    Console.WriteLine("    ⚠ 这就是「装完整合包但打不开游戏」的缺口：");
    Console.WriteLine("      装完 Mod 和配置，启动器并没有顺手把包要求的 Minecraft + 加载器装上，");
    Console.WriteLine("      实例的 VersionId 也还停在原来那个版本上。");
}

Console.WriteLine();
Console.WriteLine($"整合包装在：{inst.GameDir}");
Console.WriteLine($"结论：{(fails.Count == 0 ? "全部通过" : $"失败 {fails.Count} 项：{string.Join("；", fails)}")}");

return fails.Count == 0 ? 0 : 1;