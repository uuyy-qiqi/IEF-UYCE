using System.IO.Compression;
using PclLike.Core.Storage;

namespace PclLike.Core.Content;

/// <summary>
/// 一次安装的结果。
///
/// 安装失败的原因很多（磁盘满、重名、没有对应世界、路径穿越），
/// 而且**每一类都要让用户看懂**，所以失败也走返回值而不是抛异常：
/// 浏览界面里点"安装"是常规操作，抛异常会把整个列表刷新流程打断。
/// </summary>
/// <param name="Ok">是否真的装上了。</param>
/// <param name="Message">给用户看的一句话，失败时必须说清"为什么"。这里不写"未知错误"。</param>
/// <param name="InstalledPath">装成功时落盘的最终路径，方便界面"打开所在文件夹"。</param>
/// <param name="RequiredLoader">该内容依赖的加载器（如 mod-loader / shader-loader），null 表示无要求。</param>
public sealed record InstallResult(
    bool Ok, string Message, string? InstalledPath = null, string? RequiredLoader = null);

/// <summary>
/// 把"已经在本地下好的文件"放进实例的正确目录。
///
/// ## 为什么复制是常态
///
/// 下载器（GUI 侧）写的是临时文件，装完要清理临时目录，所以**复制**而不是移动：
/// 移动会让"装到两个实例"这件事变得不可能，而多实例共用一份下载是常见需求。
/// 只有世界解压走的是"解压到临时目录再整体搬"，因为它要的是目录不是文件。
///
/// ## 防路径穿越
///
/// <paramref name="preferredFileName"/> 和 zip 条目名都是不可信数据：
/// 前者来自 Modrinth 的文件名（有人可以在里面塞 `../`），后者来自别人做的
/// zip 包。凡是拼接路径的地方都先过 <see cref="ResolveInside"/>，
/// 保证最终路径真的在目标目录里 —— 这是唯一的判据，不做字符串前缀比较。
/// </summary>
public static class ContentInstaller
{
    /// <summary>
    /// 把 <paramref name="localFile"/> 装进实例。
    ///
    /// - **Mod / 资源包 / 光影包**：复制到各自目录，重名**直接覆盖**。
    ///   同一个 Mod 换版本是日常操作，留一堆 `sodium-1.jar`/`sodium-2.jar`
    ///   反而会让游戏同时加载两份同 ID 的 Mod 直接崩客户端。
    /// - **数据包**：必须给 <paramref name="worldName"/>，且该世界得真的存在；
    ///   世界不存在就明确失败，不猜"用户可能想装到哪个世界"。
    /// - **世界**：当 zip 解压。里面只有一层顶层目录就用那个目录名当世界名
    ///   （Modrinth / 各种站的存档包都是这个形状），否则用文件名。
    /// - **整合包**：这里不做，交给 <see cref="ModPackInstaller"/> ——
    ///   整合包要下载几十个文件并处理 overrides，和"复制一个文件"完全不是一件事。
    /// </summary>
    public static Task<InstallResult> InstallFileAsync(
        GameInstance instance,
        ContentKind kind,
        string localFile,
        string? preferredFileName = null,
        string? worldName = null,
        CancellationToken ct = default)
    {
        // 复制文件、解压 zip 都是**同步阻塞的 IO**：世界包能到几百 MB，
        // 在 UI 线程上直接跑会把窗口卡住。丢到线程池里，让界面保持能动。
        return Task.Run(() => InstallCore(instance, kind, localFile, preferredFileName, worldName, ct), ct);
    }

    /// <summary>同步实现体。放在这里是因为整段都是阻塞 IO，包进 Task.Run 更好读。</summary>
    private static InstallResult InstallCore(
        GameInstance instance,
        ContentKind kind,
        string localFile,
        string? preferredFileName,
        string? worldName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(localFile) || !File.Exists(localFile))
            return new InstallResult(false, $"要安装的文件不存在：{localFile}");

        var fileName = SafeName(preferredFileName, localFile);

        try
        {
            switch (kind)
            {
                case ContentKind.ModPack:
                    return new InstallResult(false, "整合包请走 InstallModPack");

                case ContentKind.Mod:
                case ContentKind.ResourcePack:
                case ContentKind.Shader:
                {
                    var dir = ContentPaths.TargetDir(instance, kind);
                    var dest = ResolveInside(dir, fileName);
                    if (dest is null)
                        return new InstallResult(false, $"文件名不合法，无法落到 {ContentPaths.DisplayName(kind)} 目录：{fileName}");

                    File.Copy(localFile, dest, overwrite: true);

                    return new InstallResult(
                        true, $"{ContentPaths.DisplayName(kind)} 已安装：{Path.GetFileName(dest)}",
                        dest, ContentPaths.RequiredLoader(kind));
                }

                case ContentKind.Datapack:
                {
                    if (string.IsNullOrWhiteSpace(worldName))
                        return new InstallResult(false, "安装数据包必须指定世界（世界名不能为空）");

                    var worldDir = Path.Combine(ContentPaths.TargetDir(instance, ContentKind.World), ContentPaths.SafeWorldName(worldName));

                    // 只认真的世界：有 level.dat 才算。否则一个同名空目录会骗过我们，
                    // 数据包落进去游戏永远读不到，用户还以为是包的问题。
                    if (!File.Exists(Path.Combine(worldDir, "level.dat")))
                        return new InstallResult(false, $"找不到世界「{ContentPaths.SafeWorldName(worldName)}」，请先创建或导入该世界");

                    var packDir = ContentPaths.TargetDir(instance, ContentKind.Datapack, worldName);
                    var dest = ResolveInside(packDir, fileName);
                    if (dest is null)
                        return new InstallResult(false, $"文件名不合法，无法落到数据包目录：{fileName}");

                    File.Copy(localFile, dest, overwrite: true);

                    return new InstallResult(true, $"数据包已安装到世界「{ContentPaths.SafeWorldName(worldName)}」：{Path.GetFileName(dest)}", dest);
                }

                case ContentKind.World:
                {
                    // 解压到临时目录 → 探测真正的世界名 → 整体搬进 saves。
                    // 为什么要两步：世界名要么在 zip 结构里（单层顶层目录），
                    // 要么得回退到文件名，前者必须解完才知道。
                    var staging = Path.Combine(Path.GetTempPath(), "pcllike-world-" + Guid.NewGuid().ToString("N"));

                    try
                    {
                        Directory.CreateDirectory(staging);

                        using (var zip = ZipFile.OpenRead(localFile))
                            ExtractZipInto(zip, staging, ct);

                        var innerName = DetectSingleRoot(staging);
                        var rootDir = Path.Combine(staging, innerName ?? "");
                        var savesDir = ContentPaths.TargetDir(instance, ContentKind.World);
                        var name = ContentPaths.SafeWorldName(innerName ?? Path.GetFileNameWithoutExtension(fileName));
                        var destDir = Path.Combine(savesDir, name);

                        if (Directory.Exists(destDir))
                            return new InstallResult(false, $"世界「{name}」已经存在了，请先改名或删掉旧存档");

                        // 解出来的必须真是个世界。不加这道检查的话，一个"条目全被
                        // 越界规则跳掉"或压根不是存档的 zip 会在 saves 下留一个空目录，
                        // 而 WorldStore.List 又不认它（没有 level.dat）——
                        // 用户会看到"导入成功"但列表里什么都没有。
                        if (!File.Exists(Path.Combine(rootDir, "level.dat")))
                            return new InstallResult(false, "这个 zip 里没有找到世界（缺少 level.dat）");

                        Directory.Move(rootDir, destDir);

                        return new InstallResult(true, $"世界已导入：{name}", destDir);
                    }
                    finally
                    {
                        TryDeleteDir(staging);
                    }
                }

                default:
                    return new InstallResult(false, $"不支持的内容类型：{kind}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;   // 取消要原样传出去（Task.Run 的语义），不能当成"安装失败"吞掉
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"{ContentPaths.DisplayName(kind)} 安装失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 共享工具。WorldStore / ModPackInstaller 也用这几个，避免各写一份解压逻辑
    // ——解压相关的 bug（路径穿越、临时目录残留）只修一处才靠得住。
    // ------------------------------------------------------------------

    /// <summary>
    /// 取一个安全的落盘文件名：只保留文件名部分，剔掉 `..` 与非法字符。
    /// </summary>
    /// <param name="preferred">调用方给的名字，可为空。</param>
    /// <param name="sourcePath">没给名字时退回源文件名。</param>
    public static string SafeName(string? preferred, string sourcePath)
    {
        var raw = string.IsNullOrWhiteSpace(preferred) ? sourcePath : preferred;

        // 取纯文件名就等于把 `..\..\evil.jar` 变成 `evil.jar`，穿越路径直接消失
        var name = Path.GetFileName(raw).Trim();

        if (name.Length == 0 || name is "." or "..")
            name = Path.GetFileName(sourcePath).Trim();

        if (name.Length == 0 || name is "." or "..")
            name = "content.bin";

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? string.Concat(name.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0))
            : name;
    }

    /// <summary>
    /// 把 <paramref name="relative"/> 解析到 <paramref name="root"/> 里面。
    ///
    /// 返回 null 表示解析后跑到 root 外面去了 —— 唯一的判据是
    /// <see cref="Path.GetFullPath(string)"/> 之后的前缀比较，**不做字符串层面的
    /// 替换**：`..`、符号链接、`C:\` 绝对路径都会被 GetFullPath 归一化到真相。
    /// zip-slip 就是靠这一处挡住的。
    /// </summary>
    public static string? ResolveInside(string root, string relative)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            var separator = Path.DirectorySeparatorChar;

            if (!rootFull.EndsWith(separator)) rootFull += separator;

            var target = Path.GetFullPath(Path.Combine(rootFull, relative));

            return target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? target : null;
        }
        catch
        {
            // 含非法字符、盘符非法等，一律当越界处理
            return null;
        }
    }

    /// <summary>
    /// 把 zip 解开到 <paramref name="destDir"/>。
    ///
    /// 每个条目都过 <see cref="ResolveInside"/>；越界的条目**跳过并在日志里点名**，
    /// 不抛异常也不覆盖 —— 一个坏条目不该让整包装不上。
    /// `__MACOSX/` 与 `.DS_Store` 是 mac 压缩工具塞的垃圾，装进 mods 目录只会碍眼。
    /// </summary>
    /// <param name="log">可选日志回调，用来把"跳过了谁"告诉界面。</param>
    public static void ExtractZipInto(
        ZipArchive zip, string destDir, CancellationToken ct = default, Action<string>? log = null)
    {
        Directory.CreateDirectory(destDir);

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var name = entry.FullName.Replace('\\', '/');

            if (IsJunkPath(name)) continue;

            var target = ResolveInside(destDir, name);
            if (target is null)
            {
                log?.Invoke($"跳过越界条目：{entry.FullName}");
                continue;
            }

            // 目录条目：以 / 结尾
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var src = entry.Open();
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
    }

    /// <summary>macOS/压缩工具产生的垃圾路径，解压时一律丢掉。</summary>
    public static bool IsJunkPath(string entryName)
    {
        if (entryName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) return true;

        var file = Path.GetFileName(entryName.TrimEnd('/'));
        if (file.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) return true;

        // `._foo` 是 mac 的资源叉伴生文件，解出来游戏也不认
        return file.StartsWith("._", StringComparison.Ordinal);
    }

    /// <summary>
    /// 目录里是否只有一层顶层目录，是就返回它的名字（不含结尾分隔符）。
    ///
    /// 用来判断"这个 zip 本身就是一个世界"还是"里面装着一堆世界"。
    /// 只认目录，不认文件：一个顶层文件（比如说明.txt）不算世界的容器。
    /// </summary>
    public static string? DetectSingleRoot(string dir)
    {
        var dirs = Directory.GetDirectories(dir);
        var files = Directory.GetFiles(dir);

        if (dirs.Length != 1 || files.Length != 0) return null;

        var name = Path.GetFileName(dirs[0]);

        // 隐藏/垃圾目录（.git、__MACOSX）不算
        return name.StartsWith('.') || name.StartsWith("__", StringComparison.Ordinal) ? null : name;
    }

    /// <summary>尽力删除一个临时目录，失败就放着（系统以后会清）。</summary>
    public static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* 文件被占用等，不影响安装结果 */ }
    }
}