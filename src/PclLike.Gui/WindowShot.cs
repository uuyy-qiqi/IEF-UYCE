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

        var known = new[] { "launch", "download", "settings", "tools" };

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
        var settleIdx = Array.FindIndex(args, a =>
            a.Equals("--settle", StringComparison.OrdinalIgnoreCase));

        if (settleIdx >= 0 && settleIdx + 1 < args.Length
            && double.TryParse(args[settleIdx + 1], out var settleSec))
        {
            var deadline = DateTime.UtcNow.AddSeconds(settleSec);
            while (DateTime.UtcNow < deadline)
            {
                Pump();
                System.Threading.Thread.Sleep(100);
            }
        }

        if (!string.IsNullOrEmpty(page))
        {
            try { win.NavigateForShot(page); }
            catch (Exception ex) { Console.Error.WriteLine($"切换页面失败: {ex.Message}"); }
            win.UpdateLayout();
            Pump();
        }

        // --section <key>：切到设置页/工具页里的某个分区（set-java / toolbox ...）
        var secIdx = Array.FindIndex(args, a =>
            a.Equals("--section", StringComparison.OrdinalIgnoreCase));

        if (secIdx >= 0 && secIdx + 1 < args.Length)
        {
            try { win.SectionForShot(args[secIdx + 1]); }
            catch (Exception ex) { Console.Error.WriteLine($"切换分区失败: {ex.Message}"); }
            win.UpdateLayout();
            Pump();
        }

        var srcW = (int)Math.Ceiling(win.ActualWidth);
        var srcH = (int)Math.Ceiling(win.ActualHeight);
        if (srcW <= 0 || srcH <= 0) { srcW = (int)w; srcH = (int)h; }

        var rtb = new RenderTargetBitmap(srcW, srcH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (var fs = File.Create(outPath)) enc.Save(fs);

        Console.WriteLine($"已写出 {outPath} ({srcW}x{srcH}) page={page ?? "(default)"}");
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