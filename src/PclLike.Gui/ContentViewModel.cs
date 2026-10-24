using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
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
    private readonly ModDownloader _downloader = new();
    private readonly FavoritesStore _favorites;
    private readonly string _tempDir;
    private readonly string _launcherRoot;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>收藏夹存在启动器根目录下 —— 它是全启动器一份，不跟着实例走。</summary>
    public ContentViewModel(Func<GameInstance> instance, Action<LogKind, string> log, string launcherRoot)
    {
        _instance = instance;
        _log = log;
        _favorites = new FavoritesStore(launcherRoot);
        _launcherRoot = launcherRoot;
        _tempDir = Path.Combine(Path.GetTempPath(), "pcllike-content");

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
                "整合包会下载它自带的一整份文件清单（几十到几百个），装进当前实例的根目录。装之前建议先新建一个空实例，别覆盖正在玩的那个。",
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

    public ObservableCollection<ContentRow> Results { get; } = [];

    public ObservableCollection<ContentRow> Favorites { get; } = [];

    public bool HasResults => Results.Count > 0;

    public string ResultsCountText => Results.Count > 0 ? $"{Results.Count} 条结果" : "";

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

    /// <summary>搜索。空关键词也发请求 —— Modrinth 会返回最热的那些，等于"逛一逛"。</summary>
    public async Task SearchAsync()
    {
        if (!IsOnlineTab || IsBusy) return;

        IsBusy = true;
        StatusMessage = SearchQuery.Trim().Length > 0
            ? $"正在搜索「{SearchQuery.Trim()}」…"
            : "正在列出最热门的项目…";

        try
        {
            var mc = McVersion.Trim();
            var loader = NeedsLoader && Loader.Trim().Length > 0 ? Loader.Trim() : null;

            var list = await _downloader.SearchModrinthListAsync(
                SearchQuery.Trim(),
                mc.Length > 0 ? mc : null,
                loader,
                limit: 30,
                offset: 0,
                ct: _cts.Token,
                projectType: ProjectType,
                category: Category.Length > 0 ? Category : null);

            // 类型标签由栏目给。数据包那一栏尤其需要 —— Modrinth 把数据包的
            // project_type 一律报成 mod，不覆盖的话整屏都写着「Mod」。
            var rows = list.Select(r => ContentRow.FromSearch(r, _favorites.Has(r.ProjectId), Title)).ToList();

            OnUi(() =>
            {
                Results.Clear();
                foreach (var row in rows) Results.Add(row);
            });

            // 数据包栏的"这个实例还没有存档"必须一直挂着：它是"装了也没地方放"
            // 的解释，被下面那句"共 N 条"盖掉就等于没说过。
            var warn = NeedsWorldPick && Worlds.Count == 0
                ? "⚠ 这个实例还没有存档，数据包没地方装 —— 进去建一个世界再回来。\n"
                : "";

            StatusMessage = warn + (rows.Count == 0
                ? "一条都没搜到。换个关键词，或者把游戏版本/加载器放宽到不限试试。"
                : $"共 {Results.Count} 条。点「安装」装进当前实例。");

            _log(LogKind.Info, $"{Title} 搜索「{SearchQuery.Trim()}」→ {Results.Count} 条");
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
            OnPropertyChanged(nameof(ResultsCountText));
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

        try
        {
            StatusMessage = $"正在给「{row.Title}」找一个能用的版本…";

            var cand = await _downloader.FindModrinthByIdAsync(row.ProjectId, mc, loader, _cts.Token);

            if (cand is null)
            {
                StatusMessage = mc.Length > 0
                    ? $"「{row.Title}」没有匹配 {mc}{(loader.Length > 0 ? " / " + loader : "")} 的版本。"
                    : $"「{row.Title}」没有可下载的版本。";
                return;
            }

            var ext = Path.GetExtension(cand.FileName);
            tmp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + (ext.Length > 0 ? ext : ".jar"));
            Directory.CreateDirectory(_tempDir);

            var progress = new Progress<DownloadProgress>(p => StatusMessage = $"正在下载 {p.Name}：{p.Text}");

            StatusMessage = $"正在下载 {cand.FileName}…";
            if (!await _downloader.DownloadAsync(cand, tmp, progress, _cts.Token))
            {
                StatusMessage = $"下载 {cand.FileName} 失败 —— 网络问题的话过一会儿再试一次。";
                return;
            }

            var size = new FileInfo(tmp).Length;

            InstallResult result;

            if (kind == ContentKind.ModPack)
            {
                // 清单先读出来留着：安装函数内部读的是同一个 zip，但那个临时文件
                // 在 finally 里就被删了，装完之后再想读就得重新下载一遍。
                plan = ModPackInstaller.ReadPlan(tmp);

                result = await ModPackInstaller.InstallAsync(
                    tmp,
                    inst,
                    FetchPackFileAsync,
                    new Progress<string>(s => StatusMessage = s),
                    _cts.Token);
            }
            else
            {
                result = await ContentInstaller.InstallFileAsync(
                    inst, kind.Value, tmp, cand.FileName, worldName, _cts.Token);
            }

            StatusMessage = result.Message;

            if (result.Ok)
            {
                _log(LogKind.Ok, $"{Title}：{row.Title} {cand.Version} → {result.InstalledPath ?? inst.GameDir}");
                StatusMessage = $"{result.Message}（{size / 1024.0 / 1024.0:0.#} MB）";

                // 整合包装完不等于能玩。包是用来"配出一个能跑的游戏环境"的，
                // 光把 Mod 铺进 mods/ 只会让游戏带着一堆 Mod 起不来：它要求的
                // 那个加载器版本本机可能压根没装，实例的版本也还指着原来那个。
                // 这一步把"装得下来"补成"装完能开局"。
                if (kind == ContentKind.ModPack)
                    await InstallPackEnvironmentAsync(inst, plan);

                // 装完把收藏也顺手点一下？不 —— 收藏是用户的意愿，不替他决定。
                if (kind == ContentKind.World || kind == ContentKind.Datapack)
                    RefreshWorlds();
            }
            else
            {
                _log(LogKind.Warning, $"{Title}：{row.Title} 安装失败 —— {result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "安装已取消。";
        }
        catch (Exception ex)
        {
            // 界面上只给"人话"。异常类型写进日志 —— 排查时要能分清网络超时、
            // 磁盘满，还是代码自己炸了，但用户看的是那句提示，
            // 「（IOException）」这种后缀对他没有意义，只会让提示更像乱码。
            StatusMessage = $"安装失败：{ex.Message}";
            _log(LogKind.Error, $"{Title}：{row.Title} 安装异常 {ex.GetType().Name} —— {ex.Message}");
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

                // 把实例指到这个版本上 —— 不然"当前实例"还是原来那个，
                // 玩家点启动游戏跑的仍然是没有 Mod 的原版。
                inst.VersionId = r.VersionId;

                // 存盘 —— 只改内存里的对象没用，下次开启动器读的还是文件。
                try { new PclLike.Core.Storage.InstanceStore(_launcherRoot).Save(inst); }
                catch (Exception se)
                {
                    _log(LogKind.Warning, $"{Title}：实例存盘失败 —— {se.Message}");
                }

                foreach (var w in r.Warnings.Take(5))
                    _log(LogKind.Warning, $"{Title}：加载器警告 —— {w}");

                _log(LogKind.Ok, $"{Title}：实例已指向 {r.VersionId}");

                StatusMessage = $"{StatusMessage}完成：{r.VersionId}。别忘了确认这个实例的版本选的是它。";
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

        return await _downloader.DownloadAsync(cand, destPath, null, ct);
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
        Console.WriteLine("[diag] ContentViewModel.Dispose 被调用，调用栈：");
        Console.WriteLine(Environment.StackTrace);

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

    private bool _isFavorite;

    public bool IsFavorite => _isFavorite;

    public string FavoriteButtonText => _isFavorite ? "已收藏" : "收藏";

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