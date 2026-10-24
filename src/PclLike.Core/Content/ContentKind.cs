using PclLike.Core.Storage;

namespace PclLike.Core.Content;

/// <summary>
/// 社区资源的种类，以及它和实例目录、Modrinth API 之间的对应关系。
///
/// ## 为什么要单独一个枚举
///
/// 启动器要从 Modrinth / CurseForge 装六种东西，它们只在两件事上有差别：
///   - **落盘到哪儿**（<see cref="ContentPaths.TargetDir"/>）
///   - **API 用哪个 project_type / 界面上叫什么**（<see cref="ContentPaths.ModrinthProjectType"/>、<see cref="ContentPaths.DisplayName"/>）
///
/// 把这两件事收在一个枚举上，浏览界面、下载队列、安装器就都能只持有
/// 一个 ContentKind 参数走完全程，不需要为每种资源写一遍重复的 if 树。
/// 以后再加"模组包内资源包"之类的新类型，也只在这里加一个成员。
/// </summary>
public enum ContentKind
{
    /// <summary>Mod，落盘 &lt;GameDir&gt;/mods。</summary>
    Mod,

    /// <summary>整合包，落盘 &lt;GameDir&gt; 本身（它里面自带 mods/ config/ 等结构）。</summary>
    ModPack,

    /// <summary>数据包，落盘 &lt;GameDir&gt;/saves/&lt;世界&gt;/datapacks。</summary>
    Datapack,

    /// <summary>资源包，落盘 &lt;GameDir&gt;/resourcepacks。</summary>
    ResourcePack,

    /// <summary>光影包，落盘 &lt;GameDir&gt;/shaderpacks。</summary>
    Shader,

    /// <summary>世界存档，落盘 &lt;GameDir&gt;/saves。</summary>
    World,
}

/// <summary>
/// 内容类型 → 落盘目录 / 显示名 / Modrinth 类型字符串 的映射表。
///
/// ## 为什么目录规则写在这里
///
/// 游戏读的是固定目录名（`mods`、`resourcepacks`、`shaderpacks`、
/// `saves/<世界>/datapacks`），这是 Minecraft 的约定，不是我们的自由。
/// 一旦写歪了，文件装进去游戏也看不见，用户只会觉得"启动器装完没用"。
/// 所以这些字符串**只在这一处出现**，安装器、世界管理、收藏夹都从这里取，
/// 避免几处各写一份、某天改一处忘一处。
///
/// ## 为什么 TargetDir 会建目录
///
/// 调用它的场景几乎都是"马上要往里写"（下载落盘、解压、列表前先确保存在）。
/// 让调用方每次自己 CreateDirectory 只会漏掉分支，所以这里顺手建好。
/// </summary>
public static class ContentPaths
{
    /// <summary>所有内容类型，界面做下拉/标签页时按这个顺序排。</summary>
    public static IReadOnlyList<ContentKind> All { get; } =
    [
        ContentKind.Mod,
        ContentKind.ModPack,
        ContentKind.Datapack,
        ContentKind.ResourcePack,
        ContentKind.Shader,
        ContentKind.World,
    ];

    /// <summary>
    /// 该类型内容在实例里的落盘目录，并确保目录已存在。
    ///
    /// Datapack 需要 <paramref name="worldName"/>：数据包是**挂在某个世界下面**的，
    /// 没有世界就无处可放。调用方没给世界名时退回 &lt;GameDir&gt;/datapacks ——
    /// 这个目录游戏不读，但至少是个明确的、能让人一眼看出"装歪了"的地方，
    /// 比默默丢进某个猜出来的世界（或者 mods 里）强。
    /// </summary>
    /// <param name="instance">目标实例，目录由它的 <see cref="Storage.GameInstance.GameDir"/> 决定。</param>
    /// <param name="kind">内容类型。</param>
    /// <param name="worldName">数据包所属的世界目录名；其它类型忽略。为空时数据包退到 &lt;GameDir&gt;/datapacks。</param>
    public static string TargetDir(GameInstance instance, ContentKind kind, string? worldName = null)
    {
        var gameDir = instance.GameDir;

        var dir = kind switch
        {
            ContentKind.Mod => Path.Combine(gameDir, "mods"),
            ContentKind.ResourcePack => Path.Combine(gameDir, "resourcepacks"),
            ContentKind.Shader => Path.Combine(gameDir, "shaderpacks"),

            // 数据包必须落在具体世界下面，游戏才认；没有世界名就退到 gameDir/datapacks
            // （游戏不读这里，但它是"装歪了"的显式信号，不是静默成功）。
            // 世界名先做安全化，否则 "..\\..\\evil" 这种会把目录指到实例外面去。
            ContentKind.Datapack => string.IsNullOrWhiteSpace(worldName)
                ? Path.Combine(gameDir, "datapacks")
                : Path.Combine(gameDir, "saves", SafeWorldName(worldName), "datapacks"),

            // 世界本身就落在 saves 下，ImportZip 再往下挂世界名
            ContentKind.World => Path.Combine(gameDir, "saves"),

            // 整合包掏出来是完整的 .minecraft 内容，直接铺在 gameDir 上
            ContentKind.ModPack => gameDir,

            _ => Path.Combine(gameDir, "mods"),
        };

        TryCreateDir(dir);
        return dir;
    }

    /// <summary>给界面看的中文名。</summary>
    public static string DisplayName(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "Mod",
        ContentKind.ModPack => "整合包",
        ContentKind.Datapack => "数据包",
        ContentKind.ResourcePack => "资源包",
        ContentKind.Shader => "光影包",
        ContentKind.World => "世界",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Modrinth 的 project_type 字符串，用于拼搜索/版本请求的 URL。
    ///
    /// 只有 World 在 Modrinth 上不存在（它没有世界这一分类），返回 null，
    /// 调用方据此禁用"从 Modrinth 找世界"的入口，而不是拿一个假类型去请求。
    /// </summary>
    public static string? ModrinthProjectType(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mod",
        ContentKind.ModPack => "modpack",
        ContentKind.Datapack => "datapack",
        ContentKind.ResourcePack => "resourcepack",
        ContentKind.Shader => "shader",
        ContentKind.World => null,
        _ => null,
    };

    /// <summary>
    /// 该类型内容对加载器的硬要求。返回 null 表示原版也能用。
    ///
    /// Mod 和光影包必须跑在对应的加载器/渲染器上（光影包实际上还要 Iris/OptiFine，
    /// 但那是另一个维度，这里只表达"原版装了没反应"这一层）。
    /// GUI 拿到这个值可以在安装后提示用户"该实例还是原版，装了不会生效"。
    /// </summary>
    public static string? RequiredLoader(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mod-loader",
        ContentKind.Shader => "shader-loader",
        _ => null,
    };

    /// <summary>
    /// 把世界名规范成一个安全的目录名。
    ///
    /// 只取文件名部分并剔掉 `..` —— 世界名来自用户输入、zip 条目名、
    /// 别人的整合包，全都要按不可信数据对待。空或非法时返回 "world"，
    /// 这样调用方永远拿到一个能用的目录。
    /// </summary>
    public static string SafeWorldName(string? worldName)
    {
        var name = Path.GetFileName(worldName ?? "").Trim();

        if (name.Length == 0) return "world";
        if (name is "." or "..") return "world";

        // Windows 不允许的字符也要挡掉（`:` `<` `>` 等在中文世界里很常见）
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "world" : name;
    }

    private static void TryCreateDir(string dir)
    {
        try { Directory.CreateDirectory(dir); }
        catch { /* 只读盘或权限不足：由真正写文件的调用方报错，这里不抢 */ }
    }
}