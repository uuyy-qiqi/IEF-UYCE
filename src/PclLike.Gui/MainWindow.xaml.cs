using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PclLike.Core.Skins;
using PclLike.Gui.Skins;

namespace PclLike.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>皮肤服务：负责按玩家名查皮肤并在界面里渲染 3D 头像。</summary>
    private readonly SkinService _skins = new();

    /// <summary>当前大页签：launch / download / settings / tools。</summary>
    private string _topTab = "launch";

    /// <summary>左栏当前选中的子项 id。</summary>
    private string _navKey = "launch";

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(App.GameRoot);
        DataContext = _vm;

        // 先把内置 Steve 头像画上。必须是同步的：网络查皮肤要几百毫秒，
        // 那段时间界面不能是一片空白。
        ApplyDefaultHead();

        // 再异步换成玩家的真实皮肤
        _ = LoadSkinAsync();

        Loaded += OnLoaded;
        Closing += OnClosing;

        // 昵称变化时重新拉皮肤（离线改名后头像要跟着变）
        _vm.PropertyChanged += OnVmPropertyChanged;

        BuildNav();
        ShowTopTab("launch");
    }

    /// <summary>
    /// 窗口句柄一就绪就给它打开亚克力模糊。
    ///
    /// 必须在 SourceInitialized 之后才能拿句柄 —— 在这之前 HWND 还是 0，
    /// 底层那个 SetWindowCompositionAttribute 会直接失败。系统不支持时它只是
    /// 静默返回 false，窗口就还是主题里那个半透明浅色底，不影响使用。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 窗口是 AllowsTransparency="True"（分层窗口），走的是 WPF 自己的逐像素
        // alpha 合成 —— 真正透出桌面。
        //
        // 为什么不叠 DWM 那层亚克力：实测（tools\shots\s_bare_wallpaper.png 与
        // s_real_wallpaper.png 逐像素比）Win11 的亚克力材质本身几乎是白的，盖上
        // 去之后即使把我们的 alpha 从 30% 调到 15% 也测不出任何变化 —— 看上去
        // 就是一块不透明的白板。分层窗口还给不了 DWM 材质，两者只能选一个：
        // 选了"真的能看见桌面"。
        //
        // （这里以前还有一行 Console.WriteLine("[backdrop] 分层透明 = ...")：
        //   调试时用的，会把探针输出污染一行，已删。）

        // 无边框窗口的圆角得自己跟 DWM 要（Win11 才有；Win10 会失败，忽略即可）
        WindowBackdrop.TryRoundCorners(this);
    }

    /// <summary>
    /// 顶栏空白处按住可以拖窗口 —— 原生标题栏去掉以后得自己实现。
    ///
    /// 顶栏里的页签、按钮会把鼠标按下事件吃掉（ButtonBase 会捕获鼠标），
    /// 所以这里只会被"空白处"触发，点头上那些控件不会误拖。
    /// </summary>
    private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 双击时第一下已经进了 DragMove，系统会抛这个 —— 无所谓
        }
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>最大化/还原换一下按钮图标（E922 最大化 / E923 还原）。</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (MaxButton.Template?.FindName("Glyph", MaxButton) is TextBlock glyph)
            glyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    // ────────────────────────────────────────────────────────────
    // 导航
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 按当前大页签重建左侧导航。
    ///
    /// 分组照着 PCL2 的层次：每个大页下面再分几组，组内是具体面板。
    /// </summary>
    private void BuildNav()
    {
        NavPanel.Children.Clear();

        switch (_topTab)
        {
            case "launch":
                AddGroup("游戏");
                AddNav("launch", "\uE768", "启动");
                AddNav("java", "\uE943", "Java");
                AddNav("manage", "\uE8F1", "管理");
                AddGroup("工具");
                AddNav("online", "\uE8AF", "联机");
                AddGroup("启动器");
                AddNav("appearance", "\uE790", "个性化");
                AddNav("misc", "\uE712", "杂项");
                AddGroup("关于");
                AddNav("about", "\uE946", "软件信息");
                AddNav("update", "\uE895", "软件更新");
                AddNav("feedback", "\uE939", "反馈");
                AddNav("logs", "\uE9D9", "查看日志");
                break;

            case "download":
                AddGroup("Minecraft");
                AddNav("mc", "\uE7FC", "Minecraft");
                AddGroup("社区资源");
                AddNav("mods", "\uE7B8", "Mod");
                AddNav("pack", "\uE8B7", "整合包");
                AddNav("datapack", "\uE8C8", "数据包");
                AddNav("resource", "\uE91B", "资源包");
                AddNav("shader", "\uE706", "光影包");
                AddNav("world", "\uE909", "世界");
                AddNav("fav", "\uE734", "收藏夹");
                AddGroup("安装包");
                AddNav("installer-mc", "\uE896", "Minecraft");
                AddNav("optifine", "\uE9D2", "OptiFine");
                AddNav("forge", "\uE912", "Forge");
                AddNav("neoforge", "\uE90F", "NeoForge");
                AddNav("fabric", "\uE8FD", "Fabric");
                AddNav("quilt", "\uE8EF", "Quilt");
                AddNav("legacyfabric", "\uE8FD", "Legacy Fabric");
                AddNav("liteloader", "\uE7C1", "LiteLoader");
                AddNav("cleanroom", "\uE7C1", "Cleanroom");
                break;

            case "settings":
                AddGroup("游戏");
                AddNav("set-launch", "\uE768", "启动");
                AddNav("set-java", "\uE943", "Java");
                AddNav("set-account", "\uE77B", "账号");
                AddGroup("启动器");
                AddNav("set-appearance", "\uE790", "个性化");
                AddNav("set-misc", "\uE712", "杂项");
                break;

            case "tools":
                AddGroup("联机");
                AddNav("lobby", "\uE8AF", "大厅");
                AddGroup("奇妙小工具");
                AddNav("toolbox", "\uE7B8", "百宝箱");
                AddNav("help", "\uE897", "帮助库");
                break;
        }
    }

    private void AddGroup(string title)
    {
        NavPanel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("NavGroupHeader"),
        });
    }

    private void AddNav(string key, string icon, string label)
    {
        var rb = new RadioButton
        {
            GroupName = "Nav",
            Style = (Style)FindResource("NavItem"),
            Tag = icon,
            Content = label,
            IsChecked = key == _navKey,
        };
        rb.Checked += (_, _) =>
        {
            _navKey = key;
            OnNavChanged(key);
        };
        NavPanel.Children.Add(rb);
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;

        var tab = rb.Name switch
        {
            nameof(TabDownload) => "download",
            nameof(TabSettings) => "settings",
            nameof(TabTools) => "tools",
            _ => "launch",
        };

        ShowTopTab(tab);
    }

    private void ShowTopTab(string tab)
    {
        _topTab = tab;

        // 换大页签时把左栏重置到该页的第一项
        _navKey = tab switch
        {
            "download" => "mc",
            "settings" => "set-launch",
            "tools" => "lobby",
            _ => "launch",
        };

        // 顶栏高亮要跟着走。
        //
        // 这是个无边框窗口，顶栏用的是 RadioButton 组 —— 代码里改页签时
        // 不会自动勾选对应的那个，所以这里手动同步。注意这四个都在
        // GroupName="TopTab" 组里，设一个会把其他的自动取消。
        TabLaunch.IsChecked = tab == "launch";
        TabDownload.IsChecked = tab == "download";
        TabSettings.IsChecked = tab == "settings";
        TabTools.IsChecked = tab == "tools";

        PageLaunch.Visibility = tab == "launch" ? Visibility.Visible : Visibility.Collapsed;
        PageDownload.Visibility = tab == "download" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTools.Visibility = tab == "tools" ? Visibility.Visible : Visibility.Collapsed;

        // 安装包那一屏不跟大页签走：它是下载页左栏的一项，只在点左栏时露出来。
        PageInstaller.Visibility = Visibility.Collapsed;

        // 下载页有两屏：内置版本列表（安装包）和社区资源浏览。
        // 这儿先都收起来，具体进哪一屏由下面 BuildNav 触发的 OnNavChanged 决定 ——
        // 左栏第一项是"安装包 → Minecraft"，所以默认进版本列表。
        PageContent.Visibility = Visibility.Collapsed;

        // 左栏在两类页里是不同的东西：
        //   启动页 —— 一张玩家卡片（皮肤头像 + 名字 + 正版状态），没有子导航；
        //   其余页 —— 子导航列表。
        // PCL 也是这个做法，所以启动页的左栏不跟着子导航走。
        var isLaunch = tab == "launch";
        // 人卡常驻左上角（四个大页签都在），所以这里只在"导航"和"启动按钮"之间二选一：
        // 启动页左栏让位给启动按钮，其余页给导航。
        LaunchActions.Visibility = isLaunch ? Visibility.Visible : Visibility.Collapsed;
        NavHost.Visibility = isLaunch ? Visibility.Collapsed : Visibility.Visible;

        // 人卡的位置也跟着页签走（照 PCL CE 的启动页）：
        //   启动页 —— 上半格占满、人卡在其中垂直居中，底下两个按钮贴底；
        //   其余页 —— 人卡回到顶部，整块剩余高度让给导航列表。
        PlayerSection.Height = isLaunch
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
        NavSection.Height = isLaunch
            ? GridLength.Auto
            : new GridLength(1, GridUnitType.Star);
        PlayerHost.VerticalAlignment = isLaunch
            ? VerticalAlignment.Center
            : VerticalAlignment.Top;

        BuildNav();
    }

    /// <summary>启动页「没有可用 Java」提示里的引导：跳到设置页的 Java 子页。</summary>
    private void GoJava_Click(object sender, RoutedEventArgs e)
    {
        ShowTopTab("settings");
        _navKey = "set-java";
        BuildNav();
        OnNavChanged("set-java");
    }

    /// <summary>启动页「下载新版本」：跳到下载页的 Minecraft 子页。</summary>
    private void GoDownload_Click(object sender, RoutedEventArgs e)
    {
        ShowTopTab("download");
        _navKey = "mc";
        BuildNav();
        OnNavChanged("mc");
    }

    /// <summary>左栏子项切换：设置页和工具页都是分区切换，各自换一屏内容。</summary>
    private void OnNavChanged(string key)
    {
        SwitchSection(key);

        if (_topTab == "download") ShowDownloadSection(key);

        if (key == "logs")
        {
            // 「查看日志」= 开日志窗口（以前是展开窗口底部那个抽屉）
            OpenLogWindow();
        }
    }

    /// <summary>
    /// 按左栏 key 切换设置页 / 工具页里的分区。
    ///
    /// 设置页和工具页各自是一个 Grid，里面一个分区一个 StackPanel，
    /// 切页签就是把对应那个点亮、其余收起来 —— 和 PCL 的分区切换一致。
    /// 没匹配到 key 的时候（比如切大页签时左栏还没重建）按当前大页签
    /// 落回第一个分区，避免整页空白。
    /// </summary>
    /// <summary>
    /// 下载页左栏切屏。
    ///
    /// 左栏十三项其实归两类：
    ///   · 安装包（Minecraft / OptiFine / Forge / …）—— 看的是内置的九百多个版本，走版本列表那一屏；
    ///   · 社区资源（Mod / 整合包 / 数据包 / 资源包 / 光影包 / 世界 / 收藏夹）—— 走内容浏览那一屏。
    /// 两屏各自管自己的标题、搜索栏和列表，所以这里只切可见性 + 通知 ViewModel 换栏目。
    /// </summary>
    /// <summary>安装包页：重查这个 MC 版本下有哪些加载器版本。</summary>
    private async void InstallerRefresh_Click(object sender, RoutedEventArgs e)
    {
        await _vm.Installer.RefreshAsync();
    }

    /// <summary>安装包页：装这一行那个加载器版本。</summary>
    private async void InstallerInstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallerRow row) return;
        await _vm.Installer.InstallAsync(row);
    }

    private void ShowDownloadSection(string key)
    {
        var tab = key switch
        {
            "mods" => (ContentTab?)ContentTab.Mod,
            "pack" => ContentTab.ModPack,
            "datapack" => ContentTab.Datapack,
            "resource" => ContentTab.ResourcePack,
            "shader" => ContentTab.Shader,
            "world" => ContentTab.World,
            "fav" => ContentTab.Favorite,
            _ => null,
        };

        // 「安装包」里除 Minecraft 之外的几项走加载器安装屏（InstallerViewModel）。
        // Minecraft 那一项就是原版，不归它管，仍然走版本列表。
        //
        // 这个名单必须和上面 AddNav 出来的键一一对上：漏一个，那一项点下去就
        // 只会切到"版本列表"屏，看着像没反应（LiteLoader/Cleanroom 就这样漏过）。
        if (key is "fabric" or "quilt" or "forge" or "neoforge" or "optifine"
                or "legacyfabric" or "liteloader" or "cleanroom")
        {
            PageDownload.Visibility = Visibility.Collapsed;
            PageContent.Visibility = Visibility.Collapsed;
            PageInstaller.Visibility = Visibility.Visible;
            _vm.Installer.Activate(key);
            return;
        }

        if (tab is null)
        {
            // 安装包那一类：版本列表
            PageInstaller.Visibility = Visibility.Collapsed;
            PageContent.Visibility = Visibility.Collapsed;
            PageDownload.Visibility = Visibility.Visible;
            return;
        }

        PageDownload.Visibility = Visibility.Collapsed;
        PageInstaller.Visibility = Visibility.Collapsed;
        PageContent.Visibility = Visibility.Visible;
        _vm.Content.Activate(tab.Value);
    }

    private void SwitchSection(string key)
    {
        var sections = new[]
        {
            SetSecLaunch, SetSecJava, SetSecAccount, SetSecAppearance, SetSecMisc,
            ToolSecLobby, ToolSecBox, ToolSecHelp,
        };

        foreach (var s in sections)
            s.Visibility = Visibility.Collapsed;

        var target = key switch
        {
            "set-launch" => SetSecLaunch,
            "set-java" => SetSecJava,
            "set-account" => SetSecAccount,
            "set-appearance" => SetSecAppearance,
            "set-misc" => SetSecMisc,
            "lobby" => ToolSecLobby,
            "toolbox" => ToolSecBox,
            "help" => ToolSecHelp,
            _ => null,
        };

        target ??= _topTab switch
        {
            "settings" => SetSecLaunch,
            "tools" => ToolSecLobby,
            _ => null,
        };

        if (target is not null)
            target.Visibility = Visibility.Visible;

        // 百宝箱顶上那张「目录体检」的数字得现算。第一次进来算一次就够了，
        // 之后想让数字更新就点那张卡上的「刷新」——不然每切一次页都把几万个
        // 文件数一遍，白烧磁盘。
        if (ReferenceEquals(target, ToolSecBox) && !_vm.HasSurvey)
            _vm.RefreshSurvey();

        // 服务器列表是游戏自己那份 servers.dat —— 用户可能在游戏里加过服务器，
        // 所以每次进大厅都重读一次文件。读一个小 NBT 文件，代价可以忽略。
        if (ReferenceEquals(target, ToolSecLobby))
        {
            _vm.Lobby.RefreshServers();
            _vm.Lobby.SetStatus("");
        }
    }

    /// <summary>供截图探针切页用。</summary>
    internal void NavigateForShot(string page)
    {
        ShowTopTab(page switch
        {
            "download" => "download",
            "settings" => "settings",
            "tools" => "tools",
            _ => "launch",
        });
    }

    /// <summary>供截图探针切分区用（设置页/工具页的左栏子项）。</summary>
    internal void SectionForShot(string key)
    {
        _navKey = key;
        BuildNav();
        OnNavChanged(key);
    }

    // ────────────────────────────────────────────────────────────
    // 皮肤
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 载入内置皮肤字节，并先画上"没有皮肤时那张脸"。
    ///
    /// <see cref="SkinService.DefaultSkinBytes"/> 必须有内容 —— 否则
    /// ResolveAsync 的网络兜底会返回空字节，TryParse 失败，最后什么都拿不到。
    /// 但那字节不拿来当头像：没皮肤可画时界面显示的是 avatar_fallback.png
    /// 那张方脸（用户挑的），比内置 Steve 好看得多。
    /// </summary>
    private void ApplyDefaultHead()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/default_skin.png", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is null) return;

            using var ms = new MemoryStream();
            info.Stream.CopyTo(ms);
            _skins.DefaultSkinBytes = ms.ToArray();

            // 头像先留空 —— 让 PlayerFallback 那张图露出来。
            // 查不到真皮肤时这就是最终显示的；查到了再换成皮肤脸。
            ShowFallbackAvatar();
        }
        catch
        {
            // 资源缺失不该让启动器打不开，最坏情况就是没头像
        }
    }

    /// <summary>显示"没有皮肤"那张默认脸。</summary>
    private void ShowFallbackAvatar()
    {
        PlayerHead.Head = null;
        PlayerHead.Visibility = Visibility.Collapsed;
        PlayerFallback.Visibility = Visibility.Visible;

        // 这里不再动 PlayerAuthText：有没有皮肤、和"是不是正版账号"是两件事。
        // 登录成功了但皮肤接口没查到（掉线、接口抽风），账号照样是正版 ——
        // 早先在这里写死"离线模式"，头像就成了账号状态的替身，会骗人。
        // 那一行现在由 RefreshPlayerCard 按账号状态决定。
    }

    /// <summary>切到真实皮肤脸。</summary>
    private void ShowSkinHead(SkinHead head)
    {
        PlayerHead.Head = head;
        PlayerHead.Visibility = Visibility.Visible;
        PlayerFallback.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 拉取并渲染头像。
    ///
    /// 顺序：正版登录时优先按 UUID 查（准），否则按玩家名查（离线场景），
    /// 查不到就停在默认那张脸上 —— 所以这个方法是"改善"而非"必须"。
    /// </summary>
    private async Task LoadSkinAsync()
    {
        try
        {
            // 正版账号有真实 uuid，按 uuid 查最准；离线账号只有名字，按名字查。
            var p = _vm.Accounts.Profile;
            var name = p?.Name ?? _vm.Username;
            var uuid = p is { IsOnline: true } ? p.Uuid : null;

            var result = await _skins.ResolveAsync(name, uuid).ConfigureAwait(true);

            // 查到的还是默认皮肤，就保持现状，别把头像重画一遍
            if (result.Source == SkinSource.Default) return;

            if (!SkinHead.TryParse(result.PngBytes, out var head))
                return;   // 字节不是标准皮肤，保持默认那张脸

            ShowSkinHead(head);

            // 皮肤只负责"本地皮肤"这一档 —— "正版验证"/"离线模式"由账号状态决定
            // （见 RefreshPlayerCard）。上面已经因为 Default 提前返回，所以这里
            // 剩下的只可能是真皮肤。
            if (result.Source == SkinSource.LocalFile)
                PlayerAuthText.Text = "本地皮肤";
        }
        catch
        {
            // 网络失败无所谓，默认那张脸已经在显示
        }
    }

    /// <summary>
    /// 刷新左边那张玩家卡：名字和身份都跟着账号走。
    ///
    /// 名字不能用 Config.Username —— 那是"离线名"。正版登录后真正的身份是
    /// 账号里的玩家名，启动时 --username 用的也是它。两个名字混着显示，
    /// 用户会以为自己在玩离线档（他就这么问过）。
    /// </summary>
    private void RefreshPlayerCard()
    {
        var p = _vm.Accounts.Profile;

        PlayerNameText.Text = string.IsNullOrWhiteSpace(p?.Name) ? _vm.Username : p!.Name;
        PlayerAuthText.Text = p is { IsOnline: true } ? "正版验证" : "离线模式";
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Username))
        {
            RefreshPlayerCard();
            _ = LoadSkinAsync();
        }
        else if (e.PropertyName is nameof(MainViewModel.AccountLabel))
        {
            // 登录 / 退出登录 / 续期成功都会让 AccountLabel 变，卡片要跟着换
            RefreshPlayerCard();
        }
    }

    // ────────────────────────────────────────────────────────────
    // 生命周期
    // ────────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.RefreshJava();
        UpdateJavaUi();
        _vm.RefreshAccount();
        RefreshPlayerCard();

        // 实例要先读出来：启动页那张卡显示的是实例名，启动时也按它决定 --gameDir
        _vm.RefreshInstances();
        RefreshInstanceCard();

        // 启动时按配置决定外观：先日志窗口，再默认页签
        if (_vm.Config.AutoExpandLog)
            OpenLogWindow();

        // 代理要在联网之前定下来（探测本机端口最多两秒，放后台，不挡窗口）
        _ = _vm.ApplyProxyAsync();

        var startTab = _vm.Config.DefaultTab;
        if (!string.IsNullOrWhiteSpace(startTab) && startTab != "launch")
            ShowTopTab(startTab);

        // 正版 token 会过期（约 24 小时）。启动时静默续期一次，
        // 用户不用每次开启动器都重新登录。失败不影响使用，退回离线即可。
        _ = Task.Run(async () =>
        {
            if (await _vm.TryRefreshAccountAsync())
                Dispatcher.Invoke(() =>
                {
                    _vm.RefreshAccount();
                    RefreshPlayerCard();
                    _ = LoadSkinAsync();
                });
        });

        await _vm.LoadVersionsAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _vm.Persist();

        if (_vm.Config.KillGameOnExit && _vm.IsGameRunning)
        {
            _vm.Cancel();
            _vm.KillRunningGame();
        }

        // 两个子 ViewModel 各带着一个 HttpClient 和一个 CancellationTokenSource，
        // 关窗时把它们收掉 —— 否则下载线程会在进程退出前继续往盘上写文件。
        _vm.DisposeChildren();
    }

    private void UpdateJavaUi()
    {
        NoJavaBox.Visibility = _vm.JavaList.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ────────────────────────────────────────────────────────────
    // 交互
    // ────────────────────────────────────────────────────────────

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AccountDialog(_vm.Accounts, _vm.Config.MicrosoftClientId) { Owner = this };
        dlg.AccountChanged += () =>
        {
            _vm.RefreshAccount();
            RefreshPlayerCard();
            _ = LoadSkinAsync();
        };
        dlg.ShowDialog();
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedVersion is null) return;

        _vm.Persist();

        // 「启动后隐藏启动器」：游戏跑的时候把窗口收起来，退出了再放回来。
        // LaunchAsync 会一直等到游戏进程结束，所以 finally 就是"游戏退出"。
        var hide = _vm.Config.HideOnLaunch;
        if (hide) Hide();

        try
        {
            await _vm.LaunchAsync();
        }
        finally
        {
            if (hide)
            {
                Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
            }
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedVersion is null) return;

        _vm.Persist();
        await _vm.InstallAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _vm.OpenGameFolder();

    private void InstancePick_Click(object sender, RoutedEventArgs e) => OpenInstances();

    /// <summary>实例管理窗口。关掉后刷新启动页那张「当前实例」卡。</summary>
    private void OpenInstances()
    {
        var dlg = new InstancesWindow(_vm) { Owner = this };
        dlg.ShowDialog();

        _vm.RefreshInstances();
        RefreshInstanceCard();
    }

    /// <summary>
    /// 给截图探针用的实例窗口。
    ///
    /// 实例管理是个独立对话框，不是主窗口的一页，所以每页截图那套拿不到它 ——
    /// 单独开一个不设 Owner 的（探针里 GetWindow(this) 会返回 null，设 Owner 会在
    /// Show() 时抛异常）。
    /// </summary>
    internal Window InstancesForShot() => new InstancesWindow(_vm);

    /// <summary>截图探针要造日志窗口，需要拿到 ViewModel。</summary>
    internal MainViewModel ViewModelForShot() => _vm;

    /// <summary>
    /// 账号对话框也是独立窗口，同样得单独开一个给截图探针用。
    ///
    /// 它现在有两种长相，取决于设置里那个应用号：默认（内嵌浏览器登录）和
    /// "自定义应用号被忽略"（会多出一行黄字说明）。探针要能截到后者 ——
    /// 那行字是给用户看的解释，写错了没人会知道。
    /// </summary>
    internal Window AccountForShot() => new AccountDialog(_vm.Accounts, _vm.Config.MicrosoftClientId);

    /// <summary>
    /// 把「当前实例」卡上的文字刷新一遍。
    ///
    /// 绑定了 InstanceLabel 的地方其实能自己更新，这里顺带把目录也写出来 ——
    /// 用户最需要知道的就是"存档到底在哪个目录"。
    /// </summary>
    private void RefreshInstanceCard()
    {
        if (InstancePathText is null) return;

        var inst = _vm.SelectedInstance;
        InstancePathText.Text = inst is null
            ? "还没选实例，启动时会自动用默认实例"
            : $"{inst.IsolationLabel} · {inst.GameDir}";
    }

    private void InstanceSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_vm) { Owner = this };
        if (dlg.ShowDialog() == true) UpdateJavaUi();
    }

    /// <summary>面板折叠：点标题切换对应内容块的可见性。</summary>
    private void Fold_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton tb) return;

        var panel = tb.Name switch
        {
            nameof(FoldInstalled) => PanelInstalled,
                nameof(FoldRelease) => PanelRelease,
            nameof(FoldSnapshot) => PanelSnapshot,
            nameof(FoldOld) => PanelOld,
            nameof(FoldJoke) => PanelJoke,
            nameof(FoldAdvanced) => PanelAdvanced,
            _ => null,
        };

        if (panel is not null)
            panel.Visibility = tb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private LogWindow? _logWindow;

    /// <summary>
    /// 打开日志窗口。
    ///
    /// 日志以前是挂在窗口底部的一个抽屉。它会一直占着窗口最下面那一条，
    /// 而且正好压在左栏「启动游戏」下面，看着很脏 —— 现在挪进独立窗口，
    /// 想看的时候再开（工具 → 百宝箱 → 启动器日志，或帮助库 → 查看日志）。
    ///
    /// 只留一个实例：再点一次就把已经开着的那个拉到前面，
    /// 不然用户会开出一堆一模一样的日志窗。
    /// </summary>
    private void OpenLogWindow()
    {
        if (_logWindow is { IsLoaded: true })
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow(_vm) { Owner = this };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    private void ShowLog_Click(object sender, RoutedEventArgs e) => OpenLogWindow();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _vm.ClearLog();

    // ── 奇妙小工具 → 百宝箱 ──

    /// <summary>重新体检：算一遍体积 / 存档 / 截图 / 崩溃报告。</summary>
    private void RefreshSurvey_Click(object sender, RoutedEventArgs e) => _vm.RefreshSurvey();

    /// <summary>
    /// 打开当前实例里的某个子目录（按钮的 Tag 写子目录名）。
    ///
    /// 目录不存在时不去创建 —— 用户点「打开截图目录」是想确认截图还在不在，
    /// 顺手造一个空目录只会让他以为截图丢了。
    /// </summary>
    private void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string sub) return;

        var path = Path.Combine(_vm.GameDirNow(), sub);

        if (!Directory.Exists(path))
        {
            _vm.SetToolStatus($"还没有 {sub} 目录（{path}）—— 没导出过，或者当前实例不对。");
            return;
        }

        MainViewModel.OpenInExplorer(path);
    }

    /// <summary>存档那一行的「管理」：跳下载页的「世界」分区。</summary>
    private void ManageWorlds_Click(object sender, RoutedEventArgs e)
    {
        ShowTopTab("download");
        _navKey = "world";
        BuildNav();
        OnNavChanged("world");
    }

    /// <summary>用系统默认程序打开最新的那份崩溃报告。</summary>
    private void OpenLatestCrash_Click(object sender, RoutedEventArgs e)
    {
        var crash = _vm.Survey?.LatestCrash;

        if (crash is null)
        {
            _vm.SetToolStatus("没有崩溃报告。启动失败多半写在底部日志里，展开看看。");
            return;
        }

        MainViewModel.OpenInExplorer(crash.Path);
    }

    /// <summary>
    /// 扫一遍垃圾，把清单和体积摆给用户看，确认了才删。
    ///
    /// 这一屏唯一会动用户文件的操作，所以坚持两步：先 Scan 报数，
    /// 再 Clean —— 不把"扫描"和"删除"合成一个按钮。
    /// </summary>
    private void CleanJunk_Click(object sender, RoutedEventArgs e)
    {
        var scan = _vm.ScanJunk();

        if (!scan.Any)
        {
            _vm.SetToolStatus("没找到可清理的东西 —— 日志、崩溃报告、下载残留都很干净。");
            return;
        }

        var preview = string.Join("\n", scan.Paths.Take(8));
        if (scan.Count > 8) preview += $"\n…还有 {scan.Count - 8} 个";

        var answer = MessageBox.Show(
            $"可清理 {scan.Count} 个文件，共 {scan.SizeText}：\n\n{preview}\n\n" +
            "存档、Mod、配置、版本文件一概不动。要继续吗？",
            "清理垃圾", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        var result = _vm.CleanJunk(scan);

        _vm.SetToolStatus(result.Failed == 0
            ? $"清理完成：删掉 {result.Deleted} 个文件，腾出 {result.SizeText}。"
            : $"删掉 {result.Deleted} 个，{result.Failed} 个被占用没删成（游戏正开着的时候常见）。");
    }

    /// <summary>
    /// 复制最近一次真正执行过的启动命令。
    ///
    /// 取启动日志的首行原文，不按当前配置重新拼一遍 —— 重拼会漏掉规则裁剪、
    /// natives 解压路径这些细节，贴出去是错的，反而更难排查。
    /// </summary>
    private void CopyLaunchCommand_Click(object sender, RoutedEventArgs e)
    {
        var cmd = _vm.LastLaunchCommand();

        if (string.IsNullOrWhiteSpace(cmd))
        {
            _vm.SetToolStatus("还没启动过，没有命令可复制。先启动一次游戏。");
            return;
        }

        CopyText(cmd, $"已复制启动命令（{cmd.Length} 个字符）。");
    }

    /// <summary>复制一份拼好的诊断信息，求助时可以直接贴。</summary>
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var text = _vm.BuildDiagnostics();
        CopyText(text, $"已复制诊断信息（{text.Length} 个字符）。");
    }

    /// <summary>复制到剪贴板。剪贴板偶尔会被别的程序占着，所以吃掉失败只记日志。</summary>
    private void CopyText(string text, string okMessage)
    {
        try
        {
            Clipboard.SetText(text);
            _vm.SetToolStatus(okMessage);
            _vm.AppendLog(LogKind.Info, okMessage);
        }
        catch (Exception ex)
        {
            _vm.SetToolStatus($"复制失败：{ex.Message}");
        }
    }

    // ── 下载 → 社区资源 ──

    private async void ContentSearch_Click(object sender, RoutedEventArgs e)
        => await _vm.Content.SearchAsync();

    private async void ContentInstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ContentRow row) return;

        await _vm.Content.InstallAsync(row);
    }

    private void ContentFav_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ContentRow row) return;

        _vm.Content.ToggleFavorite(row);
    }

    private async void WorldImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选一个存档压缩包",
            Filter = "存档压缩包|*.zip|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) != true) return;

        await _vm.Content.ImportWorldAsync(dlg.FileName);
    }

    private async void WorldExport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PclLike.Core.Content.WorldInfo world) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"把「{world.Name}」导出成 zip",
            Filter = "存档压缩包|*.zip",
            FileName = world.Name + ".zip",
            DefaultExt = ".zip",
        };

        if (dlg.ShowDialog(this) != true) return;

        await _vm.Content.ExportWorldAsync(world.Name, dlg.FileName);
    }

    private void WorldDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PclLike.Core.Content.WorldInfo world) return;

        var answer = MessageBox.Show(
            this,
            $"删掉世界「{world.Name}」（{world.SizeText}）？\n\n" +
            "存档文件会被直接删除，不进回收站，删了找不回来。\n" +
            "还没备份过的话，先用【导出 zip】存一份再删。",
            "删除世界", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        _vm.Content.DeleteWorld(world.Name);
    }

    private void WorldOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PclLike.Core.Content.WorldInfo world) return;

        MainViewModel.OpenInExplorer(world.Path);
    }

    private void OpenContentFolder_Click(object sender, RoutedEventArgs e)
        => MainViewModel.OpenInExplorer(_vm.Content.ContentDirForTab());

    // ── 工具 → 大厅 ──

    /// <summary>
    /// 扫局域网。这是个 5 秒的操作，界面上得看得见"正在扫描"——
    /// 所以按钮文字会变、按钮自己会禁用（IsScanning 反向绑定）。
    /// </summary>
    private async void ScanLan_Click(object sender, RoutedEventArgs e)
        => await _vm.Lobby.ScanLanAsync();

    /// <summary>把扫到的世界写进服务器列表。行数据在按钮的 Tag 上。</summary>
    private void AddLanWorld_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LanRow row) return;

        _vm.Lobby.AddWorldToServers(row);
    }

    private void JoinLobby_Click(object sender, RoutedEventArgs e) => _vm.Lobby.Join();

    private void ClearLobbyCode_Click(object sender, RoutedEventArgs e)
    {
        _vm.Lobby.JoinCode = "";
        _vm.Lobby.SetStatus("已清空输入。");
    }

    private void PasteLobbyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.Lobby.JoinCode = Clipboard.GetText().Trim();
            _vm.Lobby.SetStatus("已从剪贴板粘贴，看一眼下面的解析结果对不对再点【加入】。");
        }
        catch (Exception ex)
        {
            _vm.Lobby.SetStatus($"读剪贴板失败：{ex.Message}");
        }
    }

    private void RefreshLocalIp_Click(object sender, RoutedEventArgs e)
        => _vm.Lobby.RefreshLocalAddresses();

    private void CreateLobbyCode_Click(object sender, RoutedEventArgs e)
        => _vm.Lobby.CreateCode();

    private void CopyCreatedCode_Click(object sender, RoutedEventArgs e)
        => CopyLobbyText(_vm.Lobby.CreatedCode, "编号已复制。");

    private void CopyCreatedAddress_Click(object sender, RoutedEventArgs e)
        => CopyLobbyText($"{_vm.Lobby.SelectedAddress}:{_vm.Lobby.PortText.Trim()}", "地址已复制。");

    private void RefreshServers_Click(object sender, RoutedEventArgs e)
    {
        _vm.Lobby.RefreshServers();
        _vm.Lobby.SetStatus($"服务器列表已重读，共 {_vm.Lobby.Servers.Count} 条。");
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PclLike.Core.Online.ServerEntry entry) return;

        _vm.Lobby.RemoveServer(entry);
    }

    /// <summary>大厅专用的复制：状态写在那一屏自己的状态条上，不借用百宝箱的。</summary>
    private void CopyLobbyText(string text, string okMessage)
    {
        if (text.Length == 0)
        {
            _vm.Lobby.SetStatus("没有可复制的内容。");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _vm.Lobby.SetStatus(okMessage);
            _vm.AppendLog(LogKind.Info, okMessage);
        }
        catch (Exception ex)
        {
            _vm.Lobby.SetStatus($"复制失败：{ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 拖放：把文件拖进窗口任何位置
    //
    // 难点不在"接收文件"，在于**收到之后怎么判断它是什么**。整合包、资源包、
    // 光影包、数据包、存档全都是 .zip —— 只看扩展名就会把存档装进
    // resourcepacks/。判断在 Core 的 DroppedContentClassifier 里，这里只管
    // 把路径交出去，再把结果说给用户听。
    //
    // 也正因为拖放本身没法自动测试（模拟不了操作系统级的拖拽），真正干活的
    // 那段逻辑抽在 MainViewModel.DropFilesAsync 里，`--drop` 探针直接调它。
    // ═══════════════════════════════════════════════════════════

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        HandleDragOver(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        HandleDragOver(e);
    }

    private void HandleDragOver(DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);

        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (!ok)
        {
            HideDropHint();
            return;
        }

        DropHintText.Text = _vm.SelectedInstance is null
            ? "松手就装进当前实例"
            : $"松手就装进「{_vm.SelectedInstance.Name}」";

        DropHint.Visibility = Visibility.Visible;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        HideDropHint();
    }

    /// <summary>正在处理一次拖放导入 —— 只管住"再来一次"，不再禁用整个窗口。</summary>
    private bool _importBusy;

    protected override async void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        HideDropHint();

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        // 这里原来对**整个窗口**做了 `IsEnabled = false`，理由是"装整合包要几分钟，
        // 别让用户看着界面像卡死了" —— 结果恰恰相反：窗口是活的，但所有控件全被
        // 禁用了，连日志窗口都打不开。用户的原话是
        //   「导入进去卡住了页面什么都点不了」
        // 就是这一行造成的。现在只挡重复拖放，其余照常能点。
        if (_importBusy)
        {
            _vm.SetStatus("上一批还在装，等它装完再拖 —— 进度在顶栏和日志里");
            return;
        }

        _importBusy = true;

        try
        {
            _vm.SetStatus("正在处理拖进来的文件…（进度在顶栏和日志里）");

            var summary = await _vm.DropFilesAsync(paths);

            RefreshInstanceCard();

            if (summary.Length > 0)
            {
                MessageBox.Show(this, summary, "拖放安装",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _vm.AppendLog(LogKind.Error, $"拖放处理失败 {ex.GetType().Name} —— {ex.Message}");
            MessageBox.Show(this, $"处理拖进来的文件时出错了：\n{ex.Message}", "拖放安装",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _importBusy = false;
        }
    }

    private void HideDropHint() => DropHint.Visibility = Visibility.Collapsed;

    // ── 设置 → Java ──

    private void RescanJava_Click(object sender, RoutedEventArgs e) => _vm.RefreshJava();

    private void PickJava_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选中一个 java.exe",
            Filter = "java 可执行文件|java.exe;java|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) == true)
            _vm.UseJavaBinary(dlg.FileName);
    }

    private async void DownloadJava_Click(object sender, RoutedEventArgs e)
    {
        // 问清要哪个大版本：1.17 之前要 8，1.17~1.20.4 要 17，1.20.5 起要 21
        var suggested = SuggestJavaMajor(_vm.SelectedVersion?.Id);

        var dlg = new JavaPickDialog(suggested) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        await _vm.DownloadJavaAsync(dlg.MajorVersion);
        UpdateJavaUi();
    }

    /// <summary>
    /// 按版本号推出该用哪个大版本的 Java。规则和「工具 → 帮助库」里写的一致。
    /// 快照和加载器版本号统一按现代版本（21）处理。
    /// </summary>
    private static int SuggestJavaMajor(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return 17;
        var id = versionId.Trim();

        var m = System.Text.RegularExpressions.Regex.Match(id, @"^1\.(\d+)(?:\.(\d+))?");
        if (m.Success)
        {
            var minor = int.Parse(m.Groups[1].Value);
            var patch = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;

            if (minor <= 16) return 8;
            if (minor >= 21) return 21;
            if (minor == 20) return patch >= 5 ? 21 : 17;
            return 17;   // 1.17 ~ 1.19
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{2}w\d{2}[a-z]$"))
            return 21;

        return 17;
    }

    private void CopyRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vm.GameRoot);
            _vm.AppendLog(LogKind.Info, "游戏目录路径已复制");
        }
        catch (Exception ex)
        {
            _vm.AppendLog(LogKind.Warning, $"复制失败：{ex.Message}");
        }
    }

    // ── 网络代理 ──

    /// <summary>
    /// 改代理模式后重新找一遍。
    ///
    /// 加 IsLoaded 判断是因为：绑定在窗口构造时就会把 SelectedValue 填进去，
    /// 那一下同样会触发 SelectionChanged —— 那时候窗口还没显示，没必要去探测。
    /// </summary>
    private void ProxyMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        _vm.Config.ProxyMode = (sender as ComboBox)?.SelectedValue as string ?? "auto";
        _vm.Persist();
        _ = _vm.ApplyProxyAsync();
    }

    /// <summary>
    /// 切换游戏文件的下载源。绑定在窗口构造时也会触发一次，那时候 Config 已经
    /// 被 MainViewModel 读过一遍并生效了，所以按 IsLoaded 挡掉，避免把还没加载完的
    /// 默认值写回去。
    /// </summary>
    private void DownloadMirror_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        _vm.Config.DownloadMirror = (sender as ComboBox)?.SelectedValue as string ?? "bmclapi";
        PclLike.Core.Net.MirrorSource.Current = PclLike.Core.Net.MirrorSource.Parse(_vm.Config.DownloadMirror);
        _vm.Persist();
        _vm.AppendLog(LogKind.Info, $"下载源：{PclLike.Core.Net.MirrorSource.DisplayName(PclLike.Core.Net.MirrorSource.Current)}");
    }

    private void ProxyUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _vm.Persist();
        _ = _vm.ApplyProxyAsync();
    }

    private void RefreshProxy_Click(object sender, RoutedEventArgs e) => _ = _vm.ApplyProxyAsync();

    /// <summary>
    /// 打开 Mod 管理窗口。
    ///
    /// 必须走和启动完全同一条实例定位逻辑 —— 否则会出现"Mod 是装上了，
    /// 启动却不生效"：Mod 管理落在 A 实例的 mods 目录，游戏按 B 实例启动。
    /// 以前这里按版本 id 另建过一个实例，就是这么错的。
    /// </summary>
    private void Mods_Click(object sender, RoutedEventArgs e)
    {
        var versionId = _vm.SelectedVersion?.Id;
        if (string.IsNullOrWhiteSpace(versionId))
        {
            MessageBox.Show("先在下载页选一个游戏版本，Mod 是装在具体版本上的。",
                "还没有选择版本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var instance = _vm.InstanceForLaunch(versionId);
        var dlg = new ModsWindow(instance) { Owner = this };
        dlg.ShowDialog();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}