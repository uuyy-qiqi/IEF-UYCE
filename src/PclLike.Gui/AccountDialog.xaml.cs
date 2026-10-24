using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using PclLike.Core.Login;

namespace PclLike.Gui;

/// <summary>
/// 账号对话框：四态切换（选择方式 / 内嵌浏览器登录 / device code 等待 / 已登录档案）。
///
/// ## 为什么在同一窗口里切面板，而不是每步开一个新窗口
///
/// 正版登录是个**异步长流程**——用户要在浏览器里登录，可能花 30 秒也可能花 3 分钟。
/// 这期间对话框必须一直开着，让用户随时能看到自己走到哪一步了。
/// 所以不能"点了登录就关窗"。
///
/// device code 那一态是为**自注册的 Azure 应用**留的（设置里填了 GUID 才走）。
/// 默认的正版路是内嵌浏览器：它由 <see cref="LoginWindow"/> 承载，本窗口在它
/// 弹出期间只负责把按钮按下去，避免用户重复点。
/// </summary>
public partial class AccountDialog : Window
{
    private readonly AccountManager _accounts;
    private readonly string? _clientId;
    private MicrosoftAuth.DeviceCodeInfo? _deviceInfo;
    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>
    /// 下次登录时先让浏览器退出记住的那个微软账号。
    ///
    /// 用户点过"退出登录"之后要的就是这个效果：不然再点登录会闷头进同一个号，
    /// 看着像"退出登录没生效"。
    /// </summary>
    private bool _signOutFirst;

    public AccountDialog(AccountManager accounts, string? clientId = null)
    {
        InitializeComponent();
        _accounts = accounts;
        _clientId = clientId;
        OfflineNameBox.Text = accounts.OfflineName;

        // 用户之前照老提示注册过 Azure 应用的话，设置里还留着那个 GUID。
        // 它登不进游戏，得当面告诉他，别让他以为设置生效了。
        if (new MicrosoftAuth(clientId).CustomClientIdIgnored)
        {
            CustomIdNote.Text =
                "设置里填的应用号是自注册的 Azure 应用 —— 实测它能走完登录前半程，" +
                "但 Minecraft 不认它（会回 403 Invalid app registration），" +
                "所以这里已改用内置应用号。设置里那一栏可以留空。";
            CustomIdNote.Visibility = Visibility.Visible;
        }

        Refresh();
    }

    /// <summary>按当前账号状态切换面板。</summary>
    private void Refresh()
    {
        var prof = _accounts.Profile;

        if (prof is not null)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            BusyPanel.Visibility = Visibility.Collapsed;
            DevicePanel.Visibility = Visibility.Collapsed;
            ProfilePanel.Visibility = Visibility.Visible;

            ProfileName.Text = prof.Name;
            ProfileInitial.Text = Initial(prof.Name);
            ProfileKind.Text = prof.IsOnline ? "正版账号" : "离线账号";
            ProfileUuid.Text = prof.IsOnline ? $"UUID  {prof.Uuid}" : "离线模式下用这个名字进游戏";
        }
        else if (_busy)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            BusyPanel.Visibility = Visibility.Visible;
            DevicePanel.Visibility = Visibility.Collapsed;
            ProfilePanel.Visibility = Visibility.Collapsed;
        }
        else if (_deviceInfo is not null)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            BusyPanel.Visibility = Visibility.Collapsed;
            DevicePanel.Visibility = Visibility.Visible;
            ProfilePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            LoginPanel.Visibility = Visibility.Visible;
            BusyPanel.Visibility = Visibility.Collapsed;
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

        // 状态消息同时写两处：设备码那条路看的是 DeviceStatus，
        // 换令牌/换档案那段看的是 BusyStatus。
        var progress = new Progress<string>(msg =>
        {
            BusyStatus.Text = msg;
            DeviceStatus.Text = msg;
        });

        // 应用号被别人清空过就给个说明。注意这里**不再**引导用户去注册 Azure 应用了：
        // 实测自己注册的 Entra 应用能跑到 XSTS，但最后会被 Minecraft 拒
        // （403 Invalid app registration）。能让正版登录真正走通的只有 Live Connect。
        if (!auth.IsConfigured)
        {
            MsLoginButton.IsEnabled = true;
            MessageBox.Show(this,
                MicrosoftAuth.MissingClientIdError().Message,
                "登录应用号是空的", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();
            MicrosoftAuth.MsToken ms;

            if (auth.UsesLiveConnect)
            {
                // ── 正版路：内嵌浏览器登微软，登完把回调地址里的 code 交回来 ──
                var win = new LoginWindow(auth, _signOutFirst) { Owner = this };
                _signOutFirst = false;
                win.ShowDialog();

                if (win.Code is null)
                {
                    MessageBox.Show(this, win.Failure ?? "已取消登录。", "登录",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _busy = true;
                BusyStatus.Text = "正在换取登录令牌…";
                Refresh();

                ms = await auth.ExchangeLiveCodeAsync(win.Code, _cts.Token);
            }
            else
            {
                // ── 自注册 Azure 应用路：设备码 ──
                // 这条路留着的唯一理由是排查（能复现 Invalid app registration）。
                _deviceInfo = await auth.StartDeviceCodeAsync(_cts.Token);

                UserCodeText.Text = _deviceInfo.UserCode;
                DeviceStatus.Text = "请打开浏览器，输入上面的验证码。";
                Refresh();

                // 自动打开浏览器并把码放到剪贴板——用户只需要粘贴
                TryOpenBrowser(_deviceInfo.VerificationUriComplete);

                ms = await auth.WaitForTokenAsync(_deviceInfo, progress, _cts.Token);
            }

            // ── 换 Minecraft 档案（两条路共用）──
            _deviceInfo = null;
            _busy = true;
            BusyStatus.Text = "正在换取游戏档案…";
            Refresh();

            var profile = await auth.CompleteLoginAsync(ms, progress, _cts.Token);

            // 保存（含 refresh token，下次免登录）
            _accounts.SetOnlineProfile(profile.Uuid, profile.Name, profile.AccessToken, ms.RefreshToken);

            _busy = false;
            Refresh();
            AccountChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            BusyStatus.Text = "已取消。";
        }
        catch (Exception ex)
        {
            // 登录失败的原因往往很具体（应用号不被认可 / 没 Xbox 账号 / 未成年），
            // 直接原样展示，别用"登录失败"把有用信息盖掉
            MessageBox.Show(this, ex.Message, "登录失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _deviceInfo = null;
            _busy = false;
            MsLoginButton.IsEnabled = true;
            Refresh();
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

        // 光清掉启动器记的档案不够：内嵌浏览器那边还记着微软的登录状态，
        // 下次点登录会直接进同一个账号。标记一下，让下次登录先登出。
        _signOutFirst = true;

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