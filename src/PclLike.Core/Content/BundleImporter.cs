using System.IO.Compression;

namespace PclLike.Core.Content;

/// <summary>懒人包长什么样（只读一遍目录就能说出来）。</summary>
public sealed record BundleLayout(
    string ZipPath,
    string InnerRoot,
    string MinecraftPrefix,
    IReadOnlyList<string> VersionIds,
    string? PreferredVersionId,
    bool HasMods,
    bool HasLibraries,
    bool HasAssets,
    long UncompressedBytes,
    int EntryCount)
{
    /// <summary>解压后大概占多少（压缩包和解压后的差距可能很大，界面上得说清楚）。</summary>
    public string SizeText => UncompressedBytes >= 1024L * 1024 * 1024
        ? $"{UncompressedBytes / 1024.0 / 1024 / 1024:0.##} GB"
        : $"{UncompressedBytes / 1024.0 / 1024:0.#} MB";

    /// <summary>给用户看的一句话概括。</summary>
    public string SummaryText
    {
        get
        {
            var bits = new List<string> { $"{EntryCount} 个文件 / {SizeText}" };
            if (VersionIds.Count > 0) bits.Add($"自带版本 {string.Join("、", VersionIds.Take(4))}");
            if (HasMods) bits.Add("带 Mod");
            if (HasLibraries) bits.Add("带游戏库");
            return string.Join("，", bits);
        }
    }
}

/// <summary>导入结果。</summary>
public sealed record BundleImportResult(
    bool Ok,
    string Message,
    int Files,
    long Bytes,
    IReadOnlyList<string> VersionIds);

/// <summary>
/// 把「解压即玩」的整合包（一个 zip 里装着整个 .minecraft）导成一个实例。
///
/// 这件事和 <see cref="ModPackInstaller"/> 完全是两条路：ModPack 只有一份清单，
/// 东西要照单去网上下；懒人包是东西已经在包里了 —— 要做的只是把它摊开到
/// 磁盘上正确的位置。
///
/// 摊开时按启动器自己的目录规矩分流，而不是照搬包里的布局：
///  - versions/  → 公共 versions/（这样实例才有版本可挂）
///  - libraries/ → 公共 libraries/（省掉几百 MB 重复下载）
///  - assets/    → 公共 assets/
///  - 其它（mods/ config/ saves/ resourcepacks/ …）→ 实例自己的游戏目录
///
/// 为什么要分流：懒人包按"一个完整 .minecraft"打包，里面连游戏库和资源都带
/// 着。原样全塞进实例目录的话，一来同样一份库会在每个实例里再来一份，二来
/// 启动器读的是公共目录，塞在实例里的那些根本不会生效。
///
/// 公共目录里的同名文件**只补缺、不覆盖** —— 包里的库可能是旧的，覆盖会把
/// 已经装好的正常版本弄坏。
/// </summary>
public static class BundleImporter
{
    private const string McMarker = ".minecraft";

    public static BundleLayout Inspect(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var entries = zip.Entries
            .Select(x => x.FullName.Replace('\\', '/'))
            .Where(x => x.Length > 0 && !x.EndsWith('/'))
            .ToList();

        var prefix = FindMinecraftPrefix(entries);
        var innerRoot = prefix.EndsWith(McMarker + "/", StringComparison.OrdinalIgnoreCase)
            ? prefix[..^(McMarker.Length + 1)]
            : prefix;

        var versionIds = new List<string>();
        var hasMods = false;
        var hasLibraries = false;
        var hasAssets = false;
        long bytes = 0;
        var count = 0;

        foreach (var e in entries)
        {
            var rel = Slice(e, prefix);
            if (rel is null) continue;

            count++;
            bytes += SafeLength(zip, e);

            if (rel.StartsWith("versions/", StringComparison.OrdinalIgnoreCase))
            {
                var id = rel["versions/".Length..].Split('/')[0];
                if (id.Length > 0 && !versionIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                    versionIds.Add(id);
            }

            if (rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) hasMods = true;
            if (rel.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase)) hasLibraries = true;
            if (rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) hasAssets = true;
        }

        return new BundleLayout(zipPath, innerRoot, prefix, versionIds, PickVersion(versionIds),
            hasMods, hasLibraries, hasAssets, bytes, count);
    }

    /// <summary>
    /// 摊开到一个实例上。<paramref name="instance"/> 的游戏目录必须已经建好。
    /// </summary>
    public static async Task<BundleImportResult> ImportAsync(
        string zipPath,
        BundleLayout layout,
        string instanceGameDir,
        string gameRoot,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var versions = Path.Combine(gameRoot, "versions");
        var libraries = Path.Combine(gameRoot, "libraries");
        var assets = Path.Combine(gameRoot, "assets");

        try
        {
            var result = await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(zipPath);

                var files = 0;
                long bytes = 0;
                var skipped = 0;
                var lastReport = Environment.TickCount64;

                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    var name = entry.FullName.Replace('\\', '/');
                    if (name.Length == 0 || name.EndsWith('/')) continue;

                    var rel = Slice(name, layout.MinecraftPrefix);
                    if (rel is null) continue;

                    // zip slip：条目名里带 .. 或绝对路径，一律不碰
                    if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                    {
                        skipped++;
                        continue;
                    }

                    var dest = RouteTo(rel, versions, libraries, assets, instanceGameDir);

                    // 公共目录只补缺不覆盖；实例目录是新的，直接写
                    var isShared = dest.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase)
                                   && !dest.StartsWith(instanceGameDir, StringComparison.OrdinalIgnoreCase);

                    if (isShared && File.Exists(dest))
                    {
                        skipped++;
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                    using (var src = entry.Open())
                    using (var dst = File.Create(dest))
                    {
                        src.CopyTo(dst);
                        bytes += dst.Length;
                    }

                    files++;

                    if (Environment.TickCount64 - lastReport > 3000)
                    {
                        lastReport = Environment.TickCount64;
                        var total = layout.EntryCount;
                        var text = $"已解开 {files} / 约 {total} 个文件（{bytes / 1024.0 / 1024:0} MB）";
                        log?.Invoke(text);
                    }
                }

                log?.Invoke($"解包完成：{files} 个文件，共 {bytes / 1024.0 / 1024:0} MB"
                            + (skipped > 0 ? $"（公共目录里已存在的 {skipped} 个跳过）" : ""));
                return (files, bytes);
            }, ct).ConfigureAwait(false);

            // 版本 json 落地了才有版本可挂 —— 这里只报告事实，挂哪个由上层决定
            var usable = layout.VersionIds
                .Where(id => File.Exists(Path.Combine(versions, id, id + ".json")))
                .ToList();

            return new BundleImportResult(
                true,
                $"已解开 {result.files} 个文件（{result.bytes / 1024.0 / 1024:0} MB）",
                result.files, result.bytes, usable);
        }
        catch (OperationCanceledException)
        {
            return new BundleImportResult(false, "导入被取消", 0, 0, []);
        }
        catch (Exception ex)
        {
            return new BundleImportResult(false, $"导入失败：{ex.Message}", 0, 0, []);
        }
    }

    /// <summary>条目该落到哪儿。公共目录和实例目录的分界线就是这几条规则。</summary>
    private static string RouteTo(string rel, string versions, string libraries, string assets, string instanceGameDir)
    {
        if (rel.StartsWith("versions/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(versions, rel["versions/".Length..].Replace('/', Path.DirectorySeparatorChar));

        if (rel.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(libraries, rel["libraries/".Length..].Replace('/', Path.DirectorySeparatorChar));

        if (rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(assets, rel["assets/".Length..].Replace('/', Path.DirectorySeparatorChar));

        return Path.Combine(instanceGameDir, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// 找到 `.minecraft/` 那一层以前的前缀（含）。
    ///
    /// 取**最短**的那个：有些包里会有 `.minecraft/.minecraft/...` 这种
    /// 套娃（发布者打了两遍），拿最短的才对。
    /// </summary>
    private static string FindMinecraftPrefix(List<string> entries)
    {
        string? best = null;

        foreach (var e in entries)
        {
            var parts = e.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                if (!parts[i].Equals(McMarker, StringComparison.OrdinalIgnoreCase)) continue;

                var prefix = string.Join('/', parts.Take(i + 1)) + "/";
                if (best is null || prefix.Length < best.Length) best = prefix;
                break;
            }
        }

        if (best is not null) return best;

        // 没有 .minecraft 目录：把 versions/ 那一层的上级当成包根
        foreach (var e in entries)
        {
            var parts = e.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                if (!parts[i].Equals("versions", StringComparison.OrdinalIgnoreCase)) continue;
                return i == 0 ? "" : string.Join('/', parts.Take(i)) + "/";
            }
        }

        return "";
    }

    /// <summary>把条目名去掉前缀；不在前缀里的返回 null。</summary>
    private static string? Slice(string name, string prefix)
    {
        if (prefix.Length == 0) return name;
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : null;
    }

    private static long SafeLength(ZipArchive zip, string name)
    {
        try { return zip.GetEntry(name)?.Length ?? 0; }
        catch { return 0; }
    }

    /// <summary>
    /// 这么多版本里该挂哪个：优先带加载器的那个（懒人包的价值就在加载器和 Mod
    /// 上，挂回原版等于把 Mod 全废掉），没有加载器才退到原版。
    /// </summary>
    private static string? PickVersion(List<string> ids)
    {
        if (ids.Count == 0) return null;

        string[] loaderMarks = ["fabric-loader", "quilt-loader", "neoforge", "forge", "optifine", "liteloader"];

        foreach (var id in ids)
        {
            var lower = id.ToLowerInvariant();
            if (loaderMarks.Any(m => lower.Contains(m, StringComparison.Ordinal))) return id;
        }

        // 没有加载器：退回版本号最大的那个原版
        return ids
            .OrderByDescending(id => id, Comparer<string>.Create(CompareMcVersion))
            .First();
    }

    /// <summary>
    /// 把 1.21.1 / 1.21 / 1.20.6 这类版本号按数值比大小（比字符串靠谱：
    /// 字符串比大小会说 "1.9" 比 "1.21" 新）。
    /// </summary>
    public static int CompareMcVersion(string a, string b)
    {
        static int[] Parts(string s) => s.Split('.', '-', '_')
            .Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();

        var pa = Parts(a);
        var pb = Parts(b);

        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }

        return string.CompareOrdinal(a, b);
    }
}