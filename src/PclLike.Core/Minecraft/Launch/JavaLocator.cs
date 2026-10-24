using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PclLike.Core.Minecraft.Launch;

/// <summary>
/// Java 运行时定位。
///
/// 优先级（和主流启动器一致）：
///   1. 用户在实例里显式指定的 java.exe
///   2. 启动器自带的 runtime（jre_x64 / runtime 目录）
///   3. JAVA_HOME
///   4. 注册表 / Program Files 下的已知厂商目录（Adoptium / Zulu / Oracle / Microsoft）
///   5. PATH 里的 java
///
/// 版本匹配：每个版本 JSON 都有 javaVersion.majorVersion
/// （1.17 及以前是 8，1.18~1.20.4 是 17，1.20.5+ 是 21）。
/// 用错版本的表现是启动即崩，错误信息还不直观，所以这里要主动校验并给提示。
/// </summary>
public static class JavaLocator
{
    public sealed record JavaInfo(string Path, int MajorVersion, string RawVersion, string Vendor)
    {
        public bool Is64Bit { get; init; }

        /// <summary>给界面列表用的短标签，例如"Java 17"。</summary>
        public string VersionLabel => $"Java {MajorVersion}";

        /// <summary>厂商；探测不出来时说"未知厂商"而不是留空。</summary>
        public string VendorLabel =>
            string.IsNullOrWhiteSpace(Vendor) ? "未知厂商" : Vendor.Trim();
    }

    public static readonly Regex VersionRegex =
        new(@"(?:java|openjdk) version ""?(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>探测一个 java 可执行文件的版本信息。</summary>
    public static JavaInfo? Probe(string javaExe)
    {
        if (!File.Exists(javaExe)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-XshowSettings:properties -version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            var major = ParseMajor(output);
            if (major <= 0) return null;

            var vendor = "unknown";
            var vendorMatch = Regex.Match(output, @"java\.vendor\s*=\s*(.+)");
            if (vendorMatch.Success) vendor = vendorMatch.Groups[1].Value.Trim();

            var is64 = output.Contains("sun.arch.data.model = 64", StringComparison.Ordinal) ||
                       output.Contains("os.arch = amd64", StringComparison.Ordinal) ||
                       output.Contains("os.arch = x86_64", StringComparison.Ordinal);

            var raw = Regex.Match(output, @"(?:java|openjdk) version ""?([^""\r\n]+)").Groups[1].Value.Trim();

            return new JavaInfo(javaExe, major, raw, vendor) { Is64Bit = is64 };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 -version 输出解析主版本号。1.8.0_401 → 8；17.0.9 → 17。</summary>
    public static int ParseMajor(string versionOutput)
    {
        var m = VersionRegex.Match(versionOutput);
        if (!m.Success) return -1;

        var first = int.Parse(m.Groups[1].Value);
        if (first == 1 && m.Groups[2].Success)
            return int.Parse(m.Groups[2].Value);   // 1.8 → 8

        return first;
    }

    /// <summary>按优先级找出所有可用的 Java。</summary>
    public static List<JavaInfo> Discover(string? gameRoot = null, string? explicitJava = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(explicitJava)) candidates.Add(explicitJava);

        if (!string.IsNullOrEmpty(gameRoot))
        {
            // 启动器自带的运行时目录，PCL 也是这套布局
            foreach (var sub in new[] { "runtime", "jre_x64", "jre", "java" })
            {
                var dir = Path.Combine(gameRoot, sub);
                if (!Directory.Exists(dir)) continue;
                candidates.AddRange(SafeEnumerateJava(dir));
            }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
            candidates.Add(Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"));

        candidates.AddRange(ScanKnownVendorDirs());
        candidates.AddRange(ScanUserJdkDirs());

        var pathJava = FindOnPath("java");
        if (pathJava is not null) candidates.Add(pathJava);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<JavaInfo>();
        foreach (var c in candidates)
        {
            var full = SafeFullPath(c);
            if (full is null || !seen.Add(full)) continue;

            var info = Probe(full);
            if (info is not null && info.MajorVersion > 0) result.Add(info);
        }

        return result.OrderByDescending(j => j.MajorVersion).ToList();
    }

    private static IEnumerable<string> SafeEnumerateJava(string dir)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "java.exe", SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var f in files) yield return f;
    }

    /// <summary>扫 Program Files 下常见的 JDK 安装位置，避免漏掉手动装的。</summary>
    private static IEnumerable<string> ScanKnownVendorDirs()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            @"C:\Program Files\Java",
        };

        var vendors = new[]
        {
            @"Eclipse Adoptium", @"Java", @"Zulu", @"Microsoft\jdk", @"Amazon Corretto",
            @"BellSoft\LibericaJDK", @"Semeru", @"Temurin",
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            foreach (var vendor in vendors)
            {
                var vdir = Path.Combine(root, vendor);
                if (!Directory.Exists(vdir)) continue;

                foreach (var j in SafeEnumerateJava(vdir)) yield return j;
            }

            // 直接放在 Program Files 下的 jdk-xx
            foreach (var d in SafeEnumerateDirectories(root, "*jdk*"))
            {
                var exe = Path.Combine(d, "bin", "java.exe");
                if (File.Exists(exe)) yield return exe;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir, string pattern)
    {
        try { return Directory.EnumerateDirectories(dir, pattern, SearchOption.TopDirectoryOnly); }
        catch { return []; }
    }

    /// <summary>
    /// 扫用户主目录下的 JDK 存放点。这些位置没有注册表记录，也不在 Program Files，
    /// 但现实中工具链会把 JDK 丢在这里：
    ///   ~\.jdks\        IntelliJ IDEA / 手动解压的 Temurin
    ///   ~\.gradle\jdks\ Gradle toolchain 自动下载
    ///   ~\.sdkman\candidates\java\
    ///   ~\scoop\apps\  scoop install temurin17-jdk
    ///   ~\.m2\toolchains\
    ///   ~\.pcllike-runtimes\  本项目自己的运行时目录
    /// 这些都是"用户装过但 PATH 里没有"的 Java，漏掉就会误用系统 Java 导致崩。
    /// </summary>
    private static IEnumerable<string> ScanUserJdkDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) yield break;

        var roots = new[]
        {
            Path.Combine(home, ".jdks"),
            Path.Combine(home, ".gradle", "jdks"),
            Path.Combine(home, ".sdkman", "candidates", "java"),
            Path.Combine(home, "scoop", "apps"),
            Path.Combine(home, ".m2", "toolchains"),
            Path.Combine(home, ".pcllike-runtimes"),
            Path.Combine(home, ".minecraft", "runtime"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            // 顶层直接是 JDK 家目录的情况（~\.jdks\jdk-17.0.20.1+1\bin\java.exe）
            var direct = Path.Combine(root, "bin", "java.exe");
            if (File.Exists(direct)) yield return direct;

            foreach (var j in SafeEnumerateJava(root)) yield return j;
        }
    }

    public static string? FindOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var full = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? exe + ".exe" : exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string? SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return null; }
    }

    /// <summary>给版本挑一个合适的 Java；找不到就返回 null，调用方负责提示用户下载。</summary>
    public static JavaInfo? SelectFor(IEnumerable<JavaInfo> available, int requiredMajor)
        => SelectFor(available, requiredMajor, out _);

    /// <summary>
    /// 带匹配质量的版本。quality 说明这次是怎么选出来的，调用方据此决定是否弹提示/触发下载：
    ///   Exact    —— 版本号正好对上，没问题
    ///   Higher   —— 用了更高的版本，跨度在容忍范围内，通常能跑
    ///   Fallback —— 用了更低或跨度过大的版本，大概率崩，应该提示用户装正确的
    ///
    /// 为什么要有容忍上限：Java 8 的 MC 版本跑在 Java 24 上必崩（ASM 读不了高版本 class），
    /// Java 21 的需求给 Java 24 却能跑。跨度超过 4 就是不可接受的降级，
    /// 与其硬塞一个然后让用户看一堆 ISE 堆栈，不如明确报低质量，让上层去下载正确运行时。
    /// </summary>
    public static JavaInfo? SelectFor(IEnumerable<JavaInfo> available, int requiredMajor, out MatchQuality quality)
    {
        var list = available.ToList();
        quality = MatchQuality.None;
        if (list.Count == 0) return null;

        // 1. 精确匹配
        var exact = list.FirstOrDefault(j => j.MajorVersion == requiredMajor);
        if (exact is not null) { quality = MatchQuality.Exact; return exact; }

        // 2. 更高的版本，但跨度要可控
        var higher = list.Where(j => j.MajorVersion > requiredMajor)
                         .OrderBy(j => j.MajorVersion)
                         .FirstOrDefault();
        if (higher is not null && higher.MajorVersion - requiredMajor <= MaxUpgradeSpan)
        {
            quality = MatchQuality.Higher;
            return higher;
        }

        // 3. 兜底：比要求低，或者高得离谱。能用，但必须让用户知道有问题
        var fallback = list.Where(j => j.MajorVersion < requiredMajor)
                           .OrderByDescending(j => j.MajorVersion)
                           .FirstOrDefault() ?? higher;
        if (fallback is not null) quality = MatchQuality.Fallback;
        return fallback;
    }

    /// <summary>允许向上跨越多少个 Java 大版本。超过就视为不可用。</summary>
    public const int MaxUpgradeSpan = 4;

    public enum MatchQuality { None, Exact, Higher, Fallback }
}