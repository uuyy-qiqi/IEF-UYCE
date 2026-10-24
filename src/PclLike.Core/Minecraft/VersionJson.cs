using System.Text.Json.Serialization;

namespace PclLike.Core.Minecraft;

/// <summary>
/// 版本 JSON。字段用 nullable，因为父版本可能不提供全部字段，
/// 子版本通过 inheritsFrom 继承后覆盖。
/// </summary>
public sealed class VersionJson
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("mainClass")] public string? MainClass { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; set; }
    [JsonPropertyName("downloads")] public VersionDownloads? Downloads { get; set; }
    [JsonPropertyName("libraries")] public List<Library>? Libraries { get; set; }
    [JsonPropertyName("arguments")] public Arguments? Arguments { get; set; }

    /// <summary>1.13 之前的旧格式，是一整个命令行字符串。</summary>
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }

    [JsonPropertyName("javaVersion")] public JavaVersionRef? JavaVersion { get; set; }
    [JsonPropertyName("complianceLevel")] public int? ComplianceLevel { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset? ReleaseTime { get; set; }
    [JsonPropertyName("time")] public DateTimeOffset? Time { get; set; }

    /// <summary>原版从来没有这个字段，但一些整合包会写，用于提示内存。</summary>
    [JsonPropertyName("recommendedMemory")] public int? RecommendedMemoryMb { get; set; }

    [JsonIgnore] public string? SourceFile { get; set; }

    /// <summary>原始 JSON 文本，合并后保留以备查。</summary>
    [JsonIgnore] public string? RawJson { get; set; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")] public Artifact? Client { get; set; }
    [JsonPropertyName("server")] public Artifact? Server { get; set; }
    [JsonPropertyName("client_mappings")] public Artifact? ClientMappings { get; set; }
}

public sealed class Artifact
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class JavaVersionRef
{
    [JsonPropertyName("component")] public string? Component { get; set; }
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public sealed class Library
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("rules")] public List<Rule>? Rules { get; set; }
    [JsonPropertyName("extract")] public ExtractRules? Extract { get; set; }

    /// <summary>Forge/Fabric 这类安装器有时用这个字段给出直接下载地址。</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonIgnore] public string GroupId => Name is null ? "" : Name.Split(':')[0];
    [JsonIgnore] public string ArtifactId => Name is null ? "" : Name.Split(':')[1];
    [JsonIgnore] public string VersionPart => Name is null ? "" : Name.Split(':')[2];
    [JsonIgnore] public string? Classifier => Name is not null && Name.Split(':').Length > 3 ? Name.Split(':')[3] : null;

    /// <summary>由 Maven 坐标推导出相对路径，例如 net/minecraft/xxx/1.0/xxx-1.0.jar。</summary>
    public string ToMavenPath()
    {
        var g = GroupId.Replace('.', '/');
        var file = $"{ArtifactId}-{VersionPart}";
        if (!string.IsNullOrEmpty(Classifier)) file += "-" + Classifier;
        return $"{g}/{ArtifactId}/{VersionPart}/{file}.jar";
    }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public Artifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, Artifact>? Classifiers { get; set; }
}

public sealed class ExtractRules
{
    [JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }
}

public sealed class Arguments
{
    [JsonPropertyName("game")] public List<object>? Game { get; set; }
    [JsonPropertyName("jvm")] public List<object>? Jvm { get; set; }
}

/// <summary>规则条目。action 为 allow/disallow，条件全满足才生效。</summary>
public sealed class Rule
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("os")] public RuleOs? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }
}

public sealed class RuleOs
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public LatestVersions? Latest { get; set; }
    [JsonPropertyName("versions")] public List<ManifestEntry>? Versions { get; set; }
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
}

public sealed class ManifestEntry
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset? ReleaseTime { get; set; }
    [JsonPropertyName("complianceLevel")] public int? ComplianceLevel { get; set; }
}

/// <summary>assets 索引，objects 以路径为键。</summary>
public sealed class AssetIndex
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject>? Objects { get; set; }
    [JsonPropertyName("virtual")] public bool? Virtual { get; set; }
    [JsonPropertyName("map_to_resources")] public bool? MapToResources { get; set; }
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}