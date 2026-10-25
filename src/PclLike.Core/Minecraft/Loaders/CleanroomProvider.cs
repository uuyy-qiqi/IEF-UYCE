using System.Text.RegularExpressions;

namespace PclLike.Core.Minecraft.Loaders;

/// <summary>
/// Cleanroom —— 1.12.2 上继续维护的 Forge 分支（CleanroomMC）。
///
/// 与 Forge 的差别只有三处，所以直接继承 <see cref="ForgeLikeProvider"/> 复用
/// processor 那一整套：
///
///   · 没有 promotions 那种 JSON，版本清单在 maven-metadata.xml 里（XML，用正则取）；
///   · maven 目录是裸版本号（0.7.12-alpha），不带 MC 版本前缀；
///   · 只吃 1.12.2 —— 别的版本直接返回空表（界面会显示"这个 MC 版本还不支持它"）。
///
/// 实测（2026-10-25）：maven-metadata.xml 里 latest/release 都是 0.7.12-alpha；
/// 同一目录下 `cleanroom-{v}-installer.jar`（15.2 MB）与 `-universal.jar`（12.7 MB）
/// 都在，installer 里带 install_profile.json，形状和 Forge 一样。
/// </summary>
public sealed class CleanroomProvider : ForgeLikeProvider
{
    public const string SupportedMcVersion = "1.12.2";
    public const string DefaultMavenBase = "https://repo.cleanroommc.com/releases";

    private readonly string _mavenBase;

    public CleanroomProvider(string? mavenBase = null)
        => _mavenBase = (mavenBase ?? DefaultMavenBase).TrimEnd('/');

    public override LoaderKind Kind => LoaderKind.Cleanroom;

    protected override string MavenBase => _mavenBase;

    /// <summary>maven 坐标：com.cleanroommc:cleanroom。</summary>
    protected override string InstallerGroupPath => "com/cleanroommc/cleanroom";

    protected override string InstallerArtifactId(string loaderVersion) => "cleanroom";

    /// <summary>Cleanroom 只有 1.12.2 这一条线。</summary>
    protected override string? McToLoaderPrefix(string mcVersion)
        => mcVersion.StartsWith(SupportedMcVersion, StringComparison.Ordinal) ? "" : null;

    /// <summary>maven 目录名就是版本号本身（0.7.12-alpha）。</summary>
    protected override string InstallerVersionKey(string mcVersion, string loaderVersion)
        => loaderVersion;

    protected override string ResultVersionIdFor(string mcVersion, string loaderVersion)
        => $"{mcVersion}-cleanroom-{loaderVersion}";

    public override async Task<IReadOnlyList<LoaderVersion>> ListVersionsAsync(
        string mcVersion, CancellationToken ct = default)
    {
        if (!mcVersion.StartsWith(SupportedMcVersion, StringComparison.Ordinal)) return [];

        string xml;
        try
        {
            xml = await LoaderHttp
                .GetStringAsync($"{_mavenBase}/{InstallerGroupPath}/maven-metadata.xml", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LoaderHttp.Explain(ex, "获取 Cleanroom 版本列表"), ex);
        }

        var versions = Regex.Matches(xml, @"<version>\s*([^<\s]+)\s*</version>")
            .Select(m => m.Groups[1].Value)
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return versions
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))
            .Select(v => new LoaderVersion(
                LoaderKind.Cleanroom,
                mcVersion,
                v,

                // Cleanroom 目前发布的都是 -alpha，没有"稳定版"这回事。
                // StabilityKnown 就是说"这个字段这次查询里没意义"，
                // 免得界面把 0.7.12-alpha 写成"测试版"、显得像是我们判断错了。
                IsStable: false,
                InstallerUrlFor(v),
                _mavenBase)
            {
                StabilityKnown = false,
            })
            .ToList();
    }
}