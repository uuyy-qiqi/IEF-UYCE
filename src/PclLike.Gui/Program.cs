using System.Windows;

namespace PclLike.Gui;

/// <summary>
/// 显式入口点。
///
/// 默认用 WPF 生成的 Main（StartupUri=MainWindow.xaml），但要多支持一个
/// --selftest 分支：它必须在 Application 启动流程之外跑，否则窗口会带着
/// 消息循环弹出来，无头环境下自检没法收尾。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run();

        // 皮肤 3D 头部渲染探针：PclLike.exe --render-skin <尺寸> <输出png> [皮肤png]
        // 用来肉眼验收等距投影的几何 —— 也必须在 Application 启动前跑，
        // 但渲染需要资源系统就绪，所以在这里手动建一个 Application 实例。
        var skinIdx = Array.FindIndex(args, a => a.Equals("--render-skin", StringComparison.OrdinalIgnoreCase));
        if (skinIdx >= 0)
        {
            var app0 = new Application();
            _ = app0;   // 不 Run()，只借用资源系统
            var rest = args.Skip(skinIdx).ToArray();
            return SkinRenderProbe.Run(rest);
        }

        // 窗口截图探针：PclLike.exe --shoot <输出png> [宽] [高] [页面id]
        // 用来逐页肉眼验收界面 —— XAML 出错编译期不报，只能真渲染。
        var shootIdx = Array.FindIndex(args, a => a.Equals("--shoot", StringComparison.OrdinalIgnoreCase));
        if (shootIdx >= 0)
            return WindowShot.Run(args.Skip(shootIdx).ToArray());

        // 端到端：PclLike.exe <目录> --e2e-launch <版本id> [--instance <实例名>]
        var e2eIdx = Array.FindIndex(args, a => a.Equals("--e2e-launch", StringComparison.OrdinalIgnoreCase));
        if (e2eIdx >= 0)
        {
            var version = e2eIdx + 1 < args.Length ? args[e2eIdx + 1] : "1.21.4";

            // --instance 是可选的：验收「某个已经装好的实例」时，
            // 光给版本 id 验不到那个实例（启动器会另找一个默认实例）
            var e2eInstIdx = Array.FindIndex(args, a => a.Equals("--instance", StringComparison.OrdinalIgnoreCase));
            var instName = e2eInstIdx >= 0 && e2eInstIdx + 1 < args.Length ? args[e2eInstIdx + 1] : null;

            return E2ELaunch.Run(version, instName);
        }

        // 内嵌浏览器登录探针：PclLike.exe --login-probe [--wait 秒数]
        // 验证 WebView2 能不能把微软登录页渲染出来（它不登录，只看页面）。
        var loginIdx = Array.FindIndex(args, a => a.Equals("--login-probe", StringComparison.OrdinalIgnoreCase));
        if (loginIdx >= 0)
            return LoginProbe.Run(args.Skip(loginIdx).ToArray());

        // 选文件夹弹窗探针：PclLike.exe --pick-folder [起始目录]
        // 这个弹窗以前只有真点「添加已有文件夹」才出现，一条路验证不到它能不能
        // 加载（XAML 报错编译期看不见）。--xamlcheck 会卡住，所以单独走一条。
        var pickIdx = Array.FindIndex(args, a => a.Equals("--pick-folder", StringComparison.OrdinalIgnoreCase));
        if (pickIdx >= 0)
        {
            var app1 = new Application();
            _ = app1;   // 不 Run()，只借资源系统
            var start = pickIdx + 1 < args.Length ? args[pickIdx + 1] : null;

            // 不调 Pick()：那是模态的，探针没人点就永远卡着。
            // 改成 Show() 出来、跑完 Loaded 与 Load()，1.5 秒后自己关 ——
            // 这样既验证了 XAML 能加载，也验证了真读一次目录不炸。
            var dlg = new FolderPickerDialog(start);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                // 不要在这里设 Console.OutputEncoding：stdout 被重定向到文件时
                // SetConsoleOutputEncoding 会抛 IOException（句柄无效），
                // 直接从 UI 线程把进程带走。
                Console.WriteLine($"ok  FolderPickerDialog 加载成功，当前目录={dlg.CurrentDirectory}");
                dlg.Close();
            };
            timer.Start();
            app1.Run(dlg);
            return 0;
        }

        // 拖放探针：PclLike.exe --drop <文件> [文件...] [--install] [--instance 名字]
        // 拖放这件事没法用工具模拟操作系统级的拖拽（那要真的按下鼠标拖过桌面），
        // 所以把"收到一串路径之后干什么"整段抽成了可调用的方法，这里直接调它。
        // 默认只分类不落盘；加 --install 才真装。
        var dropIdx = Array.FindIndex(args, a => a.Equals("--drop", StringComparison.OrdinalIgnoreCase));
        if (dropIdx >= 0)
            return DropProbe.Run(args.Skip(dropIdx).ToArray());

        // 下载页整合包安装探针：PclLike.exe --content-pack [关键字] [--version 1.20.1] ...
        // 拖放那条路和下载页那条路的后半段是同一批 Core 函数，但 VM 那层不一样 ——
        // 出问题时得能分清是哪一段炸的。
        var packIdx = Array.FindIndex(args, a => a.Equals("--content-pack", StringComparison.OrdinalIgnoreCase));
        if (packIdx >= 0)
            return ContentProbe.Run(args.Skip(packIdx).ToArray());

        // 安装包探针：PclLike.exe --installer <key> [mc版本]
        // key = fabric | quilt | forge | neoforge | optifine | legacyfabric
        // 走的是界面同一条路（InstallerViewModel），验收"点一下会发生什么"。
        var instIdx = Array.FindIndex(args, a => a.Equals("--installer", StringComparison.OrdinalIgnoreCase));
        if (instIdx >= 0)
            return InstallerProbe.Run(args.Skip(instIdx).ToArray()).GetAwaiter().GetResult();

        // 常规启动：交给 WPF 的标准流程
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}