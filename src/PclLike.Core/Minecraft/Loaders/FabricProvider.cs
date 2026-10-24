using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclLike.Core.Minecraft.Loaders;

/// <summary>
/// Fabric 加载器。官方 meta API：
///
///   GET /v2/versions/loader/{mc}            → loader 版本列表
///   GET /v2/versions/loader/{mc}/{loader}/profile/json → 最终版本 JSON
///
/// profile JSON 已经是"能直接启动"的完整版本描述：它带
/// `inheritsFrom` 指向原版，`libraries` 里是 loader 自己的 jar。
/// 所以 Fabric 的安装几乎是零成本——拿到 JSON 存进 versions/ 就完事。
/// </summary>
public sealed class FabricProvider : ILoaderProvider
{
    public const string DefaultMeta = "https://meta.fabricmc.net";

    private readonly string _meta;

    public FabricProvider(string? metaBase = null)
        => _meta = (metaBase ?? DefaultMeta).TrimEnd('/');

    public LoaderKind Kind => LoaderKind.Fabric;

    private sealed class LoaderEntry
    {
        [JsonPropertyName("loader")] public LoaderInfo? Loader { get; set; }
        [JsonPropertyName("intermediary")] public IdVersion? Intermediary { get; set; }
    }

    private sealed class LoaderInfo
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("stable")] public bool Stable { get; set; }
    }

    private sealed class IdVersion
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
    }

    public async Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        var url = $"{_meta}/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";

        List<LoaderEntry>? list;
        try
        {
            list = await LoaderHttp.GetJsonAsync<List<LoaderEntry>>(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LoaderHttp.Explain(ex, $"获取 Fabric 版本列表（{mcVersion}）"), ex);
        }

        if (list is null || list.Count == 0)
            return [];

        // API 已经按新到旧排序，保持原顺序
        return list
            .Where(e => !string.IsNullOrWhiteSpace(e.Loader?.Version))
            .Select(e => new LoaderVersion(
                LoaderKind.Fabric,
                mcVersion,
                e.Loader!.Version,
                e.Loader.Stable,
                InstallerUrl: $"{_meta}/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(e.Loader.Version)}/profile/json",
                Homepage: "https://fabricmc.net/"))
            .ToList();
    }

    public async Task<LoaderVersion?> GetLatestAsync(string mcVersion, CancellationToken ct = default)
    {
        var all = await ListVersionsAsync(mcVersion, ct).ConfigureAwait(false);
        return all.FirstOrDefault(v => v.IsStable) ?? all.FirstOrDefault();
    }

    /// <summary>取最终版本 JSON（直接可落盘的那种）。</summary>
    public async Task<string> GetProfileJsonAsync(string mcVersion, string loaderVersion, CancellationToken ct = default)
    {
        var url = $"{_meta}/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        try
        {
            return await LoaderHttp.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LoaderHttp.Explain(ex, $"下载 Fabric {loaderVersion} 的版本 JSON"), ex);
        }
    }
}

/// <summary>
/// Quilt 加载器。meta API 跟 Fabric 同构，只是域不同、多一层
/// "选定 Fabric 兼容版本"的概念——Quilt 会复用 Fabric 的 loader。
///
///   GET /v3/versions/loader/{mc}
///   GET /v3/versions/loader/{mc}/{loader}/profile/json
/// </summary>
public sealed class QuiltProvider : ILoaderProvider
{
    public const string DefaultMeta = "https://meta.quiltmc.org";

    private readonly string _meta;

    public QuiltProvider(string? metaBase = null)
        => _meta = (metaBase ?? DefaultMeta).TrimEnd('/');

    public LoaderKind Kind => LoaderKind.Quilt;

    private sealed class Entry
    {
        [JsonPropertyName("loader")] public LoaderInfo? Loader { get; set; }
    }

    private sealed class LoaderInfo
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("stable")] public bool? Stable { get; set; }
    }

    public async Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        var url = $"{_meta}/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}";

        List<Entry>? list;
        try
        {
            list = await LoaderHttp.GetJsonAsync<List<Entry>>(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LoaderHttp.Explain(ex, $"获取 Quilt 版本列表（{mcVersion}）"), ex);
        }

        if (list is null) return [];

        return list
            .Where(e => !string.IsNullOrWhiteSpace(e.Loader?.Version))
            .Select(e => new LoaderVersion(
                LoaderKind.Quilt,
                mcVersion,
                e.Loader!.Version,
                e.Loader.Stable ?? false,
                Homepage: "https://quiltmc.org/"))
            .ToList();
    }

    public async Task<LoaderVersion?> GetLatestAsync(string mcVersion, CancellationToken ct = default)
    {
        var all = await ListVersionsAsync(mcVersion, ct).ConfigureAwait(false);
        return all.FirstOrDefault(v => v.IsStable) ?? all.FirstOrDefault();
    }

    public async Task<string> GetProfileJsonAsync(string mcVersion, string loaderVersion, CancellationToken ct = default)
    {
        var url = $"{_meta}/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        try
        {
            return await LoaderHttp.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LoaderHttp.Explain(ex, $"下载 Quilt {loaderVersion} 的版本 JSON"), ex);
        }
    }
}