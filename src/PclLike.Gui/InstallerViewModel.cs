using System.Collections.ObjectModel;
using PclLike.Core.Minecraft;
using PclLike.Core.Minecraft.Loaders;
using PclLike.Core.Storage;

namespace PclLike.Gui;

/// <summary>安装包列表里的一行（某个加载器对某个 MC 版本的一个具体版本）。</summary>
public sealed class InstallerRow
{
    public InstallerRow(LoaderVersion version, bool isLatest)
    {
        Version = version;
        IsLatest = isLatest;
    }

    public LoaderVersion Version { get; }

    /// <summary>「Fabric 0.20.1」这种。</summary>
    public string Name => Version.DisplayName;

    /// <summary>「1.20.1 · 稳定版」这种。
    ///
    /// 加载器的非稳定版写成「测试版」而不是「快照版」—— 「快照版」在 Minecraft 里
    /// 专指游戏本体快照，套在 Fabric 的每日构建上会让人以为装的不是加载器。
    /// </summary>
    public string Subtitle =>
        $"{Version.MinecraftVersion} · {(Version.IsStable ? "稳定版" : "测试版")}";

    /// <summary>装完之后 versions/ 下会出现的 id。</summary>
    public string ResultId => Version.ResultVersionId;

    public bool IsLatest { get; }

    public string LatestText => IsLatest ? "最新" : "";

    public bool HasLatestText => IsLatest;
}

/// <summary>
/// 下载页「安装包」那六项的 ViewModel。
///
/// 六项里只有四项是真能自动装的（Fabric / Quilt / Forge / NeoForge），
/// 它们各自的官方 API 都能列出「某个 MC 版本下有哪些加载器版本」，
/// 装法也统一走 <see cref="LoaderInstaller"/>。
///
/// OptiFine 与「Minecraft」两个键故意分开处理：
///   · Minecraft —— 就是原版，走版本列表那一屏（下载页自己的列表），
///     不归这里管；
///   · OptiFine —— **没有可用的公开 API**（它只给一个 installer jar，
///     那个 jar 是个图形安装器，还要求你先有 Java）。这里不装作能装，
///     直接把"没做"写在界面上，免得点了半天没反应。
/// </summary>
public sealed class InstallerViewModel : ObservableObject
{
    private readonly Func<GameInstance> _instance;
    private readonly Action<LogKind, string> _log;
    private readonly string _gameRoot;

    private string _title = "Fabric";
    private string _hintText = "";
    private string _kindKey = "fabric";
    private string _mcVersion = "";
    private string _statusText = "";
    private bool _isBusy;
    private bool _isSupported = true;
    private InstallerRow? _selectedRow;

    public InstallerViewModel(Func<GameInstance> instance, Action<LogKind, string> log, string gameRoot)
    {
        _instance = instance;
        _log = log;
        _gameRoot = gameRoot;
    }

    public ObservableCollection<InstallerRow> Versions { get; } = new();

    /// <summary>当前这一屏是哪个加载器。</summary>
    public LoaderKind Kind { get; private set; } = LoaderKind.Fabric;

    /// <summary>左栏那个键（fabric / quilt / forge / neoforge / optifine）。</summary>
    public string KindKey => _kindKey;

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string HintText
    {
        get => _hintText;
        private set => Set(ref _hintText, value);
    }

    /// <summary>这个加载器能不能自动装。OptiFine 是 false。</summary>
    public bool IsSupported
    {
        get => _isSupported;
        private set
        {
            if (Set(ref _isSupported, value))
            {
                OnPropertyChanged(nameof(IsUnsupported));
                OnPropertyChanged(nameof(HasVersions));
                OnPropertyChanged(nameof(EmptyVisibility));
            }
        }
    }

    public bool IsUnsupported => !_isSupported;

    /// <summary>要装哪个 MC 版本的加载器。</summary>
    public string McVersion
    {
        get => _mcVersion;
        set
        {
            if (Set(ref _mcVersion, value))
            {
                OnPropertyChanged(nameof(CanAct));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _statusText.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanAct));
                OnPropertyChanged(nameof(BusyVisibility));

                // 空列表那行提示也跟着忙/闲变（「正在查……」↔「点刷新查」）
                OnPropertyChanged(nameof(EmptyText));
            }
        }
    }

    public System.Windows.Visibility BusyVisibility =>
        _isBusy ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>没在忙、又不支持，才允许点。</summary>
    public bool CanAct => !_isBusy && _isSupported;

    public InstallerRow? SelectedRow
    {
        get => _selectedRow;
        set => Set(ref _selectedRow, value);
    }

    public bool HasVersions => _isSupported && Versions.Count > 0;

    /// <summary>列表里没有东西时那行提示该不该露。</summary>
    public System.Windows.Visibility EmptyVisibility =>
        _isSupported && Versions.Count == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public string EmptyText => _isBusy ? "正在查……" : "上面填个 Minecraft 版本，点「刷新」查可用版本。";

    /// <summary>
    /// 切到某一项。MC 版本默认取当前实例猜出来的那个（跟内容页同一个规则，
    /// 所以两边不会各猜一个）。
    /// </summary>
    public void Activate(string key)
    {
        _kindKey = key;
        Kind = key switch
        {
            "quilt" => LoaderKind.Quilt,
            "forge" => LoaderKind.Forge,
            "neoforge" => LoaderKind.NeoForge,
            "fabric" => LoaderKind.Fabric,
            _ => Kind,
        };

        if (key == "optifine")
        {
            Title = "OptiFine";
            HintText = "OptiFine 没有可用的公开接口来列版本、也没有命令行安装方式 —— "
                     + "官方只给你一个图形安装器，还要求先有 Java。这一项还没做。";
            IsSupported = false;
            Versions.Clear();
            StatusText = "";
            RaiseListChanged();
            return;
        }

        IsSupported = true;
        Title = Kind switch
        {
            LoaderKind.Quilt => "Quilt",
            LoaderKind.Forge => "Forge",
            LoaderKind.NeoForge => "NeoForge",
            _ => "Fabric",
        };
        HintText = $"选一个 Minecraft 版本，这里会列出 {Title} 的可用版本；"
                 + "点「安装这个」就会把它装成一个新版本（原版会自动一起补上），"
                 + "装完在「启动」页的版本列表里就能选。";

        var guess = ContentViewModel.GuessMcVersion(_instance().VersionId);
        if (McVersion.Trim().Length == 0 || guess.Length > 0)
        {
            McVersion = guess.Length > 0 ? guess : McVersion;
        }

        _ = RefreshAsync();
    }

    /// <summary>查这个 MC 版本下有哪些加载器版本。</summary>
    public async Task RefreshAsync()
    {
        if (!IsSupported || IsBusy) return;

        var mc = McVersion.Trim();
        if (mc.Length == 0)
        {
            StatusText = "先填一个 Minecraft 版本，比如 1.20.1。";
            return;
        }

        IsBusy = true;
        StatusText = $"正在查 {mc} 的 {Title} 版本……";

        try
        {
            var installer = new LoaderInstaller(_gameRoot);
            var list = await installer.ListAsync(Kind, mc).ConfigureAwait(false);

            // 网络那一段是 ConfigureAwait(false) 走的，回来时可能在别的线程上，
            // 而 Versions 是绑在界面上的 —— 改它必须回 UI 线程。
            OnUi(() =>
            {
                var latestStable = list.FirstOrDefault(v => v.IsStable);
                Versions.Clear();
                foreach (var v in list)
                {
                    Versions.Add(new InstallerRow(v, latestStable is not null && v.Version == latestStable.Version));
                }

                RaiseListChanged();
                StatusText = list.Count == 0
                    ? $"{mc} 没有可用的 {Title} 版本（是不是这个 MC 版本还不支持它？）"
                    : $"共 {list.Count} 个 {Title} 版本，最新的稳定版是 {latestStable?.Version ?? "（没有稳定版）"}。";
                IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                Versions.Clear();
                RaiseListChanged();
                StatusText = "✗ " + LoaderHttp.Explain(ex, $"{Title} 版本列表");
                IsBusy = false;
            });
        }
    }

    /// <summary>
    /// 把一段改界面的动作送回到 UI 线程执行。
    ///
    /// 平时（用户点按钮）本来就在 UI 线程上，CheckAccess 会直接同步跑完；
    /// 但在没有 SynchronizationContext 的环境里（截图探针、以后可能的命令行调用），
    /// await 之后会落到线程池线程上 —— 那时候直接在池线程改绑定集合，
    /// 界面上什么都不会变（而且 WPF 会抛"另一个线程拥有它"）。所以统一过这道门。
    /// </summary>
    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <summary>
    /// 装一个加载器版本。
    ///
    /// 分两步，缺一不可：
    ///   1. <c>LoaderInstaller.InstallAsync</c> —— 生成加载器的 version json，
    ///      并把它的 libraries 备齐（Forge/NeoForge 还会跑 processor 链）；
    ///   2. <c>InstanceInstaller.InstallAsync</c> —— 按刚生成的 json 补全启动环境。
    ///      少了这一步，加载器的版本目录里没有那个自己的游戏 jar，
    ///      Fabric 会报「Class path entries reference missing files」。
    /// </summary>
    public async Task InstallAsync(InstallerRow row)
    {
        if (!IsSupported || IsBusy) return;

        var mc = McVersion.Trim();
        IsBusy = true;
        StatusText = $"正在安装 {row.Name}（{mc}）……";
        _log(LogKind.Info, $"开始安装 {row.Name}（{mc}）");

        try
        {
            var installer = new LoaderInstaller(_gameRoot);
            var progress = new Progress<LoaderInstaller.Progress>(p =>
            {
                OnUi(() => StatusText = $"{p.Stage}…（{row.Name}）");
            });

            var result = await installer.InstallAsync(
                Kind, mc, row.Version.Version, progress,
                line => _log(LogKind.Muted, "    " + line)).ConfigureAwait(false);

            foreach (var w in result.Warnings)
            {
                _log(LogKind.Warning, "    " + w);
            }

            // 第二步：补全启动环境（详见方法注释）
            OnUi(() => StatusText = $"正在补全 {result.VersionId} 的启动文件……");

            var baseInstaller = new InstanceInstaller(_gameRoot);
            var filled = await baseInstaller.InstallAsync(result.VersionId).ConfigureAwait(false);

            OnUi(() =>
            {
                StatusText = $"✓ {row.Name} 装好了：{result.VersionId}"
                           + (filled.Warnings.Count > 0 ? $"（{filled.Warnings.Count} 条提醒，都在日志里）" : "");
                IsBusy = false;
            });

            _log(LogKind.Ok, $"✓ {row.Name} 安装完成：{result.VersionId}");
        }
        catch (Exception ex)
        {
            var msg = LoaderHttp.Explain(ex, $"{row.Name} 安装");
            OnUi(() =>
            {
                StatusText = "✗ " + msg;
                IsBusy = false;
            });

            _log(LogKind.Error, $"✗ {row.Name} 安装失败：{ex.Message}");
        }
    }

    private void RaiseListChanged()
    {
        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(EmptyText));
    }
}