using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PclLike.Core.Content;

namespace PclLike.Gui;

/// <summary>
/// 下载页「整合包」栏的安装链路探针：PclLike.exe --content-pack [关键字]
///   [--version 1.20.1] [--loader fabric] [--instance 名字]
///
/// 为什么单独做：拖放走的是 <see cref="MainViewModel.DropFilesAsync"/>，下载页走的是
/// <see cref="ContentViewModel"/> —— 搜索 → 挑版本 → 下载 → ModPackInstaller 铺文件 →
/// 再补齐加载器环境。两条路的后半段是同一批 Core 函数，所以出问题时必须能分清
/// 是哪一段炸的，而不是笼统地说"整合包装不上"。
///
/// 它会真下载真落盘（几十 MB 到几百 MB），并且在当前实例上动土 ——
/// 所以默认用一个专门的实例名，不去碰用户正在玩的实例。
/// </summary>
internal static class ContentProbe
{
    /// <summary>
    /// 等一个 Task 完成，但**不能阻塞 UI 线程**。
    ///
    /// ContentViewModel 里的 await 默认会回到 UI 线程；在 UI 线程上
    /// 调 .GetAwaiter().GetResult() 等于把自己等死 —— 续体排在当前线程的
    /// 消息队列里，而当前线程正卡在这一行。这个坑在 LobbyViewModel.ScanLanAsync
    /// 上已经踩过一次（那次也是探针，启动器本身没事）。
    ///
    /// 所以边等边给消息泵抽空队列。
    /// </summary>
    private static void WaitPumped(Task task)
    {
        while (!task.IsCompleted)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();   // 有异常就在这儿抛出来
    }

    public static int Run(string[] args)
    {
        // 重定向时子进程没有控制台，Console.OutputEncoding 会抛异常被吃掉，
        // 报告会按 GBK 写出去（中文全乱）。所以自己钉死 UTF-8 字节。
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        void Say(string line = "") => stdout.WriteLine(line);

        var keyword = "Fabulously Optimized";
        var version = "1.20.1";
        var loader = "";
        var instName = "下载页整合包测试";

        for (var i = 1; i < args.Length; i++)   // args[0] == "--content-pack"
        {
            switch (args[i])
            {
                case "--version":
                    if (i + 1 < args.Length) version = args[++i];
                    break;
                case "--loader":
                    if (i + 1 < args.Length) loader = args[++i];
                    break;
                case "--instance":
                    if (i + 1 < args.Length) instName = args[++i];
                    break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal)) keyword = args[i];
                    break;
            }
        }

        var app = new App();
        app.InitializeComponent();
        if (System.Windows.Application.Current is null) _ = app;

        var win = new MainWindow();
        var vm = win.ViewModelForShot();

        Say("── A. 准备当前实例 ──");

        var inst = vm.Store.LoadAll().FirstOrDefault(x => x.Name == instName);

        if (inst is null)
        {
            inst = vm.CreateInstance(instName, version, isolated: true);

            if (inst is null)
            {
                Say($"    建不出实例「{instName}」—— 本机没有版本 {version}？");
                return 3;
            }

            Say($"    新建：{inst.Name} → {inst.VersionId}（独立目录）");
        }
        else
        {
            Say($"    复用：{inst.Name} → {inst.VersionId}");
        }

        vm.SelectedInstance = inst;

        // 走的就是下载页那套 VM，连切栏、版本、加载器都照用户的操作来一遍
        var cv = vm.Content;
        cv.Activate(ContentTab.ModPack, autoSearch: false);
        cv.McVersion = version;
        if (loader.Length > 0) cv.Loader = loader;
        cv.SearchQuery = keyword;

        Say();
        Say($"── B. 搜索「{keyword}」（版本 {version}）──");

        WaitPumped(cv.SearchAsync());

        Say($"    {cv.StatusMessage}");
        Say($"    命中 {cv.Results.Count} 条");

        foreach (var r in cv.Results.Take(8))
            Say($"      {r.Title}  [{r.ProjectType}]  {r.SubtitleText}");

        var row = cv.Results.FirstOrDefault(r => r.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                  ?? cv.Results.FirstOrDefault();

        if (row is null)
        {
            Say("    没搜到可装的项目 —— 这一步就没法继续了");
            return 4;
        }

        Say();
        Say($"── C. 安装「{row.Title}」（下载页那颗「安装」按钮干的事）──");

        var modsDir = Path.Combine(inst.GameDir, "mods");
        var before = Directory.Exists(modsDir) ? Directory.GetFiles(modsDir).Length : 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        WaitPumped(cv.InstallAsync(row));
        sw.Stop();

        var after = Directory.Exists(modsDir) ? Directory.GetFiles(modsDir).Length : 0;

        Say($"    结果：{cv.StatusMessage}");
        Say($"    耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟");
        Say($"    mods/ 里的文件：{before} → {after}");

        var cfgDir = Path.Combine(inst.GameDir, "config");
        if (Directory.Exists(cfgDir))
            Say($"    config/ 里的文件：{Directory.GetFiles(cfgDir, "*", SearchOption.AllDirectories).Length}");

        Say();
        Say("── D. 装完之后 ──");

        var fresh = vm.Store.LoadAll().FirstOrDefault(x => x.Name == instName);
        Say($"    实例：{fresh?.Name} → {fresh?.VersionId}  {(fresh?.Isolated == true ? "独立" : "共享")}");
        Say($"    游戏目录：{inst.GameDir}");

        var loaderJson = Path.Combine(vm.GameRoot, "versions", fresh?.VersionId ?? "", (fresh?.VersionId ?? "") + ".json");
        Say($"    版本 json 在不在：{File.Exists(loaderJson)}  {loaderJson}");

        return 0;
    }
}