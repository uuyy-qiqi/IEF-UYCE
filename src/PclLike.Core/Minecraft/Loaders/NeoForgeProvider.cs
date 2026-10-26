using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PclLike.Core.Minecraft.Loaders;

/// <summary>
/// NeoForge。
///
/// ## 版本号映射（跟 Forge 完全不同）
///
/// NeoForge 版本号的前两段 = MC 版本去掉开头的 "1."：
///   1.21.1 → 21.1.x
///   1.21   → 21.0.x   ← minor 后无 patch 时补 0
///   1.21.4 → 21.4.x
///   1.20.4 → 20.4.x
///
/// 注意：NeoForge 从 MC 1.20.1 才开始发布，更早的 MC 没有对应版本。
/// 详见 <see cref="McToLoaderPrefix"/> 里记录的踩坑说明。
///
/// ## 网络
///
/// maven.neoforged.net 在部分网络下会被 DNS 污染（解析到 face:b00c 段），
/// 必须走代理。所以这里对网络失败给明确提示，而不是吞掉。
/// </summary>
public sealed class NeoForgeProvider : ForgeLikeProvider
{
    public const string DefaultMaven = "https://maven.neoforged.net/releases";

    /// <summary>
    /// NeoForge 的 maven 有**两套不同的 URL 结构**，容易搞混：
    ///
    ///   文件下载:  {MavenBase}/{groupPath}/{ver}/{artifact}-{ver}-installer.jar
    ///              → https://maven.neoforged.net/**releases**/net/neoforged/neoforge/...
    ///
    ///   版本列表:  {ApiBase}/api/maven/versions/releases/{groupPath}
    ///              → https://maven.neoforged.net/**api/maven/versions/releases**/net/neoforged/neoforge
    ///
    /// 注意版本列表那条**不能**写成 {MavenBase}/api/...，
    /// 那样会拼出 .../releases/api/maven/... 直接 404（实测过，就是这么踩的）。
    /// 所以 api 根单独维护一个常量。
    /// </summary>
    public const string DefaultApiBase = "https://maven.neoforged.net";

    private readonly string _mavenBase;
    private readonly string _apiBase;

    public NeoForgeProvider(string? mavenBase = null)
    {
        _mavenBase = (mavenBase ?? DefaultMaven).TrimEnd('/');

        // mavenBase 传的是 ".../releases" 这种文件根，api 根要退回域名一级。
        // 允许调用方直接传域名根进来。
        _apiBase = (_mavenBase.EndsWith("/releases", StringComparison.OrdinalIgnoreCase)
            ? _mavenBase[..^"/releases".Length]
            : _mavenBase).TrimEnd('/');
    }

    public override LoaderKind Kind => LoaderKind.NeoForge;

    protected override string MavenBase => _mavenBase;

    protected override string InstallerGroupPath => "net/neoforged/neoforge";

    protected override string InstallerArtifactId(string loaderVersion) => "neoforge";

    /// <summary>NeoForge 的 maven 路径就是它自己的版本号（21.1.219）。</summary>
    /// <remarks>
    /// ⚠️ 这个方法**必须**能处理 1.20.1 的 legacy 坐标，因为基类的
    /// <see cref="ForgeLikeProvider.FetchInstallerPayloadAsync"/> 是通过它来下载的，
    /// 而 <see cref="LoaderVersion.InstallerUrl"/> 只用于界面展示、不参与下载。
    ///
    /// 曾经的 bug：这个方法只按 `neoforge-{ver}` 拼，于是 1.20.1 的
    /// 短版本号 "47.1.106" 被拼成
    /// `.../net/neoforged/neoforge/47.1.106/neoforge-47.1.106-installer.jar` → 404。
    /// 真实地址在 net/neoforged/forge 坐标下、且带 MC 前缀：
    /// `.../net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar`
    /// </remarks>
    public override string InstallerUrlFor(string loaderVersion)
    {
        // legacy: 传入的可能是短版本 "47.1.106"，也可能是完整 "1.20.1-47.1.106"
        if (LegacyForgeCoordinates.Contains(_pendingMcVersion ?? ""))
        {
            var full = loaderVersion.StartsWith(_pendingMcVersion + "-", StringComparison.OrdinalIgnoreCase)
                ? loaderVersion
                : $"{_pendingMcVersion}-{loaderVersion}";

            return $"{MavenBase}/net/neoforged/forge/{full}/forge-{full}-installer.jar";
        }

        return $"{MavenBase}/{InstallerGroupPath}/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
    }

    /// <summary>
    /// 当前正在处理的 MC 版本。因为 <see cref="ILoaderProvider"/> 的接口签名
    /// 只带 loaderVersion（不带 mcVersion），而 1.20.1 的坐标选择又依赖 MC 版本，
    /// 所以在 ListVersionsAsync 里记下来供 InstallerUrlFor 使用。
    ///
    /// 这是无奈之举：真要干净就得改 ILoaderProvider 的签名，
    /// 但那会波及 Fabric/Forge 两个 provider，暂时用这个字段过渡。
    /// </summary>
    private string? _pendingMcVersion;

    /// <summary>
    /// MC 版本 → NeoForge 版本前缀。
    ///
    /// ## 规律（已用 install_profile.json 实测校正）
    ///
    /// NeoForge 版本号的**前两段 = MC 版本去掉开头的 "1."**：
    ///
    ///   MC 1.21.1  →  21.1.x     实测 neoforge-21.1.264 的 minecraft=1.21.1
    ///   MC 1.21    →  21.0.x     实测 neoforge-21.0.167 的 minecraft=1.21
    ///   MC 1.21.4  →  21.4.x     实测 neoforge-21.4.161 的 minecraft=1.21.4
    ///   MC 1.20.4  →  20.4.x     245 个版本
    ///   MC 1.20.1  →  20.1.x
    ///
    /// ## 曾经踩过的坑（务必不要再犯）
    ///
    /// 我最初写成 `"21." + MC 的 patch`，把 1.20.1 映射到 21.1.x。
    /// 因为当时只凭"21.1 有 259 个版本、看着像主力线"就下了结论，
    /// 而没有去读 install_profile.json 里那个白纸黑字的 "minecraft" 字段。
    /// 实际上 21.1 是 **MC 1.21.1**，跟 1.20.1 毫无关系。
    /// 同一条错误规则还把 1.19.2 拼成 21.2.x、1.7.10 拼成 21.10.x ——
    /// 这两个纯粹是凭空的版本号，maven 里对应的 jar 全 404。
    ///
    /// 教训：**版本映射必须校验，不能靠版本号"长得像"来推断。**
    /// 下面用 SupportedMcVersions 白名单兜底，不在名单里的一律返回 null，
    /// 宁可让上层报"NeoForge 不支持该 MC 版本"，也不给一个假前缀。
    /// </summary>
    protected override string? McToLoaderPrefix(string mcVersion)
    {
        if (!SupportedMcVersions.Contains(mcVersion)) return null;

        var parts = mcVersion.Split('.');
        if (parts.Length < 2 || parts[0] != "1") return null;
        if (!int.TryParse(parts[1], out var minor)) return null;

        // patch 缺省为 0：MC 1.21 → "21.0"
        var patch = parts.Length >= 3 && int.TryParse(parts[2], out var p) ? p : 0;

        return $"{minor}.{patch}.";
    }

    /// <summary>
    /// NeoForge 实际支持的 MC 版本（白名单）。
    ///
    /// 从 maven 的版本线反推，并用 installer 的 install_profile.json 抽样核实。
    /// NeoForge 从 MC 1.20.1 开始才有，之前的版本（1.19.2 / 1.7.10 等）一律不存在。
    ///
    /// 注意 1.20.1 是特例：那时 NeoForge 还挂在 **net/neoforged/forge** 坐标下，
    /// 版本号沿用 Forge 的 47.x（如 `1.20.1-47.1.106`），
    /// 而 net/neoforged/neoforge 坐标**最早只有 20.2.3-beta**。
    /// 所以 1.20.1 走 LegacyForgeCoordinates 那条分支。
    ///
    /// 维护方式：NeoForge 发新 MC 线时往这里加一行即可，比改公式安全得多。
    /// </summary>
    private static readonly HashSet<string> SupportedMcVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        "1.20.1", "1.20.2", "1.20.3", "1.20.4", "1.20.5", "1.20.6",
        "1.21", "1.21.1", "1.21.2", "1.21.3", "1.21.4", "1.21.5",
        "1.21.6", "1.21.7", "1.21.8", "1.21.9", "1.21.10", "1.21.11",
    };

    /// <summary>
    /// 这些 MC 版本的 NeoForge 在 net/neoforged/forge 坐标下，
    /// 版本号形如 "1.20.1-47.1.106"（前缀带 MC 版本）。
    /// </summary>
    private static readonly HashSet<string> LegacyForgeCoordinates = new(StringComparer.OrdinalIgnoreCase)
    {
        "1.20.1",
    };

    private sealed class MavenVersions
    {
        [JsonPropertyName("versions")] public List<string>? Versions { get; set; }
    }

    public override async Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        _pendingMcVersion = mcVersion;   // 供 InstallerUrlFor 判断坐标

        if (!SupportedMcVersions.Contains(mcVersion))
            return [];

        // 1.20.1 特例：坐标与版本号规则都不同
        if (LegacyForgeCoordinates.Contains(mcVersion))
            return await ListLegacyForgeVersionsAsync(mcVersion, ct).ConfigureAwait(false);

        var prefix = McToLoaderPrefix(mcVersion);
        if (prefix is null)
            return [];

        var versions = await FetchVersionListAsync(InstallerGroupPath, mcVersion, prefix, ct).ConfigureAwait(false);
        if (versions.Count == 0) return [];

        var matched = versions
            .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(v => !v.Contains("-beta", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))
            .ToList();

        // NeoForge 没有正式的 "stable" 标记，把最新的当稳定
        var result = new List<LoaderVersion>();
        for (var i = 0; i < matched.Count; i++)
        {
            result.Add(new LoaderVersion(
                LoaderKind.NeoForge,
                mcVersion,
                matched[i],
                IsStable: i == 0,           // 只有最新的算"推荐"
                InstallerUrl: InstallerUrlFor(matched[i]),
                Homepage: "https://neoforged.net/")
            {
                // NeoForge 的 maven 元数据里没有 stable / recommended 这种标记，
                // 上面那个 IsStable 只是"是不是最新"。告诉界面这一点，
                // 免得它把 47.1.105 这种正经发布版写成"测试版"。
                StabilityKnown = false,
            });
        }

        return result;
    }

    /// <summary>
    /// MC 1.20.1 的 NeoForge 走 net/neoforged/forge 坐标，
    /// 版本号是 "1.20.1-47.1.106" 这种带 MC 前缀的形式。
    /// </summary>
    private async Task<IReadOnlyList<LoaderVersion>> ListLegacyForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var prefix = mcVersion + "-";
        const string legacyGroup = "net/neoforged/forge";

        var versions = await FetchVersionListAsync(legacyGroup, mcVersion, prefix, ct).ConfigureAwait(false);

        var matched = versions
            .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(v => !v.Contains("-beta", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))
            .ToList();

        var result = new List<LoaderVersion>();
        for (var i = 0; i < matched.Count; i++)
        {
            // 版本号对外只暴露 "47.1.106" 这段（对应 LoaderVersion.Version），
            // 但 installer URL 必须用完整坐标 "1.20.1-47.1.106"。
            var full = matched[i];
            var shortVer = full[prefix.Length..];

            result.Add(new LoaderVersion(
                LoaderKind.NeoForge,
                mcVersion,
                shortVer,
                IsStable: i == 0,
                InstallerUrl: $"{MavenBase}/{legacyGroup}/{full}/forge-{full}-installer.jar",
                Homepage: "https://neoforged.net/")
            {
                StabilityKnown = false,   // 同上：这里只有"是不是最新"，没有稳定/测试之分
            });
        }

        return result;
    }

    /// <summary>拉某个 group 下的全部版本号，失败时给带代理提示的异常。</summary>
    private async Task<List<string>> FetchVersionListAsync(string groupPath, string mcVersion, string prefix, CancellationToken ct)
    {
        // maven 的 versions API 比 metadata.xml 好解析得多。
        // 注意用 _apiBase 而不是 _mavenBase —— 见 DefaultApiBase 的说明。
        var url = $"{_apiBase}/api/maven/versions/releases/{groupPath}";

        Exception? lastError = null;

        // 官方 maven 的 versions API。它不通是国内常态（见 LoaderHttp.MirrorBase 的说明），
        // 所以不再一失败就抛 —— 先换镜像。
        if (!LoaderHttp.NeoForgeHostDead)
        {
            try
            {
                var mv = await LoaderHttp.GetJsonAsync<MavenVersions>(url, ct).ConfigureAwait(false);
                if (mv?.Versions is { Count: > 0 }) return mv.Versions;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        // 镜像：BMCLAPI 把同一棵 maven 树放在 /maven/ 下面，metadata.xml 里就是版本号。
        // 用它而不是 /neoforge/list/{mc}，是因为它对两个 group（neoforge 与 1.20.1 的
        // net/neoforged/forge）都适用，不用按坐标写两套解析。
        try
        {
            var xml = await LoaderHttp
                .GetStringAsync($"{LoaderHttp.MirrorBase}/{groupPath}/maven-metadata.xml", ct)
                .ConfigureAwait(false);

            var list = System.Text.RegularExpressions.Regex
                .Matches(xml, @"<version>([^<]+)</version>")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (list.Count > 0) return list;

            lastError ??= new InvalidOperationException("镜像的 maven-metadata.xml 里一个版本号都没有");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lastError = ex;
        }

        throw new InvalidOperationException(
            LoaderHttp.Explain(lastError!, $"获取 NeoForge 版本列表（{mcVersion} → {prefix}x）") +
            Environment.NewLine +
            $"  NeoForge 仓库：{MavenBase}" + Environment.NewLine +
            $"  镜像仓库：{LoaderHttp.MirrorBase}", lastError);
    }

    /// <summary>
    /// 生成 NeoForge 的最终版本 JSON，并**执行 installer 的 processor 链**。
    ///
    /// ## 为什么必须跑 processor 链
    ///
    /// installer 里的 version.json 会在 libraries 里声明这样三个条目：
    ///
    ///   net.minecraft:client:1.20.1-….114412:srg
    ///   net.minecraft:client:1.20.1-….114412:extra
    ///   net.neoforged:forge:1.20.1-47.1.106:client
    ///
    /// 它们**在 maven 上并不存在**，必须由 installer 自带的 Java 工具现场做出来：
    ///
    ///   client.jar ──jarsplitter──▶ slim.jar + extra.jar
    ///   slim.jar   ──ForgeAutoRenamingTool──▶ srg.jar
    ///   srg.jar    ──binarypatcher(+client.lzma)──▶ forge-…-client.jar
    ///
    /// 早期实现只合并 libraries、不跑 processor，注释里断言"客户端只依赖
    /// libraries 齐全"。实测该断言错误：缺了上述产物，FML 在
    /// `MinecraftLocator.scanMods` 会抛
    /// `Invalid paths argument, contained no existing paths`。
    /// </summary>
    /// <param name="gameRoot">游戏根目录（.minecraft 等价物），processor 的 {ROOT}</param>
    /// <param name="javaExe">跑 processor 用的 Java（Forge 系工具需要 Java 8+）</param>
    /// <param name="onLog">日志回调</param>
    /// <remarks>
    /// 用 <c>new</c> 隐藏基类那份：NeoForge 的 maven 布局、installer 文件名和
    /// data 表占位符都跟 Forge 不一样，实测下来独立实现更好维护。
    /// （基类那份是给 Forge / Cleanroom 共用的。）
    /// </remarks>
    public new async Task<string> BuildVersionJsonAsync(
        string mcVersion, string loaderVersion,
        string? gameRoot = null, string? javaExe = null,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        _pendingMcVersion = mcVersion;   // 下载 installer 前也要设好，否则 1.20.1 会走错坐标

        var (vj, ip, bytes) = await FetchInstallerFullAsync(loaderVersion, ct).ConfigureAwait(false);
        var resultId = $"neoforge-{loaderVersion}";
        var json = MergeLibraries(vj, ip, mcVersion, resultId);

        // ── 跑 processor 链（只有拿到 root 和 java 时才行）──
        if (gameRoot is not null && javaExe is not null && !string.IsNullOrWhiteSpace(ip))
        {
            onLog?.Invoke("  ▸ 执行 installer processor 链（生成 SRG/patched 客户端 jar）");

            // 原版 client jar：processor 的 {MINECRAFT_JAR}
            var mcJar = Path.Combine(gameRoot, "versions", mcVersion, $"{mcVersion}.jar");
            if (!File.Exists(mcJar))
            {
                throw new InvalidOperationException(
                    $"processor 链需要原版客户端 jar，但没找到：{mcJar}" + Environment.NewLine +
                    "  安装加载器前必须先装好原版。");
            }

            var steps = await ProcessorChain.RunAsync(
                bytes, ip, gameRoot, "client", mcJar, mcVersion, javaExe,
                onLog, ct).ConfigureAwait(false);

            var ran = steps.Count(s => !s.Skipped);
            var skipped = steps.Count(s => s.Skipped);
            onLog?.Invoke($"  ✓ processor 链完成：执行 {ran} 步，跳过 {skipped} 步");
        }
        else if (gameRoot is null || javaExe is null)
        {
            onLog?.Invoke("  ⚠ 未提供 gameRoot/javaExe，跳过 processor 链" +
                          "——启动时可能报 Invalid paths argument");
        }

        return json;
    }
}