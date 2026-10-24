using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclLike.Core.Minecraft.Loaders;

/// <summary>支持的 Mod 加载器类型。</summary>
public enum LoaderKind
{
    Vanilla,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
}

/// <summary>
/// 一个可安装的加载器版本（某个 loader 对某个 MC 版本的某个具体版本）。
/// </summary>
public sealed record LoaderVersion(
    LoaderKind Kind,
    string MinecraftVersion,
    string Version,          // loader 自身的版本，如 fabric 0.20.1 / forge 47.4.28
    bool IsStable,
    string? InstallerUrl = null,
    string? Homepage = null)
{
    /// <summary>装完之后在 versions/ 下生成的版本 id。各加载器命名规则不同。</summary>
    public string ResultVersionId => Kind switch
    {
        LoaderKind.Fabric    => $"fabric-loader-{Version}-{MinecraftVersion}",
        LoaderKind.Quilt     => $"quilt-loader-{Version}-{MinecraftVersion}",
        LoaderKind.Forge     => $"{MinecraftVersion}-forge-{Version}",
        LoaderKind.NeoForge  => $"neoforge-{Version}",
        _                    => MinecraftVersion,
    };

    public string DisplayName => Kind switch
    {
        LoaderKind.Fabric    => $"Fabric {Version}",
        LoaderKind.Quilt     => $"Quilt {Version}",
        LoaderKind.Forge     => $"Forge {Version}",
        LoaderKind.NeoForge  => $"NeoForge {Version}",
        _                    => "原版",
    };
}

/// <summary>
/// 加载器安装的元数据来源。每个加载器一个 provider，走各自的官方 API。
///
/// ## 为什么不用 installer jar 而自己拼 JSON
///
/// Forge/NeoForge 官方给的是 installer jar，跑它需要先有 Java 且它自己会去
/// 下载一堆东西、还会改启动器目录。Fabric 干脆直接提供 API 让你拿到
/// 现成的版本 JSON。
///
/// 这里统一策略：
///   - **Fabric / Quilt**：官方 API 直接返回最终 version JSON，直接落盘。
///   - **Forge / NeoForge**：从 maven 拿 installer jar，本地解出
///     install_profile.json + version.json，把 libraries 合并进自己的
///     version JSON（这正是 installer 内部做的事，只是不做 GUI 那套）。
/// </summary>
public interface ILoaderProvider
{
    LoaderKind Kind { get; }

    /// <summary>列出该 MC 版本下可用的 loader 版本（新到旧）。</summary>
    Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(string mcVersion, CancellationToken ct = default);

    /// <summary>取最新稳定版；没有稳定版时取最新快照版。</summary>
    Task<LoaderVersion?> GetLatestAsync(string mcVersion, CancellationToken ct = default);
}

/// <summary>所有加载器共用的 HTTP 客户端与 JSON 配置。</summary>
public static class LoaderHttp
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static HttpClient? _client;

    /// <summary>
    /// 全局 HttpClient。设了 <see cref="ProxyUrl"/> 就带上代理。
    ///
    /// NeoForge 的域名在部分网络下会被 DNS 污染（解析到 Facebook 的段），
    /// 用户需要有代理才能装——所以代理必须可配。
    /// </summary>
    public static HttpClient Client
    {
        get
        {
            if (_client is not null) return _client;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                AllowAutoRedirect = true,
            };

            if (!string.IsNullOrWhiteSpace(ProxyUrl))
            {
                handler.Proxy = new System.Net.WebProxy(ProxyUrl.Trim());
                handler.UseProxy = true;
            }

            _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("PclLike/0.2 (+https://github.com/pcllike)");
            return _client;
        }
    }

    /// <summary>代理地址，如 http://127.0.0.1:7897。为空则直连。</summary>
    public static string? ProxyUrl { get; set; }

    /// <summary>
    /// processor 依赖（installertools / jarsplitter / ForgeAutoRenamingTool /
    /// binarypatcher 等）所在的 maven 仓库。
    ///
    /// NeoForge 1.20.1 这批工具的坐标在 net.minecraftforge 组下，
    /// 由 NeoForge 的 maven 提供——实测可用。
    /// </summary>
    public static string MavenBase { get; set; } = "https://maven.neoforged.net/releases";

    /// <summary>改了代理地址后要让 Client 重建。</summary>
    public static void ResetClient()
    {
        _client?.Dispose();
        _client = null;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await Client.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var resp = await Client.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var text = await GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    /// <summary>
    /// 把网络异常翻译成人话。用户最常碰到的是"连不上"，
    /// 而连不上的原因九成是 DNS 污染 / 缺代理，得直接说清楚。
    /// </summary>
    public static string Explain(Exception ex, string what)
    {
        var msg = ex switch
        {
            TaskCanceledException => $"{what} 超时（网络太慢或被墙）",
            HttpRequestException hre => $"{what} 连接失败：{hre.Message}",
            JsonException => $"{what} 返回了无法解析的数据（可能被劫持或该版本不存在）",
            _ => $"{what} 失败：{ex.Message}",
        };

        if (msg.Contains("连接失败", StringComparison.Ordinal) ||
            msg.Contains("超时", StringComparison.Ordinal))
        {
            msg += Environment.NewLine +
                   "  提示：若目标站点被 DNS 污染，请在设置里填 HTTP 代理（如 http://127.0.0.1:7897）。";
        }

        return msg;
    }
}