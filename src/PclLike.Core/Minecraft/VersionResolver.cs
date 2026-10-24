using System.Text.Json;
using System.Text.Json.Nodes;

namespace PclLike.Core.Minecraft;

/// <summary>
/// 版本 JSON 加载与 inheritsFrom 合并。
///
/// 合并规则（与原版 launcher 一致，也是所有第三方启动器的共同做法）：
///   - 标量字段：子版本非空则覆盖父版本
///   - libraries：以 name 为键做字典合并，子版本覆盖同名的父版本条目
///   - arguments.game / arguments.jvm：父在前子在后，简单拼接
///   - mainClass / assets / assetIndex / downloads：子覆盖父
///
/// 顺序很关键：必须先把父版本完整合并好，再叠加子版本，否则会丢中间层。
/// </summary>
public sealed class VersionResolver
{
    private readonly Func<string, CancellationToken, Task<string>> _loadRawJson;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 日期要宽容：Fabric 给的偏移不带冒号（+0000），
        // 内置转换器直接抛异常会让整个版本加载失败
        Converters = { new LenientDateTimeOffsetConverter() },
    };

    /// <param name="loadRawJson">给定版本 id，返回版本 JSON 的原始文本。</param>
    public VersionResolver(Func<string, CancellationToken, Task<string>> loadRawJson)
        => _loadRawJson = loadRawJson;

    public async Task<VersionJson> ResolveAsync(string versionId, CancellationToken ct = default)
    {
        var chain = new List<(string Id, string Raw)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var current = versionId;
        while (!string.IsNullOrEmpty(current))
        {
            if (!seen.Add(current))
                throw new InvalidOperationException($"版本继承链出现环：{current}");

            var raw = await _loadRawJson(current, ct).ConfigureAwait(false);
            chain.Add((current, raw));

            var node = JsonNode.Parse(raw)?.AsObject()
                ?? throw new InvalidOperationException($"版本 {current} 的 JSON 无法解析");
            current = node["inheritsFrom"]?.GetValue<string>();
        }

        // 从最顶层祖先开始合并，逐层向下
        chain.Reverse();

        VersionJson? merged = null;
        foreach (var (id, raw) in chain)
        {
            var layer = JsonSerializer.Deserialize<VersionJson>(raw, JsonOpts)
                ?? throw new InvalidOperationException($"版本 {id} 反序列化失败");
            layer.RawJson = raw;
            merged = merged is null ? layer : Merge(merged, layer);
        }

        if (merged is null) throw new InvalidOperationException($"版本 {versionId} 解析结果为空");

        // 合并后 id / mainClass 等以最终层为准，这里已经是了
        merged.Id ??= versionId;
        return merged;
    }

    /// <summary>parent 为已解析的父版本，child 为当前层。返回合并结果。</summary>
    public static VersionJson Merge(VersionJson parent, VersionJson child)
    {
        var libs = new List<Library>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void AddOrReplace(Library lib)
        {
            if (lib.Name is null) { libs.Add(lib); return; }
            if (byName.TryGetValue(lib.Name, out var idx))
            {
                // 子版本覆盖父版本的同一个库
                var old = libs[idx];
                libs[idx] = new Library
                {
                    Name = lib.Name,
                    Url = lib.Url ?? old.Url,
                    Rules = lib.Rules ?? old.Rules,
                    Natives = lib.Natives ?? old.Natives,
                    Extract = lib.Extract ?? old.Extract,
                    Downloads = MergeDownloads(old.Downloads, lib.Downloads),
                };
            }
            else
            {
                byName[lib.Name] = libs.Count;
                libs.Add(lib);
            }
        }

        foreach (var l in parent.Libraries ?? []) AddOrReplace(l);
        foreach (var l in child.Libraries ?? []) AddOrReplace(l);

        return new VersionJson
        {
            Id = child.Id ?? parent.Id,
            InheritsFrom = null,                       // 已展开，不再需要
            Type = child.Type ?? parent.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            Assets = child.Assets ?? parent.Assets,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            ComplianceLevel = child.ComplianceLevel ?? parent.ComplianceLevel,
            ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
            Time = child.Time ?? parent.Time,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Libraries = libs,
            Downloads = MergeVersionDownloads(parent.Downloads, child.Downloads),
            Arguments = MergeArguments(parent.Arguments, child.Arguments),
            RawJson = child.RawJson,
            SourceFile = child.SourceFile ?? parent.SourceFile,
        };
    }

    private static LibraryDownloads? MergeDownloads(LibraryDownloads? p, LibraryDownloads? c)
    {
        if (p is null) return c;
        if (c is null) return p;

        var classifiers = p.Classifiers is null
            ? c.Classifiers
            : new Dictionary<string, Artifact>(p.Classifiers, StringComparer.OrdinalIgnoreCase);

        if (p.Classifiers is not null && c.Classifiers is not null)
            foreach (var kv in c.Classifiers) classifiers![kv.Key] = kv.Value;

        return new LibraryDownloads
        {
            Artifact = c.Artifact ?? p.Artifact,
            Classifiers = classifiers,
        };
    }

    private static VersionDownloads? MergeVersionDownloads(VersionDownloads? p, VersionDownloads? c)
    {
        if (p is null) return c;
        if (c is null) return p;
        return new VersionDownloads
        {
            Client = c.Client ?? p.Client,
            Server = c.Server ?? p.Server,
            ClientMappings = c.ClientMappings ?? p.ClientMappings,
        };
    }

    private static Arguments? MergeArguments(Arguments? p, Arguments? c)
    {
        if (p is null) return c;
        if (c is null) return p;
        return new Arguments
        {
            Game = [.. p.Game ?? [], .. c.Game ?? []],
            Jvm = [.. p.Jvm ?? [], .. c.Jvm ?? []],
        };
    }
}