using System.IO.Compression;
using System.Text.Json;
using PclLike.Core.Storage;

namespace PclLike.Core.Content;

/// <summary>
/// 一个整合包的安装计划（只读清单）。
///
/// 存在的意义是**"装之前先告诉用户这个包要什么"**：要哪个版本的 Minecraft、
/// 要哪个加载器、要装几个文件。用户看到"需要 Forge 47.2.0 / 128 个文件"之后
/// 再决定装不装，而不是点了安装、等下完 500MB 才发现版本对不上。
/// </summary>
/// <param name="Name">整合包显示名。</param>
/// <param name="McVersion">要求的 Minecraft 版本。</param>
/// <param name="LoaderName">加载器名（vanilla / forge / neoforge / fabric / quilt）。</param>
/// <param name="LoaderVersion">加载器版本。</param>
/// <param name="Files">要下载的文件清单（已剔除服务端专用文件）。</param>
/// <param name="HasOverrides">是否带 overrides/ 或 client-overrides/ 覆盖内容。</param>
public sealed record ModPackPlan(
    string Name,
    string McVersion,
    string? LoaderName,
    string? LoaderVersion,
    IReadOnlyList<ModPackFile> Files,
    bool HasOverrides);

/// <summary>
/// 整合包清单里的一个文件。
///
/// <see cref="Path"/> 是相对 GameDir 的落盘路径（如 `mods/sodium.jar`），
/// <see cref="FileName"/> 是它的文件名（界面显示下载进度用，比全路径短）。
/// </summary>
/// <param name="Path">相对 GameDir 的路径，一律用 `/` 分隔。</param>
/// <param name="FileName">文件名部分。</param>
/// <param name="Url">下载地址（Modrinth 的 CDN 可能有多个，这里取第一个）。</param>
/// <param name="Sha1">文件的 sha1，用于校验；清单没给就是 null。</param>
/// <param name="SizeBytes">文件大小（字节），用于进度条预估。</param>
/// <param name="ClientRequired">env.client == "required"，即客户端必须装。</param>
public sealed record ModPackFile(
    string Path,
    string FileName,
    string Url,
    string? Sha1,
    long SizeBytes,
    bool ClientRequired);

/// <summary>
/// 一个文件的下载动作。
///
/// ## 为什么是委托而不是自己发 HTTP
///
/// 启动器已经有一套下载器：CDN 重试、镜像回退、并发限速、进度上报全在里面。
/// Core 层再写一份 HTTP 逻辑，等于把重试策略分裂成两处，以后修镜像逻辑要改两个地方。
/// 所以这里只定义"要下什么、下到哪"，由 GUI 用现成的下载器实现。
/// 返回 false 表示这个文件最终没下来（重试也失败了），安装器据此汇总失败清单。
/// </summary>
public delegate Task<bool> ModPackFileFetcher(ModPackFile file, string destPath, CancellationToken ct);

/// <summary>
/// 装 Modrinth 整合包（.mrpack）。
///
/// ## .mrpack 是什么
///
/// 就是一个 zip，里面：
///   - `modrinth.index.json` —— 清单：要哪些文件、从哪下、覆盖哪些配置
///   - `overrides/` —— 客户端服务端都要的覆盖文件（config、资源包……）
///   - `client-overrides/` —— 只有客户端要的覆盖文件，**优先级高于 overrides**
///
/// ## 为什么先 overrides 后下文件
///
/// 覆盖文件决定 Mod 怎么配置；先铺好配置再放 Mod，万一某个 Mod 下载失败，
/// 留下的也是一个**配置正确的残缺包**，用户补下那一个文件即可。
/// 反过来的话，配置缺失会让整个包启动就崩，更难修。
///
/// ## 单个失败不中断
///
/// 整合包动辄上百个文件，网络抖动导致第 80 个失败就整个放弃、把已下的 79 个
/// 全删掉，是最糟糕的体验。这里逐个下载、记录失败、最后汇总
/// "成功 N / 失败 M（文件名）"，用户重试一次通常就能补齐。
/// </summary>
public static class ModPackInstaller
{
    /// <summary>清单文件名。mrpack 规范固定这个路径，大小写按不可信数据处理。</summary>
    private const string IndexName = "modrinth.index.json";

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 只读 .mrpack 的清单，**不下载任何东西**。
    ///
    /// 读不出清单（不是 zip、没有 modrinth.index.json、JSON 坏了）时返回 null，
    /// 由调用方决定提示语 —— 这里不抛，因为"用户拖进来一个 zip 但不是整合包"
    /// 是很正常的误操作。
    /// </summary>
    public static ModPackPlan? ReadPlan(string mrpackPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mrpackPath) || !File.Exists(mrpackPath)) return null;

            using var zip = ZipFile.OpenRead(mrpackPath);

            var entry = zip.Entries.FirstOrDefault(
                e => NormalizeEntryName(e.FullName).Equals(IndexName, StringComparison.OrdinalIgnoreCase));

            if (entry is null) return null;

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            return BuildPlan(doc.RootElement, zip);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 装整合包。
    ///
    /// <paramref name="targetInstance"/> 的 <see cref="GameInstance.GameDir"/> 是落盘根：
    /// 清单里的 `mods/xx.jar` 就落到 `&lt;GameDir&gt;/mods/xx.jar`，overrides 同理。
    /// 所以"装整合包"= 往一个实例的 gameDir 里铺东西，不涉及创建新实例
    /// ——实例的创建（Loader 版本、命名）是另一件事，由调用方决定。
    /// </summary>
    /// <param name="mrpackPath">本地 .mrpack 文件。</param>
    /// <param name="targetInstance">目标实例。</param>
    /// <param name="fetcher">下载回调，通常由 GUI 用启动器下载器实现。</param>
    /// <param name="log">进度日志（"正在下载 3/128……"）。</param>
    /// <param name="ct">取消标记。</param>
    public static async Task<InstallResult> InstallAsync(
        string mrpackPath,
        GameInstance targetInstance,
        ModPackFileFetcher fetcher,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mrpackPath) || !File.Exists(mrpackPath))
            return new InstallResult(false, $"整合包文件不存在：{mrpackPath}");

        if (fetcher is null)
            return new InstallResult(false, "没有提供下载器，无法安装整合包");

        try
        {
            var gameDir = targetInstance.GameDir;
            Directory.CreateDirectory(gameDir);

            using var zip = ZipFile.OpenRead(mrpackPath);

            var indexEntry = zip.Entries.FirstOrDefault(
                e => NormalizeEntryName(e.FullName).Equals(IndexName, StringComparison.OrdinalIgnoreCase));

            if (indexEntry is null)
                return new InstallResult(false, "这不是整合包：压缩包里没有 modrinth.index.json");

            ModPackPlan? plan;
            using (var stream = indexEntry.Open())
            using (var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }))
            {
                plan = BuildPlan(doc.RootElement, zip);
            }

            if (plan is null)
                return new InstallResult(false, "整合包清单解析失败（modrinth.index.json 格式不对）");

            log?.Report($"整合包：{plan.Name}（Minecraft {plan.McVersion}"
                        + (plan.LoaderName is null ? "" : $" / {plan.LoaderName} {plan.LoaderVersion}")
                        + $"，{plan.Files.Count} 个文件）");

            // 1) 先铺覆盖文件：overrides 先、client-overrides 后（后者覆盖前者）
            var applied = ApplyOverrides(zip, gameDir, log, ct);
            if (applied > 0) log?.Report($"已覆盖 {applied} 个配置文件");

            // 2) 再逐个下文件。失败只记账，不打断
            var ok = 0;
            var failures = new List<string>();

            for (var i = 0; i < plan.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var file = plan.Files[i];

                var dest = ContentInstaller.ResolveInside(gameDir, file.Path);
                if (dest is null)
                {
                    failures.Add($"{file.Path}（路径越界）");
                    continue;
                }

                log?.Report($"下载 {i + 1}/{plan.Files.Count}：{file.FileName}");

                bool done;
                try
                {
                    done = await fetcher(file, dest, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    done = false;
                }

                if (done) ok++;
                else failures.Add(file.FileName);
            }

            var message = $"成功 {ok} / 失败 {failures.Count}";

            if (applied > 0) message += $"，覆盖配置 {applied} 个";

            if (failures.Count > 0)
            {
                // 失败文件名要点出来，用户才能自己去补或者重试。
                // 只报数字的话，界面上只能显示"失败了 3 个"，无从下手。
                var shown = failures.Take(10).ToList();
                message += $"；失败：{string.Join("、", shown)}";
                if (failures.Count > shown.Count) message += $" 等 {failures.Count} 个";

                log?.Report(message);

                return new InstallResult(
                    false, message, gameDir, plan.LoaderName);
            }

            log?.Report(message);
            return new InstallResult(true, message, gameDir, plan.LoaderName);
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(false, "安装被取消");
        }
        catch (InvalidDataException)
        {
            return new InstallResult(false, "这不是有效的整合包（压缩包损坏或不是 zip）");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"整合包安装失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    /// <summary>
    /// 把清单 JSON 翻译成 <see cref="ModPackPlan"/>。返回 null 表示这份清单不可用。
    /// </summary>
    private static ModPackPlan? BuildPlan(JsonElement root, ZipArchive zip)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name)) name = "未命名整合包";

        var mcVersion = "";
        string? loaderName = null;
        string? loaderVersion = null;

        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            mcVersion = GetString(deps, "minecraft") ?? "";

            // 加载器键名 → 启动器内部的 loader 名。
            // 顺序有意义：一个包理论上只会声明一种加载器，按这个顺序取第一个命中的。
            var map = new (string Key, string Name)[]
            {
                ("forge", "forge"),
                ("neoforge", "neoforge"),
                ("fabric-loader", "fabric"),
                ("quilt-loader", "quilt"),
            };

            foreach (var (key, value) in map)
            {
                var version = GetString(deps, key);
                if (string.IsNullOrWhiteSpace(version)) continue;

                loaderName = value;
                loaderVersion = version;
                break;
            }
        }

        var files = new List<ModPackFile>();
        var withOverrides = false;

        if (root.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in fileArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var path = GetString(item, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;

                // modrinth 规范用 `/`，但别人的生成器可能给出 `\`，统一掉
                path = path.Replace('\\', '/').TrimStart('/');
                if (path.Length == 0) continue;

                // env.client == "unsupported" 是**服务端专用**文件。
                // 装进客户端的 mods 目录轻则报错重则崩游戏，必须跳过。
                var clientEnv = "required";
                if (item.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                    clientEnv = GetString(env, "client") ?? "required";

                if (clientEnv.Equals("unsupported", StringComparison.OrdinalIgnoreCase)) continue;

                var url = "";
                if (item.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in downloads.EnumerateArray())
                    {
                        if (d.ValueKind != JsonValueKind.String) continue;
                        url = d.GetString() ?? "";
                        if (url.Length > 0) break;
                    }
                }

                // 没有下载地址的条目装不了，但**不能丢**——丢了用户就不知道
                // 这个包还缺东西。保留它、让 fetcher 去失败，最后汇总里会点名。
                var sha1 = "";
                if (item.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
                    sha1 = GetString(hashes, "sha1") ?? "";

                var size = 0L;
                if (item.TryGetProperty("fileSize", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number)
                    size = sizeEl.TryGetInt64(out var v) ? v : 0L;

                files.Add(new ModPackFile(
                    Path: path,
                    FileName: Path.GetFileName(path),
                    Url: url,
                    Sha1: string.IsNullOrWhiteSpace(sha1) ? null : sha1,
                    SizeBytes: size,
                    ClientRequired: clientEnv.Equals("required", StringComparison.OrdinalIgnoreCase)));
            }
        }

        // overrides 的存在性只扫条目名，不解压
        foreach (var entry in zip.Entries)
        {
            var n = NormalizeEntryName(entry.FullName);
            if (n.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
            {
                withOverrides = true;
                break;
            }
        }

        return new ModPackPlan(name, mcVersion, loaderName, loaderVersion, files, withOverrides);
    }

    /// <summary>
    /// 把 overrides/ 与 client-overrides/ 铺到 gameDir，返回落盘的文件数。
    ///
    /// 目录顺序上 client-overrides 在后，同名文件自然被它盖掉，符合规范。
    /// 条目名同样过 <see cref="ContentInstaller.ResolveInside"/> —— 整合包是
    /// 陌生人做的 zip，"解压时写进 C:\Windows"这种事故就是在这里挡住的。
    /// </summary>
    private static int ApplyOverrides(ZipArchive zip, string gameDir, IProgress<string>? log, CancellationToken ct)
    {
        var count = 0;

        foreach (var prefix in new[] { "overrides/", "client-overrides/" })
        {
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var name = NormalizeEntryName(entry.FullName);

                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = name[prefix.Length..];
                if (relative.Length == 0) continue;

                if (ContentInstaller.IsJunkPath(relative)) continue;

                var target = ContentInstaller.ResolveInside(gameDir, relative);
                if (target is null)
                {
                    log?.Report($"跳过越界条目：{entry.FullName}");
                    continue;
                }

                if (relative.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                using var src = entry.Open();
                using var dst = File.Create(target);
                src.CopyTo(dst);

                count++;
            }
        }

        return count;
    }

    /// <summary>zip 条目名统一成 `/` 分隔，去掉开头的 `./`。</summary>
    private static string NormalizeEntryName(string raw)
    {
        var name = raw.Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        return name;
    }

    private static string? GetString(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var el)) return null;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            _ => null,
        };
    }
}