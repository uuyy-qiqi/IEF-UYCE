// InstallerProbe —— 走界面同一条路（InstallerViewModel）验收「安装包」那一栏。
//
//   PclLike.exe --installer <key> [mc版本]
//   key = fabric | quilt | forge | neoforge | optifine | legacyfabric
//
// 为什么不能只靠 probe-loader：probe-loader 直接调 LoaderInstaller，验的是 Core。
// 这一栏真正的交付物是"界面上点一下会发生什么" —— 包括 OptiFine 装完要把 jar
// 落进实例 mods 目录这种只有 VM 才管的事。所以这里用 VM。
//
// 会建一个自己的隔离实例「安装器测试」，装完把它里面真的落了什么打出来。

using System.IO;
using PclLike.Core.Storage;

namespace PclLike.Gui;

internal static class InstallerProbe
{
    public static async Task<int> Run(string[] args)
    {
        // 注意：这里绝对不能碰 Console.OutputEncoding。
        // PclLike.exe 是窗口子系统程序，被重定向输出时没有控制台句柄 ——
        // 设它会抛 "句柄无效"，而且就算 catch 住了，控制台编码也已经被弄坏，
        // 后面所有中文都会变成 '?'。同项目其他探针都没碰它，中文都是好好的。

        var rest = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var listOnly = args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase));
        var key = rest.Length > 0 ? rest[0].ToLowerInvariant() : "fabric";
        var mc = rest.Length > 1 ? rest[1] : "1.20.1";

        // 第三个参数可以钉死具体的加载器版本（整合包要复现 forge 47.4.0 这种场景）
        var want = rest.Length > 2 ? rest[2] : null;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".pcllike");

        Console.WriteLine($"root = {root}");
        Console.WriteLine($"目标 = {key} / Minecraft {mc}");
        Console.WriteLine();

        var store = new InstanceStore(root);
        var inst = store.LoadAll().FirstOrDefault(i => i.Name == "安装器测试")
                   ?? store.Create("安装器测试", mc, isolated: true);

        Console.WriteLine($"实例 = {inst.Name}（隔离={inst.Isolated}）");
        Console.WriteLine($"       {inst.GameDir}");
        Console.WriteLine();

        var vm = new InstallerViewModel(
            () => inst,
            (kind, text) => Console.WriteLine($"   [{kind}] {text}"),
            root);

        // 命令行给的 MC 版本要显式塞进去：界面上这个值是从当前实例同步过来的，
        // 探针不设它就会拿实例的版本去查（比如拿 1.20.1 去查 LiteLoader 的列表，
        // 结果永远是“这个版本还不支持它”）。
        vm.McVersion = mc;

        vm.Activate(key);
        Console.WriteLine($"标题 = {vm.Title}");
        Console.WriteLine($"说明 = {vm.HintText}");
        Console.WriteLine();

        // Activate 自己就会去查一次（界面上点页签就是这么用的）。上一版探针在这里
        // 又调了一遍 RefreshAsync，结果撞上 IsBusy 直接返回，打印出来永远停在
        // 「正在查……」，白折腾一轮。所以这里只等它查完。
        var guard = 0;
        while (vm.IsBusy && guard++ < 900) await Task.Delay(100);
        if (vm.Versions.Count == 0 && !vm.IsBusy) await vm.RefreshAsync();

        Console.WriteLine($"✓ 列版本结束，共 {vm.Versions.Count} 个");
        Console.WriteLine($"  状态 = 「{vm.StatusText}」");

        if (vm.Versions.Count == 0)
        {
            Console.WriteLine("✗ 没有可装的版本");
            return 1;
        }

        // 只是想看看列表长什么样（验收文案/排序）时用 --list，别每次都真装一遍
        if (listOnly)
        {
            foreach (var v in vm.Versions.Take(8))
                Console.WriteLine($"    · 名称=「{v.Name}」 副标题=「{v.Subtitle}」 → {v.ResultId} 最新={v.LatestText}");
            return 0;
        }

        // 指定版本但列表里没有 → 走 Core 直接装那一个。
        //
        // Forge 的列表是"每个 MC 版本只给最新版"（promotions 文件就长这样），
        // 可整合包要的往往正是某个具体的旧版 —— CursePack 那条路就是直接调
        // LoaderInstaller 装指定版本的，这里跟着同一条路复现。
        var exact = want is null
            ? null
            : vm.Versions.FirstOrDefault(v => v.Version.Version.Contains(want, StringComparison.OrdinalIgnoreCase));

        if (want is not null && exact is null)
        {
            Console.WriteLine($"列表里没有 {want}（只有 {string.Join("、", vm.Versions.Select(v => v.Version.Version))}）");
            Console.WriteLine($"→ 直接按指定版本装：{key} {mc}-{want}");

            var kind = key switch
            {
                "neoforge" => PclLike.Core.Minecraft.Loaders.LoaderKind.NeoForge,
                "cleanroom" => PclLike.Core.Minecraft.Loaders.LoaderKind.Cleanroom,
                "fabric" => PclLike.Core.Minecraft.Loaders.LoaderKind.Fabric,
                "quilt" => PclLike.Core.Minecraft.Loaders.LoaderKind.Quilt,
                "legacyfabric" => PclLike.Core.Minecraft.Loaders.LoaderKind.LegacyFabric,
                "liteloader" => PclLike.Core.Minecraft.Loaders.LoaderKind.LiteLoader,
                "optifine" => PclLike.Core.Minecraft.Loaders.LoaderKind.OptiFine,
                _ => PclLike.Core.Minecraft.Loaders.LoaderKind.Forge,
            };

            var direct = new PclLike.Core.Minecraft.LoaderInstaller(root);
            var swd = System.Diagnostics.Stopwatch.StartNew();
            var res = await direct.InstallAsync(kind, mc, want,
                progress: null, onLog: t => Console.WriteLine($"   {t}"));
            swd.Stop();

            Console.WriteLine($"✓ 装完 {swd.Elapsed.TotalSeconds:0.#}s → {res.VersionId}");
            Console.WriteLine($"  版本目录存在 = {Directory.Exists(Path.Combine(root, "versions", res.VersionId))}");
            foreach (var w in res.Warnings) Console.WriteLine($"  警告：{w}");
            PrintFmlLibraries(root);
            return 0;
        }

        var row = exact ?? vm.Versions.FirstOrDefault(v => v.Version.IsStable) ?? vm.Versions[0];
        Console.WriteLine($"选中 = {row.Name}  → {row.ResultId}");
        Console.WriteLine();

        // ── 真装 ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await vm.InstallAsync(row);
        Console.WriteLine();
        Console.WriteLine($"✓ 装完 {sw.Elapsed.TotalSeconds:0.#}s");
        Console.WriteLine($"  状态 = 「{vm.StatusText}」");
        Console.WriteLine();

        // ── 落盘核对 ──
        Console.WriteLine($"实例版本 = {inst.VersionId}");

        var vdir = Path.Combine(root, "versions", inst.VersionId);
        Console.WriteLine($"版本目录 = {vdir}  存在={Directory.Exists(vdir)}");
        if (Directory.Exists(vdir))
            foreach (var f in Directory.GetFiles(vdir))
                Console.WriteLine($"    · {Path.GetFileName(f),-46} {new FileInfo(f).Length / 1024.0 / 1024.0:0.#} MB");

        var modsDir = Path.Combine(inst.GameDir, "mods");
        Console.WriteLine($"mods 目录 = {modsDir}");
        if (Directory.Exists(modsDir))
        {
            var jars = Directory.GetFiles(modsDir);
            Console.WriteLine($"    共 {jars.Length} 个文件");
            foreach (var f in jars)
                Console.WriteLine($"    · {Path.GetFileName(f),-46} {new FileInfo(f).Length / 1024.0 / 1024.0:0.#} MB");
        }
        else
        {
            Console.WriteLine("    （没有 mods 目录）");
        }

        // ── install_profile 的库有没有真的落到 libraries/ ──
        //
        // 这一串（fmlcore / *language / forge:universal）**不在**版本 json 里，
        // 官方安装器也是把它们直接铺进 libraries/ 的。少了 fmlcore，游戏会在
        // 启动第一阶段炸 Invalid paths argument, contained no existing paths。
        PrintFmlLibraries(root);

        return 0;
    }

    /// <summary>把 install_profile 那串"必须在盘上"的库列出来。</summary>
    private static void PrintFmlLibraries(string root)
    {
        Console.WriteLine();
        Console.WriteLine("── install_profile 的库（必须在盘上）──");

        var fmlRoot = Path.Combine(root, "libraries", "net", "minecraftforge");

        foreach (var art in new[] { "fmlcore", "javafmllanguage", "lowcodelanguage", "mclanguage", "fmlloader" })
        {
            var d = Path.Combine(fmlRoot, art);
            var jars = Directory.Exists(d) ? Directory.GetFiles(d, "*.jar", SearchOption.AllDirectories) : [];
            Console.WriteLine($"    {art,-18} {(jars.Length == 0 ? "—（缺）" : string.Join(" | ", jars.Select(Path.GetFileName)))}");
        }

        var forgeDir = Path.Combine(fmlRoot, "forge");
        var uni = Directory.Exists(forgeDir) ? Directory.GetFiles(forgeDir, "*universal*.jar", SearchOption.AllDirectories) : [];
        Console.WriteLine($"    {"forge:universal",-18} {(uni.Length == 0 ? "—（缺）" : string.Join(" | ", uni.Select(Path.GetFileName)))}");
    }
}