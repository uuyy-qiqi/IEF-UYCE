using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using PclLike.Core.Mods;
using PclLike.Core.Storage;

namespace PclLike.Gui;

/// <summary>Mod 列表里的一行。</summary>
public sealed class ModRow : ObservableObject
{
    private bool _enabled;

    public required ModFile File { get; init; }

    /// <summary>显示名：优先元数据里的 name，退回文件名。</summary>
    public string Title
    {
        get
        {
            var info = File.Info;
            if (info is not null && info.Name.Length > 0) return info.Name;

            // 读不出元数据时用文件名（去掉 .jar / .disabled）
            return File.FileName.Length > 0 ? File.FileName : Path.GetFileName(File.Path);
        }
    }

    /// <summary>第二行：id · 版本 · 加载器 · 文件名。</summary>
    public string Subtitle
    {
        get
        {
            var info = File.Info;
            if (info is null)
                return File.ParseError ?? "无元数据";

            var parts = new List<string>();
            if (info.Id.Length > 0) parts.Add(info.Id);
            if (info.Version.Length > 0) parts.Add("v" + info.Version);
            if (info.Loaders.Count > 0) parts.Add(string.Join("/", info.Loaders));
            if (info.McVersions.Count > 0) parts.Add(info.McVersions.Count <= 3
                ? string.Join(",", info.McVersions)
                : $"{info.McVersions.Count} 个 MC 版本");

            parts.Add(File.FileName);
            return string.Join("  ·  ", parts);
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value))
            {
                OnPropertyChanged(nameof(Badge));
                OnPropertyChanged(nameof(BadgeBrush));
                OnPropertyChanged(nameof(TitleBrush));
            }
        }
    }

    public string Badge => File.Info is null ? "未知"
        : File.Info.Loaders.Count > 0 ? string.Join("/", File.Info.Loaders).ToUpperInvariant()
        : "MOD";

    /// <summary>禁用态整体变暗，一眼能看出哪些没生效。</summary>
    public Brush TitleBrush => Enabled
        ? (Brush)System.Windows.Application.Current.FindResource("FgBrush")
        : (Brush)System.Windows.Application.Current.FindResource("MutedBrush");

    public Brush BadgeBrush => Enabled
        ? new SolidColorBrush(Color.FromArgb(0x33, 0xA0, 0x6C, 0xD5))
        : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    public string SizeText => File.SizeBytes switch
    {
        >= 1048576 => $"{File.SizeBytes / 1048576.0:F1} MB",
        >= 1024 => $"{File.SizeBytes / 1024.0:F0} KB",
        > 0 => $"{File.SizeBytes} B",
        _ => "",
    };

    /// <summary>筛选用的小写搜索串（名称 + id + 文件名）。</summary>
    public string SearchKey =>
        (Title + " " + (File.Info?.Id ?? "") + " " + File.FileName).ToLowerInvariant();
}

/// <summary>依赖问题面板里的一行。</summary>
public sealed class IssueRow
{
    public required ModIssue Issue { get; init; }

    public string LevelLabel => Issue.Level switch
    {
        ModIssueLevel.Error => "缺失",
        ModIssueLevel.Warning => "警告",
        _ => "提示",
    };

    public Brush LevelBrush => Issue.Level switch
    {
        ModIssueLevel.Error => new SolidColorBrush(Color.FromRgb(0xE5, 0x6B, 0x6B)),
        ModIssueLevel.Warning => new SolidColorBrush(Color.FromRgb(0xE5, 0xB0, 0x56)),
        _ => new SolidColorBrush(Color.FromRgb(0x6A, 0xA0, 0xE5)),
    };

    public string SourceName => Issue.SourceName;

    public string Message => Issue.Message;
}

/// <summary>
/// 搜索结果列表里的一行。
///
/// 是可变对象而不是 record —— 因为安装过程中要原地更新 <see cref="Installing"/>
/// 和 <see cref="InstallLabel"/> 让按钮变灰、文案变化，用 record 每次都得重建，
/// 列表会闪。
/// </summary>
public sealed class ModSearchRow : ObservableObject
{
    private bool _installing;
    private bool _installed;
    private string _installLabel = "安装";

    public ModSearchRow(ModSearchResult result, string mcVersion, string loader)
    {
        Result = result;
        McVersion = mcVersion;
        Loader = loader;
    }

    public ModSearchResult Result { get; }

    /// <summary>搜索时用的 MC 版本 / 加载器，安装时用来判断能不能装。</summary>
    public string McVersion { get; }
    public string Loader { get; }

    public string Name => Result.Name;
    public string Slug => Result.Slug;
    public string ProjectId => Result.ProjectId;
    public string Summary => Result.Summary;
    public string? IconUrl => Result.IconUrl;
    public string Author => Result.Author.Length > 0 ? "by " + Result.Author : "";

    /// <summary>
    /// 副标题：作者 · 加载器 · 下载数。
    ///
    /// MC 版本列表故意**不放**进来 —— 那是一个项目的**全部**历史版本
    /// （Sodium 从 1.16 一路列到 1.21），和"能不能装在当前实例上"是两回事。
    /// 放进来只会让人误判。
    /// </summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Author.Length > 0) parts.Add(Author);
            parts.Add(Result.LoadersText);
            parts.Add(Result.DownloadsText + " 次下载");
            return string.Join("  ·  ", parts);
        }
    }

    public bool Installing
    {
        get => _installing;
        set
        {
            if (Set(ref _installing, value))
            {
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(InstallLabel));
            }
        }
    }

    public bool Installed
    {
        get => _installed;
        set => Set(ref _installed, value);
    }

    public string InstallLabel
    {
        get => _installLabel;
        set => Set(ref _installLabel, value);
    }

    public bool CanInstall => !Installing;

    /// <summary>装了之后按钮变绿，给个明确的成功反馈。</summary>
    public Brush InstallBrush => Installed
        ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
        : new SolidColorBrush(Color.FromRgb(0x8A, 0x7A, 0xC8));

    /// <summary>图标下载失败的兜底色块。</summary>
    public Brush FallbackBrush => new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x40));
}

/// <summary>
/// Mod 管理窗口的 ViewModel。
///
/// 所有耗时操作（扫描、下载）都跑在后台线程，通过 <see cref="StatusText"/> 给反馈 ——
/// 扫描一个装满整合包的 mods 目录要解析上百个 zip，放 UI 线程会直接卡死窗口。
/// </summary>
public sealed class ModsViewModel : ObservableObject
{
    private readonly ModManager _manager;
    private readonly ModDependencyResolver _resolver;
    private readonly ModDownloader _downloader;
    private readonly GameInstance _instance;

    private string _filter = "";
    private string _statusText = "";
    private double _progressValue;
    private double _progressMax = 100;
    private bool _isProgressIndeterminate;

    private string _searchQuery = "";
    private string _searchMcVersion = "";
    private string _searchLoader = "（不限）";
    private bool _isSearching;

    public ModsViewModel(GameInstance instance)
    {
        _instance = instance;
        _manager = new ModManager(instance);
        _downloader = new ModDownloader();
        _resolver = new ModDependencyResolver(_manager, _downloader);

        AllMods = [];
        FilteredMods = [];
        Issues = [];
        SearchResults = [];

        SyncSearchFiltersFromInstance();
    }

    public string InstanceName => _instance.Name;

    public string ContextLabel
    {
        get
        {
            var loader = string.IsNullOrWhiteSpace(_instance.Loader) ? "原版" : _instance.Loader;
            return $"{_instance.VersionId}  ·  {loader}  ·  {_manager.ModsDirectory}";
        }
    }

    public List<ModRow> AllMods { get; }

    public ObservableCollection<ModRow> FilteredMods { get; }

    public ObservableCollection<IssueRow> Issues { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) ApplyFilter();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => Set(ref _progressValue, value);
    }

    public double ProgressMax
    {
        get => _progressMax;
        set => Set(ref _progressMax, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        set => Set(ref _isProgressIndeterminate, value);
    }

    /// <summary>当前选中的 Mod 数（决定按钮文案）。</summary>
    public int SelectedCount { get; set; }

    private void ApplyFilter()
    {
        var q = _filter.Trim().ToLowerInvariant();

        FilteredMods.Clear();
        foreach (var m in AllMods)
        {
            if (q.Length == 0 || m.SearchKey.Contains(q, StringComparison.Ordinal))
                FilteredMods.Add(m);
        }
    }

    /// <summary>重新扫描 mods 目录。</summary>
    public async Task RefreshAsync()
    {
        IsProgressIndeterminate = true;
        StatusText = "正在扫描 Mod…";

        try
        {
            var scan = await Task.Run(() => _manager.Scan());

            AllMods.Clear();

            if (!scan.DirectoryExists)
            {
                StatusText = "mods 目录还不存在 —— 装一个 Mod 就会自动创建。";
            }
            else
            {
                foreach (var f in scan.Mods)
                    AllMods.Add(new ModRow { File = f, Enabled = f.Enabled });

                var enabledCount = scan.Mods.Count(m => m.Enabled);
                var broken = scan.Mods.Count(m => m.ParseError is not null);

                var text = $"共 {scan.Mods.Count} 个 Mod，启用 {enabledCount} 个，禁用 {scan.Mods.Count - enabledCount} 个";
                if (broken > 0) text += $"；其中 {broken} 个读不出元数据";
                StatusText = text;
            }

            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>切换单个 Mod 的启用状态。</summary>
    public async Task ToggleAsync(ModRow row)
    {
        var wasEnabled = row.File.Enabled;

        var ok = await Task.Run(() => row.File.Enabled
            ? _manager.Disable(row.File)
            : _manager.Enable(row.File));

        if (!ok)
        {
            StatusText = $"无法修改 {row.File.FileName} —— 可能被游戏占用，或目录里有同名文件。";
            return;
        }

        row.Enabled = !wasEnabled;
        row.File.Enabled = !wasEnabled;

        StatusText = $"{row.Title} 已{(row.Enabled ? "启用" : "禁用")}。";
    }

    /// <summary>批量启用/禁用。</summary>
    public async Task ToggleAllAsync(IReadOnlyList<ModRow> rows, bool enable)
    {
        if (rows.Count == 0) return;

        IsProgressIndeterminate = true;
        StatusText = enable ? "正在全部启用…" : "正在全部禁用…";

        var changed = 0;

        await Task.Run(() =>
        {
            foreach (var row in rows)
            {
                // 已经是目标状态就跳过 —— 避免无意义的重命名
                if (row.File.Enabled == enable) continue;

                var ok = enable ? _manager.Enable(row.File) : _manager.Disable(row.File);
                if (ok) changed++;
            }
        });

        // 状态回写到 UI 模型
        foreach (var row in rows)
        {
            row.Enabled = enable;
            row.File.Enabled = enable;
        }

        IsProgressIndeterminate = false;
        StatusText = changed > 0
            ? $"已{(enable ? "启用" : "禁用")} {changed} 个 Mod。"
            : "没有需要修改的 Mod。";
    }

    /// <summary>删除选中的 Mod。</summary>
    public async Task<int> DeleteAsync(IReadOnlyList<ModRow> rows)
    {
        if (rows.Count == 0) return 0;

        IsProgressIndeterminate = true;
        StatusText = $"正在删除 {rows.Count} 个 Mod…";

        var deleted = 0;

        await Task.Run(() =>
        {
            foreach (var row in rows)
                if (_manager.Delete(row.File)) deleted++;
        });

        foreach (var row in rows)
        {
            AllMods.Remove(row);
            FilteredMods.Remove(row);
        }

        IsProgressIndeterminate = false;
        StatusText = $"已删除 {deleted} 个 Mod。";
        return deleted;
    }

    /// <summary>只检查依赖，不下载。</summary>
    public async Task CheckAsync()
    {
        IsProgressIndeterminate = true;
        StatusText = "正在检查依赖…";

        try
        {
            var issues = await Task.Run(() =>
                _resolver.CheckOnly(_instance.VersionId, _instance.Loader));

            Issues.Clear();
            foreach (var i in issues)
                Issues.Add(new IssueRow { Issue = i });

            var errors = issues.Count(i => i.Level == ModIssueLevel.Error);
            var warns = issues.Count(i => i.Level == ModIssueLevel.Warning);

            StatusText = issues.Count == 0
                ? "没有发现问题，所有依赖都满足。"
                : $"发现 {errors} 个会导致启动失败的问题，{warns} 个警告。";
        }
        catch (Exception ex)
        {
            StatusText = $"检查失败：{ex.Message}";
        }
        finally
        {
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>自动补齐缺失依赖（会联网下载）。</summary>
    public async Task AutoFixAsync()
    {
        IsProgressIndeterminate = true;
        StatusText = "正在补齐依赖…";

        var progress = new Progress<string>(s => StatusText = s);

        try
        {
            var report = await Task.Run(() =>
                _resolver.ResolveAsync(_instance.VersionId, _instance.Loader, progress));

            StatusText = report.Summary;

            // 补完了重新扫描，界面上立刻能看到新装的 Mod
            await RefreshAsync();
            await CheckAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"补齐失败：{ex.Message}";
        }
        finally
        {
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>安装本地 jar 文件（或文件夹里的所有 jar）。</summary>
    public async Task InstallLocalAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var targets = new List<string>();

            if (Directory.Exists(path))
            {
                targets.AddRange(Directory.EnumerateFiles(path, "*.jar", SearchOption.TopDirectoryOnly));
                if (targets.Count == 0)
                {
                    StatusText = "这个文件夹里没有 .jar 文件。";
                    return;
                }
            }
            else if (File.Exists(path))
            {
                targets.Add(path);
            }
            else
            {
                StatusText = $"路径不存在：{path}";
                return;
            }

            IsProgressIndeterminate = true;
            StatusText = $"正在安装 {targets.Count} 个文件…";

            var installed = 0;
            var skipped = new List<string>();

            await Task.Run(() =>
            {
                foreach (var t in targets)
                {
                    try
                    {
                        _manager.InstallFromFile(t);
                        installed++;
                    }
                    catch (Exception ex)
                    {
                        // 已装过 / 重名 / 权限问题都归到这里，逐条记下来告诉用户
                        skipped.Add($"{Path.GetFileName(t)}（{ex.Message}）");
                    }
                }
            });

            IsProgressIndeterminate = false;

            var text = $"已安装 {installed} 个 Mod。";
            if (skipped.Count > 0)
                text += $" 跳过 {skipped.Count} 个：{string.Join("；", skipped.Take(3))}" +
                        (skipped.Count > 3 ? " …" : "");

            StatusText = text;

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            IsProgressIndeterminate = false;
            StatusText = $"安装失败：{ex.Message}";
        }
    }

    // ════════════════════════════════════════════════════════════
    // 在线搜索与下载
    //
    // 分两步：先搜（项目级信息，一个项目一条），再选中某个项目去取
    // 它匹配当前实例的那个版本。这样搜索结果不会因为实例换了 MC 版本
    // 就失效 —— 搜索时不锁定具体文件，安装时才锁定。
    // ════════════════════════════════════════════════════════════

    /// <summary>搜索关键词。</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set => Set(ref _searchQuery, value);
    }

    /// <summary>搜索结果。</summary>
    public ObservableCollection<ModSearchRow> SearchResults { get; }

    /// <summary>是否正在搜索（控制按钮禁用 / 文案）。</summary>
    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            if (Set(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(SearchButtonText));
                OnPropertyChanged(nameof(CanSearch));
            }
        }
    }

    public bool CanSearch => !IsSearching;

    public string SearchButtonText => IsSearching ? "搜索中…" : "搜索";

    /// <summary>搜索用的 MC 版本 —— 默认跟随实例，用户可改。</summary>
    public string SearchMcVersion
    {
        get => _searchMcVersion;
        set => Set(ref _searchMcVersion, value);
    }

    /// <summary>搜索用的加载器 —— 默认跟随实例。</summary>
    public string SearchLoader
    {
        get => _searchLoader;
        set => Set(ref _searchLoader, value);
    }

    public IReadOnlyList<string> SearchLoaderOptions { get; } =
        ["（不限）", "fabric", "forge", "neoforge", "quilt"];

    /// <summary>
    /// 执行搜索。
    ///
    /// 空关键词是**允许**的 —— 配合版本和加载器过滤，等于"浏览这个版本下
    /// 最热门的 Mod"，这是很常用的用法（PCL2 也这么做）。
    /// </summary>
    public async Task SearchAsync()
    {
        if (IsSearching) return;

        // 用搜索框自己的版本 / 加载器，没填就跟随实例
        var mc = SearchMcVersion.Trim();
        var loader = SearchLoader ?? "";

        // 加载器下拉里的「（不限）」不是真值，要清掉
        if (loader.StartsWith('（') || loader.Length == 0) loader = "";

        IsSearching = true;
        StatusText = string.IsNullOrWhiteSpace(SearchQuery)
            ? "正在拉取热门 Mod…"
            : $"正在搜索「{SearchQuery}」…";

        try
        {
            var results = await _downloader.SearchModrinthListAsync(
                SearchQuery.Trim(), mc.Length > 0 ? mc : null,
                loader.Length > 0 ? loader : null, limit: 30);

            SearchResults.Clear();
            foreach (var r in results)
                SearchResults.Add(new ModSearchRow(r, mc, loader));

            StatusText = results.Count == 0
                ? $"没找到匹配的 Mod。可以试试放宽 MC 版本或加载器过滤" +
                  (mc.Length > 0 || loader.Length > 0 ? $"（当前：{mc} / {(loader.Length > 0 ? loader : "不限")}）" : "") + "。"
                : $"找到 {results.Count} 个 Mod。双击某一项安装到当前实例。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "搜索已取消。";
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败：{ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// 搜索页的「MC 版本」跟随实例变化 —— 用户改了下拉就同步到搜索框。
    /// </summary>
    public void SyncSearchFiltersFromInstance()
    {
        SearchMcVersion = _instance.VersionId ?? "";
        SearchLoader = string.IsNullOrWhiteSpace(_instance.Loader)
            ? "（不限）"
            : _instance.Loader;
    }

    /// <summary>把一个搜索结果安装到当前实例。</summary>
    public async Task InstallFromSearchAsync(ModSearchRow row)
    {
        if (row is null || row.Installing) return;

        row.Installing = true;
        row.InstallLabel = "安装中…";

        var tempPath = Path.Combine(Path.GetTempPath(),
            "pcllike-dl", $"{Guid.NewGuid():N}.jar");

        try
        {
            // 1) 按实例的 MC 版本 + 加载器取具体版本
            var mc = _instance.VersionId ?? "";
            var loader = _instance.Loader ?? "";

            StatusText = $"正在解析 {row.Name} 的可用版本…";

            var candidate = await _downloader.FindModrinthByIdAsync(row.ProjectId, mc, loader);

            if (candidate is null)
            {
                // 这是最常见的情况：这个 Mod 不支持当前实例的版本/加载器。
                // 必须说清楚是"版本不匹配"而不是"下载失败"，否则用户会一直重试。
                StatusText = $"{row.Name} 没有支持 {mc} / {loader} 的版本。" +
                             "换个支持这个版本的 Mod，或在实例设置里改版本。";
                row.InstallLabel = "版本不匹配";
                return;
            }

            // 2) 下载到临时文件
            StatusText = $"正在下载 {candidate.FileName}…";

            // 记下载开始时间：Modrinth 的 CDN 边缘有时会限速到 100~200 KB/s，
            // 一个 7MB 的 Mod 要跑 40 秒以上，有些节点甚至先挂 40~60 秒才开始吐字节。
            // 这不是失败，只是慢 —— 界面上必须能看到"还在动"，否则用户会以为卡死
            // 然后强杀窗口，反而真的失败。
            var dlStart = DateTime.UtcNow;

            var progress = new Progress<DownloadProgress>(p =>
            {
                ProgressValue = p.Fraction * 100;

                // 拿到总长度前 Fraction 可能是 0，此时只报已下载量与耗时
                var elapsed = DateTime.UtcNow - dlStart;
                var speed = elapsed.TotalSeconds > 0.5
                    ? p.Downloaded / elapsed.TotalSeconds / 1024
                    : 0;

                StatusText = speed > 1
                    ? $"正在下载 {p.Text}（{speed:F0} KB/s，已用 {elapsed.TotalSeconds:F0}s）"
                    : $"正在下载 {p.Text}（已用 {elapsed.TotalSeconds:F0}s）";
            });

            var ok = await _downloader.DownloadAsync(candidate, tempPath, progress);

            // 慢节点重连通常就快了（实测同一 URL 第一次 65s、第二次 1.3s），
            // 所以失败后无条件重试一次；仍失败才报错。
            if (!ok)
            {
                StatusText = $"下载中断，正在重试 {candidate.FileName}…";
                TryDelete(tempPath);
                ok = await _downloader.DownloadAsync(candidate, tempPath, progress);
            }

            if (!ok)
            {
                var waited = (DateTime.UtcNow - dlStart).TotalSeconds;
                StatusText = $"下载失败：{candidate.FileName}（已尝试 {waited:F0}s）。" +
                             "Modrinth 的 CDN 有时会很慢，稍后重试或改在网络较好的时段安装。";
                row.InstallLabel = "下载失败";
                return;
            }

            // 3) 从临时文件装进 mods 目录 —— 复用本地安装那套去重逻辑
            StatusText = $"正在安装 {candidate.FileName}…";

            try
            {
                // 传真实文件名：tempPath 是 Guid 命名的临时文件，不指定的话
                // mods 目录里就会留下 `3f2a9c8b….jar` 这种用户看不懂的名字。
                _manager.InstallFromFile(tempPath, preferredFileName: candidate.FileName);
            }
            catch (Exception ex)
            {
                // 异常消息里带的多半是临时路径或内部细节，对用户没意义，
                // 统一换成真实的 Mod 文件名再说
                StatusText = $"{candidate.FileName}：{ex.Message}";
                row.InstallLabel = "未安装";
                return;
            }

            // 4) 依赖补齐 —— 装完再补，避免用户装了个跑不起来的东西
            StatusText = $"{candidate.FileName} 已安装，正在检查依赖…";

            var extra = await AutoInstallRequiredDepsAsync(candidate);

            row.Installed = true;
            row.InstallLabel = extra > 0 ? $"已装（+{extra} 依赖）" : "已安装";

            StatusText = extra > 0
                ? $"已安装 {candidate.FileName}，并自动补齐 {extra} 个必需前置。"
                : $"已安装 {candidate.FileName}。";

            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "安装已取消。";
            row.InstallLabel = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"安装 {row.Name} 失败：{ex.Message}";
            row.InstallLabel = "失败";
        }
        finally
        {
            row.Installing = false;
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// 递归补齐一个候选的必需依赖，返回装了几个。
    ///
    /// 用队列而不是递归 —— 依赖图可能有环（两个 Mod 互相声明依赖），
    /// 递归会栈溢出。visited 同时也防重复下载。
    /// </summary>
    private async Task<int> AutoInstallRequiredDepsAsync(ModDownloadCandidate root, int maxDepth = 8)
    {
        var installed = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.ProjectId };
        var queue = new Queue<(string Id, int Depth)>();

        foreach (var d in root.DependencyIds)
            if (visited.Add(d)) queue.Enqueue((d, 1));

        while (queue.Count > 0 && installed < 40)
        {
            var (id, depth) = queue.Dequeue();
            if (depth > maxDepth) continue;

            // 已经装过（无论启用与否）就跳过 —— 不重复下载
            if (IsAlreadyInstalled(id)) continue;

            try
            {
                var cand = await _downloader.FindModrinthByIdAsync(
                    id, _instance.VersionId ?? "", _instance.Loader ?? "");

                if (cand is null) continue;   // 作者删包 / 版本不匹配，跳过而不是中断

                var tmp = Path.Combine(Path.GetTempPath(),
                    "pcllike-dl", $"{Guid.NewGuid():N}.jar");

                try
                {
                    // 依赖下载与主下载同源，同样会被慢节点拖住，所以也重试一次
                    var depOk = await _downloader.DownloadAsync(cand, tmp);
                    if (!depOk)
                    {
                        TryDelete(tmp);
                        StatusText = $"依赖 {cand.FileName} 下载中断，正在重试…";
                        depOk = await _downloader.DownloadAsync(cand, tmp);
                    }
                    if (!depOk) continue;

                    try { _manager.InstallFromFile(tmp, preferredFileName: cand.FileName); }
                    catch { continue; }       // 已装过 / 重名，跳过

                    installed++;

                    foreach (var d in cand.DependencyIds)
                        if (visited.Add(d)) queue.Enqueue((d, depth + 1));
                }
                finally { TryDelete(tmp); }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 单个依赖失败不影响整体 */ }
        }

        return installed;
    }

    /// <summary>
    /// 某个 Modrinth 项目 id 是不是已经装在实例里了。
    ///
    /// 比对的是解析出来的 fabric.mod.json / mods.toml 里的 id，
    /// 不是文件名 —— 用户可能把文件改过名。
    /// </summary>
    private bool IsAlreadyInstalled(string projectIdOrSlug)
    {
        if (string.IsNullOrWhiteSpace(projectIdOrSlug)) return false;

        foreach (var m in _manager.Scan().Mods)
        {
            var info = m.Info;
            if (info is null) continue;

            // Modrinth 的 slug 和 Mod 自己声明的 id 通常一致（sodium、fabric-api），
            // 但也存在不一致的情况，所以两个都比一遍
            if (string.Equals(info.Id, projectIdOrSlug, StringComparison.OrdinalIgnoreCase))
                return true;

            if (m.FileName.Contains(projectIdOrSlug, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void OpenFolder() => _manager.OpenInExplorer();
}