using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PclLike.Core.Login;

namespace PclLike.Gui;

/// <summary>
/// 正版登录用的内嵌浏览器窗口。
///
/// ## 为什么必须内嵌浏览器，不能"打开系统浏览器让用户复制地址回来"
///
/// 微软的授权页有反自动化风控，用 HTTP 客户端模拟登录会被挡。可行的是两条路：
///
/// 1. **device code**：用户自己在浏览器里输码。缺点是流程长（要切窗口、抄码），
///    而且实测自己注册的 Azure 应用走完流程也会被 Minecraft 拒
///    （`Invalid app registration`）——详细结论见 <see cref="MicrosoftAuth"/>。
/// 2. **内嵌浏览器 + 拦回调**（就是本窗口）：把授权页渲染出来，用户登完，
///    微软会跳到 `oauth20_desktop.srf?code=...`，我们在跳转发生前就把地址
///    拦下来，把 code 抠出来。用户全程不离开启动器。
///
/// 本机装了 WebView2 运行时，所以走第 2 条。这条路和本机 PCL CE 一致
/// （它的日志里就是 `login.live.com`）。
/// </summary>
public partial class LoginWindow : Window
{
    /// <summary>
    /// 微软那边的"退出登录"页。访问它就会清掉浏览器里记住的 Microsoft 账号，
    /// 之后再打开授权页才会重新问你用哪个账号。
    /// </summary>
    private const string SignOutUrl = "https://login.live.com/logout.srf";

    private readonly MicrosoftAuth _auth;
    private readonly bool _signOutFirst;
    private bool _finished;

    /// <summary>拦到的授权码。用户取消或登录失败时为 null。</summary>
    public string? Code { get; private set; }

    /// <summary>失败原因（给上层弹提示用）。</summary>
    public string? Failure { get; private set; }

    /// <param name="signOutFirst">
    /// 开窗时先把浏览器里记住的微软账号登出，再打开登录页。
    /// 用户点过"退出登录"之后要的就是这个效果 —— 否则会闷头进同一个账号。
    /// </param>
    public LoginWindow(MicrosoftAuth auth, bool signOutFirst = false)
    {
        InitializeComponent();
        _auth = auth;
        _signOutFirst = signOutFirst;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 浏览器数据的存放处。
    ///
    /// 必须自己指定：默认位置由 WebView2 按 exe 名去猜，我们既不知道它在哪，
    /// 也没法在"想清掉登录状态"的时候找到它。放到启动器自己的目录下，
    /// 卸载/重置时也一起带走。
    /// </summary>
    private static string UserDataFolder
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PclLike", "WebView2");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            await Browser.EnsureCoreWebView2Async(env);

            var core = Browser.CoreWebView2;

            // 拦掉导航：回调地址一出现，说明用户登完了，把 code 拿出来关窗。
            core.NavigationStarting += OnNavigationStarting;

            // 登录页里的弹窗（"使用其他账号"之类）也走同一套拦截规则。
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                Browser.CoreWebView2.Navigate(args.Uri);
            };

            // 只挡真实导航，别把页面自己的 iframe 拦了。
            core.Settings.AreDefaultScriptDialogsEnabled = true;

            if (_signOutFirst)
            {
                StatusText.Text = "正在退出浏览器里记住的微软账号…";
                core.Navigate(SignOutUrl);
                await Task.Delay(1500);
            }

            StatusText.Text = "正在打开登录页面…";
            core.Navigate(_auth.BuildLiveAuthorizeUrl());
        }
        catch (Exception ex)
        {
            Failure = "打不开内嵌浏览器（WebView2）：" + ex.Message;
            StatusText.Text = Failure;
            MessageBox.Show(this,
                Failure + "\n\n本机需要 Edge WebView2 运行时。装好后重开启动器再试。",
                "无法登录", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    /// <summary>
    /// 换个微软账号：先去微软登出，再回到登录页。
    ///
    /// 这是"登错账号"唯一可靠的出路 —— 内嵌浏览器（WebView2）自己有一份
    /// cookie，直接重开登录页会闷头进上次那个账号，不给选账号的机会。
    /// </summary>
    private async void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null) return;

        SwitchAccountButton.IsEnabled = false;
        StatusText.Text = "正在退出浏览器里记住的微软账号…";

        Browser.CoreWebView2.Navigate(SignOutUrl);
        await Task.Delay(1500);

        StatusText.Text = "已退出。请用另一个账号登录。";
        Browser.CoreWebView2.Navigate(_auth.BuildLiveAuthorizeUrl());

        SwitchAccountButton.IsEnabled = true;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        string? code = null;
        string? error = null;

        try
        {
            code = MicrosoftAuth.ReadCodeFromRedirect(e.Uri);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error is not null)
        {
            // 用户点了"取消"，或者微软明确拒绝了这次授权。
            // 这才是真的失败原因，别让它变成一个空白页。
            e.Cancel = true;
            _finished = true;
            Failure = error;
            StatusText.Text = error;
            DialogResult = false;
            Close();
            return;
        }

        if (code is null) return;

        e.Cancel = true;          // 别真去加载那个回调页（它本来就是给桌面应用看的空页）
        _finished = true;
        Code = code;
        StatusText.Text = "登录成功，正在换取游戏档案…";
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_finished)
        {
            _finished = true;
            Failure = "已取消登录。";
        }
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// 诊断用：把登录页真加载一次，报出页面标题与最终地址。
    ///
    /// 它**不登录** —— 输账号密码必须由真人来。这个探针只回答一个问题：
    /// 本机这套 WebView2 加上那个应用号，能不能把微软的登录页渲染出来。
    /// 这是整条登录链里唯一没法用 HTTP 校验的一环（其余端点都在探针里
    /// 逐个打过了）。
    /// </summary>
    internal async Task<string> ProbePageAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (Browser.CoreWebView2 is null && DateTime.UtcNow < deadline)
            await Task.Delay(200).ConfigureAwait(true);

        var core = Browser.CoreWebView2;
        if (core is null) return "FAIL：WebView2 核心没起来（运行时装了吗？）";

        var tcs = new TaskCompletionSource<bool>();

        void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e)
            => tcs.TrySetResult(e.IsSuccess);

        core.NavigationCompleted += OnDone;
        core.Navigate(_auth.BuildLiveAuthorizeUrl());

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(true);
        core.NavigationCompleted -= OnDone;

        var loaded = winner == tcs.Task && tcs.Task.Result;
        var title = core.DocumentTitle;
        var url = core.Source;

        return $"{(loaded ? "OK" : "FAIL")} | 标题=\"{title}\" | 地址={url}";
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (Browser.CoreWebView2 is not null)
                Browser.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            Browser.Dispose();
        }
        catch
        {
            // 关窗时的清理失败无所谓，别让它变成一次崩溃
        }
        base.OnClosed(e);
    }
}