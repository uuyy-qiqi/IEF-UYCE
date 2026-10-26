using System.IO;
using System.IO.Compression;
using System.Text;

namespace PclLike.Core.Minecraft;

/// <summary>一个「和当前加载器不是一路」的 Mod：文件名 + 一句话原因。</summary>
public sealed record LoaderMismatch(string FileName, string Reason);

/// <summary>
/// 启动前抽检：实例里的 Mod 是不是按**别的加载器**编出来的。
///
/// 为什么需要这个：Mod 装错了加载器，游戏不会给你一句人话。真实案例 ——
/// 一个包声明用 Forge 47.4.0，但包里某个 Mod 是按 NeoForge 1.20.1 编的，
/// 它引用了 NeoForge 才有的接口 <c>net.minecraftforge.client.extensions.IAbstractWidgetExtension</c>。
/// Forge 里根本没有那个类（已核实：官方 47.4.0 的 universal/client jar 都没有），
/// 于是 modlauncher 在**生成崩溃报告**的过程中炸掉，最终只留下
///   java.lang.ClassNotFoundException: ...IAbstractWidgetExtension
/// 而连一个 crash-report 文件都写不出来 —— 用户看到的就是「启动失败」，无从下手。
///
/// 这里只看每个 jar 的**元数据**（META-INF 下那几个小文件），不去扫 class 字节：
/// 270 个 Mod 全量扫类要几分钟，而「装错加载器」这件事在元数据里就写着。
/// </summary>
public static class ModLoaderAudit
{
    /// <summary>加载器家族。传 "forge" / "neoforge" / "fabric" / "quilt" / "vanilla"。</summary>
    public static IReadOnlyList<LoaderMismatch> Against(string? modsDir, string loaderFamily)
    {
        var result = new List<LoaderMismatch>();
        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) return result;

        var family = (loaderFamily ?? "").Trim().ToLowerInvariant();

        string[] jars;
        try { jars = Directory.GetFiles(modsDir, "*.jar"); }
        catch { return result; }

        foreach (var jar in jars)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries) names.Add(e.FullName);

                var hasNeoForgeToml = names.Contains("META-INF/neoforge.mods.toml");
                var hasModsToml = names.Contains("META-INF/mods.toml");
                var hasFabricJson = names.Contains("fabric.mod.json");
                var hasQuiltJson = names.Contains("quilt.mod.json");

                // mods.toml 很小，读出来看依赖里有没有点名 neoforge / forge
                var needsNeo = hasNeoForgeToml;
                var needsForge = false;
                if (hasModsToml && !hasNeoForgeToml)
                {
                    var toml = ReadEntry(zip, "META-INF/mods.toml");
                    if (toml is not null)
                    {
                        foreach (var line in toml.Split('\n'))
                        {
                            var t = line.Trim();
                            if (t.StartsWith("#")) continue;
                            // [dependencies.xxx] 段里的 modId="neoforge"
                            if (t.Contains("modId=\"neoforge\"", StringComparison.OrdinalIgnoreCase)
                                || t.Contains("modId=\"forge\"", StringComparison.OrdinalIgnoreCase))
                            {
                                if (t.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) needsNeo = true;
                                else needsForge = true;
                            }
                        }
                    }
                }

                var reason = family switch
                {
                    "forge" => hasNeoForgeToml ? "带 META-INF/neoforge.mods.toml，是 NeoForge 专用的"
                             : needsNeo ? "依赖 neoforge（NeoForge 专用）"
                             : hasFabricJson && !hasModsToml ? "只有 fabric.mod.json，是 Fabric 专用的"
                             : hasQuiltJson && !hasModsToml ? "只有 quilt.mod.json，是 Quilt 专用的"
                             : null,
                    "neoforge" => hasModsToml && !hasNeoForgeToml && needsForge ? "依赖 forge（Forge 专用）"
                                : hasFabricJson && !hasModsToml && !hasNeoForgeToml ? "只有 fabric.mod.json，是 Fabric 专用的"
                                : null,
                    "fabric" => hasModsToml || hasNeoForgeToml ? "带 META-INF/mods.toml，是 Forge/NeoForge 专用的" : null,
                    "quilt" => hasModsToml || hasNeoForgeToml ? "带 META-INF/mods.toml，是 Forge/NeoForge 专用的" : null,
                    _ => null,
                };

                if (reason is not null)
                    result.Add(new LoaderMismatch(Path.GetFileName(jar), reason));
            }
            catch
            {
                // 坏 jar 交给「校验并补全」去管，这里只管加载器不对
            }
        }

        return result;
    }

    /// <summary>从当前加载器版本 id 推断家族：1.20.1-forge-47.4.0 → forge；neoforge-47.1.106 → neoforge。</summary>
    public static string FamilyOf(string? versionId)
    {
        var id = (versionId ?? "").ToLowerInvariant();
        if (id.Contains("neoforge")) return "neoforge";
        if (id.Contains("forge")) return "forge";
        if (id.Contains("fabric")) return "fabric";
        if (id.Contains("quilt")) return "quilt";
        return "vanilla";
    }

    private static string? ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e is null || e.Length > 512 * 1024) return null;
        using var s = e.Open();
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}