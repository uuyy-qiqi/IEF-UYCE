namespace PclLike.Core.Net;

/// <summary>下载源。游戏文件（清单 / 版本 json / 客户端 jar / 库 / 资源）支持官方与国内镜像。</summary>
public enum MirrorKind
{
    /// <summary>Mojang 官方。</summary>
    Official,

    /// <summary>BMCLAPI（bangbang93 的公益镜像），国内实测快得多。</summary>
    Bmclapi,
}

/// <summary>
/// 把 Mojang 的下载地址换成镜像地址。
///
/// 实测（2026-10-26，本机，同一网络）：
///   30 个材质小文件 / 并发 8 —— 官方 30/30 成功、11.2 MB / 6.5 秒（≈1.8 MB/s）
///                               BMCLAPI 9/30 成功、3.9 MB / 8.2 秒（≈0.5 MB/s）
///   单个 22 MB 大文件 —— 官方 ≈6.7 MB/s，BMCLAPI ≈3.3 MB/s（都跑满了）
/// 结论：这条线路上**官方更快更可靠**，所以默认走官方，镜像只留给"官方被墙"。
/// 之前那个"BMCLAPI 快 6 倍"的说法来自一次 1 MB 小文件的测量，是小样本造成的
/// 误判，已经按上面的数据改掉。
///
/// 设计：只改**主机名与路径**，不动其他东西；校验仍然是 SHA1（Downloader 那层），
/// 所以镜像给错东西也进不了库 —— 最坏情况是下载失败而不是装坏。
/// </summary>
public static class MirrorSource
{
    public const string BmclapiBase = "https://bmclapi2.bangbang93.com";

    /// <summary>当前下载源。由设置驱动，进程内全局生效。默认官方（实测更快）。</summary>
    public static MirrorKind Current { get; set; } = MirrorKind.Official;

    /// <summary>设置里存的是字符串，宽容解析。认不出来一律走官方 —— 宁慢不坏。</summary>
    public static MirrorKind Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "bmclapi" or "bmcl" or "镜像" => MirrorKind.Bmclapi,
            _ => MirrorKind.Official,
        };

    public static string ToConfigValue(MirrorKind kind) =>
        kind == MirrorKind.Official ? "official" : "bmclapi";

    public static string DisplayName(MirrorKind kind) =>
        kind == MirrorKind.Official ? "官方（Mojang）" : "BMCLAPI（国内加速）";

    /// <summary>
    /// 改地址。认不出来的地址原样返回 —— 第三方（maven.fabricmc.net、forgecdn
    /// 之类）不在镜像范围内，不要乱改。
    /// </summary>
    public static string Rewrite(string url)
    {
        if (Current == MirrorKind.Official) return url;
        if (string.IsNullOrWhiteSpace(url)) return url;

        // 版本清单
        if (url.Contains("piston-meta.mojang.com/mc/game/version_manifest", StringComparison.OrdinalIgnoreCase)
            || url.Contains("launchermeta.mojang.com/mc/game/version_manifest", StringComparison.OrdinalIgnoreCase))
        {
            return $"{BmclapiBase}/mc/game/version_manifest_v2.json";
        }

        // 某个版本的 json：官方是 /v1/packages/<sha1>/<id>.json，镜像按 id 取
        var pkg = IndexOf(url, "piston-meta.mojang.com/v1/packages/");
        if (pkg >= 0)
        {
            var tail = url[(pkg + "piston-meta.mojang.com/v1/packages/".Length)..];
            var parts = tail.Split('/');
            if (parts.Length >= 2)
            {
                var id = parts[^1];
                if (id.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) id = id[..^5];
                return $"{BmclapiBase}/version/{id}/json";
            }
        }

        // 资源文件：resources.download.minecraft.net/<前两位>/<sha1> → /assets/<前两位>/<sha1>
        var res = IndexOf(url, "resources.download.minecraft.net/");
        if (res >= 0)
        {
            return BmclapiBase + "/assets/" + url[(res + "resources.download.minecraft.net/".Length)..];
        }

        // 类库：libraries.minecraft.net/<路径> → /maven/<路径>
        var lib = IndexOf(url, "libraries.minecraft.net/");
        if (lib >= 0)
        {
            return BmclapiBase + "/maven/" + url[(lib + "libraries.minecraft.net/".Length)..];
        }

        // 客户端 jar / 映射 / 服务端：这三个在官方是 /v1/objects/<sha1>/<名字>，
        // 镜像要的是版本 id —— 没 id 就改不了，交给 ClientJarUrl 那个带 id 的入口。
        // 原样返回，宁慢不坏。
        return url;
    }

    /// <summary>
    /// 客户端 jar（以及 mappings / server）的镜像地址。这几个官方是
    /// <c>piston-data.mojang.com/v1/objects/&lt;sha1&gt;/client.jar</c>，靠 sha1 反查不出版本 id，
    /// 所以必须由调用方把版本 id 传进来。
    /// </summary>
    public static string ClientJarUrl(string officialUrl, string versionId, string kind = "client")
    {
        if (Current == MirrorKind.Official) return officialUrl;
        if (string.IsNullOrWhiteSpace(versionId)) return officialUrl;

        return $"{BmclapiBase}/version/{versionId}/{kind}";
    }

    private static int IndexOf(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
}