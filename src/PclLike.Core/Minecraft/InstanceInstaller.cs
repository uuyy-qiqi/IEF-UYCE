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
        string AssetIndexId, string MainClass, List<string> Warnings);

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

        foreach (var plan in plans)
        {
            if (plan.Jar is { Url: not null } j)
                jarTasks.Add(new Downloader.DownloadTask(j.Url, Path.Combine(LibrariesDir,
                    (j.Path ?? plan.Library.ToMavenPath()).Replace('/', Path.DirectorySeparatorChar)), j.Sha1, j.Size));

            if (plan.NativeJar is { Url: not null } n)
                nativeTasks.Add((new Downloader.DownloadTask(n.Url, Path.Combine(LibrariesDir,
                    (n.Path ?? plan.Library.ToMavenPath()).Replace('/', Path.DirectorySeparatorChar)), n.Sha1, n.Size),
                    plan.ExtractExcludePrefix));
        }

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

        foreach (var r in libResults.Where(r => !r.Ok))
            warnings.Add($"缺失库：{Path.GetFileName(r.TargetPath)}（{r.Error}）");

        return new InstallResult(
            VersionId: versionId,
            ClientJar: clientJar,
            Classpath: ClasspathBuilder.JoinClasspath(cp),
            NativesDir: nativesDir,
            AssetIndexId: assetIndexId,
            MainClass: version.MainClass ?? "net.minecraft.client.main.Main",
            Warnings: warnings);
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
}