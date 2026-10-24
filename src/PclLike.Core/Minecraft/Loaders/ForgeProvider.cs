using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PclLike.Core.Minecraft.Loaders;

/// <summary>
/// Forge / NeoForge 的共同逻辑。
///
/// ## installer jar 里有什么
///
/// 两个项目的 installer jar 结构基本一样：
///
/// ```
/// install_profile.json      ← 描述"要装哪些库、processor 怎么跑"
/// version.json              ← 可直接落盘的版本描述（较新版本才有）
/// maven/...                 ← 少数内嵌的库
/// ```
///
/// 我们**不跑 installer**（它要 GUI、要改启动器目录、还要跑 processor）。
/// 只做它做的核心一步：把 version.json 拿出来，把 install_profile.json 里
/// 声明的 libraries 合进去，落盘到 versions/&lt;id&gt;/&lt;id&gt;.json。
///
/// ## 关于 processors
///
/// 新版 Forge 的 install_profile 里有 processors 数组（要跑 Java 程序做
/// 二进制 patch）。**纯客户端启动不需要它们**——那些是给服务端打补丁用的。
/// 客户端只依赖 libraries 齐全，所以我们跳过 processors，只合并 libraries。
/// </summary>
public abstract class ForgeLikeProvider : ILoaderProvider
{
    public abstract LoaderKind Kind { get; }

    /// <summary>该加载器的 maven 根（release 仓库）。</summary>
    protected abstract string MavenBase { get; }

    /// <summary>installer 在 maven 里的 groupId 路径。</summary>
    protected abstract string InstallerGroupPath { get; }

    /// <summary>
    /// 把 MC 版本映射成加载器版本前缀。
    /// Forge：1.20.1 → 47.x；NeoForge：1.20.1 → 21.1.x。
    /// 两个项目规则完全不同，所以交给子类。
    /// </summary>
    protected abstract string? McToLoaderPrefix(string mcVersion);

    public abstract Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(string mcVersion, CancellationToken ct = default);

    public async Task<LoaderVersion?> GetLatestAsync(string mcVersion, CancellationToken ct = default)
    {
        var all = await ListVersionsAsync(mcVersion, ct).ConfigureAwait(false);
        return all.FirstOrDefault(v => v.IsStable) ?? all.FirstOrDefault();
    }

    public virtual string InstallerUrlFor(string loaderVersion)
        => $"{MavenBase}/{InstallerGroupPath}/{loaderVersion}/{InstallerArtifactId(loaderVersion)}-{loaderVersion}-installer.jar";

    protected abstract string InstallerArtifactId(string loaderVersion);

    /// <summary>下载 installer jar 并取出里面两个关键 JSON。</summary>
    protected async Task<(string? VersionJson, string? InstallProfileJson)> FetchInstallerPayloadAsync(
        string loaderVersion, CancellationToken ct)
    {
        var (vj, ip, _) = await FetchInstallerFullAsync(loaderVersion, ct).ConfigureAwait(false);
        return (vj, ip);
    }

    /// <summary>
    /// 同上，但额外返回 installer 的**原始字节**。
    ///
    /// processor 链需要它：占位符 {INSTALLER} 要指向 installer 文件本身，
    /// 且 data 表里的 /data/client.lzma 等条目只能从 jar 内部解出来。
    /// </summary>
    protected async Task<(string? VersionJson, string? InstallProfileJson, byte[] Bytes)>
        FetchInstallerFullAsync(string loaderVersion, CancellationToken ct)
    {
        var url = InstallerUrlFor(loaderVersion);

        byte[] bytes;
        try
        {
            bytes = await LoaderHttp.GetBytesAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                LoaderHttp.Explain(ex, $"下载 {Kind} {loaderVersion} 的 installer"), ex);
        }

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        string? versionJson = null;
        string? profileJson = null;

        foreach (var entry in zip.Entries)
        {
            // version.json 在根；install_profile.json 可能叫 install_profile.json
            if (entry.FullName.Equals("version.json", StringComparison.OrdinalIgnoreCase))
                versionJson = await ReadEntryAsync(entry, ct).ConfigureAwait(false);
            else if (entry.FullName.Equals("install_profile.json", StringComparison.OrdinalIgnoreCase))
                profileJson = await ReadEntryAsync(entry, ct).ConfigureAwait(false);
        }

        return (versionJson, profileJson, bytes);
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var s = entry.Open();
        using var sr = new StreamReader(s);
        return await sr.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 合并 install_profile 的 libraries 到 version.json，产出最终版本 JSON。
    ///
    /// version.json 里的 libraries 优先（它们带正确的 downloads.artifact），
    /// install_profile 里的补充进去——尤其 `"clientreq": true` 那批。
    /// </summary>
    protected static string MergeLibraries(string? versionJson, string? installProfileJson, string mcVersion, string resultId)
    {
        JsonObject root;

        if (!string.IsNullOrWhiteSpace(versionJson))
        {
            root = JsonNode.Parse(versionJson)?.AsObject()
                ?? throw new InvalidOperationException("installer 里的 version.json 不是合法 JSON");
        }
        else
        {
            // 老版本 Forge 没有 version.json，只能自己搭骨架
            root = new JsonObject
            {
                ["id"] = resultId,
                ["inheritsFrom"] = mcVersion,
                ["type"] = "release",
                ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            };
        }

        // id 与 inheritsFrom 必须对，否则 VersionResolver 找不到父版本
        root["id"] = resultId;
        root["inheritsFrom"] ??= mcVersion;

        var libs = root["libraries"] as JsonArray ?? [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var l in libs)
        {
            if (l?["name"]?.GetValue<string>() is { } n) seen.Add(NormalizeName(n));
        }

        if (!string.IsNullOrWhiteSpace(installProfileJson))
        {
            try
            {
                var prof = JsonNode.Parse(installProfileJson)?.AsObject();
                if (prof?["libraries"] is JsonArray plibs)
                {
                    foreach (var l in plibs)
                    {
                        if (l is not JsonObject lo) continue;

                        // ── 只挑客户端真正需要的库 ──
                        //
                        // install_profile.json 里绝大多数条目是 **installer 自己的工具链**
                        // （lzma-java / javaxdelta / opencsv / commons-beanutils …），
                        // 客户端启动时一个都用不上，塞进 classpath 只会拖慢启动、
                        // 还可能引入版本冲突。
                        //
                        // 实测 NeoForge 1.20.1 的 install_profile.json：
                        //   40 个 libraries，clientreq 字段**全部为空**，
                        //   而真正要的客户端依赖（fancymodloader 全套）在 version.json 的 34 条里。
                        //
                        // 所以判定规则是：
                        //   - 老版 Forge 用 "clientreq": true 标记 → 采纳
                        //   - 新版用 "side": "client" / 含 "CLIENT" 的 side → 采纳
                        //   - 其余一律跳过
                        //
                        // 曾经的 bug：这里无条件全量合并，导致最终 JSON 有 62 个库
                        // （version.json 的 34 + install_profile 的 28 条工具链污染）。
                        if (!IsClientLibrary(lo)) continue;

                        var name = lo["name"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!seen.Add(NormalizeName(name))) continue;

                        // install_profile 里的库常常只有 name，没有 downloads 段。
                        // 补一个指向 maven 的坐标，让 ClasspathBuilder 能拼出 URL。
                        if (lo["downloads"] is null)
                            lo["downloads"] = BuildMavenDownloads(name);

                        libs.Add(lo.DeepClone());
                    }
                }
            }
            catch (JsonException)
            {
                // install_profile 坏了不影响主流程——version.json 里的库够启动
            }
        }

        root["libraries"] = libs;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 判断 install_profile 里的一个库是否属于客户端。
    ///
    /// 两代标记方式都要认：
    ///   - 旧版 Forge（约 1.12~1.16）：`"clientreq": true`
    ///   - 新版：`"side"` 字段，值为 "client" 或不区分大小写含 "CLIENT"
    ///
    /// 都没有则视为服务端/工具链专用，跳过。
    /// </summary>
    private static bool IsClientLibrary(JsonObject lo)
    {
        if (lo["clientreq"] is JsonNode cr)
        {
            try { if (cr.GetValue<bool>()) return true; }
            catch { /* 非布尔值，忽略 */ }
        }

        if (lo["side"]?.GetValue<string>() is { } side)
        {
            if (side.Equals("client", StringComparison.OrdinalIgnoreCase)) return true;
            if (side.Contains("CLIENT", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>把 maven 坐标规范成 group:artifact 做去重键（忽略版本）。</summary>
    private static string NormalizeName(string mavenName)
    {
        var parts = mavenName.Split(':');
        return parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : mavenName;
    }

    /// <summary>为只有坐标的库补 downloads.artifact（指向 maven 标准路径）。</summary>
    private static JsonObject BuildMavenDownloads(string mavenName)
    {
        var parts = mavenName.Split(':');
        var group = parts.ElementAtOrDefault(0) ?? "";
        var artifact = parts.ElementAtOrDefault(1) ?? "";
        var version = parts.ElementAtOrDefault(2) ?? "";

        var rel = $"{group.Replace('.', '/')}/{artifact}/{version}/{artifact}-{version}.jar";

        return new JsonObject
        {
            ["artifact"] = new JsonObject
            {
                ["path"] = rel,
                ["url"] = $"https://maven.minecraftforge.net/{rel}",
            },
        };
    }

    /// <summary>解析形如 1.20.1-47.4.28 或 21.1.219 的版本号做排序。</summary>
    protected static IOrderedEnumerable<string> SortDescending(IEnumerable<string> versions)
        => versions.OrderByDescending(v => v, Comparer<string>.Create(CompareVersions));

    /// <summary>版本号比较：数字段逐段比，非数字段按字典序。</summary>
    public static int CompareVersions(string? a, string? b)
    {
        if (a == b) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        var pa = a.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        var pb = b.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            if (i >= pa.Length) return -1;
            if (i >= pb.Length) return 1;

            var na = int.TryParse(pa[i], out var va);
            var nb = int.TryParse(pb[i], out var vb);

            int cmp = na && nb ? va.CompareTo(vb)
                    : na ? 1      // 数字比文字"新"
                    : nb ? -1
                    : string.CompareOrdinal(pa[i], pb[i]);

            if (cmp != 0) return cmp;
        }

        return 0;
    }
}

/// <summary>
/// Forge。版本号形如 `1.20.1-47.4.28`（MC 版本 + "-" + forge 版本）。
///
///   promotions_slim.json  → {"promos": {"1.20.1-latest": "47.4.28", ...}}
///   maven-metadata.xml    → 全部版本，但 XML，解析麻烦
///
/// 用 promotions 拿 recommended/latest，用 maven 目录列举兜底。
/// </summary>
public sealed class ForgeProvider : ForgeLikeProvider
{
    public const string DefaultPromotions = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    private readonly string _promotionsUrl;

    public ForgeProvider(string? promotionsUrl = null)
        => _promotionsUrl = promotionsUrl ?? DefaultPromotions;

    public override LoaderKind Kind => LoaderKind.Forge;

    protected override string MavenBase => "https://maven.minecraftforge.net";

    protected override string InstallerGroupPath => "net/minecraftforge/forge";

    protected override string InstallerArtifactId(string loaderVersion) => "forge";

    /// <summary>
    /// Forge 的 maven 路径是 `1.20.1-47.4.28`，installer 就是
    /// `.../forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-installer.jar`。
    /// </summary>
    public override string InstallerUrlFor(string loaderVersion)
        => $"{MavenBase}/{InstallerGroupPath}/{loaderVersion}/forge-{loaderVersion}-installer.jar";

    protected override string? McToLoaderPrefix(string mcVersion) => mcVersion;

    private sealed class Promotions
    {
        [System.Text.Json.Serialization.JsonPropertyName("promos")]
        public Dictionary<string, string>? Promos { get; set; }
    }

    public override async Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        Promotions? promos;
        try
        {
            promos = await LoaderHttp.GetJsonAsync<Promotions>(_promotionsUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LoaderHttp.Explain(ex, "获取 Forge 版本列表"), ex);
        }

        var result = new List<LoaderVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var table = promos?.Promos ?? [];

        // recommended 优先，其次 latest
        foreach (var (suffix, stable) in new[] { ("-recommended", true), ("-latest", false) })
        {
            var key = $"{mcVersion}{suffix}";
            if (!table.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) continue;
            if (!seen.Add(v)) continue;

            result.Add(new LoaderVersion(LoaderKind.Forge, mcVersion, v, stable, InstallerUrlFor($"{mcVersion}-{v}"), "https://files.minecraftforge.net/"));
        }

        // promotions 里没有该 MC 版本 = 这个版本没有 Forge
        return result;
    }

    /// <summary>
    /// 拿 installer 并合成最终版本 JSON，并执行 processor 链。
    ///
    /// 与 NeoForgeProvider 同理：现代 Forge 的 srg / extra / client 三个
    /// 产物必须由 installer 内嵌的 Java 工具生成，maven 上没有。
    /// </summary>
    public async Task<string> BuildVersionJsonAsync(
        string mcVersion, string loaderVersion,
        string? gameRoot = null, string? javaExe = null,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        // loaderVersion 传的是纯 forge 版本（47.4.28），maven 目录要拼成 1.20.1-47.4.28
        var full = loaderVersion.Contains('-') ? loaderVersion : $"{mcVersion}-{loaderVersion}";
        var (vj, ip, bytes) = await FetchInstallerFullAsync(full, ct).ConfigureAwait(false);
        var resultId = $"{mcVersion}-forge-{loaderVersion}";
        var json = MergeLibraries(vj, ip, mcVersion, resultId);

        if (gameRoot is not null && javaExe is not null && !string.IsNullOrWhiteSpace(ip))
        {
            var mcJar = Path.Combine(gameRoot, "versions", mcVersion, $"{mcVersion}.jar");
            if (File.Exists(mcJar))
            {
                onLog?.Invoke("  ▸ 执行 installer processor 链（生成 SRG/patched 客户端 jar）");
                var steps = await ProcessorChain.RunAsync(
                    bytes, ip, gameRoot, "client", mcJar, mcVersion, javaExe,
                    onLog, ct).ConfigureAwait(false);

                onLog?.Invoke($"  ✓ processor 链完成：执行 {steps.Count(s => !s.Skipped)} 步，" +
                              $"跳过 {steps.Count(s => s.Skipped)} 步");
            }
            else
            {
                onLog?.Invoke($"  ⚠ 原版 client jar 不在（{mcJar}），跳过 processor 链");
            }
        }

        return json;
    }
}