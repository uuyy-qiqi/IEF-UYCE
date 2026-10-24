using System.Windows;
using PclLike.Core.Login;

namespace PclLike.Gui;

/// <summary>
/// 内嵌浏览器登录的验收探针：把登录页真加载一次，报出页面标题和最终地址。
///
/// 跑法：
///     PclLike.exe --login-probe [--wait 秒数]
///
/// 它**不做登录** —— 输账号密码必须由真人来，任何自动化都不该碰那一步。
/// 它只回答一个问题：本机这套 WebView2 加上那个应用号，能不能把微软的登录页
/// 渲染出来。这是整条登录链里唯一没法用 HTTP 校验的一环（其余端点都在
/// probe-login 里逐个打过了）。
/// </summary>
internal static class LoginProbe
{
    public static int Run(string[] args)
    {
        // 资源系统要跟真启动一致：登录窗口用的 StaticResource 都在 App 的主题字典里。
        var app = new App();
        app.InitializeComponent();
        if (Application.Current is null) _ = app;

        // **必须自己装一个 Dispatcher 同步上下文。**
        //
        // 常规启动会走 Application.Run()，它负责把这个上下文装上；探针不调 Run()，
        // 所以 SynchronizationContext.Current 是空的。后果是：窗口里的 await 续体会
        // 跑到线程池线程上去，然后碰 WebView2（只能 UI 线程访问）——
        // 报「调用线程无法访问此对象，因为另一个线程拥有该对象」。
        // 装上它，再靠 Pump() 推着消息循环跑，续体就会回到 UI 线程。
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(
                System.Windows.Threading.Dispatcher.CurrentDispatcher));

        var seconds = 25.0;
        var idx = Array.FindIndex(args, a => a.Equals("--wait", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length) double.TryParse(args[idx + 1], out seconds);

        var auth = new MicrosoftAuth();
        var win = new LoginWindow(auth)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,      // 挪到屏幕外：探针不该在用户眼前闪一下
            Top = -10000,
            ShowInTaskbar = false,
        };

        win.Show();

        // 不能在 UI 线程上直接等这个 Task：它内部的 await 会把续体排回
        // Dispatcher，而 GetResult() 正堵着 Dispatcher —— 于是死锁。
        // 所以自己泵消息循环，等它完成。
        var task = win.ProbePageAsync(TimeSpan.FromSeconds(seconds));
        while (!task.IsCompleted)
        {
            Pump();
            System.Threading.Thread.Sleep(50);
        }

        var result = task.GetAwaiter().GetResult();

        Console.WriteLine($"应用号  {auth.BuildLiveAuthorizeUrl().Split('?')[0]}");
        Console.WriteLine($"client_id={MicrosoftAuth.DefaultClientId}");
        Console.WriteLine(result);

        win.Close();
        return result.StartsWith("OK", StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>把还没处理完的布局/渲染/续体跑掉。</summary>
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}