using System.IO.Compression;
using System.Text.Json;

namespace PclLike.Core.Content;

/// <summary>
/// CurseForge 清单里的一条文件。
///
/// 注意这里只有 id：CurseForge 的 manifest.json **不写文件名，也不写下载地址**，
/// 只有 projectID + fileID。想拿到文件得另外问服务端（见 <see cref="CursePack.DownloadUrl"/>）。
/// 这一点和 Modrinth 的 .mrpack 正相反 —— 那边每条都带 url + sha1，清单本身就是下载表。
/// </summary>
public sealed record CursePackFile(int ProjectId, int FileId, bool Required);

/// <summary>CurseForge 整合包的清单（manifest.json）。</summary>
public sealed record CursePackPlan(
    string Name,
    string Version,
    string McVersion,
    string? LoaderName,
    string? LoaderVersion,
    IReadOnlyList<CursePackFile> Files,
    string OverridesDir)
{
    /// <summary>给人看的名字，比如「AFoP Continue-1.5.1-fix」。</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? Version
        : string.IsNullOrWhiteSpace(Version) ? Name
        : $"{Name} {Version}";

    public string LoaderText => LoaderName is null
        ? "原版"
        : $"{LoaderName} {LoaderVersion}";

    public string SummaryText =>
        $"CurseForge 整合包：{DisplayName}｜{McVersion}｜{LoaderText}｜{Files.Count} 个文件";

    /// <summary>清单里标注为「可选」的条目数（安装时一并装上，但要说清楚）。</summary>
    public int OptionalCount => Files.Count(f => !f.Required);
}

/// <summary>装完的结果。</summary>
public sealed record CursePackResult(
    bool Ok,
    string Message,
    int Downloaded,
    int Failed,
    long Bytes,
    int Overrides);

/// <summary>
/// 把一个 CurseForge 文件下到目标目录，返回落盘的文件名；失败返回 null。
///
/// 做成委托而不是直接调下载器：这一层（Content）只管"清单怎么读、东西往哪放"，
/// 下载走哪条链路、怎么重试是上层的事 —— 和 ModPackInstaller 的分工保持一致。
/// </summary>
public delegate Task<string?> CurseFileFetcher(
    int projectId, int fileId, string targetDir, CancellationToken ct);

/// <summary>
/// CurseForge 整合包的读清单 + 安装。
///
/// 与 Modrinth 整合包的三处差别（决定了这里必须是另一套代码）：
///  1. 清单文件名与格式不同：manifest.json + projectID/fileID，没有 url；
///  2. 有一份 overrides/（作者改过的配置、材质、kubejs 脚本）要铺进实例；
///  3. 加载器写在 minecraft.modLoaders[0].id 里，形如 "forge-47.4.0"。
/// </summary>
public static class CursePack
{
    /// <summary>
    /// 公开下载端点。
    ///
    /// 这是 CurseForge 网页上那个「Download」按钮用的地址：它会 307 跳到
    /// edge.forgecdn.net，并把 api-key 一并带在查询串里，所以**不需要用户自己
    /// 申请 CurseForge API key**（直连 api.curseforge.com 才需要，实测 403）。
    /// 跳转后的地址里带着真实文件名 —— 清单里没有文件名，只能从这儿拿。
    /// </summary>
    public static string DownloadUrl(int projectId, int fileId)
        => $"https://www.curseforge.com/api/v1/mods/{projectId}/files/{fileId}/download";

    /// <summary>
    /// 读 manifest.json。
    ///
    /// 全程不抛业务异常之外的东西：清单坏了、字段缺了、json 不合法，都换成
    /// InvalidDataException 抛出去，让上层用一句人话告诉用户。
    /// </summary>
    public static CursePackPlan ReadPlan(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var entry = zip.Entries.FirstOrDefault(x =>
            x.FullName.Replace('\\', '/').Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("包里没有 manifest.json");

        JsonDocument doc;
        try
        {
            using var stream = entry.Open();
            doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"manifest.json 不是合法的 json（{ex.Message}）");
        }

        using (doc)
        {
            var root = doc.RootElement;

            var name = Text(root, "name");
            var version = Text(root, "version");
            var overrides = Text(root, "overrides");
            if (string.IsNullOrWhiteSpace(overrides)) overrides = "overrides";

            var mcVersion = "";
            string? loaderName = null;
            string? loaderVersion = null;

            if (root.TryGetProperty("minecraft", out var mc) && mc.ValueKind == JsonValueKind.Object)
            {
                mcVersion = Text(mc, "version");

                if (mc.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
                {
                    // primary=true 的那个才是这个包真正要的加载器；没有标记时取第一个。
                    JsonElement? picked = null;

                    foreach (var l in loaders.EnumerateArray())
                    {
                        picked ??= l;

                        if (l.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True)
                        {
                            picked = l;
                            break;
                        }
                    }

                    if (picked is { } chosen)
                    {
                        // id 形如 "forge-47.4.0" / "fabric-0.15.11" / "neoforge-21.1.80"
                        var id = Text(chosen, "id");

                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            var dash = id.IndexOf('-');

                            if (dash > 0)
                            {
                                loaderName = id[..dash];
                                loaderVersion = id[(dash + 1)..];
                            }
                            else
                            {
                                loaderName = id;
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new InvalidDataException("manifest.json 里没写游戏版本（minecraft.version）");

            var files = new List<CursePackFile>();

            if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    var pid = Int(f, "projectID");
                    var fid = Int(f, "fileID");

                    if (pid <= 0 || fid <= 0) continue;

                    var required = !f.TryGetProperty("required", out var req)
                                   || req.ValueKind != JsonValueKind.False;

                    files.Add(new CursePackFile(pid, fid, required));
                }
            }

            if (files.Count == 0)
                throw new InvalidDataException("manifest.json 里的文件列表是空的");

            return new CursePackPlan(
                name ?? "", version ?? "", mcVersion,
                loaderName, loaderVersion, files, overrides);
        }
    }

    /// <summary>
    /// 装一个 CurseForge 整合包到实例目录。
    ///
    /// <paramref name="instanceGameDir"/> 是这个实例的游戏目录；调用方负责先把这个
    /// 实例建出来、并把游戏版本与加载器装好 —— 这一层只做「铺 overrides + 下文件」。
    /// </summary>
    public static async Task<CursePackResult> InstallAsync(
        string zipPath,
        CursePackPlan plan,
        string instanceGameDir,
        CurseFileFetcher fetch,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(instanceGameDir);

        // ── 第一步：铺 overrides ──
        // 作者的配置就靠这一层，漏了会得到"装完了但界面/配方不对"的鬼包。
        //
        // 这一段必须丢到线程池上跑：一个真实整合包的 overrides 有七千多个文件、
        // 两三百 MB，同步解压会让调用线程（GUI 里就是 UI 线程）僵住几十秒 ——
        // 表现就是"拖进去卡死"。同时按 500 个报一次进度，让人看得见它在动。
        var overrides = 0;
        Exception? overrideError = null;

        await Task.Run(() =>
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var prefix = plan.OverridesDir.TrimEnd('/') + "/";
                var rootFull = Path.GetFullPath(instanceGameDir);

                foreach (var e in zip.Entries)
                {
                    var name = e.FullName.Replace('\\', '/');
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith('/')) continue;

                    var rel = name[prefix.Length..];
                    if (rel.Length == 0) continue;

                    var target = Path.GetFullPath(Path.Combine(rootFull, rel));

                    // zip 里的相对路径可以带 ../，别让它写到实例外面去
                    if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;

                    var dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    e.ExtractToFile(target, overwrite: true);
                    overrides++;

                    if (overrides % 500 == 0)
                        log?.Invoke($"    正在铺配置… {overrides} 个");
                }
            }
            catch (Exception ex)
            {
                overrideError = ex;
            }
        }, ct).ConfigureAwait(false);

        if (overrideError is not null)
        {
            log?.Invoke($"    铺 overrides 时出错（{overrideError.GetType().Name}：{overrideError.Message}），继续下 Mod");
        }
        else if (overrides > 0)
        {
            log?.Invoke($"    已铺 {overrides} 个配置文件（overrides/）");
        }

        // ── 第二步：下文件 ──
        var modsDir = Path.Combine(instanceGameDir, "mods");
        Directory.CreateDirectory(modsDir);

        var downloaded = 0;
        var failed = new List<CursePackFile>();
        var bytes = 0L;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastLog = TimeSpan.Zero;

        // 并发下 —— 串行下 264 个文件在这个网络上实测 3.2 秒/个（十几分钟）。
        // 我以前做过一次"并行 4 个 vs 串行"的对比，结论是"并行没用"，那是拿
        // 0.27 MB 的小包测的：小文件的时间全花在建立连接上，测不出并行收益。
        // 真实场景里 CDN 是**每连接限速**，多开几条能成倍提速；6 条也不至于
        // 被当成爬虫按死。
        const int MaxParallel = 6;

        var gate = new SemaphoreSlim(MaxParallel);
        var lockObj = new object();

        async Task<bool> TryOneAsync(CursePackFile f)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var saved = await fetch(f.ProjectId, f.FileId, modsDir, ct).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(saved)) return false;

                var info = new FileInfo(Path.Combine(modsDir, saved));
                if (!info.Exists) return false;

                lock (lockObj) bytes += info.Length;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        async Task RunPassAsync(IReadOnlyList<CursePackFile> list)
        {
            await Task.WhenAll(list.Select(async f =>
            {
                ct.ThrowIfCancellationRequested();

                var ok = await TryOneAsync(f).ConfigureAwait(false);

                lock (lockObj)
                {
                    if (ok) downloaded++;
                    else failed.Add(f);

                    if (stopwatch.Elapsed - lastLog > TimeSpan.FromSeconds(3))
                    {
                        lastLog = stopwatch.Elapsed;
                        log?.Invoke($"    已下 {downloaded}/{plan.Files.Count}（失败 {failed.Count}）");
                    }
                }
            })).ConfigureAwait(false);
        }

        await RunPassAsync(plan.Files).ConfigureAwait(false);

        // 没下下来的再来三轮，间隔递增（2/5/10 秒）。
        // 两类失败混在一起：一类是 CDN 偶发挂掉或限流（歇一下就好），另一类是
        // CurseForge 的公开端点干脆不给匿名下（404，重试也没用），所以三轮之后
        // 还失败的就如实报出来，而不是无限重试。
        for (var round = 1; round <= 3 && failed.Count > 0; round++)
        {
            var wait = round switch { 1 => 2, 2 => 5, _ => 10 };

            log?.Invoke($"    {failed.Count} 个文件没下下来，歇 {wait} 秒重试（第 {round}/3 轮）");

            await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);

            var retry = failed.ToList();
            failed.Clear();

            await RunPassAsync(retry).ConfigureAwait(false);
        }

        var sizeText = bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
            : $"{bytes / 1024.0 / 1024:0.#} MB";

        var ok = failed.Count == 0;

        var message = ok
            ? $"装好了：{downloaded} 个文件（{sizeText}），配置 {overrides} 个"
            : $"下了 {downloaded} 个，{failed.Count} 个失败（{sizeText}）；"
              + "缺文件时游戏会卡在 Mod 解析上 —— 再拖一次这个包会自动跳过已装好的，"
              + "失败清单也写在 游戏目录\\下载失败.txt 里";

        if (!ok)
        {
            // 光报一个数字没法行动 —— 把失败的 id 落一份到实例目录，带上 CurseForge
            // 的文件页地址：多数失败是"公开端点不给匿名下"，用户上网页点一下就能补，
            // 比在这儿死磕重试快得多。
            var listPath = Path.Combine(instanceGameDir, "下载失败.txt");

            try
            {
                var lines = new List<string>
                {
                    "# 这几个文件没能从 CurseForge 的公开下载端点拿到。",
                    "# 两种情况：一、端点对匿名请求限流 —— 歇一会儿再拖一次这个包，会接着补；",
                    "#           二、端点干脆不给匿名下 —— 那就打开下面的页面，在 Files 页",
                    "#               手动下载，然后把文件丢进实例的 mods 目录。",
                    "",
                    $"# 已下好 {downloaded} 个，失败 {failed.Count} 个",
                    "",
                };

                lines.AddRange(failed.Select(f =>
                    $"{f.ProjectId}/{f.FileId}   https://www.curseforge.com/projects/{f.ProjectId}/files/{f.FileId}"));

                File.WriteAllLines(listPath, lines, new System.Text.UTF8Encoding(false));

                log?.Invoke($"    失败清单：{listPath}");
            }
            catch
            {
                // 写不了清单不影响装包本身
            }

            log?.Invoke($"    失败的文件："
                + string.Join("、", failed.Take(8).Select(f => $"{f.ProjectId}/{f.FileId}"))
                + (failed.Count > 8 ? $" 等 {failed.Count} 个" : ""));
        }

        return new CursePackResult(ok, message, downloaded, failed.Count, bytes, overrides);
    }

    private static string? Text(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return 0;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(v.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }
}