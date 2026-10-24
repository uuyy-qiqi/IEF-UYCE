namespace PclLike.Core.Mods;

/// <summary>依赖问题的严重程度。</summary>
public enum ModIssueLevel
{
    /// <summary>只是提示，不影响启动。</summary>
    Info,

    /// <summary>可能出问题：版本不匹配、目标 MC 版本不符。</summary>
    Warning,

    /// <summary>几乎一定崩：缺依赖、依赖被禁用。</summary>
    Error,
}

/// <summary>一条依赖问题。</summary>
public sealed record ModIssue
{
    public required ModIssueLevel Level { get; init; }

    /// <summary>出问题的 Mod（要求方）。</summary>
    public required string SourceName { get; init; }

    public string? SourceId { get; init; }

    /// <summary>问题描述（中文，直接给用户看）。</summary>
    public required string Message { get; init; }

    /// <summary>涉及的目标 Mod id（缺哪个 / 哪个版本不对）。</summary>
    public string? TargetId { get; init; }

    /// <summary>要求的版本范围，界面上显示。</summary>
    public string? RequiredRange { get; init; }
}

/// <summary>
/// Mod 依赖与冲突分析。
///
/// 分析三类问题，按"真会崩"到"只是提醒"排序：
///
///   1. **缺依赖**（Error）—— A 要求 B，但 mods 里没有 B。启动时 FML 会直接报
///      `Mod A requires B which is missing`，游戏进不去。
///   2. **依赖被禁用**（Error）—— B 装了但后缀是 .disabled，等于没有。
///      这个最容易踩：用户手动禁用了前置库，然后抱怨主 Mod 崩。
///   3. **版本不匹配**（Warning）—— B 装了但版本范围不符。
///      有时实际能跑（约束写得宽），所以是警告不是错误。
///
/// 另外检查 MC 版本与加载器是否对得上（Warning）—— 装错平台的 Mod 最常见。
/// </summary>
public static class ModDependencyAnalyzer
{
    /// <summary>内置的"不需要 Mod 提供"的依赖 id。</summary>
    private static readonly HashSet<string> BuiltinIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "forge", "neoforge", "fml", "fabricloader", "fabric-api", "fabric",
        "quilt_loader", "quilted_fabric_api", "java", "cloth-config", "cloth_config",
        "architectury", "puzzleslib", "clothconfig",
    };

    /// <summary>
    /// Fabric API 的模块化子包前缀。
    /// Sodium / Lithium 这类优化 Mod 会在自己的 fabric.mod.json 里声明
    /// `fabric-renderer-api-v1` 之类的依赖 —— 这些不是独立 Mod，
    /// 而是由 Fabric API 或 Mod 自带的 JiJ 嵌套 jar 提供的。
    /// 把它们当成"缺依赖"会满屏误报，所以直接跳过。
    /// </summary>
    private static readonly string[] FabricApiModulePrefixes =
    [
        "fabric-", "fabric_", "quilt-", "quilt_",
    ];

    /// <summary>
    /// 判断一个依赖 id 是否由 Fabric API / JiJ 嵌套提供，不需要用户手动安装。
    /// </summary>
    private static bool IsFabricApiModule(string depId)
    {
        foreach (var prefix in FabricApiModulePrefixes)
        {
            if (!depId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            // `fabric-api` 本身在 BuiltinIds 里已处理；这里确认后面还有实际模块名
            if (depId.Length > prefix.Length) return true;
        }

        return false;
    }

    /// <summary>
    /// 已知会被其它 Mod 通过 JiJ（Jar-in-Jar）内嵌分发的库。
    /// 缺失时不算错误，只在没有上层 Mod 时才提示。
    /// </summary>
    private static readonly HashSet<string> JarInJarIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "sodium", "lithium", "ferritecore", "modernfix", "fabric-api",
    };

    /// <summary>
    /// 检查一个 Mod 的 jar 里是否内嵌了指定 id 的嵌套 jar。
    /// Sodium 等 Mod 把依赖塞进 META-INF/jars/ 下，靠这个判断。
    /// </summary>
    private static bool ProvidesViaJarInJar(string jarPath, string depId)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);

            foreach (var e in zip.Entries)
            {
                // JiJ 的固定位置
                if (!e.FullName.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.Contains(depId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // 读不了就当没有 —— 宁可多报也不能崩
        }

        return false;
    }

    /// <summary>
    /// 分析一组 Mod。返回所有问题，按严重程度降序。
    /// </summary>
    public static IReadOnlyList<ModIssue> Analyze(IReadOnlyList<ModFile> mods)
    {
        var issues = new List<ModIssue>();

        // 只按启用的 Mod 建索引 —— 禁用的等于不存在
        var byId = new Dictionary<string, ModFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
        {
            if (!m.Enabled || m.Info is null) continue;
            if (m.Info.Id.Length == 0) continue;
            byId.TryAdd(m.Info.Id, m);
        }

        // 禁用但存在的 Mod —— 用于区分"完全没装"和"装了但禁用了"
        var disabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
        {
            if (m.Enabled || m.Info is null || m.Info.Id.Length == 0) continue;
            disabledIds.Add(m.Info.Id);
        }

        foreach (var mod in mods)
        {
            if (!mod.Enabled || mod.Info is null) continue;

            foreach (var (depId, range) in mod.Info.Dependencies)
            {
                if (BuiltinIds.Contains(depId)) continue;

                // Fabric API 的模块化子包由 Fabric API 提供，不是独立 Mod
                if (IsFabricApiModule(depId)) continue;

                // 这个 Mod 自己内嵌了（JiJ），不用用户管
                if (ProvidesViaJarInJar(mod.Path, depId)) continue;

                if (byId.TryGetValue(depId, out var dep))
                {
                    // 装了，检查版本范围
                    if (!string.IsNullOrWhiteSpace(range) &&
                        !VersionRange.Matches(dep.Info?.Version ?? "", range, out var why))
                    {
                        issues.Add(new ModIssue
                        {
                            Level = ModIssueLevel.Warning,
                            SourceName = mod.DisplayName,
                            SourceId = mod.Info.Id,
                            TargetId = depId,
                            RequiredRange = range,
                            Message = $"依赖「{dep.DisplayName}」版本不符：要求 {range}，" +
                                      $"实际 {dep.Info?.Version ?? "未知"}（{why}）",
                        });
                    }
                }
                else if (disabledIds.Contains(depId))
                {
                    // 装了但禁用 —— 这个坑最常见
                    var disabledMod = mods.FirstOrDefault(m =>
                        !m.Enabled && string.Equals(m.Info?.Id, depId, StringComparison.OrdinalIgnoreCase));

                    issues.Add(new ModIssue
                    {
                        Level = ModIssueLevel.Error,
                        SourceName = mod.DisplayName,
                        SourceId = mod.Info.Id,
                        TargetId = depId,
                        RequiredRange = range,
                        Message = $"依赖「{depId}」已安装但被禁用，游戏启动会失败。" +
                                  $"请启用 {disabledMod?.DisplayName ?? depId}。",
                    });
                }
                else
                {
                    issues.Add(new ModIssue
                    {
                        Level = ModIssueLevel.Error,
                        SourceName = mod.DisplayName,
                        SourceId = mod.Info.Id,
                        TargetId = depId,
                        RequiredRange = range,
                        Message = $"缺少依赖「{depId}」{FormatRange(range)}，需要安装。",
                    });
                }
            }
        }

        // 重复的 Mod id（装了同一个 Mod 的两个版本）
        foreach (var g in mods.Where(m => m.Enabled && m.Info is { Id.Length: > 0 })
                              .GroupBy(m => m.Info!.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (g.Count() <= 1) continue;

            issues.Add(new ModIssue
            {
                Level = ModIssueLevel.Error,
                SourceName = g.First().DisplayName,
                SourceId = g.Key,
                TargetId = g.Key,
                Message = $"装了 {g.Count()} 个同 id（{g.Key}）的 Mod：" +
                          string.Join("、", g.Select(m => $"{m.FileName} {m.Info?.Version}")) +
                          "。请只保留一个。",
            });
        }

        return issues
            .OrderByDescending(i => i.Level)
            .ThenBy(i => i.SourceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string FormatRange(string range)
        => string.IsNullOrWhiteSpace(range) ? "" : $"（要求 {range}）";

    /// <summary>
    /// 检查所有 Mod 与目标 MC 版本/加载器是否兼容。
    /// 单独一个方法，因为它依赖实例配置，不是纯粹的 Mod 间关系。
    /// </summary>
    public static IReadOnlyList<ModIssue> CheckGameCompatibility(
        IReadOnlyList<ModFile> mods, string mcVersion, string loader)
    {
        var issues = new List<ModIssue>();
        if (string.IsNullOrWhiteSpace(mcVersion)) return issues;

        foreach (var mod in mods)
        {
            if (!mod.Enabled || mod.Info is null) continue;
            var info = mod.Info;

            // 加载器不匹配：Forge Mod 装进 Fabric 实例，必崩
            if (info.Loaders.Count > 0 && !string.IsNullOrWhiteSpace(loader))
            {
                var loaderMatches = info.Loaders.Any(l =>
                    l.Contains(loader, StringComparison.OrdinalIgnoreCase) ||
                    // NeoForge 兼容 Forge 的旧 Mod（1.20.1 时代）
                    (loader.Equals("neoforge", StringComparison.OrdinalIgnoreCase) &&
                     l.Equals("forge", StringComparison.OrdinalIgnoreCase)));

                if (!loaderMatches)
                {
                    issues.Add(new ModIssue
                    {
                        Level = ModIssueLevel.Error,
                        SourceName = mod.DisplayName,
                        SourceId = info.Id,
                        Message = $"这是 {string.Join("/", info.Loaders)} 平台的 Mod，" +
                                  $"当前实例是 {loader}，装错平台会启动失败。",
                    });
                    continue;
                }
            }

            // MC 版本：Fabric 的 depends.minecraft 是范围串，Forge 的通常为空
            if (info.McVersions.Count > 0)
            {
                var ok = info.McVersions.Any(v =>
                    v.Contains(mcVersion, StringComparison.OrdinalIgnoreCase));

                if (!ok)
                {
                    issues.Add(new ModIssue
                    {
                        Level = ModIssueLevel.Warning,
                        SourceName = mod.DisplayName,
                        SourceId = info.Id,
                        RequiredRange = string.Join(" ", info.McVersions),
                        Message = $"声明的 MC 版本是 {string.Join(" ", info.McVersions)}，" +
                                  $"当前实例是 {mcVersion}，可能不兼容。",
                    });
                }
            }
        }

        return issues.OrderByDescending(i => i.Level).ToList();
    }
}

/// <summary>
/// Maven 风格的版本范围匹配。
///
/// Mod 的版本约束有三大流派，全都得支持：
///
///   Maven       `[1.0,2.0)`  `[1.0,)`  `(,1.0]`  `[1.0]`      Forge / NeoForge 用
///   Fabric      `>=1.19 &lt;1.20`  `~1.19.2`  `^1.19`  `*`        Fabric 用
///   裸版本号     `1.0.0`                                    等于精确匹配
///
/// 写错的话表现是"明明版本对却报不匹配"，用户完全摸不着头脑，
/// 所以这里要尽量宽容：范围解析不出来时按"通过"处理，只记原因。
/// </summary>
public static class VersionRange
{
    /// <summary>
    /// 判断 <paramref name="version"/> 是否满足 <paramref name="range"/>。
    /// </summary>
    public static bool Matches(string version, string range, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(range)) return true;      // 无约束
        if (string.IsNullOrWhiteSpace(version)) { reason = "版本未知"; return true; }

        range = range.Trim();

        // 玩家/作者写的版本号五花八门，解析不出数字的一律放行：
        // 误报"不匹配"会把能用的 Mod 挡在门外，比漏报更伤。
        if (!LooksLikeVersion(version))
        {
            reason = $"版本号 {version} 无法解析，跳过比对";
            return true;
        }

        try
        {
            return range switch
            {
                "*" or "" => true,
                _ when range.StartsWith('[') || range.StartsWith('(') => MavenRange(version, range, out reason),
                // Fabric 的范围可能用空格分隔（`>=1.19 <1.20`），也可能用 `&`，
                // 还可能只是 `~1.21` / `^1.21` —— 顺序很重要，先把带比较符的挑出来。
                _ when range.StartsWith('~') => TildeRange(version, range, out reason),
                _ when range.StartsWith('^') => CaretRange(version, range, out reason),
                _ when range.Any(c => c is '>' or '<' or '=' or '&' or '|')
                    => FabricRange(version, range, out reason),
                _ => ExactMatch(version, range, out reason),
            };
        }
        catch (Exception ex)
        {
            // 解析不了就当通过 —— 误报"不匹配"比漏报更烦人
            reason = $"版本范围无法解析（{ex.GetType().Name}）";
            return true;
        }
    }

    /// <summary>版本号里至少得有一个数字，否则没法比大小。</summary>
    private static bool LooksLikeVersion(string s) => s.Any(char.IsDigit);

    /// <summary>Maven 区间：`[1.0,2.0)` `(,1.7]` `[1.20]`。</summary>
    private static bool MavenRange(string version, string range, out string reason)
    {
        reason = "";
        var openInclusive = range[0] == '[';
        var closeInclusive = range[^1] == ']';
        var inner = range[1..^1];

        var parts = inner.Split(',');
        var v = Parse(version);

        if (parts.Length == 1)
        {
            // [1.20] 精确
            reason = "";
            return Compare(v, Parse(parts[0])) == 0;
        }

        var lo = parts[0].Trim();
        var hi = parts[1].Trim();

        if (lo.Length > 0)
        {
            var c = Compare(v, Parse(lo));
            if (c < 0 || (c == 0 && !openInclusive))
            {
                reason = $"低于下界 {lo}";
                return false;
            }
        }

        if (hi.Length > 0)
        {
            var c = Compare(v, Parse(hi));
            // 上界形如 `1.20.1-` 表示含预发布，先剥掉
            if (c > 0 || (c == 0 && !closeInclusive))
            {
                reason = $"高于上界 {hi}";
                return false;
            }
        }

        return true;
    }

    /// <summary>Fabric 风格：`>=1.19 &lt;1.20` `>=1.19 | &lt;1.16`。</summary>
    private static bool FabricRange(string version, string range, out string reason)
    {
        reason = "";
        var v = Parse(version);

        // `|` 是"或"，任一段通过即可
        foreach (var alt in range.Split('|'))
        {
            // 空格和 `&` 都是"且" —— Fabric 规范写 `>=1.19 <1.20`（空格分隔），
            // 也有作者写 `>=1.19 & <1.20`，两种都要认。
            var clauses = alt.Split([' ', '&', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var allPass = true;
            var lastReason = "";

            foreach (var clause in clauses)
            {
                var (op, target) = SplitOperator(clause);
                if (target.Length == 0) continue;

                var c = Compare(v, Parse(target));
                var pass = op switch
                {
                    ">=" => c >= 0,
                    ">" => c > 0,
                    "<=" => c <= 0,
                    "<" => c < 0,
                    "=" or "==" => c == 0,
                    // 光秃秃一个版本号又带尖括号以外的内容：当作精确/前缀匹配
                    _ => ExactMatch(version, target, out _),
                };

                if (!pass)
                {
                    allPass = false;
                    lastReason = $"不满足 {op}{target}";
                    break;
                }
            }

            if (allPass) return true;
            reason = lastReason;
        }

        return false;
    }

    /// <summary>
    /// `~1.21` = 大于等于 1.21，且小于 1.22。
    /// `~1.21.1` = 大于等于 1.21.1，且小于 1.22.0。
    /// 规则：只允许最右边**写出来的**那一段变动。
    /// </summary>
    private static bool TildeRange(string version, string range, out string reason)
    {
        reason = "";
        var raw = range[1..].Trim();
        var target = Parse(raw);
        var v = Parse(version);

        if (Compare(v, target) < 0)
        {
            reason = $"低于 ~{raw}";
            return false;
        }

        // 关键：上界按"写了几段"决定进位位置。
        // 之前不分青红皂白地 Major+1，导致 `~1.20` 的上界变成 2.0.0，
        // 把 1.21.1 错误地判为满足。
        var written = CountSegments(raw);
        var upper = written switch
        {
            <= 1 => new Version(target.Major + 1, 0, 0, ""),      // ~1   → <2
            2 => new Version(target.Major, target.Minor + 1, 0, ""), // ~1.21 → <1.22
            _ => new Version(target.Major, target.Minor, target.Patch + 1, ""), // ~1.21.1 → <1.21.2
        };

        if (Compare(v, upper) >= 0)
        {
            reason = $"高于 ~{raw} 的上界 {upper.Major}.{upper.Minor}.{upper.Patch}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// `^1.21` = 大于等于 1.21，且小于 2.0.0（不跨主版本）。
    /// `^0.2.3` = 大于等于 0.2.3，且小于 0.3.0（0.x 特殊：不跨次版本）。
    /// </summary>
    private static bool CaretRange(string version, string range, out string reason)
    {
        reason = "";
        var raw = range[1..].Trim();
        var target = Parse(raw);
        var v = Parse(version);

        if (Compare(v, target) < 0)
        {
            reason = $"低于 ^{raw}";
            return false;
        }

        // 0.x 是开发版语义，次版本号变动即为破坏性变更，所以只允许 patch 动。
        var upper = target.Major > 0
            ? new Version(target.Major + 1, 0, 0, "")
            : target.Minor > 0
                ? new Version(0, target.Minor + 1, 0, "")
                : new Version(0, 0, target.Patch + 1, "");

        if (Compare(v, upper) >= 0)
        {
            reason = $"高于 ^{raw} 的上界 {upper.Major}.{upper.Minor}.{upper.Patch}";
            return false;
        }

        return true;
    }

    /// <summary>数一个版本号字符串写了几段数字（`1.21` → 2，`1.21.1` → 3）。</summary>
    private static int CountSegments(string s)
    {
        s = s.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        if (s.Length == 0) return 0;

        var n = 0;
        foreach (var part in s.Split('.'))
            if (part.Any(char.IsDigit)) n++;

        return n;
    }

    private static bool ExactMatch(string version, string range, out string reason)
    {
        reason = "";

        // 有时给定的是前缀（`1.20` 匹配 `1.20.1`）—— Mod 作者常这么写
        if (version.Equals(range, StringComparison.OrdinalIgnoreCase)) return true;

        if (version.StartsWith(range + ".", StringComparison.OrdinalIgnoreCase)) return true;
        if (version.StartsWith(range + "-", StringComparison.OrdinalIgnoreCase)) return true;

        reason = $"与 {range} 不等";
        return false;
    }

    private static (string Op, string Target) SplitOperator(string clause)
    {
        clause = clause.Trim();
        foreach (var op in new[] { ">=", "<=", "==", ">", "<", "=" })
        {
            if (clause.StartsWith(op, StringComparison.Ordinal))
                return (op == "==" ? "=" : op, clause[op.Length..].Trim());
        }
        return ("", clause);
    }

    /// <summary>
    /// 解析版本号。取前 3 个数字段，其余作为 qualifier。
    /// `1.20.1` → (1,20,1)   `1.20` → (1,20,0)   `1.20.1-beta` → (1,20,1,"beta")
    /// </summary>
    private static Version Parse(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return new Version(0, 0, 0, "");

        // 去掉前导 v
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        var qualifier = "";
        var dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            qualifier = s[(dashIdx + 1)..];
            s = s[..dashIdx];
        }

        var parts = s.Split('.');
        var nums = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            // "1a" 这种就取数字前缀
            var digits = new string(parts[i].TakeWhile(char.IsDigit).ToArray());
            nums[i] = int.TryParse(digits, out var n) ? n : 0;
        }

        return new Version(nums[0], nums[1], nums[2], qualifier);
    }

    /// <summary>
    /// 比较大小。带 qualifier 的视为预发布 → 小于同版本的正式版
    /// （`1.20.1-beta` &lt; `1.20.1`），符合语义化版本规则。
    /// </summary>
    private static int Compare(Version a, Version b)
    {
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
        if (a.Patch != b.Patch) return a.Patch.CompareTo(b.Patch);

        var aPre = a.Qualifier.Length > 0;
        var bPre = b.Qualifier.Length > 0;
        if (aPre != bPre) return aPre ? -1 : 1;

        return string.Compare(a.Qualifier, b.Qualifier, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Version(int Major, int Minor, int Patch, string Qualifier);
}