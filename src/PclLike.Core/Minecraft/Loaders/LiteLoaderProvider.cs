using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PclLike.Core.Minecraft.Loaders;

/// <summary>
/// LiteLoader：1.12.2 及更早的轻量加载器。
///
/// 它的挂载方式是老式的 tweakClass：靠 launchwrapper 起主类
/// <c>net.minecraft.launchwrapper.Launch</c>，再让 <c>--tweakClass</c> 指定的
/// LiteLoaderTweaker 在类加载阶段插进去。所以装出来的版本 JSON 用的是
/// **1.12 之前的旧格式**（<c>minecraftArguments</c> 一整串、没有 arguments 对象），
/// 好在 ArgumentBuilder 两条格式都认。
///
/// 元数据是 liteloader 自己的一份 versions.json（不是 maven 元数据）：
/// <code>
/// versions["1.12.2"].snapshots["com.mumfrey:liteloader"].latest
///     = { version, file, tweakClass, libraries[], build, md5 }
/// versions["1.12.2"].repo = { url, type, stream, classifier }
/// </code>
/// 1.7 那一代的条目放在 <c>artefacts</c> 里（英式拼写），字段名一样 —— 两个都认。
/// </summary>
public sealed class LiteLoaderProvider : ILoaderProvider
{
    public const string DefaultMeta = "http://dl.liteloader.com/versions";

    private const string ArtifactKey = "com.mumfrey:liteloader";

    private readonly string _meta;

    public LiteLoaderProvider(string? metaBase = null)
        => _meta = (metaBase ?? DefaultMeta).TrimEnd('/');

    public LoaderKind Kind => LoaderKind.LiteLoader;

    // ── 元数据模型（public：安装侧要拿 tweakClass 和依赖库）──
    //
    // 不把它反序列化成强类型：snapshots 这一层是"混装"的 ——
    // 除了 "com.mumfrey:liteloader"，还塞了一个 "libraries" 键，而且各版本的
    // 条目形状并不统一（dev / repo / snapshots / artefacts 交错）。用强类型
    // Dictionary 去接会直接抛"无法解析的数据"。所以按 JsonNode 松着取。
    public sealed class Artifact
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("tweakClass")] public string? TweakClass { get; set; }
        [JsonPropertyName("libraries")] public List<LibRef>? Libraries { get; set; }
    }

    public sealed class LibRef
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    public sealed class RepoEntry
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private async Task<JsonNode?> FetchAsync(CancellationToken ct)
        => await LoaderHttp
            .GetJsonAsync<JsonNode>($"{_meta}/versions.json", ct)
            .ConfigureAwait(false);

    /// <summary>安全取子节点：Node 是数组/缺键时都给 null，不抛。</summary>
    private static JsonNode? At(JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    private static string Text(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static List<LibRef> ParseLibs(JsonNode? node)
    {
        var list = new List<LibRef>();
        if (node is not JsonArray arr) return list;

        foreach (var item in arr)
        {
            var name = Text(At(item, "name"));
            if (string.IsNullOrWhiteSpace(name)) continue;

            var url = Text(At(item, "url"));
            list.Add(new LibRef { Name = name, Url = url.Length == 0 ? null : url });
        }

        return list;
    }

    public async Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(
        string mcVersion, CancellationToken ct = default)
    {
        var root = await FetchAsync(ct).ConfigureAwait(false);
        var one = Pick(root, mcVersion);
        return one is null ? [] : [Make(mcVersion, one.Value.Art, one.Value.Repo)];
    }

    public async Task<LoaderVersion?> GetLatestAsync(string mcVersion, CancellationToken ct = default)
    {
        var list = await ListVersionsAsync(mcVersion, ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>元数据里一个 MC 版本只有一个 LiteLoader 条目，所以列表最多一项。</summary>
    private static (Artifact Art, RepoEntry? Repo)? Pick(JsonNode? root, string mcVersion)
    {
        var tables = At(At(root, "versions"), mcVersion);
        if (tables is null) return null;

        // snapshots 是 1.12 那一代的，artefacts 是 1.7 时代的英式拼写，字段一样
        var slot = At(At(tables, "snapshots"), ArtifactKey)
                ?? At(At(tables, "artefacts"), ArtifactKey);

        var latest = At(slot, "latest");
        if (latest is null) return null;

        var art = new Artifact
        {
            Version = Text(At(latest, "version")),
            File = Text(At(latest, "file")),
            TweakClass = Text(At(latest, "tweakClass")) is { Length: > 0 } tc ? tc : null,
            Libraries = ParseLibs(At(latest, "libraries")),
        };

        if (art.Version.Length == 0 || art.File.Length == 0) return null;

        var repoNode = At(tables, "repo");
        var repoUrl = Text(At(repoNode, "url"));
        var repoType = Text(At(repoNode, "type"));

        return (art, repoNode is null
            ? null
            : new RepoEntry { Url = repoUrl.Length == 0 ? null : repoUrl, Type = repoType.Length == 0 ? null : repoType });
    }

    private static LoaderVersion Make(string mc, Artifact art, RepoEntry? repo) => new(
        LoaderKind.LiteLoader,
        mc,
        art.Version,
        IsStable: false,
        InstallerUrl: JarUrl(art, repo),
        Homepage: "http://www.liteloader.com/")
    {
        // 真实的 1.12.2 条目就是 "1.12.2-SNAPSHOT"，它自己也没标稳定不稳定 ——
        // 别让界面替它说"测试版"。
        StabilityKnown = false,
    };

    /// <summary>
    /// jar 的地址。m2 快照仓库和 1.7 时代的 ivy 仓库路径形状一致，区别只在 repo.url。
    /// </summary>
    public static string JarUrl(Artifact art, RepoEntry? repo)
    {
        var baseUrl = (repo?.Url ?? "http://repo.mumfrey.com/content/repositories/snapshots/")
            .TrimEnd('/');
        return $"{baseUrl}/com/mumfrey/liteloader/{art.Version}/{art.File}";
    }

    /// <summary>安装侧用：拿某个 MC 版本的 tweakClass、依赖库、jar 地址。</summary>
    public async Task<LiteLoaderArtifact?> FindAsync(
        string mcVersion, string? wantVersion, CancellationToken ct = default)
    {
        var root = await FetchAsync(ct).ConfigureAwait(false);
        var one = Pick(root, mcVersion);
        if (one is null) return null;

        var (art, repo) = one.Value;
        if (!string.IsNullOrEmpty(wantVersion) && art.Version != wantVersion) return null;

        return new LiteLoaderArtifact(
            art.Version,
            JarUrl(art, repo),
            art.TweakClass ?? "com.mumfrey.liteloader.launch.LiteLoaderTweaker",
            art.Libraries ?? []);
    }

    /// <summary>
    /// 依赖库没有给 url 时的兜底地址。
    ///
    /// launchwrapper 在 Mojang 自己的仓库上，asm-all 在 Maven Central 上 ——
    /// 元数据里这两个经常只有 name，没有 url，不兜底就会在启动时报
    /// "Class path entries reference missing files"。
    /// </summary>
    public static string FallbackUrlFor(string mavenName)
    {
        var group = mavenName.Split(':')[0];

        if (group.StartsWith("net.minecraft", StringComparison.OrdinalIgnoreCase)
            && !group.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
            return "https://libraries.minecraft.net/";

        return "https://repo1.maven.org/maven2/";
    }
}

/// <summary>LiteLoader 安装要用的一整套信息。</summary>
public sealed record LiteLoaderArtifact(
    string Version,
    string JarUrl,
    string TweakClass,
    List<LiteLoaderProvider.LibRef> Libraries);