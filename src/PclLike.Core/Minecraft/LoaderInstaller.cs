using PclLike.Core.Minecraft.Install;
using PclLike.Core.Minecraft.Launch;
using PclLike.Core.Minecraft.Loaders;
using PclLike.Core.Storage;

namespace PclLike.Core.Minecraft;

/// <summary>
/// 加载器安装入口：把某个加载器装进 versions/，产出可直接启动的版本 JSON。
///
/// ## 装完之后发生什么
///
/// 无论是 Fabric 还是 Forge，最终产物都是
/// `versions/&lt;loaderVersionId&gt;/&lt;loaderVersionId&gt;.json`，
/// 里面带 `inheritsFrom` 指向原版。之后它就是普通版本了——
/// VersionResolver 会沿 inheritsFrom 链把父版本的 libraries 合并进来，
/// 启动链路完全不用改。
///
/// ## 依赖顺序
///
/// 加载器依赖原版的 jar 和 index，所以**必须先装原版**。
/// 这里会自动检查，缺了就装。
/// </summary>
public sealed class LoaderInstaller : IDisposable
{
    private readonly string _root;
    private readonly Downloader _downloader;
    private readonly InstanceInstaller _baseInstaller;

    public LoaderInstaller(string root, Downloader? downloader = null)
    {
        _root = root;
        _downloader = downloader ?? new Downloader();
        _baseInstaller = new InstanceInstaller(root, _downloader);
    }

    public sealed record Progress(string Stage, int Done, int Total);

    public sealed record Result(
        string VersionId,
        string JsonPath,
        LoaderKind Kind,
        string LoaderVersion,
        IReadOnlyList<string> Warnings);

    /// <summary>按类型取 provider。</summary>
    public static ILoaderProvider? ProviderFor(LoaderKind kind, LoaderConfig? config = null)
    {
        config ??= new LoaderConfig();
        return kind switch
        {
            LoaderKind.Fabric   => new FabricProvider(config.FabricMeta),
            LoaderKind.Quilt    => new QuiltProvider(config.QuiltMeta),
            LoaderKind.Forge    => new ForgeProvider(config.ForgePromotions),
            LoaderKind.NeoForge => new NeoForgeProvider(config.NeoForgeMaven),
            _ => null,
        };
    }

    /// <summary>
    /// 列出某个 MC 版本下可用的加载器版本。
    /// 网络失败会抛带说明的异常（让 UI 能显示"要不要配代理"）。
    /// </summary>
    public async Task<IReadOnlyList<LoaderVersion>> ListAsync(
        LoaderKind kind, string mcVersion, CancellationToken ct = default)
    {
        var p = ProviderFor(kind)
            ?? throw new NotSupportedException($"{kind} 不是可安装的加载器");

        return await p.ListVersionsAsync(mcVersion, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 安装加载器。
    ///
    /// <paramref name="loaderVersion"/> 为空时自动取最新稳定版。
    /// </summary>
    /// <param name="onLog">
    /// 细粒度日志回调。Forge / NeoForge 的 processor 链会输出每一步的执行情况
    /// （下载了什么工具、生成了哪些 jar、sha1 校验是否通过），
    /// 这些信息对排查安装失败很关键，所以往上透传。
    /// </param>
    public async Task<Result> InstallAsync(
        LoaderKind kind,
        string mcVersion,
        string? loaderVersion = null,
        IProgress<Progress>? progress = null,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        if (kind == LoaderKind.Vanilla)
            throw new ArgumentException("原版不需要安装加载器", nameof(kind));

        var provider = ProviderFor(kind)
            ?? throw new NotSupportedException($"{kind} 不是可安装的加载器");

        var warnings = new List<string>();

        // ---- 0. 原版必须先就位（加载器 JSON 靠 inheritsFrom 引用它）----
        progress?.Report(new Progress("检查原版", 0, 1));
        var baseResult = await _baseInstaller.InstallAsync(mcVersion, null, ct).ConfigureAwait(false);
        warnings.AddRange(baseResult.Warnings);

        // ---- 1. 确定加载器版本 ----
        progress?.Report(new Progress("查询加载器版本", 0, 1));
        LoaderVersion target;
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            target = await provider.GetLatestAsync(mcVersion, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"{mcVersion} 没有找到可用的 {kind} 版本。可能该 MC 版本不支持这个加载器。");
        }
        else
        {
            target = new LoaderVersion(kind, mcVersion, loaderVersion, true);
        }

        progress?.Report(new Progress($"安装 {target.DisplayName}", 0, 1));

        // ---- 2. 拿最终版本 JSON ----
        //
        // Forge / NeoForge 这一步**不只是解析 JSON**：installer 的 processor 链
        // 会在这里被执行，现场生成 srg / extra / patched-client 三个 jar。
        // 缺了他们启动必崩（Invalid paths argument），所以 gameRoot 与 javaExe
        // 必须传下去。
        var javaExe = ResolveJavaForProcessors();

        string finalJson;
        switch (kind)
        {
            case LoaderKind.Fabric:
                finalJson = await new FabricProvider().GetProfileJsonAsync(mcVersion, target.Version, ct)
                    .ConfigureAwait(false);
                break;

            case LoaderKind.Quilt:
                finalJson = await new QuiltProvider().GetProfileJsonAsync(mcVersion, target.Version, ct)
                    .ConfigureAwait(false);
                break;

            case LoaderKind.Forge:
                finalJson = await new ForgeProvider().BuildVersionJsonAsync(
                    mcVersion, target.Version, _root, javaExe, onLog, ct)
                    .ConfigureAwait(false);
                break;

            case LoaderKind.NeoForge:
                finalJson = await new NeoForgeProvider().BuildVersionJsonAsync(
                    mcVersion, target.Version, _root, javaExe, onLog, ct)
                    .ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException($"{kind}");
        }

        // ---- 3. 落盘 ----
        var resultId = JsonVersionId(finalJson) ?? target.ResultVersionId;
        var vdir = _baseInstaller.VersionDir(resultId);
        Directory.CreateDirectory(vdir);

        var jsonPath = Path.Combine(vdir, $"{resultId}.json");
        await File.WriteAllTextAsync(jsonPath, finalJson, ct).ConfigureAwait(false);

        // ---- 4. 把加载器新增的库下载下来 ----
        progress?.Report(new Progress("下载加载器依赖", 0, 1));
        var added = await DownloadLoaderLibrariesAsync(finalJson, ct).ConfigureAwait(false);
        if (added.Failed > 0)
            warnings.Add($"{added.Failed} 个加载器库下载失败，启动前会自动重试");

        progress?.Report(new Progress("完成", 1, 1));

        return new Result(resultId, jsonPath, kind, target.Version, warnings);
    }

    /// <summary>从版本 JSON 里读它自己的 id（installer 给的可能和我拼的不同）。</summary>
    private static string? JsonVersionId(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 找跑 processor 链要用的 Java。
    ///
    /// Forge/NeoForge 的工具（installertools / jarsplitter / FART / binarypatcher）
    /// 是普通的 Java 程序，用哪个 JDK 都行；优先用本地已探测到的，实在没有
    /// 就退回 PATH 上的 java。
    ///
    /// 注意这里**不抛异常**：找不到就返回 null，让上层跳过 processor 链并告警——
    /// 这样 Fabric 之类不需要 processor 的加载器不受影响，
    /// 也让"还没装 Java 就想先看看版本列表"的场景能走通。
    /// </summary>
    private string? ResolveJavaForProcessors()
    {
        try
        {
            var javas = JavaLocator.Discover(_root, null);
            var chosen = JavaLocator.SelectFor(javas, requiredMajor: 17);
            if (chosen is not null) return chosen.Path;
            if (javas.Count > 0) return javas[0].Path;
        }
        catch
        {
            // 探测失败不是致命错误，下面退回 PATH
        }

        // 退回 PATH 上的 java（若有）
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var cand = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? "java.exe" : "java");
                if (File.Exists(cand)) return cand;
            }
            catch
            {
                // 忽略非法 PATH 项
            }
        }

        return null;
    }

    /// <summary>
    /// 下载版本 JSON 里 libraries 声明的 jar（跳过已有）。
    ///
    /// 原版那批已经在装原版时下过了，这里实际只下加载器新增的
    /// （Forge 的 launchwrapper、Fabric 的 loader jar 之类）。
    /// </summary>
    private async Task<(int Ok, int Failed)> DownloadLoaderLibrariesAsync(string versionJson, CancellationToken ct)
    {
        var libsRoot = _baseInstaller.LibrariesDir;
        var tasks = new List<(string Url, string Dest)>();

        using var doc = System.Text.Json.JsonDocument.Parse(versionJson);
        if (!doc.RootElement.TryGetProperty("libraries", out var libs) ||
            libs.ValueKind != System.Text.Json.JsonValueKind.Array)
            return (0, 0);

        foreach (var lib in libs.EnumerateArray())
        {
            if (!lib.TryGetProperty("downloads", out var dl)) continue;
            if (!dl.TryGetProperty("artifact", out var art)) continue;

            var path = art.TryGetProperty("path", out var p) ? p.GetString() : null;
            var url = art.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url)) continue;

            var dest = Path.Combine(libsRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest)) continue;

            tasks.Add((url, dest));
        }

        var ok = 0;
        var failed = 0;
        foreach (var (url, dest) in tasks)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var r = await _downloader.DownloadOneAsync(new Downloader.DownloadTask(url, dest), ct)
                    .ConfigureAwait(false);
                if (r.Ok) ok++;
                else failed++;
            }
            catch
            {
                failed++;
            }
        }

        return (ok, failed);
    }

    /// <summary>
    /// 释放内部持有的 <see cref="Downloader"/>。
    /// 实现 <see cref="IDisposable"/>（而不是只写个同名方法）是为了让
    /// `using var inst = new LoaderInstaller(...)` 能编译——
    /// 之前漏了接口声明，调用方按最自然的写法就会撞 CS1674。
    /// </summary>
    public void Dispose() => _downloader.Dispose();
}

/// <summary>各加载器站点的地址，可配置（用户可能要用镜像 / 代理）。</summary>
public sealed class LoaderConfig
{
    public string? FabricMeta { get; set; }
    public string? QuiltMeta { get; set; }
    public string? ForgePromotions { get; set; }
    public string? NeoForgeMaven { get; set; }

    /// <summary>HTTP 代理，如 http://127.0.0.1:7897。</summary>
    public string? Proxy { get; set; }

    /// <summary>把配置应用到全局 HTTP 客户端。</summary>
    public void Apply()
    {
        if (!string.IsNullOrWhiteSpace(Proxy)) LoaderHttp.ProxyUrl = Proxy;
    }
}