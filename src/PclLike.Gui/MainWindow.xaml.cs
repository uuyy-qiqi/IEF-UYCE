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

        // 下载页有两屏：内置版本列表（安装包）和社区资源浏览。
        // 这儿先都收起来，具体进哪一屏由下面 BuildNav 触发的 OnNavChanged 决定 ——
        // 左栏第一项是"安装包 → Minecraft"，所以默认进版本列表。
        PageContent.Visibility = Visibility.Collapsed;

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

        if (_topTab == "download") ShowDownloadSection(key);

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
    /// <summary>
    /// 下载页左栏切屏。
    ///
    /// 左栏十三项其实归两类：
    ///   · 安装包（Minecraft / OptiFine / Forge / …）—— 看的是内置的九百多个版本，走版本列表那一屏；
    ///   · 社区资源（Mod / 整合包 / 数据包 / 资源包 / 光影包 / 世界 / 收藏夹）—— 走内容浏览那一屏。
    /// 两屏各自管自己的标题、搜索栏和列表，所以这里只切可见性 + 通知 ViewModel 换栏目。
    /// </summary>
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

        if (tab is null)
        {
            // 安装包那一类：版本列表
            PageContent.Visibility = Visibility.Collapsed;
            PageDownload.Visibility = Visibility.Visible;
            return;
        }

        PageDownload.Visibility = Visibility.Collapsed;
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

        // 实例要先读出来：启动页那张卡显示的是实例名，启动时也按它决定 --gameDir
        _vm.RefreshInstances();
        RefreshInstanceCard();

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