// 搜索计时探针：查清「下载页整合包」那一步为什么卡住。
//
// 背景：ContentProbe 的 --content-pack 在「B. 搜索」那一步卡了 29 分钟没动，
// 而同一个搜索在 Mod 栏里是好的。这里把两种 projectType 分别计时，看是不是
// 我改 facets 的时候把 modpack 那条弄坏了。

using System.Diagnostics;
using PclLike.Core.Mods;

var cases = new (string Query, string? Mc, string? Loader, string Type)[]
{
    ("Fabulously Optimized", "1.20.1", "fabric", "modpack"),
    ("Fabulously Optimized", "1.20.1", "fabric", "mod"),
    ("sodium", "1.20.1", "fabric", "mod"),
};

var downloader = new ModDownloader();

foreach (var c in cases)
{
    Console.WriteLine($"── query=\"{c.Query}\" mc={c.Mc} loader={c.Loader} projectType={c.Type} ──");
    var sw = Stopwatch.StartNew();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

    try
    {
        var list = await downloader.SearchModrinthListAsync(
            c.Query, c.Mc, c.Loader, limit: 5, offset: 0, ct: cts.Token, projectType: c.Type);

        sw.Stop();
        Console.WriteLine($"    ✓ {sw.Elapsed.TotalSeconds:0.#} 秒，{list.Count} 条");
        foreach (var r in list.Take(3))
        {
            Console.WriteLine($"      · {r.Name}  [{r.ProjectType}]  {r.DownloadsText}");
        }
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"    ✗ {sw.Elapsed.TotalSeconds:0.#} 秒 —— {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("── 单独测 api.modrinth.com 的连通与响应 ──");
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
foreach (var url in new[]
{
    "https://api.modrinth.com/v2/search?query=sodium&limit=1",
    "https://api.modrinth.com/v2/search?query=sodium&limit=1&facets=%5B%5B%22project_type%3Amod%22%5D%5D",
    "https://api.modrinth.com/v2/search?query=sodium&limit=1&facets=%5B%5B%22project_type%3Amodpack%22%5D%2C%5B%22categories%3Afabric%22%5D%5D",
})
{
    var sw = Stopwatch.StartNew();
    try
    {
        using var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        sw.Stop();
        Console.WriteLine($"    {(int)resp.StatusCode}  {sw.Elapsed.TotalSeconds:0.##}s  {body.Length} 字节  ← {url[..Math.Min(72, url.Length)]}");
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"    ✗ {sw.Elapsed.TotalSeconds:0.##}s  {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("── 加载器版本列表（InstallerViewModel.RefreshAsync 走的同一条路）──");

var gameRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".pcllike");
var loaderInstaller = new PclLike.Core.Minecraft.LoaderInstaller(gameRoot);

foreach (var kind in new[]
{
    PclLike.Core.Minecraft.Loaders.LoaderKind.Fabric,
    PclLike.Core.Minecraft.Loaders.LoaderKind.Quilt,
    PclLike.Core.Minecraft.Loaders.LoaderKind.Forge,
    PclLike.Core.Minecraft.Loaders.LoaderKind.NeoForge,
})
{
    var sw = Stopwatch.StartNew();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try
    {
        var list = await loaderInstaller.ListAsync(kind, "1.20.1", cts.Token);
        sw.Stop();
        var stable = list.FirstOrDefault(v => v.IsStable);
        Console.WriteLine($"    {kind,-9} ✓ {sw.Elapsed.TotalSeconds:0.#}s  {list.Count,4} 个版本  最新稳定版 {stable?.Version ?? "（无）"}");
        foreach (var v in list.Take(2))
        {
            Console.WriteLine($"        · {v.DisplayName}  → {v.ResultVersionId}");
        }
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"    {kind,-9} ✗ {sw.Elapsed.TotalSeconds:0.#}s —— {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("结束。");