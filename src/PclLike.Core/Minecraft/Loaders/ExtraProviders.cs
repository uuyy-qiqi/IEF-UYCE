using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PclLike.Core.Minecraft.Loaders;

/// <summary>
/// Legacy Fabric：Fabric 的旧版本分支，把 Fabric 的 loader 带到 1.14.4 及更早
/// （官方 Fabric 从 1.14 才开始支持）。
///
/// meta API 跟官方 Fabric **完全同构**，同一个 v2 接口、同样的字段，只是域名换成
/// meta.legacyfabric.net、profile JSON 里的 intermediary 换成了
/// net.legacyfabric:intermediary。所以这个 provider 基本就是 FabricProvider 的复制。
///
/// 唯一的实质差别是**改写 profile JSON 的 id**：那边给的 id 也是
/// "fabric-loader-0.20.1-1.13.2"，跟官方 Fabric 同名 —— 而 1.13.2 / 1.14.4 这两个
/// 版本官方也支持，两边同时装就会互相覆盖同一个 versions 目录。这里统一换成
/// legacy-fabric-loader- 前缀。
/// </summary>
public sealed class LegacyFabricProvider : ILoaderProvider
{
    public const string DefaultMeta = "https://meta.legacyfabric.net";

    private readonly string _meta;

    public LegacyFabricProvider(string? metaBase = null)
        => _meta = (metaBase ?? DefaultMeta).TrimEnd('/');

    public LoaderKind Kind => LoaderKind.LegacyFabric;

    private sealed class Entry
    {
        [JsonPropertyName("loader")] public LoaderInfo? Loader { get; set; }
    }

    private sealed class LoaderInfo
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("stable")] public bool Stable { get; set; }
    }

    public async Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        var url = $"{_meta}/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";

        List<Entry>? list;
        try
        {
            list = await LoaderHttp.GetJsonAsync<List<Entry>>(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (LoaderHttp.IsNotFound(ex))
        {
            // meta API 对"没有这个 MC 版本"就是 400 而不是空列表。
            // Legacy Fabric 只支持 1.13.2 及更早，所以问 1.20.1 就会走到这里 ——
            // 这不是故障，是"没有"，如实返回空列表让界面说人话。
            return [];
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                LoaderHttp.Explain(ex, $"获取 Legacy Fabric 版本列表（{mcVersion}）"), ex);
        }

        if (list is null || list.Count == 0) return [];

        // API 已经按新到旧排序，保持原顺序
        return list
            .Where(e => !string.IsNullOrWhiteSpace(e.Loader?.Version))
            .Select(e => new LoaderVersion(
                LoaderKind.LegacyFabric,
                mcVersion,
                e.Loader!.Version,
                e.Loader.Stable,
                InstallerUrl: $"{_meta}/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(e.Loader.Version)}/profile/json",
                Homepage: "https://legacyfabric.net/"))
            .ToList();
    }

    public async Task<LoaderVersion?> GetLatestAsync(string mcVersion, CancellationToken ct = default)
    {
        var all = await ListVersionsAsync(mcVersion, ct).ConfigureAwait(false);
        return all.FirstOrDefault(v => v.IsStable) ?? all.FirstOrDefault();
    }

    /// <summary>取最终版本 JSON，并把 id 换成我们自己的（见类注释）。</summary>
    public async Task<string> GetProfileJsonAsync(string mcVersion, string loaderVersion, CancellationToken ct = default)
    {
        var url = $"{_meta}/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

        string raw;
        try
        {
            raw = await LoaderHttp.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                LoaderHttp.Explain(ex, $"下载 Legacy Fabric {loaderVersion} 的版本 JSON"), ex);
        }

        var id = new LoaderVersion(LoaderKind.LegacyFabric, mcVersion, loaderVersion, true).ResultVersionId;
        try
        {
            if (JsonNode.Parse(raw) is JsonObject obj)
            {
                obj["id"] = id;
                return obj.ToJsonString(LoaderHttp.Json);
            }
        }
        catch
        {
            // 改不动就原样用：最坏情况是版本目录跟官方 Fabric 撞名，不影响能不能启动
        }

        return raw;
    }
}

/// <summary>
/// OptiFine。
///
/// 它严格说不是"加载器"，而是给原版打补丁的优化器；但安装流程和加载器一样，
/// 所以放在同一套接口里。
///
/// 版本列表走 BMCLAPI 的镜像（optifine.net 官方没有 API）：
///
///   GET https://bmclapi2.bangbang93.com/optifine/{mc}
///       → [{ mcversion, type, patch, filename, forge }]
///   GET https://bmclapi2.bangbang93.com/optifine/{mc}/{type}/{patch}
///       → 那个 jar 本体
///
/// 列表里没有"稳定/测试"的概念，所以 StabilityKnown = false。
/// 真正的安装（跑 optifine.Patcher 打补丁）在 LoaderInstaller.InstallOptiFineAsync。
/// </summary>
public sealed class OptiFineProvider : ILoaderProvider
{
    public const string DefaultBase = "https://bmclapi2.bangbang93.com";

    private readonly string _base;

    public OptiFineProvider(string? baseUrl = null)
        => _base = (baseUrl ?? DefaultBase).TrimEnd('/');

    public LoaderKind Kind => LoaderKind.OptiFine;

    private sealed class Entry
    {
        [JsonPropertyName("mcversion")] public string McVersion { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("patch")] public string Patch { get; set; } = "";
        [JsonPropertyName("filename")] public string FileName { get; set; } = "";
    }

    public async Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        var url = $"{_base}/optifine/{Uri.EscapeDataString(mcVersion)}";

        List<Entry>? list;
        try
        {
            list = await LoaderHttp.GetJsonAsync<List<Entry>>(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                LoaderHttp.Explain(ex, $"获取 OptiFine 版本列表（{mcVersion}）"), ex);
        }

        if (list is null || list.Count == 0) return [];

        var result = new List<LoaderVersion>();
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (string.IsNullOrWhiteSpace(e.Type) || string.IsNullOrWhiteSpace(e.Patch)) continue;

            // 列表里的 "type" 是 HD_U / HD_U_L 这类，"patch" 是 I5 / H9 这种代号，
            // 拼起来正好是官方叫法 HD_U_I5。
            var edition = $"{e.Type}_{e.Patch}";

            result.Add(new LoaderVersion(
                LoaderKind.OptiFine,
                mcVersion,
                edition,
                IsStable: i == 0,          // 只有最新的算"推荐"
                InstallerUrl: $"{_base}/optifine/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(e.Type)}/{Uri.EscapeDataString(e.Patch)}",
                Homepage: "https://optifine.net/")
            {
                StabilityKnown = false,    // 来源没有稳定/测试之分，别替它下结论
            });
        }

        return result;
    }

    public async Task<LoaderVersion?> GetLatestAsync(string mcVersion, CancellationToken ct = default)
    {
        var all = await ListVersionsAsync(mcVersion, ct).ConfigureAwait(false);
        return all.FirstOrDefault();
    }
}