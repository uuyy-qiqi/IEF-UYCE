using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PclLike.Core.Content;
using PclLike.Core.Mods;
using PclLike.Core.Storage;

namespace PclLike.Gui;

/// <summary>下载页「社区资源」下的栏目。</summary>
public enum ContentTab
{
    Mod,
    ModPack,
    Datapack,
    ResourcePack,
    Shader,

    /// <summary>世界 —— 这一类 Modrinth 没有，只能管本地的。</summary>
    World,

    /// <summary>收藏夹 —— 也是本地的，但收藏的东西是从上面几个栏目来的。</summary>
    Favorite,
}

/// <summary>
/// 下载页「社区资源」那一屏。
///
/// ## 为什么不和 ModsWindow 共用一套
///
/// ModsWindow 管的是"这个实例已经装了什么"——读目录、看依赖、删多余的。
/// 这一屏管的是"还没装的东西从哪儿来"——搜索、挑版本、装进去。
/// 两边共享的是 Core 层的下载器（<see cref="ModDownloader"/>）和安装器
/// （<see cref="ContentInstaller"/> / <see cref="ModPackInstaller"/>），
/// 而不是 GUI 状态。
///
/// ## 世界与收藏夹为什么也在这一屏
///
/// 左栏那七项（Mod/整合包/数据包/资源包/光影包/世界/收藏夹）在 PCL 里
/// 就是同一屏换了内容，不是七个窗口。其中「世界」和「收藏夹」Modrinth
/// 没有对应项目类型（<see cref="ContentPaths.ModrinthProjectType"/> 对
/// World 返回 null），所以这两栏不给搜索框，直接管本地：世界就是
/// saves/ 下那一堆，收藏夹就是用户点过星的那些。
/// </summary>
public sealed class ContentViewModel : ObservableObject, IDisposable
{
    private readonly Func<GameInstance> _instance;
    private readonly Action<LogKind, string> _log;

    /// <summary>
    /// 安装进度文本的第二个出口（顶栏之外）。见 <see cref="InstallLocalPackAsync"/> 里的说明。
    /// 可为 null —— 没人关心进度时不用管它。
    /// </summary>
    private readonly Action<string>? _onProgress;

    /// <summary>在线下载整合包时，自动新建独立实例并选中的回调（由 MainViewModel 提供）。</summary>
    private readonly Func<string, string?, GameInstance?>? _createPackInstance;

    /// <summary>下载任意直链文件后转交拖放分类导入引擎的回调（由 MainViewModel 提供）。</summary>
    private readonly Func<IReadOnlyList<string>, Task<string>>? _importDownloadedFiles;

    /// <summary>实例或版本信息变化后通知主界面刷新的回调。</summary>
    private readonly Action? _onInstanceUpdated;

    private readonly ModDownloader _downloader = new();
    private readonly FavoritesStore _favorites;
    private readonly string _launcherRoot;
    private readonly InstanceStore? _store;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>下载临时缓存目录：优先落在玩家选定的实例安装目录下，避免占用 C 盘空间。</summary>
    private string TempDir => _store is not null
        ? Path.Combine(_store.InstancesDir, ".download-cache")
        : Path.Combine(Path.GetTempPath(), "pcllike-content");

    /// <summary>收藏夹存在启动器根目录下 —— 它是全启动器一份，不跟着实例走。</summary>
    public ContentViewModel(
        Func<GameInstance> instance,
        Action<LogKind, string> log,
        string launcherRoot,
        Action<string>? onProgress = null,
        Func<string, string?, GameInstance?>? createPackInstance = null,
        Func<IReadOnlyList<string>, Task<string>>? importDownloadedFiles = null,
        Action? onInstanceUpdated = null,
        InstanceStore? store = null)
    {
        _instance = instance;
        _log = log;
        _onProgress = onProgress;
        _createPackInstance = createPackInstance;
        _importDownloadedFiles = importDownloadedFiles;
        _onInstanceUpdated = onInstanceUpdated;
        _favorites = new FavoritesStore(launcherRoot);
        _launcherRoot = launcherRoot;
        _store = store;

        // 先把栏目设成 Mod，但**不**自动搜 —— 构造函数里发网络请求，
        // 会让"打开下载页"这件事莫名其妙地慢。真正的第一次搜索由
        // Activate 在用户点进这一栏时发起。
        SetTabFields(ContentTab.Mod);
    }

    // ══════════════════════════════════════════════════════════
    //  当前栏目
    // ══════════════════════════════════════════════════════════

    public ContentTab Tab { get; private set; } = ContentTab.Mod;

    public string Title { get; private set; } = "Mod";

    /// <summary>这一栏是干什么的、有什么要注意的。放在标题下面一行。</summary>
    public string HintText { get; private set; } = "";

    /// <summary>能不能从 Modrinth 搜。世界和收藏夹是 false。</summary>
    public bool IsOnlineTab { get; private set; } = true;

    public bool IsWorldTab => Tab == ContentTab.World;

    public bool IsFavoriteTab => Tab == ContentTab.Favorite;

    /// <summary>三类面板只显示一个，避免又长又空的界面。</summary>
    public bool ShowSearchPanel => IsOnlineTab;

    public bool ShowWorldPanel => IsWorldTab;

    public bool ShowFavoritePanel => IsFavoriteTab;

    /// <summary>Modrinth 的 project_type。世界/收藏夹用不上。</summary>
    public string ProjectType { get; private set; } = "mod";

    /// <summary>
    /// 附加的一条 Modrinth 分类过滤。目前只有数据包用得上 —— 它在 API 里
    /// 不是一种 project_type，而是 mod 里的 datapack 分类。
    /// </summary>
    public string Category { get; private set; } = "";

    /// <summary>对应的 ContentKind；收藏夹是 null（装的时候按收藏项自己的类型走）。</summary>
    public ContentKind? Kind { get; private set; } = ContentKind.Mod;

    /// <summary>这一栏要不要带加载器过滤（只有 Mod 和整合包有加载器分类）。</summary>
    public bool NeedsLoader { get; private set; }

    /// <summary>数据包得挂到某个世界上，所以要有世界选择器。</summary>
    public bool NeedsWorldPick => Tab == ContentTab.Datapack;

    /// <summary>Modrinth 上有没有"世界"这个项目类型。左栏据此决定要不要给搜索框。</summary>
    public static bool HasOnlineSource(ContentTab tab) => tab switch
    {
        ContentTab.World => false,
        ContentTab.Favorite => false,
        _ => true,
    };

    /// <summary>切到某一栏。第一次进这一栏会顺手发起一次搜索（浏览最热的那些）。</summary>
    public void Activate(ContentTab tab, bool autoSearch = true)
    {
        SetTabFields(tab);

        Results.Clear();
        Favorites.Clear();
        Worlds.Clear();
        StatusMessage = "";

        if (IsWorldTab)
        {
            RefreshWorlds();
            return;
        }

        if (IsFavoriteTab)
        {
            RefreshFavorites();
            return;
        }

        // 数据包也要知道有哪些世界可选
        if (NeedsWorldPick)
            RefreshWorlds();

        if (autoSearch)
            _ = SearchAsync();
    }

    private void SetTabFields(ContentTab tab)
    {
        Tab = tab;

        (Title, HintText, ProjectType, Category, Kind, NeedsLoader) = tab switch
        {
            ContentTab.Mod => ("Mod",
                "从 Modrinth 搜 Mod 直接装进当前实例的 mods/。搜索会按这个实例的加载器过滤 —— 装错加载器的 Mod 进游戏会崩。",
                "mod", "", (ContentKind?)ContentKind.Mod, true),

            ContentTab.ModPack => ("整合包",
                "点击「安装」会自动新建一个独立实例，下载全部 Mod 与配置并补齐对应加载器，装完即可直接启动；也支持在搜索框粘贴 Modrinth 或直链下载。",
                "modpack", "", (ContentKind?)ContentKind.ModPack, true),

            ContentTab.Datapack => ("数据包",
                "数据包是**挂在某个世界下面**的，所以上面要先选一个世界。世界不存在时不会瞎猜放哪儿，会直接报错。",
                // Modrinth 没有 datapack 这个 project_type，纯数据包在 API 里
                // 是 mod + categories 带 datapack。传 datapack 会被静默忽略。
                "mod", "datapack", (ContentKind?)ContentKind.Datapack, false),

            ContentTab.ResourcePack => ("资源包",
                "资源包装进 resourcepacks/。资源包没有加载器分类，所以这里不按加载器过滤，只按游戏版本。",
                "resourcepack", "", (ContentKind?)ContentKind.ResourcePack, false),

            ContentTab.Shader => ("光影包",
                "光影包装进 shaderpacks/。它本身还要游戏里装了 Iris 或 OptiFine 才会生效 —— 原版客户端装了也不会亮。",
                "shader", "", (ContentKind?)ContentKind.Shader, false),

            ContentTab.World => ("世界",
                "存档管理：导入别人的存档 zip、把自己玩的存档导出去分享、删掉不想要的。这里的东西都在当前实例的 saves/ 里。",
                "", "", null, false),

            ContentTab.Favorite => ("收藏夹",
                "你收藏过的项目。收藏只记项目本身（名字、类型、简介），不记版本 —— 所以点安装时会按当前实例的版本和加载器重新挑一个能用的。",
                "", "", null, false),

            _ => ("Mod", "", "mod", "", (ContentKind?)ContentKind.Mod, true),
        };

        IsOnlineTab = HasOnlineSource(tab);

        foreach (var name in new[]
                 {
                     nameof(Tab), nameof(Title), nameof(HintText), nameof(IsOnlineTab),
                     nameof(IsWorldTab), nameof(IsFavoriteTab), nameof(ShowSearchPanel),
                     nameof(ShowWorldPanel), nameof(ShowFavoritePanel), nameof(ProjectType),
                     nameof(Category), nameof(Kind), nameof(NeedsLoader), nameof(NeedsWorldPick),
                 })
            OnPropertyChanged(name);

        SyncFromInstance();
    }

    // ══════════════════════════════════════════════════════════
    //  搜索条件
    // ══════════════════════════════════════════════════════════

    private string _searchQuery = "";
    private string _mcVersion = "";
    private string _loader = "";
    private bool _isBusy;
    private string _statusMessage = "";

    public string SearchQuery
    {
        get => _searchQuery;
        set => Set(ref _searchQuery, value);
    }

    /// <summary>游戏版本。搜之前会按它过滤 —— 默认从当前实例的版本 id 猜出来，允许用户改。</summary>
    public string McVersion
    {
        get => _mcVersion;
        set { if (Set(ref _mcVersion, value)) OnPropertyChanged(nameof(SearchFilterText)); }
    }

    /// <summary>加载器。当前实例是原版时这里是空，空表示不限（原版也能搜到纯原版可用的东西）。</summary>
    public string Loader
    {
        get => _loader;
        set { if (Set(ref _loader, value)) OnPropertyChanged(nameof(SearchFilterText)); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(SearchButtonText));
                OnPropertyChanged(nameof(CanSearch));
            }
        }
    }

    public bool CanSearch => !IsBusy;

    public string SearchButtonText => IsBusy ? "正在处理…" : "搜索";

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (Set(ref _statusMessage, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _statusMessage.Length > 0;

    /// <summary>
    /// 把搜索条件对齐到当前实例。
    ///
    /// 游戏版本是从版本 id 里猜的（`fabric-loader-0.15.0-1.20.1` → `1.20.1`）：
    /// 猜不对也只是搜出来的东西偏一点，用户可以自己改，比不填好。
    /// </summary>
    public void SyncFromInstance()
    {
        // 整合包在线安装会自动新建独立实例，默认不应被当前实例的版本/加载器锁死；
        // 否则当前在 1.21.4 时会过滤掉 1.20.1 / 1.21.1 等绝大多数热门大整合包。
        if (Tab == ContentTab.ModPack)
        {
            if (McVersion == _lastGuess)
            {
                _lastGuess = "";
                McVersion = "";
            }
            Loader = "";
            OnPropertyChanged(nameof(InstanceText));
            OnPropertyChanged(nameof(SearchFilterText));
            return;
        }

        var inst = _instance();

        // 猜出来的值只在"用户没用自己改过"时才写回去：
        // 上一次是我们写进去的（等于 _lastGuess），或者用户清空了，
        // 就继续跟着实例走；否则保留用户手填的那个。
        var guess = GuessMcVersion(inst.VersionId);
        if (guess.Length > 0 && (McVersion.Trim().Length == 0 || McVersion == _lastGuess))
        {
            _lastGuess = guess;
            McVersion = guess;
        }

        var loader = MainViewModel.GuessLoader(inst.VersionId);
        Loader = loader == "vanilla" ? "" : loader;

        OnPropertyChanged(nameof(InstanceText));
        OnPropertyChanged(nameof(SearchFilterText));
    }

    /// <summary>上一次由我们自动填进 <see cref="McVersion"/> 的值。</summary>
    private string _lastGuess = "";

    public string InstanceText => $"当前实例：{_instance().Name}";

    public string SearchFilterText
    {
        get
        {
            var mc = McVersion.Trim();
            var ld = Loader.Trim();

            var parts = new List<string>();
            parts.Add(mc.Length > 0 ? $"游戏 {mc}" : "游戏版本不限");
            if (NeedsLoader) parts.Add(ld.Length > 0 ? $"加载器 {ld}" : "加载器不限");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 从版本 id 里抠出 Minecraft 版本号。
    ///
    /// 版本 id 长得五花八门：`1.21.4`、`fabric-loader-0.15.0-1.20.1`、
    /// `1.20.1-forge-47.2.0`、`neoforge-21.1.106`。共同点是里面都有个
    /// `数字.数字(.数字)` 的 MC 版本。取**第一个**匹配：
    /// 加载器自己的版本号总在 MC 版本之后（`fabric-loader-0.15.0-1.20.1` 里
    /// `0.15.0` 在前），所以这样取会取错。所以反过来取**最后一个**，
    /// 并且要求它以 `1.` 开头 —— MC 到现在所有版本都是 1.x。
    /// </summary>
    public static string GuessMcVersion(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return "";

        var matches = Regex.Matches(versionId, @"(?<!\d)(1\.\d+(?:\.\d+)?)");

        return matches.Count > 0 ? matches[^1].Value : "";
    }

    // ══════════════════════════════════════════════════════════
    //  搜索结果
    // ══════════════════════════════════════════════════════════

    private const int PageSize = 40;
    private int _totalHits;
    private int _nextOffset;
    private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ContentRow> Results { get; } = [];

    public ObservableCollection<ContentRow> Favorites { get; } = [];

    public bool HasResults => Results.Count > 0;

    public bool HasMoreResults => IsOnlineTab && Results.Count > 0 && Results.Count < _totalHits;

    public string LoadMoreText => HasMoreResults
        ? $"加载更多（已显示 {Results.Count} / 共 {_totalHits:N0} 个）"
        : $"已显示全部 {Results.Count} 个结果";

    public string ResultsCountText => Results.Count > 0
        ? (_totalHits > Results.Count ? $"已显示 {Results.Count} / 共 {_totalHits:N0} 条" : $"{Results.Count} 条结果")
        : "";

    // ── 线程 ──
    //
    // Core 层的异步方法内部都带 ConfigureAwait(false)，所以 await 之后的代码
    // 多半落在线程池线程上。而 WPF 的 ItemsControl 绑的是 CollectionView ——
    // 谁在非 UI 线程上动 ObservableCollection，它当场就抛
    // "不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改"。
    // 这个错在界面上表现为"搜索失败"，所以所有列表改动统一走这里。
    private readonly System.Windows.Threading.Dispatcher _ui =
        System.Windows.Application.Current?.Dispatcher
        ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

    private void OnUi(Action action)
    {
        if (_ui.CheckAccess()) action();
        else _ui.Invoke(action);
    }

    /// <summary>搜索。空关键词也发请求 —— Modrinth 会返回最热的那些；若输入了 http(s) 链接则直接下载并自动导入。</summary>
    public async Task SearchAsync()
    {
        if (!IsOnlineTab || IsBusy) return;

        var rawQuery = SearchQuery.Trim();
        if (rawQuery.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            rawQuery.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await InstallFromUrlAsync(rawQuery);
            return;
        }

        _totalHits = 0;
        _nextOffset = 0;
        await FetchPageAsync(append: false);
    }

    /// <summary>加载下一页搜索结果。</summary>
    public async Task LoadMoreAsync()
    {
        if (!IsOnlineTab || IsBusy || !HasMoreResults) return;
        await FetchPageAsync(append: true);
    }

    private async Task FetchPageAsync(bool append)
    {
        var rawQuery = SearchQuery.Trim();
        IsBusy = true;
        StatusMessage = append
            ? $"正在加载更多{Title}…"
            : (rawQuery.Length > 0 ? $"正在搜索「{rawQuery}」…" : $"正在列出最热门的{Title}…");

        try
        {
            var mc = McVersion.Trim();
            var loader = NeedsLoader && Loader.Trim().Length > 0 ? Loader.Trim() : null;
            var offset = append ? _nextOffset : 0;

            var page = await _downloader.SearchModrinthPageAsync(
                rawQuery,
                mc.Length > 0 ? mc : null,
                loader,
                limit: PageSize,
                offset: offset,
                ct: _cts.Token,
                projectType: ProjectType,
                category: Category.Length > 0 ? Category : null);

            _totalHits = page.TotalHits;
            _nextOffset = offset + page.Hits.Count;

            var rows = page.Hits
                .Select(r => ContentRow.FromSearch(r, _favorites.Has(r.ProjectId), Title))
                .ToList();

            OnUi(() =>
            {
                if (!append) Results.Clear();
                foreach (var row in rows) Results.Add(row);
            });

            // 后台并发加载封面图（不阻塞列表渲染，失败静默退回占位图标）
            _ = LoadIconsAsync(rows);

            var warn = NeedsWorldPick && Worlds.Count == 0
                ? "⚠ 这个实例还没有存档，数据包没地方装 —— 进去建一个世界再回来。\n"
                : "";

            var actionTip = Tab == ContentTab.ModPack
                ? "点「安装」会自动新建独立实例并装好加载器。"
                : "点「安装」直接导入当前实例。";

            StatusMessage = warn + (Results.Count == 0
                ? "一条都没搜到。换个关键词，或者把游戏版本/加载器清空（不限）试试。"
                : $"已显示 {Results.Count} / 共 {_totalHits:N0} 条{Title}。{actionTip}");

            _log(LogKind.Info, $"{Title} 搜索「{rawQuery}」→ 已显示 {Results.Count}/{_totalHits} 条");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "搜索已取消。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"搜索失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasMoreResults));
            OnPropertyChanged(nameof(LoadMoreText));
            OnPropertyChanged(nameof(ResultsCountText));
        }
    }

    /// <summary>后台限流拉取封面图并缓存（走代理回退通道，避免 WPF 原生 Image 不走代理而空白）。</summary>
    private async Task LoadIconsAsync(IReadOnlyList<ContentRow> rows)
    {
        using var gate = new SemaphoreSlim(6, 6);
        var tasks = rows.Where(r => !string.IsNullOrWhiteSpace(r.IconUrl)).Select(async row =>
        {
            var url = row.IconUrl!;
            if (IconCache.TryGetValue(url, out var cached))
            {
                OnUi(() => row.SetIcon(cached));
                return;
            }

            await gate.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (_cts.IsCancellationRequested) return;
                var bytes = await _downloader.FetchBytesAsync(url, _cts.Token).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0) return;

                var bmp = DecodeFrozenBitmap(bytes);
                if (bmp is null) return;

                IconCache[url] = bmp;
                OnUi(() => row.SetIcon(bmp));
            }
            catch
            {
                // 单张封面加载失败不影响列表，保留默认占位图
            }
            finally
            {
                gate.Release();
            }
        });

        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
    }

    private static ImageSource? DecodeFrozenBitmap(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 96;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  安装
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 数据包要装到哪个世界。
    ///
    /// setter 必须把 null 吃掉 —— 这个属性双向绑在「装到哪个世界」那个 ComboBox 上，
    /// 而 ComboBox 在候选被清空时会把 SelectedValue 推成 null（不是空串）。
    /// 漏进来之后 RefreshWorlds 里一句 TargetWorld.Length 就会抛
    /// NullReferenceException —— 自检就是这么炸出来的。
    /// </summary>
    private string _targetWorld = "";

    public string TargetWorld
    {
        get => _targetWorld;
        set => Set(ref _targetWorld, value ?? "");
    }

    public ObservableCollection<WorldInfo> Worlds { get; } = [];

    public bool HasWorlds => Worlds.Count > 0;

    /// <summary>
    /// 把一条搜索结果装进当前实例。
    ///
    /// 流程和 ModsWindow 里那条一致：先按 id + 版本 + 加载器挑一个能用的版本，
    /// 下载到临时目录，再交给安装器放到位。整合包走的是另一条路（它要下一整份清单）。
    /// </summary>
    public async Task InstallAsync(ContentRow row)
    {
        if (IsBusy) return;

        var kind = KindFor(row.ProjectType);
        if (kind is null)
        {
            StatusMessage = $"这一栏认不出「{row.ProjectType}」这种项目，没法安装。";
            return;
        }

        string? worldName = null;
        if (kind == ContentKind.Datapack)
        {
            worldName = ResolveDatapackWorld();
            if (worldName is null) return;
        }

        var inst = _instance();
        var mc = McVersion.Trim();
        var loader = NeedsLoader && Loader.Trim().Length > 0 ? Loader.Trim() : "";

        var tmp = "";

        // 整合包的清单（只有整合包才读）
        ModPackPlan? plan = null;

        IsBusy = true;
        row.SetInstalling(true, "解析版本…");

        try
        {
            StatusMessage = $"正在给「{row.Title}」找一个能用的版本…";

            // 光影包、资源包、整合包和数据包允许版本智能回退（例如 1.21.4 回退到 1.21 或通用版），
            // 避免因作者未勾选最新小版本而无法下载。
            var allowFallback = kind is ContentKind.Shader or ContentKind.ResourcePack
                                     or ContentKind.ModPack or ContentKind.Datapack;

            var cand = await _downloader.FindModrinthByIdAsync(
                row.ProjectId, mc, loader, _cts.Token, allowVersionFallback: allowFallback);

            if (cand is null)
            {
                StatusMessage = mc.Length > 0
                    ? $"「{row.Title}」没有匹配 {mc}{(loader.Length > 0 ? " / " + loader : "")} 的版本。"
                    : $"「{row.Title}」没有可下载的版本。";
                row.SetInstalling(false, "版本不匹配");
                return;
            }

            var ext = Path.GetExtension(cand.FileName);
            var cacheDir = TempDir;
            tmp = Path.Combine(cacheDir, Guid.NewGuid().ToString("N") + (ext.Length > 0 ? ext : ".jar"));
            Directory.CreateDirectory(cacheDir);

            var progress = new Progress<DownloadProgress>(p =>
            {
                StatusMessage = $"正在下载 {p.Name}：{p.Text}";
                _onProgress?.Invoke($"正在下载 {p.Name} ({(int)(p.Fraction * 100)}/100)");
            });

            row.SetInstalling(true, "下载中…");
            StatusMessage = $"正在下载 {cand.FileName}…";
            if (!await _downloader.DownloadWithRetryAsync(cand, tmp, progress, maxAttempts: 2, _cts.Token))
            {
                StatusMessage = $"下载 {cand.FileName} 失败 —— 网络问题的话过一会儿再试一次。";
                row.SetInstalling(false, "重试安装");
                return;
            }

            var size = new FileInfo(tmp).Length;

            InstallResult result;
            var targetInst = inst;

            if (kind == ContentKind.ModPack)
            {
                row.SetInstalling(true, "装包中…");
                plan = await Task.Run(() => ModPackInstaller.ReadPlan(tmp), _cts.Token);

                // 像 PCL2 一样：在线下载整合包时优先自动新建独立实例，绝不覆盖玩家当前正在玩的实例，
                // 也不会因为当前实例 MC 版本不同而拦死下载。
                if (_createPackInstance is not null)
                {
                    var packName = !string.IsNullOrWhiteSpace(plan?.Name) ? plan!.Name : row.Title;
                    var created = _createPackInstance(packName, plan?.McVersion);
                    if (created is not null)
                    {
                        targetInst = created;
                        _log(LogKind.Info, $"{Title}：已为整合包「{packName}」新建独立实例「{created.Name}」");
                    }
                }
                else
                {
                    // 无新建实例回调时（例如命令行探针），沿用原版本一致性检查
                    var mismatch = VersionMismatchReason(plan?.McVersion, targetInst);
                    if (mismatch is not null)
                    {
                        _log(LogKind.Error, $"{Title}：「{row.Title}」{mismatch}");
                        StatusMessage = mismatch;
                        row.SetInstalling(false, "版本不匹配");
                        return;
                    }
                }

                var packProgress = new Progress<string>(s =>
                {
                    StatusMessage = s;
                    _onProgress?.Invoke(s);
                });

                result = await ModPackInstaller.InstallAsync(
                    tmp,
                    targetInst,
                    FetchPackFileAsync,
                    packProgress,
                    _cts.Token);
            }
            else
            {
                row.SetInstalling(true, "导入中…");
                result = await ContentInstaller.InstallFileAsync(
                    targetInst, kind.Value, tmp, cand.FileName, worldName, _cts.Token);
            }

            StatusMessage = result.Message;

            if (result.Ok)
            {
                _log(LogKind.Ok, $"{Title}：{row.Title} {cand.Version} → {result.InstalledPath ?? targetInst.GameDir}");

                // Mod 安装完成后，像 PCL2 一样自动补齐必需前置依赖（如 Fabric API / Cloth Config）
                var extraDeps = 0;
                if (kind == ContentKind.Mod && cand.DependencyIds.Count > 0)
                {
                    row.SetInstalling(true, "补前置…");
                    extraDeps = await AutoInstallModDependenciesAsync(targetInst, cand, mc, loader);
                }

                var extraNote = extraDeps > 0 ? $"，并自动补齐 {extraDeps} 个必需前置" : "";
                if (kind == ContentKind.Shader && !HasShaderLoaderMod(targetInst))
                    extraNote += "（提示：进游戏前需在 Mod 栏安装 Iris 或 Oculus 才能启用光影）";

                StatusMessage = $"{result.Message}（{size / 1024.0 / 1024.0:0.#} MB）{extraNote}";

                // 整合包装完后补齐 Minecraft 原版、加载器与运行库，并切换到新实例
                if (kind == ContentKind.ModPack)
                {
                    row.SetInstalling(true, "装加载器…");
                    await InstallPackEnvironmentAsync(targetInst, plan);
                }

                row.SetInstalled(true, extraDeps > 0 ? $"已装 (+{extraDeps}前置)" : "已安装");

                if (kind == ContentKind.World || kind == ContentKind.Datapack)
                    RefreshWorlds();

                _onInstanceUpdated?.Invoke();
            }
            else
            {
                _log(LogKind.Warning, $"{Title}：{row.Title} 安装失败 —— {result.Message}");
                row.SetInstalling(false, "重试安装");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "安装已取消。";
            row.SetInstalling(false, "安装");
        }
        catch (Exception ex)
        {
            StatusMessage = $"安装失败：{ex.Message}";
            _log(LogKind.Error, $"{Title}：{row.Title} 安装异常 {ex.GetType().Name} —— {ex.Message}");
            row.SetInstalling(false, "重试安装");
        }
        finally
        {
            IsBusy = false;

            // 临时文件一定要清掉：整合包能有几百 MB，攒在 %TEMP% 里
            // 几次下来就是几个 GB。清不掉也不影响安装结果。
            if (tmp.Length > 0)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 留着无害 */ }
            }
        }
    }

    /// <summary>
    /// 通过链接直接下载并自动导入（支持 Modrinth 详情页链接，以及任意 .mrpack/.zip/.jar/.rar/.7z 直链）。
    /// </summary>
    public async Task InstallFromUrlAsync(string rawUrl)
    {
        if (IsBusy) return;
        var url = (rawUrl ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            StatusMessage = "请输入有效的 http:// 或 https:// 下载链接。";
            return;
        }

        // 情形 1：Modrinth 页面链接（如 https://modrinth.com/mod/sodium 或 /shader/bsl-shaders）
        var (mrType, slug) = ModDownloader.TryParseModrinthUrl(url);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var projectType = string.IsNullOrEmpty(mrType) ? ProjectType : mrType;
            var syntheticRow = new ContentRow
            {
                ProjectId = slug,
                Slug = slug,
                Title = slug,
                ProjectType = projectType,
                Badge = Title,
            };
            await InstallAsync(syntheticRow);
            return;
        }

        // 情形 2：文件直链（整合包 / Mod / 光影包 / 资源包 / 存档）→ 下载后走统一自动识别导入
        IsBusy = true;
        var tmp = "";
        try
        {
            var cacheDir = TempDir;
            Directory.CreateDirectory(cacheDir);
            var guessedName = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(guessedName) || !guessedName.Contains('.'))
                guessedName = "download.zip";

            tmp = Path.Combine(cacheDir, $"{Guid.NewGuid():N}_{ContentInstaller.SafeName(guessedName, "download.zip")}");
            StatusMessage = $"正在从链接下载 {guessedName}…";
            _log(LogKind.Info, $"通过链接下载：{url}");

            var progress = new Progress<DownloadProgress>(p =>
            {
                StatusMessage = $"正在下载：{p.Text}";
                _onProgress?.Invoke($"正在下载 {p.Name} ({(int)(p.Fraction * 100)}/100)");
            });

            var (ok, _) = await _downloader.DownloadUrlAsync(url, tmp, progress, _cts.Token);
            if (!ok || !File.Exists(tmp))
            {
                StatusMessage = "链接下载失败，请检查地址是否可直接访问或检查代理设置。";
                return;
            }

            if (_importDownloadedFiles is not null)
            {
                StatusMessage = $"下载完成，正在自动识别并导入 {guessedName}…";
                // 释放 IsBusy，避免 DropFilesAsync 内部回调 InstallLocalPackAsync 时被 IsBusy 拦下
                IsBusy = false;
                var summary = await _importDownloadedFiles([tmp]);
                StatusMessage = string.IsNullOrWhiteSpace(summary) ? "导入完成。" : summary.Replace('\n', ' ');
                _onInstanceUpdated?.Invoke();
            }
            else if (Kind is { } k && k != ContentKind.ModPack)
            {
                var r = await ContentInstaller.InstallFileAsync(_instance(), k, tmp, guessedName, null, _cts.Token);
                StatusMessage = r.Message;
            }
            else
            {
                StatusMessage = $"已下载到：{tmp}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "下载已取消。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"链接导入失败：{ex.Message}";
            _log(LogKind.Error, $"链接导入异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            if (tmp.Length > 0)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

    /// <summary>自动递归补齐 Mod 的必需前置依赖（最多 12 个，防止依赖环）。</summary>
    private async Task<int> AutoInstallModDependenciesAsync(
        GameInstance inst, ModDownloadCandidate root, string mcVersion, string loader)
    {
        var installed = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.ProjectId };
        var queue = new Queue<string>(root.DependencyIds);

        while (queue.Count > 0 && installed < 12)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var depId = queue.Dequeue();
            if (string.IsNullOrWhiteSpace(depId) || !visited.Add(depId)) continue;

            try
            {
                var depCand = await _downloader.FindModrinthByIdAsync(depId, mcVersion, loader, _cts.Token);
                if (depCand is null) continue;

                var modsDir = ContentPaths.TargetDir(inst, ContentKind.Mod);
                var expectedDest = ContentInstaller.ResolveInside(modsDir, ContentInstaller.SafeName(depCand.FileName, "dep.jar"));
                if (expectedDest is not null && File.Exists(expectedDest)) continue;

                var cacheDir = TempDir;
                Directory.CreateDirectory(cacheDir);
                var depTmp = Path.Combine(cacheDir, Guid.NewGuid().ToString("N") + ".jar");
                try
                {
                    StatusMessage = $"正在自动补齐前置依赖：{depCand.Name}…";
                    if (!await _downloader.DownloadWithRetryAsync(depCand, depTmp, null, maxAttempts: 2, _cts.Token))
                        continue;

                    var r = await ContentInstaller.InstallFileAsync(
                        inst, ContentKind.Mod, depTmp, depCand.FileName, null, _cts.Token);
                    if (r.Ok)
                    {
                        installed++;
                        _log(LogKind.Ok, $"自动补齐前置：{depCand.Name} ({depCand.FileName})");
                        foreach (var sub in depCand.DependencyIds)
                            if (!visited.Contains(sub)) queue.Enqueue(sub);
                    }
                }
                finally
                {
                    try { if (File.Exists(depTmp)) File.Delete(depTmp); } catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // 单个前置补齐失败不影响主 Mod 使用
            }
        }

        return installed;
    }

    /// <summary>检查实例 mods 目录里是否已经装了常见的光影加载器（Iris / Oculus / OptiFine）。</summary>
    private static bool HasShaderLoaderMod(GameInstance inst)
    {
        try
        {
            var modsDir = Path.Combine(inst.GameDir, "mods");
            if (!Directory.Exists(modsDir)) return false;

            return Directory.EnumerateFiles(modsDir, "*.jar")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Any(n => n.Contains("iris", StringComparison.OrdinalIgnoreCase)
                       || n.Contains("oculus", StringComparison.OrdinalIgnoreCase)
                       || n.Contains("optifine", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 整合包装完之后把游戏环境补齐：安装包要求的加载器，并把当前实例指到它。
    ///
    /// 为什么非要这一步：Modrinth 整合包只保证"文件都在"，不保证"能开"。实测
    /// 一个 1.20.1 + Fabric 0.14.23 的包，51 个文件全部落盘、Mod 全部加载
    /// （149 个 Mod 的 "Loading 149 mods:" 都打出来了），但如果加载器没装/版本
    /// 不对，Fabric 会直接抛：
    ///   Class path entries reference missing files: .../&lt;loaderId&gt;.jar
    /// 用户看到的就是"安装成功"四个字，然后点启动游戏什么都没发生。
    ///
    /// 失败不回滚、也不把整次安装判负 —— Mod 和配置已经躺在磁盘上了，那是
    /// 有意义的成果；这一步只是尽力把最后一段路铺上，铺不上要说清楚。
    /// </summary>
    private async Task InstallPackEnvironmentAsync(GameInstance inst, ModPackPlan? plan)
    {
        if (plan is null)
        {
            _log(LogKind.Warning, $"{Title}：读不出整合包清单，没法自动补版本和加载器。");
            return;
        }

        if (string.IsNullOrWhiteSpace(plan.LoaderName) || plan.LoaderName == "vanilla")
        {
            // 原版包：只要原版装着就行
            if (!Directory.Exists(Path.Combine(_launcherRoot, "versions", plan.McVersion)))
            {
                StatusMessage = $"{StatusMessage}（这个包是原版 {plan.McVersion}，要先去「下载」把原版装上）";
            }

            return;
        }

        var kind = PackLoaderKind(plan.LoaderName);
        if (kind is null)
        {
            _log(LogKind.Warning, $"{Title}：不认识的加载器「{plan.LoaderName}」，无法自动安装。");
            return;
        }

        try
        {
            var installer = new PclLike.Core.Minecraft.LoaderInstaller(_launcherRoot);
            using (installer)
            {
                var already = Directory.Exists(
                    Path.Combine(_launcherRoot, "versions",
                        PackLoaderVersionId(plan.LoaderName, plan.LoaderVersion ?? "", plan.McVersion)));

                StatusMessage = already
                    ? $"整合包还要求 {plan.LoaderName} {plan.LoaderVersion}，本机已有，直接指向它…"
                    : $"整合包还要求 {plan.LoaderName} {plan.LoaderVersion}，正在装加载器…";

                var p = new Progress<PclLike.Core.Minecraft.LoaderInstaller.Progress>(
                    x => StatusMessage = already
                        ? StatusMessage
                        : $"正在装 {plan.LoaderName} {plan.LoaderVersion}：{x.Stage} {x.Done}/{x.Total}");

                var r = await installer.InstallAsync(
                    kind.Value,
                    plan.McVersion,
                    plan.LoaderVersion,
                    p,
                    s => _log(LogKind.Info, $"{Title}：{s}"),
                    _cts.Token);

                // 补全加载器自身对应的客户端启动环境（libraries / assets / natives），装完即可直接启动
                StatusMessage = $"正在补全 {r.VersionId} 的启动文件…";
                using (var baseInstaller = new PclLike.Core.Storage.Downloader())
                {
                    var envInstaller = new PclLike.Core.Minecraft.InstanceInstaller(_launcherRoot, baseInstaller);
                    await envInstaller.InstallAsync(r.VersionId, null, _cts.Token);
                }

                // 把实例指到这个版本上 —— 不然"当前实例"还是原来那个，
                // 玩家点启动游戏跑的仍然是没有 Mod 的原版。
                inst.VersionId = r.VersionId;
                inst.Loader = plan.LoaderName.ToLowerInvariant();

                // 存盘 —— 写入当前实例仓库（支持自定义实例安装目录），避免落回 C 盘默认目录。
                try { (_store ?? new PclLike.Core.Storage.InstanceStore(_launcherRoot)).Save(inst); }
                catch (Exception se)
                {
                    _log(LogKind.Warning, $"{Title}：实例存盘失败 —— {se.Message}");
                }

                foreach (var w in r.Warnings.Take(5))
                    _log(LogKind.Warning, $"{Title}：加载器警告 —— {w}");

                _log(LogKind.Ok, $"{Title}：实例「{inst.Name}」已就绪（{r.VersionId}），可直接点击启动游戏");

                StatusMessage = $"✓ 整合包已装好并切换到新实例「{inst.Name}」（{r.VersionId}），回到「启动」页即可直接玩！";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log(LogKind.Error, $"{Title}：自动装加载器失败 {ex.GetType().Name} —— {ex.Message}");
            StatusMessage = $"{StatusMessage}（但自动装 {plan.LoaderName} {plan.LoaderVersion} 失败了，"
                + "请去「下载」手动装一次这个加载器，再把实例的版本改成它）";
        }
    }

    /// <summary>
    /// 加载器的版本目录名。这四种命名互不相同，是各家 provider 自己的约定，
    /// 猜错不会报错、只会让"本机已有"永远判成 false，然后就白装一遍
    /// （所以这里是照着真实目录名写的，不是拼一个看起来合理的字符串）：
    ///   fabric-loader-0.20.1-1.20.1 / quilt-loader-x-y / 1.20.1-forge-47.4.28 / neoforge-47.1.106
    /// </summary>
    private static string PackLoaderVersionId(string loaderName, string loaderVersion, string mcVersion) =>
        loaderName.ToLowerInvariant() switch
        {
            "fabric" => $"fabric-loader-{loaderVersion}-{mcVersion}",
            "quilt" => $"quilt-loader-{loaderVersion}-{mcVersion}",
            "forge" => $"{mcVersion}-forge-{loaderVersion}",
            "neoforge" => $"neoforge-{loaderVersion}",
            _ => $"{loaderName}-{loaderVersion}",
        };

    /// <summary>整合包里的加载器名 → LoaderKind。认不出返回 null。</summary>
    private static PclLike.Core.Minecraft.Loaders.LoaderKind? PackLoaderKind(string? name) =>
        name?.ToLowerInvariant() switch
        {
            "fabric" => PclLike.Core.Minecraft.Loaders.LoaderKind.Fabric,
            "quilt" => PclLike.Core.Minecraft.Loaders.LoaderKind.Quilt,
            "forge" => PclLike.Core.Minecraft.Loaders.LoaderKind.Forge,
            "neoforge" => PclLike.Core.Minecraft.Loaders.LoaderKind.NeoForge,
            _ => null,
        };

    /// <summary>
    /// 从一个实例的实际版本号里取 MC 版本。
    ///
    /// 实例的 VersionId 有三种形状：<c>1.20.1</c>（原版）、<c>1.20.1-forge-47.4.28</c>
    /// （Forge 把 MC 摆在前面）、<c>neoforge-47.1.106</c> / <c>fabric-loader-0.20.1-1.20.1</c>
    /// （MC 摆在后面甚至不写）。所以：
    ///   1) 开头就是 1.x 形式 → 直接用；
    ///   2) 否则读 versions/&lt;id&gt;/&lt;id&gt;.json 的 <c>inheritsFrom</c>（Forge/Fabric 都写这个）；
    ///   3) 都拿不到 → 返回空串，调用方按"不知道"处理（宁可不拦，也不能凭猜把
    ///      正常安装挡掉）。
    /// </summary>
    private string McVersionOfInstance(GameInstance inst)
    {
        var id = inst.VersionId ?? "";
        if (id.Length > 0 && char.IsDigit(id[0]))
        {
            var m = System.Text.RegularExpressions.Regex.Match(id, @"^\d+\.\d+(?:\.\d+)?");
            if (m.Success) return m.Value;
        }

        try
        {
            var json = Path.Combine(_launcherRoot, "versions", id, id + ".json");
            if (File.Exists(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var p)
                    && p.ValueKind == System.Text.Json.JsonValueKind.String)
                    return p.GetString() ?? "";
            }
        }
        catch { }

        return "";
    }

    /// <summary>
    /// 包要求的 MC 和实例不一致 → 返回该说的那句话；一致或拿不准 → null。
    ///
    /// 真实踩过的坑：把一个 1.21.1 的包拖进 1.20.1 的实例，启动器照装不误 ——
    /// 208 个 Mod、1123 个配置被覆盖进去，那个实例直接废了，而界面上写的是
    /// 「安装成功」。PCL 的做法是压根不装并说清为什么，这里照做。
    /// </summary>
    private string? VersionMismatchReason(string? packMc, GameInstance inst)
    {
        var want = (packMc ?? "").Trim();
        if (want.Length == 0) return null;

        var mc = McVersionOfInstance(inst);
        if (mc.Length == 0) return null;
        if (want.Equals(mc, StringComparison.OrdinalIgnoreCase)) return null;

        return $"这个整合包要求 Minecraft {want}，而实例「{inst.Name}」是 {mc} —— "
             + $"已经拦下，一个文件都没往里装。请先新建（或切到）一个 {want} 的实例，再装这个包。";
    }

    /// <summary>
    /// 装一个"本地文件"整合包（用户拖进来的，不是从 Modrinth 下的）。
    ///
    /// 和 InstallAsync 的整合包分支是同一条路，只是跳过"找版本 + 下载" ——
    /// 文件已经在磁盘上了。装完照样走 InstallPackEnvironmentAsync，
    /// 也就是说拖进来的整合包同样会把包要求的加载器顺手装上。
    /// </summary>
    public async Task<string> InstallLocalPackAsync(string mrpackPath, GameInstance target)
    {
        if (IsBusy) return "启动器正忙着别的事，等它跑完再拖一次。";

        IsBusy = true;

        try
        {
            ModPackPlan? plan = null;
            try { plan = ModPackInstaller.ReadPlan(mrpackPath); }
            catch { /* 清单读不出来，装完再慢慢说 */ }

            // ★ 拖进来的包同样先对版本。这里就是事故现场：用户把 1.21.1 的包拖到
            //   一个 1.20.1 的实例上，Mod 全铺进去了才发现加载器装不上。
            var mismatch = VersionMismatchReason(plan?.McVersion, target);
            if (mismatch is not null)
            {
                _log(LogKind.Error, $"{Title}：{mismatch}");
                return mismatch;
            }

            StatusMessage = plan is null
                ? "正在装整合包…"
                : $"正在装整合包「{plan.Name}」（{plan.McVersion}"
                  + (plan.LoaderName is null ? "" : $" / {plan.LoaderName} {plan.LoaderVersion}") + "）…";

            // 进度文本的出口有两个：
            //  · StatusMessage —— 顶栏那一行（原来只有它）
            //  · _onProgress    —— 「任务管理」窗
            //
            // 为什么要第二个：任务管理窗**不碰安装代码**，它订阅的是日志流，靠认
            // 句式推进清单。可进度回调根本不打日志（顶栏一行就够看了，每 tick 都
            // 写日志会把日志冲爆），于是拖整包时那个窗永远是 0.00%、一个步骤都没有
            // —— 用户的原话是「为什么导入没进度」。所以把同一份文本也送一份过去，
            // 让 TaskProgressViewModel.OnLog 用**同一套句式**（「已解开 189 / 约 1234 个文件」
            // 「拖入整包」「解压到新实例」）认出步骤和百分比。
            var progress = new Progress<string>(s =>
            {
                StatusMessage = s;
                _onProgress?.Invoke(s);
            });

            var r = await ModPackInstaller.InstallAsync(
                mrpackPath,
                target,
                FetchPackFileAsync,
                progress,
                _cts.Token);

            if (!r.Ok)
            {
                _log(LogKind.Warning, $"{Title}：整合包安装失败 —— {r.Message}");
                return r.Message;
            }

            StatusMessage = r.Message;
            _log(LogKind.Ok, $"{Title}：整合包 → {target.GameDir}（{r.Message}）");

            await InstallPackEnvironmentAsync(target, plan);

            return $"{r.Message}\n\n游戏目录：{target.GameDir}";
        }
        catch (OperationCanceledException)
        {
            return "安装已取消。";
        }
        catch (Exception ex)
        {
            _log(LogKind.Error, $"{Title}：整合包安装异常 {ex.GetType().Name} —— {ex.Message}");
            return $"安装失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>装一个本地文件（Mod / 资源包 / 光影包 / 数据包）到当前实例。</summary>
    public async Task<string> InstallLocalContentAsync(string path, ContentKind kind)
    {
        if (IsBusy) return "启动器正忙着别的事，等它跑完再拖一次。";

        string? worldName = null;
        if (kind == ContentKind.Datapack)
        {
            worldName = ResolveDatapackWorld();
            if (worldName is null) return "没选中存档，先停下了。";
        }

        var inst = _instance();

        IsBusy = true;

        try
        {
            StatusMessage = $"正在装 {Path.GetFileName(path)}…";

            var r = await ContentInstaller.InstallFileAsync(inst, kind, path, null, worldName, _cts.Token);

            StatusMessage = r.Message;

            if (r.Ok)
                _log(LogKind.Ok, $"{Title}：{Path.GetFileName(path)} → {r.InstalledPath ?? inst.GameDir}");
            else
                _log(LogKind.Warning, $"{Title}：{Path.GetFileName(path)} 安装失败 —— {r.Message}");

            return r.Message;
        }
        catch (OperationCanceledException)
        {
            return "安装已取消。";
        }
        catch (Exception ex)
        {
            _log(LogKind.Error, $"{Title}：{Path.GetFileName(path)} 安装异常 {ex.GetType().Name} —— {ex.Message}");
            return $"安装失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把拖进来的存档 zip 导进当前实例的 saves/。</summary>
    public async Task<string> ImportLocalWorldAsync(string zipPath)
    {
        if (IsBusy) return "启动器正忙着别的事，等它跑完再拖一次。";

        var inst = _instance();
        IsBusy = true;

        try
        {
            StatusMessage = $"正在导入存档 {Path.GetFileName(zipPath)}…";

            var r = await WorldStore.ImportZipAsync(inst, zipPath, _cts.Token);

            StatusMessage = r.Message;
            if (r.Ok) _log(LogKind.Ok, $"{Title}：存档 {Path.GetFileName(zipPath)} → {r.InstalledPath ?? inst.GameDir}");
            else _log(LogKind.Warning, $"{Title}：存档导入失败 —— {r.Message}");

            RefreshWorlds();
            return r.Message;
        }
        catch (OperationCanceledException)
        {
            return "导入已取消。";
        }
        catch (Exception ex)
        {
            _log(LogKind.Error, $"{Title}：存档导入异常 {ex.GetType().Name} —— {ex.Message}");
            return $"导入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Modrinth 的项目类型 → 我们的内容类型。认不出返回 null。</summary>
    public static ContentKind? KindFor(string? projectType) => projectType?.ToLowerInvariant() switch
    {
        "mod" => ContentKind.Mod,
        "modpack" => ContentKind.ModPack,
        "datapack" => ContentKind.Datapack,
        "resourcepack" => ContentKind.ResourcePack,
        "shader" => ContentKind.Shader,
        _ => null,
    };

    /// <summary>数据包的目标世界。没世界可选时直接说明白，不猜。</summary>
    private string? ResolveDatapackWorld()
    {
        RefreshWorlds();

        if (Worlds.Count == 0)
        {
            StatusMessage = "这个实例还没有任何世界 —— 数据包得挂在世界上，先去游戏里建一个，或者在这一栏的「世界」里导入一个存档。";
            return null;
        }

        var picked = Worlds.FirstOrDefault(w => w.Name == TargetWorld) ?? Worlds[0];
        TargetWorld = picked.Name;
        return picked.Name;
    }

    /// <summary>整合包清单里每个文件的下载动作。交给现成的下载器做重试/镜像那一套。</summary>
    private async Task<bool> FetchPackFileAsync(ModPackFile file, string destPath, CancellationToken ct)
    {
        var cand = new ModDownloadCandidate
        {
            Platform = "modrinth",
            ProjectId = "",
            VersionId = "",
            Name = file.FileName,
            Version = "",
            FileName = file.FileName,
            DownloadUrl = file.Url,
            SizeBytes = file.SizeBytes,
            Sha1 = file.Sha1,
        };

        return await _downloader.DownloadWithRetryAsync(cand, destPath, null, maxAttempts: 2, ct);
    }

    // ══════════════════════════════════════════════════════════
    //  世界
    // ══════════════════════════════════════════════════════════

    public void RefreshWorlds()
    {
        var list = new List<PclLike.Core.Content.WorldInfo>();

        try
        {
            list.AddRange(WorldStore.List(_instance()));
        }
        catch (Exception ex)
        {
            StatusMessage = $"读存档列表失败：{ex.Message}";
        }

        OnUi(() =>
        {
            Worlds.Clear();
            foreach (var w in list) Worlds.Add(w);
        });

        if (TargetWorld.Length == 0 || Worlds.All(w => w.Name != TargetWorld))
            TargetWorld = Worlds.FirstOrDefault()?.Name ?? "";

        // 这一栏空着的时候得说句话，不然用户看到的就是一大片白 ——
        // 分不清是"没有存档"还是"界面坏了没加载出来"。
        if (IsWorldTab)
            StatusMessage = Worlds.Count == 0
                ? "这个实例还没有存档。进游戏创建一个世界，或者用上面的【导入存档 zip】把别人的存档搬进来。"
                : $"共 {Worlds.Count} 个存档。点行尾的按钮可以打开目录、导出成 zip 或删除。";
        else if (NeedsWorldPick && Worlds.Count == 0)
            StatusMessage = "这个实例还没有存档，数据包没地方装。先进游戏创建一个世界，或者切到【世界】栏导入一个存档。";

        OnPropertyChanged(nameof(HasWorlds));
    }

    /// <summary>这一栏对应的目录，「打开目录」按钮用。</summary>
    public string ContentDirForTab()
    {
        var inst = _instance();

        if (Tab == ContentTab.World)
            return Path.Combine(inst.GameDir, "saves");

        return Kind is null ? inst.GameDir : ContentPaths.TargetDir(inst, Kind.Value);
    }

    /// <summary>把指定的世界打包成 zip，放到用户选的位置。</summary>
    public async Task<string> ExportWorldAsync(string worldName, string zipPath)
    {
        if (worldName.Length == 0) return "没有指定要导出的世界。";

        IsBusy = true;
        StatusMessage = $"正在打包「{worldName}」…";

        try
        {
            var result = await WorldStore.ExportZipAsync(_instance(), worldName, zipPath, _cts.Token);
            StatusMessage = result.Message;

            if (result.Ok) _log(LogKind.Ok, $"世界「{worldName}」已导出到 {zipPath}");

            return result.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.Message}";
            return StatusMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>导入一个存档 zip。</summary>
    public async Task ImportWorldAsync(string zipPath)
    {
        IsBusy = true;
        StatusMessage = "正在导入存档…";

        try
        {
            var result = await WorldStore.ImportZipAsync(_instance(), zipPath, _cts.Token);
            StatusMessage = result.Message;

            if (result.Ok)
            {
                _log(LogKind.Ok, $"导入存档成功 → {result.InstalledPath}");
                RefreshWorlds();
            }
            else
            {
                _log(LogKind.Warning, $"导入存档失败 —— {result.Message}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>删除一个世界。调用方负责先问清楚 —— 这里的删除是不可恢复的。</summary>
    public void DeleteWorld(string worldName)
    {
        if (WorldStore.Delete(_instance(), worldName))
        {
            _log(LogKind.Warning, $"已删除世界「{worldName}」");
            StatusMessage = $"已删除世界「{worldName}」。";
            RefreshWorlds();
        }
        else
        {
            StatusMessage = $"删不掉「{worldName}」—— 可能游戏正开着，文件被占用。";
        }
    }

    // ══════════════════════════════════════════════════════════
    //  收藏夹
    // ══════════════════════════════════════════════════════════

    public void RefreshFavorites()
    {
        var rows = new List<ContentRow>();

        try
        {
            rows.AddRange(_favorites.Load()
                .OrderByDescending(f => f.AddedAt)
                .Select(ContentRow.FromFavorite));
        }
        catch (Exception ex)
        {
            StatusMessage = $"读收藏夹失败：{ex.Message}";
        }

        OnUi(() =>
        {
            Favorites.Clear();
            foreach (var row in rows) Favorites.Add(row);
        });

        _ = LoadIconsAsync(rows);

        if (IsFavoriteTab)
            StatusMessage = Favorites.Count == 0
                ? "收藏夹是空的。在 Mod/整合包/资源包/光影包那一栏点一下「收藏」就会出现在这里。"
                : $"共 {Favorites.Count} 个收藏。";

        OnPropertyChanged(nameof(HasFavorites));
    }

    public bool HasFavorites => Favorites.Count > 0;

    /// <summary>收藏/取消收藏。返回收藏之后的状态。</summary>
    public bool ToggleFavorite(ContentRow row)
    {
        var now = _favorites.Toggle(new FavoriteItem
        {
            ProjectId = row.ProjectId,
            Slug = row.Slug,
            Name = row.Title,
            ProjectType = row.ProjectType,
            Summary = row.Summary,
            IconUrl = row.IconUrl,
            Kind = row.ProjectType,
            AddedAt = DateTimeOffset.Now.ToString("O"),
        });

        row.SetFavorite(now);

        _log(LogKind.Info, now ? $"已收藏「{row.Title}」" : $"已取消收藏「{row.Title}」");

        if (IsFavoriteTab) RefreshFavorites();

        return now;
    }

    /// <summary>把搜索栏里那些行的收藏状态重新对齐一次（收藏夹里刚删掉一个的时候用）。</summary>
    public void ResyncFavoriteFlags()
    {
        foreach (var row in Results)
            row.SetFavorite(_favorites.Has(row.ProjectId));
    }

    private bool _disposed;

    public void Dispose()
    {
        // 幂等：重复 Dispose 要无害。关闭窗口和切换实例都可能走到这里。
        if (_disposed) return;
        _disposed = true;

        // 关窗时如果正在下载，取消它 —— 但不能让"已经被释放"这件事泄漏到
        // 后续调用上：那一刻其它地方可能正拿着 _cts.Token 在等（自检就撞过
        // 这个：一次取消之后紧接着的安装拿到 ObjectDisposedException）。
        try { _cts.Cancel(); } catch { /* 已经释放了 */ }
        _cts.Dispose();
        _downloader.Dispose();
    }

    /// <summary>是否已经释放。已释放的实例不该再被拿去搜索/安装。</summary>
    public bool IsDisposed => _disposed;
}

/// <summary>
/// 列表里的一行。搜索结果和收藏项都变成它，这样界面只需要一个模板。
/// </summary>
public sealed class ContentRow : ObservableObject
{
    public required string ProjectId { get; init; }

    public required string Title { get; init; }

    public string Slug { get; init; } = "";

    public string Summary { get; init; } = "";

    public string Author { get; init; } = "";

    public string? IconUrl { get; init; }

    public string ProjectType { get; init; } = "mod";

    public string DownloadsText { get; init; } = "";

    public string LoadersText { get; init; } = "";

    /// <summary>中文主题分类徽章（如 冒险 / 科技 / 魔法 / 优化），让玩家一眼看出是什么类型的整合包。</summary>
    public IReadOnlyList<string> CategoryBadges { get; init; } = [];

    /// <summary>支持的游戏版本摘要（如 1.20.1 ~ 1.21.1）。</summary>
    public string VersionsText { get; init; } = "";

    private ImageSource? _iconImage;
    public ImageSource? IconImage => _iconImage;
    public bool HasIconImage => _iconImage is not null;
    public string FallbackGlyph => string.IsNullOrWhiteSpace(Title) ? "M" : Title.Trim()[..1].ToUpperInvariant();

    internal void SetIcon(ImageSource img)
    {
        if (ReferenceEquals(_iconImage, img)) return;
        _iconImage = img;
        OnPropertyChanged(nameof(IconImage));
        OnPropertyChanged(nameof(HasIconImage));
    }

    public string MetaLineText
    {
        get
        {
            var bits = new List<string>();
            if (VersionsText.Length > 0) bits.Add($"版本 {VersionsText}");
            if (LoadersText.Length > 0) bits.Add(LoadersText);
            return string.Join("　·　", bits);
        }
    }

    public bool HasMetaLine => MetaLineText.Length > 0;

    private bool _isFavorite;
    private bool _isInstalling;
    private bool _isInstalled;
    private string _installButtonText = "安装";

    public bool IsFavorite => _isFavorite;

    public string FavoriteButtonText => _isFavorite ? "已收藏" : "收藏";

    public bool IsInstalling => _isInstalling;
    public bool IsInstalled => _isInstalled;
    public bool CanInstall => !_isInstalling;
    public string InstallButtonText => _installButtonText;

    /// <summary>对应的 Modrinth 网页链接，方便用户查看详情。</summary>
    public string WebUrl
    {
        get
        {
            var seg = ProjectType switch
            {
                "modpack" => "modpack",
                "resourcepack" => "resourcepack",
                "shader" => "shader",
                "datapack" => "datapack",
                _ => "mod",
            };
            var id = !string.IsNullOrWhiteSpace(Slug) ? Slug : ProjectId;
            return $"https://modrinth.com/{seg}/{id}";
        }
    }

    internal void SetInstalling(bool installing, string label)
    {
        _isInstalling = installing;
        _installButtonText = label;
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallButtonText));
    }

    internal void SetInstalled(bool installed, string label)
    {
        _isInstalling = false;
        _isInstalled = installed;
        _installButtonText = label;
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallButtonText));
    }

    internal void SetFavorite(bool value)
    {
        if (_isFavorite == value) return;

        _isFavorite = value;
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteButtonText));
    }

    /// <summary>
    /// 这一行的类型标签（「Mod」「光影包」…）。收藏夹里混着各种类型，得标出来。
    ///
    /// <see cref="Badge"/> 优先：数据包在 Modrinth 上根本没有独立的 project_type
    /// （它查出来一律是 mod，靠 categories 里的 datapack 区分），所以那一栏
    /// 只能由界面告诉这一行"你是个数据包"，否则用户看到的是满屏的「Mod」。
    /// </summary>
    public string TypeText => Badge.Length > 0
        ? Badge
        : ProjectType switch
        {
            "mod" => "Mod",
            "modpack" => "整合包",
            "datapack" => "数据包",
            "resourcepack" => "资源包",
            "shader" => "光影包",
            _ => ProjectType,
        };

    /// <summary>所在栏目给的类型标签，空表示按 ProjectType 自己推。</summary>
    public string Badge { get; init; } = "";

    /// <summary>副标题：简介 + 作者/下载量。搜索结果是这些都有，收藏项可能缺。</summary>
    public string SubtitleText
    {
        get
        {
            var bits = new List<string>();

            if (Summary.Length > 0) bits.Add(Summary);
            if (Author.Length > 0) bits.Add($"作者 {Author}");
            if (DownloadsText.Length > 0) bits.Add(DownloadsText);

            return bits.Count > 0 ? string.Join("　·　", bits) : "（没有简介）";
        }
    }

    public bool HasLoaderText => LoadersText.Length > 0;

    /// <summary>
    /// Core 在"这个项目没有加载器分类"时给的是"—"这种占位符。界面这一行
    /// 只在有真内容时才画，所以占位符在这儿清成空串 —— 不然光影包、资源包
    /// 每一行的尾巴上都挂着一条横杠，像没渲染完。
    /// </summary>
    private static string CleanLoaders(string? text)
    {
        var t = (text ?? "").Trim();

        return t is "—" or "–" or "-" ? "" : t;
    }

    private static readonly Dictionary<string, string> CategoryZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adventure"] = "冒险",
        ["challenging"] = "高难",
        ["combat"] = "战斗",
        ["kitchen-sink"] = "大型综合",
        ["lightweight"] = "轻量",
        ["magic"] = "魔法",
        ["multiplayer"] = "联机",
        ["optimization"] = "性能优化",
        ["quests"] = "任务线",
        ["technology"] = "科技",
        ["decoration"] = "建筑装饰",
        ["equipment"] = "装备",
        ["food"] = "食物农业",
        ["game-mechanics"] = "玩法机制",
        ["library"] = "前置库",
        ["management"] = "管理",
        ["minigame"] = "小游戏",
        ["mobs"] = "生物",
        ["social"] = "社交",
        ["storage"] = "存储",
        ["transportation"] = "交通",
        ["utility"] = "实用工具",
        ["worldgen"] = "世界生成",
        ["atmosphere"] = "氛围",
        ["bloom"] = "泛光",
        ["cartoon"] = "卡通",
        ["colored-lighting"] = "彩色光照",
        ["cursed"] = "魔性",
        ["fantasy"] = "奇幻",
        ["foliage"] = "植被",
        ["path-tracing"] = "光追",
        ["pbr"] = "PBR",
        ["potato"] = "低配可用",
        ["realistic"] = "写实",
        ["reflections"] = "反射",
        ["semi-realistic"] = "半写实",
        ["shadows"] = "阴影",
        ["vanilla-like"] = "原版风格",
        ["high"] = "高配",
        ["medium"] = "中配",
        ["low"] = "低配",
        ["screenshot"] = "截图",
        ["simplistic"] = "简约",
        ["themed"] = "主题",
        ["tweaks"] = "微调",
    };

    private static IReadOnlyList<string> FormatCategories(IReadOnlyList<string> raw)
    {
        if (raw.Count == 0) return [];
        var list = new List<string>(4);
        foreach (var c in raw)
        {
            if (CategoryZh.TryGetValue(c, out var zh) && !list.Contains(zh))
                list.Add(zh);
            if (list.Count >= 4) break;
        }
        return list;
    }

    private static string FormatVersionRange(IReadOnlyList<string> versions)
    {
        if (versions.Count == 0) return "";
        var releases = versions
            .Where(v => Regex.IsMatch(v, @"^1\.\d+(?:\.\d+)?$"))
            .ToList();
        if (releases.Count == 0) return versions[^1];
        if (releases.Count == 1) return releases[0];
        return $"{releases[0]} ~ {releases[^1]}";
    }

    public static ContentRow FromSearch(ModSearchResult r, bool isFavorite, string badge = "") => new()
    {
        ProjectId = r.ProjectId,
        Title = r.Name,
        Slug = r.Slug,
        Summary = r.Summary,
        Author = r.Author,
        IconUrl = r.IconUrl,
        ProjectType = r.ProjectType,
        Badge = badge,
        DownloadsText = r.DownloadsText,
        LoadersText = CleanLoaders(r.LoadersText),
        CategoryBadges = FormatCategories(r.Categories),
        VersionsText = FormatVersionRange(r.GameVersions),
        _isFavorite = isFavorite,
    };

    public static ContentRow FromFavorite(FavoriteItem f) => new()
    {
        ProjectId = f.ProjectId,
        Title = f.Name,
        Slug = f.Slug,
        Summary = f.Summary,
        ProjectType = f.ProjectType.Length > 0 ? f.ProjectType : "mod",
        IconUrl = f.IconUrl,
    };
}