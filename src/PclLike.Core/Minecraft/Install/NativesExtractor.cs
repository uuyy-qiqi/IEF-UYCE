using System.IO.Compression;
using PclLike.Core.Minecraft;

namespace PclLike.Core.Minecraft.Install;

/// <summary>
/// 原生库（.dll/.so/.dylib）解压。
///
/// 规则（照抄官方行为）：
///   - 把 natives jar 里的文件解到版本目录下的 natives/ 子目录
///   - 排除 META-INF/（里面有签名，解出来也没用还可能冲突）
///   - 排除其他平台的二进制：一个 natives jar 里常同时放着 windows/linux/macos 三套
///   - 同名文件后解压的覆盖先解压的（不同库可能带同一个 dll）
///
/// 第二个坑最容易被忽略：1.19 之前 natives jar 里是平铺的，之后的版本
/// 有些打包成带目录结构，需要按目录判断平台。
/// </summary>
public static class NativesExtractor
{
    public sealed record ExtractResult(int Extracted, int Skipped, List<string> Files);

    public static ExtractResult Extract(string nativeJar, string nativesDir, string excludePrefix = "META-INF/")
    {
        Directory.CreateDirectory(nativesDir);
        var extracted = new List<string>();
        var skipped = 0;

        using var zip = ZipFile.OpenRead(nativeJar);
        foreach (var entry in zip.Entries)
        {
            // 目录项跳过
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var full = entry.FullName.Replace('\\', '/');

            if (!string.IsNullOrEmpty(excludePrefix) &&
                full.StartsWith(excludePrefix, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            if (IsForeignPlatform(full))
            {
                skipped++;
                continue;
            }

            // 取文件名，丢掉 jar 内的目录结构（LWJGL 期望 dll 直接平铺在 natives 里）
            var destName = Path.GetFileName(full);
            if (string.IsNullOrEmpty(destName)) continue;

            var dest = Path.Combine(nativesDir, destName);

            // 目标文件可能正被上一局游戏占着（Windows 释放 dll 句柄有延迟）。
            // 那种情况下**不能**把整个安装搞失败——正在用的 dll 本来就是我们
            // 刚写进去的那一份，内容一样，跳过即可。只有内容确实不同才值得重试。
            if (!TryExtract(entry, dest))
            {
                if (File.Exists(dest) && SameContent(entry, dest))
                {
                    skipped++;
                    continue;
                }

                // 文件在、内容却不同、还写不进去 —— 这才是真问题
                throw new IOException(
                    $"无法写入原生库 {destName}：文件被占用。请先关闭正在运行的游戏进程。");
            }

            extracted.Add(destName);
        }

        return new ExtractResult(extracted.Count, skipped, extracted);
    }

    /// <summary>单个条目的解压，失败返回 false 而不是抛。</summary>
    private static bool TryExtract(ZipArchiveEntry entry, string dest)
    {
        try
        {
            entry.ExtractToFile(dest, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>比较 jar 内条目与磁盘文件的内容是否一致（大小 + 长度前缀比对）。</summary>
    private static bool SameContent(ZipArchiveEntry entry, string dest)
    {
        try
        {
            var fi = new FileInfo(dest);
            if (!fi.Exists || fi.Length != entry.Length) return false;

            using var a = entry.Open();
            using var b = File.OpenRead(dest);

            var bufA = new byte[4096];
            var bufB = new byte[4096];

            while (true)
            {
                var ra = a.Read(bufA, 0, bufA.Length);
                var rb = b.Read(bufB, 0, bufB.Length);

                if (ra != rb) return false;
                if (ra == 0) return true;

                if (!bufA.AsSpan(0, ra).SequenceEqual(bufB.AsSpan(0, rb))) return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断 jar 内条目是否属于其它平台。只看路径前缀，避免误伤普通文件名。</summary>
    private static bool IsForeignPlatform(string entryPath)
    {
        var lower = entryPath.ToLowerInvariant();
        var segments = lower.Split('/');

        // 只看目录部分
        for (var i = 0; i < segments.Length - 1; i++)
        {
            switch (segments[i])
            {
                case "windows" or "win32" or "win64":
                    if (!OperatingSystem.IsWindows()) return true;
                    break;
                case "linux" or "linux-x86-64" or "linux-aarch64":
                    if (!OperatingSystem.IsLinux()) return true;
                    break;
                case "macos" or "osx" or "darwin" or "macosx":
                    if (!OperatingSystem.IsMacOS()) return true;
                    break;
            }
        }

        // 按后缀兜底：jar 里混放 .dll/.so/.dylib 时
        var ext = Path.GetExtension(lower);
        return ext switch
        {
            ".dll" => !OperatingSystem.IsWindows(),
            ".dylib" => !OperatingSystem.IsMacOS(),
            ".so" => !OperatingSystem.IsLinux(),
            _ => false,
        };
    }

    /// <summary>
    /// 清空并重建 natives 目录。切换版本时必须清，
    /// 否则旧版本的 dll 会被新版本加载，表现为莫名其妙的 UnsatisfiedLinkError。
    ///
    /// 容忍"删不掉"：上一局游戏刚结束、Windows 还没释放 dll 句柄时，
    /// Directory.Delete 会抛 UnauthorizedAccessException。那种情况下
    /// 保留删不掉的文件继续跑，让 Extract 的 SameContent 判断来处理——
    /// 内容相同的会跳过，不同的才报错。直接抛异常会让用户每次都要等几十秒。
    /// </summary>
    public static void Reset(string nativesDir)
    {
        if (!Directory.Exists(nativesDir))
        {
            Directory.CreateDirectory(nativesDir);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(nativesDir))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 被占用，留着让 Extract 判断内容
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // 尽量删子目录（LWJGL 3 的 natives jar 可能带目录结构解出来）
        foreach (var dir in Directory.EnumerateDirectories(nativesDir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}