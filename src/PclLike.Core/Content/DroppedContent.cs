using System.IO.Compression;

namespace PclLike.Core.Content;

/// <summary>拖进来一个文件，它到底是什么东西。</summary>
public enum DroppedKind
{
    /// <summary>认不出来 —— 不要猜，直接告诉用户"我不知道这是什么"。</summary>
    Unknown = 0,
    ModPack,

    /// <summary>
    /// CurseForge 整合包：zip 里有 <c>manifest.json</c>（manifestType /
    /// manifestVersion / minecraft / files / overrides）。
    ///
    /// 它和 <see cref="ModPack"/> 长得像，但清单格式是两码事：
    /// Modrinth 的 modrinth.index.json 里每个文件都带完整下载地址和 sha1；
    /// CurseForge 只给 projectID + fileID，文件名和地址都得另外问。
    /// 两者必须分开 —— 拿 Modrinth 的读法去读 CurseForge 的清单，
    /// 结果只有一句「这不是整合包：里面没有 modrinth.index.json」。
    /// </summary>
    CursePack,

    /// <summary>
    /// 解压即玩懒人包：一个 zip 里装着整个 .minecraft（自带 versions/、
    /// libraries/、mods/、配置）。国内分享的整合包大多长这样。
    ///
    /// 它和 <see cref="ModPack"/> 是两回事：ModPack 只有一份清单，东西要照单
    /// 去下；懒人包是东西已经全在包里了，要做的是"把它整个解出来当一个现成
    /// 实例"，而不是往当前实例里塞文件。
    /// </summary>
    PackBundle,

    Mod,
    ResourcePack,
    ShaderPack,
    Datapack,
    World,
}

/// <summary>分类结果。<paramref name="Describe"/> 是给用户看的一句话。</summary>
public sealed record DroppedContent(
    DroppedKind Kind,
    string Path,
    string FileName,
    string Describe);

/// <summary>
/// 判断拖进启动器的那个文件是什么。
///
/// 为什么不能只看扩展名：整合包、资源包、光影包、数据包、存档**全都是 .zip**
/// （整合包还有 .mrpack）。只看扩展名的话，用户拖一个存档进来会被告知"这是
/// 资源包"，然后装到 resourcepacks/ 里去 —— 装完了游戏里找不到，用户也不知道
/// 错在哪。所以 zip 一律打开看里面的目录结构。
///
/// 全程不抛异常：拖进来的可能是正在被别的程序写着的文件、可能是 0 字节的
/// 半成品，也可能根本不是 zip。读不了就当"认不出来"，让上层说人话。
/// </summary>
public static class DroppedContentClassifier
{
    public static DroppedContent Classify(string path)
    {
        var name = SafeFileName(path);

        try
        {
            if (!File.Exists(path)) return Unknown(path, name, "文件不在了（是不是刚被移走或删掉？）");

            var ext = Path.GetExtension(path).ToLowerInvariant();

            // 整合包：官方扩展名，没什么可猜的
            if (ext == ".mrpack")
                return new DroppedContent(DroppedKind.ModPack, path, name, "Modrinth 整合包（.mrpack）");

            if (ext is ".jar" or ".litemod")
                return new DroppedContent(DroppedKind.Mod, path, name, "Mod（.jar）");

            if (ext != ".zip")
                return Unknown(path, name, $"不认识「{ext}」这种文件（支持 .mrpack / .zip / .jar）");

            // 到这里都是 zip，打开看结构
            using var zip = ZipFile.OpenRead(path);

            var entries = zip.Entries
                .Select(x => x.FullName.Replace('\\', '/'))
                .Where(x => x.Length > 0)
                .ToList();

            if (entries.Count == 0)
                return Unknown(path, name, "空压缩包，里面什么都没有");

            var byName = new HashSet<string>(entries, StringComparer.OrdinalIgnoreCase);
            var byBase = new HashSet<string>(entries.Select(x => x[(x.LastIndexOf('/') + 1)..]),
                StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(
                entries.Select(x => x.Contains('/') ? x[..x.IndexOf('/')] : ""),
                StringComparer.OrdinalIgnoreCase);

            // 整合包：先按清单格式分家 —— Modrinth 与 CurseForge 的清单不是一回事
            if (byName.Contains("modrinth.index.json"))
                return new DroppedContent(DroppedKind.ModPack, path, name,
                    "Modrinth 整合包（有 modrinth.index.json）");

            if (byName.Contains("manifest.json") && IsCurseForgeManifest(zip))
                return new DroppedContent(DroppedKind.CursePack, path, name,
                    "CurseForge 整合包（有 manifest.json）");

            // 名字叫 manifest.json 但不是 CurseForge 的清单：老式手工包也爱用这个
            // 文件名，交给下面按目录结构继续判断，别硬塞进整合包这条路。

            // 解压即玩懒人包：包里有整个 .minecraft，或者有 versions/<id>/<id>.json
            // 这种"自带一份游戏"的结构。必须排在资源包/数据包/光影前面 ——
            // 懒人包里也可能有 resourcepacks/、shaderpacks/ 这些目录，
            // 但它们不是"这个包的身份"，只是包里的一部分内容。
            if (LooksLikeBundle(entries, roots))
                return new DroppedContent(DroppedKind.PackBundle, path, name,
                    "解压即玩的整包（里面有完整的 .minecraft）");

            // 存档：level.dat 是存档的唯一标志（可能在根上，也可能被套一层目录）
            if (byBase.Contains("level.dat"))
                return new DroppedContent(DroppedKind.World, path, name, "游戏存档（有 level.dat）");

            var hasMeta = byName.Contains("pack.mcmeta");

            if (hasMeta && roots.Contains("shaders"))
                return new DroppedContent(DroppedKind.ShaderPack, path, name, "光影包（有 shaders/ 和 pack.mcmeta）");

            if (hasMeta && roots.Contains("assets"))
                return new DroppedContent(DroppedKind.ResourcePack, path, name, "资源包（有 assets/ 和 pack.mcmeta）");

            if (hasMeta && roots.Contains("data"))
                return new DroppedContent(DroppedKind.Datapack, path, name, "数据包（有 data/ 和 pack.mcmeta）");

            // 没有 pack.mcmeta 的光影包也真实存在（不少光影作者不放）
            if (roots.Contains("shaders"))
                return new DroppedContent(DroppedKind.ShaderPack, path, name, "光影包（有 shaders/）");

            // 一堆 jar 的 zip：老式整合包（CurseForge 的手工包就是这样）
            if (roots.Contains("mods"))
                return Unknown(path, name,
                    "看起来是个整合包，但里面没有清单（modrinth.index.json），"
                    + "认不出它要哪个版本和加载器，不敢乱装");

            if (roots.Contains("resourcepacks") || roots.Contains("saves"))
                return Unknown(path, name, "这个 zip 里是一堆包/存档，不是单个可安装的东西，解压后再拖里面的文件");

            return Unknown(path, name, "认不出这是什么（不是整合包、资源包、光影包、数据包或存档）");
        }
        catch (InvalidDataException)
        {
            return Unknown(path, name, "这不是个完整的压缩包（下载没下完？）");
        }
        catch (IOException)
        {
            return Unknown(path, name, "读不了这个文件（可能正被别的程序占用）");
        }
        catch (UnauthorizedAccessException)
        {
            return Unknown(path, name, "没有权限读这个文件");
        }
        catch
        {
            return Unknown(path, name, "认不出这是什么文件");
        }
    }

    private static DroppedContent Unknown(string path, string name, string why)
        => new(DroppedKind.Unknown, path, name, why);

    /// <summary>
    /// zip 里的 manifest.json 是不是 CurseForge 的清单。
    ///
    /// 不能只看文件名：老式手工整合包也常放一个 manifest.json。CurseForge 的
    /// 清单有两处一眼能认的特征：<c>manifestType = "minecraftModpack"</c>，
    /// 以及 minecraft 下面同时出现 version 与 modLoaders。
    ///
    /// 只读开头 64 KB 做子串判断，不全量解析：真实清单可能有几十 KB（264 个
    /// 文件条目），为了"分类"把几百个条目解析成对象不划算。
    /// </summary>
    private static bool IsCurseForgeManifest(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(x =>
            x.FullName.Replace('\\', '/').Equals("manifest.json", StringComparison.OrdinalIgnoreCase));

        if (entry is null) return false;

        try
        {
            var capacity = (int)Math.Clamp(entry.Length, 1, 64 * 1024);

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            var buffer = new char[capacity];
            var read = reader.Read(buffer, 0, capacity);
            var head = new string(buffer, 0, read);

            if (head.Contains("minecraftModpack", StringComparison.OrdinalIgnoreCase)) return true;

            return head.Contains("\"minecraft\"", StringComparison.OrdinalIgnoreCase)
                && head.Contains("\"modLoaders\"", StringComparison.OrdinalIgnoreCase)
                && head.Contains("\"files\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// zip 里是不是装着一整份游戏（解压即玩包）。
    ///
    /// 两个判据，命中一个就算：
    ///  - 任意深度有 `.minecraft/` —— 一眼就能认出来，国内整合包最常见的形状；
    ///  - 任意深度有 `versions/&lt;id&gt;/&lt;id&gt;.json` —— 有些不套 .minecraft 目录，
    ///    直接把 versions/ 摆在根上（HMCL 的导出就长这样）。
    ///
    /// 只说"有 versions/ 目录"是不够的：资源包/光影包里也可能有叫 versions
    /// 的东西。所以要求那个目录下真的躺着一份版本 json —— 那玩意儿只有
    /// "一份能跑的游戏"才会有。
    /// </summary>
    private static bool LooksLikeBundle(List<string> entries, HashSet<string> roots)
    {
        foreach (var e in entries)
        {
            if (e.Contains("/.minecraft/", StringComparison.OrdinalIgnoreCase)
                || e.StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // versions/<id>/<id>.json —— 目录名和文件名同名，这是版本 json 的规矩
        foreach (var e in entries)
        {
            var parts = e.Split('/');
            for (var i = 0; i + 2 < parts.Length; i++)
            {
                if (!parts[i].Equals("versions", StringComparison.OrdinalIgnoreCase)) continue;

                var id = parts[i + 1];
                if (id.Length == 0) continue;

                if (parts[i + 2].Equals(id + ".json", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        _ = roots;
        return false;
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path); }
        catch { return path; }
    }
}