using System.IO.Compression;

namespace PclLike.Core.Content;

/// <summary>拖进来一个文件，它到底是什么东西。</summary>
public enum DroppedKind
{
    /// <summary>认不出来 —— 不要猜，直接告诉用户"我不知道这是什么"。</summary>
    Unknown = 0,
    ModPack,
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

            // 整合包：Modrinth 的 .zip 版也带这个清单
            if (byName.Contains("modrinth.index.json") || byName.Contains("manifest.json"))
                return new DroppedContent(DroppedKind.ModPack, path, name, "整合包（zip 里带清单）");

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

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path); }
        catch { return path; }
    }
}