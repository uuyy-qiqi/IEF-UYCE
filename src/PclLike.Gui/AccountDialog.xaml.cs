using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using PclLike.Core.Login;

namespace PclLike.Gui;

/// <summary>
/// 账号对话框：三态切换（选择方式 / device code 等待 / 已登录档案）。
///
/// ## 为什么用三个面板切换而不是多开窗口
///
/// device code 登录是个**异步长流程**——用户要在浏览器里输码，
/// 可能花 30 秒也可能花 3 分钟。这期间对话框必须一直开着显示验证码，
/// 否则用户就忘了码。所以不能"点了登录就关窗"。
///
/// 三个面板在同一个窗口里切，用户随时能看到自己走到哪一步了。
/// </summary>
public partial class AccountDialog : Window
{
    private readonly AccountManager _accounts;
    private readonly string? _clientId;
    private MicrosoftAuth.DeviceCodeInfo? _deviceInfo;
    private CancellationTokenSource? _cts;

    public AccountDialog(AccountManager accounts, string? clientId = null)
    {
        InitializeComponent();
        _accounts = accounts;
        _clientId = clientId;
        OfflineNameBox.Text = accounts.OfflineName;
        Refresh();
    }

    /// <summary>按当前账号状态切换面板。</summary>
    private void Refresh()
    {
        var prof = _accounts.Profile;

        if (prof is not null)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            DevicePanel.Visibility = Visibility.Collapsed;
            ProfilePanel.Visibility = Visibility.Visible;

            ProfileName.Text = prof.Name;
            ProfileInitial.Text = Initial(prof.Name);
            ProfileKind.Text = prof.IsOnline ? "正版账号" : "离线账号";
            ProfileUuid.Text = prof.IsOnline ? $"UUID  {prof.Uuid}" : "离线模式下用这个名字进游戏";
        }
        else if (_deviceInfo is not null)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            DevicePanel.Visibility = Visibility.Visible;
            ProfilePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            LoginPanel.Visibility = Visibility.Visible;
            DevicePanel.Visibility = Visibility.Collapsed;
            ProfilePanel.Visibility = Visibility.Collapsed;
        }
    }

    private static string Initial(string name)
        => string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();

    // ---------------------------------------------------------------
    // 正版登录
    // ---------------------------------------------------------------

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        MsLoginButton.IsEnabled = false;

        var auth = new MicrosoftAuth(_clientId);

        // 没配 client_id 就直接给注册指引，别让用户看 AADSTS700016
        if (!auth.IsConfigured)
        {
            MsLoginButton.IsEnabled = true;
            var r = MessageBox.Show(this,
                MicrosoftAuth.MissingClientIdError().Message + "\n\n要现在打开 Azure 门户去注册吗？",
                "需要先注册一个应用", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (r == MessageBoxResult.OK)
                TryOpenUrl("https://portal.azure.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/RegisteredApps");
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();

            // 步骤 1：拿 device code
            _deviceInfo = await auth.StartDeviceCodeAsync(_cts.Token);

            UserCodeText.Text = _deviceInfo.UserCode;
            DeviceStatus.Text = "请打开浏览器，输入上面的验证码。";
            Refresh();

            // 自动打开浏览器并把码放到剪贴板——用户只需要粘贴
            TryOpenBrowser(_deviceInfo.VerificationUriComplete);

            // 步骤 2：轮询等用户完成
            var progress = new Progress<string>(msg => DeviceStatus.Text = msg);
            var msToken = await auth.WaitForTokenAsync(_deviceInfo, progress, _cts.Token);

            // 步骤 3~5：换 Minecraft 档案
            var profile = await auth.CompleteLoginAsync(msToken, progress, _cts.Token);

            // 保存（含 refresh token，下次免登录）
            _accounts.SetOnlineProfile(profile.Uuid, profile.Name, profile.AccessToken, msToken.RefreshToken);

            _deviceInfo = null;
            DeviceProgress.IsIndeterminate = false;
            Refresh();
            AccountChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            _deviceInfo = null;
            DeviceStatus.Text = "已取消。";
            Refresh();
        }
        catch (Exception ex)
        {
            _deviceInfo = null;
            DeviceProgress.IsIndeterminate = false;
            Refresh();

            // 登录失败的原因往往很具体（没买游戏 / 没 Xbox 账号 / 未成年），
            // 直接原样展示，别用"登录失败"把有用信息盖掉
            MessageBox.Show(this, ex.Message, "登录失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            MsLoginButton.IsEnabled = true;
        }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceInfo is null) return;
        TryOpenBrowser(_deviceInfo.VerificationUriComplete);
    }

    private void TryOpenBrowser(string url)
    {
        try
        {
            Clipboard.SetText(UserCodeText.Text);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            DeviceStatus.Text = "已打开浏览器，验证码已复制到剪贴板。";
        }
        catch
        {
            DeviceStatus.Text = $"请手动打开 {url}";
        }
    }

    private static void TryOpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ---------------------------------------------------------------
    // 离线登录
    // ---------------------------------------------------------------

    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        var name = OfflineNameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请输入一个游戏内名字。", "离线模式",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 离线名有字符限制，非法字符会让服务端拒绝连接
        if (!IsValidOfflineName(name))
        {
            MessageBox.Show(this,
                "名字只能用字母、数字和下划线，长度 3~16 个字符。",
                "离线模式", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _accounts.SetOffline(name);
        Refresh();
        AccountChanged?.Invoke();
    }

    /// <summary>
    /// 离线名规则：ASCII 字母数字下划线，3~16 位。
    ///
    /// **必须限定 ASCII**。不能用 <c>char.IsLetterOrDigit</c>——
    /// 它对汉字、西里尔、日文假名都返回 true，会放进"玩家名"这类名字。
    /// 后果不是显示难看，而是实际的连服故障：多数服务端插件的
    /// 名字校验与记分板宽度按 ASCII 假设，非 ASCII 名会导致
    /// 玩家列表错位甚至连接被拒。
    /// </summary>
    private static bool IsValidOfflineName(string name)
    {
        if (name.Length is < 3 or > 16) return false;
        foreach (var c in name)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                     || (c >= '0' && c <= '9') || c == '_';
            if (!ok) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "确定要退出登录吗？", "退出登录",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        _accounts.Clear();
        Refresh();
        AccountChanged?.Invoke();
    }

    /// <summary>账号状态变化时通知主窗口刷新顶部栏。</summary>
    public event Action? AccountChanged;

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosed(e);
    }
}