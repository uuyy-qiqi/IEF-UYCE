using PclLike.Core.Storage;

namespace PclLike.Core.Mods;

/// <summary>
/// Mod 目录管理器：扫描、启用/禁用、删除、安装。
///
/// 所有操作都围绕一个实例的 `mods/` 目录，目录取自 <see cref="GameInstance.GameDir"/>，
/// 所以隔离实例和共享实例自动走各自的目录，不需要额外判断。
///
/// **启用/禁用用改后缀实现**（`x.jar` ↔ `x.jar.disabled`），不用移动文件：
///   - 用户在资源管理器里能看到全部 Mod，禁用的一目了然
///   - 不需要记录"原本在哪"，不会出现配置文件与实际文件不一致
///   - 复制、备份、压缩 mods 目录时禁用状态跟着走
/// 这是 PCL2 / HMCL / MultiMC 的共同做法。
/// </summary>
public sealed class ModManager
{
    private readonly GameInstance _instance;

    public ModManager(GameInstance instance) => _instance = instance;

    /// <summary>这个实例的 mods 目录。</summary>
    public string ModsDirectory => Path.Combine(_instance.GameDir, "mods");

    /// <summary>
    /// 扫描 mods 目录。
    ///
    /// 只认 .jar 和 .jar.disabled（以及 .zip.disabled 这类禁用态），
    /// 其它文件（.txt 说明、.jar.bak 备份）一律忽略 —— 列出来只会干扰用户。
    /// </summary>
    public ModScanResult Scan(bool parseMetadata = true)
    {
        var dir = ModsDirectory;

        if (!Directory.Exists(dir))
            return new ModScanResult { ModsDirectory = dir, DirectoryExists = false };

        var files = new List<(string Path, string Name, bool Enabled)>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(path);

                // 跳过 macOS/压缩工具的垃圾文件
                if (name.StartsWith("._", StringComparison.Ordinal)) continue;
                if (name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) continue;

                bool enabled;
                string logical;

                if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = false;
                    logical = name[..^".disabled".Length];
                }
                else
                {
                    enabled = true;
                    logical = name;
                }

                // 只收 jar（含禁用态）
                if (!logical.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

                files.Add((path, logical, enabled));
            }
        }
        catch (Exception ex)
        {
            return new ModScanResult
            {
                ModsDirectory = dir,
                DirectoryExists = true,
                Mods = [new ModFile
                {
                    Path = dir,
                    FileName = "",
                    Enabled = false,
                    SizeBytes = 0,
                    ParseError = $"无法枚举目录：{ex.Message}",
                }],
            };
        }

        var mods = new List<ModFile>(files.Count);

        if (parseMetadata)
        {
            // 并行解析。zip 读是 IO 密集，机械盘上收益明显；
            // 单个失败只影响它自己（解析逻辑内部已 try）
            var parsed = new ModFile[files.Count];
            Parallel.For(0, files.Count, i =>
            {
                var (path, name, enabled) = files[i];
                parsed[i] = BuildModFile(path, name, enabled);
            });
            mods.AddRange(parsed);
        }
        else
        {
            foreach (var (path, name, enabled) in files)
                mods.Add(BuildModFile(path, name, enabled, skipParse: true));
        }

        return new ModScanResult
        {
            ModsDirectory = dir,
            DirectoryExists = true,
            Mods = mods.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
        };
    }

    private static ModFile BuildModFile(string path, string logicalName, bool enabled, bool skipParse = false)
    {
        long size = 0;
        DateTimeOffset mtime = default;

        try
        {
            var fi = new FileInfo(path);
            size = fi.Length;
            mtime = fi.LastWriteTimeUtc;
        }
        catch { /* 权限或竞态，留默认值 */ }

        if (skipParse)
        {
            return new ModFile
            {
                Path = path, FileName = logicalName, Enabled = enabled,
                SizeBytes = size, ModifiedAt = mtime,
            };
        }

        try
        {
            var info = ModScanner.Parse(path);
            return new ModFile
            {
                Path = path, FileName = logicalName, Enabled = enabled,
                SizeBytes = size, ModifiedAt = mtime, Info = info,
                ParseError = info is null ? "读不出元数据（不是标准 Mod 包，或文件损坏）" : null,
            };
        }
        catch (Exception ex)
        {
            return new ModFile
            {
                Path = path, FileName = logicalName, Enabled = enabled,
                SizeBytes = size, ModifiedAt = mtime,
                ParseError = ex is InvalidDataException ? "不是有效的 zip / jar 文件" : ex.Message,
            };
        }
    }

    /// <summary>
    /// 启用一个 Mod。已经是启用态则返回 false（没做事）。
    /// </summary>
    public bool Enable(ModFile mod)
    {
        if (mod.Enabled) return false;
        return Rename(mod.Path, mod.FileName);
    }

    /// <summary>禁用一个 Mod。</summary>
    public bool Disable(ModFile mod)
    {
        if (!mod.Enabled) return false;
        return Rename(mod.Path, mod.FileName + ".disabled");
    }

    /// <summary>切换启用状态，返回新的状态。</summary>
    public bool Toggle(ModFile mod) => mod.Enabled ? !Disable(mod) : Enable(mod);

    private static bool Rename(string from, string newName)
    {
        try
        {
            var dir = Path.GetDirectoryName(from);
            if (dir is null) return false;

            var to = Path.Combine(dir, newName);

            // 目标已存在 —— 说明目录里有重名，硬改名会覆盖用户的文件，宁可不做
            if (File.Exists(to)) return false;

            File.Move(from, to);
            return true;
        }
        catch { return false; }
    }

    /// <summary>删除一个 Mod 文件（含禁用态）。</summary>
    public bool Delete(ModFile mod)
    {
        try
        {
            if (!File.Exists(mod.Path)) return false;
            File.Delete(mod.Path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把一个外部 jar 复制进 mods 目录。
    ///
    /// 重名时的策略：**不覆盖，加序号**。用户从两个来源下了同名文件时，
    /// 直接覆盖会静默丢掉一个，事后完全不知道。加 `-2` 后缀让两个都在，
    /// 用户自己决定留哪个。
    ///
    /// <paramref name="preferredFileName"/> 用来指定装进去以后叫什么。
    /// 从网上下载时必须传 —— 下载器写的是临时文件（GUI 用 Guid 命名），
    /// 不传的话 mods 目录里会留下一堆 `3f2a9c8b….jar`，用户完全认不出谁是谁。
    /// 传了之后按真实文件名（如 `sodium-fabric-0.5.13+mc1.20.1.jar`）落盘。
    /// 只取文件名，忽略路径部分，防止调用方传进带目录的字符串写到别处。
    /// </summary>
    public string InstallFromFile(
        string sourceJar, bool enabled = true, string? preferredFileName = null)
    {
        if (!File.Exists(sourceJar))
            throw new FileNotFoundException("要安装的 Mod 文件不存在", sourceJar);

        var dir = ModsDirectory;
        Directory.CreateDirectory(dir);

        // 优先用调用方给的名字；没给就退回源文件名（用户从资源管理器拖进来时
        // 源文件名本来就有意义，不需要额外传）
        var nameSource = string.IsNullOrWhiteSpace(preferredFileName)
            ? sourceJar
            : Path.GetFileName(preferredFileName);

        // 取纯文件名 + 强制 .jar 后缀。
        // 必须剥掉目录：万一调用方传进来 "..\\..\\evil.jar" 这种，
        // 直接 Path.Combine 会写到 mods 目录外面去。
        var baseName = Path.GetFileNameWithoutExtension(nameSource);

        // 空名或非法字符（Modrinth 上有些文件名带 `:` `?`）会导致落盘失败，
        // 退回到源文件名，至少保证能装上
        if (string.IsNullOrWhiteSpace(baseName) || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            baseName = Path.GetFileNameWithoutExtension(sourceJar);

        var target = Path.Combine(dir, baseName + ".jar");

        // 已经装过同一个文件就别重复装
        if (File.Exists(target))
        {
            if (SameContent(target, sourceJar))
                throw new InvalidOperationException($"这个 Mod 已经装过了：{Path.GetFileName(target)}");

            for (var n = 2; n < 1000; n++)
            {
                var candidate = Path.Combine(dir, $"{baseName}-{n}.jar");
                if (!File.Exists(candidate)) { target = candidate; break; }
            }
        }

        File.Copy(sourceJar, target, overwrite: false);

        if (!enabled)
        {
            var disabledPath = target + ".disabled";
            try { File.Move(target, disabledPath); target = disabledPath; } catch { /* 留启用态 */ }
        }

        return target;
    }

    /// <summary>
    /// 按内容判断两个文件是否相同。
    ///
    /// 先比长度（快，绝大多数不同文件长度就不同），长度相同再比 SHA256。
    /// 直接全量哈希一个 200MB 的整合包会卡界面，所以长度预筛很有必要。
    /// </summary>
    private static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;

            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            using var sha = System.Security.Cryptography.SHA256.Create();

            var ha = sha.ComputeHash(sa);
            var hb = sha.ComputeHash(sb);
            return ha.AsSpan().SequenceEqual(hb);
        }
        catch { return false; }
    }

    /// <summary>打开 mods 目录（资源管理器）—— 界面上的"打开文件夹"按钮用。</summary>
    public void OpenInExplorer()
    {
        var dir = ModsDirectory;
        Directory.CreateDirectory(dir);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch { /* 没有文件管理器或权限不足，静默失败 */ }
    }
}