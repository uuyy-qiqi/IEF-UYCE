using System.IO.Compression;
using PclLike.Core.Storage;

namespace PclLike.Core.Content;

/// <summary>
/// 一个存档的概况，纯粹给界面显示用。
///
/// 世界在磁盘上就是一个目录（`saves/&lt;名字&gt;/`），所以"名字"必须同时是
/// 显示名和目录名 —— 分开存两份的话，用户改了显示名就得猜哪个目录对应它。
/// </summary>
/// <param name="Name">世界名（= 目录名）。</param>
/// <param name="Path">世界目录的完整路径。</param>
/// <param name="SizeBytes">占用字节数。</param>
/// <param name="LastWrite">最后一次修改时间（UTC），取不到就是 null。</param>
public sealed record WorldInfo(string Name, string Path, long SizeBytes, DateTimeOffset? LastWrite)
{
    /// <summary>
    /// 给界面看的大小，如 "12.3 MB"。
    ///
    /// 用 1024 进制并按位取整：存档从几百 KB 到几个 GB 都有，
    /// 统一用 MB 会让小世界显示成 "0.0 MB"，看着像坏了。
    /// </summary>
    public string SizeText
    {
        get
        {
            var bytes = SizeBytes;

            if (bytes < 1024) return $"{bytes} B";

            var kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:0.0} KB";

            var mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:0.0} MB";

            return $"{mb / 1024.0:0.00} GB";
        }
    }

    /// <summary>给界面看的修改时间，取不到时显示 "—" 而不是空白（空白看起来像加载中）。</summary>
    public string LastWriteText =>
        LastWrite is null ? "—" : LastWrite.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}

/// <summary>
/// 本地世界的列出 / 删除 / 导入导出。
///
/// ## 为什么导出导入都用 zip
///
/// 世界目录是一堆小文件（region、playerdata、data），打包成一个 zip 才方便
/// 分享和搬机器；这也是所有启动器的共同做法。
///
/// ## 世界名一律不可信
///
/// 世界名会从 par2 的目录名、别人给的 zip 条目名、用户手输里来。
/// 凡是用它拼路径的地方都过 <see cref="IsSafeWorldName"/> ——
/// `..\..\重要文件` 这种名字必须在校验阶段就被拒，而不是等到删除时才后悔。
/// </summary>
public static class WorldStore
{
    /// <summary>
    /// 列出实例里的全部世界，按最后修改时间倒序（最近玩的排前面）。
    ///
    /// **只认带 level.dat 的目录**：Minecraft 的世界标识就是这么一个文件，
    /// 没有它就只是个普通目录（很多整合包会在 saves 下放 README 之类）。
    /// 列出来的话用户点了"进入世界"游戏反而找不到。
    /// </summary>
    public static IReadOnlyList<WorldInfo> List(GameInstance instance)
    {
        var savesDir = Path.Combine(instance.GameDir, "saves");
        var result = new List<WorldInfo>();

        if (!Directory.Exists(savesDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(savesDir))
        {
            var level = Path.Combine(dir, "level.dat");
            if (!File.Exists(level)) continue;

            var name = Path.GetFileName(dir);

            long size = 0;
            DateTimeOffset? lastWrite = null;

            try
            {
                // LastWriteTime 取的是**目录**自己的时间戳。往目录里新增文件时
                // Windows 会更新它，所以它能反映"最近玩过"，而且比对每个文件取
                // max 便宜得多（几千个 region 文件的世界差异很明显）。
                var info = new DirectoryInfo(dir);
                lastWrite = info.LastWriteTimeUtc;

                size = SumSize(dir);
            }
            catch { /* 权限或竞态，留 0 / null，不影响列出 */ }

            result.Add(new WorldInfo(name, dir, size, lastWrite));
        }

        return result
            .OrderByDescending(w => w.LastWrite ?? DateTimeOffset.MinValue)
            .ThenBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 删除一个世界。目录名**精确匹配**，不做模糊查找
    /// ——"删错存档"是不可逆的，宁可用户自己确认名字。
    /// </summary>
    public static bool Delete(GameInstance instance, string worldName)
    {
        if (!IsSafeWorldName(worldName)) return false;

        var dir = Path.Combine(instance.GameDir, "saves", worldName);
        if (!Directory.Exists(dir)) return false;

        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (IOException)
        {
            // 游戏正开着会锁住 region 文件，这是最常见的失败原因，让调用方去提示
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从 zip 导入一个世界到 &lt;GameDir&gt;/saves。
    ///
    /// zip 里只有一层顶层目录就用那个目录名当世界名（大家分享的存档包都是
    /// `MyWorld/level.dat` 这个形状），否则用文件名。重名时明确失败，
    /// 不覆盖 —— 覆盖等于把用户玩了几百小时的存档删了。
    /// </summary>
    public static Task<InstallResult> ImportZipAsync(
        GameInstance instance, string zipPath, CancellationToken ct = default)
    {
        // 解压是同步阻塞 IO，几百 MB 的存档会卡住 UI 线程，丢到线程池。
        return Task.Run(() => ImportCore(instance, zipPath, ct), ct);
    }

    private static InstallResult ImportCore(GameInstance instance, string zipPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return new InstallResult(false, $"找不到要导入的 zip：{zipPath}");

        var staging = Path.Combine(Path.GetTempPath(), "pcllike-import-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(staging);

            using (var zip = ZipFile.OpenRead(zipPath))
                ContentInstaller.ExtractZipInto(zip, staging, ct);

            var innerName = ContentInstaller.DetectSingleRoot(staging);
            var worldName = ContentPaths.SafeWorldName(
                innerName ?? Path.GetFileNameWithoutExtension(zipPath));

            var savesDir = Path.Combine(instance.GameDir, "saves");
            var destDir = Path.Combine(savesDir, worldName);
            var rootDir = Path.Combine(staging, innerName ?? "");

            if (Directory.Exists(destDir))
                return new InstallResult(false, $"世界「{worldName}」已经存在了，请先改名或删掉旧存档");

            // 解出来的必须真是个世界。不加这道检查的话，一个"条目全被越界规则跳掉"
            // 或压根不是存档的 zip 会在 saves 下留一个空目录，而 List 又不认它
            // （没有 level.dat）—— 用户看到"导入成功"但列表里什么都没有。
            if (!File.Exists(Path.Combine(rootDir, "level.dat")))
                return new InstallResult(false, "这个 zip 里没有找到世界（缺少 level.dat）");

            // 前面 ExtractZipInto 已经把越界条目跳过了，所以这里搬的是干净内容
            Directory.Move(rootDir, destDir);

            return new InstallResult(true, $"世界已导入：{worldName}", destDir);
        }
        catch (OperationCanceledException)
        {
            throw;   // 取消原样传出（Task.Run 语义），不当成导入失败
        }
        catch (InvalidDataException)
        {
            return new InstallResult(false, "这不是有效的 zip 文件");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"导入世界失败：{ex.Message}");
        }
        finally
        {
            ContentInstaller.TryDeleteDir(staging);
        }
    }

    /// <summary>
    /// 把一个世界导出成 zip（条目名以世界名为顶层，再解压就能还原）。
    ///
    /// 用 <see cref="CompressionLevel.Optimal"/>：存档里的 region 文件本身
    /// 已经压缩过，再花时间也压不动多少，但 playerdata / datapacks 这类
    /// 文本内容能明显变小，值得多花那点 CPU。
    /// </summary>
    public static async Task<InstallResult> ExportZipAsync(
        GameInstance instance, string worldName, string zipPath, CancellationToken ct = default)
    {
        if (!IsSafeWorldName(worldName))
            return new InstallResult(false, $"世界名不合法：{worldName}");

        var worldDir = Path.Combine(instance.GameDir, "saves", worldName);
        if (!Directory.Exists(worldDir))
            return new InstallResult(false, $"世界不存在：{worldName}");

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 先写临时文件再改名：导出中途被打断（用户关窗口、磁盘满）
            // 不会留下一个半截的 zip 冒充完整备份。
            var tmp = zipPath + ".part";

            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(tmp, ZipArchiveMode.Create);

                foreach (var file in Directory.EnumerateFiles(worldDir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    // 条目名 = 世界名/相对路径。用 `/` 是 zip 规范，
                    // 写 `\` 的话在 Mac/Linux 上解出来会变成一个奇怪的文件名。
                    var relative = Path.GetRelativePath(worldDir, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, $"{worldName}/{relative}", CompressionLevel.Optimal);
                }
            }, ct).ConfigureAwait(false);

            File.Move(tmp, zipPath, overwrite: true);

            return new InstallResult(true, $"世界已导出：{zipPath}", zipPath);
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(false, "导出被取消");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"导出世界失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 世界名能不能安全地当目录名用。
    ///
    /// 拒绝一切含路径分隔符、`..`、`.`、盘符、控制符的名字。
    /// 这是删除/导入/导出三条路径的**共同前提**，所以做成公开方法，
    /// 让 GUI 在启用"删除"按钮之前就能用它挡掉非法输入。
    /// </summary>
    public static bool IsSafeWorldName(string? worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName)) return false;

        var name = worldName.Trim();

        if (name.Length == 0) return false;
        if (name is "." or "..") return false;

        if (name.Contains('/') || name.Contains('\\')) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        // 防住 `..\foo` 之外还要防 `..foo` 这种披着外衣的
        return !name.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>递归求和目录字节数。单个文件读不到就跳过（存档里常有权限怪文件）。</summary>
    private static long SumSize(string dir)
    {
        long total = 0;

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch { /* 跳过读不到的 */ }
        }

        return total;
    }
}