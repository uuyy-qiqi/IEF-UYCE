using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PclLike.Gui;

/// <summary>
/// 把真实窗口渲染成 PNG，用来逐页验收界面。
///
/// 为什么需要它：XAML 的资源引用错误、布局塌陷、样式写错，编译期全都不报，
/// 只有真跑起来才看得见。这个探针在不弹窗的情况下把窗口画出来存图。
///
/// 跑法：
///     PclLike.exe --shoot &lt;输出png&gt; [宽] [高] [页面id]
/// 页面 id 对应导航项（launch / download / settings / tools ...），
/// 不给就用默认页。
/// </summary>
internal static class WindowShot
{
    public static int Run(string[] args)
    {
        // 用法：--shoot <输出png> [宽] [高] [页面id] [--settle 秒数]
        //
        // 参数按名字找，不按位置 —— 位置式解析太脆，加一个开关就会
        // 悄悄改变另一个参数的含义（曾经把 "--settle" 当成了页面名）。
        var outPath = args.Length > 1 ? args[1] : "window.png";

        var known = new[] { "launch", "download", "settings", "tools", "instances", "account", "login", "log", "tasks" };

        var page = args.Skip(1).FirstOrDefault(a => known.Contains(a));

        var sizes = args.Skip(2)
            .Where(a => double.TryParse(a, out _))
            .Select(double.Parse)
            .ToArray();

        var w = sizes.Length > 0 ? sizes[0] : 1120;
        var h = sizes.Length > 1 ? sizes[1] : 720;

        // 资源系统要跟真启动完全一致，否则探针里能跑、用户那儿崩，
        // 或者反过来 —— 这里直接用 App 自己那份 App.xaml（含主题字典和
        // 三个转换器）。不调 Run()，所以不会弹出窗口。
        var app = new App();
        app.InitializeComponent();
        if (Application.Current is null) _ = app;

        var win = new MainWindow();
        win.Width = w;
        win.Height = h;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -10000;      // 挪到屏幕外，避免闪一下
        win.Top = -10000;
        win.ShowInTaskbar = false;

        // 先让窗口完成布局，再截
        win.Show();
        win.UpdateLayout();
        Pump();

        // --settle <秒>：等网络数据回来再截。
        //
        // 平时截"静态长什么样"不用等；但要验收"版本清单拉回来之后长什么样"，
        // 必须给异步加载留时间，否则截到的永远是加载中的空壳。
        //
        // 注意顺序：等的时间必须花在【切页/切分区之后】。社区资源那一屏的
        // 搜索是切进那一栏才开始发请求的 —— 先在启动页上干等 30 秒再切过去
        // 立刻截，截到的永远是"正在处理…"。所以这里先只给一小段首屏时间，
        // 真正的等待放在导航后面。
        var settleIdx = Array.FindIndex(args, a =>
            a.Equals("--settle", StringComparison.OrdinalIgnoreCase));

        var settleSec = 0.0;

        if (settleIdx >= 0 && settleIdx + 1 < args.Length)
            double.TryParse(args[settleIdx + 1], out settleSec);

        void PumpFor(double seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                Pump();
                System.Threading.Thread.Sleep(100);
            }
        }

        PumpFor(Math.Min(settleSec, 3));

        // --xamlcheck：把每个窗口都 new 一遍，看谁在加载 XAML 时就炸。
        //
        // 为什么需要它：编译能过**不代表**能加载。TargetType 不匹配这种错
        // （把 RadioButton 的样式套给 Button）只在构造时才抛，
        // TasksWindow 和 TextInputDialog 都栽在同一个 TopBarButton 上 ——
        // 两次都是靠"真的开一次窗口"才发现的。以后新增窗口记得跑一遍这个。
        if (args.Any(a => a.Equals("--xamlcheck", StringComparison.OrdinalIgnoreCase)))
        {
            var vmShot = win.ViewModelForShot();
            var results = new List<string>();

            void Check(string name, Func<object> make)
            {
                try
                {
                    var made = make();
                    (made as Window)?.Close();
                    results.Add($"ok   {name}");
                }
                catch (Exception ex)
                {
                    var msg = ex.Message.Replace("\r", " ").Replace("\n", " ");
                    results.Add($"FAIL {name}  {ex.GetType().Name}: {msg}");
                }
            }

            Check("MainWindow", () => new MainWindow());
            Check("TasksWindow", () => new TasksWindow(vmShot));
            Check("TextInputDialog", () => new TextInputDialog());
            Check("LogWindow", () => new LogWindow(vmShot));
            Check("InstancesWindow", () => win.InstancesForShot());
            Check("AccountWindow", () => win.AccountForShot());
            Check("LoginWindow", () => new LoginWindow(new PclLike.Core.Login.MicrosoftAuth(null)));

            foreach (var r in results) Console.WriteLine(r);
        }

        if (!string.IsNullOrEmpty(page))
        {
            try { win.NavigateForShot(page); }
            catch (Exception ex) { Console.Error.WriteLine($"切换页面失败: {ex.Message}"); }
            win.UpdateLayout();
            Pump();
        }

        // --section <key>：切到设置页/工具页里的某个分区（set-java / toolbox / mods ...）
        var secIdx = Array.FindIndex(args, a =>
            a.Equals("--section", StringComparison.OrdinalIgnoreCase));

        if (secIdx >= 0 && secIdx + 1 < args.Length)
        {
            try { win.SectionForShot(args[secIdx + 1]); }
            catch (Exception ex) { Console.Error.WriteLine($"切换分区失败: {ex.Message}"); }
            win.UpdateLayout();
            Pump();
        }

        // 导航都做完了，现在才真正等数据
        PumpFor(settleSec);

        // 实例管理 / 账号都是独立窗口（对话框），不是主窗口的一页，
        // 所以要单独开一个来截。
        Window shotTarget = win;

        if (page is "instances" or "account")
        {
            var dlg = page == "instances" ? win.InstancesForShot() : win.AccountForShot();
            dlg.WindowStartupLocation = WindowStartupLocation.Manual;
            dlg.Left = -10000;
            dlg.Top = -10000;
            dlg.ShowInTaskbar = false;

            // 这两个对话框是 SizeToContent=Height 的，硬设尺寸会把内容挤变形，
            // 所以只在显式给了尺寸（或沿用实例窗口的老行为）时才覆盖。
            if (page == "instances" || sizes.Length >= 1) dlg.Width = w;
            if (page == "instances" || sizes.Length > 1) dlg.Height = h;

            dlg.Show();
            dlg.UpdateLayout();
            Pump();
            shotTarget = dlg;
        }

        // 登录窗口：内容要联网渲染，所以多等几秒。
        //
        // 注意 WebView2 是独立的合成表面，RenderTargetBitmap 拍不到网页本身 ——
        // 这张图只用来看窗口外壳（标题、换个微软账号按钮、底部状态行）的排版。
        if (page == "login")
        {
            var dlg = new LoginWindow(new PclLike.Core.Login.MicrosoftAuth(null));
            dlg.WindowStartupLocation = WindowStartupLocation.Manual;
            dlg.Left = -10000;
            dlg.Top = -10000;
            dlg.ShowInTaskbar = false;
            dlg.Width = w;
            dlg.Height = h;
            dlg.Show();
            dlg.UpdateLayout();
            PumpFor(8);
            shotTarget = dlg;
        }

        // 日志窗口：也只截外壳，看排版用。
        if (page == "log")
        {
            var dlg = new LogWindow(win.ViewModelForShot());
            dlg.WindowStartupLocation = WindowStartupLocation.Manual;
            dlg.Left = -10000;
            dlg.Top = -10000;
            dlg.ShowInTaskbar = false;
            dlg.Width = w;
            dlg.Height = h;
            dlg.Show();
            dlg.UpdateLayout();
            PumpFor(3);
            shotTarget = dlg;
        }

        // 任务管理窗：也单独开一个来截（左边三块统计 + 右边任务卡 + 分步清单）。
        // 顺手喂几行假日志进去 —— 否则截到的是一张空清单，看不出分步长什么样。
        if (page == "tasks")
        {
            var vmShot = win.ViewModelForShot();
            vmShot.Tasks.Begin("CurseForge 整合包安装：AFoP Continue-1.5.1-fix");

            foreach (var line in new[]
            {
                "正在铺配置… 412 个",
                "已铺 412 个配置文件（overrides/）",
                "装 Forge 47.4.28",
                "已下 189/264（失败 4）",
            })
            {
                vmShot.Tasks.OnLog(line);
            }

            vmShot.Tasks.SetPercent("下载额外文件", 71);

            var dlg = new TasksWindow(vmShot);
            dlg.WindowStartupLocation = WindowStartupLocation.Manual;
            dlg.Left = -10000;
            dlg.Top = -10000;
            dlg.ShowInTaskbar = false;
            dlg.Width = w;
            dlg.Height = h;
            dlg.Show();
            dlg.UpdateLayout();
            PumpFor(3);
            shotTarget = dlg;
        }

        var srcW = (int)Math.Ceiling(shotTarget.ActualWidth);
        var srcH = (int)Math.Ceiling(shotTarget.ActualHeight);
        if (srcW <= 0 || srcH <= 0) { srcW = (int)w; srcH = (int)h; }

        var rtb = new RenderTargetBitmap(srcW, srcH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(shotTarget);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (var fs = File.Create(outPath)) enc.Save(fs);

        Console.WriteLine($"已写出 {outPath} ({srcW}x{srcH}) page={page ?? "(default)"}");
        if (!ReferenceEquals(shotTarget, win)) shotTarget.Close();
        win.Close();
        return 0;
    }

    /// <summary>把还没处理完的布局/渲染工作跑掉，否则截出来是空白。</summary>
    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
}