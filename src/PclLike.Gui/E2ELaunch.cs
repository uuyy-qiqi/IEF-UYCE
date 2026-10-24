using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PclLike.Core.Storage;

namespace PclLike.Gui;

/// <summary>
/// 端到端：把 GUI 真的建起来，等版本清单加载完，然后**真的去点启动按钮**，
/// 一直等到游戏进程起来为止。
///
/// 这是唯一能证明"GUI 能启动游戏"的手段——自检（SelfTest）只验证绑定，
/// 不验证点击链路；而"进程还活着"证明不了按钮背后真的跑通了。
///
/// 用法：PclLike.exe &lt;实例目录&gt; --e2e-launch &lt;版本id&gt;
/// </summary>
public static class E2ELaunch
{
    public static int Run(string versionId)
    {
        var steps = new List<string>();
        var failed = false;

        Window? window = null;
        MainViewModel? vm = null;

        try
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();

            window = new MainWindow();
            window.Show();

            vm = window.DataContext as MainViewModel
                 ?? throw new InvalidOperationException("DataContext 不是 MainViewModel");

            steps.Add($"[1] 窗口已建，实例目录 {App.GameRoot}");

            // ---- 等版本清单 ----
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(120))
            {
                if (vm.Versions.Count > 0) break;
                DoEvents();
                Thread.Sleep(100);
            }

            if (vm.Versions.Count == 0)
            {
                steps.Add("[!] 版本清单没加载出来");
                return Report(steps, ok: false);
            }

            steps.Add($"[2] 版本清单已加载：{vm.Versions.Count} 个版本");

            // ---- 选中目标版本 ----
            var target = vm.Versions.FirstOrDefault(v => v.Id == versionId);
            if (target is null)
            {
                steps.Add($"[!] 清单里没有 {versionId}");
                return Report(steps, ok: false);
            }

            vm.SelectedVersion = target;
            DoEvents();
            steps.Add($"[3] 已选中 {target.Id}（{target.TypeLabel}）");

            // ---- 真的点按钮 ----
            var runButton = FindByName<Button>(window, "RunButton")
                            ?? throw new InvalidOperationException("找不到 RunButton");

            steps.Add($"[4] 点击启动按钮（文案 \"{runButton.Content}\"，启用={runButton.IsEnabled}）");

            // 按钮的 Click 处理器是 async void，这里直接触发它，
            // 然后靠轮询观察 VM 状态变化判断进度
            runButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            // ---- 观察：等游戏起来 ----
            var started = false;
            var sawProgress = false;
            sw.Restart();

            while (sw.Elapsed < TimeSpan.FromMinutes(12))
            {
                DoEvents();

                if (vm.StatusText != "就绪")
                    sawProgress = true;

                if (vm.IsGameRunning)
                {
                    started = true;
                    break;
                }

                // 出错了就早点放弃，别干等 12 分钟
                if (vm.StatusText.Contains("失败") || vm.StatusText.Contains("取消"))
                    break;

                Thread.Sleep(250);
            }

            steps.Add($"[5] 观察到进度更新：{sawProgress}");
            steps.Add($"[6] 状态文本：{vm.StatusText}");
            steps.Add($"[7] 游戏是否在运行（IsGameRunning）：{started}");
            steps.Add($"[8] 按钮文案已切换为：{vm.RunButtonText}");

            // ---- 日志里应该出现启动相关的行 ----
            var logText = string.Join("\n", vm.LogLines.Select(l => l.Text));
            var sawLaunchLine = logText.Contains("启动") || logText.Contains("LWJGL")
                                || logText.Contains("Setting user") || logText.Contains("Backend library");

            steps.Add($"[9] 日志里出现游戏启动迹象：{sawLaunchLine}");
            steps.Add($"    日志共 {vm.LogLines.Count} 行");

            // 打印日志尾部，便于人工核对
            steps.Add("---- 日志尾部 ----");
            foreach (var line in vm.LogLines.TakeLast(12))
                steps.Add($"      {line.Text}");

            failed = !started;

            if (started)
            {
                // 再多跑一会儿，验证游戏 stdout 真的能回灌到日志窗。
                // 刚起来的那几秒 Minecraft 还在解压/初始化 LWJGL，不刷日志；
                // 太早 kill 会误判成"回灌失效"。
                steps.Add("[10] 让游戏多跑 25 秒，观察 stdout 回灌...");
                var logBefore = vm.LogLines.Count;
                sw.Restart();
                while (sw.Elapsed < TimeSpan.FromSeconds(25))
                {
                    DoEvents();
                    Thread.Sleep(200);
                }

                var logAfter = vm.LogLines.Count;
                steps.Add($"    日志行数 {logBefore} → {logAfter}（新增 {logAfter - logBefore} 行）");

                var gameOutput = vm.LogLines
                    .Skip(logBefore)
                    .Select(l => l.Text)
                    .ToList();

                if (gameOutput.Count > 0)
                {
                    steps.Add("    游戏输出示例：");
                    foreach (var line in gameOutput.Take(6)) steps.Add($"      {line}");
                }
                else
                {
                    steps.Add("    [!] 没有收到任何游戏 stdout/stderr");
                }

                steps.Add("[11] 结束进程");
                vm.KillRunningGame();
                Thread.Sleep(4000);
                DoEvents();

                steps.Add($"[12] 结束后按钮文案：{vm.RunButtonText}，IsGameRunning={vm.IsGameRunning}");
            }
        }
        catch (Exception ex)
        {
            steps.Add($"[!] 异常：{ex.Message}");
            failed = true;
        }
        finally
        {
            try { window?.Close(); } catch { }
        }

        return Report(steps, !failed);
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T t && t.Name == name) return t;

        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var found = FindByName<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }

        return null;
    }

    private static int Report(List<string> steps, bool ok)
    {
        Console.WriteLine();
        Console.WriteLine("========== E2E LAUNCH ==========");
        foreach (var s in steps) Console.WriteLine(s);
        Console.WriteLine();
        Console.WriteLine(ok ? "E2E PASSED" : "E2E FAILED");
        Console.WriteLine("================================");
        return ok ? 0 : 1;
    }
}