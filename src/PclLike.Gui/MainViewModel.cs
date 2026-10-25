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
        Store = new PclLike.Core.Storage.InstanceStore(gameRoot);

        LogLines = new ObservableCollection<LogLine>();
        Versions = new ObservableCollection<VersionItem>();
        JavaList = new ObservableCollection<JavaLocator.JavaInfo>();
        Instances = new ObservableCollection<PclLike.Core.Storage.GameInstance>();

        VersionsView = CollectionViewSource.GetDefaultView(Versions);
        VersionsView.Filter = FilterVersion;

        // 四个分类视图共用同一个底层集合，只是过滤器不同
        ReleasesView = MakeView(o => IsKind(o, "release"));
        SnapshotsView = MakeView(o => IsKind(o, "snapshot"));
        OldView = MakeView(o => IsKind(o, "old_beta") || IsKind(o, "old_alpha"));
        JokeView = MakeView(IsAprilFools);

        // 本机装好的版本（含加载器版本）单独一类 —— 它们不在 Mojang 清单里，
        // 但正是玩家自己动手装出来的东西，得单独看得见。
        InstalledView = MakeView(o => IsKind(o, "installed"));

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

    /// <summary>本机 versions/ 里已装好的版本（含 Fabric/Forge/NeoForge/Cleanroom 等加载器版本）。</summary>
    public ICollectionView InstalledView { get; }

    /// <summary>四组一起刷新（筛选框变化、清单加载完都调它）。</summary>
    private void RefreshCategoryViews()
    {
        ReleasesView.Refresh();
        SnapshotsView.Refresh();
        OldView.Refresh();
        JokeView.Refresh();
        InstalledView.Refresh();

        OnPropertyChanged(nameof(ReleaseFoldLabel));
        OnPropertyChanged(nameof(SnapshotFoldLabel));
        OnPropertyChanged(nameof(OldFoldLabel));
        OnPropertyChanged(nameof(JokeFoldLabel));
        OnPropertyChanged(nameof(HasJokeVersions));
        OnPropertyChanged(nameof(InstalledFoldLabel));
        OnPropertyChanged(nameof(HasInstalled));
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

    public string InstalledFoldLabel => FoldLabel("已安装", InstalledView);

    /// <summary>一个愚人节版本都没有时，整条折叠项就不显示。</summary>
    public bool HasJokeVersions => CountOf(JokeView) > 0;

    /// <summary>本机一个版本都没装时，「已安装」那一栏整块不出现。</summary>
    public bool HasInstalled => CountOf(InstalledView) > 0;

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
    /// <summary>
    /// 本机 versions/ 里装好的版本 id（含加载器版本：fabric-loader-…、
    /// neoforge-…、1.12.2-cleanroom-… 之类）。
    ///
    /// 判定标准是「目录里有同名 json」——那才是能被启动的版本描述；
    /// 只有目录没有 json 的，是下载或安装没走完留下的残骸，不算。
    /// 列目录失败按「没有」处理：这只是补列表，不该让整个版本清单加载失败。
    /// </summary>
    private IReadOnlyList<string> InstalledVersionIds()
    {
        try
        {
            var dir = Path.Combine(GameRoot, "versions");
            if (!Directory.Exists(dir)) return [];

            return Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(id => id.Length > 0 &&
                             File.Exists(Path.Combine(dir, id, id + ".json")))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

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

            // 本机 versions/ 里已经装好的版本也要进列表 —— 尤其是「安装包」栏刚
            // 装上的那些加载器版本（1.12.2-cleanroom-…、fabric-loader-…、
            // 1.20.1-forge-…）。它们不在 Mojang 清单里，不补进来的话玩家在界面上
            // 根本选不到，装完了也启动不了。
            foreach (var id in InstalledVersionIds())
            {
                if (Versions.Any(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Versions.Add(new VersionItem(id, "installed", null, null));
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

    // ─────────────────── 实例 ───────────────────
    //
    // 实例决定 --gameDir 指到哪儿，也就是存档 / Mod / 设置落在哪儿。
    // 界面上只有"当前实例"这一个概念：选了它，启动就按它来。

    /// <summary>实例仓库（&lt;root&gt;/instances/&lt;id&gt;/instance.json）。</summary>
    public PclLike.Core.Storage.InstanceStore Store { get; }

    /// <summary>全部实例，按最近玩过的排前面。</summary>
    public ObservableCollection<PclLike.Core.Storage.GameInstance> Instances { get; }

    private PclLike.Core.Storage.GameInstance? _selectedInstance;

    /// <summary>当前启动用的实例。null = 还没选，启动时按当前版本自动拿默认实例。</summary>
    public PclLike.Core.Storage.GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (ReferenceEquals(_selectedInstance, value)) return;
            _selectedInstance = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InstanceLabel));
            OnPropertyChanged(nameof(InstanceIsolationLabel));

            // 实例自带它挂的版本（可能就是 Fabric/Cleanroom 这些加载器版本）。
            // 选中实例时把"当前版本"也对齐过去 —— 否则界面上写的是一个版本、
            // 真启动的是另一个，玩家根本对不上，也不知道该点哪个。
            SyncVersionToInstance();
        }
    }

    /// <summary>
    /// 让 SelectedVersion 跟着当前实例挂的版本走。
    ///
    /// 那个版本可能不在 Mojang 清单里（本地装的加载器版本基本都是这样），
    /// SelectVersionById 会现造一条「已安装」记录补进列表。
    /// </summary>
    private void SyncVersionToInstance() => SelectVersionById(_selectedInstance?.VersionId ?? "");

    /// <summary>启动页「当前实例」卡上的名字。</summary>
    public string InstanceLabel => SelectedInstance is null ? "未选择实例" : SelectedInstance.Name;

    /// <summary>独立 / 共享。界面上要显示，因为它决定存档在哪儿。</summary>
    public string InstanceIsolationLabel => SelectedInstance?.IsolationLabel ?? "共享";

    /// <summary>重新读盘，尽量保持当前选中项。</summary>
    public void RefreshInstances()
    {
        var keep = _selectedInstance?.Id;

        Instances.Clear();
        foreach (var i in Store.LoadAll()) Instances.Add(i);

        _selectedInstance = Instances.FirstOrDefault(i => i.Id == keep)
                            ?? Instances.FirstOrDefault(i => i.VersionId == SelectedVersion?.Id)
                            ?? Instances.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedInstance));
        OnPropertyChanged(nameof(InstanceLabel));
        OnPropertyChanged(nameof(InstanceIsolationLabel));
    }

    /// <summary>给某个版本建一个独立实例并选中它。</summary>
    public PclLike.Core.Storage.GameInstance? CreateInstance(string name, string? versionId = null,
                                                             bool isolated = true)
    {
        var vid = string.IsNullOrWhiteSpace(versionId) ? SelectedVersion?.Id : versionId;
        if (string.IsNullOrWhiteSpace(vid))
        {
            AppendLog(LogKind.Warning, "先选一个游戏版本 —— 实例是挂在具体版本上的");
            return null;
        }

        var inst = Store.Create(name, vid, isolated);
        inst.Loader = GuessLoader(vid);
        Store.Save(inst);

        RefreshInstances();
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == inst.Id) ?? inst;

        AppendLog(LogKind.Ok, $"已新建实例「{inst.Name}」→ {vid}（{inst.IsolationLabel}）");
        return inst;
    }

    /// <summary>从版本 id 猜加载器，只用于界面显示和 Mod 提示。</summary>
    public static string GuessLoader(string versionId)
    {
        var v = versionId.ToLowerInvariant();
        if (v.Contains("neoforge")) return "neoforge";
        if (v.Contains("forge")) return "forge";
        if (v.Contains("quilt")) return "quilt";
        if (v.Contains("fabric")) return "fabric";
        return "vanilla";
    }

    /// <summary>复制实例（连游戏文件一起）。</summary>
    public PclLike.Core.Storage.GameInstance? CopyInstance(PclLike.Core.Storage.GameInstance inst,
                                                          string? newName = null)
    {
        try
        {
            var copy = Store.Copy(inst.Id, newName);
            RefreshInstances();
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == copy.Id) ?? copy;

            AppendLog(LogKind.Ok, $"已复制为「{copy.Name}」");
            return copy;
        }
        catch (Exception ex)
        {
            AppendLog(LogKind.Error, $"复制实例失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>删除实例。<paramref name="deleteFiles"/> 决定游戏文件是否一起删。</summary>
    public bool DeleteInstance(PclLike.Core.Storage.GameInstance inst, bool deleteFiles)
    {
        if (!Store.Delete(inst.Id, deleteFiles))
        {
            AppendLog(LogKind.Error, $"删除实例「{inst.Name}」失败 —— 文件可能正被别的程序占着");
            return false;
        }

        AppendLog(LogKind.Info, deleteFiles
            ? $"已删除实例「{inst.Name}」及其文件"
            : $"已删除实例「{inst.Name}」（游戏文件保留在磁盘上）");

        if (_selectedInstance?.Id == inst.Id) _selectedInstance = null;
        RefreshInstances();
        return true;
    }

    /// <summary>
    /// 给界面用的门面：Mod 管理、实例设置这些窗口都得跟启动落在同一个实例上。
    /// 各写各的定位逻辑，迟早会错开（Mod 装到 A、游戏按 B 启动）。
    /// </summary>
    internal PclLike.Core.Storage.GameInstance InstanceForLaunch(string versionId)
        => EnsureInstanceForLaunch(versionId);

    /// <summary>
    /// 启动前确定用哪个实例。
    ///
    /// 没选过就落到"默认实例"——它是共享目录（Isolated = false），跟原版行为一致。
    /// 这里不能悄悄给用户造一个隔离实例：那样他原来的存档、Mod、设置会像凭空消失
    /// 一样，而事实上只是 gameDir 换地方了。
    /// </summary>
    private PclLike.Core.Storage.GameInstance EnsureInstanceForLaunch(string versionId)
    {
        var inst = _selectedInstance ?? Store.EnsureDefault(versionId);

        // 实例挂的版本要跟当前选的版本一致，否则等于用 A 的目录跑 B
        if (inst.VersionId != versionId)
        {
            AppendLog(LogKind.Info, $"实例「{inst.Name}」的版本从 {inst.VersionId} 改为 {versionId}");
            inst.VersionId = versionId;
        }

        inst.Loader = GuessLoader(versionId);
        inst.LastPlayedAt = DateTimeOffset.Now;
        Store.Save(inst);

        RefreshInstances();
        return inst;
    }

    // ─────────────────── 联机 ───────────────────

    private LobbyViewModel? _lobby;

    /// <summary>
    /// 联机大厅那一屏。
    ///
    /// 延迟创建是必须的：它一造出来就要读当前实例的 servers.dat，而"当前实例"
    /// 得等配置和实例列表加载完才准 —— 而字段初始化跑在构造函数体之前。
    /// </summary>
    public LobbyViewModel Lobby => _lobby ??= new LobbyViewModel(GameDirNow, AppendLog);

    // ─────────────────── 社区资源（下载页） ───────────────────

    private ContentViewModel? _content;

    /// <summary>下载页「社区资源」那一屏。同样是延迟创建（要读实例列表）。</summary>
    public ContentViewModel Content => _content ??= new ContentViewModel(CurrentInstance, AppendLog, GameRoot);

    private InstallerViewModel? _installer;

    /// <summary>下载页「安装包」那一屏（加载器安装），同样是按需创建。</summary>
    public InstallerViewModel Installer =>
        _installer ??= CreateInstaller();

    /// <summary>
    /// 建安装器 ViewModel 并接上「装完就把版本选过去」。
    ///
    /// 不接的话：玩家在「安装包」栏装完 Cleanroom，版本列表里选中的还是原来的
    /// 1.21.4，点启动就跑 1.21.4 —— 装了半天等于白装。点「安装」这个动作本身
    /// 就是"我要玩这个版本"。
    /// </summary>
    private InstallerViewModel CreateInstaller()
    {
        var vm = new InstallerViewModel(CurrentInstance, AppendLog, GameRoot);
        vm.Installed += SelectVersionById;
        return vm;
    }

    /// <summary>
    /// 把版本列表选中到指定 id（本地已装但不在清单里的版本会现造一条
    /// 「已安装」记录补进列表）。
    /// </summary>
    public void SelectVersionById(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return;

        var item = Versions.FirstOrDefault(v =>
            string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            item = new VersionItem(versionId, "installed", null, null);
            Versions.Add(item);
            RefreshCategoryViews();
        }

        if (!ReferenceEquals(SelectedVersion, item)) SelectedVersion = item;
    }

    /// <summary>
    /// 当前实例，**只读**。
    ///
    /// 和 <see cref="EnsureInstanceForLaunch"/> 的区别很关键：那个每次调用都会
    /// 改 LastPlayedAt 并 Store.Save 一次。社区资源那一屏到处都要读实例
    /// （刷新存档列表、查加载器、显示实例名），用那个等于每点一下就写一次盘。
    /// 这里只读不写。
    /// </summary>
    public PclLike.Core.Storage.GameInstance CurrentInstance()
        => _selectedInstance
           ?? Store.EnsureDefault(SelectedVersion?.Id ?? Versions.FirstOrDefault()?.Id ?? "");

    // ─────────────────── 拖放 ───────────────────

    /// <summary>
    /// 用户往窗口里拖了文件。返回一段给用户看的总结。
    ///
    /// 分流规则（PCL2 也是这么分的，用户的直觉就在这儿）：
    ///  - **整合包 → 新建一个独立实例再装进去**。整合包自带版本和加载器要求，
    ///    塞进现有实例会把人家的 Mod 冲掉、版本要求也会打架；而且整合包本来
    ///    就是一个"完整的游戏环境"，对应一个实例才是对的模型。
    ///  - **Mod / 资源包 / 光影包 / 数据包 / 存档 → 装进"当前实例"**。
    ///    拖进主界面 = 装到你正在玩的那个实例，不用再选一次。
    ///
    /// 认不出来的文件一个都不动，只报"我认不出这是什么" —— 猜错的代价是把
    /// 存档装进 resourcepacks/，用户既看不见也查不出来。
    /// </summary>
    public async Task<string> DropFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return "";

        var lines = new List<string>();

        foreach (var path in paths)
        {
            var c = PclLike.Core.Content.DroppedContentClassifier.Classify(path);

            switch (c.Kind)
            {
                case PclLike.Core.Content.DroppedKind.ModPack:
                {
                    // 包名当实例名：把 "Fabulously.Optimized-5.4.1.mrpack" 变成
                    // "Fabulously Optimized 5.4.1"。点号连字符换成空格，看着像人起的。
                    // 用包自己的名字最好（"Fabulously Optimized"）；没有才退回文件名，
                    // 而文件名会得到「pack abc006d7」那种谁也不想在实例列表里看到的东西。
                    // 开 zip 读清单只做一次（mc 版本和包名都从同一份清单来）。
                    var plan = TryReadPackPlan(path);

                    var baseName = plan?.Name;
                    if (string.IsNullOrWhiteSpace(baseName)) baseName = Path.GetFileNameWithoutExtension(c.FileName);
                    baseName = SanitizeInstanceName(baseName!, "拖入的整合包");

                    AppendLog(LogKind.Info, $"拖入整合包：{c.FileName} → 新建实例「{baseName}」");

                    // 新实例挂哪个版本？优先挂这个包自己要的那个原版 ——
                    // 那才是这个实例将来真正要跑的东西（装完还会被指到加载器
                    // 版本上）。包要求的原版本机没装的话，退回当前选中的版本，
                    // 至少别让"新建实例"卡住。
                    var packMc = plan?.McVersion;
                    var pin = packMc is not null
                              && File.Exists(Path.Combine(GameRoot, "versions", packMc, packMc + ".json"))
                        ? packMc
                        : SelectedVersion?.Id;

                    var inst = CreateInstance(baseName, pin, isolated: true);

                    if (inst is null)
                    {
                        lines.Add($"✗ {c.FileName}：建不出实例（先选一个游戏版本，或者换个名字再拖）");
                        break;
                    }

                    RefreshInstances();

                    var msg = await Content.InstallLocalPackAsync(path, inst);
                    lines.Add($"{(msg.StartsWith("安装失败", StringComparison.Ordinal) ? "✗" : "✓")} "
                        + $"{c.FileName} → 新实例「{inst.Name}」\n{msg}");
                    break;
                }

                case PclLike.Core.Content.DroppedKind.PackBundle:
                {
                    // 解压即玩的整包（一个 zip 里装着整个 .minecraft）。它和
                    // ModPack 是两条路：没有清单要下，东西全在包里，要做的是
                    // 把它摊开成一个现成实例。
                    AppendLog(LogKind.Info, $"拖入整包：{c.FileName} —— 先数清楚包里有什么");

                    PclLike.Core.Content.BundleLayout layout;
                    try
                    {
                        layout = PclLike.Core.Content.BundleImporter.Inspect(path);
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"✗ {c.FileName}：读不了这个包（{ex.GetType().Name}：{ex.Message}）");
                        break;
                    }

                    AppendLog(LogKind.Info, $"    整包内容：{layout.SummaryText}");

                    // 实例名用包里 .minecraft 上一级的目录名 —— 那是发布者起的
                    // 名字，通常就是整合包名；实在没有才退回文件名。
                    var bundleName = layout.InnerRoot.TrimEnd('/').Split('/').LastOrDefault();
                    if (string.IsNullOrWhiteSpace(bundleName)) bundleName = Path.GetFileNameWithoutExtension(c.FileName);
                    bundleName = SanitizeInstanceName(bundleName, "拖入的整包");

                    AppendLog(LogKind.Info,
                        $"    解压到新实例「{bundleName}」（约 {layout.SizeText}，要等几分钟）");

                    var bundleInst = CreateInstance(bundleName, GuessBundleVersion(layout), isolated: true);

                    if (bundleInst is null)
                    {
                        lines.Add($"✗ {c.FileName}：建不出实例（先选一个游戏版本，或者换个名字再拖）");
                        break;
                    }

                    RefreshInstances();

                    var r = await PclLike.Core.Content.BundleImporter.ImportAsync(
                        path, layout, bundleInst.GameDir, GameRoot,
                        s => AppendLog(LogKind.Normal, s));

                    if (!r.Ok)
                    {
                        lines.Add($"✗ {c.FileName}：{r.Message}");
                        break;
                    }

                    // 挂哪个版本：包里自带的那个加载器版本（懒人包的价值就在它
                    // 自带的加载器和 Mod 上，挂回原版等于把 Mod 全废掉）。
                    var pick = layout.PreferredVersionId is not null
                               && r.VersionIds.Contains(layout.PreferredVersionId, StringComparer.OrdinalIgnoreCase)
                        ? layout.PreferredVersionId
                        : r.VersionIds.FirstOrDefault();

                    var extra = "";

                    if (pick is not null)
                    {
                        bundleInst.VersionId = pick;
                        Store.Save(bundleInst);
                        extra = $"\n版本：{pick}";

                        var need = RequiredJavaMajor(pick);

                        // JavaList 是扫描出来的。扫描没跑过时它是空的 ——
                        // 空不等于"本机没有"，别在那种时候喊狼来了。
                        if (need is > 0 && JavaList.Count > 0 && !JavaList.Any(j => j.MajorVersion >= need))
                        {
                            extra += $"\n注意：这个版本要 Java {need}，本机没找到够新的 —— 去「设置 → Java」下一个，不然启动会失败。";
                            AppendLog(LogKind.Warning, $"整包要求 Java {need}，本机没有");
                        }
                    }
                    else
                    {
                        extra = "\n（包里没有可用的版本 json，暂时挂的是现在选中的版本 —— 点「安装／补全文件」补齐）";
                        AppendLog(LogKind.Warning, "整包里没找到可用的版本 json");
                    }

                    RefreshInstances();
                    lines.Add($"✓ {c.FileName} → 新实例「{bundleInst.Name}」\n{r.Message}{extra}");
                    break;
                }

                case PclLike.Core.Content.DroppedKind.Mod:
                case PclLike.Core.Content.DroppedKind.ResourcePack:
                case PclLike.Core.Content.DroppedKind.ShaderPack:
                case PclLike.Core.Content.DroppedKind.Datapack:
                {
                    var kind = c.Kind switch
                    {
                        PclLike.Core.Content.DroppedKind.Mod => PclLike.Core.Content.ContentKind.Mod,
                        PclLike.Core.Content.DroppedKind.ResourcePack => PclLike.Core.Content.ContentKind.ResourcePack,
                        PclLike.Core.Content.DroppedKind.ShaderPack => PclLike.Core.Content.ContentKind.Shader,
                        _ => PclLike.Core.Content.ContentKind.Datapack,
                    };

                    AppendLog(LogKind.Info, $"拖入{c.Describe}：{c.FileName} → 当前实例");

                    var msg = await Content.InstallLocalContentAsync(path, kind);
                    lines.Add($"{(msg.StartsWith("安装失败", StringComparison.Ordinal) ? "✗" : "✓")} {c.FileName}：{msg}");
                    break;
                }

                case PclLike.Core.Content.DroppedKind.World:
                {
                    AppendLog(LogKind.Info, $"拖入存档：{c.FileName} → 当前实例的 saves/");

                    var msg = await Content.ImportLocalWorldAsync(path);
                    lines.Add($"{(msg.StartsWith("导入失败", StringComparison.Ordinal) ? "✗" : "✓")} {c.FileName}：{msg}");
                    break;
                }

                default:
                    lines.Add($"– {c.FileName}：{c.Describe}");
                    AppendLog(LogKind.Warning, $"拖入的文件认不出来：{c.FileName} —— {c.Describe}");
                    break;
            }
        }

        RefreshInstances();
        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// 读一眼整合包的清单（拿 mc 版本 + 包名），读不出来返回 null。
    ///
    /// 单独留这个薄封装是因为"开 zip 读清单"这件事会失败：包坏了、不是 zip、
    /// 编码怪都能让它抛异常。而新建实例不该因为读不出名字就停摆 —— 拿不到
    /// 就用文件名凑合。一场拖放里只读一次，mc 版本和名字共用这份结果。
    /// </summary>
    private static PclLike.Core.Content.ModPackPlan? TryReadPackPlan(string mrpackPath)
    {
        try { return PclLike.Core.Content.ModPackInstaller.ReadPlan(mrpackPath); }
        catch { return null; }
    }

    /// <summary>
    /// 把包名擦干净成能当目录名的样子。
    ///
    /// 名字来自整合包的清单或包的目录名 —— 那是人写的，什么都可能有：
    /// `:` `?` `|` 在 Windows 上根本建不出目录，结尾的 `.` 和空格也会被系统
    /// 悄悄吃掉，于是实例名和磁盘上的目录名对不上。
    /// </summary>
    private static string SanitizeInstanceName(string? raw, string fallback)
    {
        var s = (raw ?? "").Trim();

        s = new string(s
            .Select(ch => "\\/:*?\"<>|".Contains(ch) || char.IsControl(ch) ? ' ' : ch)
            .ToArray());

        while (s.Contains("  ")) s = s.Replace("  ", " ");
        s = s.Trim().TrimEnd('.', ' ');

        if (s.Length > 40) s = s[..40].Trim().TrimEnd('.', ' ');
        if (s.Length == 0) s = fallback;

        return s;
    }

    /// <summary>
    /// 这个版本要哪个 Java 大版本。不知道返回 null。
    ///
    /// 要顺着 inheritsFrom 往上找：加载器的 version json 通常自己不带
    /// javaVersion，真正写着这个数的是它继承的那份原版 json。深度限 4 层 ——
    /// 正常继承链最多两层，再多就是 json 坏了，别转不出来。
    /// </summary>
    private int? RequiredJavaMajor(string versionId, int depth = 0)
    {
        if (depth > 4) return null;

        try
        {
            var json = Path.Combine(GameRoot, "versions", versionId, versionId + ".json");
            if (!File.Exists(json)) return null;

            var text = File.ReadAllText(json);

            var m = System.Text.RegularExpressions.Regex.Match(text, "\"majorVersion\"\\s*:\\s*(\\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var major)) return major;

            var parent = System.Text.RegularExpressions.Regex.Match(text, "\"inheritsFrom\"\\s*:\\s*\"([^\"]+)\"");
            if (parent.Success) return RequiredJavaMajor(parent.Groups[1].Value, depth + 1);
        }
        catch
        {
            // 读不了就当不知道 —— 这句提醒是"顺手多说的"，不该因为它让导入失败
        }

        return null;
    }

    /// <summary>
    /// 懒人包该挂哪个版本 —— 必须在解包**之前**就知道，因为实例得先建出来
    /// （包要往它的游戏目录里解）。
    ///
    /// 包里自带的那个版本就是答案：解包时它的 versions/ 会被合并进公共目录，
    /// 解完就能用。包里没有 versions/ 时才退到本机已装的版本。
    /// </summary>
    private string? GuessBundleVersion(PclLike.Core.Content.BundleLayout layout)
    {
        if (layout.PreferredVersionId is { Length: > 0 } pre) return pre;

        try
        {
            var dir = Path.Combine(GameRoot, "versions");
            if (Directory.Exists(dir))
            {
                var any = Directory.GetDirectories(dir)
                    .Select(d => Path.GetFileName(d))
                    .OfType<string>()
                    .Where(id => id.Length > 0 && File.Exists(Path.Combine(dir, id, id + ".json")))
                    .OrderByDescending(id => id,
                        Comparer<string>.Create(PclLike.Core.Content.BundleImporter.CompareMcVersion))
                    .FirstOrDefault();

                if (any is not null) return any;
            }
        }
        catch
        {
            // 列目录失败不该让导入停摆
        }

        return SelectedVersion?.Id;
    }

    /// <summary>关窗口时把两个带网络连接的子 VM 收掉。没创建过就不创建。</summary>
    public void DisposeChildren()
    {
        // 顺手把字段清空：释放过的实例不该再被 Content / Lobby 发出去。
        // 否则第二次访问拿到的是一具"尸体"，用它的每一次调用都会在
        // 莫名其妙的地方炸（CancellationTokenSource has been disposed）。
        var content = _content;
        var lobby = _lobby;
        _content = null;
        _lobby = null;

        content?.Dispose();
        lobby?.Dispose();
    }

    // ─────────────────── 网络代理 ───────────────────
    //
    // 「读系统代理」这件事有个坑：.NET 默认只认 Windows"局域网设置"里那个
    // 开关，而 Clash / v2ray 默认不打开它。用户明明开着代理，启动器却直连
    // 超时。所以这里不是简单的"读一下"，而是按
    // 手动配置 → 环境变量 → 系统设置 → 本机常见端口探测 的顺序去找，
    // 找到什么、从哪儿找到的，都写在设置页上给他看。

    /// <summary>当前生效的代理，写成一句人话。</summary>
    public string ProxyStatus => PclLike.Core.Net.ProxyResolver.Current.DisplayText;

    /// <summary>当前代理是从哪儿来的（"环境变量"/"系统设置"/"探测到本机代理"…）。</summary>
    public string ProxySourceText => PclLike.Core.Net.ProxyResolver.Current.SourceText;

    /// <summary>代理是不是在生效 —— 界面用它决定那一行显示什么颜色。</summary>
    public bool ProxyIsActive => PclLike.Core.Net.ProxyResolver.Current.IsActive;

    /// <summary>
    /// 按配置重新找一遍代理，并把用到的 HttpClient 全部作废。
    ///
    /// 探测最多要花两秒（要挨个试本机端口），所以在后台线程上跑；
    /// 找完再回界面刷新那一行字。
    /// </summary>
    public async Task ApplyProxyAsync(bool announce = true)
    {
        var mode = Config.ProxyMode;
        var url = Config.ProxyUrl;

        var setting = await Task.Run(() => PclLike.Core.Net.ProxyResolver.Refresh(mode, url))
            .ConfigureAwait(true);

        // 缓存的客户端里还带着老代理，必须丢掉
        PclLike.Core.Minecraft.Loaders.LoaderHttp.ProxyUrl =
            string.Equals(setting.Source.ToString(), "Manual", StringComparison.Ordinal)
                ? setting.Url
                : null;

        PclLike.Core.Minecraft.Loaders.LoaderHttp.ResetClient();

        OnPropertyChanged(nameof(ProxyStatus));
        OnPropertyChanged(nameof(ProxySourceText));
        OnPropertyChanged(nameof(ProxyIsActive));

        if (announce) AppendLog(LogKind.Info, "网络代理：" + setting.DisplayText);
    }

    // ─────────────────── 百宝箱 ───────────────────
    //
    // 这一屏的每个工具都守同一条规矩：要么只读（打开目录、复制一段文本），
    // 要么在动手之前把要删的东西数清楚给用户看。不做"静默优化" ——
    // 用户点一下然后发现自己几十小时的存档没了，是启动器能犯的最严重的错。

    private PclLike.Core.Diagnostics.GameDirSurvey? _survey;
    private string _toolStatus = "";

    /// <summary>上一次工具动作的结果。没动作时是空的，界面不显示那一行。</summary>
    public string ToolStatus
    {
        get => _toolStatus;
        private set { _toolStatus = value; OnPropertyChanged(); }
    }

    public bool HasToolStatus => _toolStatus.Length > 0;

    public bool HasSurvey => _survey is not null;

    /// <summary>
    /// 当前体检结果。暴露出来是给「打开最新崩溃报告」用的 —— 它要拿
    /// LatestCrash 的完整路径，而那个路径不适合拼成一个字符串属性传给界面。
    /// </summary>
    public PclLike.Core.Diagnostics.GameDirSurvey? Survey => _survey;

    /// <summary>体检覆盖的目录（当前实例的 GameDir）。</summary>
    public string ToolDirLabel => _survey?.GameDir ?? GameDirNow();

    public string ToolTotalText => _survey?.TotalText ?? "—";

    /// <summary>例如「1.8 GB · 12 个存档 · 3 张截图」。</summary>
    public string ToolSummaryText => _survey?.SummaryText ?? "点「刷新」统计一下这个实例占了多大";

    public string ToolWorldText => _survey?.WorldText ?? "—";

    public string ToolScreenshotText => _survey?.ScreenshotText ?? "—";

    public string ToolCrashText => _survey?.CrashText ?? "—";

    /// <summary>各子目录占用。界面按体积倒序显示，让用户先看到最占地的那块。</summary>
    public IReadOnlyList<PclLike.Core.Diagnostics.DirUsage> ToolUsages =>
        _survey is null
            ? []
            : _survey.Usages.OrderByDescending(u => u.Bytes).ToList();

    /// <summary>当前实例的 GameDir；没选实例就退回共享的启动器根目录。</summary>
    public string GameDirNow() => SelectedInstance?.GameDir ?? GameRoot;

    private bool _surveyBusy;

    /// <summary>
    /// 重算体检。切到百宝箱、换实例、手动刷新都调它。
    ///
    /// 放后台线程算：共享实例的 GameDir 就是启动器根目录，底下躺着 assets 和
    /// libraries —— 几万个文件全走一遍要好几秒，在 UI 线程上同步算会把界面冻住。
    /// <paramref name="announce"/> 为 false 时只静默更新数字（清理完重算用），
    /// 不覆盖用户刚看到的操作结果。
    /// </summary>
    public void RefreshSurvey(bool announce = true)
    {
        if (_surveyBusy) return;

        var dir = GameDirNow();
        _surveyBusy = true;

        if (announce) SetToolStatus("正在统计目录体积…");

        _ = Task.Run(() =>
        {
            var survey = PclLike.Core.Diagnostics.GameDirSurveyor.Take(dir);

            var app = Application.Current;
            if (app is not null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(() => ApplySurvey(survey, announce));
                return;
            }

            ApplySurvey(survey, announce);
        });
    }

    private void ApplySurvey(PclLike.Core.Diagnostics.GameDirSurvey survey, bool announce)
    {
        _survey = survey;
        _surveyBusy = false;

        OnPropertyChanged(nameof(HasSurvey));
        OnPropertyChanged(nameof(Survey));
        OnPropertyChanged(nameof(ToolDirLabel));
        OnPropertyChanged(nameof(ToolTotalText));
        OnPropertyChanged(nameof(ToolSummaryText));
        OnPropertyChanged(nameof(ToolWorldText));
        OnPropertyChanged(nameof(ToolScreenshotText));
        OnPropertyChanged(nameof(ToolCrashText));
        OnPropertyChanged(nameof(ToolUsages));

        if (announce) SetToolStatus($"统计完成：{survey.SummaryText}");
    }

    public void SetToolStatus(string text)
    {
        ToolStatus = text;
        OnPropertyChanged(nameof(HasToolStatus));
    }

    /// <summary>扫一遍可清理项。**只扫不删** —— 删的动作由界面确认后再走 <see cref="CleanJunk"/>。</summary>
    public PclLike.Core.Diagnostics.JunkScan ScanJunk()
        => PclLike.Core.Diagnostics.JunkCleaner.Scan(GameDirNow());

    public PclLike.Core.Diagnostics.JunkCleanResult CleanJunk(PclLike.Core.Diagnostics.JunkScan scan)
    {
        var r = PclLike.Core.Diagnostics.JunkCleaner.Clean(scan);

        var msg = r.Deleted == 0
            ? "没有删掉任何文件"
            : $"已清理 {r.Deleted} 个文件，释放 {r.SizeText}"
              + (r.Failed > 0 ? $"；{r.Failed} 个删不掉（多半被占用）" : "");

        SetToolStatus(msg);
        AppendLog(LogKind.Ok, msg);

        // 体积变了，静默重算一次让用户看到效果 —— 不 announce，
        // 免得"统计完成"把刚写上去的清理结果顶掉
        RefreshSurvey(announce: false);
        return r;
    }

    /// <summary>
    /// 最近一次真实启动用的命令行。
    ///
    /// 从 &lt;gameDir&gt;/logs/launch-*.log 的**首行**读，不在这里重新拼一遍 ——
    /// 重拼就会和实际执行的那条命令分叉（规则裁剪、参数顺序、natives 路径都可能
    /// 不一样），那样复制给用户的"启动命令"就是编的。日志里的那行是发动机真跑的。
    /// </summary>
    public string? LastLaunchCommand()
    {
        var logs = Path.Combine(GameDirNow(), "logs");
        if (!Directory.Exists(logs)) return null;

        FileInfo? newest;
        try
        {
            newest = new DirectoryInfo(logs).GetFiles("launch-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }

        if (newest is null) return null;

        try
        {
            // 启动器自己写的这个文件是以 FileShare.Read 打开的，所以游戏还在跑时也能读
            var first = File.ReadLines(newest.FullName).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(first)) return null;
            return first.TrimStart('#', ' ').Trim();
        }
        catch { return null; }
    }

    /// <summary>
    /// 拼一份能贴给别人看的诊断信息。全是只读采集：环境 + 目录体积 + 日志尾巴。
    ///
    /// 存在的意义是"我游戏崩了，帮我看看"这类求助 —— 与其来回问十轮，
    /// 不如让用户一次复制走。
    /// </summary>
    public string BuildDiagnostics()
    {
        var survey = _survey ?? PclLike.Core.Diagnostics.GameDirSurveyor.Take(GameDirNow());
        var inst = SelectedInstance;
        var java = SelectedJava;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== PclLike 诊断信息 ===");
        sb.AppendLine($"时间        : {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
        sb.AppendLine($"启动器版本  : {typeof(MainViewModel).Assembly.GetName().Version}");
        sb.AppendLine($"游戏根目录  : {GameRoot}");
        sb.AppendLine($"当前实例    : {(inst is null ? "未选择" : $"{inst.Name}（{inst.IsolationLabel}）")}");
        sb.AppendLine($"实例目录    : {GameDirNow()}");
        sb.AppendLine($"游戏版本    : {SelectedVersion?.Id ?? "未选择"}");
        sb.AppendLine($"加载器      : {(string.IsNullOrWhiteSpace(inst?.Loader) ? "原版" : inst!.Loader)}");
        sb.AppendLine($"Java        : {(java is null ? "未探测到" : $"Java {java.MajorVersion} · {java.Vendor}")}");
        sb.AppendLine($"Java 路径   : {(java?.Path ?? Config.JavaPath ?? "—")}");
        sb.AppendLine($"最大内存    : {Config.MaxMemoryMb} MB");
        sb.AppendLine($"目录占用    : {survey.TotalText}");
        sb.AppendLine($"存档 / 截图 : {survey.WorldText} · {survey.ScreenshotText}");
        sb.AppendLine($"最近崩溃    : {survey.CrashText}");
        sb.AppendLine();
        sb.AppendLine("--- 目录明细 ---");
        foreach (var u in survey.Usages.OrderByDescending(u => u.Bytes))
            sb.AppendLine($"{(u.Label + ":").PadRight(16)}{u.DisplayText}");
        sb.AppendLine();
        sb.AppendLine("--- 启动器日志（尾部 25 行） ---");
        foreach (var l in LogLines.TakeLast(25))
            sb.AppendLine($"[{l.Time:HH:mm:ss}] {l.Text}");

        return sb.ToString();
    }

    /// <summary>百宝箱里那一堆目录按钮统一走这里。</summary>
    public static void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    /// <summary>当前会用的那个 Java。设置页的列表可以改它，改完写进配置，
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
        // 启动哪个版本？
        //
        // 两个地方都能改"要玩什么"：版本列表里选中的版本、当前实例挂的版本。
        // 规则只有一条 —— **最近一次明确操作说了算**：
        //   · 选中实例 → 版本列表跟着实例走（见 SyncVersionToInstance）
        //   · 在列表里挑别的版本 → 启动就用它，EnsureInstanceForLaunch 再把它
        //     写回实例（所以不会出现"界面显示 A、实际启动 B"）
        // 列表一个都没选（还没加载完）时退回到实例挂的版本。
        var launchVersionId = SelectedVersion?.Id;
        if (string.IsNullOrWhiteSpace(launchVersionId))
            launchVersionId = SelectedInstance?.VersionId;

        if (string.IsNullOrWhiteSpace(launchVersionId)) return;

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

        var id = launchVersionId;
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

            // 实例决定 --gameDir 落在哪儿。没选过会落到共享目录的"默认实例"，
            // 这样老用户的存档不会因为这次改动而"消失"。
            var instance = EnsureInstanceForLaunch(id);
            AppendLog(LogKind.Info, $"实例：{instance.Name}（{instance.IsolationLabel}）→ {instance.GameDir}");

            // 版本 JSON 里没写 javaVersion 的老版本按 8 处理，和 Core 保持一致
            var options = new LaunchService.LaunchOptions
            {
                VersionId = id,
                Instance = instance,
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
        // 本机 versions/ 里的版本（含自己装的加载器版本，见 InstalledView）
        "installed" => "已安装",
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