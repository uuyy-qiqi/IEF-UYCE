using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace PclLike.Gui;

/// <summary>
/// 拖放探针：PclLike.exe --drop &lt;文件&gt; [文件...] [--install] [--instance 名字]
///
/// 为什么要有它：操作系统级的拖拽没法自动模拟（那要真的按下鼠标从资源管理器
/// 拖过桌面），但"收到一串路径之后干什么"才是会出错的那部分 ——
/// 会不会把存档认成资源包、装整合包会不会新建实例、认不出的文件有没有被动过。
/// 所以那段逻辑留在 MainViewModel.DropFilesAsync 里，这个探针直接调它，
/// 用的是和真实拖放完全相同的那条代码路径（窗口的 OnDrop 只是转手）。
///
/// 默认**只分类、不落盘**；加 --install 才真装。
/// </summary>
internal static class DropProbe
{
    public static int Run(string[] args)
    {
        // 探针的输出会被重定向到文件再读回来。重定向时子进程**没有控制台**，
        // `Console.OutputEncoding = UTF8` 会直接抛异常被吃掉（试过，没用）——
        // 结果就是报告按 GBK 写出去，`✓` 变成 `?`，中文字全成乱码。
        // 所以自己包一层标准输出流，钉死 UTF-8 字节。
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        void Say(string line = "") => stdout.WriteLine(line);

        var paths = new List<string>();
        var install = false;
        string? instanceName = null;

        for (var i = 1; i < args.Length; i++)   // args[0] == "--drop"
        {
            switch (args[i])
            {
                case "--install": install = true; break;
                case "--instance":
                    if (i + 1 < args.Length) instanceName = args[++i];
                    break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal)) paths.Add(args[i]);
                    break;
            }
        }

        if (paths.Count == 0)
        {
            Say("用法：PclLike.exe --drop <文件> [文件...] [--install] [--instance 名字]");
            return 2;
        }

        var app = new App();
        app.InitializeComponent();
        if (Application.Current is null) _ = app;

        // 资源系统要跟真启动一致（App.xaml 里有主题字典和转换器），
        // 否则探针里能跑、用户那儿崩。不调 Run()，所以不会弹出交互窗口。
        var win = new MainWindow();
        var vm = win.ViewModelForShot();

        // 把两个探针开关应用到 VM（真启动时这些是用户在界面上点的）
        if (instanceName is not null)
        {
            var inst = vm.Store.LoadAll().FirstOrDefault(x => x.Name == instanceName);
            if (inst is null)
            {
                Say($"找不到实例「{instanceName}」");
                return 3;
            }

            vm.SelectedInstance = inst;
        }

        Say("── A. 分类（只看，不动任何文件）──");

        foreach (var p in paths)
        {
            var c = PclLike.Core.Content.DroppedContentClassifier.Classify(p);

            Say($"    {Path.GetFileName(p)}");
            Say($"      类型 : {c.Kind}");
            Say($"      判断 : {c.Describe}");
        }

        if (!install)
        {
            Say();
            Say("（只读模式。加 --install 才会真的装。）");
            return 0;
        }

        Say();
        Say("── B. 真装（走的是窗口 OnDrop 调的那同一个方法）──");

        var summary = vm.DropFilesAsync(paths).GetAwaiter().GetResult();

        Say(summary);

        Say();
        Say("── C. 装完的实例列表 ──");

        foreach (var i in vm.Store.LoadAll())
            Say($"    {i.Name,-28} {i.VersionId,-32} {(i.Isolated ? "独立" : "共享")}");

        Say();
        Say("── D. 当前选中的实例 ──");
        Say($"    {vm.SelectedInstance?.Name ?? "（没选）"} → {vm.SelectedInstance?.VersionId}");

        // ── E/F. 任务管理窗到底吃到了什么 ──
        //
        // 「导入没进度」这种问题只能在这条真实路径上看。Tasks **不碰安装代码**，
        // 它只订阅 LogLines 然后靠认文本推进清单 —— 所以必须把
        // 「日志里到底有什么」和「它认出了几步」并排打出来，
        // 才能一眼分清是【日志压根没产生】还是【认的句式对不上】。
        Say();
        Say("── E. 任务管理（Tasks）吃到了什么 ──");
        Say($"    标题   : {vm.Tasks.Title}");
        Say($"    总进度 : {vm.Tasks.Overall:0.00} %");
        Say($"    剩余   : {vm.Tasks.RemainingText}    速度: {vm.Tasks.SpeedText}");
        Say($"    步数   : {vm.Tasks.Steps.Count}");
        foreach (var s in vm.Tasks.Steps)
        {
            var mark = s.State switch
            {
                StepState.Done => "✓",
                StepState.Running => "→",
                StepState.Failed => "✗",
                _ => "·",
            };
            Say($"      [{mark}] {s.Name,-26} {s.Percent,3}%");
        }

        Say();
        Say($"── F. 日志行（共 {vm.LogLines.Count} 行，列最后 30 行）──");
        foreach (var l in vm.LogLines.TakeLast(30)) Say($"    {l.Text}");

        return 0;
    }
}