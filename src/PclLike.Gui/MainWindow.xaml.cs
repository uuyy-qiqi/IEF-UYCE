using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        // 左栏在两类页里是不同的东西：
        //   启动页 —— 一张玩家卡片（皮肤头像 + 名字 + 正版状态），没有子导航；
        //   其余页 —— 子导航列表。
        // PCL 也是这个做法，所以启动页的左栏不跟着子导航走。
        var isLaunch = tab == "launch";
        PlayerHost.Visibility = isLaunch ? Visibility.Visible : Visibility.Collapsed;
        NavHost.Visibility = isLaunch ? Visibility.Collapsed : Visibility.Visible;

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

        if (key == "logs")
        {
            // 「查看日志」= 展开底部日志抽屉
            LogToggle.IsChecked = true;
            LogList.Visibility = Visibility.Visible;
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
    /// 载入内置 Steve 皮肤并立刻画出来。
    ///
    /// 这一步决定了两件事：
    ///   1. 界面一打开就有头像（不用等网络）
    ///   2. <see cref="SkinService.DefaultSkinBytes"/> 有内容 —— 否则
    ///      ResolveAsync 的网络兜底会返回空字节，TryParse 失败，
    ///      最后什么都画不出来。
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
            var bytes = ms.ToArray();

            _skins.DefaultSkinBytes = bytes;

            if (SkinHead.TryParse(bytes, out var head))
            {
                PlayerHead.Head = head;

                // 内置皮肤只能算"离线模式"——除非后面网络查到了真皮肤
                PlayerAuthText.Text = "离线模式";
            }
        }
        catch
        {
            // 资源缺失不该让启动器打不开，最坏情况就是没头像
        }
    }

    /// <summary>
    /// 拉取并渲染 3D 头像。
    ///
    /// 顺序：正版登录时优先按 UUID 查（准），否则按玩家名查（离线场景），
    /// 查不到就停在已经画好的内置 Steve 上 —— 所以这个方法是"改善"而非"必须"。
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
                return;   // 字节不是标准皮肤，保持默认

            PlayerHead.Head = head;

            PlayerAuthText.Text = result.Source switch
            {
                SkinSource.MojangProfile => "正版验证",
                SkinSource.MojangByUsername => "正版验证",
                SkinSource.LocalFile => "本地皮肤",
                _ => "离线模式",
            };
        }
        catch
        {
            // 网络失败无所谓，默认头像已经在显示
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Username))
        {
            PlayerNameText.Text = _vm.Username;
            _ = LoadSkinAsync();
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

        // 启动时按配置决定外观：先日志抽屉，再默认页签
        if (_vm.Config.AutoExpandLog)
        {
            LogToggle.IsChecked = true;
            LogList.Visibility = Visibility.Visible;
        }

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

    private void InstancePick_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("实例选择面板还没做。当前启动用的就是上方选中的那个版本。",
            "还没实现", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        LogList.Visibility = LogToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>展开日志抽屉（工具页那两处按钮复用）。</summary>
    private void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        LogToggle.IsChecked = true;
        LogList.Visibility = Visibility.Visible;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _vm.ClearLog();

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

    /// <summary>
    /// 打开 Mod 管理窗口。
    ///
    /// 窗口按当前选中的版本建临时实例 —— 主界面还停留在"选版本"阶段，
    /// 没有显式的实例概念，但 mods 目录必须在某个 GameDir 下，
    /// 所以按版本 id 现取/现建一个实例来定位。
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

        var store = new PclLike.Core.Storage.InstanceStore(App.GameRoot);
        var instance = store.Load(versionId) ?? store.Create(versionId, versionId);

        var dlg = new ModsWindow(instance) { Owner = this };
        dlg.ShowDialog();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}