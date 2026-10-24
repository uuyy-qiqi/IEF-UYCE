using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclLike.Core.Storage;

/// <summary>
/// 一个"实例"（PCL2 里叫版本隔离 / 整合包）。
///
/// ## 为什么需要它
///
/// Minecraft 靠 `--gameDir` 决定去哪儿读写存档、设置、Mod。启动器只改这一个
/// 参数，就能让不同版本/整合包互不污染：
///
///   - **隔离**（默认）：实例有自己的目录，`saves/`、`options.txt`、`mods/`
///     全在自己家里。玩 1.20.1 的整合包不会把 1.21.4 的设置改掉。
///   - **共享**：实例直接用启动器根目录，跟原版行为一致，多个版本共用存档。
///
/// 无论哪种模式，`assets/`、`libraries/`、`versions/` 永远在启动器根目录共享——
/// 那些是几百 MB 的公共资源，每个实例存一份纯属浪费。
///
/// ## 目录布局
///
/// ```
/// &lt;启动器根&gt;/
///   assets/  libraries/  versions/     ← 永远共享
///   instances/
///     &lt;实例id&gt;/
///       instance.json                  ← 这份元数据
///       .minecraft/                    ← 隔离模式下的 gameDir
///         saves/  options.txt  mods/
/// ```
/// </summary>
public sealed class GameInstance
{
    /// <summary>实例 id。同时是目录名，所以必须能在文件系统里安全使用。</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>显示名，用户可以随便改。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>这个实例要启动的 Minecraft 版本 id。</summary>
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = "";

    /// <summary>true = 独立目录；false = 用启动器根目录（原版行为）。</summary>
    [JsonPropertyName("isolated")] public bool Isolated { get; set; } = true;

    /// <summary>该实例专属的 JVM 参数，覆盖全局设置。</summary>
    [JsonPropertyName("jvmArgs")] public string? JvmArgs { get; set; }

    [JsonPropertyName("maxMemoryMb")] public int? MaxMemoryMb { get; set; }

    /// <summary>备注，界面上显示。</summary>
    [JsonPropertyName("note")] public string? Note { get; set; }

    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("lastPlayedAt")] public DateTimeOffset? LastPlayedAt { get; set; }

    /// <summary>这个实例的加载器类型（vanilla / forge / fabric / neoforge）。</summary>
    [JsonPropertyName("loader")] public string Loader { get; set; } = "vanilla";

    [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; set; }

    // ---- 以下为运行期计算，不写进 JSON ----

    /// <summary>实例目录（启动器根/instances/&lt;id&gt;）。</summary>
    [JsonIgnore] public string Directory { get; set; } = "";

    /// <summary>
    /// 启动器根目录。共享模式下它就是 gameDir，
    /// 由 InstanceStore 填充（JSON 里不存，因为它由实例目录推出来）。
    /// </summary>
    [JsonIgnore] public string LauncherRoot { get; set; } = "";

    /// <summary>实际传给游戏 --gameDir 的目录。</summary>
    [JsonIgnore]
    public string GameDir
    {
        get
        {
            if (Isolated) return Path.Combine(Directory, ".minecraft");

            // 共享模式：直接用启动器根，跟原版 .minecraft 布局一致
            return LauncherRoot;
        }
    }

    /// <summary>实例专属 JVM 参数（按行拆）。</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ExtraJvmArgs =>
        string.IsNullOrWhiteSpace(JvmArgs)
            ? []
            : JvmArgs.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>显示用：隔离 / 共享。</summary>
    [JsonIgnore] public string IsolationLabel => Isolated ? "独立" : "共享";

    /// <summary>从 &lt;...&gt;/instances/&lt;id&gt; 反推启动器根目录。</summary>
    private static string RootOf(string instancesParent)
        => Path.GetDirectoryName(instancesParent.TrimEnd(Path.DirectorySeparatorChar)) ?? instancesParent;

    /// <summary>
    /// 把 id 规范成安全的目录名。
    ///
    /// 保留 Unicode 字母数字——中文实例名是常态，不能因为非 ASCII 就丢成 default。
    /// 只把文件系统真正危险的字符（路径分隔符、Windows 保留字符、控制符）换掉。
    /// </summary>
    public static string SanitizeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "default";

        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
        {
            // 字母数字（含中文等 Unicode 字母）直接留
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            // 这几个无害的标点保留，让 id 好看些
            else if (c is '-' or '_' or '.') sb.Append(c);
            // 其余（空格、斜杠、冒号、控制符等）一律换下划线
            else sb.Append('_');
        }

        var cleaned = sb.ToString().Trim('.', '_');
        if (cleaned.Length == 0) return "default";

        // 截断放在最后。中文的一个字符占 1 个 char，按程序计数没问题；
        // 48 是给 Windows 260 字符 MAX_PATH 留的余量（实例目录还要往下再挂 .minecraft）。
        return cleaned.Length > 48 ? cleaned[..48] : cleaned;
    }

    /// <summary>
    /// 生成一个在 <paramref name="existing"/> 中唯一的 id。
    /// 重名时追加 -2 / -3 ……，避免两个"生存服"互相覆盖。
    /// </summary>
    public static string MakeUniqueId(string? raw, IEnumerable<string> existing)
    {
        var baseId = SanitizeId(raw);
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseId)) return baseId;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{baseId}-{n}";
            if (candidate.Length > 48) candidate = candidate[..48];
            if (!taken.Contains(candidate)) return candidate;
        }

        return $"{baseId[..Math.Min(32, baseId.Length)]}-{Guid.NewGuid():N}"[..48];
    }
}

/// <summary>实例的增删查改。</summary>
public sealed class InstanceStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;

    public InstanceStore(string launcherRoot) => _root = launcherRoot;

    public string LauncherRoot => _root;

    public string InstancesDir => Path.Combine(_root, "instances");

    public string DirFor(string id) => Path.Combine(InstancesDir, GameInstance.SanitizeId(id));

    public string FileFor(string id) => Path.Combine(DirFor(id), "instance.json");

    /// <summary>列出全部实例。目录不存在或某个实例的 JSON 坏了都跳过，不抛。</summary>
    public List<GameInstance> LoadAll()
    {
        var result = new List<GameInstance>();
        if (!Directory.Exists(InstancesDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(InstancesDir))
        {
            var id = Path.GetFileName(dir);
            var inst = Load(id);
            if (inst is not null) result.Add(inst);
        }

        return result.OrderByDescending(i => i.LastPlayedAt ?? i.CreatedAt).ToList();
    }

    public GameInstance? Load(string id)
    {
        var file = FileFor(id);
        if (!File.Exists(file)) return null;

        try
        {
            var inst = JsonSerializer.Deserialize<GameInstance>(File.ReadAllText(file), Opts);
            if (inst is null) return null;

            inst.Id = GameInstance.SanitizeId(string.IsNullOrWhiteSpace(inst.Id) ? id : inst.Id);
            inst.Directory = DirFor(inst.Id);
            inst.LauncherRoot = _root;
            inst.Name = string.IsNullOrWhiteSpace(inst.Name) ? inst.Id : inst.Name;
            return inst;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>新建或覆盖保存。</summary>
    public GameInstance Save(GameInstance instance)
    {
        instance.Id = GameInstance.SanitizeId(instance.Id);
        instance.Directory = DirFor(instance.Id);
        instance.LauncherRoot = _root;

        Directory.CreateDirectory(instance.Directory);
        if (instance.Isolated) Directory.CreateDirectory(instance.GameDir);

        var tmp = FileFor(instance.Id) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(instance, Opts));
        File.Move(tmp, FileFor(instance.Id), overwrite: true);

        return instance;
    }

    /// <summary>新建实例：自动分配不冲突的 id。</summary>
    public GameInstance Create(string name, string versionId, bool isolated = true, string? note = null)
    {
        var id = GameInstance.MakeUniqueId(name, LoadAll().Select(i => i.Id));
        return Save(new GameInstance
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
            VersionId = versionId,
            Isolated = isolated,
            Note = note,
        });
    }

    /// <summary>确保至少有一个实例存在，返回"默认"实例。</summary>
    public GameInstance EnsureDefault(string defaultVersion)
    {
        var existing = Load("default");
        if (existing is not null) return existing;

        return Save(new GameInstance
        {
            Id = "default",
            Name = "默认实例",
            VersionId = defaultVersion,
            Isolated = false,   // 默认实例不隔离，跟原版行为一致，避免用户找不到旧存档
        });
    }

    /// <summary>
    /// 删除实例。
    /// 默认只删元数据、保留游戏文件——存档删了就找不回来，
    /// 让调用方显式要求才连目录一起删。
    /// </summary>
    public bool Delete(string id, bool deleteFiles = false)
    {
        var dir = DirFor(id);
        if (!Directory.Exists(dir)) return false;

        if (deleteFiles)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
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
        else
        {
            var meta = FileFor(id);
            if (File.Exists(meta)) File.Delete(meta);
        }

        return true;
    }

    /// <summary>该实例的 Mod 目录（隔离模式下才有意义）。</summary>
    public string ModsDirFor(GameInstance instance) => Path.Combine(instance.GameDir, "mods");

    /// <summary>
    /// 复制一个实例（配置文件 + 整个游戏目录）。
    ///
    /// 复制出来的是独立副本，所以新实例强制 Isolated = true —— 共享模式的实例
    /// 本来就没有自己的游戏目录，抄一份只会得到一个空壳。
    /// </summary>
    public GameInstance Copy(string id, string? newName = null)
    {
        var src = Load(id) ?? throw new ArgumentException($"实例不存在：{id}", nameof(id));

        var name = string.IsNullOrWhiteSpace(newName) ? $"{src.Name} - 副本" : newName.Trim();
        var newId = GameInstance.MakeUniqueId(name, LoadAll().Select(i => i.Id));

        var copy = new GameInstance
        {
            Id = newId,
            Name = name,
            VersionId = src.VersionId,
            Isolated = true,
            JvmArgs = src.JvmArgs,
            MaxMemoryMb = src.MaxMemoryMb,
            Loader = src.Loader,
            LoaderVersion = src.LoaderVersion,
            Note = src.Note,
            CreatedAt = DateTimeOffset.Now,
        };

        Save(copy);

        // 把游戏文件也搬过去。源是共享模式的话 gameDir 指向启动器根，
        // 那里装着所有版本共用的东西，绝不能整目录抄 —— 只挑实例私有的那几个。
        var from = src.Isolated ? src.GameDir : null;
        if (from is not null && Directory.Exists(from))
        {
            CopyDir(from, copy.GameDir);
        }
        else
        {
            foreach (var sub in new[] { "mods", "config", "saves", "resourcepacks", "shaderpacks" })
            {
                var s = Path.Combine(src.GameDir, sub);
                if (Directory.Exists(s)) CopyDir(s, Path.Combine(copy.GameDir, sub));
            }
        }

        return copy;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>该实例的存档目录。</summary>
    public string SavesDirFor(GameInstance instance) => Path.Combine(instance.GameDir, "saves");

    /// <summary>统计实例占用空间（MB），用于界面显示。</summary>
    public static double SizeMb(GameInstance instance)
    {
        try
        {
            if (!Directory.Exists(instance.Directory)) return 0;

            var bytes = Directory.EnumerateFiles(instance.Directory, "*", SearchOption.AllDirectories)
                .Sum(f =>
                {
                    try { return new FileInfo(f).Length; }
                    catch { return 0L; }
                });

            return bytes / 1024.0 / 1024.0;
        }
        catch
        {
            return 0;
        }
    }
}