using PclLike.Core.Minecraft;
using PclLike.Core.Minecraft.Install;
using PclLike.Core.Minecraft.Launch;
using PclLike.Core.Storage;

namespace PclLike.Core.Minecraft;

/// <summary>
/// 实例安装编排：从版本 id 一路走到"可以启动"。
///
/// 步骤（顺序不能乱）：
///   1. 取版本清单 → 拿到版本 JSON 地址
///   2. 解析版本 JSON（含 inheritsFrom 合并）
///   3. 下载客户端 jar
///   4. 下载并解压资源索引 + 全部 assets
///   5. 下载全部库
///   6. 解压 natives
///   7. 组装 classpath 与参数，写回一个 launch.json
///
/// 每一步都要能单独重跑：已下好且校验通过的文件会被跳过，
/// 所以重复执行是安全的（幂等）。
/// </summary>
public sealed class InstanceInstaller
{
    public const string VersionManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly Downloader _downloader;
    private readonly string _root;          // 一般指 .minecraft

    public InstanceInstaller(string gameRoot, Downloader? downloader = null)
    {
        _root = gameRoot;
        _downloader = downloader ?? new Downloader();
    }

    public string VersionsDir => Path.Combine(_root, "versions");
    public string LibrariesDir => Path.Combine(_root, "libraries");
    public string AssetsDir => Path.Combine(_root, "assets");
    public string VersionDir(string id) => Path.Combine(VersionsDir, id);

    public sealed record InstallProgress(string Stage, int Done, int Total);
    public sealed record InstallResult(string VersionId, string ClientJar, string Classpath, string NativesDir,
        string AssetIndexId, string MainClass, List<string> Warnings,
        List<string> MissingLibraries);

    /// <summary>
    /// 启动前兜底：保证原生库已经解压到 nativesDir。
    ///
    /// 完整安装那一次会解压，但那条路依赖 nativeTasks 非空 —— 旧式版本 JSON
    /// （natives 写在 natives{} 里、downloads 只给 classifiers）在某些加载器/整合包里
    /// 推不出分类器，nativeTasks 就是空的：Reset 把 natives 目录清空、却什么都没补回来，
    /// 启动时报
    ///   java.lang.UnsatisfiedLinkError: Failed to locate library: lwjgl.dll
    /// （实测：1.12.2 + Cleanroom 0.7.12-alpha，natives 目录整个是空的。）
    ///
    /// 这里换成从 libraries 目录里按每个 plan 的 natives jar 直接解压：
    /// 幂等（内容相同会覆盖写成同一份），不依赖下载任务列表。
    /// </summary>
    public async Task<int> EnsureNativesAsync(string versionId, string nativesDir,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var resolver = new VersionResolver(LoadVersionJsonAsync);
        var version = await resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
        var plans = ClasspathBuilder.PlanAll(version.Libraries ?? []);
        Directory.CreateDirectory(nativesDir);

        var done = 0;
        var withNatives = 0;
        var missing = 0;
        foreach (var plan in plans)
        {
            var artifact = plan.NativeJar;
            if (artifact is null) continue;
            withNatives++;

            var rel = (artifact.Path ?? plan.Library.ToMavenPath()).Replace('/', Path.DirectorySeparatorChar);
            var jar = Path.Combine(LibrariesDir, rel);

            // 盘上没有就补下。老式 natives 的下载任务只在「完整安装」里排一次，
            // 如果那次因为分类器推不出来而漏了、或者用户是直接启动（跳过了安装），
            // 这里就是最后一道兜底 —— natives jar 都很小，几百 KB 到几 MB。
            if (!File.Exists(jar) && !string.IsNullOrWhiteSpace(artifact.Url))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
                    var results = await _downloader.DownloadAllAsync(
                        [new Downloader.DownloadTask(artifact.Url, jar, artifact.Sha1, artifact.Size)],
                        null, ct).ConfigureAwait(false);
                    if (results.Count > 0 && !results[0].Ok)
                        log?.Invoke($"原生库下载失败 {Path.GetFileName(jar)}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"原生库下载失败 {Path.GetFileName(jar)}：{ex.Message}");
                }
            }

            if (!File.Exists(jar)) { missing++; continue; }

            try
            {
                NativesExtractor.Extract(jar, nativesDir, plan.ExtractExcludePrefix);
                done++;
            }
            catch (Exception ex)
            {
                log?.Invoke($"原生库解压失败 {Path.GetFileName(jar)}：{ex.Message}");
            }
        }

        // 一行摘要进日志：出问题时能一眼看出是「没有 natives 计划」还是「jar 没下下来」。
        log?.Invoke($"[原生库] 计划 {plans.Count} 个，带 natives 的 {withNatives} 个，解压 {done} 个，缺 jar {missing} 个");

        return done;
    }

    public async Task<InstallResult> InstallAsync(string versionId,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        // 步骤 1~2：拿版本 JSON
        progress?.Report(new InstallProgress("解析版本", 0, 1));
        var resolver = new VersionResolver(LoadVersionJsonAsync);
        VersionJson version;
        try
        {
            version = await resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // 本地没有，先从 Mojang 拉
            await DownloadVersionJsonAsync(versionId, ct).ConfigureAwait(false);
            version = await resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
        }

        var vdir = VersionDir(versionId);
        Directory.CreateDirectory(vdir);

        // 步骤 3：客户端 jar
        progress?.Report(new InstallProgress("下载客户端", 0, 1));
        var clientJar = Path.Combine(vdir, $"{versionId}.jar");
        var clientArtifact = version.Downloads?.Client;

        if (clientArtifact?.Url is not null)
        {
            var r = await _downloader.DownloadOneAsync(
                new Downloader.DownloadTask(clientArtifact.Url, clientJar, clientArtifact.Sha1, clientArtifact.Size), ct)
                .ConfigureAwait(false);
            if (!r.Ok) warnings.Add($"客户端 jar 下载失败：{r.Error}");
        }
        else if (version.InheritsFrom is not null)
        {
            // 继承版本（Forge/Fabric）自己不带客户端 jar，指向父版本
            var parentJar = Path.Combine(VersionsDir, version.InheritsFrom, $"{version.InheritsFrom}.jar");
            if (File.Exists(parentJar)) clientJar = parentJar;
            else warnings.Add($"父版本客户端 jar 缺失：{parentJar}");
        }
        else
        {
            warnings.Add("版本 JSON 里没有客户端下载信息");
        }

        // 步骤 4：assets
        var assetIndexId = version.AssetIndex?.Id ?? version.Assets ?? "legacy";
        if (version.AssetIndex?.Url is not null)
        {
            var indexFile = Path.Combine(AssetsDir, "indexes", $"{assetIndexId}.json");
            var r = await _downloader.DownloadOneAsync(new Downloader.DownloadTask(
                version.AssetIndex.Url, indexFile, version.AssetIndex.Sha1, version.AssetIndex.Size), ct)
                .ConfigureAwait(false);

            if (r.Ok || File.Exists(indexFile))
            {
                var index = await AssetInstaller.LoadIndexAsync(indexFile, ct).ConfigureAwait(false);
                var assets = new AssetInstaller(_downloader);
                var reporter = progress is null ? null : new Progress<(int Done, int Total)>(p =>
                    progress.Report(new InstallProgress("下载资源", p.Done, p.Total)));
                await assets.InstallAsync(index, AssetsDir, reporter, ct).ConfigureAwait(false);
            }
            else
            {
                warnings.Add($"资产索引下载失败：{r.Error}");
            }
        }

        // 步骤 5：库
        progress?.Report(new InstallProgress("下载依赖库", 0, 1));
        var plans = ClasspathBuilder.PlanAll(version.Libraries ?? [], features: null);
        var jarTasks = new List<Downloader.DownloadTask>();
        var nativeTasks = new List<(Downloader.DownloadTask Task, string ExcludePrefix)>();
        var noUrlLibs = new List<string>();

        // 同一个 version json 里的库，地址推算得靠彼此的 maven 根（见 MavenIndex）
        var maven = MavenIndex.Build(version.Libraries);

        foreach (var plan in plans)
        {
            if (plan.Jar is { } j)
            {
                var url = ResolveLibraryUrl(j, plan.Library, maven);
                if (url is null)
                {
                    // 以前这里是 `is { Url: not null }`：地址空的库被静默跳过，
                    // 不下载也不报警，等启动时才炸成 ClassNotFoundException。
                    noUrlLibs.Add(Path.GetFileName(plan.Library.ToMavenPath()));
                }
                else
                {
                    jarTasks.Add(new Downloader.DownloadTask(url, Path.Combine(LibrariesDir,
                        (j.Path ?? plan.Library.ToMavenPath()).Replace('/', Path.DirectorySeparatorChar)), j.Sha1, j.Size));
                }
            }

            if (plan.NativeJar is { } n)
            {
                var url = ResolveLibraryUrl(n, plan.Library, maven);
                if (url is null)
                {
                    noUrlLibs.Add(Path.GetFileName(plan.Library.ToMavenPath()));
                }
                else
                {
                    nativeTasks.Add((new Downloader.DownloadTask(url, Path.Combine(LibrariesDir,
                        (n.Path ?? plan.Library.ToMavenPath()).Replace('/', Path.DirectorySeparatorChar)), n.Sha1, n.Size),
                        plan.ExtractExcludePrefix));
                }
            }
        }

        if (noUrlLibs.Count > 0)
            warnings.Add($"{noUrlLibs.Count} 个依赖库推不出下载地址：{string.Join("、", noUrlLibs.Take(3))}");

        var libReporter = progress is null ? null : new Progress<(int Done, int Total)>(p =>
            progress.Report(new InstallProgress("下载依赖库", p.Done, p.Total)));
        var libResults = await _downloader.DownloadAllAsync(jarTasks, libReporter, ct).ConfigureAwait(false);
        var failedLibs = libResults.Count(r => !r.Ok);
        if (failedLibs > 0) warnings.Add($"{failedLibs} 个依赖库下载失败");

        // 步骤 6：natives
        var nativesDir = Path.Combine(vdir, "natives");
        NativesExtractor.Reset(nativesDir);

        var nativeReporter = progress is null ? null : new Progress<(int Done, int Total)>(p =>
            progress.Report(new InstallProgress("解压原生库", p.Done, p.Total)));
        var nativeResults = await _downloader.DownloadAllAsync(
            nativeTasks.Select(t => t.Task).ToList(), nativeReporter, ct).ConfigureAwait(false);

        for (var i = 0; i < nativeTasks.Count; i++)
        {
            var path = nativeTasks[i].Task.TargetPath;
            if (!File.Exists(path)) continue;
            try
            {
                NativesExtractor.Extract(path, nativesDir, nativeTasks[i].ExcludePrefix);
            }
            catch (Exception ex)
            {
                warnings.Add($"原生库解压失败 {Path.GetFileName(path)}：{ex.Message}");
            }
        }

        // 步骤 7：classpath
        var cp = plans.Where(p => p.Jar is not null)
            .Select(p => Path.Combine(LibrariesDir,
                (p.Jar!.Path ?? p.Library.ToMavenPath()).Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToList();

        if (File.Exists(clientJar)) cp.Add(clientJar);

        var missingLibs = libResults.Where(r => !r.Ok)
            .Select(r => Path.GetFileName(r.TargetPath))
            .ToList();
        missingLibs.AddRange(noUrlLibs.Distinct(StringComparer.OrdinalIgnoreCase));

        foreach (var r in libResults.Where(r => !r.Ok))
            warnings.Add($"缺失库：{Path.GetFileName(r.TargetPath)}（{r.Error}）");

        return new InstallResult(
            VersionId: versionId,
            ClientJar: clientJar,
            Classpath: ClasspathBuilder.JoinClasspath(cp),
            NativesDir: nativesDir,
            AssetIndexId: assetIndexId,
            MainClass: version.MainClass ?? "net.minecraft.client.main.Main",
            Warnings: warnings,
            MissingLibraries: missingLibs);
    }

    /// <summary>本地版本 JSON 读取。找不到就抛 FileNotFoundException 让上层去下载。</summary>
    private Task<string> LoadVersionJsonAsync(string id, CancellationToken ct)
    {
        var file = Path.Combine(VersionsDir, id, $"{id}.json");
        if (!File.Exists(file))
            throw new FileNotFoundException($"本地缺少版本 JSON：{file}", file);

        return File.ReadAllTextAsync(file, ct);
    }

    /// <summary>从版本清单里找 id 对应的 URL 并下载版本 JSON 到版本目录。</summary>
    public async Task DownloadVersionJsonAsync(string versionId, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(_root, "versions", "version_manifest_v2.json");
        var r = await _downloader.DownloadOneAsync(
            new Downloader.DownloadTask(VersionManifestUrl, manifestPath), ct).ConfigureAwait(false);
        if (!r.Ok) throw new IOException($"版本清单下载失败：{r.Error}");

        var manifest = System.Text.Json.JsonSerializer.Deserialize<VersionManifest>(
            await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("版本清单解析失败");

        var entry = manifest.Versions?.FirstOrDefault(v =>
            string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"版本清单里找不到 {versionId}");

        var dest = Path.Combine(VersionsDir, versionId, $"{versionId}.json");
        var jr = await _downloader.DownloadOneAsync(
            new Downloader.DownloadTask(entry.Url!, dest, entry.Sha1), ct).ConfigureAwait(false);
        if (!jr.Ok) throw new IOException($"版本 JSON 下载失败：{jr.Error}");
    }

    /// <summary>列出 Mojang 官方提供的全部版本。</summary>
    public async Task<VersionManifest> FetchManifestAsync(CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(_root, "versions", "version_manifest_v2.json");
        await _downloader.DownloadOneAsync(
            new Downloader.DownloadTask(VersionManifestUrl, manifestPath), ct).ConfigureAwait(false);

        return System.Text.Json.JsonSerializer.Deserialize<VersionManifest>(
            await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("版本清单解析失败");
    }

    /// <summary>
    /// 库 jar 的下载地址。
    ///
    /// 三种情况按顺序兜底：
    ///   1. downloads.artifact.url 是绝对地址 —— 直接用它（绝大多数版本）；
    ///   2. 它是空的、只给了 path —— 用**同组**兄弟库反推出的 maven 根去补。
    ///      Cleanroom 的主 jar 正是这样：url 是空串，而 com.cleanroommc:* 的
    ///      兄弟们都在 https://maven.cleanroommc.com/；
    ///   3. 还是补不出来 —— 退到 Library.url（老格式的 maven 根）。
    ///
    /// 补不出来就返回 null，由调用方记进「缺失库」；绝不拿空地址去发请求
    /// （那只会得到 "An invalid request URI was provided"）。
    /// </summary>
    private static string? ResolveLibraryUrl(Artifact artifact, Library lib, MavenIndex maven)
    {
        var rel = (artifact.Path ?? lib.ToMavenPath()).Replace('\\', '/').TrimStart('/');

        if (!string.IsNullOrWhiteSpace(artifact.Url)
            && Uri.TryCreate(artifact.Url, UriKind.Absolute, out _))
        {
            return artifact.Url;
        }

        var baseUrl = maven.BaseFor(lib.Name)
                      ?? maven.MostCommon
                      ?? MavenIndex.NormalizeBase(lib.Url);

        return baseUrl is null ? null : baseUrl + rel;
    }

    /// <summary>
    /// 从一个 version json 的库列表里反推 maven 根。
    ///
    /// 做法：拿兄弟库的绝对地址减掉它自己的相对路径，剩下的就是根。
    /// 再记一份「组 → 根」，这样同组的库能对上号 —— com.cleanroommc:* 都在
    /// https://maven.cleanroommc.com/，而 ca.weblite 在 Maven Central，
    /// 光靠"出现最多的那个根"会挑错。
    /// </summary>
    private sealed class MavenIndex
    {
        private readonly Dictionary<string, string> _byGroup = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _byBase = new(StringComparer.OrdinalIgnoreCase);
        private int _mostCommonCount;

        /// <summary>出现次数最多的 maven 根（兜底用）。</summary>
        public string? MostCommon { get; private set; }

        public static MavenIndex Build(List<Library>? libs)
        {
            var index = new MavenIndex();
            if (libs is null) return index;

            foreach (var lib in libs)
            {
                var abs = lib.Downloads?.Artifact?.Url;
                var rel = (lib.Downloads?.Artifact?.Path ?? lib.ToMavenPath())
                    .Replace('\\', '/').TrimStart('/');

                string? root = null;

                if (!string.IsNullOrWhiteSpace(abs)
                    && Uri.TryCreate(abs, UriKind.Absolute, out _)
                    && abs.Length > rel.Length
                    && abs.EndsWith(rel, StringComparison.OrdinalIgnoreCase))
                {
                    root = abs[..^rel.Length];
                }
                else
                {
                    root = NormalizeBase(lib.Url);
                }

                if (root is null) continue;

                var group = GroupPath(lib.Name);
                if (group is not null) index._byGroup[group] = root;

                var n = index._byBase.TryGetValue(root, out var c) ? c + 1 : 1;
                index._byBase[root] = n;

                if (n > index._mostCommonCount)
                {
                    index._mostCommonCount = n;
                    index.MostCommon = root;
                }
            }

            return index;
        }

        public string? BaseFor(string? libraryName)
        {
            var group = GroupPath(libraryName);
            return group is not null && _byGroup.TryGetValue(group, out var root) ? root : null;
        }

        /// <summary>库名里的组换成目录形式：com.cleanroommc:cleanmix:0.7.4 → com/cleanroommc。</summary>
        private static string? GroupPath(string? libraryName)
        {
            if (string.IsNullOrWhiteSpace(libraryName)) return null;
            var colon = libraryName.IndexOf(':');
            return colon <= 0 ? null : libraryName[..colon].Replace('.', '/');
        }

        /// <summary>把 maven 根规整成「绝对地址 + 结尾斜杠」，不合规就返回 null。</summary>
        public static string? NormalizeBase(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _)) return null;
            return baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
        }
    }
}