using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclLike.Core.Minecraft.Launch;

/// <summary>
/// Java 运行时自动下载与管理。
///
/// 为什么要自己下载：靠扫描用户机器找 Java 永远不可靠 ——
/// 用户可能一个合适版本都没有，或者装了但不在 PATH/注册表里（IDEA 的 .jdks、Gradle toolchain 都是这样）。
/// PCL2 / HMCL / 官方启动器的做法都是自己把 JRE 下到固定目录，从此不再猜。
///
/// 数据源用 Adoptium (Eclipse Temurin) 的公开 API，理由：
///   - 不需要 API key，不需要鉴权
///   - 覆盖 Java 8 / 11 / 17 / 21，正好是 MC 需要的全部版本
///   - Windows x64 提供 .zip，比 .msi 好处理（不需要管理员权限、不写注册表）
///   - 是 Eclipse 基金会项目，长期存在
///
/// 目录布局（和 PCL 一致，便于迁移）：
///   {gameRoot}\runtime\{platform}\{vendor}\jdk-{major}.{patch}\bin\java.exe
/// 例如：
///   .pcllike\runtime\windows-x64\temurin\jdk-17.0.20.1\bin\java.exe
///
/// 下载后会在目录里放一个 .pcllike-runtime.json 标记文件，记录版本与来源。
/// 有了标记就不用每次重新探测 java.exe 的版本 —— Probe 要起一个进程，几百毫秒，启动时不该付这个成本。
/// </summary>
public sealed class JavaRuntimeManager
{
    private const string AdoptiumApi = "https://api.adoptium.net/v3";

    private readonly string _gameRoot;
    private readonly HttpClient _http;

    public JavaRuntimeManager(string gameRoot, HttpClient? http = null)
    {
        _gameRoot = gameRoot;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>本机对应的 Adoptium 平台标识。</summary>
    public static string Platform
    {
        get
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
                System.Runtime.InteropServices.Architecture.X86 => "x32",
                _ => "x64",
            };
            var os = OperatingSystem.IsWindows() ? "windows"
                   : OperatingSystem.IsMacOS() ? "mac"
                   : "linux";
            return $"{os}-{arch}";
        }
    }

    /// <summary>运行时根目录。</summary>
    public string RuntimeRoot => Path.Combine(_gameRoot, "runtime");

    /// <summary>某个大版本对应的 java.exe 路径；没装过返回 null。</summary>
    public string? GetInstalledPath(int majorVersion)
    {
        foreach (var (major, path, _) in ListInstalled())
        {
            if (major == majorVersion) return path;
        }
        return null;
    }

    /// <summary>是否已装过某个大版本。</summary>
    public bool IsInstalled(int majorVersion) => GetInstalledPath(majorVersion) is not null;

    /// <summary>
    /// 确保某个大版本的 Java 可用。已装则直接返回路径，没装则下载解压。
    /// </summary>
    public async Task<string> EnsureAsync(
        int majorVersion,
        IProgress<(string Stage, long Done, long Total)>? progress = null,
        CancellationToken ct = default)
    {
        var existing = GetInstalledPath(majorVersion);
        if (existing is not null)
        {
            progress?.Report(($"Java {majorVersion} 已安装", 1, 1));
            return existing;
        }

        progress?.Report(($"查询 Java {majorVersion} 下载地址", 0, 1));

        var pkg = await QueryPackageAsync(majorVersion, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"找不到 Java {majorVersion} 的可用下载包（平台 {Platform}）。请手动安装 Java {majorVersion} 后在设置里指定路径。");

        var targetDir = Path.Combine(RuntimeRoot, Platform, "temurin", $"jdk-{pkg.MajorVersion}.{pkg.PatchVersion}");
        var zipPath = Path.Combine(RuntimeRoot, $".download-java{pkg.MajorVersion}.zip");

        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        try
        {
            // 1. 下载压缩包
            await DownloadFileAsync(pkg.Link, zipPath, pkg.Size, progress, ct).ConfigureAwait(false);

            // 2. 校验
            progress?.Report(("校验下载包", 0, 1));
            var actual = await Sha256Async(zipPath, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(pkg.Checksum) &&
                !string.Equals(actual, pkg.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Java 运行时校验失败：期望 {pkg.Checksum}，实际 {actual}。文件可能被截断或篡改。");
            }

            // 3. 解压
            progress?.Report(("解压运行时", 0, 1));
            ExtractPackage(zipPath, targetDir);

            // 4. 写标记
            var marker = new RuntimeMarker
            {
                MajorVersion = pkg.MajorVersion,
                FullVersion = pkg.FullVersion,
                Vendor = "temurin",
                Platform = Platform,
                Source = pkg.Link,
                InstalledAt = DateTimeOffset.Now,
            };
            await File.WriteAllTextAsync(
                Path.Combine(targetDir, ".pcllike-runtime.json"),
                JsonSerializer.Serialize(marker, JsonOpts), ct).ConfigureAwait(false);

            var exe = FindJavaExe(targetDir)
                ?? throw new InvalidOperationException($"解压完成但没找到 java.exe，目录：{targetDir}");

            progress?.Report(($"Java {pkg.MajorVersion} 安装完成", 1, 1));
            return exe;
        }
        finally
        {
            // 压缩包不管成功失败都删 —— 占几百 MB，留着没意义
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* 忽略 */ }
        }
    }

    /// <summary>列出已安装的运行时。</summary>
    public IEnumerable<(int Major, string Path, RuntimeMarker? Marker)> ListInstalled()
    {
        if (!Directory.Exists(RuntimeRoot)) yield break;

        foreach (var vendorDir in SafeDirs(Path.Combine(RuntimeRoot, Platform)))
        {
            foreach (var jdkDir in SafeDirs(vendorDir))
            {
                var exe = FindJavaExe(jdkDir);
                if (exe is null) continue;

                RuntimeMarker? marker = null;
                var mf = Path.Combine(jdkDir, ".pcllike-runtime.json");
                if (File.Exists(mf))
                {
                    try { marker = JsonSerializer.Deserialize<RuntimeMarker>(File.ReadAllText(mf), JsonOpts); }
                    catch { /* 标记坏了不影响使用，只是少了版本信息 */ }
                }

                yield return (marker?.MajorVersion ?? ParseMajorFromDirName(jdkDir), exe, marker);
            }
        }
    }

    /// <summary>删掉某个大版本的运行时（重装/清理用）。</summary>
    public bool Remove(int majorVersion)
    {
        var removed = false;
        foreach (var (major, path, _) in ListInstalled().ToList())
        {
            if (major != majorVersion) continue;
            // path 是 ...\jdk-17.0.20.1\bin\java.exe，往上两级才是运行时目录
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (dir is null || !Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, recursive: true); removed = true; } catch { /* 文件占用，忽略 */ }
        }
        return removed;
    }

    // ── 内部实现 ──

    /// <summary>查 Adoptium 拿某个大版本的最新 release 包信息。</summary>
    private async Task<PackageInfo?> QueryPackageAsync(int majorVersion, CancellationToken ct)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
        var arch = Platform.Split('-')[1];

        var url = $"{AdoptiumApi}/assets/latest/{majorVersion}/hotspot" +
                  $"?architecture={arch}&image_type=jdk&os={os}&vendor=eclipse";

        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);

        // /assets/latest/ 返回的是数组（一个平台一条），即使是单个结果也包在数组里。
        // /assets/feature_releases/ 那些端点也一样，所以统一按数组处理。
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0) return null;
            root = root[0];
        }
        else if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty("binary", out var binary)) return null;
        if (!binary.TryGetProperty("package", out var pkg)) return null;
        if (!root.TryGetProperty("version", out var version)) return null;

        return new PackageInfo
        {
            MajorVersion = version.GetProperty("major").GetInt32(),
            PatchVersion = version.TryGetProperty("patch", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
            FullVersion = version.TryGetProperty("semver", out var sv) ? sv.GetString() ?? "" : "",
            Link = pkg.GetProperty("link").GetString() ?? throw new InvalidOperationException("下载包缺少 link 字段"),
            Checksum = pkg.GetProperty("checksum").GetString() ?? "",
            Size = pkg.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
        };
    }

    private async Task DownloadFileAsync(
        string url, string dest, long expectedSize,
        IProgress<(string Stage, long Done, long Total)>? progress,
        CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? expectedSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);

        var buf = new byte[81920];
        long done = 0;
        long lastReport = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;

            // 每 1% 报一次，避免刷爆 UI 线程
            if (total > 0 && done - lastReport >= total / 100)
            {
                lastReport = done;
                progress?.Report(($"下载 Java 运行时 {done * 100 / total}%", done, total));
            }
        }
    }

    /// <summary>
    /// 解压 Adoptium 的 zip。包内顶层固定是一个 jdk-xxx 目录，
    /// 要把它里面的内容提到 targetDir，否则会出现 jdk-17/bin/java.exe 套娃。
    /// </summary>
    private static void ExtractPackage(string zipPath, string targetDir)
    {
        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
        Directory.CreateDirectory(targetDir);

        using var zip = ZipFile.OpenRead(zipPath);

        // 找顶层的公共前缀
        var prefix = FindCommonTopLevel(zip);

        foreach (var entry in zip.Entries)
        {
            var rel = entry.FullName;
            if (!string.IsNullOrEmpty(prefix))
            {
                if (rel.Length <= prefix.Length) continue;      // 就是那个顶层目录本身
                rel = rel[prefix.Length..];
            }
            if (string.IsNullOrEmpty(rel)) continue;
            if (rel.EndsWith('/') || rel.EndsWith('\\')) continue;   // 目录项

            var outPath = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));

            // 防目录穿越：Adoptium 的包不会有 ../，但别人的包可能有
            var fullOut = Path.GetFullPath(outPath);
            if (!fullOut.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"压缩包内含非法路径：{entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);
            entry.ExtractToFile(fullOut, overwrite: true);
        }

        // Unix 上要保留可执行位，Windows 不需要
        if (!OperatingSystem.IsWindows())
        {
            foreach (var f in new[] { "bin/java", "bin/javac", "bin/keytool" })
            {
                var p = Path.Combine(targetDir, f);
                if (File.Exists(p)) TryChmodPlusX(p);
            }
        }
    }

    private static string FindCommonTopLevel(ZipArchive zip)
    {
        string? prefix = null;
        foreach (var e in zip.Entries)
        {
            var slash = e.FullName.IndexOf('/');
            if (slash < 0) return "";      // 有文件直接躺在根，说明没有统一前缀

            var top = e.FullName[..(slash + 1)];
            if (prefix is null) prefix = top;
            else if (!string.Equals(prefix, top, StringComparison.Ordinal)) return "";
        }
        return prefix ?? "";
    }

    /// <summary>Windows 上不调用（调用点已有 IsWindows 判断）。用 UnsupportedOSPlatform 而非
    /// SupportedOSPlatform，是因为调用点在 if 里而不是独立方法，标注成"仅支持 linux/mac"反而会让
    /// 调用点报警；声明"不支持 windows"才能真正消除 CA1416。</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void TryChmodPlusX(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { /* 不支持则忽略 */ }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? FindJavaExe(string root)
    {
        var exe = OperatingSystem.IsWindows() ? "java.exe" : "java";
        // 直接布局
        var direct = Path.Combine(root, "bin", exe);
        if (File.Exists(direct)) return direct;

        // 套了一层（jdk-17.0.20.1+1\bin\java.exe）
        foreach (var sub in SafeDirs(root))
        {
            var p = Path.Combine(sub, "bin", exe);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    private static int ParseMajorFromDirName(string dir)
    {
        var name = Path.GetFileName(dir);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"(?:jdk|jre)-?(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static IEnumerable<string> SafeDirs(string parent)
    {
        if (!Directory.Exists(parent)) return [];
        try { return Directory.EnumerateDirectories(parent); }
        catch { return []; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record PackageInfo
    {
        public int MajorVersion { get; init; }
        public int PatchVersion { get; init; }
        public string FullVersion { get; init; } = "";
        public required string Link { get; init; }
        public string Checksum { get; init; } = "";
        public long Size { get; init; }
    }

    /// <summary>安装标记文件的内容。</summary>
    public sealed record RuntimeMarker
    {
        public int MajorVersion { get; init; }
        public string FullVersion { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Source { get; init; } = "";
        public DateTimeOffset InstalledAt { get; init; }
    }
}