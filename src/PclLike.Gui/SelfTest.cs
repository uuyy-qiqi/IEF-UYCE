using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Diagnostics;

namespace PclLike.Gui;

/// <summary>
/// 无头自检：把 MainWindow 真的建出来、跑完数据加载、然后检查控件树。
///
/// 存在的理由：WPF 的绑定失败是**静默**的——绑错了属性名只会在输出里
/// 留一条 Trace，界面看起来照常，值是空的。光看"进程还活着"证明不了什么。
/// 这个自检把关键控件的实际值读出来，绑错就当场暴露。
///
/// 用法：PclLike.exe --selftest
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        var failures = new List<string>();
        var notes = new List<string>();

        void Check(bool ok, string what, string? detail = null)
        {
            if (ok) notes.Add($"  [OK]   {what}" + (detail is null ? "" : $"  →  {detail}"));
            else failures.Add($"  [FAIL] {what}" + (detail is null ? "" : $"  →  {detail}"));
        }

        Window? window = null;
        MainViewModel? vm = null;

        try
        {
            // 必须走和真实启动完全相同的初始化路径：
            //   new App() → InitializeComponent()（装载 App.xaml 资源字典）
            //   → 建窗口
            // 少任何一步，MainWindow.xaml 里的 StaticResource 都找不到，
            // 自检会报出一堆假的"资源缺失"。
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();

            window = new MainWindow();
            window.Show();

            vm = window.DataContext as MainViewModel;
            Check(vm is not null, "DataContext 是 MainViewModel");

            if (vm is null) return Report(failures, notes);

            // ---- 等异步的版本清单加载完成 ----
            notes.Add("  等待版本清单加载...");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (vm.Versions.Count > 0 || vm.StatusText.Contains("失败")) break;
                DoEvents();
                Thread.Sleep(120);
            }

            Check(vm.Versions.Count > 0, "版本列表已加载", $"{vm.Versions.Count} 个版本");
            Check(vm.VersionsView is not null, "VersionsView 存在");
            Check(vm.JavaList.Count > 0, "Java 已探测",
                string.Join("、", vm.JavaList.Select(j => $"Java {j.MajorVersion}")));
            Check(vm.SelectedVersion is not null, "自动选中了版本", vm.SelectedVersion?.Id ?? "null");
            Check(vm.SelectedVersion?.Type == "release",
                "默认选中的是正式版而非快照", $"{vm.SelectedVersion?.Id} ({vm.SelectedVersion?.Type})");
            Check(vm.CanLaunch, "启动按钮可用（CanLaunch）");

            // ---- 控件树：确认 XAML 绑定真的栓到了值 ----
            var versionList = FindByName<ListBox>(window, "VersionListBox");
            Check(versionList is not null, "找到版本列表控件");
            Check(versionList?.Items.Count > 0, "版本列表控件有内容", $"{versionList?.Items.Count} 项");

            var runButton = FindByName<Button>(window, "RunButton");
            Check(runButton is not null, "找到启动按钮");
            // 新 UI 的按钮内容是图标 + 文字的 StackPanel，所以查 VM 的文案。
            Check(vm.RunButtonText == "启动游戏", "启动按钮文案正确", vm.RunButtonText);
            Check(runButton?.IsEnabled == true, "启动按钮已启用");
            Check(runButton?.Background is SolidColorBrush, "启动按钮有背景色");

            var logList = FindByName<ListBox>(window, "LogList");
            Check(logList is not null, "找到日志列表");
            Check(logList?.Items.Count > 0, "日志列表有内容", $"{logList?.Items.Count} 行");

            var usernameBox = FindByName<TextBox>(window, "UsernameBox");
            Check(usernameBox?.Text is { Length: > 0 }, "用户名框有值", usernameBox?.Text);

            var memorySlider = FindByName<Slider>(window, "MemorySlider");
            Check(memorySlider is not null, "找到内存滑块");
            Check(memorySlider?.Value > 0, "内存滑块有值", $"{memorySlider?.Value:0} MB");

            var rootText = FindByName<TextBlock>(window, "GameRootText");
            Check(rootText?.Text is { Length: > 0 }, "设置页显示游戏目录", rootText?.Text);

            // ---- 筛选功能 ----
            notes.Add("  测试版本筛选...");
            var total = vm.Versions.Count;
            vm.VersionFilter = "1.21";
            DoEvents();

            var filtered = vm.VersionsView is null ? 0 : vm.VersionsView.Cast<object>().Count();
            Check(filtered > 0 && filtered < total, "筛选能缩小列表", $"1.21 → {filtered}/{total}");
            Check(vm.VersionCountText.Contains("筛选出"), "筛选计数文案切换", vm.VersionCountText);

            vm.VersionFilter = "";
            DoEvents();
            Check(vm.VersionsView is null || vm.VersionsView.Cast<object>().Count() == total, "清空筛选后恢复全部");

            // ---- 离线 UUID 正确性（这是多人服务器认人的关键）----
            var uuid = Core.Storage.ConfigStore.OfflineUuid("Notch");
            Check(uuid == "b50ad385-829d-3141-a216-7e7d7539ba7f",
                "离线 UUID 与 Mojang 算法一致", uuid);

            Check(Core.Storage.ConfigStore.SanitizeUsername("ab") == "Player",
                "用户名过短被替换");
            Check(Core.Storage.ConfigStore.SanitizeUsername("坏名字!!") == "Player",
                "非法字符被过滤后过短则替换");
            Check(Core.Storage.ConfigStore.SanitizeUsername("Good_Name123") == "Good_Name123",
                "合法用户名原样保留");

            // ---- 配置往返 ----
            notes.Add("  测试配置持久化...");
            var cfgPath = Core.Storage.ConfigStore.PathFor(App.GameRoot);
            vm.Username = "SelfTest";
            vm.MaxMemoryMb = 6144;
            vm.Persist();

            var reloaded = Core.Storage.ConfigStore.Load(App.GameRoot);
            Check(reloaded.Username == "SelfTest", "配置写盘并能读回（用户名）", reloaded.Username);
            Check(reloaded.MaxMemoryMb == 6144, "配置写盘并能读回（内存）", $"{reloaded.MaxMemoryMb} MB");

            // 恢复原值，别污染用户配置
            vm.Username = "Player";
            vm.MaxMemoryMb = 4096;
            vm.Persist();
        }
        catch (Exception ex)
        {
            failures.Add($"  [FAIL] 自检抛异常：{ex}");
        }
        finally
        {
            try { window?.Close(); } catch { }
        }

        return Report(failures, notes);
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 按 Name 找控件。先走可视树；找不到再退回名字范围。
    /// 折叠的页面（Visibility=Collapsed）不会被测量，模板也就不会展开，
    /// 它的子控件压根不存在于可视树里，只能用名字范围取。
    /// </summary>
    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var visual = FindVisual<T>(root, name);
        if (visual is not null) return visual;

        if (root is FrameworkElement fe && fe.FindName(name) is T named) return named;

        return FindLogical<T>(root, name);
    }

    private static T? FindVisual<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T typed && typed.Name == name) return typed;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindVisual<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }

        return null;
    }

    private static T? FindLogical<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T typed && typed.Name == name) return typed;

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            var found = FindLogical<T>(d, name);
            if (found is not null) return found;
        }

        return null;
    }

    private static int Report(List<string> failures, List<string> notes)
    {
        Console.WriteLine();
        Console.WriteLine("========== GUI SELFTEST ==========");

        foreach (var n in notes) Console.WriteLine(n);
        if (failures.Count > 0)
        {
            Console.WriteLine();
            foreach (var f in failures) Console.WriteLine(f);
        }

        Console.WriteLine();
        Console.WriteLine(failures.Count == 0
            ? $"ALL PASSED ({notes.Count} checks)"
            : $"{failures.Count} FAILED");
        Console.WriteLine("==================================");

        return failures.Count == 0 ? 0 : 1;
    }
}