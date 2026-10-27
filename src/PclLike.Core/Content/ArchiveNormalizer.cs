using System.IO.Compression;
using SharpCompress.Readers;

namespace PclLike.Core.Content;

/// <summary>
/// 把「不是 zip」的压缩包换成 zip，让后面的代码只认一种格式。
///
/// ## 为什么要这一层
/// 国内分享的整合包有一大半是 .rar / .7z（百度网盘、蓝奏云，作者图省事）。
/// 之前只认 .zip 和 .mrpack，用户拖一个 .7z 进来，启动器只会说"不认识 .7z"。
/// 功能其实全都在，卡在"解不开"这一步。
///
/// 所以这里把 rar/7z/tar/gz 统一**解出来再打成 zip**，然后把这个临时 zip
/// 当成用户拖进来的文件继续走。分类器、Modrinth 安装器、CurseForge 安装器、
/// 懒人包导入器全都不用改 —— 它们本来就只吃 zip。
///
/// ## 为什么不直接给每个下游加"任意压缩包"重载
/// 那要改四五个类、每个类里再分叉一次，而且 N 个地方各解一遍。
/// 统一换成 zip 只在这一处解一次，代价是多占一份临时磁盘（导入完就清）。
/// </summary>
public static class ArchiveNormalizer
{
    /// <summary>需要换格式的扩展名（zip 与 mrpack 本来就是 zip 容器，不动）。</summary>
    public static bool NeedsNormalize(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        return ext is ".rar" or ".7z" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".cab";
    }

    /// <summary>
    /// 换成 zip，返回临时 zip 的完整路径。
    ///
    /// 同一个文件重复拖不会重复解：落盘名字带源文件的长度 + 修改时间，
    /// 已经存在就直接复用。
    /// </summary>
    /// <exception cref="InvalidDataException">解不开（加密包、残缺包、未知算法）。</exception>
    public static string ToZip(string path)
    {
        var info = new FileInfo(path);
        var cache = CacheDir();
        Directory.CreateDirectory(cache);

        var stem = SafeStem(Path.GetFileNameWithoutExtension(path));
        var tag = $"{info.Length:x}_{info.LastWriteTimeUtc.Ticks:x}";
        var target = Path.Combine(cache, $"{stem}_{tag}.zip");

        if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

        // 先写 .part 再改名：解到一半被杀掉不会留下一个"看起来能用"的半截 zip
        var part = target + ".part";

        try
        {
            using (var input = File.OpenRead(path))
            using (var reader = ReaderFactory.Open(input))
            using (var output = File.Create(part))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var count = 0;

                while (reader.MoveToNextEntry())
                {
                    var entry = reader.Entry;
                    if (entry.IsDirectory) continue;
                    if (entry.Key is null) continue;

                    var name = entry.Key.Replace('\\', '/').TrimStart('/');

                    // 路径穿越：../ 或者绝对路径的条目直接丢掉，不写到包外
                    if (name.Length == 0 || name.Contains("../", StringComparison.Ordinal)) continue;
                    if (name.Length > 1 && name[1] == ':') continue;

                    var z = zip.CreateEntry(name, CompressionLevel.Fastest);
                    using var es = z.Open();
                    reader.WriteEntryTo(es);
                    count++;
                }

                if (count == 0)
                    throw new InvalidDataException("压缩包里没有能解出来的文件");
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);

            return target;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>
    /// 清掉一天前的临时 zip。
    ///
    /// 导入成功后调用方会立刻删，但用户取消、程序被杀、导入报错这些路径
    /// 都会漏下文件。每次导入前顺手扫一遍，防止 %LOCALAPPDATA% 越攒越大。
    /// </summary>
    public static void CleanupOld()
    {
        try
        {
            var dir = CacheDir();
            if (!Directory.Exists(dir)) return;

            var cutoff = DateTime.UtcNow.AddDays(-1);

            foreach (var f in Directory.EnumerateFiles(dir, "*.zip"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
                }
                catch
                {
                    // 正被读着就跳过，下次再说
                }
            }
        }
        catch
        {
            // 清理失败不影响导入
        }
    }

    /// <summary>导入完成后删掉临时 zip（源文件不动）。</summary>
    public static void Release(string zipPath)
    {
        try
        {
            var dir = CacheDir();
            if (zipPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) TryDelete(zipPath);
        }
        catch
        {
            // 删不掉就留给 CleanupOld
        }
    }

    private static string CacheDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PclLike",
        "import-cache");

    /// <summary>文件名里的非法字符换掉，避免路径拼出问题。</summary>
    private static string SafeStem(string stem)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();

        if (s.Length == 0) s = "pack";
        if (s.Length > 60) s = s[..60];

        return s;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}