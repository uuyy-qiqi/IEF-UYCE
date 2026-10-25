using System.Collections.ObjectModel;
using System.IO;
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
    ///
    /// 另外：来源根本不区分稳定/测试时（NeoForge 的 maven 就是），只写版本号，
    /// 不替它下结论 —— 47.1.105 是正经发布版，只是不是最新的那个。
    /// </summary>
    public string Subtitle => Version.StabilityKnown
        ? $"{Version.MinecraftVersion} · {(Version.IsStable ? "稳定版" : "测试版")}"
        : Version.MinecraftVersion;

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

    /// <summary>上一次由我们自动填进 <see cref="McVersion"/> 的值，用来判断框里是不是用户自己改的。</summary>
    private string _lastGuess = "";
    private string _statusText = "";
    private bool _isBusy;
    private bool _isSupported = true;

    /// <summary>查过一次、而且真的一个版本都没有。</summary>
    private bool _queriedEmpty;
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

    public string EmptyText => _isBusy
        ? "正在查……"
        : _queriedEmpty
            ? $"{Title} 在这个 Minecraft 版本下没有可用版本 —— 换个版本号再「刷新」试试。"
            : "上面填个 Minecraft 版本，点「刷新」查可用版本。";

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
            "optifine" => LoaderKind.OptiFine,
            "legacyfabric" => LoaderKind.LegacyFabric,
            "liteloader" => LoaderKind.LiteLoader,
            "cleanroom" => LoaderKind.Cleanroom,
            "fabric" => LoaderKind.Fabric,
            _ => Kind,
        };

        IsSupported = true;
        Title = Kind switch
        {
            LoaderKind.Quilt => "Quilt",
            LoaderKind.Forge => "Forge",
            LoaderKind.NeoForge => "NeoForge",
            LoaderKind.OptiFine => "OptiFine",
            LoaderKind.LegacyFabric => "Legacy Fabric",
            LoaderKind.LiteLoader => "LiteLoader",
            LoaderKind.Cleanroom => "Cleanroom",
            _ => "Fabric",
        };
        HintText = Kind switch
        {
            // 这段以前写的是"直接给原版打补丁"，那是错的：optifine.Patcher 产出的是
            // 只有 srg/ 覆盖的 Forge Mod 包，不是完整客户端。1.13 以后它本来就只以
            // Mod 形式分发（自带的图形安装器要显示器，命令行跑不了），所以正解是
            // 先装 Forge、再把这个 jar 放进实例的 mods。
            LoaderKind.OptiFine =>
                "OptiFine 从 1.13 起是以 Forge Mod 的形式发布的（版本列表来自 BMCLAPI 镜像）。"
              + "点「安装这个」会先备好同版本的 Forge，再把这个 jar 放进当前实例的 mods 目录；"
              + "启动时选装出来的那个 Forge 版本就行。",
            LoaderKind.LegacyFabric =>
                "Legacy Fabric 是给 1.13.2 及更早版本用的 Fabric 分支，版本列表来自 meta.legacyfabric.net。",
            LoaderKind.LiteLoader =>
                "LiteLoader 是 1.12.2 及更早的轻量加载器（靠 launchwrapper 的 tweakClass 挂进游戏）。"
              + "它的元数据停在 1.12.2，所以先在上面把 MC 版本填成 1.12.2 再装。",
            LoaderKind.Cleanroom =>
                "Cleanroom 是 1.12.2 上继续维护的 Forge 分支（版本列表来自 repo.cleanroommc.com）。"
              + "安装方式和 Forge 一样：下 installer、跑 processor 链生成 srg 客户端 jar，"
              + "装完在「启动」页的版本列表里选它。它只有 1.12.2 这一条线。",
            _ => $"选一个 Minecraft 版本，这里会列出 {Title} 的可用版本；"
               + "点「安装这个」就会把它装成一个新版本（原版会自动一起补上），"
               + "装完在「启动」页的版本列表里就能选。",
        };

        // 这个值只是"猜"出来的默认值：当前实例是什么版本就先填什么。
        //
        // 早先写成「只要猜得出来就拿猜的覆盖」，结果是你在框里手填了 1.12.2、
        // 一切页签或者刷新一下就被改回实例的版本 —— LiteLoader / Legacy Fabric
        // 这种只认老版本的列表因此永远查不出来（界面还一直显示 1.20.1）。
        // 现在只在"框是空的"或者"框里还是上次我们自动填的那个值"时才动手。
        var guess = ContentViewModel.GuessMcVersion(_instance().VersionId);
        if (McVersion.Trim().Length == 0 || McVersion == _lastGuess)
        {
            McVersion = guess.Length > 0 ? guess : McVersion;
        }

        _lastGuess = McVersion;

        _queriedEmpty = false;
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

                _queriedEmpty = list.Count == 0;
                RaiseListChanged();
                StatusText = list.Count == 0
                    ? $"{mc} 没有可用的 {Title} 版本（是不是这个 MC 版本还不支持它？）"
                      + (Kind == LoaderKind.LegacyFabric
                          ? " Legacy Fabric 只支持 1.13.2 及更早，试试 1.12.2。"
                          : "")
                    : list[0].StabilityKnown
                        ? $"共 {list.Count} 个 {Title} 版本，最新的稳定版是 {latestStable?.Version ?? "（没有稳定版）"}。"
                        : $"共 {list.Count} 个 {Title} 版本，最新的是 {list[0].Version}。";
                IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                Versions.Clear();
                _queriedEmpty = false;
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

            // OptiFine 是 Forge Mod：它没有自己的"版本"，真正的最后一步是把这个 jar
            // 放进实例的 mods 目录。上面 InstallAsync 已经把 Forge 备好了。
            if (result.ModJarPath is not null)
            {
                var modsDir = Path.Combine(_instance().GameDir, "mods");
                Directory.CreateDirectory(modsDir);
                var dest = Path.Combine(modsDir, Path.GetFileName(result.ModJarPath));
                File.Copy(result.ModJarPath, dest, overwrite: true);

                _log(LogKind.Ok, $"OptiFine 已放进 {dest}");
                _log(LogKind.Ok, $"✓ {row.Name} 就绪：启动时选 {result.VersionId}（Forge）");

                OnUi(() =>
                {
                    StatusText = $"✓ OptiFine 已放进实例的 mods；启动时选 {result.VersionId}（Forge）";
                    IsBusy = false;
                });
                return;
            }

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