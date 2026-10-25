using System.Diagnostics;
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
        IReadOnlyList<string> Warnings,
        // OptiFine 专用：真正要放进实例 mods 目录的那个 jar。
        // 别的加载器都是 null —— 它们靠版本 JSON 就能启动，OptiFine 得靠这个文件。
        string? ModJarPath = null);

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

            // 这两个走自己的接口（Legacy Fabric 跟 Fabric 同构，OptiFine 走 bmclapi 镜像）
            LoaderKind.LegacyFabric => new LegacyFabricProvider(config.LegacyFabricMeta),
            LoaderKind.OptiFine     => new OptiFineProvider(config.OptiFineBase),

            // LiteLoader 的元数据是它自己那份 versions.json，不是 maven 元数据
            LoaderKind.LiteLoader   => new LiteLoaderProvider(config.LiteLoaderMeta),

            // Cleanroom 是 1.12.2 的 Forge 分支，元数据在它自己的 maven 上
            LoaderKind.Cleanroom    => new CleanroomProvider(config.CleanroomMaven),

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
            // 指定了版本号：回列表里把完整那一条捞出来。
            //
            // 不能只 new 一个只有版本号的对象 —— 那会把 InstallerUrl 丢掉，
            // 而 OptiFine 的 jar 地址正是藏在这个字段里的（界面点「安装这个」
            // 传的就是一个版本号字符串，所以这里必须自己补全）。
            var all = await provider.ListVersionsAsync(mcVersion, ct).ConfigureAwait(false);

            target = all.FirstOrDefault(v => v.Version.Equals(loaderVersion, StringComparison.OrdinalIgnoreCase))
                ?? new LoaderVersion(kind, mcVersion, loaderVersion, true);
        }

        progress?.Report(new Progress($"安装 {target.DisplayName}", 0, 1));

        // ---- OptiFine 的路跟别人都不一样，单独走 ----
        // 它不是"挂到原版上的加载器"，而是把原版 jar 本身改掉。见方法注释。
        if (kind == LoaderKind.OptiFine)
            return await InstallOptiFineAsync(mcVersion, target, progress, onLog, ct).ConfigureAwait(false);

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

            case LoaderKind.LegacyFabric:
                finalJson = await new LegacyFabricProvider().GetProfileJsonAsync(mcVersion, target.Version, ct)
                    .ConfigureAwait(false);
                break;

            case LoaderKind.Forge:
                finalJson = await new ForgeProvider().BuildVersionJsonAsync(
                    mcVersion, target.Version, _root, javaExe, onLog, ct)
                    .ConfigureAwait(false);
                break;

            // LiteLoader 没有"profile JSON"这种服务，版本 JSON 是我们自己按它的
            // 元数据拼的（launchwrapper + --tweakClass），所以它单独一条路。
            case LoaderKind.LiteLoader:
                return await InstallLiteLoaderAsync(mcVersion, target, progress, onLog, ct)
                    .ConfigureAwait(false);

            case LoaderKind.NeoForge:
                finalJson = await new NeoForgeProvider().BuildVersionJsonAsync(
                    mcVersion, target.Version, _root, javaExe, onLog, ct)
                    .ConfigureAwait(false);
                break;

            // Cleanroom 与 Forge 同构（installer 里带 install_profile，要跑 processor
            // 链生成 srg 客户端 jar），差别只有 maven 目录名与产物 id，都在 provider 里。
            case LoaderKind.Cleanroom:
                finalJson = await new CleanroomProvider().BuildVersionJsonAsync(
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
    /// 装 OptiFine。
    ///
    /// OptiFine 跟别的加载器不一样：它不是"挂在原版上的一个 loader"，而是
    /// **把原版 jar 改掉**。官方给的安装器是个 Swing 程序（Main-Class =
    /// optifine.InstallerFrame），只能靠手点，而且会去改 launcher_profiles.json；
    /// 但它自带一个能直接调用的补丁器：
    ///
    ///     java -cp OptiFine.jar optifine.Patcher &lt;原版.jar&gt; &lt;OptiFine.jar&gt; &lt;输出.jar&gt;
    ///
    /// （这行用法是 `java -cp OptiFine.jar optifine.Patcher` 不带参数跑出来的。）
    ///
    /// jar 里有 4800 多个 .class.xdelta，Patcher 把它们打到原版类上，产出的 jar
    /// 就是"原版 + OptiFine 钩子"；OptiFine 自己的类则以 libraries 里一个库的形式
    /// 挂进 classpath。两步都做完才算装好 —— 少了后者，打过补丁的类会去引用
    /// 不存在的 optifine.* 然后崩在启动阶段。
    /// </summary>
    private async Task<Result> InstallOptiFineAsync(
        string mcVersion,
        LoaderVersion target,
        IProgress<Progress>? progress,
        Action<string>? onLog,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.InstallerUrl))
            throw new InvalidOperationException("这个 OptiFine 版本没有下载地址。");

        var warnings = new List<string>();

        // 1) OptiFine 本体
        var cacheDir = Path.Combine(_root, "cache", "optifine");
        Directory.CreateDirectory(cacheDir);
        var ofJar = Path.Combine(cacheDir, $"OptiFine_{mcVersion}_{target.Version}.jar");

        if (!File.Exists(ofJar) || new FileInfo(ofJar).Length < 1024 * 1024)
        {
            progress?.Report(new Progress("下载 OptiFine", 0, 1));
            var dl = await _downloader
                .DownloadOneAsync(new Downloader.DownloadTask(target.InstallerUrl, ofJar), ct)
                .ConfigureAwait(false);

            if (!dl.Ok)
                throw new InvalidOperationException(
                    $"OptiFine 下载失败：{target.InstallerUrl}（网络问题的话，到「设置 → 杂项」确认代理配置）");
        }

        // 2) 它得挂在 Forge 上跑。这一步是踩出来的：
        //
        // 我原先拿 optifine.Patcher 把原版 jar 打成一个"独立版本"，结果产出的是
        // 只有 srg/ 和 optifine/ 的覆盖包（4041 个条目、连一个 net/minecraft 类都
        // 没有），根本不是完整客户端 —— 那正是它作为 Forge Mod 的形态。1.13 以后
        // 官方也只以 Mod 形式分发（自带那个图形安装器要显示器，命令行跑不了）。
        //
        // 所以改成：先把 Forge 装好，再把这个 jar 交回上层，由它放进实例的 mods。
        progress?.Report(new Progress("确保 Forge 就位", 0, 1));
        onLog?.Invoke($"[启动器] OptiFine 是挂在 Forge 上的 Mod，先确认 {mcVersion} 的 Forge 已就位……");

        var forge = await InstallAsync(LoaderKind.Forge, mcVersion, null, progress, onLog, ct)
            .ConfigureAwait(false);

        warnings.Add($"OptiFine 是 Forge Mod：启动时请选 {forge.VersionId}，"
                   + "并把 OptiFine 的 jar 放在实例的 mods 目录里。");

        onLog?.Invoke($"[启动器] OptiFine Mod 已就绪：{ofJar}");
        progress?.Report(new Progress("完成", 1, 1));

        return new Result(forge.VersionId, forge.JsonPath, LoaderKind.OptiFine, target.Version, warnings, ofJar);
    }

    /// <summary>
    /// LiteLoader：自己拼版本 JSON。
    ///
    /// 它没有"给我一份 profile JSON"的服务，元数据里只有 version / file /
    /// tweakClass / libraries。所以照它官方安装器的做法，按老格式
    /// （minecraftArguments）拼一份：主类 net.minecraft.launchwrapper.Launch，
    /// 再用 --tweakClass 把 LiteLoaderTweaker 挂上去。
    ///
    /// 只支持 1.12.2 及更早 —— 1.13 之后这项目就停更了，元数据里没有更新的版本。
    /// </summary>
    private async Task<Result> InstallLiteLoaderAsync(
        string mcVersion, LoaderVersion target,
        IProgress<Progress>? progress, Action<string>? onLog, CancellationToken ct)
    {
        var provider = new LiteLoaderProvider();
        var art = await provider.FindAsync(mcVersion, target.Version, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"LiteLoader 的元数据里找不到 {mcVersion} 的 {target.Version} —— "
                + "它只支持 1.12.2 及更早的版本。");

        var id = target.ResultVersionId;                     // 1.12.2-LiteLoader1.12.2-SNAPSHOT
        var vdir = _baseInstaller.VersionDir(id);
        Directory.CreateDirectory(vdir);

        // 1) 本体 jar 放进 libraries，按 maven 布局摆好 —— 启动时它得在 classpath 上
        var rel = ProcessorChain.MavenRelPath($"com.mumfrey:liteloader:{art.Version}");
        var jarPath = Path.Combine(
            _baseInstaller.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(jarPath) || new FileInfo(jarPath).Length < 100_000)
        {
            progress?.Report(new Progress("下载 LiteLoader", 0, 1));
            onLog?.Invoke($"[启动器] 下载 LiteLoader {art.Version}……");

            var dl = await _downloader
                .DownloadOneAsync(new Downloader.DownloadTask(art.JarUrl, jarPath), ct)
                .ConfigureAwait(false);

            if (!dl.Ok)
                throw new InvalidOperationException(
                    $"LiteLoader 下载失败：{art.JarUrl}"
                    + Environment.NewLine
                    + "  这个站（dl.liteloader.com / repo.mumfrey.com）年头很久了，连不上不奇怪；"
                    + "可以在「设置 → 杂项」里配代理再试。");
        }

        // 2) 拼版本 JSON
        var libs = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = $"com.mumfrey:liteloader:{art.Version}",
                ["downloads"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["artifact"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["path"] = rel,
                        ["url"] = art.JarUrl,
                    },
                },
            },
        };

        foreach (var lib in art.Libraries)
        {
            if (string.IsNullOrWhiteSpace(lib.Name)) continue;

            libs.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = lib.Name,
                ["url"] = string.IsNullOrWhiteSpace(lib.Url)
                    ? LiteLoaderProvider.FallbackUrlFor(lib.Name)
                    : lib.Url,
            });
        }

        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = id,
            ["inheritsFrom"] = mcVersion,
            ["time"] = now,
            ["releaseTime"] = now,
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["minecraftArguments"] = LegacyArguments(art.TweakClass),
            ["libraries"] = libs,
        };

        var jsonPath = Path.Combine(vdir, $"{id}.json");
        await File.WriteAllTextAsync(jsonPath, json.ToJsonString(LoaderHttp.Json), ct)
            .ConfigureAwait(false);

        // 3) 把 launchwrapper / asm-all 这些真下下来（本地没有的话启动必崩）
        progress?.Report(new Progress("下载加载器依赖", 0, 1));
        await DownloadLoaderLibrariesAsync(json.ToJsonString(LoaderHttp.Json), ct).ConfigureAwait(false);

        onLog?.Invoke($"[启动器] LiteLoader 装好了：{id}（tweakClass = {art.TweakClass}）");
        progress?.Report(new Progress("完成", 1, 1));

        return new Result(id, jsonPath, LoaderKind.LiteLoader, art.Version, []);
    }

    /// <summary>
    /// 1.12 及以前的旧格式参数串。
    ///
    /// 这是照着原版 1.12.2 的 minecraftArguments 依样画的，只在末尾换成 LiteLoader
    /// 的 tweakClass。缺任何一个占位符，launchwrapper 都起不来。
    /// </summary>
    private static string LegacyArguments(string tweakClass) => string.Join(' ',
        "--username ${auth_player_name}",
        "--version ${version_name}",
        "--gameDir ${game_directory}",
        "--assetsDir ${assets_root}",
        "--assetIndex ${assets_index_name}",
        "--uuid ${auth_uuid}",
        "--accessToken ${access_token}",
        "--userType ${user_type}",
        $"--tweakClass {tweakClass}");

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
            string? path = null;
            string? url = null;

            if (lib.TryGetProperty("downloads", out var dl) &&
                dl.TryGetProperty("artifact", out var art))
            {
                path = art.TryGetProperty("path", out var p) ? p.GetString() : null;
                url = art.TryGetProperty("url", out var u) ? u.GetString() : null;
            }

            // ★ Fabric 的版本 JSON 用的是"maven 坐标 + 仓库根"的老格式：
            //   libraries 里只有 `name` 和 `url`，**没有 downloads.artifact**。
            //
            //   原样 `continue` 掉的话，fabric-loader / sponge-mixin / intermediary
            //   这些 jar 一个都不会下来。后果是启动时抛
            //   `ClassNotFoundException: net.fabricmc.loader.impl.launch.knot.KnotClient`
            //   （mainClass 就在 fabric-loader jar 里）。
            //
            //   这个坑以前被掩盖着：启动链路里的 ClasspathBuilder 会在缺文件时
            //   自己按 maven 规则补下，所以"装完立刻启动"看不出问题；
            //   一旦跳过启动期补文件（SkipInstall，或者实例启动时只想快点进游戏），
            //   就立刻炸。加载器该在装的时候就装完整。
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url))
            {
                var name = lib.TryGetProperty("name", out var n) ? n.GetString() : null;

                var repoRoot = lib.TryGetProperty("url", out var lu) ? lu.GetString() : null;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(repoRoot))
                    continue;

                var rel = ProcessorChain.MavenRelPath(name);
                path = rel;
                url = repoRoot.TrimEnd('/') + "/" + rel;
            }

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

    /// <summary>Legacy Fabric 的 meta（跟官方 Fabric 同构，只是域名不同）。</summary>
    public string? LegacyFabricMeta { get; set; }

    /// <summary>OptiFine 版本列表所在的镜像根（默认 bmclapi2）。</summary>
    public string? OptiFineBase { get; set; }

    /// <summary>LiteLoader 自己那份 versions.json 的根（默认 dl.liteloader.com）。</summary>
    public string? LiteLoaderMeta { get; set; }

    /// <summary>Cleanroom 的 maven 根（默认 repo.cleanroommc.com/releases）。</summary>
    public string? CleanroomMaven { get; set; }

    /// <summary>HTTP 代理，如 http://127.0.0.1:7897。</summary>
    public string? Proxy { get; set; }

    /// <summary>把配置应用到全局 HTTP 客户端。</summary>
    public void Apply()
    {
        if (!string.IsNullOrWhiteSpace(Proxy)) LoaderHttp.ProxyUrl = Proxy;
    }
}