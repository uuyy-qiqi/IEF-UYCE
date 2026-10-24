using System.Text.Json;
using System.Text.Encodings.Web;

namespace PclLike.Core.Content;

/// <summary>
/// 收藏夹里的一条。
///
/// 字段刻意和 Modrinth 的项目结构对齐（projectId / slug / name / projectType），
/// 因为绝大多数条目就是从搜索结果里点进来的，存原样就不用做转换，
/// 以后要拿 slug 去重新拉详情也还留着线索。
///
/// <see cref="Kind"/> 存的是 <see cref="ContentKind"/> 的**名字**而不是数字：
/// 数字在枚举成员顺序变化时会静默错位（收藏的整合包变成世界），
/// 而名字在反序列化失败时能明确知道是哪个值不认识。
/// </summary>
public sealed record FavoriteItem
{
    /// <summary>项目的唯一 id。收藏夹按它去重 —— 同一个人可能从不同入口点进来。</summary>
    public required string ProjectId { get; init; }

    /// <summary>URL 友好的短名，如 `sodium`。</summary>
    public string Slug { get; init; } = "";

    /// <summary>显示名。</summary>
    public required string Name { get; init; }

    /// <summary>Modrinth 的 project_type 字符串（mod / modpack / ...）。</summary>
    public string ProjectType { get; init; } = "mod";

    /// <summary>一句话简介，列表里显示。</summary>
    public string Summary { get; init; } = "";

    /// <summary>图标地址。</summary>
    public string? IconUrl { get; init; }

    /// <summary>属于哪个 <see cref="ContentKind"/>（存名字）。</summary>
    public required string Kind { get; init; }

    /// <summary>加入时间，只用于显示和排序。</summary>
    public string AddedAt { get; init; } = "";
}

/// <summary>
/// 收藏夹：一个 JSON 文件，装着一个项目的 id 列表。
///
/// ## 为什么不做成内存集合
///
/// 收藏夹的价值全在"下次开启动器还在"，所以每次操作都直接落盘。
/// 收藏条数是个位到几十的量级，全量重写 JSON 比维护增量方案便宜得多，
/// 而且不会出现"内存和文件不一致"那种最难查的 bug。
///
/// ## 为什么读失败不抛
///
/// 文件可能被用户手动改坏、被同步盘截断、被另一个进程占用。
/// 让"收藏夹读取失败"把整个资源浏览界面打崩，是拿次要功能换主要功能。
/// 读不出来就当空的，用户重新收藏即可。
/// </summary>
public sealed class FavoritesStore
{
    /// <summary>
    /// 中文不转义（<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>）。
    /// 默认编码器会把"钠优化"写成 `\u94A0\u4F18\u5316`，用户打开文件看一眼
    /// 完全看不懂，出了问题也没法手改 —— 这文件的价值之一就是能手改。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;

    /// <summary>
    /// 文件落在 <c>rootPath\favorites.json</c>。
    /// rootPath 通常是启动器根目录（和配置、实例目录并列），
    /// 这样收藏是"全局"的，不会因为切实例就消失。
    /// </summary>
    public FavoritesStore(string rootPath)
    {
        _root = string.IsNullOrWhiteSpace(rootPath) ? "." : rootPath.Trim();
        FilePath = Path.Combine(_root, "favorites.json");
    }

    /// <summary>收藏文件的完整路径，界面上"打开配置文件"会用到。</summary>
    public string FilePath { get; }

    /// <summary>读出全部收藏。文件不存在或内容坏了都返回空列表，不抛。</summary>
    public IReadOnlyList<FavoriteItem> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return [];

            var items = JsonSerializer.Deserialize<List<FavoriteItem>>(json, JsonOpts);
            if (items is null) return [];

            // 手改过的文件可能缺 ProjectId（required 在反序列化时并不强制），
            // 这种条目没法去重也没法跳转，直接丢掉。
            return items
                .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.ProjectId))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>这个项目是否已收藏。</summary>
    public bool Has(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return false;

        var key = projectId.Trim();
        return Load().Any(i => string.Equals(i.ProjectId, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 收/取消收藏，返回**操作之后**是否在收藏里。
    ///
    /// 为什么返回"之后的状态"而不是"是否发生了变化"：界面上按钮只有两态
    /// （已收藏 / 未收藏），拿到终态直接刷新按钮即可，不需要调用方再查一次。
    /// </summary>
    public bool Toggle(FavoriteItem item)
    {
        if (item is null) return false;

        var key = item.ProjectId?.Trim() ?? "";
        if (key.Length == 0) return false;

        var items = Load().ToList();

        var index = items.FindIndex(i => string.Equals(i.ProjectId, key, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            items.RemoveAt(index);
            Save(items);
            return false;
        }

        items.Add(item with
        {
            ProjectId = key,
            AddedAt = string.IsNullOrWhiteSpace(item.AddedAt)
                ? DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")
                : item.AddedAt,
        });

        Save(items);
        return true;
    }

    /// <summary>直接移除一条，返回是否真的删掉了。删除"不在收藏里的"返回 false，不算错。</summary>
    public bool Remove(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return false;

        var key = projectId.Trim();
        var items = Load().ToList();

        var removed = items.RemoveAll(i => string.Equals(i.ProjectId, key, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;

        Save(items);
        return true;
    }

    /// <summary>
    /// 写回文件。
    ///
    /// 先写 `.tmp` 再改名：收藏是"用户点一下就写一次"的高频操作，
    /// 直接覆写时断电/崩溃会留下 truncated JSON，下次启动收藏全没了。
    /// 改名的原子性保证文件要么是旧的完整内容，要么是新的完整内容。
    /// </summary>
    private void Save(List<FavoriteItem> items)
    {
        try
        {
            Directory.CreateDirectory(_root);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* 只读盘/被占用：这次操作没落盘，但内存里的行为已经返回给调用方了 */ }
    }
}