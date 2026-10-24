using System.Text.Json;
using PclLike.Core.Minecraft;
using PclLike.Core.Storage;

namespace PclLike.Core.Minecraft.Install;

/// <summary>
/// 资源（assets）安装。
///
/// 三种形态：
///   1. 现代（1.7.3+）：objects/&lt;hash前两位&gt;/&lt;hash&gt;，加 objects/&lt;hash前两位&gt;/ 前缀
///   2. virtual（1.6~1.7.2 的 legacy 资源）：额外映射成 资源名/路径 的目录树，
///      放在 assets/virtual/legacy/ 下，启动时用 --assetsDir 指到那里
///   3. map_to_resources：老版本直接把资源塞进游戏目录的 resources/ 下
///
/// 判定依据是资产索引 JSON 里的 virtual / map_to_resources 布尔字段。
/// 索引只下 2 个网段地址：索引本身在 downloads.assetIndex.url，
/// 每个对象 URL 由 hash 前缀拼出来，这是固定规则不用问服务器。
/// </summary>
public sealed class AssetInstaller
{
    public const string ResourcesBaseUrl = "https://resources.download.minecraft.net";

    private readonly Downloader _downloader;

    public AssetInstaller(Downloader downloader) => _downloader = downloader;

    public sealed record Plan(List<Downloader.DownloadTask> Tasks, bool Virtual, bool MapToResources,
        Dictionary<string, string> Mapping);

    /// <summary>解析资产索引，产出下载任务与虚拟映射表。纯计算，无 IO 等待。</summary>
    public static Task<Plan> BuildPlanAsync(
        AssetIndex index, string assetsRoot, CancellationToken ct = default)
    {
        var tasks = new List<Downloader.DownloadTask>();
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var objectsDir = Path.Combine(assetsRoot, "objects");

        foreach (var (name, obj) in index.Objects ?? [])
        {
            if (obj.Hash is null) continue;

            var prefix = obj.Hash[..2];
            var rel = $"{prefix}/{obj.Hash}";
            var path = Path.Combine(objectsDir, prefix, obj.Hash);

            tasks.Add(new Downloader.DownloadTask(
                $"{ResourcesBaseUrl}/{rel}", path, obj.Hash, obj.Size));

            // virtual / map_to_resources 形态需要原始资源名
            if (index.Virtual == true || index.MapToResources == true)
                mapping[name] = path;
        }

        return Task.FromResult(new Plan(tasks, index.Virtual == true, index.MapToResources == true, mapping));
    }

    public async Task<int> InstallAsync(AssetIndex index, string assetsRoot,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var plan = await BuildPlanAsync(index, assetsRoot, ct).ConfigureAwait(false);
        var results = await _downloader.DownloadAllAsync(plan.Tasks, progress, ct).ConfigureAwait(false);
        var failed = results.Count(r => !r.Ok);
        if (failed > 0)
            throw new IOException($"{failed} 个资源文件下载失败，首个错误：{results.First(r => !r.Ok).Error}");

        if (plan.Virtual) BuildVirtualTree(assetsRoot, plan.Mapping, "virtual/legacy");
        if (plan.MapToResources) BuildVirtualTree(assetsRoot, plan.Mapping, "resources");

        return results.Count(r => !r.Skipped);
    }

    /// <summary>
    /// 按原始资源名建立目录树。老版本游戏按名字直接读文件，不认 hash 布局。
    /// 用硬链接/复制都行，这里复制，避免跨盘硬链接失败。
    /// </summary>
    public static void BuildVirtualTree(string assetsRoot, Dictionary<string, string> mapping, string subDir)
    {
        var root = Path.Combine(assetsRoot, subDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(root);

        foreach (var (name, sourcePath) in mapping)
        {
            if (!File.Exists(sourcePath)) continue;

            var dest = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(dest)!;
            Directory.CreateDirectory(destDir);

            try
            {
                File.Copy(sourcePath, dest, overwrite: false);
            }
            catch (IOException) when (File.Exists(dest))
            {
                // 已存在，跳过
            }
        }
    }

    public static async Task<AssetIndex> LoadIndexAsync(string indexFile, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(indexFile);
        var index = await JsonSerializer.DeserializeAsync<AssetIndex>(fs, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }, ct).ConfigureAwait(false);

        return index ?? throw new InvalidDataException($"资产索引解析失败：{indexFile}");
    }
}