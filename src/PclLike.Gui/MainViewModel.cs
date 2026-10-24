using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PclLike.Core.Minecraft;
using PclLike.Core.Minecraft.Install;
using PclLike.Core.Minecraft.Launch;
using PclLike.Core.Storage;

namespace PclLike.Gui;

/// <summary>
/// 主面板的 ViewModel。
///
/// 这里刻意手写 INotifyPropertyChanged 而不是引 CommunityToolkit.Mvvm——
/// 整个 GUI 只此一处需要，引一个包换几个 [ObservableProperty] 不划算，
/// 而且省掉一个 NuGet 还原环节，构建更省心。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly LaunchService _service;
    private readonly string _gameRoot;

    private CancellationTokenSource? _cts;

    public MainViewModel(string gameRoot)
    {
        _gameRoot = gameRoot;
        _service = new LaunchService(gameRoot);
        Config = ConfigStore.Load(gameRoot);
        Accounts = new PclLike.Core.Login.AccountManager(gameRoot);

        LogLines = new ObservableCollection<LogLine>();
        Versions = new ObservableCollection<VersionItem>();
        JavaList = new ObservableCollection<JavaLocator.JavaInfo>();

        VersionsView = CollectionViewSource.GetDefaultView(Versions);
        VersionsView.Filter = FilterVersion;

        // 四个分类视图共用同一个底层集合，只是过滤器不同
        ReleasesView = MakeView(o => IsKind(o, "release"));
        SnapshotsView = MakeView(o => IsKind(o, "snapshot"));
        OldView = MakeView(o => IsKind(o, "old_beta") || IsKind(o, "old_alpha"));
        JokeView = MakeView(IsAprilFools);

        // 界面上改完用户名/内存等需要落盘；用命令统一保存
        // （不是每次按键都写盘，避免频繁 IO）
    }

    private ICollectionView MakeView(Predicate<object> pred)
    {
        var src = new CollectionViewSource { Source = Versions };
        var view = src.View;
        view.Filter = o => FilterVersion(o) && pred(o);
        return view;
    }

    private static bool IsKind(object o, string type)
        => o is VersionItem v && string.Equals(v.Type, type, StringComparison.OrdinalIgnoreCase);

    // 愚人节版的类型在清单里也是 snapshot，只能靠版本号认。
    // 名单不长，直接列出来比写规则可靠。
    private static readonly HashSet<string> AprilFoolsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a",                  // 2015 爱的抱抱
        "3D Shareware v1.34",      // 2019
        "20w14infinite",           // 2020 无限维度
        "22w13oneblockatatime",    // 2022 一个方块
        "23w13a_or_b",             // 2023 投票决定
        "24w14potato",             // 2024 毒马铃薯
        "25w14craftmine",          // 2025 挖矿工艺
    };

    private static bool IsAprilFools(object o)
        => o is VersionItem v && AprilFoolsIds.Contains(v.Id);

    // ---------------- 版本筛选 ----------------

    private string _versionFilter = "";
    /// <summary>版本列表搜索框的内容。</summary>
    public string VersionFilter
    {
        get => _versionFilter;
        set
        {
            if (!Set(ref _versionFilter, value)) return;

            VersionsView.Refresh();
            RefreshCategoryViews();
            OnPropertyChanged(nameof(VersionCountText));
        }
    }

    /// <summary>列表下方的计数文案（筛选时显示 x / y）。</summary>
    public string VersionCountText
    {
        get
        {
            var total = Versions.Count;
            var shown = VersionsView.Cast<object>().Count();
            return shown == total ? $"共 {total} 个版本" : $"筛选出 {shown} / {total} 个版本";
        }
    }

    private bool FilterVersion(object obj)
    {
        if (obj is not VersionItem item) return false;
        if (string.IsNullOrWhiteSpace(_versionFilter)) return true;

        return item.Id.Contains(_versionFilter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public string GameRoot => _gameRoot;
    public LauncherConfig Config { get; private set; }

    public ObservableCollection<VersionItem> Versions { get; }
    public ObservableCollection<JavaLocator.JavaInfo> JavaList { get; }
    public ObservableCollection<LogLine> LogLines { get; }

    /// <summary>
    /// 版本列表的筛选视图。XAML 直接绑这个，因为它比 Versions 多个 Filter，
    /// 搜索框才不用每次重建整个集合。
    /// </summary>
    public ICollectionView VersionsView { get; }

    // ─────────────────── 按类别分的四个视图 ───────────────────
    //
    // PCL 把版本清单拆成"正式版 / 预览版 / 远古版 / 愚人节版"四组分开折叠，
    // 比塞进一个大列表好找得多。这里不复制数据，只做四个带过滤的视图 ——
    // 清单有 1000+ 条，复制四份纯属浪费。

    /// <summary>正式版。</summary>
    public ICollectionView ReleasesView { get; }
    /// <summary>预览版（快照）。</summary>
    public ICollectionView SnapshotsView { get; }
    /// <summary>远古版（Alpha / Beta）。</summary>
    public ICollectionView OldView { get; }
    /// <summary>愚人节版。</summary>
    public ICollectionView JokeView { get; }

    /// <summary>四组一起刷新（筛选框变化、清单加载完都调它）。</summary>
    private void RefreshCategoryViews()
    {
        ReleasesView.Refresh();
        SnapshotsView.Refresh();
        OldView.Refresh();
        JokeView.Refresh();

        OnPropertyChanged(nameof(ReleaseFoldLabel));
        OnPropertyChanged(nameof(SnapshotFoldLabel));
        OnPropertyChanged(nameof(OldFoldLabel));
        OnPropertyChanged(nameof(JokeFoldLabel));
        OnPropertyChanged(nameof(HasJokeVersions));
    }

    private static int CountOf(ICollectionView v)
    {
        var n = 0;
        foreach (var _ in v) n++;
        return n;
    }

    private string FoldLabel(string title, ICollectionView v)
    {
        var n = CountOf(v);
        return n == 0 ? title : $"{title} ({n})";
    }

    public string ReleaseFoldLabel => FoldLabel("正式版", ReleasesView);
    public string SnapshotFoldLabel => FoldLabel("预览版", SnapshotsView);
    public string OldFoldLabel => FoldLabel("远古版", OldView);
    public string JokeFoldLabel => FoldLabel("愚人节版", JokeView);

    /// <summary>一个愚人节版本都没有时，整条折叠项就不显示。</summary>
    public bool HasJokeVersions => CountOf(JokeView) > 0;

    // ---------------- 绑定属性 ----------------

    private VersionItem? _selectedVersion;
    public VersionItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (Set(ref _selectedVersion, value))
            {
                // 选中即记忆，下次开启动器直接停在这个版本
                Config.LastVersion = value?.Id;
                Persist();
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(SelectedVersionLabel));
                OnPropertyChanged(nameof(RunSubtitle));
            }
        }
    }

    public string SelectedVersionLabel =>
        SelectedVersion is null ? "未选择版本" : $"{SelectedVersion.Id}  ·  {SelectedVersion.TypeLabel}";

    /// <summary>启动页「当前实例」卡片上的一行 Java 摘要。</summary>
    public string JavaSummary
    {
        get
        {
            if (JavaList.Count == 0) return "未找到";

            // 优先显示配置里指定的那个；找不到就退回列表第一个（一般是最合适的）
            var cur = JavaList.FirstOrDefault(j =>
                          string.Equals(j.Path, Config.JavaPath, StringComparison.OrdinalIgnoreCase))
                      ?? JavaList[0];

            return $"{cur.MajorVersion} · {cur.Vendor}";
        }
    }

    /// <summary>启动页「当前实例」卡片上的一行内存摘要。</summary>
    public string MemorySummary =>
        Config.MaxMemoryMb > 0 ? $"{Config.MaxMemoryMb / 1024.0:0.#} GB" : "自动配置";

    // ─────────────────── 下载页顶部的"最新版本"卡片 ───────────────────
    //
    // 这两张卡片是从版本清单里的 latest.release / latest.snapshot 来的。
    // 清单还没拉回来时它们要显示"获取中"，而不是留一片空白 ——
    // 空白会被当成"这个启动器坏了"。

    private string? _latestReleaseId;
    /// <summary>最新正式版的版本号，如 1.21.4。</summary>
    public string? LatestReleaseId
    {
        get => _latestReleaseId;
        private set => Set(ref _latestReleaseId, value);
    }

    private string? _latestSnapshotId;
    /// <summary>最新快照的版本号，如 25w03a。</summary>
    public string? LatestSnapshotId
    {
        get => _latestSnapshotId;
        private set => Set(ref _latestSnapshotId, value);
    }

    /// <summary>有没有拿到版本清单。界面用它区分"加载中"和"加载完了"。</summary>
    public bool HasManifest => Versions.Count > 0;

    public string LatestReleaseLabel =>
        LatestReleaseId ?? (IsBusy ? "正在获取…" : "获取失败");

    public string LatestReleaseSub =>
        BuildLatestSub(LatestReleaseId, "最新正式版");

    public string LatestSnapshotLabel =>
        LatestSnapshotId ?? (IsBusy ? "正在获取…" : "获取失败");

    public string LatestSnapshotSub =>
        BuildLatestSub(LatestSnapshotId, "最新预览版");

    /// <summary>
    /// 拼「最新正式版，发布于 2026/09/15 19:23」这种副标题。
    ///
    /// 发布时间要从版本清单里按 id 反查 —— manifest 的 latest 节点只给版本号，
    /// 不给时间。查不到就只留标签，不硬编一个假日期。
    /// </summary>
    private string BuildLatestSub(string? id, string label)
    {
        if (id is null)
            return IsBusy ? "正在获取…" : "获取失败，检查网络";

        var item = Versions.FirstOrDefault(v =>
            string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

        return item?.ReleaseTime is { } t
            ? $"{label}，发布于 {t.ToLocalTime():yyyy/MM/dd HH:mm}"
            : label;
    }

    /// <summary>
    /// 启动按钮上那行小字：告诉玩家"按下去会发生什么"。
    ///
    /// 三种状态各有各的说法，因为玩家的下一步动作不一样：
    ///   没选版本 → 去下载页
    ///   没装好  → 点安装
    ///   装好了  → 启动，并报出用的是哪个 Java
    /// </summary>
    public string RunSubtitle
    {
        get
        {
            if (IsGameRunning) return "游戏正在运行，点此强制结束";

            if (SelectedVersion is null)
                return "还没有选择版本 —— 去「下载」页选一个";

            if (IsBusy) return _busyText;

            var java = SelectedJava;
            var ver = SelectedVersion.Id;

            if (java is null)
                return $"{ver}  ·  未找到 Java";

            // 副标题只有按钮一行宽，带上厂商名一定会被截断；
            // 厂商/路径这类细节放在「设置 → Java」里看。
            return $"{ver}  ·  Java {java.MajorVersion}";
        }
    }

    private string _busyText = "处理中…";

    /// <summary>当前忙什么（下载/校验/启动），显示在启动按钮副标题上。</summary>
    public string BusyText
    {
        get => _busyText;
        set
        {
            if (Set(ref _busyText, value)) OnPropertyChanged(nameof(RunSubtitle));
        }
    }

    public bool CanLaunch => !IsBusy && SelectedVersion is not null;

    private string _username = "Player";
    public string Username
    {
        get => _username;
        set { if (Set(ref _username, value)) OnPropertyChanged(nameof(Greeting)); }
    }

    public string Greeting
    {
        get
        {
            var n = ConfigStore.SanitizeUsername(_username);
            return $"欢迎，{n}";
        }
    }

    // ─────────────────────────── 账号 ───────────────────────────

    /// <summary>账号管理器。GUI 与 Core 共用同一份账号状态。</summary>
    public PclLike.Core.Login.AccountManager Accounts { get; }

    /// <summary>顶部按钮上的文字：未登录显示"登录"，已登录显示玩家名。</summary>
    public string AccountLabel
    {
        get
        {
            var p = Accounts.Profile;
            if (p is null) return "登录";
            return p.IsOnline ? p.Name : $"{p.Name}（离线）";
        }
    }

    /// <summary>头像里的首字母。</summary>
    public string AccountInitial
    {
        get
        {
            var n = Accounts.Profile?.Name;
            return string.IsNullOrWhiteSpace(n) ? "?" : n[..1].ToUpperInvariant();
        }
    }

    public string AccountTooltip
    {
        get
        {
            var p = Accounts.Profile;
            return p is null
                ? "点击登录 Microsoft 账号，或使用离线模式"
                : p.IsOnline
                    ? $"{p.Name}\n正版账号 · {p.Uuid}"
                    : $"{p.Name}\n离线账号（只能玩单机）";
        }
    }

    /// <summary>账号变化后刷新顶部栏显示。</summary>
    public void RefreshAccount()
    {
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(AccountInitial));
        OnPropertyChanged(nameof(AccountTooltip));
    }

    /// <summary>启动时静默续期正版 token。返回是否真的刷新了。</summary>
    public async Task<bool> TryRefreshAccountAsync()
        => await Accounts.TryRefreshAsync(Config.MicrosoftClientId).ConfigureAwait(false);

    private int _maxMemoryMb = 4096;
    public int MaxMemoryMb
    {
        get => _maxMemoryMb;
        set
        {
            if (Set(ref _maxMemoryMb, value))
            {
                OnPropertyChanged(nameof(MemoryLabel));
                OnPropertyChanged(nameof(MemoryBarWidth));
                OnPropertyChanged(nameof(MemGameStar));
                OnPropertyChanged(nameof(MemFreeStar));
                OnPropertyChanged(nameof(MemoryUsedText));
                OnPropertyChanged(nameof(RunSubtitle));
            }
        }
    }

    /// <summary>滑杆右端的读数，如 "4 GB"。</summary>
    public string MemoryLabel => $"{MaxMemoryMb / 1024.0:0.#} GB";

    /// <summary>内存条已填充部分的宽度（像素）。</summary>
    ///
    /// 换算成像素是因为 XAML 里那条填充用的是固定宽度 Border，
    /// 而不是百分比 —— 用 0~1 的比例绑 Width 在 Grid 里不会自动
    /// 按容器宽度缩放，必须自己乘一下轨道宽度。
    public double MemoryBarWidth
    {
        get
        {
            const double trackWidth = 560;   // 和内存卡片的可用宽度对应
            const int min = 1024;            // 与 Slider.Minimum 一致
            const int max = 16384;           // 与 Slider.Maximum 一致

            var ratio = (MaxMemoryMb - min) / (double)(max - min);
            ratio = Math.Clamp(ratio, 0, 1);
            return Math.Round(trackWidth * ratio, 1);
        }
    }

    /// <summary>
    /// 内存条左边的说明文字。
    ///
    /// 这里给的是"推荐值 vs 当前值"的对比，因为玩家调内存时最需要知道
    /// 的就是这两个数差多少 —— 光看一个滑杆看不出来。
    /// </summary>
    public string MemoryUsedText
    {
        get
        {
            var rec = RecommendedMemoryMb;
            return MaxMemoryMb == rec
                ? $"已按本机内存自动配置（推荐 {rec / 1024.0:0.#} GB）"
                : $"手动指定 {MemoryLabel}，本机推荐 {rec / 1024.0:0.#} GB";
        }
    }

    private int _recommendedMemoryMb;
    /// <summary>
    /// 本机推荐内存。取物理内存的一半，上限 8 GB，下限 1 GB。
    ///
    /// 一半是社区长期验证过的经验值：给太多会挤占系统，
    /// 给太少 Minecraft 的 GC 会频繁停顿。
    /// </summary>
    public int RecommendedMemoryMb
    {
        get => _recommendedMemoryMb == 0
            ? Math.Clamp(PhysicalMemoryMb / 2, 1024, 8192)
            : _recommendedMemoryMb;
        set => _recommendedMemoryMb = value;
    }

    private static int _physicalMemoryMb;
    /// <summary>物理内存总量（MB）。取不到时按 8 GB 估。</summary>
    public static int PhysicalMemoryMb
    {
        get
        {
            if (_physicalMemoryMb != 0) return _physicalMemoryMb;

            try
            {
                // 用 GC 的 API 而不是 WMI：不用引 System.Management，
                // 也不用 COM，进程里多一个依赖不划算。
                _physicalMemoryMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
            }
            catch { }

            if (_physicalMemoryMb <= 0) _physicalMemoryMb = 8192;
            return _physicalMemoryMb;
        }
    }

    /// <summary>
    /// 系统当前的内存占用（MB）。
    ///
    /// 用 MemoryLoadBytes（整机提交量的估算），不是本进程的 WorkingSet ——
    /// 玩家关心的是"还能给游戏多少"，那取决于整机已经吃掉多少。
    /// 取不到就按物理内存的 45% 估一个，总比不画好。
    /// </summary>
    public static int SystemUsedMemoryMb
    {
        get
        {
            try
            {
                var mb = (int)(GC.GetGCMemoryInfo().MemoryLoadBytes / 1024 / 1024);
                if (mb > 0) return Math.Min(mb, PhysicalMemoryMb);
            }
            catch { }
            return (int)(PhysicalMemoryMb * 0.45);
        }
    }

    // ─────────────── 内存占用条（三段） ───────────────
    //
    // 三段分别是：系统已用 / 本次给游戏分配 / 剩下没用到的。
    // 宽度用 GridLength 的星值表达 —— XAML 里 Grid 的列宽类型是 GridLength
    // 而不是 double，所以比例必须在这里换算好；三段按同一个基数（物理内存）
    // 归一，加起来正好铺满整条。

    /// <summary>内存条第 1 段：系统已用。</summary>
    public GridLength MemUsedStar => new(Math.Max(1, SystemUsedMemoryMb), GridUnitType.Star);

    /// <summary>内存条第 2 段：分给游戏的部分（不能超过物理内存减去系统占用）。</summary>
    public GridLength MemGameStar => new(
        Math.Max(1, Math.Min(MaxMemoryMb, Math.Max(0, PhysicalMemoryMb - SystemUsedMemoryMb))),
        GridUnitType.Star);

    /// <summary>内存条第 3 段：还没被任何东西占用的部分。</summary>
    public GridLength MemFreeStar => new(
        Math.Max(1, PhysicalMemoryMb - SystemUsedMemoryMb - MaxMemoryMb),
        GridUnitType.Star);

    /// <summary>内存条左端读数，如 "9.6 GB"。</summary>
    public string MemoryUsedLabel => $"{SystemUsedMemoryMb / 1024.0:0.#} GB";

    /// <summary>内存条右端读数（物理内存总量），如 "15.4 GB"。</summary>
    public string MemoryTotalLabel => $"{PhysicalMemoryMb / 1024.0:0.#} GB";

    private string _statusText = "就绪";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(RunSubtitle));
            }
        }
    }

    public bool IsIdle => !IsBusy;

    private bool _isInstalling;
    public bool IsInstalling { get => _isInstalling; set => Set(ref _isInstalling, value); }

    private bool _isGameRunning;
    public bool IsGameRunning
    {
        get => _isGameRunning;
        set
        {
            if (Set(ref _isGameRunning, value))
            {
                OnPropertyChanged(nameof(RunButtonText));
                OnPropertyChanged(nameof(RunButtonBrush));
                OnPropertyChanged(nameof(RunSubtitle));
            }
        }
    }

    public string RunButtonText => IsGameRunning ? "结束游戏" : "启动游戏";

    public Brush RunButtonBrush => IsGameRunning ? Theme.Danger : Theme.Accent;

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set => Set(ref _progressValue, value); }

    private double _progressMax = 1;
    public double ProgressMax { get => _progressMax; set => Set(ref _progressMax, value); }

    private bool _isProgressIndeterminate;
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        set => Set(ref _isProgressIndeterminate, value);
    }

    // ---------------- 操作 ----------------

    /// <summary>拉取版本清单（带本地缓存兜底）。</summary>
    public async Task LoadVersionsAsync()
    {
        IsBusy = true;
        StatusText = "正在获取版本清单...";
        ProgressValue = 0;
        ProgressMax = 1;
        IsProgressIndeterminate = true;

        try
        {
            var manifest = await _service.Installer.FetchManifestAsync();

            Versions.Clear();
            foreach (var v in manifest.Versions ?? [])
            {
                Versions.Add(new VersionItem(
                    v.Id ?? "?",
                    v.Type ?? "",
                    v.ReleaseTime,
                    v.Url));
            }

            // 恢复上次选择的版本；没有历史记录时优先选最新正式版。
            //
            // 注意不能简单取 Versions 的第一项：Mojang 的版本清单是
            // 按发布时间倒序排的，第一项很可能是快照（26.3 这种未来版本号），
            // 普通玩家要的是最新稳定版。
            var manifestRelease = manifest.Latest?.Release;

            VersionItem? restore = null;

            if (!string.IsNullOrWhiteSpace(Config.LastVersion))
                restore = Versions.FirstOrDefault(v =>
                    string.Equals(v.Id, Config.LastVersion, StringComparison.OrdinalIgnoreCase));

            // 上次那个版本可能已经不在清单里了，或者压根没记录过
            restore ??= Versions.FirstOrDefault(v =>
                string.Equals(v.Id, manifestRelease, StringComparison.OrdinalIgnoreCase));

            // 再兜底：清单里第一个 release 类型的
            restore ??= Versions.FirstOrDefault(v =>
                string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase));

            restore ??= Versions.FirstOrDefault();

            SelectedVersion = restore;

            StatusText = $"共 {Versions.Count} 个版本，最新正式版 {manifest.Latest?.Release}";
            AppendLog(LogKind.Info, $"找到 {Versions.Count} 个可安装版本");
            OnPropertyChanged(nameof(VersionCountText));

            // 记下"最新"是哪个，下载页顶部的两个快捷卡片要显示
            LatestReleaseId = manifest.Latest?.Release;
            LatestSnapshotId = manifest.Latest?.Snapshot;

            OnPropertyChanged(nameof(LatestReleaseLabel));
            OnPropertyChanged(nameof(LatestReleaseSub));
            OnPropertyChanged(nameof(LatestSnapshotLabel));
            OnPropertyChanged(nameof(LatestSnapshotSub));
            RefreshCategoryViews();
            OnPropertyChanged(nameof(HasManifest));
            OnPropertyChanged(nameof(VersionCountText));
        }
        catch (Exception ex)
        {
            StatusText = "版本清单获取失败";
            AppendLog(LogKind.Error, $"版本清单获取失败：{ex.Message}");
            AppendLog(LogKind.Muted, "提示：检查网络，或确认能访问 piston-meta.mojang.com");

            // 失败时也要刷一次，让卡片从"正在获取"变成"获取失败 + 重试提示"
            OnPropertyChanged(nameof(LatestReleaseLabel));
            OnPropertyChanged(nameof(LatestReleaseSub));
            OnPropertyChanged(nameof(LatestSnapshotLabel));
            OnPropertyChanged(nameof(LatestSnapshotSub));
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>刷新本机 Java 列表，并把最佳选择填进配置。</summary>
    public void RefreshJava()
    {
        // 重新探测时丢掉手动选择，让 DefaultJava() 按新列表重算
        _pickedJava = null;

        JavaList.Clear();
        foreach (var j in JavaLocator.Discover(_gameRoot, Config.JavaPath))
            JavaList.Add(j);

        OnPropertyChanged(nameof(SelectedJava));
        OnPropertyChanged(nameof(JavaSummary));
        OnPropertyChanged(nameof(RunSubtitle));

        if (JavaList.Count == 0)
        {
            AppendLog(LogKind.Warning, "没有探测到任何 Java —— 启动前需要先装一个");
            if (!string.IsNullOrWhiteSpace(Config.JavaPath))
                AppendLog(LogKind.Muted, $"已手动指定：{Config.JavaPath}（探测失败）");
            return;
        }

        AppendLog(LogKind.Info, $"探测到 {JavaList.Count} 个 Java：{string.Join("、", JavaList.Select(j => $"Java {j.MajorVersion}"))}");
    }

    /// <summary>
    /// 当前会用的那个 Java。设置页的列表可以改它，改完写进配置，
    /// 之后实际启动时 <see cref="LaunchService"/> 会优先用这个路径。
    ///
    /// 没手动选过就按 <see cref="JavaLocator.Discover"/> 的排序取第一个
    /// （它已经把合适度排好了）。
    /// </summary>
    private JavaLocator.JavaInfo? _pickedJava;

    public JavaLocator.JavaInfo? SelectedJava
    {
        get => _pickedJava ?? DefaultJava();
        set
        {
            if (Equals(_pickedJava, value)) return;
            _pickedJava = value;
            OnPropertyChanged();

            if (value is null) return;

            Config.JavaPath = value.Path;
            Persist();
            OnPropertyChanged(nameof(JavaSummary));
            OnPropertyChanged(nameof(RunSubtitle));
            AppendLog(LogKind.Info, $"已选用 Java {value.MajorVersion}：{value.Path}");
        }
    }

    private JavaLocator.JavaInfo? DefaultJava()
    {
        if (JavaList.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(Config.JavaPath))
        {
            var match = JavaList.FirstOrDefault(j =>
                string.Equals(j.Path, Config.JavaPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return JavaList[0];
    }

    /// <summary>
    /// 下载一个大版本的 Java 运行时（Adoptium Temurin JRE），解压到 &lt;root&gt;/runtime。
    /// 装好后自动重新探测并选中它。
    /// </summary>
    public async Task<bool> DownloadJavaAsync(int majorVersion)
    {
        if (IsBusy)
        {
            AppendLog(LogKind.Warning, "现在正忙，等当前任务结束再下载 Java");
            return false;
        }

        var mgr = new JavaRuntimeManager(_gameRoot);

        if (mgr.IsInstalled(majorVersion))
        {
            var existing = mgr.GetInstalledPath(majorVersion);
            AppendLog(LogKind.Info, $"Java {majorVersion} 已经装过了：{existing}");
            RefreshJava();
            return true;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressValue = 0;
        ProgressMax = 1;
        IsProgressIndeterminate = true;
        StatusText = $"正在下载 Java {majorVersion}…";
        AppendLog(LogKind.Info, $"开始下载 Java {majorVersion}（Adoptium {JavaRuntimeManager.Platform}）—— 约 40~60 MB");

        try
        {
            var progress = new Progress<(string Stage, long Done, long Total)>(p =>
            {
                if (p.Total > 0)
                {
                    StatusText = $"{p.Stage}  {p.Done * 100 / p.Total}%";
                    ProgressMax = p.Total;
                    ProgressValue = p.Done;
                    IsProgressIndeterminate = false;
                }
                else
                {
                    StatusText = p.Stage;
                    IsProgressIndeterminate = true;
                }
            });

            var path = await mgr.EnsureAsync(majorVersion, progress, _cts.Token);

            AppendLog(LogKind.Ok, $"Java {majorVersion} 已就绪：{path}");
            StatusText = "Java 下载完成";

            RefreshJava();

            var picked = JavaList.FirstOrDefault(j =>
                string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
            if (picked is not null) SelectedJava = picked;

            return true;
        }
        catch (OperationCanceledException)
        {
            AppendLog(LogKind.Warning, "Java 下载已取消");
            StatusText = "已取消";
            return false;
        }
        catch (Exception ex)
        {
            AppendLog(LogKind.Error, $"下载 Java 失败：{ex.Message}");
            StatusText = "Java 下载失败";
            return false;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>手动指定一个 java.exe（用户在设置页浏览选中的）。</summary>
    public void UseJavaBinary(string javaExe)
    {
        var info = JavaLocator.Probe(javaExe);
        if (info is null)
        {
            AppendLog(LogKind.Error, $"这不是一个能用的 java.exe：{javaExe}");
            return;
        }

        if (!JavaList.Any(j => string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase)))
            JavaList.Add(info);

        SelectedJava = info;
        AppendLog(LogKind.Ok, $"已指定 Java {info.MajorVersion}（{info.VendorLabel}）：{info.Path}");
    }

    /// <summary>只下载，不启动。用于"下载"按钮。</summary>
    public async Task InstallAsync()
    {
        if (SelectedVersion is null) return;

        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsInstalling = true;
        ProgressValue = 0;
        ProgressMax = 1;

        var id = SelectedVersion.Id;
        AppendLog(LogKind.Info, $"开始安装 {id} ...");

        try
        {
            var progress = new Progress<InstanceInstaller.InstallProgress>(p =>
            {
                StatusText = p.Total > 1 ? $"{p.Stage} {p.Done}/{p.Total}" : p.Stage;
                ProgressMax = Math.Max(1, p.Total);
                ProgressValue = p.Done;
                IsProgressIndeterminate = p.Total <= 1;
            });

            var result = await _service.Installer.InstallAsync(id, progress, _cts.Token);

            foreach (var w in result.Warnings)
                AppendLog(LogKind.Warning, w);

            var cpCount = result.Classpath.Split(ArgumentBuilder.ClasspathSeparator).Length;
            AppendLog(LogKind.Ok, $"{id} 安装完成：classpath {cpCount} 项，资源索引 {result.AssetIndexId}");
            StatusText = "安装完成";
        }
        catch (OperationCanceledException)
        {
            AppendLog(LogKind.Warning, "安装已取消");
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            AppendLog(LogKind.Error, $"安装失败：{ex.Message}");
            StatusText = "安装失败";
        }
        finally
        {
            IsBusy = false;
            IsInstalling = false;
            IsProgressIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>安装（可选）+ 启动，日志实时回灌到窗口。</summary>
    public async Task LaunchAsync()
    {
        if (SelectedVersion is null) return;

        if (IsGameRunning)
        {
            KillGame();
            return;
        }

        // 先把界面上可能改过的设置落盘，再读回统一使用
        Persist();

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressValue = 0;
        ProgressMax = 1;

        var id = SelectedVersion.Id;
        var user = ConfigStore.SanitizeUsername(Config.Username);

        // 身份优先级：已登录的账号 > 配置里的离线名。
        //
        // 正版登录后 uuid / accessToken 必须是真实值，否则服务器会
        // 判定为未验证会话直接踢掉。离线档的 uuid 由名字派生，
        // 两者不能混用——所以这里整块取，不逐字段挑。
        var profile = Accounts.Profile
                      ?? PclLike.Core.Login.AccountManager.PlayerProfile.Offline(user);

        AppendLog(LogKind.Info, $"──────── 启动 {id} / {profile.Name}{(profile.IsOnline ? "（正版）" : "（离线）")} ────────");

        try
        {
            (string Host, int? Port)? server = null;
            if (!string.IsNullOrWhiteSpace(Config.Server))
            {
                var parts = Config.Server.Split(':', 2);
                server = (parts[0], parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : null);
            }

            var progress = new Progress<(string Stage, int Done, int Total)>(p =>
            {
                StatusText = p.Total > 1 ? $"{p.Stage} {p.Done}/{p.Total}" : p.Stage;
                ProgressMax = Math.Max(1, p.Total);
                ProgressValue = p.Done;
                IsProgressIndeterminate = p.Total <= 1;
            });

            // 版本 JSON 里没写 javaVersion 的老版本按 8 处理，和 Core 保持一致
            var options = new LaunchService.LaunchOptions
            {
                VersionId = id,
                Username = profile.Name,
                Uuid = profile.Uuid,
                AccessToken = profile.AccessToken,
                MaxMemoryMb = Config.MaxMemoryMb,
                MinMemoryMb = Config.MinMemoryMb,
                JavaPath = string.IsNullOrWhiteSpace(Config.JavaPath) ? null : Config.JavaPath,
                ServerAddress = server?.Host,
                ServerPort = server?.Port,
                ExtraJvmArgs = Config.ExtraJvmArgs,
                SkipInstall = !Config.AutoInstall,
            };

            // 游戏一旦起来就标记状态；LaunchAsync 会一直等到游戏退出
            var launched = new TaskCompletionSource();
            var outcome = await _service.LaunchAsync(options, progress,
                line =>
                {
                    if (!launched.Task.IsCompleted)
                    {
                        launched.TrySetResult();
                        Application.Current?.Dispatcher.Invoke(() => IsGameRunning = true);
                    }
                    AppendLog(ClassifyLog(line), line);
                }, _cts.Token);

            AppendLog(outcome.Crashed ? LogKind.Error : LogKind.Ok,
                outcome.Crashed
                    ? $"游戏异常退出（退出码 {outcome.ExitCode}）：{outcome.Reason}"
                    : $"游戏已退出（退出码 {outcome.ExitCode}）");
            AppendLog(LogKind.Muted, $"日志文件：{outcome.LogPath}");
            StatusText = outcome.Crashed ? "游戏异常退出" : "已退出";
        }
        catch (OperationCanceledException)
        {
            AppendLog(LogKind.Warning, "启动已取消");
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            AppendLog(LogKind.Error, ex.Message);
            StatusText = "启动失败";
        }
        finally
        {
            IsGameRunning = false;
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>供窗口关闭时调用：直接结束游戏进程。</summary>
    public void KillRunningGame() => KillGame();

    private void KillGame()
    {
        if (_service.KillGame())
            AppendLog(LogKind.Warning, "已强制结束游戏进程");
        else
            AppendLog(LogKind.Muted, "游戏进程已经退出了");
    }

    /// <summary>把界面上的设置写回配置文件。</summary>
    public void Persist()
    {
        Config.Username = ConfigStore.SanitizeUsername(Username);
        Config.MaxMemoryMb = Math.Clamp(MaxMemoryMb, 512, 65536);
        ConfigStore.Save(_gameRoot, Config);
    }

    /// <summary>打开游戏根目录。</summary>
    public void OpenGameFolder()
    {
        Directory.CreateDirectory(_gameRoot);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _gameRoot,
            UseShellExecute = true,
        });
    }

    public void ClearLog() => LogLines.Clear();

    public void AppendLog(LogKind kind, string text)
    {
        // 后台线程也会调进来（下载回调、游戏 stdout），必须切回 UI 线程
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => AppendLog(kind, text));
            return;
        }

        LogLines.Add(new LogLine(kind, DateTime.Now, text ?? ""));

        // 防止长跑一晚上把内存吃满
        while (LogLines.Count > 5000) LogLines.RemoveAt(0);
    }

    /// <summary>按日志内容挑颜色：错误红、警告黄、正常灰。</summary>
    private static LogKind ClassifyLog(string line)
    {
        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Exception", StringComparison.Ordinal)
            || line.Contains("失败", StringComparison.Ordinal))
            return LogKind.Error;

        if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase))
            return LogKind.Warning;

        return LogKind.Normal;
    }
}

/// <summary>版本列表里的一行。</summary>
public sealed record VersionItem(string Id, string Type, DateTimeOffset? ReleaseTime, string? Url)
{
    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        // PCL CE 管 snapshot 叫「预览版」，比「快照」好懂
        "snapshot" => "预览版",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };

    public string ReleaseDate => ReleaseTime?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";

    /// <summary>列表里的主标题，绑定到 ListBox 显示。</summary>
    public string Display => Id;
}

public enum LogKind { Normal, Info, Ok, Warning, Error, Muted }

/// <summary>日志窗里的一行，带着颜色。</summary>
public sealed record LogLine(LogKind Kind, DateTime Time, string Text)
{
    public string Timestamp => Time.ToString("HH:mm:ss");

    public Brush Brush => Kind switch
    {
        LogKind.Error => Theme.Danger,
        LogKind.Warning => Theme.Warning,
        LogKind.Ok => Theme.Ok,
        LogKind.Info => Theme.AccentHi,
        LogKind.Muted => Theme.Muted,
        _ => Theme.Foreground,
    };
}