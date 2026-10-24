using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PclLike.Core.Mods;

/// <summary>
/// 一个 Mod 文件的元数据。
///
/// 三类 Mod 读取方式完全不同，必须都覆盖：
///   Forge / NeoForge → `META-INF/mods.toml`（TOML，含 modId/version/dependencies）
///   Fabric           → `fabric.mod.json`（JSON，depends 是 Map）
///   Quilt            → `quilt.mod.json`（结构又不一样）
///
/// 全都读不出就返回 null，交给上层显示"未知 Mod"——不抛异常，
/// 因为 mods 目录里常有资源包、说明文件、半截下载残留，这是常态。
/// </summary>
public sealed class ModInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Description { get; init; }

    public IReadOnlyList<string> Authors { get; init; } = [];
    public string? License { get; init; }
    public string? Homepage { get; init; }

    /// <summary>支持的 MC 版本（Fabric 常为空，Forge 通常为空）。</summary>
    public IReadOnlyList<string> McVersions { get; init; } = [];

    /// <summary>支持的加载器：forge / neoforge / fabric / quilt。</summary>
    public IReadOnlyList<string> Loaders { get; init; } = [];

    /// <summary>依赖：依赖 id → 版本范围串。空串表示只要装了就行。</summary>
    public IReadOnlyDictionary<string, string> Dependencies { get; init; }
        = new Dictionary<string, string>();

    /// <summary>客户端/服务端：both / client / server。</summary>
    public string? Side { get; init; }

    /// <summary>包内图标路径（如果声明了）。</summary>
    public string? IconPath { get; init; }
}

/// <summary>一个 Mod 文件在磁盘上的样子。</summary>
public sealed record ModFile
{
    /// <summary>完整路径。</summary>
    public required string Path { get; init; }

    /// <summary>逻辑文件名（不含 .disabled 后缀）。</summary>
    public required string FileName { get; init; }

    /// <summary>是否启用（.disabled 后缀 = 禁用）。</summary>
    public required bool Enabled { get; set; }

    public required long SizeBytes { get; init; }

    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>解析出的元数据。null 表示读不出来。</summary>
    public ModInfo? Info { get; init; }

    /// <summary>读不出元数据时的原因（给界面显示）。</summary>
    public string? ParseError { get; init; }

    /// <summary>
    /// 界面上显示的名字：有元数据就用元数据里的 name，
    /// 否则退回文件名（去掉 .jar / .disabled），保证列表里不出现空行。
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (Info is not null && Info.Name.Length > 0) return Info.Name;

            var n = FileName.Length > 0 ? FileName : System.IO.Path.GetFileName(Path);

            if (n.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                n = n[..^".disabled".Length];
            if (n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                n = n[..^".jar".Length];

            return n.Length > 0 ? n : "未知 Mod";
        }
    }
}

/// <summary>一次扫描的结果。</summary>
public sealed record ModScanResult
{
    public IReadOnlyList<ModFile> Mods { get; init; } = [];
    public string ModsDirectory { get; init; } = "";
    public bool DirectoryExists { get; init; }
}

/// <summary>
/// Mod jar 元数据解析器。
///
/// 只读 jar 里几个固定路径的小文件，不整包解压 —— 一个 200MB 的整合包
/// 要是整体读进内存再解析，扫一次能吃掉几百 MB。
/// </summary>
public static class ModScanner
{
    /// <summary>
    /// 解析一个 jar，读出元数据。
    ///
    /// 返回 null 表示这不是标准 Mod（资源包、损坏文件、或非 Minecraft 的 jar）。
    /// </summary>
    public static ModInfo? Parse(string jarPath)
    {
        if (!File.Exists(jarPath)) return null;

        using var zip = ZipFile.OpenRead(jarPath);

        // ── Fabric ──
        var fabric = zip.GetEntry("fabric.mod.json");
        if (fabric is not null)
        {
            var json = ReadJson(fabric);
            if (json is not null) return ParseFabric(json);
        }

        // ── Quilt ──
        var quilt = zip.GetEntry("quilt.mod.json");
        if (quilt is not null)
        {
            var json = ReadJson(quilt);
            if (json is not null) return ParseQuilt(json);
        }

        // ── Forge / NeoForge ──
        // 新版在 META-INF/mods.toml；1.20.1 的 NeoForge 也用这个路径
        var toml = zip.GetEntry("META-INF/mods.toml");
        if (toml is not null)
        {
            var text = ReadText(toml);
            if (text is not null) return ParseForgeToml(text);
        }

        // 老版 Forge（1.12 及以前）用 mcmod.info
        var mcmod = zip.GetEntry("mcmod.info");
        if (mcmod is not null)
        {
            var text = ReadText(mcmod);
            if (text is not null) return ParseMcModInfo(text);
        }

        return null;
    }

    private static JsonObject? ReadJson(ZipArchiveEntry entry)
    {
        var text = ReadText(entry);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try { return JsonNode.Parse(text) as JsonObject; }
        catch { return null; }
    }

    private static string? ReadText(ZipArchiveEntry entry)
    {
        try
        {
            using var s = entry.Open();
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch { return null; }
    }

    // ────────────────────────────────────────────────────────────
    // Fabric
    // ────────────────────────────────────────────────────────────

    private static ModInfo ParseFabric(JsonObject json)
    {
        var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (json["depends"] is JsonObject depends)
        {
            foreach (var (k, v) in depends)
            {
                // 值可以是字符串（版本范围）或数组（多个可接受版本）
                deps[k] = v switch
                {
                    JsonValue jv => jv.GetValue<object?>()?.ToString() ?? "",
                    JsonArray arr => string.Join(" ", arr.Select(x => x?.ToString() ?? "")),
                    _ => "",
                };
            }
        }

        var mcVersions = new List<string>();
        if (deps.TryGetValue("minecraft", out var mcRange) && mcRange.Length > 0)
            mcVersions.AddRange(SplitRange(mcRange));

        var authors = new List<string>();
        if (json["authors"] is JsonArray authArr)
        {
            foreach (var a in authArr)
            {
                if (a is JsonValue av) authors.Add(av.ToString());
                else if (a is JsonObject ao && ao["name"] is JsonValue nv) authors.Add(nv.ToString());
            }
        }

        return new ModInfo
        {
            Id = Str(json, "id"),
            Name = Str(json, "name").Length > 0 ? Str(json, "name") : Str(json, "id"),
            Version = Str(json, "version"),
            Description = StrOrNull(json, "description"),
            Authors = authors,
            License = StrOrNull(json, "license"),
            Homepage = StrOrNull(json, "contact", "homepage") ?? StrOrNull(json, "homepage"),
            McVersions = mcVersions,
            Loaders = ["fabric"],
            Dependencies = deps,
            Side = StrOrNull(json, "environment"),
            IconPath = StrOrNull(json, "icon") ?? StrOrNull(json, "icon", "64"),
        };
    }

    // ────────────────────────────────────────────────────────────
    // Quilt
    // ────────────────────────────────────────────────────────────

    private static ModInfo ParseQuilt(JsonObject json)
    {
        var ql = json["quilt_loader"] as JsonObject ?? json;

        var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Quilt 的 depends 是数组：[{id, versions}, ...]
        if (ql["depends"] is JsonArray depArr)
        {
            foreach (var d in depArr)
            {
                if (d is not JsonObject o) continue;
                var id = Str(o, "id");
                if (id.Length == 0) continue;

                var range = o["versions"] switch
                {
                    JsonValue v => v.ToString(),
                    JsonArray a => string.Join(" ", a.Select(x => x?.ToString() ?? "")),
                    _ => "",
                };
                deps[id] = range;
            }
        }

        var meta = json["metadata"] as JsonObject;
        var name = meta is not null ? Str(meta, "name") : "";

        var authors = new List<string>();
        if (meta?["contributors"] is JsonObject contrib)
            authors.AddRange(contrib.Select(kv => kv.Key));

        return new ModInfo
        {
            Id = Str(ql, "id"),
            Name = name.Length > 0 ? name : Str(ql, "id"),
            Version = Str(ql, "version"),
            Description = meta is not null ? StrOrNull(meta, "description") : null,
            Authors = authors,
            License = meta is not null ? StrOrNull(meta, "license") : null,
            Loaders = ["quilt"],
            Dependencies = deps,
        };
    }

    // ────────────────────────────────────────────────────────────
    // Forge / NeoForge (mods.toml)
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析 mods.toml。
    ///
    /// 这是个精简 TOML 解析器 —— 只处理 mods.toml 里会出现的那几种结构：
    ///   modLoader="javafml"
    ///   loaderVersion="[47,)"
    ///   license="MIT"
    ///   [[mods]]
    ///   modId="examplemod"
    ///   version="1.0"
    ///   displayName="Example"
    ///   description='''...'''
    ///   [[dependencies.examplemod]]
    ///   modId="forge"
    ///   mandatory=true
    ///   versionRange="[47,)"
    ///
    /// 不追求完整 TOML 规范（多行字符串、内联表、日期都不需要），
    /// 引 TOML 库反而更重。
    /// </summary>
    private static ModInfo? ParseForgeToml(string toml)
    {
        var id = "";
        var version = "";
        var displayName = "";
        string? description = null;
        var license = "";
        var issueTracker = "";

        var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mcVersions = new List<string>();
        var loaders = new List<string> { "forge" };   // 有 mods.toml 就是 Forge 系

        var lines = toml.Split('\n');
        var inMods = false;          // 在 [[mods]] 段里
        var depModId = (string?)null; // 当前 [[dependencies.xxx]] 段属于自己的 modId

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // 跳过注释与空行
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // ── 段头 ──
            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                // [[dependencies.modid]] —— 记下这是谁的依赖块
                if (line.StartsWith("[[dependencies.", StringComparison.OrdinalIgnoreCase))
                {
                    depModId = line["[[dependencies.".Length..].TrimEnd(']', ' ');
                    inMods = false;
                }
                else if (line.StartsWith("[[mods]]", StringComparison.OrdinalIgnoreCase))
                {
                    inMods = true;
                    depModId = null;
                }
                else
                {
                    inMods = false;
                    depModId = null;
                }
                continue;
            }

            var (key, value) = SplitTomlLine(line);
            if (key.Length == 0) continue;

            // ── 顶层：loader / license ──
            if (!inMods && depModId is null)
            {
                if (key.Equals("modLoader", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Contains("lowcodefml", StringComparison.OrdinalIgnoreCase))
                        loaders = ["neoforge"];   // 1.20.1 之后 NeoForge 用 lowcodefml
                    else if (value.Contains("javafml", StringComparison.OrdinalIgnoreCase))
                    {
                        // javafml 两种都可能是 —— 看 loaderVersion 或依赖里有没有 neoforge
                        loaders = ["forge", "neoforge"];
                    }
                }
                else if (key.Equals("license", StringComparison.OrdinalIgnoreCase))
                {
                    license = value;
                }
                else if (key.Equals("issueTrackerURL", StringComparison.OrdinalIgnoreCase))
                {
                    issueTracker = value;
                }
                continue;
            }

            // ── [[mods]] 段 ──
            if (inMods)
            {
                switch (key.ToLowerInvariant())
                {
                    case "modid": if (id.Length == 0) id = value; break;
                    case "version": if (version.Length == 0) version = value; break;
                    case "displayname": if (displayName.Length == 0) displayName = value; break;
                    case "description": description ??= value; break;
                    case "authors": description ??= null; break;
                    case "license": if (license.Length == 0) license = value; break;
                }
                continue;
            }

            // ── [[dependencies.xxx]] 段 ──
            if (depModId is not null)
            {
                // 只关心属于主 mod 自己的依赖块
                if (!string.Equals(depModId, id, StringComparison.OrdinalIgnoreCase) && id.Length > 0)
                    continue;

                // 收集一组依赖：需要 lookahead，因为 modId/mandatory/versionRange 分散在多行
                if (key.Equals("modId", StringComparison.OrdinalIgnoreCase))
                {
                    var depId = value;
                    var range = "";
                    var mandatory = true;
                    var side = "BOTH";

                    // 往下读同一段里的其余字段
                    for (var j = i + 1; j < lines.Length; j++)
                    {
                        var l2 = lines[j].Trim();
                        if (l2.StartsWith("[[", StringComparison.Ordinal) || l2.StartsWith('[')) break;
                        if (l2.Length == 0 || l2.StartsWith('#')) continue;

                        var (k2, v2) = SplitTomlLine(l2);
                        if (k2.Equals("versionRange", StringComparison.OrdinalIgnoreCase)) range = v2;
                        else if (k2.Equals("mandatory", StringComparison.OrdinalIgnoreCase))
                            mandatory = v2.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (k2.Equals("side", StringComparison.OrdinalIgnoreCase)) side = v2;
                    }

                    // 只记必装依赖 —— 可选依赖缺了不影响启动，列出来只会让用户困惑
                    if (mandatory && !deps.ContainsKey(depId))
                    {
                        deps[depId] = range;

                        if (depId.Equals("minecraft", StringComparison.OrdinalIgnoreCase) && range.Length > 0)
                            mcVersions.AddRange(SplitRange(range));

                        if (depId.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
                            loaders = ["neoforge"];
                    }
                }
            }
        }

        // NeoForge 的 mods.toml 里 modLoader 常写 javafml，但依赖里必有 neoforge
        if (deps.ContainsKey("neoforge")) loaders = ["neoforge"];

        if (id.Length == 0) return null;

        return new ModInfo
        {
            Id = id,
            Name = displayName.Length > 0 ? displayName : id,
            Version = version,
            Description = description,
            License = license.Length > 0 ? license : null,
            Homepage = issueTracker.Length > 0 ? issueTracker : null,
            McVersions = mcVersions,
            Loaders = loaders,
            Dependencies = deps,
        };
    }

    /// <summary>把 `key="value"` 拆成两半；`#` 之后算注释。</summary>
    private static (string Key, string Value) SplitTomlLine(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return ("", "");

        var key = line[..eq].Trim();
        var val = line[(eq + 1)..].Trim();

        // 去掉结尾注释（但要小心引号里的 #）
        var quote = val.Length > 0 && (val[0] == '"' || val[0] == '\'') ? val[0] : '\0';
        if (quote == '\0')
        {
            var hash = val.IndexOf('#');
            if (hash >= 0) val = val[..hash].Trim();
        }

        // 去引号
        if (val.Length >= 2 &&
            ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
            val = val[1..^1];

        return (key, val);
    }

    // ────────────────────────────────────────────────────────────
    // 老版 Forge：mcmod.info
    // ────────────────────────────────────────────────────────────

    private static ModInfo? ParseMcModInfo(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            var obj = node as JsonArray is { Count: > 0 } arr ? arr[0] as JsonObject : node as JsonObject;
            if (obj is null) return null;

            var authors = new List<string>();
            if (obj["authorList"] is JsonArray al)
                authors.AddRange(al.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0));

            var mcVersions = new List<string>();
            if (StrOrNull(obj, "mcversion") is { Length: > 0 } mcv) mcVersions.Add(mcv);

            var id = Str(obj, "modid");

            return new ModInfo
            {
                Id = id,
                Name = Str(obj, "name").Length > 0 ? Str(obj, "name") : id,
                Version = Str(obj, "version"),
                Description = StrOrNull(obj, "description"),
                Authors = authors,
                License = StrOrNull(obj, "license"),
                Homepage = StrOrNull(obj, "url"),
                McVersions = mcVersions,
                Loaders = ["forge"],
            };
        }
        catch { return null; }
    }

    // ────────────────────────────────────────────────────────────
    // 工具
    // ────────────────────────────────────────────────────────────

    private static string Str(JsonObject o, string key)
        => o[key] is JsonValue v ? v.ToString() : "";

    private static string Str(JsonObject o, string key, string sub)
        => o[key] is JsonObject o2 && o2[sub] is JsonValue v ? v.ToString() : "";

    private static string? StrOrNull(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        var s = v.ToString();
        return s.Length > 0 ? s : null;
    }

    private static string? StrOrNull(JsonObject o, string key, string sub)
    {
        if (o[key] is not JsonObject o2) return null;
        if (o2[sub] is not JsonValue v) return null;
        var s = v.ToString();
        return s.Length > 0 ? s : null;
    }

    /// <summary>
    /// 把版本范围串拆成一个个具体版本号。
    /// `">=1.20 &lt;1.21"` → ["1.20", "1.21"]；`"1.20.1"` → ["1.20.1"]。
    /// </summary>
    private static List<string> SplitRange(string range)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(range)) return result;

        foreach (var tok in range.Split([' ', ',', '[', ']', '(', ')', '&', '|'],
                                        StringSplitOptions.RemoveEmptyEntries))
        {
            var t = tok.Trim();

            // 去掉比较运算符
            while (t.Length > 0 && (t[0] == '>' || t[0] == '<' || t[0] == '=' || t[0] == '~' || t[0] == '^'))
                t = t[1..];

            t = t.Trim();

            // 版本号至少得有数字，`*` 和空串都跳过
            if (t.Length > 0 && char.IsDigit(t[0]))
                result.Add(t);
        }

        return result;
    }
}