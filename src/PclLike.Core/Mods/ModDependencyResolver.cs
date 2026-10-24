namespace PclLike.Core.Mods;

/// <summary>一次自动补依赖的结果。</summary>
public sealed record ResolveReport
{
    /// <summary>成功装上的 Mod 名列表。</summary>
    public List<string> Installed { get; init; } = [];

    /// <summary>已经在 mods 里、不用再下的依赖。</summary>
    public List<string> AlreadyPresent { get; init; } = [];

    /// <summary>库里找不到的依赖 id（用户得手动去下）。</summary>
    public List<string> NotFound { get; init; } = [];

    /// <summary>找到了但下载失败的。</summary>
    public List<string> Failed { get; init; } = [];

    /// <summary>为补依赖顺带装上的二级依赖。</summary>
    public List<string> Transitive { get; init; } = [];

    /// <summary>给用户看的一句话总结。</summary>
    public string Summary
    {
        get
        {
            if (Installed.Count == 0 && NotFound.Count == 0 && Failed.Count == 0)
                return "所有依赖都已满足，无需补齐。";

            var parts = new List<string>();
            if (Installed.Count > 0) parts.Add($"补齐 {Installed.Count} 个：{string.Join("、", Installed)}");
            if (AlreadyPresent.Count > 0) parts.Add($"{AlreadyPresent.Count} 个已存在");
            if (NotFound.Count > 0) parts.Add($"{NotFound.Count} 个在库里找不到：{string.Join("、", NotFound)}");
            if (Failed.Count > 0) parts.Add($"{Failed.Count} 个下载失败：{string.Join("、", Failed)}");

            var tail = NotFound.Count > 0 || Failed.Count > 0
                ? "  这几项需要手动去下载（PCL2 也经常碰不到，属于正常情况）。"
                : "";

            return string.Join("；", parts) + tail;
        }
    }
}

/// <summary>
/// 自动补齐依赖。
///
/// 流程：扫描当前 mods → 分析出缺失依赖 → 逐个去平台查 → 下载 → 重扫分析，
/// 最多循环 <see cref="MaxPasses"/> 轮。为什么要循环：
/// 下回来的 Mod 自己也有依赖（cloth-config 依赖 architectury 之类），
/// 一轮只能补一层，循环能把链条走完。
///
/// 防死循环：每轮只处理**新出现**的缺失 id（`handled` 集合记录处理过的），
/// 且总下载数有上限 —— 某些 Mod 的依赖环会互相引用，不设限就下不完。
/// </summary>
public sealed class ModDependencyResolver
{
    private const int MaxPasses = 4;
    private const int MaxDownloadsTotal = 40;

    private readonly ModManager _manager;
    private readonly ModDownloader _downloader;

    public ModDependencyResolver(ModManager manager, ModDownloader downloader)
    {
        _manager = manager;
        _downloader = downloader;
    }

    /// <summary>
    /// 补全缺失依赖。全程不抛异常 —— 失败项记录在报告里。
    /// </summary>
    public async Task<ResolveReport> ResolveAsync(
        string mcVersion,
        string loader,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var report = new ResolveReport();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloadCount = 0;

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();

            status?.Report(pass == 0 ? "正在扫描 Mod…" : $"第 {pass + 1} 轮检查依赖…");
            var scan = _manager.Scan();
            if (!scan.DirectoryExists) break;

            var issues = ModDependencyAnalyzer.Analyze(scan.Mods);

            // 只处理"缺依赖"这一类；版本不符和被禁用的不自动处理
            // （版本不符得用户自己决定换哪个版本，禁用是用户的明确意图）
            var missing = issues
                .Where(i => i.Level == ModIssueLevel.Error)
                .Where(i => i.Message.StartsWith("缺少依赖", StringComparison.Ordinal))
                .Select(i => i.TargetId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => !handled.Contains(id!))
                .ToList();

            if (missing.Count == 0) break;

            var anyInstalledThisPass = false;

            foreach (var depId in missing)
            {
                ct.ThrowIfCancellationRequested();

                handled.Add(depId!);

                if (downloadCount >= MaxDownloadsTotal)
                {
                    report.NotFound.Add($"{depId}（已达自动下载上限 {MaxDownloadsTotal} 个）");
                    continue;
                }

                status?.Report($"正在查找依赖 {depId}…");

                var candidate = await _downloader.FindModrinthAsync(depId!, mcVersion, loader, ct)
                             ?? await _downloader.FindCurseForgeAsync(depId!, mcVersion, loader, ct);

                if (candidate is null)
                {
                    report.NotFound.Add(depId!);
                    continue;
                }

                // 已经装过同 id 的不同版本就不重复下
                var existing = _manager.Scan(parseMetadata: true).Mods
                    .FirstOrDefault(m => string.Equals(m.Info?.Id, depId, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    report.AlreadyPresent.Add(depId!);
                    continue;
                }

                status?.Report($"正在下载 {candidate.Name} {candidate.Version}…");

                var target = Path.Combine(_manager.ModsDirectory, candidate.FileName);

                if (await _downloader.DownloadAsync(candidate, target, null, ct))
                {
                    downloadCount++;
                    anyInstalledThisPass = true;

                    // 第一轮补的算"直接依赖"，后续轮次是传递依赖
                    if (pass == 0) report.Installed.Add($"{candidate.Name} {candidate.Version}");
                    else report.Transitive.Add($"{candidate.Name} {candidate.Version}");
                }
                else
                {
                    report.Failed.Add($"{candidate.Name}（{candidate.FileName}）");
                }
            }

            // 这一轮一个都没装成 → 再循环也没用，退出
            if (!anyInstalledThisPass) break;
        }

        status?.Report("依赖检查完成。");
        return report;
    }

    /// <summary>
    /// 只分析、不下载。给"检查"按钮用 —— 用户想先看看缺什么。
    /// </summary>
    public IReadOnlyList<ModIssue> CheckOnly(string mcVersion, string loader)
    {
        var scan = _manager.Scan();
        if (!scan.DirectoryExists) return [];

        var issues = new List<ModIssue>(ModDependencyAnalyzer.Analyze(scan.Mods));
        issues.AddRange(ModDependencyAnalyzer.CheckGameCompatibility(scan.Mods, mcVersion, loader));

        return issues
            .OrderByDescending(i => i.Level)
            .ThenBy(i => i.SourceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}